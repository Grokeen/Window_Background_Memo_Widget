using System;
using System.IO;
using System.Text.Json;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: UserPreferences; desc: 메모 원본과 분리한 사용자별 안내 표시 설정이다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 PDF 안내 연결.</summary>
internal sealed record UserPreferences(int Version, bool DoNotShowUserGuide);

/// <summary>name: UserPreferencesStore; desc: 알려진 버전의 작은 설정만 읽고 같은 폴더의 원자 교체로 보존한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 최초 안내 선호 영속화.</summary>
internal sealed class UserPreferencesStore
{
    private readonly Action? beforePublish;
    internal string FilePath { get; }
    internal UserPreferencesStore(string profile, Action? beforePublishForTest = null)
    { FilePath = Path.Combine(Path.GetFullPath(profile), "user-preferences.json"); beforePublish = beforePublishForTest; }

    /// <summary>name: Load; desc: 설정이 없으면 안내를 표시하고 손상/미지원 설정은 호출자에게 알린다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 버전 검증.</summary>
    internal UserPreferences Load()
    {
        if (!File.Exists(FilePath)) return new(1, false);
        using FileStream input = new(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4096) throw new InvalidDataException("안내 설정 크기 초과.");
        UserPreferences preferences = JsonSerializer.Deserialize<UserPreferences>(input, new JsonSerializerOptions { MaxDepth = 8 }) ?? throw new InvalidDataException("빈 안내 설정.");
        if (preferences.Version != 1) throw new InvalidDataException("지원하지 않는 안내 설정 버전.");
        return preferences;
    }

    /// <summary>name: Save; desc: 검증된 기존 설정만 교체하며 실패 시 이전 파일과 메모 자료를 보존한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 원자 설정 교체.</summary>
    internal void Save(bool doNotShowUserGuide)
    {
        _ = Load(); // 알 수 없는 미래 버전이나 손상 설정을 기본값으로 덮지 않는다.
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new UserPreferences(1, doNotShowUserGuide));
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            beforePublish?.Invoke();
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
