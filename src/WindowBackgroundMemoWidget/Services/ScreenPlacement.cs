using System;
using System.Collections.Generic;
using WindowBackgroundMemoWidget.Models;

namespace WindowBackgroundMemoWidget.Services;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : ScreenPlacement
/// desc         : 화면이 제거되거나 배치가 벗어났을 때 접근 가능한 작업 영역으로 보정한다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : 음수 좌표와 작은 화면 보정 추가.
/// </summary>
/// ---------------------------------------------------------------------
public static class ScreenPlacement
{
    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Clamp
    /// desc         : 겹침이 가장 큰 화면을 선택하며, 겹치지 않으면 가장 가까운 화면을 선택한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 화면 내부 전체 접근 보장.
    /// </summary>
    /// ---------------------------------------------------------------------
    public static PixelBounds Clamp(PixelBounds requested, IReadOnlyList<PixelBounds> workAreas)
    {
        #region 유효한 화면과 겹침 기준 선택
        ArgumentNullException.ThrowIfNull(workAreas);
        if (workAreas.Count == 0 || requested.Width <= 0 || requested.Height <= 0)
            throw new ArgumentException("메모 크기와 작업 영역은 양수여야 합니다.");

        PixelBounds selected = default;
        long bestOverlap = -1;
        double bestDistance = double.MaxValue;
        // 모든 모니터를 비교해 음수 좌표의 보조 화면도 유지한다.
        foreach (PixelBounds area in workAreas)
        {
            if (area.Width <= 0 || area.Height <= 0)
                throw new ArgumentException("잘못된 모니터 작업 영역입니다.", nameof(workAreas));
            long overlap = Math.Max(0, Math.Min(requested.Right, area.Right) - Math.Max(requested.X, area.X))
                * Math.Max(0, Math.Min(requested.Bottom, area.Bottom) - Math.Max(requested.Y, area.Y));
            double dx = (double)requested.X + requested.Width / 2.0 - area.X - area.Width / 2.0;
            double dy = (double)requested.Y + requested.Height / 2.0 - area.Y - area.Height / 2.0;
            double distance = dx * dx + dy * dy;
            if (overlap > bestOverlap || (overlap == bestOverlap && distance < bestDistance))
            {
                selected = area;
                bestOverlap = overlap;
                bestDistance = distance;
            }
        }
        #endregion

        #region 화면 안으로 크기와 좌표 보정
        // 작은 작업 영역에서도 Math.Clamp의 최소/최대가 뒤집히지 않게 한다.
        int width = Math.Clamp(requested.Width, Math.Min(240, selected.Width), selected.Width);
        int height = Math.Clamp(requested.Height, Math.Min(200, selected.Height), selected.Height);
        int x = checked((int)Math.Clamp((long)requested.X, selected.X, selected.Right - width));
        int y = checked((int)Math.Clamp((long)requested.Y, selected.Y, selected.Bottom - height));
        return new PixelBounds(x, y, width, height);
        #endregion
    }
}
