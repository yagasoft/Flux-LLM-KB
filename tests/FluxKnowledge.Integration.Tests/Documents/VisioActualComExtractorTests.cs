using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Documents;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

/// <summary>Opt-in proof against an installed interactive Visio; it creates no private fixture.</summary>
[Collection("interactive-visio")]
public sealed class VisioActualComExtractorTests
{
    [Fact]
    public void Non_endpoint_connection_cells_are_inert()
    {
        var endpoints = new Dictionary<int, (int? From, int? To)>();

        Assert.False(VisioDocumentExtractor.TryRecordConnectorEndpoint(endpoints, 10, 20, "AlignCenter"));
        Assert.Empty(endpoints);
        Assert.True(VisioDocumentExtractor.TryRecordConnectorEndpoint(endpoints, 10, 20, "BeginX"));
        Assert.True(VisioDocumentExtractor.TryRecordConnectorEndpoint(endpoints, 10, 30, "EndX"));
        Assert.Equal((20, 30), endpoints[10]);
    }

    [Fact]
    public async Task Generated_public_VSDX_extracts_effective_Arabic_shape_text_data_and_connectors_then_exits_its_owned_process()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FLUX_KB_RUN_ACTUAL_VISIO"), "1", StringComparison.Ordinal))
        {
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledge", "visio-public-proof", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = Path.Combine(root, "public-generated.vsdx");
            await CreatePublicFixtureAsync(fixture);
            var bytes = await File.ReadAllBytesAsync(fixture);
            var retained = new RetainedSourceBytes(SourceRevisionId.New(), bytes,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
            var extractor = new VisioDocumentExtractor();
            Assert.Null(extractor.GetUnavailableReason());

            using var lease = VisioExecutionLease.AcquireForTest(extractor, Path.Combine(root, "visio-execution.lock"));
            var first = await lease.ExtractAsync(retained, CancellationToken.None);
            var second = await lease.ExtractAsync(retained, CancellationToken.None);

            Assert.Contains("مرحبا", first.Extraction.Text, StringComparison.Ordinal);
            Assert.Contains("Public data: 42", first.Extraction.Text, StringComparison.Ordinal);
            Assert.Equal(9, first.ShapeCount);
            Assert.Equal(2, first.ConnectionCount); // Two glued ends of one directed connector.
            Assert.Equal(2, first.Extraction.Pages.Count);
            Assert.Contains("مرحبا", first.Extraction.Pages[0].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Master inherited", first.Extraction.Pages[0].Text, StringComparison.Ordinal);
            Assert.Contains("Master inherited", first.Extraction.Pages[1].Text, StringComparison.Ordinal);
            Assert.Contains("Local override", first.Extraction.Pages[1].Text, StringComparison.Ordinal);
            Assert.Contains("12345", first.Extraction.Pages[1].Text, StringComparison.Ordinal);
            var metadata = DocumentOcrProvenance.Parse(first.MetadataJson);
            var firstPage = metadata.Pages[0];
            var connector = Assert.Single(firstPage.Blocks, block => block.ConnectorFromShapeId is not null);
            Assert.Equal(1, connector.ConnectorFromShapeId);
            Assert.Equal(2, connector.ConnectorToShapeId);
            Assert.Equal("13", connector.EndArrow);
            Assert.Equal(2, metadata.Pages[1].Blocks.Count(block => block.MasterId is not null));
            Assert.Equal(2, metadata.Pages[1].Blocks.Count(block => block.ParentShapeId is not null));
            foreach (var expected in new[] { "Master inherited", "Local override", "Group label", "Nested alpha", "Nested beta", "12345" })
                Assert.Equal(1, first.Extraction.Text.Split(expected, StringSplitOptions.None).Length - 1);
            Assert.Equal(first.Extraction.Text, second.Extraction.Text);
            AssertNoLiveVisio();
            using var cancelled = new CancellationTokenSource();
            var cancelledRun = lease.ExtractAsync(retained, cancelled.Token).AsTask();
            var sawProcess = false;
            for (var attempt = 0; attempt < 600 && !cancelledRun.IsCompleted; attempt++)
            {
                var processes = Process.GetProcessesByName("VISIO");
                sawProcess = processes.Any(static process => !process.HasExited);
                foreach (var process in processes) process.Dispose();
                if (sawProcess) break;
                await Task.Delay(50);
            }
            await cancelled.CancelAsync();
            Assert.True(sawProcess, "Cancellation proof must observe actual Visio activation.");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRun.WaitAsync(TimeSpan.FromSeconds(45)));
            AssertNoLiveVisio();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    internal static async Task CreatePublicFixtureAsync(string path)
    {
        Assert.Null(new VisioDocumentExtractor().GetUnavailableReason());
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        OwnedVisioLifetime? lifetime = null;
        var thread = new Thread(() =>
        {
            dynamic? application = null;
            dynamic? document = null;
            try
            {
                var type = Type.GetTypeFromProgID("Visio.InvisibleApp") ?? throw new InvalidOperationException("Visio.InvisibleApp is unavailable.");
                application = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Visio activation failed.");
                var processId = VisioDocumentExtractor.GetWindowsProcessIdFromWindowHandle(Convert.ToInt32(application.WindowHandle32));
                var process = Process.GetProcessById(processId);
                Assert.Equal(Process.GetCurrentProcess().SessionId, process.SessionId);
                lifetime = OwnedVisioLifetime.Attach(process);
                stop.Token.ThrowIfCancellationRequested();
                application.Visible = false;
                application.EventsEnabled = 0;
                document = application.Documents.Add(string.Empty);
                dynamic page = document.Pages.Item(1);
                dynamic source = page.DrawRectangle(1, 1, 2, 2);
                dynamic target = page.DrawRectangle(4, 1, 5, 2);
                dynamic connector = page.DrawLine(2, 1.5, 4, 1.5);
                source.Text = "مرحبا";
                target.Text = "Target";
                source.AddNamedRow(243, "PublicData", 0);
                source.CellsU("Prop.PublicData").FormulaU = "\"42\"";
                source.CellsU("Prop.PublicData.Label").FormulaU = "\"Public data\"";
                source.AddNamedRow(7, "SourcePoint", 0);
                target.AddNamedRow(7, "TargetPoint", 0);
                connector.CellsU("BeginX").GlueTo(source.CellsU("Connections.X1"));
                connector.CellsU("EndX").GlueTo(target.CellsU("Connections.X1"));
                connector.CellsU("EndArrow").FormulaU = "13";
                dynamic secondPage = document.Pages.Add();
                dynamic master = document.Masters.Add();
                dynamic masterShape = master.DrawRectangle(0, 0, 1, 1);
                masterShape.Text = "Master inherited";
                dynamic inherited = secondPage.Drop(master, 1, 5);
                dynamic localOverride = secondPage.Drop(master, 3, 5);
                localOverride.Text = "Local override";
                dynamic firstChild = secondPage.DrawRectangle(1, 1, 2, 2);
                dynamic secondChild = secondPage.DrawRectangle(3, 1, 4, 2);
                firstChild.Text = "Nested alpha";
                secondChild.Text = "Nested beta";
                dynamic selection = secondPage.CreateSelection(0);
                selection.Select(firstChild, 2);
                selection.Select(secondChild, 2);
                dynamic group = selection.Group();
                dynamic groupCharacters = group.Characters;
                groupCharacters.Text = "Group label";
                dynamic fieldShape = secondPage.DrawRectangle(1, 7, 2, 8);
                dynamic fieldCharacters = fieldShape.Characters;
                fieldCharacters.AddCustomFieldU("12345", 0);
                document.SaveAs(path);
                foreach (object value in new object[] { fieldCharacters, fieldShape, groupCharacters, group, selection,
                    secondChild, firstChild, localOverride, inherited, masterShape, master, secondPage }) Release(value);
                Release(connector);
                Release(target);
                Release(source);
                Release(page);
                document.Saved = true;
                document.Close();
                Release(document);
                document = null;
                application.Quit();
                Release(application);
                application = null;
                if (!WaitForVisioExit(TimeSpan.FromSeconds(15)))
                {
                    throw new InvalidOperationException("Generated public Visio fixture did not exit.");
                }
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                if (document is not null)
                {
                    try { document.Saved = true; document.Close(); } catch { }
                    Release(document);
                }
                if (application is not null)
                {
                    try { application.Quit(); } catch { }
                    Release(application);
                }
                lifetime?.CloseAndConfirmExit(TimeSpan.FromSeconds(15));
            }
        }) { IsBackground = true, Name = "FluxKnowledge public Visio fixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(90)); }
        finally
        {
            await stop.CancelAsync();
            lifetime?.CloseAndConfirmExit(TimeSpan.FromSeconds(15));
            lifetime?.Dispose();
        }
    }

    private static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static void AssertNoLiveVisio()
    {
        // Windows can enumerate a signalled, terminated process briefly after its exit.
        var processes = Process.GetProcessesByName("VISIO");
        try { Assert.All(processes, process => Assert.True(process.HasExited)); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool WaitForVisioExit(TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var processes = Process.GetProcessesByName("VISIO");
            try
            {
                if (processes.Length == 0) return true;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            Thread.Sleep(50);
        }
        return false;
    }
}
