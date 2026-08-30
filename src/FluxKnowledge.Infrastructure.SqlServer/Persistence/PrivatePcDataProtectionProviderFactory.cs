using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using FluxKnowledge.Application.Operations;
using Microsoft.AspNetCore.DataProtection;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Creates the deliberately unencrypted key ring shared by trusted native processes.</summary>
public static class PrivatePcDataProtectionProviderFactory
{
    public const string LocalApplicationDataRootConfigurationKey = "PrivatePc:LocalApplicationDataRoot";
    public const string LocalApplicationDataRootEnvironmentVariable = "FLUXKNOWLEDGE_LOCAL_APP_DATA_ROOT";
    public const string ApplicationName = "FluxKnowledge.Native";
    public static string ProductionKeyRingRoot =>
        Path.Combine(LiveRootLayout.Production.ConfigRoot, "data-protection");

    public static LocalRetainedCsharpCodeSearchCursorCodec CreateCursorCodec()
        => CreateCursorCodec(
            LiveRootLayout.Production,
            new LiveRootStorageSafety(LiveRootLayout.Production, FileSystemLiveRootPathInspector.Instance),
            FileSystemPrivatePcDataProtectionStore.Instance,
            createIfMissing: false);

    public static LocalRetainedCsharpCodeSearchCursorCodec CreateCursorCodec(LiveRootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.IsProduction) return CreateCursorCodec();
        try
        {
            return new LocalRetainedCsharpCodeSearchCursorCodec(
                FileSystemPrivatePcDataProtectionStore.Instance.CreateProvider(KeyRingRoot(layout), createIfMissing: true));
        }
        catch (Exception exception) when (IsKeyRingUnavailable(exception))
        {
            return new LocalRetainedCsharpCodeSearchCursorCodec(new UnavailableDataProtectionProvider());
        }
    }

    public static NativeV1ProjectionCursorCodec CreateNativeV1CursorCodec()
        => CreateNativeV1CursorCodec(
            LiveRootLayout.Production,
            new LiveRootStorageSafety(LiveRootLayout.Production, FileSystemLiveRootPathInspector.Instance),
            FileSystemPrivatePcDataProtectionStore.Instance,
            createIfMissing: false);

    public static NativeV1ProjectionCursorCodec CreateNativeV1CursorCodec(LiveRootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.IsProduction) return CreateNativeV1CursorCodec();
        try
        {
            return new NativeV1ProjectionCursorCodec(
                FileSystemPrivatePcDataProtectionStore.Instance.CreateProvider(KeyRingRoot(layout), createIfMissing: true));
        }
        catch (Exception exception) when (IsKeyRingUnavailable(exception))
        {
            return new NativeV1ProjectionCursorCodec(new UnavailableDataProtectionProvider());
        }
    }

    internal static LocalRetainedCsharpCodeSearchCursorCodec CreateCursorCodecForIsolatedTests(string configRoot)
    {
        try
        {
            return new LocalRetainedCsharpCodeSearchCursorCodec(
                FileSystemPrivatePcDataProtectionStore.Instance.CreateProvider(KeyRingRoot(configRoot), createIfMissing: true));
        }
        catch (Exception exception) when (IsKeyRingUnavailable(exception))
        {
            return new LocalRetainedCsharpCodeSearchCursorCodec(new UnavailableDataProtectionProvider());
        }
    }

    internal static LocalRetainedCsharpCodeSearchCursorCodec CreateCursorCodec(
        LiveRootLayout layout,
        LiveRootStorageSafety storageSafety,
        IPrivatePcDataProtectionStore store)
        => CreateCursorCodec(layout, storageSafety, store, createIfMissing: true);

    private static LocalRetainedCsharpCodeSearchCursorCodec CreateCursorCodec(
        LiveRootLayout layout,
        LiveRootStorageSafety storageSafety,
        IPrivatePcDataProtectionStore store,
        bool createIfMissing)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(storageSafety);
        ArgumentNullException.ThrowIfNull(store);
        var keyRingRoot = KeyRingRoot(layout);
        storageSafety.ValidateBeforeIo(keyRingRoot);
        try
        {
            return new LocalRetainedCsharpCodeSearchCursorCodec(store.CreateProvider(keyRingRoot, createIfMissing));
        }
        catch (Exception exception) when (IsKeyRingUnavailable(exception))
        {
            return new LocalRetainedCsharpCodeSearchCursorCodec(new UnavailableDataProtectionProvider());
        }
    }

    internal static NativeV1ProjectionCursorCodec CreateNativeV1CursorCodec(
        LiveRootLayout layout,
        LiveRootStorageSafety storageSafety,
        IPrivatePcDataProtectionStore store)
        => CreateNativeV1CursorCodec(layout, storageSafety, store, createIfMissing: true);

    private static NativeV1ProjectionCursorCodec CreateNativeV1CursorCodec(
        LiveRootLayout layout,
        LiveRootStorageSafety storageSafety,
        IPrivatePcDataProtectionStore store,
        bool createIfMissing)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(storageSafety);
        ArgumentNullException.ThrowIfNull(store);
        var keyRingRoot = KeyRingRoot(layout);
        storageSafety.ValidateBeforeIo(keyRingRoot);
        try
        {
            return new NativeV1ProjectionCursorCodec(store.CreateProvider(keyRingRoot, createIfMissing));
        }
        catch (Exception exception) when (IsKeyRingUnavailable(exception))
        {
            return new NativeV1ProjectionCursorCodec(new UnavailableDataProtectionProvider());
        }
    }

    private static string KeyRingRoot(LiveRootLayout layout) => KeyRingRoot(layout.ConfigRoot);

    private static string KeyRingRoot(string configRoot)
    {
        if (string.IsNullOrWhiteSpace(configRoot) || !Path.IsPathFullyQualified(configRoot))
        {
            throw new InvalidOperationException("The private-PC configuration root is unavailable.");
        }

        return Path.Combine(Path.GetFullPath(configRoot), "data-protection");
    }

    private static bool IsKeyRingUnavailable(Exception exception) =>
        exception is ArgumentException or CryptographicException or IOException or
            InvalidOperationException or PlatformNotSupportedException or SecurityException or
            UnauthorizedAccessException;

    internal static IDataProtectionProvider CreateProviderForIsolatedTests(
        string keyRingRoot,
        bool createIfMissing) =>
        FileSystemPrivatePcDataProtectionStore.Instance.CreateProvider(keyRingRoot, createIfMissing);

    private sealed class UnavailableDataProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) =>
            throw new InvalidOperationException("The private-PC data-protection key ring is unavailable.");
    }
}

