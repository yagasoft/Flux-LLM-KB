using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web.Components.Sources;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SourceJournalOperatorTests
{
    private const string First = @"\\?\VOLUME{11111111-1111-1111-1111-111111111111}\";
    private const string Second = @"\\?\VOLUME{22222222-2222-2222-2222-222222222222}\";
    private static SourceJournalOperator Create(Reader reader) => new(
        new() { Enabled = true, PipeName = "FluxKnowledge.SourceJournal.fixture", ApprovedVolumes = [First] }, reader,
        new WindowsSourceJournalVolumeInventory());

    [Fact]
    public async Task Unknown_volume_never_reaches_the_reader_and_approved_volume_requires_query_and_read()
    {
        var reader = new Reader();
        var journal = Create(reader);
        var unapproved = await journal.ProbeAsync(Second, default);
        Assert.Null(unapproved.JournalId);
        Assert.Contains("outside the app allowlist", unapproved.Message);
        Assert.Empty(reader.Queries);
        var approved = await journal.ProbeAsync(First, default);
        Assert.Equal("0000000000000011", approved.JournalId);
        Assert.Equal(First, Assert.Single(reader.Queries));
        Assert.Equal(new JournalCursor(First, 17, 101), Assert.Single(reader.Reads));
    }

    [Theory]
    [InlineData("SourceJournalHelperRefused", "protected helper refused")]
    [InlineData("SourceJournalHelperUnauthenticated", "could not be authenticated")]
    [InlineData("journal-inactive", "USN journal is inactive")]
    [InlineData("journal-deleting", "Windows is deleting")]
    [InlineData("volume-unavailable", "could not open the volume")]
    public async Task Operator_can_distinguish_windows_and_helper_failures(string failure, string reason)
    {
        var reader = new Reader { Error = new IOException(failure) };
        var result = await Create(reader).ProbeAsync(First, default);
        Assert.Contains(reason, result.Message);
        Assert.Null(result.JournalId);
        Assert.Empty(reader.Reads);
    }

    [Fact]
    public void Proposal_binds_both_sets_and_identifies_all_roots_on_changed_volumes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var available = new[] { new JournalVolume(@"I:\", First), new JournalVolume(@"E:\", Second) };
        var roots = new[] { Root(@"I:\alpha", "Alpha"), Root(@"I:\beta", "Beta"), Root(@"E:\gamma", "Gamma"),
            Root(@"J:\offline", "Offline") };
        var journal = Create(new());
        var proposal = journal.Preview([Second], available, roots);
        Assert.Equal([First], proposal.Removed);
        Assert.Equal([Second], proposal.Added);
        Assert.Equal(["Alpha", "Beta", "Gamma"], proposal.Impacts.Select(x => x.Root.DisplayName));
        Assert.Equal([false, false, true], proposal.Impacts.Select(x => x.Added));
        Assert.Equal("Offline", Assert.Single(proposal.Unmapped).DisplayName);
        var bytes = Convert.FromBase64String(proposal.Base64);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), proposal.Sha256);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(First, Assert.Single(document.RootElement.GetProperty("ExpectedApprovedVolumes").EnumerateArray()).GetString());
        Assert.Equal(Second, Assert.Single(document.RootElement.GetProperty("ApprovedVolumes").EnumerateArray()).GetString());
        Assert.Equal([First], journal.ApprovedVolumes);
        Assert.Throws<InvalidOperationException>(() => journal.Preview([Second], [available[0]], roots));
        Assert.Throws<InvalidOperationException>(() => journal.Preview([], available, roots));
        Assert.Throws<InvalidOperationException>(() => journal.Preview([First, First], available, roots));
    }

    private static JournalSourceRoot Root(string path, string name) =>
        new(Guid.NewGuid(), name, path, "Enabled");

    private sealed class Reader : ISourceChangeJournalReader
    {
        public Exception? Error { get; init; }
        public List<string> Queries { get; } = [];
        public List<JournalCursor> Reads { get; } = [];

        public ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken)
        {
            Queries.Add(volumeIdentity);
            if (Error is not null) return ValueTask.FromException<JournalCapability>(Error);
            return ValueTask.FromResult(new JournalCapability(volumeIdentity, 17, 1, 101, 1, 1024, 256));
        }

        public ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken)
        {
            Reads.Add(cursor);
            return ValueTask.FromResult(new JournalPage(cursor, cursor, []));
        }
    }
}
