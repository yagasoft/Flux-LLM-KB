using System.Text;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Operations;

public sealed class HandleRelativeNativeFileSystemTests
{
    [Fact]
    public async Task Swap_between_validation_and_delete_is_rejected_without_deleting_either_identity()
    {
        await using var fixture = new Fixture();
        var originalPath = fixture.CreateDirectory("OutlookSpool");
        var parkedPath = Path.Combine(fixture.Root, "OutlookSpool-parked");
        var fileSystem = new HandleRelativeNativeFileSystem((operation, literalChild) =>
        {
            if (operation == NativeFileOperation.DeleteLiteralChild && literalChild == "OutlookSpool")
            {
                Directory.Move(originalPath, parkedPath);
                Directory.CreateDirectory(originalPath);
            }

            return ValueTask.CompletedTask;
        });
        using var parent = fileSystem.OpenDirectory(fixture.Root);
        using var original = fileSystem.OpenDirectory(parent, "OutlookSpool");
        var expectedIdentity = original.Identity;
        original.Dispose();

        var result = await fileSystem.DeleteLiteralChildAsync(parent, "OutlookSpool", expectedIdentity);

        Assert.False(result.Changed);
        Assert.Equal("file-identity-changed", result.Reason);
        Assert.True(Directory.Exists(originalPath));
        Assert.True(Directory.Exists(parkedPath));
    }

    [Fact]
    public async Task Delete_rejects_a_foreign_or_wildcard_child_before_mutation()
    {
        await using var fixture = new Fixture();
        var ownedPath = fixture.CreateDirectory("OutlookSpool");
        var fileSystem = new HandleRelativeNativeFileSystem();
        using var parent = fileSystem.OpenDirectory(fixture.Root);
        using var owned = fileSystem.OpenDirectory(parent, "OutlookSpool");

        var traversal = await fileSystem.DeleteLiteralChildAsync(parent, @"..\OutlookSpool", owned.Identity);
        var wildcard = await fileSystem.DeleteLiteralChildAsync(parent, "*", owned.Identity);

        Assert.Equal("foreign-child-name", traversal.Reason);
        Assert.Equal("foreign-child-name", wildcard.Reason);
        Assert.True(Directory.Exists(ownedPath));
    }

    [Fact]
    public async Task Delete_is_literal_and_never_recurses_into_a_nonempty_child()
    {
        await using var fixture = new Fixture();
        var childPath = fixture.CreateDirectory("OutlookSpool");
        var retainedPath = Path.Combine(childPath, "retained.bin");
        await File.WriteAllTextAsync(retainedPath, "retain");
        var fileSystem = new HandleRelativeNativeFileSystem();
        using var parent = fileSystem.OpenDirectory(fixture.Root);
        using var child = fileSystem.OpenDirectory(parent, "OutlookSpool");
        var expectedIdentity = child.Identity;
        child.Dispose();

        var result = await fileSystem.DeleteLiteralChildAsync(parent, "OutlookSpool", expectedIdentity);

        Assert.False(result.Changed);
        Assert.Equal("literal-child-not-empty", result.Reason);
        Assert.Equal("retain", await File.ReadAllTextAsync(retainedPath));
    }

