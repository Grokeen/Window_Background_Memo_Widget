using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowBackgroundMemoWidget.ViewModels;
using WindowBackgroundMemoWidget.Views;
using WindowBackgroundMemoWidget.Models;
using WindowBackgroundMemoWidget.Services;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: NotesUiProbe; desc: 격리 마커가 있는 정상 사용자 모드의 실제 WPF 컨트롤/AutomationPeer/렌더를 검증한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 실제 창 통합 QA.
/// update date: 2026-10-24 NotedexOrig3 : 앱 시작 안내/설정 저장/수동 단일 창 경로 검증 보강.
/// update date: 2026-10-24 NotedexOrig3 : 목록 이어쓰기 정렬과 stale widget checkbox 복구 검증 추가.
/// update date: 2026-10-24 NotedexOrig3 : ordered nested list fixture와 시각 요소 누락 진단 보강.
/// update date: 2026-10-24 NotedexOrig3 : 실제 열린 편집 메뉴에서 내어쓰기 명령을 찾도록 probe 수정.
/// update date: 2026-10-24 NotedexOrig3 : 위젯 빈 marker spacer 조회를 UIA 역할로 한정.
/// update date: 2026-10-24 NotedexOrig3 : 링크 preview 조회에서 marker spacer 제외 및 run 진단 추가.
/// update date: 2026-10-24 NotedexOrig3 : 위젯 preview/code 검사 전체에서 본문 TextBlock만 선택.
/// update date: 2026-10-24 NotedexOrig3 : 체크 상태 기대값을 직전 session 상태에서 산출하고 저장 진단 추가.
/// update date: 2026-10-24 NotedexOrig3 : 수동 안내 체크박스 검사를 실제 가시성 기준으로 교정.
/// update date: 2026-10-24 NotedexOrig3 : 첫 실행 안내 viewport 페이지 이미지 수명 검증 추가.</summary>
internal static class NotesUiProbe
{
    /// <summary>name: RunAsync; desc: 합성 메모로 관리 창을 조작하고 원문 없는 결과/자체 화면 PNG를 저장한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 자동 UI 증거.</summary>
    internal static async Task RunAsync(NotesManagerWindow window, NotesManagerViewModel model, string profile)
    {
        List<string> checks = [];
        List<double> searchDurations = [];
        List<object> largeSearch = [];
        double? compactBodyEditorHeight = null;
        string result = "FAIL";
        string? error = null;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.Width = 1440; window.Height = 900;
            model.FolderNameInput = "프로젝트"; model.AddFolderCommand.Execute(null);
            string folderId = model.Folders[0].Id;
            string longTags = string.Join(", ", Enumerable.Range(1, 12).Select(i => "긴분류태그아이디어" + i));
            for (int i = 0; i < 3; i++)
            {
                model.NewMemoCommand.Execute(null);
                model.SelectedMemo!.Title = new[] { "오늘의 기록", "프로젝트 아이디어", "이번 주 할 일" }[i];
                // Ordered child list starts at 12, so blank separators prevent CommonMark paragraph continuation.
                model.SelectedMemo.Body = "# 작은 기록\n\n오늘 꼭 남기고 싶은 생각을 적어 두세요.\n\n이모지 😀와 **강조 구간**  \n다음 줄 끝\n\n- [x] 메모 정리\n- [ ] 다음 **계획 세우기**\n\n- 일반 bullet 항목\n\n    12. 번호 하위 항목\n\n        - 부모 체크 항목\n        - [ ] 중첩 체크 항목\n\n          이어지는 설명\n\n**중요한 생각**과 `작은 단서`를 모읍니다.\n\n[도움말](https://example.com)\n\n```text\nfenced sample\n```";
                model.SelectedMemo.TagsText = "아이디어, 일상";
                model.SelectedMemo.FolderId = folderId;
                model.SelectedMemo.Priority = i % 3;
                model.SelectedMemo.IsVisible = false;
            }
            model.SelectedView = "all";
            model.SelectedMemo = model.AllMemos[0];
            model.SelectedMemo.TagsText = longTags;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            NoteDocumentEditor documentEditor = (NoteDocumentEditor)window.FindName("DocumentEditor");
            TextBox markdownSourceEditor = (TextBox)documentEditor.FindName("MarkdownSourceEditor");
            TextBox emptyEditor = (TextBox)documentEditor.FindName("EmptyEditor");
            Check(checks, "editor-delegates-native-undo-to-markdown-session", !FindBlockEditor(documentEditor, _ => true).IsUndoEnabled
                && !markdownSourceEditor.IsUndoEnabled && !emptyEditor.IsUndoEnabled
                && IsSessionSubscribed(model.SelectedMemo.Document, documentEditor));
            Check(checks, "memo-list-rows-have-no-redundant-edit-buttons", !Descendants<Button>(window).Any(button => AutomationProperties.GetName(button) == "선택한 메모 편집"));
            Check(checks, "hidden-note-remains-manager-editable", !model.SelectedMemo.IsVisible && documentEditor.IsEditable);
            CheckVisualRuns(checks, documentEditor);
            MarkdownVisualBlock managerBulletBlock = RequireBlock(model.SelectedMemo.Document.Projection,
                block => block.Text.Contains("일반 bullet 항목", StringComparison.Ordinal), "manager bullet projection");
            MarkdownVisualBlock managerOrderedBlock = RequireBlock(model.SelectedMemo.Document.Projection,
                block => block.Text.Contains("번호 하위 항목", StringComparison.Ordinal), "manager ordered projection");
            MarkdownVisualBlock managerNestedTaskBlock = RequireBlock(model.SelectedMemo.Document.Projection,
                block => block.Task is not null && block.Text.Contains("중첩 체크 항목", StringComparison.Ordinal), "manager nested task projection");
            MarkdownVisualBlock managerContinuationBlock = RequireBlock(model.SelectedMemo.Document.Projection,
                block => block.Text.Contains("이어지는 설명", StringComparison.Ordinal), "manager continuation projection");
            checks.Add("manager-list-render-diagnostic:" + DescribeListRendering(documentEditor, [managerBulletBlock, managerOrderedBlock,
                managerNestedTaskBlock, managerContinuationBlock]));
            Border managerBulletSurface = FindTaggedVisual<Border>(documentEditor, managerBulletBlock, "manager bullet surface");
            Border managerOrderedSurface = FindTaggedVisual<Border>(documentEditor, managerOrderedBlock, "manager ordered surface");
            Border managerNestedTaskSurface = FindTaggedVisual<Border>(documentEditor, managerNestedTaskBlock, "manager task surface");
            TextBlock managerBulletMarker = FindTaggedVisual<TextBlock>(documentEditor, managerBulletBlock, "manager bullet marker");
            TextBlock managerOrderedMarker = FindTaggedVisual<TextBlock>(documentEditor, managerOrderedBlock, "manager ordered marker");
            Check(checks, "manager-list-marker-and-ast-depth-rendering", managerBulletBlock.ListDepth == 1 && managerBulletBlock.ListMarker == "•"
                && managerBulletMarker.Text == "•" && managerBulletMarker.IsVisible && managerBulletSurface.Margin.Left == managerBulletBlock.IndentationColumns * NoteDocumentEditor.ListIndentColumnWidth
                && managerOrderedBlock.ListDepth == 2 && managerOrderedBlock.ListMarker == "12."
                && managerOrderedMarker.Text == "12." && managerOrderedMarker.IsVisible
                && managerOrderedSurface.Margin.Left == managerOrderedBlock.IndentationColumns * NoteDocumentEditor.ListIndentColumnWidth
                && managerOrderedBlock.IndentationColumns > managerBulletBlock.IndentationColumns);
            Check(checks, "manager-nested-task-keeps-checkbox-without-duplicate-list-marker", managerNestedTaskBlock.ListDepth == 3
                && managerNestedTaskBlock.ListMarker == "" && Descendants<CheckBox>(documentEditor).Any(box => ReferenceEquals(box.DataContext, managerNestedTaskBlock)
                    && box.Visibility == Visibility.Visible)
                && !Descendants<TextBlock>(documentEditor).Any(block => block.IsVisible && ReferenceEquals(block.Tag, managerNestedTaskBlock)
                    && AutomationProperties.GetHelpText(block) == "목록 단계 표시")
                && managerNestedTaskSurface.Margin.Left == managerNestedTaskBlock.IndentationColumns * NoteDocumentEditor.ListIndentColumnWidth);
            Check(checks, "manager-list-continuation-keeps-parent-indentation-without-marker", managerContinuationBlock.ListMarker == ""
                && managerContinuationBlock.IndentationColumns > 0
                && Descendants<Border>(documentEditor).First(border => ReferenceEquals(border.Tag, managerContinuationBlock)).Margin.Left
                    == managerContinuationBlock.IndentationColumns * NoteDocumentEditor.ListIndentColumnWidth);
            RichTextBox managerContinuationEditor = FindBlockEditor(documentEditor, block => ReferenceEquals(block, managerContinuationBlock));
            RichTextBox managerContinuationParentEditor = FindBlockEditor(documentEditor, block => ReferenceEquals(block, managerNestedTaskBlock));
            TextBlock managerContinuationSpacer = Descendants<TextBlock>(documentEditor)
                .FirstOrDefault(block => ReferenceEquals(block.Tag, managerContinuationBlock))
                ?? throw MissingTaggedVisual<TextBlock>(documentEditor, managerContinuationBlock, "manager continuation spacer");
            Check(checks, "manager-list-continuation-content-aligns-with-parent-task", managerContinuationSpacer.Visibility == Visibility.Hidden
                && managerContinuationSpacer.ActualWidth >= 26
                && Math.Abs(LeftInAncestor(managerContinuationEditor, documentEditor) - LeftInAncestor(managerContinuationParentEditor, documentEditor)) < 0.5);
            string beforeNestedTaskIndentSource = model.SelectedMemo.Document.Source;
            RichTextBox nestedTaskEditor = FindBlockEditor(documentEditor, block => block.Task is not null && block.Text.Contains("중첩 체크 항목", StringComparison.Ordinal));
            nestedTaskEditor.Focus();
            await Dispatcher.Yield(DispatcherPriority.Input);
            KeyEventArgs nestedTaskTab = new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(nestedTaskEditor)
                ?? throw new InvalidOperationException("Nested list editor has no presentation source."), Environment.TickCount, Key.Tab)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            nestedTaskEditor.RaiseEvent(nestedTaskTab);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            MarkdownVisualBlock indentedTaskBlock = model.SelectedMemo.Document.Projection.First(block => block.Task is not null
                && block.Text.Contains("중첩 체크 항목", StringComparison.Ordinal));
            Border indentedTaskSurface = Descendants<Border>(documentEditor).First(border => ReferenceEquals(border.Tag, indentedTaskBlock));
            Check(checks, "manager-tab-updates-nested-task-indentation-and-marker-state", nestedTaskTab.Handled
                && model.SelectedMemo.Document.Source != beforeNestedTaskIndentSource
                && indentedTaskBlock.ListMarker == "" && indentedTaskBlock.IndentationColumns > managerNestedTaskBlock.IndentationColumns
                && indentedTaskSurface.Margin.Left == indentedTaskBlock.IndentationColumns * NoteDocumentEditor.ListIndentColumnWidth
                && Descendants<CheckBox>(documentEditor).Any(box => ReferenceEquals(box.DataContext, indentedTaskBlock) && box.IsVisible));
            RichTextBox indentedTaskEditor = FindBlockEditor(documentEditor, block => block.Task is not null && block.Text.Contains("중첩 체크 항목", StringComparison.Ordinal));
            indentedTaskEditor.Focus();
            await Dispatcher.Yield(DispatcherPriority.Input);
            Button nestedListMoreFormats = Descendants<Button>(documentEditor)
                .Single(button => AutomationProperties.GetName(button) == "본문 서식 도구 더보기");
            ContextMenu nestedListFormatMenu = nestedListMoreFormats.ContextMenu
                ?? throw new InvalidOperationException("Nested-list format menu is missing.");
            nestedListFormatMenu.PlacementTarget = nestedListMoreFormats;
            nestedListFormatMenu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            MenuItem outdentMenuItem = nestedListFormatMenu.Items.OfType<MenuItem>()
                .SingleOrDefault(item => item.Tag?.ToString() == "outdent")
                ?? throw new InvalidOperationException("Outdent command is missing from the opened nested-list menu.");
            outdentMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, outdentMenuItem));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            nestedListFormatMenu.IsOpen = false;
            MarkdownVisualBlock outdentedTaskBlock = model.SelectedMemo.Document.Projection.First(block => block.Task is not null
                && block.Text.Contains("중첩 체크 항목", StringComparison.Ordinal));
            Check(checks, "manager-outdent-restores-nested-task-source-and-display", model.SelectedMemo.Document.Source == beforeNestedTaskIndentSource
                && outdentedTaskBlock.ListDepth == managerNestedTaskBlock.ListDepth
                && outdentedTaskBlock.IndentationColumns == managerNestedTaskBlock.IndentationColumns
                && Descendants<Border>(documentEditor).First(border => ReferenceEquals(border.Tag, outdentedTaskBlock)).Margin.Left
                    == managerNestedTaskBlock.IndentationColumns * NoteDocumentEditor.ListIndentColumnWidth);
            RichTextBox plainEditor = FindBlockEditor(documentEditor, b => b.Kind == "Paragraph" && b.Text.Contains("오늘 꼭 남기고", StringComparison.Ordinal));
            EditRun(plainEditor, "꼭", "반드시");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CheckNoVisualFallback(checks, documentEditor, "plain-edit");
            RichTextBox plainAfterEdit = FindBlockEditor(documentEditor, b => b.Kind == "Paragraph" && b.Text.Contains("오늘 반드시 남기고", StringComparison.Ordinal));
            Check(checks, "plain-run-visual-edit-keeps-source-map-and-hidden-state", model.SelectedMemo.Document.Source.Contains("오늘 반드시 남기고", StringComparison.Ordinal) && !model.SelectedMemo.IsVisible);
            CheckCaretAfterEdit(checks, plainAfterEdit, "오늘 반드시".Length);
            bool sessionUndo = model.SelectedMemo.Document.Undo();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            bool undoRestoredSource = sessionUndo && model.SelectedMemo.Document.Source.Contains("오늘 꼭 남기고", StringComparison.Ordinal)
                && FindBlockEditor(documentEditor, b => b.Text.Contains("오늘 꼭 남기고", StringComparison.Ordinal)) is not null;
            bool sessionRedo = model.SelectedMemo.Document.Redo();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(checks, "editor-session-undo-redo-preserves-markdown-edit", undoRestoredSource && sessionRedo
                && model.SelectedMemo.Document.Source.Contains("오늘 반드시 남기고", StringComparison.Ordinal));
            Button boldButton = FindFormattingButton(documentEditor, "bold");
            RichTextBox formatEditor = FindBlockEditor(documentEditor, b => b.Kind == "Paragraph" && b.Text.Contains("모읍니다", StringComparison.Ordinal));
            SelectRun(formatEditor, "모읍니다");
            boldButton.Focus();
            boldButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CheckNoVisualFallback(checks, documentEditor, "toolbar-format");
            Check(checks, "toolbar-format-uses-selection-after-focus-transfer", model.SelectedMemo.Document.Source.Contains("**모읍니다**", StringComparison.Ordinal));
            RichTextBox boldEditor = FindBlockEditor(documentEditor, b => b.Text.Contains("중요한 생각", StringComparison.Ordinal));
            EditRun(boldEditor, "중요", "핵심");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CheckNoVisualFallback(checks, documentEditor, "bold-edit");
            Check(checks, "bold-run-edit-preserves-markdown-delimiters", model.SelectedMemo.Document.Source.Contains("**핵심한 생각**", StringComparison.Ordinal));
            RichTextBox taskEditor = FindBlockEditor(documentEditor, b => b.Task is not null && b.Text.Contains("다음 계획 세우기", StringComparison.Ordinal));
            EditRun(taskEditor, "계획", "아이디어");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CheckNoVisualFallback(checks, documentEditor, "task-edit");
            Check(checks, "task-run-edit-preserves-task-marker", model.SelectedMemo.Document.Source.Contains("- [ ] 다음 **아이디어 세우기**", StringComparison.Ordinal));
            CheckCaretAfterEdit(checks, FindBlockEditor(documentEditor, b => b.Task is not null && b.Text.Contains("다음 아이디어 세우기", StringComparison.Ordinal)), "다음 아이디어".Length);
            RichTextBox indentEditor = FindBlockEditor(documentEditor, b => b.Task is not null && b.Text.Contains("다음 아이디어 세우기", StringComparison.Ordinal));
            indentEditor.Focus();
            KeyEventArgs tabIndent = new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(indentEditor) ?? throw new InvalidOperationException("Editor has no presentation source."), Environment.TickCount, Key.Tab)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            indentEditor.RaiseEvent(tabIndent);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(checks, "tab-indents-current-task-row", tabIndent.Handled && model.SelectedMemo.Document.Source.Contains("  - [ ] 다음 **아이디어 세우기**", StringComparison.Ordinal));
            indentEditor = FindBlockEditor(documentEditor, b => b.Task is not null && b.Text.Contains("다음 아이디어 세우기", StringComparison.Ordinal));
            indentEditor.Focus();
            Button moreFormats = Descendants<Button>(documentEditor).Single(button => AutomationProperties.GetName(button) == "본문 서식 도구 더보기");
            ContextMenu formatMenu = moreFormats.ContextMenu ?? throw new InvalidOperationException("Editor format menu is missing.");
            formatMenu.PlacementTarget = moreFormats;
            formatMenu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            MenuItem outdentItem = formatMenu.Items.OfType<MenuItem>().Single(item => item.Tag?.ToString() == "outdent");
            outdentItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, outdentItem));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            formatMenu.IsOpen = false;
            Check(checks, "overflow-outdent-restores-task-row", model.SelectedMemo.Document.Source.Contains("- [ ] 다음 **아이디어 세우기**", StringComparison.Ordinal));
            RichTextBox headingEditor = FindBlockEditor(documentEditor, b => b.Kind == "Paragraph" && b.Text.Contains("오늘 반드시 남기고", StringComparison.Ordinal));
            SelectRun(headingEditor, "오늘 반드시 남기고 싶은 생각을 적어 두세요.");
            headingEditor.Focus();
            formatMenu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            MenuItem headingItem = formatMenu.Items.OfType<MenuItem>().Single(item => item.Tag?.ToString() == "heading");
            headingItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, headingItem));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            formatMenu.IsOpen = false;
            Check(checks, "overflow-heading-tool-formats-current-line", model.SelectedMemo.Document.Source.Contains("# 오늘 반드시 남기고 싶은 생각을 적어 두세요.", StringComparison.Ordinal));
            RichTextBox codeEditor = FindBlockEditor(documentEditor, b => b.Text.Contains("작은 단서", StringComparison.Ordinal));
            EditRun(codeEditor, "단서", "조각");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CheckNoVisualFallback(checks, documentEditor, "code-edit");
            Check(checks, "code-run-edit-preserves-code-markers", model.SelectedMemo.Document.Source.Contains("`작은 조각`", StringComparison.Ordinal));
            MarkdownVisualBlock taskBlock = model.SelectedMemo.Document.Projection.First(b => b.Task is not null && b.Text.Contains("다음 아이디어 세우기", StringComparison.Ordinal));
            long beforeImeRevision = model.SelectedMemo.Document.Revision;
            bool imeContinued = model.SelectedMemo.Document.ContinueChecklistVisual(beforeImeRevision, taskBlock, taskBlock.Text.Length, false, true);
            Check(checks, "ime-composition-defers-task-continuation", !imeContinued && model.SelectedMemo.Document.Revision == beforeImeRevision);
            RichTextBox continuationEditor = FindBlockEditor(documentEditor, b => b.Task is not null && b.Text.Contains("다음 아이디어 세우기", StringComparison.Ordinal));
            continuationEditor.Focus();
            continuationEditor.CaretPosition = continuationEditor.Document.ContentEnd;
            KeyEventArgs enter = new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(continuationEditor) ?? throw new InvalidOperationException("Editor has no presentation source."), Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            continuationEditor.RaiseEvent(enter);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CheckNoVisualFallback(checks, documentEditor, "checklist-continuation");
            Check(checks, "bold-task-end-caret-continues-checklist", enter.Handled && model.SelectedMemo.Document.Source.Contains("- [ ] \n", StringComparison.Ordinal));
            MemoListItemViewModel sharedMemo = model.SelectedMemo ?? throw new InvalidOperationException("A note must be selected for widget integration checks.");
            await model.FlushAsync();
            sharedMemo.IsVisible = true;
            NoteWidgetWindow widget = new(sharedMemo);
            widget.Show();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(checks, "widget-default-uses-lightweight-preview-with-no-richtextboxes", widget.DocumentEditor is null
                    && !Descendants<RichTextBox>(widget).Any()
                    && Descendants<TextBlock>(widget).Any(block => AutomationProperties.GetName(block) == "메모 문단 미리보기"
                        && block.Tag is MarkdownVisualBlock));
                MarkdownVisualBlock widgetBulletBlock = sharedMemo.Document.Projection.First(block => block.Text.Contains("일반 bullet 항목", StringComparison.Ordinal));
                MarkdownVisualBlock widgetOrderedBlock = sharedMemo.Document.Projection.First(block => block.Text.Contains("번호 하위 항목", StringComparison.Ordinal));
                MarkdownVisualBlock widgetNestedTaskBlock = sharedMemo.Document.Projection.First(block => block.Task is not null
                    && block.Text.Contains("중첩 체크 항목", StringComparison.Ordinal));
                TextBlock widgetBulletMarker = Descendants<TextBlock>(widget).First(block => ReferenceEquals(block.Tag, widgetBulletBlock)
                    && AutomationProperties.GetHelpText(block) == "목록 단계 표시");
                TextBlock widgetOrderedMarker = Descendants<TextBlock>(widget).First(block => ReferenceEquals(block.Tag, widgetOrderedBlock)
                    && AutomationProperties.GetHelpText(block) == "목록 단계 표시");
                Grid widgetBulletRow = Descendants<Grid>(widget).First(row => ReferenceEquals(row.Tag, widgetBulletBlock));
                Grid widgetOrderedRow = Descendants<Grid>(widget).First(row => ReferenceEquals(row.Tag, widgetOrderedBlock));
                Check(checks, "widget-list-marker-and-ast-depth-rendering", widgetBulletMarker.Text == "•" && widgetBulletMarker.IsVisible
                    && widgetBulletRow.Margin.Left == widgetBulletBlock.IndentationColumns * NoteDocumentEditor.ListIndentColumnWidth
                    && widgetOrderedMarker.Text == "12." && widgetOrderedMarker.IsVisible
                    && widgetOrderedRow.Margin.Left == widgetOrderedBlock.IndentationColumns * NoteDocumentEditor.ListIndentColumnWidth
                    && widgetOrderedBlock.IndentationColumns > widgetBulletBlock.IndentationColumns);
                Check(checks, "widget-nested-task-checkbox-has-no-duplicate-list-marker", widgetNestedTaskBlock.ListDepth == 3
                    && widgetNestedTaskBlock.ListMarker == ""
                    && Descendants<CheckBox>(widget).Any(box => box.Tag is MarkdownTaskItem task && task.Text.Contains("중첩 체크 항목", StringComparison.Ordinal))
                    && !Descendants<TextBlock>(widget).Any(block => block.IsVisible && ReferenceEquals(block.Tag, widgetNestedTaskBlock)
                        && AutomationProperties.GetHelpText(block) == "목록 단계 표시"));
                MarkdownVisualBlock widgetContinuationBlock = sharedMemo.Document.Projection.First(block => block.Text.Contains("이어지는 설명", StringComparison.Ordinal));
                Grid widgetContinuationRow = Descendants<Grid>(widget).Single(row => ReferenceEquals(row.Tag, widgetContinuationBlock));
                TextBlock widgetContinuationSpacer = Descendants<TextBlock>(widget).Single(block => ReferenceEquals(block.Tag, widgetContinuationBlock)
                    && AutomationProperties.GetHelpText(block) == "목록 단계 표시");
                TextBlock widgetContinuationText = Descendants<TextBlock>(widget).Single(block => ReferenceEquals(block.Tag, widgetContinuationBlock)
                    && AutomationProperties.GetName(block) == "메모 문단 미리보기");
                TextBlock widgetContinuationParentText = Descendants<TextBlock>(widget).Single(block => ReferenceEquals(block.Tag, widgetNestedTaskBlock)
                    && AutomationProperties.GetName(block) == "메모 문단 미리보기");
                Check(checks, "widget-list-continuation-content-aligns-with-parent-task", widgetContinuationSpacer.Visibility == Visibility.Hidden
                    && widgetContinuationSpacer.ActualWidth >= 26
                    && widgetContinuationRow.Margin.Left == widgetContinuationBlock.IndentationColumns * NoteDocumentEditor.ListIndentColumnWidth
                    && Math.Abs(LeftInAncestor(widgetContinuationText, widget) - LeftInAncestor(widgetContinuationParentText, widget)) < 0.5);
                const string previewLinkUrl = "https://example.com";
                TextBlock linkPreview = Descendants<TextBlock>(widget).FirstOrDefault(block =>
                    AutomationProperties.GetName(block) == "메모 문단 미리보기"
                    && block.Tag is MarkdownVisualBlock visual && visual.Runs.Any(run => run.Link == previewLinkUrl))
                    ?? throw new InvalidOperationException("Link preview text block was not found; tagged marker spacers are excluded by AutomationProperties.Name.");
                Run previewLink = linkPreview.Inlines.OfType<Run>().FirstOrDefault(run => run.ToolTip?.ToString() == previewLinkUrl)
                    ?? throw new InvalidOperationException($"Rendered link run with tooltip '{previewLinkUrl}' was not found. "
                        + "Rendered runs: " + string.Join(" | ", linkPreview.Inlines.OfType<Run>().Select(run => $"text='{run.Text}', tooltip='{run.ToolTip}'")));
                TextBlock fencedCodePreview = Descendants<TextBlock>(widget).First(block =>
                    AutomationProperties.GetName(block) == "메모 문단 미리보기" && block.Tag is MarkdownVisualBlock { Kind: "Code" });
                List<string> previewFailures = [];
                CheckPreviewDetail(checks, previewFailures, "widget-preview-projection-text", Descendants<TextBlock>(widget)
                    .Where(block => AutomationProperties.GetName(block) == "메모 문단 미리보기" && block.Tag is MarkdownVisualBlock)
                    .All(block =>
                    {
                        if (block.Tag is not MarkdownVisualBlock visual) return false;
                        string rendered = string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));
                        if (AutomationProperties.GetHelpText(block)?.Contains("일부", StringComparison.Ordinal) == true)
                            return rendered.EndsWith(" …", StringComparison.Ordinal)
                                && visual.Text.StartsWith(rendered[..^2], StringComparison.Ordinal);
                        return rendered == visual.Text;
                    }));
                CheckPreviewDetail(checks, previewFailures, "widget-preview-link-foreground", previewLink.Foreground is SolidColorBrush linkBrush
                    && window.FindResource("AccentBrush") is SolidColorBrush accentBrush && linkBrush.Color == accentBrush.Color);
                CheckPreviewDetail(checks, previewFailures, "widget-preview-code-font-family", fencedCodePreview.FontFamily.Source == "Consolas");
                CheckPreviewDetail(checks, previewFailures, "widget-preview-code-background", fencedCodePreview.Background is SolidColorBrush codeBrush
                    && window.FindResource("DeepBrush") is SolidColorBrush deepBrush && codeBrush.Color == deepBrush.Color);
                CheckPreviewDetail(checks, previewFailures, "widget-preview-code-literal-text", string.Concat(fencedCodePreview.Inlines.OfType<Run>().Select(run => run.Text))
                    == ((MarkdownVisualBlock)fencedCodePreview.Tag).Text);
                if (previewFailures.Count > 0)
                    throw new InvalidOperationException("widget-preview-detail-failures: " + string.Join(", ", previewFailures));
                MarkdownTaskItem previewTask = sharedMemo.Document.Tasks.First(task => task.Text.Contains("메모 정리", StringComparison.Ordinal));
                CheckBox previewToggle = Descendants<CheckBox>(widget).First(box => box.Tag is MarkdownTaskItem task && task.Text.Contains("메모 정리", StringComparison.Ordinal));
                previewToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, previewToggle));
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "lightweight-preview-checkbox-updates-session-revision", sharedMemo.Document.Tasks.First(task => task.Text.Contains("메모 정리", StringComparison.Ordinal)).IsChecked != previewTask.IsChecked
                    && sharedMemo.Document.Revision > previewTask.Revision && widget.DocumentEditor is null);
                previewToggle = Descendants<CheckBox>(widget).First(box => box.Tag is MarkdownTaskItem task && task.Text.Contains("메모 정리", StringComparison.Ordinal));
                previewToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, previewToggle));
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "lightweight-preview-checkbox-toggles-session-without-creating-editor", widget.DocumentEditor is null
                    && sharedMemo.Document.Tasks.First(task => task.Text.Contains("메모 정리", StringComparison.Ordinal)).IsChecked == previewTask.IsChecked);
                bool previewUndo = sharedMemo.Document.Undo();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                bool previewUndoChangedState = sharedMemo.Document.Tasks.First(task => task.Text.Contains("메모 정리", StringComparison.Ordinal)).IsChecked != previewTask.IsChecked;
                bool previewRedo = sharedMemo.Document.Redo();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "lightweight-task-toggle-uses-shared-session-undo-redo", previewUndo && previewUndoChangedState && previewRedo
                    && sharedMemo.Document.Tasks.First(task => task.Text.Contains("메모 정리", StringComparison.Ordinal)).IsChecked == previewTask.IsChecked);
                MarkdownTaskItem stalePreviewTask = sharedMemo.Document.Tasks.First(task => task.Text.Contains("메모 정리", StringComparison.Ordinal));
                CheckBox stalePreviewToggle = Descendants<CheckBox>(widget).First(box => box.Tag is MarkdownTaskItem task
                    && task.Text.Contains("메모 정리", StringComparison.Ordinal));
                bool staleRevisionAdvance = sharedMemo.Document.ToggleTask(stalePreviewTask);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                stalePreviewToggle.IsChecked = stalePreviewTask.IsChecked;
                stalePreviewToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, stalePreviewToggle));
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                CheckBox refreshedPreviewToggle = Descendants<CheckBox>(widget).First(box => box.Tag is MarkdownTaskItem task
                    && task.Text.Contains("메모 정리", StringComparison.Ordinal));
                bool currentTaskState = sharedMemo.Document.Tasks.First(task => task.Text.Contains("메모 정리", StringComparison.Ordinal)).IsChecked;
                Check(checks, "stale-widget-task-click-rebuilds-checkbox-from-current-session", staleRevisionAdvance
                    && stalePreviewTask.Revision < sharedMemo.Document.Revision && widget.DocumentEditor is null
                    && refreshedPreviewToggle.IsChecked == currentTaskState && sharedMemo.Document.Source.Contains("메모 정리", StringComparison.Ordinal));
                MarkdownVisualBlock activationBlock = sharedMemo.Document.Projection.First(block => block.Text.Contains("오늘 반드시 남기고", StringComparison.Ordinal));
                TextBlock previewBlock = Descendants<TextBlock>(widget).First(block => ReferenceEquals(block.Tag, activationBlock)
                    && AutomationProperties.GetName(block) == "메모 문단 미리보기");
                Point previewPoint = new(Math.Min(60, Math.Max(0, previewBlock.ActualWidth - 1)), Math.Min(6, Math.Max(0, previewBlock.ActualHeight - 1)));
                TextPointer pointPosition = previewBlock.GetPositionFromPoint(previewPoint, true)
                    ?? throw new InvalidOperationException("Widget preview point did not map to text.");
                int expectedPointOffset = new TextRange(previewBlock.ContentStart, pointPosition).Text.TrimEnd('\r', '\n').Length;
                int previewCaret = NoteWidgetWindow.DisplayOffsetAtPoint(previewBlock, activationBlock, previewPoint);
                Check(checks, "preview-point-hit-test-maps-visible-offset", previewCaret == expectedPointOffset
                    && previewCaret > 0 && previewCaret <= activationBlock.Text.Length);
                widget.CaretPlacementDiagnostic = diagnostic => checks.Add(diagnostic);
                widget.ActivatePreviewPoint(previewBlock, previewPoint);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                NoteDocumentEditor widgetEditor = widget.DocumentEditor ?? throw new InvalidOperationException("Widget editor did not activate.");
                Check(checks, "widget-and-manager-share-the-same-document-session-after-focus", ReferenceEquals(widgetEditor.Session, sharedMemo.Document));
                RichTextBox activatedBlock = FindBlockEditor(widgetEditor, block => block.SourceStart == activationBlock.SourceStart);
                int actualWidgetCaret = new TextRange(activatedBlock.Document.ContentStart, activatedBlock.CaretPosition).Text.TrimEnd('\r', '\n').Length;
                int immediateSelectionStart = new TextRange(activatedBlock.Document.ContentStart, activatedBlock.Selection.Start).Text.TrimEnd('\r', '\n').Length;
                int caretBeforeAssignmentTarget = new TextRange(activatedBlock.Document.ContentStart,
                    NoteDocumentEditor.PositionAtDisplayOffset(activatedBlock, previewCaret)).Text.TrimEnd('\r', '\n').Length;
                checks.Add($"widget-caret-after-activation: requested={previewCaret}; targetRange={caretBeforeAssignmentTarget}; caret={actualWidgetCaret}; selectionStart={immediateSelectionStart}; block={activationBlock.Text}");
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                int nextTurnWidgetCaret = new TextRange(activatedBlock.Document.ContentStart, activatedBlock.CaretPosition).Text.TrimEnd('\r', '\n').Length;
                int nextTurnSelectionStart = new TextRange(activatedBlock.Document.ContentStart, activatedBlock.Selection.Start).Text.TrimEnd('\r', '\n').Length;
                Check(checks, "preview-point-display-caret-offset-maps-into-rich-editor", actualWidgetCaret == previewCaret
                    && nextTurnWidgetCaret == previewCaret && nextTurnSelectionStart == previewCaret);
                MarkdownVisualBlock styledEmojiBlock = sharedMemo.Document.Projection.First(block => block.Text.Contains("😀", StringComparison.Ordinal));
                TextBlock styledEmojiPreview = Descendants<TextBlock>(widget).First(block => ReferenceEquals(block.Tag, styledEmojiBlock)
                    && AutomationProperties.GetName(block) == "메모 문단 미리보기");
                Point styledEmojiPoint = new(Math.Max(0, styledEmojiPreview.ActualWidth - 2), Math.Max(0, styledEmojiPreview.ActualHeight - 1));
                TextPointer styledPointPosition = styledEmojiPreview.GetPositionFromPoint(styledEmojiPoint, true)
                    ?? throw new InvalidOperationException("Styled emoji preview point did not map to text.");
                int styledExpectedOffset = new TextRange(styledEmojiPreview.ContentStart, styledPointPosition).Text.TrimEnd('\r', '\n').Length;
                widget.ActivatePreviewPoint(styledEmojiPreview, styledEmojiPoint);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                RichTextBox styledEmojiEditor = FindBlockEditor(widgetEditor, block => block.SourceStart == styledEmojiBlock.SourceStart);
                int styledActualOffset = new TextRange(styledEmojiEditor.Document.ContentStart, styledEmojiEditor.CaretPosition).Text.TrimEnd('\r', '\n').Length;
                checks.Add($"widget-caret-styled-emoji-diagnostic: expected={styledExpectedOffset}; actual={styledActualOffset}; block={styledEmojiBlock.Text}; caret={new TextRange(styledEmojiEditor.Document.ContentStart, styledEmojiEditor.CaretPosition).Text}");
                Check(checks, "preview-point-caret-maps-across-styled-emoji-newline", styledActualOffset == styledExpectedOffset
                    && styledEmojiBlock.Text.Contains('\n') && styledEmojiBlock.Runs.Any(run => run.Bold)
                    && styledEmojiBlock.Text.Contains("😀", StringComparison.Ordinal));
                widget.Hide();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                widget.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                TextBlock keyboardPreview = Descendants<TextBlock>(widget).First(block => block.Tag is MarkdownVisualBlock
                    && AutomationProperties.GetName(block) == "메모 문단 미리보기");
                keyboardPreview.Focus();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                widgetEditor = widget.DocumentEditor ?? throw new InvalidOperationException("Keyboard focus did not activate widget editor.");
                Check(checks, "keyboard-focus-lazily-creates-widget-editor", Descendants<RichTextBox>(widgetEditor).Any());
                Check(checks, "manager-and-widget-session-observers-are-live", IsSessionSubscribed(sharedMemo.Document, documentEditor)
                    && IsSessionSubscribed(sharedMemo.Document, widgetEditor));
                RichTextBox widgetEditBlock = FindBlockEditor(widgetEditor, block => block.Text.Contains("오늘 반드시 남기고", StringComparison.Ordinal));
                EditRun(widgetEditBlock, "반드시", "분명히");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "widget-rich-edit-updates-manager-through-shared-session", sharedMemo.Document.Source.Contains("오늘 분명히 남기고", StringComparison.Ordinal)
                    && FindBlockEditor(documentEditor, block => block.Text.Contains("오늘 분명히 남기고", StringComparison.Ordinal)) is not null);
                bool widgetUndo = sharedMemo.Document.Undo();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                bool widgetRedo = sharedMemo.Document.Redo();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                bool widgetUndoRestore = sharedMemo.Document.Undo();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "widget-rich-edit-uses-shared-session-undo-redo", widgetUndo && widgetRedo && widgetUndoRestore
                    && sharedMemo.Document.Source.Contains("오늘 반드시 남기고", StringComparison.Ordinal));
                model.SetNoteColorCommand.Execute("green");
                model.SetNoteFontSizeCommand.Execute("24");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                RichTextBox managerAppearanceBlock = FindBlockEditor(documentEditor, block => block.Text.Contains("오늘 반드시 남기고", StringComparison.Ordinal));
                RichTextBox widgetAppearanceBlock = FindBlockEditor(widgetEditor, block => block.Text.Contains("오늘 반드시 남기고", StringComparison.Ordinal));
                Color greenMemo = ((SolidColorBrush)sharedMemo.MemoBackground).Color;
                Check(checks, "per-note-green-color-and-24pt-scale-reaches-both-surfaces", documentEditor.MemoFontSize == 24
                    && widgetEditor.MemoFontSize == 24
                    && ((SolidColorBrush)documentEditor.MemoBackground).Color == greenMemo
                    && ((SolidColorBrush)widgetEditor.MemoBackground).Color == greenMemo
                    && managerAppearanceBlock.Document.FontSize == 30 && widgetAppearanceBlock.Document.FontSize == 30
                    && Descendants<Border>(documentEditor).Any(border => border.Background is SolidColorBrush brush && brush.Color == greenMemo)
                    && Descendants<Border>(widgetEditor).Any(border => border.Background is SolidColorBrush brush && brush.Color == greenMemo)
                    && widgetEditor.CompactToolbar);
                Render(widget, Path.Combine(profile, "notes-widget-appearance.png"));
                model.SetNoteColorCommand.Execute("default");
                model.SetNoteFontSizeCommand.Execute("16");
                await model.FlushAsync();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                for (int retry = 0; retry < 25 && string.IsNullOrWhiteSpace(sharedMemo.WidgetStatus); retry++) await Task.Delay(100);
                Check(checks, "widget-reports-shell-attachment-or-explicit-fallback", sharedMemo.WidgetStatus.Contains("바탕화면에 표시 중", StringComparison.Ordinal)
                    || sharedMemo.WidgetStatus.Contains("일반 하단 창으로 표시 중", StringComparison.Ordinal));
                Render(widget, Path.Combine(profile, "notes-widget.png"));
                RichTextBox managerSharedBlock = FindBlockEditor(documentEditor, b => b.Text.Contains("오늘 반드시 남기고", StringComparison.Ordinal));
                EditRun(managerSharedBlock, "반드시", "항상");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "manager-edit-reaches-widget-projection", FindBlockEditor(widgetEditor, b => b.Text.Contains("오늘 항상 남기고", StringComparison.Ordinal)) is not null);
                CheckBox widgetTask = Descendants<CheckBox>(widgetEditor).Single(box => box.IsVisible
                    && box.DataContext is MarkdownVisualBlock { Task: not null } block
                    && block.Text.Contains("메모 정리", StringComparison.Ordinal));
                MarkdownVisualBlock taskBlockBeforeToggle = (MarkdownVisualBlock)widgetTask.DataContext;
                bool taskStateBeforeToggle = taskBlockBeforeToggle.Task!.IsChecked;
                Check(checks, "widget-task-checkbox-starts-from-current-session-state", widgetTask.IsChecked == taskStateBeforeToggle);
                widgetTask.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, widgetTask));
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                await model.FlushAsync();
                byte[] persistedBytes = File.ReadAllBytes(sharedMemo.Document.FilePath);
                MarkdownVisualBlock persistedTask = RequireBlock(sharedMemo.Document.Projection,
                    block => block.Task is not null && block.Text.Contains("메모 정리", StringComparison.Ordinal), "persisted widget task projection");
                bool expectedTaskState = !taskStateBeforeToggle;
                bool persistedBytesMatchSession = persistedBytes.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(sharedMemo.Document.Source));
                bool managerTaskStateMatches = Descendants<CheckBox>(documentEditor).Any(box => box.DataContext is MarkdownVisualBlock block
                    && block.Task is not null && block.Text.Contains("메모 정리", StringComparison.Ordinal)
                    && box.IsChecked == expectedTaskState);
                checks.Add($"widget-task-persist-diagnostic: before={taskStateBeforeToggle}; after={persistedTask.Task!.IsChecked}; "
                    + $"expected={expectedTaskState}; managerMatches={managerTaskStateMatches}; "
                    + $"sourceMarker={(expectedTaskState ? "checked" : "unchecked")}; bytesMatchSession={persistedBytesMatchSession}");
                Check(checks, "widget-task-toggle-reaches-manager-and-real-markdown-bytes", persistedTask.Task!.IsChecked == expectedTaskState
                    && managerTaskStateMatches
                    && sharedMemo.Document.Source.Contains(expectedTaskState ? "- [x] 메모 정리" : "- [ ] 메모 정리", StringComparison.Ordinal)
                    && persistedBytesMatchSession);
                widget.Hide();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "hidden-widget-unloads-rich-editor-and-unsubscribes-session", widget.DocumentEditor is null
                    && !IsSessionSubscribed(sharedMemo.Document, widgetEditor));
                widget.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(checks, "reshown-widget-rebuilds-latest-lightweight-projection", widget.DocumentEditor is null
                    && widget.IsPreviewVisible
                    && Descendants<TextBlock>(widget).Any(block => AutomationProperties.GetName(block) == "메모 문단 미리보기"
                        && block.Tag is MarkdownVisualBlock visual
                        && visual.Text.Contains("오늘 항상 남기고", StringComparison.Ordinal)));
                TextBlock pendingActivationPreview = Descendants<TextBlock>(widget).First(block => block.Tag is MarkdownVisualBlock visual
                    && visual.Text.Contains("오늘 항상 남기고", StringComparison.Ordinal)
                    && AutomationProperties.GetName(block) == "메모 문단 미리보기");
                widget.ActivatePreviewPoint(pendingActivationPreview, new Point(8, 6));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                NoteDocumentEditor pendingEditor = widget.DocumentEditor ?? throw new InvalidOperationException("Widget editor did not activate for pending-input probe.");
                RichTextBox pendingOverflowBlock = FindBlockEditor(pendingEditor, block => block.Text.Contains("오늘 항상 남기고", StringComparison.Ordinal));
                MarkdownVisualBlock overflowVisual = (MarkdownVisualBlock)pendingOverflowBlock.DataContext;
                string overflowSource = sharedMemo.Document.Source;
                string overflowText = new string('용', 10001);
                int overflowStart = overflowVisual.ContentStart;
                int overflowLength = overflowVisual.ContentLength;
                string expectedPendingSource = overflowSource[..overflowStart] + overflowText + overflowSource[(overflowStart + overflowLength)..];
                pendingOverflowBlock.Selection.Select(pendingOverflowBlock.Document.ContentStart, pendingOverflowBlock.Document.ContentEnd);
                pendingOverflowBlock.Selection.Text = overflowText;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "widget-overflow-input-is-marked-uncommitted", pendingEditor.HasUncommittedInput);
                widget.Hide();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "widget-hide-retains-pending-editor-and-session", ReferenceEquals(widget.DocumentEditor, pendingEditor)
                    && pendingEditor.Session == sharedMemo.Document && pendingEditor.HasUncommittedInput
                    && IsSessionSubscribed(sharedMemo.Document, pendingEditor));
                widget.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(checks, "widget-show-restores-same-pending-editor", ReferenceEquals(widget.DocumentEditor, pendingEditor)
                    && pendingEditor.HasUncommittedInput);
                string canonicalBeforeExit = sharedMemo.Document.Source;
                byte[] canonicalFileBeforeExit = File.ReadAllBytes(sharedMemo.Document.FilePath);
                model.ExitApplicationCommand.Execute(null);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "application-exit-guard-preserves-visible-pending-widget", window.IsVisible && widget.IsVisible
                    && Application.Current.Windows.OfType<NoteWidgetWindow>().Any(candidate => ReferenceEquals(candidate, widget))
                    && ReferenceEquals(widget.DocumentEditor, pendingEditor) && ReferenceEquals(pendingEditor.Session, sharedMemo.Document)
                    && pendingEditor.HasUncommittedInput);
                Check(checks, "application-exit-guard-does-not-commit-or-overwrite-canonical-input", sharedMemo.Document.Source == canonicalBeforeExit
                    && File.ReadAllBytes(sharedMemo.Document.FilePath).SequenceEqual(canonicalFileBeforeExit));
                string pendingCopyPath = Path.Combine(profile, "widget-pending-copy.md");
                await pendingEditor.SaveCopyToAsync(pendingCopyPath);
                Check(checks, "widget-pending-save-copy-preserves-exact-input", File.ReadAllBytes(pendingCopyPath)
                    .SequenceEqual(System.Text.Encoding.UTF8.GetBytes(expectedPendingSource)) && !pendingEditor.HasUncommittedInput);
                System.Reflection.FieldInfo composingField = typeof(NoteDocumentEditor).GetField("isComposing", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Composition state field is missing.");
                composingField.SetValue(pendingEditor, true);
                Check(checks, "widget-composition-is-marked-uncommitted", pendingEditor.HasUncommittedInput);
                widget.Hide();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "widget-hide-retains-editor-during-composition", ReferenceEquals(widget.DocumentEditor, pendingEditor)
                    && pendingEditor.Session == sharedMemo.Document && IsSessionSubscribed(sharedMemo.Document, pendingEditor));
                widget.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                composingField.SetValue(pendingEditor, false);
                widget.Hide();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "widget-releases-editor-after-pending-input-is-saved", widget.DocumentEditor is null);
                widget.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                string oldLongPreviewSource = new string('오', 450) + "\n\n" + new string('바', 450);
                bool acceptedOldLongPreview = sharedMemo.Document.TryReplace(sharedMemo.Document.Revision, oldLongPreviewSource);
                await model.FlushAsync();
                await Dispatcher.Yield(DispatcherPriority.Background);
                MarkdownVisualBlock oldCtaTarget = sharedMemo.Document.Projection.First(block => block.Text.Length > NoteWidgetWindow.PreviewTextElementLimit);
                string updatedLongPreviewSource = "짧아진 첫 문단\n\n" + new string('나', 450) + "\n\n" + new string('다', 450);
                bool acceptedUpdatedLongPreview = sharedMemo.Document.TryReplace(sharedMemo.Document.Revision, updatedLongPreviewSource);
                await model.FlushAsync();
                await Dispatcher.Yield(DispatcherPriority.Background);
                MarkdownVisualBlock currentCtaTarget = sharedMemo.Document.Projection.First(block => block.Text.Length > NoteWidgetWindow.PreviewTextElementLimit);
                Button refreshedContinueButton = Descendants<Button>(widget).First(button => AutomationProperties.GetName(button) == "전체 메모 계속 읽기");
                Check(checks, "widget-continue-target-rebuilds-after-first-long-block-shortened", acceptedOldLongPreview && acceptedUpdatedLongPreview
                    && oldCtaTarget.SourceStart == 0 && currentCtaTarget.SourceStart > 0
                    && currentCtaTarget.Text == new string('나', 450) && widget.IsContinueReadingAvailable
                    && widget.DocumentEditor is null && Descendants<TextBlock>(widget).Any(block => ReferenceEquals(block.Tag, currentCtaTarget)
                        && AutomationProperties.GetName(block) == "메모 문단 미리보기"));
                refreshedContinueButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, refreshedContinueButton));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                NoteDocumentEditor refreshedCtaEditor = widget.DocumentEditor ?? throw new InvalidOperationException("Rebuilt continue-reading button did not open the current document.");
                RichTextBox refreshedCtaBlock = FindBlockEditor(refreshedCtaEditor, block => block.SourceStart == currentCtaTarget.SourceStart);
                int refreshedCtaCaret = new TextRange(refreshedCtaBlock.Document.ContentStart, refreshedCtaBlock.CaretPosition).Text.TrimEnd('\r', '\n').Length;
                Check(checks, "widget-continue-target-uses-current-source-offset-and-session", ReferenceEquals(refreshedCtaEditor.Session, sharedMemo.Document)
                    && sharedMemo.Document.Source == updatedLongPreviewSource && refreshedCtaBlock.DataContext is MarkdownVisualBlock currentBlock
                    && currentBlock.SourceStart == currentCtaTarget.SourceStart && refreshedCtaCaret == NoteWidgetWindow.PreviewTextElementLimit);
                widget.Hide();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                widget.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                string sourceBeforeLongWidgetBody = sharedMemo.Document.Source;
                string longSourcePrefix = new string('가', 310) + "**굵게**" + "*기울임*" + "`코드`" + "[링크](https://example.com)" + "😀";
                string expectedLongSource = longSourcePrefix + new string('다', 10000 - longSourcePrefix.Length);
                string expectedLongDisplay = new string('가', 310) + "굵게" + "기울임" + "코드" + "링크" + "😀"
                    + new string('다', 10000 - longSourcePrefix.Length);
                long longBodyRevision = sharedMemo.Document.Revision;
                string sourceBeforeLongReplace = sharedMemo.Document.Source;
                bool editorHadPendingInput = widget.DocumentEditor?.HasUncommittedInput == true;
                bool acceptedLongWidgetBody = sharedMemo.Document.TryReplace(longBodyRevision, expectedLongSource);
                if (!acceptedLongWidgetBody || sharedMemo.Document.Source != expectedLongSource)
                {
                    string currentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sharedMemo.Document.Source)));
                    string replacementHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expectedLongSource)));
                    checks.Add($"widget-10000-replacement-diagnostic: accepted={acceptedLongWidgetBody}; currentLength={sharedMemo.Document.Source.Length}; replacementLength={expectedLongSource.Length}; currentHash={currentHash}; replacementHash={replacementHash}; revisionBefore={longBodyRevision}; revisionAfter={sharedMemo.Document.Revision}; hasConflict={sharedMemo.Document.HasConflict}; state={sharedMemo.Document.State}; widgetVisible={widget.IsVisible}; itemVisible={sharedMemo.IsVisible}; itemEditable={sharedMemo.IsEditable}; editorEditable={widget.DocumentEditor?.IsEditable}; editorHasUncommittedInput={editorHadPendingInput}; sourceBeforeLength={sourceBeforeLongReplace.Length}");
                }
                await model.FlushAsync();
                await Dispatcher.Yield(DispatcherPriority.Background);
                byte[] longWidgetBytes = File.ReadAllBytes(sharedMemo.Document.FilePath);
                Check(checks, "widget-10000-character-session-replacement-accepted", acceptedLongWidgetBody
                    && sharedMemo.Document.Source == expectedLongSource && sharedMemo.Document.Source.Length == 10000);
                Check(checks, "widget-10000-character-body-keeps-editor-lazy", widget.DocumentEditor is null
                    && !Descendants<RichTextBox>(widget).Any());
                MarkdownVisualBlock? longBodyProjection = sharedMemo.Document.Projection.FirstOrDefault(block => block.SourceStart == 0);
                Check(checks, "widget-10000-character-projection-preserves-full-text", longBodyProjection is not null
                    && longBodyProjection.Text == expectedLongDisplay);
                TextBlock? longBodyPreview = longBodyProjection is null ? null : Descendants<TextBlock>(widget)
                    .FirstOrDefault(block => ReferenceEquals(block.Tag, longBodyProjection)
                        && AutomationProperties.GetName(block) == "메모 문단 미리보기");
                string renderedLongPreview = longBodyPreview is null ? "" : string.Concat(longBodyPreview.Inlines.OfType<Run>().Select(run => run.Text));
                string renderedLongPrefix = renderedLongPreview.EndsWith(" …", StringComparison.Ordinal) ? renderedLongPreview[..^2] : "";
                Check(checks, "widget-10000-character-preview-is-a-unicode-safe-excerpt", longBodyPreview is not null
                    && renderedLongPreview.EndsWith(" …", StringComparison.Ordinal)
                    && new StringInfo(renderedLongPrefix).LengthInTextElements == NoteWidgetWindow.PreviewTextElementLimit
                    && renderedLongPrefix.EndsWith("😀", StringComparison.Ordinal));
                Check(checks, "widget-10000-character-preview-preserves-style-and-source-run-metadata", longBodyPreview is not null
                    && longBodyProjection!.Runs.Any(run => run.Bold) && longBodyProjection.Runs.Any(run => run.Italic)
                    && longBodyProjection.Runs.Any(run => run.Code) && longBodyProjection.Runs.Any(run => run.Link == "https://example.com")
                    && longBodyPreview.Inlines.OfType<Run>().Any(run => run.FontWeight == FontWeights.Bold)
                    && longBodyPreview.Inlines.OfType<Run>().Any(run => run.FontStyle == FontStyles.Italic)
                    && longBodyPreview.Inlines.OfType<Run>().Any(run => run.FontFamily.Source == "Consolas")
                    && longBodyPreview.Inlines.OfType<Run>().Any(run => run.ToolTip?.ToString() == "https://example.com")
                    && longBodyProjection.Runs.All(run => run.SourceStart >= 0 && run.SourceLength >= 0
                        && run.SourceStart <= expectedLongSource.Length - run.SourceLength));
                Check(checks, "widget-10000-character-body-persists-exact-utf8-bytes", longWidgetBytes
                    .SequenceEqual(System.Text.Encoding.UTF8.GetBytes(sharedMemo.Document.Source)));
                Check(checks, "widget-long-preview-excerpt-is-visible-in-first-viewport", longBodyPreview is not null
                    && widget.PreviewViewportHeight > 0 && longBodyPreview.TextTrimming == TextTrimming.CharacterEllipsis
                    && longBodyPreview.ActualHeight > 0
                    && longBodyPreview.GetPositionFromPoint(new Point(5, 5), true) is not null);
                Button continueReading = Descendants<Button>(widget).FirstOrDefault(button => AutomationProperties.GetName(button) == "전체 메모 계속 읽기")
                    ?? throw new InvalidOperationException("Continue reading affordance is missing for long widget content.");
                Check(checks, "widget-long-preview-continue-reading-is-accessible", widget.IsContinueReadingAvailable
                    && AutomationProperties.GetName(continueReading) == "전체 메모 계속 읽기");
                double originalLongWidgetHeight = widget.Height;
                widget.Height = 200;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                continueReading.Focus();
                await Dispatcher.Yield(DispatcherPriority.Input);
                Check(checks, "widget-small-height-keeps-preview-scroll-and-continue-keyboard-reachable", widget.PreviewViewportHeight > 0
                    && widget.PreviewScrollableHeight > 0 && widget.IsPreviewVisible && longBodyPreview is not null
                    && longBodyPreview.ActualHeight > 0 && continueReading.IsVisible
                    && continueReading.IsEnabled && continueReading.IsKeyboardFocusWithin);
                widget.Height = originalLongWidgetHeight;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                string fullBodyBeforeEditor = sharedMemo.Document.Source;
                continueReading.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, continueReading));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                NoteDocumentEditor longBodyEditor = widget.DocumentEditor ?? throw new InvalidOperationException("Continue reading did not activate the full editor.");
                RichTextBox fullBodyBlockEditor = FindBlockEditor(longBodyEditor, block => block.SourceStart == longBodyProjection!.SourceStart);
                string fullBodyEditorText = new TextRange(fullBodyBlockEditor.Document.ContentStart, fullBodyBlockEditor.Document.ContentEnd).Text.TrimEnd('\r', '\n');
                int continueCaretOffset = new TextRange(fullBodyBlockEditor.Document.ContentStart, fullBodyBlockEditor.CaretPosition).Text.TrimEnd('\r', '\n').Length;
                Check(checks, "widget-continue-reading-opens-full-same-session-document", ReferenceEquals(longBodyEditor.Session, sharedMemo.Document)
                    && sharedMemo.Document.Source == fullBodyBeforeEditor && fullBodyEditorText == longBodyProjection!.Text
                    && continueCaretOffset == renderedLongPrefix.Length);
                fullBodyBlockEditor.Focus();
                fullBodyBlockEditor.CaretPosition = NoteDocumentEditor.PositionAtDisplayOffset(fullBodyBlockEditor, 0);
                int firstBodyCaret = new TextRange(fullBodyBlockEditor.Document.ContentStart, fullBodyBlockEditor.CaretPosition).Text.TrimEnd('\r', '\n').Length;
                fullBodyBlockEditor.CaretPosition = NoteDocumentEditor.PositionAtDisplayOffset(fullBodyBlockEditor, longBodyProjection!.Text.Length);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                int lastBodyCaret = new TextRange(fullBodyBlockEditor.Document.ContentStart, fullBodyBlockEditor.CaretPosition).Text.TrimEnd('\r', '\n').Length;
                ScrollViewer fullBodyScroll = (ScrollViewer)longBodyEditor.FindName("VisualScroll");
                fullBodyScroll.ScrollToEnd();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Check(checks, "widget-full-editor-reaches-first-and-last-text-and-scroll", firstBodyCaret == 0
                    && lastBodyCaret == longBodyProjection.Text.Length && fullBodyScroll.VerticalOffset > 0);
                TextPointer firstCharStart = NoteDocumentEditor.PositionAtDisplayOffset(fullBodyBlockEditor, 0);
                TextPointer firstCharEnd = NoteDocumentEditor.PositionAtDisplayOffset(fullBodyBlockEditor, 1);
                fullBodyBlockEditor.Selection.Select(firstCharStart, firstCharEnd);
                fullBodyBlockEditor.Selection.Text = "나";
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                MarkdownVisualBlock currentLongBlock = sharedMemo.Document.Projection.First(block => block.SourceStart == longBodyProjection.SourceStart);
                RichTextBox currentLongEditor = FindBlockEditor(longBodyEditor, block => block.SourceStart == currentLongBlock.SourceStart);
                TextPointer lastCharStart = NoteDocumentEditor.PositionAtDisplayOffset(currentLongEditor, currentLongBlock.Text.Length - 1);
                TextPointer lastCharEnd = NoteDocumentEditor.PositionAtDisplayOffset(currentLongEditor, currentLongBlock.Text.Length);
                currentLongEditor.Selection.Select(lastCharStart, lastCharEnd);
                currentLongEditor.Selection.Text = "끝";
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                string expectedFirstLastEdit = "나" + expectedLongSource[1..^1] + "끝";
                await model.FlushAsync();
                Check(checks, "widget-full-editor-edits-first-and-last-character-of-long-body", sharedMemo.Document.Source == expectedFirstLastEdit);
                bool undoLastEdit = sharedMemo.Document.Undo();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                bool undoFirstEdit = sharedMemo.Document.Undo();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                bool undoLongReplacement = sharedMemo.Document.Undo();
                await model.FlushAsync();
                await Dispatcher.Yield(DispatcherPriority.Background);
                Check(checks, "widget-large-body-undo-restores-exact-source", undoLastEdit && undoFirstEdit && undoLongReplacement
                    && sharedMemo.Document.Source == sourceBeforeLongWidgetBody);
            }
            finally
            {
                widget.Release();
                sharedMemo.IsVisible = false;
                await model.FlushAsync();
            }
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(checks, "closed-widget-releases-session-observer", widget.DocumentEditor is not { } closedWidgetEditor
                || !IsSessionSubscribed(sharedMemo.Document, closedWidgetEditor));
            Check(checks, "widget-probe-preserves-hidden-intent-after-cleanup", !sharedMemo.IsVisible);
            model.NewMemoCommand.Execute(null);
            MemoListItemViewModel emptyWidgetMemo = model.SelectedMemo ?? throw new InvalidOperationException("New empty note was not selected.");
            emptyWidgetMemo.IsVisible = true;
            NoteWidgetWindow emptyWidget = new(emptyWidgetMemo);
            emptyWidget.Show();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(checks, "empty-widget-has-keyboard-entry-hint-without-richtextboxes", emptyWidget.DocumentEditor is null
                    && !Descendants<RichTextBox>(emptyWidget).Any()
                    && Descendants<TextBlock>(emptyWidget).Any(block => AutomationProperties.GetName(block) == "새 메모 본문 편집 시작"));
                TextBlock emptyHint = Descendants<TextBlock>(emptyWidget).First(block => AutomationProperties.GetName(block) == "새 메모 본문 편집 시작");
                emptyHint.Focus();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(checks, "empty-widget-lazily-focuses-native-text-entry", emptyWidget.DocumentEditor is not null
                    && Descendants<TextBox>(emptyWidget.DocumentEditor).Any(box => AutomationProperties.GetName(box) == "새 Markdown 메모 본문 입력"));
            }
            finally
            {
                emptyWidget.Release();
                emptyWidgetMemo.IsVisible = false;
                await model.FlushAsync();
            }
            ListBox list = (ListBox)window.FindName("MemoList");
            list.SelectedItem = model.AllMemos[1];
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(checks, "actual-list-selection-binding", ReferenceEquals(model.SelectedMemo, model.AllMemos[1]));
            documentEditor = (NoteDocumentEditor)window.FindName("DocumentEditor");
            Check(checks, "hidden-editor-remains-editable", !model.SelectedMemo.IsVisible && documentEditor.IsEditable);
            RichTextBox secondNoteEditor = FindBlockEditor(documentEditor, b => b.Kind == "Paragraph" && b.Text.Contains("오늘 꼭 남기고", StringComparison.Ordinal));
            EditRun(secondNoteEditor, "꼭", "정말");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CheckNoVisualFallback(checks, documentEditor, "second-hidden-note-edit");
            Check(checks, "manager-edit-does-not-show-widget", !model.SelectedMemo.IsVisible && model.SelectedMemo.Document.Source.Contains("오늘 정말 남기고", StringComparison.Ordinal));
            TextBox search = (TextBox)window.FindName("SearchBox");
            TextBlock placeholder = (TextBlock)window.FindName("SearchPlaceholder");
            Check(checks, "search-placeholder-visible-and-semantic", placeholder.IsVisible && placeholder.Text.Contains("제목", StringComparison.Ordinal) && placeholder.Text.Contains("본문", StringComparison.Ordinal) && !placeholder.IsHitTestVisible);
            for (int i = 0; i < 30; i++)
            {
                search.SetCurrentValue(TextBox.TextProperty, ""); await Task.Delay(130);
                long began = Stopwatch.GetTimestamp();
                search.SetCurrentValue(TextBox.TextProperty, "프로젝트 아이디어");
                while (model.SearchText != "프로젝트 아이디어" && Stopwatch.GetElapsedTime(began).TotalMilliseconds < 1000) await Task.Delay(5);
                searchDurations.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                if (model.VisibleMemos.Count != 1) throw new InvalidOperationException("Actual search binding result mismatch");
            }
            searchDurations.Sort();
            Check(checks, "actual-search-binding-delay-included-p95-under-200ms", searchDurations[28] <= 200);
            Check(checks, "search-placeholder-hides-on-input", !placeholder.IsVisible);
            search.SetCurrentValue(TextBox.TextProperty, ""); await Task.Delay(140);
            model.SelectedMemo = model.AllMemos[0];
            model.SelectedMemo.IsPinned = true;
            await model.FlushAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            FrameworkElement sidebarPane = (FrameworkElement)window.FindName("SidebarPane");
            FrameworkElement listPane = (FrameworkElement)window.FindName("ListPane");
            FrameworkElement wideEditorPane = (FrameworkElement)window.FindName("EditorPane");
            Check(checks, "wide-three-pane-window-rendered", sidebarPane.IsVisible && listPane.IsVisible && wideEditorPane.IsVisible);
            Check(checks, "wide-editor-dominates-narrow-navigation-and-list", sidebarPane.ActualWidth <= 210 && listPane.ActualWidth <= 320 && wideEditorPane.ActualWidth > sidebarPane.ActualWidth + listPane.ActualWidth);
            Button globalActions = (Button)window.FindName("GlobalActionsButton");
            ContextMenu globalMenu = globalActions.ContextMenu ?? throw new InvalidOperationException("Global actions menu is missing.");
            globalMenu.PlacementTarget = globalActions;
            globalMenu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(checks, "global-overflow-groups-backup-and-startup-settings", ReferenceEquals(globalMenu.DataContext, model)
                && globalMenu.Items.OfType<MenuItem>().Any(item => item.Command == model.ExportCommand)
                && globalMenu.Items.OfType<MenuItem>().Any(item => item.Command == model.ImportCommand)
                && globalMenu.Items.OfType<MenuItem>().Any(item => item.Command == model.ResetAllPlacementsCommand)
                && globalMenu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString()?.Contains("자동 시작", StringComparison.Ordinal) == true));
            MenuItem helpMenuItem = globalMenu.Items.OfType<MenuItem>().First(item => AutomationProperties.GetName(item) == "Notedex 사용 안내와 PDF 설명 열기");
            bool helpRequested = false;
            EventHandler helpHandler = (_, _) => helpRequested = true;
            window.HelpRequested += helpHandler;
            helpMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, helpMenuItem));
            window.HelpRequested -= helpHandler;
            Check(checks, "manager-help-menu-raises-view-owned-help-request", helpRequested
                && helpMenuItem.Header?.ToString() == "도움말 · PDF 설명");
            globalMenu.IsOpen = false;
            string preferencePath = Path.Combine(profile, "user-preferences.json");
            Check(checks, "automatic-guide-starts-from-empty-preference-profile", !File.Exists(preferencePath));
            UserGuideWindow automaticGuide = Application.Current.Windows.OfType<UserGuideWindow>()
                .SingleOrDefault(guide => guide.IsVisible)
                ?? throw new InvalidOperationException("The app-owned automatic guide did not open at startup.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Image[] automaticGuidePages = Descendants<Image>(automaticGuide).ToArray();
            ScrollViewer automaticGuideScroll = Descendants<ScrollViewer>(automaticGuide)
                .Single(viewer => AutomationProperties.GetName(viewer) == "사용 안내 페이지");
            Check(checks, "first-run-guide-shows-first-page-before-ready-with-bounded-decoding", automaticGuidePages.Length == 5
                && IntersectsViewport(automaticGuidePages[0], automaticGuideScroll)
                && automaticGuidePages[0].Source is BitmapSource firstRunFirstPage && firstRunFirstPage.PixelWidth > 0
                && automaticGuide.LoadedGuidePageCount <= 3);
            CheckBox automaticPreference = Descendants<CheckBox>(automaticGuide).Single(box => AutomationProperties.GetName(box) == "다시 보지 않기");
            Check(checks, "app-startup-guide-exposes-unchecked-do-not-show-preference", automaticGuide.IsDoNotShowAgainOptionVisible
                && !automaticGuide.DoNotShowAgain && automaticPreference.IsChecked != true);
            automaticPreference.IsChecked = true;
            bool automaticPreferenceValue = automaticGuide.DoNotShowAgain;
            automaticGuide.Close();
            byte[] automaticPreferenceBytes = File.ReadAllBytes(preferencePath);
            bool automaticPreferencePersisted = new UserPreferencesStore(profile).Load().DoNotShowUserGuide;
            Check(checks, "app-owned-automatic-guide-close-persists-checked-preference", automaticPreferenceValue
                && automaticPreferencePersisted && automaticPreferenceBytes.Length > 0);
            UserGuideWindow manualGuide = null!;
            bool manualGuidePagesPassed = false;
            try
            {
                globalMenu.IsOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                helpMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, helpMenuItem));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                manualGuide = Application.Current.Windows.OfType<UserGuideWindow>()
                    .Single(guide => guide.IsVisible);
                manualGuide.Width = 420;
                manualGuide.Height = 480;
                CheckBox[] visiblePreferenceBoxes = Descendants<CheckBox>(manualGuide)
                    .Where(box => box.IsVisible && AutomationProperties.GetName(box) == "다시 보지 않기").ToArray();
                globalMenu.IsOpen = true;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                helpMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, helpMenuItem));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                UserGuideWindow[] visibleGuides = Application.Current.Windows.OfType<UserGuideWindow>()
                    .Where(guide => guide.IsVisible).ToArray();
                Check(checks, "actual-help-menu-opens-manual-guide-singleton-with-hidden-preference", visibleGuides.Length == 1
                    && ReferenceEquals(visibleGuides[0], manualGuide) && visiblePreferenceBoxes.Length == 0
                    && !manualGuide.IsDoNotShowAgainOptionVisible && !manualGuide.DoNotShowAgain);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Image[] manualGuidePages = Descendants<Image>(manualGuide).ToArray();
                ScrollViewer manualGuideScroll = Descendants<ScrollViewer>(manualGuide)
                    .Single(viewer => AutomationProperties.GetName(viewer) == "사용 안내 페이지");
                manualGuideScroll.ScrollToTop();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                bool firstPageVisible = manualGuidePages.Length == 5 && IntersectsViewport(manualGuidePages[0], manualGuideScroll);
                bool allPagesLoadOnViewportWithOriginalOrderAndRatio = manualGuidePages.Length == 5;
                for (int pageIndex = 0; pageIndex < manualGuidePages.Length; pageIndex++)
                {
                    Image page = manualGuidePages[pageIndex];
                    Point pageOrigin = page.TransformToAncestor(manualGuideScroll).Transform(new Point(0, 0));
                    manualGuideScroll.ScrollToVerticalOffset(Math.Max(0, manualGuideScroll.VerticalOffset + pageOrigin.Y - 8));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    bool pageLoadedInViewport = page.Source is BitmapImage bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0
                        && bitmap.UriSource.OriginalString.EndsWith(manualGuide.GuidePageResourcePath(pageIndex), StringComparison.OrdinalIgnoreCase)
                        && IntersectsViewport(page, manualGuideScroll)
                        && page.ActualWidth > 0 && page.ActualHeight > 0
                        && Math.Abs(page.ActualHeight / page.ActualWidth - 1123d / 794d) < 0.005
                        && manualGuide.LoadedGuidePageCount <= 3;
                    Check(checks, $"manual-guide-lazy-page-{pageIndex + 1}-loads-in-order-at-original-ratio", pageLoadedInViewport);
                    allPagesLoadOnViewportWithOriginalOrderAndRatio &= pageLoadedInViewport;
                }
                manualGuideScroll.ScrollToEnd();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                bool lastPageVisible = manualGuidePages.Length == 5 && IntersectsViewport(manualGuidePages[^1], manualGuideScroll);
                bool distantFirstPageReleased = manualGuidePages[0].Source is null;
                manualGuideScroll.ScrollToTop();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                bool firstPageRestoredOnReturn = manualGuidePages[0].Source is BitmapImage restoredFirstPage
                    && restoredFirstPage.UriSource.OriginalString.EndsWith(manualGuide.GuidePageResourcePath(0), StringComparison.OrdinalIgnoreCase)
                    && IntersectsViewport(manualGuidePages[0], manualGuideScroll)
                    && manualGuide.LoadedGuidePageCount <= 3;
                Check(checks, "manual-guide-shows-five-ordered-pages-at-small-window-size", manualGuidePages.Length == 5
                    && allPagesLoadOnViewportWithOriginalOrderAndRatio && distantFirstPageReleased && firstPageRestoredOnReturn
                    && manualGuide.GuideViewportWidth > 0 && manualGuide.GuideScrollableHeight > 0
                    && manualGuide.GuideViewportHeight > 0 && manualGuidePages.All(page => page.ActualWidth <= manualGuide.GuideViewportWidth)
                    && firstPageVisible && lastPageVisible);
                manualGuidePagesPassed = true;
            }
            finally
            {
                manualGuide?.Close();
                Check(checks, "manual-guide-close-leaves-auto-show-preference-bytes-unchanged", automaticPreferenceBytes
                    .SequenceEqual(File.ReadAllBytes(preferencePath)) && new UserPreferencesStore(profile).Load().DoNotShowUserGuide);
            }
            Check(checks, "manual-guide-close-keeps-auto-preference-disabled", manualGuidePagesPassed && !manualGuide.DoNotShowAgain);
            Button memoActions = Descendants<Button>(window).Single(button => AutomationProperties.GetName(button) == "선택 메모 명령");
            ContextMenu memoMenu = memoActions.ContextMenu ?? throw new InvalidOperationException("Memo actions menu is missing.");
            memoMenu.PlacementTarget = memoActions;
            memoMenu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(checks, "memo-overflow-groups-priority-widget-and-recovery", ReferenceEquals(memoMenu.DataContext, model)
                && memoMenu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "중요도"
                    && item.Items.OfType<MenuItem>().Any(option => option.Command == model.SetPriorityCommand))
                && memoMenu.Items.OfType<MenuItem>().Any(item => item.Command == model.ShowMemoCommand)
                && memoMenu.Items.OfType<MenuItem>().Any(item => item.Command == model.RestoreMemoCommand)
                && memoMenu.Items.OfType<MenuItem>().Any(item => item.Command == model.TogglePlacementLockCommand)
                && memoMenu.Items.OfType<MenuItem>().Any(item => item.Command == model.ResetPlacementCommand)
                && memoMenu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "메모 색상"
                    && item.Items.OfType<MenuItem>().Any(option => option.Command == model.SetNoteColorCommand))
                && memoMenu.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "본문 글자 크기"
                    && item.Items.OfType<MenuItem>().Any(option => option.Command == model.SetNoteFontSizeCommand)));
            memoMenu.IsOpen = false;
            MemoListItemViewModel appearanceTarget = model.SelectedMemo ?? throw new InvalidOperationException("A note must be selected for appearance checks.");
            model.SetNoteColorCommand.Execute("blue");
            model.SetNoteFontSizeCommand.Execute("20");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            documentEditor = (NoteDocumentEditor)window.FindName("DocumentEditor");
            Check(checks, "per-note-color-and-type-size-bind-to-editor", appearanceTarget.ColorPreset == "blue"
                && appearanceTarget.MemoFontSize == 20 && documentEditor.MemoFontSize == 20
                && documentEditor.HeadingFontSize == 25
                && ((SolidColorBrush)documentEditor.MemoBackground).Color == ((SolidColorBrush)appearanceTarget.MemoBackground).Color);
            Render(window, Path.Combine(profile, "notes-appearance.png"));
            model.SetNoteColorCommand.Execute("default");
            model.SetNoteFontSizeCommand.Execute("16");
            await model.FlushAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Expander metadataDisclosure = (Expander)window.FindName("MetadataDisclosure");
            Check(checks, "metadata-details-collapsed-in-default-reading-view", !metadataDisclosure.IsExpanded);
            Check(checks, "collapsed-metadata-summary-is-single-line-and-tooltip-complete", MetadataSummaryIsSingleLine(metadataDisclosure, model));
            metadataDisclosure.IsExpanded = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(checks, "wide-folder-tag-controls-within-editor", ToolbarWithinEditor(window));
            Check(checks, "wide-long-tags-ellipsis-and-full-tooltip", LongTagsPreserved(window, model));
            RichTextBox focusableBlock = Descendants<RichTextBox>(documentEditor).First(box => box.IsEnabled);
            Keyboard.Focus(focusableBlock);
            await Dispatcher.Yield(DispatcherPriority.Input);
            Check(checks, "visual-editor-keyboard-focus-is-visible", focusableBlock.IsKeyboardFocusWithin
                && ((SolidColorBrush)focusableBlock.BorderBrush).Color == ((SolidColorBrush)focusableBlock.FindResource("FocusBrush")).Color
                && focusableBlock.BorderThickness.Left == 1);
            ComboBox focusableSort = Descendants<ComboBox>(window).Single(combo => combo.IsEnabled && combo.IsVisible
                && AutomationProperties.GetName(combo) == "메모 정렬 방식");
            Keyboard.Focus(focusableSort);
            await Dispatcher.Yield(DispatcherPriority.Input);
            ToggleButton? sortToggle = focusableSort.Template.FindName("DropDownToggle", focusableSort) as ToggleButton;
            Border? sortSurface = sortToggle?.Template.FindName("Surface", sortToggle) as Border;
            Check(checks, "combo-box-keyboard-focus-is-visible", focusableSort.IsKeyboardFocusWithin && sortSurface is not null
                && ((SolidColorBrush)sortSurface.BorderBrush).Color == ((SolidColorBrush)focusableSort.FindResource("FocusBrush")).Color
                && sortSurface.BorderThickness.Left == 2);
            Render(window, Path.Combine(profile, "notes-tags-long.png"));
            metadataDisclosure.IsExpanded = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            model.SelectedMemo!.TagsText = "아이디어, 일상";
            foreach (ComboBox combo in Descendants<ComboBox>(window)) combo.IsDropDownOpen = false;
            await model.FlushAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Render(window, Path.Combine(profile, "notes-manager-wide.png"));
            Expander startupSettings = (Expander)window.FindName("StartupSettingsDisclosure");
            startupSettings.IsExpanded = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            CheckBox startupCheck = Descendants<CheckBox>(window).Single(c => System.Windows.Data.BindingOperations.GetBindingExpression(c, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == nameof(NotesManagerViewModel.StartWithWindows));
            Check(checks, "isolated-startup-setting-disabled-and-off", !startupCheck.IsEnabled && startupCheck.IsChecked != true && !model.CanConfigureStartup);
            Button refreshStartup = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, model.RefreshStartupCommand));
            Check(checks, "isolated-startup-refresh-disabled", !refreshStartup.IsEnabled);
            TextBlock startupStatus = Descendants<TextBlock>(window).Single(t => System.Windows.Data.BindingOperations.GetBindingExpression(t, TextBlock.TextProperty)?.ParentBinding.Path.Path == nameof(NotesManagerViewModel.StartupStateMessage));
            Check(checks, "startup-isolation-explanation-visible", startupStatus.IsVisible && startupStatus.Text.Contains("등록 변경 없음", StringComparison.Ordinal));
            ScrollViewer navigationScroll = Descendants<ScrollViewer>((DependencyObject)window.FindName("SidebarPane")).First();
            navigationScroll.ScrollToEnd(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Render(window, Path.Combine(profile, "notes-settings.png"));
            startupSettings.IsExpanded = false;
            navigationScroll.ScrollToTop(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            model.IsMarkdownPreview = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Render(window, Path.Combine(profile, "notes-markdown.png"));
            model.IsMarkdownPreview = false;
            window.Width = 640; window.Height = 720;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ListBox compactList = (ListBox)window.FindName("MemoList");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            // The first row is already selected earlier in this fixture; choose a
            // different realized row so the actual SelectionChanged route is exercised.
            compactList.SelectedIndex = compactList.SelectedIndex == 0 ? 1 : 0;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(checks, "reloaded-compact-editor-observes-current-session", IsSessionSubscribed(model.SelectedMemo!.Document, documentEditor));
            Check(checks, "compact-list-selection-opens-editor", ((FrameworkElement)window.FindName("EditorPane")).IsVisible
                && !((FrameworkElement)window.FindName("ListPane")).IsVisible);
            Button compactBack = (Button)window.FindName("BackToListButton");
            compactBack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, compactBack));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(checks, "collapsed-compact-editor-retains-session-observer", IsSessionSubscribed(model.SelectedMemo!.Document, documentEditor));
            Check(checks, "compact-editor-back-returns-to-list", ((FrameworkElement)window.FindName("ListPane")).IsVisible
                && !((FrameworkElement)window.FindName("EditorPane")).IsVisible);
            Button newButton = Descendants<Button>(window).First(b => ReferenceEquals(b.Command, model.NewMemoCommand));
            IInvokeProvider invoke = (IInvokeProvider)new ButtonAutomationPeer(newButton).GetPattern(PatternInterface.Invoke);
            int before = model.AllMemos.Count;
            invoke.Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(checks, "automation-invoke-creates-real-note", model.AllMemos.Count == before + 1);
            model.SelectedMemo!.Title = "좁은 화면의 메모";
            model.SelectedMemo.TagsText = longTags;
            model.SelectedMemo.Body = "이 창에서는 탐색과 목록, 편집 화면을 전환합니다.\n\n키보드로도 메모를 찾고 정리할 수 있습니다.";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            FrameworkElement editorPane = (FrameworkElement)window.FindName("EditorPane");
            Check(checks, "compact-editor-visible-and-bounded", editorPane.IsVisible && editorPane.ActualWidth <= window.ActualWidth);
            Expander compactMetadata = (Expander)window.FindName("MetadataDisclosure");
            compactMetadata.IsExpanded = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(checks, "compact-folder-tag-controls-within-editor", ToolbarWithinEditor(window));
            Check(checks, "compact-long-tags-ellipsis-and-full-tooltip", LongTagsPreserved(window, model));
            compactMetadata.IsExpanded = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            NoteDocumentEditor compactDocumentEditor = (NoteDocumentEditor)window.FindName("DocumentEditor");
            FrameworkElement compactBodyViewport = (FrameworkElement)compactDocumentEditor.FindName("VisualScroll");
            compactBodyEditorHeight = compactBodyViewport.ActualHeight;
            Check(checks, "compact-body-editor-keeps-usable-height", compactBodyViewport.IsVisible && compactBodyViewport.ActualHeight >= 80);
            Button saveButton = Descendants<Button>(editorPane).Single(b => string.Equals(b.Content as string, "저장", StringComparison.Ordinal));
            Rect saveBounds = saveButton.TransformToAncestor(editorPane).TransformBounds(new Rect(0, 0, saveButton.ActualWidth, saveButton.ActualHeight));
            Check(checks, "compact-save-footer-stays-within-editor", saveBounds.Bottom <= editorPane.ActualHeight + 1);
            Rect bodyBounds = compactBodyViewport.TransformToAncestor(editorPane).TransformBounds(new Rect(0, 0, compactBodyViewport.ActualWidth, compactBodyViewport.ActualHeight));
            Check(checks, "compact-body-does-not-overlap-save-footer", bodyBounds.Bottom <= saveBounds.Top - 4);
            FrameworkElement root = (FrameworkElement)window.Content;
            TextBlock help = Descendants<TextBlock>(root).Single(t => t.Text.StartsWith("고정은 목록 접근", StringComparison.Ordinal));
            Check(checks, "long-help-collapsed-from-note-workspace", !help.IsVisible);
            model.SelectedMemo.TagsText = "일상";
            foreach (ComboBox combo in Descendants<ComboBox>(window)) combo.IsDropDownOpen = false;
            await model.FlushAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Render(window, Path.Combine(profile, "notes-manager-compact.png"));
            WindowAutomationPeer peer = new(window);
            Check(checks, "window-accessibility-tree-present", peer.GetChildren() is { Count: > 0 });
            RichTextBox compactEditor = FindBlockEditor(compactDocumentEditor, _ => true);
            compactEditor.Focus();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(checks, "editor-keyboard-focusable", compactEditor.Focusable && compactEditor.IsKeyboardFocusWithin);
            window.Width = 1440; window.Height = 900;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            foreach (int count in new[] { 50, 100 }) largeSearch.Add(await MeasureLargeSearchAsync(window, model, profile, count, checks));
            model.NewMemoCommand.Execute(null);
            MemoListItemViewModel overflowMemo = model.SelectedMemo!;
            overflowMemo.Title = "시각 편집 보존 경계";
            overflowMemo.Body = "초기 내용";
            await model.FlushAsync();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            NoteDocumentEditor overflowEditor = (NoteDocumentEditor)window.FindName("DocumentEditor");
            RichTextBox overflowBlock = FindBlockEditor(overflowEditor, b => b.Text.Contains("초기 내용", StringComparison.Ordinal));
            string originalSource = overflowMemo.Document.Source;
            string oversizedDisplay = new('가', 10001);
            SelectRun(overflowBlock, "초기 내용");
            overflowBlock.Selection.Text = oversizedDisplay;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            TextBox pendingSourceEditor = (TextBox)overflowEditor.FindName("MarkdownSourceEditor");
            Button saveCopyButton = (Button)overflowEditor.FindName("SaveCopyButton");
            Check(checks, "oversized-visual-input-retained-for-save-copy", overflowMemo.Document.Source == originalSource
                && pendingSourceEditor.Text.Contains(oversizedDisplay, StringComparison.Ordinal)
                && saveCopyButton.Visibility == Visibility.Visible);
            await model.FlushAsync();
            result = "PASS";
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            try { Render(window, Path.Combine(profile, "notes-ui-failure.png")); } catch (Exception renderError) when (renderError is IOException or InvalidOperationException) { }
        }
        finally
        {
            string output = Path.Combine(profile, "ui-results.json");
            File.WriteAllText(output, JsonSerializer.Serialize(new { Result = result, Checks = checks, Error = error, SearchSamples = searchDurations.Count, SearchP95Milliseconds = searchDurations.Count == 30 ? searchDurations[28] : (double?)null, LargeSearch = largeSearch, CompactBodyEditorHeight = compactBodyEditorHeight, PhysicalKeyboardImeDpiTested = false, CaptureKind = "own WPF rendered content from actual interactive HWND" }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    /// <summary>name: MeasureLargeSearchAsync; desc: 실제 창에 50/100개 10000자 합성 모델을 주입해 UI 지연 포함 검색을 계측하고 원래 모델을 복원한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 관리 UI 성능 수용.</summary>
    private static async Task<object> MeasureLargeSearchAsync(NotesManagerWindow window, NotesManagerViewModel original, string profile, int count, List<string> checks)
    {
        NotesRepository repository = new(Path.Combine(profile, "search-fixture-" + count));
        await repository.LoadAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repository.SaveAsync(new(1, 1, Enumerable.Range(0, count).Select(i => new NoteRecord(Guid.NewGuid().ToString(), "합성 메모 " + i, new string('가', 10000 - ("needle-" + i).Length) + "needle-" + i, now, now, null, [], false, 0, false, false)).ToArray(), []));
        using NotesManagerViewModel fixture = new(repository, _ => false);
        await fixture.InitializeAsync();
        string before = JsonSerializer.Serialize(original.Snapshot());
        List<double> durations = [];
        try
        {
            window.DataContext = fixture;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            TextBox search = (TextBox)window.FindName("SearchBox");
            int expected = Enumerable.Range(0, count).Count(i => ("needle-" + i).Contains("needle-7", StringComparison.Ordinal));
            for (int i = 0; i < 30; i++)
            {
                search.SetCurrentValue(TextBox.TextProperty, ""); await Task.Delay(130);
                long began = Stopwatch.GetTimestamp(); search.SetCurrentValue(TextBox.TextProperty, "needle-7");
                while (fixture.SearchText != "needle-7" && Stopwatch.GetElapsedTime(began).TotalMilliseconds < 1000) await Task.Delay(5);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                durations.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                if (fixture.VisibleMemos.Count != expected) throw new InvalidOperationException("Large fixture UI search mismatch");
            }
            double first = durations[0]; durations.Sort();
            Check(checks, $"actual-{count}-by-10000-body-search-delay-included-p95-under-200ms", durations[28] <= 200);
            return new { Count = count, BodyUtf16Length = 10000, Samples = 30, FirstMilliseconds = first, P95Milliseconds = durations[28], MaxMilliseconds = durations[^1], UiBindingDelayIncluded = true, IncludesColdAppLaunch = false };
        }
        finally
        {
            window.DataContext = original;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(checks, $"{count}-fixture-switch-preserves-original-notes", before == JsonSerializer.Serialize(original.Snapshot()));
        }
    }
    /// <summary>name: IsSessionSubscribed; desc: 편집기가 세션 이벤트를 로드 구간에서만 보유하는지 검증한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 WPF view lifecycle probe.</summary>
    private static bool IsSessionSubscribed(MarkdownDocumentSession session, NoteDocumentEditor editor)
    {
        FieldInfo? eventStorage = typeof(MarkdownDocumentSession).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(field => field.FieldType == typeof(System.ComponentModel.PropertyChangedEventHandler));
        return eventStorage?.GetValue(session) is System.ComponentModel.PropertyChangedEventHandler handlers
            && handlers.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, editor));
    }

    /// <summary>name: FindBlockEditor; desc: 실제 ItemsControl 생성 문단에서 조건에 맞는 편집기를 찾는다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 시각 UI probe.</summary>
    private static RichTextBox FindBlockEditor(NoteDocumentEditor editor, Func<MarkdownVisualBlock, bool> predicate)
        => Descendants<RichTextBox>(editor).FirstOrDefault(box => box.DataContext is MarkdownVisualBlock block && predicate(block))
           ?? throw new InvalidOperationException("Expected visual Markdown block editor was not created.");

    private static bool IntersectsViewport(FrameworkElement element, ScrollViewer viewer)
    {
        Rect elementBounds = element.TransformToAncestor(viewer).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
        elementBounds.Intersect(new Rect(0, 0, viewer.ViewportWidth, viewer.ViewportHeight));
        return !elementBounds.IsEmpty;
    }

    private static double LeftInAncestor(FrameworkElement element, Visual ancestor)
        => element.TransformToAncestor(ancestor).Transform(new Point(0, 0)).X;

    private static T FindTaggedVisual<T>(FrameworkElement root, MarkdownVisualBlock block, string role) where T : FrameworkElement
        => Descendants<T>(root).FirstOrDefault(element => ReferenceEquals(element.Tag, block))
           ?? throw MissingTaggedVisual<T>(root, block, role);

    private static MarkdownVisualBlock RequireBlock(IReadOnlyList<MarkdownVisualBlock> blocks,
        Func<MarkdownVisualBlock, bool> predicate, string role)
        => blocks.FirstOrDefault(predicate)
           ?? throw new InvalidOperationException($"Missing {role}. Projection: "
               + string.Join(" | ", blocks.Select(DescribeBlock)));

    private static InvalidOperationException MissingTaggedVisual<T>(FrameworkElement root, MarkdownVisualBlock block, string role)
        where T : FrameworkElement
    {
        string candidates = string.Join("; ", Descendants<T>(root).Take(16).Select(element =>
            $"{element.GetType().Name}(visible={element.IsVisible},tag={DescribeTag(element.Tag)})"));
        return new InvalidOperationException($"Missing {role}: expected {typeof(T).Name} tagged to "
            + $"{DescribeBlock(block)}. Visual candidates: {(candidates.Length == 0 ? "<none>" : candidates)}");
    }

    private static string DescribeListRendering(NoteDocumentEditor editor, IReadOnlyList<MarkdownVisualBlock> blocks)
    {
        string projection = string.Join(" | ", blocks.Select(DescribeBlock));
        string visuals = string.Join(" | ", Descendants<FrameworkElement>(editor)
            .Where(element => element.Tag is MarkdownVisualBlock)
            .Take(48)
            .Select(element => $"{element.GetType().Name}(visible={element.IsVisible},tag={DescribeTag(element.Tag)})"));
        return $"projection=[{projection}]; taggedVisuals=[{(visuals.Length == 0 ? "<none>" : visuals)}]";
    }

    private static string DescribeTag(object? tag)
        => tag is MarkdownVisualBlock block ? DescribeBlock(block) : tag?.GetType().Name ?? "<null>";

    private static string DescribeBlock(MarkdownVisualBlock block)
        => $"start={block.SourceStart},kind={block.Kind},depth={block.ListDepth},columns={block.IndentationColumns},marker='{block.ListMarker}',text='{block.Text}'";

    /// <summary>name: SelectRun; desc: 문단 표시 run 안의 텍스트 범위를 실제 키보드 선택 상태로 지정한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 WPF 편집 probe.</summary>
    private static void SelectRun(RichTextBox editor, string text)
    {
        foreach (Run run in editor.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>())
        {
            int start = run.Text.IndexOf(text, StringComparison.Ordinal);
            if (start < 0) continue;
            TextPointer selectionStart = run.ContentStart.GetPositionAtOffset(start, LogicalDirection.Forward)
                ?? throw new InvalidOperationException("Could not locate run selection start.");
            TextPointer selectionEnd = run.ContentStart.GetPositionAtOffset(start + text.Length, LogicalDirection.Forward)
                ?? throw new InvalidOperationException("Could not locate run selection end.");
            editor.Focus();
            editor.Selection.Select(selectionStart, selectionEnd);
            return;
        }
        throw new InvalidOperationException("Expected display text was not found: " + text);
    }

    /// <summary>name: EditRun; desc: 시각 표현에서 선택 문자열을 바꾸고 편집 경로를 실제 TextChanged로 통과시킨다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 run 편집 probe.</summary>
    private static void EditRun(RichTextBox editor, string text, string replacement)
    { SelectRun(editor, text); editor.Selection.Text = replacement; }

    /// <summary>name: CheckVisualRuns; desc: Markdown 메타데이터와 RichTextBox의 렌더된 강조 스타일을 대조한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 run 렌더 probe.</summary>
    private static void CheckVisualRuns(List<string> checks, NoteDocumentEditor editor)
    {
        MarkdownVisualBlock boldBlock = editor.Session!.Projection.First(b => b.Text.Contains("중요한 생각", StringComparison.Ordinal));
        MarkdownDisplayRun bold = boldBlock.Runs.First(r => r.Text.Contains("중요한 생각", StringComparison.Ordinal));
        RichTextBox boldBox = FindBlockEditor(editor, b => ReferenceEquals(b, boldBlock));
        Run boldRun = boldBox.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().First(r => r.Text.Contains("중요한 생각", StringComparison.Ordinal));
        Check(checks, "markdown-bold-run-rendered", bold.Bold && boldRun.FontWeight == FontWeights.Bold);

        MarkdownVisualBlock codeBlock = editor.Session.Projection.First(b => b.Text.Contains("작은 단서", StringComparison.Ordinal));
        MarkdownDisplayRun code = codeBlock.Runs.First(r => r.Text.Contains("작은 단서", StringComparison.Ordinal));
        RichTextBox codeBox = FindBlockEditor(editor, b => ReferenceEquals(b, codeBlock));
        Run codeRun = codeBox.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().First(r => r.Text.Contains("작은 단서", StringComparison.Ordinal));
        Check(checks, "markdown-code-run-rendered", code.Code && codeRun.FontFamily.Source == "Consolas");
    }

    /// <summary>name: FindFormattingButton; desc: 지정한 기능 태그의 실제 서식 버튼을 편집기 트리에서 찾는다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 도구막대 probe.</summary>
    private static Button FindFormattingButton(NoteDocumentEditor editor, string tag)
        => Descendants<Button>(editor).FirstOrDefault(button => string.Equals(button.Tag as string, tag, StringComparison.Ordinal))
           ?? throw new InvalidOperationException("Formatting button was not found: " + tag);

    /// <summary>name: CheckCaretAfterEdit; desc: 바인딩 revision 재구성 후 caret가 표시 문자열 offset에 복원됐는지 확인한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 caret 복원 probe.</summary>
    private static void CheckCaretAfterEdit(List<string> checks, RichTextBox editor, int expectedOffset)
    {
        string text = ReadEditorText(editor);
        int actualOffset = new TextRange(editor.Document.ContentStart, editor.Selection.Start).Text.TrimEnd('\r', '\n').Length;
        bool exact = actualOffset == expectedOffset;
        bool normalizedAtInlineBoundary = actualOffset == expectedOffset + 1 && editor.DataContext is MarkdownVisualBlock;
        if (!editor.Selection.IsEmpty || !(exact || normalizedAtInlineBoundary))
            throw new InvalidOperationException($"Caret restore mismatch: expected {expectedOffset}, actual {actualOffset}, selectionLength {editor.Selection.Text.Length}, textLength {text.Length}.");
        checks.Add(exact ? "visual-edit-restores-caret-offset-" + expectedOffset : "visual-edit-caret-normalized-at-inline-boundary");
    }

    private static string ReadEditorText(RichTextBox editor)
        => new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.TrimEnd('\r', '\n');

    private static void CheckNoVisualFallback(List<string> checks, NoteDocumentEditor editor, string stage)
    {
        TextBlock status = (TextBlock)editor.FindName("EditStatus");
        if (status.Visibility == Visibility.Visible)
        {
            string editors = string.Join(" | ", Descendants<RichTextBox>(editor).Where(box => box.DataContext is MarkdownVisualBlock)
                .Select(box => $"start={((MarkdownVisualBlock)box.DataContext).SourceStart}, tag={box.Tag}"));
            throw new InvalidOperationException($"Visual edit fallback after {stage}: {status.Text}; revision={editor.Session?.Revision}; blocks={editors}");
        }
        checks.Add("visual-edit-stays-synchronized-after-" + stage);
    }

    /// <summary>name: Check; desc: 실제 상태를 명시 기준으로 확인한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 UI 판정.</summary>
    private static void Check(List<string> checks, string name, bool success) { if (!success) throw new InvalidOperationException(name); checks.Add(name); }
    private static void CheckPreviewDetail(List<string> checks, List<string> failures, string name, bool success)
    {
        checks.Add((success ? "PASS: " : "FAIL: ") + name);
        if (!success) failures.Add(name);
    }
    /// <summary>name: ToolbarWithinEditor; desc: 폴더/태그 행과 실제 입력/버튼의 배치 경계를 확인한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 툴바 overflow 회귀.</summary>
    private static bool ToolbarWithinEditor(Window window)
    {
        FrameworkElement pane = (FrameworkElement)window.FindName("EditorPane");
        foreach (string name in new[] { "FolderTagSelectionRow", "TagEditingRow" })
        {
            FrameworkElement row = (FrameworkElement)window.FindName(name);
            if (!row.IsVisible) return false;
            foreach (FrameworkElement control in Descendants<FrameworkElement>(row).Where(c => c.IsVisible && (c is ComboBox or TextBox or Button)))
            {
                Rect bounds = control.TransformToAncestor(pane).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                if (bounds.Left < -1 || bounds.Right > pane.ActualWidth + 1 || bounds.Top < -1 || bounds.Bottom > pane.ActualHeight + 1) return false;
            }
        }
        return true;
    }
    /// <summary>name: LongTagsPreserved; desc: 줄임표는 표시만 줄이고 Tooltip/저장 원문은 보존하는지 검사한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 긴 태그 회귀.</summary>
    private static bool LongTagsPreserved(Window window, NotesManagerViewModel model)
    {
        TextBlock tags = (TextBlock)window.FindName("CurrentTagsText");
        return tags.TextTrimming == TextTrimming.CharacterEllipsis && tags.TextWrapping == TextWrapping.NoWrap && tags.ActualHeight <= tags.FontSize * 2 && tags.Text == model.SelectedMemo!.TagsText && tags.ToolTip as string == model.SelectedMemo.TagsText && tags.Text.Length > 100;
    }
    /// <summary>name: MetadataSummaryIsSingleLine; desc: 기본 접힘 상태에서는 긴 분류를 한 줄로 줄이고 전체 값은 tooltip에 남긴다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 메타데이터 disclosure 간결화.</summary>
    private static bool MetadataSummaryIsSingleLine(Expander disclosure, NotesManagerViewModel model)
    {
        TextBlock? summary = Descendants<TextBlock>(disclosure).FirstOrDefault(text => text.ToolTip as string == model.SelectedMemo?.TagsText);
        return !disclosure.IsExpanded && summary is not null && summary.TextWrapping == TextWrapping.NoWrap
            && summary.TextTrimming == TextTrimming.CharacterEllipsis && summary.ToolTip as string == model.SelectedMemo?.TagsText;
    }
    /// <summary>name: Descendants; desc: 본인 창의 VisualTree만 검사한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 컨트롤 탐색.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { DependencyObject child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed; foreach (T nested in Descendants<T>(child)) yield return nested; } }
    /// <summary>name: Render; desc: 화면 배경을 합성해 본인 WPF 창의 실제 레이아웃을 PNG로 기록한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 렌더 증거.</summary>
    private static void Render(Window window, string path)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(content);
        Vector offset = VisualTreeHelper.GetOffset(content);
        double logicalWidth = content.ActualWidth + Math.Max(0, offset.X) + Math.Max(0, content.Margin.Right);
        double logicalHeight = content.ActualHeight + Math.Max(0, offset.Y) + Math.Max(0, content.Margin.Bottom);
        int width = (int)Math.Ceiling(logicalWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(logicalHeight * dpi.DpiScaleY);
        DrawingVisual visual = new();
        // 직접 렌더는 root Margin offset도 보존한다. bitmap을 client 영역(여백 포함) 크기로
        // 만들어야 오른쪽/아래쪽 16 DIP가 잘리지 않는다. 컨트롤 크기를 축소하지 않는다.
        Rect area = new(0, 0, logicalWidth, logicalHeight);
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, area);
        }
        RenderTargetBitmap bitmap = new(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Render(content);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path); encoder.Save(output);
    }
}
