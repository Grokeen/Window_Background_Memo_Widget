using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowBackgroundMemoWidget.Interop;
using WindowBackgroundMemoWidget.ViewModels;
using WindowBackgroundMemoWidget.Views;
using Forms = System.Windows.Forms;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: NotesApplication; desc: 정상 시작 창·트레이·종료와 격리 통합 진단을 연결한다. 자동 실행 등록은 하지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 제품 UI 진입.</summary>
internal sealed class NotesApplication : IDisposable
{
    private readonly NotesManagerViewModel model;
    private readonly NotesManagerWindow window;
    private readonly IDesktopHost host = new NotesDesktopHost();
    private readonly string profile;
    private readonly bool integration;
    private readonly UserPreferencesStore preferences;
    private UserGuideWindow? guide;
    private bool guideAutomatic;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? menu;
    private System.Drawing.Icon? icon;
    private DispatcherTimer? probeTimer;
    private DispatcherTimer? widgetRecoveryTimer;
    private bool exiting;
    private int activations;
    private readonly uint activateMessage;
    private static readonly uint TaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    /// <summary>name: NotesApplication; desc: UI는 주입된 실제 VM으로 생성하고 수명을 소유한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 관리 창 통합.</summary>
    internal NotesApplication(NotesManagerViewModel viewModel, string dataProfile, bool isolatedIntegration, string instanceScope)
    {
        using var measurement = StartupTelemetry.Measure("manager-construction");
        model = viewModel; profile = dataProfile; integration = isolatedIntegration; activateMessage = RegisterActivation(instanceScope);
        preferences = new(profile);
        window = new() { DataContext = model, Style = (Style)Application.Current.FindResource("AppWindowStyle"), Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/WindowMemo.ico")) };
        model.CurrentEditorHasUncommittedInput = () => ManagerHasUncommittedInput();
        model.AnyUncommittedInputExists = () => ManagerHasUncommittedInput() || WidgetHasUncommittedInput();
        model.NoteHasUncommittedInput = id => ManagerHasUncommittedInput(id) || WidgetHasUncommittedInput(id);
        model.ExitRequested += Exit;
        window.HelpRequested += OnHelpRequested;
    }

    /// <summary>name: Start; desc: 정상 interactive 창과 트레이를 만들고 실제 HWND를 진단한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 일반 시작 증거.</summary>
    internal void Start()
    {
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(WindowMessage);
        window.Closing += OnClosing;
        Application.Current.MainWindow = window;
        // HKCU 로그인 실행도 무인수 정상 경로를 사용한다. 초기 표시에서 다른 앱의 포커스를 빼앗지 않는다.
        window.ShowActivated = false;
        using (StartupTelemetry.Measure("manager-show")) window.Show();
        using Stream resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/WindowMemo.ico"))!.Stream;
        using System.Drawing.Icon loaded = new(resource);
        icon = (System.Drawing.Icon)loaded.Clone();
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("메모 관리 열기", null, (_, _) => Activate());
        menu.Items.Add("새 메모", null, (_, _) => { Activate(); model.NewMemoCommand.Execute(null); });
        menu.Items.Add("전체 숨기기/다시 표시", null, (_, _) => model.GlobalHidden = !model.GlobalHidden);
        menu.Items.Add("모든 메모 배치 초기화", null, (_, _) => model.ResetAllPlacementsCommand.Execute(null));
        menu.Items.Add("백업 내보내기", null, (_, _) => model.ExportCommand.Execute(null));
        menu.Items.Add("백업 복원", null, (_, _) => model.ImportCommand.Execute(null));
        menu.Items.Add("사용 안내 / PDF", null, (_, _) => ShowGuide(manual: true));
        menu.Items.Add("종료", null, (_, _) => Exit());
        tray = new Forms.NotifyIcon { Text = "바탕화면 메모", Icon = icon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Activate();
        model.WidgetStateChanged += Synchronize;
        Synchronize();
        widgetRecoveryTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Synchronize(), window.Dispatcher);
        bool showAutomatically = true;
        try { showAutomatically = !preferences.Load().DoNotShowUserGuide; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        { model.StatusMessage = "사용 안내 설정을 읽지 못했습니다. 기존 설정은 보존하고 안내를 표시합니다."; }
        if (showAutomatically) ShowGuide(manual: false);
        WriteProbe("ready");
        if (integration)
        {
            if (File.Exists(Path.Combine(profile, "ui-fixture.request"))) _ = RunUiProbeAsync();
            DateTime deadline = DateTime.UtcNow.AddMinutes(2);
            probeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
            { if (File.Exists(Path.Combine(profile, "shutdown.request")) || DateTime.UtcNow >= deadline) Exit(); }, window.Dispatcher);
        }
    }
    /// <summary>name: RunUiProbeAsync; desc: 실제 UI 검사 커밋 뒤 재시작 대조용 본문 없는 해시를 갱신한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 강제 종료 검증.</summary>
    private async Task RunUiProbeAsync() { await NotesUiProbe.RunAsync(window, model, profile); WriteProbe("ui-complete"); }

    /// <summary>name: ActivateExisting; desc: 사용자별 기존 인스턴스에 활성화 메시지를 전송한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 단일 실행.</summary>
    internal static void ActivateExisting(string scope) => NativeMethods.PostMessage(NativeMethods.HwndBroadcast, RegisterActivation(scope), 0, 0);
    /// <summary>name: RegisterActivation; desc: 격리 시험이 실제 사용자 인스턴스를 활성화하지 않게 메시지도 프로필별 분리한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 프로필 경계.</summary>
    private static uint RegisterActivation(string scope) => NativeMethods.RegisterWindowMessage("Notedex.Notes.Activate." + scope);
    /// <summary>name: WindowMessage; desc: 중복 실행 활성화와 Explorer 트레이 재생성을 처리한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 일반 시작 메시지.</summary>
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == activateMessage) { activations++; Activate(); WriteProbe("activated"); handled = true; }
        if ((uint)message == TaskbarCreated && tray != null) { tray.Visible = false; tray.Visible = true; }
        return 0;
    }
    /// <summary>name: Activate; desc: 명시적 요청 때만 관리 창을 앞으로 연다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 창 활성화.</summary>
    private void Activate() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }

    private void OnHelpRequested(object? sender, EventArgs e) => ShowGuide(manual: true);

    /// <summary>name: ShowGuide; desc: 자동/수동 안내의 단일 창 수명을 공유하고 자동 창에서만 선호를 저장한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 migration 이후 안내·트레이/관리 재열기.</summary>
    private void ShowGuide(bool manual)
    {
        if (exiting) return;
        if (manual) Activate();
        if (guide != null)
        { guide.Show(); if (guide.WindowState == WindowState.Minimized) guide.WindowState = WindowState.Normal; guide.Activate(); return; }
        UserGuideWindow created = new(initialDoNotShowAgain: false, showDoNotShowAgain: !manual)
        { Owner = window, ShowActivated = !integration || manual };
        guide = created; guideAutomatic = !manual;
        created.Closed += (_, _) =>
        {
            // 이 창을 만든 호출 이유를 캡처해 재열기 요청이 자동/수동 저장 책임을 바꾸지 않게 한다.
            if (!manual)
            {
                try { preferences.Save(created.DoNotShowAgain); }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
                { model.StatusMessage = "사용 안내 설정을 저장하지 못했습니다. 다음 실행에서 안내가 다시 표시될 수 있습니다."; }
            }
            if (ReferenceEquals(guide, created)) { guide = null; guideAutomatic = false; }
        };
        using (StartupTelemetry.Measure("user-guide-show")) created.Show();
    }
    /// <summary>name: Synchronize; desc: 저장/관리 모델을 교체 가능한 표시 경계에 반영한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 표시 오류 안내.</summary>
    private void Synchronize()
    { try { host.Synchronize(model.AllMemos, model.GlobalHidden); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { model.StatusMessage = "위젯 표시 실패 · 관리 창에서 원문을 확인하세요."; } }
    /// <summary>name: OnClosing; desc: 닫기 버튼은 관리 창만 숨기며 트레이 종료는 별도다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 사용자 수명.</summary>
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; window.Hide(); } }
    /// <summary>name: Exit; desc: 실제 커밋 성공 후에만 창/트레이를 해제하며 실패 입력은 유지한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 데이터 보존 종료.</summary>
    private async void Exit()
    {
        if (exiting) return;
        if (model.AnyUncommittedInputExists?.Invoke() == true)
        {
            model.PendingInputMessage();
            model.StatusMessage = "보류 편집본을 다른 파일로 저장하거나 한글 조합을 마친 뒤 종료하세요.";
            Activate();
            if (!integration) MessageBox.Show(window, model.StatusMessage, "종료 전 입력 확인", MessageBoxButton.OK, MessageBoxImage.Information);
            if (ManagerHasUncommittedInput()) ((NoteDocumentEditor)window.FindName("DocumentEditor")).FocusEditor();
            else if (!host.RevealUncommittedInput())
            {
                // 격리 probe에서 직접 만든 창도 같은 앱의 volatile 입력 보호 범위에 포함한다.
                NoteWidgetWindow? recoveryWindow = Application.Current.Windows.OfType<NoteWidgetWindow>().FirstOrDefault(w => w.DocumentEditor?.HasUncommittedInput == true);
                if (recoveryWindow != null) { recoveryWindow.Show(); recoveryWindow.Activate(); recoveryWindow.DocumentEditor?.FocusEditor(); }
            }
            return;
        }
        exiting = true; window.IsEnabled = false;
        host.Synchronize(model.AllMemos, true);
        if (!await model.FlushAsync()) { exiting = false; window.IsEnabled = true; Synchronize(); Activate(); return; }
        Dispose(); WriteProbe("stopped"); window.Close(); Application.Current.Shutdown();
    }
    private bool ManagerHasUncommittedInput(string? noteId = null)
        => window.FindName("DocumentEditor") is NoteDocumentEditor editor && editor.HasUncommittedInput && (noteId == null || editor.Session?.NoteId.Equals(noteId, StringComparison.OrdinalIgnoreCase) == true);
    private bool WidgetHasUncommittedInput(string? noteId = null)
        => host.HasUncommittedInput(noteId) || Application.Current.Windows.OfType<NoteWidgetWindow>().Any(w => w.DocumentEditor?.HasUncommittedInput == true && (noteId == null || w.DataContext is MemoListItemViewModel item && item.Id.Equals(noteId, StringComparison.OrdinalIgnoreCase)));
    /// <summary>name: WriteProbe; desc: 본문 없이 실제 HWND/트레이/활성화/종료 상태를 원자적으로 기록한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 통합 검사.</summary>
    private void WriteProbe(string stage)
    {
        if (!integration) return;
        string path = Path.Combine(profile, "startup-probe.json");
        string temporary = path + ".tmp";
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model.Snapshot().Notes.Select(n => new { n.Id, n.Title, n.Body, n.CreatedAt, n.UpdatedAt, n.FolderId, n.Tags, n.IsPinned, n.Priority, n.IsDeleted, n.IsVisible })))));
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { Stage = stage, ProcessId = Environment.ProcessId, Hwnd = new WindowInteropHelper(window).Handle.ToInt64(), WindowVisible = window.IsVisible, TrayCreated = tray?.Visible == true, Activations = activations, NoteCount = model.AllMemos.Count, GuideVisible = guide?.IsVisible == true, GuideAutomatic = guideAutomatic, ContentSHA256 = fingerprint, StartupDiagnostics = StartupTelemetry.Snapshot(), Utc = DateTimeOffset.UtcNow }));
        File.Move(temporary, path, true);
    }
    /// <summary>name: Dispose; desc: 모든 소유 핸들과 구독을 정리한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 종료 자원 정리.</summary>
    public void Dispose()
    { probeTimer?.Stop(); widgetRecoveryTimer?.Stop(); window.HelpRequested -= OnHelpRequested; guide?.Close(); model.WidgetStateChanged -= Synchronize; model.ExitRequested -= Exit; host.Dispose(); tray?.Dispose(); tray = null; menu?.Dispose(); icon?.Dispose(); model.Dispose(); }
}
