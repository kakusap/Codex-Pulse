using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexPulse;

class UsageVerification {
    class FixtureDb:SqliteDatabase { public FixtureDb(string path):base(path,true) {} }
    static string root;static readonly List<string> report=new List<string>();
    static void Check(bool pass,string name) { if(!pass) throw new Exception(name);report.Add("PASS "+name); }
    static Dictionary<string,object> Object(object value) { return Json.New().Deserialize<Dictionary<string,object>>(Json.New().Serialize(value)); }
    static Settings Fixture(string name) { string directory=Path.Combine(root,name);Directory.CreateDirectory(directory);var s=new Settings { PulseDb=Path.Combine(directory,"pulse.db"),CodexHome=Path.Combine(directory,"home") };Directory.CreateDirectory(Path.Combine(s.CodexHome,"sessions"));IndependentStore.Ensure(s.PulseDb);return s; }
    static object Tokens(long total) { return new { total_tokens=total,input_tokens=total,output_tokens=0,cached_input_tokens=0,reasoning_output_tokens=0 }; }
    static string Line(string type,object payload,string at) { return Json.New().Serialize(new {type=type,payload=payload,timestamp=at}); }
    static string Meta(string thread) { return Line("session_meta",new {id=thread,prompt="PRIVATE-PROMPT-MARKER"},"2026-10-29T15:59:00Z"); }
    static string Request(string response,long tokens,string at,long? total) { return Line("token_usage_record",new {response_id=response,usage=Tokens(tokens),thread_token_usage=new {total_tokens=total}},at); }
    static string Legacy(long total,long last,string at) { return Line("event_msg",new {type="token_count",info=new {total_token_usage=Tokens(total),last_token_usage=Tokens(last)}},at); }
    static void Write(string path,params string[] lines) { File.WriteAllText(path,string.Join("\n",lines)+"\n",new UTF8Encoding(false)); }
    static double Used(Settings s) { return LocalUsage.Read(s,new List<AccountUsage>()).Groups.Sum(g=>g.Used); }
    static void Official() {
        var s=Fixture("official");string id="workspace|user";
        UsageStore.SaveOfficial(s.PulseDb,id,Object(new {summary=new {lifetimeTokens=1000},dailyUsageBuckets=new[] {new {startDate="2026-10-29",tokens=30}}}));
        UsageStore.SaveOfficial(s.PulseDb,id,Object(new {summary=new {lifetimeTokens=1000},dailyUsageBuckets=new[] {new {startDate="2026-10-29",tokens=40}}}));
        var snap=new Snapshot();snap.Accounts.Add(new AccountUsage {AccountIdentity=id,Name="测试账号"});
        var view=UsageStore.Read(s,snap)[0];Check(view.Groups.Single().Used==1000 && view.Daily.Single().Used==40,"official snapshots replace corrected dates without addition");
        UsageStore.SaveOfficial(s.PulseDb,id,Object(new {summary=new {lifetimeTokens=(long?)null},dailyUsageBuckets=(object)null}));
        view=UsageStore.Read(s,snap)[0];Check(view.Groups.Single().Used==1000 && view.Daily.Single().Used==40 && view.Notice.Contains("未返回"),"null official fields retain cache and explicitly report missing scope");
        UsageStore.OfficialFailure(s.PulseDb,id,"网络暂不可用");view=UsageStore.Read(s,snap)[0];Check(view.HasData && view.Notice.Contains("保留") && !view.Groups[0].CostKnown,"official failure preserves totals, dates and unknown cost");
        bool failed=false;try { UsageStore.SaveOfficial(s.PulseDb,id,Object(new {summary=new {lifetimeTokens=9999},dailyUsageBuckets=new[] {new {startDate="2026-99-99",tokens=4}}})); } catch(IOException) {failed=true;}
        Check(failed && UsageStore.OfficialState(s.PulseDb,id).Lifetime==1000,"invalid official payload cannot partially replace a valid snapshot");
        snap.Accounts.Add(new AccountUsage {AccountIdentity="new|user",Name="尚未返回"});view=UsageStore.Read(s,snap)[0];Check(!view.Groups[1].UsedKnown && view.DateBasis=="官方日期","missing official account usage is unknown, and timezone is not invented");
        var empty=Fixture("official-empty");UsageStore.SaveOfficial(empty.PulseDb,id,Object(new {summary=new {lifetimeTokens=(long?)null},dailyUsageBuckets=(object)null}));Check(!UsageStore.Read(empty,snap)[0].HasData,"first null official result is not a zero-token result");
        snap.Keys.Add(new KeyUsage {Id="legacy",Used=777});snap.UsageViews=UsageStore.Read(s,snap);Check(snap.UsageViews.Count==2 && UsageStore.Selected(snap,"official").Groups.Sum(g=>g.Used)==1000 && UsageStore.Selected(snap,"history").Id=="official" && snap.Keys.Single().Used==777,"retired history preference falls back to a supported source while saved history is preserved");
        var unavailable=new UsageView {Id="official",Name="官方账号"};var availableLocal=new UsageView {Id="local",HasData=true};var fallback=new Snapshot();fallback.UsageViews.Add(unavailable);fallback.UsageViews.Add(availableLocal);Check(UsageStore.Selected(fallback,"local")==unavailable && UsageStore.Selected(fallback,"auto")==unavailable,"missing official usage never silently switches to local records");
    }
    static void Incremental() {
        var s=Fixture("incremental");string path=Path.Combine(s.CodexHome,"sessions","one.jsonl");
        Write(path,Meta("thread-a"),Request("response-a",100,"2026-10-29T15:59:59Z",100),Legacy(100,100,"2026-10-29T15:59:59.1Z"));
        LocalUsage.Scan(s,true);Check(Used(s)==100,"request plus cumulative mirror counts once");LocalUsage.Scan(s,true);Check(Used(s)==100,"repeated polling and persisted cursors do not double count");
        File.AppendAllText(path,Request("response-b",20,"2026-10-29T16:00:00Z",120)+"\n"+Legacy(120,20,"2026-10-29T16:00:01Z")+"\n");LocalUsage.Scan(s,true);
        var view=LocalUsage.Read(s,new List<AccountUsage> {new AccountUsage {AccountIdentity="current|login",Name="当前登录"}});
        Check(view.Groups.Single().Id=="unassigned" && view.Daily.Single(d=>d.Day=="2026-10-29").Used==100 && view.Daily.Single(d=>d.Day=="2026-10-30").Used==20,"Beijing midnight splits days and missing identity never uses current login");
        string archived=Path.Combine(s.CodexHome,"archived_sessions");Directory.CreateDirectory(archived);File.Copy(path,Path.Combine(archived,"copy.jsonl"));LocalUsage.Scan(s,true);Check(Used(s)==120,"copied and archived files retain response and cumulative deduplication");
        File.Move(path,Path.Combine(s.CodexHome,"sessions","renamed.jsonl"));LocalUsage.Scan(s,true);Check(Used(s)==120,"renamed log files do not inflate usage");
        path=Path.Combine(s.CodexHome,"sessions","renamed.jsonl");Write(path,Meta("thread-a"),Request("response-c",5,"2026-10-29T16:01:00Z",125));LocalUsage.Scan(s,true);Check(Used(s)==125,"truncated or replaced file is re-read while prior usage remains deduplicated");
        string incomplete=Request("response-d",7,"2026-10-29T16:02:00Z",132);File.AppendAllText(path,incomplete);LocalUsage.Scan(s,true);Check(Used(s)==125,"unterminated final line is not committed prematurely");File.AppendAllText(path,"\n");LocalUsage.Scan(s,true);Check(Used(s)==132,"completed partial line resumes exactly once");
        string raw=Encoding.UTF8.GetString(File.ReadAllBytes(s.PulseDb));Check(!raw.Contains("PRIVATE-PROMPT-MARKER"),"usage database never retains prompts or raw session metadata");
        s.CollectLocalUsage=false;File.AppendAllText(path,Request("response-e",50,"2026-10-29T16:03:00Z",182)+"\n");LocalUsage.Scan(s,true);Check(Used(s)==132 && LocalUsage.Read(s,new List<AccountUsage>()).Notice.Contains("暂停"),"collection pause retains existing results without ingesting new events");
    }
    static void Cumulative() {
        var s=Fixture("cumulative");string path=Path.Combine(s.CodexHome,"sessions","legacy.jsonl");
        Write(path,Meta("old-thread"),Legacy(1000,10,"2026-10-29T13:00:00Z"),Legacy(1030,30,"2026-10-29T13:01:00Z"),Legacy(1030,30,"2026-10-29T13:01:01Z"),Legacy(5,5,"2026-10-29T13:02:00Z"));
        LocalUsage.Scan(s,true);Check(Used(s)==45,"legacy baseline uses last usage, subsequent events use delta, and counter reset is not negative");
        File.AppendAllText(path,Request("modern",7,"2026-10-29T13:03:00Z",12)+"\n"+Legacy(12,7,"2026-10-29T13:03:01Z")+"\n");LocalUsage.Scan(s,true);Check(Used(s)==52,"mixed-version thread retains older cumulative-only history");
        string fault=Path.Combine(s.CodexHome,"sessions","fault.jsonl");Write(fault,Meta("fault"),Request("fault",2,"2026-10-29T13:03:01Z",2));
        using(var db=new FixtureDb(s.PulseDb)) db.Query("CREATE TRIGGER fail_usage BEFORE INSERT ON usage_local_events WHEN NEW.thread_key='fault' BEGIN SELECT RAISE(FAIL,'fixture failure'); END");
        LocalUsage.Scan(s,true);Check(Used(s)==52,"database write failure rolls back cursor and event together");using(var db=new FixtureDb(s.PulseDb)) db.Query("DROP TRIGGER fail_usage");LocalUsage.Scan(s,true);Check(Used(s)==54,"failed database transaction can retry without dropping a record");
        Write(fault,Meta("fault"),"{\"type\":\"token_usage_record\",bad}",Request("invalid",-1,"2026-10-29T13:04:00Z",-1),Request("valid",3,"2026-10-29T13:04:01Z",5));var scan=LocalUsage.Scan(s,true);Check(Used(s)==57 && scan.Invalid>=2,"malformed and negative usage is skipped without blocking later valid records");
        string rotation=Path.Combine(s.CodexHome,"sessions","rotation.jsonl");Write(rotation,Meta("old-thread"),Legacy(1030,30,"2026-10-29T13:01:00Z"));LocalUsage.Scan(s,true);Check(Used(s)==57,"rotation baseline cannot downgrade a previously observed complete delta");
    }
    static void Pricing() {
        var s=Fixture("pricing");string path=Path.Combine(s.CodexHome,"sessions","model.jsonl");
        Write(path,Meta("priced"),Line("turn_context",new {model="model-exact"},"2026-10-29T12:00:00Z"),Line("token_usage_record",new {response_id="priced",usage=new {total_tokens=120,input_tokens=100,cached_input_tokens=40,output_tokens=20,reasoning_output_tokens=10}},"2026-10-29T12:01:00Z"));LocalUsage.Scan(s,true);
        string priceNote;var builtin=PriceBook.Load(s,out priceNote);
        Check(builtin!=null && builtin.Models.Count==27 && builtin.Version=="OpenAI-Standard-2026-10-31" && priceNote.Contains("内置"),"embedded official price table loads with no external JSON file");
        Check(!LocalUsage.Read(s,new List<AccountUsage>()).Groups.Single().CostKnown,"unknown model remains unpriced even when a built-in table exists");
        var sample=new LocalEntry {Model="gpt-6.1-sol",Tokens=new TokenNumbers {Input=100,Cached=40,CacheWrite=20,Output=20,Total=120,Breakdown=true}};
        Check(Math.Abs(builtin.Cost(sample).Value-.000334)<1e-10,"built-in standard rates separate ordinary input, cache reads, cache writes and output");
        sample.Tokens.Input=272000;Check(Math.Abs(builtin.Cost(sample).Value-.544134)<1e-10,"long context threshold is exclusive at 272000 input tokens");
        sample.Tokens.Input=272001;Check(Math.Abs(builtin.Cost(sample).Value-1.088172)<1e-10,"long context applies 2x input and cache, 1.5x output to the full request");
        sample.Tokens.CacheWrite=null;Check(!builtin.Cost(sample).HasValue,"missing cache-write breakdown cannot fabricate a complete modern-model cost");
        sample.Model="gpt-6.1-sol-unpublished";Check(!builtin.Cost(sample).HasValue,"unpublished model identifiers never inherit a prefix-matched price");
        s.PriceTablePath=Path.Combine(Path.GetDirectoryName(s.PulseDb),"prices.json");File.WriteAllText(s.PriceTablePath,Json.New().Serialize(new PriceBook {Version="fixture-v1",Models=new List<ModelPrice> {new ModelPrice {Model="model-exact",InputPerMillion=2,CachedInputPerMillion=.5,OutputPerMillion=8}}}));
        var view=LocalUsage.Read(s,new List<AccountUsage>());Check(view.Groups[0].CostKnown && Math.Abs(view.Groups[0].Cost-.0003)<.00000001 && view.Notice.Contains("fixture-v1"),"versioned exact-model pricing separates cached input and does not add reasoning twice");
        File.WriteAllText(s.PriceTablePath,"{\"Version\":\"broken\",\"Models\":[{\"Model\":\"model-exact\",\"InputPerMillion\":2}]}");Check(!LocalUsage.Read(s,new List<AccountUsage>()).Groups[0].CostKnown,"missing price fields cannot silently become free tokens");
        File.WriteAllText(s.PriceTablePath,"{\"Version\":\"v2\",\"Models\":[{\"Model\":\"different-model\",\"InputPerMillion\":2,\"CachedInputPerMillion\":1,\"OutputPerMillion\":8}]}");Check(!LocalUsage.Read(s,new List<AccountUsage>()).Groups[0].CostKnown,"unknown model never borrows another model's price");
        s.PriceTablePath=Path.Combine(Path.GetDirectoryName(s.PulseDb),"missing.json");Check(PriceBook.Load(s,out priceNote)==null,"explicit missing custom file does not silently fall back to different prices");
        s.PriceTablePath="";Check(PriceBook.Load(s,out priceNote)!=null && priceNote.Contains("内置"),"clearing custom path restores the embedded official table");
        var upgrade=Fixture("price-parser-upgrade");path=Path.Combine(upgrade.CodexHome,"sessions","usage.jsonl");
        var usage=new {input_tokens=100,cached_input_tokens=40,cache_write_input_tokens=20,output_tokens=20,total_tokens=120};
        Write(path,Meta("priced-upgrade"),Line("turn_context",new {model="gpt-6.1-sol"},"2026-10-29T12:00:00Z"),Line("token_usage_record",new {usage=usage,thread_token_usage=new {total_tokens=120}},"2026-10-29T12:01:00Z"),Line("event_msg",new {type="token_count",info=new {total_token_usage=usage,last_token_usage=usage}},"2026-10-29T12:01:01Z"));
        LocalUsage.Scan(upgrade,true);string[] beforeKeys;
        using(var db=new FixtureDb(upgrade.PulseDb)) {
            beforeKeys=db.Query("SELECT event_key FROM usage_local_events ORDER BY event_key").Select(r=>Json.S(r,"event_key")).ToArray();
            foreach(var row in db.Query("SELECT event_key,payload FROM usage_local_events")) {var payload=Json.New().Deserialize<Dictionary<string,object>>(Json.S(row,"payload"));AccountCredential.Object(payload,"Tokens").Remove("CacheWrite");db.Query("UPDATE usage_local_events SET payload=? WHERE event_key=?",Json.New().Serialize(payload),Json.S(row,"event_key"));}
            foreach(var row in db.Query("SELECT path_key,state FROM usage_local_files")) {var cursor=Json.New().Deserialize<LocalCursor>(Json.S(row,"state"));cursor.ParserVersion=0;db.Query("UPDATE usage_local_files SET state=? WHERE path_key=?",Json.New().Serialize(cursor),Json.S(row,"path_key"));}
        }
        LocalUsage.Scan(upgrade,true);LocalUsage.Scan(upgrade,true);var upgraded=LocalUsage.Read(upgrade,new List<AccountUsage>());
        using(var db=new ReadOnlyDb(upgrade.PulseDb)) Check(beforeKeys.SequenceEqual(db.Query("SELECT event_key FROM usage_local_events ORDER BY event_key").Select(r=>Json.S(r,"event_key"))) && upgraded.Groups[0].Used==120 && upgraded.Groups[0].CostKnown && Math.Abs(upgraded.Groups[0].Cost-.000334)<1e-10,"parser upgrade enriches old cache-write fields without changing event keys or duplicating usage");
    }
    static void Attribution() {
        var s=Fixture("attribution");var account=new AccountUsage {Id="owner",AccountIdentity="workspace|user",Name="明确归属"};
        using(var db=new FixtureDb(s.PulseDb)) db.Query("INSERT INTO independent_accounts VALUES(?,?,?,?)",account.Id,account.AccountIdentity,Json.New().Serialize(account),new byte[] {1});
        string path=Path.Combine(s.CodexHome,"sessions","identity.jsonl");Write(path,Line("session_meta",new {id="identity-thread",chatgpt_account_id="workspace",user_id="user"},"2026-10-29T12:00:00Z"),Request("identity",5,"2026-10-29T12:01:00Z",5));LocalUsage.Scan(s,true);
        Check(LocalUsage.Read(s,new List<AccountUsage> {account}).Groups.Single().Id==account.AccountIdentity,"explicit workspace plus user metadata can attribute usage to a known independent account");
        var bytes=Encoding.UTF8.GetBytes(Line("token_usage_record",new {response_id="utf8",usage=Tokens(3),note="中文边界"},"2026-10-29T12:02:00Z")+"\n");
        int cut=Array.IndexOf(bytes,(byte)0xe4)+1;using(var file=new FileStream(path,FileMode.Append)) file.Write(bytes,0,cut);LocalUsage.Scan(s,true);Check(LocalUsage.Read(s,new List<AccountUsage> {account}).Groups.Single().Used==5,"split UTF-8 character in a partial line is held until complete");
        using(var file=new FileStream(path,FileMode.Append)) file.Write(bytes,cut,bytes.Length-cut);LocalUsage.Scan(s,true);Check(LocalUsage.Read(s,new List<AccountUsage> {account}).Groups.Single().Used==8,"UTF-8 partial record resumes without losing account or token counts");
    }
    static void CostCoverage() {
        var s=Fixture("cost-coverage");string path=Path.Combine(s.CodexHome,"sessions","cost.jsonl");
        Write(path,Meta("cost-thread"),Line("turn_context",new {model="gpt-4.1"},"2026-10-29T12:00:00Z"),Line("token_usage_record",new {response_id="priced",usage=new {total_tokens=120,input_tokens=100,cached_input_tokens=40,output_tokens=20}},"2026-10-29T12:01:00Z"),Line("turn_context",new {model="internal-unknown-model"},"2026-10-29T12:02:00Z"),Request("unpriced",30,"2026-10-29T12:03:00Z",null));LocalUsage.Scan(s,true);
        var view=LocalUsage.Read(s,new List<AccountUsage>());var costs=view.Costs;
        Check(costs.HasAmount && costs.Partial && costs.Records==2 && costs.PricedRecords==1 && costs.RecordedTokens==150 && costs.PricedTokens==120 && Math.Abs(costs.Cost-.0003)<1e-10,"partial local costs retain priced amount and explicit record/token coverage");
        Check(!view.Groups[0].CostKnown && costs.AmountText=="<$0.01（部分）" && costs.Slices.Single(x=>!x.Priced).Problem.Contains("未收录"),"an unknown model does not hide the known subtotal or get counted as free");
        var scoped=costs.ForAccount("account-b","账号 B");Check(!scoped.HasAmount && scoped.Records==0 && scoped.ExcludedUnassigned==2 && scoped.AmountText=="—" && !scoped.Description().Contains("0.000000 USD"),"unassigned usage never leaks into a selected account or displays a fabricated zero cost");
        s.CollectLocalUsage=false;Check(LocalUsage.Read(s,new List<AccountUsage>()).Costs.Paused && LocalUsage.Read(s,new List<AccountUsage>()).Costs.HasAmount,"paused collection still exposes previously priced records");
    }
    static int Live(string settingsPath) {
        var s=Json.New().Deserialize<Settings>(File.ReadAllText(settingsPath));string original=s.PulseDb,copy=Path.Combine(root,"live-pulse.db");
        using(var lease=new AccountLease(original)) File.Copy(original,copy);s.PulseDb=copy;
        var files=new ProfileFiles(s);string auth=Path.Combine(files.DirectoryPath,"auth.json"),config=Path.Combine(files.DirectoryPath,"config.toml");
        byte[] authBefore=File.Exists(auth)?File.ReadAllBytes(auth):new byte[0],configBefore=File.Exists(config)?File.ReadAllBytes(config):new byte[0];
        var accounts=IndependentStore.Accounts(s.PulseDb);var runtime=new IndependentRuntime(s,null);var current=files.Current();int available=0;var failures=new List<string>();
        foreach(var account in accounts) try {
            var credential=IndependentStore.Credential(s.PulseDb,account.Id);if(current!=null && current.Identity==credential.Identity) credential=current;
            // Read-only workers receive no refresh token. Live verification never
            // renews a copied credential or changes the active desktop profile.
            UsageStore.SaveOfficial(s.PulseDb,account.AccountIdentity,runtime.Usage(credential));available++;
        } catch(AccountOperationException e) {failures.Add(e.Kind);UsageStore.OfficialFailure(s.PulseDb,account.AccountIdentity,e.Message);}
        long lastOffset=-1;LocalScanReport scan=null;
        for(int i=0;i<64;i++) {
            scan=LocalUsage.Scan(s,true);long offset;using(var db=new ReadOnlyDb(s.PulseDb)) offset=db.Query("SELECT state FROM usage_local_files").Sum(r=>Json.New().Deserialize<LocalCursor>(Json.S(r,"state")).Offset);
            if(scan.Pending==0 || offset==lastOffset) break;lastOffset=offset;
        }
        double before=Used(s);LocalUsage.Scan(s,true);double after=Used(s);var snapshot=new Snapshot {Accounts=accounts};var views=UsageStore.Read(s,snapshot);
        bool unchanged=(File.Exists(auth)?File.ReadAllBytes(auth):new byte[0]).SequenceEqual(authBefore) && (File.Exists(config)?File.ReadAllBytes(config):new byte[0]).SequenceEqual(configBefore);
        Check(unchanged,"real official usage and local collection leave active auth and config bytes unchanged");
        var result=new {Accounts=accounts.Count,UsageResponses=available,FailureKinds=failures,OfficialTotalsAvailable=views[0].Groups.Count(g=>g.UsedKnown),OfficialDailyAvailable=views[0].HasDaily,LocalFiles=scan.Files,LocalPending=scan.Pending,LocalInvalid=scan.Invalid,LocalTokens=after,TokensAddedOnRepeat=after-before,UnassignedGroups=views[1].Groups.Count(g=>g.Id=="unassigned"),ActiveProfileUnchanged=unchanged};
        File.WriteAllText(Path.Combine(root,"live-report.json"),Json.New().Serialize(result));Console.WriteLine(Json.New().Serialize(result));Console.WriteLine(root);return 0;
    }
    static void Upgrade() {
        var s=Fixture("upgrade");using(var db=new FixtureDb(s.PulseDb)) {
            foreach(string table in new[] {"usage_official","usage_official_days","usage_local_files","usage_local_events"}) db.Query("DROP TABLE "+table);
            db.Query("PRAGMA user_version=2");db.Query("INSERT INTO independent_meta VALUES('keep','value')");
            db.Query("INSERT INTO pulse_sources VALUES('history','old',1,'{}')");db.Query("INSERT INTO pulse_keys VALUES('history','key',0,?)",Json.New().Serialize(new KeyUsage {Id="key",Used=123}));
        }
        IndependentStore.Ensure(s.PulseDb);using(var db=new ReadOnlyDb(s.PulseDb)) Check(Json.N(db.Query("PRAGMA user_version")[0],"user_version")==3 && db.Query("SELECT value FROM independent_meta WHERE key='keep'")[0]["value"].ToString()=="value" && PulseStore.ReadHistory(s.PulseDb,"").Keys[0].Used==123,"V0.3 database upgrades to schema 3 and preserves history and account metadata");
    }
    static void Interface() {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};var s=Fixture("interface");s.Topmost=false;s.ClickThroughWhenPinned=false;s.UsageSource="local";Settings.FilePath=Path.Combine(root,"ui-settings.json");
        var snap=new Snapshot();snap.UsageViews.Add(new UsageView {Id="official",Name="官方账号",HasData=true,HasDaily=true,DateBasis="官方日期",Groups=new List<KeyUsage> {new KeyUsage {Id="account",Name="示例账号",Used=123,CostKnown=false}},Daily=new List<DailyUsage> {new DailyUsage {KeyId="account",Day=DataReader.ChinaToday.ToString("yyyy-MM-dd"),Used=123,CostKnown=false}}});
        var local=new UsageView {Id="local",Name="本机记录",HasData=true,HasDaily=true,Notice="仅统计本机保留的日志；未归属记录不计入当前账号。",Groups=new List<KeyUsage> {new KeyUsage {Id="unassigned",Name="未归属账号",Used=456,CostKnown=false}},Daily=new List<DailyUsage> {new DailyUsage {KeyId="unassigned",Day=DataReader.ChinaToday.ToString("yyyy-MM-dd"),Used=456,CostKnown=false}}};snap.UsageViews.Add(local);snap.UsageViews.Add(new UsageView {Id="history",Name="迁移历史",HasData=true,Groups=new List<KeyUsage> {new KeyUsage {Id="old",Name="历史密钥",Used=888,Cost=1}}});
        string today=DataReader.ChinaToday.ToString("yyyy-MM-dd");
        local.Costs=new LocalCostSummary {PriceVersion="fixture-price",PriceNotice="API 等值参考，不是订阅账单。",CollectedAt=DataReader.Now,Slices=new List<LocalCostSlice> {
            new LocalCostSlice {Identity="unassigned",Model="gpt-4.1",Records=3,Tokens=300,Cost=2.5,Priced=true,FirstDay=today,LastDay=today},
            new LocalCostSlice {Identity="unassigned",Model="unknown-model",Problem="价格表未收录此模型",Records=1,Tokens=100,Priced=false,FirstDay=today,LastDay=today},
            new LocalCostSlice {Identity="account",Model="gpt-4.1",Records=1,Tokens=200,Cost=1,Priced=true,FirstDay=today,LastDay=today}
        }};
        snap.Accounts.Add(new AccountUsage {Id="a",AccountIdentity="account",Name="示例账号"});
        var d=new Dashboard(s);typeof(Dashboard).GetField("loaded",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(d,true);typeof(Dashboard).GetField("latest",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(d,snap);
        try {
            d.Window.Show();d.ToggleDetails();d.Render();d.Window.Width=580;d.Window.Height=700;d.Window.UpdateLayout();
            Check(d.Window.FindName("UsageSourcePanel")==null && d.Window.FindName("UsageSourceName")==null && d.Find<TextBlock>("UsedText").Text=="123" && d.Find<TextBlock>("CostText").Text=="$3.50（部分）" && s.UsageSource=="official","dashboard keeps official tokens and independently renders the partial local cost subtotal");
            Check(d.Find<TextBlock>("CostTitle").Text=="本机参考费用" && d.Find<TextBlock>("CostCoverage").Text.Contains("4 / 5") && d.Find<TextBlock>("CostCoverage").Text.Contains("未归属"),"visible local source and priced coverage explain why cost scope differs from official tokens");
            Check(d.Find<Border>("UsageSummaryCard").ActualHeight<205,"usage summary card stays compact with local cost coverage");
            Check(d.Find<ScrollViewer>("AccountsScroll").ActualHeight>=50,"580 by 700 minimum window preserves usable account details");
            Check(Convert.ToString(d.History.ToolTipFor(DataReader.ChinaToday).Content).Contains("123 Token") && !Convert.ToString(d.History.ToolTipFor(DataReader.ChinaToday).Content).Contains("$0"),"official heatmap shows precise tokens without fabricated zero cost");
            Check(d.Find<TextBlock>("UsedText").Text=="123" && d.Find<TextBlock>("UsageDateBasis").Text.Contains("官方日期"),"official usage retains total and date basis without summing sources");
            s.UsageSource="history";d.Render();Check(s.UsageSource=="official" && d.Find<TextBlock>("UsedText").Text=="123","saved history selection migrates to official usage");
            s.SelectedUsageAccount="account";d.Render();Check(d.Find<TextBlock>("CostText").Text=="$1.00" && !d.Find<TextBlock>("CostCoverage").Text.Contains("未归属"),"account selection limits cost to reliably attributed local records");
            s.SelectedUsageAccount="";d.Render();
            var costWindow=d.CreateCostWindow();costWindow.Show();costWindow.UpdateLayout();Check(costWindow.Title.Contains("本机参考费用") && costWindow.ActualHeight<=SystemParameters.WorkArea.Height,"cost details expose model breakdown, unpriced causes and bounded layout");
            var costBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)costWindow.ActualWidth,(int)costWindow.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);costBitmap.Render(costWindow);var costPng=new System.Windows.Media.Imaging.PngBitmapEncoder();costPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(costBitmap));using(var file=File.Create(Path.Combine(root,"cost-details.png"))) costPng.Save(file);costWindow.Close();
            foreach(var theme in ThemePalette.All) {d.ApplyTheme(theme.Id);d.Window.UpdateLayout();d.Capture(Path.Combine(root,"dashboard-"+theme.Id+".png"));}
            var settings=d.CreateSettingsWindow();settings.Show();settings.UpdateLayout();Check(settings.Title.Contains("V0.4"),"V0.4 settings retain theme-aware dialog");
            foreach(var theme in ThemePalette.All) {
                d.ApplyTheme(theme.Id);theme.Apply(settings);
                var prices=PriceTableWindow.Create(settings,theme);prices.Show();prices.UpdateLayout();
                Check(prices.ActualHeight<=SystemParameters.WorkArea.Height && ((System.Windows.Media.SolidColorBrush)prices.Background).Color==ThemePalette.Brush(theme.Dialog).Color,"built-in prices render with "+theme.Id+" theme in the work area");
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)prices.ActualWidth,(int)prices.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(prices);
                var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,"prices-"+theme.Id+".png"))) png.Save(file);prices.Close();
            }
            settings.Close();
        } finally {d.ExitFromTray();}
    }
    [STAThread] static int Main(string[] args) {
        root=Path.Combine(Path.GetFullPath(args[0]),"usage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {if(args.Length>2 && args[1]=="--live-settings") return Live(args[2]);Upgrade();Official();Incremental();Cumulative();Attribution();Pricing();CostCoverage();Interface();File.WriteAllLines(Path.Combine(root,"report.txt"),report);Console.WriteLine(string.Join("\n",report));Console.WriteLine(root);return 0;}
        catch(Exception e) {report.Add("FAIL "+e);File.WriteAllLines(Path.Combine(root,"report.txt"),report);Console.WriteLine(string.Join("\n",report));Console.WriteLine(root);return 1;}
    }
}
