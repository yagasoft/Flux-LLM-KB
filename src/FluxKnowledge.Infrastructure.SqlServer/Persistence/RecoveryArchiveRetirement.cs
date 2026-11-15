using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Same-volume archive renames only. The caller holds deployment and recovery-reference exclusion.</summary>
public static class RecoveryArchiveRetirement
{
    private sealed record MoveEvent(Guid OperationId, string PlanHash, string RelativePath, string Identity, string Phase);

    public static async ValueTask MoveAsync(RecoveryArchiveRetirementPlan plan,
        Func<ValueTask> requireSession, CancellationToken token)
    {
        Validate(plan);
        var planHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan)));
        var journal = Path.Combine(plan.QuarantineRoot, "retirement-" + plan.OperationId.ToString("N") + ".jsonl");
        var events = ReadJournal(journal, plan, planHash);
        // Validate the complete disposition before the first rename, including a resumed run.
        foreach (var move in plan.Moves) CheckMove(plan, move, events);
        foreach (var move in plan.Moves)
        {
            token.ThrowIfCancellationRequested();
            await requireSession();
            if (ContainsApplication(plan, move)) ValidateRuntimeSnapshots(plan);
            var source = Child(plan.RecoveryRoot, move.RelativePath);
            var destination = Child(plan.QuarantineRoot, move.RelativePath);
            CheckMove(plan, move, events);
            if (Directory.Exists(destination)) continue;
            var intent = new MoveEvent(plan.OperationId, planHash, move.RelativePath, move.DirectoryIdentity, "intent");
            Append(journal, intent); events.Add(intent);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            NoLinks(Path.GetDirectoryName(destination)!);
            await requireSession();
            if (PhysicalFileIdentity.GetDirectory(source).IdentityFingerprint != move.DirectoryIdentity)
                throw Refuse("archive-identity-changed");
            Directory.Move(source, destination);
            await requireSession();
            var completed = intent with { Phase = "completed" };
            Append(journal, completed); events.Add(completed);
        }
        Validate(plan);
    }

    public static void Validate(RecoveryArchiveRetirementPlan plan)
    {
        if (plan.Version != 1 || plan.OperationId == Guid.Empty || plan.DatabaseIncarnation == Guid.Empty ||
            plan.CorpusEpoch == Guid.Empty || plan.CorpusVersion < 0 || plan.Moves.Count is < 1 or > 512 ||
            plan.RetainedPackets.Count > 512 || plan.Writers.Count > 1024 ||
            plan.ServiceOwners.Count is < 1 or > 8 || plan.ApplicationSha256.Length != 64)
            throw Refuse("plan-invalid");
        foreach (var root in new[] { plan.RecoveryRoot, plan.IndexRoot, plan.QuarantineRoot, Path.GetDirectoryName(plan.ApplicationEntry)! })
        {
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root) ||
                !string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(plan.RecoveryRoot), StringComparison.OrdinalIgnoreCase))
                throw Refuse("local-same-volume-roots-required");
            NoLinks(root);
        }
        if (plan.RequiredAbsentPaths is { } absent)
        {
            if (absent.Count > 16) throw Refuse("absence-proof-budget");
            foreach (var path in absent)
            {
                if (!Path.IsPathFullyQualified(path) || File.Exists(path) || Directory.Exists(path))
                    throw Refuse("live-recovery-or-deployment-hold");
                NoLinks(Path.GetDirectoryName(path)!);
            }
        }
        foreach (var live in new[] { plan.RecoveryRoot, plan.IndexRoot, Path.GetDirectoryName(plan.ApplicationEntry)! })
            if (Within(plan.QuarantineRoot, live) || Within(live, plan.QuarantineRoot)) throw Refuse("quarantine-overlaps-live-path");
        if (PhysicalFileIdentity.GetDirectory(plan.RecoveryRoot).IdentityFingerprint != plan.RecoveryIdentity)
            throw Refuse("root-identity-changed");
        var all = plan.Moves.Select(m => m.RelativePath).Concat(plan.RetainedPackets).ToArray();
        if (all.Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length) throw Refuse("packet-disposition-overlap");
        foreach (var relative in all) _ = Child(plan.RecoveryRoot, relative);
        foreach (var move in plan.Moves)
        {
            var parts = move.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!(parts.Length == 2 && parts[0] == "IncrementalUpdates") &&
                !(parts.Length == 1 && (parts[0].StartsWith("HookUtf8-", StringComparison.Ordinal) ||
                    parts[0].StartsWith("OcrEnablement-", StringComparison.Ordinal))))
                throw Refuse("archive-category-requires-separate-finalisation-proof");
        }
        var current = Child(plan.RecoveryRoot, plan.RetainedApplicationEntry);
        var baselineMove = plan.Moves.SingleOrDefault(move => ContainsApplication(plan, move));
        if (baselineMove is null && !plan.RetainedPackets.Any(packet => Within(current, Child(plan.RecoveryRoot, packet))))
            throw Refuse("running-release-not-bound");
        if (baselineMove is not null)
        {
            if (plan.Moves[^1] != baselineMove || plan.RuntimeSnapshots is not { Count: > 0 })
                throw Refuse("running-release-retirement-baseline-required");
            if (!File.Exists(current)) current = Child(plan.QuarantineRoot, plan.RetainedApplicationEntry);
        }
        foreach (var entry in new[] { plan.ApplicationEntry, current })
        {
            NoLinks(entry);
            using var input = new FileStream(PhysicalFileIdentity.OpenReadNoFollow(entry), FileAccess.Read);
            if (input.Length is < 1 or > 64 * 1024 * 1024 ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(input)), plan.ApplicationSha256, StringComparison.OrdinalIgnoreCase))
                throw Refuse("running-release-changed");
        }
        var packets = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(plan.RecoveryRoot))
        {
            if (packets.Count > 1024 || !Directory.Exists(entry)) throw Refuse("packet-inventory-changed");
            NoLinks(entry);
            if (Path.GetFileName(entry) == "IncrementalUpdates")
            {
                foreach (var packet in Directory.EnumerateFileSystemEntries(entry))
                {
                    if (packets.Count > 1024 || !Directory.Exists(packet)) throw Refuse("packet-inventory-changed");
                    NoLinks(packet); packets.Add(Path.GetRelativePath(plan.RecoveryRoot, packet));
                }
            }
            else packets.Add(Path.GetRelativePath(plan.RecoveryRoot, entry));
        }
        var expected = plan.RetainedPackets.Concat(plan.Moves.Where(m => Directory.Exists(Child(plan.RecoveryRoot, m.RelativePath)))
            .Select(m => m.RelativePath)).Order(StringComparer.OrdinalIgnoreCase);
        if (!packets.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
            throw Refuse("packet-inventory-changed");
    }

    internal static void ValidateRuntimeSnapshots(RecoveryArchiveRetirementPlan plan)
    {
        var snapshots = plan.RuntimeSnapshots;
        if (snapshots is null || snapshots.Count is < 1 or > 8 ||
            !snapshots.Any(snapshot => Within(plan.ApplicationEntry, snapshot.RootPath)))
            throw Refuse("runtime-baseline-required");
        var count = 0; long bytes = 0;
        foreach (var snapshot in snapshots)
        {
            NoLinks(snapshot.RootPath);
            if (!Path.IsPathFullyQualified(snapshot.RootPath) || !Directory.Exists(snapshot.RootPath) ||
                Within(snapshot.RootPath, plan.RecoveryRoot) || Within(snapshot.RootPath, plan.QuarantineRoot) ||
                snapshot.Files.Count is < 1 or > 2048)
                throw Refuse("runtime-baseline-invalid");
            var expected = snapshot.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(snapshot.RootPath);
            while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                NoLinks(entry);
                if (++count > 8192) throw Refuse("runtime-baseline-budget");
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                var relative = Path.GetRelativePath(snapshot.RootPath, entry);
                if (!expected.Remove(relative, out var file) || Child(snapshot.RootPath, file.RelativePath) != Path.GetFullPath(entry))
                    throw Refuse("runtime-file-set-changed");
                using var input = new FileStream(PhysicalFileIdentity.OpenReadNoFollow(entry), FileAccess.Read);
                bytes = checked(bytes + input.Length);
                if (input.Length != file.Bytes || bytes > 2L * 1024 * 1024 * 1024 ||
                    !string.Equals(Convert.ToHexStringLower(SHA256.HashData(input)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw Refuse("runtime-file-changed");
            }
            if (expected.Count != 0) throw Refuse("runtime-file-set-changed");
        }
    }

    private static bool ContainsApplication(RecoveryArchiveRetirementPlan plan, RecoveryArchiveMove move) =>
        Within(Child(plan.RecoveryRoot, plan.RetainedApplicationEntry), Child(plan.RecoveryRoot, move.RelativePath));

    private static void CheckMove(RecoveryArchiveRetirementPlan plan, RecoveryArchiveMove move, List<MoveEvent> events)
    {
        var source = Child(plan.RecoveryRoot, move.RelativePath);
        var destination = Child(plan.QuarantineRoot, move.RelativePath);
        var sourceExists = Directory.Exists(source); var destinationExists = Directory.Exists(destination);
        if (sourceExists == destinationExists || File.Exists(source) || File.Exists(destination)) throw Refuse("archive-location-ambiguous");
        var actual = sourceExists ? source : destination;
        NoLinks(actual);
        if (PhysicalFileIdentity.GetDirectory(actual).IdentityFingerprint != move.DirectoryIdentity)
            throw Refuse("archive-identity-changed");
        if (destinationExists && !events.Any(e => e.RelativePath == move.RelativePath && e.Identity == move.DirectoryIdentity && e.Phase == "intent"))
            throw Refuse("archive-intent-missing");
        if (sourceExists && events.Any(e => e.RelativePath == move.RelativePath && e.Phase == "completed"))
            throw Refuse("completed-archive-restored");
    }

    private static List<MoveEvent> ReadJournal(string path, RecoveryArchiveRetirementPlan plan, string hash)
    {
        if (!File.Exists(path)) return [];
        NoLinks(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw Refuse("journal-budget");
        var result = new List<MoveEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (result.Count >= 4096) throw Refuse("journal-budget");
            var entry = JsonSerializer.Deserialize<MoveEvent>(line) ?? throw Refuse("journal-invalid");
            if (entry.OperationId != plan.OperationId || entry.PlanHash != hash || entry.Phase is not ("intent" or "completed") ||
                !plan.Moves.Any(m => m.RelativePath == entry.RelativePath && m.DirectoryIdentity == entry.Identity))
                throw Refuse("journal-binding-changed");
            result.Add(entry);
        }
        return result;
    }

    private static void Append(string path, MoveEvent value)
    {
        if (File.Exists(path)) NoLinks(path);
        using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        output.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n")); output.Flush(true);
    }

    private static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative) ||
            relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "" or "." or ".."))
            throw Refuse("relative-path-invalid");
        var child = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(child, root) || string.Equals(child, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw Refuse("relative-path-outside-root");
        return child;
    }

    internal static bool Within(string path, string root) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Refuse("linked-path");
    }

    private static InvalidOperationException Refuse(string reason) => new("index-recovery-retirement-" + reason);
}
