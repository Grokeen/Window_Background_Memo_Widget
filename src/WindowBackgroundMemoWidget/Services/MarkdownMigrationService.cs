using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WindowBackgroundMemoWidget.Models;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: MarkdownMigrationPreview; desc: 명시적 전환 전에 원본 종류/개수/해시를 사용자에게 보여준다. 본문은 포함하지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 MD-02.</summary>
public sealed record MarkdownMigrationPreview(string SourcePath, string Format, int SchemaVersion, int NoteCount,
    string SourceSha256, string ContentSha256, string WalSha256);

/// <summary>name: MarkdownMigrationReport; desc: 전환 확정 경로와 원본 보존 대조 결과.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 이행 증거.</summary>
public sealed record MarkdownMigrationReport(string DestinationRoot, string ManifestPath, int NoteCount,
    string SourceSha256, bool SourcePreserved);

/// <summary>name: MarkdownMigrationService; desc: read-only 기존 자료를 별도 staging에서 검증한 후 새 root로만 확정한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 원본 삭제/덮어쓰기 없음.</summary>
public static class MarkdownMigrationService
{
    internal static Action? BeforePublishForTest { get; set; }
    /// <summary>name: PreviewDatabaseAsync; desc: SQLite를 읽기 전용으로 열어 지원 버전과 원문/분류를 확인한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 명시 preview.</summary>
    public static async Task<MarkdownMigrationPreview> PreviewDatabaseAsync(string path, CancellationToken cancellationToken = default)
    { var data = await ReadAsync(path, true, cancellationToken); return data.Preview; }
    /// <summary>name: PreviewJsonAsync; desc: 기존 JSON 백업은 수정하지 않고 읽는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 호환 preview.</summary>
    public static async Task<MarkdownMigrationPreview> PreviewJsonAsync(string path, CancellationToken cancellationToken = default)
    { var data = await ReadAsync(path, false, cancellationToken); return data.Preview; }
    /// <summary>name: MigrateDatabaseAsync; desc: 사용자 명시 동작에서만 read-only DB를 새 .md 저장소로 옮긴다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 staged migration.</summary>
    public static Task<MarkdownMigrationReport> MigrateDatabaseAsync(string path, string destinationDataDirectory, CancellationToken cancellationToken = default, string? expectedSourceHash = null)
        => MigrateAsync(path, destinationDataDirectory, true, cancellationToken, expectedSourceHash);
    /// <summary>name: MigrateJsonAsync; desc: 검증 legacy JSON을 같은 안전 절차로 변환한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 JSON migration.</summary>
    public static Task<MarkdownMigrationReport> MigrateJsonAsync(string path, string destinationDataDirectory, CancellationToken cancellationToken = default, string? expectedSourceHash = null)
        => MigrateAsync(path, destinationDataDirectory, false, cancellationToken, expectedSourceHash);

    /// <summary>name: ReadAsync; desc: 파일 fingerprint와 논리 snapshot을 함께 검증한다. SQLite 쓰기 연결은 만들지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 기존 자료 보호.</summary>
    private static async Task<(NotesSnapshot Snapshot, MarkdownMigrationPreview Preview)> ReadAsync(string path, bool database, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Path.GetFullPath(path);
        string before = Hash(full), wal = File.Exists(full + "-wal") ? Hash(full + "-wal") : "";
        NotesSnapshot snapshot;
        if (database)
        {
            snapshot = await Task.Run(() =>
            {
                using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
                connection.Open(); using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidDataException("지원하지 않는 기존 DB 버전입니다.");
                command.CommandText = "PRAGMA quick_check";
                if (command.ExecuteScalar() as string != "ok") throw new InvalidDataException("기존 DB 검사 실패. 원본을 보존했습니다.");
                command.CommandText = "SELECT payload FROM library WHERE id=1";
                return JsonSerializer.Deserialize<NotesSnapshot>(command.ExecuteScalar() as string ?? "null") ?? throw new InvalidDataException("기존 본문이 없습니다.");
            }, cancellationToken);
        }
        else snapshot = await NotesRepository.ReadImportAsync(full);
        snapshot.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (before != Hash(full) || wal != (File.Exists(full + "-wal") ? Hash(full + "-wal") : "")) throw new IOException("확인 중 원본이 변경되어 전환하지 않았습니다.");
        string contentHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
        return (snapshot, new(full, database ? "SQLite" : "JSON", 1, snapshot.Notes.Length, before, contentHash, wal));
    }

