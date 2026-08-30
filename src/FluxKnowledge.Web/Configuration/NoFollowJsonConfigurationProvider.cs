using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Files;
using Microsoft.Extensions.Configuration;

namespace FluxKnowledge.Web.Configuration;

public interface INoFollowPathOpener
{
    Stream OpenRead(string canonicalPath);
    string ValidateDirectory(string canonicalPath);
}

/// <summary>Loads the one production configuration file through a no-follow boundary.</summary>
public static class NoFollowJsonConfigurationProvider
{
    public const string CanonicalProductionPath = @"I:\FluxKnowledge\Config\appsettings.Production.json";

    public static IConfigurationRoot LoadCanonicalProduction(
        string canonicalPath,
        INoFollowPathOpener opener)
    {
        ArgumentNullException.ThrowIfNull(opener);
        if (!string.Equals(canonicalPath, CanonicalProductionPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Production configuration must be loaded only from {CanonicalProductionPath}.");
        }

        var canonicalDirectory = Path.GetDirectoryName(CanonicalProductionPath)
            ?? throw new InvalidOperationException("The production configuration has no directory.");
        var validatedDirectory = opener.ValidateDirectory(canonicalDirectory);
        if (!string.Equals(validatedDirectory, canonicalDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The production configuration directory resolved outside its canonical path.");
        }

        using var stream = opener.OpenRead(CanonicalProductionPath);
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    public static IConfigurationRoot LoadCanonicalProduction() =>
        LoadCanonicalProduction(CanonicalProductionPath, FileSystemNoFollowPathOpener.Instance);
}

public sealed class FileSystemNoFollowPathOpener : INoFollowPathOpener
{
    public static FileSystemNoFollowPathOpener Instance { get; } = new();

    private FileSystemNoFollowPathOpener()
    {
    }

    public Stream OpenRead(string canonicalPath)
    {
        if (!string.Equals(canonicalPath, NoFollowJsonConfigurationProvider.CanonicalProductionPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only the canonical production configuration file may be opened.");
        }

        var parent = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException("The production configuration has no parent directory.");
        _ = ValidateDirectory(parent);
        var handle = PhysicalFileIdentity.OpenReadNoFollow(canonicalPath);
        try
        {
            var finalPath = PhysicalFileIdentity.GetFinalPath(handle);
            if (!string.Equals(finalPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                handle.Dispose();
                throw new InvalidOperationException("The production configuration file resolved outside its canonical path.");
            }

            return new FileStream(handle, FileAccess.Read, bufferSize: 81920, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public string ValidateDirectory(string canonicalPath)
    {
        var expected = LiveRootLayout.Production.ConfigRoot;
        if (!string.Equals(canonicalPath, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only the canonical production configuration directory may be validated.");
        }

        PhysicalFileIdentity.EnsureNoReparsePointTraversal(canonicalPath);
        var physical = PhysicalFileIdentity.GetDirectory(canonicalPath).CanonicalPath;
        if (!string.Equals(physical, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The production configuration directory resolved outside its canonical path.");
        }

        return expected;
    }
}
