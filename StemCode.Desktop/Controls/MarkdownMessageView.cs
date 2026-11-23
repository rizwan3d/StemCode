using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace StemCode.Desktop.Controls;

public sealed class MarkdownMessageView : UserControl
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<MarkdownMessageView, string?>(nameof(SourceText));

    public static readonly StyledProperty<string?> WorkspacePathProperty =
        AvaloniaProperty.Register<MarkdownMessageView, string?>(nameof(WorkspacePath));

    public static readonly StyledProperty<IBrush> TextBrushProperty =
        AvaloniaProperty.Register<MarkdownMessageView, IBrush>(
            nameof(TextBrush),
            Brush.Parse("#E5E7EB"));

    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<MarkdownMessageView, bool>(nameof(Compact));

    private static readonly FontFamily CodeFont = new("Consolas, Cascadia Mono, JetBrains Mono");
    private static readonly IBrush MutedBrush = Brush.Parse("#8B949E");
    private static readonly IBrush LinkBrush = Brush.Parse("#7DD3FC");
    private static readonly IBrush CodeDefaultBrush = Brush.Parse("#D1D5DB");
    private static readonly IBrush CodeKeywordBrush = Brush.Parse("#C084FC");
    private static readonly IBrush CodeStringBrush = Brush.Parse("#A7F3D0");
    private static readonly IBrush CodeCommentBrush = Brush.Parse("#6B7280");
    private static readonly IBrush CodeNumberBrush = Brush.Parse("#FBBF24");
    private static readonly IBrush CodeTypeBrush = Brush.Parse("#93C5FD");
    private static readonly IBrush CodeBlockBackgroundBrush = Brush.Parse("#090D13");
    private static readonly IBrush CodeBlockBorderBrush = Brush.Parse("#263040");
    private static readonly IBrush DiffAddedBackgroundBrush = Brush.Parse("#0C2118");
    private static readonly IBrush DiffAddedTextBrush = Brush.Parse("#B7F7CF");
    private static readonly IBrush DiffRemovedBackgroundBrush = Brush.Parse("#2A1013");
    private static readonly IBrush DiffRemovedTextBrush = Brush.Parse("#FECACA");
    private static readonly IBrush DiffContextBackgroundBrush = Brush.Parse("#0D121A");
    private static readonly IBrush DiffHunkBackgroundBrush = Brush.Parse("#121A2A");
    private static readonly IBrush DiffHunkTextBrush = Brush.Parse("#93C5FD");
    private static readonly IBrush LineNumberBrush = Brush.Parse("#6B7280");
    private const string ToolBullet = "\u2022";
    private static readonly Regex FileReferenceRegex = new(
        @"(?ix)
        (?<![\w:/\\.-])
        (?:
            [A-Z]:[\\/][^\s`""'<>|]+
            |
            (?:\.{1,2}[\\/]|[A-Za-z0-9_.-]+[\\/])[^\s`""'<>|]+
            |
            [A-Za-z0-9_.-]+\.(?:cs|csproj|slnx|sln|axaml|xaml|json|md|yml|yaml|xml|props|targets|ps1|sh|txt|config|cshtml|razor|css|scss|js|ts|tsx|jsx|html|py|rs|go|java|kt|swift|c|cpp|h|hpp|sql|log)
        )
        (?::\d+)?",
        RegexOptions.Compiled);

    private static readonly Regex InlineMarkdownRegex = new(
        @"(`[^`\r\n]+`)|(\*\*[^*\r\n]+\*\*)|(__[^_\r\n]+__)",
        RegexOptions.Compiled);

    private static readonly Regex ListItemRegex = new(
        @"^\s*(?:[-*+]|\d+\.)\s+(?<text>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex ToolPreviewLineRegex = new(
        @"^\s*(?<line>\d+)\s+(?<kind>[+\- ])(?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ToolFileEditHeaderRegex = new(
        @"^\s*-\s+(?<path>.+?)\s+\(\+(?<added>\d+)\s+-(?<removed>\d+)\)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex CodeTokenRegex = new(
        @"(//.*$|#.*$|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|\b(?:abstract|async|await|bool|break|case|catch|class|const|continue|default|else|enum|false|finally|for|foreach|if|internal|namespace|new|null|private|protected|public|readonly|record|return|sealed|static|string|switch|this|throw|true|try|using|var|void|while|Task|IReadOnlyList|IEnumerable|CancellationToken)\b|\b\d+(?:\.\d+)?\b)",
        RegexOptions.Compiled);

    static MarkdownMessageView()
    {
        SourceTextProperty.Changed.AddClassHandler<MarkdownMessageView>((view, _) => view.Rebuild());
        WorkspacePathProperty.Changed.AddClassHandler<MarkdownMessageView>((view, _) => view.Rebuild());
        TextBrushProperty.Changed.AddClassHandler<MarkdownMessageView>((view, _) => view.Rebuild());
        CompactProperty.Changed.AddClassHandler<MarkdownMessageView>((view, _) => view.Rebuild());
    }

    public MarkdownMessageView()
    {
        Rebuild();
    }

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string? WorkspacePath
    {
        get => GetValue(WorkspacePathProperty);
        set => SetValue(WorkspacePathProperty, value);
    }

    public IBrush TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    private void Rebuild()
    {
        StackPanel blocks = new()
        {
            Spacing = Compact ? 4 : 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        string text = SourceText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            blocks.Children.Add(CreateTextBlock(string.Empty, TextBrush));
            Content = blocks;
            return;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (TryCreateToolOutputView(lines, out Control? toolOutputView) && toolOutputView is not null)
        {
            blocks.Children.Add(toolOutputView);
            Content = blocks;
            return;
        }

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();

            if (IsCodeFence(trimmed, out string? language))
            {
                List<string> codeLines = [];
                index++;
                while (index < lines.Length && !IsCodeFence(lines[index].Trim(), out _))
                {
                    codeLines.Add(lines[index]);
                    index++;
                }

                blocks.Children.Add(CreateCodeBlock(codeLines, language));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (!Compact && blocks.Children.Count > 0)
                {
                    blocks.Children.Add(new Border { Height = 2 });
                }

                continue;
            }

            if (TryCreateHeading(trimmed, out Control? heading) && heading is not null)
            {
                blocks.Children.Add(heading);
                continue;
            }

            if (trimmed.StartsWith('|') &&
                index + 1 < lines.Length &&
                IsTableSeparator(lines[index + 1].Trim()))
            {
                List<string> tableLines = [line, lines[index + 1]];
                index += 2;
                while (index < lines.Length && lines[index].Trim().StartsWith('|'))
                {
                    tableLines.Add(lines[index]);
                    index++;
                }

                index--;
                blocks.Children.Add(CreateTable(tableLines));
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                blocks.Children.Add(new Border
                {
                    Height = 1,
                    Background = CodeBlockBorderBrush,
                    Margin = new Thickness(0, 6)
                });
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                List<string> quoteLines = [];
                while (index < lines.Length &&
                       (lines[index].Trim().StartsWith("> ", StringComparison.Ordinal) || lines[index].Trim() == ">"))
                {
                    quoteLines.Add(lines[index].Trim().TrimStart('>').TrimStart());
                    index++;
                }

                index--;
                StackPanel quote = new() { Spacing = 2 };
                foreach (string quoteLine in quoteLines)
                {
                    quote.Children.Add(CreateInlineLine(quoteLine, MutedBrush));
                }

                blocks.Children.Add(new Border
                {
                    BorderBrush = CodeBlockBorderBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 2),
                    Child = quote
                });
                continue;
            }

            Match listMatch = ListItemRegex.Match(line);
            if (listMatch.Success)
            {
                blocks.Children.Add(CreateListItem(listMatch.Groups["text"].Value));
                continue;
            }

            blocks.Children.Add(CreateInlineLine(line, TextBrush));
        }

        Content = blocks;
    }

    private static bool IsTableSeparator(string line)
    {
        if (!line.StartsWith('|') || !line.Contains('-'))
        {
            return false;
        }

        return line.All(static character => character is '|' or '-' or ':' or ' ' or '\t');
    }

    private static string[] SplitTableRow(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(static cell => cell.Trim()).ToArray();
    }

    private Control CreateTable(IReadOnlyList<string> tableLines)
    {
        string[] header = SplitTableRow(tableLines[0]);
        List<string[]> rows = tableLines.Skip(2).Select(SplitTableRow).ToList();
        int columnCount = Math.Max(header.Length, rows.Count == 0 ? 0 : rows.Max(static row => row.Length));

        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", columnCount))),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows.Count + 1)))
        };

        void AddCell(string text, int row, int column, bool isHeader)
        {
            WrapPanel content = CreateInlineLinePanel(text, TextBrush);
            if (isHeader)
            {
                foreach (Control child in content.Children)
                {
                    if (child is TextBlock block)
                    {
                        block.FontWeight = FontWeight.SemiBold;
                    }
                }
            }

            Border cell = new()
            {
                Child = content,
                Padding = new Thickness(10, 6),
                BorderBrush = CodeBlockBorderBrush,
                BorderThickness = new Thickness(0, 0, column < columnCount - 1 ? 1 : 0, row < rows.Count ? 1 : 0),
                Background = isHeader ? CodeBlockBackgroundBrush : Brushes.Transparent
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        for (int column = 0; column < columnCount; column++)
        {
            AddCell(column < header.Length ? header[column] : string.Empty, 0, column, isHeader: true);
        }

        for (int row = 0; row < rows.Count; row++)
        {
            for (int column = 0; column < columnCount; column++)
            {
                AddCell(column < rows[row].Length ? rows[row][column] : string.Empty, row + 1, column, isHeader: false);
            }
        }

        return new Border
        {
            BorderBrush = CodeBlockBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = grid
            }
        };
    }

    private static bool IsCodeFence(string line, out string? language)
    {
        language = null;
        if (!line.StartsWith("```", StringComparison.Ordinal) &&
            !line.StartsWith("~~~", StringComparison.Ordinal))
        {
            return false;
        }

        language = line.Length > 3 ? line[3..].Trim() : null;
        if (string.IsNullOrWhiteSpace(language))
        {
            language = null;
        }

        return true;
    }

    private bool TryCreateToolOutputView(
        IReadOnlyList<string> lines,
        out Control? view)
    {
        view = null;
        string firstLine = lines.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
        if (!firstLine.StartsWith(ToolBullet, StringComparison.Ordinal))
        {
            return false;
        }

        if (firstLine.StartsWith($"{ToolBullet} Edited ", StringComparison.Ordinal))
        {
            view = CreateFileEditToolView(lines);
            return true;
        }

        if (firstLine.StartsWith($"{ToolBullet} Ran ", StringComparison.Ordinal))
        {
            view = CreateShellToolView(lines);
            return true;
        }

        if (firstLine.StartsWith($"{ToolBullet} Read ", StringComparison.Ordinal))
        {
            view = CreatePreviewToolView(lines, "preview");
            return true;
        }

        if (firstLine.StartsWith($"{ToolBullet} Previewed ", StringComparison.Ordinal))
        {
            view = CreatePreviewToolView(lines, "preview");
            return true;
        }

        view = CreateGenericToolView(lines);
        return true;
    }

    private Control CreateFileEditToolView(IReadOnlyList<string> lines)
    {
        StackPanel content = new()
        {
            Spacing = 8
        };

        content.Children.Add(CreateToolHeader(lines[0], "file edit"));

        for (int index = 1; index < lines.Count; index++)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            Match fileHeaderMatch = ToolFileEditHeaderRegex.Match(line);
            if (!fileHeaderMatch.Success)
            {
                continue;
            }

            StackPanel filePanel = new()
            {
                Spacing = 6
            };

            string path = fileHeaderMatch.Groups["path"].Value;
            string added = fileHeaderMatch.Groups["added"].Value;
            string removed = fileHeaderMatch.Groups["removed"].Value;

            Grid fileHeader = new()
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 10
            };

            fileHeader.Children.Add(CreateInlineLine(path, TextBrush));
            TextBlock stats = CreateTextBlock(
                $"+{added} -{removed}",
                MutedBrush,
                fontSize: 12,
                fontFamily: CodeFont);
            Grid.SetColumn(stats, 1);
            fileHeader.Children.Add(stats);
            filePanel.Children.Add(fileHeader);

            StackPanel diffRows = new()
            {
                Spacing = 1
            };

            while (index + 1 < lines.Count)
            {
                string candidate = lines[index + 1];
                if (ToolFileEditHeaderRegex.IsMatch(candidate))
                {
                    break;
                }

                index++;
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                Match previewMatch = ToolPreviewLineRegex.Match(candidate);
                if (previewMatch.Success)
                {
                    diffRows.Children.Add(CreateToolPreviewDiffRow(previewMatch));
                    continue;
                }

                if (candidate.TrimStart().StartsWith("...", StringComparison.Ordinal))
                {
                    diffRows.Children.Add(CreateMutedCodeRow(candidate.Trim()));
                }
            }

            filePanel.Children.Add(new Border
            {
                Background = DiffContextBackgroundBrush,
                BorderBrush = CodeBlockBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = diffRows
                }
            });

            content.Children.Add(new Border
            {
                Background = Brush.Parse("#0B111A"),
                BorderBrush = Brush.Parse("#202A3A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = filePanel
            });
        }

        return CreateToolCard(content);
    }

    private Control CreateShellToolView(IReadOnlyList<string> lines)
    {
        StackPanel content = new()
        {
            Spacing = 8
        };

        content.Children.Add(CreateToolHeader(lines[0], "command"));

        string? streamLabel = null;
        List<string> outputLines = [];
        for (int index = 1; index < lines.Count; index++)
        {
            string trimmed = lines[index].Trim();
            if (trimmed.StartsWith("- stdout:", StringComparison.Ordinal) ||
                trimmed.StartsWith("- stderr:", StringComparison.Ordinal))
            {
                streamLabel = trimmed[2..^1];
                continue;
            }

            if (trimmed.StartsWith("- exit code:", StringComparison.Ordinal))
            {
                content.Children.Add(CreateStatusPill(trimmed[2..]));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(lines[index]))
            {
                outputLines.Add(lines[index].Trim());
            }
        }

        if (outputLines.Count > 0)
        {
            content.Children.Add(CreateCodeBlock(outputLines, streamLabel ?? "output"));
        }

        return CreateToolCard(content);
    }

    private Control CreatePreviewToolView(
        IReadOnlyList<string> lines,
        string label)
    {
        StackPanel content = new()
        {
            Spacing = 8
        };

        content.Children.Add(CreateToolHeader(lines[0], label));

        List<string> previewLines = [];
        for (int index = 1; index < lines.Count; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (trimmed.StartsWith("- preview:", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("- empty file", StringComparison.Ordinal))
            {
                content.Children.Add(CreateStatusPill("empty file"));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                previewLines.Add(line.TrimStart());
            }
        }

        if (previewLines.Count > 0)
        {
            content.Children.Add(CreateCodeBlock(previewLines, label));
        }

        return CreateToolCard(content);
    }

    private Control CreateGenericToolView(IReadOnlyList<string> lines)
    {
        StackPanel content = new()
        {
            Spacing = 6
        };

        content.Children.Add(CreateToolHeader(lines[0], "tool"));
        for (int index = 1; index < lines.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(lines[index]))
            {
                content.Children.Add(CreateInlineLine(lines[index].Trim(), TextBrush));
            }
        }

        return CreateToolCard(content);
    }

    private Control CreateToolCard(Control content)
    {
        return new Border
        {
            Background = Brush.Parse("#0A0F16"),
            BorderBrush = Brush.Parse("#263247"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = content
        };
    }

    private Control CreateToolHeader(
        string title,
        string label)
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10
        };

        string normalizedTitle = title.Trim();
        if (normalizedTitle.StartsWith(ToolBullet, StringComparison.Ordinal))
        {
            normalizedTitle = normalizedTitle[ToolBullet.Length..].Trim();
        }

        grid.Children.Add(CreateTextBlock(
            normalizedTitle,
            TextBrush,
            fontSize: 13,
            fontWeight: FontWeight.SemiBold));

        Control pill = CreateStatusPill(label);
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);
        return grid;
    }

    private static Control CreateStatusPill(string text)
    {
        return new Border
        {
            Background = Brush.Parse("#111827"),
            BorderBrush = Brush.Parse("#2A3344"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                Foreground = MutedBrush,
                FontSize = 11,
                FontFamily = CodeFont,
                LineHeight = 15
            }
        };
    }

    private bool TryCreateHeading(string line, out Control? heading)
    {
        heading = null;
        if (!line.StartsWith('#'))
        {
            return false;
        }

        int level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is < 1 or > 4 ||
            level >= line.Length ||
            !char.IsWhiteSpace(line[level]))
        {
            return false;
        }

        string text = line[level..].Trim();
        heading = CreateTextBlock(
            text,
            TextBrush,
            fontSize: Compact ? 12 : Math.Max(13, 18 - level),
            fontWeight: FontWeight.SemiBold);
        return true;
    }

    private Control CreateListItem(string text)
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8
        };

        grid.Children.Add(new TextBlock
        {
            Text = "\u2022",
            Foreground = MutedBrush,
            FontSize = Compact ? 12 : 13,
            Margin = new Thickness(0, 1, 0, 0)
        });

        WrapPanel content = CreateInlineLinePanel(text, TextBrush);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private Control CreateInlineLine(string line, IBrush brush)
    {
        return CreateInlineLinePanel(line, brush);
    }

    private WrapPanel CreateInlineLinePanel(string line, IBrush brush)
    {
        WrapPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        int index = 0;
        foreach (Match match in InlineMarkdownRegex.Matches(line).Cast<Match>())
        {
            if (match.Index > index)
            {
                AddFileAwareText(panel, line[index..match.Index], brush);
            }

            string value = match.Value;
            if (value.StartsWith('`') && value.EndsWith('`'))
            {
                AddFileAwareText(panel, value[1..^1], CodeStringBrush, codeStyle: true);
            }
            else
            {
                AddFileAwareText(panel, value[2..^2], brush, fontWeight: FontWeight.SemiBold);
            }

            index = match.Index + match.Length;
        }

        if (index < line.Length)
        {
            AddFileAwareText(panel, line[index..], brush);
        }

        return panel;
    }

    private Control CreateCodeBlock(IReadOnlyList<string> lines, string? language)
    {
        StackPanel codePanel = new()
        {
            Spacing = IsDiffBlock(lines, language) ? 1 : 2
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            codePanel.Children.Add(CreateCodeBlockHeader(language));
        }

        bool isDiffBlock = IsDiffBlock(lines, language);
        foreach (string line in lines)
        {
            if (isDiffBlock && TryCreateDiffCodeRow(line, out Control? diffRow) && diffRow is not null)
            {
                codePanel.Children.Add(diffRow);
                continue;
            }

            codePanel.Children.Add(CreateCodeLineRow(line.Length == 0 ? " " : line));
        }

        Border border = new()
        {
            Background = CodeBlockBackgroundBrush,
            BorderBrush = CodeBlockBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = codePanel
            }
        };

        return border;
    }

    private static Control CreateCodeBlockHeader(string language)
    {
        return new Border
        {
            Background = Brush.Parse("#0D141F"),
            BorderBrush = CodeBlockBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
            Child = new TextBlock
            {
                Text = language,
                Foreground = MutedBrush,
                FontSize = 11,
                FontFamily = CodeFont,
                LineHeight = 15
            }
        };
    }

    private Control CreateCodeLineRow(string line)
    {
        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 0)
        };
        AddHighlightedCode(row, line);
        return row;
    }

    private static bool IsDiffBlock(
        IReadOnlyList<string> lines,
        string? language)
    {
        if (string.Equals(language, "diff", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(language, "patch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool hasHunkOrFileHeader = lines.Any(static line =>
            line.StartsWith("@@", StringComparison.Ordinal) ||
            line.StartsWith("+++", StringComparison.Ordinal) ||
            line.StartsWith("---", StringComparison.Ordinal));
        if (hasHunkOrFileHeader)
        {
            return true;
        }

        int addedLineCount = lines.Count(static line => line.StartsWith("+", StringComparison.Ordinal));
        int removedLineCount = lines.Count(static line => line.StartsWith("-", StringComparison.Ordinal));
        return addedLineCount > 0 && removedLineCount > 0;
    }

    private static bool IsDiffLikeLine(string line)
    {
        return line.StartsWith("@@", StringComparison.Ordinal) ||
            line.StartsWith("+++", StringComparison.Ordinal) ||
            line.StartsWith("---", StringComparison.Ordinal) ||
            line.StartsWith("+", StringComparison.Ordinal) ||
            line.StartsWith("-", StringComparison.Ordinal);
    }

    private bool TryCreateDiffCodeRow(
        string line,
        out Control? row)
    {
        row = null;
        if (!IsDiffLikeLine(line))
        {
            return false;
        }

        char marker = line.Length == 0 ? ' ' : line[0];
        string text = marker is '+' or '-'
            ? line[1..]
            : line;

        IBrush background;
        IBrush markerBrush;
        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            background = DiffHunkBackgroundBrush;
            markerBrush = DiffHunkTextBrush;
            text = line;
            marker = ' ';
        }
        else if (line.StartsWith("+++", StringComparison.Ordinal) ||
                 line.StartsWith("---", StringComparison.Ordinal))
        {
            background = DiffContextBackgroundBrush;
            markerBrush = MutedBrush;
            text = line;
            marker = ' ';
        }
        else if (marker == '+')
        {
            background = DiffAddedBackgroundBrush;
            markerBrush = DiffAddedTextBrush;
        }
        else if (marker == '-')
        {
            background = DiffRemovedBackgroundBrush;
            markerBrush = DiffRemovedTextBrush;
        }
        else
        {
            background = DiffContextBackgroundBrush;
            markerBrush = MutedBrush;
        }

        row = CreateDiffRow(
            null,
            marker == ' ' ? string.Empty : marker.ToString(),
            text.Length == 0 ? " " : text,
            background,
            markerBrush);
        return true;
    }

    private Control CreateToolPreviewDiffRow(Match previewMatch)
    {
        string lineNumber = previewMatch.Groups["line"].Value;
        string kind = previewMatch.Groups["kind"].Value;
        string text = previewMatch.Groups["text"].Value;

        IBrush background = kind switch
        {
            "+" => DiffAddedBackgroundBrush,
            "-" => DiffRemovedBackgroundBrush,
            _ => DiffContextBackgroundBrush
        };

        IBrush markerBrush = kind switch
        {
            "+" => DiffAddedTextBrush,
            "-" => DiffRemovedTextBrush,
            _ => MutedBrush
        };

        return CreateDiffRow(
            lineNumber,
            kind.Trim(),
            text.Length == 0 ? " " : text,
            background,
            markerBrush);
    }

    private Control CreateMutedCodeRow(string text)
    {
        return CreateDiffRow(
            null,
            string.Empty,
            text,
            DiffContextBackgroundBrush,
            MutedBrush);
    }

    private Control CreateDiffRow(
        string? lineNumber,
        string marker,
        string text,
        IBrush background,
        IBrush markerBrush)
    {
       Grid grid = new()
       {
            ColumnDefinitions = new ColumnDefinitions("48,24,Auto"),
           ColumnSpacing = 0,
            Background = background,
            MinHeight = 22
        };

        grid.Children.Add(new TextBlock
        {
            Text = lineNumber ?? string.Empty,
            Foreground = LineNumberBrush,
            FontFamily = CodeFont,
            FontSize = 12,
            LineHeight = 18,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(8, 2, 8, 2)
        });

        TextBlock markerText = new()
        {
            Text = marker,
            Foreground = markerBrush,
            FontFamily = CodeFont,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            LineHeight = 18,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 2)
        };
        Grid.SetColumn(markerText, 1);
        grid.Children.Add(markerText);

        StackPanel code = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 10, 2)
        };
        AddHighlightedCode(code, text);
        Grid.SetColumn(code, 2);
        grid.Children.Add(code);
        return grid;
    }

    private void AddHighlightedCode(Panel panel, string text)
    {
        int index = 0;
        foreach (Match pathMatch in FileReferenceRegex.Matches(text).Cast<Match>())
        {
            if (pathMatch.Index > index)
            {
                AddHighlightedCodeText(panel, text[index..pathMatch.Index]);
            }

            string display = TrimTrailingPunctuation(pathMatch.Value, out string trailing);
            if (TryResolveFileReference(display, out string? fullPath, out int? lineNumber))
            {
                panel.Children.Add(CreateFileButton(display, fullPath, lineNumber));
            }
            else
            {
                AddHighlightedCodeText(panel, display);
            }

            if (trailing.Length > 0)
            {
                AddHighlightedCodeText(panel, trailing);
            }

            index = pathMatch.Index + pathMatch.Length;
        }

        if (index < text.Length)
        {
            AddHighlightedCodeText(panel, text[index..]);
        }
    }

    private void AddHighlightedCodeText(Panel panel, string text)
    {
        int index = 0;
        foreach (Match match in CodeTokenRegex.Matches(text).Cast<Match>())
        {
            if (match.Index > index)
            {
                panel.Children.Add(CreateCodeRun(text[index..match.Index], CodeDefaultBrush));
            }

            string value = match.Value;
            IBrush brush = GetCodeTokenBrush(value);
            panel.Children.Add(CreateCodeRun(value, brush, value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith('#')));
            index = match.Index + match.Length;
        }

        if (index < text.Length)
        {
            panel.Children.Add(CreateCodeRun(text[index..], CodeDefaultBrush));
        }
    }

    private static IBrush GetCodeTokenBrush(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith('#'))
        {
            return CodeCommentBrush;
        }

        if (value.StartsWith('"') || value.StartsWith('\''))
        {
            return CodeStringBrush;
        }

        if (char.IsDigit(value[0]))
        {
            return CodeNumberBrush;
        }

        return value is "Task" or "IReadOnlyList" or "IEnumerable" or "CancellationToken"
            ? CodeTypeBrush
            : CodeKeywordBrush;
    }

    private void AddFileAwareText(
        Panel panel,
        string text,
        IBrush brush,
        FontWeight? fontWeight = null,
        bool codeStyle = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int index = 0;
        foreach (Match match in FileReferenceRegex.Matches(text).Cast<Match>())
        {
            if (match.Index > index)
            {
                panel.Children.Add(CreateInlineText(
                    text[index..match.Index],
                    brush,
                    fontWeight,
                    codeStyle));
            }

            string display = TrimTrailingPunctuation(match.Value, out string trailing);
            if (TryResolveFileReference(display, out string? fullPath, out int? lineNumber))
            {
                panel.Children.Add(CreateFileButton(display, fullPath, lineNumber));
            }
            else
            {
                panel.Children.Add(CreateInlineText(display, brush, fontWeight, codeStyle));
            }

            if (trailing.Length > 0)
            {
                panel.Children.Add(CreateInlineText(trailing, brush, fontWeight, codeStyle));
            }

            index = match.Index + match.Length;
        }

        if (index < text.Length)
        {
            panel.Children.Add(CreateInlineText(text[index..], brush, fontWeight, codeStyle));
        }
    }

    private Control CreateInlineText(
        string text,
        IBrush brush,
        FontWeight? fontWeight,
        bool codeStyle)
    {
        TextBlock textBlock = CreateTextBlock(
            text,
            brush,
            fontSize: Compact ? 12 : 13,
            fontWeight: fontWeight ?? FontWeight.Normal,
            fontFamily: codeStyle ? CodeFont : null);

        if (!codeStyle)
        {
            return textBlock;
        }

        return new Border
        {
            Background = Brush.Parse("#18202A"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0),
            Margin = new Thickness(1, 0),
            Child = textBlock
        };
    }

    private static TextBlock CreateTextBlock(
        string text,
        IBrush brush,
        double fontSize = 13,
        FontWeight? fontWeight = null,
        FontFamily? fontFamily = null)
    {
        TextBlock textBlock = new()
        {
            Text = text,
            Foreground = brush,
            FontSize = fontSize,
            FontWeight = fontWeight ?? FontWeight.Normal,
            LineHeight = Math.Max(18, fontSize + 7),
            TextWrapping = TextWrapping.Wrap
        };

        if (fontFamily is not null)
        {
            textBlock.FontFamily = fontFamily;
        }

        return textBlock;
    }

    private static TextBlock CreateCodeRun(string text, IBrush brush, bool italic = false)
    {
        return new TextBlock
        {
            Text = text.Replace(" ", "\u00A0", StringComparison.Ordinal),
            Foreground = brush,
            FontSize = 12,
            FontFamily = CodeFont,
            FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
            LineHeight = 18
        };
    }

    private Button CreateFileButton(
        string displayText,
        string fullPath,
        int? lineNumber)
    {
        Button button = new()
        {
            Content = displayText,
            Background = Brush.Parse("#102033"),
            Foreground = LinkBrush,
            BorderBrush = Brush.Parse("#23527A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = Compact ? new Thickness(4, 0) : new Thickness(5, 1),
            Margin = new Thickness(1, 0),
            MinHeight = 0,
            FontSize = Compact ? 12 : 13,
            FontFamily = CodeFont,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };

        ToolTip.SetTip(button, CreateFileTooltip(fullPath, lineNumber));
        button.Click += (_, _) => OpenFile(fullPath, lineNumber);
        return button;
    }

    private bool TryResolveFileReference(string display, out string fullPath, out int? lineNumber)
    {
        lineNumber = null;
        string candidate = display.Trim('`', '"', '\'');
        if (string.IsNullOrWhiteSpace(candidate))
        {
            fullPath = string.Empty;
            return false;
        }

        int lineSeparatorIndex = candidate.LastIndexOf(':');
        if (lineSeparatorIndex > 1 &&
            lineSeparatorIndex < candidate.Length - 1 &&
            candidate[(lineSeparatorIndex + 1)..].All(char.IsDigit))
        {
            lineNumber = int.Parse(candidate[(lineSeparatorIndex + 1)..]);
            candidate = candidate[..lineSeparatorIndex];
        }

        string root = string.IsNullOrWhiteSpace(WorkspacePath)
            ? Environment.CurrentDirectory
            : WorkspacePath!;

        string exactPath = Path.IsPathRooted(candidate)
            ? candidate
            : Path.GetFullPath(Path.Combine(root, candidate.Replace('/', Path.DirectorySeparatorChar)));

        if (File.Exists(exactPath) || Directory.Exists(exactPath))
        {
            fullPath = exactPath;
            return true;
        }

        if (candidate.IndexOfAny(['/', '\\']) < 0 && Directory.Exists(root))
        {
            string? found = Directory
                .EnumerateFiles(root, candidate, SearchOption.AllDirectories)
                .Where(static path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (found is not null)
            {
                fullPath = found;
                return true;
            }
        }

        fullPath = string.Empty;
        return false;
    }

    private static string TrimTrailingPunctuation(string value, out string trailing)
    {
        int end = value.Length;
        while (end > 0 && IsTrailingPunctuation(value[end - 1]))
        {
            end--;
        }

        trailing = value[end..];
        return value[..end];
    }

    private static bool IsTrailingPunctuation(char value)
    {
        return value is '.' or ',' or ';' or ')' or ']' or '}' or '!' or '?';
    }

    private static string CreateFileTooltip(
        string fullPath,
        int? lineNumber)
    {
        if (lineNumber is not > 0)
        {
            return fullPath;
        }

        string target = $"{fullPath}:{lineNumber.Value}";
        string? preview = TryReadLinePreview(fullPath, lineNumber.Value);
        return string.IsNullOrWhiteSpace(preview)
            ? target
            : $"{target}{Environment.NewLine}{preview}";
    }

    private static string? TryReadLinePreview(
        string path,
        int lineNumber)
    {
        if (lineNumber <= 0 || !File.Exists(path))
        {
            return null;
        }

        try
        {
            string? line = File
                .ReadLines(path)
                .Skip(lineNumber - 1)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            string preview = line.Trim();
            return preview.Length <= 180
                ? preview
                : preview[..177] + "...";
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Opens a file in the configured editor (VS Code if present), else the OS default.</summary>
    public static void OpenPath(string path) => OpenFile(path, null);

    private static void OpenFile(
        string path,
        int? lineNumber)
    {
        try
        {
            if (lineNumber is > 0 &&
                File.Exists(path) &&
                TryOpenFileAtLine(path, lineNumber.Value))
            {
                return;
            }

            OpenWithShell(path);
        }
        catch
        {
        }
    }

    private static bool TryOpenFileAtLine(
        string path,
        int lineNumber)
    {
        foreach (EditorCommand command in CreateEditorCommands(path, lineNumber))
        {
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = command.FileName,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (string argument in command.Arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                Process.Start(startInfo);
                return true;
            }
            catch (Exception exception) when (
                exception is Win32Exception ||
                exception is FileNotFoundException ||
                exception is InvalidOperationException)
            {
            }
        }

        return false;
    }

    private static IEnumerable<EditorCommand> CreateEditorCommands(
        string path,
        int lineNumber)
    {
        foreach (string executable in GetEditorExecutables().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string editorName = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
            switch (editorName)
            {
                case "code":
                case "code-insiders":
                case "codium":
                case "vscodium":
                    yield return new EditorCommand(
                        executable,
                        ["--goto", $"{path}:{lineNumber}"]);
                    break;

                case "notepad++":
                    yield return new EditorCommand(
                        executable,
                        [$"-n{lineNumber}", path]);
                    break;

                case "subl":
                case "sublime_text":
                    yield return new EditorCommand(
                        executable,
                        [$"{path}:{lineNumber}"]);
                    break;

                case "rider":
                case "rider64":
                case "idea":
                case "idea64":
                    yield return new EditorCommand(
                        executable,
                        ["--line", lineNumber.ToString(), path]);
                    break;
            }
        }
    }

    private static IEnumerable<string> GetEditorExecutables()
    {
        foreach (string variableName in new[] { "STEMCODE_EDITOR", "VISUAL", "EDITOR" })
        {
            string? executable = TryGetConfiguredEditorExecutable(
                Environment.GetEnvironmentVariable(variableName));
            if (!string.IsNullOrWhiteSpace(executable))
            {
                yield return executable;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            yield return "code.cmd";
            yield return "code-insiders.cmd";
            yield return "codium.cmd";
            yield return "notepad++.exe";
            yield return "rider64.exe";
            yield return "rider.exe";
            yield break;
        }

        yield return "code";
        yield return "code-insiders";
        yield return "codium";
        yield return "subl";
        yield return "sublime_text";
        yield return "rider";
        yield return "rider64";
    }

    private static string? TryGetConfiguredEditorExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith('"'))
        {
            int quoteIndex = trimmed.IndexOf('"', 1);
            return quoteIndex > 1
                ? trimmed[1..quoteIndex]
                : null;
        }

        int separatorIndex = trimmed.IndexOfAny([' ', '\t']);
        return separatorIndex < 0
            ? trimmed
            : trimmed[..separatorIndex];
    }

    private static void OpenWithShell(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private sealed record EditorCommand(
        string FileName,
        IReadOnlyList<string> Arguments);
}
