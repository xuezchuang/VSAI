using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>The Personalization editor uses the same user AGENTS.md as Codex CLI.</summary>
internal static class CodexUserInstructionsStore
{
    internal static JObject HandleRequest(string method, JObject values, string? environmentVariables)
    {
        var hostId = values["hostId"]?.Value<string>();
        if (!string.IsNullOrEmpty(hostId) && !string.Equals(hostId, "local", StringComparison.Ordinal))
            throw new InvalidOperationException("VSAI can only edit instructions for the local host.");
        if (method != "codex-agents-md" && method != "codex-agents-md-save")
            throw new ArgumentException("Unsupported user instructions request.", nameof(method));
        if (method == "codex-agents-md-save" && values["contents"]?.Type != JTokenType.String)
            throw new ArgumentException("Instructions contents must be a string.", nameof(values));

        // Neither the current solution nor a path supplied by the WebView selects this file.
        var directory = CodexEnvironmentPathHelper.GetCodexHomeDirectory(environmentVariables);
        var path = Path.Combine(directory, "AGENTS.md");
        using var mutex = new Mutex(false, ExtensionSettingsStore.BuildSettingsMutexName(path));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Timed out while accessing user instructions.");

            var contents = ReadDocument(path, out var encoding, out var exists);
            if (method == "codex-agents-md")
            {
                var overrides = ReadDocument(Path.Combine(directory, "AGENTS.override.md"), out _, out _);
                return new JObject
                {
                    ["path"] = path,
                    ["contents"] = contents,
                    ["hasOverride"] = !string.IsNullOrWhiteSpace(overrides)
                };
            }

            var updated = values["contents"]!.Value<string>()!;
            // Textareas use LF; retain the existing file's newline convention and BOM.
            var newline = Regex.Match(contents, @"\r\n|\r|\n");
            if (newline.Success) updated = Regex.Replace(updated, @"\r\n|\r|\n", newline.Value);
            if (!exists || !string.Equals(contents, updated, StringComparison.Ordinal))
                WriteDocument(path, updated, encoding, exists);
            return new JObject { ["path"] = path };
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static string ReadDocument(string path, out Encoding encoding, out bool exists)
    {
        encoding = new UTF8Encoding(false, true);
        exists = false;
        try
        {
            var bytes = File.ReadAllBytes(path);
            // StreamReader's BOM detection substitutes a permissive decoder. Keep
            // strict decoding for every BOM so malformed instructions cannot be lost.
            if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
                encoding = new UTF32Encoding(false, true, true);
            else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
                encoding = new UTF32Encoding(true, true, true);
            else if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                encoding = new UTF8Encoding(true, true);
            else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
                encoding = new UnicodeEncoding(false, true, true);
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
                encoding = new UnicodeEncoding(true, true, true);
            var preambleLength = encoding.GetPreamble().Length;
            var contents = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            exists = true;
            return contents;
        }
        catch (FileNotFoundException) { return string.Empty; }
        catch (DirectoryNotFoundException) { return string.Empty; }
        // Access, IO and decoding failures must reach the editor instead of looking like an empty file.
    }

    private static void WriteDocument(string path, string contents, Encoding encoding, bool exists)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents, encoding);
            if (exists)
                File.Replace(temporary, path, path + ".vsai.bak");
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
