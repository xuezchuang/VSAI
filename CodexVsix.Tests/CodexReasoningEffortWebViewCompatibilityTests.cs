using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexReasoningEffortWebViewCompatibilityTests
{
    [Fact]
    public void GeneratedShellMapsOnlyTheGuardedModulesBeforeAnyModuleLoads()
    {
        var root = ResolveResourceRoot();
        var output = Path.Combine(Path.GetTempPath(), "vsai-reasoning-test-" + Guid.NewGuid().ToString("N"));
        var moduleNames = new[]
        {
            CodexReasoningEffortWebViewCompatibility.ComposerModule,
            CodexReasoningEffortWebViewCompatibility.LabelModule,
            CodexReasoningEffortWebViewCompatibility.SettingsModule
        };
        var originals = moduleNames.ToDictionary(name => name,
            name => File.ReadAllBytes(Path.Combine(root, "webview", "assets", name)));
        try
        {
            var html = CodexOfficialWebViewShell.Build(root, "test-efforts", "en-US",
                CodexVisualStudioTheme.Create("test", "dark", new Dictionary<string, string>()),
                compatibilityDirectory: output);
            var match = Regex.Match(html, "<script type=\"importmap\">(.*?)</script>");
            Assert.True(match.Success);
            Assert.True(match.Index < html.IndexOf("<script type=\"module\"", StringComparison.Ordinal));
            var preloadIndex = html.IndexOf("rel=\"modulepreload\"", StringComparison.Ordinal);
            Assert.True(preloadIndex < 0 || match.Index < preloadIndex);
            var imports = (JObject)JObject.Parse(match.Groups[1].Value)["imports"]!;
            Assert.Equal(3, imports.Count);

            foreach (var name in moduleNames)
            {
                var url = imports[CodexReasoningEffortWebViewCompatibility.AssetBaseUrl + name]!.Value<string>()!;
                Assert.Equal("https://" + CodexOfficialWebViewShell.ShellHostName
                    + "/reasoning-compatibility-test-efforts/" + name, url);
                var generatedPath = Path.Combine(output, "reasoning-compatibility-test-efforts", name);
                var generated = File.ReadAllText(generatedPath);
                Assert.DoesNotContain("\"./", generated);
                Assert.DoesNotContain("'./", generated);
                Assert.DoesNotContain("`./", generated);
                Assert.Contains(CodexReasoningEffortWebViewCompatibility.AssetBaseUrl, generated);
                Assert.Equal(originals[name], File.ReadAllBytes(Path.Combine(root, "webview", "assets", name)));
            }

            var composer = File.ReadAllText(Path.Combine(output, "reasoning-compatibility-test-efforts", moduleNames[0]));
            Assert.Contains("max:Sa,ultra:Sa", composer);
            Assert.Contains("f(d.model,t),V()", composer);
            Assert.Contains(CodexReasoningEffortWebViewCompatibility.AssetBaseUrl + "dialog-layout-sS9Dm_y9.css", composer);
            var labels = File.ReadAllText(Path.Combine(output, "reasoning-compatibility-test-efforts", moduleNames[1]));
            Assert.Contains("case`max`:return`Max`;case`ultra`:return`Ultra`;", labels);
            var settings = File.ReadAllText(Path.Combine(output, "reasoning-compatibility-test-efforts", moduleNames[2]));
            Assert.Contains("||e===`max`||e===`ultra`)&&t.includes(e)?e:k", settings);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    [Fact]
    public void ChangedOrUnknownFrozenModulesAreRejected()
    {
        var source = File.ReadAllText(Path.Combine(ResolveResourceRoot(), "webview", "assets",
            CodexReasoningEffortWebViewCompatibility.LabelModule));
        Assert.Throws<InvalidDataException>(() => CodexReasoningEffortWebViewCompatibility.AdaptModule(
            CodexReasoningEffortWebViewCompatibility.LabelModule, source + "\n// changed"));
        Assert.Throws<InvalidDataException>(() => CodexReasoningEffortWebViewCompatibility.AdaptModule(
            "unknown.js", source));
    }

    [Fact]
    public void ModuleGuardAcceptsBothGitCheckoutLineEndings()
    {
        var source = File.ReadAllText(Path.Combine(ResolveResourceRoot(), "webview", "assets",
            CodexReasoningEffortWebViewCompatibility.SettingsModule));
        var unix = CodexReasoningEffortWebViewCompatibility.AdaptModule(
            CodexReasoningEffortWebViewCompatibility.SettingsModule, source.Replace("\r\n", "\n"));
        var windows = CodexReasoningEffortWebViewCompatibility.AdaptModule(
            CodexReasoningEffortWebViewCompatibility.SettingsModule, source.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.Equal(unix, windows.Replace("\r\n", "\n"));
    }

    private static string ResolveResourceRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "UI", "CodexWebview");
        return File.Exists(Path.Combine(root, "webview", "index.html")) ? root : Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "CodexVsix", "UI", "CodexWebview"));
    }
}
