using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Infrastructure.Inference.Documents;

public interface IDocumentOcrSessionFactory
{
    ValueTask<IDisposable> CreateAsync(
        DocumentOcrModelRole role,
        VerifiedLocalModelLease lease,
        CancellationToken cancellationToken);
}

public sealed record DocumentOcrRuntimeOpenResult(
    bool Succeeded,
    string ReasonCode,
    IReadOnlyList<DocumentOcrModelRoleResolution> Roles,
    DocumentOcrRuntimeLease? Lease);

public sealed class DocumentOcrRuntime
{
    private readonly IDocumentOcrModelResolver _modelResolver;
    private readonly IDocumentOcrSessionFactory _sessionFactory;

    public DocumentOcrRuntime(
        IDocumentOcrModelResolver modelResolver,
        IDocumentOcrSessionFactory sessionFactory)
    {
        _modelResolver = modelResolver ?? throw new ArgumentNullException(nameof(modelResolver));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public async ValueTask<DocumentOcrRuntimeOpenResult> OpenAsync(CancellationToken cancellationToken)
    {
        var modelResolution = await _modelResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!modelResolution.Succeeded || modelResolution.Lease is null)
        {
            return new DocumentOcrRuntimeOpenResult(
                false,
                modelResolution.Succeeded ? ModelStoreReasons.BundleLeaseUnavailable : modelResolution.ReasonCode,
                modelResolution.Roles,
                null);
        }

        var sessions = new Dictionary<DocumentOcrModelRole, IDisposable>();
        var success = false;
        try
        {
            foreach (var role in Enum.GetValues<DocumentOcrModelRole>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await _sessionFactory.CreateAsync(role, modelResolution.Lease.GetLease(role), cancellationToken)
                    .ConfigureAwait(false);
                sessions.Add(role, session ?? throw new InvalidOperationException("OCR session factory returned no session."));
            }

            var lease = new DocumentOcrRuntimeLease(modelResolution.Lease, sessions);
            sessions = null!;
            success = true;
            return new DocumentOcrRuntimeOpenResult(true, ModelStoreReasons.BundleVerified, modelResolution.Roles, lease);
        }
        finally
        {
            if (!success)
            {
                if (sessions is not null)
                {
                    foreach (var session in sessions.Values) session.Dispose();
                }

                modelResolution.Lease.Dispose();
            }
        }
    }
}

public sealed class DocumentOcrRuntimeLease : IDisposable
{
    private readonly DocumentOcrModelLease _modelLease;
    private readonly IReadOnlyDictionary<DocumentOcrModelRole, IDisposable> _sessions;
    private bool _disposed;

    internal DocumentOcrRuntimeLease(
        DocumentOcrModelLease modelLease,
        IReadOnlyDictionary<DocumentOcrModelRole, IDisposable> sessions)
    {
        _modelLease = modelLease ?? throw new ArgumentNullException(nameof(modelLease));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public TSession GetSession<TSession>(DocumentOcrModelRole role)
        where TSession : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sessions.TryGetValue(role, out var session) && session is TSession typed
            ? typed
            : throw new KeyNotFoundException($"No OCR session exists for '{role}'.");
    }

    internal VerifiedLocalModelLease GetModelLease(DocumentOcrModelRole role)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _modelLease.GetLease(role);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var session in _sessions.Values) session.Dispose();
        _modelLease.Dispose();
    }
}
