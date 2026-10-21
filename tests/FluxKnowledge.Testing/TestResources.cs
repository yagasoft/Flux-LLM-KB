namespace FluxKnowledge.Testing;

/// <summary>Owns disposable dependencies created by test factories until their private roots are cleaned up.</summary>
internal sealed class TestResources : IDisposable
{
    private readonly Stack<IDisposable> _owned = new();

    internal T Own<T>(T resource) where T : IDisposable
    {
        _owned.Push(resource);
        return resource;
    }

    public void Dispose()
    {
        while (_owned.TryPop(out var resource)) resource.Dispose();
    }
}
