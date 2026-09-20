using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Infrastructure.Inference.Documents;

public sealed class DocumentOcrModelBundleResolver : IDocumentOcrModelResolver
{
    private readonly ILocalModelStore _store;
    private readonly Func<DocumentOcrModelRole, CancellationToken, ValueTask<ModelBundleSpecification>> _readManifestAsync;

    public DocumentOcrModelBundleResolver(
        ILocalModelStore store,
        Func<DocumentOcrModelRole, CancellationToken, ValueTask<ModelBundleSpecification>> readManifestAsync)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readManifestAsync = readManifestAsync ?? throw new ArgumentNullException(nameof(readManifestAsync));
    }

    public async ValueTask<DocumentOcrModelResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        var roles = new List<DocumentOcrModelRoleResolution>();
        var leases = new Dictionary<DocumentOcrModelRole, VerifiedLocalModelLease>();
        var success = false;
        try
        {
            foreach (var role in Enum.GetValues<DocumentOcrModelRole>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ModelBundleSpecification specification;
                try
                {
                    specification = await _readManifestAsync(role, cancellationToken).ConfigureAwait(false);
                }
                catch (ModelManifestException exception)
                {
                    roles.Add(new DocumentOcrModelRoleResolution(role, exception.ReasonCode, null, [], null));
                    return Refused(exception.ReasonCode, roles);
                }
                catch (IOException)
                {
                    roles.Add(new DocumentOcrModelRoleResolution(role, ModelStoreReasons.StoreUnavailable, null, [], null));
                    return Refused(ModelStoreReasons.StoreUnavailable, roles);
                }

                var resolution = await _store.ResolveAsync(specification, cancellationToken).ConfigureAwait(false);
                roles.Add(new DocumentOcrModelRoleResolution(
                    role,
                    resolution.ReasonCode,
                    resolution.BundleFingerprint,
                    resolution.Files,
                    resolution.ReceiptLocation));
                if (!resolution.Succeeded || resolution.Lease is null)
                {
                    return Refused(
                        resolution.Succeeded ? ModelStoreReasons.BundleLeaseUnavailable : resolution.ReasonCode,
                        roles);
                }

                leases.Add(role, resolution.Lease);
            }

            var lease = new DocumentOcrModelLease(leases);
            leases = null!;
            success = true;
            return new DocumentOcrModelResolution(true, ModelStoreReasons.BundleVerified, roles.AsReadOnly(), lease);
        }
        finally
        {
            if (!success && leases is not null)
            {
                foreach (var lease in leases.Values) lease.Dispose();
            }
        }
    }

    private static DocumentOcrModelResolution Refused(
        string reasonCode,
        IReadOnlyList<DocumentOcrModelRoleResolution> roles) =>
        new(false, reasonCode, roles.ToArray(), null);
}
