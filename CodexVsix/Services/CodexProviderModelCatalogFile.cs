using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CodexVsix.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>Appends explicit custom model metadata without replacing the official catalog.</summary>
internal static class CodexProviderModelCatalogFile
{
    internal const string RuntimeDescription = "VSAI generated provider model metadata";

    internal static bool HasOverrides(CodexExtensionSettings settings)
        => HasLegacyOverrides(settings) || settings.Providers?.Any(provider => provider?.Catalog is not null
            && CodexProviderCatalogConfigurationService.GetEffectiveModels(provider)
                .Any(HasRuntimeMetadata)) == true;

    internal static bool HasLegacyOverrides(CodexExtensionSettings settings)
        => settings.Providers?.Any(provider => provider?.Catalog is null
            && provider!.ContextWindows?.Count > 0) == true;

    internal static JObject? Merge(JObject baseline, CodexExtensionSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (!HasOverrides(settings)) return null;
        if (baseline is null) throw new ArgumentNullException(nameof(baseline));
        if (baseline["models"] is not JArray originals || originals.Count == 0)
            throw new InvalidDataException("完整模型目录缺少 models，无法安全添加上下文容量。");

        var originalSlugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in originals)
        {
            var slug = (entry as JObject)?["slug"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(slug) || !originalSlugs.Add(slug!))
                throw new InvalidDataException("完整模型目录包含无效或重复模型 ID。");
        }

        var additions = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
        var legacyProviders = settings.Providers.Where(provider => provider?.Catalog is null).ToArray();
        foreach (var provider in legacyProviders)
        {
            if (provider?.ContextWindows is null) continue;
            foreach (var pair in provider.ContextWindows)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 200 || pair.Key.Any(char.IsControl)
                    || pair.Value <= 0 || provider.Models?.Contains(pair.Key, StringComparer.Ordinal) != true)
                    throw new InvalidDataException("自定义模型上下文容量无效或模型已被移除。");
                if (originalSlugs.Contains(pair.Key))
                    throw new InvalidDataException("自定义模型 ID 与原模型目录冲突，不能覆盖原模型容量。");

