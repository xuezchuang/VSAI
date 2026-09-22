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

    private static IReadOnlyList<string> ReasoningEfforts(CodexProviderConfiguration provider, string model)
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

    internal static string Alias(CodexProviderConfiguration provider, string model)
        => AliasPrefix + provider.Id + "/" + model;

    internal static Selection? Resolve(CodexExtensionSettings settings, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (!model!.StartsWith(AliasPrefix, StringComparison.Ordinal))
            return new Selection("openai", model, model);

        var provider = settings.Providers.FirstOrDefault(p => p is not null
            && model.StartsWith(AliasPrefix + p.Id + "/", StringComparison.Ordinal));
        var realModel = provider is null ? null : model.Substring(AliasPrefix.Length + provider.Id.Length + 1);
        if (provider is null || !provider.Models.Contains(realModel!, StringComparer.Ordinal))
            throw new InvalidOperationException("所选服务或模型已被移除，请重新选择模型。");
        CodexProviderConfigurationService.Validate(provider);
        return new Selection(CodexProviderConfigurationService.GetProviderId(provider), realModel!, model);
    }

    internal static string ToDisplayModel(CodexExtensionSettings settings, string? providerId, string model)
    {
        var provider = settings.Providers.FirstOrDefault(p =>
            string.Equals("vsai_" + p.Id, providerId, StringComparison.Ordinal));
        return provider is not null && provider.Models.Contains(model, StringComparer.Ordinal)
            ? Alias(provider, model) : model;
    }

    internal static JToken? Merge(CodexExtensionSettings settings, JToken? result, bool hasOfficialAccount)
    {
        if (settings.Providers.Count == 0) return result;
        var response = result?.DeepClone() as JObject ?? new JObject();
        var models = (JArray)(response["data"] as JArray ?? response["models"] as JArray ?? new JArray()).DeepClone();
        // The execution catalog contains real custom IDs. Only provider-qualified aliases
        // belong in the picker; a bare custom ID would otherwise route back to OpenAI.
        var capacityModels = new HashSet<string>(settings.Providers.SelectMany(provider =>
            provider.Models.Where(model => provider.ContextWindows?.ContainsKey(model) == true)), StringComparer.Ordinal);
        foreach (var model in models.OfType<JObject>().Where(model =>
            capacityModels.Contains(model["model"]?.Value<string>() ?? string.Empty)).ToArray())
            model.Remove();
        // Only append on the first page: pagination must not repeat the custom models.
        foreach (var provider in settings.Providers)
        foreach (var model in provider.Models.Distinct(StringComparer.Ordinal))
        {
            var alias = Alias(provider, model);
            if (models.Any(item => item["model"]?.Value<string>() == alias)) continue;
            var efforts = ReasoningEfforts(provider, model);
            var defaultEffort = provider.DefaultReasoningEfforts?.TryGetValue(model, out var configuredDefault) == true
                && efforts.Contains(configuredDefault, StringComparer.Ordinal) ? configuredDefault
                : efforts.Contains("medium", StringComparer.Ordinal) ? "medium" : efforts[0];
            models.Add(new JObject
            {
                ["id"] = alias, ["model"] = alias,
                ["displayName"] = provider.Name + " · " + model,
                ["description"] = provider.Name + " / Responses API",
                ["hidden"] = false, ["isDefault"] = false,
                ["supportedReasoningEfforts"] = new JArray(efforts.Select(effort => new JObject
                {
                    ["reasoningEffort"] = effort,
                    ["description"] = "Request " + effort + " reasoning effort from this provider."
                })),
                ["defaultReasoningEffort"] = defaultEffort,
                ["inputModalities"] = new JArray("text"),
                ["supportsPersonality"] = false,
                ["additionalSpeedTiers"] = new JArray(), ["serviceTiers"] = new JArray(),
                ["upgrade"] = null, ["upgradeInfo"] = null, ["availabilityNux"] = null,
                ["modelSpecialty"] = null, ["multiAgentVersion"] = null,
                ["defaultServiceTier"] = null, ["availableAccessPrograms"] = null
            });
        }
        var selected = settings.DefaultModel;
        if (string.IsNullOrWhiteSpace(selected) && !hasOfficialAccount)
            selected = Alias(settings.Providers[0], settings.Providers[0].Models[0]);
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
