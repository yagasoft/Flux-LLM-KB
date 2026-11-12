using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed record RecoveryLegacyFile(string RelativePath,long Bytes,string Sha256);

/// <summary>Hash-verified recovery bytes held against writes and deletion for one owned operation.</summary>
internal sealed class RecoveryLegacyFileLease : IAsyncDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _held=[];
    private readonly object _sync=new();
    private readonly TaskCompletionSource _drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string,string> _inventory=new(StringComparer.OrdinalIgnoreCase);
    private string[] _namespace=[];
    private int _borrows;
    private bool _closing,_disposed;
    private Task? _dispose;
    private RecoveryLegacyFileLease(string root)=>_root=root;
    internal string Root=>_root;
    internal string Digest {get;private set;}="";

    internal static async Task<RecoveryLegacyFileLease> HoldAsync(string root,IReadOnlyList<RecoveryLegacyFile> files,CancellationToken token,
        Action? validateHeldFiles=null)
    {
        var canonical=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if(!OperatingSystem.IsWindows() || !Directory.Exists(canonical) || files.Count is <1 or >2048)
            throw new InvalidDataException("legacy-recovery-file-scope-invalid");
        var expected=new Dictionary<string,RecoveryLegacyFile>(StringComparer.OrdinalIgnoreCase);
        long total=0;
        foreach(var input in files)
        {
            if(string.IsNullOrWhiteSpace(input.RelativePath) || Path.IsPathRooted(input.RelativePath) || input.RelativePath.Length>2048 ||
                input.Bytes is <0 or >68719476736 || input.Sha256.Length!=64 || !input.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("legacy-recovery-file-scope-invalid");
            var full=Path.GetFullPath(Path.Combine(canonical,input.RelativePath));
            if(!full.StartsWith(canonical+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
                input.RelativePath.Replace('/',Path.DirectorySeparatorChar)!=Path.GetRelativePath(canonical,full) ||
                !expected.TryAdd(Path.GetRelativePath(canonical,full),input with{Sha256=input.Sha256.ToLowerInvariant()}))
                throw new InvalidDataException("legacy-recovery-file-scope-invalid");
            total=checked(total+input.Bytes);
            if(total>137438953472)throw new InvalidDataException("legacy-recovery-file-byte-bound");
        }
        var lease=new RecoveryLegacyFileLease(canonical);
        try
        {
            // Parent leases prevent namespace replacement; no-follow file handles below
            // omit write/delete sharing, including for same-length in-place changes.
            var ancestors=new Stack<string>();
            for(var part=canonical;!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))ancestors.Push(part);
            while(ancestors.TryPop(out var part))lease.HoldDirectory(part);
            var entries=lease.ReadNamespace(true,token);
            var actual=entries.Where(e=>!e.EndsWith(Path.DirectorySeparatorChar)).ToArray();
            if(!actual.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected.Keys.Order(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("legacy-recovery-file-inventory-mismatch");
            lease._namespace=entries;
            var pending=new List<(string Relative,RecoveryLegacyFile Expected,FileStream Stream,string Identity)>();
            foreach(var (relative,item) in expected.OrderBy(x=>x.Key,StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var stream=new FileStream(PhysicalFileIdentity.OpenReadNoFollow(Path.Combine(canonical,relative)),FileAccess.Read,65536,true);
                lease._held.Add(stream);
                if(stream.Length!=item.Bytes)throw new InvalidDataException("legacy-recovery-file-length-mismatch");
                var identity=PhysicalFileIdentity.Get(stream.SafeFileHandle).ToString();
                pending.Add((relative,item,stream,identity));
            }
            // Inspect bounded metadata before reading large backups, with the entire
            // declared namespace and all bytes already held against replacement/writes.
            validateHeldFiles?.Invoke();
            foreach(var (relative,item,stream,identity) in pending)
            {
                var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token).ConfigureAwait(false));
                if(hash!=item.Sha256)throw new InvalidDataException("legacy-recovery-file-hash-mismatch");
                lease._inventory.Add(relative,$"{relative}|{identity}|{item.Bytes}|{hash}");
            }
            lease.ValidateInventory();
            lease.Digest=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
                lease._inventory.Values.Order(StringComparer.Ordinal)))));
            return lease;
        }
        catch(Exception primary)
        {
            try{await lease.DisposeAsync();}
            catch(Exception cleanup){primary.Data["RecoveryLegacyFileLeaseCleanupFailure"]=cleanup;}
            throw;
        }
    }

    internal string DescribeCategory(string path)
    {
        ValidateInventory();
        var relative=Path.GetRelativePath(_root,Path.GetFullPath(path));
        var values=_inventory.Where(p=>p.Key.Equals(relative,StringComparison.OrdinalIgnoreCase) ||
            p.Key.StartsWith(relative+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            .Select(p=>p.Value).Order(StringComparer.Ordinal).ToArray();
        if(values.Length==0)throw new InvalidDataException("legacy-recovery-category-unproved");
        return relative+"|legacy-held-sha256|"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',values))));
    }

    internal IDisposable Borrow()
    {
        lock(_sync)
        {
            ObjectDisposedException.ThrowIf(_closing,this);
            _borrows++;
            return new Use(this);
        }
    }

    internal void ValidateInventory()
    {
        lock(_sync)ObjectDisposedException.ThrowIf(_disposed || _closing&&_borrows==0,this);
        if(!_namespace.SequenceEqual(ReadNamespace(false),StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("legacy-recovery-namespace-changed");
    }

    private void HoldDirectory(string path)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("legacy-recovery-linked-path");
        _held.Add(PhysicalFileIdentity.OpenDirectoryLease(path));
    }

    private string[] ReadNamespace(bool hold,CancellationToken token=default)
    {
        var found=new List<string>();var pending=new Queue<(string Path,int Depth)>();pending.Enqueue((_root,0));
        while(pending.TryDequeue(out var next))
        {
            foreach(var path in Directory.EnumerateFileSystemEntries(next.Path))
            {
                token.ThrowIfCancellationRequested();
                var attributes=File.GetAttributes(path);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("legacy-recovery-linked-path");
                var directory=(attributes&FileAttributes.Directory)!=0;
                found.Add(Path.GetRelativePath(_root,path)+(directory?Path.DirectorySeparatorChar:""));
                if(found.Count>8192 || next.Depth>12)throw new InvalidDataException("legacy-recovery-namespace-bound");
                if(directory)
                {
                    if(hold)HoldDirectory(path);
                    pending.Enqueue((path,next.Depth+1));
                }
            }
        }
        return found.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ValueTask DisposeAsync()
    {
        lock(_sync)
        {
            if(_dispose is not null)return new(_dispose);
            _closing=true;if(_borrows==0)_drained.TrySetResult();
            return new(_dispose=CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        await _drained.Task.ConfigureAwait(false);
        List<Exception>? failures=null;
        try
        {
            for(var i=_held.Count-1;i>=0;i--)try{_held[i].Dispose();}catch(Exception error){(failures??=[]).Add(error);}
            _held.Clear();
        }
        finally{lock(_sync)_disposed=true;}
        if(failures is not null)throw new AggregateException(failures);
    }

    private sealed class Use(RecoveryLegacyFileLease owner):IDisposable
    {
        private RecoveryLegacyFileLease? _owner=owner;
        public void Dispose()
        {
            var current=Interlocked.Exchange(ref _owner,null);if(current is null)return;
            lock(current._sync){current._borrows--;if(current._closing&&current._borrows==0)current._drained.TrySetResult();}
        }
    }
}
