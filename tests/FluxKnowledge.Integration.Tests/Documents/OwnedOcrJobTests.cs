using System.Diagnostics;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Integrations.Documents;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class OwnedOcrJobTests
{
    [Fact]
    public async Task Native_execution_deadline_terminates_the_owned_process_and_confirms_cleanup()
    {
        var observer = new OcrTestExecutionContext.Observer();
        var context = OcrTestExecutionContext.Create(TimeSpan.FromSeconds(2), observer);
        var start = GatedPowerShell("Start-Sleep -Seconds 60");
        var timer = Stopwatch.StartNew();
        var outcome = await OwnedOcrProcessSupervisor.RunAsync(start, context, CancellationToken.None);
        Assert.Equal("document-ocr-runtime-timeout", outcome.FailureReason);
        Assert.True(outcome.CleanupConfirmed);
        Assert.Equal(DocumentOcrCleanupKind.OwnedJobEmpty, Assert.IsType<DocumentOcrCleanupProof>(observer.Proof).Kind);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Refused_durable_start_never_opens_the_native_gate()
    {
        var observer = new RefusingObserver();
        var context = OcrTestExecutionContext.Create(observer: observer);
        var sentinel = Path.Combine(Path.GetTempPath(), $"FluxOcrForbiddenStart_{Guid.NewGuid():N}");
        try
        {
            var start = GatedPowerShell($"[IO.File]::WriteAllText('{sentinel.Replace("'", "''")}', 'started')");
            var outcome = await OwnedOcrProcessSupervisor.RunAsync(start, context, CancellationToken.None);
            Assert.Equal("synthetic-start-refused", outcome.FailureReason);
            Assert.Null(outcome.ExitCode);
            Assert.True(outcome.CleanupConfirmed);
            Assert.False(File.Exists(sentinel));
            Assert.Equal(DocumentOcrCleanupKind.VerifiedUnstarted, observer.Proof?.Kind);
        }
        finally { if (File.Exists(sentinel)) File.Delete(sentinel); }
    }

    [Fact]
    public async Task Supervisor_cleans_descendant_and_inherited_pipes_after_root_success_within_its_budget()
    {
        var observer = new OcrTestExecutionContext.Observer();
        var context = OcrTestExecutionContext.Create(observer: observer);
        var start = new ProcessStartInfo("cmd.exe");
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/q"); start.ArgumentList.Add("/c");
        start.ArgumentList.Add("set /p gate= & start /b ping.exe -n 60 127.0.0.1 & exit /b 0");
        var timer = Stopwatch.StartNew();
        var outcome = await OwnedOcrProcessSupervisor.RunAsync(start, context, CancellationToken.None);
        Assert.True(outcome.CleanupConfirmed);
        Assert.Equal(0, outcome.ExitCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(DocumentOcrCleanupKind.OwnedJobEmpty, observer.Proof?.Kind);
    }

    private static ProcessStartInfo GatedPowerShell(string afterGate)
    {
        var start = new ProcessStartInfo("powershell.exe");
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("if ([Console]::ReadLine() -ne 'fluxknowledge-ocr-go-v1') { exit 42 }; " + afterGate);
        return start;
    }

    private sealed class RefusingObserver : IDocumentOcrExecutionObserver
    {
        internal DocumentOcrCleanupProof? Proof { get; private set; }
        public ValueTask<DocumentOcrLifecycleResult> PermitNativeStartAsync(DocumentOcrExecutionLease lease,
            DocumentOcrProcessIdentity process, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DocumentOcrLifecycleResult(DocumentOcrLifecycleDisposition.StaleOwner, "synthetic-start-refused"));
        public ValueTask<DocumentOcrLifecycleResult> ConfirmCleanupAsync(DocumentOcrExecutionLease lease,
            DocumentOcrCleanupProof proof, CancellationToken cancellationToken)
        {
            Proof = proof;
            return ValueTask.FromResult(new DocumentOcrLifecycleResult(proof.Kind == DocumentOcrCleanupKind.VerifiedUnstarted
                ? DocumentOcrLifecycleDisposition.Applied : DocumentOcrLifecycleDisposition.CleanupUnproven, "synthetic-cleanup"));
        }
    }

    [Fact]
    public void Creating_an_existing_execution_job_is_refused_and_missing_recovery_does_not_create_one()
    {
        var execution = Guid.NewGuid();
        Assert.Null(OwnedOcrJob.TryOpen(execution));
        using var owned = OwnedOcrJob.Create(execution);
        Assert.Throws<InvalidOperationException>(() => OwnedOcrJob.Create(execution));
        using var recovery = OwnedOcrJob.TryOpen(execution);
        Assert.NotNull(recovery);
        Assert.Equal(0u, recovery.ActiveProcesses);
    }

    [Fact]
    public async Task Successful_root_exit_does_not_establish_cleanup_while_an_owned_descendant_holds_output_open()
    {
        using var job = OwnedOcrJob.Create(Guid.NewGuid());
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /q")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        });
        Assert.NotNull(process);
        try
        {
            job.Assign(process);
            await process.StandardInput.WriteLineAsync("start /b ping.exe -n 60 127.0.0.1 >nul");
            await process.StandardInput.WriteLineAsync("exit");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(job.ActiveProcesses > 0);
            Assert.True(await job.TerminateAndConfirmAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal(0u, job.ActiveProcesses);
        }
        finally
        {
            await job.TerminateAndConfirmAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }
}
