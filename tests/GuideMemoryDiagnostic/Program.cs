using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Notedex.Diagnostics;

/// <summary>name: Program; desc: 검증 DLL의 정상 App/10위젯 위에서 안내 창과 이미지 비용만 분리하는 test-only 실행기.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 Stage2 A/B/C 진단.</summary>
internal static class Program
{
    private sealed record Settings(string SourceDll, string Profile, string Output, string Mode);
    private static readonly BindingFlags HiddenInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> samples = [];
    private static readonly List<object> visits = [];
    private static readonly Dictionary<string, object?> report = [];
    private static bool finished;

    [STAThread]
    private static int Main()
    {
        Settings settings = null!;
        try
        {
            string config = Environment.GetEnvironmentVariable("NOTEDEX_GUIDE_DIAGNOSTIC_CONFIG") ?? throw new InvalidOperationException("Missing test configuration.");
            settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(config)) ?? throw new InvalidDataException("Empty diagnostic settings.");
            string profile = Path.GetFullPath(settings.Profile);
            if (!profile.StartsWith(Path.Combine(Path.GetTempPath(), "Notedex-Integration-"), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(profile, "integration.marker")) || Environment.GetEnvironmentVariable("NOTEDEX_TEST_PROFILE") != profile)
                throw new InvalidOperationException("Only a marked synthetic profile is allowed.");
            if (settings.Mode is not ("N" or "A" or "B" or "C")) throw new InvalidDataException("Unknown diagnostic mode.");
            string folder = Path.GetDirectoryName(Path.GetFullPath(settings.SourceDll))!;
            AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
            Assembly product = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(settings.SourceDll));
            Assembly.SetEntryAssembly(product); // pack://application 리소스가 실제 제품 assembly를 가리키게 한다.
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settings.SourceDll)));
            report["ProductDllSHA256"] = hash;
            report["Mode"] = settings.Mode;
            report["DiagnosticOnly"] = true;
            report["UserDataTouched"] = false;
            report["ContextLimits"] = "Separate harness entry process and sampler; real product App/manager/ten widgets. Guide added by harness, not NotesApplication automatic/singleton preference route. No release-quality PASS inference; no private-working-set/VRAM/ETW measurement.";
            Type appType = product.GetType("WindowBackgroundMemoWidget.App", true)!;
            Application app = (Application)Activator.CreateInstance(appType)!;
            app.DispatcherUnhandledException += (_, e) => { report["Error"] = e.Exception.ToString(); e.Handled = true; WriteReport(settings); app.Shutdown(1); };
            appType.GetMethod("InitializeComponent")!.Invoke(app, null);
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await DiagnoseAsync(app, product, settings)));
            int exit = app.Run();
            if (!finished) { report["Error"] = report.GetValueOrDefault("Error") ?? "Product exited before diagnosis completed."; WriteReport(settings); }
            return exit == 0 && finished ? 0 : 1;
        }
        catch (Exception error)
        {
            report["Error"] = error.ToString();
            if (settings != null) WriteReport(settings);
            return 1;
        }
    }

    /// <summary>name: DiagnoseAsync; desc: 실제 정상 ready를 확인하고 안내 한 요소만 바꾼 fresh-process 비용을 기록한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 synthetic context 검증.</summary>
    private static async Task DiagnoseAsync(Application app, Assembly product, Settings settings)
    {
        Window? guide = null;
        try
        {
            string probePath = Path.Combine(settings.Profile, "startup-probe.json");
            Stopwatch wait = Stopwatch.StartNew();
            while (!File.Exists(probePath) && wait.Elapsed.TotalSeconds < 25) await Task.Delay(50);
            using JsonDocument probe = JsonDocument.Parse(File.ReadAllBytes(probePath));
            Window owner = app.MainWindow ?? throw new InvalidOperationException("No real product manager.");
            int widgets = app.Windows.Cast<Window>().Count(w => w.GetType().Name == "NoteWidgetWindow" && w.IsVisible);
            if (probe.RootElement.GetProperty("Stage").GetString() != "ready" || probe.RootElement.GetProperty("NoteCount").GetInt32() != 10 || widgets != 10)
                throw new InvalidOperationException("Real ten-note/ten-widget context was not reproduced.");
            report["RealNoteCount"] = 10; report["RealVisibleWidgetCount"] = widgets;
            report["RealManagerType"] = owner.GetType().FullName;
            report["RenderTier"] = RenderCapability.Tier >> 16;
            Image[] images = [];
            ScrollViewer? scroll = null;
            if (settings.Mode != "N")
            {
                Type guideType = product.GetType("WindowBackgroundMemoWidget.Views.UserGuideWindow", true)!;
                guide = (Window)Activator.CreateInstance(guideType, [false, true])!;
                guide.Owner = owner; guide.ShowActivated = false;
                if (settings.Mode is "A" or "B")
                {
                    guideType.GetField("pageTrackingInitialized", HiddenInstance)!.SetValue(guide, false);
                    guide.Loaded += (_, _) => ConfigureControlledPages(guide, guideType, settings.Mode);
                }
                guide.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                images = GetImages(guide, guideType);
                scroll = (ScrollViewer)guide.FindName("GuideScroll");
                if (settings.Mode == "A" && images.Any(i => i.Source != null) || settings.Mode == "B" && images.Count(i => i.Source != null) != 1
                    || settings.Mode == "C" && images.All(i => i.Source == null)) throw new InvalidOperationException("Image control mode was not reproduced.");
                DpiScale dpi = VisualTreeHelper.GetDpi(guide);
                report["GuideDpi"] = new { dpi.DpiScaleX, dpi.DpiScaleY };
                report["GuideSize"] = new { guide.ActualWidth, guide.ActualHeight, scroll.ViewportWidth, scroll.ViewportHeight };
            }
            await SampleAsync("after-guide-ready", images, 10);
            if (settings.Mode == "C")
            {
                for (int index = 0; index < images.Length; index++)
                {
                    FrameworkElement host = (FrameworkElement)guide!.FindName($"Page{index + 1:00}Host");
                    double top = host.TransformToAncestor(scroll!).Transform(new Point()).Y + scroll!.VerticalOffset;
                    scroll.ScrollToVerticalOffset(top);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Task.Delay(250);
                    if (images[index].Source is not BitmapSource bitmap) throw new InvalidOperationException("Visited guide page did not decode.");
                    visits.Add(new { Page = index + 1, bitmap.PixelWidth, bitmap.PixelHeight, LoadedPages = images.Count(i => i.Source != null) });
                }
                await SampleAsync("after-five-page-scroll", images, 10);
            }
            report["SourceDllUnchanged"] = report["ProductDllSHA256"]?.ToString() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settings.SourceDll)));
            if (!Equals(report["SourceDllUnchanged"], true)) throw new IOException("Product DLL changed during diagnosis.");
            finished = true; report["Result"] = "DIAGNOSTIC-COMPLETE";
            WriteReport(settings);
            guide?.Close();
            ICommand exitCommand = (ICommand)owner.DataContext.GetType().GetProperty("ExitApplicationCommand")!.GetValue(owner.DataContext)!;
            exitCommand.Execute(null);
        }
        catch (Exception error)
        { report["Error"] = error.ToString(); WriteReport(settings); guide?.Close(); app.Shutdown(1); }
    }

    private static Image[] GetImages(Window guide, Type type)
        => (Image[])type.GetField("pageImages", HiddenInstance)!.GetValue(guide)!;

    /// <summary>name: ConfigureControlledPages; desc: harness에서만 동일 placeholder와0/1 bitmap 조건을 만든다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 A/B control.</summary>
    private static void ConfigureControlledPages(Window guide, Type type, string mode)
    {
        ScrollViewer scroll = (ScrollViewer)guide.FindName("GuideScroll");
        guide.UpdateLayout();
        double width = Math.Min(920, Math.Max(240, scroll.ViewportWidth - 34));
        Image[] images = GetImages(guide, type);
        foreach (Image image in images) { image.Width = width; image.Height = width * 1123d / 794; image.Source = null; }
        if (mode == "B")
        {
            int pixels = Math.Clamp((int)Math.Ceiling(width * VisualTreeHelper.GetDpi(guide).DpiScaleX), 1, 794);
            images[0].Source = (BitmapSource)type.GetMethod("LoadPageBitmap", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [0, pixels])!;
        }
        guide.UpdateLayout();
    }

    private static async Task SampleAsync(string stage, Image[] pages, int seconds)
    {
        Stopwatch clock = Stopwatch.StartNew();
        do
        {
            using Process process = Process.GetCurrentProcess(); process.Refresh();
            samples.Add(new { Stage = stage, Milliseconds = Math.Round(clock.Elapsed.TotalMilliseconds, 1), PrivateBytes = process.PrivateMemorySize64,
                WorkingSet = process.WorkingSet64, ManagedBytes = GC.GetTotalMemory(false), HeapCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
                LoadedPageCount = pages.Count(p => p.Source != null), Pages = pages.Select((page, index) => new { Page = index + 1, PixelWidth = (page.Source as BitmapSource)?.PixelWidth ?? 0, PixelHeight = (page.Source as BitmapSource)?.PixelHeight ?? 0 }).ToArray() });
            await Task.Delay(250);
        } while (clock.Elapsed.TotalSeconds < seconds);
    }

    private static void WriteReport(Settings settings)
    {
        report["Samples"] = samples; report["PageVisits"] = visits;
        File.WriteAllText(settings.Output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
