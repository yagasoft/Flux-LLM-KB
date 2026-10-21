using System.ComponentModel;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class SourceAccessRecoveryTests
{
    private const string Root = @"I:\private-source";

    [Fact]
    public void Successful_repair_retries_the_actual_guarded_open()
    {
        var client = new Client(SourceAccessRepairOutcome.Repaired);
        var attempts = 0;
        var value = SourceAccessRecovery.Run(Root, "root-proof", client, () =>
        {
            if (++attempts == 1) throw Denied(Path.Combine(Root, "document.txt"));
            return "guarded content";
        });
        Assert.Equal("guarded content", value);
        Assert.Equal(2, attempts);
        var request = Assert.Single(client.Requests);
        Assert.Equal("document.txt", request.RelativePath);
        Assert.Equal("root-proof", request.RootIdentityFingerprint);
    }

    [Fact]
    public void Missing_policy_is_actionable_and_does_not_loop()
    {
        var client = new Client(SourceAccessRepairOutcome.NotApproved);
        var exception = Assert.Throws<SourceAccessDeniedException>(() => SourceAccessRecovery.Run<int>(Root, null, client,
            () => throw Denied(Path.Combine(Root, "old.txt"))));
        Assert.Equal("SourceAccessRepairNotApproved", exception.ReasonCode);
        Assert.Single(client.Requests);
    }

    [Fact]
    public void Cloud_unavailability_found_by_the_helper_remains_unavailable_content()
    {
        var client = new Client(SourceAccessRepairOutcome.ContentUnavailable);
        Assert.Throws<SourceContentUnavailableException>(() => SourceAccessRecovery.Run<int>(Root, null, client,
            () => throw Denied(Path.Combine(Root, "cloud-only.txt"))));
        Assert.Single(client.Requests);
    }

    [Fact]
    public void Repeated_same_denial_is_not_treated_as_success_or_repaired_again()
    {
        var client = new Client(SourceAccessRepairOutcome.Repaired);
        var exception = Assert.Throws<SourceAccessDeniedException>(() => SourceAccessRecovery.Run<int>(Root, null, client,
            () => throw Denied(Path.Combine(Root, "old.txt"))));
        Assert.Equal("SourceAccessRepairUnconfirmed", exception.ReasonCode);
        Assert.Single(client.Requests);
    }

    [Theory]
    [InlineData(@"I:\outside\document.txt")]
    [InlineData(@"I:\private-source-other\document.txt")]
    [InlineData(@"I:\")]
    public void Denied_ancestor_or_sibling_outside_the_source_cannot_request_a_grant(string target)
    {
        var client = new Client(SourceAccessRepairOutcome.Repaired);
        var exception = Assert.Throws<SourceAccessDeniedException>(() => SourceAccessRecovery.Run<int>(Root, null, client,
            () => throw Denied(target)));
        Assert.Equal("SourceAccessOutsideApprovedRoot", exception.ReasonCode);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void Security_refusal_and_unclassified_unauthorised_exception_never_request_repair()
    {
        var client = new Client(SourceAccessRepairOutcome.Repaired);
        var unsafePath = new SourcePathSecurityException("SourcePathOutsideRoot", "Escape refused");
        Assert.Same(unsafePath, Assert.Throws<SourcePathSecurityException>(() =>
            SourceAccessRecovery.Run<int>(Root, null, client, () => throw unsafePath)));
        var unknown = new UnauthorizedAccessException("Unknown origin", new Win32Exception(5));
        Assert.Same(unknown, Assert.Throws<UnauthorizedAccessException>(() =>
            SourceAccessRecovery.Run<int>(Root, null, client, () => throw unknown)));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void Component_repairs_are_bounded_even_when_each_failure_moves_to_a_different_path()
    {
        var client = new Client(SourceAccessRepairOutcome.Repaired);
        var attempted = 0;
        var exception = Assert.Throws<SourceAccessDeniedException>(() => SourceAccessRecovery.Run<int>(Root, null, client,
            () => throw Denied(Path.Combine(Root, "component" + ++attempted), true)));
        Assert.Equal(16, client.Requests.Count);
        Assert.Equal("SourceAccessRepairLimit", exception.ReasonCode);
    }

    [Fact]
    public void Disabled_recovery_retains_the_original_denial()
    {
        var original = Denied(Path.Combine(Root, "document.txt"));
        Assert.Same(original, Assert.Throws<SourceAccessDeniedException>(() =>
            SourceAccessRecovery.Run<int>(Root, null, null, () => throw original)));
    }

    [Fact]
    public void Precancelled_operation_performs_no_open_or_repair()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new Client(SourceAccessRepairOutcome.Repaired);
        var opens = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => SourceAccessRecovery.Run<int>(Root, null, client,
            () => { opens++; throw Denied(Path.Combine(Root, "file.txt")); }, cancellation.Token));
        Assert.Equal(0, opens);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void Cancellation_after_one_component_stops_before_another_open_or_repair()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Client(SourceAccessRepairOutcome.Repaired, cancellation.Cancel);
        var opens = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => SourceAccessRecovery.Run<int>(Root, null, client,
            () => throw Denied(Path.Combine(Root, "component" + ++opens), true), cancellation.Token));
        Assert.Equal(1, opens);
        Assert.Single(client.Requests);
    }

    private static SourceAccessDeniedException Denied(string path, bool directory = false) => new(path, directory, new Win32Exception(5));
    private sealed class Client(SourceAccessRepairOutcome outcome, Action? afterRepair = null) : ISourceAccessRepairClient
    {
        internal List<SourceAccessRepairRequest> Requests { get; } = [];
        public SourceAccessRepairOutcome TryRepair(SourceAccessRepairRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); afterRepair?.Invoke(); return outcome; }
    }
}
