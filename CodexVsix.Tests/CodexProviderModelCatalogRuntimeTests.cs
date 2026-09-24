using System;
using System.IO;
using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderModelCatalogRuntimeTests
{
    [Fact]
    public void NoCapacityOverridesDoNotReadOrCreateCatalogFiles()
    {
        using var directory = new TemporaryDirectory();
        var settings = new CodexExtensionSettings { EnvironmentVariables = "CODEX_HOME=" + directory.Path };
        var output = Path.Combine(directory.Path, "generated");
        Assert.Null(CodexProviderModelCatalogRuntime.ReadSnapshot(settings));
        Assert.Null(CodexProviderModelCatalogRuntime.Prepare(settings, output));
        Assert.Equal(string.Empty, CodexProviderModelCatalogRuntime.GetSettingsKey(settings));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("model_catalog_json = 'existing.json'")]
    [InlineData("[profiles.custom]\nmodel_catalog_json = 'existing.json'")]
    public void ExistingUserCatalogIsNeverSilentlyReplaced(string config)
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "vsai", "config.toml"), config);
        var error = Assert.Throws<InvalidOperationException>(() => CodexProviderModelCatalogRuntime.Prepare(settings));
        Assert.Contains("model_catalog_json", error.Message);
        Assert.Equal(config, File.ReadAllText(Path.Combine(directory.Path, "vsai", "config.toml")));
    }

    [Fact]
    public void CapacityChangeInvalidatesServerConfigurationWithoutIncludingCredentials()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        var before = CodexProviderModelCatalogRuntime.GetSettingsKey(settings);
        settings.Providers[0].ContextWindows["fixture-model"] = 2_000_000;
        var after = CodexProviderModelCatalogRuntime.GetSettingsKey(settings);
        Assert.NotEqual(before, after);
        Assert.DoesNotContain("synthetic-secret", after);
    }

    [Fact]
    public void StartupArgumentsQuoteCatalogPathAndPreserveDefaultArguments()
    {
        var settings = new CodexExtensionSettings();
        var baseline = CodexAppServerCommandLine.Build(settings);
        var path = @"C:\fixture folder\模型目录.json";
        var arguments = CodexAppServerCommandLine.SplitArguments(CodexAppServerCommandLine.Build(settings, path));
        Assert.Equal("model_catalog_json=" + CodexAppServerCommandLine.EncodeTomlString(path),
            arguments.Last(arg => arg.StartsWith("model_catalog_json=", StringComparison.Ordinal)));
        Assert.Equal(baseline, CodexAppServerCommandLine.Build(settings, null));
    }

    [Fact]
    public void AddedOfficialModelChangesSnapshotKeyAndGeneratedCatalogKeepsCustomCapacity()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        var output = Path.Combine(directory.Path, "generated");
        WriteCache(directory.Path, "official-model");

        var first = CodexProviderModelCatalogRuntime.ReadSnapshot(settings)!;
        var firstCatalog = CodexProviderModelCatalogRuntime.PrepareFromSnapshot(settings, first, output)!;

        WriteCache(directory.Path, "official-model", "gpt-6-luna");
        var second = CodexProviderModelCatalogRuntime.ReadSnapshot(settings)!;
        var secondCatalog = CodexProviderModelCatalogRuntime.PrepareFromSnapshot(settings, second, output)!;
        var models = (JArray)JObject.Parse(File.ReadAllText(secondCatalog))["models"]!;

        Assert.NotEqual(first.Key, second.Key);
        Assert.NotEqual(firstCatalog, secondCatalog);
        Assert.Contains(models, model => model["slug"]!.Value<string>() == "gpt-6-luna");
        var custom = models.Single(model => model["slug"]!.Value<string>() == "fixture-model");
        Assert.Equal(1_048_576L, custom["context_window"]!.Value<long>());
    }

    [Fact]
    public void SnapshotKeyIgnoresCacheRootMetadata()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        WriteCache(directory.Path, "official-model");
        var first = CodexProviderModelCatalogRuntime.ReadSnapshot(settings)!;

        WriteCache(directory.Path, new[] { "official-model" }, fetchedAt: "2026-01-02T00:00:00Z", etag: "new-etag", clientVersion: "999.0.0", accountMarker: "changed-account");
        var second = CodexProviderModelCatalogRuntime.ReadSnapshot(settings)!;

        Assert.Equal(first.Key, second.Key);
        Assert.Single(second.Baseline.Properties());
        Assert.NotNull(second.Baseline["models"]);
    }

    [Fact]
    public void PrepareFromHeldSnapshotDoesNotRereadChangedCache()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);
        WriteCache(directory.Path, "official-model");
        var snapshot = CodexProviderModelCatalogRuntime.ReadSnapshot(settings)!;

        WriteCache(directory.Path, "official-model", "gpt-6-sol");
        var catalog = CodexProviderModelCatalogRuntime.PrepareFromSnapshot(settings, snapshot, Path.Combine(directory.Path, "generated"))!;
        var models = (JArray)JObject.Parse(File.ReadAllText(catalog))["models"]!;

        Assert.DoesNotContain(models, model => model["slug"]!.Value<string>() == "gpt-6-sol");
        Assert.Contains(models, model => model["slug"]!.Value<string>() == "fixture-model");
    }

    [Fact]
    public void MissingOrPartialCacheFailsWhenOverridesRequireSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateSettings(directory.Path);

        var missing = Assert.Throws<InvalidOperationException>(() => CodexProviderModelCatalogRuntime.ReadSnapshot(settings));
        Assert.Contains("模型目录缓存", missing.Message);

        File.WriteAllText(Path.Combine(directory.Path, "vsai", "models_cache.json"), "{\"models\":[");
        Assert.Throws<JsonReaderException>(() => CodexProviderModelCatalogRuntime.ReadSnapshot(settings));
    }

    [Fact]
    public void AutomaticCatalogWithoutUsableNativeBaselineKeepsNativeCliBehavior()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateAutomaticSettings(directory.Path);
        var output = Path.Combine(directory.Path, "generated");

        Assert.Null(CodexProviderModelCatalogRuntime.ReadSnapshot(settings));
        Assert.Null(CodexProviderModelCatalogRuntime.Prepare(settings, output));
        Assert.False(Directory.Exists(output));

        File.WriteAllText(Path.Combine(directory.Path, "vsai", "models_cache.json"), "{not-json");
        Assert.Null(CodexProviderModelCatalogRuntime.ReadSnapshot(settings));
    }

    [Fact]
    public void AutomaticCatalogWarningsExposeRuntimeBoundariesWithoutCredentials()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateAutomaticSettings(directory.Path);
        var withoutBaseline = CodexProviderModelCatalogRuntime.GetRuntimeWarnings(settings);
        Assert.Contains("max-output-tokens-metadata-only", withoutBaseline);
        Assert.Contains("runtime-catalog-baseline-unavailable", withoutBaseline);

        WriteCache(directory.Path, "automatic-model");
        var snapshot = CodexProviderModelCatalogRuntime.ReadSnapshot(settings)!;
        var withBaseline = CodexProviderModelCatalogRuntime.GetRuntimeWarnings(settings, snapshot);
        Assert.Contains("runtime-metadata-native-id-collision", withBaseline);
        Assert.DoesNotContain("synthetic-secret", string.Join(";", withBaseline));
    }

    [Fact]
    public void ExistingUserCatalogMakesAutomaticRuntimeEnrichmentAQuietNoOp()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateAutomaticSettings(directory.Path);
        WriteCache(directory.Path, "official-model");
        var config = "model_catalog_json = 'user-owned.json'";
        File.WriteAllText(Path.Combine(directory.Path, "vsai", "config.toml"), config);

        Assert.Null(CodexProviderModelCatalogRuntime.Prepare(settings));
        Assert.Contains("runtime-catalog-user-assignment",
            CodexProviderModelCatalogRuntime.GetRuntimeWarnings(settings));
        Assert.Equal(config, File.ReadAllText(Path.Combine(directory.Path, "vsai", "config.toml")));
    }

    private static CodexExtensionSettings CreateSettings(string homeDirectory)
    {
        Directory.CreateDirectory(Path.Combine(homeDirectory, "vsai"));
        var provider = new CodexProviderConfiguration
        {
            Name = "Fixture", BaseUrl = "https://fixture.example.test/v1", ApiKey = "synthetic-secret",
            Models = { "fixture-model" }
        };
        provider.ContextWindows["fixture-model"] = 1_048_576;
        return new CodexExtensionSettings { EnvironmentVariables = "CODEX_HOME=" + homeDirectory, Providers = { provider } };
    }

    private static CodexExtensionSettings CreateAutomaticSettings(string homeDirectory)
    {
        Directory.CreateDirectory(Path.Combine(homeDirectory, "vsai"));
        var provider = new CodexProviderConfiguration
        {
            Name = "Automatic", BaseUrl = "https://fixture.example.test/v1", ApiKey = "synthetic-secret",
            Catalog = new CodexProviderCatalogConfiguration
            {
                DiscoveredModels =
                {
                    new CodexProviderModelMetadata
                    {
                        Id = "automatic-model", ContextWindow = 400_000, MaxOutputTokens = 64_000,
                        SupportsTools = true, SupportsResponses = true
                    }
                }
            }
        };
        return new CodexExtensionSettings { EnvironmentVariables = "CODEX_HOME=" + homeDirectory, Providers = { provider } };
    }

    private static void WriteCache(
        string homeDirectory,
        params string[] models)
    {
        WriteCache(homeDirectory, models, "2026-01-01T00:00:00Z", "old-etag", "1.0.0", "original-account");
    }

    private static void WriteCache(
        string homeDirectory,
        string[] models,
        string fetchedAt,
        string etag,
        string clientVersion,
        string accountMarker)
    {
        var cache = new JObject
        {
            ["fetched_at"] = fetchedAt,
            ["etag"] = etag,
            ["client_version"] = clientVersion,
            ["account_marker"] = accountMarker,
            ["models"] = new JArray(models.Select(model => new JObject { ["slug"] = model }))
        };
        Directory.CreateDirectory(Path.Combine(homeDirectory, "vsai"));
        File.WriteAllText(Path.Combine(homeDirectory, "vsai", "models_cache.json"), cache.ToString(Formatting.None));
    }
}