    [Fact]
    public async Task Move_revalidates_the_source_identity_after_the_operation_interlock()
    {
        await using var fixture = new Fixture();
        var stagePath = fixture.CreateDirectory("stage");
        var parkedPath = Path.Combine(fixture.Root, "stage-parked");
        var destinationRoot = fixture.CreateDirectory("destination");
        var fileSystem = new HandleRelativeNativeFileSystem((operation, literalChild) =>
        {
            if (operation == NativeFileOperation.MoveLiteralChild && literalChild == "stage")
            {
                Directory.Move(stagePath, parkedPath);
                Directory.CreateDirectory(stagePath);
            }

            return ValueTask.CompletedTask;
        });
        using var sourceParent = fileSystem.OpenDirectory(fixture.Root);
        using var destinationParent = fileSystem.OpenDirectory(destinationRoot);
        using var stage = fileSystem.OpenDirectory(sourceParent, "stage");
        var expectedIdentity = stage.Identity;
        stage.Dispose();

        var result = await fileSystem.MoveLiteralChildAsync(
            sourceParent,
            "stage",
            expectedIdentity,
            destinationParent,
            "App");

        Assert.False(result.Changed);
        Assert.Equal("file-identity-changed", result.Reason);
        Assert.True(Directory.Exists(stagePath));
        Assert.True(Directory.Exists(parkedPath));
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, "App")));
    }

    [Fact]
    public async Task Replace_rejects_an_unknown_temporary_file_without_changing_the_destination()
    {
        await using var fixture = new Fixture();
        var destinationPath = Path.Combine(fixture.Root, "native-go-live.json");
        var temporaryPath = Path.Combine(fixture.Root, "native-go-live.json.tmp");
        await File.WriteAllTextAsync(destinationPath, "before");
        await File.WriteAllTextAsync(temporaryPath, "foreign");
        var fileSystem = new HandleRelativeNativeFileSystem();
        using var parent = fileSystem.OpenDirectory(fixture.Root);

        var result = await fileSystem.ReplaceFileAsync(
            parent,
            "native-go-live.json.tmp",
            "native-go-live.json",
            Encoding.UTF8.GetBytes("after"),
            expectedDestinationIdentity: null);

        Assert.False(result.Changed);
        Assert.Equal("unknown-temporary-file", result.Reason);
        Assert.Equal("before", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("foreign", await File.ReadAllTextAsync(temporaryPath));
    }

    [Fact]
    public async Task Replace_revalidates_the_destination_identity_after_the_temporary_is_flushed()
    {
        await using var fixture = new Fixture();
        var destinationPath = Path.Combine(fixture.Root, "native-go-live.json");
        var parkedPath = Path.Combine(fixture.Root, "native-go-live.parked.json");
        await File.WriteAllTextAsync(destinationPath, "before");
        var fileSystem = new HandleRelativeNativeFileSystem((operation, literalChild) =>
        {
            if (operation == NativeFileOperation.ReplaceFile && literalChild == "native-go-live.json")
            {
                File.Move(destinationPath, parkedPath);
                File.WriteAllText(destinationPath, "foreign");
            }

            return ValueTask.CompletedTask;
        });
        using var parent = fileSystem.OpenDirectory(fixture.Root);
        var expected = Assert.IsType<NativeLiteralFile>(
            await fileSystem.ReadLiteralFileAsync(parent, "native-go-live.json"));

        var result = await fileSystem.ReplaceFileAsync(
            parent,
            "native-go-live.json.tmp",
            "native-go-live.json",
            Encoding.UTF8.GetBytes("after"),
            expected.Identity);

        Assert.False(result.Changed);
        Assert.Equal("file-identity-changed", result.Reason);
        Assert.Equal("foreign", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("before", await File.ReadAllTextAsync(parkedPath));
        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "native-go-live.json.tmp")));
    }

    [Fact]
    public async Task Replace_guards_the_expected_destination_identity_across_the_mutation_boundary()
    {
        await using var fixture = new Fixture();
        var destinationPath = Path.Combine(fixture.Root, "native-go-live.json");
        var parkedPath = Path.Combine(fixture.Root, "native-go-live.expected.json");
        await File.WriteAllTextAsync(destinationPath, "expected");
        var fileSystem = new HandleRelativeNativeFileSystem((operation, literalChild) =>
        {
            if (operation == NativeFileOperation.ReplaceFileAfterDestinationValidation &&
                literalChild == "native-go-live.json")
            {
                File.Move(destinationPath, parkedPath);
                File.WriteAllText(destinationPath, "foreign");
            }

            return ValueTask.CompletedTask;
        });
        using var parent = fileSystem.OpenDirectory(fixture.Root);
        var expected = Assert.IsType<NativeLiteralFile>(
            await fileSystem.ReadLiteralFileAsync(parent, "native-go-live.json"));

        var result = await fileSystem.ReplaceFileAsync(
            parent,
            "native-go-live.json.tmp",
            "native-go-live.json",
            Encoding.UTF8.GetBytes("after"),
            expected.Identity);

        Assert.False(result.Changed);
        Assert.Equal("file-identity-changed", result.Reason);
        Assert.Equal("foreign", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal("expected", await File.ReadAllTextAsync(parkedPath));
        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "native-go-live.json.tmp")));
    }

    [Fact]
    public async Task Replace_holds_the_verified_temporary_against_late_writers_until_installation()
    {
        await using var fixture = new Fixture();
        var destinationPath = Path.Combine(fixture.Root, "native-go-live.json");
        var temporaryPath = Path.Combine(fixture.Root, "native-go-live.json.tmp");
        await File.WriteAllTextAsync(destinationPath, "before");
        var lateWriteBlocked = false;
        var fileSystem = new HandleRelativeNativeFileSystem((operation, literalChild) =>
        {
            if (operation == NativeFileOperation.ReplaceFileAfterTemporaryValidation &&
                literalChild == "native-go-live.json")
            {
                try
                {
                    File.WriteAllText(temporaryPath, "corrupt");
                }
                catch (IOException)
                {
                    lateWriteBlocked = true;
                }
            }

            return ValueTask.CompletedTask;
        });
        using var parent = fileSystem.OpenDirectory(fixture.Root);
        var expected = Assert.IsType<NativeLiteralFile>(
            await fileSystem.ReadLiteralFileAsync(parent, "native-go-live.json"));

        var result = await fileSystem.ReplaceFileAsync(
            parent,
            "native-go-live.json.tmp",
            "native-go-live.json",
            Encoding.UTF8.GetBytes("after"),
            expected.Identity);

        Assert.True(result.Changed, result.Reason);
        Assert.True(lateWriteBlocked);
        Assert.Equal("after", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task Create_and_replace_are_relative_to_a_verified_parent_handle()
    {
        await using var fixture = new Fixture();
        var fileSystem = new HandleRelativeNativeFileSystem();
        using var parent = fileSystem.OpenDirectory(fixture.Root);

        var created = await fileSystem.CreateDirectoryAsync(parent, "Staging");
        using var staging = fileSystem.OpenDirectory(parent, "Staging");
        var replaced = await fileSystem.ReplaceFileAsync(
            staging,
            "payload.json.tmp",
            "payload.json",
            "{\"status\":\"pending\"}"u8.ToArray(),
            expectedDestinationIdentity: null);

        Assert.True(created.Changed, created.Reason);
        Assert.True(replaced.Changed, replaced.Reason);
        Assert.Equal(
            "{\"status\":\"pending\"}",
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, "Staging", "payload.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "Staging", "payload.json.tmp")));
    }

    [Fact]
    public async Task Create_revalidates_the_verified_parent_and_destination_after_its_interlock()
    {
        await using var fixture = new Fixture();
        var recovery = Path.Combine(fixture.Root, "Recovery");
        var fileSystem = new HandleRelativeNativeFileSystem((operation, literalChild) =>
        {
            if (operation == NativeFileOperation.CreateDirectory && literalChild == "Recovery")
                Directory.CreateDirectory(recovery);
            return ValueTask.CompletedTask;
        });
        using var parent = fileSystem.OpenDirectory(fixture.Root);

        var result = await fileSystem.CreateDirectoryAsync(parent, "Recovery");

        Assert.False(result.Changed);
        Assert.Equal("destination-exists", result.Reason);
        Assert.True(Directory.Exists(recovery));
    }

    [Fact]
    public async Task Reparse_child_is_rejected_before_delete()
    {
        await using var fixture = new Fixture();
        var targetPath = fixture.CreateDirectory("target");
        var linkPath = Path.Combine(fixture.Root, "OutlookSpool");
        Directory.CreateSymbolicLink(linkPath, targetPath);
        var fileSystem = new HandleRelativeNativeFileSystem();
        using var parent = fileSystem.OpenDirectory(fixture.Root);

        var result = await fileSystem.DeleteLiteralChildAsync(
            parent,
            "OutlookSpool",
            NativeFileIdentity.Unknown);

        Assert.False(result.Changed);
        Assert.Equal("reparse-point-not-allowed", result.Reason);
        Assert.True(Directory.Exists(targetPath));
        Assert.True((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeHandleRelativeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateDirectory(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
