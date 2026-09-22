using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexWebViewStateStoreTests
{
    [Fact]
    public void InstancesMergeDistinctUpdatesAndObserveDeletions()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "state.json");
        var first = new CodexWebViewStateStore(file);
        var second = new CodexWebViewStateStore(file);
        Assert.Empty(first.GetSnapshot());
        Assert.Empty(second.GetSnapshot());

        first.Set("pinnedThreadIds", new JArray("thread-1"));
        second.Set("setting:reviewDelivery", "detached");

        Assert.Equal("thread-1", second.Get("pinnedThreadIds")?[0]?.Value<string>());
        Assert.Equal("detached", first.Get("setting:reviewDelivery")?.Value<string>());
        first.Set("pinnedThreadIds", null);
        Assert.Null(second.Get("pinnedThreadIds"));
        Assert.Equal("detached", second.Get("setting:reviewDelivery")?.Value<string>());
    }

    [Fact]
    public async Task ConcurrentInstancesKeepEveryIndependentUpdate()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "state.json");
        var first = new CodexWebViewStateStore(file);
        var second = new CodexWebViewStateStore(file);

        await Task.WhenAll(
            Task.Run(() => { for (var index = 0; index < 20; index++) first.Set("first-" + index, index); }),
            Task.Run(() => { for (var index = 0; index < 20; index++) second.Set("second-" + index, index); }));

        var snapshot = first.GetSnapshot();
        Assert.Equal(40, snapshot.Count);
        Assert.Equal(Enumerable.Range(0, 20), Enumerable.Range(0, 20).Select(index => snapshot["first-" + index]!.Value<int>()));
        Assert.Equal(Enumerable.Range(0, 20), Enumerable.Range(0, 20).Select(index => snapshot["second-" + index]!.Value<int>()));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void FailedReplacementKeepsTheLastSavedStateAndRemovesItsTemporaryFile()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "state.json");
        var store = new CodexWebViewStateStore(file);
        store.Set("setting:reviewDelivery", "inline");
        var original = File.ReadAllText(file);

        using (var lockedFile = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => store.Set("setting:reviewDelivery", "detached"));
        }

        Assert.Equal(original, File.ReadAllText(file));
        Assert.Equal("inline", store.Get("setting:reviewDelivery")?.Value<string>());
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void UnreadableJsonIsReportedWithoutOverwritingOtherState()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "state.json");
        File.WriteAllText(file, "{broken-json");
        var store = new CodexWebViewStateStore(file);

        var fallback = store.GetSnapshot();
        Assert.Empty(fallback);
        fallback["temporary-ui-value"] = true;
        Assert.Empty(store.GetSnapshot());
        Assert.ThrowsAny<JsonException>(() => store.Set("new-key", "new-value"));

        Assert.Equal("{broken-json", File.ReadAllText(file));
        File.WriteAllText(file, "{\"recovered\":true}");
        Assert.True(store.Get("recovered")?.Value<bool>());
    }

    [Fact]
    public void ReturnedValuesAndInputValuesCannotMutateStoredState()
    {
        using var temp = new TemporaryDirectory();
        var store = new CodexWebViewStateStore(Path.Combine(temp.Path, "state.json"));
        var input = new JArray("thread-1");
        store.Set("pinnedThreadIds", input);
        input[0] = "mutated-input";
        store.GetSnapshot()["pinnedThreadIds"]![0] = "mutated-snapshot";
        store.Get("pinnedThreadIds")![0] = "mutated-value";

        Assert.Equal("thread-1", store.Get("pinnedThreadIds")?[0]?.Value<string>());
    }
}
