Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'incremental-source-access-config.psm1')
Import-Module (Join-Path $PSScriptRoot 'incremental-hybrid-passage-rebuild.psm1')
Import-Module (Join-Path $PSScriptRoot 'incremental-source-journal-migration.psm1')
# The existing helper shutdown callbacks run in dynamic script modules and use
# module-qualified commands. Register the protected module in this runner session.
Import-Module (Join-Path $PSScriptRoot 'incremental-source-journal-helper-task.psm1') -Global
Import-Module (Join-Path $PSScriptRoot 'index-format-admission.psm1')
. (Join-Path $PSScriptRoot 'source-access-install-file.ps1')

function Get-AllowlistTargets([Collections.IDictionary]$Binding) {
    $fields=@('Version','OperatorOrigin','DashboardOrigin','OperatorSids','ReceiptDirectory','StageName','DatabaseIdentity',
        'JournalPipeName','WebSha256','HelperSha256','OutlookSha256','TaskDefinitionSha256','TaskSecuritySha256','StageVolumes')
    if($Binding.Count -ne $fields.Count -or @($fields|Where-Object {$_ -cnotin $Binding.Keys}).Count -or
        $Binding.Version -isnot [int] -and $Binding.Version -isnot [long] -or $Binding.Version -notin 1,2) {throw 'binding-invalid'}
    $origins=@{OperatorOrigin='localhost';DashboardOrigin='127.0.0.1'}
    foreach($key in $origins.Keys){
        $pattern='\Ahttp://'+[regex]::Escape($origins[$key])+':([1-9][0-9]{0,4})\z'
        if($Binding[$key] -isnot [string] -or $Binding[$key] -cnotmatch $pattern -or
            [int]$Matches[1] -notin 1024..65535){throw 'binding-origin-invalid'}
    }
    $operatorPort=([uri]$Binding.OperatorOrigin).Port
    if($operatorPort -eq 5137 -or $operatorPort -eq ([uri]$Binding.DashboardOrigin).Port){throw 'binding-origin-invalid'}
    $sids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if($Binding.OperatorSids -isnot [array] -or $Binding.OperatorSids.Count -notin 1..64){throw 'binding-sids-invalid'}
    foreach($sid in $Binding.OperatorSids){
        if($sid -isnot [string] -or $sid -cnotmatch '\AS-1-(?:5-21|12-1)(?:-[0-9]{1,10}){4}\z' -or ([Security.Principal.SecurityIdentifier]::new($sid)).Value -cne $sid -or !$sids.Add($sid)){throw 'binding-sids-invalid'}
    }
    foreach($key in @('WebSha256','HelperSha256','OutlookSha256','TaskDefinitionSha256','TaskSecuritySha256')){
        if($Binding[$key] -isnot [string] -or $Binding[$key] -cnotmatch '\A[A-F0-9]{64}\z'){throw 'binding-digest-invalid'}
    }
    if($Binding.StageName -isnot [string] -or $Binding.StageName -cnotmatch '\A[A-Za-z0-9]{0,40}\z'){throw 'binding-stage-invalid'}
    $stage=$Binding.StageName
    if($Binding.StageVolumes -isnot [array]){throw 'binding-stage-volumes-invalid'}
    if($stage){
        if($Binding.StageVolumes.Count -ne 2){throw 'binding-stage-volumes-invalid'}
        $null=Get-AllowlistVolumeSet $Binding.StageVolumes
        $operator="I:\FluxKnowledgeSourceJournalOperator-Staging-$stage"
        $root="I:\Codex Workspaces\Staging\FluxKnowledge\$stage"
        $helper="I:\FluxKnowledgeSourceAccess-Staging-$stage";$site="FluxKnowledge-Staging-$stage"
        $catalog="FluxKnowledge_Staging_$stage";$task="FluxKnowledge.SourceAccess-Staging-$stage"
        $outlook=Join-Path $root 'OutlookHost'
        if($Binding.JournalPipeName -cne "FluxKnowledge.SourceJournal.Staging-$stage" -or
            $Binding.DashboardOrigin -ceq 'http://127.0.0.1:5137'){throw 'binding-production-overlap'}
    }else{
        if($Binding.StageVolumes.Count){throw 'binding-production-overlap'}
        $operator='I:\FluxKnowledgeSourceJournalOperator'
        $root='I:\FluxKnowledge';$helper='I:\FluxKnowledgeSourceAccess';$site='FluxKnowledge';$catalog='FluxKnowledge'
        $outlook='C:\inetpub\FluxKnowledge\outlook-host';$task='FluxKnowledge.SourceAccess'
        if($Binding.DashboardOrigin -cne 'http://127.0.0.1:5137' -or $Binding.JournalPipeName -isnot [string] -or
            $Binding.JournalPipeName -cnotmatch '\AFluxKnowledge\.SourceJournal\.[A-Za-z0-9.-]{1,96}\z' -or
            $Binding.JournalPipeName -imatch '\.Staging-'){throw 'binding-production-target-invalid'}
    }

    if($Binding.ReceiptDirectory -ine (Join-Path $operator 'Receipts')){throw 'binding-receipt-root-invalid'}
    $database=$Binding.DatabaseIdentity -split '\|';$incarnation=[guid]::Empty
    if($Binding.DatabaseIdentity -isnot [string] -or $database.Count -ne 3 -or $database[0] -cnotmatch '\A[A-Za-z0-9_.\\-]{1,128}\z' -or
        $database[1] -cne $catalog -or ![guid]::TryParseExact($database[2],'D',[ref]$incarnation) -or $incarnation -eq [guid]::Empty){throw 'binding-database-invalid'}
    @{LiveRoot=$root;Stage=$stage;Site=$site;Helper=$helper;Task=$task;Catalog=$catalog;Server=$database[0];Outlook=$outlook;
        Web=(Join-Path $root 'App');AppConfig=(Join-Path $root 'Config\appsettings.Production.json');Policy=(Join-Path $helper 'journal-policy.json');
        Hold=(Join-Path $root 'Runtime\deployment-validation-hold.json');BindingPath=(Join-Path $operator 'binding.json')}
}

