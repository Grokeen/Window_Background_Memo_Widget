using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WindowBackgroundMemoWidget.Models;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: NotesBackupData; desc: 복원 전에 검증된 snapshot과 정확한 .md bytes를 함께 보관한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC-15.</summary>
public sealed record NotesBackupData(NotesSnapshot Snapshot, IReadOnlyDictionary<string, byte[]>? MarkdownFiles = null);

/// <summary>name: MarkdownBackupArchive; desc: .md+본문 없는 sidecar+hash manifest 묶음을 제한된 메모리에서 검증하고 생성한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC-15 ZIP 복구.</summary>
public static class MarkdownBackupArchive
{
    private const long MaxTotalBytes = 256L * 1024 * 1024;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record EntryHash(string Path, int Length, string Sha256);
    private sealed record Manifest(int Version, string Format, EntryHash[] Entries);

    /// <summary>name: CreateAsync; desc: 현재 원본과 snapshot을 대조한 뒤 temp ZIP을 flush/검증/교체한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 BOM/CRLF 보존.</summary>
    public static async Task CreateAsync(string path, MarkdownFileStore store, NotesSnapshot snapshot)
    {
        snapshot.Validate(); store.RequireAvailable();
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        List<MarkdownNoteMetadata> notes = [];
        long total = 0;
        foreach (NoteRecord note in snapshot.Notes)
        {
            MarkdownFileVersion current = await store.ReadAsync(note.Id) ?? throw new IOException("백업할 원본이 없습니다.");
            byte[] bytes = await File.ReadAllBytesAsync(current.FilePath);
            if (Hash(bytes) != current.Hash || Decode(bytes) != note.Body) throw new IOException("백업 도중 원본이 바뀌었거나 저장되지 않은 편집이 있습니다.");
            string relative = (note.IsDeleted ? ".trash/" : "notes/") + MarkdownFileStore.NormalizeId(note.Id) + ".md";
            total += bytes.Length; if (total > MaxTotalBytes) throw new InvalidDataException("백업 크기는 256 MiB 이하여야 합니다.");
            files.Add(relative, bytes); notes.Add(MarkdownNoteMetadata.FromRecord(note, current.Hash, relative));
        }
        files.Add(".widget/store.json", Utf8.GetBytes("{\"Version\":1,\"Format\":\"Notedex.Markdown\"}"));
        files.Add(".widget/workspace.json", JsonSerializer.SerializeToUtf8Bytes(new MarkdownWorkspace(1, snapshot.Revision, notes.ToArray(), snapshot.Folders, snapshot.GlobalHidden, snapshot.StartWithWindows)));
        Manifest manifest = new(1, "Notedex.Markdown.Backup", files.Select(pair => new EntryHash(pair.Key, pair.Value.Length, Hash(pair.Value))).ToArray());
        string destination = Path.GetFullPath(path);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 기존 백업은 새 ZIP의 전체 검증이 끝나기 전까지 교체하지 않는다.
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
                {
                    foreach (var pair in files)
                    {
                        await using Stream target = archive.CreateEntry(pair.Key, CompressionLevel.Optimal).Open();
                        await target.WriteAsync(pair.Value);
                    }
                    await using Stream manifestOutput = archive.CreateEntry("manifest.json").Open();
                    await JsonSerializer.SerializeAsync(manifestOutput, manifest);
                }
                output.Flush(true);
            }
            _ = await ReadAsync(temporary);
            if (File.Exists(destination)) File.Replace(temporary, destination, null, true); else File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>name: ReadAsync; desc: 경로/크기/중복/hash/ID/본문을 모두 검사하며 디스크에 ZIP 경로를 추출하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 traversal/zip bomb 거절.</summary>
    public static async Task<NotesBackupData> ReadAsync(string path)
    {
        if (new FileInfo(path).Length > MaxTotalBytes) throw new InvalidDataException("백업 파일 크기 초과.");
        await using FileStream input = File.OpenRead(path);
        using ZipArchive archive = new(input, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 3 or > 10003) throw new InvalidDataException("백업 항목 수가 올바르지 않습니다.");
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ValidatePath(entry.FullName);
            long limit = entry.FullName.EndsWith(".md", StringComparison.Ordinal) ? MaxFileBytes : 16 * 1024 * 1024;
            total += entry.Length;
            if (entry.Length < 0 || entry.Length > limit || total > MaxTotalBytes || files.ContainsKey(entry.FullName)) throw new InvalidDataException("백업에 중복 또는 과대 항목이 있습니다.");
            // declared length를 먼저 제한하고 실제 읽은 bytes도 제한한다.
            await using Stream source = entry.Open(); using MemoryStream bytes = new();
            byte[] buffer = new byte[81920]; int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            { if (bytes.Length + read > entry.Length || bytes.Length + read > limit) throw new InvalidDataException("백업 항목 길이 불일치."); await bytes.WriteAsync(buffer.AsMemory(0, read)); }
            if (bytes.Length != entry.Length) throw new InvalidDataException("백업 항목이 불완전합니다.");
            files.Add(entry.FullName, bytes.ToArray());
        }
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(Required(files, "manifest.json")) ?? throw new InvalidDataException("백업 manifest가 없습니다.");
        if (manifest.Version != 1 || manifest.Format != "Notedex.Markdown.Backup" || manifest.Entries == null || manifest.Entries.Length != files.Count - 1) throw new InvalidDataException("지원하지 않는 백업 manifest입니다.");
        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
        foreach (EntryHash entry in manifest.Entries)
        {
            if (entry == null || entry.Path == "manifest.json" || !declared.Add(entry.Path)) throw new InvalidDataException("백업 manifest 중복.");
            byte[] bytes = Required(files, entry.Path);
            if (entry.Length != bytes.Length || entry.Sha256 != Hash(bytes)) throw new InvalidDataException("백업 hash 불일치.");
        }
        using JsonDocument marker = JsonDocument.Parse(Required(files, ".widget/store.json"));
        if (marker.RootElement.GetProperty("Version").GetInt32() != 1 || marker.RootElement.GetProperty("Format").GetString() != "Notedex.Markdown") throw new InvalidDataException("백업 저장소 형식 불일치.");
        MarkdownWorkspace workspace = JsonSerializer.Deserialize<MarkdownWorkspace>(Required(files, ".widget/workspace.json")) ?? throw new InvalidDataException("백업 sidecar 없음.");
        if (workspace.Version != 1 || workspace.Notes == null || workspace.Folders == null) throw new InvalidDataException("지원하지 않는 sidecar.");
        List<NoteRecord> records = []; Dictionary<string, byte[]> markdown = new(StringComparer.OrdinalIgnoreCase);
        foreach (MarkdownNoteMetadata note in workspace.Notes)
        {
            string relative = (note.IsDeleted ? ".trash/" : "notes/") + MarkdownFileStore.NormalizeId(note.Id) + ".md";
            if (relative != note.RelativePath) throw new InvalidDataException("백업 NoteId 경로 불일치.");
            byte[] bytes = Required(files, relative);
            if (note.ContentHash != Hash(bytes) || !markdown.TryAdd(note.Id, bytes)) throw new InvalidDataException("백업 NoteId/hash 불일치.");
            records.Add(note.ToRecord(Decode(bytes)));
        }
        if (files.Count != records.Count + 3) throw new InvalidDataException("연결되지 않은 백업 파일이 있습니다.");
        NotesSnapshot snapshot = new(1, workspace.Revision, records.ToArray(), workspace.Folders, workspace.GlobalHidden, workspace.StartWithWindows);
        snapshot.Validate(); return new(snapshot, markdown);
    }

    /// <summary>name: ValidatePath; desc: 예상한 고정 메타파일과 GUID 메모 경로만 허용한다. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC-15.</summary>
    private static void ValidatePath(string path)
    {
        if (path is "manifest.json" or ".widget/store.json" or ".widget/workspace.json") return;
        string[] parts = path.Split('/');
        if (parts.Length != 2 || parts[0] is not ("notes" or ".trash") || !parts[1].EndsWith(".md", StringComparison.Ordinal) || !Guid.TryParseExact(parts[1][..^3], "D", out Guid id) || id.ToString("D") + ".md" != parts[1]) throw new InvalidDataException("허용하지 않는 백업 경로입니다.");
    }
    private static byte[] Required(IReadOnlyDictionary<string, byte[]> files, string name) => files.TryGetValue(name, out byte[]? bytes) ? bytes : throw new InvalidDataException("필수 백업 항목이 없습니다.");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Decode(byte[] bytes) { int offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0; return Utf8.GetString(bytes, offset, bytes.Length - offset); }
}
