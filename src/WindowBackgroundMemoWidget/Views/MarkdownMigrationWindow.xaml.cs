using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using WindowBackgroundMemoWidget.Services;

namespace WindowBackgroundMemoWidget.Views;

/// <summary>name: MarkdownMigrationWindow; desc: 사용자가 지정한 legacy DB/JSON 원본을 미리 보고 명시적으로 Markdown 전환을 확인한다.
/// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 Orig2 MD-02 서비스 연결.</summary>
public partial class MarkdownMigrationWindow : Window
{
    private readonly string destinationDataDirectory;
    private MarkdownMigrationPreview? preview;
    private bool busy;
    private CancellationTokenSource? operationCancellation;
    private bool allowClose;

    /// <summary>name: MarkdownMigrationWindow; desc: 대상 경로를 표시하되 초기화나 변환을 자동 실행하지 않는다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 명시 확인 흐름.</summary>
    public MarkdownMigrationWindow(string dataDirectory)
    {
        InitializeComponent();
        destinationDataDirectory = Path.GetFullPath(dataDirectory);
        DestinationSummary.Text = "대상 Markdown 데이터 폴더: " + destinationDataDirectory;
        Closing += OnWindowClosing;
    }

    /// <summary>name: Report; desc: 확인 완료 후 앱이 대상 저장소를 다시 읽을 때 사용하는 변환 결과.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 migration handoff.</summary>
    public MarkdownMigrationReport? Report { get; private set; }

