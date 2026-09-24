using System;
using System.IO;
using CodexVsix.Models;

namespace CodexVsix.Services;

internal static class CodexWorkingDirectory
{
    public static string Resolve(CodexExtensionSettings settings, string? solutionDirectory = null)
    {
        var directory = settings.FollowSolutionDirectory && !string.IsNullOrWhiteSpace(solutionDirectory)
            ? solutionDirectory!
            : settings.WorkingDirectory;

        return Resolve(directory);
    }

    public static string ResolveForSolution(CodexExtensionSettings settings, string solutionPath, string solutionDirectory)
    {
        var key = Normalize(solutionPath);
        return settings.SolutionWorkingDirectories is not null
            && settings.SolutionWorkingDirectories.TryGetValue(key, out var selectedDirectory)
            && !string.IsNullOrWhiteSpace(selectedDirectory)
                ? Resolve(selectedDirectory)
                : Resolve(solutionDirectory);
    }

    public static string Resolve(string? directory)
    {
        // Keep an explicit choice even if a removable drive is temporarily unavailable.
        // A new thread must report that directory's error instead of running in another folder.
        return Normalize(string.IsNullOrWhiteSpace(directory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : directory!);
    }

    public static bool Select(
        CodexExtensionSettings settings,
        string directory,
        bool followSolutionDirectory,
        Action<CodexExtensionSettings> save)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Choose a working folder.", nameof(directory));
        }

        var selectedDirectory = Normalize(directory);
        if (!Directory.Exists(selectedDirectory))
        {
            throw new DirectoryNotFoundException("The working folder is unavailable: " + selectedDirectory);
        }

        var previousDirectory = settings.WorkingDirectory;
        var previousFollowSolution = settings.FollowSolutionDirectory;
        var changed = !PathsEqual(settings.WorkingDirectory, selectedDirectory);
        settings.WorkingDirectory = selectedDirectory;
        settings.FollowSolutionDirectory = followSolutionDirectory;
        try
        {
            save(settings);
        }
        catch
        {
            settings.WorkingDirectory = previousDirectory;
            settings.FollowSolutionDirectory = previousFollowSolution;
            throw;
        }

        return changed;
    }

    private static string Normalize(string directory)
    {
        var path = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.Length > root.Length
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
    }

    private static bool PathsEqual(string? first, string second)
    {
        try
        {
            return string.Equals(Resolve(first), second, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }
}
