using TtrParser;

namespace Ttr.Core;

/// <summary>
/// State of the open 'o' file-reference modal (plan §11.5, brief M5). Captures ALL input while
/// present. <see cref="Refs"/> is the source node's ordered reference list (including unresolved
/// entries so 'n'/'p' can traverse and skip them); <see cref="Index"/> is the current reference.
/// The current file's <see cref="Lines"/> are read plain first; <see cref="Highlighted"/> is filled
/// in off the render thread (plain-text first paint, plan §11.5).
/// </summary>
public sealed record ModalState
{
    public required IReadOnlyList<FileRef> Refs { get; init; }
    public int Index { get; init; }
    public int Scroll { get; init; }
    public bool Wrap { get; init; }

    /// <summary>Absolute path of the currently displayed file.</summary>
    public string FilePath { get; init; } = "";

    /// <summary>1-based line the modal opened at (highlighted); null if the ref carried no line.</summary>
    public int? TargetLine { get; init; }

    /// <summary>Plain file lines (first paint).</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>Colour-span lines once ColorCode highlighting completes (keyed to <see cref="FilePath"/>); null until ready.</summary>
    public IReadOnlyList<IReadOnlyList<HlSpan>>? Highlighted { get; init; }

    public FileRef Current => Refs[Index];
}
