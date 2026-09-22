using System.IO;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexSourceLinkNavigationTests
{
    [Theory]
    [InlineData("100% #", 1, 1)]
    [InlineData("literal%20#name", 42, 7)]
    [InlineData("literal%2523", int.MaxValue, int.MaxValue)]
    public void DecodedOfficialSourceLinkPayloadKeepsExactFileIdentityAndCoordinates(string name, int line, int column)
    {
        using var temp = new TemporaryDirectory();
        var directory = Path.Combine(temp.Path, "工程 空格", name);
        var siblingDirectory = Path.Combine(temp.Path, "other");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(siblingDirectory);
        var path = Path.Combine(directory, "File.cpp");
        File.WriteAllText(path, "int target;\r\n");
        File.WriteAllText(Path.Combine(siblingDirectory, "File.cpp"), "int wrongFile;");
        // The frozen Markdown parser supplies the decoded path and separate 1-based
        // coordinates; the host must not infer a different file from the workspace.
        var request = new JObject
        {
            ["path"] = path.Replace('\\', '/'),
            ["line"] = line,
            ["column"] = column,
            ["cwd"] = siblingDirectory
        };

        Assert.True(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(request, siblingDirectory, out var target));
        Assert.Equal(path, target.Path);
        Assert.Equal(line, target.Line);
        Assert.Equal(column, target.Column);
    }
}
