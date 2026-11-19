using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Closed, non-pinning metadata emitted by the ordinary native update helpers.</summary>
internal static class RecoveryUpdateMetadata
{
    private const string Window = "passage-window-eligibility-migration";
    private const string Outlook = "outlook-watched-source-migration";
    private const string CodexWorkspace = "codex-workspace-notes-migration";
    private const string CodexWorkspaceTarget = "20261118225053_BindCodexNotesToWorkspace";
    private const string CodexWorkspaceSql = "E04F8B2FD6478D9A05D08C3C0966AD5C9BADCC70D1D859582C36032E27C8FCCC";
    private const string Media = "media-transcription-gpu-migration";
    private const string MediaTarget = "20261119125051_AddMediaTranscriptionGpuRequests";
    private const string MediaSql = "D37933C7B951F81A6A39E92F4D0814725AB1AA0DE166F5BE795BE7141FE0B46B";
    private const string GpuResidency = "gpu-model-residency-migration";
    private const string GpuResidencyTarget = "20261119201434_AddGpuModelResidencies";
    private const string GpuResidencySql = "7A9540981ED00AA671284E011673B194E637043066906B2288E51356B52AF986";
    private const string MediaHoldForwardIntent = "media-transcription-hold-forward-intent.json";
    private const string Queue = "source-deletion-pending-queue-index";
    private const string WindowTarget = "20261117210606_PersistPassageWindowEligibility";
    private const string OutlookTarget = "20261118224032_BindOutlookWatchedSources";
    private const string QueueTarget = "IX_SourceDeletionCleanupItems_PendingQueue";
    private const string WindowSql = "A5E04911FA8738E2161FD71A6A7DEA96D887D95F74849E90E948932E8F3868EC";
    private const string OutlookSql = "3B090C12B0DD55CD7B0CFAAF8623435322AC01AD038D21337A49E99CBEDA1CBD";
    private const string QueueSql = "C247F3429F303F959583F0BC59683A1F208B29B86EA509DD4638CE3CED23707E";
    private const string Intent = "Version Purpose ReleaseId Commit Identity Target SqlSha256 InitialKind OriginalHistory OutcomePath IntentPath Attempted Applied SettledState";
    private const string Outcome = "Identity Target IntentSha256 SettledState Applied";
    private const string Binding = "Version Purpose ReleaseId Commit Identity Target SqlSha256 OriginalPayloads CandidatePayloads ConfigurationSha256 CompanionConfigurationSha256 TaskIdentityHash LauncherSha256 EnableCompanionAfterValidation";
    private static readonly IReadOnlyDictionary<string, (string Required, string Optional)> Shapes =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [Window + "-intent.json"] = (Intent, ""), [Queue + "-intent.json"] = (Intent, ""),
            [Outlook + "-intent.json"] = (Intent, ""),
            [CodexWorkspace + "-intent.json"] = (Intent, ""),
            [Media + "-intent.json"] = (Intent, ""),
            [GpuResidency + "-intent.json"] = (Intent, ""),
            [Window + "-outcome.json"] = (Outcome, "Error"), [Queue + "-outcome.json"] = (Outcome, "Error"),
            [Outlook + "-outcome.json"] = (Outcome, "Error"),
            [CodexWorkspace + "-outcome.json"] = (Outcome, "Error"),
            [Media + "-outcome.json"] = (Outcome, "Error"),
            [GpuResidency + "-outcome.json"] = (Outcome, "Error"),
            [Queue + "-release-binding.json"] = (Binding, ""),
            [Window + "-release-binding.json"] = (Binding + " HelperTask HelperCheckpointPath HelperRootReadPlanSha256 HelperJournalPolicySha256 HelperRepairPolicySha256", ""),
            [Outlook + "-release-binding.json"] = (Binding + " HelperTask HelperCheckpointPath HelperRootReadPlanSha256 HelperJournalPolicySha256 HelperRepairPolicySha256", ""),
            [CodexWorkspace + "-release-binding.json"] = (Binding + " HelperTask HelperCheckpointPath HelperRootReadPlanSha256 HelperJournalPolicySha256 HelperRepairPolicySha256", ""),
            [Media + "-release-binding.json"] = (Binding + " HelperTask HelperCheckpointPath HelperRootReadPlanSha256 HelperJournalPolicySha256 HelperRepairPolicySha256" +
                " MediaOptionsSha256 MediaConfigurationAfterSha256 MediaConfigurationBackup", ""),
            [GpuResidency + "-release-binding.json"] = (Binding + " HelperTask HelperCheckpointPath HelperRootReadPlanSha256 HelperJournalPolicySha256 HelperRepairPolicySha256", ""),
            [Window + "-failure-reconciliation.json"] = ("ReleaseId Identity SettledState Error Attempted PredecessorStarted HoldRetained", "ScriptStackTrace ErrorTarget"),
            [Outlook + "-failure-reconciliation.json"] = ("ReleaseId Identity SettledState Error Attempted PredecessorStarted HoldRetained", "ScriptStackTrace ErrorTarget"),
            [CodexWorkspace + "-failure-reconciliation.json"] = ("ReleaseId Identity SettledState Error Attempted PredecessorStarted HoldRetained", "ScriptStackTrace ErrorTarget"),
            [Media + "-failure-reconciliation.json"] = ("ReleaseId Identity SettledState Error Attempted PredecessorStarted HoldRetained", "ScriptStackTrace ErrorTarget"),
            [GpuResidency + "-failure-reconciliation.json"] = ("ReleaseId Identity SettledState Error Attempted PredecessorStarted HoldRetained", "ScriptStackTrace ErrorTarget"),
            [Queue + "-failure-reconciliation.json"] = ("ReleaseId Identity SettledState Error Attempted PredecessorStarted HoldRetained", ""),
            ["pre-ddl-abort-intent.json"] = ("ReleaseId BindingSha256 RecordedUtc", ""),
            ["pre-ddl-abort-completed.json"] = ("ReleaseId OriginalSchema CompletedUtc", ""),
            ["outlook-pre-ddl-abort-intent.json"] = ("ReleaseId BindingSha256 RecordedUtc", ""),
            ["codex-workspace-pre-ddl-abort-intent.json"] = ("ReleaseId BindingSha256 RecordedUtc", ""),
            ["outlook-pre-ddl-abort-completed.json"] = ("ReleaseId OriginalSchema CompletedUtc", ""),
            ["codex-workspace-pre-ddl-abort-completed.json"] = ("ReleaseId OriginalSchema CompletedUtc", ""),
            ["media-transcription-pre-ddl-abort-intent.json"] = ("ReleaseId BindingSha256 RecordedUtc", ""),
            ["media-transcription-pre-ddl-abort-completed.json"] = ("ReleaseId OriginalSchema CompletedUtc", ""),
            ["gpu-residency-pre-ddl-abort-intent.json"] = ("ReleaseId BindingSha256 RecordedUtc", ""),
            ["gpu-residency-pre-ddl-abort-completed.json"] = ("ReleaseId OriginalSchema CompletedUtc", ""),
            [MediaHoldForwardIntent] = ("PriorReleaseId PriorBindingSha256 ReleaseId ReleaseBindingSha256 RecordedUtc", ""),
            ["stopped-release-hold-transfer.json"] = ("Version Purpose ReleaseId PriorReleaseId BindingSha256 HoldTransferred HoldTransferIntentUtc", "")
        };

    internal static bool Supports(string name) => Shapes.ContainsKey(name) ||
        name is "passage-window-eligibility-up.sql" or "source-deletion-pending-queue-index.sql" or "outlook-watched-source-up.sql" or
            "codex-workspace-notes-up.sql" or "media-transcription-gpu-up.sql" or "gpu-model-residency-up.sql";

    internal static bool IsTypedManifest(JsonElement value) => value.TryGetProperty("passage_window_eligibility", out _) ||
        value.TryGetProperty("source_deletion_pending_queue", out _) || value.TryGetProperty("outlook_watched_source", out _) ||
        value.TryGetProperty("codex_workspace_notes", out _) || value.TryGetProperty("media_transcription_gpu", out _) ||
        value.TryGetProperty("gpu_model_residency", out _);

    internal static bool ValidateManifest(JsonElement value, string packet)
    {
        var (field, family) = value.TryGetProperty("gpu_model_residency", out _) ? ("gpu_model_residency", GpuResidency) :
            value.TryGetProperty("media_transcription_gpu", out _) ? ("media_transcription_gpu", Media) :
            value.TryGetProperty("codex_workspace_notes", out _) ? ("codex_workspace_notes", CodexWorkspace) :
            value.TryGetProperty("outlook_watched_source", out _) ? ("outlook_watched_source", Outlook) :
            value.TryGetProperty("passage_window_eligibility", out _) ? ("passage_window_eligibility", Window) :
            ("source_deletion_pending_queue", Queue);
        if (!value.TryGetProperty(field, out var declaration)) return false;
        if (!Closed(value, "commit application_root migrations clean_slate " + field, "staged_at_utc") ||
            !Commit(value.GetProperty("commit"), packet) || !Absolute(value.GetProperty("application_root")) ||
            value.GetProperty("migrations").ValueKind != (family == Queue ? JsonValueKind.False : JsonValueKind.True) ||
            value.GetProperty("clean_slate").ValueKind != JsonValueKind.False ||
            value.TryGetProperty("staged_at_utc", out var time) && !Date(time)) return false;
        return Closed(declaration, "Target SqlSha256 ReleaseBindingSha256") &&
            TargetAndSql(declaration, family) && Hash(declaration.GetProperty("ReleaseBindingSha256"));
    }

    internal static bool Validate(string name, JsonElement value, string packet)
    {
        if (!Shapes.TryGetValue(name, out var shape) || !Closed(value, shape.Required, shape.Optional)) return false;
        var release = Path.GetFileName(packet);
        if (name == MediaHoldForwardIntent)
            return Text(value.GetProperty("PriorReleaseId")) == release &&
                Text(value.GetProperty("ReleaseId")) is { } successor && successor != release &&
                Regex.IsMatch(successor, @"^\d{8}T\d{6}Z-[0-9a-f]{12}$", RegexOptions.CultureInvariant) &&
                Hash(value.GetProperty("PriorBindingSha256")) && Hash(value.GetProperty("ReleaseBindingSha256")) &&
                Date(value.GetProperty("RecordedUtc"));
        foreach (var property in value.EnumerateObject())
        {
            var item = property.Value;
            var valid = property.Name switch
            {
                "Version" => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var version) && version == 1,
                "OriginalSchema" => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var schema) &&
                    schema == (name switch
                    {
                        "gpu-residency-pre-ddl-abort-completed.json" => 83,
                        "media-transcription-pre-ddl-abort-completed.json" => 82,
                        "codex-workspace-pre-ddl-abort-completed.json" => 81,
                        "outlook-pre-ddl-abort-completed.json" => 80,
                        _ => 79
                    }),
                "ReleaseId" => Text(item) == release,
                "Commit" => Commit(item, packet),
                "PriorReleaseId" => Text(item) is { } prior && Regex.IsMatch(prior, @"^\d{8}T\d{6}Z-[0-9a-f]{12}(?:-hybrid|-hybridpatch|-repositoryrecovery)?$", RegexOptions.CultureInvariant),
                "Applied" or "Attempted" or "EnableCompanionAfterValidation" or "PredecessorStarted" or "HoldRetained" or "HoldTransferred" => Boolean(item),
                "OriginalPayloads" or "CandidatePayloads" => Payloads(item),
                "HelperTask" => Helper(item),
                "OriginalHistory" => item.ValueKind == JsonValueKind.Array && item.GetArrayLength() <= 256 &&
                    item.EnumerateArray().All(entry => Text(entry) is { } text && Regex.IsMatch(text, @"^\d{14}_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)),
                "CompletedUtc" or "RecordedUtc" or "HoldTransferIntentUtc" => Date(item),
                "HelperCheckpointPath" => Absolute(item),
                "MediaConfigurationBackup" => Absolute(item) && Text(item) is { } backup &&
                    Path.GetFileName(backup) == $"appsettings.Production.json.{release}.previous" &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(backup)), "Config", StringComparison.OrdinalIgnoreCase),
                "IntentPath" => SamePath(item, Path.Combine(packet, name)),
                "OutcomePath" => SamePath(item, Path.Combine(packet, name.Replace("-intent.json", "-outcome.json", StringComparison.Ordinal))),
                "InitialKind" => Text(item) is "Old" or "New",
                "SettledState" => Text(item) is "Old" or "New" or "Unknown" or "Unattempted",
                "ScriptStackTrace" or "ErrorTarget" => item.ValueKind == JsonValueKind.Null || Text(item, empty: true) is not null,
                _ when property.Name.EndsWith("Sha256", StringComparison.Ordinal) || property.Name == "TaskIdentityHash" => Hash(item),
                _ => Text(item) is not null
            };
            if (!valid) return false;
        }
        foreach (var family in new[] { Window, Outlook, CodexWorkspace, Media, GpuResidency, Queue })
        {
            if (!name.StartsWith(family + "-", StringComparison.Ordinal)) continue;
            if (value.TryGetProperty("Target", out _) && !TargetAndSql(value, family, optionalSql: true)) return false;
            if (value.TryGetProperty("Purpose", out var purpose) && Text(purpose) != family +
                (name.EndsWith("-release-binding.json", StringComparison.Ordinal) ? "-release" : "")) return false;
            break;
        }
        return name != "stopped-release-hold-transfer.json" || Text(value.GetProperty("Purpose")) == "incremental-stopped-release-transfer";
    }

    private static bool TargetAndSql(JsonElement value, string family, bool optionalSql = false)
    {
        var (target, hash) = family switch
        {
            Window => (WindowTarget, WindowSql),
            Outlook => (OutlookTarget, OutlookSql),
            CodexWorkspace => (CodexWorkspaceTarget, CodexWorkspaceSql),
            Media => (MediaTarget, MediaSql),
            GpuResidency => (GpuResidencyTarget, GpuResidencySql),
            Queue => (QueueTarget, QueueSql),
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
        return Text(value.GetProperty("Target")) == target &&
            (value.TryGetProperty("SqlSha256", out var sql) ? string.Equals(Text(sql), hash, StringComparison.OrdinalIgnoreCase) : optionalSql);
    }

    private static bool Closed(JsonElement value, string required, string optional = "")
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var fields = required.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowed = fields.Concat(optional.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == actual.Distinct(StringComparer.Ordinal).Count() && actual.All(allowed.Contains) && fields.All(field => value.TryGetProperty(field, out _));
    }

    private static bool Payloads(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) return false;
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in value.EnumerateArray())
            if (!Closed(payload, "Role Root Sha256") || Text(payload.GetProperty("Role")) is not { } role ||
                role is not ("Web" or "OutlookHost" or "Cli") || !roles.Add(role) || !Absolute(payload.GetProperty("Root")) || !Hash(payload.GetProperty("Sha256"))) return false;
        return true;
    }

    private static bool Helper(JsonElement value) =>
        Closed(value, "TaskName PrincipalSid Executable Arguments WorkingDirectory DefinitionSha256 SecuritySha256 Enabled Running") &&
        Text(value.GetProperty("TaskName")) == "FluxKnowledge.SourceAccess" && Text(value.GetProperty("PrincipalSid")) is not null &&
        Absolute(value.GetProperty("Executable")) && Absolute(value.GetProperty("WorkingDirectory")) && Text(value.GetProperty("Arguments"), empty: true) is not null &&
        Hash(value.GetProperty("DefinitionSha256")) && Hash(value.GetProperty("SecuritySha256")) && Boolean(value.GetProperty("Enabled")) && Boolean(value.GetProperty("Running"));

    private static bool Commit(JsonElement value, string packet) => Text(value) is { } text &&
        Regex.IsMatch(text, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) && Path.GetFileName(packet).Substring(17, 12) == text[..12];
    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Date(JsonElement value) => value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out _);
    private static bool Hash(JsonElement value) => Text(value) is { } text && Regex.IsMatch(text, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static bool Absolute(JsonElement value) => Text(value) is { } text && Path.IsPathFullyQualified(text);
    private static bool SamePath(JsonElement value, string expected)
    {
        if (Text(value) is not { } text || !Path.IsPathFullyQualified(text)) return false;
        try { return string.Equals(Path.GetFullPath(text), expected, StringComparison.OrdinalIgnoreCase); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static string? Text(JsonElement value, bool empty = false) => value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
        text.Length <= 65536 && (empty || !string.IsNullOrWhiteSpace(text)) ? text : null;
}
