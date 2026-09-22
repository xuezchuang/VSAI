using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodexVsix.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>Creates an extension-local catalog only when a provider has explicit capacity metadata.</summary>
internal static class CodexProviderModelCatalogRuntime
{
    internal static string? Prepare(CodexExtensionSettings settings, string? outputDirectory = null)
    {
        if (!CodexProviderModelCatalogFile.HasOverrides(settings)) return null;

        var codexHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        var configPath = Path.Combine(codexHome, "config.toml");
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        // Do not silently replace a catalog chosen through an existing CLI profile/config.
        if (HasCatalogAssignment(config) || HasCatalogAssignment(settings.RawTomlOverrides ?? string.Empty)
            || (settings.AdditionalArguments ?? string.Empty).IndexOf("model_catalog_json", StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("已有自定义 model_catalog_json 配置，不能再叠加 VSAI 的上下文覆盖；请先统一模型目录配置。");

        var cachePath = Path.Combine(codexHome, "models_cache.json");
        if (!File.Exists(cachePath))
            throw new InvalidOperationException("未找到 Codex 的完整模型目录缓存，请先连接官方服务刷新模型列表，再启用自定义上下文容量。");
        var baseline = JObject.Parse(File.ReadAllText(cachePath));
        if (baseline["models"] is not JArray models || models.Count == 0)
            throw new InvalidOperationException("Codex 模型目录缓存为空，无法安全保留官方模型配置。");

        var directory = outputDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSAI", "model-catalogs");
        return CodexProviderModelCatalogFile.WriteImmutableCatalog(baseline, settings, directory);
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
                ["defaultEfforts"] = provider.DefaultReasoningEfforts is null ? null : JObject.FromObject(provider.DefaultReasoningEfforts)
            })).ToString(Formatting.None);
    }

    private static bool HasCatalogAssignment(string text)
        => Regex.IsMatch(text, @"(?m)^[^\r\n#]*\bmodel_catalog_json\b[^\r\n=]*=", RegexOptions.CultureInvariant);
}
