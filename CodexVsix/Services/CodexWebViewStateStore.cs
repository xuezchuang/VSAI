using System;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal sealed class CodexWebViewStateStore
{
    private readonly string _stateFile;
    private readonly string _mutexName;

    public CodexWebViewStateStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VSAI", "official-webview-state.private-v1.json"))
    {
    }

    internal CodexWebViewStateStore(string stateFile)
    {
        _stateFile = Path.GetFullPath(stateFile);
        _mutexName = ExtensionSettingsStore.BuildSettingsMutexName(_stateFile);
    }

    public JObject GetSnapshot()
    {
        try
        {
            return WithStateLock(LoadState);
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            // Keep the interface usable, but never use this fallback when saving.
            CodexDiagnosticLogger.Shared.Write("webview.state.read-failed", new JObject
            {
                ["errorType"] = ex.GetType().FullName
            });
            return new JObject();
        }
    }

    public JToken? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return GetSnapshot()[key];
    }

    public void Set(string key, JToken? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        WithStateLock(() =>
        {
            var state = LoadState();
            if (value is null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined)
            {
                state.Remove(key);
            }
            else
            {
                state[key] = value.DeepClone();
            }

            SaveState(state);
            return true;
        });
    }

    private JObject LoadState()
    {
        // Each VS process can update this file. Read it again while holding the
        // shared mutex so an update never replaces another instance's newer keys.
        return File.Exists(_stateFile)
            ? JObject.Parse(File.ReadAllText(_stateFile))
            : new JObject();
    }

    private void SaveState(JObject state)
    {
        var directory = Path.GetDirectoryName(_stateFile)!;
        Directory.CreateDirectory(directory);
        var temporary = _stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, state.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(_stateFile))
            {
                File.Replace(temporary, _stateFile, null);
            }
            else
            {
                File.Move(temporary, _stateFile);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private T WithStateLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException("Timed out while waiting for the Codex interface state.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
