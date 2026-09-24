using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>
/// Copies only VSAI-originated rollouts into an isolated Codex home through two already
/// initialized app-servers.  The source rollout is never deleted; archiving happens only
/// after a target read proves the fork retained the complete turn history.
/// </summary>
internal static class CodexSessionMigration
{
    private const int PageSize = 100;
    private const int MaximumPagesPerArchiveState = 100;
    private const int MaximumThreads = 10000;
    private const long MaximumRolloutBytes = 32L * 1024 * 1024;
    private const string ManifestFileName = "session-migration-manifest.json";
    private static readonly string[] VolatileTurnProperties =
        { "id", "threadId", "sessionId", "createdAt", "updatedAt" };

    internal static async Task<bool> MigrateAsync(
        string sharedHome,
        string privateHome,
        Func<string, JToken?, CancellationToken, Task<JToken?>> sendSource,
        Func<string, JToken?, CancellationToken, Task<JToken?>> sendTarget,
        CancellationToken token)
    {
        if (sendSource is null) throw new ArgumentNullException(nameof(sendSource));
        if (sendTarget is null) throw new ArgumentNullException(nameof(sendTarget));

        var sourceRoot = NormalizeDirectory(sharedHome, nameof(sharedHome));
        var targetRoot = NormalizeDirectory(privateHome, nameof(privateHome));
        ValidateHomeRoots(sourceRoot, targetRoot);

        EnsureNoReparsePoint(sourceRoot, sourceRoot);
        Directory.CreateDirectory(targetRoot);
        EnsureNoReparsePoint(targetRoot, targetRoot);

        var manifestPath = Path.Combine(targetRoot, ManifestFileName);
        var manifest = LoadManifest(manifestPath);
        var records = (JArray)manifest["records"]!;
        var discovered = new Dictionary<string, DiscoveredThread>(StringComparer.Ordinal);

        foreach (var archived in new[] { false, true })
        {
            string? cursor = null;
            var reachedEnd = false;
            for (var page = 0; page < MaximumPagesPerArchiveState; page++)
            {
                token.ThrowIfCancellationRequested();
                var request = new JObject
                {
                    ["archived"] = archived,
                    ["cursor"] = cursor is null ? (JToken)JValue.CreateNull() : new JValue(cursor),
                    ["limit"] = PageSize,
                    ["modelProviders"] = new JArray(),
                    ["sourceKinds"] = new JArray()
                };
                var response = await sendSource("thread/list", request, token).ConfigureAwait(false);
                var rows = GetThreadRows(response);
                foreach (var row in rows)
                {
                    if (row is not JObject rowObject) continue;
                    var id = rowObject["id"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(id) || discovered.ContainsKey(id!)) continue;
                    if (discovered.Count >= MaximumThreads)
                        throw new InvalidOperationException("Migration stopped after reaching the safe thread discovery limit.");
                    discovered.Add(id!, new DiscoveredThread((JObject)rowObject.DeepClone(), archived));
                }

                cursor = response?["nextCursor"]?.Value<string>() ?? response?["cursor"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(cursor))
                {
                    reachedEnd = true;
                    break;
                }
            }
            if (!reachedEnd)
                throw new InvalidOperationException("Migration stopped after reaching the safe thread page limit before discovery completed.");
        }

        // Older CLI versions can omit empty rollouts or report the original metadata
        // provider after a desktop session switched providers. Supplement from the
        // read-only local catalog; each rollout is still validated and verified below.
        foreach (var candidate in CodexSessionCatalog.ReadVsaiCandidates(sourceRoot))
        {
            var id = candidate["id"]!.Value<string>()!;
            if (discovered.TryGetValue(id, out var existing))
            {
                existing.Thread["vsaiCatalogOrigin"] = true;
                var persistedProvider = candidate["modelProvider"]?.Value<string>();
                if (persistedProvider?.StartsWith("vsai_", StringComparison.Ordinal) == true)
                    existing.Thread["modelProvider"] = persistedProvider;
            }
            else
            {
                if (discovered.Count >= MaximumThreads)
                    throw new InvalidOperationException("Migration stopped after reaching the safe thread discovery limit.");
                discovered.Add(id, new DiscoveredThread(candidate, candidate["archived"]?.Value<bool>() == true));
            }
        }

        // Listing must finish before any source archive.  An archive can change pagination and
        // can cascade to children, so mutations only begin after this complete snapshot.
        foreach (var item in discovered.Values)
        {
            token.ThrowIfCancellationRequested();
            await MigrateThreadAsync(sourceRoot, targetRoot, item.Thread, item.Archived, records, manifestPath,
                sendSource, sendTarget, token).ConfigureAwait(false);
        }
        await ArchiveVerifiedSourcesAsync(sourceRoot, discovered, records, manifestPath, sendSource, token)
            .ConfigureAwait(false);

        var complete = records.OfType<JObject>().All(record => string.Equals(record["state"]?.Value<string>(), "completed", StringComparison.Ordinal));
        manifest = BuildManifest(records);
        manifest["complete"] = complete;
        manifest["completedAt"] = complete ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : (JToken)JValue.CreateNull();
        manifest["discoveredThreadCount"] = discovered.Count;
        manifest["processedThreadCount"] = records.Count;
        manifest["diagnostic"] = complete ? (JToken)JValue.CreateNull()
            : "Some VSAI sessions remain unarchived or need manual recovery; inspect records and retry without changing the source home.";
        WriteManifest(manifestPath, manifest);
        return complete;
    }

