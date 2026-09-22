using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace CodexVsix.Services;

internal static class CodexOfficialWebViewShell
{
    internal const string OfficialExtensionVersion = "26.5527.31454";
    internal const string AssetHostName = "codex-assets.local";
    internal const string ShellHostName = "codex-shell.local";

    private static readonly Regex RelativeAssetRegex = new(
        "\\b(src|href)=([\"'])\\./([^\"']+)\\2",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string WebView2TransportAdapter = @"
<script>
(function () {
  'use strict';
  if (!window.chrome || !window.chrome.webview) {
    return;
  }
  window.chrome.webview.addEventListener('message', function (event) {
    window.dispatchEvent(new MessageEvent('message', {
      data: event.data,
      origin: window.location.origin,
      source: window
    }));
  });
})();
</script>";

    private const string ThemeAdapter = @"
<script>
(function () {
  'use strict';
  function apply(theme) {
    if (!theme || typeof theme !== 'object') return;
    var root = document.documentElement;
    var colors = theme.colors || {};
    Object.keys(colors).forEach(function (name) {
      if (/^--[A-Za-z0-9_.-]+$/.test(name) && typeof colors[name] === 'string') {
        root.style.setProperty(name, colors[name]);
      }
    });
    var variant = theme.variant === 'light' ? 'light' : 'dark';
    root.style.colorScheme = variant;
    root.classList.toggle('light', variant === 'light');
    root.classList.toggle('dark', variant === 'dark');
    if (document.body) {
      document.body.classList.toggle('light', variant === 'light');
      document.body.classList.toggle('dark', variant === 'dark');
    }
  }
  window.addEventListener('message', function (event) {
    var data = event.data;
    if (data && data.type === 'theme-updated') {
      apply(data.theme || data);
    }
  });
})();
</script>";

    public static string Build(
        string resourceRoot,
        string webviewId,
        string locale,
        CodexVisualStudioTheme theme,
        string? initialRoute = null,
        bool diagnosticLoggingEnabled = false,
        bool isSettingsSurface = false,
        string? compatibilityDirectory = null)
    {
        var indexPath = Path.Combine(resourceRoot, "webview", "index.html");
        var shimPath = Path.Combine(resourceRoot, "codex-acquire-vscode-api-shim.js");
        var historyGuardPath = Path.Combine(resourceRoot, "codex-visual-studio-history-guard.js");
        var diagnosticsPath = Path.Combine(resourceRoot, "codex-visual-studio-diagnostics.js");
        var projectSettingsPath = Path.Combine(resourceRoot, "vsai-project-settings.js");
        var providersPath = Path.Combine(resourceRoot, "vsai-providers.js");
        if (!File.Exists(indexPath)
            || !File.Exists(shimPath)
            || !File.Exists(historyGuardPath)
            || !File.Exists(diagnosticsPath)
            || !File.Exists(projectSettingsPath)
            || !File.Exists(providersPath))
        {
            var missingPath = !File.Exists(indexPath)
                ? indexPath
                : !File.Exists(shimPath)
                    ? shimPath
                    : !File.Exists(historyGuardPath)
                        ? historyGuardPath
                        : !File.Exists(diagnosticsPath)
                            ? diagnosticsPath
                            : !File.Exists(projectSettingsPath)
                                ? projectSettingsPath
                                : providersPath;
            throw new FileNotFoundException("The bundled Codex webview is incomplete.", missingPath);
        }

        var html = File.ReadAllText(indexPath)
            .Replace("<!-- PROD_BASE_TAG_HERE -->", string.Empty)
            .Replace("<!-- PROD_CSP_TAG_HERE -->", string.Empty);
        html = Regex.Replace(html, "<base\\b[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        html = RelativeAssetRegex.Replace(
            html,
            match => string.Format(
                "{0}={1}https://{2}/webview/{3}{1}",
                match.Groups[1].Value,
                match.Groups[2].Value,
                AssetHostName,
                match.Groups[3].Value));
        html = AppendWebviewIdToModuleEntrypoint(html, webviewId);
        if (compatibilityDirectory != null)
        {
            var importMap = CodexReasoningEffortWebViewCompatibility.CreateImportMap(
                resourceRoot, compatibilityDirectory, webviewId);
            // Import maps must precede every module and modulepreload in the document.
            html = Regex.Replace(html, "<head\\b[^>]*>",
                match => match.Value + "\n" + importMap, RegexOptions.IgnoreCase);
        }
        html = Regex.Replace(
            html,
            "<html\\s+lang=([\"'])[^\"']*\\1",
            "<html lang=\"" + WebUtility.HtmlEncode(NormalizeLocale(locale)) + "\"",
            RegexOptions.IgnoreCase);

        var meta = "<meta name=\"codex-version\" content=\"" + OfficialExtensionVersion + "\">\n"
            + "<meta name=\"codex-session-id\" content=\"vs-" + WebUtility.HtmlEncode(webviewId) + "\">\n"
            + "<meta name=\"codex-build-flavor\" content=\"prod\">\n"
            + "<meta name=\"codex-view-kind\" content=\"sidebar\">\n"
            + "<meta name=\"vsai-settings-surface\" content=\"" + (isSettingsSurface ? "true" : "false") + "\">\n"
            + "<meta name=\"codex-diagnostic-logging-enabled\" content=\""
            + (diagnosticLoggingEnabled ? "true" : "false")
            + "\">\n"
            + "<meta name=\"initial-route\" content=\"" + WebUtility.HtmlEncode(NormalizeRoute(initialRoute)) + "\">";
        html = Regex.Replace(
            html,
            "(<meta\\s+name=([\"'])viewport\\2[^>]*>)",
            "$1\n" + meta,
            RegexOptions.IgnoreCase);

        var shim = AdaptShimForWebView2(File.ReadAllText(shimPath));
        var historyGuard = File.ReadAllText(historyGuardPath);
        var diagnostics = File.ReadAllText(diagnosticsPath);
        var projectSettings = File.ReadAllText(projectSettingsPath);
        var providers = File.ReadAllText(providersPath);
        var injection = "<style>" + theme.ToCss() + "</style>\n"
            + ThemeAdapter + "\n"
            + WebView2TransportAdapter + "\n"
            + "<script>" + shim + "</script>\n"
            + "<script>" + historyGuard + "</script>\n"
            + "<script>" + diagnostics + "</script>\n"
            + "<script>" + projectSettings + "</script>\n"
            + "<script>" + providers + "</script>\n";
        html = Regex.Replace(
            html,
            "(<script\\b(?=[^>]*\\btype=([\"'])module\\2)[^>]*>)",
            injection + "$1",
            RegexOptions.IgnoreCase);
        return html;
    }

    internal static string AdaptShimForWebView2(string shim)
    {
        const string parentTransport = "window.parent.postMessage({ channel: CHANNEL, webviewId: webviewId, message: message }, '*');";
        const string nativeTransport = "window.chrome.webview.postMessage({ channel: CHANNEL, webviewId: webviewId, message: message });";
        if (shim.IndexOf(parentTransport, StringComparison.Ordinal) < 0)
        {
            throw new InvalidDataException("The bundled Codex VS Code API shim has an unexpected transport shape.");
        }

        return shim.Replace(parentTransport, nativeTransport);
    }

    private static string AppendWebviewIdToModuleEntrypoint(string html, string webviewId)
    {
        return Regex.Replace(
            html,
            "(<script\\b(?=[^>]*\\btype=([\"'])module\\2)(?=[^>]*\\bsrc=)[^>]*\\bsrc=([\"']))([^\"']+)(\\3)",
            match =>
            {
                var src = match.Groups[4].Value;
                var separator = src.IndexOf('?') >= 0 ? "&" : "?";
                return match.Groups[1].Value
                    + src
                    + separator
                    + "webviewId="
                    + Uri.EscapeDataString(webviewId)
                    + match.Groups[5].Value;
            },
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }

    private static string NormalizeLocale(string locale)
    {
        return string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Trim();
    }

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return "/";
        }

        return route![0] == '/' ? route : "/" + route;
    }
}
