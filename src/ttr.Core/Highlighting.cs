namespace Ttr.Core;

/// <summary>
/// Semantic colour of a highlighted source span (plan §11.5). Kept as an enum in Core so the domain
/// carries no ANSI/terminal codes; the renderer maps these to actual colours. Produced by the Ui-layer
/// ColorCode highlighter off the render thread and delivered via <see cref="AppEvent.HighlightReady"/>.
/// </summary>
public enum HlColor
{
    Default,
    Keyword,
    Type,
    String,
    Comment,
    Number,
    Preprocessor,
    Identifier,
}

/// <summary>A run of source text sharing one colour. Lines are lists of these; the renderer lays them
/// out cell-aware (truncate/wrap) so a highlighted line never bleeds past the modal (CLAUDE.md inv. 5).</summary>
public readonly record struct HlSpan(string Text, HlColor Color);