    internal static bool HasCompletedMigration(string sharedHome, string privateHome)
    {
        try
        {
            var sourceRoot = NormalizeDirectory(sharedHome, nameof(sharedHome));
            var targetRoot = NormalizeDirectory(privateHome, nameof(privateHome));
            ValidateHomeRoots(sourceRoot, targetRoot);
            if (!File.Exists(Path.Combine(targetRoot, ManifestFileName))) return false;
            EnsureNoReparsePoint(targetRoot, targetRoot);
            var manifest = LoadManifest(Path.Combine(targetRoot, ManifestFileName));
            return manifest["complete"]?.Value<bool>() == true
                && manifest["completedAt"]?.Type == JTokenType.Integer
                && manifest["records"] is JArray records
                && records.OfType<JObject>().All(record => string.Equals(record["state"]?.Value<string>(), "completed", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task MigrateThreadAsync(
        string sharedHome,
        string privateHome,
        JObject listThread,
        bool archived,
        JArray records,
        string manifestPath,
        Func<string, JToken?, CancellationToken, Task<JToken?>> sendSource,
        Func<string, JToken?, CancellationToken, Task<JToken?>> sendTarget,
        CancellationToken token)
    {
        var sourceId = listThread["id"]?.Value<string>()
            ?? throw new InvalidOperationException("A listed thread did not include its id.");
        var record = FindRecord(records, sourceId);
        var originallyArchived = record?["sourceArchived"]?.Value<bool>() ?? archived;
        if (string.Equals(record?["state"]?.Value<string>(), "completed", StringComparison.Ordinal)) return;
        if (string.Equals(record?["state"]?.Value<string>(), "fork-outcome-unknown", StringComparison.Ordinal)) return;

        try
        {
            var path = listThread["path"]?.Value<string>();
            JToken? sourceRead = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                sourceRead = await RequireThreadAsync(sendSource, "thread/read", sourceId, token).ConfigureAwait(false);
                path = GetThread(sourceRead)["path"]?.Value<string>();
            }
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Source thread " + sourceId + " has no rollout path after thread/read; migration cannot safely fork it.");
            var listProvider = listThread["modelProvider"]?.Value<string>();
            var isVsaiProvider = !string.IsNullOrWhiteSpace(listProvider)
                && listProvider!.StartsWith("vsai_", StringComparison.Ordinal);
            var isCatalogOrigin = listThread["vsaiCatalogOrigin"]?.Value<bool>() == true;
            if (isVsaiProvider || isCatalogOrigin) record = record ?? CreateRecord(records, sourceId);
            var sourcePath = ValidateSourceRolloutPath(sharedHome, path!, archived);
            var bytes = ReadBoundedFile(sourcePath);
            var sourceHash = ComputeHash(bytes);
            var metadata = ReadRolloutMetadata(bytes, isVsaiProvider || isCatalogOrigin);
            if (!isVsaiProvider && !isCatalogOrigin && !metadata.IsVsaiOrigin && !metadata.IsVsaiProvider)
                return;
            record = record ?? CreateRecord(records, sourceId);
            if (IsActive(listThread))
            {
                record = record ?? CreateRecord(records, sourceId);
                record["state"] = "skipped-active";
                record["lastError"] = "Source thread is active and was not migrated.";
                WriteManifest(manifestPath, BuildManifest(records));
                return;
            }
            if (metadata.HasIncompleteTask)
            {
                record = record ?? CreateRecord(records, sourceId);
                record["state"] = "skipped-incomplete";
                record["lastError"] = "Source rollout has an unfinished task or turn.";
                WriteManifest(manifestPath, BuildManifest(records));
                return;
            }

            if (metadata.HasOnlySetupEntries && !metadata.HasHistoryBase && string.Equals(metadata.SourceId, sourceId, StringComparison.Ordinal))
            {
                record["sourcePath"] = sourcePath;
                record["sourceHash"] = sourceHash;
                record["backupPath"] = WriteBackup(privateHome, sourceId, sourceHash, bytes);
                record["sourceArchived"] = originallyArchived;
                record["disposition"] = "empty-rollout-backup-only";
                record["sourceTurnCount"] = 0;
                record["state"] = "completed";
                record.Remove("lastError");
                WriteManifest(manifestPath, BuildManifest(records));
                return;
            }

            sourceRead = sourceRead ?? await RequireThreadAsync(sendSource, "thread/read", sourceId, token).ConfigureAwait(false);
            if (IsActive(GetThread(sourceRead)))
            {
                record = record ?? CreateRecord(records, sourceId);
                record["state"] = "skipped-active";
                record["lastError"] = "Source thread became active while being inspected.";
                WriteManifest(manifestPath, BuildManifest(records));
                return;
            }

            var sourceThread = GetThread(sourceRead);
            var turns = RequireTurns(sourceThread, "source", sourceId);
            // thread/list carries the persisted database provider. Preserve it even when
            // session metadata or thread/read reports an ordinary provider such as openai.
            var provider = listProvider ?? sourceThread["modelProvider"]?.Value<string>() ?? string.Empty;
            var model = metadata.LastModel;
            var name = listThread["name"]?.Value<string>() ?? sourceThread["name"]?.Value<string>();
            var cwd = sourceThread["cwd"]?.Value<string>();
            var backupPath = WriteBackup(privateHome, sourceId, sourceHash, bytes);
            var dependencyBackups = new JArray();
            var materializedBytes = await CodexRolloutImportSnapshot.MaterializeAsync(sourceId, bytes, async parentId =>
            {
                var parent = GetThread(await RequireThreadAsync(sendSource, "thread/read", parentId, token).ConfigureAwait(false));
                var parentPath = parent["path"]?.Value<string>()
                    ?? throw new InvalidOperationException("A history dependency has no rollout path.");
                var parentArchived = IsWithinDirectory(GetCanonicalFullPath(parentPath), Path.Combine(sharedHome, "archived_sessions"));
                var parentBytes = ReadBoundedFile(ValidateSourceRolloutPath(sharedHome, parentPath, parentArchived));
                var parentHash = ComputeHash(parentBytes);
                var parentMetadata = ReadRolloutMetadata(parentBytes, strictForKnownVsai: false);
                dependencyBackups.Add(new JObject
                {
                    ["threadId"] = parentId,
                    ["sourceHash"] = parentHash,
                    ["sourcePath"] = parentPath,
                    ["isVsai"] = parentMetadata.IsVsaiOrigin || parentMetadata.IsVsaiProvider,
                    ["modelProvider"] = parent["modelProvider"]?.DeepClone() ?? new JValue("openai"),
                    ["model"] = parentMetadata.LastModel,
                    ["backupPath"] = WriteBackup(privateHome, parentId, parentHash, parentBytes)
                });
                return parentBytes;
            }, token, preserveHistoryMode: metadata.IsPaginated).ConfigureAwait(false);
            // Native paginated ordinals and byte offsets refer to the original
            // parent files. Preserve that graph byte-for-byte; flatten only legacy
            // history. Materialization above also validates and backs up every base.
            var importBytes = metadata.IsPaginated ? bytes : materializedBytes;
            var importPath = WriteBackup(privateHome, sourceId + "-import", ComputeHash(importBytes), importBytes);

            record["sourcePath"] = sourcePath;
            record["sourceHash"] = sourceHash;
            record["backupPath"] = backupPath;
            record["importPath"] = importPath;
            record["historyDependencyBackups"] = dependencyBackups;
            record["sourceArchived"] = originallyArchived;
            record["modelProvider"] = provider;
            if (!string.IsNullOrWhiteSpace(model)) record["model"] = model;
            if (!string.IsNullOrWhiteSpace(name)) record["sourceName"] = name;
            record["sourceTurnCount"] = turns.Count;
            record["sourceTurnsHash"] = ComputeTurnHash(turns);
            record["parentThreadIds"] = GetParentThreadIds(listThread);

            var targetId = record["targetThreadId"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(targetId) && string.Equals(record["state"]?.Value<string>(), "forking", StringComparison.Ordinal))
            {
                record["state"] = "fork-outcome-unknown";
                record["lastError"] = "The previous fork request has no recorded target id. Verify the target manually before retrying; the source was not archived.";
                WriteManifest(manifestPath, BuildManifest(records));
                return;
            }

            if (metadata.IsPaginated)
            {
                foreach (var dependency in dependencyBackups.OfType<JObject>().Reverse())
                {
                    var dependencyId = dependency["threadId"]!.Value<string>()!;
                    JToken? existingDependency = null;
                    try { existingDependency = await sendTarget("thread/read", new JObject { ["threadId"] = dependencyId, ["includeTurns"] = false }, token).ConfigureAwait(false); }
                    catch (InvalidOperationException ex) when (ex.Message.IndexOf("no rollout found", StringComparison.OrdinalIgnoreCase) >= 0
                        || ex.Message.IndexOf("thread not loaded:", StringComparison.OrdinalIgnoreCase) >= 0) { }
                    var dependencyPath = (existingDependency?["thread"] as JObject)?["path"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(dependencyPath))
                        dependencyPath = StagePaginatedRollout(privateHome, dependencyId, dependency["sourcePath"]!.Value<string>()!, ReadBoundedFile(dependency["backupPath"]!.Value<string>()!));
                    var dependencyArchived = IsWithinDirectory(GetCanonicalFullPath(dependencyPath!), Path.Combine(privateHome, "archived_sessions"));
                    ValidateSourceRolloutPath(privateHome, dependencyPath!, dependencyArchived);
                    var dependencyResume = new JObject
                    {
                        ["threadId"] = dependencyId, ["path"] = dependencyPath, ["excludeTurns"] = true,
                        ["modelProvider"] = dependency["modelProvider"]?.DeepClone()
                    };
                    if (!string.IsNullOrWhiteSpace(dependency["model"]?.Value<string>())) dependencyResume["model"] = dependency["model"]!.DeepClone();
                    await sendTarget("thread/resume", dependencyResume, token).ConfigureAwait(false);
                }
                // Native paginated item_completed events are discarded by a legacy
                // fork. Stage the original snapshot and its native parent references
                // in the isolated home; resume rebuilds projections under the same id.
                // No goals/queue databases are copied and no turn is started.
                var stagedPath = record["stagedPath"]?.Value<string>()
                    ?? StagePaginatedRollout(privateHome, sourceId, sourcePath, importBytes);
                ValidateSourceRolloutPath(privateHome, stagedPath, archived: false);
                if (!string.IsNullOrWhiteSpace(targetId) && !string.Equals(targetId, sourceId, StringComparison.Ordinal))
                    throw new InvalidOperationException("A previous fork must be reconciled before importing native paginated history.");
                targetId = sourceId;
                record["stagedPath"] = stagedPath;
                record["targetThreadId"] = targetId;
                record["importStrategy"] = "native-paginated-v1";
                record["state"] = "restoring";
                WriteManifest(manifestPath, BuildManifest(records));
                var resume = new JObject
                {
                    ["threadId"] = targetId,
                    ["path"] = stagedPath,
                    ["excludeTurns"] = true,
                    ["modelProvider"] = string.IsNullOrWhiteSpace(provider) ? (JToken)JValue.CreateNull() : new JValue(provider)
                };
                if (!string.IsNullOrWhiteSpace(model)) resume["model"] = model;
                if (!string.IsNullOrWhiteSpace(cwd)) resume["cwd"] = cwd;
                var restored = await sendTarget("thread/resume", resume, token).ConfigureAwait(false);
                if (!string.Equals(GetThread(restored)["id"]?.Value<string>(), targetId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Native history import returned an unexpected thread id.");
                record["state"] = "restored";
                WriteManifest(manifestPath, BuildManifest(records));
            }
            else if (string.IsNullOrWhiteSpace(targetId))
            {
                record["state"] = "forking";
                record.Remove("lastError");
                WriteManifest(manifestPath, BuildManifest(records));
                var fork = new JObject
                {
                    ["threadId"] = sourceId,
                    ["path"] = importPath,
                    ["excludeTurns"] = true,
                    ["deferGoalContinuation"] = true,
                    ["ephemeral"] = false,
                    ["modelProvider"] = string.IsNullOrWhiteSpace(provider) ? (JToken)JValue.CreateNull() : new JValue(provider)
                };
                if (!string.IsNullOrWhiteSpace(model)) fork["model"] = model;
                if (!string.IsNullOrWhiteSpace(cwd)) fork["cwd"] = cwd;
                var forked = await sendTarget("thread/fork", fork, token).ConfigureAwait(false);
                targetId = GetThread(forked)["id"]?.Value<string>() ?? forked?["threadId"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(targetId))
                    throw new InvalidOperationException("Target thread/fork did not return a target thread id.");
                record["targetThreadId"] = targetId;
                record["state"] = "forked";
                WriteManifest(manifestPath, BuildManifest(records));
            }

            var targetRead = await RequireThreadAsync(sendTarget, "thread/read", targetId!, token).ConfigureAwait(false);
            var targetThread = GetThread(targetRead);
            if (targetThread["path"]?.Value<string>() is string targetPath)
            {
                var targetArchived = IsWithinDirectory(GetCanonicalFullPath(targetPath), Path.Combine(privateHome, "archived_sessions"));
                ValidateSourceRolloutPath(privateHome, targetPath, targetArchived);
            }
            var targetTurns = RequireTurns(targetThread, "target", targetId!);
            VerifyTurns(turns, targetTurns, sourceId, targetId!);
            record["targetTurnCount"] = targetTurns.Count;
            record["targetTurnsHash"] = ComputeTurnHash(targetTurns);
            record["state"] = "verified";
            record.Remove("lastError");
            WriteManifest(manifestPath, BuildManifest(records));

            if (!string.IsNullOrWhiteSpace(name))
                await sendTarget("thread/name/set", new JObject { ["threadId"] = targetId, ["name"] = name }, token).ConfigureAwait(false);
            if (metadata.IsPaginated)
            {
                foreach (var dependency in dependencyBackups.OfType<JObject>().Where(item => item["isVsai"]?.Value<bool>() != true))
                    await sendTarget("thread/archive", new JObject { ["threadId"] = dependency["threadId"]!.Value<string>() }, token).ConfigureAwait(false);
            }
            if (originallyArchived)
            {
                await sendTarget("thread/archive", new JObject { ["threadId"] = targetId }, token).ConfigureAwait(false);
                record["targetArchived"] = true;
                record["state"] = "completed";
                record.Remove("lastError");
            }
            else
            {
                // Source archiving is deliberately deferred until every discovered descendant
                // has been proved migrated. thread/archive can cascade to child threads.
                record["state"] = "verified";
            }
            WriteManifest(manifestPath, BuildManifest(records));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unreadable non-VSAI rollout cannot be classified as a VSAI session.
            // Leave it alone and continue scanning other threads.
            var listedProvider = listThread["modelProvider"]?.Value<string>();
            if (record is null && ex is not KnownVsaiRolloutException && (string.IsNullOrWhiteSpace(listedProvider)
                || !listedProvider!.StartsWith("vsai_", StringComparison.Ordinal))) return;
            record = record ?? CreateRecord(records, sourceId);
            if (string.Equals(record["state"]?.Value<string>(), "forking", StringComparison.Ordinal))
                record["state"] = "fork-outcome-unknown";
            else if (!string.Equals(record["state"]?.Value<string>(), "fork-outcome-unknown", StringComparison.Ordinal))
                record["state"] = "failed";
            record["lastError"] = ex.Message;
            WriteManifest(manifestPath, BuildManifest(records));
            // Continue so a damaged rollout cannot prevent migration of unrelated VSAI sessions.
        }
    }

    private static async Task<JToken> RequireThreadAsync(Func<string, JToken?, CancellationToken, Task<JToken?>> send,
        string method, string id, CancellationToken token)
    {
        var response = await send(method, new JObject { ["threadId"] = id, ["includeTurns"] = true }, token).ConfigureAwait(false);
        if (response is null) throw new InvalidOperationException(method + " returned no response for " + id + ".");
        return response;
    }

    private static async Task ArchiveVerifiedSourcesAsync(
        string sharedHome,
        IDictionary<string, DiscoveredThread> discovered,
        JArray records,
        string manifestPath,
        Func<string, JToken?, CancellationToken, Task<JToken?>> sendSource,
        CancellationToken token)
    {
        var children = BuildChildMap(discovered);
        foreach (var record in records.OfType<JObject>()
            .OrderBy(record => GetDescendantDepth(record["sourceThreadId"]?.Value<string>() ?? string.Empty, children, new HashSet<string>(StringComparer.Ordinal)))
            .ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(record["state"]?.Value<string>(), "verified", StringComparison.Ordinal)
                || record["sourceArchived"]?.Value<bool>() == true)
                continue;

            var sourceId = record["sourceThreadId"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(sourceId)) continue;
            var pendingDescendant = FindUnprovenDescendant(sourceId!, children, records);
            if (!string.IsNullOrWhiteSpace(pendingDescendant))
            {
                record["lastError"] = "Source archive deferred because descendant " + pendingDescendant
                    + " was not proven migrated. The source remains unchanged.";
                WriteManifest(manifestPath, BuildManifest(records));
                continue;
            }

            try
            {
                var path = record["sourcePath"]?.Value<string>()
                    ?? throw new InvalidOperationException("Verified source record has no source path.");
                var expectedHash = record["sourceHash"]?.Value<string>()
                    ?? throw new InvalidOperationException("Verified source record has no source hash.");
                var currentSource = GetThread(await RequireThreadAsync(sendSource, "thread/read", sourceId!, token).ConfigureAwait(false));
                if (IsActive(currentSource))
                    throw new InvalidOperationException("Source thread became active after migration verification; the source was not archived.");
                if (!string.Equals(record["sourceTurnsHash"]?.Value<string>(), ComputeTurnHash(RequireTurns(currentSource, "source", sourceId!)), StringComparison.Ordinal))
                    throw new InvalidOperationException("Source history or its parent dependency changed after target verification; the source was not archived.");
                path = currentSource["path"]?.Value<string>() ?? path;
                var alreadyArchived = IsWithinDirectory(GetCanonicalFullPath(path), Path.Combine(sharedHome, "archived_sessions"));
                var currentHash = ComputeHash(ReadBoundedFile(ValidateSourceRolloutPath(sharedHome, path, archived: alreadyArchived)));
                if (!string.Equals(expectedHash, currentHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("Source rollout changed after target verification; the source was not archived.");
                if (!alreadyArchived)
                    await sendSource("thread/archive", new JObject { ["threadId"] = sourceId }, token).ConfigureAwait(false);
                record["sourceArchivedAfterMigration"] = true;
                record["state"] = "completed";
                record.Remove("lastError");
                WriteManifest(manifestPath, BuildManifest(records));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                record["lastError"] = ex.Message;
                WriteManifest(manifestPath, BuildManifest(records));
            }
        }
    }

    private static Dictionary<string, List<string>> BuildChildMap(IDictionary<string, DiscoveredThread> discovered)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in discovered)
        {
            foreach (var parentId in GetParentThreadIds(pair.Value.Thread).Values<string>())
            {
                if (string.IsNullOrWhiteSpace(parentId)) continue;
                if (!result.TryGetValue(parentId!, out var childIds))
                {
                    childIds = new List<string>();
                    result.Add(parentId!, childIds);
                }
                childIds.Add(pair.Key);
            }
        }
        return result;
    }

    private static string? FindUnprovenDescendant(string sourceId, IDictionary<string, List<string>> children, JArray records)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceId };
        pending.Enqueue(sourceId);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            if (!children.TryGetValue(parent, out var childIds)) continue;
            foreach (var childId in childIds)
            {
                if (!visited.Add(childId)) continue;
                var child = FindRecord(records, childId);
                if (!string.Equals(child?["state"]?.Value<string>(), "completed", StringComparison.Ordinal))
                    return childId;
                pending.Enqueue(childId);
            }
        }
        return null;
    }

    private static int GetDescendantDepth(string sourceId, IDictionary<string, List<string>> children, ISet<string> visited)
    {
        if (!visited.Add(sourceId) || !children.TryGetValue(sourceId, out var childIds)) return 0;
        var maximum = 0;
        foreach (var childId in childIds)
            maximum = Math.Max(maximum, 1 + GetDescendantDepth(childId, children, visited));
        visited.Remove(sourceId);
        return maximum;
    }

    private static JArray GetParentThreadIds(JObject thread)
    {
        var parents = new JArray();
        AddParentId(parents, thread["forkedFromId"]?.Value<string>());
        AddParentId(parents, thread["parentThreadId"]?.Value<string>());
        if (thread["source"] is JObject source
            && source["subAgent"] is JObject subAgent
            && subAgent["thread_spawn"] is JObject threadSpawn)
            AddParentId(parents, threadSpawn["parent_thread_id"]?.Value<string>());
        return parents;
    }

    private static void AddParentId(JArray parents, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !parents.Values<string>().Contains(value, StringComparer.Ordinal))
            parents.Add(value);
    }

