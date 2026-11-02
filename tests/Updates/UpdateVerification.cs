using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexPulse;

internal static class UpdateVerification {
    static readonly List<string> report=new List<string>();
    static string root,oldFixture,newFixture;
    static void Check(bool ok,string text) {if(!ok) throw new Exception(text);report.Add("PASS "+text);}
    static void Reject(Action action,string text) {bool rejected=false;try {action();} catch {rejected=true;}Check(rejected,text);}
    static string DirectoryFor(string name) {string path=Path.Combine(root,name+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    static string ReleaseJson(string version=null) {
        version=version??(AppUpdater.CurrentVersion.Major+"."+AppUpdater.CurrentVersion.Minor+"."+(AppUpdater.CurrentVersion.Build+1));
        string prefix=AppUpdater.ReleasesUrl+"/download/v"+version+"/";
        return Json.New().Serialize(new UpdateReleaseDto {tag_name="v"+version,html_url=AppUpdater.ReleasesUrl+"/tag/v"+version,body="版本说明",assets=new[] {
            new UpdateAssetDto {name="CodexPulse-v"+version+"-windows-x64.zip",browser_download_url=prefix+"CodexPulse-v"+version+"-windows-x64.zip",state="uploaded",size=20000},
            new UpdateAssetDto {name="SHA256SUMS.txt",browser_download_url=prefix+"SHA256SUMS.txt",state="uploaded",size=100}
        }});
    }
    static void Metadata() {
        var release=AppUpdater.Parse(ReleaseJson());
        Check(release.Version>AppUpdater.CurrentVersion,"new stable release compares numerically against installed version");
        Check(AppUpdater.SameVersion(new Version("0.4.4"),new Version("0.4.4.0")),"three-part tag and four-part assembly versions are equivalent");
        var dto=Json.New().Deserialize<UpdateReleaseDto>(ReleaseJson());dto.prerelease=true;
        Reject(()=>AppUpdater.Parse(Json.New().Serialize(dto)),"prerelease never enters stable updates");dto.prerelease=false;dto.draft=true;
        Reject(()=>AppUpdater.Parse(Json.New().Serialize(dto)),"draft release is rejected");dto.draft=false;
        dto.assets[0].browser_download_url="https://example.com/CodexPulse.exe";
        Reject(()=>AppUpdater.Parse(Json.New().Serialize(dto)),"foreign download source is rejected");
        dto=Json.New().Deserialize<UpdateReleaseDto>(ReleaseJson());dto.assets=dto.assets.Take(1).ToArray();
        Reject(()=>AppUpdater.Parse(Json.New().Serialize(dto)),"release without checksum attachment is rejected");
        string hash=new string('a',64),name=release.PackageName;
        Check(AppUpdater.Checksum(hash+"  "+name+"\n",name)==hash,"exact named checksum is parsed");
        Reject(()=>AppUpdater.Checksum(hash+"  other.zip",name),"checksum for another file cannot authorize this update");
        Reject(()=>AppUpdater.Checksum(hash+"  "+name+"\n"+hash+"  "+name,name),"ambiguous duplicate checksums are rejected");
        Check(new Settings().AutoCheckUpdates && Json.New().Deserialize<Settings>("{}").AutoCheckUpdates,"existing settings gain automatic checks with a compatible default");
        var args=UpdateInstaller.RestartArguments(new[] {"--inspect","discard.json","--settings","C:\\settings folder\\prefs.json","--pulse-db","C:\\data\\pulse.db","--autostart"});
        Check(args.Length==4 && args[0]=="--settings" && args[2]=="--pulse-db","restart preserves custom settings and database flags without replaying diagnostic commands");
    }
    static string Archive(string directory,bool unsafePath=false,bool duplicate=false) {
        string zip=Path.Combine(directory,"package.zip");
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)) {
            archive.CreateEntryFromFile(typeof(AppUpdater).Assembly.Location,"CodexPulse/CodexPulse.exe");
            archive.CreateEntryFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pulse.ico"),"CodexPulse/pulse.ico");
            using(var writer=new StreamWriter(archive.CreateEntry("CodexPulse/data/settings.json").Open())) writer.Write("must not extract");
            if(unsafePath) archive.CreateEntry("../escape.txt");
            if(duplicate) archive.CreateEntryFromFile(typeof(AppUpdater).Assembly.Location,"CodexPulse/CodexPulse.exe");
        }
        return zip;
    }
    static void Packages() {
        string directory=DirectoryFor("archive"),zip=Archive(directory);
        var prepared=AppUpdater.Extract(zip,directory,AppUpdater.CurrentVersion);
        Check(File.Exists(prepared.Executable) && File.Exists(prepared.Icon) && !Directory.Exists(Path.Combine(directory,"data")),"only executable and icon are extracted; packaged runtime data is ignored");
        directory=DirectoryFor("traversal");zip=Archive(directory,true);
        Reject(()=>AppUpdater.Extract(zip,directory,AppUpdater.CurrentVersion),"ZIP traversal is rejected before extracting any payload");
        Check(!File.Exists(Path.Combine(directory,"CodexPulse.exe")),"unsafe archive leaves no extracted executable");
        directory=DirectoryFor("duplicate");zip=Archive(directory,false,true);
        Reject(()=>AppUpdater.Extract(zip,directory,AppUpdater.CurrentVersion),"duplicate executable entries are rejected");
        directory=DirectoryFor("version");zip=Archive(directory);
        Reject(()=>AppUpdater.Extract(zip,directory,new Version("99.0.0")),"assembly version must match the release tag");
    }
    static void Transaction() {
        string directory=DirectoryFor("transaction"),install=DirectoryFor("installed"),target=Path.Combine(install,"CodexPulse.exe"),icon=Path.Combine(install,"pulse.ico");
        File.Copy(oldFixture,target);File.WriteAllText(icon,"old-icon");File.Copy(newFixture,Path.Combine(directory,"CodexPulse.exe"));File.WriteAllText(Path.Combine(directory,"pulse.ico"),"new-icon");
        string data=Path.Combine(install,"data");Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data,"settings.json"),"saved-settings");File.WriteAllText(Path.Combine(data,"pulse.db"),"saved-database");
        string original=AppUpdater.Hash(target);
        Reject(()=>UpdateInstaller.InstallFiles(directory,target,i=>{if(i==1) throw new IOException("simulated icon failure");}),"failure replacing the second application file rolls back the executable");
        Check(AppUpdater.Hash(target)==original && File.ReadAllText(icon)=="old-icon","failed transaction restores the original executable and icon");
        UpdateInstaller.InstallFiles(directory,target);
        Check(AppUpdater.Hash(target)==AppUpdater.Hash(newFixture) && File.ReadAllText(icon)=="new-icon","successful transaction replaces both application files");
        Check(File.ReadAllText(Path.Combine(data,"settings.json"))=="saved-settings" && File.ReadAllText(Path.Combine(data,"pulse.db"))=="saved-database","successful and failed updates preserve settings and database byte content");
        var plan=new UpdatePlan {ParentId=1,InstallExe=target,Version="0.4.4",OriginalHash=AppUpdater.Hash(target),ExecutableHash=AppUpdater.Hash(Path.Combine(directory,"CodexPulse.exe")),IconHash=AppUpdater.Hash(Path.Combine(directory,"pulse.ico"))};
        UpdateInstaller.Validate(plan,directory);
        File.AppendAllText(Path.Combine(directory,"pulse.ico"),"tamper");
        Reject(()=>UpdateInstaller.Validate(plan,directory),"helper revalidates staged payload hashes before replacing installed files");
        plan.IconHash=AppUpdater.Hash(Path.Combine(directory,"pulse.ico"));File.AppendAllText(target,"changed");
        Reject(()=>UpdateInstaller.Validate(plan,directory),"concurrently changed installed executable is rejected");
    }
    static async Task Helper() {
        string install=DirectoryFor("helper install 中文"),work=DirectoryFor("helper stage 中文"),target=Path.Combine(install,"CodexPulse.exe");
        File.Copy(oldFixture,target);File.WriteAllText(Path.Combine(install,"pulse.ico"),"old");
        File.Copy(newFixture,Path.Combine(work,"CodexPulse.exe"));File.WriteAllText(Path.Combine(work,"pulse.ico"),"new");
        File.Copy(typeof(AppUpdater).Assembly.Location,Path.Combine(work,"update-helper.exe"));
        string ready=Path.Combine(root,"parent-ready.txt"),release=Path.Combine(root,"parent-exit.txt"),started=Path.Combine(root,"started 新版.txt");
        Process parent=null,helper=null;
        try {
            parent=Process.Start(new ProcessStartInfo {FileName=target,Arguments="--wait "+UpdateInstaller.Quote(ready)+" "+UpdateInstaller.Quote(release),UseShellExecute=false,CreateNoWindow=true});
            for(int i=0;i<100 && !File.Exists(ready);i++) await Task.Delay(50);
            Check(File.Exists(ready),"isolated old application is running");
            string oldHash=AppUpdater.Hash(target);
            var plan=new UpdatePlan {ParentId=parent.Id,ParentStartedUtc=parent.StartTime.ToUniversalTime().Ticks,InstallExe=target,OriginalHash=oldHash,Version="0.4.4",ExecutableHash=AppUpdater.Hash(newFixture),IconHash=AppUpdater.Hash(Path.Combine(work,"pulse.ico")),RestartArgs=new[] {"--mark",started}};
            File.WriteAllText(Path.Combine(work,"update-plan.json"),Json.New().Serialize(plan));
            helper=Process.Start(new ProcessStartInfo {FileName=Path.Combine(work,"update-helper.exe"),Arguments="--apply-update "+UpdateInstaller.Quote(Path.Combine(work,"update-plan.json")),UseShellExecute=false,CreateNoWindow=true});
            await Task.Delay(400);
            Check(!helper.HasExited && AppUpdater.Hash(target)==oldHash,"real helper waits for the original process and does not replace a running executable");
            File.WriteAllText(release,"exit");
            bool exited=await Task.Run(()=>helper.WaitForExit(20000));
            Check(exited && helper.ExitCode==0,"real updater helper completes replacement successfully");
            for(int i=0;i<100 && !File.Exists(started);i++) await Task.Delay(50);
            Check(File.Exists(started) && File.ReadAllText(started)=="new","new executable restarts at the original path with quoted Unicode arguments");
            Check(File.ReadAllText(Path.Combine(work,"update-result.txt"))=="success 0.4.4","helper records successful completion");
        } finally {foreach(var p in new[] {parent,helper}) if(p!=null) {if(!p.HasExited) {p.Kill();p.WaitForExit(2000);}p.Dispose();}}
    }
    static async Task Live() {
        var updater=new AppUpdater();var release=await updater.CheckAsync(CancellationToken.None);
        Check(release.PackageUrl.StartsWith(AppUpdater.ReleasesUrl+"/download/"),"live GitHub stable release discovery uses the official repository");
        var prepared=await updater.PrepareAsync(release,null,CancellationToken.None);
        Check(File.Exists(prepared.Executable),"live published package downloads, matches SHA256, and passes assembly verification without installing");
        Directory.Delete(prepared.Directory,true);
    }
    static IEnumerable<T> Controls<T>(DependencyObject parent) where T:DependencyObject {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i);if(child is T) yield return (T)child;
            foreach(var item in Controls<T>(child)) yield return item;
        }
    }
    static void UI() {
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Settings.FilePath=Path.Combine(root,"ui-settings.json");
        var dashboard=new Dashboard(new Settings {PulseDb=Path.Combine(root,"ui.db"),CodexHome=Path.Combine(root,"fixture-home"),AutoCheckUpdates=false});
        Window settings=null,dialog=null;
        try {
            settings=dashboard.CreateSettingsWindow();settings.Show();settings.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(()=>{}));
            Check(Controls<Button>(settings).Any(b=>Convert.ToString(b.Content)=="检查更新") && Controls<CheckBox>(settings).Any(c=>Convert.ToString(c.Content).Contains("自动检查新版本") && c.IsChecked==false),"settings expose manual update and preserve the disabled automatic-check preference");
            bool observed=false;
            var window=new UpdateWindow(settings,ThemePalette.Get("green"),new AppUpdater(),AppUpdater.Parse(ReleaseJson()),r=>observed=true,()=>null,p=>{throw new Exception("UI test must not install");});
            dialog=window.Window;dialog.Show();dialog.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(()=>{}));dialog.UpdateLayout();
            Check(observed && Controls<Button>(dialog).Any(b=>Convert.ToString(b.Content)=="下载并重启更新" && b.IsEnabled),"update window shows a cached newer version and enables the explicit download/restart action");
            Check(!dialog.Topmost && (Backdrop.GetWindowLong(new WindowInteropHelper(dialog).Handle,-20)&8)==0 && dialog.Owner==settings,"update window keeps a normal settings owner and never inherits status-bar topmost");
            var bitmap=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(dialog);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,"update-window.png"))) png.Save(file);
            Check(Controls<TextBox>(dialog).Any(t=>t.IsReadOnly && t.Text.Contains("版本说明")),"release notes are read-only and scrollable");
        } finally {if(dialog!=null) dialog.Close();if(settings!=null) settings.Close();dashboard.ExitFromTray();app.Shutdown();}
    }
    [STAThread] static int Main(string[] args) {
        root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);oldFixture=args[1];newFixture=args[2];
        int result=0;
        try {Metadata();Packages();Transaction();Helper().GetAwaiter().GetResult();UI();if(args.Contains("--live")) Live().GetAwaiter().GetResult();}
        catch(Exception error) {report.Add("FAIL "+error);result=1;}
        File.WriteAllLines(Path.Combine(root,"report.txt"),report);foreach(string line in report) Console.WriteLine(line);return result;
    }
}
