using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Integrations.Files;

public sealed partial class LocalSourceEnumerator
{
    private async Task CollectMetadataFilesAsync(SourceRootConfiguration root, string directory, string rootIdentity,
        ICollection<string> files, ICollection<SourceEnumerationEvidence> errors, CrawlBudget budget,
        ISourceAccessRepairClient? accessRepair, Func<CancellationToken, ValueTask> pace)
    {
        if (!budget.CheckTime()) return;
        var relative = Path.GetRelativePath(root.CanonicalPath, directory);
        try
        {
            var entries = new List<(string Name, FileAttributes Attributes)>();
            string directoryIdentity;
            using (var cursor = SourceAccessRecovery.Run(root.CanonicalPath, rootIdentity, accessRepair,
                () => WindowsSourcePathAccess.OpenDirectoryPath(directory, enumerate: true, allowNamespaceChanges: true), budget.CallerCancellation))
            {
                directoryIdentity = cursor.Identity.IdentityFingerprint;
                if (cursor.IdentityAt(root.CanonicalPath).IdentityFingerprint != rootIdentity)
                    throw new SourceSnapshotChangedException("SourceMetadataRootChanged");
                var parentId = cursor.HeldComponents.Count > 1 ? PhysicalFileIdentity.Get(cursor.HeldComponents[^2].Handle).Split(':')[^1] : "";
                _directories.Add(new(relative, PhysicalFileIdentity.Get(cursor.Handle).Split(':')[^1], parentId, false));
                if (cursor.Metadata.NamespaceIncomplete) Incomplete();
                long bufferedBytes = 0;
                foreach (var entry in cursor.Entries(budget.CallerCancellation, root.CanonicalPath, rootIdentity, Incomplete))
                {
                    var cost = 64L + entry.Name.Length * 2L;
                    if (entries.Count >= 65536 || bufferedBytes + cost > 16L * 1024 * 1024)
                    {
                        errors.Add(new("limit", relative, "MetadataDirectoryBufferLimit") { NamespaceIncomplete = true });
                        break;
                    }
                    entries.Add(entry); bufferedBytes += cost;
                }
            }
            // Native names are hints. Neither this frame nor a recursive parent owns
            // a cursor/ancestor handle while waiting or processing those names.
            foreach (var entry in entries)
            {
                await pace(budget.CallerCancellation).ConfigureAwait(false);
                ValidateDirectory();
                LastEnumeratedEntries++;
                if (!budget.VisitEntry()) return;
                var path = Path.Combine(directory, entry.Name);
                var entryRelative = Path.GetRelativePath(root.CanonicalPath, path);
                if ((entry.Attributes & FileAttributes.Directory) == 0)
                {
                    if (MatchesPolicy(entryRelative, root.IncludePatterns, root.ExcludePatterns)) files.Add(path);
                }
                else if (root.Recursive && !root.ExcludePatterns.Any(pattern =>
                    pattern.Replace('\\', '/').EndsWith("/**", StringComparison.Ordinal) &&
                    GlobMatches(entryRelative.Replace('\\', '/'), pattern.Replace('\\', '/')[..^3])))
                    await CollectMetadataFilesAsync(root, path, rootIdentity, files, errors, budget, accessRepair, pace).ConfigureAwait(false);
            }
            _beforeDirectoryRecheck?.Invoke(directory);
            ValidateDirectory();

            void ValidateDirectory()
            {
                using var current = WindowsSourcePathAccess.OpenDirectoryPath(directory, enumerate: true);
                current.ValidateCurrent();
                if (current.Identity.IdentityFingerprint != directoryIdentity ||
                    current.IdentityAt(root.CanonicalPath).IdentityFingerprint != rootIdentity)
                    throw new SourceSnapshotChangedException("SourceMetadataDirectoryChanged");
                if (current.Metadata.NamespaceIncomplete) Incomplete();
            }
            void Incomplete()
            {
                if (!errors.Any(item => item.RelativePath == relative && item.Detail == "CloudDirectoryNamespaceIncomplete"))
                    errors.Add(new("partial", relative, "CloudDirectoryNamespaceIncomplete") { NamespaceIncomplete = true });
            }
        }
        catch (UnauthorizedAccessException error) { errors.Add(FailureEvidence(error, relative) with { NamespaceIncomplete = true }); }
        catch (IOException error) { errors.Add(FailureEvidence(error, relative) with { NamespaceIncomplete = true }); }
    }
}
