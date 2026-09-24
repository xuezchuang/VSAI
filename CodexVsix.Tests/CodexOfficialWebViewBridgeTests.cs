using System;
using System.IO;
using System.Linq;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexOfficialWebViewBridgeTests
{
    [Fact]
    public void UsesCyberVinciNeutralFallbacksForChatGptWebEndpoints()
    {
        var tasks = Assert.IsType<JObject>(CodexOfficialWebViewBridge.TryBuildSyntheticFetchResponse(
            new Uri("https://chatgpt.com/wham/tasks/list?limit=20")));
        Assert.Empty(Assert.IsType<JArray>(tasks["items"]));
        Assert.Equal(JTokenType.Null, tasks["cursor"]?.Type);

        var statsig = Assert.IsType<JObject>(CodexOfficialWebViewBridge.TryBuildSyntheticFetchResponse(
            new Uri("https://chatgpt.com/ces/v1/rgstr?secret-is-not-forwarded")));
        Assert.False(statsig["has_updates"]?.Value<bool>());
        Assert.True(statsig["time"]?.Value<long>() > 0);
    }

    [Theory]
    [InlineData("https://chatgpt.com/wham/tasks/list/")]
    [InlineData("https://chatgpt.com/backend-api/wham/tasks/list?limit=20&task_filter=current")]
    public void TaskHistoryFallbackAcceptsSupportedChatGptUrlVariants(string url)
    {
        var tasks = Assert.IsType<JObject>(CodexOfficialWebViewBridge.TryBuildSyntheticFetchResponse(new Uri(url)));

        Assert.Empty(Assert.IsType<JArray>(tasks["items"]));
        Assert.Equal(JTokenType.Null, tasks["cursor"]?.Type);
    }

    [Fact]
    public void RelativeFetchUrlsResolveAgainstChatGptWithoutAcceptingLocalFileSchemes()
    {
        var resolved = CodexOfficialWebViewBridge.ResolveProxyUri("/wham/tasks/list?limit=20");

        Assert.Equal("https", resolved.Scheme);
        Assert.Equal("chatgpt.com", resolved.Host);
        Assert.Equal("/wham/tasks/list", resolved.AbsolutePath);
        Assert.Throws<InvalidOperationException>(() => CodexOfficialWebViewBridge.ResolveProxyUri("file:///C:/secrets.txt"));
    }

    [Theory]
    [InlineData("navigate-back")]
    [InlineData("navigate-forward")]
    public void HistoryNavigationMessagesAreRelayedToTheOfficialRouter(string type)
    {
        var message = CodexOfficialWebViewBridge.CreateHistoryNavigationMessage(type);

        Assert.Equal(type, message["type"]?.Value<string>());
    }

    [Theory]
    [InlineData("/settings", true)]
    [InlineData("/settings/general-settings", true)]
    [InlineData("/settings/agent?source=profile", true)]
    [InlineData("/settings-not-a-route", false)]
    [InlineData("/local/thread-id", false)]
    public void SettingsRoutesAreDetectedWithoutCapturingChatRoutes(string route, bool expected)
    {
        Assert.Equal(expected, CodexOfficialWebViewBridge.IsSettingsRoute(route));
    }

    [Theory]
    [InlineData("/settings", "")]
    [InlineData("/settings/general-settings", "general-settings")]
    [InlineData("/settings/keyboard-shortcuts?source=menu", "keyboard-shortcuts")]
    public void SettingsSectionIsResolvedForTheIndependentSurface(string route, string expected)
    {
        var section = CodexOfficialWebViewBridge.ResolveSettingsSection(
            new JObject { ["path"] = route });

        Assert.Equal(expected, section);
    }

    [Theory]
    [InlineData("reviewDelivery", "reviewDelivery")]
    [InlineData("chatgpt.reviewDelivery", "reviewDelivery")]
    [InlineData(" chatgpt.localeOverride ", "localeOverride")]
    [InlineData("show-context-window-usage", "show-context-window-usage")]
    public void OfficialAndLegacySettingKeysUseTheSameCanonicalName(string key, string expected)
    {
        Assert.Equal(expected, CodexOfficialWebViewBridge.NormalizeSettingKey(key));
    }

    [Fact]
    public void SettingsSnapshotExposesOfficialAndLegacyAliasesWithoutLettingStaleStateWin()
    {
        var settings = new CodexVsix.Models.CodexExtensionSettings
        {
            LanguageOverride = "pt-BR",
            FollowUpQueueMode = "steer",
            ComposerEnterBehavior = "cmdIfMultiline",
            ReviewDelivery = "detached",
            EnableDiagnosticLogging = true
        };
        var persistedState = new JObject
        {
            ["setting:reviewDelivery"] = "inline",
            ["setting:show-context-window-usage"] = true,
            ["setting:chatgpt.preventSleepWhileRunning"] = true,
            ["unrelated"] = "must-not-be-exposed"
        };

        var values = CodexOfficialWebViewBridge.BuildSettingsValues(settings, persistedState);

        Assert.Equal("detached", values["reviewDelivery"]?.Value<string>());
        Assert.Equal("detached", values["chatgpt.reviewDelivery"]?.Value<string>());
        Assert.Equal("steer", values["followUpQueueMode"]?.Value<string>());
        Assert.Equal("cmdIfMultiline", values["composerEnterBehavior"]?.Value<string>());
        Assert.Equal("pt-BR", values["localeOverride"]?.Value<string>());
        Assert.True(values["diagnosticLoggingEnabled"]?.Value<bool>());
        Assert.True(values["chatgpt.diagnosticLoggingEnabled"]?.Value<bool>());
        Assert.True(values["show-context-window-usage"]?.Value<bool>());
        Assert.True(values["chatgpt.show-context-window-usage"]?.Value<bool>());
        Assert.True(values["preventSleepWhileRunning"]?.Value<bool>());
        Assert.Null(values["unrelated"]);
    }

    [Fact]
    public void OfficialSettingsRefreshDoesNotInvalidateEveryViewModelBinding()
    {
        var viewModelSource = File.ReadAllText(FindRepositoryFile(
            "CodexVsix",
            "ViewModels",
            "CodexToolWindowViewModel.cs"));
        var methodStart = viewModelSource.IndexOf(
            "internal void NotifySettingsChangedFromOfficialWebView(string settingKey)",
            StringComparison.Ordinal);
        var nextMethod = viewModelSource.IndexOf(
            "private void ApplySettings()",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && nextMethod > methodStart);
        var methodSource = viewModelSource.Substring(methodStart, nextMethod - methodStart);
        Assert.DoesNotContain("OnPropertyChanged(string.Empty)", methodSource);
        Assert.Contains("OnPropertyChanged(nameof(SelectedReviewDelivery))", methodSource);
        Assert.Contains("OnPropertyChanged(nameof(SelectedFollowUpQueueMode))", methodSource);
        Assert.Contains("OnPropertyChanged(nameof(DiagnosticLoggingEnabled))", methodSource);
    }

    [Fact]
    public void QueryInvalidationBroadcastAlwaysIncludesThePrimaryChatHost()
    {
        var hostSource = File.ReadAllText(FindRepositoryFile(
            "CodexVsix",
            "UI",
            "CodexOfficialWebViewHost.cs"));
        var methodStart = hostSource.IndexOf(
            "public static void BroadcastQueryInvalidation",
            StringComparison.Ordinal);
        var nextMethod = hostSource.IndexOf(
            "public static bool TryPrefillComposer",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && nextMethod > methodStart);
        var methodSource = hostSource.Substring(methodStart, nextMethod - methodStart);
        Assert.Contains("_primary.TryGetTarget(out var primary)", methodSource);
        Assert.Contains("targets.Add(primary)", methodSource);
    }

    [Fact]
    public void QueryInvalidationBroadcastIsBoundedAndCloned()
    {
        var original = new JArray(
            "host-rpc",
            "get-settings",
            new JObject { ["scope"] = "local" });

        var normalized = CodexOfficialWebViewBridge.NormalizeQueryKeyForBroadcast(original);

        Assert.NotNull(normalized);
        Assert.NotSame(original, normalized);
        normalized![0] = "changed";
        Assert.Equal("host-rpc", original[0]?.Value<string>());

        var notification = CodexOfficialWebViewBridge.CreateQueryInvalidationNotification(original);
        Assert.Equal("ipc-broadcast", notification["type"]?.Value<string>());
        Assert.Equal("query-cache-invalidate", notification["method"]?.Value<string>());
        Assert.Equal("host-rpc", notification["params"]?["queryKey"]?[0]?.Value<string>());
        var appBundle = File.ReadAllText(FindRepositoryFile("CodexVsix", "UI", "CodexWebview",
            "webview", "assets", "app-main-CV7KqdWX.js"));
        Assert.Contains("case`ipc-broadcast`:HN(", appBundle);
        Assert.Contains("if(n.method===`query-cache-invalidate`){i.invalidateQueries({queryKey:n.params.queryKey})", appBundle);
        Assert.Null(CodexOfficialWebViewBridge.NormalizeQueryKeyForBroadcast(new JArray()));
        Assert.Null(CodexOfficialWebViewBridge.NormalizeQueryKeyForBroadcast(
            new JArray(Enumerable.Range(0, 33))));
    }

    [Fact]
    public void RecentConversationRefreshUsesABoundedThreadListRequest()
    {
        var parameters = CodexOfficialWebViewBridge.BuildRecentConversationRefreshParams(
            new JObject { ["sortKey"] = "created_at" });

        Assert.False(parameters["archived"]?.Value<bool>());
        Assert.Equal(50, parameters["limit"]?.Value<int>());
        Assert.Equal("created_at", parameters["sortKey"]?.Value<string>());
        Assert.Equal(JTokenType.Null, parameters["cursor"]?.Type);
        Assert.Empty(Assert.IsType<JArray>(parameters["modelProviders"]));
    }

    [Fact]
    public void HistoryPageKeepsTheServerCursorAndIncludesEveryProvider()
    {
        var parameters = CodexOfficialWebViewBridge.BuildRecentConversationRefreshParams(
            new JObject { ["cursor"] = "older-page", ["modelProviders"] = new JArray("current-profile") });

        Assert.Equal("older-page", parameters["cursor"]?.Value<string>());
        Assert.Equal(50, parameters["limit"]?.Value<int>());
        Assert.Empty(Assert.IsType<JArray>(parameters["modelProviders"]));
    }

    [Fact]
    public void HistorySearchReachesTheServerWithItsCursorAndWorkspaceScope()
    {
        var parameters = CodexOfficialWebViewBridge.BuildRecentConversationRefreshParams(new JObject
        {
            ["searchTerm"] = "  重新讲解  ", ["cursor"] = "older-search-page"
        });
        var scoped = CodexOfficialWebViewBridge.PrepareWorkspaceHistoryParams(parameters, @"D:\工程");

        Assert.Equal("重新讲解", scoped["searchTerm"]?.Value<string>());
        Assert.Equal("older-search-page", scoped["cursor"]?.Value<string>());
        Assert.Equal(@"D:\工程", scoped["cwd"]?[0]?.Value<string>());
        Assert.Null(CodexOfficialWebViewBridge.BuildRecentConversationRefreshParams(
            new JObject { ["searchTerm"] = "  " })["searchTerm"]);
    }

    [Theory]
    [InlineData(@"D:\work\project", @"D:\work\project", @"\\?\D:\work\project")]
    [InlineData(@"\\?\D:\work\project\", @"D:\work\project", @"\\?\D:\work\project")]
    [InlineData(@"\\server\share\project", @"\\server\share\project", @"\\?\UNC\server\share\project")]
    public void HistoryQueryUsesTheSelectedFolderAcrossProfiles(string selected, string normal, string extended)
    {
        var original = new JObject
        {
            ["cwd"] = @"C:\previous",
            ["modelProviders"] = new JArray("selected-provider"),
            ["sourceKinds"] = new JArray("vscode"),
            ["cursor"] = "next-page", ["limit"] = 25, ["archived"] = false,
            ["sortKey"] = "updated_at", ["searchTerm"] = "render"
        };
        var request = CodexOfficialWebViewBridge.PrepareWorkspaceHistoryParams(original, selected);

        Assert.Equal(new[] { normal, extended }, Assert.IsType<JArray>(request["cwd"]).Values<string>());
        Assert.Empty(Assert.IsType<JArray>(request["modelProviders"]));
        Assert.Empty(Assert.IsType<JArray>(request["sourceKinds"]));
        Assert.Equal("next-page", request["cursor"]?.Value<string>());
        Assert.Equal("render", request["searchTerm"]?.Value<string>());
        Assert.Equal(25, request["limit"]?.Value<int>());
        Assert.False(request["archived"]?.Value<bool>());
        Assert.Equal(@"C:\previous", original["cwd"]?.Value<string>());
        Assert.Equal("selected-provider", original["modelProviders"]?[0]?.Value<string>());
    }

    [Fact]
    public void HistoryRowsMatchFullPathsAndKeepAllProvidersWithoutChangingSessionMetadata()
    {
        var original = new JObject
        {
            ["data"] = new JArray(
                new JObject { ["id"] = "official", ["cwd"] = @"D:\work\one\trunk", ["modelProvider"] = "openai" },
                new JObject { ["id"] = "custom", ["cwd"] = @"\\?\D:\work\one\trunk\", ["modelProvider"] = "custom-profile" },
                new JObject { ["id"] = "old-provider", ["cwd"] = "d:/WORK/one/trunk", ["modelProvider"] = "removed-provider" },
                new JObject { ["id"] = "other-trunk", ["cwd"] = @"D:\work\two\trunk" },
                new JObject { ["id"] = "child-folder", ["cwd"] = @"D:\work\one\trunk\src" },
                new JObject { ["id"] = "missing-cwd" }),
            ["nextCursor"] = "older"
        };
        var response = CodexOfficialWebViewBridge.FilterWorkspaceHistoryResult(
            original, @"D:\work\one\trunk", @"D:\work\one\trunk");

        foreach (var key in new[] { "data", "threads", "conversations" })
        {
            var rows = Assert.IsType<JArray>(response[key]);
            Assert.Equal(new[] { "official", "custom", "old-provider" }, rows.Select(row => row["id"]!.Value<string>()));
        }
        Assert.Equal("custom-profile", response["data"]?[1]?["modelProvider"]?.Value<string>());
        Assert.Equal(@"\\?\D:\work\one\trunk\", response["data"]?[1]?["cwd"]?.Value<string>());
        Assert.Equal("older", response["nextCursor"]?.Value<string>());
        Assert.Equal(6, Assert.IsType<JArray>(original["data"]).Count);
    }

    [Fact]
    public void DelayedHistoryPageCannotPublishRowsOrCursorAfterChangingFolders()
    {
        var response = CodexOfficialWebViewBridge.FilterWorkspaceHistoryResult(new JObject
        {
            ["data"] = new JArray(new JObject { ["id"] = "old", ["cwd"] = @"D:\old" }),
            ["nextCursor"] = "old-page", ["cursor"] = "legacy-old-page"
        }, @"D:\old", @"D:\new");

        Assert.Empty(Assert.IsType<JArray>(response["data"]));
        Assert.Equal(JTokenType.Null, response["nextCursor"]?.Type);
        Assert.Equal(JTokenType.Null, response["cursor"]?.Type);
    }

    [Fact]
    public void RecentHistoryResponseIsBoundedAndExposesOnlySafeRowMetadata()
    {
        var threads = new JArray();
        for (var index = 0; index < 52; index++)
        {
            threads.Add(new JObject
            {
                ["id"] = "thread-" + index,
                ["name"] = index == 1 ? JValue.CreateNull() : "Task " + index,
                ["preview"] = "Preview " + index,
                ["cwd"] = Path.Combine("C:\\", "work", "project-" + index),
                ["updatedAt"] = 1_750_000_000L + index,
                ["turns"] = new JArray(new JObject { ["text"] = "must-not-cross-the-bridge" }),
                ["path"] = "C:\\private\\thread.jsonl"
            });
        }

        var response = CodexOfficialWebViewBridge.BuildRecentHistoryResponse(new JObject
        {
            ["data"] = threads,
            ["nextCursor"] = "more"
        });
        var items = Assert.IsType<JArray>(response["items"]);
        var first = Assert.IsType<JObject>(items[0]);
        var second = Assert.IsType<JObject>(items[1]);

        Assert.Equal(50, items.Count);
        Assert.True(response["hasMore"]?.Value<bool>());
        Assert.Equal("more", response["nextCursor"]?.Value<string>());
        Assert.Equal("thread-0", first["id"]?.Value<string>());
        Assert.Equal("Task 0", first["title"]?.Value<string>());
        Assert.Equal("project-0", first["workspaceName"]?.Value<string>());
        Assert.Equal("Preview 1", second["title"]?.Value<string>());
        Assert.Null(first["cwd"]);
        Assert.Null(first["turns"]);
        Assert.Null(first["path"]);
        Assert.DoesNotContain("must-not-cross-the-bridge", response.ToString());
        Assert.DoesNotContain("C:\\private", response.ToString());
    }

    [Fact]
    public void WebViewNeverReceivesAnApiKeyFromTheVisualStudioProcess()
    {
        var response = CodexOfficialWebViewBridge.BuildOpenAiApiKeyResponse();

        Assert.Equal(JTokenType.Null, response["value"]?.Type);
    }

    [Fact]
    public void LeavesUnrelatedNetworkRequestsForTheProxy()
    {
        Assert.Null(CodexOfficialWebViewBridge.TryBuildSyntheticFetchResponse(
            new Uri("https://example.com/data.json")));
    }

    [Fact]
    public void IdeContextUsesTheObjectShapeExpectedByTheOfficialWebView()
    {
        var root = Path.Combine("C:\\", "work", "sample");
        var activePath = Path.Combine(root, "Controllers", "PlansController.cs");
        var otherPath = Path.Combine(root, "Services", "PlanService.cs");

        var response = CodexWebViewIdeContext.BuildResponse(
            root,
            activePath,
            "var plan = await repo.GetPlanAsync();",
            new[] { activePath, otherPath });

        var ideContext = Assert.IsType<JObject>(response["ideContext"]);
        var activeFile = Assert.IsType<JObject>(ideContext["activeFile"]);
        Assert.Equal(activePath, activeFile["path"]?.Value<string>());
        Assert.Equal("var plan = await repo.GetPlanAsync();", activeFile["activeSelectionContent"]?.Value<string>());

        var openTabs = Assert.IsType<JArray>(ideContext["openTabs"]);
        Assert.Equal(2, openTabs.Count);
        Assert.Equal("PlansController.cs", openTabs[0]?["label"]?.Value<string>());
        Assert.Equal(otherPath, openTabs[1]?["path"]?.Value<string>());
    }

    [Fact]
    public void IdeContextSafelyRepresentsTheAbsenceOfAnActiveDocument()
    {
        var response = CodexWebViewIdeContext.BuildResponse(
            "C:\\work",
            null,
            "selection without a document",
            Array.Empty<string>());

        var ideContext = Assert.IsType<JObject>(response["ideContext"]);
        Assert.Null(ideContext["activeFile"]);
        Assert.Empty(Assert.IsType<JArray>(ideContext["openTabs"]));
    }

    [Fact]
    public void OpenFileUsesTheRequestWorkspaceWhenNamesCollideWithTheConfiguredWorkspace()
    {
        using var temp = new TemporaryDirectory();
        var configured = Path.Combine(temp.Path, "configured");
        var requested = Path.Combine(temp.Path, "requested");
        Directory.CreateDirectory(configured);
        Directory.CreateDirectory(requested);
        File.WriteAllText(Path.Combine(configured, "file.cpp"), "wrong workspace");
        var expected = Path.Combine(requested, "file.cpp");
        File.WriteAllText(expected, "requested workspace");

        Assert.True(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(new JObject
        {
            ["path"] = "file.cpp:19:4", ["cwd"] = requested
        }, configured, out var target));
        Assert.Equal(expected, target.Path);
        Assert.Equal(19, target.Line);
        Assert.Equal(4, target.Column);

        Assert.True(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(new JObject
        {
            ["path"] = "file.cpp", ["line"] = 7
        }, configured, out var fallback));
        Assert.Equal(Path.Combine(configured, "file.cpp"), fallback.Path);
        Assert.Equal(7, fallback.Line);
    }

    [Fact]
    public void OpenFilePreservesUriCoordinatesAndAllowsExplicitCoordinatesToOverrideThem()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "中文 %20#文件.cpp");
        File.WriteAllText(path, "source");
        var values = new JObject { ["uri"] = new Uri("file:///" + path.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23")).AbsoluteUri + "#L21C5" };

        Assert.True(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(values, temp.Path, out var fromUri));
        Assert.Equal(path, fromUri.Path);
        Assert.Equal(21, fromUri.Line);
        Assert.Equal(5, fromUri.Column);
        values["line"] = 3;
        values["column"] = 2;
        Assert.True(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(values, temp.Path, out var explicitPosition));
        Assert.Equal(3, explicitPosition.Line);
        Assert.Equal(2, explicitPosition.Column);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("\"invalid\"")]
    [InlineData("1.5")]
    [InlineData("{}")]
    public void OpenFileRejectsInvalidCoordinatesWithoutThrowing(string coordinateJson)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "file.cpp");
        File.WriteAllText(path, "source");
        var coordinate = JToken.Parse(coordinateJson);
        Assert.False(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(new JObject
        {
            ["path"] = path, ["line"] = coordinate
        }, temp.Path, out _));
        Assert.False(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(new JObject
        {
            ["path"] = path, ["line"] = 1, ["column"] = coordinate
        }, temp.Path, out _));
    }

    [Fact]
    public void OpenFileDoesNotReportResolvableTargetsForMissingFilesOrExternalUrls()
    {
        using var temp = new TemporaryDirectory();
        Assert.False(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(new JObject
        {
            ["path"] = "missing.cpp"
        }, temp.Path, out _));
        Assert.False(CodexOfficialWebViewBridge.TryResolveOpenFileRequest(new JObject
        {
            ["path"] = "https://example.test/file.cpp"
        }, temp.Path, out _));
    }

    [Fact]
    public void FileReadsUseTheRequestedWorkspaceAndFallBackToTheConfiguredWorkspace()
    {
        using var temp = new TemporaryDirectory();
        var configured = Path.Combine(temp.Path, "configured");
        var requested = Path.Combine(temp.Path, "requested");
        Directory.CreateDirectory(configured);
        Directory.CreateDirectory(requested);
        File.WriteAllText(Path.Combine(configured, "file.txt"), "configured contents");
        File.WriteAllText(Path.Combine(requested, "file.txt"), "requested contents");
        var values = new JObject { ["path"] = "file.txt", ["cwd"] = requested };

        Assert.Equal("requested contents", File.ReadAllText(
            CodexOfficialWebViewBridge.ResolveFilePath(values, configured)));
        values["cwd"] = " ";
        Assert.Equal("configured contents", File.ReadAllText(
            CodexOfficialWebViewBridge.ResolveFilePath(values, configured)));
        Assert.Equal("file.txt", values["path"]?.Value<string>());
    }

    [Fact]
    public void FileReadsPreserveLiteralPercentNamesAndDecodeFileUrisOnlyOnce()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "中文 %20#file.bin");
        var bytes = new byte[] { 0, 1, 254, 255 };
        File.WriteAllBytes(path, bytes);
        var plain = CodexOfficialWebViewBridge.ResolveFilePath(
            new JObject { ["path"] = Path.GetFileName(path) }, temp.Path);
        var absolutePlain = CodexOfficialWebViewBridge.ResolveFilePath(
            new JObject { ["path"] = path }, Path.Combine(temp.Path, "other"));
        var uri = "file:///" + path.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23");
        var fromUri = CodexOfficialWebViewBridge.ResolveFilePath(
            new JObject { ["uri"] = uri, ["cwd"] = Path.Combine(temp.Path, "other") }, temp.Path);

        Assert.Equal(path, plain);
        Assert.Equal(path, absolutePlain);
        Assert.Equal(bytes, File.ReadAllBytes(absolutePlain));
        Assert.Equal(path, fromUri);
        Assert.Equal(bytes, File.ReadAllBytes(fromUri));
    }

    [Fact]
    public void FileResolutionLeavesMissingFileErrorsToTheReadOperation()
    {
        using var temp = new TemporaryDirectory();
        var resolved = CodexOfficialWebViewBridge.ResolveFilePath(
            new JObject { ["path"] = "missing.txt" }, temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "missing.txt"), resolved);
        Assert.Throws<FileNotFoundException>(() => File.ReadAllBytes(resolved));
    }

    [Fact]
    public void PathsExistChecksTheRequestWorkspaceAndReturnsTheOriginalReferences()
    {
        using var temp = new TemporaryDirectory();
        var requested = Path.Combine(temp.Path, "requested");
        Directory.CreateDirectory(requested);
        File.WriteAllText(Path.Combine(requested, "included.txt"), "included");
        Directory.CreateDirectory(Path.Combine(requested, "folder"));
        File.WriteAllText(Path.Combine(temp.Path, "wrong-workspace.txt"), "must not match");
        var values = new JObject
        {
            ["cwd"] = requested,
            ["paths"] = new JArray("included.txt", "folder", "wrong-workspace.txt", "missing.txt", "bad\0path")
        };

        var result = CodexOfficialWebViewBridge.PathsExist(values, temp.Path);

        Assert.Equal(new[] { "included.txt", "folder" },
            Assert.IsType<JArray>(result["existingPaths"]).Values<string>());
        Assert.Equal(5, ((JArray)values["paths"]!).Count);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateParts = new[] { directory.FullName }.Concat(parts).ToArray();
            var candidate = Path.Combine(candidateParts);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(parts));
    }
}
