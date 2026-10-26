using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

/// <summary>Explicit operator qualification, scoped to one physical root and tested filter environment.</summary>
public sealed class WindowsSourceJournalSealQualification
{
    private readonly SourceJournalOptions _options;
    private readonly Func<string?> _environment;
    private readonly Func<string, ProviderIdentity?> _provider;

    public WindowsSourceJournalSealQualification(SourceJournalOptions options)
        : this(options, GetEnvironmentFingerprint, ReadProviderIdentity) { }

    internal WindowsSourceJournalSealQualification(SourceJournalOptions options, Func<string?> environment,
        Func<string, ProviderIdentity?> provider)
    { _options = options; _environment = environment; _provider = provider; }

    internal sealed record ProviderIdentity(string Name, string Version);
    private static readonly object EnvironmentLock = new();
    private static string? _environmentStamp;
    private static string? _environmentFingerprint;

    public string? GetProfile(string? rootIdentityFingerprint, string? rootPath = null)
    {
        if (string.IsNullOrWhiteSpace(rootIdentityFingerprint)) return null;
        var qualified = _options.SealQualifications.Where(x => x.RootIdentityFingerprint == rootIdentityFingerprint).ToArray();
        if (qualified.Length != 1 || string.IsNullOrWhiteSpace(qualified[0].Scope) || string.IsNullOrWhiteSpace(qualified[0].QualificationId)) return null;
        var environment = _environment();
        var item = qualified[0];
        if (environment is null || item.EnvironmentFingerprint != environment) return null;
        var binding = string.Join('\n', item.RootIdentityFingerprint, environment, item.Scope, item.QualificationId);
        if (item.ProviderName is null && item.ProviderVersion is null)
        {
            // Existing local receipts retain their identity. A cloud scope must never
            // silently become a local qualification when provider fields are missing.
            return item.Scope.StartsWith("local-ntfs-", StringComparison.Ordinal) || item.Scope == "isolated-local-ntfs-staging"
                ? Hash(binding) : null;
        }
        if (string.IsNullOrWhiteSpace(item.ProviderName) || string.IsNullOrWhiteSpace(item.ProviderVersion) ||
            string.IsNullOrWhiteSpace(rootPath)) return null;
        var provider = _provider(rootPath);
        if (provider is null || provider.Name != item.ProviderName || provider.Version != item.ProviderVersion) return null;
        return Hash(string.Join('\n', binding, provider.Name, provider.Version));
    }

    internal static ProviderIdentity? ReadProviderIdentity(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var buffer = new byte[1028]; // CF_SYNC_ROOT_PROVIDER_INFO: DWORD + two WCHAR[256].
            return GetSyncRootInfo(path, 2, buffer, (uint)buffer.Length, out var returned) >= 0
                ? ParseProviderIdentity(buffer, returned) : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    internal static ProviderIdentity? ParseProviderIdentity(ReadOnlySpan<byte> buffer, uint returned)
    {
        if (returned != 1028 || buffer.Length < returned) return null;
        try
        {
            var name = ReadString(buffer.Slice(4, 512));
            var version = ReadString(buffer.Slice(516, 512));
            // Provider status is health information, not part of the tested binary identity.
            return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) ? null : new(name, version);
        }
        catch (DecoderFallbackException) { return null; }

        static string? ReadString(ReadOnlySpan<byte> value)
        {
            for (var offset = 0; offset < value.Length; offset += 2)
                if (value[offset] == 0 && value[offset + 1] == 0)
                    return new UnicodeEncoding(false, false, true).GetString(value[..offset]);
            return null;
        }
    }

    [DllImport("CldApi.dll", EntryPoint = "CfGetSyncRootInfoByPath", CharSet = CharSet.Unicode)]
    private static extern int GetSyncRootInfo(string path, int informationClass, [Out] byte[] buffer, uint length, out uint returned);

    public static string? GetEnvironmentFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var paths = new[] { Path.Combine(system, "ntoskrnl.exe"), Path.Combine(system, "drivers", "ntfs.sys"), Path.Combine(system, "drivers", "cldflt.sys") };
            // Recheck file metadata before each trust decision. Read version resources only
            // when that metadata changes; this never opens source or retained content.
            var stamp = string.Join('\n', paths.Select(path =>
            {
                var file = new FileInfo(path);
                return $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            }));
            stamp = RuntimeInformation.OSDescription + "|" + RuntimeInformation.OSArchitecture + "|" + RuntimeInformation.ProcessArchitecture + "\n" + stamp;
            lock (EnvironmentLock)
            {
                if (stamp != _environmentStamp)
                {
                    var versions = string.Join('\n', paths.Select(path => FileVersionInfo.GetVersionInfo(path).FileVersion));
                    _environmentFingerprint = Hash(stamp + "\n" + versions);
                    _environmentStamp = stamp;
                }
                return _environmentFingerprint;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
