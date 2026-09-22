using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace CodexVsix.Services;

// Keep the upstream bundle immutable. These version-guarded copies only extend the
// frozen UI's effort labels/normalizer; the model catalog still controls availability.
internal static class CodexReasoningEffortWebViewCompatibility
{
    internal const string ComposerModule = "composer-B3BCMq_W.js";
    internal const string LabelModule = "reasoning-minimal-BmczWw15.js";
    internal const string SettingsModule = "use-model-settings-D_RJ6qrG.js";
    internal const string AssetBaseUrl = "https://" + CodexOfficialWebViewShell.AssetHostName + "/webview/assets/";

    private static readonly IReadOnlyDictionary<string, string> ModuleHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ComposerModule] = "03b05c10f98be300790c094ee70d258907835c2cbcddc5b382d1dc97ed1f6b8d",
            [LabelModule] = "752677eaa4d9f3021b6ac4a05c6a0f778301ad2d4449f655bb01afa9316e0907",
            [SettingsModule] = "1fc45f6af86f26d85b653b2c835210dc9f999752527e8e824b5fc229045d2c6c"
        };

    private static readonly Regex RelativeAssetLiteral = new(
        "([\"'`])\\./([A-Za-z0-9_.-]+\\.(?:js|css))\\1",
        RegexOptions.Compiled);

    internal static string CreateImportMap(string resourceRoot, string shellDirectory, string webviewId)
    {
        if (!Regex.IsMatch(webviewId, @"\A[A-Za-z0-9_-]+\z"))
        {
            throw new ArgumentException("The webview ID must be a safe directory name.", nameof(webviewId));
        }

        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in ModuleHashes)
        {
            var source = File.ReadAllText(Path.Combine(resourceRoot, "webview", "assets", entry.Key));
            modules.Add(entry.Key, AdaptModule(entry.Key, source));
        }

        // Each host owns its directory so independent VS tool windows never overwrite
        // a module while another host is loading it. Recovery reuses the same contents.
        var directoryName = "reasoning-compatibility-" + webviewId;
        var outputDirectory = Path.Combine(shellDirectory, directoryName);
        Directory.CreateDirectory(outputDirectory);
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            File.WriteAllText(Path.Combine(outputDirectory, module.Key), module.Value, new UTF8Encoding(false));
            imports.Add(AssetBaseUrl + module.Key,
                "https://" + CodexOfficialWebViewShell.ShellHostName + "/" + directoryName + "/" + module.Key);
        }

        return "<script type=\"importmap\">"
            + JsonConvert.SerializeObject(new { imports })
            + "</script>";
    }

    internal static string AdaptModule(string moduleName, string source)
    {
        // Normalize only line endings for the digest, allowing Git's Windows checkout
        // conversion without accepting a different bundle or a partly patched source.
        using (var sha256 = SHA256.Create())
        {
            var digest = BitConverter.ToString(sha256.ComputeHash(
                Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n")))).Replace("-", string.Empty).ToLowerInvariant();
            if (!ModuleHashes.TryGetValue(moduleName, out var expected)
                || !string.Equals(digest, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The frozen Codex reasoning module has changed: " + moduleName);
            }
        }

        switch (moduleName)
        {
            case ComposerModule:
                // Reuse the existing highest-effort glyph, without changing the value
                // used by the menu, selection callback, telemetry or request payload.
                source = ReplaceOnce(source,
                    "var qp={none:Aa,minimal:Aa,low:Oa,medium:Ea,high:wa,xhigh:Sa};",
                    "var qp={none:Aa,minimal:Aa,low:Oa,medium:Ea,high:wa,xhigh:Sa,max:Sa,ultra:Sa};");
                break;
            case LabelModule:
                source = ReplaceOnce(source,
                    "function d(e){let t=(0,u.c)(6),{effort:n}=e;switch(n){",
                    "function d(e){let t=(0,u.c)(6),{effort:n}=e;switch(n){case`max`:return`Max`;case`ultra`:return`Ultra`;");
                break;
            case SettingsModule:
                source = ReplaceOnce(source,
                    "function R(e,t){return(e===`none`||e===`minimal`||e===`low`||e===`medium`||e===`high`||e===`xhigh`)&&t.includes(e)?e:k}",
                    "function R(e,t){return(e===`none`||e===`minimal`||e===`low`||e===`medium`||e===`high`||e===`xhigh`||e===`max`||e===`ultra`)&&t.includes(e)?e:k}");
                break;
        }

        // Moving a module changes its base URL. Anchor every asset literal (including
        // Vite's lazy-import/preload arrays and CSS) to the original immutable bundle.
        // Absolute imports still pass through the import map for the three adapters.
        source = RelativeAssetLiteral.Replace(source,
            match => match.Groups[1].Value + AssetBaseUrl + match.Groups[2].Value + match.Groups[1].Value);
        return source;
    }

    private static string ReplaceOnce(string source, string before, string after)
    {
        var index = source.IndexOf(before, StringComparison.Ordinal);
        if (index < 0 || source.IndexOf(before, index + before.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("The frozen Codex reasoning adapter does not match exactly once.");
        }

        return source.Substring(0, index) + after + source.Substring(index + before.Length);
    }
}