    private static JArray GetThreadRows(JToken? response)
    {
        if (response is not JObject value || value["data"] is not JArray rows)
            throw new InvalidOperationException("thread/list did not return its required data array.");
        return rows;
    }

    private static JObject GetThread(JToken? response)
    {
        if (response?["thread"] is JObject thread) return thread;
        if (response is JObject value) return value;
        throw new InvalidOperationException("The app-server response did not contain a thread.");
    }

    private static JArray RequireTurns(JObject thread, string side, string id)
    {
        if (thread["turns"] is not JArray turns)
            throw new InvalidOperationException("The " + side + " thread/read response did not include turns for " + id + ".");
        return turns;
    }

    private static void VerifyTurns(JArray source, JArray target, string sourceId, string targetId)
    {
        if (source.Count != target.Count || !JToken.DeepEquals(NormalizeTurns(source), NormalizeTurns(target)))
            throw new InvalidOperationException("Target " + targetId + " did not retain the complete verified turn history from " + sourceId + ".");
    }

    private static JToken NormalizeTurns(JToken value)
    {
        if (value is JArray array) return new JArray(array.Select(NormalizeTurnEnvelope));
        return NormalizeTurnEnvelope(value);
    }

    private static JToken NormalizeTurnEnvelope(JToken value)
    {
        if (value is not JObject turn) return value.DeepClone();
        var normalized = (JObject)turn.DeepClone();
        RemoveVolatileEnvelopeProperties(normalized);
        if (normalized["items"] is JArray items)
            normalized["items"] = new JArray(items.Select(NormalizeItemEnvelope));
        return normalized;
    }

