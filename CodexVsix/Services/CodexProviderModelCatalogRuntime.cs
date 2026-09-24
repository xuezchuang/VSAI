using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexVsix.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>Creates an extension-local catalog only when a provider has explicit capacity metadata.</summary>
internal static class CodexProviderModelCatalogRuntime
{
    private static readonly object SnapshotCacheGate = new();
    private static SnapshotCacheEntry? snapshotCache;

    internal sealed class ModelCatalogSnapshot
    {
        internal ModelCatalogSnapshot(JObject baseline, string key)
        {
            Baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
            Key = key ?? throw new ArgumentNullException(nameof(key));
        }

        internal JObject Baseline { get; }
        internal string Key { get; }
    }

    internal static string? Prepare(CodexExtensionSettings settings, string? outputDirectory = null)
    {
        return PrepareFromSnapshot(settings, snapshot: null, outputDirectory);
    }

    internal static ModelCatalogSnapshot? ReadSnapshot(CodexExtensionSettings settings)
    {
        if (!CodexProviderModelCatalogFile.HasOverrides(settings)) return null;

        var codexHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        var cachePath = Path.Combine(codexHome, "models_cache.json");
        CacheFileState fileState;
        try { fileState = GetCacheFileState(cachePath); }
        catch (InvalidOperationException) when (!CodexProviderModelCatalogFile.HasLegacyOverrides(settings)) { return null; }
        lock (SnapshotCacheGate)
        {
            if (snapshotCache is not null && snapshotCache.Matches(cachePath, fileState))
                return snapshotCache.Snapshot;
        }

        JObject cached;
        try { cached = JObject.Parse(File.ReadAllText(cachePath)); }
        catch (JsonException) when (!CodexProviderModelCatalogFile.HasLegacyOverrides(settings)) { return null; }
        catch (IOException) when (!CodexProviderModelCatalogFile.HasLegacyOverrides(settings)) { return null; }
        if (cached["models"] is not JArray models || models.Count == 0)
        {
            if (!CodexProviderModelCatalogFile.HasLegacyOverrides(settings)) return null;
            throw new InvalidOperationException("Codex 模型目录缓存为空，无法安全保留官方模型配置。");
        }

        var baseline = new JObject { ["models"] = models.DeepClone() };
        var key = ComputeModelsKey((JArray)baseline["models"]!);
        var snapshot = new ModelCatalogSnapshot(baseline, key);
        lock (SnapshotCacheGate)
        {
            snapshotCache = new SnapshotCacheEntry(cachePath, fileState, snapshot);
        }
        return snapshot;
    }

