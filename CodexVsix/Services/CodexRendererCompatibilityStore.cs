using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal enum CodexRendererKind
{
    OfficialWebView,
    ClassicWpf
}

internal sealed class CodexRendererCompatibilitySnapshot
{
    public int SchemaVersion { get; set; } = CodexRendererCompatibilityStore.CurrentSchemaVersion;

    public int RendererPolicyVersion { get; set; } = CodexRendererCompatibilityStore.CurrentRendererPolicyVersion;

    public string EnvironmentFingerprint { get; set; } = string.Empty;

    public string PreferredRenderer { get; set; } = CodexRendererCompatibilityStore.OfficialRendererId;

    public string LastOutcome { get; set; } = string.Empty;

    public string LastReason { get; set; } = string.Empty;

    public long? LastReadyDurationMilliseconds { get; set; }

    public string UpdatedUtc { get; set; } = string.Empty;
}

internal sealed class CodexRendererCompatibilityStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int CurrentRendererPolicyVersion = 2;
    internal const string OfficialRendererId = "official-webview";
    internal const string ClassicRendererId = "classic-wpf";

    private readonly string _stateDirectory;
    private readonly string _stateFile;
    private readonly string _mutexName;

    public CodexRendererCompatibilityStore()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VSAI",
                "renderer-compatibility.json"),
            @"Local\CodexVsix.RendererCompatibility")
    {
    }

    internal CodexRendererCompatibilityStore(string stateFile, string? mutexName = null)
    {
        if (string.IsNullOrWhiteSpace(stateFile))
        {
            throw new ArgumentException("A renderer compatibility state file is required.", nameof(stateFile));
        }

        _stateFile = Path.GetFullPath(stateFile);
        _stateDirectory = Path.GetDirectoryName(_stateFile)
            ?? throw new ArgumentException("The renderer compatibility state file must have a parent directory.", nameof(stateFile));
        _mutexName = string.IsNullOrWhiteSpace(mutexName)
            ? BuildMutexName(_stateFile)
            : mutexName!;
    }

    public string StateFilePath => _stateFile;

    public string LastLoadError { get; private set; } = string.Empty;

    public CodexRendererCompatibilitySnapshot Load(string environmentFingerprint)
    {
        var normalizedFingerprint = NormalizeFingerprint(environmentFingerprint);
        LastLoadError = string.Empty;
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            if (!WaitForMutex(mutex))
            {
                throw new IOException("Timed out while waiting to read the Codex renderer compatibility cache.");
            }

            try
            {
                if (!File.Exists(_stateFile))
                {
                    return CreateDefault(normalizedFingerprint);
                }

                var snapshot = JObject.Parse(File.ReadAllText(_stateFile, Encoding.UTF8))
                    .ToObject<CodexRendererCompatibilitySnapshot>();
                if (!IsCompatible(snapshot, normalizedFingerprint))
                {
                    return CreateDefault(normalizedFingerprint);
                }

                return Normalize(snapshot!, normalizedFingerprint);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            if (ex is JsonException)
            {
                PreserveCorruptStateFile();
            }

            return CreateDefault(normalizedFingerprint);
        }
    }

    public void Save(CodexRendererCompatibilitySnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var normalized = Normalize(snapshot, NormalizeFingerprint(snapshot.EnvironmentFingerprint));
        Directory.CreateDirectory(_stateDirectory);
        using var mutex = new Mutex(false, _mutexName);
        if (!WaitForMutex(mutex))
        {
            throw new IOException("Timed out while waiting to save the Codex renderer compatibility cache.");
        }

        try
        {
            var json = JsonConvert.SerializeObject(normalized, Formatting.Indented);
            WriteAtomically(json);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    internal static CodexRendererCompatibilitySnapshot CreateDefault(string environmentFingerprint)
    {
        return new CodexRendererCompatibilitySnapshot
        {
            EnvironmentFingerprint = NormalizeFingerprint(environmentFingerprint),
            PreferredRenderer = OfficialRendererId
        };
    }

    internal static CodexRendererKind ParseRenderer(string? renderer)
    {
        return string.Equals(renderer, ClassicRendererId, StringComparison.Ordinal)
            ? CodexRendererKind.ClassicWpf
            : CodexRendererKind.OfficialWebView;
    }

    internal static string FormatRenderer(CodexRendererKind renderer)
    {
        return renderer == CodexRendererKind.ClassicWpf
            ? ClassicRendererId
            : OfficialRendererId;
    }

    internal static string BuildMutexName(string stateFile)
    {
        var normalizedPath = Path.GetFullPath(stateFile).ToUpperInvariant();
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath));
        return @"Local\CodexVsix.RendererCompatibility."
            + BitConverter.ToString(hash).Replace("-", string.Empty);
    }

    private static bool IsCompatible(
        CodexRendererCompatibilitySnapshot? snapshot,
        string environmentFingerprint)
    {
        return snapshot is not null
            && snapshot.SchemaVersion == CurrentSchemaVersion
            && snapshot.RendererPolicyVersion == CurrentRendererPolicyVersion
            && string.Equals(snapshot.EnvironmentFingerprint, environmentFingerprint, StringComparison.Ordinal)
            && (string.Equals(snapshot.PreferredRenderer, OfficialRendererId, StringComparison.Ordinal)
                || string.Equals(snapshot.PreferredRenderer, ClassicRendererId, StringComparison.Ordinal));
    }

    private static CodexRendererCompatibilitySnapshot Normalize(
        CodexRendererCompatibilitySnapshot snapshot,
        string environmentFingerprint)
    {
        snapshot.SchemaVersion = CurrentSchemaVersion;
        snapshot.RendererPolicyVersion = CurrentRendererPolicyVersion;
        snapshot.EnvironmentFingerprint = environmentFingerprint;
        snapshot.PreferredRenderer = FormatRenderer(ParseRenderer(snapshot.PreferredRenderer));
        snapshot.LastOutcome = Trim(snapshot.LastOutcome, 80);
        snapshot.LastReason = Trim(CodexDiagnosticLogger.SanitizeText(snapshot.LastReason), 800);
        snapshot.UpdatedUtc = Trim(snapshot.UpdatedUtc, 80);
        return snapshot;
    }

    private void WriteAtomically(string json)
    {
        var tempFile = Path.Combine(
            _stateDirectory,
            "renderer-compatibility." + Guid.NewGuid().ToString("N") + ".tmp");
        var backupFile = Path.Combine(_stateDirectory, "renderer-compatibility.bak.json");
        try
        {
            File.WriteAllText(tempFile, json, new UTF8Encoding(false));
            if (File.Exists(_stateFile))
            {
                File.Replace(tempFile, _stateFile, backupFile, true);
            }
            else
            {
                File.Move(tempFile, _stateFile);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
            }
        }
    }

    private void PreserveCorruptStateFile()
    {
        try
        {
            if (!File.Exists(_stateFile))
            {
                return;
            }

            Directory.CreateDirectory(_stateDirectory);
            var destination = Path.Combine(
                _stateDirectory,
                "renderer-compatibility.corrupt."
                + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
                + ".json");
            File.Copy(_stateFile, destination, overwrite: false);
        }
        catch
        {
        }
    }

    private static bool WaitForMutex(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static string NormalizeFingerprint(string? fingerprint)
    {
        var value = (fingerprint ?? string.Empty).Trim();
        return value.Length > 0 ? value : "unknown";
    }

    private static string Trim(string? value, int maximumLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maximumLength ? text : text.Substring(0, maximumLength);
    }
}

internal static class CodexRendererEnvironmentFingerprint
{
    public static string CreateCurrent()
    {
        return Create(
            ResolveVisualStudioVersion(),
            Environment.OSVersion.Version.ToString(),
            ResolveWebViewRuntimeVersion(),
            Environment.Is64BitProcess ? "x64" : "x86");
    }

    internal static string Create(
        string visualStudioVersion,
        string operatingSystemVersion,
        string webViewRuntimeVersion,
        string processArchitecture)
    {
        return string.Join(
            "|",
            "policy=" + CodexRendererCompatibilityStore.CurrentRendererPolicyVersion,
            "vs=" + NormalizeComponent(visualStudioVersion),
            "os=" + NormalizeComponent(operatingSystemVersion),
            "webview2=" + NormalizeComponent(webViewRuntimeVersion),
            "arch=" + NormalizeComponent(processArchitecture));
    }

    private static string ResolveVisualStudioVersion()
    {
        try
        {
            var executable = Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrWhiteSpace(executable)
                ? "unknown"
                : FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ResolveWebViewRuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string NormalizeComponent(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace("|", "_");
        return normalized.Length > 0 ? normalized : "unknown";
    }
}