function Read-AllowlistFile([string]$Path,[int]$Limit=2097152,[switch]$Private){
    for($parent=[IO.Path]::GetDirectoryName($Path);$parent;$parent=[IO.Path]::GetDirectoryName($parent)){
        if([IO.File]::GetAttributes($parent) -band [IO.FileAttributes]::ReparsePoint){throw 'linked-target'}
    }
    $file=[FluxSourceAccessInstallFile]::Read($Path,$Limit)
    try{
        $acl=Get-Acl -LiteralPath $Path
        [FluxSourceAccessInstallFile]::ValidateDescriptor($acl.GetSecurityDescriptorBinaryForm(),$false)
        if($Private){Assert-AllowlistPrivateAcl $acl}
        if(!$file.Length){throw 'empty-config'}
        $bytes=[byte[]]::new($file.Length);$file.ReadExactly($bytes);return ,$bytes
    }finally{$file.Dispose()}
}

function Assert-AllowlistPrivateAcl($Acl){
    foreach($rule in $Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -cnotin @('S-1-5-18','S-1-5-32-544')){
            throw 'operator-storage-not-private'
        }
    }
}

function Read-SourceJournalAllowlistOperatorBinding {
    param([Parameter(Mandatory)][string]$Path)
    if($Path -ine 'I:\FluxKnowledgeSourceJournalOperator\binding.json' -and
        $Path -inotmatch '\AI:\\FluxKnowledgeSourceJournalOperator-Staging-[A-Za-z0-9]{1,40}\\binding\.json\z'){throw 'binding-path-invalid'}
    [FluxSourceAccessInstallFile]::ValidateDescriptor((Get-Acl -LiteralPath 'I:\').GetSecurityDescriptorBinaryForm(),$true)
    $parent=[IO.Path]::GetDirectoryName($Path)
    $lease=[FluxSourceAccessInstallFile]::OpenPrivateDirectory($parent)
    try{
        Assert-AllowlistPrivateAcl (Get-Acl -LiteralPath $parent)
        $binding=ConvertFrom-UniqueConfigurationJson (Read-AllowlistFile $Path 32768 -Private)
        $targets=Get-AllowlistTargets $binding
        if($targets.BindingPath -ine $Path){throw 'binding-location-mismatch'}
        $receipt=[FluxSourceAccessInstallFile]::OpenPrivateDirectory($binding.ReceiptDirectory)
        try{Assert-AllowlistPrivateAcl (Get-Acl -LiteralPath $binding.ReceiptDirectory)}finally{$receipt.Dispose()}
        return $binding
    }finally{$lease.Dispose()}
}

function Get-AllowlistVolumeSet($Volumes){
    $set=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if($Volumes -isnot [array] -or $Volumes.Count -notin 1..16){throw 'volume-set-invalid'}
    foreach($volume in $Volumes){
        if($volume -isnot [string] -or $volume -inotmatch '\A\\\\\?\\Volume\{[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}\\\z' -or !$set.Add($volume)){throw 'volume-set-invalid'}
    }
    return ,$set
}

function Get-AllowlistConnection($Settings,$Targets){
    $normalised=$Settings.ConnectionStrings.FluxKnowledge -replace '(?i)(^|;)\s*Trust Server Certificate\s*=','$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=','$1ConnectRetryCount='
    $sql=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
    if($sql.InitialCatalog -cne $Targets.Catalog -or $sql.DataSource -inotIn @('localhost','.','(local)',$env:COMPUTERNAME) -or
        $sql.AttachDBFilename -or $sql.FailoverPartner -or $sql.UserInstance){throw 'config-database-target-invalid'}
    if($Targets.Stage){
        $staging=ConvertFrom-UniqueConfigurationJson (Read-AllowlistFile (Join-Path $Targets.LiveRoot 'Config\staging.json'))
        if($Settings.ConnectionStrings.FluxKnowledge -cne $staging.SqlConnectionString -or $sql.DataSource -ine 'localhost' -or
            $sql.IntegratedSecurity -or !$sql.UserID -or !$sql.Password){throw 'config-staging-database-mismatch'}
    }
    $sql['Pooling']=$false;$sql['ConnectRetryCount']=0;$sql['Connect Timeout']=5
    return $sql.ConnectionString
}

function New-AllowlistPair([hashtable]$Before,[string[]]$ApprovedVolumes){
    $null=Get-AllowlistVolumeSet $ApprovedVolumes
    $out=@{AppVolumes=$ApprovedVolumes;HelperVolumes=$ApprovedVolumes}
    foreach($field in @('AppBytes','HelperBytes')){
        $null=ConvertFrom-UniqueConfigurationJson $Before[$field]
        $json=[Text.Json.Nodes.JsonNode]::Parse([Text.Encoding]::UTF8.GetString($Before[$field]).TrimStart([char]0xfeff))
        $section=if($field -ceq 'AppBytes'){,$json['SourceJournal']}else{,$json}
        if($section -isnot [Text.Json.Nodes.JsonObject] -or $section['ApprovedVolumes'] -isnot [Text.Json.Nodes.JsonArray]){throw 'policy-shape-invalid'}
        $array=[Text.Json.Nodes.JsonArray]::new()
        foreach($volume in $ApprovedVolumes){$array.Add([Text.Json.Nodes.JsonValue]::Create([string]$volume))}
        $section['ApprovedVolumes']=$array
        $out[$field]=[Text.Encoding]::UTF8.GetBytes($json.ToJsonString())
    }
    $out
}

Export-ModuleMember -Function Read-SourceJournalAllowlistOperatorBinding
. (Join-Path $PSScriptRoot 'source-journal-allowlist-owners.ps1')
Import-Module (Join-Path $PSScriptRoot 'source-journal-allowlist-transaction.psm1')

function Get-AllowlistSha([byte[]]$Bytes){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))}

