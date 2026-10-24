using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WindowBackgroundMemoWidget.Interop;
using WindowBackgroundMemoWidget.Models;
using WindowBackgroundMemoWidget.ViewModels;

namespace WindowBackgroundMemoWidget.Views;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : MemoWindow
/// desc         : 본문 입력과 드래그를 검증하는 임시 WPF 메모 창. 저장 책임은 없다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : 물리 픽셀 드래그와 안전한 셸 해제 구현.
/// </summary>
/// ---------------------------------------------------------------------
public partial class MemoWindow : Window
{
    private readonly ProbeViewModel state;
    private NativeMethods.Point dragStart;
    private PixelBounds dragBounds;
    private bool dragging;
    internal DesktopHost? Host { get; private set; }
    internal nint Handle => new WindowInteropHelper(this).Handle;
    internal string EditorText => Editor.Text;

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : MemoWindow
    /// desc         : 임시 입력 상태를 공유하고 메모 창의 UI를 초기화한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    public MemoWindow(ProbeViewModel viewModel)
    {
        state = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnSourceInitialized
    /// desc         : 작업 표시줄 및 Alt+Tab 제외 스타일을 지정하고 창 서비스에 연결한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 메모 도구 창 스타일 적용.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        long style = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GwlExStyle, new nint((style | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExAppWindow));
        Host = new DesktopHost(Handle);
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnDeactivated
    /// desc         : 일반 메모의 편집이 끝나면 표시 순서를 다시 아래로 보낸다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnDeactivated(object? sender, EventArgs e) => Safely(() => Host?.SendToBottom());

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnMoveStart
    /// desc         : 자식 창에서도 같은 화면 좌표 기준으로 이동하고 놓을 때 화면 안으로 보정한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 셸 결합에 독립적인 이동 구현.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnMoveStart(object sender, MouseButtonEventArgs e)
    {
        if (Host == null || !NativeMethods.GetCursorPos(out dragStart)) return;
        dragBounds = Host.Bounds;
        dragging = MoveArea.CaptureMouse();
        e.Handled = true;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnMoving
    /// desc         : 마우스의 물리 픽셀 이동량을 메모 창 위치에 반영한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnMoving(object sender, MouseEventArgs e)
    {
        if (!dragging || Host == null || !NativeMethods.GetCursorPos(out NativeMethods.Point cursor)) return;
        Safely(() => Host.Move(dragBounds with { X = dragBounds.X + cursor.X - dragStart.X, Y = dragBounds.Y + cursor.Y - dragStart.Y }));
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnMoveEnd
    /// desc         : 드래그 캡처를 해제하고 최종 위치를 화면 안으로 보정한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnMoveEnd(object sender, MouseButtonEventArgs e)
    {
        dragging = false;
        MoveArea.ReleaseMouseCapture();
        Safely(() => Host?.RecoverPlacement());
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnMoveCaptureLost
    /// desc         : 캡처 중단 시 이동 상태를 해제한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnMoveCaptureLost(object sender, MouseEventArgs e) => dragging = false;

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnClosing
    /// desc         : 종료 전 본인 창의 부모를 해제해 Explorer와 창 수명을 분리한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 종료 정리 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void OnClosing(object? sender, CancelEventArgs e) => Safely(() => Host?.Dispose());

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Safely
    /// desc         : 예상 가능한 Win32 창 오류를 사용자 상태 표시로 전달한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void Safely(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { state.Status = $"창 제어 실패 · {ex.Message}"; }
    }
}
