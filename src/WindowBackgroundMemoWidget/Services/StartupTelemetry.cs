using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.Linq;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: StartupTelemetry; desc: 격리 테스트에서만 UI/파일/셸 초기화 병목을 집계한다. 메모 ID·본문·사용자 경로는 담지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 실제 성능 목표 미달 원인·단계별 프로세스 CPU 계측.</summary>
internal static class StartupTelemetry
{
    private static bool enabled;
    private static readonly Dictionary<string, (double Milliseconds, int Count, double Max, double CpuMilliseconds)> phases = [];
    internal static void Enable(bool isolated) { enabled = isolated; phases.Clear(); }
    internal static Measurement Measure(string phase) => new(phase, enabled ? Stopwatch.GetTimestamp() : 0);
    internal static void Failure(string phase, Exception error)
    {
        if (!enabled) return;
        string key = "failure:" + phase + ":" + error.GetType().Name + (error is Win32Exception native ? ":" + native.NativeErrorCode : "");
        phases.TryGetValue(key, out var previous); phases[key] = (0, previous.Count + 1, 0, 0);
    }
    internal readonly struct Measurement(string phase, long started) : IDisposable
    {
        private readonly long cpuStarted = started == 0 ? 0 : ReadCpuTicks();
        public void Dispose()
        {
            if (started == 0) return;
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            double cpuElapsed = Math.Max(0, ReadCpuTicks() - cpuStarted) / (double)TimeSpan.TicksPerMillisecond;
            phases.TryGetValue(phase, out var previous);
            phases[phase] = (previous.Milliseconds + elapsed, previous.Count + 1, Math.Max(previous.Max, elapsed), previous.CpuMilliseconds + cpuElapsed);
        }
    }
    /// <summary>name: ReadCpuTicks; desc: 격리 계측에서만 현재 프로세스의 모든 스레드 CPU를 읽고 핸들을 해제한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 wall/native 대기 구분.</summary>
    private static long ReadCpuTicks()
    { using Process process = Process.GetCurrentProcess(); return process.TotalProcessorTime.Ticks; }
    /// <summary>name: Snapshot; desc: 강제 GC를 하지 않고 ready 시점의 힙과 private bytes를 읽는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 진단과 목표 판정을 분리.</summary>
    internal static object? Snapshot()
    {
        if (!enabled) return null;
        using Process process = Process.GetCurrentProcess();
        return new { Phases = phases.Select(p => new { Phase = p.Key, TotalMilliseconds = Math.Round(p.Value.Milliseconds, 2), p.Value.Count, MaxMilliseconds = Math.Round(p.Value.Max, 2), TotalCpuMilliseconds = Math.Round(p.Value.CpuMilliseconds, 2) }).ToArray(), ManagedBytes = GC.GetTotalMemory(false), HeapCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes, PrivateBytes = process.PrivateMemorySize64 };
    }
}
