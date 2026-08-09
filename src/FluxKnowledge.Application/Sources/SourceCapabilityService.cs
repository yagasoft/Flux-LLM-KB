using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

/// <summary>Explicit local processor registration. This is a registry, never an executor activation seam.</summary>
public sealed class SourceCapabilityService(
    ISourceCapabilityStore store,
    ILocalSourceCapabilityHandlerRegistry handlers)
{
    public async ValueTask<RegisteredSourceCapability> RegisterAsync(
        SourceCapabilityDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Validate(descriptor);
        var registration = new RegisteredSourceCapability(
            descriptor.Id,
            descriptor.ProcessorKind,
            descriptor.ProcessorVersion,
            descriptor.ExecutionClass,
            descriptor.ProcessorFingerprint,
            descriptor.ExecutionClass == ExecutionClass.InProcess && handlers.Matches(descriptor),
            descriptor.AcceptedActivityKind,
            descriptor.AcceptedClassification,
            descriptor.OutputContract);
        var persisted = await store.RegisterAsync(registration, cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    public bool TryResolveLocalHandler(Guid capabilityId, out SourceCapabilityDescriptor descriptor) =>
        handlers.TryResolve(capabilityId, out descriptor!);

    private static void Validate(SourceCapabilityDescriptor descriptor)
    {
        if (descriptor.Id == Guid.Empty || string.IsNullOrWhiteSpace(descriptor.ProcessorKind) ||
            string.IsNullOrWhiteSpace(descriptor.ProcessorVersion) || string.IsNullOrWhiteSpace(descriptor.ProcessorFingerprint))
        {
            throw new ArgumentException("A source capability requires an id, processor kind, version and fingerprint.", nameof(descriptor));
        }
    }
}

public interface ILocalSourceCapabilityHandler
{
    SourceCapabilityDescriptor Descriptor { get; }
}

public interface ILocalSourceCapabilityHandlerRegistry
{
    bool TryResolve(Guid capabilityId, out SourceCapabilityDescriptor descriptor);
    bool Matches(SourceCapabilityDescriptor descriptor);
}

public sealed class LocalSourceCapabilityHandlerRegistry(IEnumerable<ILocalSourceCapabilityHandler> handlers)
    : ILocalSourceCapabilityHandlerRegistry
{
    private readonly IReadOnlyDictionary<Guid, SourceCapabilityDescriptor> _descriptors = handlers
        .Select(handler => handler.Descriptor)
        .ToDictionary(descriptor => descriptor.Id);

    public bool TryResolve(Guid capabilityId, out SourceCapabilityDescriptor descriptor) =>
        _descriptors.TryGetValue(capabilityId, out descriptor!);

    public bool Matches(SourceCapabilityDescriptor descriptor) =>
        TryResolve(descriptor.Id, out var handler) && SameDescriptor(handler, descriptor);

    public static bool SameDescriptor(SourceCapabilityDescriptor left, SourceCapabilityDescriptor right) =>
        left.Id == right.Id && left.ExecutionClass == right.ExecutionClass &&
        left.AcceptedActivityKind == right.AcceptedActivityKind &&
        string.Equals(left.ProcessorKind, right.ProcessorKind, StringComparison.Ordinal) &&
        string.Equals(left.ProcessorVersion, right.ProcessorVersion, StringComparison.Ordinal) &&
        string.Equals(left.ProcessorFingerprint, right.ProcessorFingerprint, StringComparison.Ordinal) &&
        string.Equals(left.AcceptedClassification, right.AcceptedClassification, StringComparison.Ordinal) &&
        string.Equals(left.OutputContract, right.OutputContract, StringComparison.Ordinal);
}

/// <summary>The only Phase 3A local replay handler: retained UTF-8 bytes through ExtractUtf8.</summary>
public sealed class RetainedUtf8TextLocalHandler : ILocalSourceCapabilityHandler
{
    public static readonly SourceCapabilityDescriptor Capability = new(
        new Guid("9c56d5b2-c931-4c8b-ab66-fd0601e9c1df"),
        "text-metadata",
        "phase-3a-v1",
        ExecutionClass.InProcess,
        "phase-3a-inprocess-text-metadata-v1");

    public SourceCapabilityDescriptor Descriptor => Capability;
}

public sealed record SourceCapabilityDescriptor(
    Guid Id,
    string ProcessorKind,
    string ProcessorVersion,
    ExecutionClass ExecutionClass,
    string ProcessorFingerprint,
    SourceActivityKind AcceptedActivityKind = SourceActivityKind.TextExtraction,
    string AcceptedClassification = "AcceptedUtf8Text",
    string OutputContract = "pipeline:extract-utf8");

public sealed record RegisteredSourceCapability(
    Guid Id,
    string ProcessorKind,
    string ProcessorVersion,
    ExecutionClass ExecutionClass,
    string ProcessorFingerprint,
    bool IsRunnable,
    SourceActivityKind AcceptedActivityKind = SourceActivityKind.TextExtraction,
    string AcceptedClassification = "AcceptedUtf8Text",
    string OutputContract = "pipeline:extract-utf8");

public interface ISourceCapabilityStore
{
    ValueTask<RegisteredSourceCapability> RegisterAsync(
        RegisteredSourceCapability capability,
        CancellationToken cancellationToken);

    ValueTask<RegisteredSourceCapability?> FindAsync(Guid capabilityId, CancellationToken cancellationToken);
}
