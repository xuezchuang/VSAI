using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexForkAwareThreadListTests
{
    [Fact]
    public async Task PersistedForksAppearWithoutSendingANewMessageOrChangingServerRows()
    {
        var original = Page(null, Row("original", 50), Row("second", 40), Row("third", 30), Row("fourth", 20));
        var before = original.DeepClone();
        var service = WithForks(Row("fork-one", 45), Row("fork-two", 44));
        var result = await service.ListAsync(@"C:\private", Query(50), (_, _) => Task.FromResult<JToken?>(original), CancellationToken.None);

        Assert.Equal(new[] { "original", "fork-one", "fork-two", "second", "third", "fourth" }, Ids(result));
        Assert.Equal(JTokenType.Null, result?["nextCursor"]?.Type);
        Assert.True(JToken.DeepEquals(before, original));
    }

    [Fact]
    public async Task ForksDisplaceRowsAcrossPagesWithoutDroppingOrDuplicatingConversations()
    {
        var requests = new List<string?>();
        var service = WithForks(Row("fork-new", 90), Row("fork-old", 50));
        Task<JToken?> Fetch(JObject request, CancellationToken _)
        {
            var cursor = request["cursor"]?.Value<string>();
            requests.Add(cursor);
            Assert.False(cursor?.StartsWith(CodexForkAwareThreadList.CursorPrefix, StringComparison.Ordinal) ?? false);
            return Task.FromResult<JToken?>(cursor is null
                ? Page("native-page-two", Row("one", 100), Row("two", 70))
                : Page(null, Row("three", 60), Row("four", 40)));
        }

        var first = await service.ListAsync(@"C:\private", Query(2), Fetch, CancellationToken.None);
        var second = await service.ListAsync(@"C:\private", Next(Query(2), first), Fetch, CancellationToken.None);
        var third = await service.ListAsync(@"C:\private", Next(Query(2), second), Fetch, CancellationToken.None);

        Assert.Equal(new[] { "one", "fork-new" }, Ids(first));
        Assert.Equal(new[] { "two", "three" }, Ids(second));
        Assert.Equal(new[] { "fork-old", "four" }, Ids(third));
        Assert.Equal(JTokenType.Null, third?["nextCursor"]?.Type);
        Assert.Equal(new string?[] { null, "native-page-two" }, requests);
    }

    [Fact]
    public async Task CancelledOrFailedPageCanBeRetriedFromTheSameCursor()
    {
        var service = WithForks(Row("fork", 90));
        var fail = true;
        Task<JToken?> Fetch(JObject request, CancellationToken _)
        {
            if (request["cursor"]?.Value<string>() is null)
                return Task.FromResult<JToken?>(Page("older", Row("one", 100), Row("two", 80)));
            if (fail) { fail = false; throw new OperationCanceledException(); }
            return Task.FromResult<JToken?>(Page(null, Row("three", 70)));
        }
        var first = await service.ListAsync(@"C:\private", Query(2), Fetch, CancellationToken.None);
        var next = Next(Query(2), first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListAsync(@"C:\private", next, Fetch, CancellationToken.None));
        var retried = await service.ListAsync(@"C:\private", next, Fetch, CancellationToken.None);
        var replayed = await service.ListAsync(@"C:\private", next, Fetch, CancellationToken.None);

        Assert.Equal(new[] { "two", "three" }, Ids(retried));
        Assert.Equal(Ids(retried), Ids(replayed));
    }

    [Fact]
    public async Task CursorCannotBeReusedForAnotherWorkspaceSearchOrRuntime()
    {
        var service = WithForks(Row("fork", 90));
        Task<JToken?> Fetch(JObject _, CancellationToken __) => Task.FromResult<JToken?>(Page(null, Row("one", 100), Row("two", 80)));
        var first = await service.ListAsync(@"C:\private", Query(1), Fetch, CancellationToken.None);
        var next = Next(Query(1), first);
        var otherFolder = (JObject)next.DeepClone();
        otherFolder["cwd"] = @"D:\other";
        var search = (JObject)next.DeepClone();
        search["searchTerm"] = "different";

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync(@"C:\other-home", next, Fetch, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync(@"C:\private", otherFolder, Fetch, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync(@"C:\private", search, Fetch, CancellationToken.None));
        service.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync(@"C:\private", next, Fetch, CancellationToken.None));
    }

    [Fact]
    public async Task ServerForkWinsWhenItAlreadyAppearsInTheNativeList()
    {
        var staleFork = Row("fork", 90);
        staleFork["preview"] = "inherited";
        var liveFork = Row("fork", 110);
        liveFork["preview"] = "new message";
        var service = WithForks(staleFork, Row("other-fork", 80));
        var result = await service.ListAsync(@"C:\private", Query(50),
            (_, _) => Task.FromResult<JToken?>(Page(null, liveFork, Row("original", 100))), CancellationToken.None);

        Assert.Equal(new[] { "fork", "original", "other-fork" }, Ids(result));
        Assert.Equal("new message", result?["data"]?[0]?["preview"]?.Value<string>());
    }

    [Fact]
    public async Task ForkAlsoReturnedOnALaterNativePageAppearsOnlyOnce()
    {
        var service = WithForks(Row("fork", 90), Row("old-fork", 30));
        Task<JToken?> Fetch(JObject request, CancellationToken _) => Task.FromResult<JToken?>(
            request["cursor"]?.Value<string>() is null
                ? Page("older", Row("one", 100), Row("two", 80))
                : Page(null, Row("fork", 90), Row("three", 60)));

        var first = await service.ListAsync(@"C:\private", Query(2), Fetch, CancellationToken.None);
        var second = await service.ListAsync(@"C:\private", Next(Query(2), first), Fetch, CancellationToken.None);
        var third = await service.ListAsync(@"C:\private", Next(Query(2), second), Fetch, CancellationToken.None);

        // A native catalog can catch up between page requests. Keep the already
        // shown fork at its snapshot position; a first-page refresh gets its new metadata.
        Assert.Equal(new[] { "one", "fork", "two", "three", "old-fork" }, Ids(first).Concat(Ids(second)).Concat(Ids(third)));
        Assert.Equal(JTokenType.Null, third?["nextCursor"]?.Type);
    }

    [Fact]
    public async Task AscendingCreationOrderAndSearchArePreserved()
    {
        JObject? catalogQuery = null;
        var fork = Row("fork", 90);
        fork["createdAt"] = 20;
        var native = Row("native", 100);
        native["createdAt"] = 10;
        var service = new CodexForkAwareThreadList((_, query, _) => { catalogQuery = query; return new[] { fork }; });
        var parameters = Query(50);
        parameters["sortKey"] = "created_at";
        parameters["sortDirection"] = "asc";
        parameters["searchTerm"] = "heap";
        var result = await service.ListAsync(@"C:\private", parameters,
            (_, _) => Task.FromResult<JToken?>(Page(null, native)), CancellationToken.None);

        Assert.Equal(new[] { "native", "fork" }, Ids(result));
        Assert.Equal("heap", catalogQuery?["searchTerm"]?.Value<string>());
        Assert.Equal(@"D:\workspace", catalogQuery?["cwd"]?.Value<string>());
        Assert.Null(catalogQuery?["cursor"]);
    }

    [Fact]
    public async Task NativePaginationRemainsUnchangedWhenThereAreNoMissingForks()
    {
        var reads = 0;
        var service = new CodexForkAwareThreadList((_, _, _) => { reads++; return Array.Empty<JObject>(); });
        var server = Page("native-cursor", Row("one", 100));
        var first = await service.ListAsync(@"C:\private", Query(50), (_, _) => Task.FromResult<JToken?>(server), CancellationToken.None);
        var second = await service.ListAsync(@"C:\private", Next(Query(50), first), (_, _) => Task.FromResult<JToken?>(Page(null)), CancellationToken.None);

        Assert.Same(server, first);
        Assert.Empty(Ids(second));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task RuntimeReplacementDuringCatalogReadRejectsEvenTheFinalPage()
    {
        CodexForkAwareThreadList? service = null;
        service = new CodexForkAwareThreadList((_, _, _) =>
        {
            service!.Clear();
            return new[] { Row("fork", 90) };
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListAsync(@"C:\private", Query(50),
            (_, _) => Task.FromResult<JToken?>(Page(null, Row("one", 100))), CancellationToken.None));
    }

    [Fact]
    public async Task UnavailableCatalogPreservesNativeHistoryButCancellationIsNotHidden()
    {
        Exception? reported = null;
        var service = new CodexForkAwareThreadList((_, _, _) => throw new IOException("locked"), ex => reported = ex);
        var server = Page("native-next", Row("one", 100));
        var result = await service.ListAsync(@"C:\private", Query(50),
            (_, _) => Task.FromResult<JToken?>(server), CancellationToken.None);

        Assert.Same(server, result);
        Assert.IsType<IOException>(reported);
        var cancelled = new CodexForkAwareThreadList((_, _, _) => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ListAsync(@"C:\private", Query(50),
            (_, _) => Task.FromResult<JToken?>(server), CancellationToken.None));
    }

    private static CodexForkAwareThreadList WithForks(params JObject[] forks) => new((_, _, _) => forks);
    private static JObject Query(int limit) => new()
    {
        ["limit"] = limit, ["cwd"] = @"D:\workspace", ["archived"] = false,
        ["sortKey"] = "updated_at", ["modelProviders"] = new JArray(), ["sourceKinds"] = new JArray()
    };
    private static JObject Row(string id, long timestamp) => new()
    {
        ["id"] = id, ["preview"] = "same inherited title", ["cwd"] = @"D:\workspace",
        ["updatedAt"] = timestamp, ["createdAt"] = timestamp
    };
    private static JObject Page(string? cursor, params JObject[] rows) => new()
    {
        ["data"] = new JArray(rows), ["nextCursor"] = cursor is null ? JValue.CreateNull() : cursor
    };
    private static JObject Next(JObject parameters, JToken? page)
    {
        parameters["cursor"] = page?["nextCursor"]?.DeepClone();
        return parameters;
    }
    private static string[] Ids(JToken? response) => ((JArray)response!["data"]!).Select(row => row["id"]!.Value<string>()!).ToArray();
}
