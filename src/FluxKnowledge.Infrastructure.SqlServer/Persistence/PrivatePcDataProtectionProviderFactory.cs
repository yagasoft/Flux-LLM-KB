using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Creates the current-user protected key ring shared by trusted local processes.</summary>
public static class PrivatePcDataProtectionProviderFactory
{
    public const string LocalApplicationDataRootConfigurationKey = "PrivatePc:LocalApplicationDataRoot";
    public const string LocalApplicationDataRootEnvironmentVariable = "FLUXKNOWLEDGE_LOCAL_APP_DATA_ROOT";
    public const string ApplicationName = "FluxKnowledge.PrivatePc";

    public static LocalRetainedCsharpCodeSearchCursorCodec CreateCursorCodec(
        string? configuredLocalApplicationDataRoot = null)
    {
        try
        {
            return new LocalRetainedCsharpCodeSearchCursorCodec(Create(configuredLocalApplicationDataRoot));
        }
        catch (Exception exception) when (IsKeyRingUnavailable(exception))
        {
            return new LocalRetainedCsharpCodeSearchCursorCodec(new UnavailableDataProtectionProvider());
        }
    }

    private static IDataProtectionProvider Create(string? configuredLocalApplicationDataRoot = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The private-PC current-user key ring is available only on Windows.");
        }

        var localApplicationDataRoot = string.IsNullOrWhiteSpace(configuredLocalApplicationDataRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluxKnowledge")
            : Path.GetFullPath(configuredLocalApplicationDataRoot);
        if (string.IsNullOrWhiteSpace(localApplicationDataRoot))
        {
            throw new InvalidOperationException("The private-PC local application data root is unavailable.");
        }

        var keyRingRoot = Path.Combine(localApplicationDataRoot, "data-protection");
        Directory.CreateDirectory(keyRingRoot);
        return CreateWindowsProvider(keyRingRoot);
    }

    [SupportedOSPlatform("windows")]
    private static IDataProtectionProvider CreateWindowsProvider(string keyRingRoot) =>
        DataProtectionProvider.Create(
            new DirectoryInfo(keyRingRoot),
            builder => builder
                .SetApplicationName(ApplicationName)
                .ProtectKeysWithDpapi());

    private static bool IsKeyRingUnavailable(Exception exception) =>
        exception is ArgumentException or CryptographicException or IOException or
            InvalidOperationException or PlatformNotSupportedException or SecurityException or
            UnauthorizedAccessException;

    private sealed class UnavailableDataProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) =>
            throw new InvalidOperationException("The private-PC data-protection key ring is unavailable.");
    }
}
