using System.Text.Json;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class SqlFixtureTimingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unwritable_diagnostics_do_not_change_the_action_outcome(bool fail)
    {
        var path = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeTiming-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "A file cannot be a timing directory.");
        try
        {
            var expected = new InvalidOperationException("action failure");
            var action = new SqlFixtureTimings(path).MeasureAsync("create", "synthetic",
                () => fail ? Task.FromException(expected) : Task.CompletedTask);
            if (fail) Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => action));
            else await action;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Disabled_diagnostics_execute_the_action_and_preserve_its_failure()
    {
        var expected = new InvalidOperationException("synthetic action failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqlFixtureTimings(null).MeasureAsync("create", "synthetic", () => Task.FromException(expected)));
        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostics_record_phase_duration_and_outcome_without_exception_contents(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeTiming-{Guid.NewGuid():N}");
        try
        {
            var timing = new SqlFixtureTimings(root);
            var expected = new InvalidOperationException("private exception detail");
            var measuring = timing.MeasureAsync("migrate", "synthetic-catalog",
                () => fail ? Task.FromException(expected) : Task.CompletedTask);
            if (fail) Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => measuring));
            else await measuring;

            var text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(root)));
            using var json = JsonDocument.Parse(text);
            Assert.Equal("migrate", json.RootElement.GetProperty("phase").GetString());
            Assert.Equal("synthetic-catalog", json.RootElement.GetProperty("catalog").GetString());
            Assert.True(json.RootElement.GetProperty("seconds").GetDouble() >= 0);
            Assert.Equal(!fail, json.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.DoesNotContain(expected.Message, text);
            Assert.Equal(4, json.RootElement.EnumerateObject().Count());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
