using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: MarkdownDocumentSession; desc: NoteId별 유일한 편집 revision/원문/파일 상태. 모든 표면이 같은 객체를 사용한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 SHARED-01/CHECK-02.</summary>
public sealed class MarkdownDocumentSession : INotifyPropertyChanged
{
    private readonly MarkdownFileStore store;
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly Stack<(string Before, string After)> undo = [], redo = [];
    private string source;
    private string? diskHash;
    private bool bom;
    private string? conflictPrefix;
    private string? recoveryExternalHash;
    public string NoteId { get; }
    public string Source => source;
    public long Revision { get; private set; }
    public long SavedRevision { get; private set; }
    public string ContentHash => diskHash ?? "";
    public string FilePath { get; private set; }
    public bool IsDirty => Revision != SavedRevision;
    public bool HasConflict => State == MarkdownDocumentState.Conflict;
    public MarkdownDocumentState State { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public IReadOnlyList<MarkdownTaskItem> Tasks { get; private set; } = [];
    public IReadOnlyList<MarkdownVisualBlock> Projection { get; private set; } = [];
    public bool CanUndo => undo.Count > 0 && !HasConflict;
    public bool CanRedo => redo.Count > 0 && !HasConflict;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    /// <summary>name: MarkdownDocumentSession; desc: 읽은 파일 baseline 또는 신규 pending 문서를 만든다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 공유 생성 계약.</summary>
    public MarkdownDocumentSession(string noteId, MarkdownFileStore fileStore, string source = "", MarkdownFileVersion? saved = null, long revision = 0)
    {
        NoteId = MarkdownFileStore.NormalizeId(noteId); store = fileStore;
        this.source = saved?.Source ?? source; diskHash = saved?.Hash; bom = saved?.HasBom ?? false;
        FilePath = saved?.FilePath ?? store.CanonicalPath(NoteId); Revision = revision; SavedRevision = saved == null ? -1 : revision;
        State = saved == null ? MarkdownDocumentState.Modified : MarkdownDocumentState.Clean;
        Reparse();
    }

    /// <summary>name: TryReplace; desc: 표시한 revision이 현재와 같을 때만 전체 편집을 적용한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 stale 전체 덮기 거절.</summary>
    public bool TryReplace(long expectedRevision, string newSource)
    {
        if (expectedRevision != Revision || HasConflict) { StatusMessage = "문서가 바뀌었습니다. 최신 내용을 확인하세요."; Notify(); return false; }
        if (newSource.Length > 1_000_000) { StatusMessage = "지원 본문 크기를 초과했습니다."; Notify(); return false; }
        if (source == newSource) return true;
        undo.Push((source, newSource));
        if (undo.Count > 256) { var recent = undo.Take(256).Reverse().ToArray(); undo.Clear(); foreach (var item in recent) undo.Push(item); }
        redo.Clear(); SetSource(newSource); return true;
    }

    /// <summary>name: TryEdit; desc: 현재 revision의 정확한 범위만 교체한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 부분 patch.</summary>
    public bool TryEdit(long expectedRevision, int start, int length, string replacement)
    { return start >= 0 && length >= 0 && start <= source.Length - length && TryReplace(expectedRevision, source[..start] + replacement + source[(start + length)..]); }

    /// <summary>name: TryEditVisual; desc: 구분자 없는 표시 선택을 파서 source span으로 되돌린다. 혼합 서식 경계는 원문 편집으로 안내한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 시각 편집 계약 v2.</summary>
    public bool TryEditVisual(long revision, MarkdownVisualBlock block, int displayStart, int displayLength, string replacement)
    {
        if (revision != Revision || !Projection.Contains(block) || !TryMapVisual(block, displayStart, displayLength, out int start, out int length))
        { StatusMessage = "서식 경계를 포함하는 선택은 Markdown 모드에서 편집하세요. 입력은 화면에 유지됩니다."; Notify(); return false; }
        return TryEdit(revision, start, length, replacement);
    }

    /// <summary>name: FormatVisualSelection; desc: UI가 다시 파싱하지 않고 표시 선택에 서식을 적용한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 Notes 도구.</summary>
    public bool FormatVisualSelection(long revision, MarkdownVisualBlock block, int displayStart, int displayLength, string kind)
    { return revision == Revision && Projection.Contains(block) && TryMapVisual(block, displayStart, displayLength, out int start, out int length) && FormatSelection(revision, start, length, kind); }

    /// <summary>name: TryMapVisual; desc: 서식 구분자를 선택 일부로 잘라내지 않도록 같은 서식 범위 안에서만 patch한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 source 범위 보호.</summary>
    private bool TryMapVisual(MarkdownVisualBlock block, int position, int length, out int start, out int count)
    {
        start = count = 0;
        if (position < 0 || length < 0 || position > block.Text.Length - length) return false;
        if (block.Runs.Count == 0) { start = block.ContentStart + position; count = length; return block.ContentLength == block.Text.Length; }
        int offset = 0;
        List<(MarkdownDisplayRun Run, int Start, int End)> spans = [];
        foreach (MarkdownDisplayRun run in block.Runs) { spans.Add((run, offset, offset + run.Text.Length)); offset += run.Text.Length; }
        var first = spans.FirstOrDefault(s => position >= s.Start && (position < s.End || position == s.End && length == 0));
        if (first.Run == null) first = spans[^1];
        var last = length == 0 ? first : spans.LastOrDefault(s => position + length > s.Start && position + length <= s.End);
        if (last.Run == null) return false;
        foreach (var span in spans.Where(s => s.End > position && s.Start < position + length))
            if (span.Run.Bold != first.Run.Bold || span.Run.Italic != first.Run.Italic || span.Run.Code != first.Run.Code || span.Run.Link != first.Run.Link) return false;
        int localStart = position - first.Start, localEnd = position + length - last.Start;
        if (!MapBoundary(first.Run, localStart, out start) || !MapBoundary(last.Run, localEnd, out int end)) return false;
        count = end - start; return count >= 0;
    }

    /// <summary>name: MapBoundary; desc: escape/entity 같은 비선형 span은 전체 경계만 허용해 잘못된 문자 위치를 추정하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 UTF-16 mapping.</summary>
    private bool MapBoundary(MarkdownDisplayRun run, int displayOffset, out int sourceOffset)
    {
        sourceOffset = run.SourceStart;
        if (displayOffset < 0 || displayOffset > run.Text.Length) return false;
        if (displayOffset == 0) return true;
        if (displayOffset == run.Text.Length) { sourceOffset += run.SourceLength; return true; }
        if (run.SourceLength == run.Text.Length && run.SourceStart >= 0 && run.SourceStart <= source.Length - run.SourceLength && source.Substring(run.SourceStart, run.SourceLength) == run.Text)
        { sourceOffset += displayOffset; return true; }
        return false;
    }

    /// <summary>name: ToggleTask; desc: GFM AST가 검증한 현재 revision marker 한 글자만 바꾼다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 무손실 checkbox.</summary>
    public bool ToggleTask(MarkdownTaskItem item)
    {
        if (item.NoteId != NoteId || item.Revision != Revision || !Tasks.Contains(item)) { StatusMessage = "체크 항목이 변경되어 다시 표시해야 합니다."; Notify(); return false; }
        return TryEdit(item.Revision, item.MarkerOffset, 1, item.IsChecked ? " " : "x");
    }

    /// <summary>name: ConvertToChecklist; desc: 선택 행의 텍스트/줄바꿈을 유지하고 코드 범위를 거절한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 checklist 생성.</summary>
    public bool ConvertToChecklist(long revision, int selectionStart, int selectionLength, bool isComposing = false)
    {
        if (isComposing || revision != Revision || selectionStart < 0 || selectionLength < 0 || selectionStart > source.Length - selectionLength) return false;
        int start = LineStart(selectionStart), end = LineEnd(selectionStart + selectionLength);
        if (Projection.Any(p => p.Kind is "Code" or "Unsupported" && p.SourceStart < end && p.SourceStart + p.SourceLength > start)) return false;
        string segment = source[start..end];
        string changed = Regex.Replace(segment, @"(?m)^([ \t]*)(.*?)(\r?)$", m =>
        {
            int absolute = start + m.Index;
            if (Tasks.Any(t => t.MarkerStart >= absolute && t.MarkerStart < absolute + m.Length)) return m.Value;
            string body = Regex.Replace(m.Groups[2].Value, @"^(?:[-+*]|\d{1,9}[.)])[ \t]+", "");
            return m.Groups[1].Value + "- [ ] " + body + m.Groups[3].Value;
        });
        return TryEdit(revision, start, end - start, changed);
    }

    /// <summary>name: ContinueChecklist; desc: Enter/빈 Enter/Shift Enter를 원문 한 patch로 적용한다. IME 확정은 거절한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 task 키보드.</summary>
    public bool ContinueChecklist(long revision, int caret, bool shift, bool isComposing = false)
    {
        if (isComposing || revision != Revision || caret < 0 || caret > source.Length) return false;
        int line = LineStart(caret), end = LineEnd(caret);
        if (!shift && caret != end) return false;
        MarkdownTaskItem? task = Tasks.FirstOrDefault(t => t.MarkerStart <= caret && t.ContentStart + t.ContentLength >= caret);
        if (task != null) line = LineStart(task.MarkerStart);
        // 빈 작성 항목은 GFM 최종 AST에 없을 수 있으므로 현재 줄의 정확한 prefix만 본다.
        Match prefix = Regex.Match(source[line..LineEnd(line)], @"^([ \t]*)([-+*]|\d{1,9}[.)])([ \t]+)\[([ xX])\]([ \t]*)(.*)$");
        if (!prefix.Success || task == null && prefix.Groups[6].Value.Length > 0 || Projection.Any(p => p.Kind == "Code" && p.SourceStart <= line && p.SourceStart + p.SourceLength > line)) return false;
        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : source.Contains('\r') && !source.Contains('\n') ? "\r" : "\n";
        if (shift) return TryEdit(revision, caret, 0, newline + new string(' ', prefix.Groups[1].Length + prefix.Groups[2].Length + prefix.Groups[3].Length));
        if (string.IsNullOrWhiteSpace(task?.Text ?? prefix.Groups[6].Value)) return TryEdit(revision, line, end - line, prefix.Groups[1].Value);
        string bullet = prefix.Groups[2].Value;
        if (char.IsDigit(bullet[0]) && int.TryParse(bullet[..^1], out int number)) bullet = (number + 1) + bullet[^1].ToString();
        return TryEdit(revision, caret, 0, newline + prefix.Groups[1].Value + bullet + prefix.Groups[3].Value + "[ ] ");
    }
    /// <summary>name: ContinueChecklistVisual; desc: 숨겨진 서식 구분자를 UI가 추측하지 않도록 표시 caret을 원문으로 매핑한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 CHECK-02 caret.</summary>
    public bool ContinueChecklistVisual(long revision, MarkdownVisualBlock block, int displayCaret, bool shift, bool isComposing = false)
    {
        if (isComposing || revision != Revision || !Projection.Contains(block)) return false;
        if (displayCaret == block.Text.Length) return ContinueChecklist(revision, LineEnd(block.SourceStart + block.SourceLength), shift, false);
        return TryMapVisual(block, displayCaret, 0, out int start, out _) && ContinueChecklist(revision, start, shift, false);
    }

    /// <summary>name: FormatSelection; desc: 명시적 서식 도구는 현재 source range만 수정한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 Notes 서식 명령.</summary>
    public bool FormatSelection(long revision, int start, int length, string kind)
    {
        if (start < 0 || length < 0 || start > source.Length - length) return false;
        if (kind.Equals("Checklist", StringComparison.OrdinalIgnoreCase)) return ConvertToChecklist(revision, start, length);
        string selected = source.Substring(start, length);
        return kind.ToLowerInvariant() switch
        {
            "bold" => TryEdit(revision, start, length, "**" + selected + "**"),
            "italic" => TryEdit(revision, start, length, "*" + selected + "*"),
            "heading" => SetHeading(revision, start),
            "bullet" => TryEdit(revision, LineStart(start), 0, "- "),
            _ => false
        };
    }

    /// <summary>name: SetHeading; desc: 현재 행의 heading prefix만 토글하고 code 블록은 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 제목 도구.</summary>
    private bool SetHeading(long revision, int start)
    {
        int line = LineStart(start);
        if (Projection.Any(p => p.Kind == "Code" && p.SourceStart <= start && p.SourceStart + p.SourceLength >= start)) return false;
        Match prefix = Regex.Match(source[line..LineEnd(line)], @"^ {0,3}#{1,6}[ \t]+");
        return TryEdit(revision, line, prefix.Success ? prefix.Length : 0, prefix.Success ? "" : "# ");
    }

    /// <summary>name: IndentSelection; desc: 선택 목록 행을 한 source patch로 들여쓰기/내어쓰기한다. 조합·오래된 revision·code는 거절한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 CHECK-02.</summary>
    public bool IndentSelection(long revision, int start, int length, bool outdent, bool isComposing = false)
    {
        if (isComposing || revision != Revision || start < 0 || length < 0 || start > source.Length - length) return false;
        int first = LineStart(start), last = LineEnd(length > 0 ? start + length - 1 : start);
        if (Projection.Any(p => p.Kind == "Code" && p.SourceStart < last && p.SourceStart + p.SourceLength > first)) return false;
        if (!Regex.IsMatch(source[first..LineEnd(first)], @"^[ \t]*(?:[-+*]|\d{1,9}[.)])[ \t]+")) return false;
        MarkdownVisualBlock? listBlock = Projection.FirstOrDefault(p => p.ListDepth > 0 && p.Kind is "Task" or "List" && LineStart(p.SourceStart) == first);
        if (listBlock is null) return false;
        if (!outdent && !listBlock.HasPreviousListSibling) return false;
        int step = outdent ? listBlock.OutdentStep : listBlock.IndentStep;
        string selected = source[first..last];
        string replacement = Regex.Replace(selected, @"(^|(?<=\n)|(?<=\r)(?!\n))([^\r\n]*)", match =>
        {
            string text = match.Value;
            if (text.Length == 0) return text;
            if (!outdent) return new string(' ', step) + text;
            if (text.StartsWith('\t')) return text[1..];
            int remove = text.TakeWhile(c => c == ' ').Take(step).Count(); return text[remove..];
        });
        if (replacement == selected) return false;
        int expectedDepth = outdent ? Math.Max(1, listBlock.ListDepth - 1) : listBlock.ListDepth + 1;
        bool HasExpectedList(string patch)
        {
            string candidate = source[..first] + patch + source[last..];
            int content = first;
            // 이 선택의 첫 marker 위치만 추적한다. 본문과 나머지 행은 원문 patch 그대로 보존한다.
            while (content < candidate.Length && candidate[content] is '\r' or '\n' or ' ' or '\t') content++;
            return MarkdownProjection.Parse(NoteId, Revision, candidate).Blocks.Any(b => b.ListDepth == expectedDepth && b.Kind is "List" or "Task" && LineStartIn(candidate, b.SourceStart) <= content && b.SourceStart >= content && LineStartIn(candidate, b.SourceStart) == LineStartIn(candidate, content));
        }
        if (!HasExpectedList(replacement))
        {
            if (outdent) return false;
            // 1 이외 번호는 기존 문단을 interrupt하지 못한다. 번호를 바꾸지 않고 빈 줄을 포함한 한 patch로 중첩시킨다.
            string separator = first >= 2 && source.AsSpan(first - 2, 2).SequenceEqual("\r\n") ? "\r\n" : first > 0 && source[first - 1] == '\r' ? "\r" : "\n";
            replacement = separator + replacement;
            if (!HasExpectedList(replacement)) return false;
        }
        return TryEdit(revision, first, last - first, replacement);
    }

    private static int LineStartIn(string text, int position)
    { int start = Math.Min(position, text.Length); while (start > 0 && text[start - 1] is not ('\r' or '\n')) start--; return start; }

    /// <summary>name: IndentVisual; desc: UI가 숨겨진 marker offset을 추측하지 않고 block을 목록 명령에 전달한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 CHECK-02.</summary>
    public bool IndentVisual(long revision, MarkdownVisualBlock block, bool outdent, bool isComposing = false)
        => Projection.Contains(block) && IndentSelection(revision, block.SourceStart, block.SourceLength, outdent, isComposing);

    /// <summary>name: Undo; desc: 의미 있는 patch 한 번을 복구하며 revision은 계속 증가한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 공유 undo.</summary>
    public bool Undo() { if (!CanUndo) return false; var edit = undo.Pop(); if (source != edit.After) return false; redo.Push(edit); SetSource(edit.Before); return true; }
    /// <summary>name: Redo; desc: 같은 세션의 마지막 undo를 재적용한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 공유 redo.</summary>
    public bool Redo() { if (!CanRedo) return false; var edit = redo.Pop(); if (source != edit.Before) return false; undo.Push(edit); SetSource(edit.After); return true; }

    /// <summary>name: SaveAsync; desc: 현재 snapshot만 파일 교체 후 완료 처리하고 도중 새 입력은 dirty로 남긴다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 revision 저장.</summary>
    public Task<bool> SaveAsync() => SaveRevisionAsync(Revision, source);
    /// <summary>name: SaveRevisionAsync; desc: repository가 캡처한 revision/body의 저장 응답이 최신 입력을 덮지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 stale 응답 보호.</summary>
    public async Task<bool> SaveRevisionAsync(long revision, string capturedSource)
    {
        await io.WaitAsync();
        try
        {
            if (HasConflict) return false;
            if (revision > Revision || revision < 0) { StatusMessage = "유효하지 않은 저장 revision입니다."; Notify(); return false; }
            if (State is MarkdownDocumentState.Missing or MarkdownDocumentState.Unavailable)
            {
                MarkdownFileVersion? reconnected = await store.ReadAsync(NoteId);
                if (reconnected == null || reconnected.Hash != diskHash) { EnterConflict(reconnected); return false; }
                State = IsDirty ? MarkdownDocumentState.Modified : MarkdownDocumentState.Clean;
            }
            if (revision <= SavedRevision) return true;
            State = MarkdownDocumentState.Saving; StatusMessage = "파일 저장 중"; Notify();
            MarkdownFileVersion result = await store.WriteAsync(NoteId, capturedSource, diskHash, bom);
            diskHash = result.Hash; bom = result.HasBom; FilePath = result.FilePath; SavedRevision = revision;
            State = IsDirty ? MarkdownDocumentState.Modified : MarkdownDocumentState.Clean;
            StatusMessage = IsDirty ? "새 입력 저장 대기" : "파일 저장 완료"; Notify(); return true;
        }
        catch (MarkdownFileConflictException conflict) { EnterConflict(conflict.External, conflict.PrimaryRecoveryRequired); return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EncoderFallbackException or DecoderFallbackException)
        { State = MarkdownDocumentState.Error; StatusMessage = "파일 저장 실패 · 입력/기존 파일 보존 · 다른 위치 저장 가능"; Notify(); return false; }
        finally { io.Release(); }
    }

    /// <summary>name: RefreshAsync; desc: 안정된 외부 파일만 반영하며 미저장 변경이 있으면 양쪽을 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 외부 수정 감지.</summary>
    public async Task RefreshAsync()
    {
        if (HasConflict || SavedRevision < 0) return;
        await io.WaitAsync();
        try
        {
            MarkdownFileVersion? external = await store.ReadAsync(NoteId);
            if (external == null) { State = MarkdownDocumentState.Missing; StatusMessage = "파일이 삭제/이름 변경되었습니다. 원문은 유지됩니다."; Notify(); return; }
            if (external.Hash == diskHash)
            {
                if (FilePath != external.FilePath) { FilePath = external.FilePath; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(FilePath))); }
                if (State is MarkdownDocumentState.Missing or MarkdownDocumentState.Unavailable)
                { State=IsDirty?MarkdownDocumentState.Modified:MarkdownDocumentState.Clean;StatusMessage=IsDirty?"파일 연결 복원 · 저장 대기":"파일 연결 복원 · 저장된 내용 확인";Notify(); }
                return;
            }
            external = await store.ReadAsync(NoteId, true);
            if (external == null) { State = MarkdownDocumentState.Missing; StatusMessage = "외부 교체 중 파일이 사라졌습니다. 원문은 유지됩니다."; Notify(); return; }
            if (external.Hash == diskHash) return;
            if (IsDirty) { EnterConflict(external); return; }
            AcceptExternal(external);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { State = MarkdownDocumentState.Unavailable; StatusMessage = "외부 파일에 접근하지 못했습니다. 원문은 유지됩니다."; Notify(); }
        finally { io.Release(); }
    }

