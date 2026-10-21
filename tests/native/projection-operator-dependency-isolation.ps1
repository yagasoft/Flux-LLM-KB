param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('FluxProjectionLoader_'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    $payload=Join-Path $root 'payload';[void][IO.Directory]::CreateDirectory($payload)
    $entry=Join-Path $payload 'Operator.dll'
    Add-Type -TypeDefinition ('public class Operator_'+[Guid]::NewGuid().ToString('N')+' {}') -OutputAssembly $entry
    $hostCopy=Join-Path $root 'Private.dll'
    Add-Type -TypeDefinition ('public class Private_'+[Guid]::NewGuid().ToString('N')+' {}') -OutputAssembly $hostCopy
    $hostBytes=[IO.MemoryStream]::new([IO.File]::ReadAllBytes($hostCopy))
    try{$preloaded=[Runtime.Loader.AssemblyLoadContext]::Default.LoadFromStream($hostBytes)}finally{$hostBytes.Dispose()}
    $name=$preloaded.GetName()
    $privateName=$name.Name+'.dll'
    $target='.NETCoreApp,Version=v10.0'
    $dependencies=@{runtimeTarget=@{name=$target};targets=@{$target=@{'Private/1.0.0'=@{
        runtime=@{$privateName=@{}};native=@{'runtimes/win-x64/native/fk-operator-private.dll'=@{}}
    }}};libraries=@{'Private/1.0.0'=@{type='project';serviceable=$false;sha512=''}}}
    [IO.File]::WriteAllText((Join-Path $payload 'Operator.deps.json'),($dependencies | ConvertTo-Json -Depth 10))
    Add-Type -Path (Join-Path $SourceRoot 'scripts/deploy/ProjectionOperatorLoadContext.cs')
    $context=[FluxKnowledge.Deployment.ProjectionOperatorLoadContext]::new($entry,$root)
    $failures=[Collections.Generic.List[string]]::new()
    try {
        [void]$context.LoadFromAssemblyName($name)
        $failures.Add('A missing declared private assembly was substituted from the host.')
    } catch {
        if($_.Exception.GetBaseException().Message -notlike '*index-projection-declared-dependency-missing*'){throw}
    }
    $native=$context.GetType().GetMethod('LoadUnmanagedDll',[Reflection.BindingFlags]'Instance,NonPublic')
    try {
        [void]$native.Invoke($context,@('fk-operator-private'))
        $failures.Add('A missing declared native asset allowed OS fallback.')
    } catch {
        if($_.Exception.GetBaseException().Message -notlike '*index-projection-declared-dependency-missing*'){throw}
    }
    # Core framework and undeclared OS libraries retain their normal host paths.
    if($context.LoadFromAssemblyName([object].Assembly.GetName()) -ne [object].Assembly){throw 'Core framework fallback changed.'}
    if($native.Invoke($context,@('kernel32')) -ne [IntPtr]::Zero){throw 'OS fallback changed.'}
    if($failures.Count){throw ($failures -join ' ')}
    'PASS: missing declared managed/native assets refuse host substitution; framework/OS fallback remains available.'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cnotlike 'FluxProjectionLoader_*'){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
