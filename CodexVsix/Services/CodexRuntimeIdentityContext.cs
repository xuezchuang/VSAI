using System;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal static class CodexRuntimeIdentityContext
{
    private const string StartMarker = "[[visual-codex-studio-runtime-metadata:v1]]";
    private const string EndMarker = "[[/visual-codex-studio-runtime-metadata]]";
    private const string SourceLinksStartMarker = "[[visual-codex-studio-source-links:v1]]";
    private const string SourceLinksEndMarker = "[[/visual-codex-studio-source-links]]";

    public static JToken? EnrichRequest(
        string method,
        JToken? parameters,
        string? fallbackModel,
        string? fallbackReasoningEffort)
    {
        if (parameters is not JObject source)
        {
            return parameters?.DeepClone();
        }

        var enriched = (JObject)source.DeepClone();
        var model = ResolveModel(enriched, fallbackModel);
        var reasoningEffort = ResolveReasoningEffort(enriched, fallbackReasoningEffort);
        if (SupportsThreadDeveloperInstructions(method))
        {
            enriched["developerInstructions"] = BuildHostInstructions(
                enriched["developerInstructions"]?.Value<string>(),
                model, reasoningEffort);
        }
        else if (string.Equals(method, "turn/start", StringComparison.Ordinal)
            && enriched["collaborationMode"]?["settings"] is JObject modeSettings)
        {
            modeSettings["developer_instructions"] = BuildHostInstructions(
                modeSettings["developer_instructions"]?.Value<string>(),
                model, reasoningEffort);
        }

        return enriched;
    }

    private static string? ResolveModel(JObject parameters, string? fallbackModel)
    {
        return NormalizeMetadata(parameters["collaborationMode"]?["settings"]?["model"]?.Value<string>())
            ?? NormalizeMetadata(parameters["model"]?.Value<string>())
            ?? NormalizeMetadata(fallbackModel);
    }

    private static string? ResolveReasoningEffort(JObject parameters, string? fallbackReasoningEffort)
    {
        // Collaboration settings determine the effective effort. Their null/default
        // value must not acquire a conflicting top-level or previously selected effort.
        if (parameters["collaborationMode"]?["settings"] is JObject mode)
            return NormalizeMetadata(mode["reasoning_effort"]?.Value<string>());
        if (parameters["effort"] is JToken effort)
            return NormalizeMetadata(effort.Value<string>());
        if (parameters["reasoningEffort"] is JToken reasoningEffort)
            return NormalizeMetadata(reasoningEffort.Value<string>());
        return NormalizeMetadata(fallbackReasoningEffort);
    }

    private static bool SupportsThreadDeveloperInstructions(string method)
    {
        return string.Equals(method, "thread/start", StringComparison.Ordinal)
            || string.Equals(method, "thread/resume", StringComparison.Ordinal)
            || string.Equals(method, "thread/fork", StringComparison.Ordinal);
    }

    private static string BuildRuntimeInstructions(string model, string? reasoningEffort)
    {
        var effortText = string.IsNullOrWhiteSpace(reasoningEffort)
            ? string.Empty
            : $" The requested reasoning effort is \"{reasoningEffort}\".";
        return StartMarker + Environment.NewLine
            + $"The Visual Studio host reports that the model requested for this turn is \"{model}\".{effortText} "
            + "When asked which model is in use, report this exact host-provided model identifier instead of answering only \"Codex\"."
            + Environment.NewLine
            + EndMarker;
    }

    private static string BuildHostInstructions(string? existing, string? model, string? reasoningEffort)
    {
        var instructions = existing;
        if (!string.IsNullOrWhiteSpace(model))
        {
            instructions = UpsertInstructions(instructions, BuildRuntimeInstructions(model!, reasoningEffort), StartMarker, EndMarker);
        }

        var sourceLinks = SourceLinksStartMarker + Environment.NewLine
            + "This conversation is displayed inside Visual Codex Studio in Visual Studio. "
            + "When pointing to existing source code, give its clickable file-and-line link instead of pasting a large code block. "
            + "Include code snippets only when requested or necessary to explain the answer. "
            + "For source-code locations in answers, use clickable Markdown links outside code fences and inline-code spans. "
            + "For this VSIX conversation, this replaces a generic convention of listing source locations in plain text code blocks; "
            + "an explicit request in the current conversation for a different output format still takes precedence. "
            + "Use a verified absolute Windows file path as the destination, with forward slashes, wrapped in angle brackets, "
            + "followed by a 1-based line number and optionally a 1-based column: "
            + "[File.cpp:42](<D:/project/src/File.cpp:42>) or [File.cpp:42:7](<D:/project/src/File.cpp:42:7>). "
            + "Keep spaces, Unicode and literal # characters in the destination; encode each literal % as %25 exactly once. "
            + "For example, the file D:/工程 空格/100% #/File.cpp at line 42 is "
            + "[File.cpp:42](<D:/工程 空格/100%25 #/File.cpp:42>). "
            + "Include one verified start line on every source-location link, not a line range; omit an unknown column. "
            + "Verify paths and line numbers before linking instead of inventing locations. "
            + "Keep code snippets in code blocks, with their source-location links in the surrounding prose."
            + Environment.NewLine + SourceLinksEndMarker;
        return UpsertInstructions(instructions, sourceLinks, SourceLinksStartMarker, SourceLinksEndMarker);
    }

    private static string UpsertInstructions(string? existing, string instructions, string startMarker, string endMarker)
    {
        var preserved = RemoveInstructions(existing, startMarker, endMarker).Trim();
        return string.IsNullOrWhiteSpace(preserved)
            ? instructions
            : preserved + Environment.NewLine + Environment.NewLine + instructions;
    }

    private static string RemoveInstructions(string? value, string startMarker, string endMarker)
    {
        var result = value ?? string.Empty;
        while (true)
        {
            var start = result.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return result;
            }

            var end = result.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                return result.Substring(0, start);
            }

            result = result.Remove(start, end + endMarker.Length - start);
        }
    }

    private static string? NormalizeMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value!.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 160 ? normalized : normalized.Substring(0, 160);
    }
}
