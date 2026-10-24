using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace WindowBackgroundMemoWidget.Views;

/// ---------------------------------------------------------------------
/// <summary>
/// name         : UserGuideWindow
/// desc         : 검증된 PDF 페이지를 폭에 맞춰 읽기 전용으로 보여 주는 안내 창이다.
/// author       : NotedexOrig3
/// create date  : 2026-10-24
/// update date  : 2026-10-24 NotedexOrig3 : 0.3.0 사용자 안내 PNG 페이지 표시.
/// update date  : 2026-10-24 NotedexOrig3 : viewport 이웃 페이지 지연 디코드 및 화면 밖 BitmapSource 해제.
/// </summary>
/// ---------------------------------------------------------------------
public partial class UserGuideWindow : Window
{
    private const int OriginalPagePixelWidth = 794;
    private const double PageAspectRatio = 1123d / 794d;
    private static readonly string[] PageResourcePaths =
    [
        "Assets/UserGuide/NotedexManual-Page-01.png",
        "Assets/UserGuide/NotedexManual-Page-02.png",
        "Assets/UserGuide/NotedexManual-Page-03.png",
        "Assets/UserGuide/NotedexManual-Page-04.png",
        "Assets/UserGuide/NotedexManual-Page-05.png"
    ];

    private readonly Image[] pageImages;
    private readonly Border[] pageHosts;
    private readonly int[] loadedDecodeWidths = new int[5];
    private bool pageTrackingInitialized;
    private bool refreshingPageImages;
    private double fittedPageWidth;

    /// <summary>
    /// name         : UserGuideWindow
    /// desc         : 자동 표시 설정을 초기화하고 수동 호출에서는 설정 checkbox를 숨길 수 있다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 수동 안내는 자동 표시 선호를 수정하지 않는다.
    /// </summary>
    public UserGuideWindow(bool initialDoNotShowAgain = false, bool showDoNotShowAgain = true)
    {
        InitializeComponent();
        pageImages = [Page01, Page02, Page03, Page04, Page05];
        pageHosts = [Page01Host, Page02Host, Page03Host, Page04Host, Page05Host];
        pageTrackingInitialized = true;
        DoNotShowAgainCheckBox.IsChecked = initialDoNotShowAgain;
        DoNotShowAgainCheckBox.Visibility = showDoNotShowAgain ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsDoNotShowAgainOptionVisible => DoNotShowAgainCheckBox.Visibility == Visibility.Visible;
    internal double GuideViewportWidth => GuideScroll.ViewportWidth;
    internal double GuideViewportHeight => GuideScroll.ViewportHeight;
    internal double GuideScrollableHeight => GuideScroll.ScrollableHeight;
    internal int LoadedGuidePageCount => pageImages.Count(image => image.Source is not null);
    internal string GuidePageResourcePath(int index) => PageResourcePaths[index];

    /// <summary>
    /// name         : DoNotShowAgain
    /// desc         : 창을 닫을 때 선택된 현재 상태를 호출자에게 반환한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 설정 저장 책임은 앱 통합 계층과 분리.
    /// </summary>
    public bool DoNotShowAgain => IsDoNotShowAgainOptionVisible && DoNotShowAgainCheckBox.IsChecked == true;

    /// <summary>
    /// name         : OnGuideLoaded
    /// desc         : 첫 렌더 이후 읽기 좋은 최대 폭으로 모든 PDF 페이지를 맞춘다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 창 크기와 DPI에 맞춘 페이지 폭 조정.
    /// </summary>
    private void OnGuideLoaded(object sender, RoutedEventArgs e)
    {
        FitPagesToViewport();
        RefreshNearbyPages();
    }

    /// <summary>
    /// name         : OnGuideSizeChanged
    /// desc         : 창 크기가 바뀌면 PDF 페이지를 현재 읽기 영역 폭에 맞춘다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 작은 창에서 페이지가 가로로 잘리지 않게 조정.
    /// </summary>
    private void OnGuideSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitPagesToViewport();
        RefreshNearbyPages();
    }

