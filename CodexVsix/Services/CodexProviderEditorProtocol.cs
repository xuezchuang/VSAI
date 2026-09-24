using System;
using System.Collections.Generic;
using System.Linq;
using CodexVsix.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace CodexVsix.Services;

internal static class CodexProviderEditorProtocol
{
    private static JsonSerializer Serializer => JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false }
        }
    });

    internal static CodexProviderConfiguration Read(JObject input, CodexProviderConfiguration? existing, bool discovery = false,
        Func<CodexProviderConfiguration, CodexProviderDiscoveryResult?>? acceptedDiscovery = null)
    {
        var provider = existing is null ? new CodexProviderConfiguration() : Clone(existing);
        provider.Name = input["name"]?.Value<string>()?.Trim() ?? string.Empty;
        if (discovery && string.IsNullOrWhiteSpace(provider.Name)) provider.Name = "未命名服务";
        provider.BaseUrl = input["baseUrl"]?.Value<string>()?.Trim() ?? string.Empty;
        var key = input["apiKey"]?.Value<string>();
        if (!string.IsNullOrEmpty(key)) provider.ApiKey = key!;
        var suppliedCatalog = input["catalog"] as JObject;
        if (suppliedCatalog is not null)
            provider.Catalog = suppliedCatalog.ToObject<CodexProviderCatalogConfiguration>();
        else
        {
            var catalog = CodexProviderCatalogConfigurationService.EnsureCatalog(provider);
            if (input["models"] is JArray models)
                catalog.ManualModels = models.Values<string>().Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        }
        var requested = CodexProviderCatalogConfigurationService.EnsureCatalog(provider);
        // WebView input owns editable fields, never the provenance of a cached remote snapshot.
        requested.DiscoveredModels = new List<CodexProviderModelMetadata>();
        requested.LastSuccessUtc = null;
        requested.LastAttemptUtc = null;
        requested.Status = "never";
        requested.Error = null;
        if (existing?.Catalog is not null
            && CodexProviderCatalogSynchronizer.Scope(existing) == CodexProviderCatalogSynchronizer.Scope(provider))
        {
            var current = Clone(existing).Catalog!;
            requested.DiscoveredModels = current.DiscoveredModels;
            requested.LastSuccessUtc = current.LastSuccessUtc;
            requested.LastAttemptUtc = current.LastAttemptUtc;
            requested.Status = current.Status;
            requested.Error = current.Error;
        }
        var verified = acceptedDiscovery?.Invoke(provider);
        if (verified is not null && (!requested.LastAttemptUtc.HasValue || verified.AttemptedUtc >= requested.LastAttemptUtc.Value))
        {
            requested.LastAttemptUtc = verified.AttemptedUtc;
            requested.Status = verified.Status;
            requested.Error = verified.Error;
            if (verified.Status == "success" || verified.Status == "partial")
            {
                requested.DiscoveredModels = verified.Models.ToList();
                requested.LastSuccessUtc = verified.AttemptedUtc;
            }
        }
        CodexProviderCatalogConfigurationService.ApplyEffectiveProperties(provider);
        CodexProviderConfigurationService.Validate(provider);
        return provider;
    }

    internal static JObject ToPublicProfile(CodexProviderConfiguration provider)
    {
        var copy = Clone(provider);
        CodexProviderCatalogConfigurationService.EnsureCatalog(copy);
        return new JObject
        {
            ["id"] = copy.Id, ["name"] = copy.Name, ["baseUrl"] = copy.BaseUrl,
            ["models"] = new JArray(copy.Models), ["hasApiKey"] = !string.IsNullOrEmpty(copy.ApiKey),
            ["catalog"] = JObject.FromObject(copy.Catalog!, Serializer)
        };
    }

    internal static JObject DiscoveryResponse(string? requestId, CodexProviderDiscoveryResult result)
        => new()
        {
            ["type"] = "providers-discovery", ["requestId"] = requestId, ["discoveryToken"] = requestId,
            ["models"] = JArray.FromObject(result.Models, Serializer),
            ["status"] = result.Status, ["error"] = result.Error,
            ["attemptedUtc"] = result.AttemptedUtc
        };

    internal static bool ApplyDiscovery(CodexExtensionSettings settings, CodexProviderConfiguration requested, CodexProviderDiscoveryResult result)
    {
        var current = settings.Providers.FirstOrDefault(p => p.Id == requested.Id);
        if (current?.Catalog?.AutoSync != true
            || CodexProviderCatalogSynchronizer.Scope(current) != CodexProviderCatalogSynchronizer.Scope(requested)
            || current.Catalog.LastAttemptUtc > result.AttemptedUtc) return false;
        var candidate = Clone(current);
        var catalog = candidate.Catalog!;
        catalog.LastAttemptUtc = result.AttemptedUtc;
        catalog.Status = result.Status;
        catalog.Error = result.Error;
        if (result.Status == "success" || result.Status == "partial")
        {
            catalog.DiscoveredModels = result.Models.ToList();
            catalog.LastSuccessUtc = result.AttemptedUtc;
        }
        CodexProviderCatalogConfigurationService.ApplyEffectiveProperties(candidate);
        CodexProviderConfigurationService.Validate(candidate);
        settings.Providers[settings.Providers.IndexOf(current)] = candidate;
        return true;
    }

    internal static CodexProviderConfiguration Clone(CodexProviderConfiguration provider)
        => JsonConvert.DeserializeObject<CodexProviderConfiguration>(JsonConvert.SerializeObject(provider))!;
}
