using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record LegacyStagingRetirementScope(string Catalogue,Guid DatabaseIncarnation,string RecoveryRoot,string IndexRoot,
    Guid DeletionOperationId,Guid SourceRootId,Guid FixtureOperationId,Guid PublicationRecordId,Guid SourceRevisionId,
    string ContentSha256,Guid GenerationId,string SegmentId,DateTimeOffset FixtureCreatedUtc);
public sealed record LegacyStagingEvidenceFile(string Kind,string Path,long Bytes,string Sha256);
public sealed record LegacyStagingRecoveryPlan(LegacyStagingRetirementScope Scope,
    IReadOnlyList<RecoveryLegacyFile> Files,IReadOnlyList<LegacyStagingEvidenceFile> Evidence);

/// <summary>A live, target-restricted staging capability; never a reusable complete historical audit.</summary>
public sealed partial class LegacyStagingRecoveryProof : IAsyncDisposable
{
    internal const int ClassifierVersion=2;
    private readonly LegacyStagingRecoveryPlan _plan;
    private readonly List<IDisposable> _evidenceHandles=[];
    private readonly Dictionary<string,JsonDocument> _evidence=new(StringComparer.Ordinal);
    private RecoveryLegacyFileLease? _files;
    private RecoveryIndexPin[] _pins=[];
    private string _registryDigest="",_admissionDigest="",_proofDigest="";
    private byte[]? _sealVersion;
    private string? _sealChecksum,_rootFingerprint;
    private readonly object _closeSync=new();
    private Task? _close;
    private LegacyStagingRecoveryProof(LegacyStagingRecoveryPlan plan)=>_plan=plan;
    internal LegacyStagingRetirementScope Scope=>_plan.Scope;

    public static async Task<LegacyStagingRecoveryProof> CreateAsync(IDbContextFactory<FluxKnowledgeDbContext> factory,
        LegacyStagingRecoveryPlan plan,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var proof=new LegacyStagingRecoveryProof(plan with{Files=plan.Files.ToArray(),Evidence=plan.Evidence.ToArray()});
        try
        {
            proof.ValidateLayout();
            await using(var configured=await factory.CreateDbContextAsync(token))proof.ValidateConnection(configured);
            await proof.ReadEvidenceAsync(token);
            await using(var db=await factory.CreateDbContextAsync(token))await proof.ValidateDatabaseAsync(db,true,token);
            proof._files=await RecoveryLegacyFileLease.HoldAsync(proof.Scope.RecoveryRoot,proof._plan.Files,token,
                proof.ValidateApplicationAndConfigurationRoles);
            proof._proofDigest=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(proof._plan)+"\n"+proof._registryDigest+"\n"+proof._admissionDigest+"\n"+proof._files.Digest)));
            return proof;
        }
        catch(Exception primary)
        {
            try{await proof.DisposeAsync();}catch(Exception cleanup){primary.Data["LegacyStagingProofCleanupFailure"]=cleanup;}
            throw;
        }
    }

    internal IDisposable BorrowAudit()=>Files.Borrow();
    internal IDisposable BorrowRetirement(SourceDeletionWorkItem? work,int kind,string target)
    {
        var use=Files.Borrow();
        try
        {
            if(_sealVersion is null || work is null || work.OperationId!=Scope.DeletionOperationId || work.SourceRootId!=Scope.SourceRootId ||
                work.LeaseId==Guid.Empty || work.Phase!="cleanup-memberships" ||
                !(kind==3&&target==Scope.GenerationId.ToString("N") || kind==4&&target==Scope.SegmentId))
                throw new InvalidOperationException("segment-retirement-legacy-proof-scope-required");
            Files.ValidateInventory();return use;
        }
        catch{use.Dispose();throw;}
    }

    internal bool TryCaptureCategory(string path,List<string> inventory,List<RecoveryIndexPin> pins)
    {
        var name=Path.GetFileName(path);
        if(!_plan.Files.Any(f=>FirstComponent(f.RelativePath)==name))return false;
        inventory.Add(Files.DescribeCategory(path)+"|target-restricted-proof|"+_proofDigest);
        foreach(var pin in _pins)if(!pins.Contains(pin))pins.Add(pin);
        return true;
    }

    internal async Task RevalidateBeforeSealAsync(FluxKnowledgeDbContext db,CancellationToken token)
    {
        Files.ValidateInventory();await ValidateDatabaseAsync(db,false,token);
    }

    internal void BindSeal(IndexRecoveryAuditEntity audit)
    {
        Files.ValidateInventory();
        if(audit.ClassifierVersion!=ClassifierVersion || audit.DatabaseIncarnation!=Scope.DatabaseIncarnation || audit.RowVersion.Length==0)
            throw new InvalidOperationException("index-recovery-legacy-seal-binding-invalid");
        _sealVersion=audit.RowVersion.ToArray();_sealChecksum=audit.InventoryChecksum;_rootFingerprint=audit.RootFingerprint;
    }

    internal void RequireLiveSeal(FluxKnowledgeDbContext db,IndexRecoveryAuditEntity audit)
    {
        ValidateConnection(db);Files.ValidateInventory();
        if(_sealVersion is null || !audit.RowVersion.SequenceEqual(_sealVersion) || audit.InventoryChecksum!=_sealChecksum ||
            audit.RootFingerprint!=_rootFingerprint || audit.DatabaseIncarnation!=Scope.DatabaseIncarnation ||
            !SamePath(audit.RecoveryRoot,Scope.RecoveryRoot) || !SamePath(audit.IndexRoot,Scope.IndexRoot))
            throw new InvalidOperationException("segment-retirement-legacy-live-proof-required");
    }

    private RecoveryLegacyFileLease Files=>_files??throw new InvalidOperationException("index-recovery-legacy-file-proof-unavailable");
    private static string FirstComponent(string path)=>path.Replace('/',Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar)[0];
    private static bool SamePath(string left,string right)=>string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),StringComparison.OrdinalIgnoreCase);

    public ValueTask DisposeAsync(){lock(_closeSync)return new(_close??=CloseAsync());}
    private async Task CloseAsync()
    {
        List<Exception>? failures=null;
        // File-lease disposal closes admission and joins every actual retirement/audit borrow.
        try{if(_files is not null)await _files.DisposeAsync();}catch(Exception error){(failures??=[]).Add(error);}
        foreach(var document in _evidence.Values)document.Dispose();_evidence.Clear();
        for(var i=_evidenceHandles.Count-1;i>=0;i--)try{_evidenceHandles[i].Dispose();}catch(Exception error){(failures??=[]).Add(error);}
        _evidenceHandles.Clear();
        if(failures is not null)throw new AggregateException(failures);
    }
}
