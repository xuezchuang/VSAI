using System;
using System.IO;
using System.Linq;
using System.Text;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexUserInstructionsStoreTests
{
    [Fact]
    public void EditorReadsAndWritesThePrivateFileAndSeesExternalEdits()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "Private instructions\n", new UTF8Encoding(false));
        var read = fixture.Read();
        Assert.Equal(fixture.Path, read["path"]?.Value<string>());
        Assert.Equal("Private instructions\n", read["contents"]?.Value<string>());
        Assert.False(read["hasOverride"]?.Value<bool>());

        var saved = fixture.Save("Edited in VSAI\n");
        Assert.Equal(fixture.Path, saved["path"]?.Value<string>());
        Assert.Equal("Edited in VSAI\n", File.ReadAllText(fixture.Path));
        Assert.Equal("Private instructions\n", File.ReadAllText(fixture.Path + ".vsai.bak"));
        File.WriteAllText(fixture.Path, "Changed by the user\n", new UTF8Encoding(false));
        Assert.Equal("Changed by the user\n", fixture.Read()["contents"]?.Value<string>());
    }

    [Theory]
    [InlineData("utf8", "\n")]
    [InlineData("utf8-bom", "\r\n")]
    [InlineData("utf16", "\r\n")]
    public void SavePreservesEncodingBomAndExistingNewlines(string format, string newline)
    {
        using var fixture = new Fixture();
        Encoding encoding = format == "utf16" ? new UnicodeEncoding(false, true)
            : new UTF8Encoding(format == "utf8-bom");
        File.WriteAllText(fixture.Path, "原有规则" + newline, encoding);
        var previousBytes = File.ReadAllBytes(fixture.Path);

        fixture.Save("中文修改\nsecond line\n");

        var expected = encoding.GetPreamble().Concat(encoding.GetBytes("中文修改" + newline + "second line" + newline)).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(fixture.Path));
        Assert.Equal(previousBytes, File.ReadAllBytes(fixture.Path + ".vsai.bak"));
    }

    [Fact]
    public void MissingFileIsEmptyWithoutCreatingItAndExplicitEmptySaveIsAllowed()
    {
        using var fixture = new Fixture();
        Assert.Equal(string.Empty, fixture.Read()["contents"]?.Value<string>());
        Assert.False(File.Exists(fixture.Path));
        fixture.Save("new instructions");
        Assert.Equal("new instructions", File.ReadAllText(fixture.Path));
        fixture.Save(string.Empty);
        Assert.Equal(string.Empty, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void OverrideIsReportedButNeverEditedInsteadOfAgentsFile()
    {
        using var fixture = new Fixture();
        var overrides = System.IO.Path.Combine(fixture.Directory, "AGENTS.override.md");
        File.WriteAllText(overrides, "override rules");
        Assert.True(fixture.Read()["hasOverride"]?.Value<bool>());
        Assert.Equal(string.Empty, fixture.Read()["contents"]?.Value<string>());
        fixture.Save("base rules");
        Assert.Equal("override rules", File.ReadAllText(overrides));
        File.WriteAllText(overrides, " \r\n");
        Assert.False(fixture.Read()["hasOverride"]?.Value<bool>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidFileIsNotSilentlyReplacedOnSave(int format)
    {
        using var fixture = new Fixture();
        var invalid = format switch
        {
            1 => new byte[] { 0xef, 0xbb, 0xbf, 0xff, 0x41 },
            2 => new byte[] { 0xff, 0xfe, 0x41 },
            _ => new byte[] { 0xff, 0x41, 0x42 }
        };
        File.WriteAllBytes(fixture.Path, invalid);
        Assert.Throws<DecoderFallbackException>(() => fixture.Read());
        Assert.Throws<DecoderFallbackException>(() => fixture.Save("replacement"));
        Assert.Equal(invalid, File.ReadAllBytes(fixture.Path));
        Assert.False(File.Exists(fixture.Path + ".vsai.bak"));
    }

    [Fact]
    public void RemoteHostAndMissingContentsCannotWriteLocalInstructions()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>(() => fixture.Request("codex-agents-md-save",
            new JObject { ["hostId"] = "remote", ["contents"] = "wrong host" }));
        Assert.Throws<ArgumentException>(() => fixture.Request("codex-agents-md-save", new JObject()));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public void WebViewSuppliedPathCannotRedirectWrites()
    {
        using var fixture = new Fixture();
        var other = System.IO.Path.Combine(fixture.Directory, "project-AGENTS.md");
        File.WriteAllText(other, "project rules");
        fixture.Request("codex-agents-md-save", new JObject { ["path"] = other, ["contents"] = "user rules" });
        Assert.Equal("user rules", File.ReadAllText(fixture.Path));
        Assert.Equal("project rules", File.ReadAllText(other));
    }

    private sealed class Fixture : IDisposable
    {
        private string SharedHome { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VSAI-instructions-tests", Guid.NewGuid().ToString("N"));
        public string Directory => System.IO.Path.Combine(SharedHome, "vsai");
        public string Path => System.IO.Path.Combine(Directory, "AGENTS.md");
        public Fixture() => System.IO.Directory.CreateDirectory(Directory);
        public JObject Read() => Request("codex-agents-md", new JObject { ["hostId"] = "local" });
        public JObject Save(string contents) => Request("codex-agents-md-save", new JObject { ["hostId"] = "local", ["contents"] = contents });
        public JObject Request(string method, JObject values)
            => CodexUserInstructionsStore.HandleRequest(method, values, "CODEX_HOME=" + SharedHome);
        public void Dispose() => System.IO.Directory.Delete(SharedHome, recursive: true);
    }
}
