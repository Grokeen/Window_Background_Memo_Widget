using System.Runtime.CompilerServices;

// NotedexOrig2 / 2026-10-24: 파일 교체 race hook은 제품 public API가 아닌 격리 시험에만 노출한다.
[assembly: InternalsVisibleTo("WindowBackgroundMemoWidget.Tests")]
