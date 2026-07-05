using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ttr.Core.Persistence;

/// <summary>Why a <c>--continue</c> load did not restore a live session (brief M4). Every non-<see cref="Restored"/>
/// outcome degrades to a fresh session with a specific, non-crashing notice.</summary>
public enum RestoreStatus
{
    Restored,
    NoState,          // nothing to restore (first run in this .ttr) — silent fresh session
    SchemaMismatch,   // state.json is a different schema version
    TargetsChanged,   // the target set / a project file changed since the save
    Corrupt,          // state.json was unreadable / torn
}

/// <summary>The parsed prior session (brief M4): the restored UI, the per-test summaries (with finish time, so
/// the CLI can compute staleness against the on-disk assembly), and when it was saved.</summary>
public sealed record RestoredSession(
    IReadOnlyList<RestoredSession.Summary> Tests,
    RestoredUi Ui,
    DateTime SavedUtc)
{
    public sealed record Summary(TestCaseId Id, TestStatus Status, TimeSpan Duration, bool HasDetail, DateTime FinishedUtc);
}

/// <summary>The result of a restore attempt.</summary>
public sealed record RestoreOutcome(RestoreStatus Status, RestoredSession? Session = null);

/// <summary>
/// The <c>.ttr/</c> state store (plan §10, POC-8 shape): <c>state.json</c> (summaries + UI + target
/// fingerprint) plus one packed <c>results/&lt;runId&gt;/details.jsonl</c> + <c>details.idx</c> per run. Every
/// write is temp-file + <see cref="File.Move(string, string, bool)"/> (atomic; a kill -9 leaves the old or new
/// file, never a torn one) — two renames for a run's details, one for state.json, and the details are written
/// BEFORE state.json so a crash can only ever leave state.json pointing at a fully-written run. No compression
/// (POC-8: the 10k footprint is well inside the 20 MB budget); plain <c>System.Text.Json</c>. Old runs prune to
/// the most recent <see cref="RunsToKeep"/>. Save/read run on background tasks (never the reducer/render loop);
/// an internal lock serialises disk writes (this is IO plumbing, not MVU state — CLAUDE.md's no-locks rule is
/// about state mutation).
/// </summary>
public sealed class SessionStore
{
    public const int RunsToKeep = 3;
    private const string RunPrefix = "run-";

    private readonly string _stateDir;
    private readonly string _resultsDir;
    private readonly string _statePath;
    private readonly IReadOnlyList<SessionDocument.TargetEntry> _fingerprint;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();

    private DateTime _createdUtc;
    private string? _currentRunDir;                       // where live details are read from (restore or last save)
    private Dictionary<string, long[]>? _idxCache;        // id -> [offset, length] for _currentRunDir
    private string? _idxCacheDir;

    public SessionStore(
        string stateDir, IReadOnlyList<SessionDocument.TargetEntry> fingerprint, Func<DateTime>? clock = null)
    {
        _stateDir = stateDir;
        _resultsDir = Path.Combine(stateDir, "results");
        _statePath = Path.Combine(stateDir, "state.json");
        _fingerprint = fingerprint;
        _clock = clock ?? (() => DateTime.UtcNow);
        _createdUtc = _clock();
    }

    public string StateDir => _stateDir;

    // --- Fingerprint ------------------------------------------------------------

