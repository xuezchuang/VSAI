using System;
using System.IO;
using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderModelCatalogFileTests
{
    [Fact]
    public void NoOverridesAvoidsCatalogValidationAndFileCreation()
    {
        using var temp = new TemporaryDirectory();
        var destination = Path.Combine(temp.Path, "must-not-exist");
        var settings = new CodexExtensionSettings();
        Assert.False(CodexProviderModelCatalogFile.HasOverrides(settings));
        Assert.Null(CodexProviderModelCatalogFile.Merge(new JObject(), settings));
        Assert.Null(CodexProviderModelCatalogFile.WriteImmutableCatalog(new JObject(), settings, destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void OriginalCatalogAndUnknownFieldsRemainUnchanged()
    {
        var baseline = Baseline();
        var original = baseline.DeepClone();
        var settings = Settings();
        var merged = CodexProviderModelCatalogFile.Merge(baseline, settings)!;

        Assert.True(JToken.DeepEquals(original, baseline));
        foreach (var property in baseline.Properties().Where(p => p.Name != "models"))
            Assert.True(JToken.DeepEquals(property.Value, merged[property.Name]));
        Assert.True(JToken.DeepEquals(baseline["models"]![0], merged["models"]![0]));
        Assert.Equal(2, merged["models"]!.Count());
        merged["models"]![0]!["model_messages"]!["instructions"] = "changed output";
        Assert.True(JToken.DeepEquals(original, baseline));
    }

    [Fact]
    public void CustomModelUsesExplicitCapacityAndReasoningWithoutCredentialsOrInheritedInstructions()
    {
        var merged = CodexProviderModelCatalogFile.Merge(Baseline(), Settings())!;
        var model = merged["models"]!.Last!;
        Assert.Equal("custom-model", model["slug"]!.Value<string>());
        Assert.Equal(1_048_576L, model["context_window"]!.Value<long>());
        Assert.Equal(1_048_576L, model["max_context_window"]!.Value<long>());
        Assert.Equal(95, model["effective_context_window_percent"]!.Value<int>());
        Assert.Equal(new[] { "low", "high", "max" }, model["supported_reasoning_levels"]!.Select(x => x["effort"]!.Value<string>()));
        Assert.Equal("max", model["default_reasoning_level"]!.Value<string>());
        Assert.Equal("You are a coding assistant.", model["base_instructions"]!.Value<string>());
        Assert.DoesNotContain("synthetic-secret", merged.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic original instructions", model.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialSlugConflictCannotOverwriteOriginalModel()
    {
        var baseline = Baseline();
        var original = baseline.DeepClone();
        var settings = Settings("official-model");
        Assert.Throws<InvalidDataException>(() => CodexProviderModelCatalogFile.Merge(baseline, settings));
        Assert.True(JToken.DeepEquals(original, baseline));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CrossProviderCollisionRejectsMissingDifferentCapacityOrDifferentEfforts(bool hasCapacity, bool sameCapacity)
    {
        var settings = Settings();
        var other = Provider("custom-model");
        other.ContextWindows.Clear();
        if (hasCapacity) other.ContextWindows["custom-model"] = sameCapacity ? 1_048_576 : 524_288;
        if (sameCapacity) other.ReasoningEfforts["custom-model"] = new() { "low", "high" };
        settings.Providers.Add(other);
        Assert.Throws<InvalidDataException>(() => CodexProviderModelCatalogFile.Merge(Baseline(), settings));
    }

    [Fact]
    public void MatchingCapabilitiesAcrossProvidersProduceOnlyOneEntry()
    {
        var settings = Settings();
        settings.Providers.Add(Provider("custom-model"));
        var merged = CodexProviderModelCatalogFile.Merge(Baseline(), settings)!;
        Assert.Equal(2, merged["models"]!.Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidCapacityFailsBeforeAnyFileIsCreated(long capacity)
    {
        using var temp = new TemporaryDirectory();
        var settings = Settings();
        settings.Providers[0].ContextWindows["custom-model"] = capacity;
        Assert.Throws<InvalidDataException>(() => CodexProviderModelCatalogFile.WriteImmutableCatalog(Baseline(), settings, temp.Path));
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public void RemovedModelMetadataAndMalformedBaselinesAreRejected()
    {
        var settings = Settings();
        settings.Providers[0].Models.Clear();
        Assert.Throws<InvalidDataException>(() => CodexProviderModelCatalogFile.Merge(Baseline(), settings));
        settings = Settings();
        Assert.Throws<InvalidDataException>(() => CodexProviderModelCatalogFile.Merge(new JObject(), settings));
        var duplicate = Baseline();
        ((JArray)duplicate["models"]!).Add(duplicate["models"]![0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => CodexProviderModelCatalogFile.Merge(duplicate, settings));
    }

    [Fact]
    public void ImmutableFilesAreReusedAndChangesCreateNewContentAddressedFiles()
    {
        using var temp = new TemporaryDirectory();
        var settings = Settings();
        var baseline = Baseline();
        var first = CodexProviderModelCatalogFile.WriteImmutableCatalog(baseline, settings, temp.Path)!;
        var originalBytes = File.ReadAllBytes(first);
        Assert.Equal(first, CodexProviderModelCatalogFile.WriteImmutableCatalog(baseline, settings, temp.Path));
        settings.Providers[0].ContextWindows["custom-model"] = 524_288;
        var second = CodexProviderModelCatalogFile.WriteImmutableCatalog(baseline, settings, temp.Path)!;
        Assert.NotEqual(first, second);
        Assert.Equal(originalBytes, File.ReadAllBytes(first));
        Assert.Equal(2, Directory.EnumerateFiles(temp.Path, "models-*.json").Count());
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void ModifiedHashNamedFileIsRejectedWithoutOverwritingIt()
    {
        using var temp = new TemporaryDirectory();
        var settings = Settings();
        var first = CodexProviderModelCatalogFile.WriteImmutableCatalog(Baseline(), settings, temp.Path)!;
        File.WriteAllText(first, "modified synthetic file");
        Assert.Throws<InvalidDataException>(() => CodexProviderModelCatalogFile.WriteImmutableCatalog(Baseline(), settings, temp.Path));
        Assert.Equal("modified synthetic file", File.ReadAllText(first));
    }

    [Fact]
    public void RuntimeFileExcludesCacheIdentityAndIsStableAcrossCacheRefreshes()
    {
        using var temp = new TemporaryDirectory();
        var baseline = Baseline();
        var first = CodexProviderModelCatalogFile.WriteImmutableCatalog(baseline, Settings(), temp.Path)!;
        var written = JObject.Parse(File.ReadAllText(first));
        Assert.Equal("models", Assert.Single(written.Properties()).Name);
        Assert.True(JToken.DeepEquals(baseline["models"]![0], written["models"]![0]));
        baseline["identity"] = new JObject { ["synthetic"] = "changed" };
        baseline["fetched_at"] = "new-time";
        baseline["etag"] = "new-etag";
        Assert.Equal(first, CodexProviderModelCatalogFile.WriteImmutableCatalog(baseline, Settings(), temp.Path));
        Assert.Single(Directory.EnumerateFiles(temp.Path));
    }

    private static CodexExtensionSettings Settings(string model = "custom-model")
        => new() { Providers = { Provider(model) } };

    private static CodexProviderConfiguration Provider(string model)
        => new()
        {
            Name = "Synthetic provider", ApiKey = "synthetic-secret", Models = { model },
            ContextWindows = { [model] = 1_048_576 },
            ReasoningEfforts = { [model] = new() { "low", "high", "max" } },
            DefaultReasoningEfforts = { [model] = "max" }
        };

    private static JObject Baseline() => JObject.Parse(@"{
        'fetched_at':'synthetic-time','etag':'synthetic-etag','client_version':'synthetic-version',
        'identity':{'synthetic':true},'future_root_field':{'enabled':true},
        'models':[{'slug':'official-model','context_window':272000,
            'model_messages':{'instructions':'Synthetic original instructions'},
            'future_model_field':{'values':[1,2,3]}}]
    }");
}
