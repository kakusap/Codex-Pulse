using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CodexPulse {
    public static class Program {
        static Mutex instance;
        static string Option(string[] args,string name) { int i=Array.IndexOf(args,name);return i>=0 && i+1<args.Length?args[i+1]:null; }
        [STAThread] public static void Main(string[] args) {
            try {
                if(Option(args,"--settings")!=null) { Settings.FilePath=Option(args,"--settings");if(!File.Exists(Settings.FilePath)) throw new IOException("指定的设置文件不存在。"); }
                var config=Settings.Load();
                if(Array.IndexOf(args,"--verify-startup")>=0) {
                    Directory.CreateDirectory(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data"));
                    File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","startup-report.txt"),StartupRegistration.Verify());return;
                }
                if(Option(args,"--pulse-db")!=null) config.PulseDb=Option(args,"--pulse-db");
                if(Option(args,"--codex-home")!=null) config.CodexHome=Option(args,"--codex-home");
                if(Option(args,"--check-independent")!=null) {
                    var service=new IndependentAccounts(config);var profile=service.Get();int checkedAccounts=service.CheckSwitchCompatibility();
                    File.WriteAllText(Option(args,"--check-independent"),Json.New().Serialize(new { Mode=profile.Mode,CurrentKnown=!string.IsNullOrEmpty(profile.AccountId),CompatibleAccounts=checkedAccounts }),new UTF8Encoding(false));return;
                }
                if(Option(args,"--refresh-independent")!=null) {
                    var snapshot=new IndependentAccounts(config).Refresh(true);
                    File.WriteAllText(Option(args,"--refresh-independent"),Json.New().Serialize(new { Accounts=snapshot.Accounts.Count,Successful=snapshot.Accounts.Count(a=>a.RefreshError=="" && a.Captured>0),Failures=snapshot.Accounts.Select(a=>new { State=a.Status,Error=a.RefreshError,PrimaryMinutes=a.PrimaryMinutes,SecondaryMinutes=a.SecondaryMinutes }).ToArray(),UsageSources=snapshot.UsageViews.Select(v=>new {Source=v.Id,Available=v.HasData,Groups=v.Groups.Count,DailyDays=v.Daily.Select(d=>d.Day).Distinct().Count(),RecordedTokens=v.Groups.Where(g=>g.UsedKnown).Sum(g=>g.Used)}).ToArray() }),new UTF8Encoding(false));return;
                }
                if(Option(args,"--probe-runtime")!=null) { var report=RuntimeProbe.Check(config,Option(args,"--probe-runtime"));if(!string.IsNullOrEmpty(report.Error)) Environment.ExitCode=1;return; }
                if(Option(args,"--inspect")!=null) { File.WriteAllText(Option(args,"--inspect"),Json.New().Serialize(DataReader.Read(config)),new UTF8Encoding(false));return; }
                bool smoke=Array.IndexOf(args,"--smoke-test")>=0;
                bool created;
                instance=new Mutex(true,smoke?"Local\\CodexPulseSmoke":"Local\\CodexPulseDesktop",out created);
                if(!created) { if(Array.IndexOf(args,"--autostart")>=0) return; MessageBox.Show("Codex Pulse 已在运行。双击系统托盘图标可显示看板。","Codex Pulse");return; }
                if(smoke) Settings.FilePath=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","smoke-settings.json");
                var app=new System.Windows.Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += delegate(object sender,DispatcherUnhandledExceptionEventArgs e) { WriteError(e.Exception);MessageBox.Show("看板遇到错误："+e.Exception.Message,"Codex Pulse");e.Handled=true; };
                var dashboard=new Dashboard(config);app.MainWindow=dashboard.Window;
                // Developer visual check: broad color bands behind the actual HWND.
                // This is never shown during normal startup.
                if(Array.IndexOf(args,"--glass-probe")>=0) {
                    var bands=new StackPanel();
                    foreach(string color in new [] { "#1B2944","#16A6A0","#852E9E","#D48B41","#193A62" }) {
                        var band=new Border { Height=(dashboard.Window.Height+60)/5,Background=(SolidColorBrush)new BrushConverter().ConvertFromString(color) };
                        band.Child=new TextBlock { Text="BACKGROUND  0123456789",Foreground=Brushes.White,FontSize=28,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };bands.Children.Add(band);
                    }
                    var probe=new Window { Title="Codex Pulse · Glass Probe",Left=dashboard.Window.Left-30,Top=dashboard.Window.Top-20,Width=dashboard.Window.Width+60,Height=dashboard.Window.Height+60,WindowStyle=WindowStyle.None,ShowInTaskbar=false,Content=bands };
                    probe.Show();dashboard.Window.Closed += delegate { probe.Close(); };
                }
                if(smoke) dashboard.Window.Loaded += async delegate {
                    var report=new List<string>();
                    try {
                        await dashboard.Refresh(); await Task.Delay(1200);
                        if(dashboard.Window.Height!=56 || dashboard.DetailsExpanded || dashboard.Find<Grid>("DetailsPanel").Visibility!=Visibility.Collapsed) throw new Exception("启动时未显示紧凑悬浮条");
                        dashboard.Capture(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","floating-bar.png"));
                        report.Add("PASS: compact floating bar starts at 560 x 56 with two account quota rings");
                        dashboard.Find<Button>("PrimaryRingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if(!dashboard.QuickDetails.IsOpen || dashboard.QuickDetails.ActiveSection!="primary") throw new Exception("5小时圆环未打开对应详情");
                        dashboard.Find<Button>("PrimaryRingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if(dashboard.QuickDetails.IsOpen) throw new Exception("5小时圆环重复点击未收起");
                        dashboard.Find<Button>("SecondaryRingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if(!dashboard.QuickDetails.IsOpen || dashboard.QuickDetails.ActiveSection!="secondary") throw new Exception("7天圆环未打开对应详情");
                        dashboard.Find<Button>("SecondaryRingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if(dashboard.QuickDetails.IsOpen) throw new Exception("7天圆环重复点击未收起");
                        report.Add("PASS: quota rings toggle their own period details open and closed");
                        dashboard.ToggleDetails();
                        if(dashboard.IsClickThrough!=Backdrop.IsClickThrough(dashboard.Window)) throw new Exception("启动后的鼠标穿透样式与偏好不一致");
                        report.Add("PASS: startup click-through style matches saved preference");
                        bool original=dashboard.Window.Topmost;
                        dashboard.TogglePin();
                        await Task.Delay(100);
                        bool off=dashboard.Window.Topmost==!original;
                        bool nativeOff=((Backdrop.GetWindowLong(new WindowInteropHelper(dashboard.Window).Handle,-20)&8)!=0)==!original;
                        dashboard.TogglePin();
                        await Task.Delay(100);
                        bool nativeOn=((Backdrop.GetWindowLong(new WindowInteropHelper(dashboard.Window).Handle,-20)&8)!=0)==original;
                        if(!off||!nativeOff||!nativeOn) throw new Exception("置顶切换失败：logical="+off+", native first="+nativeOff+", native restore="+nativeOn+", original="+original);
                        report.Add("PASS: native topmost toggle in both directions");
                        bool originalPreference=config.ClickThroughWhenPinned;
                        if(!dashboard.Window.Topmost) dashboard.TogglePin();
                        dashboard.Show(); // A tray restore must make the dashboard operable.
                        if(dashboard.IsClickThrough || Backdrop.IsClickThrough(dashboard.Window)) throw new Exception("托盘恢复后仍处于穿透状态");
                        dashboard.ToggleClickThrough();
                        IntPtr dashboardHandle=new WindowInteropHelper(dashboard.Window).Handle;
                        int clickStyles=Backdrop.GetWindowLong(dashboardHandle,-20);
                        if(!dashboard.IsClickThrough || (clickStyles&0x08000020)!=0x08000020 || (clickStyles&0x80008)!=0x80008) throw new Exception("穿透未启用或破坏了置顶/透明样式");
                        dashboard.TogglePin();
                        if(dashboard.IsClickThrough || (Backdrop.GetWindowLong(dashboardHandle,-20)&0x08000020)!=0) throw new Exception("取消置顶后穿透样式未移除");
                        dashboard.TogglePin();
                        if(!dashboard.IsClickThrough || !Backdrop.IsClickThrough(dashboard.Window)) throw new Exception("重新置顶后未恢复穿透");
                        dashboard.ToggleClickThrough();
                        if(dashboard.IsClickThrough || Backdrop.IsClickThrough(dashboard.Window) || (Backdrop.GetWindowLong(dashboardHandle,-20)&0x80008)!=0x80008) throw new Exception("关闭穿透后窗口样式不正确");
                        if(!original) dashboard.TogglePin();
                        if(config.ClickThroughWhenPinned!=originalPreference) dashboard.ToggleClickThrough();
                        dashboard.Show();
                        report.Add("PASS: native cross-application click-through styles, unpin restores input, re-pin restores click-through, tray restores interaction");
                        report.Add("Backdrop: "+dashboard.BackdropMode+" / DWM="+Backdrop.CurrentBackdrop(dashboard.Window));
                        if(dashboard.Window.FindName("TasksPanel")!=null) throw new Exception("任务区域未移除");
                        report.Add("PASS: quota-only view");
                        dashboard.Window.Width=580;dashboard.Window.Height=700;dashboard.Window.UpdateLayout();
                        if(dashboard.Find<ScrollViewer>("AccountsScroll").ActualHeight<50) throw new Exception("最小窗口账户区域不可用");
                        dashboard.Capture(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","compact.png"));
                        dashboard.Window.Width=620;dashboard.Window.Height=Math.Min(730,SystemParameters.WorkArea.Height-24);await Task.Delay(200);
                        dashboard.Capture(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","preview.png"));report.Add("PASS: compact and full window layouts");
                        if(dashboard.History.DayCount!=365) throw new Exception("热力图日期数不正确");
                        var dayTip=dashboard.History.ToolTipFor(DataReader.ChinaToday);
                        string dayHint=dayTip==null?"":Convert.ToString(dayTip.Content);
                        if(dayTip==null || !(dayHint.Contains("Token") || dayHint.Contains("无按日记录") || dayHint.Contains("数据暂不可用"))) throw new Exception("热力图提示缺少具体数值或明确的未知状态");
                        report.Add("PASS: 365-day heatmap with exact values or explicit unknown-data tooltips");
                        foreach(var theme in ThemePalette.All) {
                            dashboard.ApplyTheme(theme.Id);await Task.Delay(80);
                            dashboard.Capture(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","theme-"+theme.Id+".png"));
                        }
                        dashboard.ApplyTheme(config.Theme);
                        report.Add("PASS: four Morandi themes render with heatmap and controls");
                        var dialog=dashboard.CreateSettingsWindow();dialog.Show();dialog.UpdateLayout();
                        var settingsBitmap=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);settingsBitmap.Render(dialog);
                        var settingsPng=new PngBitmapEncoder();settingsPng.Frames.Add(BitmapFrame.Create(settingsBitmap));using(var imageFile=File.Create(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","settings-preview.png"))) settingsPng.Save(imageFile);
                        dialog.Close();report.Add("PASS: settings window with startup and theme choices");
                        if(dashboard.Window.FindName("BottomMinimizeButton")!=null) throw new Exception("右下角最小化按钮未移除");
                        dashboard.MinimizeToTray();
                        if(!dashboard.IsRunningInTray) throw new Exception("窗口关闭后未保持托盘运行");
                        dashboard.Show();
                        if(!dashboard.Window.IsVisible) throw new Exception("托盘恢复失败");
                        dashboard.Window.Close();
                        if(!dashboard.IsRunningInTray) throw new Exception("系统关闭请求未收起到托盘");
                        dashboard.Show();
                        report.Add("PASS: window close only hides to tray, restore keeps application running; Exit is tray-only");
                        if(!dashboard.Window.AllowsTransparency || dashboard.Window.Opacity!=1) throw new Exception("背景透明或文字不透明状态不正确");
                        if((Backdrop.GetWindowLong(new WindowInteropHelper(dashboard.Window).Handle,-20)&0x80000)==0) throw new Exception("原生透明窗口样式未启用");
                        report.Add("PASS: per-pixel transparent background with opaque text");
                        dashboard.ToggleDetails();
                        if(dashboard.Window.Height!=56 || dashboard.DetailsExpanded) throw new Exception("详情无法收起为悬浮条");
                        report.Add("PASS: expanded usage and heatmap collapse back to 56-pixel floating bar");
                        report.Add("PASS: refresh finished without UI exception");
                    } catch(Exception e) { report.Add("FAIL: "+e.ToString()); }
                    bool exited=false;
                    dashboard.Window.Closed += delegate { exited=true; };
                    dashboard.ExitFromTray();
                    report.Add(exited ? "PASS: tray Exit menu truly closes the application" : "FAIL: tray Exit menu did not close the application");
                    File.WriteAllLines(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","smoke-report.txt"),report);
                };
                app.Run(dashboard.Window);instance.ReleaseMutex();instance.Dispose();
            } catch(Exception e) { WriteError(e);Environment.ExitCode=1; }
        }
        static void WriteError(Exception e) { try { Directory.CreateDirectory(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data"));File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","error.log"),DateTime.Now+" "+e+Environment.NewLine); } catch { } }
    }
}
