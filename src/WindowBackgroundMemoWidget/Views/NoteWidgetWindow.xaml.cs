using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowBackgroundMemoWidget.Services;
using WindowBackgroundMemoWidget.ViewModels;

namespace WindowBackgroundMemoWidget.Views;

/// <summary>
/// name         : NoteWidgetWindow
/// desc         : 같은 MarkdownDocumentSession을 표시하는 제품 위젯이며 숨김·배치와 문서 편집을 분리한다.
/// author       : NotedexOrig3
/// create date  : 2026-10-24
/// update date  : 2026-10-24 NotedexOrig3 : 실제 Markdown 세션 공유 위젯 화면 신규 구현.
///                2026-10-24 NotedexOrig3 : stale checkbox 표시 복구와 목록 marker 열 정렬 추가.
/// </summary>
/// ---------------------------------------------------------------------
public partial class NoteWidgetWindow : Window
{
    internal const int PreviewTextElementLimit = 320;
    private readonly MemoListItemViewModel item;
    private readonly WidgetWindowController controller;
    private bool releasing;
    private bool previewRefreshQueued;
    private NoteDocumentEditor? documentEditor;
    private MarkdownVisualBlock? continueReadingBlock;
    private int continueReadingOffset;

    public NoteDocumentEditor? DocumentEditor => documentEditor;
    internal bool IsPreviewVisible => PreviewScroll.IsVisible;
    internal double PreviewViewportHeight => PreviewScroll.ViewportHeight;
    internal double PreviewScrollableHeight => PreviewScroll.ScrollableHeight;
    internal bool IsContinueReadingAvailable => ContinueReadingButton.IsVisible;
    internal Action<string>? CaretPlacementDiagnostic { get; set; }