    /// <summary>
    /// name         : OnGuideScrollChanged
    /// desc         : 화면에 보이거나 가까운 PDF 페이지만 디코딩하고 멀어진 페이지 이미지는 참조를 해제한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : viewport 근처 페이지 이미지의 제한된 수명 관리.
    /// </summary>
    private void OnGuideScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
        => RefreshNearbyPages();

    /// <summary>
    /// name         : FitPagesToViewport
    /// desc         : 페이지의 원래 비율을 보존하며 읽기 폭과 최대 본문 폭을 적용한다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : responsive page width.
    /// </summary>
    private void FitPagesToViewport()
    {
        if (!pageTrackingInitialized || !IsLoaded || GuideScroll.ViewportWidth <= 0) return;
        fittedPageWidth = Math.Min(920, Math.Max(240, GuideScroll.ViewportWidth - 34));
        foreach (Image page in pageImages)
        {
            page.Width = fittedPageWidth;
            page.Height = fittedPageWidth * PageAspectRatio;
        }
    }

    /// <summary>
    /// name         : RefreshNearbyPages
    /// desc         : 실제 viewport와 한 viewport 분량의 예비 범위에 해당하는 페이지만 제한 해상도로 읽는다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 첫 화면 동기 표시와 스크롤 이웃 페이지 재사용.
    /// </summary>
    private void RefreshNearbyPages()
    {
        if (!pageTrackingInitialized || !IsLoaded || refreshingPageImages || GuideScroll.ViewportWidth <= 0 || GuideScroll.ViewportHeight <= 0
            || fittedPageWidth <= 0) return;

        refreshingPageImages = true;
        try
        {
            GuideScroll.UpdateLayout();
            double prefetchDistance = Math.Max(240, GuideScroll.ViewportHeight);
            Rect nearbyViewport = new(0, -prefetchDistance, GuideScroll.ViewportWidth,
                GuideScroll.ViewportHeight + (prefetchDistance * 2));
            bool[] keepPage = new bool[pageHosts.Length];
            for (int index = 0; index < pageHosts.Length; index++)
            {
                Border host = pageHosts[index];
                Rect pageBounds = host.TransformToAncestor(GuideScroll)
                    .TransformBounds(new Rect(new Point(0, 0), host.RenderSize));
                keepPage[index] = pageBounds.IntersectsWith(nearbyViewport);
            }

            // 레이아웃 초기화 중 viewport 사각형이 페이지와 만나지 않으면 첫 페이지를 먼저 준비한다.
            if (!keepPage.Any(isKept => isKept)) keepPage[0] = true;

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            int decodeWidth = Math.Clamp((int)Math.Ceiling(fittedPageWidth * dpi.DpiScaleX), 1, OriginalPagePixelWidth);
            for (int index = 0; index < pageImages.Length; index++)
            {
                if (keepPage[index])
                {
                    if (pageImages[index].Source is null || loadedDecodeWidths[index] != decodeWidth)
                    {
                        pageImages[index].Source = LoadPageBitmap(index, decodeWidth);
                        loadedDecodeWidths[index] = decodeWidth;
                    }
                }
                else
                {
                    // Source를 비우면 UIElement가 더는 BitmapSource를 보유하지 않아 화면 밖 페이지를 회수할 수 있다.
                    pageImages[index].Source = null;
                    loadedDecodeWidths[index] = 0;
                }
            }
        }
        finally { refreshingPageImages = false; }
    }

    private static BitmapImage LoadPageBitmap(int pageIndex, int decodeWidth)
    {
        BitmapImage bitmap = new();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri("pack://application:,,,/" + PageResourcePaths[pageIndex], UriKind.Absolute);
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// name         : OnCloseClick
    /// desc         : 확인 버튼으로 안내 창을 닫고 체크 상태를 호출자에게 남긴다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 호출자가 자동/수동 종료 맥락을 처리한다.
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// name         : OnGuidePreviewKeyDown
    /// desc         : Escape 키로 도움말 창을 닫는다.
    /// author       : NotedexOrig3
    /// create date  : 2026-10-24
    /// update date  : 2026-10-24 NotedexOrig3 : 키보드 접근성 동작 추가.
    /// </summary>
    private void OnGuidePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
