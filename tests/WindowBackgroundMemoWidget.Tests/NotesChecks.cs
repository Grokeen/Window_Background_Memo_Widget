using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using WindowBackgroundMemoWidget.Models;
using WindowBackgroundMemoWidget.Services;
using WindowBackgroundMemoWidget.ViewModels;

namespace WindowBackgroundMemoWidget.Tests;

/// <summary>name: NotesChecks; desc: 실제 .md 권위/세션/체크/충돌/전환을 합성 자료로 검사한다.
/// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 SQLite-era 검사 교체.</summary>
internal static class NotesChecks
{
    private static readonly List<string> passed = [];
    /// <summary>name: Run; desc: UI Dispatcher continuation과 실제 autosave를 실행한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 격리 검사.</summary>
    internal static void Run()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        DispatcherFrame frame = new(); Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        Task work = RunAsync();
        _ = work.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        try { Dispatcher.PushFrame(frame); work.GetAwaiter().GetResult(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>name: RunAsync; desc: 실제 사용자 디렉토리를 읽지 않고 temp fixture만 사용한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 AC 자동 범위.</summary>
    private static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "Notedex-MarkdownTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MarkdownFileStore store = new(Path.Combine(root, "files")); store.InitializeNew();
        string id = Guid.NewGuid().ToString();
        string body = "# 한글 📝\r\n\r\n- [ ] 반복\r\n- [x] 반복\r\n- [X] 대문자\r\n";
        byte[] original = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(body)).ToArray();
        await File.WriteAllBytesAsync(store.CanonicalPath(id), original);
        MarkdownFileVersion baseline = (await store.ReadAsync(id))!;
        MarkdownDocumentSession document = new(id, store, saved: baseline);
        Check("UTF8 BOM CRLF emoji exact read", document.Source == body && baseline.HasBom);
        Check("GFM three states parsed", document.Tasks.Count == 3 && !document.Tasks[0].IsChecked && document.Tasks[1].IsChecked && document.Tasks[2].IsChecked);
        await document.SaveAsync();
        Check("view and clean save leave bytes unchanged", original.SequenceEqual(File.ReadAllBytes(baseline.FilePath)));
        MarkdownTaskItem second = document.Tasks[1];
        Check("repeated label addressed by span", document.ToggleTask(second));
        string expected = body[..second.MarkerOffset] + " " + body[(second.MarkerOffset + 1)..];
        Check("checkbox changes marker only", document.Source == expected);
        Check("stale marker and source rejected", !document.ToggleTask(second) && !document.TryReplace(0, "stale") && document.Source == expected);
        Check("undo exact source", document.Undo() && document.Source == body);
        Check("redo exact patch", document.Redo() && document.Source == expected);
        Check("file completion after commit", await document.SaveAsync() && !document.IsDirty && document.StatusMessage == "파일 저장 완료");
        byte[] committedBytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(expected)).ToArray();
        Check("BOM and CRLF persist", committedBytes.SequenceEqual(File.ReadAllBytes(baseline.FilePath)));
        (string Source, int Count)[] fixtures =
        [
            ("~~~md\n- [ ] code\n~~~\n",0), ("```\n- [x] code\n```\n",0),
            ("text [ ] literal and `- [x] inline`",0), ("\\- [ ] escaped",0),
            ("- text [ ] not-first",0), ("- [x]no-space",0), ("- ordinary",0),
            ("- [ ] parent\n  - [X] child\n",2), ("1. [ ] one\n2) [x] two\n",2),
            ("- [ ] first\n\n  later [x] not-task\n",1)
        ];
        foreach (var example in fixtures) Check("AST fixture " + passed.Count, MarkdownProjection.Parse(id, 0, example.Source).Tasks.Count == example.Count);
        Check("invalid list marker remains literal visually", MarkdownProjection.Parse(id,0,"- text [ ] not-first").Blocks.Single().Text.Contains("[ ]",StringComparison.Ordinal));
        Check("NBSP is not GFM separator",MarkdownProjection.Parse(id,0,"- [x]\u00a0not-task").Tasks.Count==0);
        MarkdownDocumentSession crOnly=new(Guid.NewGuid().ToString(),store,"- [ ] 한글\r- [X] 둘째");
        Check("CR-only line indexes and Enter preserve newline",crOnly.Tasks[1].LineIndex==1&&crOnly.ContinueChecklist(0,crOnly.Source.Length,false)&&crOnly.Source=="- [ ] 한글\r- [X] 둘째\r- [ ] ");
        MarkdownDocumentSession visual = new(Guid.NewGuid().ToString(), store, "**bold** and _italic_ with `code` [web](https://example.com)");
        MarkdownVisualBlock block = visual.Projection.Single();
        Check("inline markers hidden with style flags", block.Text == "bold and italic with code web" && block.Runs.Any(r=>r.Bold) && block.Runs.Any(r=>r.Italic) && block.Runs.Any(r=>r.Code));
        Check("visual edit preserves bold delimiters", visual.TryEditVisual(0, block, 1, 1, "X") && visual.Source.StartsWith("**bXld**", StringComparison.Ordinal));
        Check("visual undo", visual.Undo() && visual.Source.StartsWith("**bold**", StringComparison.Ordinal));
        string beforeVisual = visual.Source;
        Check("mixed style boundary rejected safely", !visual.TryEditVisual(visual.Revision, visual.Projection.Single(), 2, 6, "mix") && visual.Source == beforeVisual);
        MarkdownDocumentSession enter = new(Guid.NewGuid().ToString(), store, "- [x] **완료**");
        Check("visual Enter maps hidden delimiters", enter.ContinueChecklistVisual(0, enter.Projection.Single(), enter.Projection.Single().Text.Length, false) && enter.Source == "- [x] **완료**\n- [ ] ");
        Check("empty Enter exits checklist", enter.ContinueChecklist(enter.Revision, enter.Source.Length, false) && enter.Source == "- [x] **완료**\n");
        MarkdownDocumentSession shift = new(Guid.NewGuid().ToString(), store, "- [ ] 항목");
        Check("Shift Enter same item", shift.ContinueChecklist(0, shift.Source.Length, true) && shift.Source == "- [ ] 항목\n  ");
        shift.TryEdit(shift.Revision,shift.Source.Length,0,"계속");
        Check("Enter after continuation creates next task",shift.ContinueChecklistVisual(shift.Revision,shift.Projection.Single(),shift.Projection.Single().Text.Length,false)&&shift.Source=="- [ ] 항목\n  계속\n- [ ] ");
        string beforeIme = shift.Source;
        Check("composition Enter never changes source", !shift.ContinueChecklist(shift.Revision, shift.Source.Length, false, true) && shift.Source == beforeIme);
        MarkdownDocumentSession convert = new(Guid.NewGuid().ToString(), store, "일반\n- 목록");
        Check("convert rows preserves text and bullet", convert.ConvertToChecklist(0, 0, convert.Source.Length) && convert.Source == "- [ ] 일반\n- [ ] 목록");
        MarkdownDocumentSession code = new(Guid.NewGuid().ToString(), store, "```\n- [ ] code\n```");
        Check("code conversion rejected", !code.ConvertToChecklist(0, 0, code.Source.Length));
        document.TryReplace(document.Revision, "파일 잠금 중 입력");
        using (FileStream locked = new(baseline.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check("md lock fails and retains input", !await document.SaveAsync() && document.IsDirty && document.Source == "파일 잠금 중 입력");
            Check("md lock keeps original bytes", File.ReadAllBytes(baseline.FilePath).SequenceEqual(committedBytes));
        }
        Check("retry commits input", await document.SaveAsync());
        await File.WriteAllTextAsync(baseline.FilePath, "깨끗한 외부 변경", new UTF8Encoding(false)); await document.RefreshAsync();
        Check("clean external refresh", document.Source == "깨끗한 외부 변경" && !document.IsDirty);
        long stableRevision = document.Revision; await document.RefreshAsync(); await document.RefreshAsync();
        Check("own hash causes no revision loop", stableRevision == document.Revision);
        document.TryReplace(document.Revision, "로컬 미저장");
        await File.WriteAllTextAsync(baseline.FilePath, "외부 미저장 경쟁", new UTF8Encoding(false)); await document.RefreshAsync();
        Check("dirty conflict blocks overwrite", document.HasConflict && document.Source == "로컬 미저장" && !await document.SaveAsync() && File.ReadAllText(baseline.FilePath) == "외부 미저장 경쟁");
        string[] conflicts = Directory.GetFiles(Path.Combine(store.Root, ".conflicts", id), "*.md");
        Check("both conflict copies durable", conflicts.Any(p=>File.ReadAllText(p)=="로컬 미저장") && conflicts.Any(p=>File.ReadAllText(p)=="외부 미저장 경쟁"));
        string pending = Path.Combine(root,"pending.md"); await document.SaveCopyAsync(pending,"UI 보류 입력");
        Check("pending UI input separately saved", File.ReadAllText(pending)=="UI 보류 입력");
        Check("explicit external resolution", await document.ResolveConflictAsync("external") && document.Source=="외부 미저장 경쟁");
        string renamed = Path.Combine(store.Root,"notes","이름--"+id+".md"); File.Move(baseline.FilePath,renamed); await document.RefreshAsync();
        Check("rename links explicit ID", document.FilePath==renamed);
        File.Delete(renamed); await document.RefreshAsync();
        Check("deletion never becomes blank document", document.State==MarkdownDocumentState.Missing && document.Source=="외부 미저장 경쟁" && !await document.SaveAsync());
        string raceId=Guid.NewGuid().ToString(); MarkdownFileVersion raceBase=await store.WriteAsync(raceId,"base",null);
        MarkdownFileStore racing=new(store.Root,p=>File.WriteAllText(p,"raced external",new UTF8Encoding(false)));
        MarkdownDocumentSession raced=new(raceId,racing,saved:raceBase); raced.TryReplace(0,"raced local");
        Check("late race reports conflict", !await raced.SaveAsync() && raced.HasConflict);
        Check("late race primary remains external",File.ReadAllText(store.CanonicalPath(raceId))=="raced external");
        string[] raceCopies=Directory.GetFiles(Path.Combine(store.Root,".conflicts",raceId),"*.md");
        Check("late race both versions survive",raceCopies.Any(p=>File.ReadAllText(p)=="raced external")&&raceCopies.Any(p=>File.ReadAllText(p)=="raced local"));
        string failureId=Guid.NewGuid().ToString(); MarkdownFileVersion recoveryBase=await store.WriteAsync(failureId,"base",null);
        FileStream? restorationLock=null;
        MarkdownFileStore failedRestoration=new(store.Root,p=>File.WriteAllText(p,"external recovery",new UTF8Encoding(false)),p=>restorationLock=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read));
        MarkdownDocumentSession recoveryDocument=new(failureId,failedRestoration,saved:recoveryBase); recoveryDocument.TryReplace(0,"local recovery");
        try
        {
            Check("failed race restoration never claims saved",!await recoveryDocument.SaveAsync()&&recoveryDocument.IsDirty&&recoveryDocument.HasConflict);
            string[] recoveryCopies=Directory.GetFiles(Path.Combine(store.Root,".conflicts",failureId),"*.md");
            Check("failed restoration preserves all recovery copies",recoveryCopies.Any(p=>File.ReadAllText(p)=="external recovery")&&recoveryCopies.Any(p=>File.ReadAllText(p)=="local recovery"));
        }
        finally{restorationLock?.Dispose();}
        string profile=Path.Combine(root,"profile"); NotesRepository repository=new(profile);
        using NotesManagerViewModel vm=new(repository,_=>true);
        await vm.InitializeAsync(); vm.NewMemoCommand.Execute(null); MemoListItemViewModel note=vm.SelectedMemo!;
        Check("hidden manager note editable",!note.IsVisible&&note.IsEditable);
        note.Body="# 제목\nBODY_AUTHORITY_SENTINEL\n- [ ] 체크";
        Check("automatic title removes Markdown markers",note.Title=="제목");
        MarkdownDocumentSession shared=repository.GetSession(note.Snapshot());
        Check("NoteId singleton session",ReferenceEquals(shared,note.Document));
        MemoListItemViewModel widget=new(note.Snapshot(),shared); long oldRevision=shared.Revision;
        widget.Document.ToggleTask(widget.Document.Tasks.Single());
        Check("widget and manager same checkbox state",ReferenceEquals(widget.Document,note.Document)&&note.Body.Contains("[x]",StringComparison.Ordinal));
        Check("old surface cannot replace latest",!shared.TryReplace(oldRevision,"stale widget")&&note.Body.Contains("[x]",StringComparison.Ordinal));
        vm.GlobalHidden=true; note.Body+="\n숨긴 관리 편집";
        Check("global hide independent of content edit",note.IsEditable&&!note.IsVisible&&vm.GlobalHidden);
        Check("md file commit controls global save label",await vm.FlushAsync()&&vm.SaveStateMessage=="파일 저장 완료");
        Check("md authority without sqlite creation",File.Exists(repository.FileStore.CanonicalPath(note.Id))&&!File.Exists(repository.DatabasePath));
        using(JsonDocument metadata=JsonDocument.Parse(File.ReadAllText(repository.FileStore.MetadataPath)))
            Check("sidecar has no body",!metadata.RootElement.GetProperty("Notes")[0].TryGetProperty("Body",out _));
        NotesSnapshot current=vm.Snapshot(), restart=await new NotesRepository(profile).LoadAsync();
        Check("restart restores body and metadata",JsonSerializer.Serialize(current)==JsonSerializer.Serialize(restart));
        string beforeBrowse=JsonSerializer.Serialize(vm.Snapshot());
        for(int i=0;i<100;i++){vm.SearchText="없음";vm.SearchText="";vm.SortMode=i%2==0?"title":"modified";}
        Check("browse100 never mutates body or placement",beforeBrowse==JsonSerializer.Serialize(vm.Snapshot()));
        Check("one original file for two surfaces",Directory.GetFiles(Path.Combine(repository.FileStore.Root,"notes"),"*.md").Length==1);
        vm.GlobalHidden=false; await vm.FlushAsync();
        long idleStart=Stopwatch.GetTimestamp(); double? idleMs=null;
        void Idle(long a,long b,double c){idleMs??=Stopwatch.GetElapsedTime(idleStart).TotalMilliseconds;}
        vm.CommitCompleted+=Idle; note.Body+="\nidle"; await Task.Delay(1150); vm.CommitCompleted-=Idle;
        Check("md idle commit <=1000ms",idleMs is >0 and <=1000&&File.ReadAllText(repository.FileStore.CanonicalPath(note.Id))==note.Body);
        long continuousStart=Stopwatch.GetTimestamp();double? continuousMs=null;
        void Continuous(long a,long b,double c){continuousMs??=Stopwatch.GetElapsedTime(continuousStart).TotalMilliseconds;}
        vm.CommitCompleted+=Continuous;
        while(Stopwatch.GetElapsedTime(continuousStart).TotalMilliseconds<5200){note.Body+="연속";await Task.Delay(100);}
        vm.CommitCompleted-=Continuous;await vm.FlushAsync();
        Check("md continuous commit <=5000ms",continuousMs is >0 and <=5000);
        vm.SelectedMemo=note;vm.MoveToTrashCommand.Execute(null);await vm.FlushAsync();
        Check("trash moves same original md",File.Exists(repository.FileStore.CanonicalPath(note.Id,true))&&!File.Exists(repository.FileStore.CanonicalPath(note.Id)));
        vm.SelectedView="trash";vm.SelectedMemo=note;vm.RestoreMemoCommand.Execute(null);await vm.FlushAsync();
        Check("restore same ID hidden and editable",File.Exists(repository.FileStore.CanonicalPath(note.Id))&&!note.IsVisible&&!note.IsDeleted&&note.IsEditable);
        File.Delete(repository.FileStore.MetadataPath);NotesSnapshot recovered=await new NotesRepository(profile).LoadAsync();
        Check("sidecar removal recovers source and checks",recovered.Notes.Single().Body==note.Body&&recovered.Notes.Single().Id==note.Id);
        await CheckMigrationAsync(root);
        await P0CompletionChecks.RunAsync(root, Check);
        string evidence=Path.GetFullPath("artifacts/markdown-tests");Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence,"results.json"),JsonSerializer.Serialize(new{Passed=passed,IdleCommitMilliseconds=idleMs,ContinuousCommitMilliseconds=continuousMs,TestRoot="%TEMP%/Notedex-MarkdownTests-<unique-id>",UserDataTouched=false,PhysicalImeDpiTested=false},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Markdown integration: {passed.Count} checks passed");
    }

    /// <summary>name: CheckMigrationAsync; desc: 합성 DB/JSON·취소·rollback의 재시작을 검사한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 MD-02.</summary>
    private static async Task CheckMigrationAsync(string root)
    {
        string legacy=Path.Combine(root,"legacy");Directory.CreateDirectory(legacy);
        DateTimeOffset now=DateTimeOffset.UtcNow;string id=Guid.NewGuid().ToString();
        NotesSnapshot snapshot=new(1,5,[new(id,"원래 제목","- [X] 이전 체크\r\n한글 📝",now,now,null,["보존"],true,2,false,true,250,200)],[]);
        string db=Path.Combine(legacy,"notes.db");
        using(SqliteConnection connection=new(new SqliteConnectionStringBuilder{DataSource=db,Pooling=false}.ToString()))
        {connection.Open();using SqliteCommand command=connection.CreateCommand();command.CommandText="CREATE TABLE library(id INTEGER PRIMARY KEY,payload TEXT NOT NULL); PRAGMA user_version=1; INSERT INTO library VALUES(1,$p)";command.Parameters.AddWithValue("$p",JsonSerializer.Serialize(snapshot));command.ExecuteNonQuery();}
        byte[] before=File.ReadAllBytes(db);
        bool blocked=false;try{await new NotesRepository(legacy).LoadAsync();}catch(MarkdownMigrationRequiredException){blocked=true;}
        Check("legacy startup requires explicit action",blocked&&before.SequenceEqual(File.ReadAllBytes(db))&&!Directory.Exists(Path.Combine(legacy,"markdown-v1")));
        MarkdownMigrationPreview preview=await MarkdownMigrationService.PreviewDatabaseAsync(db);
        Check("read-only preview exact hash/count",preview.NoteCount==1&&preview.SourceSha256==Convert.ToHexString(SHA256.HashData(before))&&before.SequenceEqual(File.ReadAllBytes(db)));
        MarkdownMigrationReport report=await MarkdownMigrationService.MigrateDatabaseAsync(db,legacy,expectedSourceHash:preview.SourceSha256);
        NotesSnapshot converted=await new NotesRepository(legacy).LoadAsync();
        Check("DB migration preserves bytes and complete mapping",report.SourcePreserved&&before.SequenceEqual(File.ReadAllBytes(db))&&JsonSerializer.Serialize(snapshot)==JsonSerializer.Serialize(converted));
        bool duplicate=false;try{await MarkdownMigrationService.MigrateDatabaseAsync(db,legacy);}catch(IOException){duplicate=true;}
        Check("migration retry never overwrites root",duplicate);
        string retained=await MarkdownMigrationService.RollbackAsync(legacy);
        bool rollback=false;try{await new NotesRepository(legacy).LoadAsync();}catch(MarkdownMigrationRequiredException){rollback=true;}
        Check("DB rollback retains md and clear startup route",Directory.Exists(retained)&&rollback&&before.SequenceEqual(File.ReadAllBytes(db)));
        string json=Path.Combine(root,"legacy.json");await File.WriteAllTextAsync(json,JsonSerializer.Serialize(snapshot));byte[] jsonBytes=File.ReadAllBytes(json);
        string destination=Path.Combine(root,"json-conversion");await MarkdownMigrationService.MigrateJsonAsync(json,destination);
        Check("JSON migration preserves source and md",jsonBytes.SequenceEqual(File.ReadAllBytes(json))&&(await new NotesRepository(destination).LoadAsync()).Notes.Single().Body==snapshot.Notes.Single().Body);
        string jsonRetained=await MarkdownMigrationService.RollbackAsync(destination);
        bool jsonRoute=false;try{await new NotesRepository(destination).LoadAsync();}catch(MarkdownMigrationRequiredException){jsonRoute=true;}
        Check("JSON rollback has explicit route without DB",jsonRoute&&Directory.Exists(jsonRetained)&&jsonBytes.SequenceEqual(File.ReadAllBytes(json)));
        using CancellationTokenSource cancelled=new();cancelled.Cancel();bool stopped=false;
        try{await MarkdownMigrationService.MigrateJsonAsync(json,Path.Combine(root,"cancelled"),cancelled.Token);}catch(OperationCanceledException){stopped=true;}
        Check("cancelled migration publishes nothing",stopped&&!Directory.Exists(Path.Combine(root,"cancelled","markdown-v1"))&&jsonBytes.SequenceEqual(File.ReadAllBytes(json)));
        using CancellationTokenSource midCancel=new(); bool midStopped=false;
        MarkdownMigrationService.BeforePublishForTest=()=>midCancel.Cancel();
        try{await MarkdownMigrationService.MigrateJsonAsync(json,Path.Combine(root,"mid-cancel"),midCancel.Token);}catch(OperationCanceledException){midStopped=true;}
        finally{MarkdownMigrationService.BeforePublishForTest=null;}
        Check("cancel after staging keeps source and no final root",midStopped&&!Directory.Exists(Path.Combine(root,"mid-cancel","markdown-v1"))&&Directory.GetDirectories(Path.Combine(root,"mid-cancel"),".migration-*").Length==1&&jsonBytes.SequenceEqual(File.ReadAllBytes(json)));
        string unsupported=Path.Combine(root,"unsupported.json");await File.WriteAllTextAsync(unsupported,"{\"Version\":999}");
        bool rejected=false;try{await MarkdownMigrationService.MigrateJsonAsync(unsupported,Path.Combine(root,"invalid"));}catch(Exception e)when(e is InvalidDataException or JsonException){rejected=true;}
        Check("unsupported migration rejected",rejected&&!Directory.Exists(Path.Combine(root,"invalid","markdown-v1")));
    }

    /// <summary>name: Check; desc: 본문/개인 경로 없이 검사 이름만 출력한다.
    /// author: NotedexOrig2; create date: 2026-10-24; update date: 2026-10-24 결과.</summary>
    private static void Check(string name,bool success){if(!success)throw new InvalidOperationException(name);passed.Add(name);Console.WriteLine("PASS Markdown: "+name);}
}
