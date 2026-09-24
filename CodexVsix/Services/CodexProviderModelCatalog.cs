using System;
using System.Collections.Generic;
using System.Linq;
using CodexVsix.Models;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>UI model ids carry provider identity; only real model ids reach Codex.</summary>
internal static class CodexProviderModelCatalog
{
    internal const string AliasPrefix = "vsai:";
    private static readonly string[] RequestedReasoningEfforts = { "low", "medium", "high", "xhigh", "max" };

    private static IReadOnlyList<string> LegacyReasoningEfforts(CodexProviderConfiguration provider, string model)
    {
        if (provider.ReasoningEfforts?.TryGetValue(model, out var configured) == true && configured is not null)
        {
            var options = configured.Where(e => e == "none" || CodexModelCatalog.GenericReasoningEfforts.Contains(e, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (options.Length > 0) return options;
        }
        // A manually entered model has no capability catalog. These are request controls;
        // the provider remains responsible for accepting the selected effort.
        return RequestedReasoningEfforts;
    }

    internal static IReadOnlyList<CodexProviderModelMetadata> GetEligibleModels(CodexProviderConfiguration provider)
    {
        if (provider.Catalog is not null)
            return CodexProviderCatalogConfigurationService.GetEffectiveModels(provider)
                .Where(IsEligibleCodingModel).ToArray();

        // Reading the picker must not migrate legacy settings. Their historical request-control
        // fallback remains intact until the settings service explicitly creates a Catalog.
        return (provider.Models ?? new List<string>()).Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.Ordinal).Select(model => new CodexProviderModelMetadata
            {
                Id = model,
                ContextWindow = provider.ContextWindows?.TryGetValue(model, out var window) == true ? window : null,
                ReasoningEfforts = LegacyReasoningEfforts(provider, model).ToList(),
                DefaultReasoningEffort = provider.DefaultReasoningEfforts?.TryGetValue(model, out var effort) == true
                    ? effort : null
            }).ToArray();
    }

    private static bool IsEligibleCodingModel(CodexProviderModelMetadata model)
        => model.SupportsResponses != false && model.SupportsTools != false;

    internal static string Alias(CodexProviderConfiguration provider, string model)
        => AliasPrefix + provider.Id + "/" + model;

    internal static string? FirstAvailableAlias(CodexExtensionSettings settings)
    {
        foreach (var provider in settings.Providers)
        {
            var first = GetEligibleModels(provider).FirstOrDefault();
            if (first is not null) return Alias(provider, first.Id);
        }
        return null;
    }

    internal static Selection? Resolve(CodexExtensionSettings settings, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (!model!.StartsWith(AliasPrefix, StringComparison.Ordinal))
            return new Selection("openai", model, model);

        var provider = settings.Providers.FirstOrDefault(p => p is not null
            && model.StartsWith(AliasPrefix + p.Id + "/", StringComparison.Ordinal));
        var realModel = provider is null ? null : model.Substring(AliasPrefix.Length + provider.Id.Length + 1);
        if (provider is null || !GetEligibleModels(provider).Any(item => item.Id == realModel))
            throw new InvalidOperationException("所选服务或模型已被移除，请重新选择模型。");
        CodexProviderConfigurationService.Validate(provider);
        return new Selection(CodexProviderConfigurationService.GetProviderId(provider), realModel!, model);
    }

    internal static string ToDisplayModel(CodexExtensionSettings settings, string? providerId, string model)
    {
        var provider = settings.Providers.FirstOrDefault(p =>
            string.Equals("vsai_" + p.Id, providerId, StringComparison.Ordinal));
        return provider is not null && GetEligibleModels(provider).Any(item => item.Id == model)
            ? Alias(provider, model) : model;
    }

    internal static JToken? Merge(CodexExtensionSettings settings, JToken? result, bool hasChatGptLogin, bool includeProviderModels = true)
    {
        if (settings.Providers.Count == 0 && hasChatGptLogin) return result;
        var response = result?.DeepClone() as JObject ?? new JObject();
        var models = (JArray)(response["data"] as JArray ?? response["models"] as JArray ?? new JArray()).DeepClone();
        // Authentication controls access, not catalog visibility. A fresh private home
        // has no ChatGPT login yet, but must still expose the official model choices.
        // The execution catalog contains real custom IDs. Only provider-qualified aliases
        // belong in the picker; a bare custom ID would otherwise route back to OpenAI.
        var capacityModels = new HashSet<string>(settings.Providers.Where(provider => provider.Catalog is null)
            .SelectMany(provider => (provider.Models ?? new List<string>()).Where(model =>
                provider.ContextWindows?.ContainsKey(model) == true)), StringComparer.Ordinal);
        foreach (var model in models.OfType<JObject>().Where(model =>
            capacityModels.Contains(model["model"]?.Value<string>() ?? string.Empty)).ToArray())
            model.Remove();
        // A static runtime catalog exposes its real IDs through model/list. Hide only entries
        // generated by this extension; native models with the same ID keep their original row.
        foreach (var model in models.OfType<JObject>().Where(model =>
            string.Equals(model["description"]?.Value<string>(),
                CodexProviderModelCatalogFile.RuntimeDescription, StringComparison.Ordinal)).ToArray())
            model.Remove();
        // Custom models belong on the first page only; never add them to a continuation page.
        if (includeProviderModels)
        {
            foreach (var provider in settings.Providers.Where(provider => provider is not null))
            foreach (var metadata in GetEligibleModels(provider))
            {
                var model = metadata.Id;
                var alias = Alias(provider, model);
                if (models.Any(item => item["model"]?.Value<string>() == alias)) continue;
                var isLegacy = provider.Catalog is null;
                var efforts = isLegacy
                    ? LegacyReasoningEfforts(provider, model)
                    : metadata.SupportsReasoning == false
                        ? Array.Empty<string>()
                        : (IReadOnlyList<string>)(metadata.ReasoningEfforts?.ToArray() ?? Array.Empty<string>());
                string? defaultEffort;
                if (isLegacy)
                {
                    defaultEffort = provider.DefaultReasoningEfforts?.TryGetValue(model, out var configuredDefault) == true
                        && efforts.Contains(configuredDefault, StringComparer.Ordinal) ? configuredDefault
                        : efforts.Contains("medium", StringComparer.Ordinal) ? "medium" : efforts[0];
                }
                else
                {
                    defaultEffort = metadata.DefaultReasoningEffort is not null
                        && efforts.Contains(metadata.DefaultReasoningEffort, StringComparer.Ordinal)
                            ? metadata.DefaultReasoningEffort : null;
                }
                models.Add(new JObject
                {
                    ["id"] = alias, ["model"] = alias,
                    ["displayName"] = provider.Name + " · " + (metadata.DisplayName ?? model),
                    ["description"] = provider.Name + " / Responses API",
                    ["hidden"] = false, ["isDefault"] = false,
                    ["supportedReasoningEfforts"] = new JArray(efforts.Select(effort => new JObject
                    {
                        ["reasoningEffort"] = effort,
                        ["description"] = "Request " + effort + " reasoning effort from this provider."
                    })),
                    ["defaultReasoningEffort"] = defaultEffort is null ? JValue.CreateNull() : defaultEffort,
                    ["inputModalities"] = metadata.SupportsImages == true
                        ? new JArray("text", "image") : new JArray("text"),
                    ["supportsPersonality"] = false,
                    ["additionalSpeedTiers"] = new JArray(), ["serviceTiers"] = new JArray(),
                    ["upgrade"] = null, ["upgradeInfo"] = null, ["availabilityNux"] = null,
                    ["modelSpecialty"] = null, ["multiAgentVersion"] = null,
                    ["defaultServiceTier"] = null, ["availableAccessPrograms"] = null
                });
            }
        }
        var selected = settings.DefaultModel;
        if (string.IsNullOrWhiteSpace(selected) && !hasChatGptLogin)
            selected = FirstAvailableAlias(settings);
        if (models.Any(m => m["model"]?.Value<string>() == selected))
            foreach (var model in models.OfType<JObject>()) model["isDefault"] = model["model"]?.Value<string>() == selected;
        response["data"] = models;
        response["models"] = models.DeepClone();
        return response;
    }

    internal sealed class Selection
    {
        internal Selection(string provider, string model, string alias)
        { Provider = provider; Model = model; DisplayModel = alias; }
        internal string Provider { get; }
        internal string Model { get; }
        internal string DisplayModel { get; }
        internal bool IsCustom => Provider.StartsWith("vsai_", StringComparison.Ordinal);
    }
}
