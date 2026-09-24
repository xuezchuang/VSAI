using System;
using System.Collections.Generic;
using System.Linq;
using CodexVsix.Models;

namespace CodexVsix.Services;

/// <summary>Migrates and projects provider catalogs while keeping legacy consumers compatible.</summary>
public static class CodexProviderCatalogConfigurationService
{
    private static readonly HashSet<string> ValidSources = new(StringComparer.Ordinal)
    {
        "auto", "openai", "midas"
    };

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.Ordinal)
    {
        "never", "success", "partial", "error", "auth-error"
    };

    private static readonly HashSet<string> ValidReasoningEfforts = new(StringComparer.Ordinal)
    {
        "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"
    };

    /// <summary>
    /// Initializes a legacy provider exactly once. Existing model settings become manual
    /// entries and per-model legacy settings become explicit field overrides.
    /// </summary>
    public static CodexProviderCatalogConfiguration EnsureCatalog(CodexProviderConfiguration provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        if (provider.Catalog is not null) return provider.Catalog;

        var catalog = CreateLegacyCatalog(provider);
        provider.Catalog = catalog;
        return catalog;
    }

    /// <summary>Returns discovered and manual model ids with per-field overrides applied.</summary>
    public static IReadOnlyList<CodexProviderModelMetadata> GetEffectiveModels(CodexProviderConfiguration provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        // Runtime reads may inspect a legacy provider while another operation owns
        // persistence. Build the same migration view without changing that object.
        var catalog = provider.Catalog ?? CreateLegacyCatalog(provider);
        ValidateCatalog(catalog);
        var hidden = new HashSet<string>(catalog.HiddenModels, StringComparer.Ordinal);
        var models = new Dictionary<string, CodexProviderModelMetadata>(StringComparer.Ordinal);

        foreach (var discovered in catalog.DiscoveredModels)
        {
            if (discovered is not { Id: var discoveredId } || !IsValidId(discoveredId) || models.ContainsKey(discoveredId))
                continue;
            models.Add(discoveredId, Clone(discovered));
        }
        foreach (var id in DistinctValidIds(catalog.ManualModels))
        {
            if (!models.ContainsKey(id)) models.Add(id, new CodexProviderModelMetadata { Id = id });
        }
        foreach (var pair in catalog.Overrides)
        {
            if (!IsValidId(pair.Key) || pair.Value is null) continue;
            // Overrides express fields for a discovered or manual identity. They
            // intentionally remain stored when a remote identity disappears, but
            // cannot bring that identity back by themselves.
            if (!models.TryGetValue(pair.Key, out var existing)) continue;
            models[pair.Key] = ApplyOverride(existing, pair.Value, pair.Key);
        }

        if (models.Count > 1000)
            throw new ArgumentException("Catalog may contain at most 1000 effective models.");

        var effective = models.Values.Where(model => !hidden.Contains(model.Id)).ToArray();
        foreach (var model in effective) ValidateMetadata(model, requireId: true, validateDefaultEffort: true);
        return effective;
    }

    private static CodexProviderCatalogConfiguration CreateLegacyCatalog(CodexProviderConfiguration provider)
    {
        var catalog = new CodexProviderCatalogConfiguration { AutoSync = false };
        foreach (var id in DistinctValidIds(provider.Models))
        {
            catalog.ManualModels.Add(id);
            var metadata = new CodexProviderModelMetadata { Id = id };
            var hasOverride = false;
            if (provider.ContextWindows?.TryGetValue(id, out var contextWindow) == true && contextWindow > 0)
            {
                metadata.ContextWindow = contextWindow;
                hasOverride = true;
            }
            if (provider.ReasoningEfforts?.TryGetValue(id, out var efforts) == true && efforts is not null)
            {
                metadata.ReasoningEfforts = NormalizeEfforts(efforts).ToList();
                hasOverride = true;
            }
            if (provider.DefaultReasoningEfforts?.TryGetValue(id, out var defaultEffort) == true
                && metadata.ReasoningEfforts?.Contains(defaultEffort, StringComparer.Ordinal) == true)
            {
                metadata.DefaultReasoningEffort = defaultEffort;
                hasOverride = true;
            }
            if (hasOverride) catalog.Overrides[id] = metadata;
        }

        return catalog;
    }

    /// <summary>Projects the effective catalog into the legacy fields consumed by existing runtime code.</summary>
    public static void ApplyEffectiveProperties(CodexProviderConfiguration provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        var models = GetEffectiveModels(provider);
        provider.Models = models.Select(model => model.Id).ToList();
        provider.ContextWindows = new Dictionary<string, long>(StringComparer.Ordinal);
        provider.ReasoningEfforts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        provider.DefaultReasoningEfforts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var model in models)
        {
            if (model.ContextWindow is > 0) provider.ContextWindows.Add(model.Id, model.ContextWindow.Value);
            if (model.ReasoningEfforts is not null)
                provider.ReasoningEfforts.Add(model.Id, new List<string>(model.ReasoningEfforts));
            if (model.DefaultReasoningEffort is string defaultReasoningEffort && defaultReasoningEffort.Length > 0)
                provider.DefaultReasoningEfforts.Add(model.Id, defaultReasoningEffort);
        }
    }

    public static void ValidateCatalog(CodexProviderCatalogConfiguration catalog)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (!ValidSources.Contains(catalog.Source ?? string.Empty))
            throw new ArgumentException("Catalog source must be auto, openai, or midas.");
        if (!ValidStatuses.Contains(catalog.Status ?? string.Empty))
            throw new ArgumentException("Catalog status is invalid.");
        ValidateIds(catalog.ManualModels, "manual models");
        ValidateIds(catalog.HiddenModels, "hidden models");
        if (catalog.DiscoveredModels is null || catalog.DiscoveredModels.Count > 1000)
            throw new ArgumentException("Catalog may contain at most 1000 discovered models.");
        if (catalog.Overrides is null || catalog.Overrides.Count > 1000)
            throw new ArgumentException("Catalog may contain at most 1000 overrides.");
        foreach (var model in catalog.DiscoveredModels) ValidateMetadata(model, requireId: true, validateDefaultEffort: true);
        foreach (var pair in catalog.Overrides)
        {
            if (!IsValidId(pair.Key) || pair.Value is null) throw new ArgumentException("Catalog override is invalid.");
            ValidateMetadata(pair.Value, requireId: false, validateDefaultEffort: false);
        }
    }

    private static void ValidateIds(IReadOnlyCollection<string>? ids, string label)
    {
        if (ids is null || ids.Count > 1000 || ids.Any(id => !IsValidId(id)))
            throw new ArgumentException("Catalog " + label + " are invalid.");
    }

    private static void ValidateMetadata(CodexProviderModelMetadata metadata, bool requireId, bool validateDefaultEffort)
    {
        if (metadata is null || (requireId && !IsValidId(metadata.Id))) throw new ArgumentException("Catalog model id is invalid.");
        if (metadata.DisplayName is not null
            && (metadata.DisplayName.Length > 200 || metadata.DisplayName.Any(char.IsControl)))
            throw new ArgumentException("Catalog display name is invalid.");
        if (metadata.ContextWindow is <= 0 || metadata.MaxOutputTokens is <= 0)
            throw new ArgumentException("Catalog token limits must be positive.");
        if (metadata.ReasoningEfforts is not null)
        {
            if (metadata.ReasoningEfforts.Count > ValidReasoningEfforts.Count
                || metadata.ReasoningEfforts.Any(effort => !ValidReasoningEfforts.Contains(effort))
                || metadata.ReasoningEfforts.Distinct(StringComparer.Ordinal).Count() != metadata.ReasoningEfforts.Count)
                throw new ArgumentException("Catalog reasoning efforts are invalid.");
        }
        if (validateDefaultEffort && metadata.DefaultReasoningEffort is not null
            && (metadata.ReasoningEfforts is null
                || !metadata.ReasoningEfforts.Contains(metadata.DefaultReasoningEffort, StringComparer.Ordinal)))
            throw new ArgumentException("Catalog default reasoning effort must be one of the available efforts.");
    }

    private static IEnumerable<string> DistinctValidIds(IEnumerable<string>? ids)
        => (ids ?? Enumerable.Empty<string>()).Where(IsValidId).Distinct(StringComparer.Ordinal);

    private static bool IsValidId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id!.Length <= 200 && !id.Any(char.IsControl);

    private static IEnumerable<string> NormalizeEfforts(IEnumerable<string> efforts)
        => efforts.Where(effort => ValidReasoningEfforts.Contains(effort)).Distinct(StringComparer.Ordinal);

    private static CodexProviderModelMetadata ApplyOverride(
        CodexProviderModelMetadata baseline, CodexProviderModelMetadata value, string id)
    {
        var supportsReasoning = value.SupportsReasoning ?? baseline.SupportsReasoning;
        var reasoningEfforts = value.ReasoningEfforts is null ? Copy(baseline.ReasoningEfforts) : Copy(value.ReasoningEfforts);
        var defaultReasoningEffort = value.DefaultReasoningEffort ?? baseline.DefaultReasoningEffort;
        if (supportsReasoning == false)
        {
            reasoningEfforts = new List<string>();
            defaultReasoningEffort = null;
        }
        else if (reasoningEfforts is not null && defaultReasoningEffort is not null
            && !reasoningEfforts.Contains(defaultReasoningEffort, StringComparer.Ordinal))
        {
            defaultReasoningEffort = null;
        }
        return new CodexProviderModelMetadata
        {
            Id = id,
            DisplayName = value.DisplayName ?? baseline.DisplayName,
            ContextWindow = value.ContextWindow ?? baseline.ContextWindow,
            MaxOutputTokens = value.MaxOutputTokens ?? baseline.MaxOutputTokens,
            SupportsImages = value.SupportsImages ?? baseline.SupportsImages,
            SupportsTools = value.SupportsTools ?? baseline.SupportsTools,
            SupportsReasoning = supportsReasoning,
            SupportsResponses = value.SupportsResponses ?? baseline.SupportsResponses,
            ReasoningEfforts = reasoningEfforts,
            DefaultReasoningEffort = defaultReasoningEffort
        };
    }

    private static CodexProviderModelMetadata Clone(CodexProviderModelMetadata model)
        => ApplyOverride(new CodexProviderModelMetadata { Id = model.Id }, model, model.Id);

    private static List<string>? Copy(List<string>? values)
        => values is null ? null : new List<string>(values);
}
