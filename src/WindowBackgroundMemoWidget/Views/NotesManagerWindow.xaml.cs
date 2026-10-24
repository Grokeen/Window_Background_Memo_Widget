using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowBackgroundMemoWidget.ViewModels;

namespace WindowBackgroundMemoWidget.Views;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : NotesManagerWindow
/// desc         : Notes형 폴더·목록·Markdown 편집과 폭별 단일 패널 탐색을 제공한다.
/// author       : NotedexOrig3
/// create date  : 2026-10-24
/// update date  : 2026-10-24 NotedexOrig3 : 사용자 참조를 따라 좁은 탐색/목록과 넓은 편집기 위계로 재정리.
/// </summary>
/// ---------------------------------------------------------------------
public partial class NotesManagerWindow : Window
{
    public event EventHandler? HelpRequested;

    private enum CompactPane
    {
        Navigation,
        MemoList,
        Editor
    }

    private const double ThreePaneWidth = 1120;
    private const double CompactWidth = 820;
    private CompactPane compactPane = CompactPane.MemoList;
    private bool isCompact;
    private bool navigationPaneOpen = true;

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : NotesManagerWindow
    /// desc         : ViewModel 생성과 저장소 구성을 앱에 맡기고 실제 화면만 준비한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : NotesManagerViewModel 외부 DataContext 계약 연결.
    /// </summary>
    /// ---------------------------------------------------------------------
    public NotesManagerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
    }

    private void OnHelpMenuClick(object sender, RoutedEventArgs e)
        => HelpRequested?.Invoke(this, EventArgs.Empty);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnWindowSizeChanged
    /// desc         : 넓은 창은 세 패널, 중간 창은 두 패널, 좁은 창은 한 패널 순서로 재배치한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 100%/150% DIP 폭 변화에 대응하는 UI 탐색 모드 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : ApplyResponsiveLayout
    /// desc         : 창 폭에 따라 기존 View를 다시 배치하고 좁은 화면에서 한 단계 탐색을 제공한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 관리 창을 삼단/이단/단일 패널로 적응 배치.
    ///                2026-10-24 NotedexOrig3 : 넓은 화면의 중복 탐색 복귀 버튼을 숨기고 compact 복귀 경로는 보존.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void ApplyResponsiveLayout()
    {
        if (!IsInitialized || ManagerGrid is null) return;

        #region 창 폭에 따른 패널 전환
        /// ---------------------------------------------------------------------
        /// 넓은 화면은 3-pane을 유지하되 탐색과 목록 폭을 제한하고 편집기가 남은 폭을 받는다.
        /// 작은 폭에서는 같은 화면을 단계별로 전환하며 사용자가 목록 위치를 잃지 않게 한다.
        /// ---------------------------------------------------------------------
        double availableWidth = ManagerGrid.ActualWidth;
        if (availableWidth >= ThreePaneWidth)
        {
            isCompact = false;
            Grid.SetColumn(SidebarPane, 0);
            Grid.SetColumn(ListPane, 1);
            Grid.SetColumn(EditorPane, 2);
            SidebarPane.Visibility = navigationPaneOpen ? Visibility.Visible : Visibility.Collapsed;
            ListPane.Visibility = Visibility.Visible;
            EditorPane.Visibility = Visibility.Visible;
            SidebarColumn.Width = navigationPaneOpen ? new GridLength(190) : new GridLength(0);
            ListColumn.Width = navigationPaneOpen ? new GridLength(290) : new GridLength(340);
            EditorColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else if (availableWidth >= CompactWidth)
        {
            isCompact = false;
            Grid.SetColumn(SidebarPane, 0);
            Grid.SetColumn(ListPane, 1);
            Grid.SetColumn(EditorPane, 2);
            SidebarPane.Visibility = navigationPaneOpen ? Visibility.Visible : Visibility.Collapsed;
            ListPane.Visibility = Visibility.Visible;
            EditorPane.Visibility = Visibility.Visible;
            SidebarColumn.Width = navigationPaneOpen ? new GridLength(170) : new GridLength(0);
            ListColumn.Width = navigationPaneOpen ? new GridLength(0.38, GridUnitType.Star) : new GridLength(0.42, GridUnitType.Star);
            EditorColumn.Width = navigationPaneOpen ? new GridLength(0.62, GridUnitType.Star) : new GridLength(0.58, GridUnitType.Star);
        }
        else
        {
            isCompact = true;
            Grid.SetColumn(SidebarPane, 0);
            Grid.SetColumn(ListPane, 0);
            Grid.SetColumn(EditorPane, 0);
            SidebarColumn.Width = new GridLength(1, GridUnitType.Star);
            ListColumn.Width = new GridLength(0);
            EditorColumn.Width = new GridLength(0);
            ShowCompactPane();
        }
        BackToListButton.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        NavigationBackButton.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        #endregion
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : ShowCompactPane
    /// desc         : 단일 열 폭에서 탐색, 목록, 편집기를 한 화면씩 교체한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 탐색/목록/편집기의 작은 창 이동 구현.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void ShowCompactPane()
    {
        if (!isCompact) return;

        SidebarPane.Visibility = compactPane == CompactPane.Navigation ? Visibility.Visible : Visibility.Collapsed;
        ListPane.Visibility = compactPane == CompactPane.MemoList ? Visibility.Visible : Visibility.Collapsed;
        EditorPane.Visibility = compactPane == CompactPane.Editor ? Visibility.Visible : Visibility.Collapsed;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnWindowPreviewKeyDown
    /// desc         : Ctrl+F를 검색으로 보낸다. TextBox의 IME 조합 키 입력은 가로채지 않는다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 키보드 검색 진입 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (DataContext is NotesManagerViewModel manager && manager.NewMemoCommand.CanExecute(null))
                manager.NewMemoCommand.Execute(null);
            NavigateToEditor();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (isCompact) compactPane = CompactPane.MemoList;
            ApplyResponsiveLayout();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnOpenNavigationClick
    /// desc         : 좁은 창에서 폴더와 보기 탐색을 연다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 좁은 창 탐색 패널 진입 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnOpenNavigationClick(object sender, RoutedEventArgs e)
    {
        if (isCompact)
        {
            compactPane = CompactPane.Navigation;
            ShowCompactPane();
            return;
        }

        navigationPaneOpen = !navigationPaneOpen;
        ApplyResponsiveLayout();
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnSidebarSelectionClick
    /// desc         : 보기/폴더/태그 명령 뒤 좁은 창에서 메모 목록으로 이동한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 탐색 선택 뒤 compact 목록 전환 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnSidebarSelectionClick(object sender, RoutedEventArgs e)
    {
        compactPane = CompactPane.MemoList;
        if (isCompact) ShowCompactPane();
        else { navigationPaneOpen = false; ApplyResponsiveLayout(); }
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnCompactListClick
    /// desc         : 좁은 화면에서 편집기 또는 탐색 패널에서 목록으로 돌아온다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 좁은 화면 뒤로 이동 구현.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnCompactListClick(object sender, RoutedEventArgs e)
    {
        if (isCompact)
        {
            compactPane = CompactPane.MemoList;
            ShowCompactPane();
            MemoList.Focus();
        }
        else if (navigationPaneOpen)
        {
            navigationPaneOpen = false;
            ApplyResponsiveLayout();
        }
    }

    /// <summary>
    /// name         : OnMemoListPreviewMouseLeftButtonUp
    /// desc         : 같은 항목을 다시 눌러도 compact 창에서 편집기로 이동하게 한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 Notes 목록 행 전체를 선택·열기 대상으로 사용.
    /// </summary>
    private void OnMemoListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isCompact) return;
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && !ReferenceEquals(current, MemoList))
        {
            if (current is ListBoxItem) { NavigateToEditor(); return; }
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
    }

    /// <summary>
    /// name         : OnMemoListSelectionChanged
    /// desc         : compact 목록에서 실제 선택 변경 즉시 선택 메모 편집기를 연다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 선택 변경 경로를 추가해 목록 행 선택을 보장.
    /// </summary>
    private void OnMemoListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isCompact && e.AddedItems.Count > 0) NavigateToEditor();
    }

    /// <summary>
    /// name         : OnMemoListKeyDown
    /// desc         : 좁은 화면 목록에서 Enter로 선택 메모 상세를 연다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 목록 중심 키보드 탐색.
    /// </summary>
    private void OnMemoListKeyDown(object sender, KeyEventArgs e)
    {
        if (!isCompact || e.Key != Key.Enter || MemoList.SelectedItem is null) return;
        NavigateToEditor();
        e.Handled = true;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnNewMemoClick
    /// desc         : 신규 메모 생성 명령 이후 상세 편집 패널을 표시한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : compact 새 메모 생성 후 본문 편집 진입 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnNewMemoClick(object sender, RoutedEventArgs e) => NavigateToEditor();

    private async void OnSaveDocumentClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is NotesManagerViewModel manager && manager.SelectedMemo is { } selected)
            await selected.Document.SaveAsync();
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : NavigateToEditor
    /// desc         : 좁은 창에서 편집 패널을 연 뒤 WPF binding이 새 선택을 반영하면 본문에 포커스한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 목록 선택과 신규 메모의 공통 편집 진입 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void NavigateToEditor()
    {
        if (isCompact)
        {
            compactPane = CompactPane.Editor;
            ShowCompactPane();
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => DocumentEditor.FocusEditor()));
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnFocusSearchClick
    /// desc         : 검색 진입 버튼에서 검색 상자로 포커스를 이동한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 검색 진입 버튼 동작 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnFocusSearchClick(object sender, RoutedEventArgs e)
    {
        if (isCompact) compactPane = CompactPane.MemoList;
        ShowCompactPane();
        SearchBox.Focus();
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnOpenPriorityMenu
    /// desc         : 키보드로 중요도 버튼을 활성화해도 선택 항목 메뉴를 연다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 마우스·키보드 공통 중요도 메뉴 진입 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnOpenPriorityMenu(object sender, RoutedEventArgs e)
    {
        #region 우선순위 명령 열기
        /// ---------------------------------------------------------------------
        /// Button 기본 클릭은 ContextMenu를 자동으로 열지 않으므로 Enter/Space에서도
        /// 메뉴 명령에 접근하도록 PlacementTarget을 지정하고 메뉴를 명시적으로 연다.
        /// ---------------------------------------------------------------------
        if (sender is FrameworkElement element && element.ContextMenu is ContextMenu menu)
        {
            menu.PlacementTarget = element;
            menu.IsOpen = true;
        }
        #endregion
    }
}
