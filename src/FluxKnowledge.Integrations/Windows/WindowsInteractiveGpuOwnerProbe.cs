using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Gpu;
using Microsoft.Win32;

namespace FluxKnowledge.Integrations.Windows;

/// <summary>Observes local OS process incarnation. Inaccessibility and another machine fail closed.</summary>
public sealed class WindowsInteractiveGpuOwnerProbe : IGpuInteractiveOwnerProbe
{
    public WindowsInteractiveGpuOwnerProbe()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("interactive-gpu-owner-requires-windows");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
        if (!Guid.TryParse(key?.GetValue("MachineGuid") as string, out var machineGuid) || machineGuid == Guid.Empty)
            throw new InvalidOperationException("interactive-gpu-machine-identity-unavailable");
        using var process = Process.GetCurrentProcess();
        Current = new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(machineGuid.ToString("N")))));
        Current.Validate();
    }

    public GpuInteractiveOwnerIdentity Current { get; }

    public GpuInteractiveOwnerObservation Observe(GpuInteractiveOwnerIdentity owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Validate();
        if (owner.MachineFingerprint != Current.MachineFingerprint) return GpuInteractiveOwnerObservation.Unknown;
        Process process;
        try { process = Process.GetProcessById(owner.ProcessId); }
        catch (ArgumentException) { return GpuInteractiveOwnerObservation.Exited; }
        catch (InvalidOperationException) { return GpuInteractiveOwnerObservation.Unknown; }
        using (process)
        {
            try
            {
                if (process.HasExited) return GpuInteractiveOwnerObservation.Exited;
                return new DateTimeOffset(process.StartTime.ToUniversalTime()) == owner.StartedAtUtc
                    ? GpuInteractiveOwnerObservation.Alive : GpuInteractiveOwnerObservation.Exited;
            }
            catch (Win32Exception) { return GpuInteractiveOwnerObservation.Unknown; }
            catch (InvalidOperationException) { return GpuInteractiveOwnerObservation.Unknown; }
            catch (NotSupportedException) { return GpuInteractiveOwnerObservation.Unknown; }
        }
    }
}
