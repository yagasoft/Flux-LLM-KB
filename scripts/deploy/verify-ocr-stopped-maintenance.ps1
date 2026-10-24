[CmdletBinding()]
param([string]$PlanPath, [string]$ExpectedPlanSha256)
$ErrorActionPreference='Stop'

function Get-OcrBytesHash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)) }

function Assert-OcrPlainLocalPath([string]$Path) {
    if (![IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or
        [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Path)).DriveType -ne 'Fixed') { throw 'document-ocr-maintenance-local-path-required' }
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) { throw 'document-ocr-maintenance-reparse-path-refused' }
        }
        $current=[IO.Path]::GetDirectoryName($current)
    }
    return [IO.Path]::GetFullPath($Path)
}

function Test-OcrExecutionBoundary([string]$Path, [string]$Name, [string]$CommandLine) {
    if ($Name -match '^(?i:pythonw?|pypy\d*|paddle.*|nvcc)\.exe$') { return $true }
    foreach ($root in @('J:\Models\', 'I:\FluxKnowledge\App\', 'C:\inetpub\FluxKnowledge\outlook-host\')) {
        if ($Path -and ($Path+'\').StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    if ($Name -match '^(?i:dotnet|pwsh|powershell|cmd|node|wscript|cscript)\.exe$' -and
        $CommandLine -match '(?i:paddleocr|paddlex|I:\\FluxKnowledge\\App\\|C:\\inetpub\\FluxKnowledge\\outlook-host\\)') { return $true }
    return $false
}

function Get-OcrOsSnapshot {
    @(Get-CimInstance Win32_Process -ErrorAction Stop | ForEach-Object {
        [ordered]@{ Pid=[int]$_.ProcessId; ParentPid=[int]$_.ParentProcessId; StartedUtc=$(if ($_.CreationDate) {$_.CreationDate.ToUniversalTime().ToString('O')}else{''});
            Name=[string]$_.Name; Path=[string]$_.ExecutablePath; CommandLine=[string]$_.CommandLine }
    })
}

function Assert-OcrOwnersStopped($Plan, $StopReceipt, $Processes) {
    if ($Plan.HoldPath -cne 'I:\FluxKnowledge\Runtime\deployment-validation-hold.json' -or
        $Plan.ConfigurationPath -cne 'I:\FluxKnowledge\Config\appsettings.Production.json' -or
        $Plan.IisPool -cne 'FluxKnowledge' -or $Plan.TaskName -cne 'FluxKnowledge.OutlookHost') {
        throw 'document-ocr-maintenance-target-invalid'
    }
    if (($Plan.ReleaseId | ConvertTo-Json -Compress) -cne [IO.File]::ReadAllText($Plan.HoldPath) -or
        (Get-FileHash -LiteralPath $Plan.ConfigurationPath).Hash -cne $Plan.ConfigurationSha256 -or
        (Get-FileHash -LiteralPath $Plan.CompanionConfigurationPath).Hash -cne $Plan.CompanionConfigurationSha256) {
        throw 'document-ocr-maintenance-hold-or-configuration-changed'
    }
    if ($StopReceipt.ReleaseId -cne $Plan.ReleaseId -or !$StopReceipt.StopIntentAtUtc -or !$StopReceipt.Contains('ExitedIdentities')) {
        throw 'document-ocr-maintenance-stopped-receipt-invalid'
    }
    Import-Module WebAdministration -ErrorAction Stop -WarningAction SilentlyContinue
    if ((Get-WebAppPoolState -Name $Plan.IisPool).Value -ne 'Stopped') { throw 'document-ocr-maintenance-pool-running' }
    $appcmd=Join-Path $env:SystemRoot 'System32/inetsrv/appcmd.exe'
    $workers=& $appcmd list wp /xml
    if ($LASTEXITCODE -ne 0) { throw 'document-ocr-maintenance-worker-query-failed' }
    [xml]$workerXml=$workers -join "`n"
    if (@($workerXml.SelectNodes('//WP') | Where-Object { $_.GetAttribute('APPPOOL.NAME') -ceq $Plan.IisPool }).Count) {
        throw 'document-ocr-maintenance-worker-running'
    }
    $task=@(Get-ScheduledTask -TaskName $Plan.TaskName -TaskPath '\' -ErrorAction Stop)
    if ($task.Count -ne 1 -or $task[0].Settings.Enabled -or $task[0].State -eq 'Running') { throw 'document-ocr-maintenance-companion-running' }
    [xml]$taskXml=Export-ScheduledTask -TaskName $Plan.TaskName -TaskPath '\'
    $ns=[Xml.XmlNamespaceManager]::new($taskXml.NameTable);$ns.AddNamespace('t',$taskXml.DocumentElement.NamespaceURI)
    $enabled=$taskXml.SelectSingleNode('/t:Task/t:Settings/t:Enabled',$ns)
    if ($null -ne $enabled) { [void]$enabled.ParentNode.RemoveChild($enabled) }
    $identity=Get-OcrBytesHash ([Text.Encoding]::UTF8.GetBytes(($taskXml.OuterXml | ConvertTo-Json -Compress)))
    if ($identity -cne $Plan.Task.IdentityHash -or (Get-FileHash -LiteralPath $Plan.Task.LauncherPath).Hash -cne $Plan.Task.LauncherSha256) {
        throw 'document-ocr-maintenance-companion-policy-changed'
    }
    foreach ($process in $Processes) {
        if (Test-OcrExecutionBoundary $process.Path $process.Name $process.CommandLine) { throw 'document-ocr-maintenance-eligible-worker-present' }
        foreach ($old in $StopReceipt.ExitedIdentities) {
            if ($process.Pid -eq $old.Pid -and $process.StartedUtc -ceq $old.StartedUtc) { throw 'document-ocr-maintenance-owned-process-present' }
        }
    }
}

function ConvertFrom-OcrGpuObservation([string]$Xml, $Plan, $Before, $After) {
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Ignore;$settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create([IO.StringReader]::new($Xml),$settings)
    try { $document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader) } finally { $reader.Dispose() }
    $gpus=@($document.SelectNodes('/nvidia_smi_log/gpu'))
    if ($gpus.Count -ne 1 -or $document.nvidia_smi_log.attached_gpus -ne '1' -or
        [string]$gpus[0].uuid -cne $Plan.GpuUuid -or [string]$document.nvidia_smi_log.driver_version -cne $Plan.DriverVersion) {
        throw 'document-ocr-maintenance-driver-identity-changed'
    }
    $processNode=$gpus[0].SelectSingleNode('processes')
    if ($null -eq $processNode -or ($processNode.SelectNodes('process_info').Count -eq 0 -and $processNode.InnerText.Trim())) {
        throw 'document-ocr-maintenance-driver-process-data-unavailable'
    }
    $entries=@(foreach ($item in $processNode.SelectNodes('process_info')) {
        $pidValue=0
        if (![int]::TryParse([string]$item.pid,[ref]$pidValue) -or $pidValue -le 0 -or
            [string]$item.type -cnotin @('C','G','C+G','M','M+C','O')) { throw 'document-ocr-maintenance-driver-process-data-unavailable' }
        $beforeIdentity=@($Before | Where-Object Pid -eq $pidValue);$afterIdentity=@($After | Where-Object Pid -eq $pidValue)
        $path=[string]$item.process_name
        if ($beforeIdentity.Count -ne 1 -or $afterIdentity.Count -ne 1 -or
            $beforeIdentity[0].StartedUtc -cne $afterIdentity[0].StartedUtc -or !$beforeIdentity[0].StartedUtc -or
            !$path -or $path -ine $beforeIdentity[0].Path -or $path -ine $afterIdentity[0].Path) {
            throw 'document-ocr-maintenance-driver-process-incarnation-unproven'
        }
        if ((Test-OcrExecutionBoundary $path $beforeIdentity[0].Name $beforeIdentity[0].CommandLine) -or
            [IO.Path]::GetFileName($path) -match '^(?i:pythonw?|pypy\d*|dotnet|powershell|pwsh|cmd|node|rundll32|mshta|wscript|cscript)\.exe$') {
            throw 'document-ocr-maintenance-driver-process-unclassified'
        }
        $excluded=@($Plan.ExcludedGpuExecutables | Where-Object { $_.Path -ieq $path -and $_.BoundaryClass -ceq 'UnrelatedDesktopExecutable' })
        if ($excluded.Count -ne 1 -or !$excluded[0].DesktopIdentity -or !$excluded[0].Reason -or
            !$excluded[0].Evidence -or $excluded[0].Evidence.Kind -cnotin @('Authenticode','AppxPackage')) {
            throw 'document-ocr-maintenance-driver-process-unclassified'
        }
        [void](Assert-OcrPlainLocalPath $path)
        if ((Get-FileHash -LiteralPath $path).Hash -cne $excluded[0].Sha256) { throw 'document-ocr-maintenance-desktop-executable-changed' }
        [ordered]@{Pid=$pidValue;StartedUtc=$beforeIdentity[0].StartedUtc;Path=$path;Type=[string]$item.type;
            Exclusion=$excluded[0].BoundaryClass;Sha256=$excluded[0].Sha256;DesktopIdentity=$excluded[0].DesktopIdentity}
    })
    return ,$entries
}

function Invoke-OcrPhysicalVerification($Plan) {
    [void](Assert-OcrPlainLocalPath $Plan.SmiPath)
    if ((Get-FileHash -LiteralPath $Plan.SmiPath).Hash -cne $Plan.SmiSha256) { throw 'document-ocr-maintenance-driver-tool-changed' }
    $stopPath=Join-Path $Plan.OperationRoot 'stopped-owners.json'
    [void](Assert-OcrPlainLocalPath $stopPath)
    $stop=Get-Content -Raw -LiteralPath $stopPath | ConvertFrom-Json -AsHashtable -DateKind String
    $samples=@(foreach ($iteration in 1..2) {
        $before=@(Get-OcrOsSnapshot);Assert-OcrOwnersStopped $Plan $stop $before
        $driver=& $Plan.SmiPath -q -x
        if ($LASTEXITCODE -ne 0 -or !$driver) { throw 'document-ocr-maintenance-driver-query-failed' }
        $after=@(Get-OcrOsSnapshot);Assert-OcrOwnersStopped $Plan $stop $after
        $entries=ConvertFrom-OcrGpuObservation ($driver -join "`n") $Plan $before $after
        [ordered]@{ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');GpuUuid=$Plan.GpuUuid;DriverVersion=$Plan.DriverVersion;
            DriverXml=($driver -join "`n");Processes=$entries;
            OsBefore=@($before | ForEach-Object { [ordered]@{Pid=$_.Pid;ParentPid=$_.ParentPid;StartedUtc=$_.StartedUtc;Name=$_.Name;Path=$_.Path} });
            OsAfter=@($after | ForEach-Object { [ordered]@{Pid=$_.Pid;ParentPid=$_.ParentPid;StartedUtc=$_.StartedUtc;Name=$_.Name;Path=$_.Path} })}
        if ($iteration -eq 1) { Start-Sleep -Milliseconds 250 }
    })
    if ((Get-OcrBytesHash ([Text.Encoding]::UTF8.GetBytes(($samples[0].Processes | ConvertTo-Json -Depth 8 -Compress)))) -cne
        (Get-OcrBytesHash ([Text.Encoding]::UTF8.GetBytes(($samples[1].Processes | ConvertTo-Json -Depth 8 -Compress))))) {
        throw 'document-ocr-maintenance-driver-observation-changed'
    }
    Assert-OcrOwnersStopped $Plan $stop @(Get-OcrOsSnapshot)
    [ordered]@{CleanupVerified=$true;ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');ReleaseId=$Plan.ReleaseId;
        Binding=$Plan.Binding;StoppedReceiptSha256=(Get-FileHash -LiteralPath $stopPath).Hash;Samples=$samples}
}

if ($MyInvocation.InvocationName -ne '.') {
    if (!$PlanPath -or $ExpectedPlanSha256 -cnotmatch '^[A-Fa-f0-9]{64}$') { throw 'document-ocr-maintenance-reviewed-plan-required' }
    [void](Assert-OcrPlainLocalPath $PlanPath)
    if ((Get-FileHash -LiteralPath $PlanPath).Hash -cne $ExpectedPlanSha256) { throw 'document-ocr-maintenance-plan-changed' }
    $plan=Get-Content -Raw -LiteralPath $PlanPath | ConvertFrom-Json -AsHashtable -DateKind String
    Invoke-OcrPhysicalVerification $plan | ConvertTo-Json -Depth 35 -Compress
}