                var candidate = CreateLegacyModel(provider, pair.Key, pair.Value);
                foreach (var other in legacyProviders.Where(p => p is not null
                    && p.Models?.Contains(pair.Key, StringComparer.Ordinal) == true))
                {
                    if (other.ContextWindows is null || !other.ContextWindows.TryGetValue(pair.Key, out var otherWindow)
                        || otherWindow != pair.Value
                        || !JToken.DeepEquals(candidate, CreateLegacyModel(other, pair.Key, otherWindow)))
                        throw new InvalidDataException("多个服务使用相同模型 ID，但模型容量或推理设置不同。");
                }
                additions[pair.Key] = candidate;
            }
        }

        // Catalog-backed providers are discovered data, not explicit global overrides. The CLI
        // accepts only one process catalog, so ambiguous IDs must retain native metadata instead
        // of making the whole catalog unusable.
        var automaticGroups = settings.Providers.Where(provider => provider?.Catalog is not null)
            .SelectMany(provider => CodexProviderCatalogConfigurationService.GetEffectiveModels(provider))
            .GroupBy(model => model.Id, StringComparer.Ordinal);
        foreach (var group in automaticGroups)
        {
            if (originalSlugs.Contains(group.Key) || additions.ContainsKey(group.Key)
                || legacyProviders.Any(provider => provider?.Models?.Contains(group.Key, StringComparer.Ordinal) == true)
                || group.Any(model => model.SupportsResponses == false || model.SupportsTools == false || !HasRuntimeMetadata(model)))
                continue;
            var candidates = group.Select(CreateDiscoveredModel).ToArray();
            var candidate = candidates[0];
            if (candidates.All(other => JToken.DeepEquals(candidate, other))) additions[group.Key] = candidate;
        }

        // Avoid replacing the native ModelsManager with an identical static snapshot when every
        // discovered override was unsafe to apply.
        if (additions.Count == 0) return null;

        var merged = (JObject)baseline.DeepClone();
        var models = (JArray)merged["models"]!;
        foreach (var addition in additions.Values) models.Add(addition);
        return merged;
    }

    internal static string? WriteImmutableCatalog(JObject baseline, CodexExtensionSettings settings, string directory)
    {
        var merged = Merge(baseline, settings);
        if (merged is null) return null;
        // Cache timestamps and account identity are not ModelInfo and must not enter runtime files.
        var catalog = new JObject { ["models"] = merged["models"]!.DeepClone() };
        var bytes = new UTF8Encoding(false).GetBytes(catalog.ToString(Formatting.None));
        string hash;
        using (var sha = SHA256.Create())
            hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "models-" + hash + ".json");
        if (File.Exists(destination))
        {
            VerifyExisting(destination, bytes);
            return destination;
        }

        var temporary = Path.Combine(directory, ".models-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { VerifyExisting(destination, bytes); }
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void VerifyExisting(string path, byte[] expected)
    {
        if (!File.ReadAllBytes(path).SequenceEqual(expected))
            throw new InvalidDataException("生成的模型目录已被修改，无法安全复用。");
    }

    private static bool HasRuntimeMetadata(CodexProviderModelMetadata model)
        => model.ContextWindow is > 0;

    private static JObject CreateLegacyModel(CodexProviderConfiguration provider, string model, long contextWindow)
    {
        var efforts = provider.ReasoningEfforts?.TryGetValue(model, out var configured) == true
            && configured is not null ? configured.Distinct(StringComparer.Ordinal).ToArray()
            : new[] { "low", "medium", "high", "xhigh", "max" };
        if (efforts.Length == 0 || efforts.Any(e => e != "none"
            && !CodexModelCatalog.GenericReasoningEfforts.Contains(e, StringComparer.Ordinal)))
            throw new InvalidDataException("自定义模型的推理档位元数据无效。");
        var defaultEffort = provider.DefaultReasoningEfforts?.TryGetValue(model, out var selected) == true
            && efforts.Contains(selected, StringComparer.Ordinal) ? selected
            : efforts.Contains("medium", StringComparer.Ordinal) ? "medium" : efforts[0];
        return CreateModelInfo(
            model,
            model,
            efforts,
            defaultEffort,
            contextWindow,
            supportsImages: false,
            supportsParallelTools: true,
            supportsReasoning: false);
    }

    private static JObject CreateDiscoveredModel(CodexProviderModelMetadata metadata)
    {
        var efforts = metadata.SupportsReasoning == false
            ? Array.Empty<string>()
            : metadata.ReasoningEfforts?.ToArray() ?? Array.Empty<string>();
        var defaultEffort = metadata.DefaultReasoningEffort is not null
            && efforts.Contains(metadata.DefaultReasoningEffort, StringComparer.Ordinal)
                ? metadata.DefaultReasoningEffort : null;
        return CreateModelInfo(
            metadata.Id,
            metadata.DisplayName ?? metadata.Id,
            efforts,
            defaultEffort,
            metadata.ContextWindow,
            metadata.SupportsImages == true,
            supportsParallelTools: false,
            supportsReasoning: efforts.Length > 0);
    }

    private static JObject CreateModelInfo(
        string model,
        string displayName,
        IReadOnlyList<string> efforts,
        string? defaultEffort,
        long? contextWindow,
        bool supportsImages,
        bool supportsParallelTools,
        bool supportsReasoning)
    {
        var result = new JObject
        {
            ["slug"] = model, ["display_name"] = displayName,
            ["description"] = RuntimeDescription,
            ["default_reasoning_level"] = defaultEffort is null ? JValue.CreateNull() : defaultEffort,
            ["supported_reasoning_levels"] = new JArray(efforts.Select(e => new JObject
            {
                ["effort"] = e, ["description"] = "Request " + e + " reasoning effort."
            })),
            ["shell_type"] = "shell_command", ["visibility"] = "list", ["supported_in_api"] = true,
            ["priority"] = 100, ["availability_nux"] = null, ["upgrade"] = null,
            ["base_instructions"] = "You are a coding assistant.",
            ["supports_reasoning_summaries"] = supportsReasoning, ["default_reasoning_summary"] = "none",
            ["support_verbosity"] = false, ["default_verbosity"] = null,
            ["apply_patch_tool_type"] = "freeform", ["web_search_tool_type"] = "text_and_image",
            ["truncation_policy"] = new JObject { ["mode"] = "tokens", ["limit"] = 10000 },
            ["supports_parallel_tool_calls"] = supportsParallelTools, ["supports_image_detail_original"] = false,
            ["effective_context_window_percent"] = 95,
            ["experimental_supported_tools"] = new JArray(),
            ["input_modalities"] = supportsImages ? new JArray("text", "image") : new JArray("text"),
            ["supports_search_tool"] = false
        };
        if (contextWindow is > 0)
        {
            result["context_window"] = contextWindow.Value;
            result["max_context_window"] = contextWindow.Value;
        }
        return result;
    }
}