    /// <summary>name: OnBrowseClick; desc: SQLite DB 또는 JSON 백업을 사용자가 직접 선택한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 원본 선택.</summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = "기존 메모 자료 (*.db;*.sqlite;*.json)|*.db;*.sqlite;*.json|모든 파일 (*.*)|*.*",
            Title = "읽기 전용으로 미리 볼 기존 메모 파일 선택"
        };
        if (dialog.ShowDialog(this) != true) return;
        SourcePathBox.Text = dialog.FileName;
        preview = null;
        ConfirmButton.IsEnabled = false;
        PreviewSummary.Text = "선택한 파일의 미리보기를 실행하세요.";
        HashSummary.Text = "";
        StatusText.Text = "";
    }

    /// <summary>name: OnPreviewClick; desc: 파일 형식과 건수/해시만 읽어 변환 전 요약을 표시한다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 read-only preview.</summary>
    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (busy || string.IsNullOrWhiteSpace(SourcePathBox.Text)) return;
        SetBusy(true);
        try
        {
            operationCancellation = new CancellationTokenSource();
            preview = await ReadPreviewAsync(SourcePathBox.Text, operationCancellation.Token);
            PreviewSummary.Text = $"형식: {preview.Format}\n스키마 버전: {preview.SchemaVersion}\n메모 수: {preview.NoteCount}";
            HashSummary.Text = $"원본 SHA-256: {preview.SourceSha256}\n내용 SHA-256: {preview.ContentSha256}" +
                (string.IsNullOrEmpty(preview.WalSha256) ? "" : $"\nSQLite WAL SHA-256: {preview.WalSha256}");
            StatusText.Text = "미리보기 완료. 확인을 누르기 전까지 변환되지 않습니다.";
            ConfirmButton.IsEnabled = true;
        }
        catch (Exception error)
        {
            preview = null;
            ConfirmButton.IsEnabled = false;
            StatusText.Text = error is OperationCanceledException ? "미리보기를 취소했습니다. 원본과 대상은 변경하지 않았습니다." : "미리보기를 열지 못했습니다. 원본은 변경하지 않았습니다. " + error.Message;
        }
        finally { operationCancellation?.Dispose(); operationCancellation = null; SetBusy(false); }
    }

    /// <summary>name: OnConfirmClick; desc: 사용자가 명시한 변환만 실행하고 검증 보고서가 성공한 경우에만 대화상자를 닫는다.
    /// author: NotedexOrig3; create date: 2026-10-24; update date: 2026-10-24 staged migration.</summary>
    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (Report is not null)
        {
            allowClose = true;
            DialogResult = true;
            return;
        }
        if (preview is null) return;
        MessageBoxResult choice = MessageBox.Show(this,
            $"{preview.NoteCount}개 메모를 새 Markdown 저장 위치로 복사하고 검증합니다.\n\n원본은 유지됩니다. 계속할까요?",
            "Markdown 변환 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            operationCancellation = new CancellationTokenSource();
            Report = IsJson(preview.SourcePath)
                ? await MarkdownMigrationService.MigrateJsonAsync(preview.SourcePath, destinationDataDirectory, operationCancellation.Token, preview.SourceSha256)
                : await MarkdownMigrationService.MigrateDatabaseAsync(preview.SourcePath, destinationDataDirectory, operationCancellation.Token, preview.SourceSha256);
            if (!Report.SourcePreserved) throw new IOException("원본 보존 검증이 완료되지 않았습니다.");
            StatusText.Text = $"변환 및 검증 완료: {Report.NoteCount}개 메모. 원본은 보존됐습니다.";
            ConfirmButton.Content = "Markdown으로 계속";
            CancelButton.Content = "변환 되돌리고 취소";
            StatusText.Text += " 계속을 누르면 앱을 새 저장소로 시작합니다. 되돌리기를 누르면 새 저장소를 삭제하지 않고 보관 이름으로 옮깁니다.";
        }
        catch (Exception error)
        {
            Report = null;
            StatusText.Text = error is OperationCanceledException
                ? "변환을 중단했습니다. 원본은 변경되지 않았습니다. staging이 남아 있다면 상태를 확인한 뒤 재시도하세요."
                : "변환이 완료되지 않았습니다. 원본은 보존됩니다. 검증 실패 시 staging 자료를 남겨 점검할 수 있습니다. " + error.Message;
        }
        finally { operationCancellation?.Dispose(); operationCancellation = null; SetBusy(false); }
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (busy)
        {
            operationCancellation?.Cancel();
            StatusText.Text = "중단 요청을 보냈습니다. 진행 중인 읽기/검증이 안전하게 멈출 때까지 기다립니다.";
            return;
        }
        if (Report is not null)
        {
            MessageBoxResult choice = MessageBox.Show(this,
                "새 Markdown 저장소를 비활성화하고 별도 보관 이름으로 옮깁니다. 기존 원본 파일은 그대로 유지됩니다. 계속할까요?",
                "Markdown 변환 되돌리기", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes) return;
            SetBusy(true);
            try
            {
                string retained = await MarkdownMigrationService.RollbackAsync(destinationDataDirectory);
                Report = null;
                StatusText.Text = "변환을 되돌렸습니다. 생성된 Markdown 자료는 보관 폴더에 유지되고 원본은 변경되지 않았습니다. " + retained;
                allowClose = true;
                DialogResult = false;
            }
            catch (Exception error)
            {
                StatusText.Text = "변환을 되돌리지 못했습니다. Markdown 저장소와 원본 자료는 그대로 유지됩니다. " + error.Message;
            }
            finally { SetBusy(false); }
            return;
        }
        DialogResult = false;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (allowClose) return;
        if (busy)
        {
            e.Cancel = true;
            operationCancellation?.Cancel();
            StatusText.Text = "창 닫기와 함께 중단을 요청했습니다. 작업이 안전하게 멈출 때까지 기다립니다.";
        }
        else if (Report is not null)
        {
            e.Cancel = true;
            StatusText.Text = "변환 결과를 유지하려면 ‘Markdown으로 계속’, 되돌리려면 ‘변환 되돌리고 취소’를 선택하세요.";
        }
    }

    private static Task<MarkdownMigrationPreview> ReadPreviewAsync(string path, CancellationToken token)
        => IsJson(path) ? MarkdownMigrationService.PreviewJsonAsync(path, token) : MarkdownMigrationService.PreviewDatabaseAsync(path, token);

    private static bool IsJson(string path) => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    private void SetBusy(bool value)
    {
        busy = value;
        SourcePathBox.IsEnabled = !value;
        PreviewButton.IsEnabled = !value && !string.IsNullOrWhiteSpace(SourcePathBox.Text);
        ConfirmButton.IsEnabled = !value && preview is not null;
        CancelButton.IsEnabled = true;
        if (busy) CancelButton.Content = "중단 요청";
        else CancelButton.Content = Report is null ? "취소" : "변환 되돌리고 취소";
        System.Windows.Input.Mouse.OverrideCursor = value ? System.Windows.Input.Cursors.Wait : null;
    }
}
