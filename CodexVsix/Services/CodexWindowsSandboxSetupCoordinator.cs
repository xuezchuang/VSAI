using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using CodexVsix.Models;
using Newtonsoft.Json.Linq;

namespace CodexVsix.Services;

internal interface IWindowsSandboxSetupSession : IDisposable
{
    event Action<string, JToken?>? NotificationReceived;

    Task<JToken?> StartAsync(
        CodexExtensionSettings settings,
        JToken? parameters,
        CancellationToken cancellationToken);
}

internal sealed class CodexWindowsSandboxSetupCoordinator : IDisposable
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private readonly object _sync = new();
    private readonly Func<IWindowsSandboxSetupSession> _sessionFactory;
    private readonly Func<CodexExtensionSettings, CancellationToken, Task<bool>> _readiness;
    private readonly Action<JToken?> _publishCompletion;
    private readonly CodexDiagnosticLogger? _diagnostics;
    private SetupOperation? _active;
    private bool _disposed;

    internal CodexWindowsSandboxSetupCoordinator(
        CodexDiagnosticLogger diagnostics,
        Func<CodexExtensionSettings, CancellationToken, Task<bool>> readiness,
        Action<JToken?> publishCompletion)
        : this(
            () => new CodexWindowsSandboxSetupSession(diagnostics),
            readiness,
            publishCompletion)
    {
        _diagnostics = diagnostics;
    }

    internal CodexWindowsSandboxSetupCoordinator(
        Func<IWindowsSandboxSetupSession> sessionFactory,
        Func<CodexExtensionSettings, CancellationToken, Task<bool>> readiness,
        Action<JToken?> publishCompletion)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _publishCompletion = publishCompletion ?? throw new ArgumentNullException(nameof(publishCompletion));
    }

    internal async Task<JToken?> StartAsync(
        CodexExtensionSettings settings,
        JToken? parameters,
        CancellationToken cancellationToken)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        cancellationToken.ThrowIfCancellationRequested();
        var setupSettings = settings.SnapshotForSandboxSetup();
        var mode = (parameters as JObject)?["mode"]?.Value<string>();
        if (mode != "elevated" && mode != "unelevated")
            throw new ArgumentException("A supported Windows sandbox setup mode is required.", nameof(parameters));

        SetupOperation operation;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CodexWindowsSandboxSetupCoordinator));
            if (_active is not null) throw new InvalidOperationException("Windows sandbox setup is already in progress.");
            operation = new SetupOperation(_sessionFactory(), mode!);
            _active = operation;
            operation.Session.NotificationReceived += operation.OnNotificationReceived;
        }

        try
        {
            using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, operation.Lifetime.Token);
            var response = await operation.Session.StartAsync(
                setupSettings, parameters?.DeepClone(), startCancellation.Token).ConfigureAwait(false);
            if (response?["started"]?.Value<bool>() != true)
            {
                Cleanup(operation);
                return response;
            }

            _ = CompleteAsync(operation, setupSettings);
            return response;
        }
        catch
        {
            Cleanup(operation);
            throw;
        }
    }

    [SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification =
        "The notification completion source uses RunContinuationsAsynchronously and neither wait captures the Visual Studio UI context.")]
    private async Task CompleteAsync(SetupOperation operation, CodexExtensionSettings settings)
    {
        try
        {
            var timeout = Task.Delay(SetupTimeout, operation.Token);
            var first = await Task.WhenAny(operation.Completion.Task, timeout).ConfigureAwait(false);
            if (first != operation.Completion.Task)
            {
                operation.Token.ThrowIfCancellationRequested();
                Publish(operation, Failure(operation.Mode, "Windows sandbox setup timed out."));
                return;
            }

            var result = await operation.Completion.Task.ConfigureAwait(false);
            if (result["success"]?.Value<bool>() == true)
            {
                try
                {
                    using var readinessTimeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                    readinessTimeout.CancelAfter(ReadinessTimeout);
                    if (!await _readiness(settings, readinessTimeout.Token).ConfigureAwait(false))
                        result = Failure(operation.Mode, "Windows sandbox setup finished, but the sandbox is not ready.");
                }
                catch (OperationCanceledException) when (!operation.Token.IsCancellationRequested)
                {
                    result = Failure(operation.Mode, "Windows sandbox readiness check timed out.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = Failure(operation.Mode, "Windows sandbox readiness check failed: " + ex.Message);
                }
            }
            Publish(operation, result);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            // The owning process service is closing.
        }
        catch (Exception ex)
        {
            Publish(operation, Failure(operation.Mode, "Windows sandbox setup could not be confirmed: " + ex.Message));
        }
        finally
        {
            Cleanup(operation);
        }
    }

    private void Publish(SetupOperation operation, JObject result)
    {
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(_active, operation)) return;
        }
        try
        {
            _publishCompletion(result);
        }
        catch (Exception ex)
        {
            _diagnostics?.Write("windows-sandbox.setup.notification-failed",
                new JObject { ["errorType"] = ex.GetType().FullName });
        }
    }

    private static JObject Failure(string mode, string error) => new()
    {
        ["mode"] = mode,
        ["success"] = false,
        ["error"] = error
    };

    private void Cleanup(SetupOperation operation)
    {
        if (Interlocked.Exchange(ref operation.Cleaned, 1) != 0) return;
        try
        {
            operation.Session.NotificationReceived -= operation.OnNotificationReceived;
            operation.Lifetime.Cancel();
            try
            {
                operation.Session.Dispose();
            }
            catch (Exception ex)
            {
                _diagnostics?.Write("windows-sandbox.setup.cleanup-failed",
                    new JObject { ["errorType"] = ex.GetType().FullName });
            }
        }
        finally
        {
            operation.Lifetime.Dispose();
            lock (_sync)
            {
                if (ReferenceEquals(_active, operation)) _active = null;
            }
        }
    }

    public void Dispose()
    {
        SetupOperation? operation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            operation = _active;
        }
        if (operation is not null) Cleanup(operation);
    }

    private sealed class SetupOperation
    {
        internal SetupOperation(IWindowsSandboxSetupSession session, string mode)
        {
            Session = session;
            Mode = mode;
            Token = Lifetime.Token;
        }

        internal IWindowsSandboxSetupSession Session { get; }
        internal string Mode { get; }
        internal CancellationTokenSource Lifetime { get; } = new();
        internal CancellationToken Token { get; }
        internal TaskCompletionSource<JObject> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Cleaned;

        internal void OnNotificationReceived(string method, JToken? parameters)
        {
            if (method == "windowsSandbox/setupCompleted"
                && parameters is JObject details
                && details["mode"]?.Value<string>() == Mode)
            {
                Completion.TrySetResult((JObject)details.DeepClone());
            }
        }
    }
}

