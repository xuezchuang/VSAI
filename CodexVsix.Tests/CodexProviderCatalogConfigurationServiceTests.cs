using System;
using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderCatalogConfigurationServiceTests
{
    [Fact]
    public void EnsureCatalogMigratesLegacyFieldsOnceWithoutEnablingAutoSync()
    {
        var provider = new CodexProviderConfiguration { Models = { "model-a" } };
        provider.ContextWindows["model-a"] = 128_000;
        provider.ReasoningEfforts["model-a"] = new() { "low", "high" };
        provider.DefaultReasoningEfforts["model-a"] = "high";

        var catalog = CodexProviderCatalogConfigurationService.EnsureCatalog(provider);
        provider.Models.Add("later-legacy-model");

        Assert.False(catalog.AutoSync);
        Assert.Equal("auto", catalog.Source);
        Assert.Equal(new[] { "model-a" }, catalog.ManualModels);
        Assert.Equal(128_000, catalog.Overrides["model-a"].ContextWindow);
        Assert.Equal(new[] { "low", "high" }, catalog.Overrides["model-a"].ReasoningEfforts);
        Assert.Same(catalog, CodexProviderCatalogConfigurationService.EnsureCatalog(provider));
        Assert.DoesNotContain("later-legacy-model", catalog.ManualModels);
    }

    [Fact]
    public void LegacyRuntimeReadUsesMigrationViewWithoutWritingCatalog()
    {
        var provider = new CodexProviderConfiguration { Models = { "legacy" } };

        var models = CodexProviderCatalogConfigurationService.GetEffectiveModels(provider);

        Assert.Equal("legacy", Assert.Single(models).Id);
        Assert.Null(provider.Catalog);
    }

    [Fact]
    public void EffectiveModelsMergeExactIdsApplyFieldOverridesAndProjectLegacyProperties()
    {
        var provider = new CodexProviderConfiguration
        {
            Catalog = new CodexProviderCatalogConfiguration
            {
                DiscoveredModels =
                {
                    new CodexProviderModelMetadata
                    {
                        Id = "Model-A", DisplayName = "Gateway model", ContextWindow = 32_000,
                        SupportsImages = false, ReasoningEfforts = new() { "low", "high" }, DefaultReasoningEffort = "low"
                    }
                },
                ManualModels = { "model-a", "manual" },
                Overrides =
                {
                    ["Model-A"] = new CodexProviderModelMetadata { ContextWindow = 64_000, DefaultReasoningEffort = "high" },
                    ["manual"] = new CodexProviderModelMetadata { SupportsTools = false }
                },
                HiddenModels = { "model-a" }
            }
        };

        var models = CodexProviderCatalogConfigurationService.GetEffectiveModels(provider);
        CodexProviderCatalogConfigurationService.ApplyEffectiveProperties(provider);

        Assert.Equal(new[] { "Model-A", "manual" }, models.Select(model => model.Id));
        Assert.Equal(64_000, models[0].ContextWindow);
        Assert.Equal(false, models[0].SupportsImages);
        Assert.Equal("high", models[0].DefaultReasoningEffort);
        Assert.Null(models[1].ReasoningEfforts);
        Assert.Equal(false, models[1].SupportsTools);
        Assert.Equal(new[] { "Model-A", "manual" }, provider.Models);
        Assert.Equal(64_000, provider.ContextWindows["Model-A"]);
        Assert.Equal(new[] { "low", "high" }, provider.ReasoningEfforts["Model-A"]);
        Assert.Equal("high", provider.DefaultReasoningEfforts["Model-A"]);
    }

    [Fact]
    public void CatalogValidationRejectsInvalidLimitsAndDefaultEffort()
    {
        var negativeLimit = new CodexProviderCatalogConfiguration
        {
            DiscoveredModels = { new CodexProviderModelMetadata { Id = "model", ContextWindow = 0 } }
        };
        var invalidDefault = new CodexProviderCatalogConfiguration
        {
            DiscoveredModels =
            {
                new CodexProviderModelMetadata { Id = "model", ReasoningEfforts = new() { "low" }, DefaultReasoningEffort = "high" }
            }
        };

        Assert.Throws<ArgumentException>(() => CodexProviderCatalogConfigurationService.ValidateCatalog(negativeLimit));
        Assert.Throws<ArgumentException>(() => CodexProviderCatalogConfigurationService.ValidateCatalog(invalidDefault));
    }

    [Fact]
    public void OrphanedOverridesDoNotRestoreModelsAndFalseReasoningClearsOldChoices()
    {
        var provider = new CodexProviderConfiguration
        {
            Catalog = new CodexProviderCatalogConfiguration
            {
                ManualModels = { "current" },
                Overrides =
                {
                    ["current"] = new CodexProviderModelMetadata { SupportsReasoning = false },
                    ["removed"] = new CodexProviderModelMetadata { ContextWindow = 32_000 }
                },
                DiscoveredModels =
                {
                    new CodexProviderModelMetadata
                    {
                        Id = "current", ReasoningEfforts = new() { "low" }, DefaultReasoningEffort = "low"
                    }
                }
            }
        };

        var model = Assert.Single(CodexProviderCatalogConfigurationService.GetEffectiveModels(provider));

        Assert.Equal("current", model.Id);
        Assert.Equal(false, model.SupportsReasoning);
        Assert.Empty(model.ReasoningEfforts!);
        Assert.Null(model.DefaultReasoningEffort);
    }
}
