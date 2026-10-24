param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
function Assert-Refused([scriptblock]$Action,[string]$Reason) {
    $refused=$false
    try { & $Action | Out-Null } catch { $refused=$_.Exception.Message.Contains($Reason) }
    if (!$refused) { throw "Expected refusal: $Reason" }
}
if ($IsWindows -and [IO.File]::Exists((Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/Modules/WebAdministration/WebAdministration.psd1'))) {
    # Exercise the verifier's real import in a clean process. Compatibility
    # warnings must not corrupt the JSON consumed by the maintenance CLI.
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $SourceRoot 'scripts/deploy/verify-ocr-stopped-maintenance.ps1'),[ref]$tokens,[ref]$errors)
    $imports=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Import-Module'},$true))
    if ($errors.Count -or $imports.Count -ne 1) { throw 'Verifier import contract is ambiguous.' }
    $start=[Diagnostics.ProcessStartInfo]::new('pwsh')
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $command=$imports[0].Extent.Text+"`n"+'[ordered]@{SyntheticProtocolCheck=$true} | ConvertTo-Json -Compress'
    foreach ($argument in @('-NoProfile','-NonInteractive','-Command',$command)) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(25000)) { $process.Kill($true);throw 'Verifier import protocol timed out.' }
        $text=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $errorText) { throw 'Verifier import protocol failed.' }
        try { $receipt=$text | ConvertFrom-Json -ErrorAction Stop }
        catch { throw 'Verifier import emitted non-JSON protocol output.' }
        if (!$receipt.SyntheticProtocolCheck) { throw 'Verifier protocol receipt missing.' }
    } finally { $process.Dispose() }
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('FluxOcrMaintenance_'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    $path=Join-Path $root 'desktop.exe';[IO.File]::WriteAllText($path,'synthetic desktop identity')
    $entry=@{Path=$path;Sha256=(Get-FileHash $path).Hash;DesktopIdentity='Synthetic desktop';Reason='Reviewed synthetic fixture';
        Evidence=@{Kind='Authenticode';SignerSubject='Synthetic signer';SignerThumbprint=('A'*40)}}
    $signatureValid=$true
    function Get-AuthenticodeSignature { param($LiteralPath)
        @{Status=$(if ($signatureValid) {'Valid'}else{'NotSigned'});SignerCertificate=@{Subject='Synthetic signer';Thumbprint=('A'*40)}}
    }
    $classified=Assert-OcrReviewedDesktopEntry $entry
    if ($classified.ObservedVersion.ProductName -or $classified.ObservedVersion.CompanyName) { throw 'Synthetic version metadata was invented.' }
    $classificationPath=Join-Path $root 'classifications.json'
    Write-OcrRecoveryJson $classificationPath @{Version=1;Entries=@($entry)}
    $packet=Read-OcrReviewedDesktopClassifications $classificationPath
    if ($packet.Entries.Count -ne 1 -or $packet.InputSha256 -cne (Get-FileHash $classificationPath).Hash) { throw 'Classification input not pinned.' }
    Write-OcrRecoveryJson $classificationPath @{Version=1;Entries=@($entry,$entry)}
    Assert-Refused { Read-OcrReviewedDesktopClassifications $classificationPath } 'classification-duplicate'
    $entry.Reason=''
    Assert-Refused { Assert-OcrReviewedDesktopEntry $entry } 'classification-incomplete'
    $entry.Reason='Reviewed synthetic fixture';$signatureValid=$false
    Assert-Refused { Assert-OcrReviewedDesktopEntry $entry } 'signer-unproven'
    $signatureValid=$true
    foreach ($name in @('python.exe','node.exe','pwsh.exe')) {
        $forbidden=$entry.Clone();$forbidden.Path=Join-Path $root $name
        Assert-Refused { Assert-OcrReviewedDesktopEntry $forbidden } 'runtime-exclusion-forbidden'
    }
    $packageRoot=Join-Path $root 'package';[void][IO.Directory]::CreateDirectory($packageRoot)
    $packagePath=Join-Path $packageRoot 'desktop.exe';[IO.File]::WriteAllText($packagePath,'synthetic package executable')
    $package=@{Name='SyntheticPackage';PackageFullName='SyntheticPackage_1';Publisher='SyntheticPublisher';
        InstallLocation=$packageRoot;SignatureKind='Store';Status='Ok'}
    function Get-AppxPackage { param($Name) $package }
    $packaged=@{Path=$packagePath;Sha256=(Get-FileHash $packagePath).Hash;DesktopIdentity='Packaged desktop';Reason='Reviewed package';
        Evidence=@{Kind='AppxPackage';Name=$package.Name;PackageFullName=$package.PackageFullName;Publisher=$package.Publisher;
            InstallLocation=$packageRoot;SignatureKind='Store'}}
    [void](Assert-OcrReviewedDesktopEntry $packaged)
    $packaged.Path=$path;$packaged.Sha256=$entry.Sha256
    Assert-Refused { Assert-OcrReviewedDesktopEntry $packaged } 'package-unproven'
    $plan=@{GpuUuid='GPU-synthetic';DriverVersion='synthetic';ExcludedGpuExecutables=@($classified)}
    $identity=@{Pid=1234;StartedUtc='2026-01-01T00:00:00Z';Path=$path;Name='desktop.exe';CommandLine=''}
    $xml='<nvidia_smi_log><driver_version>synthetic</driver_version><attached_gpus>1</attached_gpus><gpu><uuid>GPU-synthetic</uuid><processes><process_info><pid>1234</pid><type>C+G</type><process_name>'+[Security.SecurityElement]::Escape($path)+'</process_name><used_memory>N/A</used_memory></process_info></processes></gpu></nvidia_smi_log>'
    $accepted=ConvertFrom-OcrGpuObservation $xml $plan @($identity) @($identity)
    if ($accepted.Count -ne 1) { throw 'Valid WDDM observation refused.' }
    Assert-Refused { ConvertFrom-OcrGpuObservation ($xml.Replace('GPU-synthetic','GPU-other')) $plan @($identity) @($identity) } 'driver-identity-changed'
    Assert-Refused { ConvertFrom-OcrGpuObservation ($xml.Replace('<processes>','<missing>').Replace('</processes>','</missing>')) $plan @($identity) @($identity) } 'process-data-unavailable'
    $reused=$identity.Clone();$reused.StartedUtc='2026-02-01T00:00:00Z'
    Assert-Refused { ConvertFrom-OcrGpuObservation $xml $plan @($identity) @($reused) } 'incarnation-unproven'
    Assert-Refused { ConvertFrom-OcrGpuObservation $xml $plan @() @($identity) } 'incarnation-unproven'
    $plan.ExcludedGpuExecutables[0].BoundaryClass='Unknown'
    Assert-Refused { ConvertFrom-OcrGpuObservation $xml $plan @($identity) @($identity) } 'unclassified'
    $plan.ExcludedGpuExecutables[0].BoundaryClass='UnrelatedDesktopExecutable'
    $plan.ExcludedGpuExecutables=@()
    Assert-Refused { ConvertFrom-OcrGpuObservation $xml $plan @($identity) @($identity) } 'unclassified'
    $plan.ExcludedGpuExecutables=@($classified)
    [IO.File]::WriteAllText($path,'changed')
    Assert-Refused { ConvertFrom-OcrGpuObservation $xml $plan @($identity) @($identity) } 'executable-changed'
    Assert-Refused { Assert-OcrReviewedDesktopEntry $entry } 'executable-changed'
    foreach ($name in @('python.exe','pythonw.exe','pypy3.exe')) {
        if (!(Test-OcrExecutionBoundary ('I:\another\'+$name) $name '')) { throw 'Unclassified interpreter accepted.' }
    }
    if (!(Test-OcrExecutionBoundary 'J:\Models\runtimes\helper.exe' 'helper.exe' '')) { throw 'Runtime helper accepted.' }

    $modes=@('none','EnsureHold','StopOwners','AssertStopped','Migrate','Settle','BackupAndActivate','Start','ValidateHeld','ReleaseHold','RestoreCompanionPolicy','ValidateReleased')
    foreach ($mode in $modes) {
        $evidence=@{Hold=$false;Stopped=$false;Started=$false;Restored=$false;Fail=$mode;Failed=$false;Settlements=0;Starts=0}
        $ports=@{}
        foreach ($name in @('AssertBindings','EnsureHold','StopOwners','AssertStopped','Migrate','Settle','BackupAndActivate','Start',
            'ValidateHeld','ReleaseHold','RestoreCompanionPolicy','ValidateReleased','RestorePriorPayloads','AssertCompleted')) {
            $captured=$name
            $ports[$name]={
                if ($captured -eq 'EnsureHold') { $evidence.Hold=$true }
                if ($captured -eq 'StopOwners') { $evidence.Stopped=$true;$evidence.Started=$false }
                if ($captured -eq 'Settle') { $evidence.Settlements++ }
                if ($captured -eq 'Start') { $evidence.Started=$true;$evidence.Stopped=$false;$evidence.Starts++ }
                if ($captured -eq 'ReleaseHold') { $evidence.Hold=$false }
                if ($captured -eq 'RestorePriorPayloads') { $evidence.Restored=$true }
                if ($captured -eq $evidence.Fail -and !$evidence.Failed) { $evidence.Failed=$true;throw 'synthetic boundary response loss' }
            }.GetNewClosure()
        }
        $state=Join-Path $root ($mode+'.json')
        if ($mode -eq 'none') {
            $done=Invoke-OcrRecoveryFlow $state ('A'*64) $ports
            if (!$done.Completed -or $evidence.Hold -or $evidence.Starts -ne 1) { throw 'Successful flow incorrect.' }
            [void](Invoke-OcrRecoveryFlow $state ('A'*64) $ports)
            if ($evidence.Starts -ne 1) { throw 'Completed replay restarted owners.' }
        } else {
            Assert-Refused { Invoke-OcrRecoveryFlow $state ('A'*64) $ports } 'failed-held'
            $failed=Get-Content -Raw $state | ConvertFrom-Json
            if (!$evidence.Hold -or !$evidence.Stopped -or ($failed.StartIntent -and $evidence.Restored)) { throw "Unsafe containment: $mode" }
            Assert-Refused { Invoke-OcrRecoveryFlow $state ('A'*64) $ports } 'explicit-resume-required'
            $done=Invoke-OcrRecoveryFlow $state ('A'*64) $ports -Resume
            if (!$done.Completed) { throw "Resume failed: $mode" }
        }
        Assert-Refused { Invoke-OcrRecoveryFlow $state ('B'*64) $ports -Resume } 'state-binding-changed'
    }
    $preflight=@{AssertBindings={throw 'changed-binding'};EnsureHold={throw 'Unexpected mutation'};StopOwners={throw 'Unexpected stop'}}
    Assert-Refused { Invoke-OcrRecoveryFlow (Join-Path $root 'preflight.json') ('A'*64) $preflight } 'changed-binding'
    if ([IO.File]::Exists((Join-Path $root 'preflight.json'))) { throw 'Refused preflight wrote deployment state.' }
    'OCR maintenance contracts passed: driver identity/incarnation/classification and deployment boundaries/replay.'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('FluxOcrMaintenance_')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
