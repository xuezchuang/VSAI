using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE90a;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using DteThread = EnvDTE.Thread;

namespace CodexVsix.Services;

// All DTE objects and event subscriptions belong to the VS UI thread. No debugger
// context setters, expression evaluation, or execution commands are used here.
internal sealed class VisualStudioDebugService : IVsDebugToolService
{
    private const int MaxProcesses = 16;
    private const int MaxProgramsPerProcess = 16;
    private const int MaxReadMilliseconds = 1500;
    private const int MaxNameLength = 512;
    private const int MaxValueLength = 2048;
    private const int MaxPathLength = 4096;
    private const int MaxOutputChars = 65536;

    private readonly object _lifetimeSync = new();
    private DTE? _dte;
    private DebuggerEvents? _events;
    private readonly DebugStopTracker _stops = new();
    private JObject? _exception;
    private dbgEventReason? _eventBreakReason;
    private volatile bool _disposed;
    private bool _subscriptionRemoved;

    public async Task<JObject> ExecuteAsync(string toolName, JObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (_disposed)
                return Error("disposed", "The Visual Studio debug service is no longer available.");

            EnsureSubscribed();
            if (_disposed)
                return Error("disposed", "The Visual Studio debug service is no longer available.");
            if (_dte?.Debugger == null)
                return Error("debugger_unavailable", "Visual Studio debugger automation is unavailable.");

            var read = new ReadGuard(this, cancellationToken);
            JObject result;
            switch (toolName)
            {
                case "vs_debug_snapshot":
                    result = Snapshot(arguments, read);
                    break;
                case "vs_debug_frame_variables":
                    result = Variables(arguments, read);
                    break;
                case "vs_debug_threads":
                    result = Threads(arguments, read);
                    break;
                default:
                    return Error("unknown_tool", "Unknown Visual Studio debug tool.");
            }

            read.Check();
            if (NewtonsoftJsonCompatibility.Serialize(result, Newtonsoft.Json.Formatting.None).Length > MaxOutputChars)
                return Error("output_limit", "Debug result exceeded the output limit. Request fewer items.");
            return result;
        }
        catch (OperationCanceledException)
        {
            return Error("cancelled", "Debug read was cancelled.");
        }
        catch (DebugReadException ex)
        {
            return Error(ex.Code, ex.Message);
        }
        catch (COMException)
        {
            return Error("debugger_unavailable", "The debugger could not provide this data.");
        }
        catch (InvalidComObjectException)
        {
            return Error("debugger_unavailable", "The debugger context is no longer available.");
        }
        catch (Exception)
        {
            // Do not surface exception text: DTE errors can contain debuggee data.
            return Error("debugger_unavailable", "The debugger could not provide this data.");
        }
    }

    private void EnsureSubscribed()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        lock (_lifetimeSync)
        {
            if (_disposed || _events != null) return;
        }

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var events = dte?.Events?.DebuggerEvents;
        if (events == null)
            return;

        try
        {
            events.OnEnterRunMode += OnEnterRunMode;
            events.OnEnterDesignMode += OnEnterDesignMode;
            events.OnEnterBreakMode += OnEnterBreakMode;
            events.OnExceptionThrown += OnExceptionThrown;
            events.OnExceptionNotHandled += OnExceptionNotHandled;
        }
        catch
        {
            UnsubscribeAll(events);
            throw;
        }

        var discard = false;
        lock (_lifetimeSync)
        {
            if (_disposed || _events != null)
                discard = true;
            else
            {
                _dte = dte;
                _events = events; // Strong reference is required for DTE COM event delivery.
            }
        }
        if (discard) UnsubscribeAll(events);
    }

    private void OnEnterRunMode(dbgEventReason reason) { if (!_disposed) InvalidateStop(); }
    private void OnEnterDesignMode(dbgEventReason reason) { if (!_disposed) InvalidateStop(); }

    private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction action)
    {
        if (_disposed) return;
        var exception = reason == dbgEventReason.dbgEventReasonExceptionThrown
            || reason == dbgEventReason.dbgEventReasonExceptionNotHandled ? _exception : null;
        InvalidateStop();
        _eventBreakReason = reason;
        _stops.EnsureBreak();
        _exception = exception;
    }

    private void OnExceptionThrown(string exceptionType, string name, int code, string description, ref dbgExceptionAction action)
        => RecordException(exceptionType, name, code, description, false);

    private void OnExceptionNotHandled(string exceptionType, string name, int code, string description, ref dbgExceptionAction action)
        => RecordException(exceptionType, name, code, description, true);

    private void RecordException(string exceptionType, string name, int code, string description, bool unhandled)
    {
        if (_disposed) return;
        _exception = new JObject
        {
            ["type"] = Clip(exceptionType), ["name"] = Clip(name), ["code"] = code,
            ["description"] = Clip(description), ["unhandled"] = unhandled
        };
    }

    private void InvalidateStop()
    {
        _stops.Invalidate();
        _eventBreakReason = null;
        _exception = null;
    }

    private JObject Snapshot(JObject args, ReadGuard read)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var maxFrames = IntArg(args, "maxFrames", 32, 1, 128);
        var debugger = _dte!.Debugger;
        var mode = debugger.CurrentMode;
        var result = new JObject { ["mode"] = ModeName(mode) };
        var solutionPath = SafeReference(() => _dte.Solution?.FullName);
        result["solutionPath"] = PathOrNull(solutionPath);
        result["solutionPathUnavailable"] = solutionPath != null && PathOrNull(solutionPath) == null;
        if (mode != dbgDebugMode.dbgBreakMode)
        {
            InvalidateStop();
            result["paused"] = false;
            read.Check();
            if (debugger.CurrentMode != mode)
                throw new DebugReadException("stale_snapshot", "Debugger state changed while reading. Capture a new snapshot.");
            return result;
        }

        var snapshotId = _stops.EnsureBreak();
        read.BindStop(snapshotId, _stops.Revision);
        result["paused"] = true;
        result["snapshotId"] = snapshotId;
        result["breakReason"] = (_eventBreakReason ?? debugger.LastBreakReason).ToString();
        if (_exception != null)
            result["exception"] = _exception.DeepClone();

        var selectedThread = SafeReference(() => debugger.CurrentThread);
        var selectedProgram = SafeReference(() => selectedThread?.Program);
        var selectedProcess = SafeReference(() => selectedProgram?.Process) ?? SafeReference(() => debugger.CurrentProcess);
        var selectedFrame = SafeReference(() => debugger.CurrentStackFrame);
        if (selectedFrame != null && selectedThread != null && !SameComObject(SafeReference(() => selectedFrame.Parent), selectedThread))
            selectedFrame = null;
        result["processId"] = selectedProcess?.ProcessID;
        var processName = SafeReference(() => selectedProcess?.Name);
        result["processName"] = Clip(processName);
        result["processNameTruncated"] = processName?.Length > MaxNameLength;
        result["threadId"] = selectedThread?.ID;
        var threadName = SafeReference(() => selectedThread?.Name);
        result["threadName"] = Clip(threadName);
        result["threadNameTruncated"] = threadName?.Length > MaxNameLength;
        var selectedPrograms = SafeReference(() => selectedProcess?.Programs);
        var selectedProgramCount = selectedPrograms == null ? null : SafeValue(() => selectedPrograms.Count);
        if (selectedProgram != null && selectedProgramCount.HasValue)
        {
            for (var pi = 0; pi < Math.Min(selectedProgramCount.Value, MaxProgramsPerProcess); pi++)
            {
                read.Check();
                if (!SameComObject(SafeReference(() => selectedPrograms!.Item(pi + 1)), selectedProgram)) continue;
                result["programIndex"] = pi;
                break;
            }
        }
        var frames = new JArray();
        var stack = SafeReference(() => selectedThread?.StackFrames);
        var stackCount = stack == null ? null : SafeValue(() => stack.Count);
        result["framesUnavailable"] = !stackCount.HasValue;
        var count = Math.Min(stackCount ?? 0, maxFrames);
        for (var index = 0; index < count; index++)
        {
            read.Check();
            var frame = SafeReference(() => stack!.Item(index + 1));
            if (frame == null)
            {
                result["framesUnavailable"] = true;
                break;
            }
            var item = FrameJson(frame, index);
            item["selected"] = selectedFrame != null && SameFrame(frame, selectedFrame);
            if (!read.TryReserve(NewtonsoftJsonCompatibility.Serialize(item, Newtonsoft.Json.Formatting.None).Length))
                break;
            if (item.Value<bool>("selected"))
                result["selectedFrameIndex"] = index;
            frames.Add(item);
        }
        result["frames"] = frames;
        result["framesTruncated"] = (stackCount ?? 0) > frames.Count;
        read.Check();
        return result;
    }

    private JObject Threads(JObject args, ReadGuard read)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var id = StringArg(args, "snapshotId");
        var maxThreads = IntArg(args, "maxThreads", 16, 1, 64);
        var maxFrames = IntArg(args, "maxFrames", 8, 1, 32);
        RequireStop(id, read);
        var debugger = _dte!.Debugger;
        var result = new JObject { ["snapshotId"] = id, ["processes"] = new JArray() };
        var output = (JArray)result["processes"]!;
        var processes = debugger.DebuggedProcesses;
        var processCount = Math.Min(processes.Count, MaxProcesses);
        var threadCount = 0;
        var truncated = processes.Count > MaxProcesses;
        var outputFull = false;
        for (var pi = 0; pi < processCount && !outputFull; pi++)
        {
            read.Check();
            var process = processes.Item(pi + 1);
            var processName = process.Name;
            var processJson = new JObject { ["processId"] = process.ProcessID, ["name"] = Clip(processName), ["nameTruncated"] = processName?.Length > MaxNameLength, ["programs"] = new JArray() };
            if (!read.TryReserve(256)) { truncated = true; break; }
            output.Add(processJson);
            var programs = process.Programs;
            var programCount = Math.Min(programs.Count, MaxProgramsPerProcess);
            truncated |= programs.Count > MaxProgramsPerProcess;
            for (var gi = 0; gi < programCount && !outputFull; gi++)
            {
                read.Check();
                var program = programs.Item(gi + 1);
                var programName = program.Name;
                var programJson = new JObject { ["programIndex"] = gi, ["name"] = Clip(programName), ["nameTruncated"] = programName?.Length > MaxNameLength, ["threads"] = new JArray() };
                if (!read.TryReserve(256)) { truncated = true; outputFull = true; break; }
                ((JArray)processJson["programs"]!).Add(programJson);
                var threads = program.Threads;
                for (var ti = 0; ti < threads.Count; ti++)
                {
                    read.Check();
                    if (threadCount >= maxThreads) { truncated = true; break; }
                    var thread = threads.Item(ti + 1);
                    var threadName = thread.Name;
                    var threadJson = new JObject { ["threadId"] = thread.ID, ["name"] = Clip(threadName), ["nameTruncated"] = threadName?.Length > MaxNameLength, ["frames"] = new JArray() };
                    if (!read.TryReserve(256)) { truncated = true; outputFull = true; break; }
                    ((JArray)programJson["threads"]!).Add(threadJson);
                    threadCount++;
                    var stack = SafeReference(() => thread.StackFrames);
                    var totalFrames = stack == null ? null : SafeValue(() => stack.Count);
                    threadJson["framesUnavailable"] = !totalFrames.HasValue;
                    var stackCount = Math.Min(totalFrames ?? 0, maxFrames);
                    for (var fi = 0; fi < stackCount; fi++)
                    {
                        read.Check();
                        var frame = SafeReference(() => stack!.Item(fi + 1));
                        if (frame == null) { threadJson["framesUnavailable"] = true; break; }
                        var frameJson = FrameJson(frame, fi);
                        if (!read.TryReserve(NewtonsoftJsonCompatibility.Serialize(frameJson, Newtonsoft.Json.Formatting.None).Length))
                        {
                            truncated = true;
                            outputFull = true;
                            break;
                        }
                        ((JArray)threadJson["frames"]!).Add(frameJson);
                    }
                    threadJson["framesTruncated"] = (totalFrames ?? 0) > ((JArray)threadJson["frames"]!).Count;
                    if (outputFull) break;
                    if (threadCount >= maxThreads)
                    {
                        truncated |= ti + 1 < threads.Count || gi + 1 < programCount || pi + 1 < processCount;
                        break;
                    }
                }
                if (threadCount >= maxThreads) break;
            }
            if (threadCount >= maxThreads) break;
        }
        result["threadsTruncated"] = truncated;
        read.Check();
        return result;
    }

    private JObject Variables(JObject args, ReadGuard read)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var id = StringArg(args, "snapshotId");
        var processId = IntArg(args, "processId", null, 0, int.MaxValue);
        var threadId = IntArg(args, "threadId", null, 0, int.MaxValue);
        var frameIndex = IntArg(args, "frameIndex", null, 0, int.MaxValue);
        int? programIndex = args["programIndex"] == null ? null : IntArg(args, "programIndex", null, 0, MaxProgramsPerProcess - 1);
        var maxVariables = IntArg(args, "maxVariables", 64, 1, 128);
        RequireStop(id, read);

        var processes = _dte!.Debugger.DebuggedProcesses;
        DteThread? targetThread = null;
        var processMatches = 0;
        for (var pi = 0; pi < Math.Min(processes.Count, MaxProcesses); pi++)
        {
            read.Check();
            var process = processes.Item(pi + 1);
            if (process.ProcessID != processId) continue;
            processMatches++;
            if (processMatches > 1)
                throw new DebugReadException("ambiguous_process", "More than one debugged process has this process ID.");
            var programs = process.Programs;
            if (programs.Count > MaxProgramsPerProcess)
                throw new DebugReadException("read_limit", "This process has too many programs for exact thread lookup.");
            for (var gi = 0; gi < Math.Min(programs.Count, MaxProgramsPerProcess); gi++)
            {
                read.Check();
                if (programIndex.HasValue && gi != programIndex.Value) continue;
                var threads = programs.Item(gi + 1).Threads;
                if (threads.Count > 256)
                    throw new DebugReadException("read_limit", "This program has too many threads for exact lookup.");
                for (var ti = 0; ti < Math.Min(threads.Count, 256); ti++)
                {
                    read.Check();
                    var thread = threads.Item(ti + 1);
                    if (thread.ID != threadId) continue;
                    if (targetThread != null)
                        throw new DebugReadException("ambiguous_thread", "More than one debugged program has this thread ID.");
                    targetThread = thread;
                }
            }
        }
        if (processes.Count > MaxProcesses)
            throw new DebugReadException("read_limit", "Too many debugged processes for exact lookup.");
        if (targetThread == null)
            throw new DebugReadException("frame_unavailable", "Requested process or thread is unavailable.");
        read.Check();
        var stack = targetThread.StackFrames;
        if (frameIndex >= Math.Min(stack.Count, 128))
            throw new DebugReadException("frame_unavailable", "Requested stack frame is unavailable or outside the read limit.");
        var frame = stack.Item(frameIndex + 1) as StackFrame2;
        if (frame == null)
            throw new DebugReadException("frame_unavailable", "This debugger does not support safe frame variable access.");
        var remaining = maxVariables;
        var arguments = SafeReference(() => frame.Arguments2[false]);
        var argumentItems = ReadExpressions(arguments, ref remaining, read, out var argumentsTruncated);
        var locals = SafeReference(() => frame.Locals2[false]);
        var localItems = ReadExpressions(locals, ref remaining, read, out var localsTruncated);
        var result = new JObject
        {
            ["snapshotId"] = id, ["processId"] = processId, ["threadId"] = threadId,
            ["frameIndex"] = frameIndex,
            ["arguments"] = argumentItems, ["argumentsUnavailable"] = arguments == null,
            ["argumentsTruncated"] = argumentsTruncated,
            ["locals"] = localItems, ["localsUnavailable"] = locals == null,
            ["localsTruncated"] = localsTruncated
        };
        if (programIndex.HasValue) result["programIndex"] = programIndex.Value;
        // EnvDTE.Expression.DataMembers has no safe-evaluation switch. It
        // cannot be traversed without risking debugger function calls.
        result["members"] = new JArray();
        result["membersUnavailable"] = true;
        result["membersUnavailableReason"] = "DTE does not expose a no-evaluation option for member enumeration.";
        result["variablesTruncated"] = argumentsTruncated || localsTruncated;
        read.Check();
        return result;
    }

    private static JArray ReadExpressions(Expressions? expressions, ref int remaining, ReadGuard read, out bool truncated)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var output = new JArray();
        truncated = false;
        if (expressions == null) return output;
        var countValue = SafeValue(() => expressions.Count);
        if (!countValue.HasValue) { truncated = true; return output; }
        var total = countValue.Value;
        var count = Math.Min(total, remaining);
        truncated = total > count;
        for (var i = 0; i < count; i++)
        {
            read.Check();
            var expression = SafeReference(() => expressions.Item(i + 1));
            if (expression == null) { truncated = true; continue; }
            var name = SafeReference(() => expression.Name);
            var type = SafeReference(() => expression.Type);
            var value = SafeReference(() => expression.Value);
            var item = new JObject
            {
                ["name"] = Clip(name, MaxNameLength), ["nameTruncated"] = name?.Length > MaxNameLength,
                ["type"] = Clip(type, MaxNameLength), ["typeTruncated"] = type?.Length > MaxNameLength,
                ["value"] = Clip(value, MaxValueLength), ["valueTruncated"] = value?.Length > MaxValueLength,
                ["valid"] = SafeValue(() => expression.IsValidValue),
                ["membersUnavailable"] = true
            };
            if (!read.TryReserve(NewtonsoftJsonCompatibility.Serialize(item, Newtonsoft.Json.Formatting.None).Length))
            {
                truncated = true;
                break;
            }
            output.Add(item);
            remaining--;
        }
        return output;
    }

    private void RequireStop(string id, ReadGuard read)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_dte?.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode || _stops.SnapshotId == null || !string.Equals(_stops.SnapshotId, id, StringComparison.Ordinal))
            throw new DebugReadException("stale_snapshot", "The debugger has left this stop. Capture a new snapshot.");
        read.BindStop(id, _stops.Revision);
    }

    private static JObject FrameJson(StackFrame frame, int index)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var frame2 = frame as StackFrame2;
        uint? line = null;
        if (frame2 != null)
        {
            var reportedLine = SafeValue(() => frame2.LineNumber);
            if (reportedLine > 0) line = reportedLine;
        }
        var function = SafeReference(() => frame.FunctionName);
        var module = SafeReference(() => frame.Module);
        var sourcePath = frame2 == null ? null : SafeReference(() => frame2.FileName);
        return new JObject
        {
            ["frameIndex"] = index,
            ["function"] = Clip(function, MaxNameLength), ["functionTruncated"] = function?.Length > MaxNameLength,
            ["module"] = Clip(module, MaxNameLength), ["moduleTruncated"] = module?.Length > MaxNameLength,
            ["sourcePath"] = PathOrNull(sourcePath), ["sourcePathUnavailable"] = PathOrNull(sourcePath) == null,
            ["line"] = line.HasValue ? JToken.FromObject(line.Value) : null
        };
    }

    private static bool SameFrame(StackFrame a, StackFrame b)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ReferenceEquals(a, b)) return true;
        var left = a as StackFrame2;
        var right = b as StackFrame2;
        return left != null && right != null && left.Depth == right.Depth
            && string.Equals(SafeReference(() => left.FunctionName), SafeReference(() => right.FunctionName), StringComparison.Ordinal)
            && string.Equals(SafeReference(() => left.FileName), SafeReference(() => right.FileName), StringComparison.Ordinal);
    }

    private static bool SameComObject(object? left, object? right)
    {
        if (left == null || right == null) return false;
        if (ReferenceEquals(left, right)) return true;
        if (!Marshal.IsComObject(left) || !Marshal.IsComObject(right)) return false;
        IntPtr leftUnknown = IntPtr.Zero;
        IntPtr rightUnknown = IntPtr.Zero;
        try
        {
            leftUnknown = Marshal.GetIUnknownForObject(left);
            rightUnknown = Marshal.GetIUnknownForObject(right);
            return leftUnknown == rightUnknown;
        }
        catch (COMException) { return false; }
        catch (InvalidComObjectException) { return false; }
        finally
        {
            if (leftUnknown != IntPtr.Zero) Marshal.Release(leftUnknown);
            if (rightUnknown != IntPtr.Zero) Marshal.Release(rightUnknown);
        }
    }

    private static int IntArg(JObject args, string key, int? defaultValue, int min, int max)
    {
        var token = args[key];
        if (token == null && defaultValue.HasValue) return defaultValue.Value;
        if (token?.Type != JTokenType.Integer || !int.TryParse(token.ToString(), out var value) || value < min || value > max)
            throw new DebugReadException("invalid_arguments", "Invalid " + key + ".");
        return value;
    }

    private static string StringArg(JObject args, string key)
    {
        var token = args[key];
        if (token?.Type != JTokenType.String || string.IsNullOrWhiteSpace(token.Value<string>()))
            throw new DebugReadException("invalid_arguments", "Invalid " + key + ".");
        return token.Value<string>()!;
    }

    private static string ModeName(dbgDebugMode mode) => mode == dbgDebugMode.dbgBreakMode ? "break" : mode == dbgDebugMode.dbgRunMode ? "run" : "design";
    private static string? Clip(string? value, int maxLength = MaxNameLength) => value == null ? null : value.Length <= maxLength ? value : value.Substring(0, maxLength);
    private static string? PathOrNull(string? value) => value != null && !string.IsNullOrWhiteSpace(value) && value.Length <= MaxPathLength ? value : null;
    private static string? SafeText(Func<string?> read) => Clip(SafeReference(read));
    private static T? SafeReference<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (COMException) { return default; }
        catch (InvalidComObjectException) { return default; }
    }

    private static T? SafeValue<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (COMException) { return null; }
        catch (InvalidComObjectException) { return null; }
    }

    private static JObject Error(string code, string message) => new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = message } };

    public void Dispose()
    {
        bool subscribed;
        lock (_lifetimeSync)
        {
            if (_disposed) return;
            _disposed = true;
            subscribed = _events != null;
        }
        if (!subscribed) return;
        if (ThreadHelper.CheckAccess()) { DisposeOnUiThread(); return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                DisposeOnUiThread();
            }
            catch (Exception) { /* VS may already be shutting down. */ }
        }).FileAndForget("CodexVsix/DebugServiceDispose");
    }

    private void DisposeOnUiThread()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        DebuggerEvents? events;
        lock (_lifetimeSync)
        {
            if (_subscriptionRemoved) return;
            _subscriptionRemoved = true;
            events = _events;
            _events = null;
            _dte = null;
        }
        InvalidateStop();
        if (events != null) UnsubscribeAll(events);
    }

    private void UnsubscribeAll(DebuggerEvents events)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        TryUnsubscribe(() => events.OnEnterRunMode -= OnEnterRunMode);
        TryUnsubscribe(() => events.OnEnterDesignMode -= OnEnterDesignMode);
        TryUnsubscribe(() => events.OnEnterBreakMode -= OnEnterBreakMode);
        TryUnsubscribe(() => events.OnExceptionThrown -= OnExceptionThrown);
        TryUnsubscribe(() => events.OnExceptionNotHandled -= OnExceptionNotHandled);
    }

    private static void TryUnsubscribe(Action remove)
    {
        try { remove(); }
        catch (COMException) { }
        catch (InvalidComObjectException) { }
    }

    private sealed class ReadGuard
    {
        private readonly VisualStudioDebugService _owner;
        private readonly CancellationToken _cancellation;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private string? _id;
        private long _revision;
        private int _outputRemaining = 60000;

        public ReadGuard(VisualStudioDebugService owner, CancellationToken cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
        }

        public void BindStop(string id, long revision) { _id = id; _revision = revision; }

        public bool TryReserve(int characters)
        {
            if (characters > _outputRemaining) return false;
            _outputRemaining -= characters;
            return true;
        }

        public void Check()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _cancellation.ThrowIfCancellationRequested();
            if (_timer.ElapsedMilliseconds > MaxReadMilliseconds)
                throw new DebugReadException("time_limit", "Debug read exceeded its time limit. Request fewer items.");
            if (_owner._disposed)
                throw new DebugReadException("disposed", "The Visual Studio debug service is no longer available.");
            if (_id != null && (!_owner._stops.Matches(_id, _revision) || _owner._dte?.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode))
                throw new DebugReadException("stale_snapshot", "The debugger stop changed while reading. Capture a new snapshot.");
        }
    }

    private sealed class DebugReadException : Exception
    {
        public string Code { get; }
        public DebugReadException(string code, string message) : base(message) => Code = code;
    }
}
