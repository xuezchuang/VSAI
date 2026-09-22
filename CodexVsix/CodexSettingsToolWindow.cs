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

[Guid(GuidList.SettingsToolWindowPersistenceString)]
public sealed class CodexSettingsToolWindow : ToolWindowPane
{
    private CodexRendererCoordinator? _rendererCoordinator;
    private CodexToolWindowViewModel? _viewModel;
    private CodexOfficialWebViewHost? _webViewHost;
    private CodexSettingsToolWindowControl? _classicControl;

    public CodexSettingsToolWindow() : base(null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Caption = "VSAI - " + new LocalizationService().CodexSettingsNav;

        try
        {
            _viewModel = CodexViewModelHost.GetOrCreate();
        }
        catch (Exception ex)
        {
            ActivityLog.TryLogError("CodexVsix", new LocalizationService().SettingsToolWindowInitializeLogMessage + Environment.NewLine + ex);
            Content = CreateErrorView(ex);
            return;
        }

        _rendererCoordinator = CodexRendererCoordinator.Shared;
        _rendererCoordinator.RendererSwitching += OnRendererSwitching;
        _rendererCoordinator.RendererChanged += OnRendererChanged;
        ShowRenderer(_rendererCoordinator.CurrentRenderer);
    }

    internal void ShowSection(string section)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_webViewHost is not null)
        {
            _webViewHost.ShowSettings(section);
            return;
        }

        (_viewModel ?? CodexViewModelHost.GetOrCreate()).EnsureExternalSettingsSection(section);
    }

    private void OnWebViewReady(object? sender, CodexOfficialWebViewReadyEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // Settings is an independent surface. Its success or failure must not
        // change the renderer selected for the primary chat window.
    }

    private void OnWebViewFallbackRequested(object? sender, CodexOfficialWebViewFallbackEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ShowLocalClassicFallback(e.FailureKind, e.Reason);
    }

    private void OnRendererSwitching(object? sender, CodexRendererTransitionEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        DisposeActiveRenderer();
    }

    private void OnRendererChanged(object? sender, CodexRendererTransitionEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ShowRenderer(e.NextRenderer);
    }

    private void ShowRenderer(CodexRendererKind renderer)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            if (renderer == CodexRendererKind.ClassicWpf)
            {
                _classicControl = new CodexSettingsToolWindowControl(RetryModernInterface);
                Content = _classicControl;
                return;
            }

            _webViewHost = new CodexOfficialWebViewHost(
                _viewModel,
                initialRoute: "/settings",
                isSettingsSurface: true,
                registerAsPrimaryHost: false);
            _webViewHost.Ready += OnWebViewReady;
            _webViewHost.FallbackRequested += OnWebViewFallbackRequested;
            Content = _webViewHost;
        }
        catch (Exception ex)
        {
            ActivityLog.TryLogError("CodexVsix", new LocalizationService().SettingsToolWindowInitializeLogMessage + Environment.NewLine + ex);
            if (renderer == CodexRendererKind.OfficialWebView)
            {
                ShowLocalClassicFallback("construction", ex.Message);
                return;
            }

            Content = CreateErrorView(ex);
        }
    }

    private void ShowLocalClassicFallback(string failureKind, string reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        CodexDiagnosticLogger.Shared.Write(
            "renderer.settings.local-fallback",
            new Newtonsoft.Json.Linq.JObject
            {
                ["failureKind"] = failureKind ?? string.Empty,
                ["reason"] = reason ?? string.Empty,
                ["mainRenderer"] = _rendererCoordinator is null
                    ? string.Empty
                    : CodexRendererCompatibilityStore.FormatRenderer(_rendererCoordinator.CurrentRenderer)
            });
        DisposeActiveRenderer();
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.EnsureExternalSettingsSection("codex");
        _classicControl = new CodexSettingsToolWindowControl(RetryModernInterface);
        Content = _classicControl;
    }

    private void RetryModernInterface()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_rendererCoordinator?.CurrentRenderer == CodexRendererKind.ClassicWpf)
        {
            _rendererCoordinator.RetryOfficialRenderer("manual-settings");
            return;
        }

        CodexDiagnosticLogger.Shared.Write(
            "renderer.settings.manual-retry",
            new Newtonsoft.Json.Linq.JObject());
        DisposeActiveRenderer();
        ShowRenderer(CodexRendererKind.OfficialWebView);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_rendererCoordinator is not null)
            {
                _rendererCoordinator.RendererSwitching -= OnRendererSwitching;
                _rendererCoordinator.RendererChanged -= OnRendererChanged;
                _rendererCoordinator = null;
            }

            DisposeActiveRenderer();
            _viewModel = null;
        }

        base.Dispose(disposing);
    }

    private void DisposeActiveRenderer()
    {
        if (_webViewHost is not null)
        {
            _webViewHost.Ready -= OnWebViewReady;
            _webViewHost.FallbackRequested -= OnWebViewFallbackRequested;
            _webViewHost.Dispose();
            _webViewHost = null;
        }

        _classicControl = null;
        Content = null;
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
