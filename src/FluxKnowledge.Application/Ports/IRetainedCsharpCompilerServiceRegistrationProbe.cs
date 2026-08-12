namespace FluxKnowledge.Application.Ports;

/// <summary>
/// Reports whether the completed local dependency-injection catalogue contains
/// a Roslyn workspace, analyser or source-generator service. The syntax-only
/// retained C# processor fails closed when any such service is registered.
/// </summary>
public interface IRetainedCsharpCompilerServiceRegistrationProbe
{
    bool HasForbiddenCompilerServicesRegistered();
}
