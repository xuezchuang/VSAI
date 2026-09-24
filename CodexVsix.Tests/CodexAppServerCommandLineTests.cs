using System;
using System.IO;
using System.Linq;
using CodexVsix.Models;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexAppServerCommandLineTests
{
    [Fact]
    public void SandboxSetupPermissionOverrideIsLastAndDoesNotChangeNormalServerArguments()
    {
        var settings = new CodexExtensionSettings { RawTomlOverrides = "sandbox_mode=\"danger-full-access\"" };
        var overrideValue = "sandbox_mode=\"workspace-write\"";

        var setupArguments = CodexAppServerCommandLine.SplitArguments(
            CodexAppServerCommandLine.Build(settings, finalConfigOverrides: new[] { overrideValue })).ToArray();
        var regularArguments = CodexAppServerCommandLine.SplitArguments(
            CodexAppServerCommandLine.Build(settings)).ToArray();

        Assert.Equal("-c", setupArguments[setupArguments.Length - 2]);
        Assert.Equal(overrideValue, setupArguments[setupArguments.Length - 1]);
        Assert.DoesNotContain(overrideValue, regularArguments);
    }

    [Fact]
    public void ExecutableResolverAcceptsQuotedAndEnvironmentExpandedPaths()
    {
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "codex.cmd");
        File.WriteAllText(executable, string.Empty);
        var variableName = "CODEX_VSIX_TEST_" + Guid.NewGuid().ToString("N");

        Environment.SetEnvironmentVariable(variableName, directory.Path);
        try
        {
            Assert.Equal(executable, CodexExecutableResolver.ResolveExecutableLocation("\"" + executable + "\"", null));
            Assert.Equal(executable, CodexExecutableResolver.ResolveExecutableLocation("%" + variableName + "%\\codex.cmd", null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void ComparablePathPreservesWindowsDriveRoot()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(@"C:\"));

        Assert.Equal(root, CodexProcessService.NormalizeComparablePath(@"\\?\C:\"));
    }

    [Fact]
    public void SkillScopeUsesDirectoryBoundariesAndExactSystemSegments()
    {
        Assert.True(CodexProcessService.IsPathWithinDirectory(@"C:\skills\review\SKILL.md", @"C:\skills"));
        Assert.False(CodexProcessService.IsPathWithinDirectory(@"C:\skills-old\review\SKILL.md", @"C:\skills"));
        Assert.True(CodexProcessService.IsSystemSkillPath(@"C:\skills\.system\review\SKILL.md"));
        Assert.False(CodexProcessService.IsSystemSkillPath(@"C:\skills\my.systematic\review\SKILL.md"));
    }

    [Fact]
    public void ManagedMcpOverridesUseDottedKeysAcceptedByCodexCli()
    {
        var settings = new CodexExtensionSettings
        {
            ModelVerbosity = "HIGH",
            ManagedMcpServers =
            {
                new CodexManagedMcpServer
                {
                    Name = "review-probe",
                    TransportType = "stdio",
                    Command = @"C:\Program Files\node.exe",
                    Arguments = "server.js\n--safe"
                },
                new CodexManagedMcpServer
                {
                    Name = "remote_docs",
                    TransportType = "url",
                    Url = "https://example.test/mcp"
                }
            }
        };

        var overrides = CodexAppServerCommandLine.BuildConfigOverrides(settings);

        Assert.Contains("model_verbosity=\"high\"", overrides);
        Assert.Contains("mcp_servers.review-probe.command=\"C:\\\\Program Files\\\\node.exe\"", overrides);
        Assert.Contains("mcp_servers.review-probe.args=[\"server.js\", \"--safe\"]", overrides);
        Assert.Contains("mcp_servers.remote_docs.url=\"https://example.test/mcp\"", overrides);
        Assert.DoesNotContain(overrides, value => value.StartsWith("[mcp_servers."));
    }

    [Theory]
    [InlineData(@"C:\tools\codex.exe", @"C:\tools\codex.exe")]
    [InlineData(@"C:\Program Files\codex.ps1", @"""C:\Program Files\codex.ps1""")]
    [InlineData("", "\"\"")]
    public void QuoteArgumentPreservesSimpleValues(string value, string expected)
    {
        Assert.Equal(expected, CodexAppServerCommandLine.QuoteArgument(value));
    }

    [Fact]
    public void SplitArgumentsRoundTripsQuotedWindowsPath()
    {
        var values = CodexAppServerCommandLine.SplitArguments("--profile \"My Profile\" --flag=C:\\work").ToArray();

        Assert.Equal(new[] { "--profile", "My Profile", @"--flag=C:\work" }, values);
    }
}
