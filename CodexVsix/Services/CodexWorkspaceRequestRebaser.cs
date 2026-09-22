using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

// Only threads created through this bridge participate. Resumed/history threads
// must keep their own cwd even when the new-chat folder changes.
internal sealed class CodexWorkspaceRequestRebaser
{
    private readonly object _sync = new();
    private readonly HashSet<string> _previousDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NewThread> _threads = new(StringComparer.Ordinal);
    private string? _directory;

    public void ChangeDirectory(string previousDirectory, string newDirectory, string? priorComposerPrefillDirectory = null)
    {
        var directory = CodexWorkingDirectory.Resolve(newDirectory);
        lock (_sync)
        {
            RememberPreviousDirectory(previousDirectory);
            RememberPreviousDirectory(priorComposerPrefillDirectory);
            _directory = directory;
            foreach (var thread in _threads.Values)
            {
                if (!thread.Started && _previousDirectories.Contains(thread.Directory))
                {
                    RebaseThread(thread, directory);
                }
            }
        }
    }

    public JToken? PrepareRequest(string method, JToken? parameters, string defaultDirectory)
    {
        var clone = parameters?.DeepClone();
        if (method != "thread/start" && method != "turn/start")
        {
            return clone;
        }

        if (clone is null && method == "thread/start")
        {
            clone = new JObject();
        }

        if (clone is not JObject request)
        {
            return clone;
        }

        lock (_sync)
        {
            var requestedDirectory = NormalizePath(request["cwd"]);
            if (method == "thread/start")
            {
                var directory = _directory ?? CodexWorkingDirectory.Resolve(defaultDirectory);
                if (string.IsNullOrWhiteSpace(request["cwd"]?.Value<string>())
                    || (requestedDirectory is not null && _previousDirectories.Contains(requestedDirectory)))
                {
                    request["cwd"] = directory;
                    if (requestedDirectory is not null && !PathsEqual(requestedDirectory, directory))
                    {
                        ReplaceRoots(request, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { requestedDirectory }, directory);
                    }
                }

                return request;
            }

            var threadId = request["threadId"]?.Value<string>();
            if (threadId is null || !_threads.TryGetValue(threadId, out var thread))
            {
                return request;
            }

            if (thread.PreviousDirectories.Count > 0
                && (string.IsNullOrWhiteSpace(request["cwd"]?.Value<string>())
                    || (requestedDirectory is not null
                        && (thread.PreviousDirectories.Contains(requestedDirectory)
                            || PathsEqual(requestedDirectory, thread.Directory)))))
            {
                request["cwd"] = thread.Directory;
                ReplaceRoots(request, thread.PreviousDirectories, thread.Directory);
            }

            // Freeze when the request leaves the bridge, not when its response arrives:
            // tools may already be running while the user chooses another folder.
            if (!thread.Started)
            {
                thread.Started = true;
                thread.Directory = NormalizePath(request["cwd"]) ?? thread.Directory;
                thread.PreviousDirectories.Remove(thread.Directory);
            }

            return request;
        }
    }

    // Call only after a successful app-server response, with the original request
    // and the exact prepared request. Failed requests retain their retry mapping.
    public void ObserveResponse(string method, JToken? originalParameters, JToken? preparedParameters, JToken? result)
    {
        lock (_sync)
        {
            if (method == "thread/start")
            {
                var threadId = result?["thread"]?["id"]?.Value<string>();
                var preparedDirectory = NormalizePath(preparedParameters?["cwd"]);
                if (string.IsNullOrWhiteSpace(threadId) || preparedDirectory is null || _threads.ContainsKey(threadId!))
                {
                    return;
                }

                var thread = new NewThread(preparedDirectory);
                var originalDirectory = NormalizePath(originalParameters?["cwd"]);
                if (originalDirectory is not null && !PathsEqual(originalDirectory, preparedDirectory))
                {
                    thread.PreviousDirectories.Add(originalDirectory);
                }

                // A start/prewarm request may have been sent before the folder switch
                // and returned afterwards. Its first turn still belongs to the new folder.
                if (_directory is not null && _previousDirectories.Contains(preparedDirectory))
                {
                    RebaseThread(thread, _directory);
                }

                _threads.Add(threadId!, thread);
            }
            else if (method == "turn/start" && result?["turn"] is JObject)
            {
                var threadId = preparedParameters?["threadId"]?.Value<string>();
                if (threadId is not null && _threads.TryGetValue(threadId, out var thread)
                    && thread.Started && thread.PreviousDirectories.Count == 0)
                {
                    _threads.Remove(threadId);
                }
            }
        }
    }

    private void RememberPreviousDirectory(string? directory)
    {
        var path = NormalizePath(directory);
        if (path is not null)
        {
            _previousDirectories.Add(path);
        }
    }

    private static void RebaseThread(NewThread thread, string directory)
    {
        if (!PathsEqual(thread.Directory, directory))
        {
            thread.PreviousDirectories.Add(thread.Directory);
            thread.Directory = directory;
        }

        thread.PreviousDirectories.Remove(directory);
    }

    private static void ReplaceRoots(JObject request, HashSet<string> previousDirectories, string directory)
    {
        ReplaceRootArray(request["workspaceRoots"], previousDirectories, directory);
        ReplaceRootArray((request["sandboxPolicy"] as JObject)?["writableRoots"], previousDirectories, directory);
        ReplaceRootArray(request["permissions"] is JObject permissions
            ? (permissions["sandboxPolicy"] as JObject)?["writableRoots"] : null, previousDirectories, directory);
        if (request["config"] is JObject config)
        {
            ReplaceRootArray(config["sandbox_workspace_write.writable_roots"], previousDirectories, directory);
            ReplaceRootArray((config["sandbox_workspace_write"] as JObject)?["writable_roots"], previousDirectories, directory);
        }
    }

    private static void ReplaceRootArray(JToken? token, HashSet<string> previousDirectories, string directory)
    {
        if (token is not JArray roots)
        {
            return;
        }

        for (var i = 0; i < roots.Count; i++)
        {
            var root = NormalizePath(roots[i]);
            if (root is not null && previousDirectories.Contains(root))
            {
                roots[i] = directory;
            }
        }
    }

    private static string? NormalizePath(JToken? token)
    {
        return token?.Type == JTokenType.String ? NormalizePath(token.Value<string>()) : null;
    }

    private static string? NormalizePath(string? directory)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(directory) && Path.IsPathRooted(directory!)
                ? CodexWorkingDirectory.Resolve(directory) : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    private static bool PathsEqual(string first, string second)
    {
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NewThread
    {
        public NewThread(string directory) { Directory = directory; }
        public string Directory { get; set; }
        public bool Started { get; set; }
        public HashSet<string> PreviousDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
