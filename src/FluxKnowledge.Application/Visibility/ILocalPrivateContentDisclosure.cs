namespace FluxKnowledge.Application.Visibility;

/// <summary>Applies the retained-content secret boundary before trusted-local disclosure.</summary>
public interface ILocalPrivateContentDisclosure
{
    LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind);
}
