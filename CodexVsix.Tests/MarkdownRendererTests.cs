using System;
using System.IO;
using System.Windows.Input;
using CodexVsix.UI;
using Xunit;

namespace CodexVsix.Tests;

public sealed class MarkdownRendererTests
{
    [Theory]
    [InlineData("https://example.test/path")]
    [InlineData("http://example.test/")]
    [InlineData("mailto:user@example.test")]
    public void ExternalLinksAllowOnlyExpectedSchemes(string value)
    {
        Assert.True(MarkdownRenderer.TryGetSafeExternalUri(value, out _));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,unsafe")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("C:\\work\\file.cs")]
    public void ExternalLinksRejectExecutableAndLocalSchemes(string value)
    {
        Assert.False(MarkdownRenderer.TryGetSafeExternalUri(value, out _));
    }
    [Fact]
    public void WorkspaceReferencesUseTheSharedUriAndPositionParser()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "中文 %20#file.cpp");
        File.WriteAllText(path, "source");

        Assert.True(MarkdownRenderer.TryResolveWorkspaceFileReference(new Uri("file:///" + path.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23")).AbsoluteUri + "#L12C3", temp.Path, out var target));
        Assert.Equal(path + ":12:3", target);
        Assert.False(MarkdownRenderer.TryResolveWorkspaceFileReference(path + ":0", temp.Path, out _));
        Assert.False(MarkdownRenderer.TryResolveWorkspaceFileReference(path, Path.Combine(temp.Path, "other"), out _));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("text", true)]
    [InlineData("plaintext", true)]
    [InlineData("cpp", false)]
    public void PlainTextCodeReferenceTargetsPreserveTheCompletePathAndPosition(string language, bool expectedLink)
    {
        const string reference = @"C:\工程 目录\file.cpp:12";
        var command = new ReferenceCommand(reference);

        Assert.Equal(expectedLink, MarkdownRenderer.TryGetCodeFileReferenceTarget("  " + reference + "  ", language, command, out var target));
        Assert.Equal(expectedLink ? reference : string.Empty, target);
        Assert.False(MarkdownRenderer.TryGetCodeFileReferenceTarget(reference, language, null, out _));
        Assert.False(MarkdownRenderer.TryGetCodeFileReferenceTarget("ordinary code", language, command, out _));
    }

    private sealed class ReferenceCommand : ICommand
    {
        private readonly string _reference;
        public ReferenceCommand(string reference) => _reference = reference;
        public bool CanExecute(object? parameter) => string.Equals(parameter as string, _reference, StringComparison.Ordinal);
        public void Execute(object? parameter) { }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
