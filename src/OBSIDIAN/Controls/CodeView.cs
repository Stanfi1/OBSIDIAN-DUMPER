using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Obsidian.Services;

namespace Obsidian.Controls;

public enum CodeLanguage
{
    CSharp,
    Il,
    Plain,
}

/// <summary>Token classes produced by the highlighter. The theme layer maps each
/// one to a palette token (CodeText, CodeKeyword, CodeType, ...).</summary>
internal enum CodeTokenKind
{
    Plain,
    Comment,
    String,
    Keyword,
    Type,
    Number,
    Label,
    Directive,
}

/// <summary>
/// Read-only code surface with line numbers, horizontal scrolling and lightweight
/// syntax colouring.
///
/// Layout: a line-number gutter (its own <see cref="RichTextBox"/>, never focused or
/// hit-tested) next to the code surface, both built from the same FlowDocument
/// metrics so one rendered line of code always aligns with one gutter number. The
/// gutter follows the code scroll offset.
///
/// Colouring is a single regex pass per line rather than a full parser: it is fast
/// enough for multi-hundred-KB sources and has no third-party dependency. Rendering
/// is capped (characters, lines and runs) because a WPF FlowDocument is not
/// virtualised and an unbounded document would freeze the UI thread.
/// </summary>
public sealed class CodeView : Grid
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(CodeView), new PropertyMetadata(string.Empty, OnCodeChanged));

    public static readonly DependencyProperty CodeKindProperty = DependencyProperty.Register(
        nameof(CodeKind), typeof(CodeLanguage), typeof(CodeView), new PropertyMetadata(CodeLanguage.CSharp, OnCodeChanged));

    /// <summary>Case-insensitive text to highlight inside the rendered code. Empty disables highlighting.</summary>
    public static readonly DependencyProperty SearchTermProperty = DependencyProperty.Register(
        nameof(SearchTerm), typeof(string), typeof(CodeView), new PropertyMetadata(string.Empty, OnSearchTermChanged));

    private const int MaxRenderedCharacters = 250_000;
    private const int MaxRenderedLines = 6_000;
    private const int MaxTokenizedLineLength = 8_000;
    private const int MaxRuns = 50_000;
    private const double LineSpacingFactor = 1.55;
    private const double CodeFontSize = 12.4;

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Cascadia Code, Consolas, Courier New");

    private readonly RichTextBox _editor;
    private readonly RichTextBox _gutter;
    private readonly double _lineHeight = CodeFontSize * LineSpacingFactor;
    private string _renderedText = string.Empty;
    private CodeLanguage _renderedKind = CodeLanguage.CSharp;
    private string _highlightTerm = string.Empty;
    private bool _syncingScroll;

    public CodeView()
    {
        ClipToBounds = true;

        _gutter = new RichTextBox
        {
            IsReadOnly = true,
            Focusable = false,
            IsTabStop = false,
            IsHitTestVisible = false,
            IsDocumentEnabled = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            FontFamily = CodeFont,
            FontSize = CodeFontSize,
            Padding = new Thickness(6, 14, 10, 18),
        };

        _editor = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = CodeFont,
            FontSize = CodeFontSize,
            Padding = new Thickness(18, 14, 18, 18),
            IsReadOnlyCaretVisible = false,
            CaretBrush = Brushes.Transparent,
            SelectionBrush = FallbackSelection,
        };

        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetColumn(_gutter, 0);
        SetColumn(_editor, 1);
        Children.Add(_gutter);
        Children.Add(_editor);

        // ScrollChanged is a bubbling routed event: the editor's internal
        // ScrollViewer raises it and it reaches the RichTextBox handler list.
        _editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnEditorScrollChanged));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        ApplyTheme();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => ThemeService.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // ThemeChanged is raised on the UI thread by the service; marshal anyway so a
        // stray background raise cannot touch the FlowDocument off-thread.
        Dispatcher.BeginInvoke(ApplyTheme);
    }

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public CodeLanguage CodeKind
    {
        get => (CodeLanguage)GetValue(CodeKindProperty);
        set => SetValue(CodeKindProperty, value);
    }

    public string SearchTerm
    {
        get => (string)GetValue(SearchTermProperty);
        set => SetValue(SearchTermProperty, value);
    }

    /// <summary>Number of rendered logical lines (including the truncation notice).</summary>
    public int LineCount { get; private set; }

    /// <summary>True when the document was capped for performance and the tail was not rendered.</summary>
    public bool IsTruncated { get; private set; }

    private static void OnCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CodeView)d).Render();

    private static void OnSearchTermChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CodeView)d;
        if (!string.Equals(view._highlightTerm, view.SearchTerm, StringComparison.Ordinal))
            view.Render();
    }

    // ------------------------------------------------------------------ rendering

    private void Render()
    {
        string text = Code ?? string.Empty;
        CodeLanguage kind = CodeKind;
        string searchTerm = SearchTerm ?? string.Empty;

        bool truncatedCharacters = text.Length > MaxRenderedCharacters;
        if (truncatedCharacters)
        {
            text = text[..MaxRenderedCharacters];
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");

        string[] lines = text.Split('\n');
        bool truncatedLines = lines.Length > MaxRenderedLines;
        if (truncatedLines)
            Array.Resize(ref lines, MaxRenderedLines);

        IsTruncated = truncatedCharacters || truncatedLines;

        // Re-rendering an unchanged document would throw away caret/selection and
        // scroll position for no reason (e.g. a re-raised binding).
        if (!IsTruncated && text == _renderedText && kind == _renderedKind
            && string.Equals(searchTerm, _highlightTerm, StringComparison.Ordinal))
            return;

        _renderedText = text;
        _renderedKind = kind;
        LineCount = lines.Length;

        double charWidth = MeasureCharWidth();
        int maxLineLength = 0;
        foreach (string line in lines)
        {
            if (line.Length > maxLineLength)
                maxLineLength = line.Length;
        }

        FlowDocument code = CreateDocument();
        double pageWidth = Math.Max(360, maxLineLength * charWidth + 56);
        code.PageWidth = pageWidth;

        int runBudget = MaxRuns;
        foreach (string line in lines)
        {
            var paragraph = NewParagraph();
            AppendTokenized(paragraph, line, kind, ref runBudget);
            code.Blocks.Add(paragraph);
        }

        if (IsTruncated)
        {
            var notice = NewParagraph();
            string message = truncatedCharacters
                ? $"// … display truncated at {MaxRenderedCharacters:N0} characters for performance"
                : $"// … display truncated at {MaxRenderedLines:N0} lines for performance";
            notice.Inlines.Add(new Run(message) { Foreground = BrushFor(CodeTokenKind.Comment), Tag = CodeTokenKind.Comment });
            code.Blocks.Add(notice);
            LineCount++;
        }

        _editor.Document = code;

        RenderGutter(LineCount);

        // A brand-new document starts at the top; that is the correct behaviour for
        // newly loaded code, and the gutter has to follow it.
        _editor.ScrollToHome();
        SyncGutterScroll();
        HighlightInPlace(searchTerm);
        _highlightTerm = searchTerm;
    }

    /// <summary>
    /// Splits the rendered runs at case-insensitive matches of <paramref name="term"/>
    /// and paints those runs with the selection token. Called after every render and
    /// after a live theme change; splitting is idempotent because the runs are first
    /// stripped of their previous highlight.
    /// </summary>
    private void HighlightInPlace(string? term)
    {
        FlowDocument? document = _editor.Document;
        if (document == null)
            return;

        string needle = term ?? string.Empty;
        Brush highlight = ResolveBrush("CodeSelection", FallbackSelection);
        Brush highlightedText = BrushFor(CodeTokenKind.Plain);

        foreach (Block block in document.Blocks)
        {
            if (block is not Paragraph paragraph)
                continue;

            foreach (Inline inline in paragraph.Inlines)
            {
                if (inline is Run existing)
                    existing.Background = null;
            }

            if (needle.Length == 0)
                continue;

            List<Run> runs = paragraph.Inlines.OfType<Run>().ToList();
            foreach (Run run in runs)
            {
                string text = run.Text ?? string.Empty;
                int first = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (first < 0)
                    continue;

                var pieces = new List<(string Text, bool Match)>();
                int position = 0;
                while (first >= 0)
                {
                    if (first > position)
                        pieces.Add((text[position..first], false));
                    pieces.Add((text.Substring(first, needle.Length), true));
                    position = first + needle.Length;
                    first = position <= text.Length
                        ? text.IndexOf(needle, position, StringComparison.OrdinalIgnoreCase)
                        : -1;
                }

                if (position < text.Length)
                    pieces.Add((text[position..], false));
                if (pieces.Count == 0)
                    continue;

                Brush foreground = run.Foreground;
                object? tag = run.Tag;
                run.Text = pieces[0].Text;
                if (pieces[0].Match)
                {
                    run.Background = highlight;
                    run.Foreground = highlightedText;
                }

                Inline previous = run;
                for (int i = 1; i < pieces.Count; i++)
                {
                    var piece = new Run(pieces[i].Text)
                    {
                        Foreground = pieces[i].Match ? highlightedText : foreground,
                        Tag = tag,
                    };
                    if (pieces[i].Match)
                        piece.Background = highlight;
                    paragraph.Inlines.InsertAfter(previous, piece);
                    previous = piece;
                }
            }
        }
    }

    private FlowDocument CreateDocument()
    {
        return new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = CodeFont,
            FontSize = CodeFontSize,
            Foreground = BrushFor(CodeTokenKind.Plain),
            LineHeight = _lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Left,
            PageWidth = 1000,
        };
    }

    private Paragraph NewParagraph() => new()
    {
        Margin = new Thickness(0),
        TextIndent = 0,
        LineHeight = _lineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    private void RenderGutter(int lineCount)
    {
        var gutter = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = CodeFont,
            FontSize = CodeFontSize,
            Foreground = _gutterBrush,
            LineHeight = _lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Right,
            // PageWidth must stay automatic: a fixed page width would place the
            // right-aligned numbers outside the narrow gutter viewport.
            PageWidth = double.NaN,
        };

        for (int i = 1; i <= lineCount; i++)
        {
            var paragraph = new Paragraph(new Run(i.ToString(CultureInfo.InvariantCulture)))
            {
                Margin = new Thickness(0),
                TextIndent = 0,
                TextAlignment = TextAlignment.Right,
                LineHeight = _lineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = _gutterBrush,
            };
            gutter.Blocks.Add(paragraph);
        }

        _gutter.Document = gutter;

        int digits = Math.Max(2, lineCount.ToString(CultureInfo.InvariantCulture).Length);
        double charWidth = MeasureCharWidth();
        _gutter.Width = Math.Ceiling(charWidth * digits) + 16;
    }

    private void AppendTokenized(Paragraph paragraph, string line, CodeLanguage language, ref int runBudget)
    {
        if (line.Length == 0)
        {
            paragraph.Inlines.Add(new Run(" ") { Foreground = BrushFor(CodeTokenKind.Plain) });
            return;
        }

        if (line.Length > MaxTokenizedLineLength || runBudget <= 0)
        {
            // Very long (minified/obfuscated) lines would otherwise produce an
            // enormous number of Runs. One plain run keeps layout responsive.
            paragraph.Inlines.Add(new Run(line) { Foreground = BrushFor(CodeTokenKind.Plain) });
            runBudget -= 1;
            return;
        }

        int position = 0;
        foreach ((string token, CodeTokenKind tokenKind) in Tokenize(line, language))
        {
            if (runBudget-- <= 0)
                break;

            if (position + token.Length < line.Length && token.Length == 0)
                continue;

            paragraph.Inlines.Add(new Run(token) { Foreground = BrushFor(tokenKind), Tag = tokenKind });
            position += token.Length;
        }

        if (position < line.Length)
            paragraph.Inlines.Add(new Run(line[position..]) { Foreground = BrushFor(CodeTokenKind.Plain) });
    }

    // ------------------------------------------------------------------ scrolling

    private void OnEditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll)
            return;
        if (Math.Abs(e.VerticalChange) < 0.01 && Math.Abs(e.ExtentHeightChange) < 0.01)
            return;

        SyncGutterScroll();
    }

    private void SyncGutterScroll()
    {
        if (_syncingScroll)
            return;

        _syncingScroll = true;
        try
        {
            double offset = _editor.VerticalOffset;
            if (Math.Abs(_gutter.VerticalOffset - offset) > 0.01)
                _gutter.ScrollToVerticalOffset(offset);
        }
        catch (InvalidOperationException)
        {
            // The document can be replaced between a scroll notification and this
            // call; the next notification re-syncs.
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    // ------------------------------------------------------------------ theming

    /// <summary>
    /// Re-applies palette colours without rebuilding the document, so caret,
    /// selection and scroll position survive a live theme switch. Called by the
    /// theme service hook (see task-9) and safe to call before any document exists.
    /// </summary>
    public void ApplyTheme()
    {
        try
        {
            Brush plain = ResolveBrush("CodeText", FallbackPlain);
            Brush comment = ResolveBrush("CodeComment", FallbackComment);
            Brush str = ResolveBrush("CodeString", FallbackString);
            Brush keyword = ResolveBrush("CodeKeyword", FallbackKeyword);
            Brush type = ResolveBrush("CodeType", FallbackType);
            Brush number = ResolveBrush("CodeNumber", FallbackNumber);
            Brush directive = ResolveBrush("CodeDirective", FallbackDirective);
            _brushes = new Dictionary<CodeTokenKind, Brush>
            {
                [CodeTokenKind.Plain] = plain,
                [CodeTokenKind.Comment] = comment,
                [CodeTokenKind.String] = str,
                [CodeTokenKind.Keyword] = keyword,
                [CodeTokenKind.Type] = type,
                [CodeTokenKind.Number] = number,
                // The palette contract has no CodeLabel token; IL labels are
                // type-like references, so they share CodeType.
                [CodeTokenKind.Label] = type,
                [CodeTokenKind.Directive] = directive,
            };

            _gutterBrush = ResolveBrush("CodeLineNumber", FallbackLineNumber);
            Background = ResolveBrush("CodeBackground", FallbackBackground);
            _editor.SelectionBrush = ResolveBrush("CodeSelection", FallbackSelection);

            FlowDocument? codeDocument = _editor.Document;
            FlowDocument? gutterDocument = _gutter.Document;
            if (codeDocument != null)
                codeDocument.Foreground = plain;
            if (gutterDocument != null)
                gutterDocument.Foreground = _gutterBrush;

            if (codeDocument != null)
            {
                foreach (Block block in codeDocument.Blocks)
                {
                    if (block is not Paragraph paragraph)
                        continue;
                    foreach (Inline inline in paragraph.Inlines)
                    {
                        if (inline is Run run && run.Tag is CodeTokenKind tokenKind)
                            run.Foreground = BrushFor(tokenKind);
                    }
                }
            }

            if (gutterDocument != null)
            {
                foreach (Block block in gutterDocument.Blocks)
                {
                    if (block is Paragraph paragraph)
                        paragraph.Foreground = _gutterBrush;
                }
            }

            HighlightInPlace(_highlightTerm);
        }
        catch
        {
            // A theme refresh must never take the page down; keep the last colours.
        }
    }

    private static Brush ResolveBrush(string token, Brush fallback)
    {
        try
        {
            return Application.Current?.TryFindResource(token) as Brush ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    // Fallback palette: used only until the runtime theme system supplies the
    // Code* tokens (e.g. design-time or if the dictionaries are not loaded yet).
    private static readonly Brush FallbackPlain = FromRgb(0xCB, 0xD5, 0xE5);
    private static readonly Brush FallbackComment = FromRgb(0x5F, 0x6B, 0x80);
    private static readonly Brush FallbackString = FromRgb(0x7E, 0xE7, 0x87);
    private static readonly Brush FallbackKeyword = FromRgb(0xC4, 0xA7, 0xFF);
    private static readonly Brush FallbackType = FromRgb(0x7D, 0xD3, 0xFC);
    private static readonly Brush FallbackNumber = FromRgb(0xF5, 0xB8, 0x4B);
    private static readonly Brush FallbackDirective = FromRgb(0xF5, 0xB8, 0x4B);
    private static readonly Brush FallbackLineNumber = FromRgb(0x3A, 0x47, 0x61);
    private static readonly Brush FallbackSelection = FromRgb(0x3B, 0x2C, 0x6B);
    private static readonly Brush FallbackBackground = FromRgb(0x08, 0x0C, 0x12);

    private static readonly Dictionary<CodeTokenKind, Brush> DefaultBrushes = new()
    {
        [CodeTokenKind.Plain] = FallbackPlain,
        [CodeTokenKind.Comment] = FallbackComment,
        [CodeTokenKind.String] = FallbackString,
        [CodeTokenKind.Keyword] = FallbackKeyword,
        [CodeTokenKind.Type] = FallbackType,
        [CodeTokenKind.Number] = FallbackNumber,
        [CodeTokenKind.Label] = FallbackType,
        [CodeTokenKind.Directive] = FallbackDirective,
    };

    private Dictionary<CodeTokenKind, Brush> _brushes = DefaultBrushes;
    private Brush _gutterBrush = FallbackLineNumber;

    private Brush BrushFor(CodeTokenKind kind)
        => _brushes.TryGetValue(kind, out Brush? brush) ? brush : FallbackPlain;

    private static Brush FromRgb(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ tokenizer

    private static IEnumerable<(string Token, CodeTokenKind Kind)> Tokenize(string line, CodeLanguage language)
    {
        if (line.Length == 0)
            yield break;

        Regex pattern = language switch
        {
            CodeLanguage.CSharp => CSharpPattern,
            CodeLanguage.Il => IlPattern,
            _ => PlainPattern,
        };

        int position = 0;
        foreach (Match match in pattern.Matches(line))
        {
            if (match.Index > position)
                yield return (line[position..match.Index], CodeTokenKind.Plain);

            yield return (match.Value, KindFor(match));
            position = match.Index + match.Length;
        }

        if (position < line.Length)
            yield return (line[position..], CodeTokenKind.Plain);
    }

    private static CodeTokenKind KindFor(Match match)
    {
        if (match.Groups["comment"].Success)
            return CodeTokenKind.Comment;
        if (match.Groups["string"].Success)
            return CodeTokenKind.String;
        if (match.Groups["directive"].Success)
            return CodeTokenKind.Directive;
        if (match.Groups["keyword"].Success)
            return CodeTokenKind.Keyword;
        if (match.Groups["type"].Success)
            return CodeTokenKind.Type;
        if (match.Groups["number"].Success)
            return CodeTokenKind.Number;
        if (match.Groups["label"].Success)
            return CodeTokenKind.Label;
        return CodeTokenKind.Plain;
    }

    private static double MeasureCharWidth()
    {
        try
        {
            var typeface = new Typeface(CodeFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var text = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, CodeFontSize, Brushes.Black, 1.0);
            return text.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            return CodeFontSize * 0.6;
        }
    }

    // Each pattern is anchored to a single line and alternates named groups. All
    // quantifiers are deterministic (no nested overlapping repetition), so there is
    // no catastrophic-backtracking path; a multi-line comment is deliberately not
    // matched across lines because lines are tokenised independently.
    private static readonly Regex CSharpPattern = new(
        """
        (?<comment>//[^\n]*|/\*.*?\*/)
        |(?<directive>^[ \t]*\#[^\n]*)
        |(?<string>@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')
        |(?<keyword>\b(?:abstract|add|and|as|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|dynamic|else|enum|event|explicit|extern|false|file|finally|fixed|float|for|foreach|get|global|goto|if|implicit|in|init|int|interface|internal|is|lock|long|namespace|new|nint|not|notnull|nuint|null|object|operator|or|out|override|params|partial|private|protected|public|readonly|record|ref|remove|required|return|sbyte|scoped|sealed|set|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unmanaged|unsafe|ushort|using|value|var|virtual|void|volatile|when|where|while|with|yield|nameof)\b)
        |(?<type>\b[A-Z][A-Za-z0-9_]*\b(?![ \t]*\())
        |(?<number>\b(?:0[xX][0-9A-Fa-f_]+|\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?[fFdDmMlLuU]?)\b)
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex IlPattern = new(
        """
        (?<comment>//[^\n]*)
        |(?<directive>^[ \t]*\.[A-Za-z][A-Za-z0-9_]*)
        |(?<string>"(?:\\.|[^"\\])*")
        |(?<label>\bIL_[0-9A-Fa-f]{4}\b)
        |(?<type>\[[A-Za-z0-9_.]+\][A-Za-z0-9_.]+|\b(?:System|Microsoft|mscorlib)\.[A-Za-z0-9_.]+)
        |(?<keyword>\b(?:ldarg|ldarga|ldc|ldloc|loca|ldloca|ldsfld|ldfld|ldstr|ldtoken|ldnull|ldftn|ldvirtftn|starg|stloc|stsfld|stfld|ldobj|stobj|ldind|stind|conv|add|sub|mul|div|rem|neg|not|and|or|xor|shl|shr|dup|pop|call|callvirt|calli|ret|br|brtrue|brfalse|beq|bne|bge|bgt|ble|blt|bge\.un|bgt\.un|ble\.un|blt\.un|bne\.un|switch|throw|rethrow|leave|endfinally|endfilter|box|unbox|castclass|isinst|newarr|newobj|initobj|sizeof|mkrefany|refanyval|refanytype|constrained|volatile|readonly|tail|nop|break|arglist|localloc|cpobj|cpblk|initblk|jmp|instance|void|class|valuetype|native|int8|int16|int32|int64|uint8|uint16|uint32|uint64|float32|float64|string|object|bool|char|unsigned|signed|not|unmanaged|managed)\b)
        |(?<number>\b(?:0[xX][0-9A-Fa-f]+|-?\d+(?:\.\d+)?)\b)
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex PlainPattern = new(
        "(?<comment>//[^\\n]*)|(?<string>\"(?:\\\\.|[^\"\\\\])*\")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
