using System;
using WindowBackgroundMemoWidget.Models;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: MarkdownWorkspace; desc: 본문을 포함하지 않는 versioned sidecar.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 MD-01 metadata.</summary>
public sealed record MarkdownWorkspace(int Version, long Revision, MarkdownNoteMetadata[] Notes, FolderRecord[] Folders, bool GlobalHidden = false, bool StartWithWindows = false);

/// <summary>name: MarkdownNoteMetadata; desc: 본문 대신 ID/분류/배치/hash만 보관한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 body-free sidecar.</summary>
public sealed record MarkdownNoteMetadata(string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? FolderId, string[] Tags, bool IsPinned, int Priority, bool IsDeleted, bool IsVisible,
    double X, double Y, double Width, double Height, bool IsLocked, long DocumentRevision, string ContentHash, bool AutoTitle = false, string? RelativePath = null, NoteAppearance? Appearance = null, NotePlacement? Placement = null)
{
    /// <summary>name: ToRecord; desc: .md를 읽을 때만 본문과 metadata를 결합한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 authority 경계.</summary>
    public NoteRecord ToRecord(string body) => new(Id, Title, body, CreatedAt, UpdatedAt, FolderId, Tags, IsPinned, Priority, IsDeleted, IsVisible, X, Y, Width, Height, IsLocked, DocumentRevision, AutoTitle, Appearance, Placement);
    /// <summary>name: FromRecord; desc: sidecar에는 본문을 복제하지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 body 제외.</summary>
    public static MarkdownNoteMetadata FromRecord(NoteRecord note, string hash, string? relativePath = null) => new(note.Id, note.Title, note.CreatedAt, note.UpdatedAt, note.FolderId, note.Tags, note.IsPinned, note.Priority, note.IsDeleted, note.IsVisible, note.X, note.Y, note.Width, note.Height, note.IsLocked, note.DocumentRevision, hash, note.AutoTitle, relativePath, note.Appearance, note.Placement);
}

/// <summary>name: MarkdownMigrationRequiredException; desc: 기존 DB를 무단 전환하지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 MD-02 보호.</summary>
public sealed class MarkdownMigrationRequiredException() : InvalidOperationException("기존 SQLite 자료가 있습니다. 원본을 보존한 명시적 Markdown 전환이 필요합니다.");
