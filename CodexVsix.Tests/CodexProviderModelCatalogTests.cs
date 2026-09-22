using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderModelCatalogTests
{
    [Fact]
    public void CustomModelsOfferReasoningWithoutSubscriptionSpeedTiers()
    {
        var provider = new CodexProviderConfiguration { Name = "Synthetic", Models = { "model-a" } };
        var settings = new CodexExtensionSettings { Providers = { provider } };
        var official = new JObject
        {
            ["model"] = "official-model", ["supportedReasoningEfforts"] = new JArray(new JObject { ["reasoningEffort"] = "high" }),
            ["defaultReasoningEffort"] = "high", ["serviceTiers"] = new JArray("priority")
        };
        var original = official.DeepClone();

        var result = CodexProviderModelCatalog.Merge(settings, new JObject { ["data"] = new JArray(official) }, true)!;
        var custom = result["data"]!.Last!;

        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" },
            custom["supportedReasoningEfforts"]!.Select(e => e["reasoningEffort"]!.Value<string>()));
        Assert.Equal("medium", custom["defaultReasoningEffort"]!.Value<string>());
        Assert.Empty(custom["serviceTiers"]!);
        Assert.Empty(custom["additionalSpeedTiers"]!);
        Assert.Equal(JTokenType.Null, custom["defaultServiceTier"]!.Type);
        Assert.True(JToken.DeepEquals(original, result["data"]!.First));
        Assert.True(JToken.DeepEquals(original, official));
    }

    [Fact]
    public void ImportedEffortsAreSpecificToProviderAndExactModel()
    {
        var first = new CodexProviderConfiguration { Name = "One", Models = { "shared-model" } };
        first.ReasoningEfforts["shared-model"] = new() { "low", "high", "max" };
        first.DefaultReasoningEfforts["shared-model"] = "max";
        var second = new CodexProviderConfiguration { Name = "Two", Models = { "shared-model" } };
        var settings = new CodexExtensionSettings { Providers = { first, second } };

        var result = CodexProviderModelCatalog.Merge(settings, new JObject(), true)!;
        var models = (JArray)result["data"]!;

        Assert.Equal(new[] { "low", "high", "max" }, models[0]["supportedReasoningEfforts"]!
            .Select(e => e["reasoningEffort"]!.Value<string>()));
        Assert.Equal("max", models[0]["defaultReasoningEffort"]!.Value<string>());
        Assert.Equal(5, models[1]["supportedReasoningEfforts"]!.Count());
        Assert.Equal("medium", models[1]["defaultReasoningEffort"]!.Value<string>());
    }

    [Fact]
    public void ExecutionCatalogModelIsExposedOnlyThroughItsProviderAlias()
    {
        var provider = new CodexProviderConfiguration { Name = "Synthetic", Models = { "custom-model" } };
        provider.ContextWindows["custom-model"] = 1_048_576;
        var raw = new JObject
        {
            ["data"] = new JArray(new JObject { ["model"] = "official-model" }, new JObject { ["model"] = "custom-model" })
        };
        var result = CodexProviderModelCatalog.Merge(new CodexExtensionSettings { Providers = { provider } }, raw, true)!;

        Assert.Equal(new[] { "official-model", CodexProviderModelCatalog.Alias(provider, "custom-model") },
            result["data"]!.Select(model => model["model"]!.Value<string>()));
        Assert.Equal("custom-model", raw["data"]![1]!["model"]!.Value<string>());
    }

    [Fact]
    public void InvalidImportedDefaultFallsBackToOneOfTheAvailableEfforts()
    {
        var provider = new CodexProviderConfiguration { Name = "Synthetic", Models = { "model-a" } };
        provider.ReasoningEfforts["model-a"] = new() { "high", "high", "invalid" };
        provider.DefaultReasoningEfforts["model-a"] = "medium";

        var result = CodexProviderModelCatalog.Merge(new CodexExtensionSettings { Providers = { provider } }, null, true)!;
        var model = result["data"]![0]!;

        Assert.Single(model["supportedReasoningEfforts"]!);
        Assert.Equal("high", model["defaultReasoningEffort"]!.Value<string>());
    }
}