    /// <summary>Content-hash the target set (primary solution, if any, + every project file), plan §10. The
    /// paths + hashes are the fingerprint <c>--continue</c> compares against so an edited/added/removed project
    /// degrades gracefully instead of restoring stale results against a different tree.</summary>
    public static IReadOnlyList<SessionDocument.TargetEntry> Fingerprint(
        string? primaryTarget, IReadOnlyList<string> projectPaths)
    {
        var entries = new List<SessionDocument.TargetEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var full = Path.GetFullPath(path);
            if (!seen.Add(full)) return;
            entries.Add(new SessionDocument.TargetEntry { Path = full, Hash = HashFile(full) });
        }
        Add(primaryTarget);
        foreach (var p in projectPaths) Add(p);
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }

    private static string HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException) { return "sha256:?"; }
        catch (UnauthorizedAccessException) { return "sha256:?"; }
    }

    private bool FingerprintMatches(List<SessionDocument.TargetEntry> stored)
    {
        if (stored.Count != _fingerprint.Count) return false;
        var byPath = _fingerprint.ToDictionary(e => e.Path, e => e.Hash, StringComparer.Ordinal);
        foreach (var e in stored)
            if (!byPath.TryGetValue(e.Path, out var hash) || hash != e.Hash)
                return false;
        return true;
    }

    // --- Restore ----------------------------------------------------------------

    public RestoreOutcome Restore()
    {
        if (!File.Exists(_statePath)) return new RestoreOutcome(RestoreStatus.NoState);

        SessionDocument? doc;
        try { doc = JsonSerializer.Deserialize<SessionDocument>(File.ReadAllText(_statePath)); }
        catch (JsonException) { return new RestoreOutcome(RestoreStatus.Corrupt); }
        catch (IOException) { return new RestoreOutcome(RestoreStatus.Corrupt); }
        if (doc is null) return new RestoreOutcome(RestoreStatus.Corrupt);

        if (doc.Schema != SessionDocument.CurrentSchema) return new RestoreOutcome(RestoreStatus.SchemaMismatch);
        if (!FingerprintMatches(doc.Targets)) return new RestoreOutcome(RestoreStatus.TargetsChanged);

        var tests = new List<RestoredSession.Summary>(doc.Tests.Count);
        foreach (var t in doc.Tests)
        {
            if (!Enum.TryParse<TestStatus>(t.Status, out var status)) continue;
            tests.Add(new RestoredSession.Summary(
                new TestCaseId(t.Id), status, TimeSpan.FromMilliseconds(t.DurationMs), t.HasDetail, t.FinishedUtc));
        }

        var ui = new RestoredUi(
            doc.Ui.Filters.FailedOnly, doc.Ui.Filters.Times, doc.Ui.Filters.Details,
            doc.Ui.Filters.DetailsBottom, doc.Ui.Filters.Wrap,
            doc.Ui.Expanded, doc.Ui.Selected, doc.Ui.Scroll.Tree, doc.Ui.Scroll.Detail);

        // Preserve createdUtc across saves; point lazy detail reads at the restored run.
        _createdUtc = doc.CreatedUtc == default ? _createdUtc : doc.CreatedUtc;
        lock (_gate)
        {
            _currentRunDir = doc.Runs.Latest is { Length: > 0 } latest ? Path.Combine(_resultsDir, latest) : null;
            _idxCache = null;
            _idxCacheDir = null;
        }
        return new RestoreOutcome(RestoreStatus.Restored, new RestoredSession(tests, ui, doc.SavedUtc));
    }

    // --- Save -------------------------------------------------------------------

    /// <summary>Persist a snapshot: write the run's details (jsonl + idx) then state.json, all atomically, then
    /// prune. Serialised by an internal lock so overlapping run-completions (watch) can't collide.</summary>
    public void Save(SessionSnapshot snapshot)
    {
        lock (_gate)
        {
            EnsureDir();
            var now = _clock();
            var runId = NextRunId();
            var runDir = Path.Combine(_resultsDir, runId);
            Directory.CreateDirectory(runDir);

            var withDetail = WriteDetails(snapshot, runDir);   // ids that actually got a detail record
            WriteState(snapshot, now, runId, withDetail);
            Prune();

            _currentRunDir = runDir;
            _idxCache = null;
            _idxCacheDir = null;
        }
    }

    /// <summary>Write <c>details.jsonl</c> + <c>details.idx</c>, carrying forward any restored-but-unopened
    /// detail from the previous run so it is never lost across saves. Returns the set of ids that got a record.</summary>
    private HashSet<string> WriteDetails(SessionSnapshot snapshot, string runDir)
    {
        var idx = new Dictionary<string, long[]>(StringComparer.Ordinal);
        var jsonlTmp = Path.Combine(runDir, "details.jsonl.tmp");
        var idxTmp = Path.Combine(runDir, "details.idx.tmp");

        using (var fs = new FileStream(jsonlTmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            long offset = 0;
            foreach (var t in snapshot.Tests)
            {
                if (!t.HasDetail) continue;
                var detail = t.Detail ?? ReadDetailFrom(_currentRunDir, t.Id.Value);
                if (detail is null || detail.IsEmpty) continue;   // carry-forward failed → drop hasDetail
                var record = DetailRecord.From(t.Id.Value, detail);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
                fs.Write(bytes);
                fs.WriteByte((byte)'\n');
                idx[t.Id.Value] = [offset, bytes.Length];
                offset += bytes.Length + 1;
            }
        }

        File.WriteAllBytes(idxTmp, JsonSerializer.SerializeToUtf8Bytes(idx));
        File.Move(jsonlTmp, Path.Combine(runDir, "details.jsonl"), overwrite: true);
        File.Move(idxTmp, Path.Combine(runDir, "details.idx"), overwrite: true);
        return idx.Keys.ToHashSet(StringComparer.Ordinal);
    }

    private void WriteState(SessionSnapshot snapshot, DateTime now, string runId, HashSet<string> withDetail)
    {
        var doc = new SessionDocument
        {
            Schema = SessionDocument.CurrentSchema,
            CreatedUtc = _createdUtc,
            SavedUtc = now,
            Targets = _fingerprint.ToList(),
            Ui = new SessionDocument.UiBlock
            {
                Filters = new SessionDocument.FiltersBlock
                {
                    FailedOnly = snapshot.Ui.FailedOnly,
                    Details = snapshot.Ui.DetailVisible,
                    DetailsBottom = snapshot.Ui.DetailBottom,
                    Wrap = snapshot.Ui.WordWrap,
                    Times = snapshot.Ui.ShowDurations,
                },
                Expanded = snapshot.Ui.Expanded.ToList(),
                Selected = snapshot.Ui.Selected,
                Scroll = new SessionDocument.ScrollBlock { Tree = snapshot.Ui.TreeScroll, Detail = snapshot.Ui.DetailScroll },
            },
            Runs = new SessionDocument.RunsBlock { Keep = RunsToKeep, Latest = runId },
        };
        foreach (var t in snapshot.Tests)
            doc.Tests.Add(new SessionDocument.TestEntry
            {
                Id = t.Id.Value,
                Status = t.Status.ToString(),
                DurationMs = (long)t.Duration.TotalMilliseconds,
                FinishedUtc = now,
                HasDetail = withDetail.Contains(t.Id.Value),
            });

        var tmp = _statePath + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _statePath, overwrite: true);
    }

    // --- Lazy detail read (brief M5) --------------------------------------------

    /// <summary>Fetch one restored result's detail from the current run's packed jsonl via its idx offset
    /// (POC-8 bar: &lt; 20 ms; the idx is parsed once and cached). Null if absent/unreadable.</summary>
    public TestResultDetail? ReadDetail(TestCaseId id)
    {
        lock (_gate) return ReadDetailFrom(_currentRunDir, id.Value);
    }

    private TestResultDetail? ReadDetailFrom(string? runDir, string id)
    {
        if (runDir is null) return null;
        var idx = LoadIndex(runDir);
        if (idx is null || !idx.TryGetValue(id, out var span)) return null;
        try
        {
            using var fs = new FileStream(Path.Combine(runDir, "details.jsonl"), FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(span[0], SeekOrigin.Begin);
            var buffer = new byte[span[1]];
            var read = fs.Read(buffer, 0, buffer.Length);
            if (read != buffer.Length) return null;
            return JsonSerializer.Deserialize<DetailRecord>(buffer)?.ToDetail();
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private Dictionary<string, long[]>? LoadIndex(string runDir)
    {
        if (_idxCache is not null && _idxCacheDir == runDir) return _idxCache;
        var idxPath = Path.Combine(runDir, "details.idx");
        if (!File.Exists(idxPath)) return null;
        try
        {
            _idxCache = JsonSerializer.Deserialize<Dictionary<string, long[]>>(File.ReadAllText(idxPath))
                        ?? new Dictionary<string, long[]>(StringComparer.Ordinal);
            _idxCacheDir = runDir;
            return _idxCache;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    // --- Directory / run housekeeping -------------------------------------------

    private void EnsureDir()
    {
        Directory.CreateDirectory(_resultsDir);
        var gitignore = Path.Combine(_stateDir, ".gitignore");
        if (!File.Exists(gitignore))
            File.WriteAllText(gitignore, "*\n");   // the whole .ttr/ is machine-local (plan §10)
    }

    private string NextRunId()
    {
        var max = 0;
        if (Directory.Exists(_resultsDir))
            foreach (var dir in Directory.EnumerateDirectories(_resultsDir))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(RunPrefix, StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(RunPrefix.Length), out var n) && n > max)
                    max = n;
            }
        return RunPrefix + (max + 1);
    }

    private void Prune()
    {
        if (!Directory.Exists(_resultsDir)) return;
        var runs = Directory.EnumerateDirectories(_resultsDir)
            .Select(d => (Dir: d, N: RunNumber(Path.GetFileName(d))))
            .Where(r => r.N > 0)
            .OrderByDescending(r => r.N)
            .ToList();
        foreach (var (dir, _) in runs.Skip(RunsToKeep))
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private static int RunNumber(string name) =>
        name.StartsWith(RunPrefix, StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(RunPrefix.Length), out var n) ? n : 0;
}
