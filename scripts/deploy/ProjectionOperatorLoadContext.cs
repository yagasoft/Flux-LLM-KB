using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace FluxKnowledge.Deployment;

// A private dependency context prevents PowerShell's already-loaded package
// versions from silently becoming the reviewed operator's implementation.
public sealed class ProjectionOperatorLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _root;
    private readonly string _framework;
    private readonly HashSet<string> _managed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _native = new(StringComparer.OrdinalIgnoreCase);
    public ProjectionOperatorLoadContext(string entry, string framework) : base("FluxKnowledge.ProjectionOperator." + Guid.NewGuid().ToString("N"), false)
    {
        _root = Path.GetDirectoryName(Path.GetFullPath(entry)) + Path.DirectorySeparatorChar;
        _resolver = new AssemblyDependencyResolver(entry);
        _framework = Path.GetFullPath(framework);
        using var file = File.OpenRead(Path.ChangeExtension(entry, ".deps.json"));
        if (file.Length > 16 * 1024 * 1024) throw new InvalidOperationException("index-projection-dependencies-invalid");
        using var dependencies = JsonDocument.Parse(file);
        var target = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString();
        foreach (var library in dependencies.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
        {
            foreach (var group in library.Value.EnumerateObject())
            {
                if (group.Name != "runtime" && group.Name != "native" && group.Name != "resources" && group.Name != "runtimeTargets") continue;
                foreach (var asset in group.Value.EnumerateObject())
                {
                    var kind = group.Name == "runtimeTargets" ? asset.Value.GetProperty("assetType").GetString() : group.Name;
                    var filename = Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (kind == "native") _native.Add(NativeName(filename));
                    else if (kind == "runtime" || kind == "resources") _managed.Add(Path.GetFileNameWithoutExtension(filename));
                }
            }
        }
    }
    private string Contained(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("index-projection-operator-dependency-outside-payload");
        return path;
    }
    protected override Assembly Load(AssemblyName name)
    {
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path != null) return LoadFromAssemblyPath(Contained(path));
        // PowerShell hosts NETCore only. The CLI also declares AspNetCore;
        // resolve its reference assemblies from the selected installed runtime.
        var shared = Path.Combine(_framework, name.Name + ".dll");
        if (!File.Exists(shared))
        {
            if (_managed.Contains(name.Name)) throw Missing(name.Name);
            return null;
        }
        var actual = AssemblyName.GetAssemblyName(shared);
        if (actual.Name != name.Name || actual.Version != name.Version ||
            !System.Linq.Enumerable.SequenceEqual(actual.GetPublicKeyToken() ?? Array.Empty<byte>(), name.GetPublicKeyToken() ?? Array.Empty<byte>()))
            throw new InvalidOperationException("index-projection-framework-identity-mismatch");
        return LoadFromAssemblyPath(shared);
    }
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        if (path == null && _native.Contains(NativeName(name)))
            throw Missing(name);
        if (path == null) return IntPtr.Zero;
        path = Contained(path);
        // LoadLibrary's ordinary path limit is shorter than managed file I/O's.
        // Keep the validated payload path when a staging directory is long.
        if (OperatingSystem.IsWindows() && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        return LoadUnmanagedDllFromPath(path);
    }
    private static InvalidOperationException Missing(string name)
    {
        var failure = new InvalidOperationException("index-projection-declared-dependency-missing");
        failure.Data["Dependency"] = name;
        return failure;
    }
    private static string NativeName(string name)
    {
        name = Path.GetFileName(name);
        foreach (var suffix in new[] { ".dll", ".dylib", ".so" })
        {
            var position = name.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (position >= 0) { name = name.Substring(0, position); break; }
        }
        return name.StartsWith("lib", StringComparison.OrdinalIgnoreCase) ? name.Substring(3) : name;
    }
}
