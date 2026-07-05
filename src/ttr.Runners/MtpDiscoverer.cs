using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using StreamJsonRpc;
using Ttr.Core;

namespace Ttr.Runners;

/// <summary>
/// MTP server-mode discovery over JSON-RPC (plan §6.4, validated by POC-2). Every binding rule applies:
/// <list type="bullet">
/// <item>ttr LISTENS first on an ephemeral port; the host connects outward via
///   <c>--server --client-host localhost --client-port &lt;port&gt;</c>.</item>
/// <item>StreamJsonRpc with LSP <c>Content-Length</c> framing (<see cref="HeaderDelimitedMessageHandler"/>),
///   tolerating the server's extra <c>Content-Type: application/testingplatform</c> header.</item>
/// <item>Named params via <c>InvokeWithParameterObjectAsync</c>; the <c>tests</c> field is OMITTED for
///   discover-all (never sent as null — MTP's deserializer crashes on null).</item>
/// <item>Completion is the sentinel notification (<c>testing/testUpdates/tests</c> with <c>changes: null</c>),
///   which arrives BEFORE the response — the sentinel is primary.</item>
/// <item>ONE request in flight; <c>exit</c> then a 5 s timeout then <c>Process.Kill(entireProcessTree)</c>.</item>
/// <item>Unknown notifications (<c>telemetry/update</c>, <c>client/log</c>) are tolerated and dropped.</item>
/// <item>Smoke validation (§6.2): a host that exits without a handshake, or a project that discovers zero
///   tests, becomes a warning/error node — never a silently empty subtree.</item>
/// </list>
/// Test nodes carry structured <c>location.file</c> / <c>location.line-start</c>, used as the default 'o'
/// target (wired via the node's file references). Tuned against the MTP fixtures in M8.
/// </summary>
public static class MtpDiscoverer
{
    public const string AdapterKind = "mtp";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The MTP versions POC-2 validated as protocol-identical; a major jump outside this warns.</summary>
    private const int MinTestedMajor = 1;
    private const int MaxTestedMajor = 2;

