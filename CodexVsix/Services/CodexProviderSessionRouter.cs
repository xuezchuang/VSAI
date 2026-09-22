using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>
/// Routes UI model aliases without changing the user's Codex configuration or auth.
/// The owner serializes requests and settings writes. Notifications may arrive concurrently.
/// </summary>
internal sealed class CodexProviderSessionRouter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, JObject> _resumeOptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JObject> _runtimeSettings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _loadedProviders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeThreads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _completed = new(StringComparer.Ordinal);
    private readonly Action<CodexExtensionSettings> _save;
    private int _starting;
    private long _completionVersion;
    private bool _hasOfficialAccount = true;
    private bool _managedRouting;

    internal CodexProviderSessionRouter(Action<CodexExtensionSettings> save) => _save = save;
    internal bool IsBusy { get { lock (_sync) return _starting != 0 || _activeThreads.Count != 0; } }

    internal async Task CaptureLoadedThreadSettingsAsync(Func<string, JToken?, CancellationToken, Task<JToken?>> send, CancellationToken token)
    {
        if (IsBusy) throw new InvalidOperationException("仍有任务正在运行，请等待完成后再切换服务。");
        string[] threads;
        lock (_sync) threads = _loadedProviders.Keys.ToArray();
        foreach (var id in threads)
        {
            var request = new JObject { ["threadId"] = id, ["excludeTurns"] = true };
            var response = await send("thread/resume", request, token).ConfigureAwait(false);
            RememberThread("thread/resume", request, response);
        }
    }

    internal void ServerStopped()
    {
        lock (_sync)
        {
            _loadedProviders.Clear();
            _activeThreads.Clear();
            _completed.Clear();
        }
    }

    internal void ObserveNotification(string method, JToken? parameters)
    {
        var id = parameters?["threadId"]?.Value<string>() ?? parameters?["thread"]?["id"]?.Value<string>();
        if (string.IsNullOrEmpty(id)) return;
        if (method == "thread/settings/updated" && parameters?["threadSettings"] is JObject threadSettings)
            RememberThread("thread/settings/update", new JObject { ["threadId"] = id }, threadSettings);
        lock (_sync)
        {
            if (method == "turn/started") _activeThreads.Add(id!);
            if (method == "turn/completed" || method == "thread/closed")
            {
                _activeThreads.Remove(id!);
                _completed[id!] = ++_completionVersion;
            }
            if (method == "thread/closed") _loadedProviders.Remove(id!);
            if (method == "thread/status/changed" || method == "thread/statusChanged")
            {
                var status = parameters?["status"];
                var type = status is JObject ? status["type"]?.Value<string>() : status?.Value<string>();
                if (type == "active") _activeThreads.Add(id!);
                if (type == "idle" || type == "notLoaded" || type == "systemError")
                {
                    _activeThreads.Remove(id!);
                    _completed[id!] = ++_completionVersion;
                    if (type == "notLoaded") _loadedProviders.Remove(id!);
                }
            }
        }
    }

    internal async Task<JToken?> InvokeAsync(
        CodexExtensionSettings settings, string method, JToken? parameters,
        Func<string, JToken?, CancellationToken, Task<JToken?>> send,
        Func<CancellationToken, Task> restart,
        CancellationToken cancellationToken)
    {
        var values = parameters?.DeepClone() as JObject ?? new JObject();
        _managedRouting |= settings.Providers.Count > 0;
        // A null/omitted filter can use the server's default provider. The shared history
        // must still show conversations made with a custom provider, including removed ones.
        if (method == "thread/list" && values["modelProviders"] is not JArray)
            values["modelProviders"] = new JArray();
        // Model preferences belong to this extension, including stale writes after provider deletion.
        if (method == "config/batchWrite" || method == "config/value/write")
        {
            var intercepted = await WriteModelConfigAsync(settings, method, values, send, cancellationToken).ConfigureAwait(false);
            if (intercepted is not null) return intercepted;
        }

        var routesModel = method == "thread/start" || method == "thread/resume" || method == "thread/fork"
            || method == "turn/start" || method == "thread/settings/update";
        var selection = routesModel ? ResolveSelection(settings, values, method) : null;
        if (selection is not null)
        {
            if (values["model"]?.Type == JTokenType.String || method == "thread/start" || method == "thread/resume")
                values["model"] = selection.Model;
            if (values["collaborationMode"]?["settings"] is JObject collaboration)
                collaboration["model"] = selection.Model;
            if (method == "thread/start" || method == "thread/resume" || method == "thread/fork")
                values["modelProvider"] = selection.Provider;
        }
        if (routesModel && IsCustomProviderRequest(values, selection)) values.Remove("serviceTier");

        var threadId = values["threadId"]?.Value<string>();
        JObject? restoreAfterResume = null;
        var needsLoadedThread = _managedRouting && (method == "turn/start" || method == "thread/settings/update"
            || method == "thread/compact/start" || method == "review/start");
        if (!string.IsNullOrEmpty(threadId) && (needsLoadedThread || method == "thread/resume"))
        {
            string? current;
            lock (_sync) _loadedProviders.TryGetValue(threadId!, out current);
            if (selection is not null && current is not null && current != selection.Provider)
                await RestartWhenIdleAsync(restart, cancellationToken).ConfigureAwait(false);

            lock (_sync) _loadedProviders.TryGetValue(threadId!, out current);
            if (method == "thread/resume" && current is null) restoreAfterResume = GetRuntimeSettings(threadId!);

            if (needsLoadedThread)
            {
                lock (_sync) _loadedProviders.TryGetValue(threadId!, out current);
                if (current is null)
                {
                    var resume = ResumeParameters(threadId!, selection);
                    var runtime = GetRuntimeSettings(threadId!);
                    var resumed = await send("thread/resume", Enrich(settings, "thread/resume", resume), cancellationToken).ConfigureAwait(false);
                    resumed = await RestoreRuntimeSettingsAsync(threadId!, runtime, resumed, send, cancellationToken).ConfigureAwait(false);
                    RememberThread("thread/resume", resume, resumed);
                    await VerifyProviderAsync(settings, threadId!, selection, resumed, send, restart, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // A lazy resume can identify a provider that was not yet in this process's cache.
        if (routesModel && IsCustomProviderRequest(values, selection)) values.Remove("serviceTier");
        var startsWork = method == "turn/start" || method == "review/start" || method == "thread/compact/start";
        long beforeCompletion;
        lock (_sync)
        {
            beforeCompletion = _completionVersion;
            if (startsWork) _starting++;
        }
        JToken? result;
        try
        {
            result = await send(method, routesModel ? Enrich(settings, method, values) : values, cancellationToken).ConfigureAwait(false);
            if (startsWork)
            {
                var activeId = result?["reviewThreadId"]?.Value<string>() ?? threadId;
                var turnStatus = result?["turn"]?["status"]?.Value<string>();
                lock (_sync)
                    if (!string.IsNullOrEmpty(activeId) && turnStatus != "completed" && turnStatus != "failed" && turnStatus != "interrupted"
                        && (!_completed.TryGetValue(activeId!, out var completedAt) || completedAt <= beforeCompletion))
                        _activeThreads.Add(activeId!);
            }
        }
        finally
        {
            if (startsWork) { lock (_sync) _starting--; }
        }

        if (restoreAfterResume is not null && threadId is not null)
        {
            // Explicit user changes on resume take precedence over the saved effective settings.
            foreach (var key in new[] { "cwd", "approvalPolicy", "approvalsReviewer", "serviceTier", "permissions" })
                if (values[key] is JToken value) restoreAfterResume[key] = value.DeepClone();
            if (values["sandbox"] is not null || values["permissions"] is not null) restoreAfterResume.Remove("sandboxPolicy");
            result = await RestoreRuntimeSettingsAsync(threadId, restoreAfterResume, result, send, cancellationToken).ConfigureAwait(false);
        }
        RememberThread(method, values, result);
        if ((method == "thread/start" || method == "thread/resume" || method == "thread/fork") && selection is not null)
        {
            var id = result?["thread"]?["id"]?.Value<string>();
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("服务没有返回有效会话，尚未发送消息。");
            result = await VerifyProviderAsync(settings, id!, selection, result, send, restart, cancellationToken).ConfigureAwait(false);
        }
        return TransformResponse(settings, method, values, result);
    }

    private CodexProviderModelCatalog.Selection? ResolveSelection(CodexExtensionSettings settings, JObject values, string method)
    {
        var model = values["collaborationMode"]?["settings"]?["model"]?.Value<string>() ?? values["model"]?.Value<string>();
        if (model is null && method == "thread/start")
        {
            model = settings.DefaultModel;
            if (string.IsNullOrWhiteSpace(model) && !_hasOfficialAccount && settings.Providers.Count > 0)
                model = CodexProviderModelCatalog.Alias(settings.Providers[0], settings.Providers[0].Models[0]);
        }
        // Without managed providers, leave existing profiles and ordinary model requests untouched.
        if (!_managedRouting && settings.Providers.Count == 0 && model?.StartsWith(CodexProviderModelCatalog.AliasPrefix, StringComparison.Ordinal) != true)
            return null;
        var selected = CodexProviderModelCatalog.Resolve(settings, model);
        if (selected is null && values["modelProvider"]?.Value<string>() is string provider && provider.StartsWith("vsai_", StringComparison.Ordinal))
            throw new InvalidOperationException("请从模型列表选择该服务的模型。");
        return selected;
    }

    private JToken? Enrich(CodexExtensionSettings settings, string method, JObject values)
    {
        var model = values["collaborationMode"]?["settings"]?["model"]?.Value<string>() ?? values["model"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(model) && values["threadId"]?.Value<string>() is string id)
        {
            lock (_sync)
                if (_resumeOptions.TryGetValue(id, out var options)) model = options["model"]?.Value<string>();
        }
        if (string.IsNullOrWhiteSpace(model)) model = CodexProviderModelCatalog.Resolve(settings, settings.DefaultModel)?.Model;
        // A global UI preference is not evidence of this thread's effective effort.
        // Settings updates may also carry collaboration instructions that need refreshing.
        var identityMethod = method == "thread/settings/update" ? "turn/start" : method;
        return CodexRuntimeIdentityContext.EnrichRequest(identityMethod, values, model, null);
    }

    private bool IsCustomProviderRequest(JObject values, CodexProviderModelCatalog.Selection? selection)
    {
        var provider = selection?.Provider;
        if (provider is null && values["threadId"]?.Value<string>() is string id)
        {
            lock (_sync)
            {
                if (!_loadedProviders.TryGetValue(id, out provider) && _resumeOptions.TryGetValue(id, out var remembered))
                    provider = remembered["modelProvider"]?.Value<string>();
            }
        }
        provider ??= values["modelProvider"]?.Value<string>();
        return provider?.StartsWith("vsai_", StringComparison.Ordinal) == true;
    }

    private async Task RestartWhenIdleAsync(Func<CancellationToken, Task> restart, CancellationToken token)
    {
        if (IsBusy) throw new InvalidOperationException("仍有任务正在运行，请等待完成或停止任务后再切换服务。");
        await restart(token).ConfigureAwait(false);
    }

    private async Task<JToken?> VerifyProviderAsync(CodexExtensionSettings settings, string threadId,
        CodexProviderModelCatalog.Selection? selection, JToken? result,
        Func<string, JToken?, CancellationToken, Task<JToken?>> send,
        Func<CancellationToken, Task> restart, CancellationToken token)
    {
        if (selection is null) return result;
        if (result?["modelProvider"]?.Value<string>() == selection.Provider) return result;
        // Loaded threads ignore resume overrides. Unload the process only after every turn is idle,
        // then resume from its persisted history. Never send a turn on an unverified provider.
        await RestartWhenIdleAsync(restart, token).ConfigureAwait(false);
        var resume = ResumeParameters(threadId, selection);
        var runtime = GetRuntimeSettings(threadId);
        result = await send("thread/resume", Enrich(settings, "thread/resume", resume), token).ConfigureAwait(false);
        result = await RestoreRuntimeSettingsAsync(threadId, runtime, result, send, token).ConfigureAwait(false);
        RememberThread("thread/resume", resume, result);
        if (result?["modelProvider"]?.Value<string>() != selection.Provider)
            throw new InvalidOperationException("Codex 未确认所选服务生效，已阻止发送。请检查 CLI 版本及服务配置。");
        return result;
    }

    private JObject? GetRuntimeSettings(string id)
    {
        lock (_sync) return _runtimeSettings.TryGetValue(id, out var value) ? (JObject)value.DeepClone() : null;
    }

    private static async Task<JToken?> RestoreRuntimeSettingsAsync(string id, JObject? runtime, JToken? resumed,
        Func<string, JToken?, CancellationToken, Task<JToken?>> send, CancellationToken token)
    {
        if (runtime is null || runtime.Count == 0) return resumed;
        runtime["threadId"] = id;
        var model = resumed?["model"]?.Value<string>();
        var provider = resumed?["modelProvider"]?.Value<string>();
        if (runtime["collaborationMode"]?["settings"] is JObject mode && model is not null) mode["model"] = model;
        if (provider?.StartsWith("vsai_", StringComparison.Ordinal) == true)
        {
            runtime["serviceTier"] = null;
        }
        if (runtime["collaborationMode"] is JObject)
            runtime = (JObject)CodexRuntimeIdentityContext.EnrichRequest("turn/start", runtime, model, runtime["effort"]?.Value<string>())!;
        await send("thread/settings/update", runtime, token).ConfigureAwait(false);
        var effective = await send("thread/resume", new JObject
        {
            ["threadId"] = id, ["excludeTurns"] = true, ["model"] = model, ["modelProvider"] = provider
        }, token).ConfigureAwait(false);
        if (runtime["sandboxPolicy"] is JObject policy && !JToken.DeepEquals(policy, effective?["sandbox"]))
            throw new InvalidOperationException("恢复后的权限与原会话不一致，已阻止发送，请重新打开会话确认权限。");
        if (runtime["approvalPolicy"] is JToken approval && !JToken.DeepEquals(approval, effective?["approvalPolicy"]))
            throw new InvalidOperationException("恢复后的审批设置与原会话不一致，已阻止发送，请重新打开会话确认权限。");
        if (effective is JObject metadata && resumed is JObject original)
        {
            var merged = (JObject)original.DeepClone();
            foreach (var property in metadata.Properties())
                if (property.Name != "thread" && property.Name != "initialTurnsPage") merged[property.Name] = property.Value.DeepClone();
            return merged;
        }
        return resumed;
    }

    private JObject ResumeParameters(string threadId, CodexProviderModelCatalog.Selection? selection)
    {
        JObject options;
        lock (_sync) options = _resumeOptions.TryGetValue(threadId, out var stored) ? (JObject)stored.DeepClone() : new JObject();
        options["threadId"] = threadId;
        options["excludeTurns"] = true;
        if (selection is not null)
        {
            options["model"] = selection.Model;
            options["modelProvider"] = selection.Provider;
        }
        if (IsCustomProviderRequest(options, selection)) options["serviceTier"] = null;
        return options;
    }

    private void RememberThread(string method, JObject request, JToken? response)
    {
        if (method != "thread/start" && method != "thread/resume" && method != "thread/fork" && method != "thread/settings/update") return;
        var settings = response?["settings"] as JObject ?? response as JObject;
        var id = response?["thread"]?["id"]?.Value<string>() ?? request["threadId"]?.Value<string>();
        if (settings is null || string.IsNullOrEmpty(id)) return;
        lock (_sync)
        {
            var options = _resumeOptions.TryGetValue(id!, out var previous) ? previous : new JObject();
            foreach (var key in new[] { "cwd", "model", "modelProvider", "approvalPolicy", "approvalsReviewer", "serviceTier", "runtimeWorkspaceRoots" })
                if (settings[key] is JToken value) options[key] = value.DeepClone();
            foreach (var key in new[] { "config", "baseInstructions", "developerInstructions", "permissions", "sandbox" })
                if (request[key] is JToken value) options[key] = value.DeepClone();
            // Resume accepts a sandbox mode, while responses expose the complete sandbox policy.
            // Preserve its constraints through config overrides rather than silently widening access.
            var sandbox = settings["sandboxPolicy"] as JObject ?? settings["sandbox"] as JObject;
            if (sandbox is not null && options["permissions"]?.Type != JTokenType.String)
            {
                var type = sandbox["type"]?.Value<string>();
                var mode = type == "readOnly" ? "read-only" : type == "workspaceWrite" ? "workspace-write"
                    : type == "dangerFullAccess" ? "danger-full-access" : null;
                if (mode is not null)
                {
                    options["sandbox"] = mode;
                    var config = options["config"] as JObject ?? new JObject();
                    if (type == "workspaceWrite")
                    {
                        var policy = new JObject();
                        foreach (var pair in new[] { ("writableRoots", "writable_roots"), ("networkAccess", "network_access"),
                            ("excludeTmpdirEnvVar", "exclude_tmpdir_env_var"), ("excludeSlashTmp", "exclude_slash_tmp") })
                            if (sandbox[pair.Item1] is JToken value) policy[pair.Item2] = value.DeepClone();
                        config["sandbox_workspace_write"] = policy;
                    }
                    options["config"] = config;
                }
            }
            _resumeOptions[id!] = options;
            var runtime = _runtimeSettings.TryGetValue(id!, out var existingRuntime) ? existingRuntime : new JObject();
            foreach (var key in new[] { "cwd", "approvalPolicy", "approvalsReviewer", "disabledPluginIds", "serviceTier", "effort", "summary", "collaborationMode", "personality" })
                if (settings[key] is JToken value) runtime[key] = value.DeepClone();
            if (settings["reasoningEffort"] is JToken effort) runtime["effort"] = effort.DeepClone();
            if (sandbox is not null) runtime["sandboxPolicy"] = sandbox.DeepClone();
            _runtimeSettings[id!] = runtime;
            var provider = settings["modelProvider"]?.Value<string>();
            if (provider is not null) _loadedProviders[id!] = provider;
        }
    }

    internal JToken? TransformNotification(CodexExtensionSettings settings, string method, JToken? parameters)
    {
        if (method != "thread/settings/updated" || parameters is not JObject original
            || original["threadSettings"] is not JObject threadSettings) return parameters;
        var visible = (JObject)original.DeepClone();
        var transformed = visible["threadSettings"]!;
        var provider = threadSettings["modelProvider"]?.Value<string>();
        if (threadSettings["model"]?.Value<string>() is string model)
            transformed["model"] = CodexProviderModelCatalog.ToDisplayModel(settings, provider, model);
        if (transformed["collaborationMode"]?["settings"] is JObject mode && mode["model"]?.Value<string>() is string modeModel)
            mode["model"] = CodexProviderModelCatalog.ToDisplayModel(settings, provider, modeModel);
        return visible;
    }

    private JToken? TransformResponse(CodexExtensionSettings settings, string method, JObject request, JToken? result)
    {
        if (method == "account/read")
        {
            _hasOfficialAccount = result?["account"] is JObject;
            if (!_hasOfficialAccount && settings.Providers.Count > 0 && result is JObject account)
            {
                var visible = (JObject)account.DeepClone();
                visible["requiresOpenaiAuth"] = false;
                return visible;
            }
        }
        if (method == "model/list" && request["cursor"]?.Type != JTokenType.String)
            return CodexProviderModelCatalog.Merge(settings, result, _hasOfficialAccount);
        if (method == "config/read" && result is JObject configuration)
        {
            var visible = (JObject)configuration.DeepClone();
            if (visible["config"] is JObject config)
            {
                var selected = settings.DefaultModel;
                if (string.IsNullOrWhiteSpace(selected) && !_hasOfficialAccount && settings.Providers.Count > 0)
                    selected = CodexProviderModelCatalog.Alias(settings.Providers[0], settings.Providers[0].Models[0]);
                if (!string.IsNullOrWhiteSpace(selected)) config["model"] = selected;
                if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort)) config["model_reasoning_effort"] = settings.ReasoningEffort;
                if (config["profile"]?.Value<string>() is string profile && config["profiles"]?[profile] is JObject profileConfig)
                {
                    if (!string.IsNullOrWhiteSpace(selected)) profileConfig["model"] = selected;
                    if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort)) profileConfig["model_reasoning_effort"] = settings.ReasoningEffort;
                }
            }
            return visible;
        }
        if (result is JObject record && (method == "thread/start" || method == "thread/resume" || method == "thread/fork"
            || method == "thread/settings/read" || method == "thread/settings/update"))
        {
            var visible = (JObject)record.DeepClone();
            var values = visible["settings"] as JObject ?? visible;
            var provider = values["modelProvider"]?.Value<string>();
            if (values["model"]?.Value<string>() is string model) values["model"] = CodexProviderModelCatalog.ToDisplayModel(settings, provider, model);
            if (values["collaborationMode"]?["settings"] is JObject mode && mode["model"]?.Value<string>() is string collaborationModel)
                mode["model"] = CodexProviderModelCatalog.ToDisplayModel(settings, provider, collaborationModel);
            return visible;
        }
        return result;
    }

    private async Task<JToken?> WriteModelConfigAsync(CodexExtensionSettings settings, string method, JObject values,
        Func<string, JToken?, CancellationToken, Task<JToken?>> send, CancellationToken token)
    {
        var edits = method == "config/batchWrite" ? values["edits"] as JArray : new JArray(values.DeepClone());
        if (edits is null) return null;
        var local = edits.OfType<JObject>().Where(e => IsModelSetting(e["keyPath"]?.Value<string>())).ToArray();
        if (local.Length == 0) return null;
        var model = settings.DefaultModel;
        var effort = settings.ReasoningEffort;
        foreach (var edit in local)
        {
            if (edit["keyPath"]!.Value<string>()!.EndsWith("model_reasoning_effort", StringComparison.Ordinal))
                effort = edit["value"]?.Value<string>() ?? string.Empty;
            else
            {
                model = edit["value"]?.Value<string>() ?? string.Empty;
                CodexProviderModelCatalog.Resolve(settings, model);
            }
        }
        JToken? response = null;
        if (method == "config/batchWrite")
        {
            values["edits"] = new JArray(edits.Where(e => e is not JObject obj || !local.Contains(obj)).Select(e => e.DeepClone()));
            if (((JArray)values["edits"]!).Count > 0) response = await send(method, values, token).ConfigureAwait(false);
        }
        var oldModel = settings.DefaultModel;
        var oldEffort = settings.ReasoningEffort;
        try
        {
            settings.DefaultModel = model;
            settings.ReasoningEffort = effort;
            _save(settings);
        }
        catch
        {
            settings.DefaultModel = oldModel;
            settings.ReasoningEffort = oldEffort;
            throw;
        }
        return response ?? new JObject
        {
            ["status"] = "ok", ["version"] = "vsai-local",
            ["filePath"] = new ExtensionSettingsStore().SettingsFilePath, ["overriddenMetadata"] = null
        };
    }

    private static bool IsModelSetting(string? key)
        => key == "model" || key == "model_reasoning_effort"
            || (key?.StartsWith("profiles.", StringComparison.Ordinal) == true
                && (key.EndsWith(".model", StringComparison.Ordinal) || key.EndsWith(".model_reasoning_effort", StringComparison.Ordinal)));
}
