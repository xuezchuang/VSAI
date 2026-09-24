using System;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

// This fixture deliberately controls a pending network response to verify nonblocking refreshes.
#pragma warning disable VSTHRD003
public sealed class CodexProviderCatalogSynchronizerTests
{
    [Fact]
    public async Task SimultaneousRefreshesShareFetchAndBusyHostDefersApplication()
    {
        var response = new TaskCompletionSource<CodexProviderDiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        var applications = 0;
        var busy = true;
        var settings = new CodexExtensionSettings { Providers = { CodexProviderEditorProtocolTests.Provider() } };
        using var synchronizer = new CodexProviderCatalogSynchronizer((_, __) => { fetches++; return response.Task; },
            (_, __, ___, ____) => { applications++; return Task.FromResult(!busy); });
        var first = synchronizer.RefreshDueAsync(settings);
        await synchronizer.RefreshDueAsync(settings);
        Assert.Equal(1, fetches);
        response.SetResult(CodexProviderEditorProtocolTests.Result("remote"));
        await first;
        Assert.Equal(1, applications);
        busy = false;
        await synchronizer.RefreshDueAsync(settings);
        Assert.Equal(1, fetches);
        Assert.Equal(2, applications);
        await synchronizer.RefreshDueAsync(settings);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task DisablingSyncDropsDeferredResultAndCredentialChangeStartsFreshFetch()
    {
        var provider = CodexProviderEditorProtocolTests.Provider();
        var settings = new CodexExtensionSettings { Providers = { provider } };
        var fetches = 0;
        var applications = 0;
        using var synchronizer = new CodexProviderCatalogSynchronizer((_, __) =>
        {
            fetches++;
            return Task.FromResult(CodexProviderEditorProtocolTests.Result("remote"));
        }, (_, __, ___, ____) => { applications++; return Task.FromResult(false); });
        await synchronizer.RefreshDueAsync(settings);
        provider.Catalog!.AutoSync = false;
        await synchronizer.RefreshDueAsync(settings);
        Assert.Equal(1, applications);
        provider.Catalog.AutoSync = true;
        provider.ApiKey = "changed-synthetic-secret";
        await synchronizer.RefreshDueAsync(settings);
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task FreshPersistedCacheSkipsNetworkAndExpiredCacheRefreshes()
    {
        var now = DateTime.UtcNow;
        var provider = CodexProviderEditorProtocolTests.Provider();
        provider.Catalog!.LastAttemptUtc = now;
        var settings = new CodexExtensionSettings { Providers = { provider } };
        var fetches = 0;
        using var synchronizer = new CodexProviderCatalogSynchronizer((_, __) =>
        {
            fetches++;
            return Task.FromResult(CodexProviderEditorProtocolTests.Result("remote"));
        }, (_, __, ___, ____) => Task.FromResult(true), () => now);
        await synchronizer.RefreshDueAsync(settings);
        Assert.Equal(0, fetches);
        now += TimeSpan.FromMinutes(11);
        await synchronizer.RefreshDueAsync(settings);
        Assert.Equal(1, fetches);
    }
}
