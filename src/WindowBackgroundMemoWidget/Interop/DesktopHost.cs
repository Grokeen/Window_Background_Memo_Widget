using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WindowBackgroundMemoWidget.Models;
using WindowBackgroundMemoWidget.Services;
using Forms = System.Windows.Forms;

namespace WindowBackgroundMemoWidget.Interop;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : DesktopMode
/// desc         : M1 비교 후보인 일반 하단 창과 Explorer 자식 창을 구분한다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-24 NotedexOrig2 : 모드 정의 설명 추가.
/// </summary>
/// ---------------------------------------------------------------------
public enum DesktopMode { BottomWindow, ShellChild }

/// ---------------------------------------------------------------------
/// <summary>
/// name         : DesktopHost
/// desc         : 일반 창 하단 배치와 Explorer 아이콘 호스트의 자식 결합을 비교한다.
///                셸 결합은 비공식 셸 구조를 이용하는 실험이며 제품 호환성을 보장하지 않는다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : 스타일 복원 및 실패 롤백 구현.
///                2026-10-24 NotedexOrig2 : WS_CHILD 복원 후 부모 검증 순서 수정.
/// </summary>
/// ---------------------------------------------------------------------
internal sealed class DesktopHost(nint hwnd) : IDisposable
{
    private nint originalStyle;
    private nint originalExStyle;
    private nint originalParent;
    internal bool IsAttached { get; private set; }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Apply
    /// desc         : 기존 결합을 해제하고 선택한 표시 방식을 적용한다. 실패 시 원상복구한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 부모 HWND 검증과 롤백 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal string Apply(DesktopMode mode)
    {
        #region 원래 창 상태 보관 및 스타일 준비
        Detach();
        PixelBounds bounds = Bounds;
        if (mode == DesktopMode.BottomWindow)
        {
            SendToBottom();
            return "일반 창 하단 배치 적용 · Win + D 동작은 수동 비교가 필요합니다.";
        }
        nint shell = FindIconHost();
        if (shell == 0)
            throw new InvalidOperationException("Explorer 아이콘 호스트를 찾지 못했습니다. 일반 창 방식으로 전환하세요.");

        originalStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle);
        originalExStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
        originalParent = NativeMethods.GetParent(hwnd);
        #endregion