function Get-AllowlistDescriptor($Acl){
    # ReplaceFileW may mark a DACL as auto-inherited after merging it. This
    # bookkeeping bit does not change its owner, ACEs or protection from inheritance.
    $descriptor=[Security.AccessControl.RawSecurityDescriptor]::new($Acl.GetSecurityDescriptorBinaryForm(),0)
    $descriptor.SetFlags($descriptor.ControlFlags -band (-bnot [Security.AccessControl.ControlFlags]::DiscretionaryAclAutoInherited))
    $descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]'Access,Owner,Group')
}

function Save-AllowlistFile([string]$Path,[byte[]]$Expected,[byte[]]$Replacement){
    $parent=[FluxSourceAccessInstallFile]::OpenPrivateDirectory([IO.Path]::GetDirectoryName($Path))
    $temp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try{
        $current=Read-AllowlistFile $Path
        if((Get-AllowlistSha $current) -cne (Get-AllowlistSha $Expected)){throw 'config-write-conflict'}
        $acl=Get-Acl -LiteralPath $Path

        $sddl=Get-AllowlistDescriptor $acl
        $write=[IO.FileSystemAclExtensions]::Create([IO.FileInfo]::new($temp),[IO.FileMode]::CreateNew,
            [Security.AccessControl.FileSystemRights]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough,$acl)
        try{$write.Write($Replacement);$write.Flush($true)}finally{$write.Dispose()}
        # Hold the predecessor against byte writes through the atomic NTFS replacement.
        # Only SYSTEM/admins may rename under the held directory, and all supported
        # privileged writers share the global incremental updater mutex.
        $held=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]'Read,Delete')
        try{
            if($held.Length -ne $Expected.Length -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($held)) -cne (Get-AllowlistSha $Expected)){throw 'config-write-conflict'}
            [IO.File]::Replace($temp,$Path,[NullString]::Value,$false)
        }finally{$held.Dispose()}
        if((Get-AllowlistSha (Read-AllowlistFile $Path)) -cne (Get-AllowlistSha $Replacement) -or
            (Get-AllowlistDescriptor (Get-Acl -LiteralPath $Path)) -cne $sddl){throw 'config-replacement-unconfirmed'}
    }finally{
        if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}
        $parent.Dispose()
    }
}

