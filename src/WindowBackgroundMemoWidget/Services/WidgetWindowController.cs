using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowBackgroundMemoWidget.Interop;
using WindowBackgroundMemoWidget.Models;
using WindowBackgroundMemoWidget.ViewModels;
using Forms = System.Windows.Forms;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: WidgetWindowController; desc: 제품 위젯의 모니터/DIP 저장·셸 표시·화면/절전 복구를 한 곳에서 관리한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-07/AC-14 실제 표시 경계.</summary>
public sealed class WidgetWindowController : IDisposable
{
    private readonly Window window;
    private readonly MemoListItemViewModel item;
    private readonly DispatcherTimer recovery;
    private DesktopHost? host;
    private HwndSource? source;
    private nint hwnd;
    private bool ready, moving, disposed, queued, initiallyPositioned, initialHostPrepared;
    private static readonly uint TaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private static readonly ConditionalWeakTable<Window, WidgetWindowController> Controllers = new();
    public bool IsShellAttached => host?.IsAttached == true && NativeMethods.IsWindow(NativeMethods.GetParent(hwnd));
    public bool IsDisposed => disposed;

    /// <summary>name: WidgetWindowController; desc: UI는 이 인스턴스만 만들고 native host/좌표 저장을 중복 수행하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 Orig3 연결 API.</summary>
    public WidgetWindowController(Window window, MemoListItemViewModel item)
    {
        this.window = window; this.item = item;
        Controllers.Add(window, this);
        window.SourceInitialized += Initialize;
        window.Loaded += Loaded;
        window.LocationChanged += BoundsChanged;
        window.SizeChanged += BoundsChanged;
        window.Deactivated += Deactivated;
        window.Closed += Closed;
        item.PropertyChanged += ItemChanged;
        recovery = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => CheckShell(), window.Dispatcher);
        recovery.Stop();
        if (new WindowInteropHelper(window).Handle != 0) Initialize(null, EventArgs.Empty);
    }

    private void Initialize(object? sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(window).Handle;
        if (host != null || hwnd == 0) return;
        host = new DesktopHost(hwnd); source = HwndSource.FromHwnd(hwnd); source?.AddHook(Message);
    }

    /// <summary>name: PrepareForDisplay; desc: HWND 생성 이벤트가 끝난 안전한 시점에 저장 배치를 적용한다. 전체 Show 완료 조건은 유지한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 생성 중 native resize 재진입 방지.</summary>
    internal static void PrepareForDisplay(Window window)
    {
        if (!Controllers.TryGetValue(window, out WidgetWindowController? controller) || controller.ready || controller.disposed) return;
        controller.PrepareHiddenWindow();
    }
    private void PrepareHiddenWindow()
    {
        using var measurement = StartupTelemetry.Measure("widget-prepare");
        // EnsureHandle 내부 SourceInitialized에서는 host만 연결하고, 반환 후 위치를 바꾼다.
        _ = new WindowInteropHelper(window).EnsureHandle();
        Initialize(null, EventArgs.Empty);
        moving = true;
        try
        {
            Position(); initiallyPositioned = true;
            Capture();
            // 보이는 10k 문서를 SetParent로 다시 layout하지 않도록 숨긴 HWND에서 연결한다.
            Attach(); initialHostPrepared = true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException)
        { StartupTelemetry.Failure("widget-initial-position", error); item.WidgetStatus = "초기 배치를 복원하지 못했습니다. 표시 후 다시 시도합니다."; }
        finally { moving = false; }
    }
    private void Loaded(object sender, RoutedEventArgs e)
    {
        if (ready) return; ready = true;
        if (moving) { recovery.Start(); return; }
        if (initiallyPositioned) { Capture(); if (!initialHostPrepared) Attach(); }
        else Recover();
        recovery.Start();
    }
    private void Closed(object? sender, EventArgs e) => Dispose();
    private void Deactivated(object? sender, EventArgs e) { if (!IsShellAttached) Try(() => host?.SendToBottom()); }
    private void BoundsChanged(object? sender, EventArgs e) { if (ready && !moving && !disposed) Capture(); }
    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MemoListItemViewModel.PlacementResetVersion)) QueueRecovery(); }

    /// <summary>name: Recover; desc: target 화면으로 옮긴 뒤 GetDpiForWindow로 실제 DPI를 다시 읽어 한 번 보정한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 GetDpiForMonitor 추측 제거.</summary>
    public void Recover()
    {
        if (!ready || host == null || disposed || !NativeMethods.IsWindow(hwnd)) return;
        using var measurement = StartupTelemetry.Measure("widget-placement-recover");
        moving = true;
        try
        {
            Position();
            Capture();
            Attach();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException)
        { item.WidgetStatus = "위젯 배치 복구 실패 · 관리 창에서 편집할 수 있습니다."; }
        finally { moving = false; }
    }

    /// <summary>name: Position; desc: HWND 첫 layout 전과 화면 복구에서 같은 DIP 규칙을 사용하며 불필요한 resize는 생략한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 시작 layout 중복 제거.</summary>
    private void Position()
    {
        if (host == null) return;
        using var measurement = StartupTelemetry.Measure("widget-native-position");
        // 연결된 셸의 DPI를 좌표 계산에 섞지 않도록 top-level 상태에서 계산한다.
        host.Detach();
        NoteRecord saved = item.Snapshot();
        PixelBounds initial = MonitorPlacement.Restore(saved, Monitors());
        if (host.Bounds != initial) host.Move(initial);
        // target에 놓인 뒤 읽은 실제 DPI가 달라졌을 때만 추가 보정한다.
        PixelBounds corrected = MonitorPlacement.Restore(saved, Monitors());
        if (corrected != initial) host.Move(corrected);
    }

    private MonitorDescriptor[] Monitors()
    {
        string current = Forms.Screen.FromHandle(hwnd).DeviceName;
        uint dpi = NativeMethods.GetDpiForWindow(hwnd); if (dpi == 0) dpi = 96;
        return Forms.Screen.AllScreens.Select(screen =>
        {
            double scale = screen.DeviceName == current ? dpi : item.Placement?.MonitorId == screen.DeviceName ? item.Placement.DpiX : 96;
            return new MonitorDescriptor(screen.DeviceName, new(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height), scale, scale);
        }).ToArray();
    }
    private void Capture()
    {
        if (host == null || !NativeMethods.IsWindow(hwnd)) return;
        Try(() =>
        {
            Forms.Screen screen = Forms.Screen.FromHandle(hwnd);
            uint dpi = NativeMethods.GetDpiForWindow(hwnd); if (dpi == 0) dpi = 96;
            MonitorDescriptor monitor = new(screen.DeviceName, new(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height), dpi, dpi);
            PixelBounds bounds = host.Bounds;
            NotePlacement placement = MonitorPlacement.Capture(bounds, monitor);
            // 작은 작업영역에서도 저장 모델의 최소 DIP 범위를 지킨다.
            placement = placement with { Width = Math.Max(120, placement.Width), Height = Math.Max(100, placement.Height) };
            item.RememberPlacement(bounds, placement);
        });
    }
    private void Attach()
    {
        if (host == null) return;
        using var measurement = StartupTelemetry.Measure("widget-shell-attach");
        try
        {
            host.Apply(DesktopMode.ShellChild);
            item.WidgetStatus = "바탕화면에 표시 중";
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            Try(() => host.Apply(DesktopMode.BottomWindow));
            item.WidgetStatus = "바탕화면 연결 실패 · 일반 하단 창으로 표시 중";
        }
    }
    private void CheckShell()
    {
        if (!ready || disposed || !window.IsVisible || !NativeMethods.IsWindow(hwnd)) return;
        if (!IsShellAttached) QueueRecovery();
    }
    private nint Message(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x007e or 0x001a || (uint)message == TaskbarCreated || message == 0x0218 && wParam.ToInt64() is 6 or 7 or 18) QueueRecovery();
        // WPF가 WM_DPICHANGED suggested rect를 적용한 뒤 capture한다. 이중 크기 조정을 피한다.
        if (message == 0x02e0) window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Capture));
        return 0;
    }
    private void QueueRecovery()
    {
        if (queued || disposed) return; queued = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { queued = false; Recover(); }));
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException)
        { item.WidgetStatus = "위젯 상태 변경 실패 · 메모 원본은 유지됩니다."; }
    }

    /// <summary>name: Dispose; desc: native hook/타이머/구독을 해제하고 살아 있는 자기 창만 원복한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 종료/Explorer 복구.</summary>
    public void Dispose()
    {
        if (disposed) return; disposed = true; Controllers.Remove(window); recovery.Stop(); source?.RemoveHook(Message);
        window.SourceInitialized -= Initialize; window.Loaded -= Loaded; window.LocationChanged -= BoundsChanged; window.SizeChanged -= BoundsChanged; window.Deactivated -= Deactivated; window.Closed -= Closed; item.PropertyChanged -= ItemChanged;
        Try(() => host?.Dispose());
    }
}