    private static JToken NormalizeItemEnvelope(JToken value)
    {
        if (value is not JObject item) return value.DeepClone();
        var normalized = (JObject)item.DeepClone();
        RemoveVolatileEnvelopeProperties(normalized);
        // Deliberately do not recurse: nested payload/content fields, including file paths,
        // are part of the preserved conversation and must compare exactly.
        return normalized;
    }

    private static void RemoveVolatileEnvelopeProperties(JObject envelope)
    {
        foreach (var property in VolatileTurnProperties)
            envelope.Remove(property);
    }

    private static string ValidateSourceRolloutPath(string sharedHome, string path, bool archived)
    {
        var portablePath = NormalizeWin32ExtendedPathPrefix(path);
        if (!Path.IsPathRooted(portablePath)) throw new InvalidOperationException("Source rollout path must be absolute.");
        var fullPath = GetCanonicalFullPath(portablePath);
        var allowedRoot = Path.Combine(sharedHome, archived ? "archived_sessions" : "sessions");
        if (!IsWithinDirectory(fullPath, allowedRoot))
            throw new InvalidOperationException("Source rollout path is outside the allowed session root.");
        EnsureNoReparsePoint(sharedHome, fullPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Source rollout does not exist.", fullPath);
        return fullPath;
    }

    private static byte[] ReadBoundedFile(string path)
    {
        // An idle desktop thread can retain a writer handle. Read a bounded snapshot;
        // active-turn checks and the final pre-archive hash still prevent moving a
        // thread whose history changed during migration.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length < 0 || length > MaximumRolloutBytes)
            throw new InvalidOperationException("Source rollout exceeds the safe migration size limit.");
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new IOException("Source rollout changed while its migration snapshot was being read.");
            offset += read;
        }
        if (stream.Length != length)
            throw new IOException("Source rollout changed while its migration snapshot was being read.");
        return bytes;
    }

