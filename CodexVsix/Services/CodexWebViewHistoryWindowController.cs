using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal sealed class CodexWebViewHistoryWindowController : IDisposable
{
    internal const int InitialTurnBudget = 120;
    internal const int AdditionalTurnBudget = 20;
    internal const long InitialByteBudget = 2 * 1024 * 1024;
    internal const long AdditionalByteBudget = 512 * 1024;

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, ThreadWindowState> _states = new(StringComparer.Ordinal);
    private readonly Action<CodexWebViewHistoryWindowStatus> _statusChanged;
    private bool _disposed;

    public CodexWebViewHistoryWindowController(Action<CodexWebViewHistoryWindowStatus> statusChanged)
    {
        _statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));
    }

    public async Task WaitForCapacityAsync(string method, JToken? parameters, CancellationToken cancellationToken)
    {
        if (!TryReadTurnsRequest(method, parameters, out var threadId, out var cursor)
            || string.IsNullOrWhiteSpace(cursor))
        {
            return;
        }

        Task? waitTask = null;
        CodexWebViewHistoryWindowStatus? status = null;
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            var state = GetOrCreateState(threadId);
            state.HasMore = true;
            if (!HasReachedBudget(state))
            {
                return;
            }

            if (state.PendingPageGate is null || state.PendingPageGate.Task.IsCompleted)
            {
                state.PendingPageGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            state.IsWaitingForUser = true;
            state.IsLoading = false;
            waitTask = state.PendingPageGate.Task;
            status = CreateStatus(state);
        }

        Publish(status);
        await WaitWithCancellationAsync(waitTask!, cancellationToken).ConfigureAwait(false);
    }

    public void ObserveResponse(string method, JToken? parameters, JToken? response)
    {
        if (!TryReadTurnsRequest(method, parameters, out var threadId, out _))
        {
            return;
        }

        CodexWebViewHistoryWindowStatus status;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            var state = GetOrCreateState(threadId);
            if (response is JObject record)
            {
                if (record["data"] is JArray turns)
                {
                    foreach (var turn in turns)
                    {
                        var identity = GetTurnIdentity(turn);
                        if (state.LoadedTurnIds.Add(identity))
                        {
                            state.LoadedTurns++;
                            state.LoadedBytes += EstimateBytes(turn);
                        }
                    }
                }

                state.HasMore = record["nextCursor"]?.Type == JTokenType.String
                    && !string.IsNullOrWhiteSpace(record["nextCursor"]?.Value<string>());
            }
            else
            {
                state.HasMore = false;
            }

            if (!state.HasMore)
            {
                state.IsLoading = false;
                state.IsWaitingForUser = false;
            }
            else if (HasReachedBudget(state))
            {
                state.IsLoading = false;
            }

            status = CreateStatus(state);
        }

        Publish(status);
    }

    public bool LoadOlder(string? threadId)
    {
        TaskCompletionSource<bool>? gate;
        CodexWebViewHistoryWindowStatus? status;
        lock (_syncRoot)
        {
            if (_disposed || !TryGetState(threadId, out var state) || !state.HasMore || state.IsLoading)
            {
                return false;
            }

            // A single server page can exceed the previous budget. Each click
            // must grant a new batch from what has actually been loaded.
            state.AllowedTurns = Math.Max(state.AllowedTurns, state.LoadedTurns) + AdditionalTurnBudget;
            state.AllowedBytes = Math.Max(state.AllowedBytes, state.LoadedBytes) + AdditionalByteBudget;
            state.IsWaitingForUser = false;
            state.IsLoading = state.HasMore;
            gate = state.PendingPageGate;
            state.PendingPageGate = null;
            status = CreateStatus(state);
        }

        gate?.TrySetResult(true);
        Publish(status);
        return true;
    }

    public void ObserveFailure(string method, JToken? parameters)
    {
        if (!TryReadTurnsRequest(method, parameters, out var threadId, out _))
        {
            return;
        }

        CodexWebViewHistoryWindowStatus? status = null;
        lock (_syncRoot)
        {
            if (!_disposed && _states.TryGetValue(threadId, out var state))
            {
                state.IsLoading = false;
                status = CreateStatus(state);
            }
        }

        Publish(status);
    }

    public void Republish(string? threadId)
    {
        CodexWebViewHistoryWindowStatus? status = null;
        lock (_syncRoot)
        {
            if (!_disposed && TryGetState(threadId, out var state))
            {
                status = CreateStatus(state);
            }
        }

        Publish(status);
    }

    internal CodexWebViewHistoryWindowStatus? GetStatus(string threadId)
    {
        lock (_syncRoot)
        {
            return _states.TryGetValue(threadId, out var state) ? CreateStatus(state) : null;
        }
    }

    public void Dispose()
    {
        List<TaskCompletionSource<bool>> gates = new();
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var state in _states.Values)
            {
                if (state.PendingPageGate is not null)
                {
                    gates.Add(state.PendingPageGate);
                }
            }

            _states.Clear();
        }

        foreach (var gate in gates)
        {
            gate.TrySetCanceled();
        }
    }

    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "Both tasks use asynchronous continuations and this method never depends on the Visual Studio UI context.")]
    private static async Task WaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancellation.TrySetCanceled()))
        {
            var completed = await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
    }

    private static bool TryReadTurnsRequest(string method, JToken? parameters, out string threadId, out string? cursor)
    {
        threadId = string.Empty;
        cursor = null;
        if (!string.Equals(method, "thread/turns/list", StringComparison.Ordinal)
            || parameters is not JObject record)
        {
            return false;
        }

        threadId = record["threadId"]?.Value<string>() ?? string.Empty;
        cursor = record["cursor"]?.Value<string>();
        return !string.IsNullOrWhiteSpace(threadId);
    }

    private ThreadWindowState GetOrCreateState(string threadId)
    {
        if (!_states.TryGetValue(threadId, out var state))
        {
            state = new ThreadWindowState(threadId);
            _states.Add(threadId, state);
        }

        return state;
    }

    private bool TryGetState(string? threadId, out ThreadWindowState state)
    {
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            return _states.TryGetValue(threadId!, out state!);
        }

        foreach (var candidate in _states.Values)
        {
            if (candidate.IsWaitingForUser || candidate.IsLoading)
            {
                state = candidate;
                return true;
            }
        }

        state = null!;
        return false;
    }

    private static bool HasReachedBudget(ThreadWindowState state)
    {
        return state.LoadedTurns >= state.AllowedTurns
            || state.LoadedBytes >= state.AllowedBytes;
    }

    private static CodexWebViewHistoryWindowStatus CreateStatus(ThreadWindowState state)
    {
        var reachedBudget = HasReachedBudget(state);
        return new CodexWebViewHistoryWindowStatus(
            state.ThreadId,
            state.LoadedTurns,
            state.LoadedBytes,
            state.HasMore && (reachedBudget || state.IsLoading || state.IsWaitingForUser),
            state.IsLoading,
            state.IsWaitingForUser || reachedBudget);
    }

    private static string GetTurnIdentity(JToken turn)
    {
        var id = turn["id"]?.Value<string>() ?? turn["turnId"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id!;
        }

        var serialized = turn.ToString(Formatting.None);
        return "anonymous:" + serialized.GetHashCode().ToString("X8");
    }

    private static int EstimateBytes(JToken token)
    {
        return Encoding.UTF8.GetByteCount(token.ToString(Formatting.None));
    }

    private void Publish(CodexWebViewHistoryWindowStatus? status)
    {
        if (status is not null)
        {
            _statusChanged(status);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CodexWebViewHistoryWindowController));
        }
    }

    private sealed class ThreadWindowState
    {
        public ThreadWindowState(string threadId)
        {
            ThreadId = threadId;
        }

        public string ThreadId { get; }
        public HashSet<string> LoadedTurnIds { get; } = new(StringComparer.Ordinal);
        public int LoadedTurns { get; set; }
        public long LoadedBytes { get; set; }
        public int AllowedTurns { get; set; } = InitialTurnBudget;
        public long AllowedBytes { get; set; } = InitialByteBudget;
        public bool HasMore { get; set; }
        public bool IsLoading { get; set; }
        public bool IsWaitingForUser { get; set; }
        public TaskCompletionSource<bool>? PendingPageGate { get; set; }
    }
}

internal sealed class CodexWebViewHistoryWindowStatus
{
    public CodexWebViewHistoryWindowStatus(
        string threadId,
        int loadedTurns,
        long loadedBytes,
        bool isVisible,
        bool isLoading,
        bool canLoadMore)
    {
        ThreadId = threadId;
        LoadedTurns = loadedTurns;
        LoadedBytes = loadedBytes;
        IsVisible = isVisible;
        IsLoading = isLoading;
        CanLoadMore = canLoadMore;
    }

    public string ThreadId { get; }
    public int LoadedTurns { get; }
    public long LoadedBytes { get; }
    public bool IsVisible { get; }
    public bool IsLoading { get; }
    public bool CanLoadMore { get; }
}
