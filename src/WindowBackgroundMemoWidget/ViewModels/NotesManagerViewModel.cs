using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowBackgroundMemoWidget.Models;
using WindowBackgroundMemoWidget.Services;

namespace WindowBackgroundMemoWidget.ViewModels;

/// <summary>
/// name: NotesManagerViewModel; desc: 실제 메모 관리·검색·분류·영속 저장을 연결하며 위젯 표시 의도와 보기 선택을 분리한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 Notes형 command/직렬 autosave 구현.
/// </summary>
public sealed class NotesManagerViewModel : ObservableModel, IDisposable
{
    private readonly NotesRepository repository;
    private readonly List<MemoListItemViewModel> notes = [];
    private readonly List<NotesCommand> commands = [];
    private readonly DispatcherTimer autosave;
    private readonly Func<string, bool> confirm;
    private readonly LoginStartupService? loginStartup;
    private bool startWithWindows, changingStartup;
    private string startupStateMessage = "격리 검사 프로필 · 자동 실행 등록 변경 없음";
    private long revision;
    private long savedRevision;
    private long? firstDirty;
    private long lastEdit;
    private bool saving;
    private bool refreshing;
    private bool initialized;
    private bool paused;
    private bool pollingFiles;
    private long lastFilePoll = Stopwatch.GetTimestamp();
    private MemoListItemViewModel? selected;
    private string searchText = "", selectedView = "all", sortMode = "modified", folderName = "", tagInput = "";
    private string saveMessage = "불러오는 중", statusMessage = "";
    private FolderItem? selectedFolder;
    private string? selectedTag;
    private bool markdownPreview, globalHidden;
    private FlowDocument markdownDocument = SafeMarkdown.Render("");
    public ObservableCollection<FolderItem> Folders { get; } = [];
    public ObservableCollection<string> AllTags { get; } = [];
    public ObservableCollection<MemoListItemViewModel> VisibleMemos { get; } = [];
    public IReadOnlyList<MemoListItemViewModel> AllMemos => notes;
    public event Action? WidgetStateChanged;
    public event Action? ExitRequested;
    public event Action<long, long, double>? CommitCompleted;
    public bool IsInitialized => initialized;
    public Func<bool>? CurrentEditorHasUncommittedInput { get; set; }
    public Func<bool>? AnyUncommittedInputExists { get; set; }
    public Func<string, bool>? NoteHasUncommittedInput { get; set; }
    public bool HasUnsavedChanges => revision != savedRevision || AnyUncommittedInputExists?.Invoke() == true;
    public bool HasSelection => selected != null;
    public bool IsTrashView => selectedView == "trash";
    public string SaveStateMessage { get => saveMessage; private set => Assign(ref saveMessage, value); }
    public string StatusMessage { get => statusMessage; set => Assign(ref statusMessage, value); }
    public bool CanConfigureStartup => loginStartup != null && initialized && !changingStartup;
    public string StartupStateMessage { get => startupStateMessage; private set => Assign(ref startupStateMessage, value); }
    public bool StartWithWindows { get => startWithWindows; set { if (value != startWithWindows && CanConfigureStartup) Run(() => ChangeStartupAsync(value)); } }
    public string FolderNameInput { get => folderName; set => Assign(ref folderName, value); }
    public string TagInput { get => tagInput; set => Assign(ref tagInput, value); }
    public string SearchText { get => searchText; set { if (Assign(ref searchText, value ?? "")) RefreshList(); } }
    public string SortMode { get => sortMode; set { if (Assign(ref sortMode, value ?? "modified")) RefreshList(); } }
    public string SelectedView { get => selectedView; set { if (Assign(ref selectedView, value ?? "all")) { Notify(nameof(IsTrashView)); RefreshList(); } } }
    public FolderItem? SelectedFolder { get => selectedFolder; set { if (Assign(ref selectedFolder, value)) RefreshCommands(); } }
    public string? SelectedTag { get => selectedTag; set => Assign(ref selectedTag, value); }
    public bool GlobalHidden { get => globalHidden; set { if (Assign(ref globalHidden, value)) { foreach (MemoListItemViewModel item in notes) item.SetGlobalHidden(value); Dirty(); WidgetStateChanged?.Invoke(); } } }
    public bool IsMarkdownPreview { get => markdownPreview; set { if (Assign(ref markdownPreview, value)) UpdatePreview(); } }
    public FlowDocument MarkdownDocument { get => markdownDocument; private set => Assign(ref markdownDocument, value); }
    public MemoListItemViewModel? SelectedMemo
    {
        get => selected;
        set
        {
            if (refreshing) return;
            if (!ReferenceEquals(selected, value) && CurrentEditorHasUncommittedInput?.Invoke() == true)
            { PendingInputMessage(); Notify(nameof(SelectedMemo)); return; }
            if (Assign(ref selected, value)) { Notify(nameof(HasSelection)); UpdatePreview(); RefreshCommands(); }
        }
    }
    public NotesCommand NewMemoCommand { get; }
    public NotesCommand SelectViewCommand { get; }
    public NotesCommand SelectFolderCommand { get; }
    public NotesCommand SelectTagCommand { get; }
    public NotesCommand SelectMemoCommand { get; }
    public NotesCommand TogglePinnedCommand { get; }
    public NotesCommand SetPriorityCommand { get; }
    public NotesCommand ShowMemoCommand { get; }
    public NotesCommand HideMemoCommand { get; }
    public NotesCommand MoveToTrashCommand { get; }
    public NotesCommand RestoreMemoCommand { get; }
    public NotesCommand PermanentlyDeleteCommand { get; }
    public NotesCommand AddFolderCommand { get; }
    public NotesCommand RenameFolderCommand { get; }
    public NotesCommand DeleteFolderCommand { get; }
    public NotesCommand AddTagCommand { get; }
    public NotesCommand RemoveTagCommand { get; }
    public NotesCommand ToggleMarkdownPreviewCommand { get; }
    public NotesCommand SaveCommand { get; }
    public NotesCommand RetrySaveCommand { get; }
    public NotesCommand ExportCommand { get; }
    public NotesCommand ImportCommand { get; }
    public NotesCommand RefreshStartupCommand { get; }
    public NotesCommand SetNoteColorCommand { get; }
    public NotesCommand SetNoteFontSizeCommand { get; }
    public NotesCommand TogglePlacementLockCommand { get; }
    public NotesCommand ResetPlacementCommand { get; }
    public NotesCommand ResetAllPlacementsCommand { get; }
    public NotesCommand ExitApplicationCommand { get; }

