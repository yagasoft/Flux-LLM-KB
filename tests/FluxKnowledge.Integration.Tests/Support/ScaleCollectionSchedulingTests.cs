using FluxKnowledge.Integration.Tests.Indexing;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class ScaleCollectionSchedulingTests
{
    private const string ScaleCollectionName = "large-scale-sql";

    [Fact]
    public void Scale_priority_preserves_every_collection_and_the_default_relative_order_of_others()
    {
        var factory = CreateFactory();
        var scale = factory.Get(Reflector.Wrap(typeof(SegmentedSelectionScaleTests)));
        ITestCollection[] collections =
        [
            factory.Get(Reflector.Wrap(typeof(SqlFullTextCollectionContractTests))),
            scale,
            factory.Get(Reflector.Wrap(typeof(SqlToUsearchRebuildTests))),
            factory.Get(Reflector.Wrap(typeof(NativeResourceIsolationCollectionContractTests)))
        ];
        var defaultOrder = new DefaultTestCollectionOrderer().OrderTestCollections(collections).ToArray();
        var ordered = RegisteredOrderer().OrderTestCollections(collections).ToArray();

        Assert.Same(scale, ordered[0]);
        Assert.Equal(collections.Length, ordered.Length);
        Assert.Equal(defaultOrder.Where(collection => !ReferenceEquals(collection, scale)), ordered.Skip(1));
    }

    [Fact]
    public void A_focused_run_without_scale_keeps_the_default_order_and_nonparallel_collection_metadata()
    {
        var factory = CreateFactory();
        ITestCollection[] collections =
        [
            factory.Get(Reflector.Wrap(typeof(NativeResourceIsolationCollectionContractTests))),
            factory.Get(Reflector.Wrap(typeof(SqlToUsearchRebuildTests))),
            factory.Get(Reflector.Wrap(typeof(SqlFullTextCollectionContractTests)))
        ];
        Assert.Equal(new DefaultTestCollectionOrderer().OrderTestCollections(collections),
            RegisteredOrderer().OrderTestCollections(collections));
        var fullText = Assert.Single(collections, collection => collection.DisplayName == "sql-full-text");
        Assert.True(Assert.Single(fullText.CollectionDefinition.GetCustomAttributes(typeof(CollectionDefinitionAttribute)))
            .GetNamedArgument<bool>(nameof(CollectionDefinitionAttribute.DisableParallelization)));
    }

    [Fact]
    public void The_priority_collection_is_parallel_and_contains_only_the_isolated_scale_class()
    {
        var scale = CreateFactory().Get(Reflector.Wrap(typeof(SegmentedSelectionScaleTests)));
        Assert.Equal(ScaleCollectionName, scale.DisplayName);
        Assert.NotNull(scale.CollectionDefinition);
        Assert.False(Assert.Single(scale.CollectionDefinition.GetCustomAttributes(typeof(CollectionDefinitionAttribute)))
            .GetNamedArgument<bool>(nameof(CollectionDefinitionAttribute.DisableParallelization)));
        Assert.Equal([typeof(SegmentedSelectionScaleTests)], typeof(SegmentedSelectionScaleTests).Assembly.GetTypes()
            .Where(type => type.CustomAttributes.Any(attribute => attribute.AttributeType == typeof(CollectionAttribute) &&
                attribute.ConstructorArguments.Single().Value is string name && name == ScaleCollectionName)));
    }

    private static CollectionPerClassTestCollectionFactory CreateFactory() =>
        new(new TestAssembly(Reflector.Wrap(typeof(ScaleCollectionSchedulingTests).Assembly)), new NullMessageSink());

    private static ITestCollectionOrderer RegisteredOrderer()
    {
        var assembly = typeof(ScaleCollectionSchedulingTests).Assembly;
        var registration = Assert.Single(assembly.CustomAttributes,
            attribute => attribute.AttributeType == typeof(TestCollectionOrdererAttribute));
        Assert.Equal(assembly.GetName().Name, registration.ConstructorArguments[1].Value);
        var type = assembly.GetType(Assert.IsType<string>(registration.ConstructorArguments[0].Value), throwOnError: true)!;
        return Assert.IsAssignableFrom<ITestCollectionOrderer>(Activator.CreateInstance(type));
    }
}
