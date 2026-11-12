using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluxKnowledge.Integrations.Files;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class LegacyStagingRecoveryProof
{
    private long _backupVersion,_publicationVersion;
    private Guid _epoch;

    private void ValidateLayout()
    {
        var stage=Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Scope.RecoveryRoot)))!;
        var parent=Path.GetDirectoryName(stage);
        if(!new[]{@"I:\Codex Workspaces\Staging\FluxKnowledge",@"E:\Codex Workspaces\Staging\FluxKnowledge"}.Contains(parent,StringComparer.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(stage),"^[A-Za-z0-9_-]{1,64}$",RegexOptions.CultureInvariant) ||
            !SamePath(Scope.RecoveryRoot,Path.Combine(stage,"Recovery")) || !SamePath(Scope.IndexRoot,Path.Combine(stage,"Data","Index")) ||
            !Regex.IsMatch(Scope.Catalogue,"^FluxKnowledge_(?:Staging_[A-Za-z0-9_]+|Phase1Tests_[0-9a-f]{32})$",RegexOptions.CultureInvariant) ||
            new[]{Scope.DatabaseIncarnation,Scope.DeletionOperationId,Scope.SourceRootId,Scope.FixtureOperationId,
                Scope.PublicationRecordId,Scope.SourceRevisionId,Scope.GenerationId}.Contains(Guid.Empty) ||
            Scope.ContentSha256.Length!=64 || !Scope.ContentSha256.All(Uri.IsHexDigit) || Scope.SegmentId.Length!=64 || !Scope.SegmentId.All(Uri.IsHexDigit))
            throw new InvalidOperationException("index-recovery-legacy-staging-binding-required");
        var roots=_plan.Files.Select(f=>FirstComponent(f.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(roots.Length!=6 || !new[]{"App-before-M32","App-before-model-admission","source-copy-only.bak","staging-before-ann512.json"}.All(roots.Contains) ||
            roots.Count(r=>Regex.IsMatch(r,"^App-before-keyword-literal-[0-9]{8}$",RegexOptions.CultureInvariant))!=1 ||
            roots.Count(r=>Regex.IsMatch(r,"^pre-m32-activation-[0-9a-f]{32}\\.bak$",RegexOptions.CultureInvariant))!=1 ||
            _plan.Evidence.Count is <8 or >12 || _plan.Evidence.Select(e=>e.Kind).Distinct(StringComparer.Ordinal).Count()!=_plan.Evidence.Count ||
            !new[]{"before-intake","publication","records","backup-source","backup-stage","application-m32","application-model","application-keyword"}
                .All(k=>_plan.Evidence.Any(e=>e.Kind==k)))
            throw new InvalidOperationException("index-recovery-exact-legacy-staging-inventory-required");
    }

    internal void ValidateConnection(FluxKnowledgeDbContext db)
    {
        var binding=new SqlConnectionStringBuilder(db.Database.GetConnectionString());
        if(!string.Equals(binding.DataSource,"localhost",StringComparison.OrdinalIgnoreCase) || binding.InitialCatalog!=Scope.Catalogue)
            throw new InvalidOperationException("index-recovery-legacy-exact-catalogue-required");
    }

    private async Task ReadEvidenceAsync(CancellationToken token)
    {
        foreach(var descriptor in _plan.Evidence)
        {
            var full=Path.GetFullPath(descriptor.Path);
            const string evidence=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\";
            if(!full.StartsWith(evidence,StringComparison.OrdinalIgnoreCase) || descriptor.Bytes is <1 or >1048576 ||
                descriptor.Sha256.Length!=64 || !descriptor.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("index-recovery-legacy-provenance-scope-invalid");
            var ancestors=new Stack<string>();
            for(var part=Path.GetDirectoryName(full);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))ancestors.Push(part);
            while(ancestors.TryPop(out var part))
            {
                if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("index-recovery-legacy-provenance-linked");
                _evidenceHandles.Add(PhysicalFileIdentity.OpenDirectoryLease(part));
            }
            var stream=new FileStream(PhysicalFileIdentity.OpenReadNoFollow(full),FileAccess.Read,65536,true);_evidenceHandles.Add(stream);
            if(stream.Length!=descriptor.Bytes || !string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token)),descriptor.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("index-recovery-legacy-provenance-hash-mismatch");
            stream.Position=0;
            var document=await JsonDocument.ParseAsync(stream,new(){MaxDepth=32},token);_evidence.Add(descriptor.Kind,document);
        }
        JsonElement E(string kind)=>_evidence.TryGetValue(kind,out var value)?value.RootElement:throw new InvalidDataException("index-recovery-legacy-provenance-missing");
        var before=E("before-intake");var publication=E("publication");var records=E("records");
        var source=E("backup-source");var stage=E("backup-stage");
        var stageRoot=Path.GetDirectoryName(Scope.RecoveryRoot)!;
        var directory=Path.Combine(stageRoot,"Sources","controlled-index-"+Scope.FixtureOperationId.ToString("N"));
        if(G(before,"operation").GetGuid()!=Scope.FixtureOperationId || !SamePath(G(before,"directory").GetString()!,directory) ||
            G(before,"createdUtc").GetDateTimeOffset()!=Scope.FixtureCreatedUtc || G(records,"rootId").GetGuid()!=Scope.SourceRootId ||
            !G(records,"records").EnumerateArray().Any(r=>G(r,"id").GetGuid()==Scope.PublicationRecordId &&
                G(r,"sourceRevision").GetGuid()==Scope.SourceRevisionId && G(r,"hash").GetString()==Scope.ContentSha256 &&
                SamePath(G(r,"path").GetString()!,Path.Combine(directory,"publication.txt"))) ||
            G(publication,"recordId").GetGuid()!=Scope.PublicationRecordId || G(G(publication,"after"),"generation").GetGuid()!=Scope.GenerationId ||
            G(source,"database").GetString()!="FluxKnowledge" || !G(source,"is_copy_only").GetBoolean() || !G(source,"has_checksums").GetBoolean() ||
            G(source,"observed_utc").GetDateTimeOffset()>=Scope.FixtureCreatedUtc || !G(stage,"Success").GetBoolean() ||
            G(stage,"Catalogue").GetString()!=Scope.Catalogue || G(stage,"VerifiedUtc").GetDateTimeOffset()>=Scope.FixtureCreatedUtc)
            throw new InvalidDataException("index-recovery-legacy-provenance-binding-invalid");
        void Backup(JsonElement receipt,string path,string bytes,string sha)
        {
            var file=G(receipt,path).GetString()!;
            if(!SamePath(Path.GetDirectoryName(file)!,Scope.RecoveryRoot) || !_plan.Files.Any(f=>SamePath(Path.Combine(Scope.RecoveryRoot,f.RelativePath),file) &&
                f.Bytes==G(receipt,bytes).GetInt64() && string.Equals(f.Sha256,G(receipt,sha).GetString(),StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("index-recovery-legacy-backup-identity-mismatch");
        }
        Backup(source,"path","bytes","sha256");Backup(stage,"BackupPath","Bytes","Sha256");
        var header=G(stage,"Header");
        if(G(header,"DatabaseName").GetString()!=Scope.Catalogue || !G(header,"IsCopyOnly").GetBoolean() ||
            !G(header,"HasBackupChecksums").GetBoolean() || G(header,"IsDamaged").GetBoolean())
            throw new InvalidDataException("index-recovery-legacy-backup-header-invalid");
        _backupVersion=G(stage,"Version").GetInt64();_publicationVersion=G(G(publication,"after"),"version").GetInt64();
        _epoch=G(G(publication,"after"),"epoch").GetGuid();
        if(_backupVersion<0 || _backupVersion>=_publicationVersion ||
            _publicationVersion!=G(G(publication,"before"),"version").GetInt64()+1 || G(stage,"Epoch").GetGuid()!=_epoch)
            throw new InvalidDataException("index-recovery-legacy-publication-lineage-invalid");
        _pins=new[]{new RecoveryIndexPin("legacy-staging/provenance",G(stage,"Base").GetGuid()),
            new RecoveryIndexPin("legacy-staging/provenance",G(stage,"Target").GetGuid())};
    }

    private static JsonElement G(JsonElement element,string key)
    {
        var matches=element.EnumerateObject().Where(p=>string.Equals(p.Name,key,StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length==1?matches[0].Value:throw new InvalidDataException("index-recovery-legacy-provenance-field-invalid");
    }

    private async Task ValidateDatabaseAsync(FluxKnowledgeDbContext db,bool initial,CancellationToken token)
    {
        ValidateConnection(db);db.Database.SetCommandTimeout(TimeSpan.FromSeconds(5));await db.Database.OpenConnectionAsync(token);
        if(await SqlIndexRecoveryReferences.DatabaseIdentityAsync(db,token)!=Scope.DatabaseIncarnation)
            throw new InvalidOperationException("index-recovery-legacy-incarnation-changed");
        var operation=await db.SourceDeletionOperations.AsNoTracking().SingleAsync(o=>o.Id==Scope.DeletionOperationId,token);
        var root=await db.SourceRootConfigurations.AsNoTracking().SingleAsync(r=>r.Id==Scope.SourceRootId,token);
        var record=await db.PipelineRecords.AsNoTracking().SingleAsync(r=>r.Id==Scope.PublicationRecordId,token);
        var source=await db.SourceRevisions.AsNoTracking().SingleAsync(r=>r.Id==Scope.SourceRevisionId,token);
        var generation=await db.IndexGenerations.AsNoTracking().SingleAsync(g=>g.Id==Scope.GenerationId,token);
        var segment=await db.IndexSegments.AsNoTracking().SingleAsync(s=>s.Id==Scope.SegmentId,token);
        var candidate=await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(c=>c.Id==segment.ProducingCandidateId,token);
        var directory=Path.Combine(Path.GetDirectoryName(Scope.RecoveryRoot)!,"Sources","controlled-index-"+Scope.FixtureOperationId.ToString("N"));
        var targets=await db.SourceDeletionCleanupItems.AsNoTracking().Where(c=>c.SourceDeletionOperationId==Scope.DeletionOperationId &&
            (c.StorageKind==3 || c.StorageKind==4)).OrderBy(c=>c.StorageKind).Take(3).ToArrayAsync(token);
        if(operation.SourceRootId!=Scope.SourceRootId || operation.State!=0 || operation.LeaseId is not null || operation.LeaseExpiresAtUtc is not null ||
            operation.Phase!="cleanup-memberships" || root.State!=2 ||
            !SamePath(root.CanonicalPath,directory) || root.CreatedAtUtc<Scope.FixtureCreatedUtc || record.SourceRevisionId!=Scope.SourceRevisionId ||
            record.Revision!=1 || record.ContentHash!=Scope.ContentSha256 || record.RegisteredAtUtc<Scope.FixtureCreatedUtc ||
            source.SourceRootId!=Scope.SourceRootId || source.OriginKind!=0 || source.Revision!=1 || source.ContentSha256!=Scope.ContentSha256 ||
            source.DiscoveredAtUtc<Scope.FixtureCreatedUtc || !SamePath(source.CanonicalPath,Path.Combine(directory,"publication.txt")) ||
            generation.CorpusEpoch!=_epoch || generation.CorpusVersion!=_publicationVersion || generation.CreatedAtUtc<Scope.FixtureCreatedUtc ||
            segment.CreatedAtUtc<Scope.FixtureCreatedUtc || candidate.State!=2 || candidate.BuildPurpose!=0 || candidate.ResultGenerationId!=Scope.GenerationId ||
            candidate.PublicationCorpusVersion!=_publicationVersion || candidate.RepackedSegmentsJson!="[]" || targets.Length!=2 ||
            targets[0].RelativePath!=Scope.GenerationId.ToString("N") || targets[1].RelativePath!=Scope.SegmentId)
            throw new InvalidOperationException("index-recovery-legacy-owned-target-provenance-invalid");
        var state=await db.IndexState.AsNoTracking().SingleAsync(s=>s.Id==1,token);
        var admission=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {operation.Id,operation.State,operation.Phase,operation.LeaseId,operation.LeaseExpiresAtUtc,operation.RowVersion,
            RootVersion=root.RowVersion,Targets=targets,Stamp=new{state.CorpusEpoch,state.CorpusVersion,state.ActiveIndexGenerationId}}))));
        if(initial)_admissionDigest=admission;
        else if(admission!=_admissionDigest)throw new InvalidOperationException("index-recovery-legacy-admission-snapshot-changed");
        using var affected=JsonDocument.Parse(candidate.AffectedScopesJson);
        var scopes=affected.RootElement.EnumerateArray().Select(s=>(Kind:G(s,"Kind").GetInt32(),Id:G(s,"Id").GetGuid())).ToArray();
        if(scopes.Length!=2 || !scopes.Contains((1,record.SourceIdentityId)) || !scopes.Contains((2,Scope.SourceRevisionId)))
            throw new InvalidOperationException("index-recovery-legacy-owned-candidate-scope-invalid");
        // Classifier 2 cannot authorise any other retirement. Capture the complete
        // selected/owned projection, rather than walking the historical catalogue.
        var generationIds=new[]{Scope.GenerationId,state.ActiveIndexGenerationId??throw new InvalidOperationException("index-recovery-legacy-selected-generation-required")}.Distinct().ToArray();
        var generations=await db.IndexGenerations.AsNoTracking().Where(g=>generationIds.Contains(g.Id)).OrderBy(g=>g.Id)
            .Select(g=>new{g.Id,g.IndexPath}).Take(3).ToArrayAsync(token);
        var membership=await db.IndexGenerationSegments.AsNoTracking().Where(m=>generationIds.Contains(m.GenerationId))
            .OrderBy(m=>m.GenerationId).ThenBy(m=>m.Ordinal).Select(m=>new{m.GenerationId,m.SegmentId,m.Ordinal}).Take(4097).ToArrayAsync(token);
        if(membership.Length>4096)throw new InvalidOperationException("index-recovery-legacy-projection-capacity-exceeded");
        var segmentIds=membership.Select(m=>m.SegmentId).Append(Scope.SegmentId).Distinct().ToArray();
        var segments=await db.IndexSegments.AsNoTracking().Where(s=>segmentIds.Contains(s.Id)).OrderBy(s=>s.Id)
            .Select(s=>new{s.Id,s.DirectoryPath,s.MembershipChecksum,s.FileChecksum,s.VectorCount}).Take(4097).ToArrayAsync(token);
        var previous=await db.IndexRecoveryPins.AsNoTracking().OrderBy(p=>p.Id).Take(8193).ToArrayAsync(token);
        if(generations.Length!=generationIds.Length || segments.Length!=segmentIds.Length || previous.Length>8192)
            throw new InvalidOperationException("index-recovery-legacy-projection-capacity-or-completeness-invalid");
        if(previous.Any(p=>p.GenerationId==Scope.GenerationId || p.IndexPath==generation.IndexPath || p.IndexPath==segment.DirectoryPath) ||
            _pins.Any(p=>p.GenerationId==Scope.GenerationId || p.IndexPath==generation.IndexPath || p.IndexPath==segment.DirectoryPath))
            throw new InvalidOperationException("index-recovery-legacy-existing-reference-cannot-be-excluded");
        var digest=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{generations,membership,segments,previous}))));
        if(!initial && digest!=_registryDigest)throw new InvalidOperationException("index-recovery-legacy-registry-changed");
        if(initial)
        {
            _registryDigest=digest;
            _pins=_pins.Concat(previous.Select(p=>new RecoveryIndexPin(p.Packet,p.GenerationId,p.IndexPath)))
                .Concat(generations.Where(g=>g.Id!=Scope.GenerationId).Select(g=>new RecoveryIndexPin("legacy-staging/registered-generation",g.Id,g.IndexPath.Length==0?null:g.IndexPath)))
                .Concat(segments.Where(s=>s.Id!=Scope.SegmentId).Select(s=>new RecoveryIndexPin("legacy-staging/registered-segment",IndexPath:s.DirectoryPath)))
                .Distinct().ToArray();
            if(_pins.Length>8192)throw new InvalidOperationException("index-recovery-legacy-pin-budget");
        }
    }
}
