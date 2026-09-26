using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexProviderSessionRouterTests
{
    [Fact]
    public async Task ColdHistoryResumeUsesItsLastTurnModelInsteadOfTheGlobalDefault()
    {
        using var shared = new TemporaryDirectory();
        var harness = new Harness();
        harness.Settings.EnvironmentVariables = "CODEX_HOME=" + shared.Path;
        harness.Settings.DefaultModel = "gpt-5.5";
        var privateHome = CodexEnvironmentPathHelper.GetCodexHomeDirectory(harness.Settings.EnvironmentVariables);
        var path = Path.Combine(privateHome, "sessions", "same-thread.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"same-thread\",\"model_provider\":\"openai\"}}\n"
            + "{\"type\":\"turn_context\",\"payload\":{\"model\":\"shared-model\"}}\n",
            new UTF8Encoding(false));

        var response = await harness.Invoke("thread/resume", new JObject
        {
            ["threadId"] = "same-thread", ["path"] = path, ["model"] = JValue.CreateNull(),
            ["modelProvider"] = harness.ProviderId
        });

        var sent = Assert.Single(harness.Requests.Where(request => request.Method == "thread/resume")).Values;
        Assert.Equal("shared-model", sent["model"]?.Value<string>());
        Assert.Equal(harness.ProviderId, sent["modelProvider"]?.Value<string>());
        Assert.Equal(harness.Alias, response?["model"]?.Value<string>());

        var explicitChoice = new Harness();
        explicitChoice.Settings.EnvironmentVariables = harness.Settings.EnvironmentVariables;
        await explicitChoice.Invoke("thread/resume", new JObject
        {
            ["threadId"] = "same-thread", ["path"] = path,
            ["model"] = "official-model", ["modelProvider"] = "openai"
        });
        Assert.Equal("official-model", explicitChoice.Requests.Single().Values["model"]?.Value<string>());
    }

    [Fact]
    public async Task HistoryIncludesAllProvidersButRespectsAnExplicitFilter()
    {
        var harness = new Harness();
        await harness.Invoke("thread/list", new JObject());
        Assert.Empty(Assert.IsType<JArray>(harness.Requests.Last().Values["modelProviders"]));
        await harness.Invoke("thread/list", new JObject { ["modelProviders"] = new JArray("explicit-provider") });
        Assert.Equal("explicit-provider", harness.Requests.Last().Values["modelProviders"]?[0]?.Value<string>());
    }

    [Fact]
    public async Task OfficialCustomOfficialSwitchRestartsAndResumesSameHistoryBeforeEachTurn()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject
        {
            ["model"] = "official-model", ["cwd"] = @"D:\synthetic-workspace",
            ["approvalPolicy"] = "on-request", ["sandbox"] = "read-only"
        });
        var customRequest = Turn(harness.Alias);
        var originalRequest = customRequest.DeepClone();

        await harness.Invoke("turn/start", customRequest);
        await harness.Invoke("turn/start", Turn("official-model"));

        Assert.Equal(2, harness.Restarts);
        Assert.Equal(new[]
        {
            "thread/start", "restart", "thread/resume", "thread/settings/update", "thread/resume", "turn/start",
            "restart", "thread/resume", "thread/settings/update", "thread/resume", "turn/start"
        }, harness.Events);
        var allResumes = harness.Requests.Where(r => r.Method == "thread/resume").ToArray();
        Assert.Equal(4, allResumes.Length);
        Assert.All(allResumes, r => Assert.Equal("same-thread", r.Values["threadId"]?.Value<string>()));
        var resumes = allResumes.Where(r => r.Values["developerInstructions"] is not null).ToArray();
        Assert.Equal(2, resumes.Length);
        Assert.All(resumes, r =>
        {
            Assert.Equal("same-thread", r.Values["threadId"]?.Value<string>());
            Assert.Equal(@"D:\synthetic-workspace", r.Values["cwd"]?.Value<string>());
            Assert.Equal("on-request", r.Values["approvalPolicy"]?.Value<string>());
            Assert.Equal("read-only", r.Values["sandbox"]?.Value<string>());
        });
        Assert.Equal(harness.ProviderId, resumes[0].Values["modelProvider"]?.Value<string>());
        Assert.Equal("openai", resumes[1].Values["modelProvider"]?.Value<string>());
        Assert.Equal("shared-model", harness.Requests.First(r => r.Method == "turn/start").Values["model"]?.Value<string>());
        Assert.True(JToken.DeepEquals(originalRequest, customRequest));
    }

    [Theory]
    [InlineData("turn/start")]
    [InlineData("review/start")]
    [InlineData("thread/compact/start")]
    public async Task ActiveTurnReviewOrCompactionPreventsProviderRestart(string method)
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = "official-model" });
        harness.Response = (requestedMethod, values) => requestedMethod == method
            ? new JObject { ["turn"] = new JObject { ["status"] = "inProgress" } }
            : harness.DefaultResponse(requestedMethod, values);
        await harness.Invoke(method, new JObject { ["threadId"] = "same-thread", ["model"] = "official-model" });
        Assert.True(harness.Router.IsBusy);
        var sentCount = harness.Requests.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Invoke("turn/start", Turn(harness.Alias)));

        Assert.Equal(0, harness.Restarts);
        Assert.Equal(sentCount, harness.Requests.Count);
        harness.Router.ObserveNotification("turn/completed", new JObject { ["threadId"] = "same-thread" });
        Assert.False(harness.Router.IsBusy);
    }

    [Fact]
    public async Task CompletionNotificationBeforeTurnResponseDoesNotLeavePhantomBusyState()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = "official-model" });
        harness.Response = (method, values) =>
        {
            if (method != "turn/start") return harness.DefaultResponse(method, values);
            harness.Router.ObserveNotification("turn/completed", new JObject { ["threadId"] = "same-thread" });
            return new JObject { ["turn"] = new JObject { ["status"] = "inProgress" } };
        };

        await harness.Invoke("turn/start", Turn("official-model"));

        Assert.False(harness.Router.IsBusy);
        await harness.Invoke("turn/start", Turn(harness.Alias));
        Assert.Equal(1, harness.Restarts);
    }

    [Fact]
    public async Task UnconfirmedProviderAfterRestartNeverReceivesTheTurn()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = "official-model" });
        harness.Response = (method, values) =>
        {
            var response = harness.DefaultResponse(method, values);
            if (method == "thread/resume") response!["modelProvider"] = "openai";
            return response;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Invoke("turn/start", Turn(harness.Alias)));

        Assert.DoesNotContain(harness.Requests, r => r.Method == "turn/start");
        Assert.True(harness.Restarts >= 1);
        Assert.All(harness.Requests.Where(r => r.Method == "thread/resume" && r.Values["developerInstructions"] is not null), r =>
            Assert.Equal(harness.ProviderId, r.Values["modelProvider"]?.Value<string>()));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("models")]
    public async Task CurrentAndLegacyModelListsKeepDuplicateNamesIsolatedByProvider(string property)
    {
        var harness = new Harness();
        var second = Provider("Other provider");
        harness.Settings.Providers.Add(second);
        harness.Settings.Providers[0].Models.Add("shared-model");
        var original = new JObject
        {
            [property] = new JArray(new JObject { ["id"] = "shared-model", ["model"] = "shared-model", ["isDefault"] = true }),
            ["nextCursor"] = "page-two"
        };
        harness.Response = (method, _) => method == "account/read"
            ? new JObject { ["account"] = new JObject { ["type"] = "chatgpt" } }
            : original;
        await harness.Invoke("account/read", new JObject());

        var result = await harness.Invoke("model/list", new JObject());

        var models = Assert.IsType<JArray>(result?["data"]);
        Assert.Equal(3, models.Count);
        Assert.Equal(3, models.Select(m => m["id"]!.Value<string>()).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(models, m => m["model"]?.Value<string>() == "shared-model");
        Assert.Contains(models, m => m["model"]?.Value<string>() == harness.Alias);
        Assert.Contains(models, m => m["model"]?.Value<string>() == CodexProviderModelCatalog.Alias(second, "shared-model"));
        Assert.Equal(harness.ProviderId, CodexProviderModelCatalog.Resolve(harness.Settings, harness.Alias)?.Provider);
        Assert.Equal("openai", CodexProviderModelCatalog.Resolve(harness.Settings, "shared-model")?.Provider);
        Assert.Single(Assert.IsType<JArray>(original[property]));
        Assert.Equal("page-two", result?["nextCursor"]?.Value<string>());
        Assert.True(JToken.DeepEquals(models, result?["models"]));

        var nextPage = await harness.Invoke("model/list", new JObject { ["cursor"] = "page-two" });
        Assert.Single(Assert.IsType<JArray>(nextPage?[property]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("apiKey")]
    public async Task NonChatGptAccountsKeepOfficialChoicesAndPaginationAlongsideManagedProviders(string? accountType)
    {
        var harness = new Harness();
        var account = new JObject
        {
            ["account"] = accountType is null ? null : new JObject { ["type"] = accountType },
            ["requiresOpenaiAuth"] = true
        };
        var originalModels = new JObject
        {
            ["data"] = new JArray(new JObject { ["model"] = "official-model", ["id"] = "official-model", ["isDefault"] = true }),
            ["nextCursor"] = "page-two"
        };
        harness.Response = (method, _) => method == "account/read" ? account : originalModels;

        var result = await harness.Invoke("account/read", new JObject());

        Assert.True(JToken.DeepEquals(account["account"], result?["account"]));
        Assert.False(result?["requiresOpenaiAuth"]?.Value<bool>());
        Assert.True(account["requiresOpenaiAuth"]?.Value<bool>());
        var list = await harness.Invoke("model/list", new JObject());
        Assert.Equal(2, Assert.IsType<JArray>(list?["data"]).Count);
        Assert.Equal("official-model", list?["data"]?[0]?["model"]?.Value<string>());
        Assert.True(list?["data"]?[1]?["isDefault"]?.Value<bool>());
        Assert.Equal(harness.Alias, list?["data"]?[1]?["model"]?.Value<string>());
        Assert.Equal("page-two", list?["nextCursor"]?.Value<string>());
        Assert.Equal("official-model", originalModels["data"]?[0]?["model"]?.Value<string>());
        var nextPage = await harness.Invoke("model/list", new JObject { ["cursor"] = "page-two" });
        Assert.Single(Assert.IsType<JArray>(nextPage?["data"]));
        Assert.Equal("official-model", nextPage?["data"]?[0]?["model"]?.Value<string>());
    }

    [Fact]
    public async Task ModelListBeforeAccountReadStillIncludesOfficialChoices()
    {
        var harness = new Harness();
        harness.Response = (_, __) => new JObject { ["data"] = new JArray(new JObject { ["model"] = "official-model" }) };
        var list = await harness.Invoke("model/list", new JObject());
        Assert.Contains(list!["data"]!, model => model["model"]?.Value<string>() == "official-model");
        Assert.Contains(list["data"]!, model => model["model"]?.Value<string>() == harness.Alias);
    }

    [Fact]
    public async Task OfficialLoginProtectsOwningServerUntilCompletedOrCancelled()
    {
        var harness = new Harness();
        harness.Response = (method, _) => method == "account/login/start"
            ? new JObject { ["type"] = "chatgpt", ["loginId"] = "synthetic-login" } : new JObject();
        await harness.Invoke("account/login/start", new JObject { ["type"] = "chatgpt" });
        Assert.True(harness.Router.IsBusy);
        Assert.Equal("synthetic-login", harness.Router.PendingLoginId);
        harness.Router.ObserveNotification("account/login/completed", new JObject { ["loginId"] = "different-login", ["success"] = true });
        Assert.True(harness.Router.IsBusy);
        harness.Router.ObserveNotification("account/login/completed", new JObject { ["loginId"] = "synthetic-login", ["success"] = true });
        Assert.False(harness.Router.IsBusy);
        Assert.Null(harness.Router.PendingLoginId);
        harness.Router.ServerStopped();
        await harness.Invoke("account/login/start", new JObject { ["type"] = "chatgpt" });
        await harness.Invoke("account/login/cancel", new JObject { ["loginId"] = "synthetic-login" });
        Assert.False(harness.Router.IsBusy);
    }

    [Fact]
    public async Task LoginCompletionBeforeStartResponseDoesNotLeavePhantomBusyState()
    {
        var harness = new Harness();
        harness.Response = (_, __) =>
        {
            harness.Router.ObserveNotification("account/login/completed", new JObject { ["loginId"] = "synthetic-login", ["success"] = true });
            return new JObject { ["type"] = "chatgpt", ["loginId"] = "synthetic-login" };
        };
        await harness.Invoke("account/login/start", new JObject { ["type"] = "chatgpt" });
        Assert.False(harness.Router.IsBusy);
        Assert.Null(harness.Router.PendingLoginId);
    }

    [Fact]
    public async Task UnidentifiedLoginCompletionClearsPendingLogin()
    {
        var harness = new Harness();
        harness.Response = (_, __) => new JObject { ["type"] = "chatgpt", ["loginId"] = "synthetic-login" };
        await harness.Invoke("account/login/start", new JObject { ["type"] = "chatgpt" });
        Assert.True(harness.Router.IsBusy);
        harness.Router.ObserveNotification("account/login/completed", new JObject { ["loginId"] = null, ["success"] = false });
        Assert.False(harness.Router.IsBusy);
        Assert.Null(harness.Router.PendingLoginId);
    }

    [Fact]
    public async Task SecondLoginCannotReplacePendingLogin()
    {
        var harness = new Harness();
        harness.Response = (_, __) => new JObject { ["type"] = "chatgpt", ["loginId"] = "synthetic-login" };
        await harness.Invoke("account/login/start", new JObject { ["type"] = "chatgpt" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Invoke("account/login/start", new JObject { ["type"] = "chatgpt" }));
        Assert.Single(harness.Requests);
        Assert.Equal("synthetic-login", harness.Router.PendingLoginId);
    }

    [Fact]
    public async Task AccountUpdateImmediatelyRestoresOfficialDefaultWithoutWaitingForRead()
    {
        var harness = new Harness();
        harness.Router.ObserveNotification("account/updated", new JObject { ["authMode"] = "chatgpt" });
        harness.Response = (_, __) => new JObject { ["data"] = new JArray(new JObject { ["model"] = "official-model", ["isDefault"] = true }) };
        var list = await harness.Invoke("model/list", new JObject());
        Assert.True(list!["data"]![0]!["isDefault"]!.Value<bool>());
        harness.Router.ServerStopped();
        list = await harness.Invoke("model/list", new JObject());
        Assert.True(list!["data"]![1]!["isDefault"]!.Value<bool>());
    }

    [Fact]
    public async Task RealChatGptAccountAndOfficialModelDefaultRemainUnchanged()
    {
        var harness = new Harness();
        var account = new JObject
        {
            ["account"] = new JObject { ["type"] = "chatgpt", ["email"] = "synthetic@example.test", ["planType"] = "plus" },
            ["requiresOpenaiAuth"] = true
        };
        harness.Response = (method, _) => method == "account/read" ? account : new JObject
        {
            ["data"] = new JArray(new JObject { ["model"] = "official-model", ["id"] = "official-model", ["isDefault"] = true })
        };

        var result = await harness.Invoke("account/read", new JObject());
        var list = await harness.Invoke("model/list", new JObject());

        Assert.True(JToken.DeepEquals(account, result));
        Assert.True(list?["data"]?[0]?["isDefault"]?.Value<bool>());
        Assert.False(list?["data"]?[1]?["isDefault"]?.Value<bool>());
    }

    [Fact]
    public async Task BatchWriteKeepsModelAndEffortLocalAndForwardsOnlyUnrelatedEdits()
    {
        var harness = new Harness();
        var otherEdit = new JObject { ["keyPath"] = "features.synthetic", ["value"] = true, ["mergeStrategy"] = "upsert" };
        var request = new JObject
        {
            ["edits"] = new JArray(
                new JObject { ["keyPath"] = "model", ["value"] = harness.Alias },
                new JObject { ["keyPath"] = "profiles.synthetic.model_reasoning_effort", ["value"] = "high" }, otherEdit),
            ["expectedVersion"] = "synthetic-version"
        };
        var original = request.DeepClone();
        harness.Response = (_, __) => new JObject { ["status"] = "ok", ["version"] = "remote-version" };

        var response = await harness.Invoke("config/batchWrite", request);

        Assert.Equal(harness.Alias, harness.Settings.DefaultModel);
        Assert.Equal("high", harness.Settings.ReasoningEffort);
        Assert.Equal(1, harness.Saves);
        var forwarded = Assert.Single(harness.Requests);
        Assert.Equal("config/batchWrite", forwarded.Method);
        Assert.True(JToken.DeepEquals(otherEdit, Assert.Single(Assert.IsType<JArray>(forwarded.Values["edits"]))));
        Assert.Equal("synthetic-version", forwarded.Values["expectedVersion"]?.Value<string>());
        Assert.Equal("remote-version", response?["version"]?.Value<string>());
        Assert.True(JToken.DeepEquals(original, request));
    }

    [Fact]
    public async Task ModelOnlyBatchWriteDoesNotSendAnyGlobalConfigWrite()
    {
        var harness = new Harness();
        var result = await harness.Invoke("config/batchWrite", new JObject
        {
            ["edits"] = new JArray(new JObject { ["keyPath"] = "model", ["value"] = harness.Alias })
        });

        Assert.Empty(harness.Requests);
        Assert.Equal(1, harness.Saves);
        Assert.Equal(harness.Alias, harness.Settings.DefaultModel);
        Assert.Equal("ok", result?["status"]?.Value<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedProviderOrModelAliasIsRejectedBeforeSending(bool removeProvider)
    {
        var harness = new Harness();
        var alias = harness.Alias;
        if (removeProvider) harness.Settings.Providers.Clear();
        else harness.Settings.Providers[0].Models.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Invoke("turn/start", Turn(alias)));

        Assert.Empty(harness.Requests);
        Assert.Equal(0, harness.Restarts);
    }

    [Theory]
    [InlineData("config/batchWrite")]
    [InlineData("config/value/write")]
    public async Task RemovedLastProviderAliasCannotBeWrittenIntoGlobalConfiguration(string method)
    {
        var harness = new Harness();
        var alias = harness.Alias;
        harness.Settings.Providers.Clear();
        var edit = new JObject { ["keyPath"] = "model", ["value"] = alias };
        var request = method == "config/batchWrite" ? new JObject { ["edits"] = new JArray(edit) } : edit;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Invoke(method, request));

        Assert.Empty(harness.Requests);
        Assert.Equal(0, harness.Saves);
        Assert.Equal(string.Empty, harness.Settings.DefaultModel);
    }

    [Fact]
    public async Task ExplicitAliasesAreRemovedFromProtocolAndIdentityInstructions()
    {
        var harness = new Harness();
        harness.Settings.DefaultModel = harness.Alias;
        var started = await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        var turn = Turn(harness.Alias);
        turn["effort"] = "low";
        turn["serviceTier"] = "fast";
        turn["collaborationMode"] = new JObject
        {
            ["mode"] = "default", ["settings"] = new JObject
            {
                ["model"] = harness.Alias, ["reasoning_effort"] = "high", ["developer_instructions"] = "Keep original rule."
            }
        };

        await harness.Invoke("turn/start", turn);

        Assert.Equal(harness.Alias, started?["model"]?.Value<string>());
        Assert.All(harness.Requests, request => Assert.DoesNotContain(harness.Alias, request.Values.ToString(), StringComparison.Ordinal));
        Assert.Contains("shared-model", harness.Requests[0].Values["developerInstructions"]?.Value<string>());
        var instructions = harness.Requests.Last().Values["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>();
        Assert.Contains("Keep original rule.", instructions);
        Assert.Contains("shared-model", instructions);
        Assert.Equal("low", harness.Requests.Last().Values["effort"]?.Value<string>());
        Assert.Equal("high", harness.Requests.Last().Values["collaborationMode"]?["settings"]?["reasoning_effort"]?.Value<string>());
        Assert.Contains("reasoning effort is \"high\"", instructions);
        Assert.DoesNotContain("reasoning effort is \"low\"", instructions);
        Assert.Null(harness.Requests.Last().Values["serviceTier"]);
    }

    [Theory]
    [InlineData("thread/fork")]
    [InlineData("turn/start")]
    public async Task OmittedModelCannotLeakSavedAliasIntoRuntimeIdentity(string method)
    {
        var harness = new Harness();
        harness.Settings.DefaultModel = harness.Alias;
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        var request = new JObject { ["threadId"] = "same-thread" };
        if (method == "turn/start") request["collaborationMode"] = new JObject
        {
            ["mode"] = "default", ["settings"] = new JObject { ["developer_instructions"] = "Keep original rule." }
        };

        await harness.Invoke(method, request);

        Assert.DoesNotContain(harness.Alias, harness.Requests.Last().Values.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmittedForkInheritsSourceProviderAndModelThenVerifiesTheNewThread()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        harness.Response = (method, values) =>
        {
            var response = harness.DefaultResponse(method, values);
            if (method == "thread/fork")
            {
                response!["model"] = "official-model";
                response["modelProvider"] = "openai";
            }
            return response;
        };
        var request = new JObject { ["threadId"] = "same-thread" };

        var result = await harness.Invoke("thread/fork", request);

        var fork = Assert.Single(harness.Requests, sent => sent.Method == "thread/fork").Values;
        Assert.Equal("shared-model", fork["model"]?.Value<string>());
        Assert.Equal(harness.ProviderId, fork["modelProvider"]?.Value<string>());
        Assert.Equal(1, harness.Restarts);
        var resumes = harness.Requests.Where(sent => sent.Method == "thread/resume"
            && sent.Values["threadId"]?.Value<string>() == "forked-thread").Select(sent => sent.Values).ToArray();
        Assert.Equal(2, resumes.Length);
        Assert.All(resumes, resume =>
        {
            Assert.Equal("shared-model", resume["model"]?.Value<string>());
            Assert.Equal(harness.ProviderId, resume["modelProvider"]?.Value<string>());
        });
        Assert.Equal("forked-thread", result?["thread"]?["id"]?.Value<string>());
        Assert.Equal(harness.Alias, result?["model"]?.Value<string>());
        Assert.True(JToken.DeepEquals(request, new JObject { ["threadId"] = "same-thread" }));
    }

    [Theory]
    [InlineData("high")]
    [InlineData("ultra")]
    [InlineData("none")]
    [InlineData(null)]
    public async Task CustomTurnPreservesExplicitTopLevelEffortWithoutInventingCollaborationSettings(string? effort)
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        var request = Turn(harness.Alias);
        request["effort"] = effort;
        request["serviceTier"] = "fast";
        var original = request.DeepClone();

        await harness.Invoke("turn/start", request);

        var forwarded = harness.Requests.Last().Values;
        Assert.True(JToken.DeepEquals(request["effort"], forwarded["effort"]));
        Assert.Null(forwarded["collaborationMode"]);
        Assert.Null(forwarded["serviceTier"]);
        Assert.True(JToken.DeepEquals(original, request));
    }

    [Fact]
    public async Task ThreadIdOnlySettingsUpdateUsesItsProviderAndPreservesEffort()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["threadId"] = "custom-thread", ["model"] = harness.Alias });
        await harness.Invoke("thread/start", new JObject { ["threadId"] = "official-thread", ["model"] = "official-model" });
        harness.Settings.DefaultModel = "official-model";
        harness.Settings.ReasoningEffort = "ultra";

        await harness.Invoke("thread/settings/update", new JObject
        {
            ["threadId"] = "custom-thread", ["effort"] = "low", ["serviceTier"] = "fast"
        });
        var custom = harness.Requests.Last().Values;
        Assert.Equal("low", custom["effort"]?.Value<string>());
        Assert.Null(custom["serviceTier"]);
        Assert.Null(custom["model"]);

        await harness.Invoke("thread/settings/update", new JObject
        {
            ["threadId"] = "official-thread", ["effort"] = "medium", ["serviceTier"] = "fast"
        });
        var official = harness.Requests.Last().Values;
        Assert.Equal("medium", official["effort"]?.Value<string>());
        Assert.Equal("fast", official["serviceTier"]?.Value<string>());
        Assert.Equal(0, harness.Restarts);
    }

    [Fact]
    public async Task ThreadIdOnlyTurnAfterServerStopStillRemovesCustomServiceTier()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        harness.Router.ServerStopped();
        harness.Settings.DefaultModel = "official-model";

        await harness.Invoke("turn/start", new JObject
        {
            ["threadId"] = "same-thread", ["effort"] = "high", ["serviceTier"] = "fast"
        });

        var forwarded = harness.Requests.Last().Values;
        Assert.Equal("high", forwarded["effort"]?.Value<string>());
        Assert.Null(forwarded["serviceTier"]);
        Assert.Null(forwarded["model"]);
        Assert.Equal("turn/start", harness.Requests.Last().Method);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollaborationDefaultEffortDoesNotAcquireTopLevelCachedOrGlobalEffort(bool explicitNull)
    {
        var harness = new Harness();
        harness.Settings.ReasoningEffort = "ultra";
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        harness.Router.ObserveNotification("thread/settings/updated", new JObject
        {
            ["threadId"] = "same-thread", ["threadSettings"] = new JObject { ["effort"] = "high" }
        });
        var mode = new JObject { ["model"] = harness.Alias, ["developer_instructions"] = "Preserve user rule." };
        if (explicitNull) mode["reasoning_effort"] = null;
        var request = Turn(harness.Alias);
        request["effort"] = "low";
        request["collaborationMode"] = new JObject { ["mode"] = "default", ["settings"] = mode };

        await harness.Invoke("turn/start", request);

        var forwardedMode = harness.Requests.Last().Values["collaborationMode"]?["settings"];
        Assert.Equal(explicitNull ? JTokenType.Null : (JTokenType?)null, forwardedMode?["reasoning_effort"]?.Type);
        var instructions = forwardedMode?["developer_instructions"]?.Value<string>();
        Assert.Contains("Preserve user rule.", instructions);
        Assert.DoesNotContain("requested reasoning effort", instructions);
        Assert.DoesNotContain(harness.Alias, instructions);
    }

    [Fact]
    public async Task PartialCollaborationSettingsUpdateRefreshesIdentityUsingExplicitEffort()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        await harness.Invoke("thread/settings/update", new JObject
        {
            ["threadId"] = "same-thread", ["effort"] = "low", ["serviceTier"] = "fast",
            ["collaborationMode"] = new JObject
            {
                ["mode"] = "default", ["settings"] = new JObject
                {
                    ["reasoning_effort"] = "high", ["developer_instructions"] = "Keep user rule."
                }
            }
        });

        var forwarded = harness.Requests.Last().Values;
        Assert.Null(forwarded["serviceTier"]);
        Assert.Equal("low", forwarded["effort"]?.Value<string>());
        var instructions = forwarded["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>();
        Assert.Contains("\"shared-model\"", instructions);
        Assert.Contains("reasoning effort is \"high\"", instructions);
        Assert.DoesNotContain("reasoning effort is \"low\"", instructions);
    }

    [Fact]
    public async Task ProviderRestoreKeepsPersistedTopLevelEffortAndAvoidsGlobalPreference()
    {
        var harness = new Harness();
        harness.Settings.ReasoningEffort = "ultra";
        await harness.Invoke("thread/start", new JObject { ["model"] = "official-model" });
        harness.Router.ObserveNotification("thread/settings/updated", new JObject
        {
            ["threadId"] = "same-thread", ["threadSettings"] = new JObject { ["reasoningEffort"] = "high" }
        });

        await harness.Invoke("turn/start", Turn(harness.Alias));

        var restored = Assert.Single(harness.Requests.Where(r => r.Method == "thread/settings/update")).Values;
        Assert.Equal("high", restored["effort"]?.Value<string>());
        Assert.Equal(JTokenType.Null, restored["serviceTier"]?.Type);
        Assert.Null(harness.Requests.Last().Values["effort"]);
        Assert.Null(restored["collaborationMode"]);
    }

    [Fact]
    public async Task PersistedNullEffortReplacesOlderValueWhenProviderIsRestored()
    {
        var harness = new Harness();
        harness.Settings.ReasoningEffort = "ultra";
        await harness.Invoke("thread/start", new JObject { ["model"] = "official-model" });
        foreach (var effort in new[] { "high", (string?)null })
        {
            var notification = new JObject
            {
                ["threadId"] = "same-thread", ["threadSettings"] = new JObject { ["reasoningEffort"] = effort }
            };
            // Match wire JSON: converting a null string directly creates a String-typed JValue.
            var received = JObject.Parse(notification.ToString());
            Assert.Equal(effort is null ? JTokenType.Null : JTokenType.String,
                received["threadSettings"]?["reasoningEffort"]?.Type);
            harness.Router.ObserveNotification("thread/settings/updated", received);
        }

        await harness.Invoke("turn/start", Turn(harness.Alias));

        var restored = Assert.Single(harness.Requests.Where(r => r.Method == "thread/settings/update")).Values;
        Assert.Equal(JTokenType.Null, restored["effort"]?.Type);
        Assert.DoesNotContain("requested reasoning effort", harness.Requests[0].Values["developerInstructions"]?.Value<string>());
        Assert.Null(harness.Requests.Last().Values["effort"]);
    }

    [Fact]
    public async Task SettingsNotificationShowsProviderAliasWithoutMutatingRuntimeModel()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        var notification = new JObject
        {
            ["threadId"] = "same-thread",
            ["threadSettings"] = new JObject
            {
                ["model"] = "shared-model", ["modelProvider"] = harness.ProviderId,
                ["collaborationMode"] = new JObject
                {
                    ["mode"] = "default", ["settings"] = new JObject { ["model"] = "shared-model" }
                }
            }
        };
        var original = notification.DeepClone();

        harness.Router.ObserveNotification("thread/settings/updated", notification);
        var visible = harness.Router.TransformNotification(harness.Settings, "thread/settings/updated", notification);

        Assert.Equal(harness.Alias, visible?["threadSettings"]?["model"]?.Value<string>());
        Assert.Equal(harness.Alias, visible?["threadSettings"]?["collaborationMode"]?["settings"]?["model"]?.Value<string>());
        Assert.True(JToken.DeepEquals(original, notification));
        await harness.Invoke("thread/fork", new JObject { ["threadId"] = "same-thread" });
        var request = harness.Requests.Last().Values;
        Assert.Contains("shared-model", request["developerInstructions"]?.Value<string>());
        Assert.DoesNotContain(harness.Alias, request.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"type\":\"externalSandbox\",\"networkAccess\":\"enabled\"}")]
    [InlineData("{\"type\":\"workspaceWrite\",\"writableRoots\":[\"D:/allowed\"],\"networkAccess\":false,\"excludeTmpdirEnvVar\":true,\"excludeSlashTmp\":true,\"readOnlyAccess\":{\"type\":\"restricted\",\"includePlatformDefaults\":false,\"readableRoots\":[\"D:/reference\"]}}")]
    public async Task ProviderSwitchReplaysExactNotifiedSandboxBeforeSendingTurn(string policyJson)
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = "official-model" });
        var policy = JObject.Parse(policyJson);
        var runtime = new JObject
        {
            ["model"] = "official-model", ["modelProvider"] = "openai",
            ["sandboxPolicy"] = policy.DeepClone(), ["approvalPolicy"] = "untrusted",
            ["disabledPluginIds"] = new JArray("synthetic-disabled-plugin"),
            ["collaborationMode"] = new JObject
            {
                ["mode"] = "plan", ["settings"] = new JObject { ["model"] = "official-model", ["reasoning_effort"] = "high" }
            }
        };
        harness.Router.ObserveNotification("thread/settings/updated", new JObject
        {
            ["threadId"] = "same-thread", ["threadSettings"] = runtime
        });

        await harness.Invoke("turn/start", Turn(harness.Alias));

        Assert.Equal(1, harness.Restarts);
        var restored = Assert.Single(harness.Requests.Where(r => r.Method == "thread/settings/update")).Values;
        Assert.True(JToken.DeepEquals(policy, restored["sandboxPolicy"]));
        Assert.Equal("untrusted", restored["approvalPolicy"]?.Value<string>());
        Assert.Equal("synthetic-disabled-plugin", restored["disabledPluginIds"]?[0]?.Value<string>());
        Assert.Equal("shared-model", restored["collaborationMode"]?["settings"]?["model"]?.Value<string>());
        Assert.Equal("high", restored["collaborationMode"]?["settings"]?["reasoning_effort"]?.Value<string>());
        Assert.Equal("turn/start", harness.Requests.Last().Method);
        Assert.Equal("thread/resume", harness.Requests[harness.Requests.Count - 2].Method);
        Assert.True(JToken.DeepEquals(policy, harness.DefaultResponse("thread/resume", new JObject { ["threadId"] = "same-thread" })?["sandbox"]));
        Assert.Equal("official-model", runtime["collaborationMode"]?["settings"]?["model"]?.Value<string>());
    }

    [Fact]
    public async Task SandboxMismatchAfterSettingsReplayBlocksTheTurn()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = "official-model" });
        harness.Router.ObserveNotification("thread/settings/updated", new JObject
        {
            ["threadId"] = "same-thread", ["threadSettings"] = new JObject
            {
                ["model"] = "official-model", ["modelProvider"] = "openai",
                ["sandboxPolicy"] = new JObject { ["type"] = "externalSandbox", ["networkAccess"] = "enabled" }
            }
        });
        harness.Response = (method, values) => method == "thread/settings/update"
            ? new JObject { ["settings"] = new JObject() } // Simulate a server that accepts but ignores the policy.
            : harness.DefaultResponse(method, values);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Invoke("turn/start", Turn(harness.Alias)));

        Assert.Contains(harness.Requests, request => request.Method == "thread/settings/update");
        Assert.DoesNotContain(harness.Requests, request => request.Method == "turn/start");
    }

    [Fact]
    public async Task RestoredCollaborationIdentityUsesSelectedModelAndPreservesUserInstructions()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = "official-model" });
        var oldTurn = CodexRuntimeIdentityContext.EnrichRequest("turn/start", new JObject
        {
            ["collaborationMode"] = new JObject
            {
                ["mode"] = "default", ["settings"] = new JObject
                {
                    ["model"] = "official-model", ["developer_instructions"] = "Preserve this user rule."
                }
            }
        }, "official-model", null);
        harness.Router.ObserveNotification("thread/settings/updated", new JObject
        {
            ["threadId"] = "same-thread", ["threadSettings"] = new JObject
            {
                ["model"] = "official-model", ["modelProvider"] = "openai",
                ["collaborationMode"] = oldTurn?["collaborationMode"]?.DeepClone()
            }
        });

        await harness.Invoke("turn/start", Turn(harness.Alias));

        var restored = Assert.Single(harness.Requests.Where(r => r.Method == "thread/settings/update")).Values;
        var instructions = restored["collaborationMode"]?["settings"]?["developer_instructions"]?.Value<string>();
        Assert.Contains("Preserve this user rule.", instructions);
        Assert.Contains("\"shared-model\"", instructions);
        Assert.DoesNotContain("\"official-model\"", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Alias, restored.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingAllProvidersStillRoutesAnExistingCustomThreadBackToOfficial()
    {
        var harness = new Harness();
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        harness.Settings.Providers.Clear();
        harness.Settings.DefaultModel = string.Empty;

        await harness.Invoke("turn/start", Turn("official-model"));

        Assert.Equal(1, harness.Restarts);
        Assert.All(harness.Requests.Where(r => r.Method == "thread/resume"), request =>
            Assert.Equal("openai", request.Values["modelProvider"]?.Value<string>()));
        Assert.Equal("official-model", harness.Requests.Last().Values["model"]?.Value<string>());
        Assert.Equal("same-thread", harness.Requests.Last().Values["threadId"]?.Value<string>());
    }

    [Fact]
    public async Task OfficialModelPreferenceRemainsLocalAfterLastProviderIsDeleted()
    {
        var harness = new Harness();
        harness.Settings.Providers.Clear();
        var remote = new JObject
        {
            ["config"] = new JObject
            {
                ["model"] = "global-model", ["profile"] = "synthetic",
                ["profiles"] = new JObject { ["synthetic"] = new JObject { ["model"] = "profile-model" } }
            }
        };
        harness.Response = (_, __) => remote;

        await harness.Invoke("config/value/write", new JObject { ["keyPath"] = "model", ["value"] = "official-model" });
        Assert.Empty(harness.Requests);
        var visible = await harness.Invoke("config/read", new JObject());

        Assert.Equal(1, harness.Saves);
        Assert.Equal("official-model", harness.Settings.DefaultModel);
        Assert.Equal("official-model", visible?["config"]?["model"]?.Value<string>());
        Assert.Equal("official-model", visible?["config"]?["profiles"]?["synthetic"]?["model"]?.Value<string>());
        Assert.Equal("global-model", remote["config"]?["model"]?.Value<string>());
        Assert.Equal("profile-model", remote["config"]?["profiles"]?["synthetic"]?["model"]?.Value<string>());
        Assert.Equal("config/read", Assert.Single(harness.Requests).Method);
    }

    [Fact]
    public async Task CatalogUnknownReasoningDoesNotForwardStaleGlobalEffort()
    {
        var harness = new Harness();
        var provider = harness.Settings.Providers[0];
        provider.Catalog = new() { ManualModels = new() { "shared-model" } };
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        var turn = Turn(harness.Alias);
        turn["effort"] = "max";
        turn["collaborationMode"] = new JObject
        {
            ["mode"] = "default", ["settings"] = new JObject { ["model"] = harness.Alias, ["reasoning_effort"] = "max" }
        };
        await harness.Invoke("turn/start", turn);
        var sent = Assert.Single(harness.Requests, request => request.Method == "turn/start").Values;
        Assert.Null(sent["effort"]);
        Assert.Equal(JTokenType.Null, sent["collaborationMode"]!["settings"]!["reasoning_effort"]!.Type);
        Assert.Equal("max", turn["effort"]!.Value<string>());
    }

    [Fact]
    public async Task CatalogEffortSelectionIsLimitedToReportedLevels()
    {
        var harness = new Harness();
        var provider = harness.Settings.Providers[0];
        provider.Catalog = new()
        {
            DiscoveredModels = new()
            {
                new() { Id = "shared-model", ReasoningEfforts = new() { "low", "max" }, DefaultReasoningEffort = "max" }
            }
        };
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        var turn = Turn(harness.Alias);
        turn["effort"] = "high";
        await harness.Invoke("turn/start", turn);
        Assert.Equal("max", Assert.Single(harness.Requests, request => request.Method == "turn/start").Values["effort"]!.Value<string>());
    }

    [Fact]
    public async Task KnownUnsupportedImageIsRejectedBeforeSendingTurn()
    {
        var harness = new Harness();
        var provider = harness.Settings.Providers[0];
        provider.Catalog = new() { DiscoveredModels = new() { new() { Id = "shared-model", SupportsImages = false } } };
        await harness.Invoke("thread/start", new JObject { ["model"] = harness.Alias });
        var turn = Turn(harness.Alias);
        turn["input"] = new JArray(new JObject { ["type"] = "localImage", ["path"] = "synthetic.png" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Invoke("turn/start", turn));
        Assert.DoesNotContain(harness.Requests, request => request.Method == "turn/start");
        Assert.False(harness.Router.IsBusy);
    }

    private static JObject Turn(string model) => new JObject
    {
        ["threadId"] = "same-thread", ["model"] = model,
        ["input"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Synthetic prompt" })
    };

    private static CodexProviderConfiguration Provider(string name) => new CodexProviderConfiguration
    {
        Name = name, BaseUrl = "https://synthetic.example.test/v1", ApiKey = "synthetic-test-key", Models = { "shared-model" }
    };

    private sealed class Request
    {
        internal Request(string method, JObject values) { Method = method; Values = values; }
        internal string Method { get; }
        internal JObject Values { get; }
    }

    private sealed class Harness
    {
        private readonly Dictionary<string, JObject> _effectiveSettings = new(StringComparer.Ordinal);

        internal Harness()
        {
            Settings.Providers.Add(Provider("Synthetic provider"));
            Router = new CodexProviderSessionRouter(_ => Saves++);
            Response = DefaultResponse;
        }

        internal CodexExtensionSettings Settings { get; } = new();
        internal CodexProviderSessionRouter Router { get; }
        internal List<Request> Requests { get; } = new();
        internal List<string> Events { get; } = new();
        internal int Restarts { get; private set; }
        internal int Saves { get; private set; }
        internal string Alias => CodexProviderModelCatalog.Alias(Settings.Providers[0], "shared-model");
        internal string ProviderId => CodexProviderConfigurationService.GetProviderId(Settings.Providers[0]);
        internal Func<string, JObject, JToken?> Response { get; set; }

        internal Task<JToken?> Invoke(string method, JObject values) => Router.InvokeAsync(
            Settings, method, values, Send, Restart, CancellationToken.None);

        private Task<JToken?> Send(string method, JToken? values, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var copy = Assert.IsType<JObject>(values?.DeepClone());
            Requests.Add(new Request(method, copy));
            Events.Add(method);
            return Task.FromResult(Response(method, copy));
        }

        private Task Restart(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Restarts++;
            Events.Add("restart");
            Router.ServerStopped();
            _effectiveSettings.Clear();
            return Task.CompletedTask;
        }

        internal JToken? DefaultResponse(string method, JObject values)
        {
            var id = method == "thread/fork" ? "forked-thread" : values["threadId"]?.Value<string>() ?? "same-thread";
            if (method == "thread/start" || method == "thread/resume" || method == "thread/fork" || method == "thread/settings/update")
            {
                if (!_effectiveSettings.TryGetValue(id, out var effective))
                {
                    effective = new JObject
                    {
                        ["model"] = "official-model", ["modelProvider"] = "openai",
                        ["cwd"] = @"D:\synthetic-workspace", ["approvalPolicy"] = "on-request",
                        ["sandbox"] = new JObject { ["type"] = "readOnly" }
                    };
                    _effectiveSettings[id] = effective;
                }
                foreach (var key in new[] { "model", "modelProvider", "cwd", "approvalPolicy", "approvalsReviewer",
                    "serviceTier", "disabledPluginIds", "effort", "summary", "collaborationMode", "personality" })
                    if (values[key] is JToken value) effective[key] = value.DeepClone();
                if (values["sandbox"]?.Type == JTokenType.String)
                {
                    var sandboxType = values["sandbox"]!.Value<string>() == "danger-full-access" ? "dangerFullAccess"
                        : values["sandbox"]!.Value<string>() == "workspace-write" ? "workspaceWrite" : "readOnly";
                    effective["sandbox"] = new JObject { ["type"] = sandboxType };
                }
                if (values["sandboxPolicy"] is JObject policy) effective["sandbox"] = policy.DeepClone();
                if (method == "thread/settings/update") return new JObject { ["settings"] = effective.DeepClone() };
                var response = (JObject)effective.DeepClone();
                response["thread"] = new JObject { ["id"] = id };
                return response;
            }
            return new JObject { ["turn"] = new JObject { ["status"] = "completed" } };
        }
    }
}