internal interface IPrivatePcDataProtectionStore
{
    IDataProtectionProvider CreateProvider(string keyRingRoot, bool createIfMissing);
}

internal sealed class FileSystemPrivatePcDataProtectionStore : IPrivatePcDataProtectionStore
{
    public static FileSystemPrivatePcDataProtectionStore Instance { get; } = new();

    private FileSystemPrivatePcDataProtectionStore()
    {
    }

    public IDataProtectionProvider CreateProvider(string keyRingRoot, bool createIfMissing)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The private-PC current-user key ring is available only on Windows.");
        }

        if (createIfMissing)
        {
            Directory.CreateDirectory(keyRingRoot);
        }
        else if (!Directory.Exists(keyRingRoot) || !Directory.EnumerateFiles(keyRingRoot, "key-*.xml").Any())
        {
            throw new CryptographicException("The existing native data-protection key ring is unavailable.");
        }

        return CreateWindowsProvider(keyRingRoot, createIfMissing);
    }

    [SupportedOSPlatform("windows")]
    private static IDataProtectionProvider CreateWindowsProvider(string keyRingRoot, bool createIfMissing) =>
        DataProtectionProvider.Create(
            new DirectoryInfo(keyRingRoot),
            builder =>
            {
                builder.SetApplicationName(PrivatePcDataProtectionProviderFactory.ApplicationName);
                if (!createIfMissing) builder.DisableAutomaticKeyGeneration();
            });
}
