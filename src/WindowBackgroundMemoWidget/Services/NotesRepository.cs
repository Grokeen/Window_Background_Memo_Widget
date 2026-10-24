using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WindowBackgroundMemoWidget.Models;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: NotesRepository; desc: .md 권위 원본과 본문 없는 metadata를 연결한다. 기존 DB 자동 접근/전환 없음.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 MD-01 저장소 교체.</summary>
public sealed class NotesRepository
{
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly Dictionary<string, MarkdownDocumentSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarkdownFileVersion> versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> knownIds = new(StringComparer.OrdinalIgnoreCase);
    private long committedRevision = -1;
    public string DirectoryPath { get; }
    // 호환 진단 경로이며 이 저장소는 SQLite를 열지 않는다.
    public string DatabasePath => Path.Combine(DirectoryPath, "notes.db");
    public MarkdownFileStore FileStore { get; }
    public string BackupWarning { get; private set; } = "";
    public IReadOnlyCollection<MarkdownDocumentSession> Sessions => sessions.Values;
    internal Action? BeforeRestoreSwapForTest { get; set; }
    internal Func<bool>? ReplacementBlocked { get; set; }
    internal Func<string, bool>? DeletionBlocked { get; set; }

    /// <summary>name: NotesRepository; desc: configurable root의 파일 경로만 결정한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 사용자 자료 접근 없음.</summary>
    public NotesRepository(string directory) { DirectoryPath = Path.GetFullPath(directory); FileStore = new(Path.Combine(DirectoryPath, "markdown-v1")); }

    /// <summary>name: GetSession; desc: 같은 NoteId에는 같은 실제 문서 객체를 반환한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 SHARED-01.</summary>
    public MarkdownDocumentSession GetSession(NoteRecord note)
    {
        if (sessions.TryGetValue(note.Id, out MarkdownDocumentSession? session)) return session;
        versions.TryGetValue(note.Id, out MarkdownFileVersion? baseline);
        session = new(note.Id, FileStore, note.Body, baseline, note.DocumentRevision);
        sessions.Add(note.Id, session); return session;
    }

