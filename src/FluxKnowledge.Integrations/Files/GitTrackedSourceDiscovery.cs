using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Read-only index membership; never executes repository-supplied helpers or loads file blobs.</summary>
public sealed class GitTrackedSourceDiscovery(Func<long>? availableMemory = null)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".agents", ".superpowers", "private", "exports", "artifacts", "bin", "obj", "build", "dist", "data", "runtime", "transcripts", "attachments", "FluxKnowledgeData", "FluxKnowledgeIndexes", "node_modules", "__pycache__", ".venv", "venv", "model-cache", "model-staging" };
    private static readonly HashSet<string> EligibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".cs", ".ps1", ".psm1", ".py", ".js", ".sql", ".razor", ".css", ".csproj", ".props", ".slnx" };

    public sealed record Repository(string GitDirectory, string CommonDirectory, string Identity);
    public sealed record Inventory(Repository Repository, string Generation, IReadOnlyList<string> Paths, int TrackedCount, int ExcludedCount);

    public static Repository ResolveRepository(string root)
    {
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(root);
        var dotGit = Path.Combine(root, ".git");
        string gitDirectory;
        if (Directory.Exists(dotGit)) gitDirectory = dotGit;
        else
        {
            EnsureRegularFile(dotGit);
            var text = File.ReadAllText(dotGit, StrictUtf8).Trim();
            if (!text.StartsWith("gitdir: ", StringComparison.Ordinal) || text.Contains('\n')) throw new InvalidDataException("git-directory-invalid");
            gitDirectory = Path.GetFullPath(text[8..], root);
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(gitDirectory);
            EnsureRegularFile(Path.Combine(gitDirectory, "gitdir"));
            var reverse = Path.GetFullPath(File.ReadAllText(Path.Combine(gitDirectory, "gitdir"), StrictUtf8).Trim(), gitDirectory);
            if (!string.Equals(reverse, dotGit, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("git-worktree-identity-invalid");
        }
        var commonPath = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(commonPath)) EnsureRegularFile(commonPath);
        var common = File.Exists(commonPath) ? Path.GetFullPath(File.ReadAllText(commonPath, StrictUtf8).Trim(), gitDirectory) : gitDirectory;
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(common);
        if (!Directory.Exists(Path.Combine(common, "objects"))) throw new InvalidDataException("git-repository-invalid");
        var framed = string.Join("\n", PhysicalFileIdentity.GetDirectory(root).IdentityFingerprint,
            PhysicalFileIdentity.GetDirectory(gitDirectory).IdentityFingerprint, PhysicalFileIdentity.GetDirectory(common).IdentityFingerprint);
        return new Repository(gitDirectory, common, Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(framed))));
    }

    public async ValueTask<Inventory> ReadAsync(string root, CancellationToken cancellationToken)
    {
        var repository = ResolveRepository(root);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe");
        EnsureRegularFile(executable);
        if (!File.Exists(executable)) throw new IOException("git-executable-unavailable");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = StrictUtf8
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_CONFIG_SYSTEM"] = "NUL";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        foreach (var argument in new[] { "--no-pager", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false",
            "-c", "core.hooksPath=NUL", "-c", $"safe.directory={root}", $"--git-dir={repository.GitDirectory}",
            $"--work-tree={root}", "ls-files", "--cached", "--stage", "-z", "--full-name" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("git-start-failed");
        var errors = DrainAsync(process.StandardError, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var paths = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tracked = 0;
        var excluded = 0;
        var buffer = new char[4096];
        var record = new StringBuilder();
        try
        {
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                EnsureMemoryAvailable(read * sizeof(char));
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != '\0') { record.Append(buffer[i]); continue; }
                    var value = record.ToString(); record.Clear();
                    EnsureMemoryAvailable(checked(value.Length * 8L + 128));
                    hash.AppendData(StrictUtf8.GetBytes(value + "\0"));
                    var tab = value.IndexOf('\t');
                    if (tab < 0) throw new InvalidDataException("git-inventory-malformed");
                    var fields = value[..tab].Split(' ');
                    if (fields.Length != 3 || fields[2] != "0" || fields[1].Length is not (40 or 64) || !fields[1].All(char.IsAsciiHexDigit))
                        throw new InvalidDataException("git-index-conflicted-or-malformed");
                    var relative = value[(tab + 1)..];
                    ValidatePath(root, relative);
                    if (!unique.Add(relative)) throw new InvalidDataException("git-path-ambiguous");
                    tracked++;
                    if (fields[0] is not ("100644" or "100755") || !IsEligible(relative)) { excluded++; continue; }
                    paths.Add(relative);
                }
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || record.Length != 0) throw new IOException("git-inventory-incomplete");
            if (ResolveRepository(root).Identity != repository.Identity) throw new IOException("git-repository-changed");
            return new Inventory(repository, Convert.ToHexStringLower(hash.GetHashAndReset()), paths, tracked, excluded);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await errors.ConfigureAwait(false);
        }
    }

    public static bool IsEligible(string relative) => !relative.Split('/').Any(ExcludedDirectories.Contains) &&
        !string.Equals(relative.Split('/')[0], "models", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Path.GetFileName(relative), "appsettings.Local.json", StringComparison.OrdinalIgnoreCase) && EligibleExtensions.Contains(Path.GetExtension(relative));

    private static void EnsureRegularFile(string path)
    {
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new UnauthorizedAccessException("git-file-reparse-or-directory");
    }

    private static void ValidatePath(string root, string relative)
    {
        if (relative.Length == 0 || relative.Any(character => character < 32 || character is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*') ||
            relative.Split('/').Any(component => component is "" or "." or ".." || component.EndsWith(' ') || component.EndsWith('.')) ||
            Path.IsPathRooted(relative)) throw new InvalidDataException("git-path-invalid");
        var full = Path.GetFullPath(relative, root);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("git-path-outside-root");
    }

    private void EnsureMemoryAvailable(long nextAllocation)
    {
        var info = GC.GetGCMemoryInfo();
        var available = availableMemory?.Invoke() ?? Math.Min(info.TotalAvailableMemoryBytes - GC.GetTotalMemory(false),
            info.HighMemoryLoadThresholdBytes - info.MemoryLoadBytes);
        if (available <= nextAllocation) throw new IOException("git-inventory-resource-pressure");
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) { }
    }
}
