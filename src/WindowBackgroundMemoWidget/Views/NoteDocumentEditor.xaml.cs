using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowBackgroundMemoWidget.Services;

namespace WindowBackgroundMemoWidget.Views;

/// <summary>
/// name         : NoteDocumentEditor
/// desc         : manager/widget 공용 MarkdownDocumentSession의 projection과 원문을 표시하는 Notes형 편집기.
/// author       : NotedexOrig3
/// create date  : 2026-10-24
/// update date  : 2026-10-24 NotedexOrig3 : P0 색상/글자 크기와 제목·Tab 들여쓰기 도구 추가.
///                2026-10-24 NotedexOrig3 : Undo 소유권과 세션 구독 수명을 공유 문서 세션에 맞춤.
/// </summary>
/// ---------------------------------------------------------------------
public partial class NoteDocumentEditor : UserControl
{
    internal const double ListIndentColumnWidth = 8;
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(MarkdownDocumentSession), typeof(NoteDocumentEditor),
        new PropertyMetadata(null, OnSessionChanged));
    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(NoteDocumentEditor), new PropertyMetadata(true));
    public static readonly DependencyProperty AllowMarkdownModeProperty = DependencyProperty.Register(
        nameof(AllowMarkdownMode), typeof(bool), typeof(NoteDocumentEditor), new PropertyMetadata(true, OnAllowMarkdownModeChanged));
    public static readonly DependencyProperty MemoBackgroundProperty = DependencyProperty.Register(
        nameof(MemoBackground), typeof(Brush), typeof(NoteDocumentEditor), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x36))));
    public static readonly DependencyProperty MemoFontSizeProperty = DependencyProperty.Register(
        nameof(MemoFontSize), typeof(double), typeof(NoteDocumentEditor), new PropertyMetadata(16d, OnMemoFontSizeChanged));
    public static readonly DependencyProperty HeadingFontSizeProperty = DependencyProperty.Register(
        nameof(HeadingFontSize), typeof(double), typeof(NoteDocumentEditor), new PropertyMetadata(20d));
    public static readonly DependencyProperty CompactToolbarProperty = DependencyProperty.Register(
        nameof(CompactToolbar), typeof(bool), typeof(NoteDocumentEditor), new PropertyMetadata(false));

    private bool updatingMode;
    private bool updatingSource;
    private bool isComposing;
    private bool applyingSessionEdit;
    private bool isMarkdownMode;
    private bool isViewLoaded;
    private MarkdownDocumentSession? subscribedSession;
    private string? pendingVisualSource;
    private MarkdownVisualBlock? activeVisualBlock;
    private int activeSelectionStart;
    private int activeSelectionLength;
    private int activeSourceSelectionStart;
    private int activeSourceSelectionLength;
    private int? pendingCaretOffset;
    private int pendingSelectionLength;
    private int? pendingCaretSourceStart;

    public MarkdownDocumentSession? Session
    {
        get => (MarkdownDocumentSession?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    /// <summary>True while the editor owns display input that has not reached the shared document session.</summary>
    public bool HasUncommittedInput => pendingVisualSource is not null || isComposing;

    /// <summary>Save the pending visual draft when present, otherwise save the canonical session snapshot.</summary>
    internal async Task SaveCopyToAsync(string path)
    {
        if (Session is null) throw new InvalidOperationException("The editor has no document session.");
        if (pendingVisualSource is not null) await Session.SaveCopyAsync(path, pendingVisualSource);
        else await Session.SaveCopyAsync(path);
        pendingVisualSource = null;
        if (!IsLoaded)
        {
            isViewLoaded = false;
            UpdateSessionSubscription();
        }
        UpdateConflictActions();
    }

    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public bool AllowMarkdownMode
    {
        get => (bool)GetValue(AllowMarkdownModeProperty);
        set => SetValue(AllowMarkdownModeProperty, value);
    }

    public Brush MemoBackground
    {
        get => (Brush)GetValue(MemoBackgroundProperty);
        set => SetValue(MemoBackgroundProperty, value);
    }

    public double MemoFontSize
    {
        get => (double)GetValue(MemoFontSizeProperty);
        set => SetValue(MemoFontSizeProperty, value);
    }

    public double HeadingFontSize
    {
        get => (double)GetValue(HeadingFontSizeProperty);
        private set => SetValue(HeadingFontSizeProperty, value);
    }

    public bool CompactToolbar
    {
        get => (bool)GetValue(CompactToolbarProperty);
        set => SetValue(CompactToolbarProperty, value);
    }

    private static void OnMemoFontSizeChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        NoteDocumentEditor editor = (NoteDocumentEditor)owner;
        editor.HeadingFontSize = Math.Round((double)e.NewValue * 1.25, MidpointRounding.AwayFromZero);
        if (editor.IsInitialized)
            foreach (RichTextBox block in FindVisualRichTextBoxes(editor.VisualScroll)) block.Document.FontSize = block.FontSize;
    }

    public NoteDocumentEditor()
    {
        InitializeComponent();
        Loaded += OnEditorLoaded;
        Unloaded += OnEditorUnloaded;
        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnCompositionStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, OnCompositionUpdate);
        TextCompositionManager.AddPreviewTextInputHandler(this, OnCompositionCommit);
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnSessionChanged
    /// desc         : 같은 NoteId 세션을 manager와 widget에 연결하고 세션 상태 알림을 구독한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : UI 임시 인터페이스 제거, 개발 세션 API 사용.
    ///                2026-10-24 NotedexOrig3 : 화면 Loaded 동안에만 세션 변경 이벤트 구독.
    /// </summary>
    /// ---------------------------------------------------------------------
    private static void OnSessionChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        NoteDocumentEditor editor = (NoteDocumentEditor)owner;
        editor.UpdateSessionSubscription();
        editor.DataContext = args.NewValue;
        editor.UpdateMode();
        editor.UpdateEmptyState();
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnEditorLoaded
    /// desc         : 실제 표시 중인 편집기에서만 공유 세션의 상태 변경을 관찰한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 세션 PropertyChanged 수명 구간을 화면 표시 수명에 맞춤.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        isViewLoaded = true;
        UpdateSessionSubscription();
        UpdateMode();
        UpdateEmptyState();
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnEditorUnloaded
    /// desc         : 위젯 닫힘/탐색 전환에서 세션 참조를 끊어 화면이 장수 세션에 남지 않게 한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 Unloaded 수명 종료 시 PropertyChanged 구독 해제.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnEditorUnloaded(object sender, RoutedEventArgs e)
    {
        if (HasUncommittedInput) return;
        isViewLoaded = false;
        UpdateSessionSubscription();
    }

    /// <summary>
    /// name         : UpdateSessionSubscription
    /// desc         : 현재 DataContext 세션 하나만 로드된 View에 구독하고 나머지는 해제한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 재사용 가능한 편집기 세션 구독 정리.
    /// </summary>
    private void UpdateSessionSubscription()
    {
        MarkdownDocumentSession? target = isViewLoaded ? Session : null;
        if (ReferenceEquals(subscribedSession, target)) return;
        if (subscribedSession is not null) subscribedSession.PropertyChanged -= OnSessionPropertyChanged;
        subscribedSession = target;
        if (subscribedSession is not null) subscribedSession.PropertyChanged += OnSessionPropertyChanged;
    }

    private static void OnAllowMarkdownModeChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is NoteDocumentEditor editor && editor.IsInitialized)
            editor.SourceModeButton.Visibility = Visibility.Collapsed;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!isViewLoaded || sender is not MarkdownDocumentSession changedSession || !ReferenceEquals(Session, changedSession)) return;
        if (args.PropertyName is nameof(MarkdownDocumentSession.Source) or null or "")
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                if (!isViewLoaded || !ReferenceEquals(Session, changedSession)) return;
                SynchronizeSourceFromSession(); UpdateEmptyState(); RestoreVisualCaret();
            }));
        if (args.PropertyName is nameof(MarkdownDocumentSession.HasConflict) or nameof(MarkdownDocumentSession.State) or null or "")
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                if (isViewLoaded && ReferenceEquals(Session, changedSession)) UpdateConflictActions();
            }));
    }

    private void UpdateConflictActions()
    {
        if (!IsInitialized) return;
        bool conflicted = Session?.HasConflict == true;
        ConflictActions.Visibility = conflicted ? Visibility.Visible : Visibility.Collapsed;
        SaveCopyButton.Visibility = Session?.State is MarkdownDocumentState.Error or MarkdownDocumentState.Conflict or MarkdownDocumentState.Unavailable or MarkdownDocumentState.Missing
            || pendingVisualSource is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : UpdateMode
    /// desc         : 표시 모드만 전환하며 모드 변경 자체로 Source·revision·저장 상태를 수정하지 않는다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 시각 편집 기본·Markdown 원문 전환 구현.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void UpdateMode()
    {
        if (!IsInitialized) return;
        bool sourceMode = isMarkdownMode;
        updatingMode = true;
        try
        {
            VisualModeButton.IsChecked = !sourceMode;
            SourceModeButton.IsChecked = sourceMode;
            SourceModeButton.Visibility = Visibility.Collapsed;
            VisualScroll.Visibility = sourceMode ? Visibility.Collapsed : Visibility.Visible;
            MarkdownSourceEditor.Visibility = sourceMode ? Visibility.Visible : Visibility.Collapsed;
            SynchronizeSourceFromSession();
            UpdateConflictActions();
            UpdateEmptyState();
        }
        finally { updatingMode = false; }
    }

    private void OnVisualModeClick(object sender, RoutedEventArgs e)
    {
        if (updatingMode || Session is null) return;
        isMarkdownMode = false;
        // Session State is owned by Orig2; display mode remains local to this surface.
        VisualScroll.Visibility = Visibility.Visible;
        MarkdownSourceEditor.Visibility = Visibility.Collapsed;
        VisualModeButton.IsChecked = true;
        SourceModeButton.IsChecked = false;
        UpdateEmptyState();
    }

    private void OnSourceModeClick(object sender, RoutedEventArgs e)
    {
        if (updatingMode || Session is null || !AllowMarkdownMode) return;
        isMarkdownMode = true;
        VisualScroll.Visibility = Visibility.Collapsed;
        MarkdownSourceEditor.Visibility = Visibility.Visible;
        VisualModeButton.IsChecked = false;
        SourceModeButton.IsChecked = true;
        SynchronizeSourceFromSession();
        MarkdownSourceEditor.Focus();
    }

    public void FocusEditor()
    {
        if (isMarkdownMode) { MarkdownSourceEditor.Focus(); return; }
        if (Session?.Projection.Count == 0) { EmptyEditor.Focus(); return; }
        foreach (TextBox editor in FindVisualTextBoxes(VisualScroll))
        {
            if (editor.IsVisible && !editor.IsReadOnly && editor.IsEnabled) { editor.Focus(); return; }
        }
        RichTextBox? rich = FindVisualRichTextBoxes(VisualScroll).FirstOrDefault(box => box.IsEnabled);
        if (rich is not null) { rich.Focus(); return; }
        VisualScroll.Focus();
    }

    private static System.Collections.Generic.IEnumerable<TextBox> FindVisualTextBoxes(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBox textBox) yield return textBox;
            foreach (TextBox nested in FindVisualTextBoxes(child)) yield return nested;
        }
    }

    private static System.Collections.Generic.IEnumerable<RichTextBox> FindVisualRichTextBoxes(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is RichTextBox rich) yield return rich;
            foreach (RichTextBox nested in FindVisualRichTextBoxes(child)) yield return nested;
        }
    }

    private void OnVisualEditorGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    { if (sender is RichTextBox editor) CaptureVisualSelection(editor); }

    private void OnVisualEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    { if (sender is RichTextBox editor) CaptureVisualSelection(editor); }

    private void CaptureVisualSelection(RichTextBox editor)
    {
        if (editor.DataContext is not MarkdownVisualBlock block) return;
        activeVisualBlock = block;
        activeSelectionStart = DisplaySelectionStart(editor);
        activeSelectionLength = DisplaySelectionLength(editor);
    }

    private RichTextBox? FindActiveRichTextBox()
        => FindVisualRichTextBoxes(VisualScroll).FirstOrDefault(box => ReferenceEquals(box.DataContext, activeVisualBlock));

    private void RestoreVisualCaret()
    {
        if (pendingCaretOffset is not int caret || pendingCaretSourceStart is not int sourceStart) return;
        int selectionLength = pendingSelectionLength;
        pendingCaretOffset = null; pendingCaretSourceStart = null; pendingSelectionLength = 0;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            RichTextBox? editor = FindVisualRichTextBoxes(VisualScroll)
                .FirstOrDefault(box => box.DataContext is MarkdownVisualBlock block && block.SourceStart == sourceStart);
            if (editor?.DataContext is not MarkdownVisualBlock visualBlock) return;
            editor.Focus();
            TextPointer end = PositionAtDisplayOffset(editor, Math.Clamp(caret, 0, visualBlock.Text.Length));
            TextPointer start = PositionAtDisplayOffset(editor, Math.Clamp(caret - selectionLength, 0, visualBlock.Text.Length));
            if (selectionLength == 0) editor.CaretPosition = end;
            else editor.Selection.Select(start, end);
            CaptureVisualSelection(editor);
        }));
    }

    private static int DisplaySelectionStart(RichTextBox editor)
        => new TextRange(editor.Document.ContentStart, editor.Selection.Start).Text.TrimEnd('\r', '\n').Length;

    private static int DisplaySelectionLength(RichTextBox editor)
        => editor.Selection.Text.TrimEnd('\r', '\n').Length;

    internal static TextPointer PositionAtDisplayOffset(RichTextBox editor, int displayOffset)
    {
        int passed = 0;
        Run[] runs = editor.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().ToArray();
        for (int index = 0; index < runs.Length; index++)
        {
            Run run = runs[index];
            // Run 끝 경계는 WPF insertion position 정규화에서 다음 글자 뒤로 이동할 수 있어
            // 실제 표시 텍스트 길이를 확인하며 인접 insertion position으로 보정한다.
            if (displayOffset == passed + run.Text.Length && index + 1 < runs.Length)
                return AlignDisplayOffset(editor, displayOffset, runs[index + 1].ContentStart);
            if (displayOffset == passed + run.Text.Length) return AlignDisplayOffset(editor, displayOffset, run.ContentEnd);
            if (displayOffset <= passed + run.Text.Length)
                return AlignDisplayOffset(editor, displayOffset,
                    run.ContentStart.GetPositionAtOffset(displayOffset - passed, LogicalDirection.Forward) ?? run.ContentEnd);
            passed += run.Text.Length;
        }
        return AlignDisplayOffset(editor, displayOffset, editor.Document.ContentEnd);
    }

    private static TextPointer AlignDisplayOffset(RichTextBox editor, int displayOffset, TextPointer candidate)
    {
        TextPointer? pointer = candidate;
        for (int step = 0; pointer is not null && step < 16; step++)
        {
            int actual = new TextRange(editor.Document.ContentStart, pointer).Text.TrimEnd('\r', '\n').Length;
            if (actual == displayOffset) return pointer;
            pointer = actual > displayOffset
                ? pointer.GetNextInsertionPosition(LogicalDirection.Backward)
                : pointer.GetNextInsertionPosition(LogicalDirection.Forward);
        }
        return candidate;
    }

    private void UpdateEmptyState()
    {
        if (!IsInitialized || Session is null) return;
        bool empty = !isMarkdownMode && Session.Projection.Count == 0;
        EmptyEditor.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = empty && string.IsNullOrEmpty(EmptyEditor.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!empty && EmptyEditor.Text.Length > 0 && Session.Projection.Count > 0)
        {
            updatingSource = true;
            try { EmptyEditor.Clear(); EmptyEditor.Tag = Session.Revision; }
            finally { updatingSource = false; }
        }
    }

    private void OnEmptyEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox editor) editor.Tag = Session?.Revision ?? 0;
    }

    private void OnEmptyTextChanged(object sender, TextChangedEventArgs e)
    {
        if (isComposing || sender is not TextBox editor) return;
        ApplyEmptyEdit(editor);
    }

    private void ApplyEmptyEdit(TextBox editor)
    {
        if (Session is null || !IsEditable || updatingSource || applyingSessionEdit) return;
        EmptyHint.Visibility = string.IsNullOrEmpty(editor.Text) ? Visibility.Visible : Visibility.Collapsed;
        long revision = editor.Tag is long value ? value : Session.Revision;
        if (Session.TryReplace(revision, editor.Text))
        {
            editor.Tag = Session.Revision;
            EditStatus.Visibility = Visibility.Collapsed;
        }
        else
        {
            EditStatus.Text = Session.StatusMessage;
            EditStatus.Visibility = Visibility.Visible;
        }
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnBlockTextChanged
    /// desc         : 시각 block의 최소 변경 구간만 현재 revision의 TryEdit에 전달한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 전체 본문 교체를 피하는 projection 범위 편집.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnBlockTextChanged(object sender, TextChangedEventArgs e)
    {
        if (isComposing || sender is not RichTextBox box || applyingSessionEdit) return;
        ApplyVisualEdit(box);
    }

    private void ApplyVisualEdit(RichTextBox box)
    {
        if (applyingSessionEdit || box.DataContext is not MarkdownVisualBlock block || Session is null || !IsEditable) return;
        string current = ReadRichText(box);
        if (current.Length > 10000)
        {
            PreservePendingVisualEdit(block, box.Tag is BlockEditBaseline overflowBaseline ? overflowBaseline : new(block.Text, Session.Revision, Session.Source), current);
            EditStatus.Text = "시각 편집 블록은 10,000자를 넘었습니다. 입력을 다른 Markdown 파일로 저장한 뒤 계속하세요.";
            EditStatus.Visibility = Visibility.Visible;
            isMarkdownMode = true;
            UpdateMode();
            ShowPendingVisualSource();
            return;
        }
        BlockEditBaseline baseline = box.Tag is BlockEditBaseline prior ? prior : new(block.Text, Session.Revision, Session.Source);
        string previous = baseline.Text;
        if (TryFindTextDelta(previous, current, out int offset, out int removed, out string inserted))
        {
            pendingCaretOffset = offset + inserted.Length;
            pendingCaretSourceStart = block.SourceStart;
            pendingSelectionLength = 0;
            applyingSessionEdit = true;
            try
            {
                bool applied = Session.TryEditVisual(baseline.Revision, block, offset, removed, inserted);
                if (applied) box.Tag = new BlockEditBaseline(current, Session.Revision, Session.Source);
                else
                {
                    pendingCaretOffset = null;
                    pendingCaretSourceStart = null;
                    pendingSelectionLength = 0;
                    PreservePendingVisualEdit(block, baseline, current);
                    EditStatus.Text = "이 위치는 Markdown 서식 경계를 포함합니다. 원문 모드로 전환했습니다. 현재 표시 입력은 ‘다른 파일로 저장’으로 보존할 수 있습니다.";
                    EditStatus.Visibility = Visibility.Visible;
                    isMarkdownMode = true;
                    UpdateMode();
                    ShowPendingVisualSource();
                }
            }
            finally { applyingSessionEdit = false; }
        }
    }

    /// <summary>name: PreservePendingVisualEdit; desc: 이전 revision의 전체 원문 snapshot을 사용해 source span을 안전하게 자르고 현재 표시 입력을 별도 저장 대상으로 보존한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 stale/overflow fallback.</summary>
    private void PreservePendingVisualEdit(MarkdownVisualBlock block, BlockEditBaseline baseline, string currentDisplayText)
    {
        string sourceSnapshot = baseline.Source;
        int start = Math.Clamp(block.ContentStart, 0, sourceSnapshot.Length);
        int length = Math.Clamp(block.ContentLength, 0, sourceSnapshot.Length - start);
        pendingVisualSource = sourceSnapshot[..start] + currentDisplayText + sourceSnapshot[(start + length)..];
        UpdateConflictActions();
    }

    private void OnBlockLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RichTextBox box && box.DataContext is MarkdownVisualBlock block)
        {
            applyingSessionEdit = true;
            try { PopulateRichText(box, block); }
            finally { applyingSessionEdit = false; }
            box.Tag = new BlockEditBaseline(block.Text, Session?.Revision ?? 0, Session?.Source ?? "");
        }
    }

    private void OnBlockSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border surface || surface.DataContext is not MarkdownVisualBlock block) return;
        double indent = Math.Clamp(block.IndentationColumns, 0, 48) * ListIndentColumnWidth;
        surface.Margin = new Thickness(indent, 1, 0, 1);
    }

    private void OnTaskClick(object sender, RoutedEventArgs e)
    {
        if (!IsEditable || sender is not CheckBox toggle || toggle.DataContext is not MarkdownVisualBlock block || block.Task is null || Session is null) return;
        // Checkbox state is changed only by the parser-provided task identity and its original revision.
        if (!Session.ToggleTask(block.Task)) toggle.IsChecked = block.Task.IsChecked;
    }

    private void OnInsertChecklistClick(object sender, RoutedEventArgs e)
    {
        if (!IsEditable || Session is null) return;
        if (isMarkdownMode)
        {
            Session.ConvertToChecklist(Session.Revision, activeSourceSelectionStart, activeSourceSelectionLength, isComposing);
            return;
        }
        if (activeVisualBlock is not MarkdownVisualBlock block) return;
        if (!Session.FormatVisualSelection(Session.Revision, block, activeSelectionStart, activeSelectionLength, "Checklist"))
            SwitchToSourceForAmbiguousSelection(block, FindActiveRichTextBox());
    }

    private void OnFormatClick(object sender, RoutedEventArgs e)
    {
        if (!IsEditable || sender is not FrameworkElement element || Session is null) return;
        string kind = element.Tag?.ToString() ?? "";
        if (isMarkdownMode)
        {
            Session.FormatSelection(Session.Revision, activeSourceSelectionStart, activeSourceSelectionLength, kind);
            return;
        }
        if (activeVisualBlock is not MarkdownVisualBlock block) return;
        if (!Session.FormatVisualSelection(Session.Revision, block, activeSelectionStart, activeSelectionLength, kind))
            SwitchToSourceForAmbiguousSelection(block, FindActiveRichTextBox());
    }

    /// <summary>name: OnIndentClick; desc: 본문 도구 메뉴에서 현재 목록 행을 들여쓰기하거나 내어쓴다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 Orig2 IndentSelection API 연결.</summary>
    private void OnIndentClick(object sender, RoutedEventArgs e)
    {
        if (!IsEditable || Session is null || sender is not FrameworkElement element) return;
        bool outdent = element.Tag?.ToString() == "outdent";
        if (isMarkdownMode)
        {
            (int start, int length) = GetSourceSelection();
            Session.IndentSelection(Session.Revision, start, length, outdent, isComposing);
            return;
        }
        if (activeVisualBlock is MarkdownVisualBlock block)
            Session.IndentVisual(Session.Revision, block, outdent, isComposing);
    }

    private void OnOpenUnsupportedClick(object sender, RoutedEventArgs e)
    {
        if (Session is null || sender is not FrameworkElement element || element.DataContext is not MarkdownVisualBlock block) return;
        OnSourceModeClick(SourceModeButton, e);
        int start = Math.Clamp(block.SourceStart, 0, MarkdownSourceEditor.Text.Length);
        int length = Math.Clamp(block.SourceLength, 0, MarkdownSourceEditor.Text.Length - start);
        MarkdownSourceEditor.Select(start, length);
        MarkdownSourceEditor.ScrollToLine(MarkdownSourceEditor.GetLineIndexFromCharacterIndex(start));
    }

    private void OnMarkdownSourceChanged(object sender, TextChangedEventArgs e)
    {
        if (isComposing || updatingSource || Session is null || !IsEditable) return;
        // The source TextBox is bound one-way to Session.Source. A visual edit updates that binding
        // and raises TextChanged even though the user typed in a RichTextBox; synchronize its
        // revision tag before distinguishing a genuine source-mode edit from this reflected update.
        if (!isMarkdownMode || MarkdownSourceEditor.Text == Session.Source)
        {
            MarkdownSourceEditor.Tag = Session.Revision;
            return;
        }
        ApplyMarkdownSourceEdit();
    }

    private void ApplyMarkdownSourceEdit()
    {
        if (updatingSource || Session is null || !IsEditable) return;
        long expectedRevision = MarkdownSourceEditor.Tag is long revision ? revision : Session.Revision;
        applyingSessionEdit = true;
        try
        {
            if (Session.TryReplace(expectedRevision, MarkdownSourceEditor.Text))
            {
                MarkdownSourceEditor.Tag = Session.Revision;
                pendingVisualSource = null;
                UpdateConflictActions();
                EditStatus.Visibility = Visibility.Collapsed;
                return;
            }
            EditStatus.Text = "문서가 변경되어 현재 원문 입력을 보류했습니다. 내용을 복사하거나 충돌 해결을 진행하세요.";
            EditStatus.Visibility = Visibility.Visible;
        }
        finally { applyingSessionEdit = false; }
    }

    private void SynchronizeSourceFromSession()
    {
        if (!IsInitialized || Session is null || MarkdownSourceEditor.Text == Session.Source) return;
        int selectionStart = MarkdownSourceEditor.SelectionStart;
        int selectionLength = MarkdownSourceEditor.SelectionLength;
        updatingSource = true;
        try
        {
            MarkdownSourceEditor.Text = Session.Source;
            int start = Math.Min(selectionStart, MarkdownSourceEditor.Text.Length);
            MarkdownSourceEditor.Select(start, Math.Min(selectionLength, MarkdownSourceEditor.Text.Length - start));
            MarkdownSourceEditor.Tag = Session.Revision;
        }
        finally { updatingSource = false; }
    }

    private (int start, int length) GetSourceSelection()
    {
        if (Keyboard.FocusedElement is TextBox sourceEditor && sourceEditor == MarkdownSourceEditor)
            return (sourceEditor.SelectionStart, sourceEditor.SelectionLength);
        if (activeVisualBlock is MarkdownVisualBlock block)
        {
            return (block.SourceStart, block.SourceLength);
        }
        if (Keyboard.FocusedElement is TextBox editor)
        {
            int start = editor.SelectionStart;
            int sourceStart = editor.DataContext is MarkdownVisualBlock visualBlock ? visualBlock.ContentStart + start : start;
            return (sourceStart, editor.SelectionLength);
        }
        if (isMarkdownMode) return (activeSourceSelectionStart, activeSourceSelectionLength);
        return (Session?.Source.Length ?? 0, 0);
    }

    private void OnSourceEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        activeSourceSelectionStart = MarkdownSourceEditor.SelectionStart;
        activeSourceSelectionLength = MarkdownSourceEditor.SelectionLength;
    }

    private void ShowPendingVisualSource()
    {
        if (pendingVisualSource is null || Session is null) return;
        updatingSource = true;
        try
        {
            MarkdownSourceEditor.Text = pendingVisualSource;
            MarkdownSourceEditor.Tag = Session.Revision;
            MarkdownSourceEditor.CaretIndex = MarkdownSourceEditor.Text.Length;
        }
        finally { updatingSource = false; }
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Session is null || !IsEditable) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        { if (Session.Undo()) e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        { if (Session.Redo()) e.Handled = true; return; }
        if (e.Key == Key.Tab && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift) && !isComposing)
        {
            bool outdent = Keyboard.Modifiers == ModifierKeys.Shift;
            bool changed = isMarkdownMode
                ? IndentSourceSelection(outdent)
                : activeVisualBlock is MarkdownVisualBlock block && Session.IndentVisual(Session.Revision, block, outdent, isComposing);
            if (changed) e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || isComposing) return;
        if (Keyboard.FocusedElement is RichTextBox rich && rich.DataContext is MarkdownVisualBlock visual)
        {
            int displayCaret = DisplaySelectionStart(rich) + DisplaySelectionLength(rich);
            if (Session.ContinueChecklistVisual(Session.Revision, visual, displayCaret, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), isComposing)) e.Handled = true;
            return;
        }
        int caret = GetSourceSelection().start + (Keyboard.FocusedElement is TextBox focused ? focused.SelectionLength : 0);
        if (Session.ContinueChecklist(Session.Revision, caret, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), isComposing)) e.Handled = true;
    }

    /// <summary>name: IndentSourceSelection; desc: Markdown 원문 선택을 현재 revision 계약으로 들여쓰기한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 Tab/Shift+Tab P0 입력 연결.</summary>
    private bool IndentSourceSelection(bool outdent)
    {
        if (Session is null) return false;
        (int start, int length) = GetSourceSelection();
        return Session.IndentSelection(Session.Revision, start, length, outdent, isComposing);
    }

    private void OnCompositionStart(object sender, TextCompositionEventArgs e) => isComposing = true;
    private void OnCompositionUpdate(object sender, TextCompositionEventArgs e) => isComposing = true;
    private void OnCompositionCommit(object sender, TextCompositionEventArgs e)
    {
        isComposing = false;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (isMarkdownMode) ApplyMarkdownSourceEdit();
            else if (Session?.Projection.Count == 0) ApplyEmptyEdit(EmptyEditor);
            else if (Keyboard.FocusedElement is RichTextBox editor) ApplyVisualEdit(editor);
        }));
    }

    /// <summary>name: ReadRichText; desc: 표시 문자열만 읽어 parser source span 편집 API에 전달한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 Orig2 v2 Runs UI.</summary>
    private static string ReadRichText(RichTextBox editor)
    {
        TextRange range = new(editor.Document.ContentStart, editor.Document.ContentEnd);
        return range.Text.TrimEnd('\r', '\n');
    }

    /// <summary>name: PopulateRichText; desc: parser가 제공한 run만 사용해 Markdown 구분자를 숨긴 시각 표현을 구성한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 bold/italic/code 렌더링.</summary>
    private static void PopulateRichText(RichTextBox editor, MarkdownVisualBlock block)
    {
        Paragraph paragraph = new() { Margin = new Thickness(0) };
        foreach (MarkdownDisplayRun run in block.Runs)
        {
            Run inline = new(run.Text)
            {
                FontWeight = run.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = run.Italic ? FontStyles.Italic : FontStyles.Normal,
                FontFamily = run.Code ? new FontFamily("Consolas") : editor.FontFamily,
                Background = run.Code ? (Brush)editor.FindResource("DeepBrush") : Brushes.Transparent,
                Foreground = run.Link is not null ? (Brush)editor.FindResource("AccentBrush") : editor.Foreground,
                ToolTip = run.Link
            };
            paragraph.Inlines.Add(inline);
        }
        editor.Document.Blocks.Clear();
        editor.Document.Blocks.Add(paragraph);
        editor.Document.PagePadding = new Thickness(0);
        editor.Document.FontSize = editor.FontSize;
    }

    /// <summary>name: SwitchToSourceForAmbiguousSelection; desc: 거절된 서식 선택은 원문 편집으로 보내고 화면 입력을 별도 저장 대상으로 유지한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 안전 편집 대체 경로.</summary>
    private void SwitchToSourceForAmbiguousSelection(MarkdownVisualBlock block, RichTextBox? editor)
    {
        EditStatus.Text = Session?.StatusMessage ?? "이 Markdown 서식 경계는 시각 편집에서 안전하게 변경할 수 없습니다.";
        EditStatus.Visibility = Visibility.Visible;
        OnSourceModeClick(SourceModeButton, new RoutedEventArgs());
    }

    private sealed record BlockEditBaseline(string Text, long Revision, string Source);

    private async void OnResolveExternalClick(object sender, RoutedEventArgs e)
    { if (Session is not null) await Session.ResolveConflictAsync("external"); }

    private async void OnResolveLocalClick(object sender, RoutedEventArgs e)
    { if (Session is not null) await Session.ResolveConflictAsync("local-copy"); }

    private async void OnSaveCopyClick(object sender, RoutedEventArgs e)
    {
        if (Session is null) return;
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            AddExtension = true,
            DefaultExt = ".md",
            FileName = Session.NoteId + ".md",
            Filter = "Markdown 문서 (*.md)|*.md|모든 파일 (*.*)|*.*",
            OverwritePrompt = true,
            Title = "편집 중인 메모를 다른 파일로 보존"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await SaveCopyToAsync(dialog.FileName);
            EditStatus.Text = "편집본을 별도 Markdown 파일로 저장했습니다.";
            EditStatus.Visibility = Visibility.Visible;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            EditStatus.Text = "다른 파일 저장에 실패했습니다. 편집 입력은 유지됩니다.";
            EditStatus.Visibility = Visibility.Visible;
        }
    }

    private static bool TryFindTextDelta(string previous, string current, out int start, out int removed, out string inserted)
    {
        int prefix = 0;
        while (prefix < previous.Length && prefix < current.Length && previous[prefix] == current[prefix]) prefix++;
        int suffix = 0;
        while (suffix < previous.Length - prefix && suffix < current.Length - prefix && previous[previous.Length - 1 - suffix] == current[current.Length - 1 - suffix]) suffix++;
        start = prefix;
        removed = previous.Length - prefix - suffix;
        inserted = current.Substring(prefix, current.Length - prefix - suffix);
        return removed != 0 || inserted.Length != 0;
    }
}
