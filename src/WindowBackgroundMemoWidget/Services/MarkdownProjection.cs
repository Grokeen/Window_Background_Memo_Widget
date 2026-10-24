using System;
using System.Collections.Generic;
using System.Linq;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: MarkdownDocumentState; desc: 실제 파일 저장/충돌 상태. 화면 표시 상태와 독립이다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 세션 API.</summary>
public enum MarkdownDocumentState { Clean, Modified, Saving, Conflict, Missing, Unavailable, Error }

/// <summary>name: MarkdownTaskItem; desc: 특정 문서 revision의 정확한 marker와 본문 범위.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 CHECK-01.</summary>
public sealed record MarkdownTaskItem(string NoteId, long Revision, int MarkerStart, int MarkerOffset,
    int ContentStart, int ContentLength, int LineIndex, bool IsChecked, string Text)
{ public string Id => NoteId + ":" + Revision + ":" + MarkerStart; }

/// <summary>name: MarkdownVisualBlock; desc: 원문 위치를 잃지 않는 시각 편집 projection. Kind는 Paragraph/Heading/Task/List/Code/Unsupported.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 EDIT-01 계약.</summary>
public sealed record MarkdownVisualBlock(string Kind, int SourceStart, int SourceLength, int ContentStart,
    int ContentLength, string Text, int Level, MarkdownTaskItem? Task = null)
{
    public IReadOnlyList<MarkdownDisplayRun> Runs { get; init; } = [];
    // 2026-10-24 NotedexOrig2: AST 목록 계층을 모든 표시 표면에 공유한다. 원문 공백을 다시 파싱하지 않는다.
    public int ListDepth { get; init; }
    public int IndentationColumns { get; init; }
    public string ListMarker { get; init; } = "";
    internal int IndentStep { get; init; } = 2;
    internal int OutdentStep { get; init; } = 2;
    internal bool HasPreviousListSibling { get; init; }
}

/// <summary>name: MarkdownDisplayRun; desc: 구분자를 숨긴 표시 문자열과 정확한 원문 content span/서식. 이미지 요청 없음.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 시각 편집 source mapping.</summary>
public sealed record MarkdownDisplayRun(string Text, int SourceStart, int SourceLength, bool Bold = false,
    bool Italic = false, bool Code = false, string? Link = null);

