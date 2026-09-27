using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>Recovers persisted forks omitted by app-server's nonempty-preview thread/list filter.</summary>
internal static class CodexForkHistoryCatalog
{
    private const int MaximumRows = 10000;
    private const int MaximumHeaderBytes = 1024 * 1024;
    private const int MaximumForkDepth = 16;
    private const int SqliteReadOnly = 1;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;

    internal static IReadOnlyList<JObject> Read(string privateHome, JObject request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(privateHome)) throw new ArgumentException("A private Codex home is required.", nameof(privateHome));
        if (request is null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var root = ComparableFullPath(privateHome).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) return Array.Empty<JObject>();
        EnsureNoReparsePoints(root);
        var database = Directory.EnumerateFiles(root, "state_*.sqlite", SearchOption.TopDirectoryOnly)
            .Select(path => new { Path = path, Version = ReadDatabaseVersion(Path.GetFileName(path)) })
            .Where(item => item.Version.HasValue)
            .OrderByDescending(item => item.Version!.Value)
            .Select(item => item.Path)
            .FirstOrDefault();
        if (database is null) return Array.Empty<JObject>();
        EnsureNoReparsePoints(database);

        using var catalog = new CatalogReader(database);
        var rows = catalog.ReadCandidates(request, cancellationToken);
        var archived = request["archived"]?.Value<bool>() == true;
        var search = request["searchTerm"]?.Value<string>()?.Trim();
        var results = new List<JObject>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Archived != archived || !string.IsNullOrEmpty(row.Preview)
                || !MatchesCwd(request["cwd"], row.Cwd)
                || !MatchesProvider(request["modelProviders"], row.Provider)
                || !MatchesSource(request["sourceKinds"], row.Source)) continue;
            if (!TryReadForkParent(root, row, cancellationToken, out var parentId)) continue;
            var title = FirstTitle(row) ?? InheritedTitle(root, parentId!, catalog, cancellationToken);
            if (title is null || string.IsNullOrWhiteSpace(title)) title = "Forked chat";
            if (search is not null && search.Length > 0
                && title.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (title.Length > 240) title = title.Substring(0, 240);
            results.Add(new JObject
            {
                ["id"] = row.Id,
                ["sessionId"] = row.Id,
                ["forkedFromId"] = parentId,
                ["parentThreadId"] = JValue.CreateNull(),
                ["preview"] = title,
                ["ephemeral"] = false,
                ["modelProvider"] = row.Provider,
                ["createdAt"] = row.CreatedAtMs / 1000,
                ["updatedAt"] = row.UpdatedAtMs / 1000,
                ["status"] = new JObject { ["type"] = "notLoaded" },
                ["path"] = row.Path,
                ["cwd"] = row.Cwd,
                ["cliVersion"] = row.CliVersion,
                ["source"] = ParseSource(row.Source),
                ["threadSource"] = JValue.CreateNull(),
                ["agentNickname"] = JValue.CreateNull(),
                ["agentRole"] = JValue.CreateNull(),
                ["gitInfo"] = JValue.CreateNull(),
                ["name"] = title,
                ["turns"] = new JArray()
            });
        }
        return results;
    }

    private static long? ReadDatabaseVersion(string name)
    {
        if (!name.StartsWith("state_", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)) return null;
        var digits = name.Substring(6, name.Length - 13);
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : null;
    }

    private static string? FirstTitle(Row row)
    {
        if (!string.IsNullOrWhiteSpace(row.Name)) return row.Name.Trim();
        if (!string.IsNullOrWhiteSpace(row.Title)) return row.Title.Trim();
        if (!string.IsNullOrWhiteSpace(row.Preview)) return row.Preview.Trim();
        return null;
    }

    private static string? InheritedTitle(string root, string parentId, CatalogReader catalog, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < MaximumForkDepth && seen.Add(parentId); depth++)
        {
            token.ThrowIfCancellationRequested();
            var parent = catalog.ReadById(parentId);
            if (parent is null) break;
            var title = FirstTitle(parent);
            if (title is not null) return title;
            if (!TryReadForkParent(root, parent, token, out var next)) break;
            parentId = next!;
        }
        return null;
    }

    private static bool TryReadForkParent(string root, Row row, CancellationToken token, out string? parentId)
    {
        parentId = null;
        try
        {
            if (string.IsNullOrWhiteSpace(row.Id) || row.Id.Length > 256 || !IsSafeRolloutPath(root, row.Path)) return false;
            using var stream = new FileStream(row.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            using var header = new MemoryStream();
            for (var count = 0; count < MaximumHeaderBytes; count++)
            {
                if ((count & 0xfff) == 0) token.ThrowIfCancellationRequested();
                var value = stream.ReadByte();
                if (value < 0 || value == '\n') break;
                header.WriteByte((byte)value);
            }
            if (header.Length == 0 || (header.Length == MaximumHeaderBytes && stream.Position < stream.Length)) return false;
            var line = Encoding.UTF8.GetString(header.ToArray()).TrimStart('\uFEFF').TrimEnd('\r');
            var record = JObject.Parse(line);
            if (!string.Equals(record["type"]?.Value<string>(), "session_meta", StringComparison.Ordinal)) return false;
            var payload = record["payload"] as JObject;
            if (!string.Equals(payload?["id"]?.Value<string>(), row.Id, StringComparison.Ordinal)) return false;
            var candidate = payload?["forked_from_id"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(candidate) || candidate!.Length > 256 || candidate == row.Id) return false;
            parentId = candidate;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
            || ex is ArgumentException || ex is NotSupportedException || ex is InvalidOperationException
            || ex is JsonException)
        {
            return false;
        }
    }

    private static bool IsSafeRolloutPath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)
            || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        var full = ComparableFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        EnsureNoReparsePoints(full);
        return File.Exists(full);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = ComparableFullPath(path);
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fork history path uses a reparse point.");
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private static string ComparableFullPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path.Substring(8);
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            path = path.Substring(4);
        return Path.GetFullPath(path);
    }

    private static bool MatchesCwd(JToken? filter, string actual)
    {
        if (filter is null || filter.Type == JTokenType.Null) return true;
        var values = filter is JArray array ? array.Values<string>() : new[] { filter.Value<string>() };
        var normalized = CodexProcessService.NormalizeComparablePath(actual);
        return values.Any(value => !string.IsNullOrWhiteSpace(value)
            && string.Equals(CodexProcessService.NormalizeComparablePath(value), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesProvider(JToken? filter, string actual)
    {
        return filter is not JArray values || values.Count == 0
            || values.Values<string>().Contains(actual, StringComparer.Ordinal);
    }

    private static bool MatchesSource(JToken? filter, string actual)
    {
        var source = ParseSource(actual);
        var sourceName = source.Type == JTokenType.String ? source.Value<string>() : null;
        var custom = source is JObject sourceObject ? sourceObject["custom"]?.Value<string>() : null;
        var subAgent = source is JObject agentObject ? agentObject["sub_agent"] : null;
        var subAgentKind = subAgent?.Type == JTokenType.String
            ? subAgent.Value<string>() : (subAgent as JObject)?.Properties().FirstOrDefault()?.Name;
        if (filter is not JArray kinds || kinds.Count == 0)
            return sourceName == "cli" || sourceName == "vscode" || custom == "atlas" || custom == "chatgpt";
        return kinds.Values<string>().Any(kind => kind switch
        {
            "cli" => sourceName == "cli",
            "vscode" => sourceName == "vscode",
            "appServer" => sourceName == "mcp",
            "exec" => sourceName == "exec",
            "subAgent" => subAgent is not null,
            "subAgentReview" => subAgentKind == "review",
            "subAgentCompact" => subAgentKind == "compact",
            "subAgentThreadSpawn" => subAgentKind == "thread_spawn",
            "subAgentOther" => subAgentKind == "other",
            "unknown" => sourceName == "unknown",
            _ => false
        });
    }

    private static JToken ParseSource(string source)
    {
        try { return JToken.Parse(source); }
        catch (JsonException) { return new JValue(source); }
    }

    private static string? ColumnText(IntPtr statement, int index)
    {
        var pointer = sqlite3_column_text(statement, index);
        if (pointer == IntPtr.Zero) return null;
        var length = sqlite3_column_bytes(statement, index);
        if (length < 0 || length > 1024 * 1024) throw new InvalidDataException("Fork history catalog field exceeds the safe size limit.");
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void CheckSqlite(int code)
    {
        if (code != 0 && code != SqliteRow && code != SqliteDone)
            throw new InvalidDataException("Unable to read the fork history catalog.");
    }

    private sealed class CatalogReader : IDisposable
    {
        private IntPtr _connection;
        private readonly string _select;
        private readonly string _updatedExpression;

        internal CatalogReader(string database)
        {
            try
            {
                CheckSqlite(sqlite3_open_v2(Encoding.UTF8.GetBytes(database + "\0"), out _connection, SqliteReadOnly, IntPtr.Zero));
                var columns = ReadColumns();
                string ColumnOr(string name, string fallback) => columns.Contains(name) ? name : fallback;
                var created = ColumnOr("created_at_ms", columns.Contains("created_at") ? "CAST(created_at AS INTEGER) * 1000" : "0");
                var updated = ColumnOr("updated_at_ms", columns.Contains("updated_at") ? "CAST(updated_at AS INTEGER) * 1000" : created);
                _updatedExpression = updated;
                _select = "SELECT id, rollout_path, model_provider, archived, cwd, source, "
                    + created + ", " + updated + ", " + ColumnOr("name", "NULL") + ", "
                    + ColumnOr("title", "NULL") + ", preview, " + ColumnOr("cli_version", "NULL") + " FROM threads";
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal List<Row> ReadCandidates(JObject request, CancellationToken token)
        {
            var directoryFilter = request["cwd"];
            var directories = DirectoryVariants(directoryFilter).ToArray();
            if (directoryFilter is not null && directoryFilter.Type != JTokenType.Null && directories.Length == 0)
                return new List<Row>();
            var providers = request["modelProviders"] is JArray providerArray
                ? providerArray.Values<string>().OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                : Array.Empty<string>();
            if (directories.Length > 192 || providers.Length > 64)
                throw new ArgumentException("Fork history filters exceed the safe size limit.", nameof(request));
            var sql = new StringBuilder(_select);
            sql.Append(" WHERE archived = ").Append(request["archived"]?.Value<bool>() == true ? '1' : '0')
                .Append(" AND COALESCE(preview, '') = ''");
            var bindings = new List<string>();
            if (directoryFilter is not null && directoryFilter.Type != JTokenType.Null)
            {
                sql.Append(" AND cwd COLLATE NOCASE IN (").Append(string.Join(",", directories.Select(_ => "?"))).Append(')');
                bindings.AddRange(directories);
            }
            if (providers.Length > 0)
            {
                sql.Append(" AND model_provider IN (").Append(string.Join(",", providers.Select(_ => "?"))).Append(')');
                bindings.AddRange(providers);
            }
            sql.Append(" ORDER BY ").Append(_updatedExpression).Append(" DESC LIMIT ").Append(MaximumRows + 1);
            return Execute(sql.ToString(), bindings, token, MaximumRows);
        }

        internal Row? ReadById(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256) return null;
            return Execute(_select + " WHERE id = ? LIMIT 1", new[] { id }, CancellationToken.None, 1).FirstOrDefault();
        }

        private HashSet<string> ReadColumns()
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr statement = IntPtr.Zero;
            try
            {
                CheckSqlite(sqlite3_prepare_v2(_connection, Encoding.UTF8.GetBytes("PRAGMA table_info(threads)\0"), -1, out statement, IntPtr.Zero));
                while (true)
                {
                    var step = sqlite3_step(statement);
                    if (step == SqliteDone) break;
                    CheckSqlite(step);
                    if (columns.Count > 100) throw new InvalidDataException("Fork history catalog schema exceeds the safe column limit.");
                    var name = ColumnText(statement, 1);
                    if (name is not null) columns.Add(name);
                }
            }
            finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); }
            if (!columns.Contains("id") || !columns.Contains("rollout_path") || !columns.Contains("preview"))
                throw new InvalidDataException("Fork history catalog schema is unsupported.");
            return columns;
        }

        private List<Row> Execute(string sql, IReadOnlyList<string> bindings, CancellationToken token, int maximum)
        {
            IntPtr statement = IntPtr.Zero;
            try
            {
                CheckSqlite(sqlite3_prepare_v2(_connection, Encoding.UTF8.GetBytes(sql + "\0"), -1, out statement, IntPtr.Zero));
                for (var index = 0; index < bindings.Count; index++)
                {
                    var bytes = Encoding.UTF8.GetBytes(bindings[index]);
                    CheckSqlite(sqlite3_bind_text(statement, index + 1, bytes, bytes.Length, new IntPtr(-1)));
                }
                var rows = new List<Row>();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var step = sqlite3_step(statement);
                    if (step == SqliteDone) break;
                    CheckSqlite(step);
                    if (rows.Count >= maximum) throw new InvalidDataException("Fork history catalog exceeds the safe row limit.");
                    rows.Add(new Row(
                        ColumnText(statement, 0) ?? string.Empty,
                        ColumnText(statement, 1) ?? string.Empty,
                        ColumnText(statement, 2) ?? string.Empty,
                        sqlite3_column_int(statement, 3) != 0,
                        ColumnText(statement, 4) ?? string.Empty,
                        ColumnText(statement, 5) ?? string.Empty,
                        sqlite3_column_int64(statement, 6),
                        sqlite3_column_int64(statement, 7),
                        ColumnText(statement, 8) ?? string.Empty,
                        ColumnText(statement, 9) ?? string.Empty,
                        ColumnText(statement, 10) ?? string.Empty,
                        ColumnText(statement, 11) ?? string.Empty));
                }
                return rows;
            }
            finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); }
        }

        public void Dispose()
        {
            if (_connection != IntPtr.Zero)
            {
                sqlite3_close(_connection);
                _connection = IntPtr.Zero;
            }
        }
    }

    private static IEnumerable<string> DirectoryVariants(JToken? filter)
    {
        if (filter is null || filter.Type == JTokenType.Null) return Array.Empty<string>();
        var paths = filter is JArray array ? array.Values<string>() : new[] { filter.Value<string>() };
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddVariant(string value)
        {
            variants.Add(value);
            variants.Add(value.TrimEnd('\\', '/') + "\\");
            variants.Add(value.Replace('\\', '/'));
        }
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var normal = CodexProcessService.NormalizeComparablePath(path);
            if (string.IsNullOrWhiteSpace(normal)) continue;
            AddVariant(normal);
            if (normal.StartsWith(@"\\", StringComparison.Ordinal))
                AddVariant(@"\\?\UNC\" + normal.Substring(2));
            else if (normal.Length >= 3 && normal[1] == ':')
                AddVariant(@"\\?\" + normal);
        }
        return variants;
    }

    private sealed class Row
    {
        internal Row(string id, string path, string provider, bool archived, string cwd, string source,
            long createdAtMs, long updatedAtMs, string name, string title, string preview, string cliVersion)
        {
            Id = id; Path = path; Provider = provider; Archived = archived; Cwd = cwd; Source = source;
            CreatedAtMs = createdAtMs; UpdatedAtMs = updatedAtMs; Name = name; Title = title;
            Preview = preview; CliVersion = cliVersion;
        }
        internal string Id { get; }
        internal string Path { get; }
        internal string Provider { get; }
        internal bool Archived { get; }
        internal string Cwd { get; }
        internal string Source { get; }
        internal long CreatedAtMs { get; }
        internal long UpdatedAtMs { get; }
        internal string Name { get; }
        internal string Title { get; }
        internal string Preview { get; }
        internal string CliVersion { get; }
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int length, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_int(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr statement, int column);
}
