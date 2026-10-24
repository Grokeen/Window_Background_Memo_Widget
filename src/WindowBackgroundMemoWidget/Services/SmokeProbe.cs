using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowBackgroundMemoWidget.Interop;
using WindowBackgroundMemoWidget.ViewModels;
using WindowBackgroundMemoWidget.Views;

namespace WindowBackgroundMemoWidget.Services;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : SmokeProbe
/// desc         : 실제 WPF/Win32 창 생성, 결합, 해제, 입력 바인딩과 진단 내보내기의 자동 1차 검증.
///                물리 IME·Win+D·Explorer 재시작 검증을 통과했다고 간주하지 않는다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : 명령행 검증 및 본인 화면 렌더링 추가.
/// </summary>
/// ---------------------------------------------------------------------
internal static class SmokeProbe
{
    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : RunAsync
    /// desc         : 별도 임시 창으로 검증하고 결과 JSON·PNG와 프로세스 종료 코드를 반환한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 실제 셸 부모/스타일 확인과 본문 비수집 검증.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal static async Task<int> RunAsync(string outputPath)
    {
        MainWindow? control = null;
        DiagnosticReport report = new();
        int code;
        try
        {
            #region 일반 창과 셸 자식 창 비교
            nint foreground = NativeMethods.GetForegroundWindow();
            control = new MainWindow(interactive: false);
            report = control.Report;
            control.Show();
            control.OpenMemo(DesktopMode.BottomWindow);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            MemoWindow memo = control.Memo!;
            nint initialStyle = NativeMethods.GetWindowLongPtr(memo.Handle, NativeMethods.GwlStyle);
            nint initialExStyle = NativeMethods.GetWindowLongPtr(memo.Handle, NativeMethods.GwlExStyle);
            Check(report, "normal-window-created", NativeMethods.IsWindow(memo.Handle) && NativeMethods.IsWindowVisible(memo.Handle), "Real WPF HWND is visible");
            Check(report, "dark-theme-window-colors", control.Background is SolidColorBrush mainBackground && mainBackground.Color == Color.FromRgb(0x28, 0x2a, 0x36)
                && control.Foreground is SolidColorBrush mainText && mainText.Color == Color.FromRgb(0xf8, 0xf8, 0xf2)
                && memo.Foreground is SolidColorBrush memoText && memoText.Color == Color.FromRgb(0xf8, 0xf8, 0xf2), "Explicit Dracula window background and readable foreground on both derived windows");
            Check(report, "normal-window-parent", NativeMethods.GetParent(memo.Handle) == 0, "Top-level window has no parent");
            Check(report, "tool-window-style", (initialExStyle.ToInt64() & NativeMethods.WsExToolWindow) != 0 && (initialExStyle.ToInt64() & NativeMethods.WsExAppWindow) == 0, "Tool window style; taskbar and Alt+Tab require manual observation");
            Check(report, "show-without-focus", foreground == NativeMethods.GetForegroundWindow(), "Foreground HWND remained unchanged during non-activating show");

            control.OpenMemo(DesktopMode.ShellChild);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            nint expectedHost = DesktopHost.FindIconHost();
            Check(report, "shell-parent-attached", expectedHost != 0 && memo.Host!.IsAttached && NativeMethods.GetParent(memo.Handle) == expectedHost, $"Detected host class: {report.ShellHostClass}");
            long childStyle = NativeMethods.GetWindowLongPtr(memo.Handle, NativeMethods.GwlStyle).ToInt64();
            Check(report, "shell-child-style", (childStyle & NativeMethods.WsChild) != 0 && (childStyle & NativeMethods.WsPopup) == 0, "WS_CHILD present and WS_POPUP absent");

            const string syntheticBody = "검증전용-본문비수집-한글\nEnglish / emoji 📝\n줄바꿈";
            control.State.MemoText = syntheticBody;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(report, "unicode-text-binding", memo.EditorText == syntheticBody, "Programmatic Unicode binding only; physical IME is pending");
            control.OpenMemo(DesktopMode.BottomWindow);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(report, "detach-restores-parent", !memo.Host!.IsAttached && NativeMethods.GetParent(memo.Handle) == 0, "Parent restored to top-level");
            Check(report, "detach-restores-styles", NativeMethods.GetWindowLongPtr(memo.Handle, NativeMethods.GwlStyle) == initialStyle && NativeMethods.GetWindowLongPtr(memo.Handle, NativeMethods.GwlExStyle) == initialExStyle, "Original style and extended style restored");
            Check(report, "mode-switch-preserves-input", memo.EditorText == syntheticBody, "In-memory text preserved across mode change");
            memo.Hide();
            memo.Show();
            Check(report, "hide-show-preserves-input", memo.EditorText == syntheticBody && NativeMethods.IsWindowVisible(memo.Handle), "Hide and show preserve temporary input");
            memo.Host.RecoverPlacement();
            Check(report, "placement-within-work-area", DesktopHost.WorkAreas().Any(area => memo.Host.Bounds.X >= area.X && memo.Host.Bounds.Y >= area.Y && memo.Host.Bounds.Right <= area.Right && memo.Host.Bounds.Bottom <= area.Bottom), "Memo is entirely within a current monitor work area");
            #endregion

            #region 진단 파일의 본문 비수집 및 본인 화면 검증
            report.Write(outputPath);
            string exported = File.ReadAllText(outputPath);
            using JsonDocument parsed = JsonDocument.Parse(exported);
            Check(report, "diagnostic-json-parse", parsed.RootElement.GetProperty("SchemaVersion").GetInt32() == 1, "Exported report is parseable versioned JSON");
            string serializedBody = JsonSerializer.Serialize(syntheticBody);
            Check(report, "diagnostic-excludes-body", !exported.Contains(serializedBody, StringComparison.Ordinal) && !parsed.RootElement.TryGetProperty("MemoText", out _), "Synthetic memo body is absent from exported diagnostics");

            control.State.MemoText = ProbeViewModel.InitialText;
            control.State.Status = "자동 검증 완료 · Win + D / 한글 IME / 배율 등 실제 조작 검증은 대기 중입니다.";
            control.State.ModeLabel = "M1 검증 앱 · 일반 창 / Explorer 셸 결합 비교";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            Render(control, Path.Combine(directory, "m1-control.png"));
            Render(memo, Path.Combine(directory, "m1-memo.png"));
            Check(report, "wpf-render", true, "Own WPF control and memo content rendered to PNG");
            report.Write(outputPath);
            code = report.Checks.Any(check => check.Result == "FAIL") ? 2 : 0;
            #endregion
        }
        catch (Exception ex)
        {
            // 자동 검증은 비대화식이므로 오류 메시지를 결과 파일에 남기고 실패 코드로 끝낸다.
            report.Checks.Add(new ProbeCheck("probe-exception", "FAIL", $"{ex.GetType().Name}: {ex.Message}"));
            try { report.Write(outputPath); } catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or ArgumentException) { }
            code = 1;
        }
        finally
        {
            control?.Close();
        }
        return code;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Check
    /// desc         : 자동 검증의 기대 조건을 PASS 또는 FAIL로 기록한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private static void Check(DiagnosticReport report, string name, bool passed, string detail) =>
        report.Checks.Add(new ProbeCheck(name, passed ? "PASS" : "FAIL", detail));

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Render
    /// desc         : 사용자 바탕화면을 캡처하지 않고 검증 앱 자신의 WPF 콘텐츠만 PNG로 렌더링한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 화면 검토 근거 생성.
    /// </summary>
    /// ---------------------------------------------------------------------
    private static void Render(Window window, string path)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(content);
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Window 바깥의 사용자 화면 대신, 콘텐츠 뒤에 창 자신의 배경만 합성한다.
        DrawingVisual background = new();
        using (DrawingContext drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background ?? Brushes.Transparent, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(content);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
