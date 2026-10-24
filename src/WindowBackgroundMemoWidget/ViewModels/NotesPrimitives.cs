using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using WindowBackgroundMemoWidget.Models;
using WindowBackgroundMemoWidget.Services;

namespace WindowBackgroundMemoWidget.ViewModels;

/// <summary>name: ObservableModel; desc: 관리 UI 공용 변경 알림.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 실제 바인딩 기반.</summary>
public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>name: Notify; desc: 바인딩에 속성 변경을 알린다. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 알림.</summary>
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    /// <summary>name: Assign; desc: 값이 변한 경우에만 알린다. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 알림.</summary>
    protected bool Assign<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
}

/// <summary>name: FolderItem; desc: 평면 폴더 바인딩. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 폴더 계약.</summary>
public sealed class FolderItem(string id, string name) : ObservableModel
{
    private string nameValue = name;
    public string Id { get; } = id;
    public string Name { get => nameValue; internal set => Assign(ref nameValue, value); }
    public override string ToString() => Name;
}

/// <summary>name: MemoListItemViewModel; desc: 편집 원본과 표시 의도를 공유하는 메모 VM. UI 선택으로 배치를 바꾸지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 목록/위젯 공용 모델.</summary>
public sealed class MemoListItemViewModel : ObservableModel
{
    private NoteRecord data;
    private bool editingSuspended;
    private bool globallyHidden;
    public MarkdownDocumentSession Document { get; }
    public event Action<MemoListItemViewModel>? Edited;
    public string Id => data.Id;
    public string Title { get => data.AutoTitle ? AutomaticTitle() : data.Title; set { if (value != Title) Change(data with { Title = Limit(value, 200), AutoTitle = false }, true); } }
    public string Body { get => Document.Source; set { if (!editingSuspended) Document.TryReplace(Document.Revision, value ?? ""); } }
    public string Preview => Body.Replace('\r', ' ').Replace('\n', ' ') is string text ? text[..Math.Min(100, text.Length)] : "";
    public DateTimeOffset UpdatedAt => data.UpdatedAt;
    public string ModifiedLabel => UpdatedAt.ToLocalTime().ToString("MM.dd HH:mm");
    public bool IsPinned { get => data.IsPinned; set => Change(data with { IsPinned = value }); }
    public int Priority { get => data.Priority; set => Change(data with { Priority = Math.Clamp(value, 0, 2) }); }
    public string PriorityLabel => Priority switch { 1 => "중요", 2 => "긴급", _ => "일반" };
    public string? FolderId { get => data.FolderId; set => Change(data with { FolderId = value }, true); }
    public string TagsText { get => string.Join(", ", data.Tags); set => Change(data with { Tags = ParseTags(value) }, true); }
    public bool IsVisible { get => data.IsVisible; set => Change(data with { IsVisible = value }); }
    public string VisibilityLabel => IsDeleted ? "휴지통" : IsVisible ? "표시 요청됨" : "숨김";
    public bool IsDeleted { get => data.IsDeleted; internal set => Change(data with { IsDeleted = value }); }
    public bool IsEditable => !IsDeleted && !editingSuspended && !Document.HasConflict;
    public double X { get => data.X; set => Change(data with { X = value }); }
    public double Y { get => data.Y; set => Change(data with { Y = value }); }
    public double Width { get => data.Width; set => Change(data with { Width = Math.Clamp(value, 240, 20000) }); }
    public double Height { get => data.Height; set => Change(data with { Height = Math.Clamp(value, 200, 20000) }); }
    public bool IsLocked { get => data.IsLocked; set => Change(data with { IsLocked = value }); }
    public string ColorPreset { get => data.Appearance?.Color ?? "default"; set { NoteAppearance next = (data.Appearance ?? new()) with { Color = value }; next.Validate(); Change(data with { Appearance = next }); } }
    public double MemoFontSize { get => data.Appearance?.FontSize ?? 16; set { NoteAppearance next = (data.Appearance ?? new()) with { FontSize = value }; next.Validate(); Change(data with { Appearance = next }); } }
    public Brush MemoBackground => ColorPreset switch { "purple" => Background("#343044"), "blue" => Background("#293746"), "green" => Background("#293C37"), "rose" => Background("#422F3D"), _ => Background("#282A36") };
    public NotePlacement? Placement => data.Placement;
    public long PlacementResetVersion { get; private set; }
    private string widgetStatus = "";
    public string WidgetStatus { get => widgetStatus; internal set => Assign(ref widgetStatus, value); }

