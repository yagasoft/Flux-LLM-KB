using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class LegacyStagingRecoveryProof
{
    private void ValidateApplicationAndConfigurationRoles()
    {
        var stage=Path.GetDirectoryName(Scope.RecoveryRoot)!;var application=Path.Combine(stage,"App");
        foreach(var folder in _plan.Files.Select(f=>FirstComponent(f.RelativePath)).Distinct(StringComparer.Ordinal).Where(n=>n.StartsWith("App-before-",StringComparison.Ordinal)))
        {
            var files=_plan.Files.Where(f=>FirstComponent(f.RelativePath)==folder).ToArray();
            var root=Path.Combine(Scope.RecoveryRoot,folder);
            string Relative(RecoveryLegacyFile f)=>f.RelativePath[(folder.Length+1)..].Replace('\\','/');
            var manifest=files.OrderBy(Relative,StringComparer.Ordinal).Select(f=>$"{Relative(f)}|{f.Bytes}|{f.Sha256.ToLowerInvariant()}").ToArray();
            var manifestHash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',manifest))));
            var kind=folder=="App-before-M32"?"application-m32":folder=="App-before-model-admission"?"application-model":"application-keyword";
            var provenance=_evidence[kind].RootElement;
            if(kind=="application-m32")
            {
                // This is a fresh publish-tree/restore-role observation, not a claim
                // that historical per-file hashes were recorded when it was copied.
                if(G(provenance,"Kind").GetString()!="observed-published-application-only" ||
                    !SamePath(G(provenance,"PayloadRoot").GetString()!,root) || !SamePath(G(provenance,"RestoreTarget").GetString()!,application) ||
                    !string.Equals(G(provenance,"FileManifestSha256").GetString(),manifestHash,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("index-recovery-legacy-application-role-unproved");
            }
            else if(kind=="application-model")
            {
                if(!SamePath(G(provenance,"Source").GetString()!,application) || !SamePath(G(provenance,"Destination").GetString()!,root) ||
                    G(provenance,"Files").GetInt32()!=files.Length || G(provenance,"Bytes").GetDecimal()!=files.Sum(f=>(decimal)f.Bytes) ||
                    !G(provenance,"AllCopiedHashesMatched").GetBoolean() || G(provenance,"ModelsCopied").GetBoolean() ||
                    G(provenance,"PreservedUtc").GetDateTimeOffset()>=Scope.FixtureCreatedUtc)
                    throw new InvalidDataException("index-recovery-legacy-application-preservation-unproved");
            }
            else
            {
                if(!SamePath(G(provenance,"source").GetString()!,application) || !SamePath(G(provenance,"preserved").GetString()!,root) ||
                    !G(provenance,"allCopiesVerified").GetBoolean() || G(provenance,"createdUtc").GetDateTimeOffset()>=Scope.FixtureCreatedUtc)
                    throw new InvalidDataException("index-recovery-legacy-application-preservation-unproved");
                var preserved=G(provenance,"files").EnumerateArray().ToArray();
                if(preserved.Length!=files.Length || files.Any(f=>!preserved.Any(p=>G(p,"relativePath").GetString()!.Replace('\\','/')==Relative(f) &&
                    G(p,"bytes").GetDecimal()==f.Bytes && string.Equals(G(p,"sha256").GetString(),f.Sha256,StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("index-recovery-legacy-application-historical-hash-mismatch");
            }
            foreach(var file in files)
            {
                var relative=Relative(file);var extension=Path.GetExtension(relative).ToLowerInvariant();
                var code=extension is ".dll" or ".exe" or ".pdb" or ".so" or ".dylib" or ".lib" or ".css" or ".js";
                var compressed=extension is ".br" or ".gz" && (relative.EndsWith(".js"+extension,StringComparison.Ordinal) || relative.EndsWith(".css"+extension,StringComparison.Ordinal)) &&
                    files.Any(f=>Relative(f)==relative[..^extension.Length]);
                var adapter=relative=="Documents/paddleocr_vl_executor.py";
                var configuration=relative=="web.config";
                var metadata=relative=="appsettings.json" || relative is "FluxKnowledge.Cli.deps.json" or "FluxKnowledge.Cli.runtimeconfig.json" or
                    "FluxKnowledge.StagingHost.deps.json" or "FluxKnowledge.StagingHost.runtimeconfig.json" or "FluxKnowledge.StagingHost.staticwebassets.endpoints.json" or
                    "FluxKnowledge.Web.deps.json" or "FluxKnowledge.Web.runtimeconfig.json";
                if(!code && !compressed && !adapter && !configuration && !metadata)
                    throw new InvalidDataException("index-recovery-legacy-application-extra-data-or-reference-metadata");
                if(metadata)
                {
                    using var json=ReadRoleJson(Path.Combine(Scope.RecoveryRoot,file.RelativePath));
                    RejectGenerationSelectors(json.RootElement);
                }
            }
            using var webFile=File.OpenRead(Path.Combine(root,"web.config"));
            if(webFile.Length>65536)throw new InvalidDataException("index-recovery-legacy-web-configuration-bound");
            using var xml=XmlReader.Create(webFile,new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});
            var web=XDocument.Load(xml);var host=web.Descendants("aspNetCore").Single();
            var entryPoint=(string?)host.Attribute("arguments") switch
            {".\\FluxKnowledge.StagingHost.dll"=>"FluxKnowledge.StagingHost",".\\FluxKnowledge.Web.dll"=>"FluxKnowledge.Web",_=>null};
            if((string?)host.Attribute("processPath")!="dotnet" || entryPoint is null)
                throw new InvalidDataException("index-recovery-legacy-application-restore-entry-unproved");
            foreach(var entry in new[]{"FluxKnowledge.Web",entryPoint}.Distinct(StringComparer.Ordinal))
                if(!files.Any(f=>Relative(f)==entry+".dll" && f.Bytes is >0 and <=67108864) || AssemblyName.GetAssemblyName(Path.Combine(root,entry+".dll")).Name!=entry)
                    throw new InvalidDataException("index-recovery-legacy-published-entry-unproved");
            using var deps=ReadRoleJson(Path.Combine(root,entryPoint+".deps.json"));
            var target=G(G(deps.RootElement,"runtimeTarget"),"name").GetString()!;
            var projects=G(G(deps.RootElement,"targets"),target).EnumerateObject().Select(p=>p.Name).ToArray();
            if(!projects.Any(p=>p.StartsWith(entryPoint+"/",StringComparison.Ordinal)) || !projects.Any(p=>p.StartsWith("FluxKnowledge.Web/",StringComparison.Ordinal)))
                throw new InvalidDataException("index-recovery-legacy-publish-dependencies-unproved");
            using var runtime=ReadRoleJson(Path.Combine(root,entryPoint+".runtimeconfig.json"));
            if(G(G(runtime.RootElement,"runtimeOptions"),"tfm").GetString()!="net10.0")throw new InvalidDataException("index-recovery-legacy-publish-runtime-unproved");
        }
        using var saved=ReadRoleJson(Path.Combine(Scope.RecoveryRoot,"staging-before-ann512.json"));
        var allowed=new[]{"Origin","DashboardReadBenchmark","SqlConnectionString","CpuSemantic","SourceJournalEnabled","IndexPublicationMode","CpuRerankerShards","CpuRerankerThreads","AnnSearchExpansion"};
        var settings=saved.RootElement;
        if(settings.EnumerateObject().Any(p=>!allowed.Contains(p.Name,StringComparer.Ordinal)) ||
            settings.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=settings.EnumerateObject().Count() ||
            !G(settings,"DashboardReadBenchmark").GetBoolean() || !G(settings,"CpuSemantic").GetBoolean() || G(settings,"SourceJournalEnabled").GetBoolean() ||
            G(settings,"IndexPublicationMode").GetString()!="Segmented" || G(settings,"CpuRerankerShards").GetInt32() is not(1 or 4) ||
            G(settings,"CpuRerankerThreads").GetInt32() is not(1 or 4) ||
            !Uri.TryCreate(G(settings,"Origin").GetString(),UriKind.Absolute,out var origin) || origin.Scheme!="http" || origin.Host!="127.0.0.1" || origin.Port==5137)
            throw new InvalidDataException("index-recovery-legacy-saved-configuration-role-invalid");
        var connection=new SqlConnectionStringBuilder(G(settings,"SqlConnectionString").GetString());
        if(!string.Equals(connection.DataSource,"localhost",StringComparison.OrdinalIgnoreCase) || connection.InitialCatalog!=Scope.Catalogue)
            throw new InvalidDataException("index-recovery-legacy-saved-configuration-binding-invalid");
        if(settings.TryGetProperty("AnnSearchExpansion",out var expansion) && expansion.GetInt32() is not(256 or 512 or 1024 or 2048))
            throw new InvalidDataException("index-recovery-legacy-saved-configuration-value-invalid");
        RejectGenerationSelectors(settings);
    }

    private static JsonDocument ReadRoleJson(string path)
    {
        using var file=File.OpenRead(path);
        if(file.Length is <1 or >8388608)throw new InvalidDataException("index-recovery-legacy-role-metadata-bound");
        return JsonDocument.Parse(file,new(){MaxDepth=64});
    }

    private void RejectGenerationSelectors(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
            foreach(var property in value.EnumerateObject())
            {
                if(new[]{"GenerationId","IndexGeneration","IndexGenerationId","SegmentId","ManifestId","CorpusVersion"}.Contains(property.Name,StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("index-recovery-legacy-configuration-reference-selector");
                RejectGenerationSelectors(property.Value);
            }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())RejectGenerationSelectors(item);
        else if(value.ValueKind==JsonValueKind.String)
        {
            var text=value.GetString()!;
            if(text.Contains(Scope.GenerationId.ToString("N"),StringComparison.OrdinalIgnoreCase) || text.Contains(Scope.GenerationId.ToString("D"),StringComparison.OrdinalIgnoreCase) ||
                text.Contains(Scope.SegmentId,StringComparison.OrdinalIgnoreCase) || text.Replace('/','\\').Contains("\\manifests\\",StringComparison.OrdinalIgnoreCase) ||
                text.Replace('/','\\').Contains("\\generations\\",StringComparison.OrdinalIgnoreCase) || text.Replace('/','\\').Contains("\\segments\\",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("index-recovery-legacy-configuration-reference-selector");
        }
    }
}
