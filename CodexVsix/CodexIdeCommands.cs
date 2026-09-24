using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexVsix.Services;
using CodexVsix.UI;
using CodexVsix.ViewModels;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace CodexVsix;

internal sealed class CodexIdeCommands
{
    private readonly AsyncPackage _package;
    private readonly SolutionContextService _solutionContextService = new();

    private CodexIdeCommands(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        AddCommand(commandService, PackageIds.OpenSidebarCommand, ExecuteOpenSidebarAsync);
        AddCommand(commandService, PackageIds.NewCodexAgentCommand, ExecuteNewAgentAsync);
        AddCommand(commandService, PackageIds.AddSelectionToThreadCommand, ExecuteAddSelectionAsync);
        AddCommand(commandService, PackageIds.AddFileToThreadCommand, ExecuteAddFileAsync);
        AddCommand(commandService, PackageIds.OpenCodexSettingsCommand, ExecuteOpenSettingsAsync);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (commandService is not null)
        {
            _ = new CodexIdeCommands(package, commandService);
        }
    }

    private static void AddCommand(OleMenuCommandService commandService, int commandId, Func<Task> executeAsync)
    {
        var menuCommand = new MenuCommand(
            (_, _) => executeAsync().FileAndForget("CodexVsix/IdeCommand"),
            new CommandID(new Guid(GuidList.CommandSetString), commandId));
        commandService.AddCommand(menuCommand);
    }

    private async Task ExecuteOpenSidebarAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
        var viewModel = await ShowCodexAsync();
        if (!CodexOfficialWebViewHostRegistry.TryFocusComposer())
        {
            viewModel.RequestComposerFocus();
        }
    }

    private async Task ExecuteNewAgentAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
        var viewModel = await ShowCodexAsync();
        if (!CodexOfficialWebViewHostRegistry.TryStartNewConversation())
        {
            viewModel.StartNewAgentFromIdeCommand();
        }
    }

    private async Task ExecuteAddSelectionAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
        var selection = _solutionContextService.GetActiveSelectionForAttachment();
        var viewModel = await ShowCodexAsync();
        if (selection is null)
        {
            ShowMessage(viewModel.Localization.SelectCodeOrOpenFileMessage);
            viewModel.RequestComposerFocus();
            return;
        }

        var attachment = new JObject
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["text"] = selection.Text
        };
        if (!string.IsNullOrWhiteSpace(selection.Path)
            && selection.StartLine.HasValue && selection.StartColumn.HasValue
            && selection.EndLine.HasValue && selection.EndColumn.HasValue)
        {
            attachment["source"] = new JObject
            {
                ["path"] = selection.Path,
                ["range"] = new JObject
                {
                    ["start"] = new JObject { ["line"] = selection.StartLine.Value, ["character"] = selection.StartColumn.Value },
                    ["end"] = new JObject { ["line"] = selection.EndLine.Value, ["character"] = selection.EndColumn.Value }
                }
            };
        }
        else if (!string.IsNullOrWhiteSpace(selection.Path))
        {
            var relativePath = _solutionContextService.FormatPathForPrompt(
                viewModel.Settings.WorkingDirectory, selection.Path!);
            attachment["text"] = "File: " + FormatFileMention(relativePath)
                + Environment.NewLine + Environment.NewLine + selection.Text;
        }

        if (!CodexOfficialWebViewHostRegistry.TryAddSelectionAttachment(attachment))
        {
            viewModel.AppendComposerContextFromIdeCommand(BuildSelectionContext(viewModel, selection));
        }
    }

    private async Task ExecuteAddFileAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
        var viewModel = await ShowCodexAsync();
        var context = BuildFileContext(viewModel);
        if (string.IsNullOrWhiteSpace(context))
        {
            ShowMessage(viewModel.Localization.SelectFileOrOpenFileMessage);
            viewModel.RequestComposerFocus();
            return;
        }

        if (!CodexOfficialWebViewHostRegistry.TryPrefillComposer(context))
        {
            viewModel.AppendComposerContextFromIdeCommand(context);
        }
    }

    private async Task ExecuteOpenSettingsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
        CodexToolWindowManager.ShowSettingsToolWindow("agent");
    }

    private async Task<CodexToolWindowViewModel> ShowCodexAsync()
    {
        await CodexToolWindowManager.ShowMainToolWindowAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(_package.DisposalToken);
        return CodexViewModelHost.GetOrCreate();
    }

    private string BuildSelectionContext(
        CodexToolWindowViewModel viewModel,
        SolutionContextService.ActiveEditorSelection selection)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var workingDirectory = viewModel.Settings.WorkingDirectory;
        var relativePath = string.IsNullOrWhiteSpace(selection.Path)
            ? viewModel.Localization.ActiveEditorLabel
            : _solutionContextService.FormatPathForPrompt(workingDirectory, selection.Path!);

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("File: " + FormatFileMention(relativePath));
        builder.AppendLine();
        builder.AppendLine(BuildFencedCode(relativePath, selection.Text));
        return builder.ToString().Trim();
    }

    private string BuildFileContext(CodexToolWindowViewModel viewModel)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var workingDirectory = viewModel.Settings.WorkingDirectory;
        var selectedItem = _solutionContextService.GetSelectedItemPathsForPrompt(workingDirectory).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(selectedItem))
        {
            return FormatFileMention(selectedItem!);
        }

        var activeDocument = _solutionContextService.GetActiveDocumentPath();
        if (string.IsNullOrWhiteSpace(activeDocument))
        {
            return string.Empty;
        }

        return FormatFileMention(_solutionContextService.FormatPathForPrompt(workingDirectory, activeDocument!));
    }

    private static string BuildFencedCode(string path, string content)
    {
        var language = GetFenceLanguage(path);
        var normalizedContent = (content ?? string.Empty).Trim();
        var longestBacktickRun = 0;
        var currentRun = 0;
        foreach (var character in normalizedContent)
        {
            currentRun = character == '`' ? currentRun + 1 : 0;
            longestBacktickRun = Math.Max(longestBacktickRun, currentRun);
        }

        var fence = new string('`', Math.Max(3, longestBacktickRun + 1));
        return fence + language + Environment.NewLine + normalizedContent + Environment.NewLine + fence;
    }

    private static string FormatFileMention(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/');
        return normalized.Any(char.IsWhiteSpace)
            ? "@\"" + normalized.Replace("\"", "\\\"") + "\""
            : "@" + normalized;
    }

    private static string GetFenceLanguage(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "cs" => "csharp",
            "csproj" => "xml",
            "vb" => "vbnet",
            "js" => "javascript",
            "ts" => "typescript",
            "tsx" => "tsx",
            "jsx" => "jsx",
            "xaml" => "xml",
            "props" => "xml",
            "targets" => "xml",
            "json" => "json",
            "md" => "markdown",
            "ps1" => "powershell",
            "cmd" => "batch",
            "bat" => "batch",
            _ => extension
        };
    }

    private void ShowMessage(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(
            _package,
            message,
            "Codex",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
