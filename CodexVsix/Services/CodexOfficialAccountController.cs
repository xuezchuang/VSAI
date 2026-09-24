using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

/// <summary>Explicit ChatGPT login in the private client; never imports desktop credentials.</summary>
internal sealed class CodexOfficialAccountController
{
    private readonly Func<string, JToken?, CancellationToken, Task<JToken?>> _send;
    private readonly Func<string?> _pendingLogin;
    private readonly Func<bool> _busy;
    private readonly Action<string> _openBrowser;

    internal CodexOfficialAccountController(Func<string, JToken?, CancellationToken, Task<JToken?>> send,
        Func<string?> pendingLogin, Func<bool> busy, Action<string> openBrowser)
    {
        _send = send;
        _pendingLogin = pendingLogin;
        _busy = busy;
        _openBrowser = openBrowser;
    }

    internal async Task<JObject> HandleAsync(string action, string? requestId, CancellationToken token)
    {
        try
        {
            var loginId = _pendingLogin();
            if (action == "official-account-cancel")
            {
                if (loginId is not null)
                    await _send("account/login/cancel", new JObject { ["loginId"] = loginId }, token).ConfigureAwait(false);
            }
            else if (loginId is not null) return State("signing-in", requestId);
            var account = await _send("account/read", new JObject { ["refreshToken"] = false }, token).ConfigureAwait(false);
            var accountType = (account?["account"] as JObject)?["type"]?.Value<string>();
            if (action == "official-account-login" && accountType != "chatgpt")
            {
                if (_busy()) return Error(requestId, "请等待当前任务完成后再登录官方账号。");
                var login = await _send("account/login/start", new JObject { ["type"] = "chatgpt" }, token).ConfigureAwait(false);
                var authUrl = login?["authUrl"]?.Value<string>();
                var startedLoginId = login?["loginId"]?.Value<string>();
                try
                {
                    if (login?["type"]?.Value<string>() != "chatgpt" || string.IsNullOrWhiteSpace(startedLoginId)
                        || !Uri.TryCreate(authUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidOperationException("Invalid ChatGPT login response.");
                    _openBrowser(uri.AbsoluteUri);
                }
                catch
                {
                    if (!string.IsNullOrWhiteSpace(startedLoginId))
                        await _send("account/login/cancel", new JObject { ["loginId"] = startedLoginId }, token).ConfigureAwait(false);
                    throw;
                }
                return State("signing-in", requestId);
            }
            var state = State(accountType == "chatgpt" || accountType == "apiKey" ? "signed-in" : "signed-out", requestId);
            state["accountType"] = accountType;
            return state;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Authentication responses/errors may contain login URLs or tokens.
            return Error(requestId, "官方账号连接未完成。请刷新状态，或取消后重新登录。");
        }
    }

    internal static JObject Error(string? requestId, string message)
    {
        var state = State("error", requestId);
        state["error"] = message;
        return state;
    }

    private static JObject State(string status, string? requestId)
        => new() { ["type"] = "official-account-state", ["requestId"] = requestId, ["status"] = status };
}
