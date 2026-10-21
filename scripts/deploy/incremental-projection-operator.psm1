Set-StrictMode -Version Latest

function Get-ProjectionOperatorFramework([string]$OperatorRoot) {
    $runtime=Get-Content -LiteralPath (Join-Path $OperatorRoot 'FluxKnowledge.Cli.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworks=@($runtime.runtimeOptions.frameworks | Where-Object name -CEQ 'Microsoft.AspNetCore.App')
    if($frameworks.Count -ne 1){throw 'index-projection-framework-declaration-invalid'}
    $required=[version]$frameworks[0].version
    if($required.Major -ne [Environment]::Version.Major -or $required.Minor -ne [Environment]::Version.Minor){throw 'index-projection-framework-host-incompatible'}
    $dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $parent=Join-Path (Split-Path -Parent $dotnet) 'shared/Microsoft.AspNetCore.App'
    if(!(Test-Path -LiteralPath $parent -PathType Container)){throw 'index-projection-framework-not-installed'}
    $available=@(Get-ChildItem -LiteralPath $parent -Directory | ForEach-Object {
        $version=$null
        if([version]::TryParse($_.Name,[ref]$version) -and $version.Major -eq $required.Major -and $version.Minor -eq $required.Minor -and $version -ge $required) {
            [pscustomobject]@{Version=$version;Path=$_.FullName}
        }
    } | Sort-Object Version -Descending)
    if(!$available.Count){throw 'index-projection-framework-not-installed'}
    for($path=$available[0].Path;$path;$path=[IO.Path]::GetDirectoryName($path)) {
        if([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint){throw 'index-projection-framework-path-unsafe'}
    }
    return $available[0].Path
}

function Invoke-ProjectionOperatorBridge {
    param([Parameter(Mandatory)][string]$OperatorRoot,
        [Parameter(Mandatory)][ValidateSet('BeginProduction','BeginForIsolatedTests')][string]$BeginMethod,
        [Parameter(Mandatory)][object[]]$BeginArguments,[ValidateRange(1,3600)][int]$TimeoutSeconds=1800)
    if($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 10){throw 'index-projection-operator-requires-pwsh-net10'}
    $entry=Join-Path ([IO.Path]::GetFullPath($OperatorRoot)) 'FluxKnowledge.Cli.dll'
    for($path=$entry;$path;$path=[IO.Path]::GetDirectoryName($path)) {
        if([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint){throw 'index-projection-operator-path-unsafe'}
    }
    $bindingIndex=if($BeginMethod -ceq 'BeginProduction'){0}else{3}
    $bindings=[string]$BeginArguments[$bindingIndex] | ConvertFrom-Json
    $file=[IO.File]::Open($entry,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $capability=$null;$stage='load'
    try {
        if($file.Length -gt 64MB -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file)) -ine $bindings.OperatorEntrySha256){throw 'index-projection-operator-binding-changed'}
        if(!('FluxKnowledge.Deployment.ProjectionOperatorLoadContext' -as [type])) {
            Add-Type -Path (Join-Path $PSScriptRoot 'ProjectionOperatorLoadContext.cs') -ErrorAction Stop
        }
        $framework=Get-ProjectionOperatorFramework $OperatorRoot
        $context=[FluxKnowledge.Deployment.ProjectionOperatorLoadContext]::new($entry,$framework)
        $assembly=$context.LoadFromAssemblyPath($entry)
        $bridge=$assembly.GetType('FluxKnowledge.Cli.Commands.IncrementalProjectionBridge',$true)
        $flags=[Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
        $stage='bind'
        $arguments=[object[]]::new($BeginArguments.Count)
        for($index=0;$index -lt $arguments.Count;$index++) {
            $arguments[$index]=$BeginArguments[$index].PSObject.BaseObject
        }
        $capability=$bridge.GetMethod($BeginMethod,$flags).Invoke($null,$arguments)
        $stage='run'
        $json=$bridge.GetMethod('Run',$flags).Invoke($null,@($capability,$TimeoutSeconds))
        return ($json | ConvertFrom-Json)
    } catch {
        $cause=$_.Exception.GetBaseException()
        Write-Verbose ('Projection operator failure: stage='+$stage+'; type='+$cause.GetType().FullName)
        if($cause.Data.Contains('Dependency') -and $cause.Data['Dependency'] -cmatch '^[A-Za-z0-9_.-]{1,160}$') {
            Write-Verbose ('Missing declared operator dependency: '+$cause.Data['Dependency'])
        }
        if($cause -is [IO.FileNotFoundException] -and $cause.FileName -cmatch '^[A-Za-z0-9_.]+, Version=[0-9.]+, Culture=neutral, PublicKeyToken=[a-f0-9]+$') {
            Write-Verbose ('Missing operator dependency: '+$cause.FileName)
        }
        if($cause -is [DllNotFoundException]) {
            Write-Verbose ('Native operator dependency could not load: '+$cause.Message)
        }
        $reason=$cause.Message
        if($reason -cnotmatch '^index-(projection|rebuild|storage)-[a-z-]{1,120}$'){$reason='index-projection-operator-failed-reconcile-before-resume'}
        throw [InvalidOperationException]::new($reason)
    } finally {
        try{if($null -ne $capability){([IDisposable]$capability).Dispose()}}
        finally{$file.Dispose()}
    }
}

function Invoke-IncrementalProjectionOperator {
    param([Parameter(Mandatory)][string]$OperatorRoot,[Parameter(Mandatory)][string]$BindingsJson,
        [Parameter(Mandatory)][scriptblock]$AssertQuiescent,[ValidateRange(1,3600)][int]$TimeoutSeconds=1800)
    # The public updater surface always selects fixed production roots. The test
    # entry is private and the managed bridge independently refuses live paths.
    Invoke-ProjectionOperatorBridge -OperatorRoot $OperatorRoot -BeginMethod 'BeginProduction' `
        -BeginArguments @($BindingsJson,[Action]$AssertQuiescent) -TimeoutSeconds $TimeoutSeconds
}

Export-ModuleMember -Function Invoke-IncrementalProjectionOperator
