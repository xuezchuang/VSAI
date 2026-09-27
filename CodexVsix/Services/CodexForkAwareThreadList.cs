using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

// Codex currently omits persisted paginated forks whose preview is still empty.
// Merge those rows with the server stream, retaining displaced rows for the next
// page instead of dropping them or forwarding a host cursor to app-server.
internal sealed class CodexForkAwareThreadList
{
    internal const string CursorPrefix = "vsai-fork-history:";
    private const int MaximumContinuations = 32;
    private const int MaximumServerPagesPerRequest = 16;
    private const int MaximumSeenThreads = 20000;
    private static readonly TimeSpan CursorLifetime = TimeSpan.FromMinutes(15);
    private readonly object _sync = new();
    private readonly Dictionary<string, SavedPage> _continuations = new(StringComparer.Ordinal);
    private readonly Func<string, JObject, CancellationToken, IReadOnlyList<JObject>> _readForks;
    private readonly Action<Exception>? _catalogReadFailed;
    private long _generation;

    internal CodexForkAwareThreadList(
        Func<string, JObject, CancellationToken, IReadOnlyList<JObject>>? readForks = null,
        Action<Exception>? catalogReadFailed = null)
    {
        _readForks = readForks ?? CodexForkHistoryCatalog.Read;
        _catalogReadFailed = catalogReadFailed;
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _generation++;
            _continuations.Clear();
        }
    }

    internal async Task<JToken?> ListAsync(
        string privateHome,
        JObject parameters,
        Func<JObject, CancellationToken, Task<JToken?>> fetchPage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = (JObject)parameters.DeepClone();
        var cursor = query["cursor"]?.Value<string>();
        query.Remove("cursor");
        var home = Path.GetFullPath(privateHome);
        PageState state;
        if (cursor?.StartsWith(CursorPrefix, StringComparison.Ordinal) == true)
        {
            lock (_sync)
            {
                if (!_continuations.TryGetValue(cursor, out var saved)
                    || DateTime.UtcNow - saved.CreatedAt > CursorLifetime
                    || saved.State.Generation != _generation
                    || !string.Equals(saved.State.Home, home, StringComparison.OrdinalIgnoreCase)
                    || !JToken.DeepEquals(saved.State.Query, query))
                {
                    throw new InvalidOperationException("历史分页已失效，请关闭历史列表后重新打开。");
                }
                // A cancelled/failed fetch must not consume this cursor. Replaying
                // it starts from the same native rows and fork position.
                state = saved.State.Copy();
            }
        }
        else
        {
            long generation;
            lock (_sync) generation = _generation;
            var result = await fetchPage((JObject)parameters.DeepClone(), cancellationToken).ConfigureAwait(false);
            // Native cursors were issued for a list with no missing forks. Keep
            // that stream unchanged until the next first-page refresh.
            if (!string.IsNullOrEmpty(cursor) || result is not JObject response || response["data"] is not JArray)
                return result;
            IReadOnlyList<JObject> forks;
            try
            {
                forks = await Task.Run(() => _readForks(home, query, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is InvalidOperationException || ex is ArgumentException
                || ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // Catalog recovery is additive. An unavailable/older local index
                // must not hide the history that app-server already returned.
                _catalogReadFailed?.Invoke(ex);
                forks = Array.Empty<JObject>();
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureGeneration(generation);
            if (forks.Count == 0) return result;
            state = new PageState(home, query, generation, response, forks);
        }

        var limit = Math.Max(1, Math.Min(500, query["limit"]?.Value<int>() ?? 50));
        var output = new JArray();
        var pagesRead = 0;
        while (output.Count < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SkipEmitted(state);
            while (state.NativeIndex >= state.NativeRows.Length && state.NativeCursor is not null)
            {
                if (++pagesRead > MaximumServerPagesPerRequest)
                    throw new InvalidOperationException("历史分页未取得进展，请重新打开历史列表。");
                var next = (JObject)query.DeepClone();
                var nativeCursor = state.NativeCursor;
                next["cursor"] = nativeCursor;
                var page = await fetchPage(next, cancellationToken).ConfigureAwait(false);
                if (page is not JObject nativePage || nativePage["data"] is not JArray)
                    throw new InvalidDataException("Codex 返回了无效的历史分页。");
                state.LoadNativePage(nativePage);
                if (string.Equals(nativeCursor, state.NativeCursor, StringComparison.Ordinal))
                    throw new InvalidDataException("Codex 返回了重复的历史分页游标。");
                SkipEmitted(state);
            }

            var native = state.NativeIndex < state.NativeRows.Length ? state.NativeRows[state.NativeIndex] : null;
            var fork = state.ForkIndex < state.Forks.Length ? state.Forks[state.ForkIndex] : null;
            if (native is null && fork is null) break;
            var takeNative = native is not null && (fork is null
                || string.Equals(Id(native), Id(fork), StringComparison.Ordinal)
                || Compare(native, fork, query) <= 0);
            var row = takeNative ? state.NativeRows[state.NativeIndex++] : state.Forks[state.ForkIndex++];
            if (!state.Emitted.Add(Id(row))) continue;
            if (state.Emitted.Count > MaximumSeenThreads)
                throw new InvalidOperationException("历史分页读取过多，请使用搜索缩小范围。");
            output.Add(row.DeepClone());
        }
        SkipEmitted(state);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGeneration(state.Generation);
        var hasMore = state.NativeIndex < state.NativeRows.Length || state.NativeCursor is not null
            || state.ForkIndex < state.Forks.Length;
        var resultPage = (JObject)state.ResponseTemplate.DeepClone();
        resultPage["data"] = output;
        if (resultPage["threads"] is not null) resultPage["threads"] = output.DeepClone();
        if (resultPage["conversations"] is not null) resultPage["conversations"] = output.DeepClone();
        var nextCursor = hasMore ? Save(state) : null;
        resultPage["nextCursor"] = nextCursor is null ? JValue.CreateNull() : nextCursor;
        if (resultPage["cursor"] is not null) resultPage["cursor"] = resultPage["nextCursor"]!.DeepClone();
        return resultPage;
    }

    private string Save(PageState state)
    {
        lock (_sync)
        {
            if (state.Generation != _generation)
                throw new OperationCanceledException("The history runtime changed during pagination.");
            var now = DateTime.UtcNow;
            foreach (var key in _continuations.Where(pair => now - pair.Value.CreatedAt > CursorLifetime)
                .Select(pair => pair.Key).ToArray()) _continuations.Remove(key);
            while (_continuations.Count >= MaximumContinuations)
                _continuations.Remove(_continuations.OrderBy(pair => pair.Value.CreatedAt).First().Key);
            var cursor = CursorPrefix + Guid.NewGuid().ToString("N");
            _continuations.Add(cursor, new SavedPage(state, now));
            return cursor;
        }
    }

    private void EnsureGeneration(long generation)
    {
        lock (_sync)
        {
            if (generation != _generation)
                throw new OperationCanceledException("The history runtime changed during pagination.");
        }
    }

    private static void SkipEmitted(PageState state)
    {
        while (state.NativeIndex < state.NativeRows.Length && state.Emitted.Contains(Id(state.NativeRows[state.NativeIndex])))
            state.NativeIndex++;
        while (state.ForkIndex < state.Forks.Length && state.Emitted.Contains(Id(state.Forks[state.ForkIndex])))
            state.ForkIndex++;
    }

    private static string Id(JObject row) => row["id"]?.Value<string>() ?? string.Empty;

    private static JObject[] Rows(IEnumerable<JToken> rows) => rows.OfType<JObject>()
        .Where(row => !string.IsNullOrWhiteSpace(Id(row)))
        .Select(row => (JObject)row.DeepClone()).ToArray();

    private static int Compare(JObject first, JObject second, JObject query)
    {
        var updated = query["sortKey"]?.Value<string>() == "updated_at";
        var camel = updated ? "updatedAt" : "createdAt";
        var snake = updated ? "updated_at" : "created_at";
        var result = Timestamp(first[camel] ?? first[snake]).CompareTo(Timestamp(second[camel] ?? second[snake]));
        if (result == 0) result = string.Compare(Id(first), Id(second), StringComparison.Ordinal);
        return query["sortDirection"]?.Value<string>() == "asc" ? result : -result;
    }

    private static decimal Timestamp(JToken? value)
    {
        return decimal.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private sealed class SavedPage
    {
        internal SavedPage(PageState state, DateTime createdAt) { State = state; CreatedAt = createdAt; }
        internal PageState State { get; }
        internal DateTime CreatedAt { get; }
    }

    private sealed class PageState
    {
        internal PageState(string home, JObject query, long generation, JObject firstPage, IReadOnlyList<JObject> forks)
        {
            Home = home;
            Query = query;
            Generation = generation;
            ResponseTemplate = (JObject)firstPage.DeepClone();
            ResponseTemplate.Remove("data");
            if (ResponseTemplate["threads"] is not null) ResponseTemplate["threads"] = new JArray();
            if (ResponseTemplate["conversations"] is not null) ResponseTemplate["conversations"] = new JArray();
            LoadNativePage(firstPage);
            var present = new HashSet<string>(NativeRows.Select(Id), StringComparer.Ordinal);
            Forks = Rows(forks).Where(row => !present.Contains(Id(row))).ToArray();
            Array.Sort(Forks, (first, second) => Compare(first, second, query));
        }

        private PageState(PageState other)
        {
            Home = other.Home;
            Query = other.Query;
            Generation = other.Generation;
            ResponseTemplate = other.ResponseTemplate;
            NativeRows = other.NativeRows;
            NativeIndex = other.NativeIndex;
            NativeCursor = other.NativeCursor;
            Forks = other.Forks;
            ForkIndex = other.ForkIndex;
            Emitted = new HashSet<string>(other.Emitted, StringComparer.Ordinal);
        }

        internal PageState Copy() => new(this);
        internal string Home { get; }
        internal JObject Query { get; }
        internal long Generation { get; }
        internal JObject ResponseTemplate { get; }
        internal JObject[] NativeRows { get; private set; } = Array.Empty<JObject>();
        internal int NativeIndex { get; set; }
        internal string? NativeCursor { get; private set; }
        internal JObject[] Forks { get; }
        internal int ForkIndex { get; set; }
        internal HashSet<string> Emitted { get; } = new(StringComparer.Ordinal);

        internal void LoadNativePage(JObject page)
        {
            NativeRows = Rows((JArray)page["data"]!);
            NativeIndex = 0;
            var cursor = page["nextCursor"] ?? page["cursor"];
            NativeCursor = cursor?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(cursor.Value<string>())
                ? cursor.Value<string>() : null;
        }
    }
}
