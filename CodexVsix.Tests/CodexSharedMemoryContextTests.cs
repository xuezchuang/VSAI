using System;
using System.IO;
using System.Text;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexSharedMemoryContextTests
{
    [Fact]
    public void ResumeReloadsTheSharedSummaryAndReplacesOnlyItsOwnBlock()
    {
        using var directory = new TemporaryDirectory();
        WriteSummary(directory.Path, "First shared fact.");
        var request = new JObject { ["developerInstructions"] = "Keep the current project rules." };

        var first = Assert.IsType<JObject>(CodexSharedMemoryContext.EnrichRequest("thread/resume", request, directory.Path));
        WriteSummary(directory.Path, "Second shared fact.");
        var second = Assert.IsType<JObject>(CodexSharedMemoryContext.EnrichRequest("thread/resume", first, directory.Path));
        var instructions = second["developerInstructions"]?.Value<string>();

        Assert.Contains("Keep the current project rules.", instructions);
        Assert.Contains("Second shared fact.", instructions);
        Assert.DoesNotContain("First shared fact.", instructions);
        Assert.Equal(1, CountOccurrences(instructions!, "[[visual-codex-studio-shared-memory-context:v1]]"));
        Assert.Equal("Keep the current project rules.", request["developerInstructions"]?.Value<string>());
    }

    [Theory]
    [InlineData("thread/start")]
    [InlineData("thread/resume")]
    [InlineData("thread/fork")]
    public void ThreadRequestsPreserveInstructionsAndAreIdempotent(string method)
    {
        using var directory = new TemporaryDirectory();
        WriteSummary(directory.Path, "Facts can include 中文 and emoji: 记忆.");
        var original = new JObject
        {
            ["developerInstructions"] = "Preserve existing instructions.",
            ["memoryMode"] = "disabled"
        };

        var first = CodexSharedMemoryContext.EnrichRequest(method, original, directory.Path);
        var second = Assert.IsType<JObject>(CodexSharedMemoryContext.EnrichRequest(method, first, directory.Path));
        var instructions = second["developerInstructions"]?.Value<string>();

        Assert.Contains("Preserve existing instructions.", instructions);
        Assert.Contains("Facts can include 中文 and emoji: 记忆.", instructions);
        Assert.Contains(Path.Combine(directory.Path, "memories", "MEMORY.md"), instructions);
        Assert.Contains(Path.Combine(directory.Path, "memories", "rollout_summaries"), instructions);
        Assert.Contains(Path.Combine(directory.Path, "memories", "skills"), instructions);
        Assert.Contains("Only user-requested memory additions", instructions);
        Assert.Equal("disabled", second["memoryMode"]?.Value<string>());
        Assert.True(JToken.DeepEquals(first, second));
        Assert.Equal("Preserve existing instructions.", original["developerInstructions"]?.Value<string>());
    }

    [Fact]
    public void MissingSummaryLeavesAClonedOriginalRequestUsable()
    {
        using var directory = new TemporaryDirectory();
        var original = new JObject { ["developerInstructions"] = "Keep this active context." };

        var enriched = Assert.IsType<JObject>(CodexSharedMemoryContext.EnrichRequest("thread/start", original, directory.Path));

        Assert.NotSame(original, enriched);
        Assert.True(JToken.DeepEquals(original, enriched));
    }

    [Fact]
    public void MissingSummaryDoesNotErasePreviouslyInjectedActiveContext()
    {
        using var directory = new TemporaryDirectory();
        WriteSummary(directory.Path, "Previously loaded synthetic fact.");
        var first = CodexSharedMemoryContext.EnrichRequest("thread/resume", new JObject(), directory.Path);
        File.Delete(Path.Combine(directory.Path, "memories", "memory_summary.md"));

        var resumed = CodexSharedMemoryContext.EnrichRequest("thread/resume", first, directory.Path);

        Assert.True(JToken.DeepEquals(first, resumed));
    }

    [Fact]
    public void MalformedUtf8ReportsAnActionableFailure()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "memories"));
        File.WriteAllBytes(Path.Combine(directory.Path, "memories", "memory_summary.md"), new byte[] { 0xc3, 0x28 });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodexSharedMemoryContext.EnrichRequest("thread/start", new JObject(), directory.Path));

        Assert.Contains("shared memory summary", exception.Message);
        Assert.Contains("valid UTF-8", exception.Message);
    }

    [Fact]
    public void NonThreadRequestsDoNotReadSharedFiles()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "memories"));
        File.WriteAllBytes(Path.Combine(directory.Path, "memories", "memory_summary.md"), new byte[] { 0xc3, 0x28 });
        var original = new JObject { ["developerInstructions"] = "No file I/O for this request." };

        var enriched = Assert.IsType<JObject>(CodexSharedMemoryContext.EnrichRequest("thread/read", original, directory.Path));

        Assert.NotSame(original, enriched);
        Assert.True(JToken.DeepEquals(original, enriched));
    }

    [Fact]
    public void LargeSummaryIsBoundedAndPointsToTheSharedFiles()
    {
        using var directory = new TemporaryDirectory();
        WriteSummary(directory.Path, new string('x', 32769));

        var enriched = Assert.IsType<JObject>(CodexSharedMemoryContext.EnrichRequest("thread/start", new JObject(), directory.Path));
        var instructions = enriched["developerInstructions"]?.Value<string>();

        Assert.Contains("truncated after 32768 characters", instructions);
        Assert.Contains(Path.Combine(directory.Path, "memories", "memory_summary.md"), instructions);
        Assert.DoesNotContain(new string('x', 32769), instructions);
    }

    [Fact]
    public void UnicodeAndSpacesInTheSharedHomeAreUsedVerbatimInGuidance()
    {
        using var directory = new TemporaryDirectory();
        var sharedHome = Path.Combine(directory.Path, "共享 memory space");
        Directory.CreateDirectory(sharedHome);
        WriteSummary(sharedHome, "A synthetic summary only.");

        var enriched = Assert.IsType<JObject>(CodexSharedMemoryContext.EnrichRequest("thread/fork", new JObject(), sharedHome));
        var instructions = enriched["developerInstructions"]?.Value<string>();

        Assert.Contains(Path.Combine(sharedHome, "memories", "memory_summary.md"), instructions);
        Assert.Contains(Path.Combine(sharedHome, "memories", "MEMORY.md"), instructions);
    }

    private static void WriteSummary(string directory, string contents)
    {
        var memoryRoot = Path.Combine(directory, "memories");
        Directory.CreateDirectory(memoryRoot);
        File.WriteAllText(Path.Combine(memoryRoot, "memory_summary.md"), contents, new UTF8Encoding(false));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
