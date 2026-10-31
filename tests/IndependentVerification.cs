using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexPulse;

class IndependentVerification {
    class FixtureDb : SqliteDatabase { public FixtureDb(string path) : base(path,true) { } }
    static List<string> report=new List<string>();
    static string root;
    static void Check(bool value,string name) { if(!value) throw new Exception(name);report.Add("PASS "+name); }
    static string Jwt(object claims) { return "header."+Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.New().Serialize(claims))).TrimEnd('=').Replace('+','-').Replace('/','_')+".signature"; }
    static AccountCredential Auth(string user,long exp) {
        string access=Jwt(new Dictionary<string,object> { {"sub",user},{"exp",exp},{"https://api.openai.com/auth",new { chatgpt_account_id="workspace-"+user,chatgpt_plan_type="plus" }} });
        string id=Jwt(new { sub=user,email=user+"@example.test",exp=exp });
        return AccountCredential.Parse(Json.New().Serialize(new { tokens=new { access_token=access,id_token=id,refresh_token="refresh-secret-"+user,account_id="workspace-"+user },last_refresh="2026-01-01T00:00:00Z" }));
    }
    class Fixture {
        public int Rotations,QuotaCalls;
        public string Failure="";public bool Cancel,WrongRenew;
        public string CredentialStore="file";
        public ICodexRpc Create(string home) { return new Session(this,home); }
    }
    class Desktop : ICodexDesktop { public int Starts;public void Prepare() { }public void Restart() { Starts++; } }
    class Session : ICodexRpc {
        readonly Fixture fixture;readonly string home;
        public Session(Fixture fixture,string home) { this.fixture=fixture;this.home=home; }
        public Dictionary<string,object> Call(string method,object args,int timeout) {
            var p=Json.New().Deserialize<Dictionary<string,object>>(Json.New().Serialize(args));
            if(method=="account/login/start") return new Dictionary<string,object> { {"loginId","login"},{"authUrl","https://auth.openai.com/fixture"},{"verificationUrl","https://auth.openai.com/codex/device"},{"userCode","fixture-code"} };
            if(method=="account/read") {
                var auth=AccountCredential.Parse(File.ReadAllText(Path.Combine(home,"auth.json")));
                if(p.ContainsKey("refreshToken") && object.Equals(p["refreshToken"],true)) {
                    fixture.Rotations++;var next=Auth(fixture.WrongRenew?"wrong":auth.UserId,DataReader.Now+7200);
                    File.WriteAllText(Path.Combine(home,"auth.json"),next.JsonValue(true));
                }
                return new Dictionary<string,object> { {"account",new Dictionary<string,object> { {"type","chatgpt"} }} };
            }
            if(method=="account/rateLimits/read") {
                fixture.QuotaCalls++;
                var auth=AccountCredential.Parse(File.ReadAllText(Path.Combine(home,"auth.json")));
                Check(AccountCredential.Object(auth.Auth,"tokens")["refresh_token"].ToString()=="","quota worker does not receive a refresh token");
                if(!string.IsNullOrEmpty(fixture.Failure)) throw new AccountOperationException(fixture.Failure,"fixture failure");
                return Json.New().Deserialize<Dictionary<string,object>>(Json.New().Serialize(new { rateLimits=new { primary=new { usedPercent=25,windowDurationMins=300,resetsAt=DataReader.Now+300 },secondary=new { usedPercent=50,windowDurationMins=10080,resetsAt=DataReader.Now+1000 } } }));
            }
            if(method=="config/batchWrite") {
                string path=Json.S(p,"filePath");string content=File.Exists(path)?File.ReadAllText(path):"";
                File.WriteAllText(path,"model_provider=\"openai\"\n# preserved fixture\n");return new Dictionary<string,object>();
            }
            if(method=="config/read") return new Dictionary<string,object> { {"config",new Dictionary<string,object> { {"model_provider","openai"},{"cli_auth_credentials_store",fixture.CredentialStore} }} };
            throw new Exception(method);
        }
        public bool WaitLogin(string id,CancellationToken cancel) {
            if(fixture.Cancel || cancel.IsCancellationRequested) return false;
            File.WriteAllText(Path.Combine(home,"auth.json"),Auth("new",DataReader.Now+3600).JsonValue(true));return true;
        }
        public void Dispose() { }
    }
    static Settings SettingsFor(string suffix) {
        string path=Path.Combine(root,suffix);Directory.CreateDirectory(path);
        return new Settings { PulseDb=Path.Combine(path,"pulse.db"),CodexHome=Path.Combine(path,"codex-home") };
    }
    static AccountUsage Save(Settings settings,string user,long exp) {
        IndependentStore.Ensure(settings.PulseDb);return IndependentStore.Save(settings.PulseDb,new AccountUsage { Id=user,Name=user,Status="active",PrimaryUsed=80,PrimaryMinutes=300,Captured=1 },Auth(user,exp));
    }
    static void Workflows() {
        var settings=SettingsFor("workflows");var a=Save(settings,"one",DataReader.Now+3600);var b=Save(settings,"two",DataReader.Now-60);
        var fixture=new Fixture();var service=new IndependentAccounts(settings,fixture.Create);
        var snapshot=service.Refresh(true);Check(snapshot.Accounts.Count==2 && snapshot.Accounts.All(x=>x.Captured>1 && x.RefreshError==""),"two independent accounts refresh using only the local account store");
        Check(fixture.Rotations==1 && IndependentStore.Credential(settings.PulseDb,"two").Expires>DataReader.Now,"expired inactive account renews once and persists new credentials");
        int calls=fixture.QuotaCalls;service.Refresh(false);Check(fixture.QuotaCalls==calls,"periodic refresh uses stored cache interval");
        fixture.Failure="network";snapshot=service.Refresh(true);Check(snapshot.Accounts.All(x=>x.PrimaryUsed==25 && x.RefreshError!=""),"network failure retains previous quota snapshot");
        fixture.Failure="limited";snapshot=service.Refresh(true);Check(snapshot.Accounts.All(x=>x.RetryAfter>=DataReader.Now+295 && x.Status=="active"),"rate limits back off without disabling accounts");
        fixture.Failure="login";snapshot=service.Refresh(true);Check(snapshot.Accounts.All(x=>x.Status=="needs_login"),"invalid login is shown as needing reauthentication");
        fixture.Failure="";
        var runtime=new IndependentRuntime(settings,fixture.Create);LoginPrompt prompt=null;
        var added=runtime.Login(null,false,p=>prompt=p,CancellationToken.None);Check(prompt!=null && prompt.Url.StartsWith("https://auth.openai.com") && added.Status=="active","browser login stores independently encrypted account");
        int count=IndependentStore.Accounts(settings.PulseDb).Count;runtime.Login(null,true,p=>prompt=p,CancellationToken.None);Check(prompt.Device && IndependentStore.Accounts(settings.PulseDb).Count==count,"device login and duplicate identity replace instead of adding duplicates");
        bool rejected=false;try { runtime.Login("one",false,p=>{},CancellationToken.None); } catch(AccountOperationException) { rejected=true; }Check(rejected,"reauthentication cannot replace an account with a different identity");
        fixture.Cancel=true;rejected=false;try { runtime.Login(null,false,p=>{},CancellationToken.None); } catch(AccountOperationException) { rejected=true; }Check(rejected && IndependentStore.Accounts(settings.PulseDb).Count==count,"cancelled login leaves account store unchanged");
        IndependentStore.Remove(settings.PulseDb,added.Id);Check(IndependentStore.Accounts(settings.PulseDb).Count==count-1,"removing account removes its independent credential");
        var activeSettings=SettingsFor("active");Save(activeSettings,"active",DataReader.Now-10);Directory.CreateDirectory(activeSettings.CodexHome);File.WriteAllText(Path.Combine(activeSettings.CodexHome,"auth.json"),Auth("active",DataReader.Now-10).JsonValue(true));
        var activeFixture=new Fixture();var activeService=new IndependentAccounts(activeSettings,activeFixture.Create);activeService.Refresh(true);
        Check(activeFixture.Rotations==0,"active desktop account is never renewed concurrently by Pulse");
        File.WriteAllText(Path.Combine(activeSettings.CodexHome,"auth.json"),Auth("active",DataReader.Now+3600).JsonValue(true));activeService.Refresh(true);
        Check(IndependentStore.Credential(activeSettings.PulseDb,"active").Expires>DataReader.Now && activeFixture.QuotaCalls==1,"desktop renewal is synchronized back to encrypted store");
        var keyringSettings=SettingsFor("keyring");Save(keyringSettings,"keyring",DataReader.Now-10);var keyringFixture=new Fixture { CredentialStore="keyring" };
        var keyringSnapshot=new IndependentAccounts(keyringSettings,keyringFixture.Create).Refresh(true);
        Check(keyringFixture.Rotations==0 && keyringSnapshot.Accounts[0].RefreshError!="","unidentified system credential owner prevents unsafe parallel renewal");
        var q=new AccountUsage();IndependentRuntime.ApplyQuota(q,Json.New().Deserialize<Dictionary<string,object>>("{\"rateLimits\":{\"primary\":{\"usedPercent\":11,\"windowDurationMins\":60,\"resetsAt\":123}}}"));
        Check(q.PrimaryMinutes==60 && !QuotaSummary.For(new[] {q},300).Remaining.HasValue,"nonstandard quota windows are preserved and excluded from five-hour average");
        Check(!Directory.GetDirectories(Path.Combine(Path.GetDirectoryName(settings.PulseDb),"private-accounts")).Any(),"all isolated credential files are removed after operations");
        var wrongSettings=SettingsFor("wrong-renew");Save(wrongSettings,"expired",DataReader.Now-10);var wrongFixture=new Fixture { WrongRenew=true };
        var wrongResult=new IndependentAccounts(wrongSettings,wrongFixture.Create).Refresh(true);
        Check(wrongResult.Accounts[0].Status=="needs_login" && IndependentStore.Credential(wrongSettings.PulseDb,"expired").UserId=="expired","renewal identity mismatch cannot overwrite stored credentials");
        var switching=SettingsFor("switching");var target=Save(switching,"target",DataReader.Now+3600);Directory.CreateDirectory(switching.CodexHome);
        File.WriteAllText(Path.Combine(switching.CodexHome,"config.toml"),"# original\n");
        var switchFixture=new Fixture();var desktop=new Desktop();var controller=new AccountSwitchController(()=>new IndependentAccounts(switching,switchFixture.Create),()=>desktop);
        controller.Switch(target).GetAwaiter().GetResult();
        Check(!controller.HasError && desktop.Starts==1 && new ProfileFiles(switching).Current().UserId=="target" && controller.CurrentAccountId=="target","independent switch applies, restarts and confirms selected account independently");
    }
    static void Transactions() {
        var settings=SettingsFor("transactions");Directory.CreateDirectory(settings.CodexHome);
        var files=new ProfileFiles(settings);byte[] old=Encoding.UTF8.GetBytes("# previous config\nmodel_provider=\"custom\"\n");byte[] next=Encoding.UTF8.GetBytes("model_provider=\"openai\"\n");
        File.WriteAllBytes(Path.Combine(settings.CodexHome,"config.toml"),old);File.WriteAllText(Path.Combine(settings.CodexHome,"auth.json"),Auth("old",DataReader.Now+3600).JsonValue(true));
        bool failed=false;try { files.Apply(Auth("new",DataReader.Now+3600),old,next,()=> { throw new IOException("fixture write failure"); }); } catch(IOException) { failed=true; }
        Check(failed && files.Current().UserId=="old" && File.ReadAllBytes(Path.Combine(settings.CodexHome,"config.toml")).SequenceEqual(old),"partial profile write failure restores original auth and config");
        files.Apply(Auth("new",DataReader.Now+3600),old,next,null);Check(files.Current().UserId=="new","profile transaction applies selected account");
        Check(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(settings.PulseDb),"profile-backups"),"*.backup").Length==2,"both successful and failed profile changes retain encrypted backups");
        files.RestoreLatest();Check(files.Current().UserId=="old","encrypted profile backup restores previous account and config");
        failed=false;try { files.Apply(Auth("new",DataReader.Now+3600),old,next,()=>File.WriteAllText(Path.Combine(settings.CodexHome,"auth.json"),Auth("external",DataReader.Now+3600).JsonValue(true))); } catch(IOException) { failed=true; }
        files.Recover();Check(failed && files.Current().UserId=="external" && File.ReadAllBytes(Path.Combine(settings.CodexHome,"config.toml")).SequenceEqual(old),"concurrent auth update is preserved while this transaction's config is restored");
        File.WriteAllText(Path.Combine(settings.CodexHome,"config.toml"),"external change");failed=false;try { files.Apply(Auth("new",DataReader.Now+3600),old,next,null); } catch(IOException) { failed=true; }
        Check(failed && File.ReadAllText(Path.Combine(settings.CodexHome,"config.toml"))=="external change","concurrent external config change is not overwritten");
    }
    static void Compatibility() {
        var settings=SettingsFor("compatibility");settings.PulseSourceId="saved";settings.Theme="pink";settings.Width=640;
        Save(settings,"existing",DataReader.Now+3600);
        using(var db=new FixtureDb(settings.PulseDb)) {
            db.Query("INSERT INTO pulse_sources VALUES(?,?,?,?)","saved","retired-source",DataReader.Now,"{}");
            db.Query("INSERT INTO pulse_keys VALUES(?,?,?,?)","saved","history",0,Json.New().Serialize(new KeyUsage { Id="history",Name="历史用量",Used=12345,Cost=1.25 }));
            db.Query("INSERT INTO pulse_daily VALUES(?,?,?,?)","saved","history",DataReader.ChinaToday.ToString("yyyy-MM-dd"),Json.New().Serialize(new DailyUsage { KeyId="history",Day=DataReader.ChinaToday.ToString("yyyy-MM-dd"),Used=2345 }));
        }
        string previous=Settings.FilePath;Settings.FilePath=Path.Combine(Path.GetDirectoryName(settings.PulseDb),"legacy-settings.json");
        try {
            foreach(string mode in new[] { "manager","imported","independent" }) {
                var raw=Json.New().Deserialize<Dictionary<string,object>>(Json.New().Serialize(settings));
                raw["ManagerDb"]="missing.db";raw["ManagerExe"]="missing.exe";raw["ManagerRpcAddress"]="localhost:48760";raw["DataSource"]=mode;
                File.WriteAllText(Settings.FilePath,Json.New().Serialize(raw));var loaded=Settings.Load();
                Check(loaded.Theme=="pink" && loaded.Width==640 && loaded.PulseDb==settings.PulseDb && loaded.PulseSourceId=="saved","old "+mode+" settings preserve account path, history and appearance");
                var snapshot=new IndependentAccounts(loaded,new Fixture().Create).Refresh(true);
                Check(snapshot.SourceKind=="independent" && snapshot.Accounts.Single().Id=="existing" && snapshot.Keys.Single().Used==12345 && snapshot.Daily.Single().Used==2345,"old "+mode+" settings now use independent accounts and preserve saved history");
                loaded.Save();var saved=Json.New().Deserialize<Dictionary<string,object>>(File.ReadAllText(Settings.FilePath));
                Check(!saved.ContainsKey("ManagerDb") && !saved.ContainsKey("ManagerExe") && !saved.ContainsKey("ManagerRpcAddress") && !saved.ContainsKey("DataSource"),"saving old "+mode+" settings removes retired connection fields");
            }
            Check(IndependentStore.Credential(settings.PulseDb,"existing").UserId=="existing","existing encrypted account remains readable");
            Check(typeof(Dashboard).Assembly.GetType("CodexPulse.ManagerProfiles")==null && typeof(Dashboard).Assembly.GetType("CodexPulse.MigrationWindow")==null,"retired service and migration form are absent from the application");
        } finally { Settings.FilePath=previous; }
    }
    static IEnumerable<T> Controls<T>(DependencyObject parent) where T:DependencyObject {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) { var child=VisualTreeHelper.GetChild(parent,i);if(child is T) yield return (T)child;foreach(var nested in Controls<T>(child)) yield return nested; }
    }
    static void Capture(Window window,string path) {
        window.UpdateLayout();var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(path)) png.Save(file);
    }
    static void Interface() {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };var settings=SettingsFor("interface");
        Save(settings,"personal",DataReader.Now+3600);Save(settings,"work",DataReader.Now+3600);var fixture=new Fixture();var snapshot=new IndependentAccounts(settings,fixture.Create).Refresh(true);
        var owner=new Window { Width=560,Height=56,Left=100,Top=0,ShowInTaskbar=false,ShowActivated=false };
        using(var stream=typeof(Dashboard).Assembly.GetManifestResourceStream("MainWindow.xaml")) owner.Resources=((Window)System.Windows.Markup.XamlReader.Load(stream)).Resources;
        ThemePalette.Get("pink").Apply(owner);var anchor=new Button { Content="独立账号" };owner.Content=anchor;owner.Show();
        var controller=new AccountSwitchController(()=>new IndependentAccounts(settings,fixture.Create),()=>new Desktop());controller.Ready=true;controller.Candidates=new HashSet<string>(snapshot.Accounts.Select(a=>a.Id));
        using(var popup=new QuotaDetailsPopup(owner,()=>MonitorDock.Resolve("").DeviceName) { AccountSwitch=controller }) {
            bool called=false;popup.ManageAccounts=()=>called=true;popup.Toggle("accounts",anchor,snapshot,ThemePalette.Get("pink"));
            var manage=Controls<Button>(popup.PopupWindow).Single(b=>Convert.ToString(b.Content)=="账号管理 / 添加账号");manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(called && Controls<Button>(popup.PopupWindow).Count(b=>System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("切换账号 ") && b.IsEnabled)==2,"independent popup exposes account management and two enabled switch actions");
            Capture(popup.PopupWindow,Path.Combine(root,"independent-popup.png"));
        }
        var window=new IndependentAccountWindow(owner,settings,proposed=>{});window.Window.Show();Capture(window.Window,Path.Combine(root,"account-management.png"));
        Check(Controls<Button>(window.Window).Count()==8,"account management exposes login, device code, reauth, removal, refresh, directory settings and recovery");window.Window.Close();owner.Close();
        var isolated=SettingsFor("settings-ui");string previous=Settings.FilePath;Settings.FilePath=Path.Combine(Path.GetDirectoryName(isolated.PulseDb),"settings.json");
        var dashboard=new Dashboard(isolated);dashboard.Window.Show();var dialog=dashboard.CreateSettingsWindow();
        try {
            dialog.Show();Capture(dialog,Path.Combine(root,"settings.png"));
            string labels=string.Join(" ",Controls<TextBlock>(dialog).Select(t=>t.Text).Concat(Controls<Button>(dialog).Select(b=>Convert.ToString(b.Content))));
            Check(!labels.Contains("Manager") && !labels.Contains("接管") && !labels.Contains("兼容模式"),"settings form contains no retired connection fields or migration actions");
            Check(Controls<TextBox>(dialog).Count()==2 && Controls<Button>(dialog).Any(b=>Convert.ToString(b.Content)=="账号管理") && !Controls<CheckBox>(dialog).Any(b=>Convert.ToString(b.Content)=="采集本机 Codex 用量记录") && !labels.Contains("用量与参考费用") && !labels.Contains("用量与热力图固定显示官方账号数据"),"settings retain account management, executable and price table while removing the local collection block");
            Check(dashboard.Window.FindName("OpenManagerButton")==null,"expanded dashboard has no retired launch button");
        } finally { dialog.Close();dashboard.ExitFromTray();Settings.FilePath=previous; }
    }
    static void RealRuntime() {
        var settings=SettingsFor("runtime");var runtime=new IndependentRuntime(settings,null);
        var config=Encoding.UTF8.GetBytes("# keep this comment\nmodel_provider = \"custom\"\nmodel = \"gpt-test\"\n[projects.'C:\\fixture']\ntrust_level = \"trusted\"\n[model_providers.custom]\nname = \"Custom\"\nbase_url = \"http://localhost:9999/v1\"\nwire_api = \"responses\"\n");
        string patched=Encoding.UTF8.GetString(runtime.DirectConfig(config));
        Check(patched.Contains("keep this comment") && patched.Contains("gpt-test") && patched.Contains("trust_level") && patched.Contains("model_providers.custom") && runtime.IsDirect(Encoding.UTF8.GetBytes(patched)),"official TOML writer preserves unrelated config and validates direct provider");
        var profiled=Encoding.UTF8.GetBytes("profile=\"work\"\n[profiles.work]\nmodel_provider=\"custom\"\nmodel=\"gpt-test\"\n[model_providers.custom]\nname=\"Custom\"\nbase_url=\"http://localhost:9999/v1\"\nwire_api=\"responses\"\n");
        bool legacyRejected=false;try { runtime.DirectConfig(profiled); } catch(AccountOperationException e) { legacyRejected=e.Kind=="config"; }
        Check(legacyRejected,"unsupported legacy profile is rejected without writing live configuration");
        using(var home=new PrivateHome(runtime.PrivateRoot)) {
            using(var rpc=new CodexRpc(settings,home.Path)) {
                var result=rpc.Call("account/login/start",new { type="chatgpt" },20000);
                Check(Json.S(result,"authUrl").StartsWith("https://"),"real runtime begins isolated browser login without changing live account");
                var cancel=new CancellationTokenSource();cancel.Cancel();Check(!rpc.WaitLogin(Json.S(result,"loginId"),cancel.Token),"real runtime login cancellation completes");
            }
        }
    }
    [STAThread] static int Main(string[] args) {
        root=Path.Combine(Path.GetFullPath(args[0]),"independent-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try { Workflows();Transactions();Compatibility();if(args.Contains("--runtime")) RealRuntime();Interface();File.WriteAllLines(Path.Combine(root,"report.txt"),report,Encoding.UTF8);Console.WriteLine(string.Join("\n",report));Console.WriteLine(root);return 0; }
        catch(Exception e) { report.Add("FAIL "+e);File.WriteAllLines(Path.Combine(root,"report.txt"),report,Encoding.UTF8);Console.WriteLine(e);Console.WriteLine(root);return 1; }
    }
}
