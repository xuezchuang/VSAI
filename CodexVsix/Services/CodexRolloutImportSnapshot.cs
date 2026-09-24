using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>
/// Materializes a paginated rollout into one legacy JSONL snapshot for the isolated importer.
/// The caller owns locating and reading parent rollouts; this type never follows filesystem paths.
/// </summary>
internal static class CodexRolloutImportSnapshot
{
    private const int MaximumDepth = 32;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static async Task<byte[]> MaterializeAsync(
        string sourceId,
        byte[] sourceBytes,
        Func<string, Task<byte[]>> loadParent,
        CancellationToken token,
        bool preserveHistoryMode = false)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A source thread id is required.", nameof(sourceId));
        if (sourceBytes is null) throw new ArgumentNullException(nameof(sourceBytes));
        if (loadParent is null) throw new ArgumentNullException(nameof(loadParent));

        var budget = new ByteBudget();
        var stack = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<RawLine>();
        var source = Parse(sourceBytes, sourceId, budget);
        output.Add(source.CreateSessionMeta(preserveHistoryMode));
        await AppendParentPrefixAsync(source, loadParent, stack, output, budget, depth: 0, token).ConfigureAwait(false);
        output.AddRange(source.NonMetaLines);
        return Serialize(output, budget);
    }

    private static async Task AppendParentPrefixAsync(
        ParsedRollout child,
        Func<string, Task<byte[]>> loadParent,
        HashSet<string> stack,
        List<RawLine> output,
        ByteBudget budget,
        int depth,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var historyBase = child.GetHistoryBase();
        if (historyBase is null) return;
        if (depth >= MaximumDepth) throw new InvalidDataException("Rollout history exceeds the maximum parent depth.");
        if (!stack.Add(child.Id)) throw new InvalidDataException("Rollout history contains a parent cycle.");

        try
        {
            var parentBytes = await loadParent(historyBase.ParentId).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (parentBytes is null) throw new InvalidDataException("A parent rollout could not be loaded.");
            var parent = Parse(parentBytes, historyBase.ParentId, budget);
            await AppendParentPrefixAsync(parent, loadParent, stack, output, budget, depth + 1, token).ConfigureAwait(false);
            output.AddRange(parent.GetPrefix(historyBase));
        }
        finally
        {
            stack.Remove(child.Id);
        }
    }

    private static ParsedRollout Parse(byte[] bytes, string expectedId, ByteBudget budget)
    {
        budget.Add(bytes.Length);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Rollout is not valid UTF-8 JSONL.", ex);
        }

        var lines = SplitLines(text);
        JObject? sessionMeta = null;
        RawLine? sessionMetaLine = null;
        foreach (var line in lines)
        {
            if (line.Content.Length == 0) throw new InvalidDataException("Rollout contains an empty JSONL record.");
            var entry = ParseObject(line.Content);
            if (!string.Equals(entry["type"]?.Value<string>(), "session_meta", StringComparison.Ordinal)) continue;
            if (sessionMeta is not null) throw new InvalidDataException("Rollout contains more than one session_meta record.");
            sessionMeta = entry["payload"] as JObject
                ?? throw new InvalidDataException("Rollout session_meta has no object payload.");
            sessionMetaLine = line;
        }

        if (sessionMeta is null || sessionMetaLine is null)
            throw new InvalidDataException("Rollout does not contain a session_meta record.");
        var actualId = sessionMeta["id"]?.Value<string>();
        if (!string.Equals(expectedId, actualId, StringComparison.Ordinal))
            throw new InvalidDataException("Rollout session_meta id does not match the expected source thread id.");

        return new ParsedRollout(expectedId, bytes, lines, sessionMeta, sessionMetaLine);
    }

    private static JObject ParseObject(string json)
    {
        try
        {
            return NewtonsoftJsonCompatibility.ParseProtocolValue(json) as JObject
                ?? throw new InvalidDataException("A JSONL record is not an object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Rollout contains malformed JSONL.", ex);
        }
    }

    private static List<RawLine> SplitLines(string text)
    {
        var lines = new List<RawLine>();
        var offset = 0;
        while (offset < text.Length)
        {
            var newline = text.IndexOf('\n', offset);
            if (newline < 0)
            {
                lines.Add(new RawLine(text.Substring(offset), string.Empty));
                offset = text.Length;
                continue;
            }

            var contentEnd = newline > offset && text[newline - 1] == '\r' ? newline - 1 : newline;
            lines.Add(new RawLine(text.Substring(offset, contentEnd - offset), text.Substring(contentEnd, newline - contentEnd + 1)));
            offset = newline + 1;
        }
        if (lines.Count == 0) throw new InvalidDataException("Rollout is empty.");
        return lines;
    }

    private static byte[] Serialize(IReadOnlyList<RawLine> lines, ByteBudget budget)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            builder.Append(line.Content);
            if (line.Newline.Length > 0)
            {
                builder.Append(line.Newline);
            }
            else if (index + 1 < lines.Count)
            {
                builder.Append('\n');
            }
        }

        var bytes = StrictUtf8.GetBytes(builder.ToString());
        budget.EnsureOutput(bytes.Length);
        return bytes;
    }

    private sealed class ParsedRollout
    {
        private readonly byte[] _bytes;
        private readonly IReadOnlyList<RawLine> _lines;
        private readonly RawLine _sessionMetaLine;
        private readonly JObject _sessionMeta;

        internal ParsedRollout(string id, byte[] bytes, IReadOnlyList<RawLine> lines, JObject sessionMeta, RawLine sessionMetaLine)
        {
            Id = id;
            _bytes = bytes;
            _lines = lines;
            _sessionMeta = sessionMeta;
            _sessionMetaLine = sessionMetaLine;
        }

        internal string Id { get; }

        internal IEnumerable<RawLine> NonMetaLines
        {
            get
            {
                foreach (var line in _lines)
                    if (!ReferenceEquals(line, _sessionMetaLine)) yield return line;
            }
        }

        internal RawLine CreateSessionMeta(bool preserveHistoryMode)
        {
            var root = ParseObject(_sessionMetaLine.Content);
            var payload = root["payload"] as JObject
                ?? throw new InvalidDataException("Rollout session_meta has no object payload.");
            payload.Remove("history_base");
            payload.Remove("forked_from_ordinal_exclusive");
            if (!preserveHistoryMode) payload["history_mode"] = "legacy";
            return new RawLine(NewtonsoftJsonCompatibility.Serialize(root, Formatting.None), _sessionMetaLine.Newline.Length == 0 ? "\n" : _sessionMetaLine.Newline);
        }

        internal HistoryBase? GetHistoryBase()
        {
            var value = _sessionMeta["history_base"];
            if (value is null || value.Type == JTokenType.Null) return null;
            if (value is not JObject historyBase) throw new InvalidDataException("Rollout history_base is not an object.");
            var parentId = historyBase["thread_id"]?.Value<string>();
            var ordinal = historyBase["end_ordinal_exclusive"]?.Value<int?>();
            var byteOffset = historyBase["end_byte_offset"]?.Value<long?>();
            if (string.IsNullOrWhiteSpace(parentId) || ordinal is null || byteOffset is null || ordinal < 0 || byteOffset < 0)
                throw new InvalidDataException("Rollout history_base is incomplete.");
            return new HistoryBase(parentId!, ordinal.Value, byteOffset.Value);
        }

        internal IReadOnlyList<RawLine> GetPrefix(HistoryBase historyBase)
        {
            if (historyBase.ByteOffset > _bytes.LongLength)
                throw new InvalidDataException("Rollout history_base byte offset exceeds its parent rollout.");
            if (historyBase.ByteOffset < _bytes.LongLength && (historyBase.ByteOffset == 0 || _bytes[checked((int)historyBase.ByteOffset - 1)] != (byte)'\n'))
                throw new InvalidDataException("Rollout history_base byte offset does not end on a JSONL boundary.");

            string prefixText;
            try { prefixText = StrictUtf8.GetString(_bytes, 0, checked((int)historyBase.ByteOffset)); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Rollout history_base splits an invalid UTF-8 sequence.", ex); }
            var prefix = historyBase.ByteOffset == 0 ? new List<RawLine>() : SplitLines(prefixText);
            if (prefix.Count != historyBase.Ordinal)
                throw new InvalidDataException("Rollout history_base ordinal does not match its parent JSONL prefix.");

            var result = new List<RawLine>();
            foreach (var line in prefix)
            {
                var entry = ParseObject(line.Content);
                if (string.Equals(entry["type"]?.Value<string>(), "session_meta", StringComparison.Ordinal)) continue;
                result.Add(line);
            }
            return result;
        }
    }

    private sealed class HistoryBase
    {
        internal HistoryBase(string parentId, int ordinal, long byteOffset)
        {
            ParentId = parentId;
            Ordinal = ordinal;
            ByteOffset = byteOffset;
        }

        internal string ParentId { get; }
        internal int Ordinal { get; }
        internal long ByteOffset { get; }
    }

    private sealed class RawLine
    {
        internal RawLine(string content, string newline)
        {
            Content = content;
            Newline = newline;
        }

        internal string Content { get; }
        internal string Newline { get; }
    }

    private sealed class ByteBudget
    {
        private long _inputBytes;

        internal void Add(int bytes)
        {
            _inputBytes += bytes;
            if (_inputBytes > MaximumBytes) throw new InvalidDataException("Rollout history exceeds the maximum import size.");
        }

        internal void EnsureOutput(int bytes)
        {
            if (bytes > MaximumBytes) throw new InvalidDataException("Materialized rollout exceeds the maximum import size.");
        }
    }
}