/// <summary>name: MarkdownProjection; desc: GFM AST로 task 구조를 찾되 원문을 직렬화/정규화하지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 원문 보존 파서.</summary>
public static class MarkdownProjection
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseTaskLists().UsePreciseSourceLocation().Build();

    /// <summary>name: Parse; desc: 첫 list paragraph의 첫 inline marker만 추출하고 코드/이스케이프를 제외한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AST source spans.</summary>
    public static (IReadOnlyList<MarkdownTaskItem> Tasks, IReadOnlyList<MarkdownVisualBlock> Blocks) Parse(string noteId, long revision, string source)
    {
        MarkdownDocument document = Markdown.Parse(source, Pipeline);
        Dictionary<ListItemBlock, ListGeometry> listGeometry = [];
        CollectListGeometry(document, listGeometry);
        List<MarkdownTaskItem> tasks = [];
        List<MarkdownVisualBlock> blocks = [];
        foreach (LeafBlock leaf in Leaves(document))
        {
            int start = Math.Clamp(leaf.Span.Start, 0, source.Length);
            int end = Math.Clamp(leaf.Span.End + 1, start, source.Length);
            MarkdownTaskItem? task = null;
            if (leaf is ParagraphBlock paragraph && paragraph.Parent is ListItemBlock parent && parent.Count > 0 && ReferenceEquals(parent[0], paragraph) && paragraph.Inline?.FirstChild is TaskList marker)
            {
                int p = marker.Span.Start;
                if (p >= 0 && p + 2 < source.Length && source[p] == '[' && source[p + 2] == ']' && source[p + 1] is ' ' or '\t' or '\v' or '\f' or 'x' or 'X' && (p + 3 >= source.Length || IsGfmWhitespace(source[p + 3])))
                {
                    int content = p + 3;
                    while (content < end && source[content] is ' ' or '\t') content++;
                    task = new(noteId, revision, p, p + 1, content, end - content, CountLines(source, p), source[p + 1] is 'x' or 'X', source[content..end]);
                    tasks.Add(task);
                }
            }
            string kind = leaf switch { CodeBlock => "Code", HeadingBlock => "Heading", ParagraphBlock when task != null => "Task", ParagraphBlock when leaf.Parent is ListItemBlock => "List", ParagraphBlock => "Paragraph", _ => "Unsupported" };
            int contentStart = task?.ContentStart ?? start;
            if (leaf is HeadingBlock heading)
            {
                while (contentStart < end && source[contentStart] == '#') contentStart++;
                while (contentStart < end && source[contentStart] == ' ') contentStart++;
            }
            IReadOnlyList<MarkdownDisplayRun> runs = leaf.Inline == null || kind is "Code" or "Unsupported" ? [new(source[contentStart..end], contentStart, end - contentStart)] : DisplayRuns(leaf.Inline, source, taskMarker: task?.MarkerStart);
            string display = string.Concat(runs.Select(r => r.Text));
            ListItemBlock? listItem = null;
            for (ContainerBlock? ancestor = leaf.Parent; ancestor != null; ancestor = ancestor.Parent)
                if (ancestor is ListItemBlock found) { listItem = found; break; }
            ListGeometry? geometry = listItem != null && listGeometry.TryGetValue(listItem, out ListGeometry? foundGeometry) ? foundGeometry : null;
            bool markerVisible = task == null && leaf is ParagraphBlock && listItem?.Count > 0 && ReferenceEquals(listItem[0], leaf);
            blocks.Add(new(kind, start, end - start, contentStart, end - contentStart, display, leaf is HeadingBlock h ? h.Level : 0, task)
            {
                Runs = runs, ListDepth = geometry?.Depth ?? 0,
                IndentationColumns = geometry?.Columns ?? 0,
                ListMarker = markerVisible ? geometry?.Marker ?? "" : "",
                IndentStep = geometry?.IndentStep ?? 2, OutdentStep = geometry?.OutdentStep ?? 2,
                HasPreviousListSibling = geometry?.HasPreviousSibling ?? false
            });
        }
        return (tasks, blocks);
    }

    private sealed record ListGeometry(int Depth, int Columns, string Marker, int IndentStep, int OutdentStep, bool HasPreviousSibling);

    /// <summary>name: CollectListGeometry; desc: AST의 실제 item 폭과 부모 구조로 번호·중첩·Tab 폭을 한 번에 계산한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 목록 표시 정합성.</summary>
    private static void CollectListGeometry(ContainerBlock container, Dictionary<ListItemBlock, ListGeometry> result, int depth = 0, int parentWidth = 2, int? rootColumn = null)
    {
        foreach (Block child in container)
        {
            if (child is ListBlock list)
            {
                ListItemBlock? previous = null;
                foreach (ListItemBlock item in list.OfType<ListItemBlock>())
                {
                    // 12. 는 네 열을 차지하므로 고정 두 칸을 넣으면 자식 목록이 되지 않는다.
                    string marker = list.IsOrdered ? item.Order.ToString(System.Globalization.CultureInfo.InvariantCulture) + list.OrderedDelimiter : "•";
                    int root = rootColumn ?? item.Column;
                    result[item] = new(depth + 1, Math.Max(0, item.Column - root), marker, Math.Max(1, previous?.ColumnWidth ?? item.ColumnWidth), Math.Max(1, parentWidth), previous != null);
                    CollectListGeometry(item, result, depth + 1, Math.Max(1, item.ColumnWidth), root);
                    previous = item;
                }
            }
            else if (child is ContainerBlock nested) CollectListGeometry(nested, result, depth, parentWidth, rootColumn);
        }
    }

    /// <summary>name: Leaves; desc: list/quote 내부도 재귀 탐색하되 HTML을 실행하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AST 순회.</summary>
    private static IEnumerable<LeafBlock> Leaves(ContainerBlock container)
    { foreach (Block block in container) { if (block is LeafBlock leaf) yield return leaf; if (block is ContainerBlock child) foreach (LeafBlock nested in Leaves(child)) yield return nested; } }
    /// <summary>name: IsGfmWhitespace; desc: Unicode NBSP를 GFM whitespace로 오인하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 명세 §2.1/5.3.</summary>
    private static bool IsGfmWhitespace(char value) => value is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';
    /// <summary>name: CountLines; desc: CR/LF/CRLF를 각각 한 줄로 센다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 원문 line index.</summary>
    private static int CountLines(string source, int end) { int count = 0; for (int i=0;i<end;i++) { if(source[i]=='\r'){count++;if(i+1<end&&source[i+1]=='\n')i++;}else if(source[i]=='\n')count++; } return count; }

    /// <summary>name: DisplayRuns; desc: 파서 AST의 inline 서식만 표시하며 source는 그대로 둔다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 raw 구분자 숨김.</summary>
    private static IReadOnlyList<MarkdownDisplayRun> DisplayRuns(ContainerInline parent, string source, bool bold = false, bool italic = false, string? link = null, int? taskMarker = null)
    {
        List<MarkdownDisplayRun> result = [];
        for (Inline? inline = parent.FirstChild; inline != null; inline = inline.NextSibling)
        {
            int start = Math.Clamp(inline.Span.Start, 0, source.Length);
            int end = Math.Clamp(inline.Span.End + 1, start, source.Length);
            switch (inline)
            {
                case TaskList:
                    if (taskMarker != start) result.Add(new(source[start..end], start, end - start, bold, italic, false, link));
                    break;
                case EmphasisInline emphasis:
                    result.AddRange(DisplayRuns(emphasis, source, bold || emphasis.DelimiterCount >= 2, italic || emphasis.DelimiterCount % 2 == 1, link, taskMarker)); break;
                case LinkInline url when !url.IsImage:
                    result.AddRange(DisplayRuns(url, source, bold, italic, SafeMarkdown.IsSafeLink(url.Url ?? "") ? url.Url : null, taskMarker)); break;
                case LiteralInline literal: result.Add(new(literal.Content.ToString(), start, end - start, bold, italic, false, link)); break;
                case CodeInline code:
                    int ticks = 0; while (start + ticks < end && source[start + ticks] == '`') ticks++;
                    int inner = Math.Min(end, start + ticks), length = Math.Max(0, end - start - ticks * 2);
                    result.Add(new(code.Content, inner, length, bold, italic, true, link)); break;
                case LineBreakInline: result.Add(new("\n", start, end - start, bold, italic, false, link)); break;
                case ContainerInline container: result.AddRange(DisplayRuns(container, source, bold, italic, link, taskMarker)); break;
                default: result.Add(new(source[start..end], start, end - start, bold, italic, false, link)); break;
            }
        }
        return result;
    }
}
