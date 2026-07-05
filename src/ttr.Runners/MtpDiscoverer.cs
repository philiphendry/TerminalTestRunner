using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using StreamJsonRpc;
using Ttr.Core;

namespace Ttr.Runners;

/// <summary>
/// A persistent MTP server-mode session over JSON-RPC (plan §6.4, validated by POC-2, corrected against the
/// live protocol in Phase 3). ONE session per (project, TFM): the host process and JSON-RPC connection are
/// launched once (<see cref="StartAsync"/>), discovered against (<see cref="DiscoverAsync"/>), and then
/// REUSED for every subsequent run (<see cref="RunAsync"/>) — process reuse across runs on the same build is
/// the point (brief M3). The host is restarted only after a rebuild (staleness), which the caller effects by
/// disposing and recreating the session. Every binding rule applies:
/// <list type="bullet">
/// <item>ttr LISTENS first on an ephemeral port; the host connects outward via
///   <c>--server --client-host localhost --client-port &lt;port&gt;</c>.</item>
/// <item>StreamJsonRpc with LSP <c>Content-Length</c> framing (<see cref="HeaderDelimitedMessageHandler"/>).</item>
/// <item>Params are a SINGLE named object (<c>UseSingleObjectParameterDeserialization</c>) with FLAT dotted
///   keys; the <c>tests</c> field is OMITTED for discover/run-all (never null), a <c>{uid, display-name}</c>
///   array for a subset.</item>
/// <item>Completion is the sentinel notification (<c>testing/testUpdates/tests</c> with <c>changes: null</c>),
///   which arrives BEFORE the response — the sentinel is primary.</item>
/// <item>Cancellation is <c>$/cancelRequest</c>: StreamJsonRpc emits it automatically when the run token is
///   cancelled, the host cancels (~2 s) and SURVIVES to service the next run (brief M3/AC3).</item>
/// <item>ONE request in flight; <c>exit</c> then a 5 s timeout then <c>Process.Kill(entireProcessTree)</c>.</item>
/// <item>Unknown notifications (<c>telemetry/update</c>, <c>client/log</c>) are tolerated and dropped.</item>
/// </list>
/// Test nodes carry structured <c>location.file</c> / <c>location.line-start</c> (the default 'o' target).
/// Run nodes carry <c>execution-state</c> (in-progress → Running; passed/failed/skipped → finished),
/// <c>time.duration-ms</c>, and <c>error.message</c> / <c>error.stacktrace</c> for failures.
/// </summary>
public sealed class MtpSession : IAsyncDisposable
{
    public const string AdapterKind = "mtp";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The MTP versions POC-2 validated as protocol-identical; a major jump outside this warns.</summary>
    private const int MinTestedMajor = 1;
    private const int MaxTestedMajor = 2;

    private readonly string _projectPath;
    private readonly string _assemblyPath;
    private readonly string _tfm;
    private readonly string? _tfmForNode;

    private TcpListener? _listener;
    private Process? _host;
    private TcpClient? _client;
    private Stream? _stream;
    private JsonRpc? _rpc;
    private Sink? _sink;

    public MtpSession(string projectPath, string assemblyPath, string tfm, string? tfmForNode)
    {
        _projectPath = projectPath;
        _assemblyPath = assemblyPath;
        _tfm = tfm;
        _tfmForNode = tfmForNode;
    }

    public string ProjectPath => _projectPath;
    public string Tfm => _tfm;
    public string? ServerVersion { get; private set; }

    private void Fail(ChannelWriter<AppEvent> events, NoticeSeverity sev, string summary, string? detail = null) =>
        events.TryWrite(new AppEvent.DiscoveryFailed(_projectPath, _tfmForNode, new NodeNotice(sev, summary, detail)));

