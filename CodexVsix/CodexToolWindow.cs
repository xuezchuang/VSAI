using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexVsix.Services;
using CodexVsix.UI;
using CodexVsix.ViewModels;
using Microsoft.VisualStudio.Shell;

namespace CodexVsix;

[Guid(GuidList.ToolWindowPersistanceString)]
public sealed class CodexToolWindow : ToolWindowPane
{
    private CodexToolWindowViewModel? _viewModel;
    private CodexToolWindowControl? _classicControl;

    public CodexToolWindow() : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = "VSAI (WPF)";

        try
        {
            _viewModel = CodexViewModelHost.GetOrCreate();
            _classicControl = new CodexToolWindowControl();
            Content = WithWorkspaceSelector(_classicControl);
        }
        catch (Exception ex)
        {
            ActivityLog.TryLogError("CodexVsix", new LocalizationService().ToolWindowInitializeLogMessage + Environment.NewLine + ex);
            Content = CreateErrorView(ex);
        }
    }

    private FrameworkElement WithWorkspaceSelector(FrameworkElement renderer)
    {
        var panel = new DockPanel();
        var selector = new CodexWorkspaceSelector { DataContext = _viewModel };
        DockPanel.SetDock(selector, Dock.Bottom);
        panel.Children.Add(selector);
        panel.Children.Add(renderer);
        return panel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _classicControl?.Dispose();
            _classicControl = null;
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
                    Text = localization.ToolWindowErrorMessage
                        + Environment.NewLine
                        + Environment.NewLine
                        + ex.Message,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }
}
