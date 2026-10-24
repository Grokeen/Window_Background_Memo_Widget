using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using WindowBackgroundMemoWidget.Interop;
using WindowBackgroundMemoWidget.Services;
using WindowBackgroundMemoWidget.Views;
using WindowBackgroundMemoWidget.ViewModels;

namespace WindowBackgroundMemoWidget;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : App
/// desc         : 사용자별 단일 인스턴스 실행과 별도 M1 자동 검증 진입점을 제공한다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : .NET 10 WPF 부트스트랩 추가.
///                2026-10-24 NotedexOrig2 : Notes 관리/로컬 저장/격리 일반 시작 진단 연결.
/// </summary>
/// ---------------------------------------------------------------------
public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;
    private NotesApplication? notesApplication;

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnStartup
    /// desc         : 자동 검증은 사용자 입력 및 트레이를 만들지 않고 결과 파일과 종료 코드로 보고한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : 중복 실행 활성화 및 진단 인수 처리.
    /// </summary>
    /// ---------------------------------------------------------------------
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            int code = await SmokeProbe.RunAsync(e.Args[1]);
            Shutdown(code);
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--m1-probe")
        {
            MainWindow probe = new(); MainWindow = probe; probe.Show(); return;
        }
        if (e.Args.Length != 0)
        {
            MessageBox.Show("지원 인수: --smoke-test <결과 JSON 경로>", "M1 검증 앱");
            Shutdown(1);
            return;
        }

        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notedex", "Notes");
        bool isolated = false;
        string? testProfile = Environment.GetEnvironmentVariable("NOTEDEX_TEST_PROFILE");
        if (!string.IsNullOrEmpty(testProfile))
        {
            string candidate = Path.GetFullPath(testProfile);
            string permitted = Path.Combine(Path.GetTempPath(), "Notedex-Integration-");
            if (!candidate.StartsWith(permitted, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(candidate, "integration.marker")))
            { Shutdown(3); return; }
            profile = candidate; isolated = true;
        }
        StartupTelemetry.Enable(isolated);
        try
        {
            Directory.CreateDirectory(profile);
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToUpperInvariant())))[..16];
            instanceMutex = new Mutex(true, $"Local\\Notedex.Notes.{sid}.{scope}", out ownsMutex);
            if (!ownsMutex) { NotesApplication.ActivateExisting(scope); Shutdown(); return; }
            if ((File.Exists(Path.Combine(profile, "notes.db")) || File.Exists(Path.Combine(profile, "markdown-rollback.json"))) && !Directory.Exists(Path.Combine(profile, "markdown-v1")))
            {
                // 기존 사용자 자료를 자동 변환하지 않는다. 명시 preview/확인 창을 통해서만 전환한다.
                MarkdownMigrationWindow migration = new(profile);
                if (migration.ShowDialog() != true) { Shutdown(); return; }
            }
            LoginStartupService? startup = isolated ? null : new(new UserRunRegistration(), Environment.ProcessPath ?? "");
            NotesManagerViewModel model = new(new NotesRepository(profile), startup: startup);
            using (StartupTelemetry.Measure("model-load")) await model.InitializeAsync();
            notesApplication = new NotesApplication(model, profile, isolated, scope);
            notesApplication.Start();
        }
        catch (Exception error)
        {
            // 일반 프로필 로그에는 메모/개인 경로가 섞일 수 있는 예외 상세를 남기지 않는다.
            // 격리된 합성 UI 검사 프로필에서만 XAML 시작 실패의 실제 원인을 진단한다.
            try { File.WriteAllText(Path.Combine(profile, "startup-failure.json"), JsonSerializer.Serialize(new { Stage = "managed-startup", ErrorType = error.GetType().FullName, Diagnostic = isolated ? error.ToString() : null, Utc = DateTimeOffset.UtcNow })); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            if (!isolated) MessageBox.Show("메모를 시작하지 못했습니다. 기존 데이터는 보존됩니다. 데이터 폴더의 startup-failure.json을 확인하세요.", "바탕화면 메모");
            Shutdown(1);
        }
    }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : OnExit
    /// desc         : 소유한 단일 실행 뮤텍스만 해제하고 OS 핸들을 정리한다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-24 NotedexOrig2 : M1 소스 설명과 유지보수 이력 보완.
    /// </summary>
    /// ---------------------------------------------------------------------
    protected override void OnExit(ExitEventArgs e)
    {
        notesApplication?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
