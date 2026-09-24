using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexNativeHistoryMigrationTests
{
    [Fact]
    public async Task RestoresPaginatedChildWithItsNativeParentWithoutFlatteningEitherRollout()
    {
        using var shared = new TemporaryDirectory();
        using var isolated = new TemporaryDirectory();
        const string parentId = "parent";
        const string childId = "child";
        var parentPath = Path.Combine(shared.Path, "sessions", "parent.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(parentPath)!);
        var parentBytes = JsonLines(
            SessionMeta(parentId, "Codex Desktop", "legacy", null),
            TurnContext("openai"),
            Event("item_completed"),
            Event("task_complete"));
        File.WriteAllBytes(parentPath, parentBytes);
        var childPath = Path.Combine(shared.Path, "sessions", "child.jsonl");
        var childBytes = JsonLines(
            SessionMeta(childId, "codex-vsix", "paginated", new JObject
            {
                ["thread_id"] = parentId,
                ["end_ordinal_exclusive"] = 4,
                ["end_byte_offset"] = parentBytes.Length
            }),
            TurnContext("openai"),
            Event("item_completed"),
            Event("task_complete"));
        File.WriteAllBytes(childPath, childBytes);

        var parent = Thread(parentId, parentPath, "openai", Turns("parent-turn"));
        var child = Thread(childId, childPath, "openai", Turns("child-turn"));
        child["parentThreadId"] = parentId;
        var sourceArchives = new List<string>();
        var targetArchives = new List<string>();
        var resumes = new List<string>();
        var stagedPaths = new Dictionary<string, string>(StringComparer.Ordinal);

        var complete = await CodexSessionMigration.MigrateAsync(shared.Path, isolated.Path,
            (method, values, _) =>
            {
                if (method == "thread/list")
                    return Task.FromResult<JToken?>(new JObject { ["data"] = values?["archived"]?.Value<bool>() == false ? new JArray(parent, child) : new JArray() });
                if (method == "thread/read")
                {
                    var id = values?["threadId"]?.Value<string>();
                    return Task.FromResult<JToken?>(new JObject { ["thread"] = (id == parentId ? parent : child).DeepClone() });
                }
                Assert.Equal("thread/archive", method);
                sourceArchives.Add(values?["threadId"]?.Value<string>() ?? string.Empty);
                return Task.FromResult<JToken?>(new JObject());
            },
            (method, values, _) =>
            {
                var id = values?["threadId"]?.Value<string>();
                if (method == "thread/read" && id == parentId)
                    throw new InvalidOperationException("thread not loaded: parent");
                if (method == "thread/resume")
                {
                    var stagedPath = values!["path"]!.Value<string>()!;
                    resumes.Add(id ?? string.Empty);
                    stagedPaths[id ?? string.Empty] = stagedPath;
                    Assert.True(values?["excludeTurns"]?.Value<bool>());
                    Assert.Equal(id == parentId ? parentBytes : childBytes, File.ReadAllBytes(stagedPath));
                    return Task.FromResult<JToken?>(new JObject { ["thread"] = new JObject { ["id"] = id } });
                }
                if (method == "thread/read" && id == childId)
                    return Task.FromResult<JToken?>(new JObject
                    {
                        ["thread"] = Thread(childId, @"\\?\" + stagedPaths[childId], "openai", Turns("child-turn"))
                    });
                if (method == "thread/name/set") return Task.FromResult<JToken?>(new JObject());
                if (method == "thread/archive")
                {
                    targetArchives.Add(id ?? string.Empty);
                    return Task.FromResult<JToken?>(new JObject());
                }
                throw new Xunit.Sdk.XunitException("Unexpected target method: " + method);
            }, CancellationToken.None);

        Assert.True(complete);
        Assert.Equal(new[] { parentId, childId }, resumes);
        Assert.Equal(new[] { parentId }, targetArchives);
        Assert.Equal(new[] { childId }, sourceArchives);
    }

    private static JObject Thread(string id, string path, string provider, JArray turns) => new JObject
    {
        ["id"] = id,
        ["path"] = path,
        ["modelProvider"] = provider,
        ["status"] = "idle",
        ["name"] = id,
        ["turns"] = turns
    };

    private static JArray Turns(string id) => new JArray(new JObject
    {
        ["id"] = id,
        ["items"] = new JArray(new JObject { ["type"] = "message", ["text"] = "synthetic" })
    });

    private static JObject SessionMeta(string id, string originator, string historyMode, JObject? historyBase)
    {
        var payload = new JObject { ["id"] = id, ["originator"] = originator, ["history_mode"] = historyMode };
        if (historyBase is not null) payload["history_base"] = historyBase;
        return new JObject { ["type"] = "session_meta", ["payload"] = payload };
    }

    private static JObject TurnContext(string model) => new JObject { ["type"] = "turn_context", ["payload"] = new JObject { ["model"] = model } };
    private static JObject Event(string type) => new JObject { ["type"] = "event_msg", ["payload"] = new JObject { ["type"] = type } };

    private static byte[] JsonLines(params JObject[] lines)
        => new UTF8Encoding(false).GetBytes(string.Join("\n", Array.ConvertAll(lines, line => line.ToString(Formatting.None))) + "\n");
}
