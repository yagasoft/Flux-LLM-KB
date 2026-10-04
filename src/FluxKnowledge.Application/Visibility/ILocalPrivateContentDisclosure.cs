namespace FluxKnowledge.Application.Visibility;

/// <summary>Applies the retained-content secret boundary before trusted-local disclosure.</summary>
public interface ILocalPrivateContentDisclosure
{
    LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind);

    LocalDisclosureResult EvaluateDecodedText(string value) => Evaluate(value, LocalDisclosureKind.CodeExcerpt);

    LocalDisclosureResult EvaluateCode(string value, LocalDisclosureKind kind,
        CodeDisclosureWindow? proof, int headerLength = 0) => Evaluate(value, kind);
}
