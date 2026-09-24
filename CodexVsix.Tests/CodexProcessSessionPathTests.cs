using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProcessSessionPathTests
{
    [Fact]
    public void ResolvePrivateSessionPathRejectsSharedDesktopPathWhenNoPrivateCacheExists()
    {
        using var shared = new TemporaryDirectory();
        var privateHome = Path.Combine(shared.Path, "vsai");
        var desktopPath = WriteRollout(Path.Combine(shared.Path, "sessions"), "desktop-thread.jsonl");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CodexProcessService.ResolvePrivateSessionPath(desktopPath, "desktop-thread", privateHome));

        Assert.Contains("private session storage", exception.Message);
    }

    [Fact]
    public void ResolvePrivateSessionPathFallsBackToPrivateLegacyCacheInsteadOfSharedPath()
    {
        using var shared = new TemporaryDirectory();
        var privateHome = Path.Combine(shared.Path, "vsai");
        var desktopPath = WriteRollout(Path.Combine(shared.Path, "sessions"), "thread-42.jsonl");
        var privateCache = WriteRollout(Path.Combine(privateHome, "sessions", "legacy"), "thread-42-cache.jsonl");

        var resolved = CodexProcessService.ResolvePrivateSessionPath(desktopPath, "thread-42", privateHome);

        Assert.Equal(Path.GetFullPath(privateCache), resolved);
    }

    [Fact]
    public void ResolvePrivateSessionPathAcceptsExtendedWin32PrivatePath()
    {
        using var shared = new TemporaryDirectory();
        var privateHome = Path.Combine(shared.Path, "vsai");
        var cache = WriteRollout(Path.Combine(privateHome, "sessions"), "thread-extended.jsonl");

        var resolved = CodexProcessService.ResolvePrivateSessionPath(@"\\?\" + cache, "thread-extended", privateHome);

        Assert.Equal(Path.GetFullPath(cache), resolved);
    }

    [Fact]
    public void ResolvePrivateSessionPathRejectsJunctionInsidePrivateSessions()
    {
        using var shared = new TemporaryDirectory();
        var privateHome = Path.Combine(shared.Path, "vsai");
        var outside = Path.Combine(shared.Path, "outside");
        var link = Path.Combine(privateHome, "sessions", "linked");
        WriteRollout(outside, "thread-linked.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateJunction(link, outside);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                CodexProcessService.ResolvePrivateSessionPath(Path.Combine(link, "thread-linked.jsonl"), "thread-linked", privateHome));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    private static string WriteRollout(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "{\"type\":\"event_msg\"}", new UTF8Encoding(false));
        return path;
    }

    private static void CreateJunction(string link, string target)
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = "/d /s /c mklink /J " + Quote(link) + " " + Quote(target),
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException("Could not create the synthetic session junction.");
        }
    }

    private static string Quote(string path) => "\"" + path + "\"";
}