    private static string WriteBackup(string privateHome, string sourceId, string hash, byte[] bytes)
    {
        var directory = Path.Combine(privateHome, "migration-backups", SafeFilePart(sourceId));
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoint(privateHome, directory);
        var destination = Path.Combine(directory, hash + ".jsonl");
        if (File.Exists(destination))
        {
            if (!string.Equals(ComputeHash(ReadBoundedFile(destination)), hash, StringComparison.Ordinal))
                throw new InvalidOperationException("Existing migration backup does not match its recorded hash.");
            return destination;
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string StagePaginatedRollout(string privateHome, string sourceId, string sourcePath, byte[] bytes)
    {
        var directory = Path.Combine(privateHome, "sessions", DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), DateTime.UtcNow.ToString("dd"));
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoint(privateHome, directory);
        var name = Path.GetFileName(sourcePath);
        if (!name.StartsWith("rollout-", StringComparison.Ordinal) || !name.EndsWith("-" + sourceId + ".jsonl", StringComparison.Ordinal))
            name = "rollout-" + DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss") + "-" + SafeFilePart(sourceId) + ".jsonl";
        var path = Path.Combine(directory, name);
        if (File.Exists(path))
        {
            EnsureNoReparsePoint(privateHome, path);
            if (!string.Equals(ComputeHash(ReadBoundedFile(path)), ComputeHash(bytes), StringComparison.Ordinal))
                throw new InvalidOperationException("An existing private rollout conflicts with this import; it was not overwritten.");
            return path;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    private static RolloutMetadata ReadRolloutMetadata(byte[] bytes, bool strictForKnownVsai)
    {
        var metadata = new RolloutMetadata();
        var text = Encoding.UTF8.GetString(bytes);
        var activeTurnIds = new HashSet<string>(StringComparer.Ordinal);
        var hasUnnamedActiveTurn = false;
        foreach (var rawLine in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            JObject entry;
            try { entry = JObject.Parse(rawLine.TrimStart('\ufeff')); }
            catch (JsonException ex)
            {
                metadata.HasOnlySetupEntries = false;
                if (strictForKnownVsai || metadata.IsVsaiOrigin || metadata.IsVsaiProvider)
                    throw new KnownVsaiRolloutException("Known VSAI rollout contains malformed JSON.", ex);
                continue;
            }
            var envelopeType = entry["type"]?.Value<string>() ?? string.Empty;
            if (envelopeType != "session_meta" && envelopeType != "turn_context") metadata.HasOnlySetupEntries = false;
            var payload = entry["payload"] as JObject;
            var type = string.Equals(envelopeType, "event_msg", StringComparison.Ordinal)
                ? payload?["type"]?.Value<string>() ?? string.Empty
                : envelopeType;
            if (!metadata.SeenSessionMeta && string.Equals(type, "session_meta", StringComparison.Ordinal))
            {
                metadata.SeenSessionMeta = true;
                metadata.SourceId = payload?["id"]?.Value<string>();
                metadata.IsPaginated = string.Equals(payload?["history_mode"]?.Value<string>(), "paginated", StringComparison.Ordinal);
                metadata.HasHistoryBase = payload?["history_base"] is JObject;
                var originator = entry["payload"]?["originator"]?.Value<string>() ?? entry["originator"]?.Value<string>();
                metadata.IsVsaiOrigin = string.Equals(originator, "codex-vsix", StringComparison.OrdinalIgnoreCase);
            }
            var metadataProvider = payload?["model_provider"]?.Value<string>()
                ?? payload?["modelProvider"]?.Value<string>()
                ?? entry["model_provider"]?.Value<string>()
                ?? entry["modelProvider"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(metadataProvider)
                && metadataProvider!.StartsWith("vsai_", StringComparison.Ordinal))
                metadata.IsVsaiProvider = true;
            if (string.Equals(type, "turn_context", StringComparison.Ordinal))
            {
                var model = entry["payload"]?["model"]?.Value<string>() ?? entry["model"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(model)) metadata.LastModel = model;
            }
            var turnId = payload?["turn_id"]?.Value<string>()
                ?? payload?["turnId"]?.Value<string>()
                ?? entry["turn_id"]?.Value<string>()
                ?? entry["turnId"]?.Value<string>();
            if (IsTaskStart(type))
            {
                if (string.IsNullOrWhiteSpace(turnId)) hasUnnamedActiveTurn = true;
                else activeTurnIds.Add(turnId!);
            }
            else if (IsTaskCompletion(type))
            {
                if (string.IsNullOrWhiteSpace(turnId)) hasUnnamedActiveTurn = false;
                else activeTurnIds.Remove(turnId!);
            }
        }
        metadata.HasIncompleteTask = hasUnnamedActiveTurn || activeTurnIds.Count != 0;
        return metadata;
    }

    private static bool IsTaskStart(string type)
        => string.Equals(type, "turn/started", StringComparison.Ordinal)
            || string.Equals(type, "task_started", StringComparison.Ordinal)
            || string.Equals(type, "codex/event/task_started", StringComparison.Ordinal)
            || string.Equals(type, "turn_started", StringComparison.Ordinal);

    private static bool IsTaskCompletion(string type)
        => string.Equals(type, "turn/completed", StringComparison.Ordinal)
            || string.Equals(type, "task_complete", StringComparison.Ordinal)
            || string.Equals(type, "codex/event/task_complete", StringComparison.Ordinal)
            || string.Equals(type, "turn_completed", StringComparison.Ordinal)
            || string.Equals(type, "turn/aborted", StringComparison.Ordinal)
            || string.Equals(type, "turn/failed", StringComparison.Ordinal)
            || string.Equals(type, "task_failed", StringComparison.Ordinal)
            || string.Equals(type, "turn_aborted", StringComparison.Ordinal)
            || string.Equals(type, "codex/event/task_failed", StringComparison.Ordinal);

    private static bool IsActive(JObject? thread)
    {
        var status = thread?["status"];
        var type = status is JObject objectStatus ? objectStatus["type"]?.Value<string>() : status?.Value<string>();
        return string.Equals(type, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "inProgress", StringComparison.OrdinalIgnoreCase);
    }

    private static JObject LoadManifest(string path)
    {
        if (!File.Exists(path)) return BuildManifest(new JArray());
        try
        {
            var value = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (value["records"] is not JArray) throw new InvalidDataException("Migration manifest records are missing.");
            return value;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Migration manifest is malformed; resolve it before retrying.", ex);
        }
    }

    private static JObject BuildManifest(JArray records) => new JObject
    {
        ["format"] = 1,
        ["records"] = records
    };

    private static void WriteManifest(string path, JObject manifest)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, NewtonsoftJsonCompatibility.Serialize(manifest, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static JObject? FindRecord(JArray records, string sourceId)
        => records.OfType<JObject>().FirstOrDefault(record => string.Equals(record["sourceThreadId"]?.Value<string>(), sourceId, StringComparison.Ordinal));

    private static JObject CreateRecord(JArray records, string sourceId)
    {
        var record = new JObject { ["sourceThreadId"] = sourceId, ["state"] = "discovered" };
        records.Add(record);
        return record;
    }

    private static void ValidateHomeRoots(string sharedHome, string privateHome)
    {
        if (string.Equals(sharedHome, privateHome, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The shared and private Codex homes must be separate directories.");

        // The private home is intentionally allowed to be sharedHome\\vsai.  It must never
        // overlap the old rollout roots, however, or a backup could be mistaken for a source.
        var sessions = Path.Combine(sharedHome, "sessions");
        var archivedSessions = Path.Combine(sharedHome, "archived_sessions");
        if (DirectoriesOverlap(privateHome, sessions) || DirectoriesOverlap(privateHome, archivedSessions))
            throw new InvalidOperationException("The private Codex home must not overlap shared session directories.");
        if (IsWithinDirectory(sharedHome, privateHome))
            throw new InvalidOperationException("The shared Codex home must not be contained by the private Codex home.");
    }

    private static bool DirectoriesOverlap(string first, string second)
        => string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
            || IsWithinDirectory(first, second)
            || IsWithinDirectory(second, first);

    private static string GetCanonicalFullPath(string path)
        => Path.GetFullPath(NormalizeWin32ExtendedPathPrefix(path));

    private static string NormalizeWin32ExtendedPathPrefix(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string extendedUncPrefix = @"\\?\UNC\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(extendedUncPrefix.Length);
        if (!path.StartsWith(extendedPrefix, StringComparison.Ordinal)) return path;

        var drivePath = path.Substring(extendedPrefix.Length);
        if (drivePath.Length >= 3
            && char.IsLetter(drivePath[0])
            && drivePath[1] == ':'
            && (drivePath[2] == Path.DirectorySeparatorChar || drivePath[2] == Path.AltDirectorySeparatorChar))
            return drivePath;
        throw new InvalidOperationException("Migration accepts only Win32 extended drive or UNC paths.");
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A directory is required.", parameterName);
        var fullPath = GetCanonicalFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = NormalizeDirectory(directory, nameof(directory));
        var root = normalizedDirectory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;
        var candidate = GetCanonicalFullPath(path);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoReparsePoint(string root, string path)
    {
        var normalizedRoot = NormalizeDirectory(root, nameof(root));
        var normalizedPath = GetCanonicalFullPath(path);
        if (!string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !IsWithinDirectory(normalizedPath, normalizedRoot))
            throw new InvalidOperationException("Path is outside its expected root.");
        CheckReparsePoint(normalizedRoot);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return;
        var relative = normalizedPath.Substring(normalizedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = normalizedRoot;
        foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            CheckReparsePoint(current);
        }
    }

    private static void CheckReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Migration does not follow reparse points: " + path);
    }

    private static string ComputeHash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string ComputeTurnHash(JArray turns)
        => ComputeHash(new UTF8Encoding(false).GetBytes(NewtonsoftJsonCompatibility.Serialize(NormalizeTurns(turns), Formatting.None)));

    private static string SafeFilePart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        return builder.Length == 0 ? "thread" : builder.ToString();
    }

    private sealed class DiscoveredThread
    {
        internal DiscoveredThread(JObject thread, bool archived)
        {
            Thread = thread;
            Archived = archived;
        }

        internal JObject Thread { get; }
        internal bool Archived { get; }
    }

    private sealed class RolloutMetadata
    {
        internal bool SeenSessionMeta { get; set; }
        internal bool IsVsaiOrigin { get; set; }
        internal bool IsVsaiProvider { get; set; }
        internal bool HasIncompleteTask { get; set; }
        internal string? LastModel { get; set; }
        internal string? SourceId { get; set; }
        internal bool HasOnlySetupEntries { get; set; } = true;
        internal bool IsPaginated { get; set; }
        internal bool HasHistoryBase { get; set; }
    }

    private sealed class KnownVsaiRolloutException : IOException
    {
        internal KnownVsaiRolloutException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
