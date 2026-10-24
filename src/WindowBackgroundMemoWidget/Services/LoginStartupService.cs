using System;
using System.IO;
using Microsoft.Win32;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: IStartupRegistrationBackend; desc: 등록 대상이 사용자 Run인지 메모리 시험인지 분리하는 경계.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 실제 PC 등록 없는 검사.</summary>
public interface IStartupRegistrationBackend
{
    string? ReadCommand();
    bool HasWindowsOverride();
    void WriteCommand(string command);
    void RemoveCommand();
}

/// <summary>name: UserRunRegistration; desc: HKCU Run의 앱 전용 값 하나만 관리한다. Windows 허용 정책은 읽기만 한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 per-user 자동 실행.</summary>
public sealed class UserRunRegistration : IStartupRegistrationBackend
{
    public const string ValueName = "Notedex.Notes";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>name: ReadCommand; desc: 관리자 권한 없이 자기 등록만 읽는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 조회.</summary>
    public string? ReadCommand() { using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunPath); return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string; }
    /// <summary>name: HasWindowsOverride; desc: Windows 시작 앱의 별도 설정 존재만 확인한다. 비공식 binary 형식을 활성/비활성으로 추정하거나 수정하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 외부 선택 존중.</summary>
    public bool HasWindowsOverride() { using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"); return key?.GetValue(ValueName) != null; }
    /// <summary>name: WriteCommand; desc: 명시적 사용자 켜기 동작에서만 자기 REG_SZ를 쓴다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 사용자 선택 등록.</summary>
    public void WriteCommand(string command) { using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunPath, true); key.SetValue(ValueName, command, RegistryValueKind.String); }
    /// <summary>name: RemoveCommand; desc: 다른 앱 항목과 Windows 승인 설정은 지우지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 자기 항목만 해제.</summary>
    public void RemoveCommand() { using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunPath, true); key?.DeleteValue(ValueName, false); }
}

/// <summary>name: LoginStartupState; desc: 등록 존재와 실행 가능 경로, Windows 별도 정책을 구분한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 상태 계약.</summary>
public sealed record LoginStartupState(bool Registered, bool CurrentPath, string Message);

/// <summary>name: LoginStartupService; desc: 기본 꺼짐/명시적 켜기/끄기만 실행하며 시작 시 누락·외부 해제를 자동 복구하지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 quoting/이동/실패 처리.</summary>
public sealed class LoginStartupService(IStartupRegistrationBackend backend, string executablePath, Func<string, bool>? exists = null)
{
    private readonly Func<string, bool> fileExists = exists ?? File.Exists;
    /// <summary>name: Inspect; desc: 읽기만 수행하며 오래된 경로와 Windows 외부 설정을 성공으로 숨기지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 stale 경로 점검.</summary>
    public LoginStartupState Inspect()
    {
        string? command = backend.ReadCommand();
        if (command == null) return new(false, false, "자동 실행 꺼짐 · 수동 실행할 수 있습니다.");
        string expected = BuildCommand(executablePath);
        if (!string.Equals(command, expected, StringComparison.OrdinalIgnoreCase) || !fileExists(executablePath))
            return new(true, false, "자동 실행 경로가 변경/삭제되었습니다. 끈 뒤 현재 위치에서 다시 켜세요.");
        return new(true, true, backend.HasWindowsOverride() ? "등록됨 · Windows 시작 앱의 별도 허용 설정을 확인하세요. 외부에서 끈 설정은 되돌리지 않습니다." : "등록됨 · Windows 로그인 후 실행을 요청합니다. Windows 설정/정책에 따라 실행이 제한될 수 있습니다.");
    }
    /// <summary>name: SetEnabled; desc: 사용자 동작으로만 등록을 바꾸고 쓰기 후 다시 읽어 검증한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 실패를 성공으로 보고하지 않음.</summary>
    public LoginStartupState SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string command = BuildCommand(executablePath);
            if (!fileExists(executablePath)) throw new FileNotFoundException("실행 파일이 없습니다.");
            backend.WriteCommand(command);
            if (!string.Equals(backend.ReadCommand(), command, StringComparison.Ordinal)) throw new IOException("자동 실행 등록 확인 실패.");
        }
        else { backend.RemoveCommand(); if (backend.ReadCommand() != null) throw new IOException("자동 실행 해제 확인 실패."); }
        return Inspect();
    }
    /// <summary>name: BuildCommand; desc: shell 인수를 추가하지 않고 전체 exe 경로를 따옴표로 감싼다. Run 값의 260자 제한을 지킨다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 경로 quoting.</summary>
    public static string BuildCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || executable.Length + 2 > 260)
            throw new ArgumentException("자동 실행에는 258자 이하의 절대 EXE 경로가 필요합니다.", nameof(executable));
        return "\"" + executable + "\"";
    }
}
