using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class SolutionContextServiceTests
{
    [Fact]
    public void FormatPathRequiresARealDirectoryBoundary()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "foo");
        var inside = Path.Combine(root, "src", "file.cs");
        var sibling = Path.Combine(temp.Path, "foobar", "file.cs");
        var service = new SolutionContextService();

        Assert.Equal("src/file.cs", service.FormatPathForPrompt(root, inside));
        Assert.Equal(Path.GetFullPath(sibling).Replace('\\', '/'), service.FormatPathForPrompt(root, sibling));
    }

    [Fact]
    public void FormatPathPreservesDriveRootContainment()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.GetPathRoot(temp.Path)!;
        var service = new SolutionContextService();

        var result = service.FormatPathForPrompt(root, temp.Path);

        Assert.False(Path.IsPathRooted(result));
        Assert.DoesNotContain(":", result);
    }

    [Fact]
    public async Task FileIndexPrunesGeneratedDirectoriesAndSupportsSpaces()
    {
        using var temp = new TemporaryDirectory();
        WriteFile(Path.Combine(temp.Path, "src", "Feature Folder", "Good File.cs"));
        WriteFile(Path.Combine(temp.Path, "bin", "Ignored.cs"));
        WriteFile(Path.Combine(temp.Path, "node_modules", "IgnoredToo.js"));
        var service = new SolutionContextService();

        var results = await service.FindSolutionFilesAsync(temp.Path, "Good File", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("src/Feature Folder/Good File.cs", results.Single());
        Assert.DoesNotContain(results, path => path.IndexOf("Ignored", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [Theory]
    [InlineData(@"C:\工程 目录\文件.cpp:42:7", @"C:\工程 目录\文件.cpp", 42, 7)]
    [InlineData(@"C:\src\file.cpp#L12C3", @"C:\src\file.cpp", 12, 3)]
    [InlineData("src/file.cpp#L12-L16", "src/file.cpp", 12, null)]
    [InlineData("src/file.cpp:12", "src/file.cpp", 12, null)]
    [InlineData("common.cpp:12:3", "common.cpp", 12, 3)]
    [InlineData(@"C:\src\literal%20#name.cpp:8", @"C:\src\literal%20#name.cpp", 8, null)]
    [InlineData("file:///C:/src/literal%2520%23name.cpp#L8C2", @"C:\src\literal%20#name.cpp", 8, 2)]
    [InlineData("file://server/share/file.cpp#L8", @"\\server\share\file.cpp", 8, null)]
    public void FileReferencesPreservePathIdentityAndOneBasedCoordinates(string reference, string path, int line, int? column)
    {
        Assert.True(SolutionContextService.TryParseFileReference(reference, out var target));
        Assert.Equal(path, target.Path);
        Assert.Equal(line, target.Line);
        Assert.Equal(column, target.Column);
    }

    [Theory]
    [InlineData("https://example.test/file.cpp#L1")]
    [InlineData("file:///C:/file.cpp#unrecognized")]
    [InlineData("src/file.cpp:0")]
    [InlineData("src/file.cpp:1:0")]
    [InlineData("src/file.cpp:2147483648")]
    [InlineData("src/file.cpp#L99999999999999999999")]
    public void InvalidReferencesDoNotSilentlyDropTheirRequestedPosition(string reference)
    {
        Assert.False(SolutionContextService.TryParseFileReference(reference, out _));
    }

    [Fact]
    public void FileUriWithUnicodeSpacesAndLiteralEscapesResolvesWithoutDoubleDecoding()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "中文 目录", "literal%20#name.cpp");
        WriteFile(path);
        var reference = new Uri("file:///" + path.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23")).AbsoluteUri + "#L17C4";
        Assert.Equal(path, new Uri(reference).LocalPath);

        Assert.True(SolutionContextService.TryResolveFileReference(reference, null, out var target));
        Assert.Equal(path, target.Path);
        Assert.Equal(17, target.Line);
        Assert.Equal(4, target.Column);
    }

    [Fact]
    public void RelativeReferencesUseOnlyTheSuppliedWorkspaceAndRejectDriveRelativePaths()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "src", "file.cpp");
        WriteFile(path);

        Assert.True(SolutionContextService.TryResolveFileReference("src/file.cpp:2", temp.Path, out var target));
        Assert.Equal(path, target.Path);
        Assert.False(SolutionContextService.TryResolveFileReference("src/file.cpp:2", null, out _));
        Assert.False(SolutionContextService.TryResolveFileReference("C:file.cpp:2", temp.Path, out _));
    }

    [Fact]
    public void SolutionFallbackRejectsAmbiguousNamesButKeepsExactAndUniqueSuffixes()
    {
        using var temp = new TemporaryDirectory();
        var first = Path.Combine(temp.Path, "a", "common.cpp");
        var second = Path.Combine(temp.Path, "longer", "common.cpp");
        WriteFile(first);
        WriteFile(second);
        var files = new[] { first, second };

        Assert.Null(SolutionContextService.FindUnambiguousSolutionFile("common.cpp", temp.Path, files));
        Assert.Equal(second, SolutionContextService.FindUnambiguousSolutionFile("longer/common.cpp", temp.Path, files));
        Assert.Equal(first, SolutionContextService.FindUnambiguousSolutionFile("a/common.cpp", null, files));
        Assert.Null(SolutionContextService.FindUnambiguousSolutionFile(Path.Combine(temp.Path, "missing", "common.cpp"), temp.Path, files));
        Assert.Equal(first, SolutionContextService.FindUnambiguousSolutionFile("common.cpp", temp.Path, new[] { first, first }));
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue, 3, 1)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(2, 99, 2, 5)]
    [InlineData(2, null, 2, null)]
    public void NavigationClampsAgainstTheSelectedLineIncludingEmptyLastLines(int line, int? column, int expectedLine, int? expectedColumn)
    {
        var lengths = new[] { 8, 4, 0 };
        SolutionContextService.ClampNavigationPosition(line, column, lengths.Length, selected => lengths[selected - 1],
            out var actualLine, out var actualColumn);

        Assert.Equal(expectedLine, actualLine);
        Assert.Equal(expectedColumn, actualColumn);
    }

    [Theory]
    [InlineData("common.cpp:12", true)]
    [InlineData("src/文件 名.hpp#L3", true)]
    [InlineData("missing.cpp", true)]
    [InlineData("return value;", false)]
    [InlineData("source.cpp:0", false)]
    [InlineData("https://example.test/file.cpp", false)]
    [InlineData(@"C:\missing\file.cpp", false)]
    public void PotentialSolutionReferencesAreRecognizedWithoutEnumeratingOrRequiringFiles(string reference, bool expected)
    {
        Assert.Equal(expected, SolutionContextService.IsPotentialRelativeFileReference(reference));
    }

    [Fact]
    public void EscapedMarkdownPathsDecodeOnceOnlyAfterCheckingTheLiteralFileName()
    {
        using var temp = new TemporaryDirectory();
        var literal = Path.Combine(temp.Path, "file%20name.cpp");
        var spaced = Path.Combine(temp.Path, "other name.cpp");
        WriteFile(literal);
        WriteFile(spaced);

        Assert.True(SolutionContextService.TryResolveFileReference("file%20name.cpp:2", temp.Path, out var literalTarget));
        Assert.Equal(literal, literalTarget.Path);
        Assert.True(SolutionContextService.TryResolveFileReference("other%20name.cpp:3", temp.Path, out var decodedTarget));
        Assert.Equal(spaced, decodedTarget.Path);
        var missingLiteral = Path.Combine(temp.Path, "other%20name.cpp");
        var missingUri = "file:///" + missingLiteral.Replace('\\', '/').Replace("%", "%25");
        Assert.Equal(missingLiteral, new Uri(missingUri).LocalPath);
        Assert.False(SolutionContextService.TryResolveFileReference(missingUri, temp.Path, out _));
    }

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
    }
}
