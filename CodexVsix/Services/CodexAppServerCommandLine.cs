using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CodexVsix.Models;

namespace CodexVsix.Services;

/// <summary>
/// Builds the Codex app-server command line used by both the runtime and the
/// environment probe. Keeping this in one place prevents a probe from reporting
/// a configuration as healthy when the real process would start differently.
/// </summary>
internal static class CodexAppServerCommandLine
{
    internal static string Build(CodexExtensionSettings settings, string? modelCatalogPath = null, bool migrationSource = false)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var args = new List<string> { "app-server", "--listen", "stdio://" };

        if (!HasProfileArgument(settings.AdditionalArguments) && !string.IsNullOrWhiteSpace(settings.Profile))
        {
            args.Add("--profile");
            args.Add(settings.Profile.Trim());
        }

        foreach (var value in BuildConfigOverridesCore(settings, includeProviders: false))
        {
            args.Add("-c");
            args.Add(value);
        }

        if (!string.IsNullOrWhiteSpace(settings.AdditionalArguments))
        {
            args.AddRange(SplitArguments(settings.AdditionalArguments));
        }

        foreach (var value in BuildProviderOverrides(settings))
        {
            args.Add("-c");
            args.Add(value);
        }
        if (!string.IsNullOrWhiteSpace(modelCatalogPath))
        {
            args.Add("-c");
            args.Add("model_catalog_json=" + EncodeTomlString(modelCatalogPath!));
        }
        // Storage ownership wins over profile/raw/extra CLI overrides.
        foreach (var value in CodexSessionStorage.BuildConfigOverrides(settings, migrationSource))
        {
            args.Add("-c");
            args.Add(value);
        }
        return JoinArguments(args);
    }

    internal static IReadOnlyList<string> BuildConfigOverrides(CodexExtensionSettings settings)
        => BuildConfigOverridesCore(settings, includeProviders: true);

    private static IReadOnlyList<string> BuildConfigOverridesCore(CodexExtensionSettings settings, bool includeProviders)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var values = new List<string>();
        var verbosity = NormalizeVerbosity(settings.ModelVerbosity);
        if (!string.IsNullOrWhiteSpace(verbosity))
        {
            values.Add("model_verbosity=" + EncodeTomlString(verbosity!));
        }

        values.AddRange(BuildManagedMcpOverrides(settings.ManagedMcpServers));

        if (!string.IsNullOrWhiteSpace(settings.RawTomlOverrides))
        {
            foreach (var line in settings.RawTomlOverrides.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && !candidate.StartsWith("#", StringComparison.Ordinal))
                {
                    values.Add(candidate);
                }
            }
        }

        if (includeProviders) values.AddRange(BuildProviderOverrides(settings));
        return values;
    }

    private static IReadOnlyList<string> BuildProviderOverrides(CodexExtensionSettings settings)
    {
        var values = new List<string>();
        foreach (var provider in settings.Providers)
        {
            values.AddRange(CodexProviderConfigurationService.BuildConfigOverrides(provider));
        }
        if (settings.Providers.Count > 0)
        {
            // Keep account/model discovery on OpenAI. Custom providers are selected per thread.
            values.Add("model_provider=\"openai\"");
        }

        return values;
    }

    internal static IReadOnlyList<string> BuildManagedMcpOverrides(IEnumerable<CodexManagedMcpServer>? servers)
    {
        var values = new List<string>();
        if (servers is null)
        {
            return values;
        }

        foreach (var server in servers)
        {
            if (server is null || !server.Enabled)
            {
                continue;
            }

            var name = (server.Name ?? string.Empty).Trim();
            if (!IsValidManagedMcpName(name))
            {
                continue;
            }

            var keyPrefix = "mcp_servers." + name + ".";
            if (string.Equals(server.TransportType, "url", StringComparison.OrdinalIgnoreCase))
            {
                var url = (server.Url ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    values.Add(keyPrefix + "url=" + EncodeTomlString(url));
                }

                continue;
            }

            var command = (server.Command ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            values.Add(keyPrefix + "command=" + EncodeTomlString(command));
            var commandArguments = SplitManagedMcpArguments(server.Arguments).ToList();
            if (commandArguments.Count > 0)
            {
                values.Add(keyPrefix + "args=[" + string.Join(", ", commandArguments.Select(EncodeTomlString)) + "]");
            }
        }

        return values;
    }

    internal static IEnumerable<string> SplitArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            yield break;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var backslashCount = 0;

        foreach (var ch in commandLine!)
        {
            if (ch == '\\')
            {
                backslashCount++;
                continue;
            }

            if (ch == '"')
            {
                current.Append('\\', backslashCount / 2);
                if (backslashCount % 2 == 0)
                {
                    inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('"');
                }

                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                current.Append('\\', backslashCount);
                backslashCount = 0;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (backslashCount > 0)
        {
            current.Append('\\', backslashCount);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    internal static string JoinArguments(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(QuoteArgument));
    }

    /// <summary>
    /// Quotes one argument using the CommandLineToArgvW/MS C runtime rules.
    /// Backslashes are only doubled when they precede a quote or the closing
    /// quote, so ordinary Windows paths retain their original value.
    /// </summary>
    internal static string QuoteArgument(string value)
    {
        value ??= string.Empty;
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!value.Any(ch => char.IsWhiteSpace(ch) || ch == '"'))
        {
            return value;
        }

        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        var backslashCount = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashCount++;
                continue;
            }

            if (ch == '"')
            {
                result.Append('\\', backslashCount * 2 + 1);
                result.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                result.Append('\\', backslashCount);
                backslashCount = 0;
            }

            result.Append(ch);
        }

        result.Append('\\', backslashCount * 2);
        result.Append('"');
        return result.ToString();
    }

    internal static bool HasProfileArgument(string? commandLine)
    {
        return SplitArguments(commandLine).Any(token =>
            string.Equals(token, "--profile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "-p", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase));
    }

    internal static string EncodeTomlString(string value)
    {
        return "\"" + (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n") + "\"";
    }

    private static bool IsValidManagedMcpName(string name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');
    }

    private static IEnumerable<string> SplitManagedMcpArguments(string? text)
    {
        return (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static string? NormalizeVerbosity(string? verbosity)
    {
        var value = (verbosity ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => null
        };
    }
}
