using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WindowBackgroundMemoWidget.Models;
using WindowBackgroundMemoWidget.Services;
using WindowBackgroundMemoWidget.ViewModels;

namespace WindowBackgroundMemoWidget.Tests;

/// <summary>name: P0CompletionChecks; desc: 실제 ZIP 왕복·오류 원본 보존·외형 영속화·DIP/입력 명령을 격리 검증한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 누락 P0 보완.</summary>
internal static class P0CompletionChecks
{
    internal static async Task RunAsync(string root, Action<string, bool> check)
    {
        string profile = Path.Combine(root, "p0-bundle"); NotesRepository repository = new(profile); await repository.LoadAsync();
        string firstId = Guid.NewGuid().ToString(), secondId = Guid.NewGuid().ToString(), folder = Guid.NewGuid().ToString();
        string body = "# 원본 📝\r\n\r\n- [x] 첫 항목\r\n- [ ] 다음 항목\r\n";
        NotePlacement placement = new("right", 50, 60, 400, 300, 144, 144);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NoteRecord first = new(firstId, "첫 제목", body, now, now, folder, ["태그"], true, 2, false, true, Appearance: new("blue", 18), Placement: placement);
        NoteRecord second = new(secondId, "휴지통", "휴지통 원본\n", now, now, null, [], false, 0, true, false, IsLocked: true, Appearance: new("rose", 20));
        NotesSnapshot snapshot = new(1, 1, [first, second], [new(folder, "분류")]);
        await repository.SaveAsync(snapshot);
        var loaded = await new NotesRepository(profile).LoadAsync();
        check("appearance and monitor DIP metadata survive restart", JsonSerializer.Serialize(snapshot) == JsonSerializer.Serialize(loaded));
        MemoListItemViewModel item = new(first, repository.GetSession(first));
        item.ColorPreset = "green"; item.MemoFontSize = 24; item.ResetPlacement();
        check("appearance reset does not rewrite source or visibility", item.Document.Source == body && item.IsVisible && item.ColorPreset == "green" && item.MemoFontSize == 24 && item.Placement == null && item.PlacementResetVersion == 1);
        bool invalidStyle = false; try { item.MemoFontSize = double.NaN; } catch (InvalidDataException) { invalidStyle = true; }
        check("invalid appearance rejected without changing note", invalidStyle && item.MemoFontSize == 24);
        bool badColor = false; try { (snapshot with { Notes = [first with { Appearance = new("arbitrary", 16) }] }).Validate(); } catch (InvalidDataException) { badColor = true; }
        check("unknown color preset is not accepted from backup", badColor);

        byte[] exact = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(body)).ToArray();
        await File.WriteAllBytesAsync(repository.FileStore.CanonicalPath(firstId), exact);
        NotesRepository source = new(profile); snapshot = await source.LoadAsync();
        string archivePath = Path.Combine(root, "complete.notedex.zip"); await source.ExportAsync(archivePath, snapshot);
        NotesBackupData backup = await NotesRepository.ReadBackupAsync(archivePath);
        check("ZIP contains exact BOM CRLF original and trash", backup.MarkdownFiles![firstId].SequenceEqual(exact) && backup.MarkdownFiles.ContainsKey(secondId) && backup.Snapshot.Notes.Length == 2);
        using (ZipArchive zip = ZipFile.OpenRead(archivePath))
        using (Stream metadata = zip.GetEntry(".widget/workspace.json")!.Open())
        using (JsonDocument json = await JsonDocument.ParseAsync(metadata))
            check("ZIP sidecar is body free and full metadata", !json.RootElement.GetProperty("Notes")[0].TryGetProperty("Body", out _) && zip.GetEntry("manifest.json") != null && zip.GetEntry(".trash/" + secondId + ".md") != null);
        string destination = Path.Combine(root, "p0-restored"); NotesRepository target = new(destination); await target.LoadAsync();
        NoteRecord previous = first with { Id = Guid.NewGuid().ToString(), Body = "기존 보존", IsVisible = false, FolderId = null };
        await target.SaveAsync(new(1, 1, [previous], []));
        NotesSnapshot restored = await target.RestoreAsync(backup);
        check("ZIP restore preserves original bytes and same ID hidden", File.ReadAllBytes(target.FileStore.CanonicalPath(firstId)).SequenceEqual(exact) && restored.Notes.All(n => !n.IsVisible) && restored.Notes.Select(n => n.Id).SequenceEqual(snapshot.Notes.Select(n => n.Id)));
        check("ZIP restore retains previous complete store", Directory.GetDirectories(destination, "markdown-v1.before-restore-*").Any(d => File.ReadAllText(Path.Combine(d, "notes", previous.Id + ".md")) == previous.Body));
        check("ZIP restore retains classification appearance placement and lock", restored.Notes[0].FolderId == folder && restored.Notes[0].Appearance == first.Appearance && restored.Notes[0].Placement == first.Placement && restored.Notes[1].IsLocked && restored.Notes[1].IsDeleted);
        check("daily backup uses complete Markdown ZIP", Directory.GetFiles(Path.Combine(target.FileStore.Root, ".backups"), "daily-*.notedex.zip").Length == 1);
        byte[] before = File.ReadAllBytes(target.FileStore.CanonicalPath(firstId));
        target.BeforeRestoreSwapForTest = () => throw new IOException("synthetic before swap failure");
        bool swapFailed = false; try { await target.RestoreAsync(backup); } catch (IOException) { swapFailed = true; }
        target.BeforeRestoreSwapForTest = null;
        check("failed staged restore retains current original", swapFailed && before.SequenceEqual(File.ReadAllBytes(target.FileStore.CanonicalPath(firstId))));
        string legacy = Path.Combine(root, "compat-backup.json"); await source.ExportAsync(legacy, snapshot);
        check("legacy JSON import remains compatible", (await NotesRepository.ReadBackupAsync(legacy)).MarkdownFiles == null && (await NotesRepository.ReadImportAsync(legacy)).Notes.Length == 2);

