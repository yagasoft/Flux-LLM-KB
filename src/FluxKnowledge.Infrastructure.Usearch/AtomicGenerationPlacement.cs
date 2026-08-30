using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.Infrastructure.Usearch;

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
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
        {
            throw new InvalidOperationException("An immutable index generation already occupies the final path.");
        }

        directoryCreator.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
        Directory.Move(stagingDirectory, finalDirectory);
        return finalDirectory;
    }

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
