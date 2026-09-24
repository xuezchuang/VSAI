using System;
using System.IO;
using CodexVsix.Models;
using CodexVsix.Services;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexWorkingDirectoryTests
{
    [Fact]
    public void ExistingSettingsFollowTheSolutionUntilAFolderIsChosen()
    {
        var settings = new CodexExtensionSettings { WorkingDirectory = @"C:\previous" };

        Assert.Equal(@"D:\solution", CodexWorkingDirectory.Resolve(settings, @"D:\solution"));
    }

    [Fact]
    public void DifferentSolutionsDoNotInheritALegacyPinnedFolder()
    {
        var settings = new CodexExtensionSettings
        {
            WorkingDirectory = @"C:\previous",
            FollowSolutionDirectory = false
        };

        Assert.Equal(@"D:\first", CodexWorkingDirectory.ResolveForSolution(
            settings, @"D:\first\first.sln", @"D:\first"));
        Assert.Equal(@"E:\second", CodexWorkingDirectory.ResolveForSolution(
            settings, @"E:\second\second.sln", @"E:\second"));
    }

    [Fact]
    public void ACustomFolderAppliesOnlyToItsSolution()
    {
        var settings = new CodexExtensionSettings();
        settings.SolutionWorkingDirectories[@"D:\first\first.sln"] = @"F:\first-worktree";

        Assert.Equal(@"F:\first-worktree", CodexWorkingDirectory.ResolveForSolution(
            settings, @"d:\FIRST\FIRST.sln", @"D:\first"));
        Assert.Equal(@"E:\second", CodexWorkingDirectory.ResolveForSolution(
            settings, @"E:\second\second.sln", @"E:\second"));
    }

    [Fact]
    public void SelectedFolderSurvivesReloadWithoutASolution()
    {
        using var temp = new TemporaryDirectory();
        var chosen = Path.Combine(temp.Path, "工程 with spaces %23 #1");
        Directory.CreateDirectory(chosen);
        var store = new ExtensionSettingsStore(Path.Combine(temp.Path, "settings.json"));
        var settings = new CodexExtensionSettings { WorkingDirectory = @"C:\solution\src" };

        Assert.True(CodexWorkingDirectory.Select(settings, chosen, false, value => store.Save(value)));
        var reloaded = store.Load();

        Assert.False(reloaded.FollowSolutionDirectory);
        Assert.Equal(chosen, CodexWorkingDirectory.Resolve(reloaded));
        Assert.Equal(chosen, CodexWorkingDirectory.Resolve(reloaded.WorkingDirectory));
    }

    [Fact]
    public void UsingSolutionDirectoryRestoresAutomaticSelection()
    {
        using var temp = new TemporaryDirectory();
        var settings = new CodexExtensionSettings { WorkingDirectory = @"C:\manual", FollowSolutionDirectory = false };

        Assert.True(CodexWorkingDirectory.Select(settings, temp.Path, true, _ => { }));

        Assert.True(settings.FollowSolutionDirectory);
        Assert.Equal(@"D:\next-solution", CodexWorkingDirectory.Resolve(settings, @"D:\next-solution"));
    }

    [Fact]
    public void ChoosingTheSameFolderPinsItWithoutResettingTheConversation()
    {
        using var temp = new TemporaryDirectory();
        var settings = new CodexExtensionSettings { WorkingDirectory = temp.Path, CurrentThreadId = "current" };

        Assert.False(CodexWorkingDirectory.Select(settings, temp.Path.ToUpperInvariant() + "\\", false, _ => { }));

        Assert.False(settings.FollowSolutionDirectory);
        Assert.Equal("current", settings.CurrentThreadId);
    }

    [Fact]
    public void SaveFailureKeepsThePreviousSelection()
    {
        using var temp = new TemporaryDirectory();
        var settings = new CodexExtensionSettings { WorkingDirectory = @"C:\solution", CurrentThreadId = "current" };

        Assert.Throws<IOException>(() => CodexWorkingDirectory.Select(settings, temp.Path, false,
            _ => throw new IOException("Settings are unavailable.")));

        Assert.Equal(@"C:\solution", settings.WorkingDirectory);
        Assert.True(settings.FollowSolutionDirectory);
        Assert.Equal("current", settings.CurrentThreadId);
    }

    [Fact]
    public void UnavailableSelectionDoesNotPersistOrReplaceCurrentDirectory()
    {
        using var temp = new TemporaryDirectory();
        var settings = new CodexExtensionSettings { WorkingDirectory = temp.Path };
        var saved = false;

        Assert.Throws<DirectoryNotFoundException>(() => CodexWorkingDirectory.Select(
            settings, Path.Combine(temp.Path, "missing"), false, _ => saved = true));

        Assert.False(saved);
        Assert.Equal(temp.Path, settings.WorkingDirectory);
        Assert.True(settings.FollowSolutionDirectory);
    }

    [Fact]
    public void DisconnectedPinnedFolderIsNotSilentlyReplacedBySolutionOrHome()
    {
        using var temp = new TemporaryDirectory();
        var missing = Path.Combine(temp.Path, "disconnected");
        var settings = new CodexExtensionSettings { WorkingDirectory = missing, FollowSolutionDirectory = false };

        Assert.Equal(missing, CodexWorkingDirectory.Resolve(settings, temp.Path));
        Assert.Equal(missing, CodexWorkingDirectory.Resolve(settings.WorkingDirectory));
    }

    [Theory]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"D:\工程 空格\100% #\", @"D:\工程 空格\100% #")]
    [InlineData(@"\\server\share\project\", @"\\server\share\project")]
    public void WindowsPathsKeepLiteralCharactersAndVolumeRoots(string input, string expected)
    {
        Assert.Equal(expected, CodexWorkingDirectory.Resolve(input));
    }

}