        string tampered = await MutateAsync(root, archivePath, "tampered", zip => { var entry = zip.GetEntry("notes/" + firstId + ".md")!; entry.Delete(); using StreamWriter text = new(zip.CreateEntry("notes/" + firstId + ".md").Open()); text.Write("changed"); });
        check("ZIP hash mismatch rejected before restore", await RejectedAsync(tampered));
        string traversal = await MutateAsync(root, archivePath, "traversal", zip => { using StreamWriter text = new(zip.CreateEntry("../escaped.md").Open()); text.Write("escape"); });
        check("ZIP traversal rejected without extraction", await RejectedAsync(traversal) && !File.Exists(Path.Combine(root, "escaped.md")));
        string duplicate = await MutateAsync(root, archivePath, "duplicate", zip => { using StreamWriter text = new(zip.CreateEntry("notes/" + firstId + ".md").Open()); text.Write("duplicate"); });
        check("ZIP duplicate file rejected", await RejectedAsync(duplicate));
        string future = await MutateAsync(root, archivePath, "future", zip => { var entry = zip.GetEntry("manifest.json")!; string json; using (StreamReader reader = new(entry.Open())) json = reader.ReadToEnd(); entry.Delete(); using StreamWriter text = new(zip.CreateEntry("manifest.json").Open()); text.Write(json.Replace("\"Version\":1", "\"Version\":999", StringComparison.Ordinal)); });
        check("ZIP unsupported manifest rejected", await RejectedAsync(future));
        string missing = await MutateAsync(root, archivePath, "missing", zip => zip.GetEntry(".widget/workspace.json")!.Delete());
        check("ZIP missing sidecar rejected", await RejectedAsync(missing));
        byte[] archiveBefore = await File.ReadAllBytesAsync(archivePath); bool exportFailed = false;
        try { await source.ExportAsync(archivePath, snapshot with { Notes = [first with { Body = "unsaved" }, second] }); } catch (IOException) { exportFailed = true; }
        byte[] archiveAfter = await File.ReadAllBytesAsync(archivePath);
        check("failed backup does not replace previous archive", exportFailed && archiveBefore.SequenceEqual(archiveAfter));
        check("invalid archive leaves active original unchanged", before.SequenceEqual(File.ReadAllBytes(target.FileStore.CanonicalPath(firstId))));

