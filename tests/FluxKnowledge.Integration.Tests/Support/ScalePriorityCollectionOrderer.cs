using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer("FluxKnowledge.Integration.Tests.Support.ScalePriorityCollectionOrderer", "FluxKnowledge.Integration.Tests")]

namespace FluxKnowledge.Integration.Tests.Support;

[CollectionDefinition(Name)]
public sealed class ScaleTestCollection
{
    public const string Name = "large-scale-sql";
}

public sealed class ScalePriorityCollectionOrderer : ITestCollectionOrderer
{
    // Give the measured long-running collection queue priority while retaining
    // xUnit's default relative order and isolation metadata for every other one.
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        new DefaultTestCollectionOrderer().OrderTestCollections(testCollections)
            .OrderByDescending(collection => collection.DisplayName == ScaleTestCollection.Name);
}
