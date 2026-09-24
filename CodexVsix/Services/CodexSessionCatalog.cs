using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>Reads only the small thread catalog required to recover VSAI sessions that app-server listing omits.</summary>
internal static class CodexSessionCatalog
{
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int MaximumCandidates = 10000;
    private static readonly Regex StateDatabaseName = new Regex(@"^state_(\d+)\.sqlite$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<JObject> ReadVsaiCandidates(string sharedHome)
    {
        if (string.IsNullOrWhiteSpace(sharedHome)) throw new ArgumentException("A shared Codex home is required.", nameof(sharedHome));
        var root = Path.GetFullPath(sharedHome).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) return Array.Empty<JObject>();
        RejectReparsePoint(root);

        var database = Directory.EnumerateFiles(root, "state_*.sqlite", SearchOption.TopDirectoryOnly)
            .Select(path => new { Path = path, Match = StateDatabaseName.Match(Path.GetFileName(path)) })
            .Where(item => item.Match.Success)
            .OrderByDescending(item => long.Parse(item.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Select(item => item.Path)
            .FirstOrDefault();
        if (database is null) return Array.Empty<JObject>();
        RejectReparsePoint(database);

        IntPtr connection = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            ThrowIfSqliteError(sqlite3_open_v2(Encoding.UTF8.GetBytes(database + "\0"), out connection, SqliteOpenReadOnly, IntPtr.Zero), connection, "open catalog");
            var hasOriginator = HasColumn(connection, "originator");
            var sql = hasOriginator
                ? "SELECT id, rollout_path, model_provider, archived FROM threads WHERE originator = 'codex-vsix' OR model_provider LIKE 'vsai\\_%' ESCAPE '\\'"
                : "SELECT id, rollout_path, model_provider, archived FROM threads WHERE model_provider LIKE 'vsai\\_%' ESCAPE '\\'";
            var sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
            ThrowIfSqliteError(sqlite3_prepare_v2(connection, sqlBytes, -1, out statement, IntPtr.Zero), connection, "query catalog");
            var candidates = new List<JObject>();
            while (true)
            {
                var step = sqlite3_step(statement);
                if (step == SqliteDone) break;
                ThrowIfSqliteError(step, connection, "read catalog row");
                if (candidates.Count >= MaximumCandidates)
                    throw new InvalidDataException("VSAI catalog candidate limit was reached; migration was not started.");
                candidates.Add(new JObject
                {
                    ["id"] = ColumnText(statement, 0),
                    ["path"] = ColumnText(statement, 1),
                    ["modelProvider"] = ColumnText(statement, 2),
                    ["archived"] = sqlite3_column_int(statement, 3) != 0,
                    ["vsaiCatalogOrigin"] = true
                });
            }
            return candidates;
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException("Windows SQLite is unavailable; VSAI catalog recovery cannot continue.", ex);
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (connection != IntPtr.Zero) sqlite3_close(connection);
        }
    }

    private static bool HasColumn(IntPtr connection, string name)
    {
        IntPtr statement = IntPtr.Zero;
        try
        {
            var bytes = Encoding.UTF8.GetBytes("PRAGMA table_info(threads)\0");
            ThrowIfSqliteError(sqlite3_prepare_v2(connection, bytes, -1, out statement, IntPtr.Zero), connection, "inspect catalog schema");
            while (true)
            {
                var step = sqlite3_step(statement);
                if (step == SqliteDone) return false;
                ThrowIfSqliteError(step, connection, "inspect catalog schema");
                if (string.Equals(ColumnText(statement, 1), name, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); }
    }

    private static string? ColumnText(IntPtr statement, int index)
    {
        var value = sqlite3_column_text(statement, index);
        if (value == IntPtr.Zero) return null;
        var length = sqlite3_column_bytes(statement, index);
        if (length <= 0) return string.Empty;
        var bytes = new byte[length];
        Marshal.Copy(value, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void ThrowIfSqliteError(int result, IntPtr connection, string operation)
    {
        if (result == 0 || result == SqliteRow || result == SqliteDone) return;
        var message = connection == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(sqlite3_errmsg(connection));
        throw new InvalidDataException("Unable to " + operation + " in the VSAI catalog" + (string.IsNullOrWhiteSpace(message) ? "." : ": " + message));
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("VSAI catalog recovery does not follow reparse points.");
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_int(IntPtr statement, int column);
}
