using ColorCode;
using ColorCode.Common;
using ColorCode.Parsing;
using Ttr.Core;

namespace Ttr.Ui;

/// <summary>
/// Turns source text into per-line colour spans using ColorCode.Core purely for tokenisation (plan
/// §11.5). ColorCode's shipped formatters emit HTML; we drive the parser directly and map its scope
/// names onto <see cref="HlColor"/>. Runs off the render thread (the orchestrator calls this and posts
/// <see cref="AppEvent.HighlightReady"/>); the modal shows a plain-text first paint until it arrives.
/// Unrecognised extensions return <c>null</c> so the modal renders plain.
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>The ColorCode language id for a file extension, or null if we should render plain.</summary>
    public static ILanguage? LanguageForExtension(string path)
    {
        var id = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".json" => "json",
            ".xml" or ".csproj" or ".props" or ".targets" or ".config" => "xml",
            ".fs" => "fsharp",
            ".vb" => "vb-net",
            ".js" => "javascript",
            ".ts" => "typescript",
            ".css" => "css",
            ".html" or ".htm" => "html",
            ".sql" => "sql",
            ".py" => "python",
            ".md" => "markdown",
            _ => null,
        };
        return id is null ? null : Languages.FindById(id);
    }

    public static IReadOnlyList<IReadOnlyList<HlSpan>>? Highlight(string source, ILanguage? language)
    {
        if (language is null) return null;
        var colorizer = new SpanColorizer();
        return colorizer.Highlight(source, language);
    }

    private sealed class SpanColorizer : CodeColorizerBase
    {
        private readonly List<List<HlSpan>> _lines = [];
        private List<HlSpan> _current = [];

        public SpanColorizer() : base(null, null) { }

        public IReadOnlyList<IReadOnlyList<HlSpan>> Highlight(string source, ILanguage language)
        {
            _lines.Clear();
            _current = [];
            languageParser.Parse(source, language, (parsed, scopes) => Write(parsed, scopes));
            _lines.Add(_current);
            return _lines;
        }

        protected override void Write(string parsedSourceCode, IList<Scope> scopes)
        {
            var n = parsedSourceCode.Length;
            if (n == 0) return;

            // Paint each character with its most-specific scope (children override parents).
            var names = new string?[n];
            foreach (var scope in scopes) Paint(scope, names, n);

            var i = 0;
            while (i < n)
            {
                var name = names[i];
                var j = i;
                while (j < n && names[j] == name) j++;
                Emit(parsedSourceCode.Substring(i, j - i), MapColor(name));
                i = j;
            }
        }

        private static void Paint(Scope scope, string?[] names, int n)
        {
            var end = Math.Min(scope.Index + scope.Length, n);
            for (var i = Math.Max(0, scope.Index); i < end; i++) names[i] = scope.Name;
            foreach (var child in scope.Children) Paint(child, names, n);
        }

        private void Emit(string text, HlColor color)
        {
            var parts = text.Replace("\r", "").Split('\n');
            for (var k = 0; k < parts.Length; k++)
            {
                if (k > 0)
                {
                    _lines.Add(_current);
                    _current = [];
                }
                if (parts[k].Length > 0) _current.Add(new HlSpan(parts[k], color));
            }
        }

        private static HlColor MapColor(string? scopeName) => scopeName switch
        {
            null => HlColor.Default,
            ScopeName.Keyword => HlColor.Keyword,
            ScopeName.PreprocessorKeyword => HlColor.Preprocessor,
            ScopeName.String or ScopeName.StringCSharpVerbatim or ScopeName.JsonString
                or ScopeName.XmlAttributeValue or ScopeName.HtmlAttributeValue => HlColor.String,
            ScopeName.Comment or ScopeName.XmlComment or ScopeName.XmlDocComment
                or ScopeName.HtmlComment => HlColor.Comment,
            ScopeName.ClassName or ScopeName.Type or ScopeName.XmlDocTag
                or ScopeName.HtmlElementName or ScopeName.XmlName or ScopeName.JsonKey => HlColor.Type,
            ScopeName.JsonNumber => HlColor.Number,
            _ => HlColor.Default,
        };
    }
}
