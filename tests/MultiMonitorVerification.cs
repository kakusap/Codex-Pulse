using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using CodexPulse;
using Forms=System.Windows.Forms;

internal static class MultiMonitorVerification {
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
    static readonly List<string> report=new List<string>();
    static int result=1;
    static void Require(bool value,string message) { if(!value) throw new Exception(message); }
    static void Check(Dashboard dashboard,Forms.Screen screen,string stage) {
        var hwnd=new WindowInteropHelper(dashboard.Window).Handle;NativeRect rect;
        Require(GetWindowRect(hwnd,out rect),"Cannot read native window bounds.");
        var area=screen.WorkingArea;
        int expected=area.Left+(area.Width-(rect.Right-rect.Left))/2;
        Require(Math.Abs(rect.Left-expected)<=2 && Math.Abs(rect.Top-area.Top)<=2,stage+": window did not dock at target screen's top center.");
        Require(MonitorDock.Current(dashboard.Window).DeviceName==screen.DeviceName,stage+": window returned to another monitor.");
        report.Add("PASS: "+stage+" on "+screen.DeviceName+" native bounds="+rect.Left+","+rect.Top+" "+(rect.Right-rect.Left)+"x"+(rect.Bottom-rect.Top));
    }
    [STAThread] static int Main(string[] args) {
        string folder=Path.GetFullPath(args[0]);Directory.CreateDirectory(folder);
        Settings.FilePath=Path.Combine(folder,"settings.json");
        var screens=Forms.Screen.AllScreens;
        var target=screens[screens.Length-1];
        var config=new Settings { MonitorDevice=target.DeviceName,PulseDb=Path.Combine(folder,"pulse.db"),CodexHome=Path.Combine(folder,"codex-home"),Topmost=true,Width=560 };
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        Dashboard active=null;
        try {
            active=new Dashboard(config);app.MainWindow=active.Window;
            active.Window.Loaded+=async delegate {
                try {
                    await Task.Delay(400);Check(active,target,"startup restore");
                    foreach(var screen in screens) {
                        active.MoveToMonitor(screen.DeviceName);await Task.Delay(180);Check(active,screen,"move");
                        Require(Settings.Load().MonitorDevice==screen.DeviceName,"Selected monitor was not persisted.");
                        active.ToggleDetails();await Task.Delay(100);Check(active,screen,"expand details");
                        active.ToggleDetails();await Task.Delay(100);Check(active,screen,"collapse details");
                        active.Window.Width=580;await Task.Delay(100);Check(active,screen,"resize");
                        active.Window.Width=560;await Task.Delay(100);Check(active,screen,"resize back");
                    }
                    Require(MonitorDock.Resolve("nonexistent-display").Primary,"Disconnected monitor fallback must use the primary display.");
                    var saved=Settings.Load();var expected=MonitorDock.Resolve(saved.MonitorDevice);
                    active.ExitFromTray();active=null;
                    active=new Dashboard(saved);app.MainWindow=active.Window;active.Window.Show();
                    await Task.Delay(400);Check(active,expected,"new window restores saved monitor");
                    report.Add("PASS: monitor choice survives recreation; missing monitor resolves to primary; no mouse input was injected.");
                    result=0;
                } catch(Exception error) { report.Add("FAIL: "+error); }
                finally {
                    if(active!=null) { active.ExitFromTray();active=null; }
                    app.Shutdown(result);
                }
            };
            app.Run(active.Window);
        } catch(Exception error) { report.Add("FAIL: "+error); }
        File.WriteAllLines(Path.Combine(folder,"report.txt"),report);
        foreach(var line in report) Console.WriteLine(line);
        return result;
    }
}