        MonitorDescriptor left = new("left", new(-1920, 0, 1920, 1080), 96, 96);
        MonitorDescriptor right = new("right", new(0, 0, 2560, 1440), 144, 144);
        PixelBounds pixels = MonitorPlacement.Restore(first, [left, right]);
        check("monitor local DIP converts at 150 percent", pixels == new PixelBounds(75, 90, 600, 450));
        check("monitor capture roundtrips same local DIP", MonitorPlacement.Capture(pixels, right) == placement);
        MonitorDescriptor changed = right with { DpiX = 120, DpiY = 120 };
        PixelBounds corrected = MonitorPlacement.Restore(first, [left, changed]);
        check("target DPI reread requires conditional correction", corrected == new PixelBounds(63, 75, 500, 375) && corrected != pixels);
        PixelBounds missingMonitor = MonitorPlacement.Restore(first, [left]);
        check("monitor disconnect restores accessible remaining screen", missingMonitor.X >= left.WorkArea.X && missingMonitor.Right <= left.WorkArea.Right && missingMonitor.Y >= 0 && missingMonitor.Bottom <= left.WorkArea.Bottom);
        NoteRecord negative = first with { Placement = MonitorPlacement.Capture(new(-1820, 100, 400, 300), left) };
        check("negative desktop coordinates preserved in local DIP", MonitorPlacement.Restore(negative, [left]) == new PixelBounds(-1820, 100, 400, 300));

