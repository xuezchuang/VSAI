using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexSessionStorageTests
{
    [Fact]
    public void PrepareSeedsConfigurationAndAgentsOnceWithoutCopyingSessionOrCredentialData()
    {
        using var shared = new TemporaryDirectory();
        var environment = EnvironmentFor(shared.Path);
        var privateHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(environment);
        WriteUtf8(Path.Combine(shared.Path, "config.toml"), "model = \"source\"");
        WriteUtf8(Path.Combine(shared.Path, "AGENTS.md"), "Source user instructions");
        WriteUtf8(Path.Combine(shared.Path, "AGENTS.override.md"), "Source override");
        WriteUtf8(Path.Combine(shared.Path, "provider.config.toml"), "provider source");
        WriteModelCache(shared.Path, "0.154.0", "2026-09-23T04:00:00Z", "official-model");
        WriteUtf8(Path.Combine(shared.Path, "auth.json"), "synthetic auth must stay shared");
        WriteUtf8(Path.Combine(shared.Path, "sessions", "desktop.jsonl"), "synthetic desktop session");
        WriteUtf8(Path.Combine(shared.Path, "archived_sessions", "desktop.jsonl"), "synthetic archived session");
        WriteUtf8(Path.Combine(shared.Path, "memory", "state.md"), "synthetic private memory");
        WriteUtf8(Path.Combine(shared.Path, "memories", "memory_summary.md"), "synthetic shared memory");
        WriteUtf8(Path.Combine(shared.Path, "state.sqlite"), "not a database copy");

        CodexSessionStorage.Prepare(environment);

        Assert.Equal("model = \"source\"", File.ReadAllText(Path.Combine(privateHome, "config.toml")));
        Assert.Equal("Source user instructions", File.ReadAllText(Path.Combine(privateHome, "AGENTS.md")));
        Assert.Equal("Source override", File.ReadAllText(Path.Combine(privateHome, "AGENTS.override.md")));
        Assert.True(File.Exists(Path.Combine(privateHome, "provider.config.toml")));
        Assert.True(File.Exists(Path.Combine(privateHome, "models_cache.json")));
        Assert.False(File.Exists(Path.Combine(privateHome, "auth.json")));
        Assert.False(File.Exists(Path.Combine(privateHome, "sessions", "desktop.jsonl")));
        Assert.False(File.Exists(Path.Combine(privateHome, "archived_sessions", "desktop.jsonl")));
        Assert.False(File.Exists(Path.Combine(privateHome, "memory", "state.md")));
        Assert.False(File.Exists(Path.Combine(privateHome, "memories", "memory_summary.md")));
        Assert.False(File.Exists(Path.Combine(privateHome, "state.sqlite")));

        WriteUtf8(Path.Combine(privateHome, "config.toml"), "model = \"user edit\"");
        WriteUtf8(Path.Combine(privateHome, "AGENTS.md"), "User edited instructions");
        WriteUtf8(Path.Combine(shared.Path, "config.toml"), "model = \"updated source\"");
        WriteUtf8(Path.Combine(shared.Path, "AGENTS.md"), "Updated source instructions");

        CodexSessionStorage.Prepare(environment);

        Assert.Equal("model = \"user edit\"", File.ReadAllText(Path.Combine(privateHome, "config.toml")));
        Assert.Equal("User edited instructions", File.ReadAllText(Path.Combine(privateHome, "AGENTS.md")));
    }

    [Fact]
    public void PrepareRefreshesOnlyAValidNewerNativeModelCache()
    {
        using var shared = new TemporaryDirectory();
        var environment = EnvironmentFor(shared.Path);
        var privateHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(environment);
        var privateCache = Path.Combine(privateHome, "models_cache.json");
        WriteModelCache(shared.Path, "0.154.0", "2026-09-23T04:00:00Z", "official-model");
        CodexSessionStorage.Prepare(environment);

        WriteModelCache(shared.Path, "0.155.0", "2026-09-24T00:33:56.605954700Z",
            "official-model", "gpt-6-luna");
        CodexSessionStorage.Prepare(environment);
        Assert.Contains("gpt-6-luna", File.ReadAllText(privateCache));
        var lastGood = File.ReadAllBytes(privateCache);

        WriteModelCache(shared.Path, "0.154.0", "2026-09-25T00:00:00Z", "older-cli-model");
        CodexSessionStorage.Prepare(environment);
        Assert.Equal(lastGood, File.ReadAllBytes(privateCache));

        WriteUtf8(Path.Combine(shared.Path, "models_cache.json"), "{\"models\":[");
        CodexSessionStorage.Prepare(environment);
        Assert.Equal(lastGood, File.ReadAllBytes(privateCache));
        Assert.False(File.Exists(Path.Combine(privateHome, "auth.json")));
        Assert.False(File.Exists(Path.Combine(privateHome, "state.sqlite")));
    }

    [Fact]
    public void PrepareDoesNotReplaceAValidPrivateCacheWithAnOlderFetch()
    {
        using var shared = new TemporaryDirectory();
        var environment = EnvironmentFor(shared.Path);
        var privateHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(environment);
        WriteModelCache(shared.Path, "0.155.0", "2026-09-24T01:00:00Z", "newer-model");
        CodexSessionStorage.Prepare(environment);
        var privateCache = Path.Combine(privateHome, "models_cache.json");
        var lastGood = File.ReadAllBytes(privateCache);

        WriteModelCache(shared.Path, "0.155.0", "2026-09-24T00:30:00Z", "stale-model");
        CodexSessionStorage.Prepare(environment);

        Assert.Equal(lastGood, File.ReadAllBytes(privateCache));
    }

    [Fact]
    public void ApplyEnvironmentForcesPrivateHomeAndSqliteHomeOverConflictingSettings()
    {
        using var shared = new TemporaryDirectory();
        var environment = EnvironmentFor(shared.Path, "CODEX_SQLITE_HOME=C:\\conflicting-sqlite\r\nHOME=C:\\conflicting-home");
        var start = new ProcessStartInfo { UseShellExecute = false };
        start.EnvironmentVariables["CODEX_HOME"] = @"C:\inherited-home";
        start.EnvironmentVariables["CODEX_SQLITE_HOME"] = @"C:\inherited-sqlite";

        CodexSessionStorage.ApplyEnvironment(start, environment);

        var privateHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(environment);
        Assert.Equal(privateHome, start.EnvironmentVariables["CODEX_HOME"]);
        Assert.Equal(privateHome, start.EnvironmentVariables["CODEX_SQLITE_HOME"]);
    }

    [Fact]
    public void StorageOverridesFollowRawOverridesAndRestorePrivateOwnership()
    {
        using var shared = new TemporaryDirectory();
        var settings = Settings(shared.Path,
            "sqlite_home=\"C:\\\\malicious\"\r\nlog_dir=\"C:\\\\malicious-log\"\r\nfeatures.memories=true\r\nmemories.generate_memories=true");

        var configValues = ConfigValues(CodexAppServerCommandLine.Build(settings));
        var privateHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        var sqlite = "sqlite_home=" + CodexAppServerCommandLine.EncodeTomlString(privateHome);
        var log = "log_dir=" + CodexAppServerCommandLine.EncodeTomlString(Path.Combine(privateHome, "log"));

        Assert.True(configValues.IndexOf("sqlite_home=\"C:\\\\malicious\"") < configValues.LastIndexOf(sqlite));
        Assert.True(configValues.IndexOf("log_dir=\"C:\\\\malicious-log\"") < configValues.LastIndexOf(log));
        Assert.Equal(sqlite, LastValue(configValues, "sqlite_home="));
        Assert.Equal(log, LastValue(configValues, "log_dir="));
        Assert.Equal("cli_auth_credentials_store=\"file\"", LastValue(configValues, "cli_auth_credentials_store="));
        Assert.Equal("features.memories=false", LastValue(configValues, "features.memories="));
        Assert.Equal("memories.generate_memories=false", LastValue(configValues, "memories.generate_memories="));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareRequestForcesPrivateStorageAndSharedMemoryWithoutMutatingTheOriginal(bool extendedPath)
    {
        using var shared = new TemporaryDirectory();
        var settings = Settings(shared.Path);
        var privateHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        WriteUtf8(Path.Combine(shared.Path, "memories", "memory_summary.md"), "Synthetic shared context.");
        var original = new JObject
        {
            ["path"] = (extendedPath ? @"\\?\" : string.Empty) + Path.Combine(privateHome, "sessions", "vsai.jsonl"),
            ["developerInstructions"] = "Preserve user instruction.",
            ["config"] = new JObject
            {
                ["sqlite_home"] = @"C:\outside",
                ["log_dir"] = @"C:\outside-log",
                ["features.memories"] = true
            }
        };
        var baseline = (JObject)original.DeepClone();

        var prepared = Assert.IsType<JObject>(CodexSessionStorage.PrepareRequest(settings, "thread/resume", original));

        Assert.Equal(privateHome, prepared["config"]?["sqlite_home"]?.Value<string>());
        Assert.Equal(Path.Combine(privateHome, "sessions", "vsai.jsonl"), prepared["path"]?.Value<string>());
        Assert.Equal("file", prepared["config"]?["cli_auth_credentials_store"]?.Value<string>());
        Assert.Equal(Path.Combine(privateHome, "log"), prepared["config"]?["log_dir"]?.Value<string>());
        Assert.False(prepared["config"]?["features.memories"]?.Value<bool>() ?? true);
        Assert.False(prepared["config"]?["memories.generate_memories"]?.Value<bool>() ?? true);
        Assert.False(prepared["config"]?["memories.use_memories"]?.Value<bool>() ?? true);
        Assert.Contains("Preserve user instruction.", prepared["developerInstructions"]?.Value<string>());
        Assert.Contains("Synthetic shared context.", prepared["developerInstructions"]?.Value<string>());
        Assert.Contains(Path.Combine(shared.Path, "memories", "MEMORY.md"), prepared["developerInstructions"]?.Value<string>());
        Assert.True(JToken.DeepEquals(baseline, original));
    }

    [Fact]
    public void PrepareRequestRejectsDesktopRolloutsOutsideThePrivateHome()
    {
        using var shared = new TemporaryDirectory();
        var settings = Settings(shared.Path);
        var desktopRequest = new JObject { ["path"] = Path.Combine(shared.Path, "sessions", "desktop.jsonl") };

        Assert.Throws<InvalidOperationException>(() =>
            CodexSessionStorage.PrepareRequest(settings, "thread/resume", desktopRequest));
    }

    [Fact]
    public void LastTurnModelComesOnlyFromTheMatchingPrivateRollout()
    {
        using var shared = new TemporaryDirectory();
        var settings = Settings(shared.Path);
        var privateHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(settings.EnvironmentVariables);
        var path = Path.Combine(privateHome, "sessions", "thread-1.jsonl");
        WriteUtf8(path, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-1\",\"model_provider\":\"vsai_provider\"}}\n"
            + "{\"type\":\"turn_context\",\"payload\":{\"model\":\"older-model\"}}\n"
            + "{\"type\":\"turn_context\",\"payload\":{\"model\":\"last-model\"}}\n");

        var last = CodexSessionStorage.ReadLastTurnModel(settings, "thread-1", path);
        Assert.True(last.HasValue);
        Assert.Equal("last-model", last.Value.Model);
        Assert.Equal("vsai_provider", last.Value.Provider);
        Assert.Throws<InvalidDataException>(() => CodexSessionStorage.ReadLastTurnModel(settings, "another-thread", path));

        var desktop = Path.Combine(shared.Path, "sessions", "desktop-only.jsonl");
        WriteUtf8(desktop, "{\"type\":\"turn_context\",\"payload\":{\"model\":\"wrong-home\"}}\n");
        Assert.Throws<InvalidOperationException>(() => CodexSessionStorage.ReadLastTurnModel(settings, "desktop-only", desktop));
    }

    [Fact]
    public void MigrationSourceUsesSharedHomeButPreservesItsExistingSqliteHomeWhileDisablingMemories()
    {
        using var shared = new TemporaryDirectory();
        var settings = Settings(shared.Path);
        var start = new ProcessStartInfo { UseShellExecute = false };
        var existingSqliteHome = Path.Combine(shared.Path, "legacy-sqlite");
        start.EnvironmentVariables["CODEX_SQLITE_HOME"] = existingSqliteHome;

        CodexSessionStorage.ApplyEnvironment(start, settings.EnvironmentVariables, migrationSource: true);
        var overrides = CodexSessionStorage.BuildConfigOverrides(settings, migrationSource: true);

        Assert.Equal(shared.Path, start.EnvironmentVariables["CODEX_HOME"]);
        Assert.Equal(existingSqliteHome, start.EnvironmentVariables["CODEX_SQLITE_HOME"]);
        Assert.Contains("features.memories=false", overrides);
        Assert.Contains("memories.generate_memories=false", overrides);
        Assert.Contains("memories.use_memories=false", overrides);
        Assert.DoesNotContain(overrides, value => value.StartsWith("sqlite_home=", StringComparison.Ordinal));
        Assert.DoesNotContain(overrides, value => value.StartsWith("log_dir=", StringComparison.Ordinal));
    }

    [Fact]
    public void LoginStartInfoUsesPrivateEnvironmentWithoutLaunchingAProcess()
    {
        using var shared = new TemporaryDirectory();
        var environment = EnvironmentFor(shared.Path, "CODEX_SQLITE_HOME=C:\\conflicting-sqlite");

        var start = CodexEnvironmentService.CreateLoginStartInfo(@"C:\tools\codex.cmd", environment);
        var privateHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(environment);

        Assert.False(start.UseShellExecute);
        Assert.Contains("cli_auth_credentials_store='file'", start.Arguments);
        Assert.Equal(privateHome, start.EnvironmentVariables["CODEX_HOME"]);
        Assert.Equal(privateHome, start.EnvironmentVariables["CODEX_SQLITE_HOME"]);
    }

    private static CodexExtensionSettings Settings(string sharedHome, string rawTomlOverrides = "")
    {
        return new CodexExtensionSettings
        {
            EnvironmentVariables = EnvironmentFor(sharedHome),
            RawTomlOverrides = rawTomlOverrides
        };
    }

    private static string EnvironmentFor(string sharedHome, string? additional = null)
    {
        return "CODEX_HOME=" + sharedHome
            + (string.IsNullOrWhiteSpace(additional) ? string.Empty : Environment.NewLine + additional);
    }

    private static List<string> ConfigValues(string commandLine)
    {
        var tokens = CodexAppServerCommandLine.SplitArguments(commandLine).ToArray();
        var values = new List<string>();
        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            if (tokens[index] == "-c") values.Add(tokens[++index]);
        }

        return values;
    }

    private static string LastValue(IReadOnlyList<string> values, string prefix)
    {
        for (var index = values.Count - 1; index >= 0; index--)
        {
            if (values[index].StartsWith(prefix, StringComparison.Ordinal)) return values[index];
        }

        throw new Xunit.Sdk.XunitException("No configuration value starts with " + prefix + ".");
    }

    private static void WriteUtf8(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
    }

    private static void WriteModelCache(string home, string version, string fetchedAt, params string[] slugs)
    {
        WriteUtf8(Path.Combine(home, "models_cache.json"), new JObject
        {
            ["client_version"] = version,
            ["fetched_at"] = fetchedAt,
            ["models"] = new JArray(slugs.Select(slug => new JObject { ["slug"] = slug }))
        }.ToString());
    }
}