function Get-AllowlistTargetIdentity($Context){
    if(!$Context.Initialised){
        $task=Get-AllowlistTask $Context
        $state=@{InstallRoot=$Context.Target.Helper;TaskName=$Context.Target.Task;OriginalTask=$task}
        $Context.Native=New-SourceJournalHelperNativeTaskPorts -State $state -AssertApplicationStopped {throw 'unexpected-update-port'} -Probe {throw 'unexpected-update-port'}
        foreach($path in @($Context.Target.LiveRoot,[IO.Path]::GetDirectoryName($Context.Target.AppConfig),[IO.Path]::GetDirectoryName($Context.Target.Hold),$Context.Target.Helper)){
            $Context.Leases.Add([FluxSourceAccessInstallFile]::OpenPrivateDirectory($path))
        }
        foreach($payload in $Context.Payloads){
            if ($Context.Binding.Version -eq 2) {
                # Pin and check every replaceable directory from the volume to the role root.
                $volume = [IO.Path]::GetPathRoot($payload.Root)
                [FluxSourceAccessInstallFile]::ValidateDescriptor((Get-Acl -LiteralPath $volume).GetSecurityDescriptorBinaryForm(),$true)
                for ($dir = $payload.Root; $dir -ne $volume; $dir = [IO.Path]::GetDirectoryName($dir)) {
                    $servicedAncestor = $dir -cin @('C:\inetpub','C:\inetpub\FluxKnowledge')
                    $Context.Leases.Add([FluxSourceAccessInstallFile]::OpenPrivateDirectory($dir,$servicedAncestor))
                }
            }
            for($dir=$payload.Root;$dir;$dir=[IO.Path]::GetDirectoryName($dir)){
                if([IO.File]::GetAttributes($dir) -band [IO.FileAttributes]::ReparsePoint){throw 'linked-payload'}
            }
            $Context.Leases.Add([FluxSourceAccessInstallFile]::OpenPrivateDirectory($payload.Root))
            # Hold every reviewed file against writes and deletion until the operation ends.
            # Detect later directory additions independently of these immutable handles.
            $names=[Collections.Generic.List[string]]::new()
            foreach($item in @(Get-ChildItem -LiteralPath $payload.Root -Recurse -Force|Sort-Object FullName)){
                if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'linked-payload'}
                if ($item.PSIsContainer -and $Context.Binding.Version -eq 2) {
                    $Context.Leases.Add([FluxSourceAccessInstallFile]::OpenPrivateDirectory($item.FullName))
                }
                if(!$item.PSIsContainer){
                    $handle=[FluxSourceAccessInstallFile]::Read($item.FullName,[long]::MaxValue)
                    $Context.Leases.Add($handle);$names.Add($item.FullName)
                    if ($Context.Binding.Version -eq 2) {
                        [FluxSourceAccessInstallFile]::ValidateDescriptor((Get-Acl -LiteralPath $item.FullName).GetSecurityDescriptorBinaryForm(),$false)
                    }
                }
            }
            $entryPoint = if ($Context.Target.Stage -and $payload.Role -ceq 'Web') { 'FluxKnowledge.StagingHost.exe' } else { 'FluxKnowledge.' + $payload.Role + '.exe' }
            if ($Context.Binding.Version -eq 2 -and !(Test-Path -LiteralPath (Join-Path $payload.Root $entryPoint) -PathType Leaf)) {
                throw 'operator-role-entrypoint-missing'
            }
            $observed = Get-HybridPayloadFingerprint $payload.Root
            if ($Context.Binding.Version -eq 2) { $payload.Digest = $observed }
            elseif ($observed -cne $payload.Digest) { throw 'payload-hash-mismatch' }
            $payload.Names=$names.ToArray()
        }
        $Context.Initialised=$true
    }
    foreach($payload in $Context.Payloads){
        $items=@(Get-ChildItem -LiteralPath $payload.Root -Recurse -Force|Sort-Object FullName)
        if(@($items|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -or
            (@($items|Where-Object {!$_.PSIsContainer}|ForEach-Object FullName) -join '|') -cne ($payload.Names -join '|')){throw 'payload-files-drift'}
    }
    $manager=Get-AllowlistManager
    try{$null=Get-AllowlistIis $Context $manager}finally{$manager.Dispose()}
    $null=Get-AllowlistTask $Context
    $app=ConvertFrom-UniqueConfigurationJson (Read-AllowlistFile $Context.Target.AppConfig)
    $sql=Get-AllowlistConnection $app $Context.Target
    $db=Get-SourceJournalDatabaseState -ConnectionString $sql
    if($db.Identity -cne $Context.Binding.DatabaseIdentity){throw 'database-incarnation-mismatch'}
    Assert-SourceJournalSchema $db
    $parts=@($Context.BindingDigest,$db.Identity,($db.History -join ','),$db.SchemaSha256,$db.VectorPublicationIndexSha256,
        $db.WindowEligibilitySchemaSha256,$db.QueueIndexSha256)
    if ($Context.Binding.Version -eq 2) { $parts += @($Context.Payloads | ForEach-Object { $_.Role + '=' + $_.Digest }) }
    foreach($file in @('policy.json','root-read-plan.json')){
        $parts+=Get-AllowlistSha (Read-AllowlistFile (Join-Path $Context.Target.Helper $file) 2097152 -Private)
    }
    foreach($file in @($Context.Target.AppConfig,$Context.Target.Policy)){
        $parts+=(Get-AllowlistDescriptor (Get-Acl -LiteralPath $file))
    }
    return Get-AllowlistSha ([Text.Encoding]::UTF8.GetBytes(($parts -join '|')))
}

