using System;
using System.IO;
using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
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
        File.WriteAllText(Path.Combine(directory.Path, "config.toml"), config);
        var error = Assert.Throws<InvalidOperationException>(() => CodexProviderModelCatalogRuntime.Prepare(settings));
        Assert.Contains("model_catalog_json", error.Message);
        Assert.Equal(config, File.ReadAllText(Path.Combine(directory.Path, "config.toml")));
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
        Assert.Equal("model_catalog_json=" + CodexAppServerCommandLine.EncodeTomlString(path), arguments.Last());
        Assert.Equal(baseline, CodexAppServerCommandLine.Build(settings, null));
    }

    private static CodexExtensionSettings CreateSettings(string homeDirectory)
    {
        var provider = new CodexProviderConfiguration
        {
            Name = "Fixture", BaseUrl = "https://fixture.example.test/v1", ApiKey = "synthetic-secret",
            Models = { "fixture-model" }
        };
        provider.ContextWindows["fixture-model"] = 1_048_576;
        return new CodexExtensionSettings { EnvironmentVariables = "CODEX_HOME=" + homeDirectory, Providers = { provider } };
    }
}
