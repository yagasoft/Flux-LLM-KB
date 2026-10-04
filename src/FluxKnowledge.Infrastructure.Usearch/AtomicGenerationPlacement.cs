using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.Infrastructure.Usearch;

internal sealed class GenerationPlacementCollisionException(string path, Exception? cause = null)
    : IOException("An immutable index generation already occupies the final path.", cause)
{
    internal string DestinationPath { get; } = path;
}

public static class AtomicGenerationPlacement
{
    public static string Place(UsearchIndexOptions options, Guid generationId, string stagingDirectory)
        => Place(
            options,
            generationId,
            stagingDirectory,
            storageSafety: null,
            FileSystemUsearchDirectoryCreator.Instance);

    internal static string Place(
        UsearchIndexOptions options,
        Guid generationId,
        string stagingDirectory,
        LiveRootStorageSafety? storageSafety,
        IUsearchDirectoryCreator directoryCreator)
    {
        var finalDirectory = Path.Combine(options.RootPath, "generations", generationId.ToString("N"));
        storageSafety?.ValidateBeforeIo(finalDirectory);
        if (Directory.Exists(finalDirectory))
            throw new GenerationPlacementCollisionException(finalDirectory);
        if (File.Exists(finalDirectory))
        {
            throw new InvalidOperationException("An immutable index generation already occupies the final path.");
        }

        directoryCreator.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
        try
        {
            Directory.Move(stagingDirectory, finalDirectory);
        }
        catch (IOException exception) when (IsDestinationCollision(exception) && Directory.Exists(finalDirectory))
        {
            throw new GenerationPlacementCollisionException(finalDirectory, exception);
        }
        return finalDirectory;
    }

    // Windows ERROR_FILE_EXISTS/ALREADY_EXISTS and POSIX EEXIST/ENOTEMPTY.
    // The catch surrounds only Move: a creator or unrelated I/O error is not a collision.
    internal static bool IsDestinationCollision(IOException exception) => OperatingSystem.IsWindows()
        ? (exception.HResult & 0xffff) is 80 or 183
        : (exception.HResult & 0xffff) is 17 or 39;

    public static string PlaceRecovery(UsearchIndexOptions options, Guid generationId, string stagingDirectory)
        => PlaceRecovery(
            options,
            generationId,
            stagingDirectory,
            storageSafety: null,
            FileSystemUsearchDirectoryCreator.Instance);

    internal static string PlaceRecovery(
        UsearchIndexOptions options,
        Guid generationId,
        string stagingDirectory,
        LiveRootStorageSafety? storageSafety,
        IUsearchDirectoryCreator directoryCreator)
    {
        var fileSystem = new DerivedIndexFileSystem(options, null, storageSafety, directoryCreator);
        if (!fileSystem.TryPlaceRecoveryCandidate(generationId, stagingDirectory, out var finalDirectory))
        {
            throw new InvalidOperationException("The recovery candidate cannot be placed safely.");
        }
        return finalDirectory;
    }
}