        MarkdownFileStore editStore = new(Path.Combine(root, "indent")); editStore.InitializeNew();
        MarkdownDocumentSession editor = new(Guid.NewGuid().ToString(), editStore, "- 부모\r\n- [ ] 첫째\r\n- [x] 둘째\r\n");
        string original = editor.Source;
        int selectionStart = original.IndexOf("- [ ]", StringComparison.Ordinal);
        check("indent selected task rows preserves CRLF and markers", editor.IndentSelection(editor.Revision, selectionStart, original.Length - selectionStart, false) && editor.Source == "- 부모\r\n  - [ ] 첫째\r\n  - [x] 둘째\r\n" && editor.Projection.Skip(1).All(b => b.ListDepth == 2));
        check("indent undo is one shared edit", editor.Undo() && editor.Source == original);
        editor.IndentSelection(editor.Revision, selectionStart, original.Length - selectionStart, false);
        check("outdent selected task rows restores source", editor.IndentSelection(editor.Revision, selectionStart, editor.Source.Length - selectionStart, true) && editor.Source == original);
        check("composition does not indent checklist", !editor.IndentSelection(editor.Revision, 0, 0, false, true) && editor.Source == original);
        check("stale indent command rejected", !editor.IndentSelection(editor.Revision - 1, 0, 0, false));
        editor.TryReplace(editor.Revision, "```text\n- [ ] code\n```");
        check("indent does not change code block", !editor.IndentSelection(editor.Revision, editor.Source.IndexOf("- [ ]", StringComparison.Ordinal), 0, false));
        editor.TryReplace(editor.Revision, "제목 본문");
        check("heading tool creates title and toggles paragraph", editor.FormatSelection(editor.Revision, 0, 0, "heading") && editor.Source == "# 제목 본문" && editor.FormatSelection(editor.Revision, 2, 0, "heading") && editor.Source == "제목 본문");
        await CheckUncommittedGuardsAsync(root, backup, check);
        CheckListProjection(editStore, check);
        CheckUserPreferences(root, check);
    }

    /// <summary>name: CheckUserPreferences; desc: 새 프로필의 안내 선호·실패 원자성·손상 설정과 메모 원본 보존을 검증한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 PDF 도움말 선호.</summary>
    private static void CheckUserPreferences(string root, Action<string, bool> check)
    {
        string profile = Path.Combine(root, "guide-preferences");
        UserPreferencesStore settings = new(profile);
        check("new user profile shows guide without writing preferences", !settings.Load().DoNotShowUserGuide && !File.Exists(settings.FilePath));
        settings.Save(false);
        check("unchecked guide remains automatic after preference reload", !new UserPreferencesStore(profile).Load().DoNotShowUserGuide);
        settings.Save(true);
        check("checked guide suppresses automatic display after reload", new UserPreferencesStore(profile).Load() == new UserPreferences(1, true));
        byte[] before = File.ReadAllBytes(settings.FilePath);
        UserPreferencesStore failing = new(profile, () => throw new IOException("synthetic preference publish failure"));
        bool failed = false; try { failing.Save(false); } catch (IOException) { failed = true; }
        check("failed preference publish preserves old bytes and removes temporary file", failed && before.SequenceEqual(File.ReadAllBytes(settings.FilePath)) && !Directory.EnumerateFiles(profile, "*.tmp-*").Any());
        string memo = Path.Combine(profile, "memo.md"); File.WriteAllBytes(memo, Encoding.UTF8.GetBytes("# 메모 📝\r\n- [x] 원본\r\n"));
        byte[] memoBefore = File.ReadAllBytes(memo); settings.Save(false);
        check("guide preference writes never change memo bytes", memoBefore.SequenceEqual(File.ReadAllBytes(memo)));
        foreach (string invalid in new[] { "{\"Version\":999,\"DoNotShowUserGuide\":true}", "{broken", new string(' ', 4097) })
        {
            File.WriteAllText(settings.FilePath, invalid);
            bool rejectedRead = false, rejectedWrite = false;
            try { settings.Load(); } catch (Exception error) when (error is InvalidDataException or IOException or JsonException) { rejectedRead = true; }
            try { settings.Save(false); } catch (Exception error) when (error is InvalidDataException or IOException or JsonException) { rejectedWrite = true; }
            check("unknown malformed oversized preference preserved: " + invalid.Length, rejectedRead && rejectedWrite && File.ReadAllText(settings.FilePath) == invalid);
        }
        check("separate test profile cannot inherit another user's guide preference", !new UserPreferencesStore(Path.Combine(root, "other-guide-profile")).Load().DoNotShowUserGuide);
    }

    /// <summary>name: CheckListProjection; desc: AST 목록 기호·중첩·실제 Tab 계층과 원문 복구를 검증한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 목록 표시 누락 회귀.</summary>
    private static void CheckListProjection(MarkdownFileStore store, Action<string, bool> check)
    {
        string source = "- 부모\r\n  - 자식\r\n    - [x] 손자 📝\r\n\r\n12. 번호\r\n13. 다음\r\n\r\n```text\r\n- 코드\r\n```\r\n";
        MarkdownDocumentSession session = new(Guid.NewGuid().ToString(), store, source);
        var parent = session.Projection.Single(b => b.Text == "부모");
        var child = session.Projection.Single(b => b.Text == "자식");
        var task = session.Projection.Single(b => b.Task != null);
        check("AST bullets and nested task expose distinct depth and marker", parent.ListMarker == "•" && parent.ListDepth == 1 && parent.IndentationColumns == 0 && child.ListMarker == "•" && child.ListDepth == 2 && child.IndentationColumns == 2 && task.ListDepth == 3 && task.IndentationColumns == 4 && task.ListMarker == "" && task.Task!.IsChecked);
        check("ordered list retains actual source numbers", session.Projection.Single(b => b.Text == "번호").ListMarker == "12." && session.Projection.Single(b => b.Text == "다음").ListMarker == "13.");
        check("code never invents a list marker or task", session.Projection.Where(b => b.Kind == "Code").All(b => b.ListMarker == "" && b.Task == null) && session.Source == source);
        session.TryReplace(session.Revision, "- 첫 문단\n\n  연속 문단\n");
        check("continuation paragraph keeps depth without duplicate marker", session.Projection.Count == 2 && session.Projection[0].ListMarker == "•" && session.Projection[1].ListDepth == 1 && session.Projection[1].ListMarker == "");
        session.TryReplace(session.Revision, "7) 번호\n8) 다음\n");
        check("ordered parenthesis marker is preserved", session.Projection[0].ListMarker == "7)" && session.Projection[1].ListMarker == "8)");
        foreach (string prefix in new[] { "- ", "12. " })
        {
            string before = prefix + "부모\r\n" + prefix + "자식\r\n";
            session.TryReplace(session.Revision, before);
            MarkdownVisualBlock second = session.Projection.Last();
            bool indented = session.IndentVisual(session.Revision, second, false);
            string nested = session.Source;
            string necessarySeparator = prefix == "- " ? "" : "\r\n";
            check(prefix + "Tab creates AST child using actual marker width", indented && session.Projection.Last().ListDepth == 2 && session.Projection.Last().IndentationColumns == prefix.Length && nested == prefix + "부모\r\n" + necessarySeparator + new string(' ', prefix.Length) + prefix + "자식\r\n");
            check(prefix + "Undo Redo restore exact CRLF UTF8 source", session.Undo() && session.Source == before && session.Redo() && Encoding.UTF8.GetBytes(session.Source).SequenceEqual(Encoding.UTF8.GetBytes(nested)));
            check(prefix + "Shift Tab restores parent AST level", session.IndentVisual(session.Revision, session.Projection.Last(), true) && session.Source == prefix + "부모\r\n" + necessarySeparator + prefix + "자식\r\n" && session.Projection.Last().ListDepth == 1);
        }
        session.TryReplace(session.Revision, "12. 부모\r\n\r\n13. 자식\r\n");
        string separated = session.Source;
        check("already separated ordered list does not add another blank line", session.IndentVisual(session.Revision, session.Projection.Last(), false) && session.Source == "12. 부모\r\n\r\n    13. 자식\r\n" && session.Projection.Last().ListMarker == "13." && session.Projection.Last().IndentationColumns == 4 && session.Undo() && session.Source == separated);
        session.TryReplace(session.Revision, "- 첫 항목\n- 둘째 항목\n");
        string firstUnchanged = session.Source;
        check("first sibling cannot pretend to nest without a parent", !session.IndentVisual(session.Revision, session.Projection.First(), false) && session.Source == firstUnchanged);
    }

    /// <summary>name: CheckUncommittedGuardsAsync; desc: UI callback이 선택/삭제/저장소 교체를 실제 서비스 경계까지 차단하는지 검사한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 volatile 입력 보호.</summary>
    private static async Task CheckUncommittedGuardsAsync(string root, NotesBackupData backup, Action<string, bool> check)
    {
        NotesRepository repository = new(Path.Combine(root, "draft-guards"));
        using NotesManagerViewModel model = new(repository, _ => true); await model.InitializeAsync();
        model.NewMemoCommand.Execute(null); var first = model.SelectedMemo!; first.Body = "첫 원본";
        model.NewMemoCommand.Execute(null); var second = model.SelectedMemo!; second.Body = "다른 원본";
        await model.FlushAsync(); model.SelectedMemo = first;
        bool currentDraft = true, anyDraft = true;
        model.CurrentEditorHasUncommittedInput = () => currentDraft;
        model.AnyUncommittedInputExists = () => anyDraft;
        model.NoteHasUncommittedInput = id => anyDraft && id == first.Id;
        model.SelectedMemo = second;
        check("uncommitted manager input blocks memo replacement", ReferenceEquals(model.SelectedMemo, first));
        int count = model.AllMemos.Count; model.NewMemoCommand.Execute(null);
        check("uncommitted manager input blocks new memo switch", model.AllMemos.Count == count && ReferenceEquals(model.SelectedMemo, first));
        model.MoveToTrashCommand.Execute(null);
        check("uncommitted note cannot be trashed or released", !first.IsDeleted && File.Exists(repository.FileStore.CanonicalPath(first.Id)));
        check("uncommitted UI input prevents false global saved state", !await model.FlushAsync() && model.HasUnsavedChanges && model.SaveStateMessage == "보류 입력 확인 필요");
        string original = File.ReadAllText(repository.FileStore.CanonicalPath(first.Id));
        bool blockedRestore = !await model.RestoreFromAsync(Path.Combine(root, "not-read-while-pending.zip"));
        check("manager restore rejected before reading replacing store", blockedRestore && File.ReadAllText(repository.FileStore.CanonicalPath(first.Id)) == original);
        bool rootBlocked = false; try { await repository.RestoreAsync(backup); } catch (IOException) { rootBlocked = true; }
        check("repository root swap guard cannot be bypassed by direct call", rootBlocked && File.ReadAllText(repository.FileStore.CanonicalPath(first.Id)) == original);
        bool deleteBlocked = false;
        try { await repository.SaveAsync(model.Snapshot() with { Revision = model.Snapshot().Revision + 1, Notes = [second.Snapshot()] }); } catch (IOException) { deleteBlocked = true; }
        check("repository permanent removal retains pending note original", deleteBlocked && File.ReadAllText(repository.FileStore.CanonicalPath(first.Id)) == original);
        currentDraft = false; model.SelectedMemo = second;
        check("other hidden widget draft does not block recovery navigation", ReferenceEquals(model.SelectedMemo, second));
        anyDraft = false;
        check("saved copy or composition completion reallows save", await model.FlushAsync() && !model.HasUnsavedChanges);
        int requests = 0; model.ExitRequested += () => requests++; model.ExitApplicationCommand.Execute(null);
        check("exit command routes to application owner", requests == 1);
        model.SelectedMemo = first; model.MoveToTrashCommand.Execute(null); await model.FlushAsync();
        check("resolved draft permits normal trash lifecycle", first.IsDeleted && File.Exists(repository.FileStore.CanonicalPath(first.Id, true)));
    }

    private static async Task<string> MutateAsync(string root, string original, string name, Action<ZipArchive> change)
    {
        string path = Path.Combine(root, name + ".notedex.zip"); File.Copy(original, path);
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Update)) change(zip);
        await Task.CompletedTask; return path;
    }
    private static async Task<bool> RejectedAsync(string path)
    {
        try { _ = await NotesRepository.ReadBackupAsync(path); return false; }
        catch (Exception error) when (error is InvalidDataException or JsonException) { return true; }
    }
}
