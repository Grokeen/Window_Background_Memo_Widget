using System;
using System.Collections.Generic;
using WindowBackgroundMemoWidget.Models;
using WindowBackgroundMemoWidget.Services;
using WindowBackgroundMemoWidget.ViewModels;

namespace WindowBackgroundMemoWidget.Tests;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : Program
/// desc         : 화면 밖 배치·음수 모니터·잘못된 좌표와 임시 입력 상태를 검증하는 무패키지 테스트 실행기.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : M1 회귀 위험 중심 검증 추가.
/// </summary>
/// ---------------------------------------------------------------------
internal static class Program
{
    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Main
    /// desc         : 명시한 기대 위치와 실제 결과를 비교하고 실패 시 1을 반환한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 단일·다중 화면 경계 검증.
    /// </summary>
    /// ---------------------------------------------------------------------
    [STAThread]
    private static int Main()
    {
        PixelBounds primary = new(0, 0, 1920, 1032);
        List<(string Name, Action Verify)> cases =
        [
            ("valid placement unchanged", () => Equal(new(100, 200, 380, 340), ScreenPlacement.Clamp(new(100, 200, 380, 340), [primary]))),
            ("disconnected monitor recovered", () => Equal(new(1540, 692, 380, 340), ScreenPlacement.Clamp(new(2600, 1300, 380, 340), [primary]))),
            ("negative-coordinate monitor preserved", () => Equal(new(-1500, 100, 380, 340), ScreenPlacement.Clamp(new(-1500, 100, 380, 340), [new(-1920, 0, 1920, 1080), primary]))),
            ("largest overlap selects correct monitor", () => Equal(new(1920, 100, 500, 340), ScreenPlacement.Clamp(new(1800, 100, 500, 340), [primary, new(1920, 0, 1920, 1080)]))),
            ("oversized memo fits working area", () => Equal(new(0, 0, 1920, 1032), ScreenPlacement.Clamp(new(-100, -100, 3000, 2000), [primary]))),
            ("tiny work area remains accessible", () => Equal(new(20, 30, 120, 100), ScreenPlacement.Clamp(new(500, 500, 380, 340), [new(20, 30, 120, 100)]))),
            ("extreme coordinates do not overflow", () => Equal(new(1540, 0, 380, 340), ScreenPlacement.Clamp(new(int.MaxValue, int.MinValue, 380, 340), [primary]))),
            ("empty monitors rejected", () => Throws(() => ScreenPlacement.Clamp(new(0, 0, 380, 340), []))),
            ("invalid memo size rejected", () => Throws(() => ScreenPlacement.Clamp(new(0, 0, 0, 340), [primary]))),
            ("invalid monitor size rejected", () => Throws(() => ScreenPlacement.Clamp(new(0, 0, 380, 340), [new(0, 0, -1, 100)]))),
            ("edit state and dependent notifications", VerifyInputState),
            ("persistent Notes management integration", NotesChecks.Run)
        ];
        int failures = 0;
        // 실패한 케이스도 수집해 다음 검증을 계속하고 총 결과를 반환한다.
        foreach ((string name, Action verify) in cases)
        {
            try { verify(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
        }
        Console.WriteLine($"{cases.Count - failures}/{cases.Count} checks passed");
        return failures == 0 ? 0 : 1;
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Equal
    /// desc         : 예상한 물리 픽셀 배치와 실제 결과가 같은지 검사한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private static void Equal(PixelBounds expected, PixelBounds actual)
    {
        if (expected != actual) throw new InvalidOperationException($"Expected {expected}; actual {actual}");
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Throws
    /// desc         : 잘못된 좌표 입력이 ArgumentException으로 거절되는지 검사한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private static void Throws(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected ArgumentException");
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : VerifyInputState
    /// desc         : 편집 여부와 글자 수의 변경 알림이 입력 상태와 일치하는지 검사한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    private static void VerifyInputState()
    {
        ProbeViewModel state = new();
        HashSet<string> notified = [];
        state.PropertyChanged += (_, e) => notified.Add(e.PropertyName!);
        state.MemoText = "한글\nEnglish 📝";
        if (!state.HasEdits || state.CharacterCount != state.MemoText.Length || !notified.SetEquals(["MemoText", "CharacterCount", "HasEdits"]))
            throw new InvalidOperationException("Edit state or dependent notifications are inconsistent");
    }
}