    /// <summary>Launch the host, accept its outward connection, and complete the JSON-RPC handshake. Emits a
    /// smoke-validation notice and returns false if the host never connects / never handshakes (§6.2).</summary>
    public async Task<bool> StartAsync(ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _host = LaunchHost(_assemblyPath, port);
        if (_host is null) { Fail(events, NoticeSeverity.Error, "could not launch MTP test host"); return false; }

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeCts.CancelAfter(HandshakeTimeout);
        try
        {
            var accept = _listener.AcceptTcpClientAsync(handshakeCts.Token).AsTask();
            var exited = _host.WaitForExitAsync(handshakeCts.Token);
            var winner = await Task.WhenAny(accept, exited).ConfigureAwait(false);
            if (winner == exited && !accept.IsCompleted)
            {
                Fail(events, NoticeSeverity.Error, "MTP host exited without a handshake",
                    $"The server process exited (code {SafeExitCode(_host)}) before completing the connection.");
                return false;
            }
            _client = await accept.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail(events, NoticeSeverity.Error, "MTP host did not connect", "Timed out waiting for the server handshake.");
            return false;
        }

        _stream = _client.GetStream();
        var handler = new HeaderDelimitedMessageHandler(_stream, _stream, new SystemTextJsonFormatter());
        _rpc = new JsonRpc(handler);
        _sink = new Sink(_projectPath, _tfm, events);   // fallback writer until the first Begin sets one
        _rpc.AddLocalRpcTarget(_sink, new JsonRpcTargetOptions { AllowNonPublicInvocation = true });
        _rpc.StartListening();

        var initResult = await _rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "ttr", version = "0.4.0" },
            capabilities = new { testing = new { debuggerProvider = false } },
        }, ct).ConfigureAwait(false);
        ServerVersion = initResult.TryGetProperty("serverInfo", out var si) ? GetString(si, "version") : null;
        WarnOnVersion(events);
        return true;
    }

    /// <summary>Send <c>testing/discoverTests</c> (tests omitted), stream nodes into the tree, wait on the
    /// sentinel, and raise the §6.2 zero-tests warning if nothing was reported. Returns the discovered count.</summary>
    public async Task<int> DiscoverAsync(ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        if (_rpc is null || _sink is null) return 0;
        _sink.Begin(runMode: false, events);
        var discoverTask = _rpc.InvokeWithParameterObjectAsync<JsonElement>("testing/discoverTests", new
        {
            runId = Guid.NewGuid().ToString(),
        }, ct);
        await Task.WhenAny(_sink.Completion, discoverTask).ConfigureAwait(false);
        try { await discoverTask.ConfigureAwait(false); } catch (RemoteInvocationException) { }
        _sink.Flush();

        var count = _sink.Discovered;
        if (count == 0)
            Fail(events, NoticeSeverity.Warning, "test project discovered zero tests",
                "The MTP host handshook but reported no tests — a silent no-op configuration? (plan §6.2)");
        return count;
    }

    /// <summary>Run a subset (only ids this session owns; empty = every test in this host). in-progress →
    /// Running, terminal states → finished with detail. Cancellation → <c>$/cancelRequest</c> (host survives).</summary>
    public async Task RunAsync(IReadOnlyList<TestCaseId> subset, ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        if (_rpc is null || _sink is null) return;

        object runParams;
        if (subset.Count == 0)
        {
            // Run all: 'tests' OMITTED (never null — MTP's deserialiser crashes on null).
            runParams = new { runId = Guid.NewGuid().ToString() };
        }
        else
        {
            var tests = _sink.ResolveSubset(subset);
            if (tests.Count == 0) return;   // none of these ids belong to this session
            runParams = new { runId = Guid.NewGuid().ToString(), tests };
        }

        _sink.Begin(runMode: true, events);
        // The run token is passed to the outbound request: StreamJsonRpc auto-emits $/cancelRequest on
        // cancellation, the host cancels and the connection survives for the next run.
        var runTask = _rpc.InvokeWithParameterObjectAsync<JsonElement>("testing/runTests", runParams, ct);
        await Task.WhenAny(_sink.Completion, runTask).ConfigureAwait(false);
        try { await runTask.ConfigureAwait(false); }
        catch (RemoteInvocationException) { }
        catch (OperationCanceledException) { /* cancelled via $/cancelRequest; host survives */ }
        _sink.Flush();
    }

    private static Process? LaunchHost(string assemblyPath, int port)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath) ?? ".",
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(assemblyPath);
        psi.ArgumentList.Add("--server");
        psi.ArgumentList.Add("--client-host");
        psi.ArgumentList.Add("localhost");
        psi.ArgumentList.Add("--client-port");
        psi.ArgumentList.Add(port.ToString());
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";   // CLAUDE.md locale rule
        var proc = new Process { StartInfo = psi };
        return proc.Start() ? proc : null;
    }

    private void WarnOnVersion(ChannelWriter<AppEvent> events)
    {
        if (ServerVersion is null) return;
        var major = ParseMajor(ServerVersion);
        if (major is { } m && (m < MinTestedMajor || m > MaxTestedMajor))
            Fail(events, NoticeSeverity.Info, $"MTP serverInfo.version {ServerVersion} outside tested range 1.x–2.x",
                "Protocol was validated across MTP 1.9.1–2.2.3; a newer major may behave differently.");
    }

    private static int? ParseMajor(string version)
    {
        var dot = version.IndexOf('.');
        var head = dot < 0 ? version : version[..dot];
        return int.TryParse(head, out var m) ? m : null;
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return -1; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_rpc is not null)
        {
            try { await _rpc.NotifyAsync("exit").ConfigureAwait(false); } catch (Exception) { }
        }
        try { _rpc?.Dispose(); } catch (Exception) { }
        try { _stream?.Dispose(); } catch (Exception) { }
        try { _client?.Dispose(); } catch (Exception) { }
        await ShutdownHostAsync(_host).ConfigureAwait(false);
        try { _listener?.Stop(); } catch (Exception) { }
    }

    private static async Task ShutdownHostAsync(Process? host)
    {
        if (host is null) return;
        try
        {
            if (!host.HasExited)
            {
                using var cts = new CancellationTokenSource(ExitTimeout);
                try { await host.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { host.Kill(entireProcessTree: true); }
            }
        }
        catch (Exception) { /* best-effort */ }
        finally { host.Dispose(); }
    }

    /// <summary>Receives the host's JSON-RPC notifications for the current operation. Discovery nodes stream
    /// into <see cref="AppEvent.TestsDiscovered"/> batches and populate the run cache; run nodes emit
    /// started/finished immediately (MTP batches on its own 200 ms timer — the coalescing render loop absorbs
    /// the burst; no artificial pacing). The <c>changes: null</c> sentinel completes the operation.</summary>
    private sealed class Sink(string projectPath, string tfm, ChannelWriter<AppEvent> fallbackEvents)
    {
        private TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TestIdentity> _batch = [];
        // Derived id → the host's native (uid, display-name) for a subset run.
        private readonly Dictionary<TestCaseId, (string Uid, string Display)> _runCache = new();
        private ChannelWriter<AppEvent> _events = fallbackEvents;
        private bool _runMode;

        public Task Completion => _completion.Task;
        public int Discovered { get; private set; }

        /// <summary>Reset for a new discover/run operation: fresh sentinel + the current operation's writer
        /// (each discover/run supplies its own; in the composed app it is the one shared channel).</summary>
        public void Begin(bool runMode, ChannelWriter<AppEvent> events)
        {
            _runMode = runMode;
            _events = events;
            _batch.Clear();
            if (!runMode) Discovered = 0;
            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Native {uid, display-name} objects for the subset ids this session owns.</summary>
        public IReadOnlyList<object> ResolveSubset(IReadOnlyList<TestCaseId> subset)
        {
            var result = new List<object>();
            foreach (var id in subset)
                if (_runCache.TryGetValue(id, out var entry))
                    result.Add(new Dictionary<string, object> { ["uid"] = entry.Uid, ["display-name"] = entry.Display });
            return result;
        }

        [JsonRpcMethod("testing/testUpdates/tests", UseSingleObjectParameterDeserialization = true)]
        public void TestUpdates(JsonElement p)
        {
            if (!p.TryGetProperty("changes", out var changes) || changes.ValueKind == JsonValueKind.Null)
            {
                Flush();
                _completion.TrySetResult();   // sentinel (primary completion)
                return;
            }
            if (changes.ValueKind != JsonValueKind.Array) return;
            foreach (var change in changes.EnumerateArray())
                HandleChange(change);
            if (!_runMode && _batch.Count >= 200) Flush();
        }

        [JsonRpcMethod("client/log", UseSingleObjectParameterDeserialization = true)]
        public void ClientLog(JsonElement p) { }

        [JsonRpcMethod("telemetry/update", UseSingleObjectParameterDeserialization = true)]
        public void Telemetry(JsonElement p) { }

        public void Flush()
        {
            if (_batch.Count == 0) return;
            _events.TryWrite(new AppEvent.TestsDiscovered([.. _batch]));
            _batch.Clear();
        }

        private void HandleChange(JsonElement change)
        {
            if (change.TryGetProperty("node", out var node) is false) return;
            if (MapNode(node) is not { } mapped) return;
            var (identity, uid, display, state) = mapped;

            _runCache[identity.Id] = (uid, display);

            switch (state)
            {
                case "in-progress":
                    _events.TryWrite(new AppEvent.TestStarted(identity));
                    break;
                case "passed":
                case "failed":
                case "skipped":
                case "error":
                case "timeout":
                    _events.TryWrite(new AppEvent.TestFinished(identity, Outcome(state), Duration(node), Detail(state, node)));
                    break;
                case "cancelled":
                    // Leave it Running; the reducer's RunCompleted sweep flips it to NotRun (no phantom spinner).
                    break;
                default:
                    // "discovered" (or absent) → a discovery node.
                    _batch.Add(identity);
                    Discovered++;
                    break;
            }
        }

        private static TestOutcome Outcome(string state) => state switch
        {
            "passed" => TestOutcome.Passed,
            "skipped" => TestOutcome.Skipped,
            _ => TestOutcome.Failed,   // failed / error / timeout
        };

        private static TimeSpan Duration(JsonElement node) =>
            node.TryGetProperty("time.duration-ms", out var d) && d.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromMilliseconds(d.GetDouble())
                : TimeSpan.Zero;

        private static TestResultDetail? Detail(string state, JsonElement node)
        {
            if (state is "passed" or "skipped") return null;   // parity with the fake adapter (failures carry detail)
            var message = GetString(node, "error.message");
            var stack = GetString(node, "error.stacktrace");
            if (message is null && stack is null) return null;
            return new TestResultDetail(Message: message, ExceptionChain: null, StackTrace: stack, StandardOutput: null);
        }

        /// <summary>Map an MTP node (flat dotted keys) to a <see cref="TestIdentity"/> plus its raw uid/display
        /// /execution-state. Theory rows share a <c>location.method</c> signature (e.g. <c>Even(System.Int32)</c>)
        /// while their <c>display-name</c> carries the concrete args (<c>Even(n: 2)</c>): key the Method on the
        /// BARE name (<c>Even</c>) so rows land as distinct Case leaves under one Method (never collapsed).</summary>
        private (TestIdentity Identity, string Uid, string Display, string State)? MapNode(JsonElement node)
        {
            var displayName = GetString(node, "display-name") ?? GetString(node, "uid");
            if (string.IsNullOrEmpty(displayName)) return null;

            var nodeType = GetString(node, "node-type");
            if (nodeType is not null
                && !nodeType.Contains("test", StringComparison.OrdinalIgnoreCase)
                && !nodeType.Contains("action", StringComparison.OrdinalIgnoreCase))
                return null;

            var typeName = GetString(node, "location.type");
            var methodSig = GetString(node, "location.method");

            string ns, cls, method, bareMethod;
            if (typeName is not null && methodSig is not null)
            {
                var typeDot = typeName.LastIndexOf('.');
                ns = typeDot < 0 ? "" : typeName[..typeDot];
                cls = typeDot < 0 ? typeName : typeName[(typeDot + 1)..];
                var paren = methodSig.IndexOf('(');
                bareMethod = paren < 0 ? methodSig : methodSig[..paren];
                method = bareMethod;
            }
            else
            {
                (ns, cls, method) = SplitDisplayName(displayName);
                bareMethod = method;
            }

            // Derive the row label: the display-name suffix after "type.bareMethod". If the suffix is empty
            // or is exactly the type-signature (the method-level node itself), it's a plain test/method.
            string? caseDisplay = null;
            if (typeName is not null)
            {
                var prefix = $"{typeName}.{bareMethod}";
                if (displayName.StartsWith(prefix, StringComparison.Ordinal) && displayName.Length > prefix.Length)
                {
                    var suffix = displayName[prefix.Length..];
                    // "(System.Int32)" == the location.method signature → the method node itself, not a row.
                    var sig = methodSig is { } ms && ms.Length > bareMethod.Length ? ms[bareMethod.Length..] : null;
                    if (suffix != sig) caseDisplay = suffix.Trim();
                }
            }

            var uid = GetString(node, "uid") ?? displayName;
            var id = TestCaseId.ForCase(AdapterKind, projectPath, tfm, uid, caseDisplay);
            var file = GetString(node, "location.file");
            var line = GetInt(node, "location.line-start");
            var state = GetString(node, "execution-state") ?? "discovered";
            var identity = new TestIdentity(id, projectPath, tfm, ns, cls, method, caseDisplay, file, line);
            return (identity, uid, displayName, state);
        }
    }

    /// <summary>Split a dotted display name into ns/class/method; falls back to a flat method name.</summary>
    internal static (string Namespace, string Class, string Method) SplitDisplayName(string name)
    {
        var lastDot = name.LastIndexOf('.');
        if (lastDot < 0) return ("", "", name);
        var method = name[(lastDot + 1)..];
        var type = name[..lastDot];
        var typeDot = type.LastIndexOf('.');
        return typeDot < 0 ? ("", type, method) : (type[..typeDot], type[(typeDot + 1)..], method);
    }

    private static int? GetInt(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}

/// <summary>
/// Backward-compatible one-shot discovery entry point (used where a persistent session is not needed, e.g. the
/// discovery-only integration test): starts a <see cref="MtpSession"/>, discovers, and tears it down — asserting
/// no orphaned host survives. Live sessions for runs are owned by the backend.
/// </summary>
public static class MtpDiscoverer
{
    public const string AdapterKind = MtpSession.AdapterKind;

    public static async Task DiscoverAsync(
        string projectPath, string assemblyPath, string tfm, string? tfmForNode,
        ChannelWriter<AppEvent> events, string? logPath, CancellationToken ct)
    {
        var session = new MtpSession(projectPath, assemblyPath, tfm, tfmForNode);
        try
        {
            if (await session.StartAsync(events, ct).ConfigureAwait(false))
                await session.DiscoverAsync(events, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            events.TryWrite(new AppEvent.DiscoveryFailed(projectPath, tfmForNode,
                new NodeNotice(NoticeSeverity.Error, "MTP discovery failed", ex.Message)));
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
