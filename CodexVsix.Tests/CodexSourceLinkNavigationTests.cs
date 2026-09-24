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

    [Fact]
    public void RelativeOfficialSourceLinkUsesOnlyAnUnambiguousSolutionFile()
    {
        using var temp = new TemporaryDirectory();
        var project = Path.Combine(temp.Path, "project");
        var other = Path.Combine(temp.Path, "other");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        Directory.CreateDirectory(Path.Combine(other, "src"));
        var expected = Path.Combine(project, "src", "Example.cpp");
        var duplicate = Path.Combine(other, "src", "Example.cpp");
        File.WriteAllText(expected, "int target;\r\n");
        File.WriteAllText(duplicate, "int other;\r\n");
        var request = new JObject { ["path"] = "src/Example.cpp:23", ["column"] = 4 };

        Assert.False(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(request, temp.Path, out _));
        Assert.True(CodexOfficialWebViewBridge.TryResolveOpenFileRequestFromSolution(
            request, temp.Path, () => new[] { expected }, out var target));
        Assert.Equal(expected, target.Path);
        Assert.Equal(23, target.Line);
        Assert.Equal(4, target.Column);

        Assert.False(CodexOfficialWebViewBridge.TryResolveOpenFileRequestFromSolution(
            request, temp.Path, () => new[] { expected, duplicate }, out _));
    }
}
