using System;
using CodexVsix.UI;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexWebViewHostAttachmentTrackerTests
{
    [Fact]
    public void FirstAttachmentAndSameHostReuseExistingWindowedControl()
    {
        var tracker = new CodexWebViewHostAttachmentTracker();

        var first = tracker.Observe(new IntPtr(10), new IntPtr(20), CodexOfficialWebViewHostingMode.Windowed);
        var second = tracker.Observe(new IntPtr(10), new IntPtr(20), CodexOfficialWebViewHostingMode.Windowed);

        Assert.Equal(CodexWebViewHostAttachmentAction.FirstAttachment, first.Action);
        Assert.Equal(CodexWebViewHostAttachmentAction.Reuse, second.Action);
    }

    [Fact]
    public void RootOrNativeHostChangeRecreatesOnlyTheWindowedControl()
    {
        var rootTracker = new CodexWebViewHostAttachmentTracker();
        rootTracker.Observe(new IntPtr(10), new IntPtr(20), CodexOfficialWebViewHostingMode.Windowed);
        var changedRoot = rootTracker.Observe(new IntPtr(11), new IntPtr(20), CodexOfficialWebViewHostingMode.Windowed);

        var nativeTracker = new CodexWebViewHostAttachmentTracker();
        nativeTracker.Observe(new IntPtr(10), new IntPtr(20), CodexOfficialWebViewHostingMode.Windowed);
        var changedNative = nativeTracker.Observe(new IntPtr(10), new IntPtr(21), CodexOfficialWebViewHostingMode.Windowed);

        Assert.Equal(CodexWebViewHostAttachmentAction.RecreateWindowedControl, changedRoot.Action);
        Assert.Equal(CodexWebViewHostAttachmentAction.RecreateWindowedControl, changedNative.Action);
    }

    [Fact]
    public void CompositionControlDoesNotNeedNativeRecreationAcrossRootWindows()
    {
        var tracker = new CodexWebViewHostAttachmentTracker();
        tracker.Observe(new IntPtr(10), IntPtr.Zero, CodexOfficialWebViewHostingMode.Composition);

        var changed = tracker.Observe(new IntPtr(11), IntPtr.Zero, CodexOfficialWebViewHostingMode.Composition);

        Assert.Equal(CodexWebViewHostAttachmentAction.Reuse, changed.Action);
    }

    [Fact]
    public void CompositionPageSurvivesRepeatedDockAutoHideAndFloatTransitions()
    {
        var tracker = new CodexWebViewHostAttachmentTracker();
        var hostingMode = CodexOfficialWebViewHostingMode.Composition;
        tracker.Observe(new IntPtr(10), IntPtr.Zero, hostingMode);

        // VS temporarily detaches the visual before attaching to the auto-hide or floating host.
        foreach (var root in new[] { 11, 10, 12, 10 })
        {
            var detached = tracker.Observe(IntPtr.Zero, IntPtr.Zero, hostingMode);
            var attached = tracker.Observe(new IntPtr(root), IntPtr.Zero, hostingMode);

            Assert.Equal(CodexWebViewHostAttachmentAction.MissingHost, detached.Action);
            Assert.Equal(CodexWebViewHostAttachmentAction.Reuse, attached.Action);
            Assert.Equal(new IntPtr(root), attached.CurrentRootWindow);
        }
    }

    [Fact]
    public void MissingPresentationSourceDefersInitializationDecision()
    {
        var tracker = new CodexWebViewHostAttachmentTracker();

        var observation = tracker.Observe(IntPtr.Zero, IntPtr.Zero, CodexOfficialWebViewHostingMode.Windowed);

        Assert.Equal(CodexWebViewHostAttachmentAction.MissingHost, observation.Action);
    }
}
