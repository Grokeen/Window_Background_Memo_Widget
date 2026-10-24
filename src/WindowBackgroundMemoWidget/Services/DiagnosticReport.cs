using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using WindowBackgroundMemoWidget.Interop;
using Forms = System.Windows.Forms;

namespace WindowBackgroundMemoWidget.Services;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : ProbeCheck
/// desc         : 본문 없이 검증 이름·판정·근거만 보관한다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-24 NotedexOrig2 : 검증 레코드 설명 추가.
/// </summary>
/// ---------------------------------------------------------------------
public sealed record ProbeCheck(string Name, string Result, string Detail);

/// ---------------------------------------------------------------------
/// <summary>
/// name         : DiagnosticReport
/// desc         : 환경 및 검증 결과만 JSON으로 내보낸다. 메모 본문·사용자명은 수집하지 않는다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : M1 진단 보고 형식 추가.
/// </summary>
/// ---------------------------------------------------------------------
public sealed class DiagnosticReport
{
    public int SchemaVersion { get; } = 1;
    public string Stage { get; } = "M1 prototype - not a release acceptance result";
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public string OperatingSystem { get; } = RuntimeInformation.OSDescription;
    public string Architecture { get; } = RuntimeInformation.ProcessArchitecture.ToString();
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public uint MemoDpi { get; set; }
    public string ShellHostClass { get; set; } = "Not detected";
    public object[] Monitors { get; } = Forms.Screen.AllScreens.Select(screen => (object)new
    {
        screen.DeviceName, screen.Primary,
        Bounds = new { screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height },
        WorkArea = new { screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height }
    }).ToArray();
    public List<ProbeCheck> Checks { get; } = [];
    public string[] ManualChecksPending { get; } =
    [
        "Win+D visibility and desktop icon interaction",
        "Physical Korean IME composition and clipboard editing",
        "100/125/150 percent scale and mixed-DPI monitors",
        "Monitor disconnect/reconnect",
        "Explorer restart and sleep/resume",
        "Idle resource use and long-running stability"
    ];

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : CaptureWindow
    /// desc         : 창 정보 스냅샷 및 본문 없는 진단 JSON 기록. 파일 실패는 호출자에게 전달한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : UTF-8 내보내기 구현.
    /// </summary>
    /// ---------------------------------------------------------------------
    internal void CaptureWindow(nint hwnd)
    {
        MemoDpi = NativeMethods.GetDpiForWindow(hwnd);
        nint host = DesktopHost.FindIconHost();
        ShellHostClass = host == 0 ? "Not detected" : DesktopHost.GetClass(host);
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Write
    /// desc         : 진단 JSON을 UTF-8로 기록하고 파일 접근 실패는 호출자에게 알린다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    public void Write(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
