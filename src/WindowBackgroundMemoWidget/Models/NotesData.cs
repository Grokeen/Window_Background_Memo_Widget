using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowBackgroundMemoWidget.Models;

/// <summary>
/// name: NotesSnapshot; desc: 버전이 명시된 로컬 원문/배치/분류 스냅샷. 로그에는 넣지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 저장 모델 구현.
/// </summary>
public sealed record NotesSnapshot(int Version, long Revision, NoteRecord[] Notes, FolderRecord[] Folders, bool GlobalHidden = false, bool StartWithWindows = false)
{
    public static NotesSnapshot Empty => new(1, 0, [], []);

    /// <summary>name: Validate; desc: 손상/미래 형식/중복 ID와 잘못된 참조를 교체 전에 거절한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 복원 보호.</summary>
    public void Validate()
    {
        #region 원본을 쓰기 전에 전체 스냅샷 검증
        if (Version != 1 || Revision < 0 || Notes == null || Folders == null || Notes.Length > 10000 || Folders.Length > 1000)
            throw new InvalidDataException("지원하지 않거나 손상된 메모 형식입니다.");
        HashSet<string> folderIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (FolderRecord folder in Folders)
            if (folder == null || !Guid.TryParse(folder.Id, out _) || !folderIds.Add(folder.Id) || string.IsNullOrWhiteSpace(folder.Name) || folder.Name.Length > 100)
                throw new InvalidDataException("폴더 형식이 올바르지 않습니다.");
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (NoteRecord note in Notes)
        {
            if (note == null || !Guid.TryParse(note.Id, out _) || !ids.Add(note.Id) || note.Title == null || note.Title.Length > 200 || note.Body == null || note.Body.Length > 1_000_000 || note.DocumentRevision < 0 || note.Tags == null || note.Tags.Length > 50 || note.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 50) || note.Priority is < 0 or > 2 || note.FolderId != null && !folderIds.Contains(note.FolderId))
                throw new InvalidDataException("메모 형식이 올바르지 않습니다.");
            if (!double.IsFinite(note.X) || !double.IsFinite(note.Y) || Math.Abs(note.X) > 1_000_000 || Math.Abs(note.Y) > 1_000_000 || !double.IsFinite(note.Width) || !double.IsFinite(note.Height) || note.Width is < 240 or > 20000 || note.Height is < 200 or > 20000)
                throw new InvalidDataException("메모 배치 형식이 올바르지 않습니다.");
            (note.Appearance ?? new()).Validate();
            note.Placement?.Validate();
        }
        #endregion
    }
}

/// <summary>name: NoteRecord; desc: Markdown 원문과 표시 의도를 가진 불변 저장 레코드.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 원문/분류 저장.</summary>
public sealed record NoteRecord(string Id, string Title, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? FolderId, string[] Tags, bool IsPinned, int Priority, bool IsDeleted, bool IsVisible,
    double X = 100, double Y = 100, double Width = 380, double Height = 340, bool IsLocked = false,
    long DocumentRevision = 0, bool AutoTitle = false, NoteAppearance? Appearance = null, NotePlacement? Placement = null);

/// <summary>name: NoteAppearance; desc: Dracula 대비를 유지하는 사용자별 메모 외형. null legacy 값은 기본값으로 해석한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-05 영속화.</summary>
public sealed record NoteAppearance(string Color = "default", double FontSize = 16)
{
    public static IReadOnlyList<string> Colors { get; } = Array.AsReadOnly(new[] { "default", "purple", "blue", "green", "rose" });
    public static IReadOnlyList<double> FontSizes { get; } = Array.AsReadOnly(new double[] { 12, 14, 16, 18, 20, 24, 28 });
    /// <summary>name: Validate; desc: 임의 색/NaN·과대 크기를 복원 전에 거절한다. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-05.</summary>
    public void Validate()
    {
        if (!Colors.Contains(Color, StringComparer.Ordinal) || !FontSizes.Contains(FontSize)) throw new InvalidDataException("지원하지 않는 메모 색 또는 글자 크기입니다.");
    }
}

/// <summary>name: NotePlacement; desc: 모니터 작업영역 기준 DIP 배치와 마지막 DPI. legacy 물리 좌표와 명시적으로 구분한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-07.</summary>
public sealed record NotePlacement(string MonitorId, double X, double Y, double Width, double Height, double DpiX = 96, double DpiY = 96)
{
    /// <summary>name: Validate; desc: 파일에서 가져온 DIP/DPI 배치를 화면 API에 전달하기 전에 검증한다. author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 F-07.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MonitorId) || MonitorId.Length > 256 || !double.IsFinite(X) || !double.IsFinite(Y) || Math.Abs(X) > 1_000_000 || Math.Abs(Y) > 1_000_000 || !double.IsFinite(Width) || !double.IsFinite(Height) || Width is < 120 or > 20000 || Height is < 100 or > 20000 || !double.IsFinite(DpiX) || !double.IsFinite(DpiY) || DpiX is < 48 or > 960 || DpiY is < 48 or > 960) throw new InvalidDataException("모니터/DIP 배치 형식이 올바르지 않습니다.");
    }
}

/// <summary>name: FolderRecord; desc: 계층 없는 폴더 저장 레코드.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 폴더 모델.</summary>
public sealed record FolderRecord(string Id, string Name);
