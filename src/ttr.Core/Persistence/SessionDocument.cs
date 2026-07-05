using System.Text.Json.Serialization;

namespace Ttr.Core.Persistence;

/// <summary>
/// The <c>state.json</c> schema v1, exactly per plan Appendix A (property names are load-bearing — they are
/// the on-disk contract). Plain <c>System.Text.Json</c> DTOs; POC-8 found source-generation made no measurable
/// difference (the cost is filesystem I/O, not serialisation), so none is used.
/// </summary>
public sealed class SessionDocument
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; }
    [JsonPropertyName("savedUtc")] public DateTime SavedUtc { get; set; }
    [JsonPropertyName("targets")] public List<TargetEntry> Targets { get; set; } = [];
    [JsonPropertyName("ui")] public UiBlock Ui { get; set; } = new();
    [JsonPropertyName("tests")] public List<TestEntry> Tests { get; set; } = [];
    [JsonPropertyName("runs")] public RunsBlock Runs { get; set; } = new();

    public sealed class TargetEntry
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    }

    public sealed class UiBlock
    {
        [JsonPropertyName("filters")] public FiltersBlock Filters { get; set; } = new();
        [JsonPropertyName("expanded")] public List<string> Expanded { get; set; } = [];
        [JsonPropertyName("selected")] public string? Selected { get; set; }
        [JsonPropertyName("scroll")] public ScrollBlock Scroll { get; set; } = new();
    }

    public sealed class FiltersBlock
    {
        [JsonPropertyName("failedOnly")] public bool FailedOnly { get; set; }
        [JsonPropertyName("details")] public bool Details { get; set; }
        [JsonPropertyName("detailsBottom")] public bool DetailsBottom { get; set; }
        [JsonPropertyName("wrap")] public bool Wrap { get; set; }
        [JsonPropertyName("times")] public bool Times { get; set; }
    }

    public sealed class ScrollBlock
    {
        [JsonPropertyName("tree")] public int Tree { get; set; }
        [JsonPropertyName("detail")] public int Detail { get; set; }
    }

    public sealed class TestEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("durationMs")] public long DurationMs { get; set; }
        [JsonPropertyName("finishedUtc")] public DateTime FinishedUtc { get; set; }
        [JsonPropertyName("hasDetail")] public bool HasDetail { get; set; }
    }

    public sealed class RunsBlock
    {
        [JsonPropertyName("keep")] public int Keep { get; set; } = SessionStore.RunsToKeep;
        [JsonPropertyName("latest")] public string? Latest { get; set; }
    }
}

/// <summary>One packed detail record (a line of <c>details.jsonl</c>). Self-describing (carries its id) so the
/// jsonl is meaningful without the sidecar index; the <c>details.idx</c> maps id → byte offset/length for the
/// lazy seek (brief M5 / POC-8).</summary>
public sealed class DetailRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("exceptionChain")] public List<string>? ExceptionChain { get; set; }
    [JsonPropertyName("stackTrace")] public string? StackTrace { get; set; }
    [JsonPropertyName("stdout")] public string? StandardOutput { get; set; }

    public static DetailRecord From(string id, TestResultDetail d) => new()
    {
        Id = id,
        Message = d.Message,
        ExceptionChain = d.ExceptionChain?.ToList(),
        StackTrace = d.StackTrace,
        StandardOutput = d.StandardOutput,
    };

    public TestResultDetail ToDetail() => new(Message, ExceptionChain, StackTrace, StandardOutput);
}
