using System;
using System.Collections.Generic;
using System.Linq;
using WindowBackgroundMemoWidget.Models;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: MonitorDescriptor; desc: 현재 모니터의 안정 ID·물리 작업영역·DPI.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-07.</summary>
public sealed record MonitorDescriptor(string Id, PixelBounds WorkArea, double DpiX = 96, double DpiY = 96);

/// <summary>name: MonitorPlacement; desc: 모니터별 DIP 보관과 물리 화면 복원을 분리하는 순수 배치 규칙.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-07 혼합 DPI/분리 복구.</summary>
public static class MonitorPlacement
{
    /// <summary>name: Restore; desc: 같은 모니터는 local DIP를 유지하고 사라진 모니터는 가까운 작업영역에 복원한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-07.</summary>
    public static PixelBounds Restore(NoteRecord note, IReadOnlyList<MonitorDescriptor> monitors)
    {
        Validate(monitors);
        PixelBounds legacy = new(checked((int)note.X), checked((int)note.Y), checked((int)note.Width), checked((int)note.Height));
        if (note.Placement is not { } placement) return ScreenPlacement.Clamp(legacy, monitors.Select(m => m.WorkArea).ToArray());
        placement.Validate();
        MonitorDescriptor? monitor = monitors.FirstOrDefault(m => m.Id == placement.MonitorId);
        if (monitor == null)
        {
            PixelBounds closest = ScreenPlacement.Clamp(legacy, monitors.Select(m => m.WorkArea).ToArray());
            monitor = monitors.First(m => m.WorkArea.X <= closest.X && m.WorkArea.Y <= closest.Y && m.WorkArea.Right >= closest.Right && m.WorkArea.Bottom >= closest.Bottom);
            return ScreenPlacement.Clamp(new(closest.X, closest.Y, Scale(placement.Width, monitor.DpiX), Scale(placement.Height, monitor.DpiY)), [monitor.WorkArea]);
        }
        return ScreenPlacement.Clamp(new(checked(monitor.WorkArea.X + Scale(placement.X, monitor.DpiX)), checked(monitor.WorkArea.Y + Scale(placement.Y, monitor.DpiY)), Scale(placement.Width, monitor.DpiX), Scale(placement.Height, monitor.DpiY)), [monitor.WorkArea]);
    }

    /// <summary>name: Capture; desc: 실제 모니터 작업영역 기준 DIP를 저장하고 음수 가상화면 좌표도 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-07.</summary>
    public static NotePlacement Capture(PixelBounds bounds, MonitorDescriptor monitor)
    {
        Validate([monitor]);
        return new(monitor.Id, (bounds.X - monitor.WorkArea.X) * 96.0 / monitor.DpiX, (bounds.Y - monitor.WorkArea.Y) * 96.0 / monitor.DpiY, bounds.Width * 96.0 / monitor.DpiX, bounds.Height * 96.0 / monitor.DpiY, monitor.DpiX, monitor.DpiY);
    }
    private static int Scale(double dip, double dpi) => checked((int)Math.Round(dip * dpi / 96, MidpointRounding.AwayFromZero));
    private static void Validate(IReadOnlyList<MonitorDescriptor> monitors)
    {
        if (monitors.Count == 0 || monitors.Any(m => string.IsNullOrWhiteSpace(m.Id) || m.WorkArea.Width <= 0 || m.WorkArea.Height <= 0 || !double.IsFinite(m.DpiX) || !double.IsFinite(m.DpiY) || m.DpiX is < 48 or > 960 || m.DpiY is < 48 or > 960)) throw new ArgumentException("모니터 작업영역/DPI를 확인하세요.");
    }
}
