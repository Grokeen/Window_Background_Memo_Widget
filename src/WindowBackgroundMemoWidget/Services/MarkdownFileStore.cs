using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: MarkdownFileVersion; desc: 실제 UTF-8 파일의 본문·인코딩·바이트 해시. 로그 직렬화 금지.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 MD-01 파일 버전.</summary>
public sealed record MarkdownFileVersion(string FilePath, string Source, string Hash, bool HasBom);

/// <summary>name: MarkdownFileConflictException; desc: 외부 버전과 편집본을 덮지 않고 충돌 사실을 전달한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 FILE-01.</summary>
public sealed class MarkdownFileConflictException(MarkdownFileVersion? external, string message, bool primaryRecoveryRequired = false) : IOException(message)
{ public MarkdownFileVersion? External { get; } = external; public bool PrimaryRecoveryRequired { get; } = primaryRecoveryRequired; }

/// <summary>name: MarkdownFileStore; desc: 구성 가능한 격리 root에서 NoteId별 .md 원본을 안전 교체한다. SQLite를 사용하지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 MD-01/FILE-01.</summary>
public sealed class MarkdownFileStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Action<string>? beforeReplace;
    private readonly Action<string>? beforeRestore;
    public const int MaxBytes = 4 * 1024 * 1024;
    public string Root { get; }
    public string MetadataPath => Path.Combine(Root, ".widget", "workspace.json");

    /// <summary>name: MarkdownFileStore; desc: 경로만 결정하며 생성/전환은 명시적으로 한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 configurable root.</summary>
    public MarkdownFileStore(string root) : this(root, null) { }
    /// <summary>name: MarkdownFileStore; desc: friend 테스트에서만 교체 직전 race를 결정적으로 주입한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 audit 테스트 경계.</summary>
    internal MarkdownFileStore(string root, Action<string>? beforeReplaceForTest, Action<string>? beforeRestoreForTest = null)
    { Root = Path.GetFullPath(root); beforeReplace = beforeReplaceForTest; beforeRestore = beforeRestoreForTest; }

    /// <summary>name: InitializeNew; desc: 신규 저장소만 만들며 기존 알 수 없는 디렉토리는 덮지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 빈 원본 오인 방지.</summary>
    public void InitializeNew()
    {
        if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any()) throw new IOException("기존 폴더는 초기화하지 않습니다.");
        Directory.CreateDirectory(Root);
        foreach (string folder in new[] { "notes", ".trash", ".widget", ".backups", ".conflicts" }) Directory.CreateDirectory(Path.Combine(Root, folder));
        File.WriteAllText(Path.Combine(Root, ".widget", "store.json"), "{\"Version\":1,\"Format\":\"Notedex.Markdown\"}", Utf8);
    }

    /// <summary>name: RequireAvailable; desc: 기존 root가 사라지면 빈 저장소로 재생성하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 오프라인 보호.</summary>
    public void RequireAvailable()
    {
        if (!Directory.Exists(Root) || !File.Exists(Path.Combine(Root, ".widget", "store.json"))) throw new IOException("메모 보관 폴더가 없거나 연결되지 않았습니다.");
        foreach (string directory in new[] { Root, Path.Combine(Root,"notes"), Path.Combine(Root,".trash"), Path.Combine(Root,".widget"), Path.Combine(Root,".backups"), Path.Combine(Root,".conflicts") })
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("연결 디렉토리를 따라 원본을 쓰지 않습니다.");
        using JsonDocument marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, ".widget", "store.json")));
        if (marker.RootElement.GetProperty("Version").GetInt32() != 1 || marker.RootElement.GetProperty("Format").GetString() != "Notedex.Markdown") throw new InvalidDataException("지원하지 않는 파일 저장소입니다.");
    }

    /// <summary>name: CanonicalPath; desc: 사용자 제목 대신 검증 GUID만 경로에 넣는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 traversal 방지.</summary>
    public string CanonicalPath(string noteId, bool deleted = false)
    { string id = NormalizeId(noteId); return Path.Combine(Root, deleted ? ".trash" : "notes", id + ".md"); }

    /// <summary>name: NormalizeId; desc: 유효 GUID를 안정적인 파일명으로 정규화한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 ID 검증.</summary>
    public static string NormalizeId(string noteId) => Guid.TryParse(noteId, out Guid id) ? id.ToString("D") : throw new ArgumentException("유효한 NoteId가 필요합니다.", nameof(noteId));

    /// <summary>name: Locate; desc: 명시 ID가 파일명에 남은 rename만 연결한다. hash로 문서를 합치지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 rename/중복 보호.</summary>
    public string? Locate(string noteId)
    {
        RequireAvailable(); string id = NormalizeId(noteId);
        string[] matches = new[] { "notes", ".trash" }.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(Root, folder), "*.md", SearchOption.TopDirectoryOnly))
            .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(path).EndsWith("--" + id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new IOException("같은 NoteId 파일이 여러 개여서 자동 연결하지 않았습니다.");
        return matches.SingleOrDefault();
    }

    /// <summary>name: ReadAsync; desc: strict UTF-8/BOM/줄바꿈을 읽고 필요 시 두 번 안정화한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 외부 임시 교체 보호.</summary>
    public async Task<MarkdownFileVersion?> ReadAsync(string noteId, bool stabilize = false)
    {
        MarkdownFileVersion? last = await Task.Run(() => ReadCurrent(noteId)).ConfigureAwait(false);
        if (!stabilize) return last;
        int same = 0;
        // 빈 중간 파일은 더 긴 안정화 구간을 거친다. 확정된 빈 문서도 삭제로 보지 않는다.
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            MarkdownFileVersion? next = await Task.Run(() => ReadCurrent(noteId)).ConfigureAwait(false);
            same = next?.Hash == last?.Hash && next?.FilePath == last?.FilePath ? same + 1 : 0;
            last = next;
            if (last != null && same >= (last.Source.Length == 0 ? 8 : 2)) return last;
            if (last == null && same >= 4) return null;
        }
        throw new IOException("외부 파일이 계속 변경되어 아직 읽지 않았습니다.");
    }

    /// <summary>name: ReadCurrent; desc: reparse/과대/잘못된 UTF-8을 거절하고 본문을 초기화하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 파일 읽기.</summary>
    private MarkdownFileVersion? ReadCurrent(string noteId)
    {
        string? path = Locate(noteId); if (path == null) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("연결 파일은 자동으로 열지 않습니다.");
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > MaxBytes) throw new InvalidDataException("Markdown 파일이 지원 크기를 넘었습니다.");
        using MemoryStream bytes = new(); input.CopyTo(bytes);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Markdown 파일이 지원 크기를 넘었습니다.");
        return Decode(path, bytes.ToArray());
    }

    /// <summary>name: Decode; desc: BOM 유무와 바이트 hash를 본문과 함께 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 byte roundtrip.</summary>
    private static MarkdownFileVersion Decode(string path, byte[] bytes)
    { bool bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }); return new(path, Utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), Convert.ToHexString(SHA256.HashData(bytes)), bom); }

    /// <summary>name: WriteAsync; desc: NoteId 직렬화·기존 hash 대조·flush·atomic replace 후 실제 bytes를 확인한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 저장 완료 기준.</summary>
    public async Task<MarkdownFileVersion> WriteAsync(string noteId, string source, string? expectedHash, bool bom = false, bool deleted = false)
    {
        string id = NormalizeId(noteId);
        SemaphoreSlim gate = Gates.GetOrAdd(Path.Combine(Root, id), _ => new(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Write(id, source, expectedHash, bom, deleted)).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>name: Write; desc: 외부 변경은 교체 전 거절하며 극소 race도 교체 백업으로 양쪽을 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 원자 파일 저장.</summary>
    private MarkdownFileVersion Write(string id, string source, string? expectedHash, bool bom, bool deleted)
    {
        #region 권위 파일 버전 확인 및 동일 root 임시 쓰기
        RequireAvailable();
        string lockDirectory = Path.Combine(Root, ".widget", "locks"); Directory.CreateDirectory(lockDirectory);
        using FileStream lease = new(Path.Combine(lockDirectory, id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        MarkdownFileVersion? current = ReadCurrent(id);
        if (current?.Hash != expectedHash) throw new MarkdownFileConflictException(current, "외부 파일 버전이 달라 저장하지 않았습니다.");
        // 선두 U+FEFF가 실제 본문이면 encoding BOM과 별개로 보존한다.
        bool useBom = bom || source.StartsWith('\ufeff');
        byte[] text = Utf8.GetBytes(source);
        if (text.Length + (useBom ? 3 : 0) > MaxBytes) throw new InvalidDataException("Markdown 파일이 지원 크기를 넘었습니다.");
        byte[] bytes = useBom ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(text).ToArray() : text;
        string destination = current?.FilePath ?? CanonicalPath(id, deleted);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + id + "." + Guid.NewGuid().ToString("N") + ".tmp");
        string backupDirectory = Path.Combine(Root, ".backups", "revisions"); Directory.CreateDirectory(backupDirectory);
        string backup = Path.Combine(backupDirectory, id + "-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            MarkdownFileVersion? beforeSwap = ReadCurrent(id);
            if (beforeSwap?.Hash != expectedHash || beforeSwap?.FilePath != current?.FilePath) throw new MarkdownFileConflictException(beforeSwap, "교체 전 외부 변경을 확인했습니다.");
            beforeReplace?.Invoke(destination);
            if (current == null) File.Move(temporary, destination, false);
            else File.Replace(temporary, destination, backup, true);
            // 외부 편집기는 앱의 lease에 참여하지 않는다. 비교와 Replace 사이 race를 숨기지 않는다.
            if (current != null && Decode(backup, File.ReadAllBytes(backup)).Hash != expectedHash)
            {
                MarkdownFileVersion raced = Decode(backup, File.ReadAllBytes(backup));
                PreserveConflict(id, source, raced);
                // 교체 직전의 외부 버전을 primary로 되돌린다. 새 제3자 변경도 backup으로 잡아
                // 복구 후보를 갱신하며, local bytes를 외부본으로 오인해 primary에 두지 않는다.
                string candidate = backup;
                string localHash = Convert.ToHexString(SHA256.HashData(bytes));
                try
                {
                    beforeRestore?.Invoke(destination);
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        MarkdownFileVersion? primary = ReadCurrent(id);
                        if (primary == null || primary.Hash != localHash) break;
                        string displaced = Path.Combine(backupDirectory, id + "-race-" + Guid.NewGuid().ToString("N") + ".md");
                        string restoringHash = Decode(candidate, File.ReadAllBytes(candidate)).Hash;
                        File.Replace(candidate, destination, displaced, true);
                        MarkdownFileVersion intervening = Decode(displaced, File.ReadAllBytes(displaced));
                        if (intervening.Hash == localHash) break;
                        PreserveConflict(id, source, intervening);
                        candidate = displaced; localHash = restoringHash;
                    }
                }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
                { throw new MarkdownFileConflictException(raced, "외부본 주 파일 복구가 막혔습니다. 양쪽 복사본을 보존했습니다.", true); }
                throw new MarkdownFileConflictException(raced, "교체 시점 외부 변경을 백업에 보존했습니다. 충돌 해결이 필요합니다.");
            }
            MarkdownFileVersion committed = ReadCurrent(id) ?? throw new IOException("저장 후 파일을 찾지 못했습니다.");
            string wantedHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (committed.Hash != wantedHash) { PreserveConflict(id, source, committed); throw new MarkdownFileConflictException(committed, "저장 확인 중 외부 파일이 변경됐습니다."); }
            foreach (string old in Directory.EnumerateFiles(backupDirectory, id + "-*.md").OrderByDescending(File.GetLastWriteTimeUtc).Skip(7)) File.Delete(old);
            return committed;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        #endregion
    }

    /// <summary>name: PreserveConflict; desc: 자동 덮어쓰기 전에 양쪽을 별도 UTF-8 파일로 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 충돌 복구 자료.</summary>
    public string PreserveConflict(string noteId, string local, MarkdownFileVersion? external)
    {
        RequireAvailable(); string directory = Path.Combine(Root, ".conflicts", NormalizeId(noteId)); Directory.CreateDirectory(directory);
        string prefix = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        WriteNewDurable(prefix + ".local.md", Utf8.GetBytes(local));
        if (external != null) WriteNewDurable(prefix + ".external.md", (external.HasBom ? new byte[] { 0xef, 0xbb, 0xbf } : []).Concat(Utf8.GetBytes(external.Source)).ToArray());
        return prefix;
    }

    /// <summary>name: WriteNewDurable; desc: 기존 파일을 덮지 않는 복구본 쓰기.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 flush.</summary>
    internal static void WriteNewDurable(string path, byte[] bytes)
    { using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true); }
}
