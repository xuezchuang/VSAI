using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>A short-lived metadata/archive connection to the old store. Never resumes or runs a turn.</summary>
internal sealed class CodexMigrationSourceClient : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private int _nextId;

    internal CodexMigrationSourceClient(CodexExtensionSettings settings)
    {
        var executable = CodexExecutableResolver.ResolveExecutableLocation(settings.CodexExecutablePath, settings.EnvironmentVariables);
        if (string.IsNullOrWhiteSpace(executable)) throw new IOException("找不到用于迁移历史的 Codex CLI。");
        var start = CodexEnvironmentService.CreateServerProbeStartInfo(executable!, settings, migrationSource: true);
        foreach (var pair in CodexEnvironmentPathHelper.ParseEnvironmentVariables(settings.EnvironmentVariables))
            start.EnvironmentVariables[pair.Key] = pair.Value;
        CodexSessionStorage.ApplyEnvironment(start, settings.EnvironmentVariables, migrationSource: true);
        foreach (var provider in settings.Providers) CodexProviderConfigurationService.ApplyEnvironment(start, provider);
        _process = new Process { StartInfo = start };
        try
        {
            if (!_process.Start()) throw new IOException("无法启动历史迁移连接。");
            _input = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            // Drain diagnostics without collecting private paths, prompts or credentials.
            _ = DrainErrorsAsync(_process.StandardError);
        }
        catch
        {
            try { if (!_process.HasExited) _process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            _process.Dispose();
            throw;
        }
    }

    internal async Task InitializeAsync(CancellationToken token)
    {
        await SendCoreAsync("initialize", new JObject
        {
            ["clientInfo"] = new JObject { ["name"] = "vsai-history-migration", ["version"] = "1" },
            ["capabilities"] = new JObject { ["experimentalApi"] = true }
        }, token).ConfigureAwait(false);
        await _input.WriteLineAsync("{\"method\":\"initialized\"}").ConfigureAwait(false);
    }

    internal Task<JToken?> SendAsync(string method, JToken? parameters, CancellationToken token)
    {
        if (method != "thread/list" && method != "thread/read" && method != "thread/archive")
            throw new InvalidOperationException("Migration source connection only supports listing, reading and archiving history.");
        return SendCoreAsync(method, parameters, token);
    }

    private async Task<JToken?> SendCoreAsync(string method, JToken? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var id = ++_nextId;
        await _input.WriteLineAsync(NewtonsoftJsonCompatibility.Serialize(new JObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, Formatting.None)).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var delay = Task.Delay(Timeout.Infinite, timeout.Token);
        while (true)
        {
            var read = _process.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, delay).ConfigureAwait(false) != read)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("读取旧会话库超时；原始记录和迁移备份已保留，请稍后重试。");
            }
            var line = await read.ConfigureAwait(false);
            if (line is null) throw new IOException("历史迁移连接已关闭，尚未完成的原始记录不会删除。");
            // Pagination cursors can look like ISO timestamps. They are opaque
            // strings; default date parsing changes their value on the next page.
            var message = NewtonsoftJsonCompatibility.ParseProtocolValue(line) as JObject
                ?? throw new IOException("旧会话库返回了无效的协议消息。");
            if (message["method"] is not null)
            {
                if (message["id"] is JToken requestId)
                    await _input.WriteLineAsync(NewtonsoftJsonCompatibility.Serialize(new JObject
                    {
                        ["id"] = requestId.DeepClone(),
                        ["error"] = new JObject { ["code"] = -32601, ["message"] = "History migration does not execute tools or request approvals." }
                    }, Formatting.None)).ConfigureAwait(false);
                continue;
            }
            if (message["id"]?.Value<int>() != id) continue;
            if (message["error"] is JObject error)
                throw new IOException("旧会话库操作失败 (" + method + ", code " + error["code"] + ")；原始记录已保留。");
            timeout.Cancel();
            return message["result"];
        }
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        try { _input.Dispose(); } catch (IOException) { }
        try { if (!_process.WaitForExit(500)) _process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _process.Dispose();
    }
}
