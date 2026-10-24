namespace WindowBackgroundMemoWidget.Models;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : PixelBounds
/// desc         : Win32 화면 배치에 사용하는 물리 픽셀 사각형. DIP와 혼용하지 않는다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : M1 좌표 모델 추가.
/// </summary>
/// ---------------------------------------------------------------------
public readonly record struct PixelBounds(int X, int Y, int Width, int Height)
{
    public long Right => (long)X + Width;
    public long Bottom => (long)Y + Height;
}