    /// <summary>name: Background; desc: 안전한 프리셋의 frozen brush만 생성한다. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-05.</summary>
    private static Brush Background(string hex) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(hex)!; brush.Freeze(); return brush; }
    /// <summary>name: RememberPlacement; desc: 물리 좌표와 모니터 기준 DIP를 하나의 변경으로 기록한다. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-07.</summary>
    public void RememberPlacement(PixelBounds bounds, NotePlacement placement)
    { placement.Validate(); Change(data with { X = bounds.X, Y = bounds.Y, Width = Math.Max(240, bounds.Width), Height = Math.Max(200, bounds.Height), Placement = placement }); }
    /// <summary>name: ResetPlacement; desc: 내용·표시 의도·배치잠금은 유지하고 배치 초기화 요청을 알린다. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-07.</summary>
    public void ResetPlacement() { PlacementResetVersion++; Change(data with { X = 100, Y = 100, Width = 380, Height = 340, Placement = null }); Notify(nameof(PlacementResetVersion)); }

    /// <summary>name: MemoListItemViewModel; desc: 검증된 불변 레코드로 초기화. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 모델 초기화.</summary>
    public MemoListItemViewModel(NoteRecord record, MarkdownDocumentSession document)
    {
        data = record; Document = document;
        Document.Changed += () => { data = data with { Body = Document.Source, UpdatedAt = DateTimeOffset.UtcNow }; Notify(""); Edited?.Invoke(this); };
        Document.PropertyChanged += (_, _) => Notify(nameof(IsEditable));
    }
    /// <summary>name: AutomaticTitle; desc: 첫 비어 있지 않은 행을 기본 제목으로 쓴다. 명시 제목은 별도 유지한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 Notes 기본 제목.</summary>
    private string AutomaticTitle() { string title = Document.Projection.SelectMany(b=>b.Text.Split(['\r','\n'])).FirstOrDefault(s=>!string.IsNullOrWhiteSpace(s))?.Trim() ?? "새 메모"; return Limit(title,200); }
    /// <summary>name: SuspendEditing; desc: 복원 트랜잭션 중 편집을 잠시 막아 새 입력이 교체로 사라지지 않게 한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 복원 경쟁 보호.</summary>
    internal void SuspendEditing(bool value) { editingSuspended = value; Notify(nameof(IsEditable)); }
    /// <summary>name: SetGlobalHidden; desc: 전체 숨김은 개별 표시 설정을 바꾸지 않고 편집 가능 여부만 알린다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 가시성 분리.</summary>
    internal void SetGlobalHidden(bool value) { globallyHidden = value; Notify(nameof(IsEditable)); }
    /// <summary>name: Snapshot; desc: 저장 writer에 불변 데이터를 전달. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 snapshot.</summary>
    public NoteRecord Snapshot() => data with { Title = Title, Body = Document.Source, Tags = data.Tags.ToArray(), DocumentRevision = Document.Revision };
    /// <summary>name: ParseTags; desc: 태그 중복/길이/개수를 정규화. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 태그 검증.</summary>
    public static string[] ParseTags(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimStart('#')).Where(s => s.Length > 0).Select(s => Limit(s, 50)).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
    /// <summary>name: Limit; desc: 원문 최대 길이를 일관되게 제한. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 입력 범위.</summary>
    private static string Limit(string? value, int max) => (value ?? "")[..Math.Min(max, (value ?? "").Length)];
    /// <summary>name: Change; desc: 레코드 변경과 파생 속성을 한 번에 알리고 저장을 예약한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 최신 편집 보존.</summary>
    private void Change(NoteRecord replacement, bool modifiesContent = false)
    {
        if (editingSuspended || replacement == data || replacement with { Tags = data.Tags } == data && replacement.Tags.SequenceEqual(data.Tags)) return;
        data = modifiesContent ? replacement with { UpdatedAt = DateTimeOffset.UtcNow } : replacement;
        Notify("");
        Edited?.Invoke(this);
    }
}

/// <summary>name: NotesCommand; desc: 실제 VM 명령과 CanExecute 상태를 연결한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 UI command 계약.</summary>
public sealed class NotesCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    /// <summary>name: CanExecute; desc: 현재 선택/보기의 허용 여부. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 명령 상태.</summary>
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    /// <summary>name: Execute; desc: 허용된 명령만 실행. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 명령 실행.</summary>
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    /// <summary>name: Refresh; desc: 선택 변경 후 버튼 상태 갱신. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 명령 알림.</summary>
    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