        #region 셸 자식 창 결합 및 실패 복구
        try
        {
            // SetParent는 스타일을 바꾸지 않으므로 공식 API 규약에 맞춰 WS_CHILD를 먼저 지정한다.
            SetStyle(NativeMethods.GwlStyle, new nint((originalStyle.ToInt64() & ~NativeMethods.WsPopup) | NativeMethods.WsChild));
            SetStyle(NativeMethods.GwlExStyle, new nint((originalExStyle.ToInt64() | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExAppWindow));
            Marshal.SetLastPInvokeError(0);
            NativeMethods.SetParent(hwnd, shell);
            int error = Marshal.GetLastPInvokeError();
            // 원래 부모가 NULL이면 성공 반환도 NULL이므로 실제 부모까지 검사한다.
            if (NativeMethods.GetParent(hwnd) != shell)
                throw new Win32Exception(error, "Explorer 부모 창 결합에 실패했습니다.");
            IsAttached = true;
            Move(bounds);
            return $"셸 결합 적용 · {GetClass(shell)} · 한글 IME / Win + D / Explorer 복구 검증 대기";
        }
        catch
        {
            RestoreOriginalWindow();
            throw;
        }
        #endregion
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Detach
    /// desc         : 본인이 변경한 메모 창만 원래 부모와 스타일로 되돌린다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 종료 및 표시 방식 전환 공통 처리.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal void Detach()
    {
        if (!IsAttached) return;
        if (!NativeMethods.IsWindow(hwnd)) { IsAttached = false; return; }
        PixelBounds bounds = Bounds;
        RestoreOriginalWindow();
        Move(bounds);
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : RestoreOriginalWindow
    /// desc         : 부모와 스타일을 복원하며 실패를 숨기지 않는다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 스타일 변경 전 상태 복원.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void RestoreOriginalWindow()
    {
        nint targetParent = NativeMethods.IsWindow(originalParent) ? originalParent : 0;
        Marshal.SetLastPInvokeError(0);
        nint previous = NativeMethods.SetParent(hwnd, targetParent);
        int error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
            throw new Win32Exception(error, "메모 창 부모 복원 실패");
        // WS_CHILD가 남아 있으면 GetParent가 데스크톱 HWND를 반환하므로,
        // 원래 최상위 창 스타일을 복원한 다음 부모가 올바른지 확인한다.
        SetStyle(NativeMethods.GwlStyle, originalStyle);
        SetStyle(NativeMethods.GwlExStyle, originalExStyle);
        if (NativeMethods.GetParent(hwnd) != targetParent)
            throw new Win32Exception("메모 창 부모 복원 후 검증 실패");
        IsAttached = false;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Move
    /// desc         : 화면 좌표를 현재 부모의 클라이언트 좌표로 변환해 위치와 크기를 적용한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 결합 전후 화면 좌표 보존.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal void Move(PixelBounds bounds)
    {
        NativeMethods.Point point = new() { X = bounds.X, Y = bounds.Y };
        if (IsAttached)
            NativeMethods.MapWindowPoints(0, NativeMethods.GetParent(hwnd), ref point, 1);
        if (!NativeMethods.SetWindowPos(hwnd, 0, point.X, point.Y, bounds.Width, bounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpFrameChanged))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "메모 위치 조정 실패");
    }

    internal PixelBounds Bounds => NativeMethods.GetWindowRect(hwnd, out NativeMethods.Rect rect)
        ? rect.ToBounds() : throw new Win32Exception(Marshal.GetLastPInvokeError(), "메모 위치 조회 실패");

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : RecoverPlacement
    /// desc         : 현재 화면 구성의 작업 영역 안으로 메모를 보정한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 화면 분리 시 공통 보정 적용.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal void RecoverPlacement() => Move(ScreenPlacement.Clamp(Bounds, WorkAreas()));

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : WorkAreas
    /// desc         : 현재 연결된 화면의 물리 픽셀 작업 영역을 반환한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal static PixelBounds[] WorkAreas() => Forms.Screen.AllScreens.Select(screen =>
        new PixelBounds(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height)).ToArray();

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : SendToBottom
    /// desc         : 포커스를 가져오지 않고 일반 메모 창을 표시 순서 맨 아래로 이동한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 일반 창 비교 후보 구현.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal void SendToBottom()
    {
        if (IsAttached || !NativeMethods.IsWindow(hwnd)) return;
        if (!NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndBottom, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "표시 순서 변경 실패");
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : FindIconHost
    /// desc         : 기존 Explorer의 Progman/WorkerW 중 아이콘 영역을 가진 호스트만 선택한다.
    ///                비공식 메시지 전송, Explorer 종료 및 타 프로세스 창 변경은 하지 않는다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 발견 실패를 명시하는 셸 탐색 추가.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal static nint FindIconHost()
    {
        nint found = 0;
        NativeMethods.EnumWindows((window, _) =>
        {
            string className = GetClass(window);
            if ((className == "Progman" || className == "WorkerW") && NativeMethods.IsWindowVisible(window)
                && NativeMethods.FindWindowEx(window, 0, "SHELLDLL_DefView", null) != 0)
            {
                found = window;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : GetClass
    /// desc         : 창 제목이나 사용자 입력 없이 Win32 클래스명만 조회한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal static string GetClass(nint window)
    {
        StringBuilder value = new(256);
        NativeMethods.GetClassName(window, value, value.Capacity);
        return value.ToString();
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : SetStyle
    /// desc         : NULL 반환과 Win32 오류를 구분해 스타일 변경 실패를 전달한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void SetStyle(int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = NativeMethods.SetWindowLongPtr(hwnd, index, value);
        int error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0) throw new Win32Exception(error);
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Dispose
    /// desc         : 현재 메모 창의 셸 결합을 해제한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    public void Dispose() => Detach();
}
