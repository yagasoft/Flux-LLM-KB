[CmdletBinding(DefaultParameterSetName='Plan')]
param(
    [ValidatePattern('^SqlTests-[A-Za-z0-9_-]{1,48}$')][string]$StageName='SqlTests-20261014',
    [ValidatePattern('^fluxknowledge_staging_tests_[a-z0-9_]{1,48}$')][string]$Login='fluxknowledge_staging_tests_r1',
    [Parameter(ParameterSetName='Plan')][switch]$PlanOnly,
    [Parameter(Mandatory,ParameterSetName='Apply')][switch]$Apply
)
$ErrorActionPreference='Stop'
$root=Join-Path 'E:\Codex Workspaces\Staging\FluxKnowledge' $StageName
$fileRoot=Join-Path $root 'Data\Sql\Disposable'
$config=Join-Path $root 'Config\test-sql.json'
$receipt=Join-Path $root 'Config\provisioning-receipt.json'
function Assert-UnlinkedPath([string]$Path) {
    for ($cursor=[IO.Path]::GetFullPath($Path);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Disposable SQL storage is linked.'}
    }
}
Add-Type -AssemblyName System.Data
function New-TestConnection([string]$Password) {
    $builder=[System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    $builder['Data Source']='localhost';$builder['User ID']=$Login;$builder['Password']=$Password
    $builder['Encrypt']=$true;$builder['TrustServerCertificate']=$true
    return $builder.ConnectionString
}
[void](New-TestConnection 'synthetic-plan-check')
Assert-UnlinkedPath $root
$plan=[ordered]@{root=$root;file_root=$fileRoot;login=$Login;settings_file=$config;server_permissions=@('CREATE ANY DATABASE');excluded_permissions=@('sysadmin','dbcreator','CONTROL SERVER','ALTER ANY DATABASE');catalogue_prefix='FluxKnowledge_Phase1Tests_<guid>';purpose='Disposable test provisioning only; never the running staging app login';production_actions=@();cleanup='Fixture must verify exact generated name, creator ownership and file manifest before migration/drop; preserve uncertain outcomes.'}
if(-not $Apply){$plan|ConvertTo-Json -Depth 5;return}
if(Test-Path -LiteralPath $root){throw 'Create-only disposable SQL provisioning refuses an existing root.'}
$admin=[System.Data.SqlClient.SqlConnection]::new('Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5;Application Name=FluxKnowledge.TestProvisioner')
try {
    $admin.Open()
    $command=$admin.CreateCommand()
    $command.CommandText="SELECT CASE WHEN SUSER_ID(@login) IS NULL AND SERVERPROPERTY('IsFullTextInstalled')=1 AND SERVERPROPERTY('IsIntegratedSecurityOnly')=0 THEN 1 ELSE 0 END"
    [void]$command.Parameters.AddWithValue('@login',$Login)
    if($command.ExecuteScalar() -ne 1){throw 'Test principal exists or prerequisites are missing. No changes applied.'}
    $command.Dispose()
    [void](New-Item -ItemType Directory -Path $root)
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))){
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $root -AclObject $acl
    foreach($directory in @((Join-Path $root 'Config'),$fileRoot)){Assert-UnlinkedPath $directory;[void](New-Item -ItemType Directory -Path $directory -Force)}
    $sqlAcl=Get-Acl -LiteralPath $fileRoot
    $sqlAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('NT SERVICE\MSSQLSERVER','Modify','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $fileRoot -AclObject $sqlAcl
    $password='FkTests!'+[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    @{ServerConnectionString=(New-TestConnection $password);FileRoot=$fileRoot}|ConvertTo-Json|Set-Content -LiteralPath $config -Encoding utf8
    $plan['state']='prepared-before-login-create';$plan|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $receipt -Encoding utf8
    $command=$admin.CreateCommand()
    $command.CommandText=@'
DECLARE @sql nvarchar(max)=N'CREATE LOGIN '+QUOTENAME(@login)+N' WITH PASSWORD='+QUOTENAME(@password,'''')+N', DEFAULT_DATABASE=[master], CHECK_POLICY=ON, CHECK_EXPIRATION=OFF; GRANT CREATE ANY DATABASE TO '+QUOTENAME(@login)+N';';
EXEC sys.sp_executesql @sql;
'@
    [void]$command.Parameters.AddWithValue('@login',$Login);[void]$command.Parameters.AddWithValue('@password',$password)
    [void]$command.ExecuteNonQuery();$command.Dispose();$password=$null
    $plan['state']='provisioned';$plan['provisioned_utc']=[DateTimeOffset]::UtcNow.ToString('o')
    $plan|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $receipt -Encoding utf8
    @{status='provisioned';settings_file=$config;receipt=$receipt}|ConvertTo-Json
}
catch {
    if(Test-Path -LiteralPath $receipt){$plan['state']='failed-preserve-for-inspection';$plan['failure_type']=$_.Exception.GetType().FullName;$plan|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $receipt -Encoding utf8}
    throw
}
finally{$admin.Dispose()}
