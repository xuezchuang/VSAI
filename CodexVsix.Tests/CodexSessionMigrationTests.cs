using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexSessionMigrationTests
{
    [Fact]
    public async Task BacksUpAnEmptyRolloutWithoutCreatingOrArchivingAConversation()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var path = WriteRollout(shared.Path, "sessions", "empty.jsonl", "codex-vsix", "model");
        File.WriteAllLines(path, File.ReadAllLines(path).Take(2), new UTF8Encoding(false));
        var source = Thread("empty", path, "openai", false, new JArray());
        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, values, _) =>
            {
                Assert.Equal("thread/list", method);
                return Task.FromResult<JToken?>(new JObject { ["data"] = values?["archived"]?.Value<bool>() == false ? new JArray(source) : new JArray() });
            },
            (_, _, _) => throw new Xunit.Sdk.XunitException("An empty rollout must not create a target conversation."), CancellationToken.None);
        Assert.True(complete);
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(isolated.Path, "session-migration-manifest.json")));
        var record = Assert.Single((JArray)manifest["records"]!);
        Assert.Equal("empty-rollout-backup-only", record["disposition"]?.Value<string>());
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(record["backupPath"]!.Value<string>()!));
        Assert.Null(record["targetThreadId"]);
    }

    [Fact]
    public async Task NeverArchivesWhenTheForkStillPointsAtTheSharedRollout()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var path = WriteRollout(shared.Path, "sessions", "source.jsonl", "codex-vsix", "model");
        var source = Thread("source", path, "openai", false, Turns());
        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, values, _) =>
            {
                if (method == "thread/list") return Task.FromResult<JToken?>(new JObject { ["data"] = values?["archived"]?.Value<bool>() == false ? new JArray(source) : new JArray() });
                Assert.Equal("thread/read", method);
                return Task.FromResult<JToken?>(new JObject { ["thread"] = source.DeepClone() });
            },
            (method, _, _) => Task.FromResult<JToken?>(new JObject { ["thread"] = Thread("target", path, "openai", false, Turns()) }), CancellationToken.None);
        Assert.False(complete);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task MigratesVsaiProviderWithExactBackupThenArchivesOnlyTheSource(bool useExtendedRolloutPath, bool keepWriterOpen, bool paginated)
    {
        using var shared = new TemporaryDirectory();
        var isolatedPath = Path.Combine(shared.Path, "vsai");
        var sourcePath = WriteRollout(shared.Path, "sessions", "source.jsonl", "codex-vsix", "custom-model");
        if (paginated)
        {
            var lines = File.ReadAllLines(sourcePath);
            var head = JObject.Parse(lines[0]);
            head["payload"]!["history_mode"] = "paginated";
            lines[0] = head.ToString(Newtonsoft.Json.Formatting.None);
            File.WriteAllLines(sourcePath, lines, new UTF8Encoding(false));
        }
        var originalSource = File.ReadAllText(sourcePath);
        using var idleWriter = keepWriterOpen ? new FileStream(sourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete) : null;
        var listedPath = useExtendedRolloutPath ? ToExtendedLocalPath(sourcePath) : sourcePath;
        var sourceThread = Thread("source", listedPath, "vsai_provider", archived: false, Turns());
        sourceThread["source"] = "cli";
        sourceThread["cwd"] = "D:\\work\\example";
        var sourceCalls = new List<string>();
        var targetCalls = new List<string>();
        var targetId = paginated ? "source" : "target";

        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolatedPath,
            (method, values, _) =>
            {
                sourceCalls.Add(method);
                if (method == "thread/list") return Task.FromResult<JToken?>(new JObject { ["data"] = values?["archived"]?.Value<bool>() == false ? new JArray(sourceThread) : new JArray(), ["nextCursor"] = JValue.CreateNull() });
                if (method == "thread/read") return Task.FromResult<JToken?>(new JObject { ["thread"] = sourceThread.DeepClone() });
                Assert.Equal("thread/archive", method);
                Assert.Equal("source", values?["threadId"]?.Value<string>());
                return Task.FromResult<JToken?>(new JObject());
            },
            (method, values, _) =>
            {
                targetCalls.Add(method);
                if (method == (paginated ? "thread/resume" : "thread/fork"))
                {
                    Assert.Equal("vsai_provider", values?["modelProvider"]?.Value<string>());
                    Assert.Equal("D:\\work\\example", values?["cwd"]?.Value<string>());
                    if (!paginated) Assert.True(values?["deferGoalContinuation"]?.Value<bool>());
                    else
                    {
                        var stagedPath = values!["path"]!.Value<string>()!;
                        Assert.StartsWith(isolatedPath + Path.DirectorySeparatorChar + "sessions", stagedPath, StringComparison.Ordinal);
                        Assert.Equal("paginated", JObject.Parse(File.ReadLines(stagedPath).First())["payload"]?["history_mode"]?.Value<string>());
                    }
                    Assert.True(values?["excludeTurns"]?.Value<bool>());
                    return Task.FromResult<JToken?>(new JObject { ["thread"] = new JObject { ["id"] = targetId } });
                }
                if (method == "thread/read") return Task.FromResult<JToken?>(new JObject { ["thread"] = Thread(targetId, null, "vsai_provider", false, Turns()) });
                if (method == "thread/name/set") return Task.FromResult<JToken?>(new JObject());
                throw new Xunit.Sdk.XunitException("Unexpected target method: " + method);
            }, CancellationToken.None);

        Assert.True(complete);
        Assert.True(CodexSessionMigration.HasCompletedMigration(shared.Path, isolatedPath));
        Assert.Contains("thread/archive", sourceCalls);
        Assert.Equal(new[] { paginated ? "thread/resume" : "thread/fork", "thread/read", "thread/name/set" }, targetCalls);
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(isolatedPath, "session-migration-manifest.json")));
        var record = Assert.Single((JArray)manifest["records"]!);
        Assert.Equal("completed", record["state"]?.Value<string>());
        var backup = record["backupPath"]!.Value<string>();
        Assert.Equal(originalSource, File.ReadAllText(backup!));
    }

    [Fact]
    public async Task MigratesVsaiOriginArchivedThreadAndArchivesTheTargetOnly()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var sourcePath = WriteRollout(shared.Path, "archived_sessions", "source.jsonl", "codex-vsix", "official-model");
        var sourceThread = Thread("source", sourcePath, "openai", archived: true, Turns());
        var sourceArchiveCalls = 0;
        var targetArchiveCalls = 0;

        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, values, _) =>
            {
                if (method == "thread/list") return Task.FromResult<JToken?>(new JObject { ["data"] = values?["archived"]?.Value<bool>() == true ? new JArray(sourceThread) : new JArray(), ["nextCursor"] = JValue.CreateNull() });
                if (method == "thread/read") return Task.FromResult<JToken?>(new JObject { ["thread"] = sourceThread.DeepClone() });
                if (method == "thread/archive") sourceArchiveCalls++;
                return Task.FromResult<JToken?>(new JObject());
            },
            (method, _, _) =>
            {
                if (method == "thread/fork") return Task.FromResult<JToken?>(new JObject { ["thread"] = new JObject { ["id"] = "target" } });
                if (method == "thread/read") return Task.FromResult<JToken?>(new JObject { ["thread"] = Thread("target", null, "openai", true, Turns()) });
                if (method == "thread/archive") targetArchiveCalls++;
                return Task.FromResult<JToken?>(new JObject());
            }, CancellationToken.None);

        Assert.True(complete);
        Assert.Equal(0, sourceArchiveCalls);
        Assert.Equal(1, targetArchiveCalls);
    }

    [Fact]
    public async Task SkipsActiveThreadsWithoutForkingOrArchiving()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var sourcePath = WriteRollout(shared.Path, "sessions", "source.jsonl", "codex-vsix", "model");
        var active = Thread("source", sourcePath, "vsai_provider", archived: false, Turns());
        active["status"] = "active";
        var calls = 0;

        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, _, _) => Task.FromResult<JToken?>(new JObject { ["data"] = method == "thread/list" ? new JArray(active) : new JArray() }),
            (method, values, _) => { calls++; return Task.FromResult<JToken?>(new JObject()); }, CancellationToken.None);

        Assert.False(complete);
        Assert.False(CodexSessionMigration.HasCompletedMigration(shared.Path, isolated.Path));
        Assert.Equal(0, calls);
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(isolated.Path, "session-migration-manifest.json")));
        Assert.Equal("skipped-active", manifest["records"]?[0]?["state"]?.Value<string>());
    }

    [Fact]
    public async Task SkipsUnfinishedEventMessageTurnWithoutForking()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var sourcePath = WriteRollout(shared.Path, "sessions", "source.jsonl", "codex-vsix", "model");
        File.AppendAllText(sourcePath, "\n" + new JObject
        {
            ["type"] = "event_msg",
            ["payload"] = new JObject { ["type"] = "task_started", ["turn_id"] = "turn-1" }
        }.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
        var source = Thread("source", sourcePath, "openai", false, Turns());
        var targetCalls = 0;

        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, values, _) => Task.FromResult<JToken?>(new JObject
            {
                ["data"] = method == "thread/list" && values?["archived"]?.Value<bool>() == false ? new JArray(source) : new JArray()
            }),
            (method, values, _) => { targetCalls++; return Task.FromResult<JToken?>(new JObject()); }, CancellationToken.None);

        Assert.False(complete);
        Assert.Equal(0, targetCalls);
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(isolated.Path, "session-migration-manifest.json")));
        Assert.Equal("skipped-incomplete", manifest["records"]?[0]?["state"]?.Value<string>());
    }

    [Fact]
    public async Task RecordsMalformedKnownVsaiRolloutInsteadOfSilentlySkippingIt()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var sourcePath = WriteRollout(shared.Path, "sessions", "source.jsonl", "codex-vsix", "model");
        File.AppendAllText(sourcePath, "\n{ malformed", new UTF8Encoding(false));
        var source = Thread("source", sourcePath, "openai", false, Turns());
        var targetCalls = 0;

        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, values, _) => Task.FromResult<JToken?>(new JObject
            {
                ["data"] = method == "thread/list" && values?["archived"]?.Value<bool>() == false ? new JArray(source) : new JArray()
            }),
            (method, values, _) => { targetCalls++; return Task.FromResult<JToken?>(new JObject()); }, CancellationToken.None);

        Assert.False(complete);
        Assert.Equal(0, targetCalls);
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(isolated.Path, "session-migration-manifest.json")));
        Assert.Equal("failed", manifest["records"]?[0]?["state"]?.Value<string>());
    }

    [Fact]
    public async Task RejectsExtendedPathThatEscapesTheSessionDirectory()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var outsidePath = WriteRollout(shared.Path, "outside", "source.jsonl", "codex-vsix", "model");
        var escapedPath = ToExtendedLocalPath(Path.Combine(shared.Path, "sessions", "..", "outside", "source.jsonl"));
        var source = Thread("source", escapedPath, "vsai_provider", false, Turns());
        var targetCalls = 0;

        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, values, _) => Task.FromResult<JToken?>(new JObject
            {
                ["data"] = method == "thread/list" && values?["archived"]?.Value<bool>() == false ? new JArray(source) : new JArray(),
                ["thread"] = method == "thread/read" ? source.DeepClone() : null
            }),
            (method, values, _) => { targetCalls++; return Task.FromResult<JToken?>(new JObject()); }, CancellationToken.None);

        Assert.False(complete);
        Assert.Equal(0, targetCalls);
        Assert.True(File.Exists(outsidePath));
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(isolated.Path, "session-migration-manifest.json")));
        Assert.Equal("failed", manifest["records"]?[0]?["state"]?.Value<string>());
    }

    [Fact]
    public async Task DoesNotRetryForkWhenThePreviousOutcomeIsUnknown()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var sourcePath = WriteRollout(shared.Path, "sessions", "source.jsonl", "codex-vsix", "model");
        var source = Thread("source", sourcePath, "vsai_provider", false, Turns());
        var forks = 0;

        Func<string, JToken?, CancellationToken, Task<JToken?>> readSource = (method, values, _) =>
        {
            if (method == "thread/list")
                return Task.FromResult<JToken?>(new JObject { ["data"] = values?["archived"]?.Value<bool>() == false ? new JArray(source) : new JArray() });
            if (method == "thread/read") return Task.FromResult<JToken?>(new JObject { ["thread"] = source.DeepClone() });
            throw new Xunit.Sdk.XunitException("Source must not be archived.");
        };
        Func<string, JToken?, CancellationToken, Task<JToken?>> unknownFork = (method, _, _) =>
        {
            if (method == "thread/fork")
            {
                forks++;
                throw new IOException("Target connection ended after request dispatch.");
            }
            throw new Xunit.Sdk.XunitException("Unexpected target request: " + method);
        };

        Assert.False(await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path, readSource, unknownFork, CancellationToken.None));
        Assert.False(await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path, readSource, unknownFork, CancellationToken.None));
        Assert.Equal(1, forks);
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(isolated.Path, "session-migration-manifest.json")));
        Assert.Equal("fork-outcome-unknown", manifest["records"]?[0]?["state"]?.Value<string>());
    }

    [Fact]
    public async Task LeavesParentSourceWhenAnUnmigratedDescendantWouldBeArchivedWithIt()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var parentPath = WriteRollout(shared.Path, "sessions", "parent.jsonl", "codex-vsix", "model");
        var childPath = WriteRollout(shared.Path, "sessions", "child.jsonl", "other-client", "model");
        var parent = Thread("parent", parentPath, "vsai_provider", false, Turns());
        var child = Thread("child", childPath, "openai", false, Turns());
        child["parentThreadId"] = "parent";
        var sourceArchiveCalls = 0;

        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, values, _) =>
            {
                if (method == "thread/list")
                    return Task.FromResult<JToken?>(new JObject { ["data"] = values?["archived"]?.Value<bool>() == false ? new JArray(parent, child) : new JArray() });
                if (method == "thread/read")
                {
                    var id = values?["threadId"]?.Value<string>();
                    return Task.FromResult<JToken?>(new JObject { ["thread"] = (id == "parent" ? parent : child).DeepClone() });
                }
                if (method == "thread/archive") sourceArchiveCalls++;
                return Task.FromResult<JToken?>(new JObject());
            },
            (method, _, _) =>
            {
                if (method == "thread/fork") return Task.FromResult<JToken?>(new JObject { ["thread"] = new JObject { ["id"] = "target" } });
                if (method == "thread/read") return Task.FromResult<JToken?>(new JObject { ["thread"] = Thread("target", null, "vsai_provider", false, Turns()) });
                return Task.FromResult<JToken?>(new JObject());
            }, CancellationToken.None);

        Assert.False(complete);
        Assert.Equal(0, sourceArchiveCalls);
        var manifest = JObject.Parse(File.ReadAllText(Path.Combine(isolated.Path, "session-migration-manifest.json")));
        Assert.Equal("verified", manifest["records"]?[0]?["state"]?.Value<string>());
    }

    [Fact]
    public async Task ArchivesVerifiedVsaiDescendantsBeforeTheirParentInTheSameMigration()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        var parentPath = WriteRollout(shared.Path, "sessions", "parent.jsonl", "codex-vsix", "model");
        var childPath = WriteRollout(shared.Path, "sessions", "child.jsonl", "codex-vsix", "model");
        var parent = Thread("parent", parentPath, "vsai_parent", false, Turns());
        var child = Thread("child", childPath, "vsai_child", false, Turns());
        child["parentThreadId"] = "parent";
        var archiveOrder = new List<string>();

        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, values, _) =>
            {
                if (method == "thread/list")
                    return Task.FromResult<JToken?>(new JObject { ["data"] = values?["archived"]?.Value<bool>() == false ? new JArray(parent, child) : new JArray() });
                if (method == "thread/read")
                {
                    var id = values?["threadId"]?.Value<string>();
                    return Task.FromResult<JToken?>(new JObject { ["thread"] = (id == "parent" ? parent : child).DeepClone() });
                }
                if (method == "thread/archive") archiveOrder.Add(values?["threadId"]?.Value<string>() ?? string.Empty);
                return Task.FromResult<JToken?>(new JObject());
            },
            (method, values, _) =>
            {
                if (method == "thread/fork")
                    return Task.FromResult<JToken?>(new JObject { ["thread"] = new JObject { ["id"] = "target-" + values?["threadId"]?.Value<string>() } });
                if (method == "thread/read")
                    return Task.FromResult<JToken?>(new JObject { ["thread"] = Thread(values?["threadId"]?.Value<string>() ?? "target", null, "vsai_target", false, Turns()) });
                return Task.FromResult<JToken?>(new JObject());
            }, CancellationToken.None);

        Assert.True(complete);
        Assert.Equal(new[] { "child", "parent" }, archiveOrder);
    }

    private static JObject Thread(string id, string? path, string provider, bool archived, JArray turns)
    {
        var value = new JObject
        {
            ["id"] = id,
            ["modelProvider"] = provider,
            ["status"] = "idle",
            ["name"] = "Migrated title",
            ["turns"] = turns
        };
        if (path is not null) value["path"] = path;
        return value;
    }

    private static JArray Turns() => new JArray(new JObject
    {
        ["id"] = "turn-id",
        ["items"] = new JArray(new JObject { ["type"] = "message", ["text"] = "Preserved full history" })
    });

    private static string WriteRollout(string home, string directory, string file, string originator, string model)
    {
        var target = Path.Combine(home, directory);
        Directory.CreateDirectory(target);
        var path = Path.Combine(target, file);
        File.WriteAllText(path,
            new JObject { ["type"] = "session_meta", ["payload"] = new JObject { ["id"] = Path.GetFileNameWithoutExtension(file), ["originator"] = originator } }.ToString(Newtonsoft.Json.Formatting.None)
            + "\n" + new JObject { ["type"] = "turn_context", ["payload"] = new JObject { ["model"] = model } }.ToString(Newtonsoft.Json.Formatting.None)
            + "\n" + new JObject { ["type"] = "event_msg", ["payload"] = new JObject { ["type"] = "task_complete" } }.ToString(Newtonsoft.Json.Formatting.None) + "\n",
            new UTF8Encoding(false));
        return path;
    }

    private static string ToExtendedLocalPath(string path) => @"\\?\" + path;
}
