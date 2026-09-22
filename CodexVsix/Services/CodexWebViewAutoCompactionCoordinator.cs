using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal sealed class CodexWebViewAutoCompactionCoordinator : IDisposable
{
    internal const double ContextUsageThreshold = 0.85d;

    private static readonly TimeSpan CompactionCooldown = TimeSpan.FromMinutes(5);

    private readonly object _syncRoot = new();
    private readonly Func<bool> _isEnabled;
    private readonly Func<string, CancellationToken, Task> _compactThreadAsync;
    private readonly Action<string> _log;
    private readonly Func<CancellationToken, Task> _delayAsync;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly HashSet<string> _pendingThreads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeThreads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _compactingThreads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastCompactionUtc = new(StringComparer.Ordinal);
    private bool _disposed;

    public CodexWebViewAutoCompactionCoordinator(
        Func<bool> isEnabled,
        Func<string, CancellationToken, Task> compactThreadAsync,
        Action<string> log,
        Func<CancellationToken, Task>? delayAsync = null)
    {
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _compactThreadAsync = compactThreadAsync ?? throw new ArgumentNullException(nameof(compactThreadAsync));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _delayAsync = delayAsync ?? (token => Task.Delay(250, token));
    }

    public void ObserveNotification(string method, JToken? parameters)
    {
        if (_disposed)
        {
            return;
        }

        if (TryReadContextUsage(method, parameters, out var threadId, out var usedTokens, out var contextWindow))
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                if (_isEnabled() && usedTokens / (double)contextWindow >= ContextUsageThreshold)
                {
                    _pendingThreads.Add(threadId);
                }
                else
                {
                    _pendingThreads.Remove(threadId);
                }
            }

            return;
        }

        var isTurnStart = IsTurnStart(method);
        if (!isTurnStart && !IsTurnCompletion(method))
        {
            return;
        }

        threadId = ReadString(parameters, "threadId", "thread_id");
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        CancellationToken lifetimeToken;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            if (isTurnStart)
            {
                _activeThreads.Add(threadId!);
                return;
            }

            _activeThreads.Remove(threadId!);
            if (!_isEnabled())
            {
                _pendingThreads.Remove(threadId!);
                return;
            }

            if (!_pendingThreads.Contains(threadId!) || _compactingThreads.Contains(threadId!))
            {
                return;
            }

            if (_lastCompactionUtc.TryGetValue(threadId!, out var last)
                && DateTime.UtcNow - last < CompactionCooldown)
            {
                _pendingThreads.Remove(threadId!);
                return;
            }

            _compactingThreads.Add(threadId!);
            lifetimeToken = _lifetimeCts.Token;
        }

        _ = RunCompactionAsync(threadId!, lifetimeToken);
    }

    internal static bool TryReadContextUsage(
        string method,
        JToken? parameters,
        out string threadId,
        out long usedTokens,
        out long contextWindow)
    {
        threadId = ReadString(parameters, "threadId", "thread_id") ?? string.Empty;
        usedTokens = 0;
        contextWindow = 0;

        if (string.Equals(method, "thread/tokenUsage/updated", StringComparison.Ordinal))
        {
            var usage = parameters?["tokenUsage"];
            var reportedTokens = ReadLong(usage, "last", "totalTokens")
                ?? ReadLong(usage, "lastTokenUsage", "totalTokens")
                ?? ReadLong(usage, "last_token_usage", "total_tokens")
                ?? ReadLong(usage, "total", "totalTokens")
                ?? ReadLong(usage, "totalTokenUsage", "totalTokens")
                ?? ReadLong(usage, "total_token_usage", "total_tokens");
            usedTokens = reportedTokens ?? 0L;
            contextWindow = ReadLong(usage, "modelContextWindow")
                ?? ReadLong(usage, "model_context_window")
                ?? 0L;
            return !string.IsNullOrWhiteSpace(threadId) && reportedTokens.HasValue && usedTokens >= 0 && contextWindow > 0;
        }

        if (string.Equals(method, "codex/event/token_count", StringComparison.Ordinal))
        {
            var info = parameters?["msg"]?["info"];
            var reportedTokens = ReadLong(info, "last_token_usage", "total_tokens")
                ?? ReadLong(info, "lastTokenUsage", "totalTokens")
                ?? ReadLong(info, "total_token_usage", "total_tokens")
                ?? ReadLong(info, "totalTokenUsage", "totalTokens");
            usedTokens = reportedTokens ?? 0L;
            contextWindow = ReadLong(info, "model_context_window")
                ?? ReadLong(info, "modelContextWindow")
                ?? 0L;
            return !string.IsNullOrWhiteSpace(threadId) && reportedTokens.HasValue && usedTokens >= 0 && contextWindow > 0;
        }

        return false;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingThreads.Clear();
            _activeThreads.Clear();
        }

        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }

    private async Task RunCompactionAsync(string threadId, CancellationToken lifetimeToken)
    {
        try
        {
            // Let the app-server finish publishing turn/completed, then recheck
            // the latest settings, usage and turn state before issuing the request.
            await _delayAsync(lifetimeToken).ConfigureAwait(false);
            lock (_syncRoot)
            {
                if (_disposed || lifetimeToken.IsCancellationRequested)
                {
                    return;
                }

                if (!_isEnabled())
                {
                    _pendingThreads.Remove(threadId);
                    return;
                }

                if (!_pendingThreads.Contains(threadId) || _activeThreads.Contains(threadId))
                {
                    return;
                }
            }

            lifetimeToken.ThrowIfCancellationRequested();
            await _compactThreadAsync(threadId, lifetimeToken).ConfigureAwait(false);
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _pendingThreads.Remove(threadId);
                _lastCompactionUtc[threadId] = DateTime.UtcNow;
            }

            _log("Automatically compacted a long conversation at 85% context usage.");
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                // Keep only an existing high-usage decision for a later retry.
                // A lower-usage notification may already have withdrawn it.
                if (!_isEnabled())
                {
                    _pendingThreads.Remove(threadId);
                }
            }

            _log("Automatic conversation compaction failed: " + ex.Message);
        }
        finally
        {
            lock (_syncRoot)
            {
                _compactingThreads.Remove(threadId);
            }
        }
    }

    private static bool IsTurnStart(string method)
    {
        return string.Equals(method, "turn/started", StringComparison.Ordinal)
            || string.Equals(method, "codex/event/task_started", StringComparison.Ordinal);
    }

    private static bool IsTurnCompletion(string method)
    {
        return string.Equals(method, "turn/completed", StringComparison.Ordinal)
            || string.Equals(method, "codex/event/task_complete", StringComparison.Ordinal);
    }

    private static long? ReadLong(JToken? token, params string[] path)
    {
        foreach (var segment in path)
        {
            token = token?[segment];
        }

        return token?.Value<long?>();
    }

    private static string? ReadString(JToken? token, params string[] names)
    {
        if (token is JObject record)
        {
            foreach (var name in names)
            {
                var value = record[name]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            foreach (var property in record.Properties())
            {
                var nested = ReadString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