function Confirm-AllowlistAddedVolumes([hashtable]$Before,[string[]]$After){
    foreach($volume in $After){
        if($volume -iin $Before.AppVolumes){continue}
        $escaped=$volume.Replace('\','\\')
        $row=@(Get-CimInstance -ClassName Win32_Volume -Filter "DeviceID='$escaped'" -OperationTimeoutSec 5)
        if($row.Count -ne 1 -or $row[0].DeviceID -ine $volume -or $row[0].DriveType -ne 3 -or $row[0].FileSystem -ine 'NTFS'){throw 'new-volume-not-local-fixed-ntfs'}
    }
}

function New-AllowlistNativePorts($Context){
    # Explicit module invocation keeps these private functions in their owning
    # session state when the transaction calls a port after this factory returns.
    $owner=$ExecutionContext.SessionState.Module
    @{
        GetTargetIdentity={ & $owner {param($c) Get-AllowlistTargetIdentity $c} $Context }.GetNewClosure()
        ReadPair={ & $owner {param($c) Read-AllowlistPair $c} $Context }.GetNewClosure()
        BuildPair={ param($before,[string[]]$after) & $owner {param($c,$b,$a) Confirm-AllowlistAddedVolumes $b $a;Assert-AllowlistPredecessor $c;Invoke-AllowlistAdmission $c {};New-AllowlistPair $b $a} $Context $before $after }.GetNewClosure()
        GetAdmissionHeld={ & $owner {param($c) Test-AllowlistHold $c} $Context }.GetNewClosure()
        HoldAdmission={ & $owner {param($c) Set-AllowlistHold $c} $Context }.GetNewClosure()
        StopOwners={ & $owner {param($c) Stop-AllowlistOwners $c} $Context }.GetNewClosure()
        AssertOwnersStopped={ & $owner {param($c) Assert-AllowlistStopped $c} $Context }.GetNewClosure()
        WriteApp={param([byte[]]$expected,[byte[]]$replacement) & $owner {param($c,$e,$r) Assert-AllowlistHold $c;Save-AllowlistFile $c.Target.AppConfig $e $r} $Context $expected $replacement}.GetNewClosure()
        WriteHelper={param([byte[]]$expected,[byte[]]$replacement) & $owner {param($c,$e,$r) Assert-AllowlistHold $c;Save-AllowlistFile $c.Target.Policy $e $r} $Context $expected $replacement}.GetNewClosure()
        StartOwners={ & $owner {param($c) Start-AllowlistOwners $c} $Context }.GetNewClosure()
        VerifyTransport={param([string[]]$allowed,[string[]]$removed) & $owner {param($c,$a,$r) Confirm-AllowlistTransport $c $a $r} $Context $allowed $removed}.GetNewClosure()
        ReleaseAdmission={ & $owner {param($c) Invoke-AllowlistAdmission $c {Assert-AllowlistHold $c;[IO.File]::Delete($c.Target.Hold)}} $Context }.GetNewClosure()
    }
}

function Read-AllowlistBoundInstallation([hashtable]$Binding,$Targets){
    $validated=Read-SourceJournalAllowlistOperatorBinding $Targets.BindingPath
    foreach($key in $Binding.Keys){
        if((@($validated[$key]) -join '|') -cne (@($Binding[$key]) -join '|')){throw 'operator-binding-changed'}
    }
    return $validated
}

function ConvertTo-AllowlistResult($Receipt){
    switch -CaseSensitive ($Receipt.Status){
        'Completed' {return @{Status='succeeded';Code='configuration-verified'}}
        'Reverted' {return @{Status='rolled-back';Code='previous-configuration-verified'}}
        default {return @{Status='recovery-required';Code='review-protected-receipt'}}
    }
}

function Get-ConfiguredSourceJournalAllowlistResult {
    param([Parameter(Mandatory)][hashtable]$Binding,[Parameter(Mandatory)][guid]$OperationId,
        [Parameter(Mandatory)][string[]]$ExpectedApprovedVolumes,[Parameter(Mandatory)][string[]]$ApprovedVolumes)
    try{
        $targets=Get-AllowlistTargets $Binding
        $trusted=Read-AllowlistBoundInstallation $Binding $targets
        $lease=[FluxSourceAccessInstallFile]::OpenPrivateDirectory($trusted.ReceiptDirectory)
        try{
            $entry=Get-SourceJournalAllowlistTransactionResult -ReceiptRoot $trusted.ReceiptDirectory -OperationId $OperationId -ExpectedApprovedVolumes $ExpectedApprovedVolumes -ApprovedVolumes $ApprovedVolumes
            if($null -ne $entry -and $entry.Status -cin @('Completed','Reverted')){return ConvertTo-AllowlistResult $entry}
        }finally{$lease.Dispose()}
    }catch{ return $null }
    return $null
}

function Invoke-ConfiguredSourceJournalAllowlist {
    param([Parameter(Mandatory)][hashtable]$Binding,[Parameter(Mandatory)][guid]$OperationId,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ExpectedApprovedVolumes,[Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ApprovedVolumes)
    $mutex=$null;$locked=$false;$ctx=$null;$receiptPath=$null;$bindingLease=$null
    try{
        $targets=Get-AllowlistTargets $Binding
        if($OperationId -eq [guid]::Empty){throw 'invalid-operation-id'}
        $gate='Global\FluxKnowledge.IncrementalIisUpdate.v1'
        if($targets.Stage){
            $gate+='-Staging-'+$targets.Stage
            $permitted=Get-AllowlistVolumeSet $Binding.StageVolumes
            if(!$permitted.IsSupersetOf((Get-AllowlistVolumeSet $ExpectedApprovedVolumes)) -or
                !$permitted.IsSupersetOf((Get-AllowlistVolumeSet $ApprovedVolumes))){throw 'staging-volume-outside-binding'}
        }
        $mutex=[Threading.Mutex]::new($false,$gate)
        try{$locked=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$locked=$true}
        if(!$locked){return @{Status='rejected';Code='deployment-busy'}}
        $bindingLease=[FluxSourceAccessInstallFile]::Read($targets.BindingPath,32768)
        $validated=Read-AllowlistBoundInstallation $Binding $targets
        $id=$OperationId.ToString('D')
        $receiptPath=Join-Path $validated.ReceiptDirectory "source-journal-allowlist-$id.json"
        $ctx=@{Id=$id;Target=$targets;Binding=$validated;Native=$null;Initialised=$false;Release=('usn-allowlist-'+$id);
            ProcessPath=(Join-Path $validated.ReceiptDirectory ("processes-$id.json"));
            BindingDigest=(Get-AllowlistSha (Read-AllowlistFile $targets.BindingPath 32768 -Private));
            Leases=[Collections.Generic.List[IDisposable]]::new();Payloads=@(
                @{Role='Web';Root=$targets.Web;Digest=$validated.WebSha256},
                @{Role='Cli';Root=(Join-Path $targets.Helper 'payload');Digest=$validated.HelperSha256},
                @{Role='OutlookHost';Root=$targets.Outlook;Digest=$validated.OutlookSha256})}
        $ctx.Leases.Add([FluxSourceAccessInstallFile]::OpenPrivateDirectory($validated.ReceiptDirectory))
        $result=Invoke-SourceJournalAllowlistTransaction -OperationId $OperationId -ExpectedApprovedVolumes $ExpectedApprovedVolumes -ApprovedVolumes $ApprovedVolumes -ReceiptRoot $validated.ReceiptDirectory -Ports (New-AllowlistNativePorts $ctx)
        return ConvertTo-AllowlistResult $result
    }catch{
        if($receiptPath -and [IO.File]::Exists($receiptPath)){return @{Status='recovery-required';Code='review-protected-receipt'}}
        $reason=$_.Exception.Message
        $public=@('allowlist-stale-or-mismatched-volume-sets','allowlist-unresolved-operation-exists','new-volume-not-local-fixed-ntfs','operator-binding-changed',
            'staging-volume-outside-binding','helper-task-drift','payload-hash-mismatch','payload-files-drift','config-staging-database-mismatch',
            'database-incarnation-mismatch','configuration-binding-mismatch','iis-binding-invalid','iis-app-binding-invalid','iis-pool-shared',
            'iis-stage-host-invalid','iis-production-host-invalid','config-database-target-invalid','foreign-hold','operator-storage-not-private')
        return @{Status='rejected';Code=$(if($reason -cin $public){$reason}else{'native-preflight-rejected'})}
    }finally{
        if($ctx){
            if($ctx.Native){Close-SourceJournalHelperNativeTaskPorts $ctx.Native}
            foreach($lease in $ctx.Leases){$lease.Dispose()}
        }
        if($bindingLease){$bindingLease.Dispose()}
        if($locked){$mutex.ReleaseMutex()}
        if($mutex){$mutex.Dispose()}
    }
}
Export-ModuleMember -Function Read-SourceJournalAllowlistOperatorBinding,Invoke-ConfiguredSourceJournalAllowlist,Get-ConfiguredSourceJournalAllowlistResult
