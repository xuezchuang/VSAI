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

public sealed class CodexRolloutImportSnapshotTests
{
    [Fact]
    public async Task MaterializesRootWithoutHistoryBaseAsLegacy()
    {
        var source = Rollout("root", "paginated", null, "2026-09-23T01:02:03.123456+08:00");

        var result = await CodexRolloutImportSnapshot.MaterializeAsync("root", source, _ => throw new Xunit.Sdk.XunitException("No parent expected."), CancellationToken.None);

        var entries = Entries(result);
        var meta = Assert.IsType<JObject>(entries[0]["payload"]);
        Assert.Equal("legacy", meta["history_mode"]?.Value<string>());
        Assert.Null(meta["history_base"]);
        Assert.Contains("2026-09-23T01:02:03.123456+08:00", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public async Task MaterializesParentPrefixBeforeChildAndExcludesParentMeta()
    {
        var parent = Rollout("parent", "legacy", null, "parent-date");
        var parentPrefix = Prefix(parent, 3);
        var child = Rollout("child", "paginated", new JObject
        {
            ["thread_id"] = "parent",
            ["end_ordinal_exclusive"] = parentPrefix.Ordinal,
            ["end_byte_offset"] = parentPrefix.ByteOffset
        }, "child-date");

        var result = await CodexRolloutImportSnapshot.MaterializeAsync("child", child,
            id => Task.FromResult(id == "parent" ? parent : throw new Xunit.Sdk.XunitException("Unexpected parent.")), CancellationToken.None);

        var text = Encoding.UTF8.GetString(result);
        Assert.Equal(1, Count(text, "\"type\":\"session_meta\""));
        Assert.StartsWith("{\"type\":\"session_meta\",\"payload\":{\"id\":\"child\"", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("\"text\":\"parent-date\"", StringComparison.Ordinal) < text.IndexOf("\"text\":\"child-date\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaterializesMultipleParentLevels()
    {
        var grandparent = Rollout("grandparent", "legacy", null, "grandparent-date");
        var grandPrefix = Prefix(grandparent, 3);
        var parent = Rollout("parent", "paginated", new JObject
        {
            ["thread_id"] = "grandparent",
            ["end_ordinal_exclusive"] = grandPrefix.Ordinal,
            ["end_byte_offset"] = grandPrefix.ByteOffset
        }, "parent-date");
        var parentPrefix = Prefix(parent, 3);
        var child = Rollout("child", "paginated", new JObject
        {
            ["thread_id"] = "parent",
            ["end_ordinal_exclusive"] = parentPrefix.Ordinal,
            ["end_byte_offset"] = parentPrefix.ByteOffset
        }, "child-date");

        var values = new Dictionary<string, byte[]> { ["grandparent"] = grandparent, ["parent"] = parent };
        var result = await CodexRolloutImportSnapshot.MaterializeAsync("child", child, id => Task.FromResult(values[id]), CancellationToken.None);

        var text = Encoding.UTF8.GetString(result);
        Assert.True(text.IndexOf("\"text\":\"grandparent-date\"", StringComparison.Ordinal) < text.IndexOf("\"text\":\"parent-date\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"text\":\"parent-date\"", StringComparison.Ordinal) < text.IndexOf("\"text\":\"child-date\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsInvalidBoundsCyclesAndMalformedJsonl()
    {
        var parent = Rollout("parent", "legacy", null, "parent-date");
        var invalid = Rollout("child", "paginated", new JObject
        {
            ["thread_id"] = "parent",
            ["end_ordinal_exclusive"] = 1,
            ["end_byte_offset"] = 1
        }, "child-date");
        await Assert.ThrowsAsync<InvalidDataException>(() => CodexRolloutImportSnapshot.MaterializeAsync("child", invalid, _ => Task.FromResult(parent), CancellationToken.None));

        var wrongOrdinalPrefix = Prefix(parent, 2);
        var wrongOrdinal = Rollout("child", "paginated", new JObject
        {
            ["thread_id"] = "parent",
            ["end_ordinal_exclusive"] = 1,
            ["end_byte_offset"] = wrongOrdinalPrefix.ByteOffset
        }, "child-date");
        await Assert.ThrowsAsync<InvalidDataException>(() => CodexRolloutImportSnapshot.MaterializeAsync("child", wrongOrdinal, _ => Task.FromResult(parent), CancellationToken.None));

        var cycle = Rollout("cycle", "paginated", new JObject
        {
            ["thread_id"] = "cycle",
            ["end_ordinal_exclusive"] = 1,
            ["end_byte_offset"] = Prefix(parent, 1).ByteOffset
        }, "cycle-date");
        await Assert.ThrowsAsync<InvalidDataException>(() => CodexRolloutImportSnapshot.MaterializeAsync("cycle", cycle, _ => Task.FromResult(cycle), CancellationToken.None));

        var deep = new Dictionary<string, byte[]>();
        for (var index = 0; index <= 33; index++)
        {
            var id = "depth-" + index;
            deep[id] = Rollout(id, "paginated", index == 33 ? null : new JObject
            {
                ["thread_id"] = "depth-" + (index + 1),
                ["end_ordinal_exclusive"] = 0,
                ["end_byte_offset"] = 0
            }, "depth-date");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => CodexRolloutImportSnapshot.MaterializeAsync("depth-0", deep["depth-0"], id => Task.FromResult(deep[id]), CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() => CodexRolloutImportSnapshot.MaterializeAsync("child", Encoding.UTF8.GetBytes("{bad}\n"), _ => Task.FromResult(parent), CancellationToken.None));
    }

    [Fact]
    public async Task PreservesNonMetadataRawDateText()
    {
        const string date = "2026-09-23T01:02:03.123456+08:00";
        var source = Rollout("root", "paginated", null, date);

        var result = await CodexRolloutImportSnapshot.MaterializeAsync("root", source, _ => throw new Xunit.Sdk.XunitException("No parent expected."), CancellationToken.None);

        Assert.Contains("\"text\":\"" + date + "\"", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public async Task PreservesPaginatedCompletedItemsAcrossExpandedParentPath()
    {
        var parent = Rollout("parent-source", "paginated", null, "parent-date", includeCompletedItems: true, itemThreadId: "parent-item-thread");
        var parentPrefix = Prefix(parent, 6);
        var child = Rollout("child-source", "paginated", new JObject
        {
            ["thread_id"] = "parent-source",
            ["end_ordinal_exclusive"] = parentPrefix.Ordinal,
            ["end_byte_offset"] = parentPrefix.ByteOffset
        }, "child-date", includeCompletedItems: true, itemThreadId: "child-item-thread");

        var result = await CodexRolloutImportSnapshot.MaterializeAsync("child-source", child,
            id => Task.FromResult(id == "parent-source" ? parent : throw new Xunit.Sdk.XunitException("Unexpected parent.")),
            CancellationToken.None,
            preserveHistoryMode: true);

        var entries = Entries(result);
        var meta = Assert.IsType<JObject>(entries[0]["payload"]);
        var text = Encoding.UTF8.GetString(result);
        Assert.Equal("paginated", meta["history_mode"]?.Value<string>());
        Assert.Null(meta["history_base"]);
        Assert.Contains("\"thread_id\":\"parent-item-thread\"", text);
        Assert.Contains("\"thread_id\":\"child-item-thread\"", text);
        Assert.True(text.IndexOf("\"thread_id\":\"parent-item-thread\"", StringComparison.Ordinal) < text.IndexOf("\"thread_id\":\"child-item-thread\"", StringComparison.Ordinal));
        Assert.Contains("\"id\":\"item-parent-source\"", text);
    }

    private static byte[] Rollout(string id, string mode, JObject? historyBase, string dateText, bool includeCompletedItems = false, string? itemThreadId = null)
    {
        var meta = new JObject
        {
            ["id"] = id,
            ["session_id"] = id,
            ["history_mode"] = mode,
            ["timestamp"] = dateText
        };
        if (historyBase is not null) meta["history_base"] = historyBase;
        var entries = new List<JObject>
        {
            new JObject { ["type"] = "session_meta", ["payload"] = meta },
            new JObject { ["type"] = "turn_context", ["payload"] = new JObject { ["model"] = "gpt-5" } },
            new JObject { ["type"] = "response_item", ["payload"] = new JObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JArray(new JObject { ["type"] = "output_text", ["text"] = dateText }) } }
        };
        if (includeCompletedItems)
        {
            entries.Add(new JObject
            {
                ["type"] = "event_msg",
                ["payload"] = new JObject
                {
                    ["type"] = "item_completed",
                    ["thread_id"] = itemThreadId ?? id,
                    ["turn_id"] = "turn-" + id,
                    ["item"] = new JObject
                    {
                        ["type"] = "AgentMessage",
                        ["id"] = "item-" + id,
                        ["client_id"] = "item-" + id,
                        ["content"] = new JArray(new JObject { ["type"] = "output_text", ["text"] = dateText })
                    },
                    ["started_at_ms"] = 1,
                    ["completed_at_ms"] = 2
                }
            });
            entries.Add(new JObject { ["type"] = "codex/event/task_complete", ["payload"] = new JObject { ["last_agent_message"] = dateText } });
            entries.Add(new JObject
            {
                ["type"] = "event_msg",
                ["payload"] = new JObject
                {
                    ["type"] = "thread_settings_applied",
                    ["thread_id"] = itemThreadId ?? id,
                    ["thread_settings"] = new JObject { ["model"] = "gpt-5" }
                }
            });
        }
        return Encoding.UTF8.GetBytes(string.Join("\r\n", entries.ConvertAll(entry => entry.ToString(Formatting.None))) + "\r\n");
    }

    private static (int Ordinal, long ByteOffset) Prefix(byte[] bytes, int ordinal)
    {
        var offset = 0;
        for (var index = 0; index < ordinal; index++)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', offset);
            Assert.True(newline >= 0);
            offset = newline + 1;
        }
        return (ordinal, offset);
    }

    private static JArray Entries(byte[] bytes)
    {
        var result = new JArray();
        foreach (var line in Encoding.UTF8.GetString(bytes).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            result.Add(NewtonsoftJsonCompatibility.ParseProtocolValue(line));
        return result;
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
