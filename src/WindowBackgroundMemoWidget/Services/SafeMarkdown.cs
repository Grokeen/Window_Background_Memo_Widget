using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace WindowBackgroundMemoWidget.Services;

/// <summary>name: SafeMarkdown; desc: 원문을 실행하지 않고 허용된 문법만 WPF 요소로 만든다. 이미지/HTML 리소스는 로드하지 않는다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 제한 Markdown 미리보기.</summary>
public static class SafeMarkdown
{
    private static readonly Regex Inline = new(@"(\*\*[^*\r\n]+\*\*|\*[^*\r\n]+\*|`[^`\r\n]+`|(?<!!)\[[^\]\r\n]+\]\([^\)\r\n]+\))", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>name: Render; desc: heading/list/checklist/강조/code/link를 변환하며 원문은 바꾸지 않는다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 안전 렌더.</summary>
    public static FlowDocument Render(string source)
    {
        FlowDocument document = new() { FontFamily = new FontFamily("Segoe UI"), FontSize = 14, PagePadding = new Thickness(16), Foreground = Brushes.WhiteSmoke, Background = Brushes.Transparent };
        foreach (string original in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = original;
            Paragraph paragraph = new() { Margin = new Thickness(0, 0, 0, 8) };
            int heading = 0;
            while (heading < line.Length && heading < 6 && line[heading] == '#') heading++;
            if (heading > 0 && line.Length > heading && line[heading] == ' ')
            { paragraph.FontSize = 26 - heading * 2; paragraph.FontWeight = FontWeights.Bold; line = line[(heading + 1)..]; }
            else if (line.StartsWith("- [ ] ", StringComparison.Ordinal)) line = "☐ " + line[6..];
            else if (line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase)) line = "☑ " + line[6..];
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = "• " + line[2..];
            int cursor = 0;
            try
            {
                foreach (Match match in Inline.Matches(line))
                {
                    paragraph.Inlines.Add(new Run(line[cursor..match.Index]));
                    string token = match.Value;
                    if (token.StartsWith("**", StringComparison.Ordinal)) paragraph.Inlines.Add(new Bold(new Run(token[2..^2])));
                    else if (token[0] == '*') paragraph.Inlines.Add(new Italic(new Run(token[1..^1])));
                    else if (token[0] == '`') paragraph.Inlines.Add(new Run(token[1..^1]) { FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromRgb(48, 50, 64)) });
                    else
                    {
                        int split = token.IndexOf("](", StringComparison.Ordinal);
                        string destination = token[(split + 2)..^1];
                        if (IsSafeLink(destination))
                        {
                            Hyperlink link = new(new Run(token[1..split])) { NavigateUri = new Uri(destination), ToolTip = destination };
                            link.RequestNavigate += (_, e) =>
                            {
                                e.Handled = true;
                                if (!IsSafeLink(e.Uri.AbsoluteUri)) return;
                                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show("링크를 열 수 없습니다.", "메모"); }
                            };
                            paragraph.Inlines.Add(link);
                        }
                        else paragraph.Inlines.Add(new Run(token));
                    }
                    cursor = match.Index + match.Length;
                }
            }
            catch (RegexMatchTimeoutException) { }
            paragraph.Inlines.Add(new Run(line[cursor..]));
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    /// <summary>name: IsSafeLink; desc: 사용자 클릭 전에도 http/https 절대 주소만 허용한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 file/javascript/UNC 차단.</summary>
    public static bool IsSafeLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrWhiteSpace(uri.Host);
}