    /// <summary>name: NotesManagerViewModel; desc: UI command와 테스트 가능한 확인 callback을 연결한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 관리 동작.</summary>
    public NotesManagerViewModel(NotesRepository store, Func<string, bool>? confirmation = null, LoginStartupService? startup = null)
    {
        repository = store;
        repository.ReplacementBlocked = () => AnyUncommittedInputExists?.Invoke() == true;
        repository.DeletionBlocked = id => NoteHasUncommittedInput?.Invoke(id) == true;
        loginStartup = startup;
        confirm = confirmation ?? (message => MessageBox.Show(message, "바탕화면 메모", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        NewMemoCommand = Command(_ => NewMemo());
        SelectViewCommand = Command(p => SelectedView = p as string ?? "all");
        SelectFolderCommand = Command(p => { if (p is FolderItem folder) { SelectedFolder = folder; SelectedView = "folder"; RefreshList(); } });
        SelectTagCommand = Command(p => { SelectedTag = p as string; SelectedView = "tag"; RefreshList(); });
        SelectMemoCommand = Command(p => SelectedMemo = p as MemoListItemViewModel);
        TogglePinnedCommand = Command(_ => selected!.IsPinned = !selected.IsPinned, _ => Active());
        SetPriorityCommand = Command(p => { if (int.TryParse(p?.ToString(), out int priority)) selected!.Priority = priority; }, _ => Active());
        ShowMemoCommand = Command(_ => selected!.IsVisible = true, _ => Active());
        HideMemoCommand = Command(_ => selected!.IsVisible = false, _ => Active());
        MoveToTrashCommand = Command(_ => Trash(), _ => Active());
        RestoreMemoCommand = Command(_ => { MemoListItemViewModel item = selected!; item.IsVisible = false; item.IsDeleted = false; }, _ => selected?.IsDeleted == true);
        PermanentlyDeleteCommand = Command(_ => DeletePermanently(), _ => selected?.IsDeleted == true);
        AddFolderCommand = Command(_ => AddFolder());
        RenameFolderCommand = Command(_ => RenameFolder(), _ => selectedFolder != null);
        DeleteFolderCommand = Command(_ => DeleteFolder(), _ => selectedFolder != null);
        AddTagCommand = Command(_ => { selected!.TagsText = string.Join(",", selected.Snapshot().Tags.Append(TagInput)); TagInput = ""; }, _ => Active());
        RemoveTagCommand = Command(p => selected!.TagsText = string.Join(",", selected.Snapshot().Tags.Where(t => !MemoListItemViewModel.ParseTags(p as string).Contains(t, StringComparer.OrdinalIgnoreCase))), _ => Active());
        ToggleMarkdownPreviewCommand = Command(_ => IsMarkdownPreview = !IsMarkdownPreview, _ => selected != null);
        SaveCommand = Command(_ => Run(FlushAsync));
        RetrySaveCommand = Command(_ => Run(FlushAsync));
        ExportCommand = Command(_ => Run(ExportDialogAsync), _ => initialized);
        ImportCommand = Command(_ => Run(ImportDialogAsync), _ => initialized);
        RefreshStartupCommand = Command(_ => RefreshStartup(), _ => loginStartup != null);
        SetNoteColorCommand = Command(p => { if (p is string color && NoteAppearance.Colors.Contains(color)) selected!.ColorPreset = color; }, _ => Active());
        SetNoteFontSizeCommand = Command(p => { if (double.TryParse(p?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double size) && NoteAppearance.FontSizes.Contains(size)) selected!.MemoFontSize = size; }, _ => Active());
        TogglePlacementLockCommand = Command(_ => selected!.IsLocked = !selected.IsLocked, _ => Active());
        ResetPlacementCommand = Command(_ => selected!.ResetPlacement(), _ => Active());
        ResetAllPlacementsCommand = Command(_ => { foreach (MemoListItemViewModel item in notes.Where(n => !n.IsDeleted)) item.ResetPlacement(); StatusMessage = "메모 배치를 현재 화면 안으로 초기화했습니다."; });
        ExitApplicationCommand = Command(_ => ExitRequested?.Invoke());
        autosave = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnAutosave, Dispatcher.CurrentDispatcher);
    }

    /// <summary>name: InitializeAsync; desc: 실패 시 편집/빈 저장을 허용하지 않고 읽기 원본을 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 읽기 진입.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            Apply(await repository.LoadAsync());
            initialized = true;
            Notify(nameof(IsInitialized));
            SaveStateMessage = "파일 저장 완료";
            StatusMessage = repository.BackupWarning.Length > 0 ? repository.BackupWarning : "메모는 이 PC에 저장됩니다.";
            RefreshCommands();
            RefreshStartup(); Notify(nameof(CanConfigureStartup));
        }
        catch (Exception ex) { SaveStateMessage = "불러오기 실패 · 원본 보존"; StatusMessage = ErrorDescription(ex); throw; }
    }

    /// <summary>name: Snapshot; desc: UI 스레드의 현재 편집을 불변 snapshot으로 고정한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 저장 단위.</summary>
    public NotesSnapshot Snapshot() => new(1, revision, notes.Select(n => n.Snapshot()).ToArray(), Folders.Select(f => new FolderRecord(f.Id, f.Name)).ToArray(), globalHidden, startWithWindows);

    /// <summary>name: FlushAsync; desc: 새 편집을 포함할 때까지 커밋하며 실패 입력을 메모리에 유지한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 실패/경쟁 처리.</summary>
    public async Task<bool> FlushAsync()
    {
        if (!initialized) return false;
        if (saving) { while (saving) await Task.Delay(20); if (!HasUnsavedChanges) return true; }
        saving = true;
        try
        {
            while (savedRevision < revision)
            {
                NotesSnapshot snapshot = Snapshot();
                long began = Stopwatch.GetTimestamp();
                SaveStateMessage = "파일 저장 중";
                long committed = await repository.SaveAsync(snapshot);
                savedRevision = Math.Max(savedRevision, committed);
                CommitCompleted?.Invoke(snapshot.Revision, committed, Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            }
            firstDirty = null;
            if (AnyUncommittedInputExists?.Invoke() == true) { PendingInputMessage(); Notify(nameof(HasUnsavedChanges)); return false; }
            SaveStateMessage = "파일 저장 완료";
            Notify(nameof(HasUnsavedChanges));
            return true;
        }
        catch (Exception ex)
        {
            SaveStateMessage = "저장 실패 · 입력은 유지됩니다";
            StatusMessage = ErrorDescription(ex) + " · 재시도 또는 내보내기를 사용하세요.";
            return false;
        }
        finally { saving = false; }
    }

    /// <summary>name: RestoreFromAsync; desc: 확인 후 현재 메모리 저장을 먼저 완료하고 검증된 백업을 복원한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 복원 경쟁 방지.</summary>
    public async Task<bool> RestoreFromAsync(string path)
    {
        if (AnyUncommittedInputExists?.Invoke() == true) { PendingInputMessage(); return false; }
        NotesBackupData imported = await NotesRepository.ReadBackupAsync(path);
        if (!confirm("백업 내용으로 현재 메모를 교체할까요? 현재 저장본은 먼저 백업합니다.")) return false;
        if (AnyUncommittedInputExists?.Invoke() == true) { PendingInputMessage(); return false; }
        paused = true;
        foreach (MemoListItemViewModel item in notes) item.SuspendEditing(true);
        WidgetStateChanged?.Invoke(); RefreshCommands();
        try
        {
            if (!await FlushAsync()) return false;
            Apply(await repository.RestoreAsync(imported));
            RefreshStartup();
            StatusMessage = "복원 완료 · 기존 저장본 백업 보관";
            WidgetStateChanged?.Invoke();
            return true;
        }
        finally { paused = false; foreach (MemoListItemViewModel item in notes) item.SuspendEditing(false); WidgetStateChanged?.Invoke(); RefreshCommands(); }
    }

    /// <summary>name: Apply; desc: 검증한 snapshot만 UI에 교체한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 복원/시작.</summary>
    private void Apply(NotesSnapshot snapshot)
    {
        snapshot.Validate();
        foreach (MemoListItemViewModel item in notes) item.Edited -= OnEdited;
        notes.Clear(); Folders.Clear();
        foreach (FolderRecord folder in snapshot.Folders) Folders.Add(new(folder.Id, folder.Name));
        foreach (NoteRecord note in snapshot.Notes) { MemoListItemViewModel item = new(note, repository.GetSession(note)); item.Edited += OnEdited; notes.Add(item); }
        revision = savedRevision = snapshot.Revision;
        globalHidden = snapshot.GlobalHidden;
        startWithWindows = snapshot.StartWithWindows; Notify(nameof(StartWithWindows));
        foreach (MemoListItemViewModel item in notes) item.SetGlobalHidden(globalHidden);
        firstDirty = null;
        SelectedMemo = null;
        Notify(nameof(GlobalHidden)); Notify(nameof(AllMemos)); Notify(nameof(HasUnsavedChanges));
        RefreshList();
    }
    /// <summary>name: RefreshStartup; desc: 시작·복원 시 사용자 등록을 읽기만 하고 외부 해제를 강제로 복구하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 자동 실행 상태.</summary>
    private void RefreshStartup()
    {
        if (loginStartup == null) { startWithWindows = false; Notify(nameof(StartWithWindows)); return; }
        try { LoginStartupState state = loginStartup.Inspect(); startWithWindows = state.Registered; StartupStateMessage = state.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { StartupStateMessage = "자동 실행 상태를 확인하지 못했습니다. 수동 실행을 이용하세요."; }
        Notify(nameof(StartWithWindows));
    }
    /// <summary>name: ChangeStartupAsync; desc: 명시 켜기/끄기 후 등록 상태를 읽고 설정 저장 실패와 구분한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 실패 상태 보존.</summary>
    private async Task<bool> ChangeStartupAsync(bool enabled)
    {
        changingStartup = true; Notify(nameof(CanConfigureStartup));
        try
        {
            LoginStartupState state = loginStartup!.SetEnabled(enabled);
            startWithWindows = state.Registered; Notify(nameof(StartWithWindows)); StartupStateMessage = state.Message;
            Dirty();
            if (!await FlushAsync()) { StartupStateMessage = "Windows 등록은 변경됐지만 앱 설정 저장에 실패했습니다. 저장 재시도를 누르세요."; return false; }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { RefreshStartup(); StartupStateMessage = "자동 실행 변경 실패 · 성공으로 처리하지 않았습니다. 수동 실행을 이용하세요."; Notify(nameof(StartWithWindows)); return false; }
        finally { changingStartup = false; Notify(nameof(CanConfigureStartup)); }
    }

    /// <summary>name: NewMemo; desc: 명시적 생성만 새 ID/초기 표시 의도를 만든다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 CRUD.</summary>
    private void NewMemo()
    {
        if (CurrentEditorHasUncommittedInput?.Invoke() == true) { PendingInputMessage(); return; }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NoteRecord record = new(Guid.NewGuid().ToString(), "새 메모", "", now, now, selectedView == "folder" ? selectedFolder?.Id : null, [], false, 0, false, false, 100 + notes.Count % 8 * 25, 100 + notes.Count % 8 * 25, AutoTitle: true);
        MemoListItemViewModel item = new(record, repository.GetSession(record));
        item.SetGlobalHidden(globalHidden); item.Edited += OnEdited; notes.Add(item);
        searchText = ""; selectedView = "all"; Notify(nameof(SearchText)); Notify(nameof(SelectedView)); Notify(nameof(IsTrashView));
        Dirty(); RefreshList(); SelectedMemo = item; WidgetStateChanged?.Invoke();
    }

    /// <summary>name: Trash; desc: 확인 후 같은 ID의 본문/분류를 보존하며 위젯을 숨긴다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 휴지통.</summary>
    private void Trash()
    {
        MemoListItemViewModel? item = selected;
        if (item != null && NoteHasUncommittedInput?.Invoke(item.Id) == true) { PendingInputMessage(); return; }
        if (item == null || !confirm("메모를 휴지통으로 이동할까요? 나중에 복구할 수 있습니다.")) return;
        item.IsVisible = false; item.IsDeleted = true;
    }
    /// <summary>name: DeletePermanently; desc: 명시 확인된 휴지통 항목만 영구 삭제한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 삭제 보호.</summary>
    private void DeletePermanently()
    {
        MemoListItemViewModel? item = selected;
        if (item != null && NoteHasUncommittedInput?.Invoke(item.Id) == true) { PendingInputMessage(); return; }
        if (item == null || !item.IsDeleted || !confirm("영구 삭제하면 복구할 수 없습니다. 삭제할까요?")) return;
        item.Edited -= OnEdited; notes.Remove(item); Dirty(); RefreshList(); WidgetStateChanged?.Invoke();
    }
    /// <summary>name: AddFolder; desc: 중복 없는 평면 폴더를 만든다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 폴더 생성.</summary>
    private void AddFolder()
    {
        string name = FolderNameInput.Trim();
        if (!ValidFolderName(name)) return;
        FolderItem folder = new(Guid.NewGuid().ToString(), name); Folders.Add(folder); SelectedFolder = folder; FolderNameInput = ""; Dirty(); RefreshCommands();
    }
    /// <summary>name: RenameFolder; desc: ID를 보존해 폴더 이름을 변경한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 이름 변경.</summary>
    private void RenameFolder()
    { string name = FolderNameInput.Trim(); if (selectedFolder == null || !ValidFolderName(name, selectedFolder.Id)) return; selectedFolder.Name = name; FolderNameInput = ""; Dirty(); }
    /// <summary>name: DeleteFolder; desc: 메모를 삭제하지 않고 미분류로 옮긴다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 분류 보존.</summary>
    private void DeleteFolder()
    {
        FolderItem? folder = selectedFolder;
        if (folder == null || !confirm("폴더를 삭제할까요? 메모는 전체 메모에 유지됩니다.")) return;
        foreach (MemoListItemViewModel item in notes.Where(n => n.FolderId == folder.Id).ToArray()) item.FolderId = null;
        Folders.Remove(folder); SelectedFolder = null; SelectedView = "all"; Dirty(); RefreshCommands();
    }
    /// <summary>name: ValidFolderName; desc: 저장 계약 범위와 중복을 검사한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 폴더 검증.</summary>
    private bool ValidFolderName(string name, string? exclude = null)
    { if (name.Length is > 0 and <= 100 && !Folders.Any(f => f.Id != exclude && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) return true; StatusMessage = "폴더 이름은 중복 없이 1~100자로 입력하세요."; return false; }

    /// <summary>name: OnEdited; desc: 원문 변경을 저장 예약하고 검색/미리보기/위젯을 같은 모델에 연결한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 편집 파이프라인.</summary>
    private void OnEdited(MemoListItemViewModel item)
    { Dirty(); RefreshList(); if (ReferenceEquals(item, selected)) UpdatePreview(); RefreshCommands(); WidgetStateChanged?.Invoke(); }
    /// <summary>name: Dirty; desc: 실제 편집만 revision을 올리고 저장 시간을 기록한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 저장 계측.</summary>
    private void Dirty()
    { if (!initialized) return; revision++; lastEdit = Stopwatch.GetTimestamp(); firstDirty ??= lastEdit; SaveStateMessage = "저장 대기"; Notify(nameof(HasUnsavedChanges)); }

    /// <summary>name: RefreshList; desc: 현재 원본에 즉시 필터·안정 정렬한다. 검색은 배치/가시성을 바꾸지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 오래된 검색 응답 없음.</summary>
    private void RefreshList()
    {
        if (refreshing) return;
        IEnumerable<MemoListItemViewModel> query = notes.Where(n => n.IsDeleted == IsTrashView);
        if (selectedView == "pinned") query = query.Where(n => n.IsPinned);
        if (selectedView == "folder") query = query.Where(n => n.FolderId == selectedFolder?.Id);
        if (selectedView == "tag") query = query.Where(n => n.Snapshot().Tags.Contains(selectedTag ?? "", StringComparer.OrdinalIgnoreCase));
        string term = searchText.Trim();
        if (term.Length > 0) query = query.Where(n => n.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || n.Body.Contains(term, StringComparison.OrdinalIgnoreCase));
        IOrderedEnumerable<MemoListItemViewModel> ordered = query.OrderByDescending(n => n.IsPinned);
        ordered = sortMode switch { "title" => ordered.ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase), "priority" => ordered.ThenByDescending(n => n.Priority).ThenByDescending(n => n.UpdatedAt), _ => ordered.ThenByDescending(n => n.UpdatedAt) };
        MemoListItemViewModel[] results = ordered.ThenBy(n => n.Id, StringComparer.Ordinal).ToArray();
        refreshing = true;
        try
        {
            // 불변 항목을 재추가해 타이핑 중 선택/커서가 초기화되는 일을 피한다.
            for (int i = VisibleMemos.Count - 1; i >= 0; i--) if (!results.Contains(VisibleMemos[i])) VisibleMemos.RemoveAt(i);
            for (int i = 0; i < results.Length; i++)
            { int old = VisibleMemos.IndexOf(results[i]); if (old < 0) VisibleMemos.Insert(i, results[i]); else if (old != i) VisibleMemos.Move(old, i); }
            string[] tags = notes.Where(n => !n.IsDeleted).SelectMany(n => n.Snapshot().Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (!AllTags.SequenceEqual(tags)) { AllTags.Clear(); foreach (string tag in tags) AllTags.Add(tag); }
        }
        finally { refreshing = false; }
        if (selected != null && !results.Contains(selected)) SelectedMemo = null;
        RefreshCommands();
    }
    /// <summary>name: UpdatePreview; desc: 현재 선택 원문만 렌더하며 본문을 역으로 수정하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 Markdown 경쟁 제거.</summary>
    private void UpdatePreview() { if (markdownPreview) MarkdownDocument = SafeMarkdown.Render(selected?.Body ?? ""); }
    /// <summary>name: OnAutosave; desc: 중단 600ms/연속4.5초 후 커밋을 시작하며 deadline 통과는 별도 계측한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 자동 저장.</summary>
    private void OnAutosave(object? sender, EventArgs e)
    {
        if (initialized && !paused && !pollingFiles && Stopwatch.GetElapsedTime(lastFilePoll).TotalMilliseconds >= 1000)
        { lastFilePoll = Stopwatch.GetTimestamp(); _ = RefreshFilesAsync(); }
        if (paused || saving || !HasUnsavedChanges || firstDirty == null) return;
        if (Stopwatch.GetElapsedTime(lastEdit).TotalMilliseconds >= 600 || Stopwatch.GetElapsedTime(firstDirty.Value).TotalMilliseconds >= 4500) Run(FlushAsync);
    }
    /// <summary>name: RefreshFilesAsync; desc: 동일 세션에 안정 외부 변경을 전달하며 자동 저장/감지 루프를 만들지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 FILE-01.</summary>
    private async Task RefreshFilesAsync()
    {
        pollingFiles = true;
        try
        {
            await repository.RefreshDocumentsAsync();
            if (repository.Sessions.Any(s => s.HasConflict)) { SaveStateMessage = "외부 수정 충돌 · 양쪽 보존"; StatusMessage = "해당 메모에서 외부 변경을 확인하세요."; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StatusMessage = "보관 폴더 접근 실패 · 현재 입력은 유지됩니다."; }
        finally { pollingFiles = false; }
    }
    /// <summary>name: Command; desc: 초기화 이후 사용 가능한 명령을 등록한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 명령 생명주기.</summary>
    private NotesCommand Command(Action<object?> execute, Func<object?, bool>? allowed = null)
    { NotesCommand command = new(execute, p => initialized && !paused && (allowed?.Invoke(p) ?? true)); commands.Add(command); return command; }
    /// <summary>name: Active; desc: 휴지통 이외 선택 여부. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 선택 보호.</summary>
    private bool Active() => selected is { IsDeleted: false };
    /// <summary>name: PendingInputMessage; desc: canonical 저장 성공을 보류 UI 입력의 전체 저장 완료로 보고하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC-10 lifecycle.</summary>
    internal void PendingInputMessage()
    { SaveStateMessage = "보류 입력 확인 필요"; StatusMessage = "보류 편집본을 다른 파일로 저장하거나 조합을 마친 뒤 다시 시도하세요."; Notify(nameof(HasUnsavedChanges)); }
    /// <summary>name: RefreshCommands; desc: 상태 변경 후 명령 갱신. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 명령 알림.</summary>
    private void RefreshCommands() { foreach (NotesCommand command in commands) command.Refresh(); }
    /// <summary>name: Run; desc: async command 예외를 UI 상태에 보고하며 원문은 유지한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 비동기 오류.</summary>
    private async void Run(Func<Task<bool>> action) { try { await action(); } catch (Exception ex) { StatusMessage = ErrorDescription(ex); } }
    /// <summary>name: ErrorDescription; desc: 본문/SQL 매개변수 없이 오류 종류만 보고한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 개인정보 제외.</summary>
    private static string ErrorDescription(Exception error) => error switch { UnauthorizedAccessException => "파일 접근 권한을 확인하세요", IOException => "파일 읽기/쓰기 실패", System.Text.Json.JsonException or InvalidDataException => "백업/저장 형식이 올바르지 않습니다", Microsoft.Data.Sqlite.SqliteException => "DB 읽기/쓰기 실패 · 원본 보존", _ => "처리 실패 · " + error.GetType().Name };
    /// <summary>name: ExportDialogAsync; desc: 사용자가 선택한 JSON에 현재 원문을 내보낸다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 백업 UI.</summary>
    private async Task<bool> ExportDialogAsync()
    { SaveFileDialog dialog = new() { Filter = "Markdown 메모 묶음 (*.notedex.zip)|*.notedex.zip|이전 호환 JSON (*.json)|*.json", FileName = "notes-backup.notedex.zip", DefaultExt = ".notedex.zip" }; if (dialog.ShowDialog() != true) return false; if (!await FlushAsync()) return false; await repository.ExportAsync(dialog.FileName, Snapshot()); StatusMessage = "백업 완료 · 메모 원본과 설정을 포함합니다."; return true; }
    /// <summary>name: ImportDialogAsync; desc: 선택한 JSON을 검증/확인하고 복원한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 복원 UI.</summary>
    private async Task<bool> ImportDialogAsync()
    { OpenFileDialog dialog = new() { Filter = "메모 백업 (*.notedex.zip;*.json)|*.notedex.zip;*.json" }; return dialog.ShowDialog() == true && await RestoreFromAsync(dialog.FileName); }
    /// <summary>name: Dispose; desc: UI 타이머와 구독을 해제한다. 종료 전 FlushAsync는 호출자가 수행한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 종료 처리.</summary>
    public void Dispose() { autosave.Stop(); foreach (MemoListItemViewModel item in notes) item.Edited -= OnEdited; }
}
