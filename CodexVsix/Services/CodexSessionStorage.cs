using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            if (File.Exists(marker)) return;
            // A one-time configuration snapshot retains profiles and user guidance.
            // Account auth.json is not cloned: refresh tokens have their own
            // lifecycle. Existing provider settings inside config files are retained.
            var names = new List<string> { "config.toml", "AGENTS.md", "AGENTS.override.md", "models_cache.json" };
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
        finally { if (acquired) mutex.ReleaseMutex(); }
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
