using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal sealed class CodexDiagnosticLoggingChangedEventArgs : EventArgs
{
    public CodexDiagnosticLoggingChangedEventArgs(bool enabled)
    {
        Enabled = enabled;
    }

    public bool Enabled { get; }
}

internal sealed class CodexDiagnosticLogger
{
    private const int DefaultMaximumFileBytes = 2 * 1024 * 1024;
    private const int DefaultArchiveCount = 3;
    private const int MaximumTextLength = 4000;

    private static readonly Regex BearerTokenRegex = new(
        @"Bearer\s+[^\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ApiKeyRegex = new(
        @"\b(?:sk|sess|pat)-[A-Za-z0-9_-]{10,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string SensitiveNamePattern =
        @"(?:[A-Za-z0-9]+[_-])*(?:api[_-]?key|access[_-]?token|auth[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|token|password|secret|authorization|cookie|set-cookie)";

    private static readonly Regex SensitivePropertyNameRegex = new(
        "^" + SensitiveNamePattern + "$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SensitiveAssignmentRegex = new(
        @"\b(" + SensitiveNamePattern + @")[""']?\s*[:=]\s*(?:""(?:\\.|[^""\\])*(?:""|$)|'(?:\\.|[^'\\])*(?:'|$)|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly object _syncRoot = new();
    private readonly string _logDirectory;
    private readonly string _logFile;
    private readonly int _maximumFileBytes;
    private readonly int _archiveCount;
    private bool _enabled;

    public CodexDiagnosticLogger()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VSAI",
            "logs"))
    {
    }

    internal CodexDiagnosticLogger(
        string logDirectory,
        int maximumFileBytes = DefaultMaximumFileBytes,
        int archiveCount = DefaultArchiveCount)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("A diagnostics log directory is required.", nameof(logDirectory));
        }

        if (maximumFileBytes < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        if (archiveCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveCount));
        }

        _logDirectory = Path.GetFullPath(logDirectory);
        _logFile = Path.Combine(_logDirectory, "codex-diagnostics.jsonl");
        _maximumFileBytes = maximumFileBytes;
        _archiveCount = archiveCount;
    }

    public static CodexDiagnosticLogger Shared { get; } = new();

    public bool IsEnabled
    {
        get
        {
            lock (_syncRoot)
            {
                return _enabled;
            }
        }
    }

    internal string LogFilePath => _logFile;

    public event EventHandler<CodexDiagnosticLoggingChangedEventArgs>? EnabledChanged;

    public void SetEnabled(bool enabled, bool writeTransition = true)
    {
        EventHandler<CodexDiagnosticLoggingChangedEventArgs>? handler;
        lock (_syncRoot)
        {
            if (_enabled == enabled)
            {
                return;
            }

            if (!enabled && writeTransition)
            {
                WriteUnsafe("diagnostics.disabled", null);
            }

            _enabled = enabled;
            if (enabled && writeTransition)
            {
                WriteUnsafe("diagnostics.enabled", null);
            }

            handler = EnabledChanged;
        }

        if (handler is null)
        {
            return;
        }

        var args = new CodexDiagnosticLoggingChangedEventArgs(enabled);
        foreach (EventHandler<CodexDiagnosticLoggingChangedEventArgs> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch
            {
                // Diagnostics must never affect extension behavior.
            }
        }
    }

    public void Write(string eventName, JObject? details = null)
    {
        lock (_syncRoot)
        {
            if (!_enabled)
            {
                return;
            }

            WriteUnsafe(eventName, details);
        }
    }

    private void WriteUnsafe(string eventName, JObject? details)
    {
        try
        {
            var entry = new JObject
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["event"] = NormalizeEventName(eventName)
            };
            if (details is not null && details.HasValues)
            {
                entry["details"] = SanitizeToken(details);
            }

            var line = entry.ToString(Formatting.None) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetByteCount(line);
            Directory.CreateDirectory(_logDirectory);
            RotateIfRequired(bytes);
            File.AppendAllText(_logFile, line, new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics are best effort and are deliberately isolated from the UI.
        }
    }

    private void RotateIfRequired(int incomingBytes)
    {
        if (!File.Exists(_logFile)
            || new FileInfo(_logFile).Length + incomingBytes <= _maximumFileBytes)
        {
            return;
        }

        var oldest = GetArchivePath(_archiveCount);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _archiveCount - 1; index >= 1; index--)
        {
            var source = GetArchivePath(index);
            if (File.Exists(source))
            {
                File.Move(source, GetArchivePath(index + 1));
            }
        }

        File.Move(_logFile, GetArchivePath(1));
    }

    private string GetArchivePath(int index)
    {
        return Path.Combine(_logDirectory, "codex-diagnostics." + index + ".jsonl");
    }

    private static string NormalizeEventName(string? eventName)
    {
        var value = (eventName ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "diagnostic.event";
        }

        return value.Length <= 120 ? value : value.Substring(0, 120);
    }

    private static JToken SanitizeToken(JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
                var sourceObject = (JObject)token;
                var sanitizedObject = new JObject();
                foreach (var property in sourceObject.Properties())
                {
                    sanitizedObject[property.Name] = SensitivePropertyNameRegex.IsMatch(property.Name)
                        ? new JValue("[redacted]")
                        : SanitizeToken(property.Value);
                }

                return sanitizedObject;

            case JTokenType.Array:
                var sanitizedArray = new JArray();
                foreach (var item in (JArray)token)
                {
                    sanitizedArray.Add(SanitizeToken(item));
                }

                return sanitizedArray;

            case JTokenType.String:
                return new JValue(SanitizeText(token.Value<string>()));

            default:
                return token.DeepClone();
        }
    }

    internal static string SanitizeText(string? value)
    {
        var text = value ?? string.Empty;
        text = BearerTokenRegex.Replace(text, "Bearer [redacted]");
        text = ApiKeyRegex.Replace(text, "[redacted-secret]");
        text = SensitiveAssignmentRegex.Replace(text, "$1=[redacted]");
        return text.Length <= MaximumTextLength ? text : text.Substring(0, MaximumTextLength);
    }
}
