using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class RecoveryLegacyFileLeaseTests : IDisposable
{
    private readonly string _root=@"I:\Codex Workspaces\Staging\FluxKnowledge\LegacyFileTest-"+Guid.NewGuid().ToString("N");
    private string Payload=>Path.Combine(_root,"App-before-M32","FluxKnowledge.Web.dll");
    public RecoveryLegacyFileLeaseTests(){Directory.CreateDirectory(Path.GetDirectoryName(Payload)!);File.WriteAllText(Payload,"original",new UTF8Encoding(false));}
    private RecoveryLegacyFile Descriptor(string hash)=>new(Path.GetRelativePath(_root,Payload),new FileInfo(Payload).Length,hash);
    private string Hash()=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Payload)));

    [Fact]
    public async Task Verified_bytes_remain_locked_until_the_last_borrow_settles()
    {
        var lease=await RecoveryLegacyFileLease.HoldAsync(_root,[Descriptor(Hash())],default);
        var use=lease.Borrow();var closing=lease.DisposeAsync().AsTask();
        try
        {
            Assert.False(closing.IsCompleted);
            Assert.Throws<IOException>(()=>File.WriteAllText(Payload,"modified")); // Same byte length.
            Assert.Throws<IOException>(()=>File.Move(Payload,Payload+".moved"));
            Assert.Throws<ObjectDisposedException>(()=>lease.Borrow());
        }
        finally{use.Dispose();await closing;}
        File.WriteAllText(Payload,"modified");
    }

    [Fact]
    public async Task A_same_length_mismatch_is_refused_and_all_opened_handles_are_released()
    {
        var expected=Hash();File.WriteAllText(Payload,"modified",new UTF8Encoding(false));
        await Assert.ThrowsAsync<InvalidDataException>(()=>RecoveryLegacyFileLease.HoldAsync(_root,[Descriptor(expected)],default));
        File.WriteAllText(Payload,"restored");
    }

    [Fact]
    public async Task An_added_entry_is_detected_without_reading_its_content()
    {
        await using var lease=await RecoveryLegacyFileLease.HoldAsync(_root,[Descriptor(Hash())],default);
        File.WriteAllText(Path.Combine(_root,"App-before-M32","unexpected.json"),"{}");
        Assert.Throws<InvalidDataException>(()=>lease.ValidateInventory());
    }

    [Fact]
    public async Task Escaped_or_duplicate_targets_are_refused_before_any_hash_is_accepted()
    {
        var descriptor=Descriptor(Hash());
        await Assert.ThrowsAsync<InvalidDataException>(()=>RecoveryLegacyFileLease.HoldAsync(_root,[descriptor,descriptor],default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>RecoveryLegacyFileLease.HoldAsync(_root,[descriptor with{RelativePath="..\\outside"}],default));
    }

    public void Dispose()
    {
        var absolute=Path.GetFullPath(_root);
        if(absolute!=_root || !Path.GetFileName(absolute).StartsWith("LegacyFileTest-",StringComparison.Ordinal))throw new InvalidOperationException("owned-legacy-test-cleanup-target");
        if(Directory.Exists(absolute))Directory.Delete(absolute,true);
    }
}
