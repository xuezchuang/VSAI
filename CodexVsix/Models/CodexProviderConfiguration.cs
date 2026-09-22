using System;
using System.Collections.Generic;

namespace CodexVsix.Models;

public sealed class CodexProviderConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public List<string> Models { get; set; } = new();
    public Dictionary<string, List<string>> ReasoningEfforts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> DefaultReasoningEfforts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> ContextWindows { get; set; } = new(StringComparer.Ordinal);
}