    /// <summary>name: LoadAsync; desc: 신규 root만 만들며 기존 DB가 있으면 명시 migration을 요구한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 legacy 보호.</summary>
    public async Task<NotesSnapshot> LoadAsync()
    {
        await writer.WaitAsync();
        try
        {
            if (!Directory.Exists(FileStore.Root))
            {
                if (File.Exists(DatabasePath)) throw new MarkdownMigrationRequiredException();
                if (File.Exists(Path.Combine(DirectoryPath, "markdown-rollback.json"))) throw new MarkdownMigrationRequiredException();
                if (File.Exists(Path.Combine(DirectoryPath, "markdown-store.pointer.json"))) throw new IOException("기존 Markdown 보관 폴더를 찾지 못해 초기화하지 않았습니다.");
                Directory.CreateDirectory(DirectoryPath); FileStore.InitializeNew();
                await AtomicJsonAsync(Path.Combine(DirectoryPath, "markdown-store.pointer.json"), new { Version = 1, RelativeRoot = "markdown-v1" });
            }
            FileStore.RequireAvailable();
            NotesSnapshot snapshot = await ReadSnapshotAsync();
            committedRevision = snapshot.Revision;
            knownIds.Clear(); foreach (NoteRecord note in snapshot.Notes) knownIds.Add(note.Id);
            try { await DailyBackupAsync(snapshot); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { BackupWarning = "Markdown 자동 백업 실패 · 원본 파일은 유지됩니다."; }
            return snapshot;
        }
        finally { writer.Release(); }
    }

    /// <summary>name: ReadSnapshotAsync; desc: metadata 소실 시 파일에서 복구하고 손상 metadata는 거절한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC-12.</summary>
    private async Task<NotesSnapshot> ReadSnapshotAsync()
    {
        versions.Clear();
        MarkdownWorkspace? workspace = null;
        if (File.Exists(FileStore.MetadataPath))
        {
            if (new FileInfo(FileStore.MetadataPath).Length > 16 * 1024 * 1024) throw new InvalidDataException("메타데이터 크기 초과.");
            workspace = JsonSerializer.Deserialize<MarkdownWorkspace>(await File.ReadAllBytesAsync(FileStore.MetadataPath)) ?? throw new InvalidDataException("빈 메타데이터.");
            if (workspace.Version != 1 || workspace.Notes == null || workspace.Folders == null) throw new InvalidDataException("지원하지 않는 메타데이터.");
        }
        List<NoteRecord> notes = [];
        if (workspace != null)
        {
            foreach (MarkdownNoteMetadata metadata in workspace.Notes)
            {
                MarkdownFileVersion? file = await FileStore.ReadAsync(metadata.Id);
                if (file == null) throw new IOException("메타데이터의 원본 파일이 없습니다. 기존 자료를 보존했습니다.");
                versions.Add(metadata.Id, file);
                notes.Add(metadata.ToRecord(file.Source) with { DocumentRevision = metadata.DocumentRevision + (file.Hash == metadata.ContentHash ? 0 : 1) });
            }
        }
        foreach (string folder in new[] { "notes", ".trash" })
        foreach (string path in Directory.EnumerateFiles(Path.Combine(FileStore.Root, folder), "*.md"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            string candidate = stem.Length >= 36 ? stem[^36..] : stem;
            if (!Guid.TryParse(candidate, out Guid id)) { BackupWarning = "NoteId 없는 파일은 자동 연결하지 않았습니다."; continue; }
            string key = id.ToString(); if (versions.ContainsKey(key)) continue;
            MarkdownFileVersion? file = await FileStore.ReadAsync(key); if (file == null) continue;
            versions.Add(key, file);
            DateTimeOffset time = File.GetLastWriteTimeUtc(path);
            string title = file.Source.Split('\n').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim().TrimStart('#', ' ', '*') ?? "새 메모";
            notes.Add(new(key, title[..Math.Min(200, title.Length)], file.Source, time, time, null, [], false, 0, folder == ".trash", false, AutoTitle: true));
            BackupWarning = "파일에서 메모를 복구했습니다. 없는 분류/배치는 기본값으로 표시합니다.";
        }
        NotesSnapshot snapshot = new(1, workspace?.Revision ?? 0, notes.ToArray(), workspace?.Folders ?? [], workspace?.GlobalHidden ?? false, workspace?.StartWithWindows ?? false);
        snapshot.Validate(); return snapshot;
    }

    /// <summary>name: SaveAsync; desc: NoteId별 captured revision을 먼저 저장하고 본문 없는 metadata를 커밋한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 실제 파일 완료 기준.</summary>
    public async Task<long> SaveAsync(NotesSnapshot snapshot)
    {
        snapshot.Validate(); await writer.WaitAsync();
        try
        {
            FileStore.RequireAvailable();
            if (snapshot.Revision <= committedRevision) return committedRevision;
            List<MarkdownNoteMetadata> metadata = [];
            foreach (NoteRecord note in snapshot.Notes)
            {
                MarkdownDocumentSession session = GetSession(note);
                if (!await session.SaveRevisionAsync(note.DocumentRevision, note.Body)) throw new IOException("Markdown 저장 실패/충돌. 원문과 편집본을 보존했습니다.");
                await MoveForTrashAsync(note, session.ContentHash);
                string actualPath = FileStore.Locate(note.Id) ?? throw new IOException("저장 파일 경로를 확인할 수 없습니다.");
                metadata.Add(MarkdownNoteMetadata.FromRecord(note, session.ContentHash, Path.GetRelativePath(FileStore.Root, actualPath).Replace('\\','/')));
            }
            foreach (string removed in knownIds.Where(id => !snapshot.Notes.Any(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray())
            {
                if (DeletionBlocked?.Invoke(removed) == true) throw new IOException("보류 편집본이 있는 메모는 삭제하지 않았습니다.");
                MarkdownFileVersion? file = await FileStore.ReadAsync(removed); if (file == null) continue;
                if (sessions.TryGetValue(removed, out MarkdownDocumentSession? document) && file.Hash != document.ContentHash) throw new MarkdownFileConflictException(file, "외부 수정 문서는 영구 삭제하지 않았습니다.");
                string backup = Path.Combine(FileStore.Root, ".backups", "deleted-" + removed + "-" + Guid.NewGuid().ToString("N") + ".md");
                File.Copy(file.FilePath, backup, false); File.Delete(file.FilePath);
                sessions.Remove(removed); versions.Remove(removed);
            }
            await AtomicJsonAsync(FileStore.MetadataPath, new MarkdownWorkspace(1, snapshot.Revision, metadata.ToArray(), snapshot.Folders, snapshot.GlobalHidden, snapshot.StartWithWindows));
            committedRevision = snapshot.Revision; knownIds.Clear(); foreach (NoteRecord note in snapshot.Notes) knownIds.Add(note.Id);
            return committedRevision;
        }
        finally { writer.Release(); }
    }

    /// <summary>name: MoveForTrashAsync; desc: 같은 ID 원본을 일반/휴지통 폴더 사이 이동한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 RECOVERY-01.</summary>
    private async Task MoveForTrashAsync(NoteRecord note, string expectedHash)
    {
        MarkdownFileVersion? file = await FileStore.ReadAsync(note.Id); if (file == null) throw new IOException("원본 파일 없음.");
        bool inTrash = string.Equals(Path.GetDirectoryName(file.FilePath), Path.Combine(FileStore.Root, ".trash"), StringComparison.OrdinalIgnoreCase);
        if (inTrash == note.IsDeleted) return;
        if (file.Hash != expectedHash) throw new MarkdownFileConflictException(file, "이동 전 외부 수정.");
        File.Move(file.FilePath, FileStore.CanonicalPath(note.Id, note.IsDeleted), false);
    }

    /// <summary>name: RefreshDocumentsAsync; desc: UI dispatcher의 같은 세션을 안정 파일로 갱신한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 FILE-01.</summary>
    public async Task RefreshDocumentsAsync() { foreach (MarkdownDocumentSession document in sessions.Values.ToArray()) await document.RefreshAsync(); }

    /// <summary>name: ExportAsync; desc: 기본 .md+sidecar ZIP 백업. 명시 .json 확장자만 이전 호환 형식을 사용한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 백업.</summary>
    public async Task ExportAsync(string path, NotesSnapshot snapshot)
    {
        snapshot.Validate();
        if (Path.GetFullPath(path).StartsWith(FileStore.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(path), DatabasePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("원본 저장소에 내보낼 수 없습니다.");
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) await AtomicJsonAsync(path, snapshot);
        else await MarkdownBackupArchive.CreateAsync(path, FileStore, snapshot);
    }

    /// <summary>name: ReadImportAsync; desc: legacy JSON을 읽기 전용으로 검증한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 MD-02.</summary>
    public static async Task<NotesSnapshot> ReadImportAsync(string path)
        => (await ReadBackupAsync(path)).Snapshot;

    /// <summary>name: ReadBackupAsync; desc: ZIP 파일 signature로 새 묶음을 읽고 legacy JSON은 별도 검증한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC-15.</summary>
    public static async Task<NotesBackupData> ReadBackupAsync(string path)
    {
        if (new FileInfo(path).Length > 256 * 1024 * 1024) throw new InvalidDataException("백업 크기 초과.");
        await using FileStream input = File.OpenRead(path);
        byte[] signature = new byte[4]; int length = await input.ReadAsync(signature);
        if (length == 4 && signature[0] == 0x50 && signature[1] == 0x4b) return await MarkdownBackupArchive.ReadAsync(path);
        input.Position = 0;
        NotesSnapshot snapshot = await JsonSerializer.DeserializeAsync<NotesSnapshot>(input) ?? throw new InvalidDataException("빈 백업.");
        snapshot.Validate(); return new(snapshot);
    }

    /// <summary>name: RestoreAsync; desc: staging .md 검증 후 기존 폴더를 보존하고 교체한다. 사용자 DB는 이동하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 rollback 가능 복원.</summary>
    public async Task<NotesSnapshot> RestoreAsync(NotesSnapshot imported)
        => await RestoreAsync(new NotesBackupData(imported));

    /// <summary>name: RestoreAsync; desc: 검증된 파일 bytes와 모든 metadata를 staging에서 대조하고 기존 root를 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC-15 원본 보존.</summary>
    public async Task<NotesSnapshot> RestoreAsync(NotesBackupData backup)
    {
        NotesSnapshot imported = backup.Snapshot;
        imported.Validate(); await writer.WaitAsync();
        string stagingParent = Path.Combine(DirectoryPath, ".restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (ReplacementBlocked?.Invoke() == true || sessions.Values.Any(s => s.IsDirty || s.HasConflict)) throw new IOException("보류 입력 또는 미저장 원본이 있어 저장소를 교체하지 않았습니다.");
            NotesSnapshot next = imported with { Revision = Math.Max(committedRevision, imported.Revision) + 1, Notes = imported.Notes.Select(n => n with { IsVisible = false }).ToArray() };
            NotesRepository staging = new(stagingParent); staging.FileStore.InitializeNew();
            if (backup.MarkdownFiles is { } raw)
            {
                if (raw.Count != next.Notes.Length) throw new InvalidDataException("백업 원본 수 불일치.");
                List<MarkdownNoteMetadata> metadata = []; long total = 0;
                foreach (NoteRecord note in next.Notes)
                {
                    if (!raw.TryGetValue(note.Id, out byte[]? bytes) || bytes == null || bytes.Length > 4 * 1024 * 1024 || (total += bytes.Length) > 256L * 1024 * 1024) throw new InvalidDataException("백업 원본 크기/ID 불일치.");
                    int bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
                    if (new UTF8Encoding(false, true).GetString(bytes, bom, bytes.Length - bom) != note.Body) throw new InvalidDataException("백업 원문 불일치.");
                    string target = staging.FileStore.CanonicalPath(note.Id, note.IsDeleted);
                    MarkdownFileStore.WriteNewDurable(target, bytes);
                    metadata.Add(MarkdownNoteMetadata.FromRecord(note, Convert.ToHexString(SHA256.HashData(bytes)), Path.GetRelativePath(staging.FileStore.Root, target).Replace('\\','/')));
                }
                await AtomicJsonAsync(staging.FileStore.MetadataPath, new MarkdownWorkspace(1, next.Revision, metadata.ToArray(), next.Folders, next.GlobalHidden, next.StartWithWindows));
            }
            else await staging.SaveAsync(next);
            NotesSnapshot verified = await new NotesRepository(stagingParent).LoadAsync();
            if (JsonSerializer.Serialize(next) != JsonSerializer.Serialize(verified)) throw new InvalidDataException("본문/ID/설정 복원 대조 실패.");
            BeforeRestoreSwapForTest?.Invoke();
            string retained = FileStore.Root + ".before-restore-" + Guid.NewGuid().ToString("N");
            Directory.Move(FileStore.Root, retained);
            NotesSnapshot result;
            try
            {
                Directory.Move(staging.FileStore.Root, FileStore.Root);
                result = await ReadSnapshotAsync();
            }
            catch
            {
                if (Directory.Exists(FileStore.Root)) Directory.Move(FileStore.Root, FileStore.Root + ".failed-restore-" + Guid.NewGuid().ToString("N"));
                Directory.Move(retained, FileStore.Root); throw;
            }
            sessions.Clear(); committedRevision = result.Revision;
            knownIds.Clear(); foreach (NoteRecord note in result.Notes) knownIds.Add(note.Id);
            return result;
        }
        finally { writer.Release(); }
    }

    /// <summary>name: AtomicJsonAsync; desc: metadata/백업을 flush 후 같은 폴더에서 원자 교체한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 sidecar 저장.</summary>
    internal static async Task AtomicJsonAsync<T>(string path, T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        string temporary = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await Task.Run(() => MarkdownFileStore.WriteNewDurable(temporary, bytes)); if (File.Exists(path)) File.Replace(temporary, path, null, true); else File.Move(temporary, path, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>name: DailyBackupAsync; desc: 파일 기반 snapshot의 일일 백업 최근 7개를 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 백업 정책.</summary>
    private async Task DailyBackupAsync(NotesSnapshot snapshot)
    {
        string directory = Path.Combine(FileStore.Root, ".backups");
        string today = Path.Combine(directory, "daily-" + DateTime.Now.ToString("yyyyMMdd") + ".notedex.zip");
        if (!File.Exists(today)) await MarkdownBackupArchive.CreateAsync(today, FileStore, snapshot);
        foreach (string old in Directory.EnumerateFiles(directory, "daily-*.notedex.zip").OrderByDescending(Path.GetFileName).Skip(7)) File.Delete(old);
    }
}
