using System.Diagnostics;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Integrations.Windows;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Gpu;

public sealed class WindowsInteractiveGpuOwnerProbeTests
{
    [WindowsOwnerFact]
    public async Task Local_process_identity_checks_live_exit_pid_incarnation_and_machine_boundary()
    {
        var probe = new WindowsInteractiveGpuOwnerProbe();
        Assert.Equal(Environment.ProcessId, probe.Current.ProcessId);
        Assert.Equal(GpuInteractiveOwnerObservation.Alive, probe.Observe(probe.Current));
        Assert.Equal(GpuInteractiveOwnerObservation.Exited, probe.Observe(probe.Current with { StartedAtUtc = probe.Current.StartedAtUtc.AddSeconds(-1) }));
        Assert.Equal(GpuInteractiveOwnerObservation.Unknown, probe.Observe(probe.Current with { MachineFingerprint = new string('0', 64) }));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            var identity = new GpuInteractiveOwnerIdentity(child.Id, new DateTimeOffset(child.StartTime.ToUniversalTime()), probe.Current.MachineFingerprint);
            Assert.Equal(GpuInteractiveOwnerObservation.Alive, probe.Observe(identity));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(GpuInteractiveOwnerObservation.Exited, probe.Observe(identity));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    private sealed class WindowsOwnerFactAttribute : FactAttribute
    {
        public WindowsOwnerFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Local Windows process observation requires Windows.";
        }
    }
}
