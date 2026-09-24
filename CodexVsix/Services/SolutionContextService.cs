using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace CodexVsix.Services;

public sealed class SolutionContextService
{
    private const int MaxIndexedFiles = 100000;
    private static readonly TimeSpan FileIndexLifetime = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "TestResults"
    };

    private readonly object _fileIndexSync = new();
    private string _fileIndexRoot = string.Empty;
    private DateTime _fileIndexCreatedUtc;
    private IReadOnlyList<string> _fileIndex = Array.Empty<string>();
    private Task<IReadOnlyList<string>>? _fileIndexBuildTask;
    private string? _lastActiveDocumentPath;

    public string? TryGetBestWorkspaceDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetSolutionDirectory()
            ?? TryGetActiveProjectDirectory()
            ?? TryGetFirstProjectDirectory()
            ?? TryGetActiveDocumentDirectory();
    }

    public string? TryGetSolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var solutionPath = dte?.Solution?.FullName;
        if (!string.IsNullOrWhiteSpace(solutionPath) && File.Exists(solutionPath))
        {
            return Path.GetDirectoryName(solutionPath);
        }

        return null;
    }

    public string GetBestWorkingDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetBestWorkspaceDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string BuildIdeContextSummary(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var sections = new List<string>();
        var solutionDirectory = TryGetSolutionDirectory();
        if (!string.IsNullOrWhiteSpace(solutionDirectory))
        {
            sections.Add("## Solution: " + FormatPath(workingDirectory, solutionDirectory));
        }

        var activeDocument = TryGetActiveDocumentPath();
        if (!string.IsNullOrWhiteSpace(activeDocument))
        {
            sections.Add("## Active file: " + FormatPath(workingDirectory, activeDocument));
        }

        var selectedItems = GetSelectedPaths(workingDirectory);
        if (selectedItems.Count > 0)
        {
            sections.Add("## Selected items:" + Environment.NewLine
                + string.Join(Environment.NewLine, selectedItems.Take(5).Select(path => "- " + path)));
        }

        var openDocuments = GetOpenDocumentPaths(workingDirectory);
        if (openDocuments.Count > 0)
        {
            sections.Add("## Open tabs:" + Environment.NewLine
                + string.Join(Environment.NewLine, openDocuments.Take(6).Select(path => "- " + path)));
        }

        var selectionSnippet = TryGetActiveSelectionSnippet();
        if (!string.IsNullOrWhiteSpace(selectionSnippet))
        {
            sections.Add("## Active selection of the file:" + Environment.NewLine + selectionSnippet);
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            sections.Where(section => !string.IsNullOrWhiteSpace(section)));
    }

    public string? GetActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetActiveDocumentPath();
    }

    public string GetActiveSelectionSnippetForPrompt(int maxLength = 6000)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return TryGetActiveSelectionSnippet(maxLength);
    }

    internal ActiveEditorSelection? GetActiveSelectionForAttachment()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var text = GetActiveSelectionSnippetForPrompt();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var path = GetActiveDocumentPath();
        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            var document = dte?.ActiveDocument;
            if (!string.IsNullOrWhiteSpace(path)
                && string.Equals(document?.FullName, path, StringComparison.OrdinalIgnoreCase)
                && document?.Selection is TextSelection selection
                && string.Equals(NormalizeSelectionSnippet(selection.Text, 6000), text, StringComparison.Ordinal))
            {
                var start = selection.TopPoint;
                var end = selection.BottomPoint;
                return new ActiveEditorSelection(text, path,
                    start.Line - 1, start.LineCharOffset - 1,
                    end.Line - 1, end.LineCharOffset - 1);
            }
        }
        catch
        {
            // Some editor implementations expose text but not DTE selection points.
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(path)
                && string.Equals(TryGetActiveTextViewDocumentPath(), path, StringComparison.OrdinalIgnoreCase)
                && TryGetActiveTextView(out var textView)
                && ErrorHandler.Succeeded(textView!.GetSelection(
                    out var startLine, out var startColumn, out var endLine, out var endColumn))
                && (startLine != endLine || startColumn != endColumn))
            {
                NormalizeSelectionRange(ref startLine, ref startColumn, ref endLine, ref endColumn);
                return new ActiveEditorSelection(text, path,
                    startLine, startColumn, endLine, endColumn);
            }
        }
        catch
        {
            // Keep the selected text even when an editor cannot provide a range.
        }

        return new ActiveEditorSelection(text, path);
    }

    internal sealed class ActiveEditorSelection
    {
        internal ActiveEditorSelection(string text, string? path,
            int? startLine = null, int? startColumn = null, int? endLine = null, int? endColumn = null)
        {
            Text = text;
            Path = path;
            StartLine = startLine;
            StartColumn = startColumn;
            EndLine = endLine;
            EndColumn = endColumn;
        }

        public string Text { get; }
        public string? Path { get; }
        public int? StartLine { get; }
        public int? StartColumn { get; }
        public int? EndLine { get; }
        public int? EndColumn { get; }
    }

    public IReadOnlyList<string> GetOpenDocumentPathsForIdeContext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetOpenDocumentPathsRaw();
    }

    public string FormatPathForPrompt(string root, string path)
    {
        return FormatPath(root, path);
    }

    public IReadOnlyList<string> GetSelectedItemPathsForPrompt(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetSelectedPaths(workingDirectory);
    }

    public async Task<IReadOnlyList<string>> FindSolutionFilesAsync(string workingDirectory, string search, CancellationToken cancellationToken)
    {
        var root = NormalizeFullPath(workingDirectory);
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        var normalized = (search ?? string.Empty).Trim().Replace('\\', '/');
        var files = await GetFileIndexAsync(root, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return files
            .Where(f => string.IsNullOrWhiteSpace(normalized) || f.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(f => Score(f, normalized))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
    }

    public void InvalidateFileIndex()
    {
        lock (_fileIndexSync)
        {
            _fileIndexRoot = string.Empty;
            _fileIndexCreatedUtc = DateTime.MinValue;
            _fileIndex = Array.Empty<string>();
            _fileIndexBuildTask = null;
        }
    }

    public IReadOnlyList<string> GetSolutionFilePaths()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        if (dte?.Solution?.Projects is null)
        {
            return Array.Empty<string>();
        }

        var files = new List<string>();
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Project project in dte.Solution.Projects)
            {
                CollectProjectFiles(project, files, visitedProjects);
            }
        }
        catch
        {
        }

        return files
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetCodexConfigPath(string? environmentVariables = null)
    {
        return Path.Combine(GetCodexHomeDirectory(environmentVariables), "config.toml");
    }

    public string GetCodexHomeDirectory(string? environmentVariables = null)
    {
        return CodexEnvironmentPathHelper.GetCodexHomeDirectory(environmentVariables);
    }

    public string GetCodexSkillsDirectory(string? environmentVariables = null)
    {
        return Path.Combine(GetCodexHomeDirectory(environmentVariables), "skills");
    }

    public void OpenCodexConfig(string? environmentVariables = null)
    {
        var path = GetCodexConfigPath(environmentVariables);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    public void OpenCodexSkillsDirectory(string? environmentVariables = null)
    {
        var path = GetCodexSkillsDirectory(environmentVariables);
        Directory.CreateDirectory(path);
        OpenPath(path);
    }

    public void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    public bool OpenFileInVisualStudio(string path, int? line = null, int? column = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !TryOpenDocumentInVisualStudio(path))
        {
            return false;
        }

        return !line.HasValue || NavigateActiveDocument(path, line.Value, column);
    }

    internal static bool TryParseFileReference(string? reference, out FileNavigationTarget target)
    {
        target = default;
        var path = (reference ?? string.Empty).Trim().Trim('`', '\'', '"', '<', '>').TrimEnd('.', ',', ';');
        if (path.Length == 0)
        {
            return false;
        }

        string fragment = string.Empty;
        // Only explicit file URIs are decoded. Ordinary Windows paths can contain literal % and #.
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var fileUri) || !fileUri.IsFile)
            {
                return false;
            }

            fragment = fileUri.Fragment;
            path = fileUri.LocalPath;
        }
        else
        {
            var fragmentMatch = Regex.Match(path, @"#L?\d+(?:(?:C|:)\d+)?(?:-L?\d+)?$", RegexOptions.IgnoreCase);
            if (fragmentMatch.Success)
            {
                fragment = fragmentMatch.Value;
                path = path.Substring(0, fragmentMatch.Index);
            }
        }

        int? line = null;
        int? column = null;
        var position = Regex.Match(path, @"^(?<path>.+?):(?<line>\d+)(?::(?<column>\d+))?$");
        if (position.Success)
        {
            if (!TryReadFilePosition(position, out line, out column))
            {
                return false;
            }

            path = position.Groups["path"].Value;
        }

        if (fragment.Length > 0)
        {
            position = Regex.Match(fragment, @"^#L?(?<line>\d+)(?:(?:C|:)(?<column>\d+))?(?:-L?\d+)?$", RegexOptions.IgnoreCase);
            if (!position.Success || !TryReadFilePosition(position, out line, out column))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile))
        {
            return false;
        }

        target = new FileNavigationTarget(path, line, column);
        return true;
    }

    private static bool TryReadFilePosition(Match match, out int? line, out int? column)
    {
        line = null;
        column = null;
        if (!int.TryParse(match.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLine)
            || parsedLine < 1)
        {
            return false;
        }

        line = parsedLine;
        if (match.Groups["column"].Success)
        {
            if (!int.TryParse(match.Groups["column"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedColumn)
                || parsedColumn < 1)
            {
                return false;
            }

            column = parsedColumn;
        }

        return true;
    }

    internal static bool TryResolveFileReference(string? reference, string? workingDirectory, out FileNavigationTarget target)
    {
        target = default;
        if (!TryParseFileReference(reference, out var parsed))
        {
            return false;
        }

        try
        {
            var candidates = new List<string> { parsed.Path };
            // Preserve literal percent characters first, then accept once-escaped Markdown paths.
            // LocalPath has already decoded explicit file URIs and must not be decoded again.
            if (!(reference ?? string.Empty).Trim().Trim('`', '\'', '"', '<', '>').StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = Uri.UnescapeDataString(parsed.Path);
                if (!string.Equals(decoded, parsed.Path, StringComparison.Ordinal))
                {
                    candidates.Add(decoded);
                }
            }

            foreach (var candidate in candidates)
            {
                var path = candidate;
                if (!Path.IsPathRooted(path))
                {
                    if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathRooted(workingDirectory))
                    {
                        return false;
                    }

                    path = Path.Combine(workingDirectory, path);
                }

                // Require a drive root or UNC root instead of depending on devenv's current drive.
                if (!Regex.IsMatch(path, @"^(?:[A-Za-z]:[\\/]|[\\/]{2})"))
                {
                    return false;
                }

                path = Path.GetFullPath(path);
                if (File.Exists(path))
                {
                    target = new FileNavigationTarget(path, parsed.Line, parsed.Column);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
        {
            return false;
        }
    }

    internal static bool IsPotentialRelativeFileReference(string reference)
    {
        return TryParseFileReference(reference, out var parsed)
            && !Path.IsPathRooted(parsed.Path)
            && Regex.IsMatch(Path.GetExtension(parsed.Path), @"^\.[A-Za-z0-9]{1,12}$");
    }

    internal static string? FindUnambiguousSolutionFile(string reference, string? solutionDirectory, IEnumerable<string> files)
    {
        if (Path.IsPathRooted(reference))
        {
            return null;
        }

        var candidates = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (TryResolveFileReference(reference, solutionDirectory, out var exact)
            && candidates.Contains(exact.Path, StringComparer.OrdinalIgnoreCase))
        {
            return exact.Path;
        }

        var suffix = reference.Replace('\\', '/');
        while (suffix.StartsWith("./", StringComparison.Ordinal))
        {
            suffix = suffix.Substring(2);
        }

        var matches = candidates.Where(path => path.Replace('\\', '/').EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    internal static void ClampNavigationPosition(int line, int? column, int lineCount, Func<int, int> getLineLength,
        out int targetLine, out int? targetColumn)
    {
        targetLine = Math.Max(1, Math.Min(line, Math.Max(1, lineCount)));
        targetColumn = column.HasValue ? Math.Max(1, Math.Min(column.Value, getLineLength(targetLine) + 1)) : (int?)null;
    }

    internal readonly struct FileNavigationTarget
    {
        public FileNavigationTarget(string path, int? line, int? column)
        {
            Path = path;
            Line = line;
            Column = column;
        }

        public string Path { get; }
        public int? Line { get; }
        public int? Column { get; }
    }

    private static bool TryOpenDocumentInVisualStudio(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (TryOpenDocumentWithDte(path))
        {
            return true;
        }

        try
        {
            VsShellUtilities.OpenDocument(Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider, path);
            return true;
        }
        catch
        {
        }

        return false;
    }

    private static bool TryOpenDocumentWithDte(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.ItemOperations is null)
            {
                return false;
            }

            var window = dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            if (window is null)
            {
                return false;
            }

            window.Activate();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool NavigateActiveDocument(string path, int line, int? column)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            var document = dte?.ActiveDocument;
            if (document is null || !string.Equals(document.FullName, path, StringComparison.OrdinalIgnoreCase)
                || document.Selection is not TextSelection selection
                || document.Object("TextDocument") is not TextDocument textDocument)
            {
                return false;
            }

            var point = textDocument.StartPoint.CreateEditPoint();
            ClampNavigationPosition(line, column, textDocument.EndPoint.Line, requestedLine =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                point.MoveToLineAndOffset(requestedLine, 1);
                return point.LineLength;
            }, out var targetLine, out var targetColumn);

            if (targetColumn.HasValue)
            {
                selection.MoveToLineAndOffset(targetLine, targetColumn.Value, false);
            }
            else
            {
                selection.GotoLine(targetLine, false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    public string CreateSkillTemplate(string skillName, string description, string? environmentVariables = null)
    {
        var normalizedSkillName = NormalizeSkillName(skillName);
        var skillDirectory = Path.Combine(GetCodexSkillsDirectory(environmentVariables), normalizedSkillName);
        Directory.CreateDirectory(skillDirectory);

        var skillFile = Path.Combine(skillDirectory, "SKILL.md");
        if (!File.Exists(skillFile))
        {
            File.WriteAllText(
                skillFile,
                BuildSkillTemplate(normalizedSkillName, description),
                new UTF8Encoding(false));
        }

        return skillFile;
    }

    public static bool IsValidSkillName(string? skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return false;
        }

        var trimmed = skillName!.Trim();
        if (!char.IsLetterOrDigit(trimmed[0]))
        {
            return false;
        }

        return trimmed.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-');
    }

    private static string NormalizeSkillName(string skillName)
    {
        var trimmed = (skillName ?? string.Empty).Trim();
        if (!IsValidSkillName(trimmed))
        {
            throw new ArgumentException(new LocalizationService().InvalidSkillNameMessage, nameof(skillName));
        }

        return trimmed;
    }

    private static string BuildSkillTemplate(string skillName, string description)
    {
        var localization = new LocalizationService();
        var summary = string.IsNullOrWhiteSpace(description)
            ? localization.SkillTemplateSummary
            : description.Trim();

        return "# " + skillName + Environment.NewLine
            + Environment.NewLine
            + summary + Environment.NewLine
            + Environment.NewLine
            + localization.SkillTemplateWhenToUseHeading + Environment.NewLine
            + localization.SkillTemplateWhenToUseBullet + Environment.NewLine
            + Environment.NewLine
            + localization.SkillTemplateFlowHeading + Environment.NewLine
            + localization.SkillTemplateFlowStep1 + Environment.NewLine
            + localization.SkillTemplateFlowStep2 + Environment.NewLine
            + localization.SkillTemplateFlowStep3 + Environment.NewLine;
    }

    private async Task<IReadOnlyList<string>> GetFileIndexAsync(string root, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<string>> buildTask;
        lock (_fileIndexSync)
        {
            if (string.Equals(_fileIndexRoot, root, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - _fileIndexCreatedUtc < FileIndexLifetime
                && _fileIndex.Count > 0)
            {
                return _fileIndex;
            }

            if (_fileIndexBuildTask is null || !string.Equals(_fileIndexRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _fileIndexRoot = root;
                _fileIndexBuildTask = Task.Run(() => BuildFileIndex(root));
            }

            buildTask = _fileIndexBuildTask;
        }

        IReadOnlyList<string> result;
        try
        {
            result = await buildTask.ConfigureAwait(false);
        }
        catch
        {
            lock (_fileIndexSync)
            {
                if (ReferenceEquals(_fileIndexBuildTask, buildTask))
                {
                    _fileIndexBuildTask = null;
                }
            }

            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_fileIndexSync)
        {
            if (ReferenceEquals(_fileIndexBuildTask, buildTask)
                && string.Equals(_fileIndexRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _fileIndex = result;
                _fileIndexCreatedUtc = DateTime.UtcNow;
                _fileIndexBuildTask = null;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> BuildFileIndex(string root)
    {
        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0 && files.Count < MaxIndexedFiles)
        {
            var directory = directories.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    files.Add(MakeRelative(root, file));
                    if (files.Count >= MaxIndexedFiles)
                    {
                        break;
                    }
                }

                if (files.Count >= MaxIndexedFiles)
                {
                    break;
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IgnoredDirectoryNames.Contains(Path.GetFileName(child)))
                    {
                        continue;
                    }

                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    directories.Push(child);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                || ex is IOException
                || ex is PathTooLongException
                || ex is DirectoryNotFoundException)
            {
            }
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryGetActiveProjectDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.ActiveSolutionProjects is not Array activeProjects)
            {
                return null;
            }

            foreach (var entry in activeProjects)
            {
                if (entry is Project project)
                {
                    var directory = TryGetProjectDirectory(project);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        return directory;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? TryGetFirstProjectDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            var projects = dte?.Solution?.Projects;
            if (projects is null)
            {
                return null;
            }

            foreach (Project project in projects)
            {
                var directory = TryGetProjectDirectory(project);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private string? TryGetActiveDocumentDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var path = TryGetActiveDocumentPath();
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
    }

    private string? TryGetActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var path = TryGetDteActiveDocumentPath() ?? TryGetActiveTextViewDocumentPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            _lastActiveDocumentPath = path;
            return path;
        }

        if (!string.IsNullOrWhiteSpace(_lastActiveDocumentPath) && File.Exists(_lastActiveDocumentPath))
        {
            return _lastActiveDocumentPath;
        }

        _lastActiveDocumentPath = null;
        return null;
    }

    private static string? TryGetDteActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            return NormalizeExistingDocumentPath(dte?.ActiveDocument?.FullName);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetActiveTextViewDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!TryGetActiveTextView(out var textView)
                || ErrorHandler.Failed(textView!.GetBuffer(out var textLines))
                || textLines is not IPersistFileFormat persistFileFormat
                || ErrorHandler.Failed(persistFileFormat.GetCurFile(out var filePath, out _)))
            {
                return null;
            }

            return NormalizeExistingDocumentPath(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeExistingDocumentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path!);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetActiveTextView(out IVsTextView? textView)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        textView = null;

        var textManager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
        return textManager is not null
            && ErrorHandler.Succeeded(textManager.GetActiveView(0, null, out textView))
            && textView is not null;
    }

    private static string? TryGetProjectDirectory(Project? project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (project is null)
        {
            return null;
        }

        try
        {
            var fullName = project.FullName;
            if (!string.IsNullOrWhiteSpace(fullName))
            {
                if (File.Exists(fullName))
                {
                    return Path.GetDirectoryName(fullName);
                }

                if (Directory.Exists(fullName))
                {
                    return fullName;
                }
            }
        }
        catch
        {
        }

        try
        {
            var fullPath = project.Properties?.Item("FullPath")?.Value as string;
            if (!string.IsNullOrWhiteSpace(fullPath) && Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }
        catch
        {
        }

        try
        {
            if (project.ProjectItems is null)
            {
                return null;
            }

            foreach (ProjectItem item in project.ProjectItems)
            {
                var nested = TryGetProjectDirectory(item.SubProject);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void CollectProjectFiles(Project? project, ICollection<string> files, ISet<string> visitedProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project is null)
        {
            return;
        }

        var projectKey = TryGetProjectKey(project);
        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            var key = projectKey!;
            if (!visitedProjects.Add(key))
            {
                return;
            }
        }

        try
        {
            TryAddFile(files, project.FullName);
        }
        catch
        {
        }

        try
        {
            if (project.ProjectItems is null)
            {
                return;
            }

            foreach (ProjectItem item in project.ProjectItems)
            {
                CollectProjectItemFiles(item, files, visitedProjects);
            }
        }
        catch
        {
        }
    }

    private static void CollectProjectItemFiles(ProjectItem item, ICollection<string> files, ISet<string> visitedProjects)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            for (short index = 1; index <= item.FileCount; index++)
            {
                TryAddFile(files, item.FileNames[index]);
            }
        }
        catch
        {
        }

        try
        {
            CollectProjectFiles(item.SubProject, files, visitedProjects);
        }
        catch
        {
        }

        try
        {
            if (item.ProjectItems is null)
            {
                return;
            }

            foreach (ProjectItem child in item.ProjectItems)
            {
                CollectProjectItemFiles(child, files, visitedProjects);
            }
        }
        catch
        {
        }
    }

    private static string? TryGetProjectKey(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (!string.IsNullOrWhiteSpace(project.UniqueName))
            {
                return project.UniqueName;
            }
        }
        catch
        {
        }

        try
        {
            return project.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static void TryAddFile(ICollection<string> files, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var filePath = path!;
        if (File.Exists(filePath))
        {
            files.Add(filePath);
        }
    }

    private static string MakeRelative(string root, string file)
    {
        var normalizedRoot = NormalizeFullPath(root);
        var normalizedFile = NormalizeFullPath(file);
        var rootWithSeparator = EnsureTrailingDirectorySeparator(normalizedRoot);
        if (!normalizedFile.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedFile.Replace('\\', '/');
        }

        return normalizedFile.Substring(rootWithSeparator.Length).Replace('\\', '/');
    }

    private static string FormatPath(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalizedPath = NormalizeFullPath(path);
        if (!string.IsNullOrWhiteSpace(root))
        {
            var normalizedRoot = NormalizeFullPath(root);
            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return ".";
            }

            if (normalizedPath.StartsWith(EnsureTrailingDirectorySeparator(normalizedRoot), StringComparison.OrdinalIgnoreCase))
            {
                return MakeRelative(normalizedRoot, normalizedPath);
            }
        }

        return normalizedPath.Replace('\\', '/');
    }

    private static string NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var fullPath = Path.GetFullPath(path!.Trim());
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? root!
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path!.Trim();
        }
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }

    private static IReadOnlyList<string> GetOpenDocumentPaths(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetOpenDocumentPathsRaw()
            .Select(path => FormatPath(workingDirectory, path))
            .ToList();
    }

    private static IReadOnlyList<string> GetOpenDocumentPathsRaw()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.Documents is null)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>();
            foreach (Document document in dte.Documents)
            {
                var path = NormalizeExistingDocumentPath(document.FullName);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path!);
                }
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> GetSelectedPaths(string workingDirectory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.SelectedItems is null)
            {
                return Array.Empty<string>();
            }

            var items = new List<string>();
            foreach (SelectedItem selectedItem in dte.SelectedItems)
            {
                var path = selectedItem.ProjectItem?.FileNames[1]
                    ?? selectedItem.Project?.FullName;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                items.Add(FormatPath(workingDirectory, path));
            }

            return items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string TryGetActiveSelectionSnippet(int maxLength = 900)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            var textSelection = dte?.ActiveDocument?.Selection as TextSelection;
            var selectedText = textSelection?.Text;
            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                return NormalizeSelectionSnippet(selectedText!, maxLength);
            }
        }
        catch
        {
        }

        try
        {
            if (!TryGetActiveTextView(out var textView)
                || ErrorHandler.Failed(textView!.GetSelection(
                    out var startLine,
                    out var startIndex,
                    out var endLine,
                    out var endIndex))
                || (startLine == endLine && startIndex == endIndex)
                || ErrorHandler.Failed(textView.GetBuffer(out var textLines)))
            {
                return string.Empty;
            }

            NormalizeSelectionRange(ref startLine, ref startIndex, ref endLine, ref endIndex);
            return ErrorHandler.Succeeded(textLines.GetLineText(
                startLine,
                startIndex,
                endLine,
                endIndex,
                out var selectedText))
                ? NormalizeSelectionSnippet(selectedText, maxLength)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void NormalizeSelectionRange(
        ref int startLine,
        ref int startIndex,
        ref int endLine,
        ref int endIndex)
    {
        if (startLine < endLine || (startLine == endLine && startIndex <= endIndex))
        {
            return;
        }

        (startLine, endLine) = (endLine, startLine);
        (startIndex, endIndex) = (endIndex, startIndex);
    }

    private static string NormalizeSelectionSnippet(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || maxLength <= 0)
        {
            return string.Empty;
        }

        var normalized = text!.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized.Substring(0, maxLength).TrimEnd() + Environment.NewLine + "...";
    }

    private static int Score(string file, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return int.MaxValue / 2;
        }

        var idx = file.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? int.MaxValue : idx * 10 + file.Length;
    }
}
