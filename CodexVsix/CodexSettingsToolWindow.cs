using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexVsix.Services;
using CodexVsix.ViewModels;
using Microsoft.VisualStudio.Shell;

namespace CodexVsix;

[Guid(GuidList.SettingsToolWindowPersistenceString)]
public sealed class CodexSettingsToolWindow : ToolWindowPane
{
    private CodexToolWindowViewModel? _viewModel;

    public CodexSettingsToolWindow() : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = "VSAI - " + new LocalizationService().CodexSettingsNav;

        try
        {
            _viewModel = CodexViewModelHost.GetOrCreate();
            _viewModel.EnsureExternalSettingsSection("codex");
            Content = new CodexSettingsToolWindowControl();
        }
        catch (Exception ex)
        {
            ActivityLog.TryLogError("CodexVsix", new LocalizationService().SettingsToolWindowInitializeLogMessage + Environment.NewLine + ex);
            Content = CreateErrorView(ex);
        }
    }

    internal void ShowSection(string section)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        (_viewModel ?? CodexViewModelHost.GetOrCreate()).EnsureExternalSettingsSection(section);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Content = null;
            _viewModel = null;
        }

        base.Dispose(disposing);
    }

    private static FrameworkElement CreateErrorView(Exception ex)
    {
        var localization = new LocalizationService();
        return new Border
        {
            Padding = new Thickness(16),
            Background = Brushes.Transparent,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = localization.SettingsToolWindowErrorMessage
                        + Environment.NewLine
                        + Environment.NewLine
                        + ex.Message,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }
}