    public static async Task DiscoverAsync(
        string projectPath, string assemblyPath, string tfm, string? tfmForNode,
        ChannelWriter<AppEvent> events, string? logPath, CancellationToken ct)
    {
        void Fail(NoticeSeverity sev, string summary, string? detail = null) =>
            events.TryWrite(new AppEvent.DiscoveryFailed(projectPath, tfmForNode, new NodeNotice(sev, summary, detail)));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Process? host = null;
        try
        {
            host = LaunchHost(assemblyPath, port);
            if (host is null) { Fail(NoticeSeverity.Error, "could not launch MTP test host"); return; }

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(HandshakeTimeout);

            TcpClient client;
            try
            {
                var accept = listener.AcceptTcpClientAsync(handshakeCts.Token).AsTask();
                var exited = host.WaitForExitAsync(handshakeCts.Token);
                var winner = await Task.WhenAny(accept, exited).ConfigureAwait(false);
                if (winner == exited && !accept.IsCompleted)
                {
                    // Host exited before ever connecting → the POC-9 silent-no-op class (§6.2).
                    Fail(NoticeSeverity.Error, "MTP host exited without a handshake",
                        $"The server process exited (code {SafeExitCode(host)}) before completing the connection.");
                    return;
                }
                client = await accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Fail(NoticeSeverity.Error, "MTP host did not connect", "Timed out waiting for the server handshake.");
                return;
            }

            using (client)
            using (var stream = client.GetStream())
            {
                var count = await RunSessionAsync(projectPath, tfm, tfmForNode, stream, events, ct).ConfigureAwait(false);
                if (count == 0)
                    Fail(NoticeSeverity.Warning, "test project discovered zero tests",
                        "The MTP host handshook but reported no tests — a silent no-op configuration? (plan §6.2)");
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            Fail(NoticeSeverity.Error, "MTP discovery failed", ex.Message);
        }
        finally
        {
            listener.Stop();
            await ShutdownHostAsync(host).ConfigureAwait(false);
        }
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

    private static async Task<int> RunSessionAsync(
        string projectPath, string tfm, string? tfmForNode, Stream stream,
        ChannelWriter<AppEvent> events, CancellationToken ct)
    {
        var handler = new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter());
        using var rpc = new JsonRpc(handler);

        var discovered = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = new List<TestIdentity>();

        // The host streams test-node updates here; changes:null is the completion sentinel (primary).
        rpc.AddLocalRpcMethod("testing/testUpdates/tests", (JsonElement args) =>
        {
            if (!args.TryGetProperty("changes", out var changes) || changes.ValueKind == JsonValueKind.Null)
            {
                if (batch.Count > 0) { events.TryWrite(new AppEvent.TestsDiscovered([.. batch])); batch.Clear(); }
                completion.TrySetResult();   // sentinel
                return;
            }
            if (changes.ValueKind != JsonValueKind.Array) return;
            foreach (var change in changes.EnumerateArray())
            {
                var identity = MapNode(change, projectPath, tfm);
                if (identity is null) continue;
                batch.Add(identity);
                discovered++;
            }
            if (batch.Count >= 200) { events.TryWrite(new AppEvent.TestsDiscovered([.. batch])); batch.Clear(); }
        });

        rpc.StartListening();

        // initialize — capture serverInfo.version, warn on a major-version jump outside the tested range.
        var initResult = await rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "ttr", version = "0.3.0" },
            capabilities = new { testing = new { debuggerProvider = false } },
        }, ct).ConfigureAwait(false);
        WarnOnVersion(initResult, projectPath, tfmForNode, events);

        // discoverTests — 'tests' OMITTED (discover all); one request in flight.
        var discoverTask = rpc.InvokeWithParameterObjectAsync<JsonElement>("testing/discoverTests", new
        {
            runId = Guid.NewGuid().ToString(),
        }, ct);

        // Sentinel is primary completion; the response is secondary. Whichever lands first ends the wait.
        await Task.WhenAny(completion.Task, discoverTask).ConfigureAwait(false);
        try { await discoverTask.ConfigureAwait(false); } catch (RemoteInvocationException) { }

        if (batch.Count > 0) events.TryWrite(new AppEvent.TestsDiscovered([.. batch]));

        try { await rpc.NotifyAsync("exit").ConfigureAwait(false); } catch (Exception) { }
        return discovered;
    }

    /// <summary>Map one MTP node change to a leaf identity, if it is a test node. Uses the structured
    /// location for the default 'o' target where present. Best-effort field parsing (tuned in M8).</summary>
    private static TestIdentity? MapNode(JsonElement change, string projectPath, string tfm)
    {
        if (!change.TryGetProperty("node", out var node)) return null;

        var uid = GetString(node, "uid");
        var displayName = GetString(node, "display-name") ?? uid;
        if (string.IsNullOrEmpty(displayName)) return null;

        // Only surface test nodes (skip group/action nodes without an execution state where identifiable).
        var nodeType = GetString(node, "node-type");
        if (nodeType is not null && !nodeType.Contains("test", StringComparison.OrdinalIgnoreCase)
            && !nodeType.Contains("action", StringComparison.OrdinalIgnoreCase))
            return null;

        var (ns, cls, method) = SplitDisplayName(displayName);
        var id = TestCaseId.ForCase(AdapterKind, projectPath, tfm, uid ?? displayName, null);
        return new TestIdentity(id, projectPath, tfm, ns, cls, method);
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

    private static void WarnOnVersion(
        JsonElement initResult, string projectPath, string? tfm, ChannelWriter<AppEvent> events)
    {
        var version = initResult.TryGetProperty("serverInfo", out var si) ? GetString(si, "version") : null;
        if (version is null) return;
        var major = ParseMajor(version);
        if (major is { } m && (m < MinTestedMajor || m > MaxTestedMajor))
            events.TryWrite(new AppEvent.DiscoveryFailed(projectPath, tfm, new NodeNotice(
                NoticeSeverity.Info, $"MTP serverInfo.version {version} outside tested range 1.x–2.x",
                "Protocol was validated across MTP 1.9.1–2.2.3; a newer major may behave differently.")));
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
}
