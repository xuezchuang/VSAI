using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using Newtonsoft.Json;

namespace CodexVsix.Services;

/// <summary>Fetches off the request path and retains results until the host can apply them safely.</summary>
internal sealed class CodexProviderCatalogSynchronizer : IDisposable
{
    private readonly Func<CodexProviderConfiguration, CancellationToken, Task<CodexProviderDiscoveryResult>> _fetch;
    private readonly Func<CodexExtensionSettings, CodexProviderConfiguration, CodexProviderDiscoveryResult, CancellationToken, Task<bool>> _apply;
    private readonly Func<DateTime> _utcNow;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, DateTime> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private int _running;
    private bool _disposed;
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    internal CodexProviderCatalogSynchronizer(
        Func<CodexProviderConfiguration, CancellationToken, Task<CodexProviderDiscoveryResult>> fetch,
        Func<CodexExtensionSettings, CodexProviderConfiguration, CodexProviderDiscoveryResult, CancellationToken, Task<bool>> apply,
        Func<DateTime>? utcNow = null)
    {
        _fetch = fetch;
        _apply = apply;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal async Task RefreshDueAsync(CodexExtensionSettings settings)
    {
        if (_disposed || Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            var profiles = settings.Providers.ToArray();
            var activeKeys = new HashSet<string>(profiles.Where(p => p.Catalog?.AutoSync == true).Select(Scope), StringComparer.Ordinal);
            foreach (var key in _pending.Keys.Where(k => !activeKeys.Contains(k)).ToArray()) _pending.Remove(key);
            foreach (var key in _attempts.Keys.Where(k => !activeKeys.Contains(k)).ToArray()) _attempts.Remove(key);
            foreach (var provider in profiles)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (provider.Catalog?.AutoSync != true) continue;
                var key = Scope(provider);
                if (_pending.TryGetValue(key, out var pending))
                {
                    if (await _apply(settings, pending.Provider, pending.Result, _lifetime.Token).ConfigureAwait(false))
                        _pending.Remove(key);
                    continue;
                }
                var lastAttempt = _attempts.TryGetValue(key, out var attempted) ? attempted : provider.Catalog.LastAttemptUtc;
                if (lastAttempt.HasValue && _utcNow() - lastAttempt.Value < RefreshInterval) continue;
                var snapshot = JsonConvert.DeserializeObject<CodexProviderConfiguration>(JsonConvert.SerializeObject(provider))!;
                _attempts[key] = _utcNow();
                var result = await _fetch(snapshot, _lifetime.Token).ConfigureAwait(false);
                if (!await _apply(settings, snapshot, result, _lifetime.Token).ConfigureAwait(false))
                    _pending[key] = new Pending(snapshot, result);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Keep the last successful catalog; retry on the next scheduled refresh. */ }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    internal static string Scope(CodexProviderConfiguration provider) => provider.Id + ":" + ConnectionScope(provider);

    internal static string ConnectionScope(CodexProviderConfiguration provider)
    {
        // Only the digest is kept as a cache key. Never log credential-bearing input.
        var value = JsonConvert.SerializeObject(new[] { provider.BaseUrl, provider.ApiKey, provider.Catalog?.Source ?? "auto" });
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        // An outstanding fetch can still observe Token; dispose its source only with the owner.
    }

    private sealed class Pending
    {
        internal Pending(CodexProviderConfiguration provider, CodexProviderDiscoveryResult result)
        { Provider = provider; Result = result; }
        internal CodexProviderConfiguration Provider { get; }
        internal CodexProviderDiscoveryResult Result { get; }
    }
}
