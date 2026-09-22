using System;
using System.IO;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderSettingsStoreTests
{
    [Fact]
    public void EntireProviderConfigurationIsEncryptedAndRoundTrips()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "settings.json");
        var store = new ExtensionSettingsStore(file);
        var settings = new CodexExtensionSettings();
        var provider = new CodexProviderConfiguration
        {
            Name = "Private synthetic provider",
            BaseUrl = "https://private-provider.example.test/v1",
            ApiKey = "synthetic-provider-secret",
            Models = { "private-model" }
        };
        provider.ReasoningEfforts["private-model"] = new() { "low", "high", "max" };
        provider.DefaultReasoningEfforts["private-model"] = "max";
        provider.ContextWindows["private-model"] = 1_048_576;
        settings.Providers.Add(provider);

        store.Save(settings);

        var json = File.ReadAllText(file);
        Assert.Null(JObject.Parse(json)["Providers"]);
        Assert.DoesNotContain(provider.Id, json, StringComparison.Ordinal);
        Assert.DoesNotContain(provider.Name, json, StringComparison.Ordinal);
        Assert.DoesNotContain(provider.BaseUrl, json, StringComparison.Ordinal);
        Assert.DoesNotContain(provider.ApiKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(provider.Models[0], json, StringComparison.Ordinal);
        var loaded = Assert.Single(store.Load().Providers);
        Assert.Equal(provider.Id, loaded.Id);
        Assert.Equal(provider.Name, loaded.Name);
        Assert.Equal(provider.BaseUrl, loaded.BaseUrl);
        Assert.Equal(provider.ApiKey, loaded.ApiKey);
        Assert.Equal(provider.Models, loaded.Models);
        Assert.Equal(provider.ReasoningEfforts["private-model"], loaded.ReasoningEfforts["private-model"]);
        Assert.Equal("max", loaded.DefaultReasoningEfforts["private-model"]);
        Assert.Equal(1_048_576, loaded.ContextWindows["private-model"]);
    }

    [Fact]
    public void LegacySettingsWithoutProvidersRemainCompatible()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(file, "{\"DefaultModel\":\"saved-model\"}");
        var store = new ExtensionSettingsStore(file);

        var settings = store.Load();

        Assert.Empty(settings.Providers);
        Assert.Equal("saved-model", settings.DefaultModel);
        store.Save(settings);
        Assert.Empty(store.Load().Providers);
    }

    [Fact]
    public void StaleHistorySaveCannotEraseProvidersFromAnotherInstance()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExtensionSettingsStore(Path.Combine(directory.Path, "settings.json"));
        store.Save(new CodexExtensionSettings());
        var stale = store.Load();
        var edited = store.Load();
        edited.Providers.Add(new CodexProviderConfiguration
        {
            Name = "Synthetic", BaseUrl = "https://fixture.example/v1", ApiKey = "synthetic-secret", Models = { "model" }
        });
        store.Save(edited, updateProviders: true);

        stale.PromptHistory.Add("unrelated history update");
        store.Save(stale);

        Assert.Single(store.Load().Providers);
        Assert.Contains("unrelated history update", store.Load().PromptHistory);
        edited.Providers.Clear();
        store.Save(edited, updateProviders: true);
        store.Save(stale);
        Assert.Empty(store.Load().Providers);
    }

    [Fact]
    public void NullProviderValuesNormalizeAndPlaintextProviderMigratesOnSave()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(file, "{\"Providers\":[null,{\"Id\":null,\"Name\":null,\"BaseUrl\":null,\"ApiKey\":null,\"Models\":null}]}");
        var store = new ExtensionSettingsStore(file);

        var settings = store.Load();
        var provider = Assert.Single(settings.Providers);
        Assert.Equal(string.Empty, provider.Id);
        Assert.Equal(string.Empty, provider.Name);
        Assert.Equal(string.Empty, provider.BaseUrl);
        Assert.Equal(string.Empty, provider.ApiKey);
        Assert.Empty(provider.Models);
        store.Save(settings);

        Assert.Null(JObject.Parse(File.ReadAllText(file))["Providers"]);
        Assert.Single(store.Load().Providers);
    }

    [Fact]
    public void NullProviderCollectionNormalizesAfterProtectedRoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var store = new ExtensionSettingsStore(Path.Combine(directory.Path, "settings.json"));
        store.Save(new CodexExtensionSettings { Providers = null! });

        Assert.Empty(store.Load().Providers);
    }

    [Fact]
    public void CorruptProtectedProvidersCannotBeReplacedByFallbackDefaults()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "settings.json");
        var store = new ExtensionSettingsStore(file);
        store.Save(new CodexExtensionSettings
        {
            Providers = { new CodexProviderConfiguration { Name = "Synthetic", ApiKey = "synthetic-secret" } }
        });
        var root = JObject.Parse(File.ReadAllText(file));
        root["ProtectedSensitiveSettings"] = "not-valid-base64";
        var original = root.ToString();
        File.WriteAllText(file, original);

        var defaults = store.Load();
        Assert.NotEmpty(store.LastLoadError);
        Assert.Empty(defaults.Providers);
        Assert.Throws<InvalidDataException>(() => store.Save(defaults));
        Assert.Equal(original, File.ReadAllText(file));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }
}