    /// <summary>
    /// name         : NoteWidgetWindow
    /// desc         : NotesDesktopHost의 new NoteWidgetWindow(item) 계약으로 같은 문서 세션을 사용한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : F-07 배치/셸 수명 주기를 WidgetWindowController에 위임.
    /// </summary>
    /// ---------------------------------------------------------------------
    public NoteWidgetWindow(MemoListItemViewModel item)
    {
        this.item = item ?? throw new ArgumentNullException(nameof(item));
        InitializeComponent();
        DataContext = item;
        Style = (Style)Application.Current.FindResource("AppWindowStyle");
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStyle = WindowStyle.ToolWindow;
        SizeToContent = SizeToContent.Manual;
        Width = 380;
        Height = 340;
        Title = item.Title;
        item.PropertyChanged += OnItemPropertyChanged;
        item.Document.Changed += OnDocumentChanged;
        controller = new WidgetWindowController(this, item);
        IsVisibleChanged += OnWidgetVisibilityChanged;
        Closed += (_, _) =>
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            item.Document.Changed -= OnDocumentChanged;
            IsVisibleChanged -= OnWidgetVisibilityChanged;
            ReleaseCompactEditor();
        };
        Closing += OnClosing;
        UpdateLock();
        RebuildPreview();
    }

    private void OnDocumentChanged()
    {
        if (documentEditor is not null || !IsVisible || previewRefreshQueued) return;
        previewRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            previewRefreshQueued = false;
            RebuildPreview();
        }));
    }

    private void OnWidgetVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is bool visible && !visible) ReleaseCompactEditor();
        else if (e.NewValue is true) RebuildPreview();
    }

    private void ReleaseCompactEditor()
    {
        if (documentEditor is null) return;
        if (documentEditor.HasUncommittedInput) return;
        NoteDocumentEditor oldEditor = documentEditor;
        documentEditor = null;
        oldEditor.Session = null;
        DocumentEditorHost.Content = null;
        DocumentEditorHost.Visibility = Visibility.Collapsed;
        PreviewScroll.Visibility = Visibility.Visible;
        UpdateContinueReadingVisibility();
    }

    private void RebuildPreview()
    {
        if (documentEditor is not null || !IsInitialized) return;
        PreviewBlocks.Children.Clear();
        continueReadingBlock = null;
        continueReadingOffset = 0;
        if (item.Document.Projection.Count == 0)
        {
            ContinueReadingButton.Visibility = Visibility.Collapsed;
            TextBlock hint = new()
            {
                Text = "내용을 입력하세요",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = item.MemoFontSize,
                Padding = new Thickness(4, 8, 4, 8),
                Focusable = item.IsEditable
            };
            AutomationProperties.SetName(hint, "새 메모 본문 편집 시작");
            hint.MouseLeftButtonDown += OnEmptyPreviewClick;
            hint.GotKeyboardFocus += OnEmptyPreviewGotKeyboardFocus;
            hint.PreviewKeyDown += OnEmptyPreviewKeyDown;
            PreviewBlocks.Children.Add(hint);
            return;
        }
        foreach (MarkdownVisualBlock block in item.Document.Projection)
        {
            Grid row = new()
            {
                Tag = block,
                Margin = new Thickness(Math.Clamp(block.IndentationColumns, 0, 48) * NoteDocumentEditor.ListIndentColumnWidth, 0, 0, 6)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (block.Task is MarkdownTaskItem task)
            {
                CheckBox toggle = new() { IsChecked = task.IsChecked, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(3, 5, 8, 0), Tag = task, IsEnabled = item.IsEditable };
                AutomationProperties.SetName(toggle, task.Text);
                toggle.Click += OnPreviewTaskClick;
                row.Children.Add(toggle);
            }
            TextBlock marker = new()
            {
                Text = block.ListMarker,
                FontFamily = new FontFamily("Consolas"),
                FontSize = Math.Max(12, item.MemoFontSize - 2),
                Foreground = (Brush)FindResource("MutedBrush"),
                MinWidth = 26,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 7, 0),
                Focusable = false,
                Tag = block,
                Visibility = block.ListDepth == 0 ? Visibility.Collapsed
                    : block.ListMarker.Length > 0 ? Visibility.Visible : Visibility.Hidden
            };
            if (block.ListMarker.Length > 0)
            {
                AutomationProperties.SetName(marker, block.ListMarker);
            }
            if (block.ListDepth > 0) AutomationProperties.SetHelpText(marker, "목록 단계 표시");
            row.Children.Add(marker);
            TextBlock text = new()
            {
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Focusable = item.IsEditable,
                FontSize = block.Kind == "Heading" ? item.MemoFontSize * 1.25 : item.MemoFontSize,
                FontWeight = block.Kind == "Heading" ? FontWeights.SemiBold : FontWeights.Normal,
                Background = block.Kind == "Code" ? (Brush)FindResource("DeepBrush") : Brushes.Transparent,
                Padding = block.Kind == "Code" ? new Thickness(8, 6, 8, 6) : new Thickness(4, 3, 4, 3),
                FontFamily = block.Kind == "Code" ? new FontFamily("Consolas") : new FontFamily("Malgun Gothic"),
                Foreground = block.Kind == "Unsupported" ? (Brush)FindResource("MutedBrush") : (Brush)FindResource("TextBrush"),
                Tag = block
            };
            AutomationProperties.SetName(text, "메모 문단 미리보기");
            IReadOnlyList<PreviewRunSlice> previewRuns = CreatePreviewRunSlices(block, out bool truncated, out int visibleOffset);
            foreach (PreviewRunSlice slice in previewRuns)
            {
                MarkdownDisplayRun run = slice.Origin;
                Run visualRun = new(slice.Text);
                if (run.Bold) visualRun.FontWeight = FontWeights.Bold;
                if (run.Italic) visualRun.FontStyle = FontStyles.Italic;
                if (run.Code) { visualRun.FontFamily = new FontFamily("Consolas"); visualRun.Background = (Brush)FindResource("DeepBrush"); }
                if (run.Link is not null)
                {
                    visualRun.Foreground = (Brush)FindResource("AccentBrush");
                    visualRun.ToolTip = run.Link;
                }
                text.Inlines.Add(visualRun);
            }
            if (truncated)
            {
                text.Inlines.Add(new Run(" …") { Foreground = (Brush)FindResource("MutedBrush"), ToolTip = "계속 읽기" });
                AutomationProperties.SetHelpText(text, "문단 일부를 미리 봅니다. 계속 읽기에서 전체 메모를 열 수 있습니다.");
                continueReadingBlock ??= block;
                if (continueReadingBlock == block) continueReadingOffset = visibleOffset;
            }
            else AutomationProperties.SetHelpText(text, "메모 문단 전체 미리보기");
            text.MouseLeftButtonDown += OnPreviewBlockClick;
            text.GotKeyboardFocus += OnPreviewBlockGotKeyboardFocus;
            text.PreviewKeyDown += OnPreviewBlockKeyDown;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            PreviewBlocks.Children.Add(row);
        }
        UpdateContinueReadingVisibility();
    }

    private void UpdateContinueReadingVisibility()
    {
        ContinueReadingButton.Visibility = documentEditor is null && continueReadingBlock is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnContinueReadingClick(object sender, RoutedEventArgs e)
    {
        MarkdownVisualBlock? longBlock = continueReadingBlock;
        if (longBlock is null) return;
        ActivateEditing(longBlock.SourceStart, continueReadingOffset);
        ContinueReadingButton.Visibility = Visibility.Collapsed;
    }

    private static IReadOnlyList<PreviewRunSlice> CreatePreviewRunSlices(MarkdownVisualBlock block, out bool truncated, out int visibleOffset)
    {
        IReadOnlyList<MarkdownDisplayRun> sourceRuns = block.Runs.Count == 0
            ? [new MarkdownDisplayRun(block.Text, block.ContentStart, block.ContentLength)]
            : block.Runs;
        List<PreviewRunSlice> result = [];
        int remaining = PreviewTextElementLimit;
        visibleOffset = 0;
        truncated = false;
        foreach (MarkdownDisplayRun sourceRun in sourceRuns)
        {
            if (remaining == 0) { truncated = true; break; }
            StringInfo runInfo = new(sourceRun.Text);
            int elementCount = runInfo.LengthInTextElements;
            int take = Math.Min(elementCount, remaining);
            string visibleText = take == elementCount ? sourceRun.Text : runInfo.SubstringByTextElements(0, take);
            if (visibleText.Length > 0) result.Add(new PreviewRunSlice(visibleText, sourceRun));
            visibleOffset += visibleText.Length;
            remaining -= take;
            if (take < elementCount) { truncated = true; break; }
        }
        return result;
    }

    private sealed record PreviewRunSlice(string Text, MarkdownDisplayRun Origin);

    private void OnEmptyPreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (!item.IsEditable) return;
        ActivateEditing(-1);
        e.Handled = true;
    }

    private void OnEmptyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!item.IsEditable || e.Key is not (Key.Enter or Key.Space)) return;
        ActivateEditing(-1);
        e.Handled = true;
    }

    private void OnEmptyPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (item.IsEditable) ActivateEditing(-1);
    }

    private void OnPreviewTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: MarkdownTaskItem task } toggle)
        {
            if (!item.Document.ToggleTask(task)) RebuildPreview();
            e.Handled = true;
        }
    }

    private void OnPreviewBlockClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock text)
        {
            ActivatePreviewPoint(text, e.GetPosition(text));
            if (item.IsEditable) e.Handled = true;
        }
    }

    private void OnPreviewBlockGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        if (sender is TextBlock { Tag: MarkdownVisualBlock block } && item.IsEditable)
            ActivateEditing(block.SourceStart, 0);
    }

    internal void ActivatePreviewPoint(TextBlock text, Point point)
    {
        if (!item.IsEditable || text.Tag is not MarkdownVisualBlock block) return;
        int displayOffset = DisplayOffsetAtPoint(text, block, point);
        ActivateEditing(block.SourceStart, Math.Clamp(displayOffset, 0, block.Text.Length));
    }

    internal static int DisplayOffsetAtPoint(TextBlock text, MarkdownVisualBlock block, Point point)
    {
        TextPointer? position = text.GetPositionFromPoint(point, true);
        if (position is null) return 0;
        string prefix = new TextRange(text.ContentStart, position).Text.TrimEnd('\r', '\n');
        if (prefix.Length <= block.Text.Length && block.Text.StartsWith(prefix, StringComparison.Ordinal)) return prefix.Length;
        string renderedText = string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text));
        int ellipsisOffset = renderedText.LastIndexOf(" …", StringComparison.Ordinal);
        if (ellipsisOffset >= 0 && prefix.Length >= ellipsisOffset && block.Text.StartsWith(renderedText[..ellipsisOffset], StringComparison.Ordinal))
            return ellipsisOffset;
        return 0;
    }

    private void OnPreviewBlockKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || sender is not TextBlock { Tag: MarkdownVisualBlock block }) return;
        ActivateEditing(block.SourceStart, 0);
        e.Handled = true;
    }

    public void ActivateEditing(int sourceStart, int displayOffset = 0)
    {
        if (!item.IsEditable) return;
        int targetDisplayOffset = Math.Clamp(displayOffset, 0,
            item.Document.Projection.FirstOrDefault(block => block.SourceStart == sourceStart)?.Text.Length ?? 0);
        if (documentEditor is null)
        {
            NoteDocumentEditor createdEditor = new()
            {
                Session = item.Document,
                MemoBackground = item.MemoBackground,
                MemoFontSize = item.MemoFontSize,
                CompactToolbar = true,
                AllowMarkdownMode = false,
                IsEditable = item.IsEditable
            };
            documentEditor = createdEditor;
            ContinueReadingButton.Visibility = Visibility.Collapsed;
            DocumentEditorHost.Content = createdEditor;
            PreviewScroll.Visibility = Visibility.Collapsed;
            DocumentEditorHost.Visibility = Visibility.Visible;
            createdEditor.Loaded += (_, _) =>
            {
                if (createdEditor.Session?.Projection.Count == 0)
                {
                    if (createdEditor.FindName("EmptyEditor") is TextBox emptyEditor) emptyEditor.Focus();
                    return;
                }
                QueueFocusAtDisplayOffset(createdEditor, sourceStart, targetDisplayOffset);
            };
        }
        else
        {
            QueueFocusAtDisplayOffset(documentEditor, sourceStart, targetDisplayOffset);
        }
    }

    private void QueueFocusAtDisplayOffset(NoteDocumentEditor editor, int sourceStart, int displayOffset)
    {
        // Parent Loaded can run before each virtualized block's Loaded handler populates its RichTextBox.
        // Defer focus/caret placement until WPF has completed the entire Loaded pass.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!ReferenceEquals(documentEditor, editor)) return;
            RichTextBox? target = DescendantEditors(editor).FirstOrDefault(box =>
                box.DataContext is MarkdownVisualBlock candidate && candidate.SourceStart == sourceStart);
            FocusAtDisplayOffset(target, displayOffset);
        }));
    }

    private void FocusAtDisplayOffset(RichTextBox? editor, int displayOffset)
    {
        if (editor?.DataContext is not MarkdownVisualBlock block) return;
        TextPointer target = NoteDocumentEditor.PositionAtDisplayOffset(editor, Math.Clamp(displayOffset, 0, block.Text.Length));
        int targetRangeOffset = new TextRange(editor.Document.ContentStart, target).Text.TrimEnd('\r', '\n').Length;
        editor.Focus();
        editor.CaretPosition = target;
        int immediateCaretOffset = new TextRange(editor.Document.ContentStart, editor.CaretPosition).Text.TrimEnd('\r', '\n').Length;
        int immediateSelectionStart = new TextRange(editor.Document.ContentStart, editor.Selection.Start).Text.TrimEnd('\r', '\n').Length;
        CaretPlacementDiagnostic?.Invoke($"caret-placement-immediate: requested={displayOffset}; targetRange={targetRangeOffset}; caret={immediateCaretOffset}; selectionStart={immediateSelectionStart}; block={block.Text}");
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            int nextCaretOffset = new TextRange(editor.Document.ContentStart, editor.CaretPosition).Text.TrimEnd('\r', '\n').Length;
            int nextSelectionStart = new TextRange(editor.Document.ContentStart, editor.Selection.Start).Text.TrimEnd('\r', '\n').Length;
            CaretPlacementDiagnostic?.Invoke($"caret-placement-next-turn: requested={displayOffset}; targetRange={targetRangeOffset}; caret={nextCaretOffset}; selectionStart={nextSelectionStart}; block={block.Text}");
        }));
    }

    private static System.Collections.Generic.IEnumerable<RichTextBox> DescendantEditors(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is RichTextBox rich) yield return rich;
            foreach (RichTextBox nested in DescendantEditors(child)) yield return nested;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MemoListItemViewModel.Title) or null or "") Title = item.Title;
        if (e.PropertyName is nameof(MemoListItemViewModel.IsEditable) or nameof(MemoListItemViewModel.IsLocked) or null or "") UpdateLock();
        if (e.PropertyName is nameof(MemoListItemViewModel.MemoBackground) or nameof(MemoListItemViewModel.MemoFontSize) or nameof(MemoListItemViewModel.IsEditable))
        {
            if (documentEditor is not null)
            {
                documentEditor.MemoBackground = item.MemoBackground;
                documentEditor.MemoFontSize = item.MemoFontSize;
                documentEditor.IsEditable = item.IsEditable;
            }
            else RebuildPreview();
        }
        else if (string.IsNullOrEmpty(e.PropertyName))
        {
            if (documentEditor is not null)
            {
                documentEditor.MemoBackground = item.MemoBackground;
                documentEditor.MemoFontSize = item.MemoFontSize;
                documentEditor.IsEditable = item.IsEditable;
            }
            else OnDocumentChanged();
        }
    }

    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || item.IsLocked) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        e.Handled = true;
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => item.IsVisible = false;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!releasing)
        {
            e.Cancel = true;
            item.IsVisible = false;
            Hide();
            return;
        }
        controller.Dispose();
    }

    /// <summary>name: UpdateLock; desc: placement lock blocks move/resize but preserves same-session editing and task toggles.</summary>
    public void UpdateLock()
    {
        ResizeMode = item.IsLocked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        if (documentEditor is not null) documentEditor.IsEditable = item.IsEditable;
    }

    /// <summary>name: Release; desc: host shutdown closes the window without changing the saved visibility intent.</summary>
    public void Release()
    {
        if (documentEditor?.HasUncommittedInput == true)
        {
            item.IsVisible = false;
            Hide();
            return;
        }
        releasing = true;
        Close();
    }

}
