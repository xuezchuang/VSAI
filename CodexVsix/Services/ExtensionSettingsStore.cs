using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CodexVsix.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

public sealed class ExtensionSettingsStore
{
    private const string ProtectedSettingsProperty = "ProtectedSensitiveSettings";
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("CodexVsix.Settings.v1");
    private readonly string _settingsDirectory;
    private readonly string _settingsFile;
    private readonly string _settingsMutexName;

    public ExtensionSettingsStore()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSAI", "settings.json"),
            @"Local\VSAI.Settings")
    {
    }

    internal ExtensionSettingsStore(string settingsFile, string? mutexName = null)
    {
        if (string.IsNullOrWhiteSpace(settingsFile))
        {
            throw new ArgumentException("A settings file path is required.", nameof(settingsFile));
        }

        _settingsFile = Path.GetFullPath(settingsFile);
        _settingsDirectory = Path.GetDirectoryName(_settingsFile)
            ?? throw new ArgumentException("The settings file must have a parent directory.", nameof(settingsFile));
        _settingsMutexName = string.IsNullOrWhiteSpace(mutexName)
            ? BuildSettingsMutexName(_settingsFile)
            : mutexName!;
    }

    public string SettingsFilePath => _settingsFile;

    public string LastLoadError { get; private set; } = string.Empty;

    public CodexExtensionSettings Load()
    {
        LastLoadError = string.Empty;
        try
        {
            using var mutex = new Mutex(false, _settingsMutexName);
            if (!WaitForMutex(mutex))
            {
                throw new IOException("Timed out while waiting to read the Codex extension settings.");
            }

            try
            {
                return LoadWithoutLock();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            if (IsCorruptSettingsException(ex))
            {
                PreserveCorruptSettingsFile();
            }

            return Normalize(new CodexExtensionSettings());
        }
    }

    public void Save(CodexExtensionSettings settings, bool mergePromptHistory = true, bool updateProviders = false)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        Directory.CreateDirectory(_settingsDirectory);
        using var mutex = new Mutex(false, _settingsMutexName);
        if (!WaitForMutex(mutex))
        {
            throw new IOException("Timed out while waiting to save the Codex extension settings.");
        }

        try
        {
            var hadStoredSettings = File.Exists(_settingsFile);
            CodexExtensionSettings? existing = null;
            try
            {
                existing = LoadWithoutLock();
            }
            catch (JsonException)
            {
                // A malformed JSON file can be replaced after Save has a valid payload.
                // Decryption and IO failures must propagate, preserving the existing settings.
            }

            if (mergePromptHistory && existing is not null)
            {
                MergePromptHistory(settings, existing);
            }
            // Background/history saves from another VS instance must not erase newly saved keys.
            // Only the provider editor explicitly replaces this protected collection.
            if (!updateProviders && hadStoredSettings && existing is not null)
            {
                settings.Providers = existing.Providers;
            }
            // Solution folders are edited under this mutex by UpdateSolutionWorkingDirectory.
            // An unrelated save from another VS instance must not restore a stale map.
            if (hadStoredSettings && existing is not null)
            {
                settings.SolutionWorkingDirectories = existing.SolutionWorkingDirectories;
            }

            var json = SerializeForStorage(settings);
            WriteAtomically(json);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    internal void UpdateSolutionWorkingDirectory(CodexExtensionSettings settings, string solutionPath, string? customDirectory)
    {
        Directory.CreateDirectory(_settingsDirectory);
        using var mutex = new Mutex(false, _settingsMutexName);
        if (!WaitForMutex(mutex))
        {
            throw new IOException("Timed out while saving the solution working folder.");
        }

        try
        {
            var latest = LoadWithoutLock();
            var key = CodexWorkingDirectory.Resolve(solutionPath);
            if (customDirectory is null)
            {
                latest.SolutionWorkingDirectories.Remove(key);
            }
            else
            {
                latest.SolutionWorkingDirectories[key] = CodexWorkingDirectory.Resolve(customDirectory);
            }

            WriteAtomically(SerializeForStorage(latest));
            settings.SolutionWorkingDirectories = new Dictionary<string, string>(
                latest.SolutionWorkingDirectories, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    // Provider edits and discovery merge under the same cross-instance mutex as ordinary saves.
    // A discovery reply must never replace a newer key, local override, or another service.
    internal bool UpdateProviderCatalogs(CodexExtensionSettings settings, Func<CodexExtensionSettings, bool> update)
    {
        Directory.CreateDirectory(_settingsDirectory);
        using var mutex = new Mutex(false, _settingsMutexName);
        if (!WaitForMutex(mutex)) throw new IOException("Timed out while waiting to save provider settings.");
        try
        {
            var latest = File.Exists(_settingsFile)
                ? LoadWithoutLock()
                : JsonConvert.DeserializeObject<CodexExtensionSettings>(JsonConvert.SerializeObject(settings))!;
            var changed = update(latest);
            if (changed) WriteAtomically(SerializeForStorage(latest));
            settings.Providers = latest.Providers;
            settings.DefaultModel = latest.DefaultModel;
            return changed;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private CodexExtensionSettings LoadWithoutLock()
    {
        if (!File.Exists(_settingsFile))
        {
            return Normalize(new CodexExtensionSettings());
        }

        var json = File.ReadAllText(_settingsFile, Encoding.UTF8);
        var root = JObject.Parse(json);
        var settings = root.ToObject<CodexExtensionSettings>() ?? new CodexExtensionSettings();
        if (root[ProtectedSettingsProperty]?.Value<string>() is { Length: > 0 } protectedPayload)
        {
            var sensitive = UnprotectSensitiveSettings(protectedPayload);
            settings.EnvironmentVariables = sensitive.EnvironmentVariables;
            settings.RawTomlOverrides = sensitive.RawTomlOverrides;
            settings.AdditionalArguments = sensitive.AdditionalArguments;
            settings.Providers = sensitive.Providers;
            if (root[nameof(CodexExtensionSettings.PromptHistory)] is null)
            {
                settings.PromptHistory = sensitive.PromptHistory;
            }
        }

        return Normalize(settings);
    }

    private static string SerializeForStorage(CodexExtensionSettings settings)
    {
        var root = JObject.FromObject(settings);
        root.Remove(nameof(CodexExtensionSettings.EnvironmentVariables));
        root.Remove(nameof(CodexExtensionSettings.RawTomlOverrides));
        root.Remove(nameof(CodexExtensionSettings.AdditionalArguments));
        root.Remove(nameof(CodexExtensionSettings.PromptHistory));
        root.Remove(nameof(CodexExtensionSettings.Providers));
        root[ProtectedSettingsProperty] = ProtectSensitiveSettings(new SensitiveSettings
        {
            EnvironmentVariables = settings.EnvironmentVariables ?? string.Empty,
            RawTomlOverrides = settings.RawTomlOverrides ?? string.Empty,
            AdditionalArguments = settings.AdditionalArguments ?? string.Empty,
            PromptHistory = settings.PromptHistory ?? new List<string>(),
            Providers = settings.Providers ?? new List<CodexProviderConfiguration>()
        });
        return NewtonsoftJsonCompatibility.Serialize(root, Formatting.Indented);
    }

    private static string ProtectSensitiveSettings(SensitiveSettings settings)
    {
        var plaintext = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(settings));
        var protectedBytes = ProtectedData.Protect(plaintext, ProtectionEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static SensitiveSettings UnprotectSensitiveSettings(string value)
    {
        try
        {
            var protectedBytes = Convert.FromBase64String(value);
            var plaintext = ProtectedData.Unprotect(protectedBytes, ProtectionEntropy, DataProtectionScope.CurrentUser);
            return JsonConvert.DeserializeObject<SensitiveSettings>(Encoding.UTF8.GetString(plaintext))
                ?? new SensitiveSettings();
        }
        catch (Exception ex) when (ex is CryptographicException || ex is FormatException || ex is JsonException)
        {
            throw new InvalidDataException("The protected Codex settings could not be decrypted for the current Windows user.", ex);
        }
    }

    private static void MergePromptHistory(CodexExtensionSettings settings, CodexExtensionSettings existing)
    {
        settings.PromptHistory = (existing.PromptHistory ?? new List<string>())
            .Concat(settings.PromptHistory ?? new List<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Reverse()
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .Reverse()
            .ToList();
    }

    private void WriteAtomically(string json)
    {
        var tempFile = Path.Combine(_settingsDirectory, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
        var backupFile = Path.Combine(_settingsDirectory, "settings.bak.json");
        try
        {
            File.WriteAllText(tempFile, json, new UTF8Encoding(false));
            if (File.Exists(_settingsFile))
            {
                File.Replace(tempFile, _settingsFile, backupFile, true);
            }
            else
            {
                File.Move(tempFile, _settingsFile);
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

    private static CodexExtensionSettings Normalize(CodexExtensionSettings settings)
    {
        settings.PromptHistory ??= new List<string>();
        settings.CodexExecutablePath ??= "codex.cmd";
        settings.LanguageOverride ??= string.Empty;
        settings.WorkingDirectory ??= string.Empty;
        settings.SolutionWorkingDirectories = settings.SolutionWorkingDirectories is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(settings.SolutionWorkingDirectories, StringComparer.OrdinalIgnoreCase);
        settings.DefaultModel ??= string.Empty;
        settings.ReasoningEffort ??= string.Empty;
        settings.ModelVerbosity ??= string.Empty;
        settings.ServiceTier ??= string.Empty;
        settings.Profile ??= string.Empty;
        settings.ApprovalPolicy ??= string.Empty;
        settings.SandboxMode ??= string.Empty;
        settings.FollowUpQueueMode ??= "queue";
        settings.ComposerEnterBehavior ??= "enter";
        settings.ReviewDelivery ??= "inline";
        settings.AdditionalArguments ??= string.Empty;
        settings.EnvironmentVariables ??= string.Empty;
        settings.RawTomlOverrides ??= string.Empty;
        settings.CurrentThreadId ??= string.Empty;
        settings.LastThreadWorkingDirectory ??= string.Empty;
        settings.CustomModels ??= new List<string>();
        settings.Providers ??= new List<CodexProviderConfiguration>();
        settings.Providers = settings.Providers.Where(provider => provider is not null).ToList();
        foreach (var provider in settings.Providers)
        {
            // Keep invalid identities intact so validation can report them; never
            // silently assign a different provider to an existing model selection.
            provider.Id ??= string.Empty;
            provider.Name ??= string.Empty;
            provider.BaseUrl ??= string.Empty;
            provider.ApiKey ??= string.Empty;
            provider.Models ??= new List<string>();
            if (provider.Catalog is not null)
                CodexProviderCatalogConfigurationService.ApplyEffectiveProperties(provider);
        }
        settings.CustomReasoningEfforts ??= new List<string>();
        settings.CustomVerbosityOptions ??= new List<string>();
        settings.CustomServiceTiers ??= new List<string>();
        settings.ManagedMcpServers ??= new List<CodexManagedMcpServer>();
        settings.PreferredMcpServers ??= new List<string>();
        return settings;
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

    internal static string BuildSettingsMutexName(string settingsFile)
    {
        var normalizedPath = Path.GetFullPath(settingsFile).ToUpperInvariant();
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath));
        return @"Local\CodexVsix.Settings." + BitConverter.ToString(hash).Replace("-", string.Empty);
    }

    private static bool IsCorruptSettingsException(Exception exception)
    {
        return exception is JsonException || exception is InvalidDataException;
    }

    private void PreserveCorruptSettingsFile()
    {
        try
        {
            if (!File.Exists(_settingsFile))
            {
                return;
            }

            Directory.CreateDirectory(_settingsDirectory);
            var destination = Path.Combine(
                _settingsDirectory,
                "settings.corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json");
            File.Copy(_settingsFile, destination, overwrite: false);
        }
        catch
        {
        }
    }

    private sealed class SensitiveSettings
    {
        public string EnvironmentVariables { get; set; } = string.Empty;

        public string RawTomlOverrides { get; set; } = string.Empty;

        public string AdditionalArguments { get; set; } = string.Empty;

        public List<string> PromptHistory { get; set; } = new();

        public List<CodexProviderConfiguration> Providers { get; set; } = new();
    }
}

internal static class EnumerableCompatibilityExtensions
{
    internal static IEnumerable<T> TakeLastCompat<T>(this IEnumerable<T> source, int count)
    {
        if (count <= 0)
        {
            return Enumerable.Empty<T>();
        }

        var values = source as IList<T> ?? source.ToList();
        return values.Skip(Math.Max(0, values.Count - count));
    }
}
