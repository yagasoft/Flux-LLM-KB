using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Web.Configuration;

namespace FluxKnowledge.Web;

/// <summary>Fail-closed opt-in witness written by the bounded external staging trial controller.</summary>
internal sealed class StagingGpuTrialGate
{
    private readonly StagingHostSettings _settings;
    private readonly int _maximumCohorts;
    private readonly string? _ledgerResetReceipt;
    private int _cohortsClaimed;
    internal StagingGpuTrialGate(StagingHostSettings settings,int maximumCohorts=1,string? ledgerResetReceipt=null)
    {
        if(maximumCohorts is not (1 or 21)) throw new ArgumentOutOfRangeException(nameof(maximumCohorts));
        _settings=settings;_maximumCohorts=maximumCohorts;_ledgerResetReceipt=ledgerResetReceipt;
    }
    // A host-lifetime claim is never refunded by cancellation, failure or permit replacement.
    internal void ClaimCohort()
    {
        while(true)
        {
            var claimed=Volatile.Read(ref _cohortsClaimed);
            if(claimed>=_maximumCohorts) throw new PassageRetrievalRefusalException("busy");
            if(Interlocked.CompareExchange(ref _cohortsClaimed,claimed+1,claimed)==claimed) return;
        }
    }
    internal string PermitPath => Path.Combine(_settings.Layout.RuntimeRoot,"expanded-gpu-trial-permit.json");

    internal void Require()
    {
        try
        {
            for (var path = Path.GetFullPath(PermitPath); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
                if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new PassageRetrievalRefusalException("unavailable");
            if (!File.Exists(PermitPath) || new FileInfo(PermitPath).Length > 16384) throw new PassageRetrievalRefusalException("unavailable");
            var permit = JsonSerializer.Deserialize<StagingGpuTrialPermit>(File.ReadAllText(PermitPath))
                ?? throw new PassageRetrievalRefusalException("unavailable");
            using var host = Process.GetCurrentProcess();
            Validate(permit,DateTimeOffset.UtcNow,host.Id,host.StartTime.ToUniversalTime(),
                Hash(Assembly.GetEntryAssembly()!.Location),Hash(Path.Combine(_settings.Layout.ConfigRoot,"staging.json")),
                Hash(_ledgerResetReceipt ?? StagingGpuSchedulerBinding.FilePath(_settings)),
                StagingGpuAdapterIdentity.ReadZero(),
                (id,birth) =>
                {
                    using var controller = Process.GetProcessById(id);
                    return !controller.HasExited && controller.StartTime.ToUniversalTime() == birth.UtcDateTime;
                },_maximumCohorts);
        }
        catch (PassageRetrievalRefusalException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { throw new PassageRetrievalRefusalException("unavailable"); }
    }

    internal static void Validate(StagingGpuTrialPermit permit, DateTimeOffset now, int hostId, DateTimeOffset hostBirth,
        string appHash, string settingsHash, string schedulerHash, StagingGpuAdapterIdentity adapter,
        Func<int,DateTimeOffset,bool> controllerAlive,int maximumCohorts=1)
    {
        if (maximumCohorts is not (1 or 21) || permit.MaximumCohorts!=maximumCohorts ||
            permit.Version != 1 || permit.OperationId == Guid.Empty || permit.IssuedUtc > now || permit.DeadlineUtc <= now ||
            permit.DeadlineUtc - permit.IssuedUtc > TimeSpan.FromMinutes(10) || now - permit.IssuedUtc > TimeSpan.FromMinutes(10) ||
            permit.HostProcessId != hostId || permit.HostStartedUtc != hostBirth || permit.ControllerProcessId <= 0 ||
            permit.HostAssemblySha256 != appHash || permit.SettingsSha256 != settingsHash || permit.SchedulerBindingSha256 != schedulerHash ||
            permit.AdapterIndex!=0 || permit.Vendor!=4318 || permit.Device!=10115 || adapter.Index!=permit.AdapterIndex ||
            adapter.Vendor!=permit.Vendor || adapter.Device!=permit.Device || adapter.Luid!=permit.AdapterLuid ||
            !permit.AdmissionFenceHeld || !permit.PoolStopped || !permit.ProductionOwnerAbsenceVerified ||
            !controllerAlive(permit.ControllerProcessId,permit.ControllerStartedUtc))
            throw new PassageRetrievalRefusalException("unavailable");
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}

internal sealed record StagingGpuTrialPermit(int Version, Guid OperationId, DateTimeOffset IssuedUtc, DateTimeOffset DeadlineUtc,
    int ControllerProcessId, DateTimeOffset ControllerStartedUtc, int HostProcessId, DateTimeOffset HostStartedUtc,
    string HostAssemblySha256, string SettingsSha256, string SchedulerBindingSha256, bool AdmissionFenceHeld, bool PoolStopped, bool ProductionOwnerAbsenceVerified,
    uint AdapterIndex,uint Vendor,uint Device,string AdapterLuid,int MaximumCohorts=1);
