using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderEditorProtocolTests
{
    [Fact]
    public void PublicStateContainsCapabilitiesAndExactKeysButNoCredential()
    {
        var provider = Provider();
        provider.Catalog!.Overrides["Case-Sensitive"] = new() { ContextWindow = 123456 };
        var state = CodexProviderEditorProtocol.ToPublicProfile(provider);
        Assert.True(state["hasApiKey"]!.Value<bool>());
        Assert.Null(state["apiKey"]);
        Assert.DoesNotContain(provider.ApiKey, state.ToString(), StringComparison.Ordinal);
        Assert.Equal(123456, state["catalog"]!["overrides"]!["Case-Sensitive"]!["contextWindow"]!.Value<long>());
    }

    [Fact]
    public void FailedRefreshKeepsSnapshotAndLocalOverride()
    {
        var provider = Provider();
        provider.Catalog!.DiscoveredModels.Add(new() { Id = "remote", ContextWindow = 200000 });
        provider.Catalog.Overrides["remote"] = new() { DisplayName = "My label" };
        CodexProviderCatalogConfigurationService.ApplyEffectiveProperties(provider);
        var settings = new CodexExtensionSettings { Providers = { provider } };
        var result = new CodexProviderDiscoveryResult(Array.Empty<CodexProviderModelMetadata>(), "auth-error", "密钥失效", DateTime.UtcNow);

        Assert.True(CodexProviderEditorProtocol.ApplyDiscovery(settings, provider, result));
        var saved = settings.Providers[0];
        Assert.Contains("remote", saved.Models);
        Assert.Equal("My label", saved.Catalog!.Overrides["remote"].DisplayName);
        Assert.Equal("auth-error", saved.Catalog.Status);
    }

    [Fact]
    public void ChangedCredentialsAndDisabledSyncRejectLateDiscovery()
    {
        var original = Provider();
        var current = CodexProviderEditorProtocol.Clone(original);
        current.ApiKey = "changed-synthetic-secret";
        var settings = new CodexExtensionSettings { Providers = { current } };
        Assert.False(CodexProviderEditorProtocol.ApplyDiscovery(settings, original, Result("late")));
        current.ApiKey = original.ApiKey;
        current.Catalog!.AutoSync = false;
        Assert.False(CodexProviderEditorProtocol.ApplyDiscovery(settings, original, Result("late")));
        Assert.DoesNotContain("late", current.Models);
    }

    [Fact]
    public void EditorPreservesNewerServerSnapshotAndDoesNotTrustPostedRemoteModels()
    {
        var current = Provider();
        var draft = CodexProviderEditorProtocol.ToPublicProfile(current);
        current.Catalog!.DiscoveredModels.Add(new() { Id = "newer" });
        current.Catalog.LastAttemptUtc = DateTime.UtcNow;
        draft["catalog"]!["discoveredModels"] = new JArray(new JObject { ["id"] = "unverified" });
        draft["catalog"]!["overrides"] = new JObject { ["manual"] = new JObject { ["displayName"] = "Renamed" } };
        var result = CodexProviderEditorProtocol.Read(draft, current);
        Assert.Contains("newer", result.Models);
        Assert.DoesNotContain("unverified", result.Models);
        Assert.Equal("Renamed", result.Catalog!.Overrides["manual"].DisplayName);
        Assert.Equal(current.ApiKey, result.ApiKey);
    }

    [Fact]
    public void ChangedEndpointDropsOldSnapshotButAcceptsBoundPreview()
    {
        var original = Provider();
        original.Catalog!.DiscoveredModels.Add(new() { Id = "old-account-model" });
        var draft = CodexProviderEditorProtocol.ToPublicProfile(original);
        draft["baseUrl"] = "https://other.example/v1";
        var withoutPreview = CodexProviderEditorProtocol.Read(draft, original);
        Assert.DoesNotContain("old-account-model", withoutPreview.Models);
        var withPreview = CodexProviderEditorProtocol.Read(draft, original, acceptedDiscovery: _ => Result("fresh"));
        Assert.Contains("fresh", withPreview.Models);
        Assert.DoesNotContain("old-account-model", withPreview.Models);
    }

    [Fact]
    public void AtomicDiscoveryMergesWithOtherInstancePreferencesAndServices()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExtensionSettingsStore(Path.Combine(directory.Path, "settings.json"));
        var original = Provider();
        store.Save(new CodexExtensionSettings { Providers = { original } }, updateProviders: true);
        var stale = store.Load();
        var latest = store.Load();
        latest.Providers[0].Catalog!.Overrides["manual"] = new() { ContextWindow = 999999 };
        latest.Providers.Add(Provider());
        store.Save(latest, updateProviders: true);

        Assert.True(store.UpdateProviderCatalogs(stale, s => CodexProviderEditorProtocol.ApplyDiscovery(s, original, Result("remote"))));
        var saved = store.Load();
        Assert.Equal(2, saved.Providers.Count);
        Assert.Equal(999999, saved.Providers[0].ContextWindows["manual"]);
        Assert.Contains("remote", saved.Providers[0].Models);
        Assert.DoesNotContain("synthetic", File.ReadAllText(Path.Combine(directory.Path, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void NewEmptyDiscoveryServiceCanBeSavedWithoutSelectingAnInvalidModel()
    {
        var draft = new JObject
        {
            ["name"] = "Empty", ["baseUrl"] = "https://fixture.example/v1", ["apiKey"] = "synthetic-secret",
            ["catalog"] = new JObject { ["autoSync"] = true }
        };
        var provider = CodexProviderEditorProtocol.Read(draft, null);
        Assert.Empty(provider.Models);
        Assert.True(provider.Catalog!.AutoSync);
    }

    internal static CodexProviderConfiguration Provider() => new()
    {
        Name = "Synthetic", BaseUrl = "https://fixture.example/v1", ApiKey = "synthetic-secret",
        Models = new() { "manual" },
        Catalog = new() { AutoSync = true, ManualModels = new() { "manual" } }
    };

    internal static CodexProviderDiscoveryResult Result(string model)
        => new(new[] { new CodexProviderModelMetadata { Id = model } }, "partial", null, DateTime.UtcNow);
}