internal sealed class CodexWindowsSandboxSetupSession : IWindowsSandboxSetupSession
{
    private readonly CodexProcessService _service;
    private string? _mode;

    internal CodexWindowsSandboxSetupSession(CodexDiagnosticLogger diagnostics)
    {
        // A setup process needs an enforceable profile. This override belongs only to
        // this process; the user's Full access setting and running chat stay intact.
        _service = new CodexProcessService(
            diagnostics,
            skipSessionMigration: true,
            processConfigOverrides: new[] { "sandbox_mode=\"workspace-write\"" });
        _service.AppServerNotificationReceived += ForwardNotification;
        _service.AppServerProcessExited += OnProcessExited;
    }

    public event Action<string, JToken?>? NotificationReceived;

    public Task<JToken?> StartAsync(
        CodexExtensionSettings settings,
        JToken? parameters,
        CancellationToken cancellationToken)
    {
        _mode = parameters?["mode"]?.Value<string>();
        return _service.InvokeAppServerRequestAsync(
            settings, "windowsSandbox/setupStart", parameters, cancellationToken);
    }

    private void ForwardNotification(string method, JToken? parameters) =>
        NotificationReceived?.Invoke(method, parameters);

    private void OnProcessExited()
    {
        NotificationReceived?.Invoke("windowsSandbox/setupCompleted", new JObject
        {
            ["mode"] = _mode,
            ["success"] = false,
            ["error"] = "Windows sandbox setup process exited before completion."
        });
    }

    public void Dispose()
    {
        _service.AppServerNotificationReceived -= ForwardNotification;
        _service.AppServerProcessExited -= OnProcessExited;
        _service.Dispose();
    }
}
