using System;
using System.Collections.Generic;
using System.Linq;
using WindowBackgroundMemoWidget.ViewModels;
using WindowBackgroundMemoWidget.Views;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: IDesktopHost; desc: 문서 저장과 위젯 표시 경계.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 SHARED-01.</summary>
public interface IDesktopHost : IDisposable
{
    void Synchronize(IReadOnlyList<MemoListItemViewModel> notes, bool globallyHidden);
    bool HasUncommittedInput(string? noteId = null);
    bool RevealUncommittedInput();
}

/// <summary>name: NotesDesktopHost; desc: Orig3 제품 위젯에 동일 VM/MarkdownDocumentSession을 전달한다. 본문 사본 없음.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 실제 Views 위젯 연결.</summary>
internal sealed class NotesDesktopHost : IDesktopHost
{
    private readonly Dictionary<string, NoteWidgetWindow> windows = [];
    private bool synchronizing;
    private readonly HashSet<string> recoveringDrafts = new(StringComparer.OrdinalIgnoreCase);
    public bool HasUncommittedInput(string? noteId = null) => windows.Any(pair => (noteId == null || pair.Key.Equals(noteId, StringComparison.OrdinalIgnoreCase)) && pair.Value.DocumentEditor?.HasUncommittedInput == true);
    /// <summary>name: RevealUncommittedInput; desc: 저장된 숨김 의도는 바꾸지 않고 복구가 필요한 화면만 임시로 연다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC-10 종료 보호.</summary>
    public bool RevealUncommittedInput()
    {
        foreach (var pair in windows.Where(p => p.Value.DocumentEditor?.HasUncommittedInput == true))
        { recoveringDrafts.Add(pair.Key); pair.Value.Show(); pair.Value.Activate(); pair.Value.DocumentEditor?.FocusEditor(); return true; }
        return false;
    }

    /// <summary>name: Synchronize; desc: 표시 의도만 반영하고 숨김 관리 편집을 막지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 shared surface.</summary>
    public void Synchronize(IReadOnlyList<MemoListItemViewModel> notes, bool globallyHidden)
    {
        if (synchronizing) return;
        synchronizing = true;
        try
        {
            foreach (string id in windows.Keys.Where(id => !notes.Any(n => n.Id == id && !n.IsDeleted)).ToArray())
            { if (HasUncommittedInput(id)) continue; windows[id].Release(); windows.Remove(id); }
            foreach (MemoListItemViewModel note in notes.Where(n => !n.IsDeleted))
            {
                if (!HasUncommittedInput(note.Id)) recoveringDrafts.Remove(note.Id);
                bool visible = note.IsVisible && !globallyHidden || recoveringDrafts.Contains(note.Id);
                if (windows.TryGetValue(note.Id, out NoteWidgetWindow? previous) && !ReferenceEquals(previous.DataContext, note))
                {
                    if (HasUncommittedInput(note.Id)) { if (visible && !previous.IsVisible) previous.Show(); continue; }
                    previous.Release(); windows.Remove(note.Id);
                }
                if (!windows.TryGetValue(note.Id, out NoteWidgetWindow? window))
                {
                    if (!visible) continue;
                    using (StartupTelemetry.Measure("widget-construction")) window = new NoteWidgetWindow(note);
                    windows.Add(note.Id, window);
                    NoteWidgetWindow created = window;
                    window.Closed += (_, _) => { if (windows.TryGetValue(note.Id, out NoteWidgetWindow? current) && ReferenceEquals(current, created)) windows.Remove(note.Id); };
                }
                if (visible && !window.IsVisible)
                {
                    WidgetWindowController.PrepareForDisplay(window);
                    using var measurement = StartupTelemetry.Measure("widget-show"); window.Show();
                }
                else if (!visible && window.IsVisible) window.Hide();
                window.UpdateLock();
            }
        }
        finally { synchronizing = false; }
    }

    /// <summary>name: Dispose; desc: 문서 표시 의도를 바꾸지 않고 창만 해제한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 수명 정리.</summary>
    public void Dispose()
    {
        // 명시 UI 종료는 App guard가 먼저 확인한다. 직접 Dispose 호출에도 draft 화면을 폐기하지 않는다.
        foreach (var pair in windows.ToArray()) if (!HasUncommittedInput(pair.Key)) { pair.Value.Release(); windows.Remove(pair.Key); }
    }
}
