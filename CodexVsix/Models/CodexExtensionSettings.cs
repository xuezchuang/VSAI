using System.Collections.Generic;
using Newtonsoft.Json;

namespace CodexVsix.Models;

public sealed class CodexExtensionSettings
{
    public string CodexExecutablePath { get; set; } = "codex.cmd";
    public string LanguageOverride { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public bool FollowSolutionDirectory { get; set; } = true;
    public string DefaultModel { get; set; } = "";
    public string ReasoningEffort { get; set; } = "";
    public string ModelVerbosity { get; set; } = "";
    public string ServiceTier { get; set; } = "";
    public string Profile { get; set; } = "";
    public string ApprovalPolicy { get; set; } = "";
    public string SandboxMode { get; set; } = "";
    public string FollowUpQueueMode { get; set; } = "queue";
    public string ComposerEnterBehavior { get; set; } = "enter";
    public string ReviewDelivery { get; set; } = "inline";
    public string AdditionalArguments { get; set; } = "";
    public string EnvironmentVariables { get; set; } = "";
    public string RawTomlOverrides { get; set; } = "";
    [JsonIgnore]
    public string CurrentThreadId { get; set; } = "";

    [JsonIgnore]
    public string LastThreadWorkingDirectory { get; set; } = "";
    public List<string> PromptHistory { get; set; } = new();
    public List<string> CustomModels { get; set; } = new();
    public List<CodexProviderConfiguration> Providers { get; set; } = new();
    public List<string> CustomReasoningEfforts { get; set; } = new();
    public List<string> CustomVerbosityOptions { get; set; } = new();
    public List<string> CustomServiceTiers { get; set; } = new();
    public List<CodexManagedMcpServer> ManagedMcpServers { get; set; } = new();
    public List<string> PreferredMcpServers { get; set; } = new();
    public bool PlanModeEnabled { get; set; } = false;
    public bool IncludeIdeContext { get; set; } = true;
    public bool IncludeHiddenModels { get; set; } = false;
    public bool OpenOnStartup { get; set; } = false;
    public bool AutoCompactLongConversations { get; set; } = false;
    public bool EnableDiagnosticLogging { get; set; } = false;
}
