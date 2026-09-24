using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexOfficialAccountControllerTests
{
    [Theory]
    [InlineData(null, "signed-out")]
    [InlineData("chatgpt", "signed-in")]
    [InlineData("apiKey", "signed-in")]
    public async Task StatusReadsOnlyAccountMetadataAndDoesNotOpenLogin(string? type, string expected)
    {
        var sent = new List<string>();
        var controller = new CodexOfficialAccountController((method, parameters, token) =>
        {
            sent.Add(method);
            Assert.False(parameters!["refreshToken"]!.Value<bool>());
            return Task.FromResult<JToken?>(new JObject
            {
                ["account"] = type is null ? null : new JObject { ["type"] = type, ["email"] = "private@example.test" }
            });
        }, () => null, () => false, _ => throw new Exception("Unexpected browser launch"));

        var state = await controller.HandleAsync("official-account-request", "request-1", CancellationToken.None);

        Assert.Equal(new[] { "account/read" }, sent);
        Assert.Equal(expected, state["status"]!.Value<string>());
        Assert.Equal(type, state["accountType"]?.Value<string>());
        Assert.Equal("request-1", state["requestId"]!.Value<string>());
        Assert.DoesNotContain("private@example.test", state.ToString());
    }

    [Fact]
    public async Task ExplicitLoginUsesManagedChatGptProtocolAndKeepsUrlOutOfUiState()
    {
        var calls = new List<string>();
        string? opened = null;
        var controller = new CodexOfficialAccountController((method, parameters, token) =>
        {
            calls.Add(method);
            if (method == "account/read") return Task.FromResult<JToken?>(new JObject { ["account"] = null });
            Assert.Equal("chatgpt", parameters!["type"]!.Value<string>());
            Assert.Single((JObject)parameters);
            return Task.FromResult<JToken?>(new JObject
            {
                ["type"] = "chatgpt", ["loginId"] = "synthetic-login",
                ["authUrl"] = "https://auth.openai.com/authorize?state=synthetic-private-state"
            });
        }, () => null, () => false, url => opened = url);

        var state = await controller.HandleAsync("official-account-login", "login-1", CancellationToken.None);

        Assert.Equal(new[] { "account/read", "account/login/start" }, calls);
        Assert.Contains("synthetic-private-state", opened);
        Assert.Equal("signing-in", state["status"]!.Value<string>());
        Assert.DoesNotContain("synthetic-private-state", state.ToString());
    }

    [Fact]
    public async Task PendingLoginIsNotDuplicatedAndCancellationTargetsItsId()
    {
        var calls = new List<string>();
        var controller = new CodexOfficialAccountController((method, parameters, token) =>
        {
            calls.Add(method);
            if (method == "account/login/cancel") Assert.Equal("owned-login", parameters!["loginId"]!.Value<string>());
            return Task.FromResult<JToken?>(new JObject { ["account"] = null });
        }, () => "owned-login", () => true, _ => throw new Exception("Unexpected browser launch"));

        var state = await controller.HandleAsync("official-account-login", null, CancellationToken.None);
        Assert.Equal("signing-in", state["status"]!.Value<string>());
        Assert.Empty(calls);
        state = await controller.HandleAsync("official-account-cancel", null, CancellationToken.None);
        Assert.Equal(new[] { "account/login/cancel", "account/read" }, calls);
        Assert.Equal("signed-out", state["status"]!.Value<string>());
    }

    [Fact]
    public async Task ActiveTaskPreventsLoginWithoutDiscardingProviderState()
    {
        var controller = new CodexOfficialAccountController((method, _, __) =>
        {
            Assert.Equal("account/read", method);
            return Task.FromResult<JToken?>(new JObject());
        }, () => null, () => true, _ => throw new Exception("Unexpected browser launch"));
        var state = await controller.HandleAsync("official-account-login", null, CancellationToken.None);
        Assert.Equal("error", state["status"]!.Value<string>());
        Assert.Contains("任务", state["error"]!.Value<string>());
    }

    [Theory]
    [InlineData("https://auth.openai.com/authorize")]
    [InlineData("file:///synthetic-login")]
    public async Task FailedBrowserLaunchOrInvalidUrlCancelsLoginAndDoesNotExposeSensitiveException(string authUrl)
    {
        var calls = new List<string>();
        var controller = new CodexOfficialAccountController((method, _, __) =>
        {
            calls.Add(method);
            return Task.FromResult<JToken?>(method == "account/login/start"
                ? new JObject { ["type"] = "chatgpt", ["loginId"] = "owned-login", ["authUrl"] = authUrl }
                : new JObject());
        }, () => null, () => false, _ => throw new Exception("private-token"));
        var state = await controller.HandleAsync("official-account-login", null, CancellationToken.None);
        Assert.Equal(new[] { "account/read", "account/login/start", "account/login/cancel" }, calls);
        Assert.Equal("error", state["status"]!.Value<string>());
        Assert.DoesNotContain("private-token", state.ToString());
    }
}
