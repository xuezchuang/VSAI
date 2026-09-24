using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>Fetches a bounded, same-origin provider model catalog without persisting it.</summary>
public sealed class CodexProviderDiscoveryService
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumModels = 1000;
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> ValidReasoningEfforts = new(StringComparer.Ordinal)
    {
        "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"
    };

    private readonly HttpClient httpClient;

    /// <summary>Uses a non-redirecting client with a short discovery timeout.</summary>
    public CodexProviderDiscoveryService()
        : this(CreateDefaultHttpClient())
    {
    }

    /// <summary>The supplied client is intended for controlled callers and HTTP-handler tests.</summary>
    public CodexProviderDiscoveryService(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<CodexProviderDiscoveryResult> FetchAsync(
        CodexProviderConfiguration provider,
        CancellationToken cancellationToken)
    {
        var attemptedUtc = DateTime.UtcNow;
        if (provider is null)
            return Error("error", "服务配置缺失。", attemptedUtc);
        if (!TryCreateBaseUri(provider.BaseUrl, out var baseUri))
            return Error("error", "服务地址无效。", attemptedUtc);

        var source = provider.Catalog?.Source ?? "auto";
        if (source != "auto" && source != "openai" && source != "midas")
            return Error("error", "模型目录来源无效。", attemptedUtc);
        if (source == "midas" && string.IsNullOrWhiteSpace(provider.ApiKey))
            return Error("auth-error", "MIDAS 模型目录发现需要 API 密钥。", attemptedUtc);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DiscoveryTimeout);
            var requestToken = timeout.Token;
            if (source == "midas")
                return await FetchEndpointAsync(provider, baseUri, GetMidasUri(baseUri), false, attemptedUtc, requestToken)
                    .ConfigureAwait(false);

            var generic = await FetchEndpointAsync(provider, baseUri, GetOpenAiModelsUri(baseUri), true, attemptedUtc, requestToken)
                .ConfigureAwait(false);
            if (generic.Status == "error" || generic.Status == "auth-error") return generic;
            if (source != "auto" || !generic.OwnedByMidasGateway) return generic;

            var rich = await FetchEndpointAsync(provider, baseUri, GetMidasUri(baseUri), false, attemptedUtc, requestToken)
                .ConfigureAwait(false);
            // The generic response only established the MIDAS endpoint. Never expose
            // its incomplete list when the rich, authorized request did not succeed.
            return rich;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error("error", "获取模型目录超时。", attemptedUtc);
        }
        catch (HttpRequestException)
        {
            return Error("error", "无法连接服务模型目录。", attemptedUtc);
        }
        catch (IOException)
        {
            return Error("error", "无法读取服务模型目录。", attemptedUtc);
        }
        catch (JsonException)
        {
            return Error("error", "服务返回的模型目录 JSON 无效。", attemptedUtc);
        }
        catch (DecoderFallbackException)
        {
            return Error("error", "服务返回的模型目录无法读取。", attemptedUtc);
        }
        catch (Exception)
        {
            return Error("error", "模型目录获取未能完成。", attemptedUtc);
        }
    }

    private async Task<CodexProviderDiscoveryResult> FetchEndpointAsync(
        CodexProviderConfiguration provider,
        Uri configuredOrigin,
        Uri endpoint,
        bool recognizeMidasOwner,
        DateTime attemptedUtc,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null || !HasSameOrigin(configuredOrigin, responseUri))
            return Error("error", "服务将模型目录请求重定向到了未配置的来源。", attemptedUtc);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            return Error("auth-error", "服务拒绝了模型目录请求的身份验证。", attemptedUtc);
        if (!response.IsSuccessStatusCode)
            return Error("error", "获取模型目录失败（HTTP " + (int)response.StatusCode + "）。", attemptedUtc);
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            return Error("error", "服务返回的模型目录过大。", attemptedUtc);

        var body = await ReadBoundedContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var document = JToken.Parse(body);
        if (document is JObject root && (root["has_more"]?.Value<bool>() == true
            || root["next_cursor"]?.Type == JTokenType.String && !string.IsNullOrEmpty(root["next_cursor"]!.Value<string>())))
            return Error("error", "服务返回了未完整的分页目录，请使用完整模型目录接口或手动补充。", attemptedUtc);
        var parsed = ParseModels(document, recognizeMidasOwner);
        if (parsed.TooManyModels)
            return Error("error", "服务返回的模型数量超出上限。", attemptedUtc);
        if (parsed.InvalidEntries)
            return Error("error", "服务返回的模型目录包含无效条目。", attemptedUtc);
        if (parsed.NoModels)
            return Error("error", "服务没有返回可用模型。", attemptedUtc);
        try
        {
            CodexProviderCatalogConfigurationService.ValidateCatalog(new CodexProviderCatalogConfiguration
                { DiscoveredModels = parsed.Models.ToList() });
        }
        catch (ArgumentException)
        {
            return Error("error", "服务返回的模型能力信息无效，已保留原有目录。", attemptedUtc);
        }
        return new CodexProviderDiscoveryResult(parsed.Models, parsed.Partial ? "partial" : "success", null, attemptedUtc)
        {
            OwnedByMidasGateway = parsed.OwnedByMidasGateway
        };
    }

    private static async Task<string> ReadBoundedContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new IOException("Model catalog exceeds the size limit.");
            await buffer.WriteAsync(chunk, 0, read, cancellationToken).ConfigureAwait(false);
        }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }

    private static ParsedModels ParseModels(JToken document, bool recognizeMidasOwner)
    {
        var entries = document as JArray ?? document["data"] as JArray ?? document["models"] as JArray;
        if (entries is null) throw new JsonException("Model catalog does not contain an array.");
        if (entries.Count > MaximumModels)
            return new ParsedModels(Array.Empty<CodexProviderModelMetadata>(), false, false, true, false);
        var models = new List<CodexProviderModelMetadata>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var partial = false;
        var invalidEntries = false;
        var ownedByMidasGateway = false;
        foreach (var entry in entries)
        {
            if (entry is not JObject item)
            {
                invalidEntries = true;
                continue;
            }
            if (recognizeMidasOwner && string.Equals(item["owned_by"]?.Value<string>(), "midas-gateway", StringComparison.Ordinal))
                ownedByMidasGateway = true;
            var id = ReadString(item, "id", "model", "slug");
            if (!IsValidId(id) || !seen.Add(id!))
            {
                invalidEntries = true;
                continue;
            }
            var model = ParseMetadata(item, id!);
            if (!HasKnownCapabilities(model)) partial = true;
            models.Add(model);
        }
        return new ParsedModels(models, partial, ownedByMidasGateway, false, invalidEntries, models.Count == 0);
    }

    private static CodexProviderModelMetadata ParseMetadata(JObject item, string id)
    {
        var reasoning = item["reasoning"] as JObject;
        var capabilities = item["capabilities"] as JObject;
        var reasoningEfforts = ReadReasoningEfforts(item, reasoning);
        var defaultReasoningEffort = ReadDefaultReasoningEffort(item, reasoning);
        var metadata = new CodexProviderModelMetadata
        {
            Id = id,
            DisplayName = ReadString(item, "display_name", "displayName", "name"),
            ContextWindow = ReadPositiveLong(item, "context_window", "contextWindow", "max_context_window"),
            MaxOutputTokens = ReadPositiveLong(item, "max_output_tokens", "maxOutputTokens"),
            SupportsImages = ReadBoolean(item, "supports_images", "supports_image", "supportsImages")
                ?? ReadBoolean(capabilities, "image_input", "images"),
            SupportsTools = ReadBoolean(item, "supports_tools", "supportsTools")
                ?? ReadBoolean(capabilities, "tool_calling", "tools"),
            SupportsReasoning = ReadBoolean(item, "supports_reasoning", "supportsReasoning")
                ?? ReadBoolean(capabilities, "reasoning"),
            SupportsResponses = ReadBoolean(item, "supports_responses", "supportsResponses")
                ?? ReadBoolean(capabilities, "responses"),
            ReasoningEfforts = reasoningEfforts,
            // A default is not a capability declaration. Keep it only when the
            // endpoint also named that exact selectable effort.
            DefaultReasoningEffort = defaultReasoningEffort is not null
                && reasoningEfforts?.Contains(defaultReasoningEffort, StringComparer.Ordinal) == true
                ? defaultReasoningEffort : null
        };
        return metadata;
    }

    private static List<string>? ReadReasoningEfforts(JObject item, JObject? reasoning)
    {
        var levels = reasoning?["levels"] as JArray
            ?? item["supported_reasoning_levels"] as JArray
            ?? item["supportedReasoningEfforts"] as JArray;
        if (levels is null) return null;
        var efforts = new List<string>();
        foreach (var value in levels)
        {
            var effort = value.Type == JTokenType.String ? value.Value<string>()
                : (value as JObject)?["level"]?.Value<string>()
                    ?? (value as JObject)?["effort"]?.Value<string>()
                    ?? (value as JObject)?["request"]?["reasoning_effort"]?.Value<string>();
            if (effort is not null && ValidReasoningEfforts.Contains(effort) && !efforts.Contains(effort, StringComparer.Ordinal))
                efforts.Add(effort);
        }
        return efforts;
    }

    private static string? ReadDefaultReasoningEffort(JObject item, JObject? reasoning)
    {
        var value = ReadString(reasoning, "default", "default_level", "defaultLevel")
            ?? ReadString(reasoning?["default"] as JObject, "level", "reasoning_effort")
            ?? ReadString(item, "default_reasoning_level", "defaultReasoningEffort");
        return value is not null && ValidReasoningEfforts.Contains(value) ? value : null;
    }

    private static string? ReadString(JObject? item, params string[] names)
    {
        if (item is null) return null;
        foreach (var name in names)
        {
            var value = item[name];
            if (value?.Type == JTokenType.String) return value.Value<string>();
        }
        return null;
    }

    private static long? ReadPositiveLong(JObject item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item[name]?.Type is JTokenType.Integer && item[name]!.Value<long>() > 0)
                return item[name]!.Value<long>();
        }
        return null;
    }

    private static bool? ReadBoolean(JObject? item, params string[] names)
    {
        if (item is null) return null;
        foreach (var name in names)
        {
            if (item[name]?.Type == JTokenType.Boolean) return item[name]!.Value<bool>();
        }
        return null;
    }

    private static bool TryCreateBaseUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value!.Trim();
        return string.Equals(value, trimmed, StringComparison.Ordinal)
            && Uri.TryCreate(value, UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static Uri GetOpenAiModelsUri(Uri baseUri)
        => new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/models", UriKind.Absolute);

    private static Uri GetMidasUri(Uri baseUri)
        => new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/api/models", UriKind.Absolute);

    private static bool HasSameOrigin(Uri expected, Uri actual)
        => string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
            && expected.Port == actual.Port;

    private static bool IsValidId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id!.Length <= 200 && !id.Any(char.IsControl);

    private static bool HasKnownCapabilities(CodexProviderModelMetadata model)
        => model.ContextWindow.HasValue || model.MaxOutputTokens.HasValue
            || model.SupportsImages.HasValue || model.SupportsTools.HasValue
            || model.SupportsReasoning.HasValue || model.SupportsResponses.HasValue
            || model.ReasoningEfforts is not null || model.DefaultReasoningEffort is not null;

    private static CodexProviderDiscoveryResult Error(string status, string message, DateTime attemptedUtc)
        => new(Array.Empty<CodexProviderModelMetadata>(), status, message, attemptedUtc);

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        return new HttpClient(handler) { Timeout = DiscoveryTimeout };
    }

    private sealed class ParsedModels
    {
        internal ParsedModels(IReadOnlyList<CodexProviderModelMetadata> models, bool partial, bool ownedByMidasGateway,
            bool tooManyModels = false, bool invalidEntries = false, bool noModels = false)
        {
            Models = models;
            Partial = partial;
            OwnedByMidasGateway = ownedByMidasGateway;
            TooManyModels = tooManyModels;
            InvalidEntries = invalidEntries;
            NoModels = noModels;
        }

        internal IReadOnlyList<CodexProviderModelMetadata> Models { get; }
        internal bool Partial { get; }
        internal bool OwnedByMidasGateway { get; }
        internal bool TooManyModels { get; }
        internal bool InvalidEntries { get; }
        internal bool NoModels { get; }
    }
}
