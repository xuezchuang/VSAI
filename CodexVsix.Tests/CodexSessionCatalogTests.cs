using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexSessionCatalogTests
{
    [Fact]
    public void ReadsHighestCatalogVersionWithoutChangingTheDatabase()
    {
        using var home = new TemporaryDirectory();
        CreateCatalog(Path.Combine(home.Path, "state_1.sqlite"), "CREATE TABLE threads (id TEXT, rollout_path TEXT, model_provider TEXT, archived INTEGER, originator TEXT); INSERT INTO threads VALUES ('old','C:\\旧','vsai_old',0,'other');");
        var database = Path.Combine(home.Path, "state_12.sqlite");
        CreateCatalog(database, "CREATE TABLE threads (id TEXT, rollout_path TEXT, model_provider TEXT, archived INTEGER, originator TEXT);"
            + "INSERT INTO threads VALUES ('origin','C:\\会话\\一.jsonl','openai',0,'codex-vsix');"
            + "INSERT INTO threads VALUES ('provider','C:\\会话\\二.jsonl','vsai_测试',1,'Codex Desktop');"
            + "INSERT INTO threads VALUES ('other','C:\\会话\\三.jsonl','openai',0,'other');");
        var before = Hash(database);

        var candidates = CodexSessionCatalog.ReadVsaiCandidates(home.Path);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("origin", candidates[0]["id"]?.Value<string>());
        Assert.Equal("C:\\会话\\一.jsonl", candidates[0]["path"]?.Value<string>());
        Assert.True(candidates[0]["vsaiCatalogOrigin"]?.Value<bool>());
        Assert.Equal("provider", candidates[1]["id"]?.Value<string>());
        Assert.True(candidates[1]["archived"]?.Value<bool>());
        Assert.Equal(before, Hash(database));
    }

    [Fact]
    public void ReturnsEmptyWhenTheSharedHomeHasNoCatalog()
    {
        using var home = new TemporaryDirectory();
        Assert.Empty(CodexSessionCatalog.ReadVsaiCandidates(home.Path));
    }

    private static void CreateCatalog(string path, string sql)
    {
        IntPtr database = IntPtr.Zero;
        try
        {
            Assert.Equal(0, sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out database, 0x00000002 | 0x00000004, IntPtr.Zero));
            var error = IntPtr.Zero;
            var result = sqlite3_exec(database, Encoding.UTF8.GetBytes(sql + "\0"), IntPtr.Zero, IntPtr.Zero, out error);
            var message = error == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            Assert.True(result == 0, message);
        }
        finally { if (database != IntPtr.Zero) sqlite3_close(database); }
    }

    private static string Hash(string path)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)));
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
}