    /// <summary>name: ResolveConflictAsync; desc: 보존본을 만든 뒤 명시 선택만 수용한다. 외부 원본 자동 덮어쓰기는 없다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 충돌 선택.</summary>
    public async Task<bool> ResolveConflictAsync(string choice)
    {
        if (!HasConflict || choice is not ("external" or "local-copy")) return false;
        await io.WaitAsync();
        try
        {
            MarkdownFileVersion? external = await store.ReadAsync(NoteId, true);
            if (recoveryExternalHash != null && external?.Hash != recoveryExternalHash)
            { StatusMessage = "주 파일 복구가 필요합니다. 충돌 폴더의 외부본을 원래 위치에 복구한 뒤 다시 선택하세요. 양쪽 복사본은 유지됩니다."; Notify(); return false; }
            conflictPrefix = store.PreserveConflict(NoteId, source, external);
            if (external == null) { StatusMessage = "외부 파일이 없습니다. 편집본은 충돌 폴더에 보존했습니다."; Notify(); return false; }
            AcceptExternal(external); StatusMessage = choice == "local-copy" ? "내 편집본을 별도 보존하고 외부 파일을 열었습니다." : "외부 파일 수용 · 이전 편집본도 보존됨"; Notify(); return true;
        }
        finally { io.Release(); }
    }

