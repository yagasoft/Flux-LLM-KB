using System.Text.Json;
using System.Text.Json.Nodes;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class RecoveryUpdatePacketTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RecoveryUpdatePackets_" + Guid.NewGuid().ToString("N"));
    private const string Release = "20261117T232915Z-aaaaaaaaaaaa";
    private string Recovery => Path.Combine(_root, "Recovery");
    private string Index => Path.Combine(_root, "Index");
    private string Packet => Path.Combine(Recovery, "IncrementalUpdates", Release);
    private const string WindowSql = "A5E04911FA8738E2161FD71A6A7DEA96D887D95F74849E90E948932E8F3868EC";
    private const string QueueSql = "C247F3429F303F959583F0BC59683A1F208B29B86EA509DD4638CE3CED23707E";
    private const string OutlookSql = "3B090C12B0DD55CD7B0CFAAF8623435322AC01AD038D21337A49E99CBEDA1CBD";
    private const string CodexWorkspaceSql = "E04F8B2FD6478D9A05D08C3C0966AD5C9BADCC70D1D859582C36032E27C8FCCC";
    private static string Hash => new('b', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outlook_migration_packets_are_non_pinning_and_reject_foreign_bindings(bool failed)
    {
        WritePacket(true, failed, outlook: true);
        if (failed)
        {
            Write("outlook-pre-ddl-abort-intent.json", new { ReleaseId = Release, BindingSha256 = Hash, RecordedUtc = DateTimeOffset.UtcNow });
            Write("outlook-pre-ddl-abort-completed.json", new { ReleaseId = Release, OriginalSchema = 80, CompletedUtc = DateTimeOffset.UtcNow });
        }
        Assert.Empty(new RecoveryIndexReferenceAudit(Index).Capture(Recovery).Pins);
        const string binding = "outlook-watched-source-migration-release-binding.json";
        Mutate(binding, value => value["SqlSha256"] = WindowSql);
        var refusal = Assert.Throws<RecoveryIndexAuditException>(() => new RecoveryIndexReferenceAudit(Index).Capture(Recovery));
        Assert.Equal(Path.GetRelativePath(Recovery, Path.Combine(Packet, binding)), refusal.RelativePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Workspace_notes_migration_packets_reject_foreign_bindings(bool failed)
    {
        WritePacket(true, failed, codexWorkspace: true);
        if (failed)
        {
            Write("codex-workspace-pre-ddl-abort-intent.json", new { ReleaseId = Release, BindingSha256 = Hash, RecordedUtc = DateTimeOffset.UtcNow });
            Write("codex-workspace-pre-ddl-abort-completed.json", new { ReleaseId = Release, OriginalSchema = 81, CompletedUtc = DateTimeOffset.UtcNow });
        }
        Assert.Empty(new RecoveryIndexReferenceAudit(Index).Capture(Recovery).Pins);
        const string binding = "codex-workspace-notes-migration-release-binding.json";
        Mutate(binding, value => value["SqlSha256"] = OutlookSql);
        var refusal = Assert.Throws<RecoveryIndexAuditException>(() => new RecoveryIndexReferenceAudit(Index).Capture(Recovery));
        Assert.Equal(Path.GetRelativePath(Recovery, Path.Combine(Packet, binding)), refusal.RelativePath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Native_queue_and_window_update_packets_are_self_contained_even_after_a_pre_DDL_failure(bool window, bool failed)
    {
        WritePacket(window, failed);
        var audit = new RecoveryIndexReferenceAudit(Index).Capture(Recovery);
        Assert.Empty(audit.Pins);
        Assert.Equal(1, audit.PacketCount);
        Assert.Equal(audit.InventoryChecksum, new RecoveryIndexReferenceAudit(Index).Capture(Recovery).InventoryChecksum);
        File.AppendAllText(Path.Combine(Packet, "stopped-release-hold-transfer.json"), " ");
        Assert.NotEqual(audit.InventoryChecksum, new RecoveryIndexReferenceAudit(Index).Capture(Recovery).InventoryChecksum);
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("stopped-release-hold-transfer.json")]
    [InlineData("passage-window-eligibility-migration-intent.json")]
    [InlineData("passage-window-eligibility-migration-outcome.json")]
    [InlineData("passage-window-eligibility-migration-release-binding.json")]
    [InlineData("passage-window-eligibility-migration-failure-reconciliation.json")]
    [InlineData("pre-ddl-abort-intent.json")]
    [InlineData("pre-ddl-abort-completed.json")]
    public void Unknown_reference_fields_are_refused_in_every_new_metadata_shape(string file)
    {
        WritePacket(true, true);
        Mutate(file, value => value["IndexPath"] = Index);
        var refusal = Assert.Throws<RecoveryIndexAuditException>(() => new RecoveryIndexReferenceAudit(Index).Capture(Recovery));
        Assert.Equal(Path.GetRelativePath(Recovery, Path.Combine(Packet, file)), refusal.RelativePath);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("helper")]
    [InlineData("declaration")]
    [InlineData("wrong-target")]
    [InlineData("wrong-sql")]
    [InlineData("wrong-release")]
    [InlineData("wrong-commit")]
    [InlineData("missing-time")]
    [InlineData("helper-entry")]
    [InlineData("unknown-file")]
    [InlineData("duplicate-role")]
    [InlineData("escaped-receipt")]
    public void Native_update_shapes_do_not_hide_unknown_nested_references_or_other_packet_types(string fault)
    {
        WritePacket(true, true);
        const string binding = "passage-window-eligibility-migration-release-binding.json";
        switch (fault)
        {
            case "payload": Mutate(binding, value => value["CandidatePayloads"]![0]!["IndexPath"] = Index); break;
            case "helper": Mutate(binding, value => value["HelperTask"]!["IndexPath"] = Index); break;
            case "declaration": Mutate("manifest.json", value => value["passage_window_eligibility"]!["IndexPath"] = Index); break;
            case "wrong-target": Mutate("manifest.json", value => value["passage_window_eligibility"]!["Target"] = "unknown-migration"); break;
            case "wrong-sql": Mutate(binding, value => value["SqlSha256"] = new string('c', 64)); break;
            case "wrong-release": Mutate(binding, value => value["ReleaseId"] = "20261117T232915Z-bbbbbbbbbbbb"); break;
            case "wrong-commit": Mutate(binding, value => value["Commit"] = new string('c', 40)); break;
            case "missing-time": Mutate("pre-ddl-abort-completed.json", value => value.Remove("CompletedUtc")); break;
            case "helper-entry": File.Move(Path.Combine(Packet, "candidate-source-helper", "FluxKnowledge.Cli.dll"), Path.Combine(Packet, "candidate-source-helper", "FluxKnowledge.Web.dll")); break;
            case "unknown-file": File.WriteAllText(Path.Combine(Packet, "unknown-recovery-reference.json"), "{}"); break;
            case "duplicate-role": Mutate(binding, value => value["CandidatePayloads"]![1]!["Role"] = "Web"); break;
            case "escaped-receipt": Mutate("passage-window-eligibility-migration-intent.json", value => value["IntentPath"] = Path.Combine(Index, "passage-window-eligibility-migration-intent.json")); break;
        }
        Assert.ThrowsAny<Exception>(() => new RecoveryIndexReferenceAudit(Index).Capture(Recovery));
    }

    [Fact]
    public void Pre_DDL_failure_evidence_does_not_need_an_intent_that_was_never_written()
    {
        WritePacket(true, true);
        File.Delete(Path.Combine(Packet, "passage-window-eligibility-migration-intent.json"));
        File.Delete(Path.Combine(Packet, "passage-window-eligibility-migration-outcome.json"));
        Assert.Empty(new RecoveryIndexReferenceAudit(Index).Capture(Recovery).Pins);
    }

    [Fact]
    public void Queue_failure_reconciliation_remains_closed_to_unknown_references()
    {
        WritePacket(false, true);
        const string file = "source-deletion-pending-queue-index-failure-reconciliation.json";
        Assert.Empty(new RecoveryIndexReferenceAudit(Index).Capture(Recovery).Pins);
        Mutate(file, value => value["IndexPath"] = Index);
        var refusal = Assert.Throws<RecoveryIndexAuditException>(() => new RecoveryIndexReferenceAudit(Index).Capture(Recovery));
        Assert.Equal(Path.GetRelativePath(Recovery, Path.Combine(Packet, file)), refusal.RelativePath);
    }

    [Fact]
    public void A_new_native_classifier_identity_keeps_legacy_staging_separate()
    {
        Assert.NotEqual(1, RecoveryIndexReferenceAudit.ClassifierVersion);
        Assert.NotEqual(2, RecoveryIndexReferenceAudit.ClassifierVersion);
    }

    private void WritePacket(bool window, bool failed, bool outlook = false, bool codexWorkspace = false)
    {
        Directory.CreateDirectory(Packet); Directory.CreateDirectory(Index);
        var family = codexWorkspace ? "codex-workspace-notes-migration" : outlook ? "outlook-watched-source-migration" : window ? "passage-window-eligibility-migration" : "source-deletion-pending-queue-index";
        var target = codexWorkspace ? "20261118225053_BindCodexNotesToWorkspace" : outlook ? "20261118224032_BindOutlookWatchedSources" : window ? "20261117210606_PersistPassageWindowEligibility" : "IX_SourceDeletionCleanupItems_PendingQueue";
        var sql = codexWorkspace ? CodexWorkspaceSql : outlook ? OutlookSql : window ? WindowSql : QueueSql;
        var manifestField = codexWorkspace ? "codex_workspace_notes" : outlook ? "outlook_watched_source" : window ? "passage_window_eligibility" : "source_deletion_pending_queue";
        var up = codexWorkspace ? "codex-workspace-notes-up.sql" : outlook ? "outlook-watched-source-up.sql" : window ? "passage-window-eligibility-up.sql" : "source-deletion-pending-queue-index.sql";
        var payloads = new[] { "Web", "OutlookHost", "Cli" }.Select(role => new { Role = role, Root = Path.Combine(_root, role), Sha256 = Hash }).ToArray();
        var binding = new Dictionary<string, object?> {
            ["Version"] = 1, ["Purpose"] = family + "-release", ["ReleaseId"] = Release, ["Commit"] = new string('a', 40),
            ["Identity"] = "synthetic|test|" + Guid.NewGuid(), ["Target"] = target, ["SqlSha256"] = sql,
            ["OriginalPayloads"] = payloads, ["CandidatePayloads"] = payloads, ["ConfigurationSha256"] = Hash,
            ["CompanionConfigurationSha256"] = Hash, ["TaskIdentityHash"] = Hash, ["LauncherSha256"] = Hash,
            ["EnableCompanionAfterValidation"] = false };
        if (window)
        {
            binding["HelperTask"] = new { TaskName = "FluxKnowledge.SourceAccess", PrincipalSid = "synthetic", Executable = Path.Combine(_root, "helper.exe"),
                Arguments = "source-access-host", WorkingDirectory = _root, DefinitionSha256 = Hash, SecuritySha256 = Hash, Enabled = true, Running = true };
            foreach (var field in new[] { "HelperRootReadPlanSha256", "HelperJournalPolicySha256", "HelperRepairPolicySha256" }) binding[field] = Hash;
            binding["HelperCheckpointPath"] = Path.Combine(_root, "checkpoint.json");
            var helper = Path.Combine(Packet, "candidate-source-helper"); Directory.CreateDirectory(helper);
            File.WriteAllText(Path.Combine(helper, "FluxKnowledge.Cli.dll"), "synthetic CLI, never executed");
        }
        Write(family + "-release-binding.json", binding);
        Write("manifest.json", new Dictionary<string, object?> { ["commit"] = new string('a', 40), ["application_root"] = Path.Combine(_root, "App"),
            ["migrations"] = window, ["clean_slate"] = false, [manifestField] = new { Target = target, SqlSha256 = sql, ReleaseBindingSha256 = Hash } });
        Write(family + "-intent.json", new { Version = 1, Purpose = family, ReleaseId = Release, Commit = new string('a', 40), Identity = "synthetic|test|" + Guid.NewGuid(),
            Target = target, SqlSha256 = sql, InitialKind = "Old", OriginalHistory = new[] { "20261101195325_AddVectorPublicationMetadata" },
            OutcomePath = Path.Combine(Packet, family + "-outcome.json"), IntentPath = Path.Combine(Packet, family + "-intent.json"),
            Attempted = false, Applied = false, SettledState = "Unattempted" });
        Write(family + "-outcome.json", new { Identity = "synthetic|test|" + Guid.NewGuid(), Target = target, IntentSha256 = Hash, SettledState = failed ? "Old" : "New", Applied = !failed });
        File.WriteAllText(Path.Combine(Packet, up), "-- synthetic SQL metadata, never executed");
        Write("stopped-release-hold-transfer.json", new { Version = 1, Purpose = "incremental-stopped-release-transfer", ReleaseId = Release,
            PriorReleaseId = "20261117T220000Z-bbbbbbbbbbbb", BindingSha256 = Hash, HoldTransferred = true, HoldTransferIntentUtc = DateTimeOffset.UtcNow });
        if (failed)
        {
            Write(family + "-failure-reconciliation.json", new { ReleaseId = Release, Identity = "synthetic|test|" + Guid.NewGuid(), SettledState = "Old",
                Error = "synthetic failure", Attempted = false, PredecessorStarted = false, HoldRetained = true });
            if (window && !outlook && !codexWorkspace)
            {
                Write("pre-ddl-abort-intent.json", new { ReleaseId = Release, BindingSha256 = Hash, RecordedUtc = DateTimeOffset.UtcNow });
                Write("pre-ddl-abort-completed.json", new { ReleaseId = Release, OriginalSchema = 79, CompletedUtc = DateTimeOffset.UtcNow });
            }
        }
    }

    private void Write(string name, object value) => File.WriteAllText(Path.Combine(Packet, name), JsonSerializer.Serialize(value));
    private void Mutate(string file, Action<JsonObject> change)
    {
        var path = Path.Combine(Packet, file); var value = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); change(value); File.WriteAllText(path, value.ToJsonString());
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
