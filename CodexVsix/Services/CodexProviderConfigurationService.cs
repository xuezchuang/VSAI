using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CodexVsix.Models;

namespace CodexVsix.Services;

/// <summary>Builds process-local provider settings without exposing API keys in arguments.</summary>
public static class CodexProviderConfigurationService
{
    public static void Validate(CodexProviderConfiguration provider, bool requireApiKey = true)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        ValidateId(provider.Id);
        if (!IsValidText(provider.Name, 80))
        {
            throw new ArgumentException("Provider name must contain 1 to 80 characters without control characters.");
        }

        if (!IsValidText(provider.BaseUrl, 2048)
            || !string.Equals(provider.BaseUrl, provider.BaseUrl.Trim(), StringComparison.Ordinal)
            || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || provider.BaseUrl.IndexOf('?') >= 0
            || provider.BaseUrl.IndexOf('#') >= 0)
        {
            throw new ArgumentException("Base URL 须为 HTTP 或 HTTPS 地址，不能包含账号、密码、查询参数或片段。");
        }

        if (provider.Models is null || provider.Models.Count == 0 || provider.Models.Count > 50
            || provider.Models.Any(model => !IsValidText(model, 200)))
        {
            throw new ArgumentException("Provider must list 1 to 50 models, each with 1 to 200 characters and no control characters.");
        }

        if (provider.ContextWindows is not null && provider.ContextWindows.Values.Any(value => value <= 0))
        {
            throw new ArgumentException("Model context windows must be positive token counts.");
        }

        if (requireApiKey && string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new ArgumentException("Provider API key is required.");
        }

        if (provider.ApiKey is not null && provider.ApiKey.Any(char.IsControl))
        {
            throw new ArgumentException("Provider API key must not contain control characters.");
        }
    }

    public static string GetProviderId(CodexProviderConfiguration provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        ValidateId(provider.Id);
        return "vsai_" + provider.Id;
    }

    public static string GetEnvironmentKey(CodexProviderConfiguration provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        ValidateId(provider.Id);
        return "VSAI_PROVIDER_" + provider.Id.ToUpperInvariant() + "_API_KEY";
    }

    /// <summary>Registers a provider; selection remains a per-thread responsibility.</summary>
    public static IReadOnlyList<string> BuildConfigOverrides(CodexProviderConfiguration provider)
    {
        Validate(provider);
        var providerId = GetProviderId(provider);
        var prefix = "model_providers." + providerId + ".";
        return new[]
        {
            prefix + "name=" + CodexAppServerCommandLine.EncodeTomlString(provider.Name),
            prefix + "base_url=" + CodexAppServerCommandLine.EncodeTomlString(provider.BaseUrl),
            prefix + "wire_api=\"responses\"",
            prefix + "requires_openai_auth=false",
            prefix + "env_key=" + CodexAppServerCommandLine.EncodeTomlString(GetEnvironmentKey(provider))
        };
    }

    public static void ApplyEnvironment(ProcessStartInfo startInfo, CodexProviderConfiguration provider)
    {
        if (startInfo is null) throw new ArgumentNullException(nameof(startInfo));
        Validate(provider);
        startInfo.EnvironmentVariables[GetEnvironmentKey(provider)] = provider.ApiKey;
    }

    private static void ValidateId(string? id)
    {
        if (id is null || id.Length != 32 || id.Any(ch => !(ch >= '0' && ch <= '9') && !(ch >= 'a' && ch <= 'f')))
        {
            throw new ArgumentException("Provider ID must contain exactly 32 lowercase hexadecimal characters.");
        }
    }

    private static bool IsValidText(string? text, int maximumLength)
    {
        return !string.IsNullOrWhiteSpace(text) && text!.Length <= maximumLength && !text.Any(char.IsControl);
    }
}