    /// <summary>name: MigrateAsync; desc: staging 저장/재실행/전체 매핑 대조와 source 재검사 뒤 rename으로 확정한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 취소/실패/중복 목적지 보호.</summary>
    private static async Task<MarkdownMigrationReport> MigrateAsync(string path, string destinationDataDirectory, bool database, CancellationToken cancellationToken, string? expectedSourceHash)
    {
        var original = await ReadAsync(path, database, cancellationToken);
        if (expectedSourceHash != null && original.Preview.SourceSha256 != expectedSourceHash) throw new IOException("미리보기 이후 원본이 바뀌었습니다. 다시 확인하세요.");
        string parent = Path.GetFullPath(destinationDataDirectory);
        string destination = Path.Combine(parent, "markdown-v1");
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("기존 Markdown 목적지는 덮어쓰지 않습니다.");
        Directory.CreateDirectory(parent);
        string stagingParent = Path.Combine(parent, ".migration-" + Guid.NewGuid().ToString("N"));
        NotesRepository staging = new(stagingParent); staging.FileStore.InitializeNew();
        NotesSnapshot converted = original.Snapshot with { Revision = Math.Max(1, original.Snapshot.Revision) };
        await staging.SaveAsync(converted); cancellationToken.ThrowIfCancellationRequested();
        NotesSnapshot reread = await new NotesRepository(stagingParent).LoadAsync();
        if (JsonSerializer.Serialize(converted) != JsonSerializer.Serialize(reread)) throw new InvalidDataException("전환 내용/ID/메타데이터 대조 실패. 원본과 staging을 보존했습니다.");
        var finalSource = await ReadAsync(path, database, cancellationToken);
        if (finalSource.Preview != original.Preview) throw new IOException("전환 중 기존 자료가 변경되어 확정하지 않았습니다.");
        string sourceBackup = Path.Combine(staging.FileStore.Root, ".backups", database ? "migration-source.db" : "migration-source.json");
        File.Copy(original.Preview.SourcePath, sourceBackup, false);
        if (database && File.Exists(path + "-wal")) File.Copy(path + "-wal", sourceBackup + "-wal", false);
        await NotesRepository.AtomicJsonAsync(Path.Combine(staging.FileStore.Root, ".backups", "migration-snapshot.json"), original.Snapshot);
        string manifest = Path.Combine(staging.FileStore.Root, ".widget", "migration.json");
        await NotesRepository.AtomicJsonAsync(manifest, new { Version = 1, Source = original.Preview, ConvertedNotes = reread.Notes.Length, Verified = true, OriginalRetained = true, Utc = DateTimeOffset.UtcNow });
        BeforePublishForTest?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (Hash(path) != original.Preview.SourceSha256) throw new IOException("확정 직전 원본 변경.");
        Directory.Move(staging.FileStore.Root, destination);
        await NotesRepository.AtomicJsonAsync(Path.Combine(parent, "markdown-store.pointer.json"), new { Version = 1, RelativeRoot = "markdown-v1" });
        return new(destination, Path.Combine(destination, ".widget", "migration.json"), reread.Notes.Length, original.Preview.SourceSha256, Hash(path) == original.Preview.SourceSha256);
    }

    /// <summary>name: RollbackAsync; desc: 새 저장소도 버리지 않고 보관 이름으로 분리한다. 기존 DB를 수정하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 rollback.</summary>
    public static async Task<string> RollbackAsync(string destinationDataDirectory)
    {
        string root = Path.Combine(Path.GetFullPath(destinationDataDirectory), "markdown-v1");
        if (!File.Exists(Path.Combine(root, ".widget", "migration.json"))) throw new InvalidDataException("전환 manifest 없는 저장소는 rollback하지 않습니다.");
        string parent = Path.GetDirectoryName(root)!;
        string retained = root + ".rolled-back-" + Guid.NewGuid().ToString("N");
        await NotesRepository.AtomicJsonAsync(Path.Combine(parent, "markdown-rollback.json"), new { Version = 1, RetainedRoot = Path.GetFileName(retained), RequiresExplicitMigration = true });
        Directory.Move(root, retained);
        string pointer = Path.Combine(parent, "markdown-store.pointer.json");
        if (File.Exists(pointer)) File.Copy(pointer, Path.Combine(retained, ".widget", "before-rollback-pointer.json"), false);
        await NotesRepository.AtomicJsonAsync(pointer, new { Version = 1, RelativeRoot = Path.GetFileName(retained), ReadOnly = true, RolledBack = true });
        return retained;
    }
    /// <summary>name: Hash; desc: 원본을 읽기 전용으로 해시한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 원본 보존 대조.</summary>
    private static string Hash(string path) { using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexString(SHA256.HashData(input)); }
}
