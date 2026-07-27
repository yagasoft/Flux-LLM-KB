namespace FluxKnowledge.Infrastructure.Usearch;

public static class AtomicGenerationPlacement
{
    public static string Place(UsearchIndexOptions options, Guid generationId, string stagingDirectory)
    {
        var finalDirectory = Path.Combine(options.RootPath, "generations", generationId.ToString("N"));
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
        {
            throw new InvalidOperationException("An immutable index generation already occupies the final path.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
        Directory.Move(stagingDirectory, finalDirectory);
        return finalDirectory;
    }

    public static string PlaceRecovery(UsearchIndexOptions options, Guid generationId, string stagingDirectory)
    {
        var fileSystem = new DerivedIndexFileSystem(options);
        if (!fileSystem.TryPlaceRecoveryCandidate(generationId, stagingDirectory, out var finalDirectory))
        {
            throw new InvalidOperationException("The recovery candidate cannot be placed safely.");
        }
        return finalDirectory;
    }
}
