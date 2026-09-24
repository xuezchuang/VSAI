using System;
using System.Collections.Generic;

namespace CodexVsix.Models;

/// <summary>Persisted capability information for a configured provider.</summary>
public sealed class CodexProviderCatalogConfiguration
{
    /// <summary>Retained for settings compatibility; catalog discovery is manual in VSAI.</summary>
    public bool AutoSync { get; set; }
    /// <summary>Discovery protocol: auto, openai, or midas.</summary>
    public string Source { get; set; } = "auto";
    public List<string> ManualModels { get; set; } = new();
    public List<CodexProviderModelMetadata> DiscoveredModels { get; set; } = new();
    public Dictionary<string, CodexProviderModelMetadata> Overrides { get; set; } = new(StringComparer.Ordinal);
    public List<string> HiddenModels { get; set; } = new();
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    /// <summary>never, success, partial, error, or auth-error.</summary>
    public string Status { get; set; } = "never";
    /// <summary>A user-safe summary that never contains a response body or credential.</summary>
    public string? Error { get; set; }
}

/// <summary>Known model capabilities. Null means the provider did not report the field.</summary>
public sealed class CodexProviderModelMetadata
{
    public string Id { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public long? ContextWindow { get; set; }
    public long? MaxOutputTokens { get; set; }
    public bool? SupportsImages { get; set; }
    public bool? SupportsTools { get; set; }
    public bool? SupportsReasoning { get; set; }
    public bool? SupportsResponses { get; set; }
    /// <summary>Null means unknown; an empty list means no selectable reasoning efforts.</summary>
    public List<string>? ReasoningEfforts { get; set; }
    public string? DefaultReasoningEffort { get; set; }
}

/// <summary>Result returned by a bounded discovery request.</summary>
public sealed class CodexProviderDiscoveryResult
{
    public CodexProviderDiscoveryResult(
        IReadOnlyList<CodexProviderModelMetadata> models,
        string status,
        string? error,
        DateTime attemptedUtc)
    {
        Models = models ?? throw new ArgumentNullException(nameof(models));
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Error = error;
        AttemptedUtc = attemptedUtc;
    }

    public IReadOnlyList<CodexProviderModelMetadata> Models { get; }
    public string Status { get; }
    public string? Error { get; }
    public DateTime AttemptedUtc { get; }

    // Discovery-only routing hint. It is deliberately not persisted as catalog state.
    internal bool OwnedByMidasGateway { get; set; }
}