    /// <summary>name: SaveCopyAsync; desc: 실패/충돌 입력을 사용자가 선택한 새 경로에 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 다른 위치 저장.</summary>
    public async Task SaveCopyAsync(string path)
    { byte[] bytes = new UTF8Encoding(false, true).GetBytes(source); await Task.Run(() => MarkdownFileStore.WriteNewDurable(path, bytes)); }
    /// <summary>name: SaveCopyAsync; desc: stale 입력으로 세션 반영이 거절된 UI의 보류 원문도 별도 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC-07/10.</summary>
    public async Task SaveCopyAsync(string path, string pendingSource)
    { byte[] bytes = new UTF8Encoding(false, true).GetBytes(pendingSource); await Task.Run(() => MarkdownFileStore.WriteNewDurable(path, bytes)); }

    /// <summary>name: EnterConflict; desc: 원본을 바꾸기 전에 보존본을 만들고 자동 저장을 중지한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 양쪽 원문 보호.</summary>
    private void EnterConflict(MarkdownFileVersion? external, bool primaryRecoveryRequired = false)
    {
        State = MarkdownDocumentState.Conflict;
        recoveryExternalHash = primaryRecoveryRequired ? external?.Hash : null;
        try { conflictPrefix = store.PreserveConflict(NoteId, source, external); StatusMessage = primaryRecoveryRequired ? "외부 수정 충돌 · 주 파일 복구 필요 · 양쪽 복사본 보존" : "외부 수정 충돌 · 양쪽 버전 보존 · 선택 필요"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StatusMessage = "외부 수정 충돌 · 파일 접근 실패 · 입력을 유지합니다. 다른 위치에 저장하세요."; }
        Notify();
    }
    /// <summary>name: AcceptExternal; desc: 깨끗한 외부 내용을 같은 세션에 반영하고 이전 undo를 새 버전에 적용하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 외부 버전 수용.</summary>
    private void AcceptExternal(MarkdownFileVersion external)
    { source = external.Source; Revision++; SavedRevision = Revision; diskHash = external.Hash; bom = external.HasBom; FilePath = external.FilePath; recoveryExternalHash = null; undo.Clear(); redo.Clear(); State = MarkdownDocumentState.Clean; StatusMessage = "외부 파일 갱신 완료"; Reparse(); Notify(); Changed?.Invoke(); }
    /// <summary>name: SetSource; desc: 메모리 편집 한 번에 revision과 projection을 함께 갱신한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 공유 상태.</summary>
    private void SetSource(string value) { source = value; Revision++; State = MarkdownDocumentState.Modified; StatusMessage = "파일 저장 대기"; Reparse(); Notify(); Changed?.Invoke(); }
    /// <summary>name: Reparse; desc: 현재 revision에만 유효한 source span을 생성한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 projection.</summary>
    private void Reparse() { (Tasks, Projection) = MarkdownProjection.Parse(NoteId, Revision, source); }
    /// <summary>name: Notify; desc: UI가 현재 revision/저장 상태를 다시 읽도록 알린다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 알림.</summary>
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    /// <summary>name: LineStart; desc: CRLF를 정규화하지 않고 행 시작을 찾는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 caret 범위.</summary>
    private int LineStart(int p) { int last = Math.Min(p-1,source.Length-1); while(last>=0&&source[last] is not ('\r' or '\n'))last--; return last+1; }
    /// <summary>name: LineEnd; desc: 행 종료 문자 앞의 source offset을 반환한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 caret 범위.</summary>
    private int LineEnd(int p) { int end=p;while(end<source.Length&&source[end] is not ('\r' or '\n'))end++;return end; }
}
