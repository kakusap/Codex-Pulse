using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexPulse;

class AccountSwitchVerification {
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd,int index);
    static readonly List<string> report=new List<string>();
    static string output;
    class Profiles : IAccountProfiles {
        public DirectProfile Profile=new DirectProfile { Home=@"C:\fake-codex",Mode="direct_account",AccountId="a" };
        public HashSet<string> Ids=new HashSet<string> { "a","b" };
        public int Applies;public bool FailApply,WrongResult,FailGet;
        public ManualResetEvent Gate;
        public DirectProfile Get() { if(FailGet) throw new IOException("offline");return Profile; }
        public HashSet<string> Candidates() { return Ids; }
        public DirectProfile Apply(string id,string home) {
            Interlocked.Increment(ref Applies);if(Gate!=null) Gate.WaitOne();
            if(FailApply) throw new IOException("apply failed");
            Profile=new DirectProfile { Home=home,Mode="direct_account",AccountId=WrongResult?"other":id };return Profile;
        }
    }
    class Desktop : ICodexDesktop {
        public int Restarts;public bool Missing,FailRestart;
        public void Prepare() { if(Missing) throw new IOException("missing desktop"); }
        public void Restart() { Restarts++;if(FailRestart) throw new IOException("restart failed"); }
    }
    static AccountUsage Target=new AccountUsage { Id="b",Name="工作账号",Status="active",PrimaryUsed=30,SecondaryUsed=60,PrimaryMinutes=300,SecondaryMinutes=10080,Captured=DataReader.Now };
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message);report.Add("PASS "+message); }
    static void Run(Task task) {
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(10) };
        timer.Tick+=delegate { if(task.IsCompleted) { timer.Stop();frame.Continue=false; } };
        timer.Start();Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
    }
    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i);if(child is T) yield return (T)child;
            foreach(var nested in Descendants<T>(child)) yield return nested;
        }
    }
    static void Workflow() {
        var m=new Profiles();var d=new Desktop();var c=new AccountSwitchController(()=>m,()=>d);
        Run(c.Refresh());Check(c.Ready && c.CurrentAccountId=="a","read actual current account");
        Run(c.Switch(Target));Check(m.Applies==1 && d.Restarts==1 && c.CurrentAccountId=="b" && !c.HasError && !c.Busy,"apply then restart success");
        m=new Profiles { Gate=new ManualResetEvent(false) };d=new Desktop();c=new AccountSwitchController(()=>m,()=>d);
        Task first=c.Switch(Target);Run(c.Switch(Target));Check(c.Busy,"second click cannot start concurrent switch");m.Gate.Set();Run(first);Check(m.Applies==1 && d.Restarts==1,"only one apply and restart");m.Gate.Dispose();
        m=new Profiles();d=new Desktop { Missing=true };c=new AccountSwitchController(()=>m,()=>d);Run(c.Switch(Target));Check(m.Applies==0 && d.Restarts==0 && c.HasError,"missing desktop prevents profile mutation");
        m=new Profiles { FailApply=true };d=new Desktop();c=new AccountSwitchController(()=>m,()=>d);Run(c.Switch(Target));Check(d.Restarts==0 && c.HasError && c.CurrentAccountId=="a","apply failure reconciles status without restart");
        m=new Profiles { WrongResult=true };d=new Desktop();c=new AccountSwitchController(()=>m,()=>d);Run(c.Switch(Target));Check(d.Restarts==0 && c.HasError,"unconfirmed account does not restart desktop");
        m=new Profiles();m.Ids.Clear();d=new Desktop();c=new AccountSwitchController(()=>m,()=>d);Run(c.Switch(Target));Check(m.Applies==0 && d.Restarts==0,"account absent from local profiles is rejected");
        m=new Profiles();d=new Desktop { FailRestart=true };c=new AccountSwitchController(()=>m,()=>d);Run(c.Switch(Target));Check(c.CurrentAccountId=="b" && c.HasError && c.Message.Contains("账号已切换"),"restart failure retains switched account and clear recovery message");
        d.FailRestart=false;Run(c.Switch(Target));Check(!c.HasError && d.Restarts==2,"reapply recovers failed restart");
        m=new Profiles();d=new Desktop();c=new AccountSwitchController(()=>m,()=>d);Run(c.Switch(new AccountUsage { Id="b",Status="disabled" }));Check(m.Applies==0 && d.Restarts==0,"disabled account cannot switch");
        m.FailGet=true;Run(c.Refresh());Check(!c.Ready && c.HasError && c.CurrentAccountId=="","offline state does not show stale current account");m.FailGet=false;Run(c.Refresh());Check(c.Ready && !c.HasError,"reconnecting clears connection error");
    }
    static void Popup() {
        var m=new Profiles();var d=new Desktop();var c=new AccountSwitchController(()=>m,()=>d);Run(c.Refresh());
        var owner=new Window { Width=560,Height=56,Left=100,Top=0,ShowInTaskbar=false,ShowActivated=false };
        using(var resource=typeof(Dashboard).Assembly.GetManifestResourceStream("MainWindow.xaml")) {
            var source=(Window)XamlReader.Load(resource);owner.Resources=source.Resources;
        }
        var anchor=new Button { Content="全部账号" };owner.Content=anchor;ThemePalette.Get("pink").Apply(owner);owner.Show();
        var snapshot=new Snapshot();snapshot.Accounts.Add(new AccountUsage { Id="a",Name="个人账号",Status="active",PrimaryUsed=15,SecondaryUsed=40,PrimaryMinutes=300,SecondaryMinutes=10080,Captured=DataReader.Now });snapshot.Accounts.Add(Target);snapshot.Accounts.Add(new AccountUsage { Id="c",Name="待重新登录的账号",Status="disabled" });
        using(var popup=new QuotaDetailsPopup(owner,()=>MonitorDock.Resolve("").DeviceName) { AccountSwitch=c }) {
            c.Changed+=()=>popup.Refresh(snapshot,ThemePalette.Get("pink"));popup.Toggle("accounts",anchor,snapshot,ThemePalette.Get("pink"));
            var buttons=Descendants<Button>(popup.PopupWindow).Where(b=>System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("切换账号 ")).ToList();
            Check(buttons.Count==3 && buttons.Count(b=>b.IsEnabled)==2,"account rows expose usable switch buttons and disable inactive account");
            var hwnd=new WindowInteropHelper(popup.PopupWindow).Handle;
            Check((GetWindowLong(hwnd,-20)&0x20)==0,"account popup receives native mouse input");
            var bitmap=new RenderTargetBitmap((int)popup.PopupWindow.ActualWidth,(int)popup.PopupWindow.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(popup.PopupWindow);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,"account-switch-preview.png"))) png.Save(file);
            buttons.Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="切换账号 工作账号").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var frameTask=Task.Run(()=> { var until=DateTime.UtcNow.AddSeconds(5);while(c.Busy && DateTime.UtcNow<until) Thread.Sleep(10); });Run(frameTask);
            Check(m.Applies==1 && d.Restarts==1 && c.CurrentAccountId=="b","popup button invokes complete account switch and restart workflow");
            int refreshes=0;var refreshGate=new TaskCompletionSource<bool>();
            popup.RefreshSnapshot=async delegate { refreshes++;popup.SetRefreshing(true);try { await refreshGate.Task; } finally { popup.SetRefreshing(false); } };
            popup.Toggle("primary",anchor,snapshot,ThemePalette.Get("pink"));
            var refresh=Descendants<Button>(popup.PopupWindow).Single();
            Check(System.Windows.Automation.AutomationProperties.GetName(refresh)=="刷新额度快照" && refresh.IsEnabled,"five-hour popup exposes one refresh icon");
            Check(refresh.TranslatePoint(new Point(),popup.PopupWindow).X>popup.PopupWindow.ActualWidth-55,"refresh icon sits in popup top-right corner");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var rotation=(RotateTransform)((TextBlock)refresh.Content).RenderTransform;
            Run(Task.Delay(180));double firstAngle=rotation.Angle;
            Check(popup.IsRefreshing && !refresh.IsEnabled && rotation.HasAnimatedProperties && firstAngle>0,"refresh click starts rotation and disables duplicate requests");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(refreshes==1,"busy popup rejects repeated refresh clicks");
            popup.Refresh(snapshot,ThemePalette.Get("blue"));Run(Task.Delay(180));
            Check(Descendants<Button>(popup.PopupWindow).Single()==refresh && rotation.HasAnimatedProperties && Math.Abs(rotation.Angle-firstAngle)>1,"live snapshot and theme updates preserve continuous icon rotation");
            popup.Close();Check(!rotation.HasAnimatedProperties,"closing popup releases its animation clock");
            popup.Toggle("primary",anchor,snapshot,ThemePalette.Get("pink"));refresh=Descendants<Button>(popup.PopupWindow).Single();rotation=(RotateTransform)((TextBlock)refresh.Content).RenderTransform;
            Run(Task.Delay(1100));Check(popup.IsRefreshing && !refresh.IsEnabled && rotation.HasAnimatedProperties && rotation.Angle>0,"reopened popup keeps spinning beyond one full rotation");
            refreshGate.SetResult(true);Run(Task.Delay(50));
            Check(!popup.IsRefreshing && refresh.IsEnabled && !rotation.HasAnimatedProperties && rotation.Angle==0,"completion stops rotation and enables the icon again");
            bitmap=new RenderTargetBitmap((int)popup.PopupWindow.ActualWidth,(int)popup.PopupWindow.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(popup.PopupWindow);png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,"quota-refresh-preview.png"))) png.Save(file);
            popup.Toggle("secondary",anchor,snapshot,ThemePalette.Get("pink"));Check(!Descendants<Button>(popup.PopupWindow).Any(),"seven-day popup keeps its existing detail controls");
        }
        owner.Close();
    }
    static void RestartFixture() {
        string directory=Path.Combine(output,"desktop-fixture"),other=Path.Combine(output,"unrelated-fixture");
        string executable=Path.Combine(directory,"ChatGPT.exe");
        File.WriteAllText(Path.Combine(directory,"starts.txt"),"");File.WriteAllText(Path.Combine(other,"starts.txt"),"");
        var first=Process.Start(new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true });
        var unrelated=Process.Start(new ProcessStartInfo(Path.Combine(other,"ChatGPT.exe")) { UseShellExecute=false,CreateNoWindow=true });
        try {
            var until=DateTime.UtcNow.AddSeconds(5);while((File.ReadAllLines(Path.Combine(directory,"starts.txt")).Length<1 || File.ReadAllLines(Path.Combine(other,"starts.txt")).Length<1) && DateTime.UtcNow<until) Thread.Sleep(50);
            var runtime=new CodexDesktop(executable);runtime.Prepare();runtime.Restart();
            until=DateTime.UtcNow.AddSeconds(5);while(File.ReadAllLines(Path.Combine(directory,"starts.txt")).Length<2 && DateTime.UtcNow<until) Thread.Sleep(50);
            Check(first.HasExited && File.ReadAllLines(Path.Combine(directory,"starts.txt")).Length==2,"real restart stops fixture and launches one replacement");
            Check(!unrelated.HasExited,"same-name application in another directory survives restart");
            Check(!CodexDesktop.IsDesktopProcess(Path.Combine(directory,"resources","codex.exe"),directory),"embedded CLI executable is excluded from desktop termination");
            bool rejected=false;try { CodexDesktop.ResolveLauncher(Path.Combine(directory,"resources","codex.exe")); } catch(IOException) { rejected=true; }
            Check(rejected,"CLI path cannot be configured as desktop launcher");
        } finally {
            foreach(var process in Process.GetProcessesByName("ChatGPT")) using(process) try {
                string path=process.MainModule.FileName;
                if(CodexDesktop.IsDesktopProcess(path,directory) || CodexDesktop.IsDesktopProcess(path,other)) { if(!process.HasExited) { process.Kill();process.WaitForExit(3000); } }
            } catch(System.ComponentModel.Win32Exception) { } catch(InvalidOperationException) { }
            first.Dispose();unrelated.Dispose();
        }
    }
    [STAThread] static int Main(string[] args) {
        output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try {
            Workflow();Popup();RestartFixture();
            File.WriteAllLines(Path.Combine(output,"account-switch-report.txt"),report,Encoding.UTF8);Console.WriteLine(string.Join("\n",report));return 0;
        }
        catch(Exception e) { report.Add("FAIL "+e);File.WriteAllLines(Path.Combine(output,"account-switch-report.txt"),report,Encoding.UTF8);Console.WriteLine(e);return 1; }
    }
}
