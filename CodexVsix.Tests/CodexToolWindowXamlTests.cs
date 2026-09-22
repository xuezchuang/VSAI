using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexToolWindowXamlTests
{
    private static readonly XNamespace PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void ReadOnlyTextBoxesUseOneWayTextBindings()
    {
        var document = XDocument.Load(FindRepositoryFile("CodexVsix", "CodexToolWindowControl.xaml"));
        var readOnlyBoundTextBoxes = document
            .Descendants(PresentationNamespace + "TextBox")
            .Where(IsReadOnlyTextBox)
            .Where(element => IsBinding(element.Attribute("Text")?.Value))
            .ToList();

        Assert.NotEmpty(readOnlyBoundTextBoxes);

        foreach (var textBox in readOnlyBoundTextBoxes)
        {
            var binding = textBox.Attribute("Text")!.Value;
            var name = textBox.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "Name")
                ?.Value ?? "unnamed TextBox";

            Assert.True(
                binding.IndexOf("Mode=OneWay", StringComparison.OrdinalIgnoreCase) >= 0,
                $"The read-only {name} TextBox must use a OneWay Text binding, but found: {binding}");
        }
    }

    [Fact]
    public void ChatDisplayDetailBindingIsExplicitlyOneWay()
    {
        var document = XDocument.Load(FindRepositoryFile("CodexVsix", "CodexToolWindowControl.xaml"));
        var eventDetailText = document
            .Descendants(PresentationNamespace + "TextBox")
            .Single(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && string.Equals(attribute.Value, "EventDetailText", StringComparison.Ordinal)));

        Assert.Equal("{Binding DisplayDetail, Mode=OneWay}", eventDetailText.Attribute("Text")?.Value);
    }

    [Fact]
    public void SettingsOpenAsWpfInTheDocumentWellAndPreserveRequestedSections()
    {
        var managerSource = File.ReadAllText(FindRepositoryFile("CodexVsix", "CodexToolWindowManager.cs"));
        var settingsWindowSource = File.ReadAllText(FindRepositoryFile("CodexVsix", "CodexSettingsToolWindow.cs"));

        Assert.Contains("VSFM_MdiChild", managerSource);
        Assert.Contains("VSFPROPID_FrameMode", managerSource);
        Assert.Contains("Content = new CodexSettingsToolWindowControl();", settingsWindowSource);
        Assert.Contains("EnsureExternalSettingsSection(\"codex\")", settingsWindowSource);
        Assert.Contains("EnsureExternalSettingsSection(section)", settingsWindowSource);
        Assert.Contains("[Guid(GuidList.SettingsToolWindowPersistenceString)]", settingsWindowSource);
        Assert.DoesNotContain("CodexOfficialWebViewHost", settingsWindowSource);
        Assert.DoesNotContain("CodexRendererCoordinator", settingsWindowSource);
    }

    [Fact]
    public void MainWindowUsesWpfDirectlyWithTheWorkspaceSelector()
    {
        var toolWindowSource = File.ReadAllText(FindRepositoryFile("CodexVsix", "CodexToolWindow.cs"));
        var classicControlSource = File.ReadAllText(FindRepositoryFile("CodexVsix", "CodexToolWindowControl.xaml.cs"));

        Assert.Contains("_classicControl = new CodexToolWindowControl();", toolWindowSource);
        Assert.Contains("Content = WithWorkspaceSelector(_classicControl);", toolWindowSource);
        Assert.Contains("new CodexWorkspaceSelector { DataContext = _viewModel }", toolWindowSource);
        Assert.Contains("[Guid(GuidList.ToolWindowPersistanceString)]", toolWindowSource);
        Assert.Contains("_classicControl?.Dispose();", toolWindowSource);
        Assert.DoesNotContain("CodexRendererCoordinator", toolWindowSource);
        Assert.DoesNotContain("CodexOfficialWebViewHost", toolWindowSource);
        Assert.DoesNotContain("CodexOfficialWebViewHost", classicControlSource);
    }

    [Fact]
    public void MainWindowAutoOpenWaitsForShellIdle()
    {
        var packageSource = File.ReadAllText(FindRepositoryFile("CodexVsix", "CodexPackage.cs"));
        var initializeSection = packageSource.Split(
            new[] { "private async Task OpenMainToolWindowWhenShellIsIdleAsync" },
            StringSplitOptions.None)[0];

        Assert.Contains("OpenMainToolWindowWhenShellIsIdleAsync(DisposalToken)", initializeSection);
        Assert.DoesNotContain("ShowMainToolWindowAsync()", initializeSection);
        Assert.Contains("DispatcherPriority.ApplicationIdle", packageSource);
        Assert.Contains("new ExtensionSettingsStore().Load().OpenOnStartup", packageSource);
    }

    [Fact]
    public void ReleaseWebViewDisablesDeveloperTools()
    {
        var hostSource = File.ReadAllText(FindRepositoryFile(
            "CodexVsix",
            "UI",
            "CodexOfficialWebViewHost.cs"));

        Assert.Contains("#if DEBUG", hostSource);
        Assert.Contains("core.Settings.AreDevToolsEnabled = true;", hostSource);
        Assert.Contains("#else", hostSource);
        Assert.Contains("core.Settings.AreDevToolsEnabled = false;", hostSource);
    }

    [Fact]
    public void OfficialWebViewExhaustsModernRecoveryBeforeRequestingClassicFallback()
    {
        var hostSource = File.ReadAllText(FindRepositoryFile(
            "CodexVsix",
            "UI",
            "CodexOfficialWebViewHost.cs"));

        Assert.Contains("RequestFallback(failureKind ?? \"official-failure\", exception.Message);", hostSource);
        Assert.Contains("new CodexOfficialWebViewFallbackEventArgs", hostSource);
        Assert.Contains("_recoveryPlan.TryAdvance(out var nextAttempt)", hostSource);
        Assert.Contains("\"webview.recovery.attempt\"", hostSource);
        Assert.Contains("\"webview.recovery.exhausted\"", hostSource);
        Assert.Contains("\"ready-timeout\"", hostSource);
        Assert.Contains("StartReadySignalTimeout(_initializationGeneration, _webViewControl);", hostSource);
        Assert.DoesNotContain("_statusLayer.MouseLeftButtonUp +=", hostSource);
    }

    [Theory]
    [InlineData("CodexToolWindowControl")]
    [InlineData("CodexSettingsToolWindowControl")]
    public void WpfSurfacesDoNotOfferAnUnavailableModernInterface(string controlName)
    {
        var document = XDocument.Load(FindRepositoryFile("CodexVsix", controlName + ".xaml"));
        var controlSource = File.ReadAllText(FindRepositoryFile("CodexVsix", controlName + ".xaml.cs"));

        Assert.DoesNotContain(
            document.Descendants(PresentationNamespace + "Button"),
            element => element.Attributes().Any(attribute =>
                attribute.Value.Contains("RetryModernInterface")));
        Assert.DoesNotContain("retryModernInterface", controlSource);
        Assert.DoesNotContain("OnRetryModernInterfaceClick", controlSource);
    }

    [Fact]
    public void WindowedWebViewIsRecreatedOnlyWhenItsHostWindowChanges()
    {
        var hostSource = File.ReadAllText(FindRepositoryFile(
            "CodexVsix",
            "UI",
            "CodexOfficialWebViewHost.cs"));

        Assert.Contains("CodexWebViewHostAttachmentAction.RecreateWindowedControl", hostSource);
        Assert.Contains("RecreateWebViewForHostChange(rootWindow);", hostSource);
        Assert.Contains("PresentationSource.AddSourceChangedHandler(this, OnPresentationSourceChanged);", hostSource);
        Assert.Contains(
            "ScheduleHostObservation();",
            ExtractMethod(
                hostSource,
                "private void OnPresentationSourceChanged",
                "private void ScheduleHostObservation"));
        Assert.DoesNotContain("ResetWebViewForReload", hostSource);
        Assert.DoesNotContain("RecreateWebViewForHostChange", ExtractMethod(hostSource, "private void OnUnloaded", "private void OnSizeChanged"));
    }

    [Fact]
    public void DiagnosticLoggingUsesAVisuallyConsistentSwitchOnEveryClassicSettingsSurface()
    {
        var mainDocument = XDocument.Load(FindRepositoryFile("CodexVsix", "CodexToolWindowControl.xaml"));
        var settingsDocument = XDocument.Load(FindRepositoryFile("CodexVsix", "CodexSettingsToolWindowControl.xaml"));

        var mainSwitches = mainDocument
            .Descendants(PresentationNamespace + "ToggleButton")
            .Count(element => (element.Attribute("IsChecked")?.Value ?? string.Empty)
                .Contains("DiagnosticLoggingEnabled"));
        var settingsSwitches = settingsDocument
            .Descendants(PresentationNamespace + "ToggleButton")
            .Count(element => (element.Attribute("IsChecked")?.Value ?? string.Empty)
                .Contains("DiagnosticLoggingEnabled"));

        Assert.Equal(2, mainSwitches);
        Assert.Equal(1, settingsSwitches);
    }

    [Fact]
    public void ClassicSettingsOpenOnAUsableSectionInsteadOfABlankPanel()
    {
        var viewModelSource = File.ReadAllText(FindRepositoryFile(
            "CodexVsix",
            "ViewModels",
            "CodexToolWindowViewModel.cs"));

        var openSettingsMethod = ExtractMethod(
            viewModelSource,
            "private void OpenSettingsPanel()",
            "private void OpenHistoryPanel()");
        var toggleSettingsMethod = ExtractMethod(
            viewModelSource,
            "private void ToggleSettingsPanel()",
            "private void CloseSidebar()");

        Assert.Contains("SelectedSettingsSection = SettingsSectionCodex;", openSettingsMethod);
        Assert.Contains("SelectedSettingsSection = SettingsSectionCodex;", toggleSettingsMethod);
        Assert.DoesNotContain("SelectedSettingsSection = string.Empty;", openSettingsMethod);
    }

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source.Substring(start, end - start);
    }

    private static bool IsReadOnlyTextBox(XElement element)
    {
        var isReadOnly = element.Attribute("IsReadOnly")?.Value;
        var style = element.Attribute("Style")?.Value;

        return string.Equals(isReadOnly, "True", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(style)
                && style!.IndexOf("ChatMessageTextBoxStyle", StringComparison.Ordinal) >= 0);
    }

    private static bool IsBinding(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value!.TrimStart().StartsWith("{Binding", StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var segments = new List<string> { current.FullName };
            segments.AddRange(relativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repository file: " + Path.Combine(relativePath));
    }
}
