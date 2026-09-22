using System;
using System.IO;
using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class ExtensionSettingsStoreTests
{
    [Fact]
    public void DefaultMutexNameIsStableAcrossPathCasingAndDistinctAcrossFiles()
    {
        using var directory = new TemporaryDirectory();
        var firstPath = Path.Combine(directory.Path, "settings.json");
        var samePathDifferentCasing = firstPath.ToUpperInvariant();
        var secondPath = Path.Combine(directory.Path, "other-settings.json");

        var first = ExtensionSettingsStore.BuildSettingsMutexName(firstPath);
        var same = ExtensionSettingsStore.BuildSettingsMutexName(samePathDifferentCasing);
        var second = ExtensionSettingsStore.BuildSettingsMutexName(secondPath);

        Assert.Equal(first, same);
        Assert.NotEqual(first, second);
        Assert.StartsWith(@"Local\CodexVsix.Settings.", first, StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveValuesAreProtectedAndRoundTripForCurrentWindowsUser()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        var store = new ExtensionSettingsStore(file, "Local\\CodexVsix.Tests." + Guid.NewGuid().ToString("N"));
        var settings = new CodexExtensionSettings
        {
            EnvironmentVariables = "OPENAI_API_KEY=super-secret-value",
            RawTomlOverrides = "model_provider=\"private-provider\"",
            AdditionalArguments = "--secret-argument",
            PromptHistory = { "first" }
        };

        store.Save(settings);
        var storedJson = File.ReadAllText(file);

        Assert.DoesNotContain("super-secret-value", storedJson);
        Assert.DoesNotContain("private-provider", storedJson);
        Assert.DoesNotContain("--secret-argument", storedJson);
        Assert.DoesNotContain("first", storedJson);
        Assert.Contains("ProtectedSensitiveSettings", storedJson);

        var loaded = store.Load();
        Assert.Equal(settings.EnvironmentVariables, loaded.EnvironmentVariables);
        Assert.Equal(settings.RawTomlOverrides, loaded.RawTomlOverrides);
        Assert.Equal(settings.AdditionalArguments, loaded.AdditionalArguments);
        Assert.Equal(settings.PromptHistory, loaded.PromptHistory);
    }

    [Fact]
    public void SaveMergesPromptHistoryFromAnotherVisualStudioInstance()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        var mutex = "Local\\CodexVsix.Tests." + Guid.NewGuid().ToString("N");
        var firstStore = new ExtensionSettingsStore(file, mutex);
        var secondStore = new ExtensionSettingsStore(file, mutex);
        firstStore.Save(new CodexExtensionSettings { PromptHistory = { "from-first" } });

        secondStore.Save(new CodexExtensionSettings { PromptHistory = { "from-second" } });

        var history = firstStore.Load().PromptHistory;
        Assert.Contains("from-first", history);
        Assert.Contains("from-second", history);
    }

    [Fact]
    public void SaveCanExplicitlyClearPromptHistoryWithoutMergingItBackFromDisk()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        var store = new ExtensionSettingsStore(file, "Local\\CodexVsix.Tests." + Guid.NewGuid().ToString("N"));
        store.Save(new CodexExtensionSettings { PromptHistory = { "sensitive prompt" } });

        var settings = store.Load();
        settings.PromptHistory.Clear();
        store.Save(settings, mergePromptHistory: false);

        Assert.Empty(store.Load().PromptHistory);
        Assert.DoesNotContain("sensitive prompt", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void SaveKeepsReusedPromptRecentWhenHistoryIsFull()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        var store = new ExtensionSettingsStore(file);
        store.Save(new CodexExtensionSettings
        {
            PromptHistory = Enumerable.Range(0, 50).Select(index => "prompt-" + index).ToList()
        });
        var settings = store.Load();
        settings.PromptHistory.Remove("prompt-0");
        settings.PromptHistory.Add("prompt-0");
        settings.PromptHistory.RemoveAt(0);
        settings.PromptHistory.Add("new-prompt");

        store.Save(settings);

        var history = store.Load().PromptHistory;
        Assert.Equal(50, history.Count);
        Assert.Equal(new[] { "prompt-0", "new-prompt" }, history.Skip(48));
        Assert.DoesNotContain("prompt-1", history);
    }

    [Theory]
    [InlineData("not-base64", true)]
    [InlineData("not-base64", false)]
    [InlineData("YWJj", true)]
    [InlineData("YWJj", false)]
    public void SaveCannotOverwriteSettingsWhoseProtectedPayloadCannotBeRead(string payload, bool mergePromptHistory)
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        var original = "{\"DefaultModel\":\"saved-model\",\"ProtectedSensitiveSettings\":\"" + payload + "\"}";
        File.WriteAllText(file, original);
        var store = new ExtensionSettingsStore(file);
        var fallback = store.Load();
        Assert.False(string.IsNullOrWhiteSpace(store.LastLoadError));

        Assert.Throws<InvalidDataException>(() => store.Save(fallback, mergePromptHistory));

        Assert.Equal(original, File.ReadAllText(file));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
        Assert.False(File.Exists(Path.Combine(temp.Path, "settings.bak.json")));
    }

    [Fact]
    public void SaveCanReplaceMalformedJsonAndKeepsOriginalBackup()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(file, "{not-json");
        var store = new ExtensionSettingsStore(file);

        store.Save(new CodexExtensionSettings { DefaultModel = "recovered-model" });

        Assert.Equal("recovered-model", store.Load().DefaultModel);
        Assert.Equal("{not-json", File.ReadAllText(Path.Combine(temp.Path, "settings.bak.json")));
    }

    [Fact]
    public void LegacyPlaintextSensitiveSettingsLoadAndMigrateOnSave()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            file,
            "{\"EnvironmentVariables\":\"TOKEN=legacy-secret\",\"RawTomlOverrides\":\"provider=legacy\",\"AdditionalArguments\":\"--legacy\",\"PromptHistory\":[\"legacy prompt\"]}");
        var store = new ExtensionSettingsStore(file, "Local\\CodexVsix.Tests." + Guid.NewGuid().ToString("N"));

        var settings = store.Load();

        Assert.Equal("TOKEN=legacy-secret", settings.EnvironmentVariables);
        Assert.Equal(new[] { "legacy prompt" }, settings.PromptHistory);

        store.Save(settings);
        var migratedJson = File.ReadAllText(file);
        Assert.DoesNotContain("legacy-secret", migratedJson);
        Assert.DoesNotContain("legacy prompt", migratedJson);
        Assert.Contains("ProtectedSensitiveSettings", migratedJson);
    }

    [Fact]
    public void CorruptFileIsPreservedAndReported()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(file, "{not-json");
        var store = new ExtensionSettingsStore(file, "Local\\CodexVsix.Tests." + Guid.NewGuid().ToString("N"));

        var settings = store.Load();

        Assert.NotNull(settings);
        Assert.False(string.IsNullOrWhiteSpace(store.LastLoadError));
        Assert.Single(Directory.EnumerateFiles(temp.Path, "settings.corrupt.*.json"));
    }

    [Fact]
    public void DiagnosticLoggingIsOptInAndPersistsWhenExplicitlyEnabled()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "settings.json");
        var store = new ExtensionSettingsStore(
            file,
            "Local\\CodexVsix.Tests." + Guid.NewGuid().ToString("N"));

        Assert.False(store.Load().EnableDiagnosticLogging);

        store.Save(new CodexExtensionSettings { EnableDiagnosticLogging = true });

        Assert.True(store.Load().EnableDiagnosticLogging);
    }
}
