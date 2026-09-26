using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>Owns VSAI's private runtime state; never links or copies desktop session databases.</summary>
internal static class CodexSessionStorage
{
    internal static void Prepare(string? environmentVariables)
    {
        var source = CodexEnvironmentPathHelper.GetSharedCodexHomeDirectory(environmentVariables);
        var target = CodexEnvironmentPathHelper.GetCodexHomeDirectory(environmentVariables);
        EnsurePrivateDirectory(target);
        using var mutex = new Mutex(false, ExtensionSettingsStore.BuildSettingsMutexName(Path.Combine(target, "bootstrap")));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("VSAI 会话库正在初始化，请稍后重试。");
            foreach (var name in new[] { "sessions", "archived_sessions", "log", "migration-backups" })
                EnsurePrivateDirectory(Path.Combine(target, name));

            var marker = Path.Combine(target, ".vsai-storage-v1.json");
            if (!File.Exists(marker))
            {
                // Configuration and user guidance are snapshots; account credentials
                // and conversation state remain owned by their respective homes.
                var names = new List<string> { "config.toml", "AGENTS.md", "AGENTS.override.md" };
                if (Directory.Exists(source))
                    names.AddRange(Directory.EnumerateFiles(source, "*.config.toml", SearchOption.TopDirectoryOnly).Select(path => Path.GetFileName(path)));
                foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var from = Path.Combine(source, name);
                    var to = Path.Combine(target, name);
                    if (File.Exists(from) && !File.Exists(to))
                    {
                        if (name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) CodexConfigurationSnapshot.Copy(from, to);
                        else CopyAtomically(from, to);
                    }
                }
                WriteNewFile(marker, new JObject { ["version"] = 1, ["sharedHome"] = source }.ToString());
            }
            RefreshOfficialModelCache(source, target);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static void RefreshOfficialModelCache(string sourceHome, string privateHome)
    {
        var source = Path.Combine(sourceHome, "models_cache.json");
        var target = Path.Combine(privateHome, "models_cache.json");
        if (!TryReadModelCache(source, out var sourceBytes, out var sourceVersion, out var sourceFetched)) return;
        if (TryReadModelCache(target, out _, out var targetVersion, out var targetFetched)
            && (sourceVersion.CompareTo(targetVersion) < 0 || sourceFetched <= targetFetched)) return;

        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return;
            File.WriteAllBytes(temporary, sourceBytes);
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        catch (IOException) { /* A CLI may be writing its cache; keep the last good copy. */ }
        catch (UnauthorizedAccessException) { /* The model cache is optional runtime data. */ }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TryReadModelCache(string path, out byte[] bytes, out Version version, out DateTimeOffset fetched)
    {
        bytes = Array.Empty<byte>();
        version = new Version(0, 0);
        fetched = default;
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            var before = new FileInfo(path);
            var length = before.Length;
            var lastWrite = before.LastWriteTimeUtc;
            if (length <= 0 || length > 16 * 1024 * 1024) return false;
            bytes = File.ReadAllBytes(path);
            var after = new FileInfo(path);
            if (length != bytes.Length || after.Length != length
                || after.LastWriteTimeUtc != lastWrite) return false;
            var cache = JObject.Parse(Encoding.UTF8.GetString(bytes));
            if (!Version.TryParse(cache["client_version"]?.Value<string>(), out version)
                || !DateTimeOffset.TryParse(cache["fetched_at"]?.Value<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out fetched)
                || cache["models"] is not JArray models || models.Count == 0) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            return models.All(model => model is JObject record
                && record["slug"]?.Value<string>() is string id && !string.IsNullOrWhiteSpace(id) && ids.Add(id));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (Newtonsoft.Json.JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static void ApplyEnvironment(ProcessStartInfo startInfo, string? environmentVariables, bool migrationSource = false)
    {
        var home = migrationSource
            ? CodexEnvironmentPathHelper.GetSharedCodexHomeDirectory(environmentVariables)
            : CodexEnvironmentPathHelper.GetCodexHomeDirectory(environmentVariables);
        startInfo.EnvironmentVariables["CODEX_HOME"] = home;
        // An inherited CODEX_SQLITE_HOME must not reconnect the private client to
        // the desktop's index, even when config.toml has no sqlite_home assignment.
        if (!migrationSource) startInfo.EnvironmentVariables["CODEX_SQLITE_HOME"] = home;
    }

    internal static IReadOnlyList<string> BuildConfigOverrides(CodexExtensionSettings settings, bool migrationSource = false)
    {
        var home = migrationSource
            ? CodexEnvironmentPathHelper.GetSharedCodexHomeDirectory(settings.EnvironmentVariables)
            : CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        var values = new List<string>
        {
            "features.memories=false",
            "memories.generate_memories=false",
            "memories.use_memories=false"
        };
        if (!migrationSource)
        {
            values.Add("cli_auth_credentials_store=\"file\"");
            values.Add("sqlite_home=" + CodexAppServerCommandLine.EncodeTomlString(home));
            values.Add("log_dir=" + CodexAppServerCommandLine.EncodeTomlString(Path.Combine(home, "log")));
        }
        return values;
    }

    internal static JToken? PrepareRequest(CodexExtensionSettings settings, string method, JToken? parameters)
    {
        if (method != "thread/start" && method != "thread/resume" && method != "thread/fork") return parameters;
        var request = parameters?.DeepClone() as JObject ?? new JObject();
        var home = CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        if (request["path"]?.Value<string>() is string path)
        {
            path = NormalizeAbsolutePath(path);
            if (!Path.IsPathRooted(path) || !IsWithinDirectory(path, home))
                throw new InvalidOperationException("此会话不在 VSAI 会话库中。请先完成历史迁移，不能直接恢复桌面端的会话文件。");
            EnsureOwnedPath(home, path);
            request["path"] = path;
        }
        if (request["config"] is JToken value && value.Type != JTokenType.Null && value is not JObject)
            throw new ArgumentException("Thread config must be an object.", nameof(parameters));
        var config = request["config"] as JObject ?? new JObject();
        config["cli_auth_credentials_store"] = "file";
        config["sqlite_home"] = home;
        config["log_dir"] = Path.Combine(home, "log");
        config["memories.generate_memories"] = false;
        config["memories.use_memories"] = false;
        config["features.memories"] = false;
        request["config"] = config;
        return CodexSharedMemoryContext.EnrichRequest(method, request,
            CodexEnvironmentPathHelper.GetSharedCodexHomeDirectory(settings.EnvironmentVariables));
    }

    internal static (string Model, string? Provider)? ReadLastTurnModel(
        CodexExtensionSettings settings, string threadId, string? reportedPath)
    {
        var home = CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        var path = CodexProcessService.ResolvePrivateSessionPath(reportedPath, threadId, home);
        if (path is null || !File.Exists(path)) return null;

        string? sourceId = null;
        string? model = null;
        string? provider = null;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                // Most rollout entries contain private prompts or tool output. Parse
                // only the two metadata envelopes needed for model restoration.
                if (!line.Contains("\"session_meta\"") && !line.Contains("\"turn_context\"")) continue;
                JObject entry;
                try { entry = JObject.Parse(line.TrimStart('\ufeff')); }
                catch (Newtonsoft.Json.JsonException) { continue; } // Interrupted final JSONL write.
                var type = entry["type"]?.Value<string>();
                var payload = entry["payload"];
                if (type == "session_meta")
                {
                    sourceId ??= payload?["id"]?.Value<string>();
                    provider ??= payload?["model_provider"]?.Value<string>()
                        ?? payload?["modelProvider"]?.Value<string>();
                }
                else if (type == "turn_context")
                {
                    var last = payload?["model"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(last)) model = last;
                    provider = payload?["model_provider"]?.Value<string>()
                        ?? payload?["modelProvider"]?.Value<string>() ?? provider;
                }
            }
        }
        if (sourceId is not null && sourceId != threadId)
            throw new InvalidDataException("VSAI 会话文件与请求的会话 ID 不一致。");
        return sourceId is not null && !string.IsNullOrWhiteSpace(model) ? (model!, provider) : null;
    }

    internal static bool IsWithinDirectory(string path, string directory)
    {
        var fullPath = NormalizeAbsolutePath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = NormalizeAbsolutePath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAbsolutePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var drive = path.Substring(4);
            if (drive.Length < 3 || !char.IsLetter(drive[0]) || drive[1] != ':' || drive[2] != '\\')
                throw new InvalidOperationException("Unsupported device session path.");
            path = drive;
        }
        if (!Path.IsPathRooted(path)) throw new InvalidOperationException("A session path must be absolute.");
        return Path.GetFullPath(path);
    }

    internal static async Task<FileStream> AcquireMigrationLeaseAsync(string privateHome, CancellationToken token)
    {
        var path = Path.Combine(privateHome, ".vsai-migration.lock");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("VSAI 会话目录不能是指向其他会话库的链接: " + path);
        Directory.CreateDirectory(path);
    }

    private static void EnsureOwnedPath(string home, string path)
    {
        home = NormalizeAbsolutePath(home);
        var current = NormalizeAbsolutePath(path);
        while (IsWithinDirectory(current, home) || string.Equals(current, home, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("VSAI 会话路径不能链接到其他会话库: " + current);
            if (string.Equals(current, home, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static void CopyAtomically(string from, string to)
    {
        var temporary = to + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(from, temporary); File.Move(temporary, to); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteNewFile(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