    // The native app-server also loads its model catalog at process startup.
    // Track that catalog even when no third-party capacity overrides are used.
    internal static string? ReadNativeModelsKey(CodexExtensionSettings settings)
    {
        var home = CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        var path = Path.Combine(home, "models_cache.json");
        try
        {
            if (!File.Exists(path)) return null;
            var cache = JObject.Parse(File.ReadAllText(path));
            return cache["models"] is JArray models && models.Count > 0
                ? ComputeModelsKey(models) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    internal static string? PrepareFromSnapshot(
        CodexExtensionSettings settings,
        ModelCatalogSnapshot? snapshot,
        string? outputDirectory = null)
    {
        if (!CodexProviderModelCatalogFile.HasOverrides(settings)) return null;

        if (CodexProviderModelCatalogFile.HasLegacyOverrides(settings) && HasExistingCatalogAssignment(settings))
            throw new InvalidOperationException("已有自定义 model_catalog_json 配置，不能再叠加 VSAI 的上下文覆盖；请先统一模型目录配置。");
        snapshot ??= ReadSnapshot(settings);
        if (snapshot is null) return null;
        if (HasExistingCatalogAssignment(settings))
            return null;

        var directory = outputDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSAI", "model-catalogs");
        return CodexProviderModelCatalogFile.WriteImmutableCatalog(snapshot.Baseline, settings, directory);
    }

    internal static string GetSettingsKey(CodexExtensionSettings settings)
    {
        if (!CodexProviderModelCatalogFile.HasOverrides(settings)) return string.Empty;
        return new JArray(settings.Providers.OrderBy(provider => provider.Id, StringComparer.Ordinal).Select(provider =>
            new JObject
            {
                ["id"] = provider.Id,
                ["models"] = new JArray(provider.Models.OrderBy(model => model, StringComparer.Ordinal)),
                ["windows"] = provider.ContextWindows is null ? null : JObject.FromObject(provider.ContextWindows),
                ["efforts"] = provider.ReasoningEfforts is null ? null : JObject.FromObject(provider.ReasoningEfforts),
                ["defaultEfforts"] = provider.DefaultReasoningEfforts is null ? null : JObject.FromObject(provider.DefaultReasoningEfforts),
                ["catalog"] = provider.Catalog is null ? null : new JArray(
                    CodexProviderCatalogConfigurationService.GetEffectiveModels(provider)
                        .OrderBy(model => model.Id, StringComparer.Ordinal).Select(model => new JObject
                        {
                            ["id"] = model.Id,
                            ["displayName"] = model.DisplayName,
                            ["contextWindow"] = model.ContextWindow,
                            ["maxOutputTokens"] = model.MaxOutputTokens,
                            ["supportsImages"] = model.SupportsImages,
                            ["supportsTools"] = model.SupportsTools,
                            ["supportsReasoning"] = model.SupportsReasoning,
                            ["supportsResponses"] = model.SupportsResponses,
                            ["reasoningEfforts"] = model.ReasoningEfforts is null ? null : new JArray(model.ReasoningEfforts),
                            ["defaultReasoningEffort"] = model.DefaultReasoningEffort
                        }))
            })).ToString(Formatting.None);
    }

    /// <summary>Stable codes for metadata that cannot be enforced by the current CLI catalog.</summary>
    internal static IReadOnlyList<string> GetRuntimeWarnings(
        CodexExtensionSettings settings,
        ModelCatalogSnapshot? snapshot = null)
    {
        var warnings = new List<string>();
        var catalogModels = settings.Providers.Where(provider => provider?.Catalog is not null)
            .SelectMany(provider => CodexProviderCatalogConfigurationService.GetEffectiveModels(provider))
            .ToArray();
        if (catalogModels.Any(model => model.MaxOutputTokens is > 0))
            warnings.Add("max-output-tokens-metadata-only");
        if (catalogModels.Any(model => model.ContextWindow is null
            && (model.SupportsImages is not null || model.SupportsReasoning is not null
                || model.ReasoningEfforts is not null || model.DefaultReasoningEffort is not null)))
            warnings.Add("runtime-metadata-context-unknown");
        if (catalogModels.Any(HasRuntimeMetadata) && snapshot is null)
            warnings.Add("runtime-catalog-baseline-unavailable");
        if (catalogModels.Any(HasRuntimeMetadata) && HasExistingCatalogAssignment(settings))
            warnings.Add("runtime-catalog-user-assignment");
        if (snapshot?.Baseline["models"] is JArray nativeModels)
        {
            var nativeIds = new HashSet<string>(nativeModels.OfType<JObject>()
                .Select(model => model["slug"]?.Value<string>()).Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!), StringComparer.Ordinal);
            if (catalogModels.Any(model => HasRuntimeMetadata(model) && nativeIds.Contains(model.Id)))
                warnings.Add("runtime-metadata-native-id-collision");
        }
        var contextualIds = new HashSet<string>(catalogModels.Where(HasRuntimeMetadata).Select(model => model.Id), StringComparer.Ordinal);
        var ambiguous = catalogModels.GroupBy(model => model.Id, StringComparer.Ordinal).Any(group =>
                group.Any(HasRuntimeMetadata) && (group.Any(model => !HasRuntimeMetadata(model)
                    || model.SupportsResponses == false || model.SupportsTools == false)
                    || group.Select(RuntimeMetadataKey).Distinct(StringComparer.Ordinal).Skip(1).Any()))
            || settings.Providers.Where(provider => provider?.Catalog is null).Any(provider =>
                (provider.Models ?? new List<string>()).Any(contextualIds.Contains));
        if (ambiguous) warnings.Add("runtime-metadata-cross-provider-collision");
        return warnings;
    }

    private static bool HasCatalogAssignment(string text)
        => Regex.IsMatch(text, @"(?m)^[^\r\n#]*\bmodel_catalog_json\b[^\r\n=]*=", RegexOptions.CultureInvariant);

    private static bool HasExistingCatalogAssignment(CodexExtensionSettings settings)
    {
        var codexHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        var configPath = Path.Combine(codexHome, "config.toml");
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        // Do not silently replace a catalog chosen through an existing CLI profile/config.
        return HasCatalogAssignment(config) || HasCatalogAssignment(settings.RawTomlOverrides ?? string.Empty)
            || (settings.AdditionalArguments ?? string.Empty).IndexOf("model_catalog_json", StringComparison.Ordinal) >= 0;
    }

    private static bool HasRuntimeMetadata(CodexProviderModelMetadata model)
        => model.ContextWindow is > 0;

    private static string RuntimeMetadataKey(CodexProviderModelMetadata model)
        => new JObject
        {
            ["displayName"] = model.DisplayName,
            ["contextWindow"] = model.ContextWindow,
            ["supportsImages"] = model.SupportsImages,
            ["supportsReasoning"] = model.SupportsReasoning,
            ["reasoningEfforts"] = model.ReasoningEfforts is null ? null : new JArray(model.ReasoningEfforts),
            ["defaultReasoningEffort"] = model.DefaultReasoningEffort
        }.ToString(Formatting.None);

    private static CacheFileState GetCacheFileState(string cachePath)
    {
        if (!File.Exists(cachePath))
            throw new InvalidOperationException("未找到 Codex 的完整模型目录缓存，请先连接官方服务刷新模型列表，再启用自定义上下文容量。");

        try
        {
            var info = new FileInfo(cachePath);
            return new CacheFileState(info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException("未找到 Codex 的完整模型目录缓存，请先连接官方服务刷新模型列表，再启用自定义上下文容量。");
        }
    }

    private static string ComputeModelsKey(JArray models)
    {
        var bytes = new UTF8Encoding(false).GetBytes(models.ToString(Formatting.None));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private readonly struct CacheFileState
    {
        internal CacheFileState(long length, long lastWriteTimeUtcTicks)
        {
            Length = length;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        }

        internal long Length { get; }
        internal long LastWriteTimeUtcTicks { get; }
    }

    private sealed class SnapshotCacheEntry
    {
        internal SnapshotCacheEntry(string cachePath, CacheFileState fileState, ModelCatalogSnapshot snapshot)
        {
            CachePath = cachePath;
            FileState = fileState;
            Snapshot = snapshot;
        }

        internal string CachePath { get; }
        internal CacheFileState FileState { get; }
        internal ModelCatalogSnapshot Snapshot { get; }

        internal bool Matches(string cachePath, CacheFileState fileState)
        {
            return string.Equals(CachePath, cachePath, StringComparison.OrdinalIgnoreCase)
                && FileState.Length == fileState.Length
                && FileState.LastWriteTimeUtcTicks == fileState.LastWriteTimeUtcTicks;
        }
    }
}
