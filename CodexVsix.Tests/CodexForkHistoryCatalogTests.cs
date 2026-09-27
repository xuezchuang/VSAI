using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CodexVsix.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexForkHistoryCatalogTests
{
    [Fact]
    public void IncludesPersistedBlankForkAndInheritsParentTitleWithoutWritingDatabase()
    {
        using var fixture = new Fixture();
        fixture.Add("parent", "Parent title", "Parent preview");
        fixture.Add("fork", "", "", parentId: "parent");
        fixture.Add("prewarm", "", "");
        fixture.Save();
        var before = Hash(fixture.Database);

        var rows = CodexForkHistoryCatalog.Read(fixture.Home, fixture.Request(), CancellationToken.None);

        var fork = Assert.Single(rows);
        Assert.Equal("fork", fork["id"]?.Value<string>());
        Assert.Equal("parent", fork["forkedFromId"]?.Value<string>());
        Assert.Equal("Parent title", fork["name"]?.Value<string>());
        Assert.Equal("Parent title", fork["preview"]?.Value<string>());
        Assert.Equal(1750000000L, fork["createdAt"]?.Value<long>());
        Assert.Equal("notLoaded", fork["status"]?["type"]?.Value<string>());
        Assert.False(fork["ephemeral"]?.Value<bool>());
        Assert.Equal(before, Hash(fixture.Database));
    }

    [Fact]
    public void AppliesArchivedWorkspaceProviderSourceAndInheritedSearchFilters()
    {
        using var fixture = new Fixture();
        fixture.Add("parent", "Original render task", "Original preview");
        fixture.Add("wanted", "", "", parentId: "parent", cwd: @"D:\Work\Project", provider: "openai", source: "vscode");
        fixture.Add("archived", "", "", parentId: "parent", archived: true);
        fixture.Add("other-cwd", "", "", parentId: "parent", cwd: @"D:\Work\Elsewhere");
        fixture.Add("other-provider", "", "", parentId: "parent", provider: "custom");
        fixture.Add("other-source", "", "", parentId: "parent", source: "mcp");
        fixture.Save();
        var request = fixture.Request();
        request["cwd"] = new JArray(@"\\?\D:\work\project\");
        request["modelProviders"] = new JArray("openai");
        request["sourceKinds"] = new JArray("vscode");
        request["searchTerm"] = "render";

        Assert.Equal("wanted", Assert.Single(CodexForkHistoryCatalog.Read(fixture.Home, request, CancellationToken.None))["id"]?.Value<string>());
        request["searchTerm"] = "missing";
        Assert.Empty(CodexForkHistoryCatalog.Read(fixture.Home, request, CancellationToken.None));
    }

    [Fact]
    public void FollowsBoundedParentChainAndFallsBackWhenParentTitleUnavailable()
    {
        using var fixture = new Fixture();
        fixture.Add("original", "Source task", "Question");
        fixture.Add("middle", "", "", parentId: "original");
        fixture.Add("child", "", "", parentId: "middle");
        fixture.Add("orphan", "", "", parentId: "missing-parent");
        fixture.Save();

        var rows = CodexForkHistoryCatalog.Read(fixture.Home, fixture.Request(), CancellationToken.None);

        Assert.Equal("Source task", Assert.Single(rows, row => row["id"]?.Value<string>() == "child")["name"]?.Value<string>());
        Assert.Equal("Forked chat", Assert.Single(rows, row => row["id"]?.Value<string>() == "orphan")["name"]?.Value<string>());
    }

    [Fact]
    public void KeepsOwnNameAndSearchesTheFullInheritedNameBeforeDisplayTruncation()
    {
        using var fixture = new Fixture();
        fixture.Add("parent", "Old parent title", "Prompt", name: "Renamed parent " + new string('x', 245) + " needle");
        fixture.Add("own", "", "", parentId: "parent", name: "Renamed fork");
        fixture.Add("inherited", "", "", parentId: "parent");
        fixture.Save();

        var rows = CodexForkHistoryCatalog.Read(fixture.Home, fixture.Request(), CancellationToken.None);
        Assert.Equal("Renamed fork", Assert.Single(rows, row => row["id"]?.Value<string>() == "own")["name"]?.Value<string>());
        Assert.StartsWith("Renamed parent ", Assert.Single(rows, row => row["id"]?.Value<string>() == "inherited")["name"]?.Value<string>());
        var request = fixture.Request();
        request["searchTerm"] = "needle";
        var found = Assert.Single(CodexForkHistoryCatalog.Read(fixture.Home, request, CancellationToken.None));
        Assert.Equal("inherited", found["id"]?.Value<string>());
        Assert.Equal(240, found["name"]?.Value<string>()?.Length);
    }

    [Fact]
    public void IgnoresManyBlankRowsInOtherWorkspacesBeforeApplyingTheCandidateLimit()
    {
        using var fixture = new Fixture();
        fixture.Add("parent", "Parent", "Message");
        for (var index = 0; index < 10001; index++)
            fixture.Add("other-" + index, "", "", cwd: @"D:\Other", writeFile: false);
        fixture.Add("wanted", "", "", parentId: "parent");
        fixture.Save();

        Assert.Equal("wanted", Assert.Single(CodexForkHistoryCatalog.Read(fixture.Home, fixture.Request(), CancellationToken.None))["id"]?.Value<string>());
    }

    [Fact]
    public void ReadsOlderCatalogWithoutNameOrMillisecondTimestampColumns()
    {
        using var fixture = new Fixture(legacyTimestamps: true);
        fixture.Add("parent", "Parent title", "Message");
        fixture.Add("fork", "", "", parentId: "parent");
        fixture.Save();

        var fork = Assert.Single(CodexForkHistoryCatalog.Read(fixture.Home, fixture.Request(), CancellationToken.None));
        Assert.Equal("Parent title", fork["name"]?.Value<string>());
        Assert.Equal(1750000000L, fork["createdAt"]?.Value<long>());
        Assert.Equal(1750000001L, fork["updatedAt"]?.Value<long>());
    }

    [Fact]
    public void AcceptsAValidForkHeaderLargerThanTheOld64KiBLimit()
    {
        using var fixture = new Fixture();
        fixture.Add("parent", "Parent", "Message");
        fixture.Add("fork", "", "", parentId: "parent");
        var header = JObject.Parse(Header("fork", "parent"));
        header["payload"]!["base_instructions"] = new string('x', 80 * 1024);
        File.WriteAllText(Path.Combine(fixture.Home, "sessions", "fork.jsonl"),
            NewtonsoftJsonCompatibility.Serialize(header, Formatting.None) + "\n", new UTF8Encoding(false));
        fixture.Save();

        Assert.Equal("fork", Assert.Single(CodexForkHistoryCatalog.Read(fixture.Home, fixture.Request(), CancellationToken.None))["id"]?.Value<string>());
    }

    [Fact]
    public void RejectsEscapedPathAndMismatchedSessionHeader()
    {
        using var fixture = new Fixture();
        fixture.Add("parent", "Parent", "Message");
        var outside = Path.Combine(Path.GetDirectoryName(fixture.Home)!, "outside-fork-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllText(outside, Header("escape", "parent"), new UTF8Encoding(false));
            fixture.Add("escape", "", "", parentId: "parent", path: outside);
            fixture.Add("mismatch", "", "", parentId: "parent", headerId: "another-id");
            fixture.Save();

            Assert.Empty(CodexForkHistoryCatalog.Read(fixture.Home, fixture.Request(), CancellationToken.None));
        }
        finally { if (File.Exists(outside)) File.Delete(outside); }
    }

    [Fact]
    public void ReturnsEmptyWithoutCatalogAndHonorsCancellation()
    {
        using var fixture = new Fixture();
        Assert.Empty(CodexForkHistoryCatalog.Read(fixture.Home, fixture.Request(), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CodexForkHistoryCatalog.Read(fixture.Home, fixture.Request(), cancellation.Token));
    }

    private static string Header(string id, string? parentId)
    {
        var record = new JObject
        {
            ["type"] = "session_meta",
            ["payload"] = new JObject
            {
                ["id"] = id,
                ["forked_from_id"] = parentId is null ? JValue.CreateNull() : new JValue(parentId)
            }
        };
        return NewtonsoftJsonCompatibility.Serialize(record, Formatting.None) + "\n";
    }

    private static string Hash(string path)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vsai-fork-catalog-" + Guid.NewGuid().ToString("N"));
        private readonly StringBuilder _sql = new StringBuilder();
        private readonly bool _legacyTimestamps;

        internal Fixture(bool legacyTimestamps = false)
        {
            _legacyTimestamps = legacyTimestamps;
            Directory.CreateDirectory(_root);
            _sql.Append(legacyTimestamps
                ? "CREATE TABLE threads (id TEXT, rollout_path TEXT, model_provider TEXT, archived INTEGER, cwd TEXT, source TEXT, created_at INTEGER, updated_at INTEGER, title TEXT, preview TEXT, cli_version TEXT);"
                : "CREATE TABLE threads (id TEXT, rollout_path TEXT, model_provider TEXT, archived INTEGER, cwd TEXT, source TEXT, created_at_ms INTEGER, updated_at_ms INTEGER, name TEXT, title TEXT, preview TEXT, cli_version TEXT);");
        }

        internal string Home => _root;
        internal string Database => Path.Combine(_root, "state_12.sqlite");

        internal JObject Request() => new JObject
        {
            ["archived"] = false,
            ["cwd"] = new JArray(@"D:\Work\Project"),
            ["modelProviders"] = new JArray(),
            ["sourceKinds"] = new JArray()
        };

        internal void Add(string id, string title, string preview, string? parentId = null,
            bool archived = false, string cwd = @"D:\Work\Project", string provider = "openai",
            string source = "vscode", string? path = null, string? headerId = null,
            string name = "", bool writeFile = true)
        {
            path ??= Path.Combine(_root, "sessions", id + ".jsonl");
            if (writeFile && path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Header(headerId ?? id, parentId), new UTF8Encoding(false));
            }
            _sql.Append("INSERT INTO threads VALUES (")
                .Append(Q(id)).Append(',').Append(Q(path)).Append(',').Append(Q(provider)).Append(',')
                .Append(archived ? '1' : '0').Append(',').Append(Q(cwd)).Append(',').Append(Q(source)).Append(',')
                .Append(_legacyTimestamps ? "1750000000,1750000001," : "1750000000000,1750000000001,");
            if (!_legacyTimestamps) _sql.Append(Q(name)).Append(',');
            _sql.Append(Q(title)).Append(',').Append(Q(preview))
                .Append(", '0.156.1');");
        }

        internal void Save()
        {
            IntPtr database = IntPtr.Zero;
            try
            {
                Assert.Equal(0, sqlite3_open_v2(Encoding.UTF8.GetBytes(Database + "\0"), out database, 0x00000002 | 0x00000004, IntPtr.Zero));
                var result = sqlite3_exec(database, Encoding.UTF8.GetBytes(_sql + "\0"), IntPtr.Zero, IntPtr.Zero, out var error);
                var message = error == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(error);
                if (error != IntPtr.Zero) sqlite3_free(error);
                Assert.True(result == 0, message);
            }
            finally { if (database != IntPtr.Zero) sqlite3_close(database); }
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

        private static string Q(string value) => "'" + value.Replace("'", "''") + "'";
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
}
