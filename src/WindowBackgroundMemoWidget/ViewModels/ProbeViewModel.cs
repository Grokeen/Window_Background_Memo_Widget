using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowBackgroundMemoWidget.ViewModels;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : ProbeViewModel
/// desc         : M1 검증 상태와 임시 본문을 창 전환과 독립적으로 유지한다. 영구 저장하지 않는다.
/// author       : NotedexOrig2
/// create date  : 2026-10-23
/// update date  : 2026-10-23 NotedexOrig2 : 검증 상태 바인딩 추가.
/// </summary>
/// ---------------------------------------------------------------------
public sealed class ProbeViewModel : INotifyPropertyChanged
{
    public const string InitialText = "오늘의 작은 메모\n\n여기를 클릭해 한글과 영문을 입력해 보세요.\n상단을 드래그하면 이동할 수 있습니다.\n\n이 메모는 기술 검증용이며 자동 저장되지 않습니다.";
    private string memoText = InitialText;
    private string status = "준비됨 · 표시 방식을 선택하고 메모를 열어 주세요.";
    private string modeLabel = "일반 창 · 맨 아래 배치";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string MemoText
    {
        get => memoText;
        set
        {
            if (memoText == value) return;
            memoText = value;
            Changed();
            Changed(nameof(CharacterCount));
            Changed(nameof(HasEdits));
        }
    }

    public int CharacterCount => memoText.Length;
    public bool HasEdits => memoText != InitialText;
    public string Status { get => status; set { status = value; Changed(); } }
    public string ModeLabel { get => modeLabel; set { modeLabel = value; Changed(); } }

    /// ---------------------------------------------------------------------
    /// <summary>
    /// name         : Changed
    /// desc         : 변경된 속성만 WPF 바인딩에 알린다.
    /// author       : NotedexOrig2
    /// create date  : 2026-10-23
    /// update date  : 2026-10-23 NotedexOrig2 : MVVM 알림 구현.
    /// </summary>
    /// ---------------------------------------------------------------------
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
