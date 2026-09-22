using System;
using System.Diagnostics;
using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderConfigurationServiceTests
{
    [Fact]
    public void ManagedProviderArgumentsTakePrecedenceWithoutPuttingKeysOnCommandLine()
    {
        var provider = CreateProvider();
        var prefix = "model_providers." + CodexProviderConfigurationService.GetProviderId(provider) + ".env_key=";
        var settings = new CodexExtensionSettings
        {
            Providers = { provider },
            AdditionalArguments = CodexAppServerCommandLine.JoinArguments(new[]
            {
                "-c", "model_provider=\"legacy\"", "-c", prefix + "\"WRONG_KEY\""
            })
        };

        var command = CodexAppServerCommandLine.Build(settings);
        var arguments = CodexAppServerCommandLine.SplitArguments(command).ToArray();

        Assert.Equal("model_provider=\"openai\"", arguments.Last());
        Assert.Equal(prefix + "\"" + CodexProviderConfigurationService.GetEnvironmentKey(provider) + "\"",
            arguments.Last(arg => arg.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.DoesNotContain(provider.ApiKey, command, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDefinitionsEscapeTomlAndNeverSelectGlobalProviderOrExposeKey()
    {
        var provider = CreateProvider();
        provider.Name = "Example \"quoted\" \\ provider";
        var overrides = CodexProviderConfigurationService.BuildConfigOverrides(provider);
        var prefix = "model_providers.vsai_" + provider.Id + ".";

        Assert.Equal(5, overrides.Count);
        Assert.All(overrides, value => Assert.StartsWith(prefix, value, StringComparison.Ordinal));
        Assert.Contains(prefix + "name=\"Example \\\"quoted\\\" \\\\ provider\"", overrides);
        Assert.Contains(prefix + "wire_api=\"responses\"", overrides);
        Assert.Contains(prefix + "requires_openai_auth=false", overrides);
        Assert.Contains(prefix + "env_key=\"VSAI_PROVIDER_" + provider.Id.ToUpperInvariant() + "_API_KEY\"", overrides);
        Assert.DoesNotContain(provider.ApiKey, string.Join(" ", overrides), StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInjectionUsesIndependentKeysAndPreservesOtherEnvironmentAndArguments()
    {
        var first = CreateProvider();
        var second = CreateProvider();
        second.ApiKey = "other-synthetic-secret";
        var startInfo = new ProcessStartInfo { UseShellExecute = false, Arguments = "app-server" };
        startInfo.EnvironmentVariables.Clear();
        startInfo.EnvironmentVariables["TEST_SENTINEL"] = "preserved";

        CodexProviderConfigurationService.ApplyEnvironment(startInfo, first);
        Assert.Equal(2, startInfo.EnvironmentVariables.Count);
        Assert.Equal(first.ApiKey, startInfo.EnvironmentVariables[CodexProviderConfigurationService.GetEnvironmentKey(first)]);
        Assert.False(startInfo.EnvironmentVariables.ContainsKey(CodexProviderConfigurationService.GetEnvironmentKey(second)));

        CodexProviderConfigurationService.ApplyEnvironment(startInfo, second);
        Assert.Equal(3, startInfo.EnvironmentVariables.Count);
        Assert.Equal(second.ApiKey, startInfo.EnvironmentVariables[CodexProviderConfigurationService.GetEnvironmentKey(second)]);
        Assert.Equal("preserved", startInfo.EnvironmentVariables["TEST_SENTINEL"]);
        Assert.Equal("app-server", startInfo.Arguments);
    }

    [Theory]
    [InlineData("https://example.test/v1")]
    [InlineData("http://example.test/v1")]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://[::1]:8080/v1")]
    [InlineData("http://localhost:8080/v1")]
    public void ValidEndpointsAndExactModelNamesAreRetained(string url)
    {
        var provider = CreateProvider();
        provider.BaseUrl = url;
        provider.Models.Clear();
        provider.Models.Add("vendor/Model:latest+preview #1");

        CodexProviderConfigurationService.Validate(provider);

        Assert.Equal(url, provider.BaseUrl);
        Assert.Equal("vendor/Model:latest+preview #1", Assert.Single(provider.Models));
    }

    [Theory]
    [InlineData("https://user:secret@example.test/v1")]
    [InlineData("https://example.test/v1?key=secret")]
    [InlineData("https://example.test/v1?")]
    [InlineData("https://example.test/v1#secret")]
    [InlineData("https://example.test/v1#")]
    [InlineData("https://example.test/\nv1")]
    [InlineData("file:///C:/private")]
    [InlineData("/relative/v1")]
    [InlineData(" https://example.test/v1")]
    public void InvalidEndpointsFailWithoutEchoingSensitiveInput(string url)
    {
        var provider = CreateProvider();
        provider.BaseUrl = url;

        var error = Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.Validate(provider));

        Assert.DoesNotContain(url, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(provider.ApiKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("abcdef0123456789abcdef012345678g")]
    [InlineData("abcdef01-2345-6789-abcd-ef0123456789")]
    [InlineData("x\";model_provider=\"attacker")]
    public void InvalidIdentitiesCannotEnterTomlOrEnvironmentKeys(string id)
    {
        var provider = CreateProvider();
        provider.Id = id;

        Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.Validate(provider));
        Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.GetProviderId(provider));
        Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.GetEnvironmentKey(provider));
    }

    [Fact]
    public void ModelListAndNameLimitsAreValidatedWithoutTrimmingIdentifiers()
    {
        var provider = CreateProvider();
        provider.Name = new string('n', 81);
        Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.Validate(provider));
        provider.Name = "provider";
        provider.Models.Clear();
        Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.Validate(provider));
        provider.Models.Add(new string('m', 201));
        Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.Validate(provider));
        provider.Models[0] = "model\ninvalid";
        Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.Validate(provider));
        provider.Models = Enumerable.Repeat("model", 51).ToList();
        Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.Validate(provider));
        provider.Models = Enumerable.Repeat("model", 50).ToList();
        CodexProviderConfigurationService.Validate(provider);
    }

    [Fact]
    public void OptionalKeyValidationAllowsEditorToReuseStoredKeyButLaunchRequiresIt()
    {
        var provider = CreateProvider();
        provider.ApiKey = string.Empty;
        CodexProviderConfigurationService.Validate(provider, requireApiKey: false);
        Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.BuildConfigOverrides(provider));
        provider.ApiKey = "synthetic\nsecret";
        var error = Assert.Throws<ArgumentException>(() => CodexProviderConfigurationService.Validate(provider, requireApiKey: false));
        Assert.DoesNotContain(provider.ApiKey, error.Message, StringComparison.Ordinal);
    }

    private static CodexProviderConfiguration CreateProvider()
    {
        return new CodexProviderConfiguration
        {
            Name = "Synthetic provider",
            BaseUrl = "https://example.test/v1",
            ApiKey = "synthetic-provider-test-secret",
            Models = { "model-one" }
        };
    }
}
