using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CodexVsix.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexVsix.Tests;

public sealed class CodexWindowsSandboxDiagnosticsTests
{
    [Fact]
    public void SetupCompletedFailureIsLoggedBeforeDispatchWithoutChangingForwardedPayload()
    {
        using var temp = new TemporaryDirectory();
        var logger = CreateEnabledLogger(temp);
        using var service = new CodexProcessService(logger);
        AddProviderSecret(service, "provider-secret-value");
        SetField(service, "_serverGeneration", 17L);

        var rawParameters = new JObject
        {
            ["mode"] = "elevated",
            ["cwd"] = @"C:\sandbox\workspace",
            ["status"] = "failed",
            ["started"] = true,
            ["success"] = false,
            ["error"] = "provider-secret-value; api_key=second-secret",
            ["extra"] = "must-not-be-logged",
            ["nested"] = new JObject { ["token"] = "nested-secret" }
        };
        JToken? forwarded = null;
        var loggedBeforeDispatch = false;
        service.AppServerNotificationReceived += (_, parameters) =>
        {
            Assert.True(File.Exists(logger.LogFilePath));
            loggedBeforeDispatch = true;
            forwarded = parameters;
        };

        Invoke(service, "HandleNotification", new JObject
        {
            ["method"] = "windowsSandbox/setupCompleted",
            ["params"] = rawParameters
        });

        Assert.NotNull(forwarded);
        Assert.True(loggedBeforeDispatch);
        Assert.True(JToken.DeepEquals(rawParameters, forwarded));

        var entry = ReadEvents(logger).Single(item => item["event"]?.Value<string>() == "windows-sandbox.completed");
        var details = (JObject)entry["details"]!;
        AssertDetailKeys(details, "method", "generation", "mode", "success", "error");
        Assert.Equal("windowsSandbox/setupCompleted", details["method"]?.Value<string>());
        Assert.Equal(17L, details["generation"]?.Value<long>());
        Assert.Equal("elevated", details["mode"]?.Value<string>());
        Assert.False(details["success"]?.Value<bool>());
        Assert.Contains("[redacted]", details["error"]?.Value<string>() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("api_key=[redacted]", details["error"]?.Value<string>() ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret-value", File.ReadAllText(logger.LogFilePath), StringComparison.Ordinal);
        Assert.DoesNotContain("second-secret", File.ReadAllText(logger.LogFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledSandboxDiagnosticsDoNotCreateALogFile()
    {
        using var temp = new TemporaryDirectory();
        var logger = new CodexDiagnosticLogger(Path.Combine(temp.Path, "logs"));
        using var service = new CodexProcessService(logger);
        SetField(service, "_serverGeneration", 4L);

        Invoke(service, "HandleNotification", new JObject
        {
            ["method"] = "windowsSandbox/setupCompleted",
            ["params"] = new JObject { ["success"] = false, ["error"] = "setup failed" }
        });

        Assert.False(File.Exists(logger.LogFilePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(logger.LogFilePath)));
    }

    [Fact]
    public void UnrelatedNotificationIsNotCapturedAsSandboxDiagnostics()
    {
        using var temp = new TemporaryDirectory();
        var logger = CreateEnabledLogger(temp);
        using var service = new CodexProcessService(logger);

        Invoke(service, "HandleNotification", new JObject
        {
            ["method"] = "windowsSandbox/setupCompletedExtra",
            ["params"] = new JObject
            {
                ["mode"] = "workspace-write",
                ["success"] = false,
                ["error"] = "unrelated"
            }
        });

        Assert.DoesNotContain(ReadEvents(logger), item =>
            item["event"]?.Value<string>()?.StartsWith("windows-sandbox.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void StaleGenerationNotificationIsDiscardedBeforeSandboxLoggingOrDispatch()
    {
        using var temp = new TemporaryDirectory();
        var logger = CreateEnabledLogger(temp);
        using var service = new CodexProcessService(logger);
        SetField(service, "_serverGeneration", 8L);
        var dispatched = false;
        service.AppServerNotificationReceived += (_, _) => dispatched = true;

        var message = new JObject
        {
            ["method"] = "windowsSandbox/setupCompleted",
            ["params"] = new JObject { ["success"] = false, ["error"] = "stale" }
        };
        Invoke(service, "HandleServerMessage", message.ToString(Formatting.None), 7L);

        Assert.False(dispatched);
        Assert.DoesNotContain(ReadEvents(logger), item =>
            item["event"]?.Value<string>() == "windows-sandbox.completed");
    }

    [Theory]
    [InlineData("windowsSandbox/readiness")]
    [InlineData("windowsSandbox/setupStart")]
    public async Task SandboxResponsesCorrelateByMethodGenerationAndRequestId(string method)
    {
        using var temp = new TemporaryDirectory();
        var logger = CreateEnabledLogger(temp);
        using var service = new CodexProcessService(logger);
        var requestId = 71L;
        var generation = 23L;
        var completion = AddPendingRequest(service, requestId, generation, method);
        var result = new JObject
        {
            ["status"] = "ready",
            ["started"] = true,
            ["mode"] = "must-not-leak",
            ["cwd"] = @"C:\must-not-leak",
            ["success"] = true,
            ["error"] = "must-not-leak",
            ["extra"] = "must-not-leak"
        };

        Invoke(service, "ResolvePendingRequest", new JObject
        {
            ["id"] = requestId,
            ["result"] = result
        });

        var resolved = await completion.Task;
        Assert.Same(result, resolved);

        var entry = ReadEvents(logger).Single(item => item["event"]?.Value<string>() == "windows-sandbox.response");
        var details = (JObject)entry["details"]!;
        AssertDetailKeys(details, "method", "generation", "requestId", "status", "started");
        Assert.Equal(method, details["method"]?.Value<string>());
        Assert.Equal(generation, details["generation"]?.Value<long>());
        Assert.Equal(requestId, details["requestId"]?.Value<long>());
        Assert.Equal("ready", details["status"]?.Value<string>());
        Assert.True(details["started"]?.Value<bool>());
    }

    [Fact]
    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The test owns and completes this task source in memory without a Visual Studio UI context.")]
    public async Task SandboxRpcFailureKeepsGenericErrorLoggingAndDoesNotEmitSuccessResponse()
    {
        using var temp = new TemporaryDirectory();
        var logger = CreateEnabledLogger(temp);
        using var service = new CodexProcessService(logger);
        var completion = AddPendingRequest(service, 72L, 24L, "windowsSandbox/readiness");
        const string errorMessage = "sandbox RPC failed at readiness";

        Invoke(service, "ResolvePendingRequest", new JObject
        {
            ["id"] = 72L,
            ["error"] = new JObject { ["code"] = -32001, ["message"] = errorMessage }
        });

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => completion.Task);
        Assert.Equal(errorMessage, exception.Message);
        var errorEntry = ReadEvents(logger).Single(item => item["event"]?.Value<string>() == "appserver.response.error");
        Assert.Equal(errorMessage, errorEntry["details"]?["message"]?.Value<string>());
        Assert.DoesNotContain(ReadEvents(logger), item =>
            item["event"]?.Value<string>() == "windows-sandbox.response");
    }

    private static CodexDiagnosticLogger CreateEnabledLogger(TemporaryDirectory temp)
    {
        var logger = new CodexDiagnosticLogger(Path.Combine(temp.Path, "logs"));
        logger.SetEnabled(true, writeTransition: false);
        return logger;
    }

    private static List<JObject> ReadEvents(CodexDiagnosticLogger logger)
    {
        if (!File.Exists(logger.LogFilePath)) return new List<JObject>();
        return File.ReadAllLines(logger.LogFilePath).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(JObject.Parse).ToList();
    }

    private static void AssertDetailKeys(JObject details, params string[] expected)
    {
        Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal),
            details.Properties().Select(property => property.Name).OrderBy(value => value, StringComparer.Ordinal));
    }

    private static TaskCompletionSource<JToken?> AddPendingRequest(
        CodexProcessService service, long requestId, long generation, string method)
    {
        var completion = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingType = typeof(CodexProcessService).GetNestedType("PendingRequest", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType, new object?[] { generation, method, completion })!;
        var pendingRequests = (IDictionary)GetField(service, "_pendingRequests")!;
        pendingRequests.Add(requestId, pending);
        return completion;
    }

    private static void AddProviderSecret(CodexProcessService service, string secret)
    {
        var secrets = (ISet<string>)GetField(service, "_providerSecrets")!;
        secrets.Add(secret);
    }

    private static object? Invoke(CodexProcessService service, string name, params object?[] arguments)
    {
        return typeof(CodexProcessService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, arguments);
    }

    private static object? GetField(CodexProcessService service, string name)
    {
        return typeof(CodexProcessService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service);
    }

    private static void SetField(CodexProcessService service, string name, object? value)
    {
        typeof(CodexProcessService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, value);
    }
}
