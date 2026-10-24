using System;
using System.Runtime.InteropServices;
using System.Text;
using WindowBackgroundMemoWidget.Models;

namespace WindowBackgroundMemoWidget.Interop;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : NativeMethods
/// desc         : x64 Windows 창 API 선언을 격리한다. 좌표는 물리 픽셀로 전달한다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : M1 셸 결합과 창 상태 진단 API 추가.
/// </summary>
/// ---------------------------------------------------------------------
internal static class NativeMethods
{
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const long WsChild = 0x40000000L;
    internal const long WsPopup = 0x80000000L;
    internal const long WsExToolWindow = 0x80L;
    internal const long WsExAppWindow = 0x40000L;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoSize = 0x0001;
    internal static readonly nint HwndBottom = new(1);
    internal static readonly nint HwndBroadcast = new(0xffff);

    internal delegate bool EnumWindowsCallback(nint hwnd, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X; internal int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left; internal int Top; internal int Right; internal int Bottom;
        /// ---------------------------------------------------------------------
        /// <summary>
        /// name         : ToBounds
        /// desc         : Win32의 네 경계를 위치와 크기로 변환한다.
        /// author       : NotedexOrig2
        /// create date  : 2026-10-23
        /// update date  : 2026-10-24 NotedexOrig2 : 좌표 변환 설명 추가.
        /// </summary>
        /// ---------------------------------------------------------------------
        internal readonly PixelBounds ToBounds() => new(Left, Top, Right - Left, Bottom - Top);
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : EnumWindows
    /// desc         : 현재 최상위 창을 열거한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : FindWindowEx
    /// desc         : 부모 안에서 지정한 클래스의 자식 창을 찾는다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : GetClassName
    /// desc         : Win32 클래스명을 제한된 버퍼로 읽는다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint hwnd, StringBuilder text, int maxCount);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : GetParent
    /// desc         : 현재 부모 또는 소유 창을 조회한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll")]
    internal static extern nint GetParent(nint hwnd);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : SetParent
    /// desc         : 메모의 부모 창을 변경하고 이전 부모를 반환한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetParent(nint hwnd, nint parent);

    // 지원 대상은 x64이며 LongPtr의 64비트 진입점을 명시한다.
    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : GetWindowLongPtr
    /// desc         : x64 창 스타일 값을 조회한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint hwnd, int index);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : SetWindowLongPtr
    /// desc         : x64 창 스타일 값을 변경한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : SetWindowPos
    /// desc         : 위치·크기·표시 순서를 지정한 플래그로 조정한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : GetWindowRect
    /// desc         : 화면 기준 물리 픽셀 사각형을 조회한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : MapWindowPoints
    /// desc         : 화면 좌표와 부모 클라이언트 좌표를 변환한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll")]
    internal static extern int MapWindowPoints(nint from, nint to, ref Point point, uint count);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : GetCursorPos
    /// desc         : 드래그 계산을 위한 화면 기준 커서 좌표를 조회한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : IsWindow
    /// desc         : 메모 또는 부모 HWND가 아직 유효한지 확인한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : IsWindowVisible
    /// desc         : Win32의 표시 상태를 조회한다. 실제 가림 여부는 별도 검증한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : GetForegroundWindow
    /// desc         : 포커스 보존 검사에서 현재 활성 창 핸들을 조회한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : GetDpiForWindow
    /// desc         : 현재 메모 창에 적용된 DPI를 조회한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : RegisterWindowMessage
    /// desc         : 인스턴스 호출과 Explorer 재시작용 메시지 번호를 얻는다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string message);

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : PostMessage
    /// desc         : 기존 인스턴스에 제어 화면 열기 메시지를 비동기로 보낸다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : API 역할과 변경 이력 명시.
    /// </summary>
    /// ---------------------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
