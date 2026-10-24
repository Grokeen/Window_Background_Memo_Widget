using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WindowBackgroundMemoWidget.Interop;
using WindowBackgroundMemoWidget.Services;
using WindowBackgroundMemoWidget.ViewModels;
using Forms = System.Windows.Forms;

namespace WindowBackgroundMemoWidget.Views;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : MainWindow
/// desc         : 검증 모드, 트레이 접근, 진단 및 메모 창 수명을 조정하는 제어 화면.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : M1 제어 진입점과 오류 안내 구현.
///                2026-10-24 NotedexOrig2 : 공통 메모 아이콘을 제어 창과 트레이에 연결.
/// </summary>
/// ---------------------------------------------------------------------
public partial class MainWindow : Window
{
    internal readonly ProbeViewModel State = new();
    internal readonly DiagnosticReport Report = new();
    internal MemoWindow? Memo { get; private set; }
    private readonly bool interactive;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? trayMenu;
    private System.Drawing.Icon? applicationTrayIcon;
    private bool ready;
    internal static readonly uint ActivateMessage = NativeMethods.RegisterWindowMessage("Notedex.M1.Activate.20261023");
    private static readonly uint TaskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : MainWindow
    /// desc         : 검증 상태와 화면 바인딩을 연결하고 화면 구성 변경을 구독한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    ///                2026-10-24 NotedexOrig2 : 창 생성 전 공통 아이콘 리소스 로드.
    /// </summary>
    /// ---------------------------------------------------------------------
    public MainWindow(bool interactive = true)
    {
        this.interactive = interactive;
        InitializeComponent();
        LoadApplicationIcon();
        DataContext = State;
        ShowActivated = interactive;
        ready = true;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        if (interactive) Closed += (_, _) => Application.Current.Shutdown();
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : LoadApplicationIcon
    /// desc         : EXE와 동일한 내장 ICO를 창과 트레이용으로 읽고 스트림과 아이콘 수명을 분리한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig2 : 외부 자산 파일 없는 단일 EXE 아이콘 로드 구현.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void LoadApplicationIcon()
    {
        #region 내장 리소스에서 WPF와 트레이 아이콘 준비
        Uri resourceUri = new("pack://application:,,,/Assets/WindowMemo.ico", UriKind.Absolute);
        using Stream source = Application.GetResourceStream(resourceUri)?.Stream
            ?? throw new InvalidOperationException("내장 프로그램 아이콘을 찾을 수 없습니다.");
        // OnLoad는 스트림이 닫힌 후에도 창 아이콘의 모든 프레임을 사용할 수 있게 한다.
        Icon = BitmapFrame.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Icon.Freeze();
        source.Position = 0;
        using System.Drawing.Icon loadedIcon = new(source, Forms.SystemInformation.SmallIconSize);
        // NotifyIcon이 사용하는 네이티브 아이콘은 창이 닫힐 때까지 유지한다.
        applicationTrayIcon = (System.Drawing.Icon)loadedIcon.Clone();
        #endregion
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OpenMemo
    /// desc         : 숨긴 메모를 다시 표시하고 선택한 표시 방식을 적용한다. 사라진 창은 같은 임시 본문으로 다시 만든다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 포커스 비활성 표시와 셸 실패 안내 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal void OpenMemo(DesktopMode? requestedMode = null)
    {
        #region 메모 창 생성 및 표시
        if (Memo == null || !NativeMethods.IsWindow(Memo.Handle))
        {
            MemoWindow created = new(State);
            Memo = created;
            created.Closed += (_, _) => { if (ReferenceEquals(Memo, created)) Memo = null; };
            created.Show();
            created.Host!.RecoverPlacement();
        }
        else if (!Memo.IsVisible) Memo.Show();
        #endregion

        #region 선택 모드 적용과 오류 상태 구분
        DesktopMode mode = requestedMode ?? (ShellMode.IsChecked == true ? DesktopMode.ShellChild : DesktopMode.BottomWindow);
        try
        {
            State.Status = Memo.Host!.Apply(mode);
            State.ModeLabel = mode == DesktopMode.ShellChild ? "Explorer 셸 결합 · 실험" : "일반 창 · 맨 아래 배치";
            Report.CaptureWindow(Memo.Handle);
            Report.Checks.Add(new ProbeCheck($"Mode: {mode}", "APPLIED", State.Status));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            State.ModeLabel = "표시 방식 적용 실패";
            State.Status = ex.Message;
            Report.Checks.Add(new ProbeCheck($"Mode: {mode}", "FAIL", ex.Message));
        }
        #endregion
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnOpenMemo
    /// desc         : 현재 선택한 표시 방식으로 메모를 연다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnOpenMemo(object sender, RoutedEventArgs e) => OpenMemo();
    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnHideMemo
    /// desc         : 본문을 메모리에 유지하면서 메모 창만 숨긴다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnHideMemo(object sender, RoutedEventArgs e)
    {
        Memo?.Hide();
        State.Status = "메모를 숨겼습니다. 임시 본문은 앱을 종료할 때까지 유지됩니다.";
    }
    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnModeChanged
    /// desc         : 초기 XAML 로딩 이후 열린 메모에만 표시 방식 변경을 적용한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnModeChanged(object sender, RoutedEventArgs e) { if (ready && Memo != null) OpenMemo(); }
    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnResetPlacement
    /// desc         : 현재 모니터의 작업 영역으로 메모를 복구한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnResetPlacement(object sender, RoutedEventArgs e)
    {
        OpenMemo();
        try { Memo?.Host?.RecoverPlacement(); State.Status = "현재 화면의 작업 영역 안으로 메모를 복구했습니다."; }
        catch (Win32Exception ex) { State.Status = $"배치 복구 실패 · {ex.Message}"; }
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnExportDiagnostics
    /// desc         : 사용자가 선택한 JSON에 진단만 내보내고 파일 오류를 화면에 보고한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 본문 제외 진단 내보내기 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new() { Filter = "진단 JSON (*.json)|*.json", FileName = "memo-m1-diagnostics.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { Report.Write(dialog.FileName); State.Status = "진단 파일을 내보냈습니다. 메모 본문은 포함하지 않았습니다."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { State.Status = $"진단 내보내기 실패 · {ex.Message}"; }
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnSourceInitialized
    /// desc         : 트레이와 기존 인스턴스 호출을 연결하고 Explorer 재시작 후 수동 복구를 안내한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 중복 실행 및 TaskbarCreated 처리.
    ///                2026-10-24 NotedexOrig2 : Windows 기본 아이콘을 내장 메모 아이콘으로 교체.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        if (!interactive) return;
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("검증 화면 열기", null, (_, _) => Dispatcher.Invoke(ShowControl));
        trayMenu.Items.Add("메모 표시", null, (_, _) => Dispatcher.Invoke(() => OpenMemo()));
        trayMenu.Items.Add("메모 숨기기", null, (_, _) => Dispatcher.Invoke(() => Memo?.Hide()));
        trayMenu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(Close));
        tray = new Forms.NotifyIcon { Text = "바탕화면 메모 · M1 검증", Icon = applicationTrayIcon, ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowControl);
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : WindowMessage
    /// desc         : 중복 실행 호출과 Explorer 재시작 메시지를 처리한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == ActivateMessage && interactive) { ShowControl(); handled = true; }
        if ((uint)message == TaskbarCreatedMessage)
        {
            if (tray != null) { tray.Visible = false; tray.Visible = true; }
            State.Status = "Explorer가 재시작되었습니다. ‘메모 열기’로 셸 결합을 다시 확인해 주세요.";
        }
        return 0;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : ShowControl
    /// desc         : 사용자가 요청한 제어 화면을 복원하고 활성화한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void ShowControl() { Show(); WindowState = WindowState.Normal; Activate(); }
    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnDisplaySettingsChanged
    /// desc         : 화면 변경을 UI 스레드에 전달하고 살아 있는 메모의 위치만 보정한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        try { if (Memo?.Host != null && NativeMethods.IsWindow(Memo.Handle)) Memo.Host.RecoverPlacement(); }
        catch (Win32Exception ex) { State.Status = $"화면 구성 변경 처리 실패 · {ex.Message}"; }
    });

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnClosing
    /// desc         : 임시 입력이 있으면 종료 확인 후 메모 및 트레이와 정적 이벤트를 정리한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 검증용 입력 유실 안내 및 자원 정리.
    ///                2026-10-24 NotedexOrig2 : 트레이 해제 후 소유한 아이콘 리소스 정리.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (interactive && State.HasEdits && MessageBox.Show(this, "입력한 임시 메모는 저장되지 않습니다. 종료하시겠습니까?", "기술 검증 종료", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Memo?.Close();
        tray?.Dispose();
        trayMenu?.Dispose();
        applicationTrayIcon?.Dispose();
    }
}
