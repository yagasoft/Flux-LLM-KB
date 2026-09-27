using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluxKnowledge.Application.Gpu;

public enum GpuWorkloadKind { Ocr, Retrieval }
public sealed record GpuWorkloadProfile(string ModelRuntimeKey, string SettingsFingerprint, GpuWorkloadKind Kind);

/// <summary>Exact trusted runtime/settings allowlist for the OCR turn rule; lanes do not identify workloads.</summary>
public sealed class GpuWorkloadPolicy
{
    private readonly GpuWorkloadProfile[] _profiles;
    public GpuWorkloadPolicy(IEnumerable<GpuWorkloadProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles.OrderBy(p => p.ModelRuntimeKey, StringComparer.Ordinal)
            .ThenBy(p => p.SettingsFingerprint, StringComparer.Ordinal).ToArray();
        if (_profiles.Length is < 1 or > 16 || _profiles.GroupBy(p => (p.ModelRuntimeKey, p.SettingsFingerprint)).Any(g => g.Count() != 1))
            throw new ArgumentException("gpu-workload-allowlist-invalid", nameof(profiles));
        foreach (var profile in _profiles)
        {
            GpuSchedulerOpaqueKeyValidator.RequireCanonical(profile.ModelRuntimeKey, nameof(profile.ModelRuntimeKey), 256);
            GpuSchedulerOpaqueKeyValidator.RequireCanonical(profile.SettingsFingerprint, nameof(profile.SettingsFingerprint), 256);
            if (!Enum.IsDefined(profile.Kind)) throw new ArgumentOutOfRangeException(nameof(profiles));
        }
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_profiles))));
    }
    public string Fingerprint { get; }
    public IReadOnlyList<GpuWorkloadProfile> Profiles => Array.AsReadOnly(_profiles);
    public GpuWorkloadKind? Classify(string runtime, string settings) => _profiles.FirstOrDefault(p =>
        string.Equals(p.ModelRuntimeKey, runtime, StringComparison.Ordinal) &&
        string.Equals(p.SettingsFingerprint, settings, StringComparison.Ordinal))?.Kind;
}
