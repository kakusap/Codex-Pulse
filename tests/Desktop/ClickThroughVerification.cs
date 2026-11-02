using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexPulse;
using Forms = System.Windows.Forms;

// Compile beside CodexPulse.exe so the CLR can resolve the referenced assembly.
// This checks real cross-process Windows hit testing without moving the mouse,
// injecting input, opening a database, or reading the user's saved preferences.
internal static class ClickThroughVerification {
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    sealed class Sample { public string Name; public NativePoint Point; public bool Pin; }

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError=true)] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError=true)] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);

    static readonly List<string> report = new List<string>();
    static Process probe;
    static IntPtr probeWindow;
    static string output;
    static bool cleanupFailed;

    [STAThread] static int Main(string[] args) {
        if(args.Length > 0 && args[0] == "--probe") return RunProbe(args);
        int result = 1;
        Dashboard dashboard = null;
        try {
            output = args.Length == 2 && args[0] == "--output" ? Path.GetFullPath(args[1]) :
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "clickthrough-verification-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            Settings.FilePath = Path.Combine(output, "settings.json");
            var area = SystemParameters.WorkArea;
            var settings = new Settings {
                PulseDb = Path.Combine(output, "fixture-pulse.db"),
                CodexHome = Path.Combine(output, "codex-home"),
                Topmost = true, ClickThroughWhenPinned = true,
                Left = area.Left + 40, Top = area.Top + 40, Width = 500, Height = 680
            };
            foreach(var screen in Forms.Screen.AllScreens) if(!screen.Primary) { settings.MonitorDevice=screen.DeviceName;break; }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            dashboard = new Dashboard(settings);
            app.MainWindow = dashboard.Window;
            dashboard.Window.Loaded += async delegate {
                try {
                    // Let Dashboard's asynchronous initial read and Show(false) finish.
                    await dashboard.Refresh();
                    await Task.Delay(1200);
                    AssertMode(dashboard, true, true, "startup");
                    Require(!dashboard.Window.ShowActivated, "Startup must not activate a click-through dashboard.");
                    await CheckFloatingBar(dashboard);
                    dashboard.ToggleDetails();
                    await Task.Delay(150);
                    PlaceAwayFromDesktopBar(dashboard);
                    await StartProbe(dashboard.Window);
                    await CheckDialogLayers(dashboard, settings);
                    await CheckOutsideClicks(dashboard);
                    await CheckBarControls(dashboard);
                    await CheckCostDetails(dashboard);
                    await CheckUsageFilter(dashboard);
                    var samples = Samples(dashboard);
                    await CheckHits(dashboard, samples, true, "startup");
                    await CheckPassiveHover(dashboard);

                    ClickPinIsland(dashboard);
                    Require(!dashboard.Window.Topmost, "The pin input island did not forward its click.");
                    await CheckHits(dashboard, samples, false, "unpin restores input");
                    dashboard.Find<Button>("PinButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(dashboard.Window.Topmost, "The original pin button did not re-pin the dashboard.");
                    await CheckHits(dashboard, samples, true, "re-pin restores click-through");

                    dashboard.Show();
                    await CheckHits(dashboard, samples, false, "tray Show restores interaction");
                    Require(dashboard.Window.Topmost, "Restoring interaction must keep the dashboard pinned.");
                    dashboard.ToggleClickThrough();
                    await CheckHits(dashboard, samples, true, "resume click-through from tray preference");

                    // Earlier Show/toggle actions can dock the window again. Keep
                    // the last visible coordinates for the hidden-window check.
                    samples = Samples(dashboard);
                    dashboard.MinimizeToTray();
                    await Task.Delay(150);
                    Require(!dashboard.Window.IsVisible, "MinimizeToTray must hide the dashboard.");
                    AssertMode(dashboard, true, true, "hidden dashboard retains mode");
                    AssertHits(dashboard, samples, true, "hidden dashboard exposes probe");
                    // Direct WPF Show intentionally preserves mode; Dashboard.Show is
                    // separately tested above as the explicit user recovery action.
                    dashboard.Window.Show();
                    await CheckHits(dashboard, samples, true, "native Hide/Show preserves click-through");

                    dashboard.Show();
                    await CheckHits(dashboard, samples, false, "recovery after Hide/Show");
                    dashboard.ToggleClickThrough();
                    await CheckHits(dashboard, samples, true, "resume after recovery");
                    dashboard.ToggleClickThrough();
                    await CheckHits(dashboard, samples, false, "disable click-through preference");
                    dashboard.TogglePin();
                    dashboard.TogglePin();
                    await CheckHits(dashboard, samples, false, "disabled preference survives re-pin");
                    Require(!Settings.Load().ClickThroughWhenPinned, "The test preference was not saved independently.");
                    report.Add("PASS: preference saved only to " + Settings.FilePath);
                    report.Add("PASS: all hits were checked using WindowFromPoint against an independent process; no input was injected.");
                    result = 0;
                } catch(Exception error) {
                    report.Add("FAIL: " + error);
                } finally {
                    StopProbe();
                    try { dashboard.ExitFromTray(); } catch(Exception error) { report.Add("FAIL: dashboard cleanup: " + error); result = 1; }
                    app.Shutdown(result);
                }
            };
            app.Run(dashboard.Window);
        } catch(Exception error) {
            report.Add("FAIL: " + error);
            result = 1;
        } finally {
            StopProbe();
            if(output != null) {
                try { File.WriteAllLines(Path.Combine(output, "report.txt"), report, new UTF8Encoding(false)); }
                catch(Exception error) { Console.Error.WriteLine(error); result = 1; }
            }
            if(cleanupFailed) result = 1;
            foreach(string line in report) Console.WriteLine(line);
        }
        return result;
    }

    static async Task CheckFloatingBar(Dashboard dashboard) {
        Require(dashboard.Window.Height==56 && !dashboard.DetailsExpanded,"The initial window must be a slim 56-pixel floating bar.");
        NativeRect docked;Require(GetWindowRect(new WindowInteropHelper(dashboard.Window).Handle,out docked),"Cannot read docked bounds.");
        var area=MonitorDock.Current(dashboard.Window).WorkingArea;
        Require(Math.Abs(docked.Top-area.Top)<=2 && Math.Abs(docked.Left-(area.Left+(area.Width-docked.Right+docked.Left)/2))<=2,"The bar must start docked at its screen's top center.");
        var latestField=typeof(Dashboard).GetField("latest",BindingFlags.Instance|BindingFlags.NonPublic);
        var original=(Snapshot)latestField.GetValue(dashboard);
        var snapshot=new Snapshot();
        snapshot.Accounts.Add(new AccountUsage { Id="a1",Name="主账户",Status="active",PrimaryUsed=22,SecondaryUsed=8,PrimaryMinutes=300,SecondaryMinutes=10080,PrimaryReset=DataReader.Now+8290,SecondaryReset=DataReader.Now+367200,Captured=DataReader.Now });
        snapshot.Accounts.Add(new AccountUsage { Id="a2",Name="备用账户",Status="active",PrimaryUsed=90,SecondaryUsed=35,PrimaryMinutes=300,SecondaryMinutes=10080,Captured=DataReader.Now });
        snapshot.Accounts.Add(new AccountUsage { Id="missing",Name="无快照" });
        snapshot.Accounts.Add(new AccountUsage { Id="swapped",Name="反向窗口",PrimaryUsed=8,SecondaryUsed=22,PrimaryMinutes=10080,SecondaryMinutes=300,PrimaryReset=DataReader.Now-1,SecondaryReset=DataReader.Now-1,Captured=DataReader.Now-900 });
        latestField.SetValue(dashboard,snapshot);dashboard.Render();
        Require(Math.Abs(dashboard.PrimaryRing.Remaining.Value-166.0/3)<0.001 && dashboard.SecondaryRing.Remaining==83,"Rings must average the remaining quota of ALL accounts with matching snapshots.");
        Require(dashboard.Find<TextBlock>("PrimaryResetText").Text.Contains("3/4") && dashboard.Find<TextBlock>("PrimaryResetText").Text.Contains("待更新"),"Coverage and expired snapshot status must be visible.");
        dashboard.Window.UpdateLayout();dashboard.Capture(Path.Combine(output,"floating-bar-sample.png"));
        string[] triggers={"AccountButton","PrimaryRingButton","SecondaryRingButton"};
        string[] sections={"accounts","primary","secondary"};
        for(int index=0;index<triggers.Length;index++) {
            var trigger=dashboard.Find<Button>(triggers[index]);trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(dashboard.QuickDetails.IsOpen && dashboard.QuickDetails.ActiveSection==sections[index],"Trigger opened the wrong quick-detail section.");
            dashboard.Render();Require(dashboard.QuickDetails.ActiveSection==sections[index],"A live refresh changed the open section.");
            var popup=dashboard.QuickDetails.PopupWindow;popup.UpdateLayout();
            var picture=new RenderTargetBitmap((int)Math.Ceiling(popup.ActualWidth),(int)Math.Ceiling(popup.ActualHeight),96,96,PixelFormats.Pbgra32);picture.Render(popup);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(picture));using(var file=File.Create(Path.Combine(output,"quick-"+sections[index]+".png"))) encoder.Save(file);
            trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(!dashboard.QuickDetails.IsOpen,"Second click did not collapse the same section.");
        }
        for(int index=0;index<triggers.Length;index++) { dashboard.Find<Button>(triggers[index]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(dashboard.QuickDetails.ActiveSection==sections[index],"Switching triggers did not replace the open section."); }
        dashboard.Find<Button>(triggers[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(!dashboard.QuickDetails.IsOpen && !dashboard.DetailsExpanded,"Quick details should close without expanding the full dashboard.");
        report.Add("PASS: all three triggers toggle their corresponding section, switch sections, and retain state across data refresh");
        Require(QuotaSummary.For(new[] { snapshot.Accounts[2] },300).Remaining==null,"Missing snapshots must not appear as full quota.");
        Require(QuotaSummary.For(new[] { snapshot.Accounts[3] },300).Remaining==78,"Reversed windows must match their duration.");
        var mismatched=new AccountUsage { PrimaryUsed=1,PrimaryMinutes=600,SecondaryUsed=2,SecondaryMinutes=20160 };
        Require(QuotaSummary.For(new[] { mismatched },300).Remaining==null,"Different quota periods must not be mislabeled as five hours.");
        var ring=new QuotaRing();ring.Update(-10,"#D5AFBA");Require(ring.Remaining==100,"Full ring clamp failed.");ring.Update(101,"#D5AFBA");Require(ring.Remaining==0,"Empty ring clamp failed.");ring.Update(double.NaN,"#D5AFBA");Require(!ring.Remaining.HasValue,"Invalid percentage must be unavailable.");
        latestField.SetValue(dashboard,original);dashboard.Render();await Task.Delay(50);
        report.Add("PASS: slim 56px top-centered bar, ALL-account averages, snapshot coverage, missing/expired snapshots and reversed windows");
    }

    static async Task StartProbe(Window window) {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        NativeRect rect;
        Require(GetWindowRect(hwnd, out rect), "Could not locate the native dashboard bounds.");
        string ready = Path.Combine(output, "probe-ready-" + Guid.NewGuid().ToString("N") + ".txt");
        var start = new ProcessStartInfo {
            FileName = Assembly.GetExecutingAssembly().Location,
            UseShellExecute = false, CreateNoWindow = true,
            Arguments = "--probe " + Quote(ready) + " " + Process.GetCurrentProcess().Id + " " +
                (rect.Left - 20) + " " + (rect.Top - 20) + " " + (rect.Right - rect.Left + 40) + " " + (rect.Bottom - rect.Top + 40)
        };
        probe = Process.Start(start);
        for(int attempt = 0; attempt < 80; attempt++) {
            Require(!probe.HasExited, "The probe exited before becoming ready.");
            if(File.Exists(ready)) {
                long handle;
                if(long.TryParse(File.ReadAllText(ready), NumberStyles.Integer, CultureInfo.InvariantCulture, out handle) && handle != 0) {
                    // The probe is a regular window, immediately below our topmost
                    // dashboard. Move it without injecting or synthesizing input.
                    probeWindow=new IntPtr(handle);
                    Require(SetWindowPos(new IntPtr(handle), IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010), "Could not raise the probe below the dashboard.");
                    await Task.Delay(150);
                    return;
                }
            }
            await Task.Delay(100);
        }
        throw new Exception("Timed out waiting for the independent probe window.");
    }

    static List<Sample> Samples(Dashboard dashboard) {
        dashboard.Window.UpdateLayout();
        var pin = dashboard.Find<System.Windows.Controls.Button>("PinButton");
        var used = dashboard.Find<System.Windows.Controls.TextBlock>("UsedText");
        var heatmapCell = (FrameworkElement)dashboard.History.ToolTipFor(DataReader.ChinaToday).PlacementTarget;
        var pinSample = At("painted pin button", pin.PointToScreen(new Point(pin.ActualWidth / 2, pin.ActualHeight / 2)));
        pinSample.Pin = true;
        return new List<Sample> {
            pinSample,
            At("painted dashboard content", used.PointToScreen(new Point(used.ActualWidth / 2, used.ActualHeight / 2))),
            At("heatmap cell", heatmapCell.PointToScreen(new Point(heatmapCell.ActualWidth / 2, heatmapCell.ActualHeight / 2))),
            At("resize edge", dashboard.Window.PointToScreen(new Point(dashboard.Window.ActualWidth - 2, dashboard.Window.ActualHeight / 2)))
        };
    }

    static void ClickPinIsland(Dashboard dashboard) {
        Sample pin = Samples(dashboard).Find(sample => sample.Pin);
        IntPtr root = GetAncestor(WindowFromPoint(pin.Point), 2);
        var source = HwndSource.FromHwnd(root);
        var island = source == null ? null : source.RootVisual as Window;
        var button = island == null ? null : island.Content as Button;
        Require(button != null && island != dashboard.Window, "The clickable pin island was not found.");
        // WindowFromPoint proves native routing; the routed event exercises the
        // island's forwarding handler without injecting mouse input globally.
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        report.Add("PASS: pin input island forwards its click while the dashboard is transparent");
    }

    static async Task CheckBarControls(Dashboard dashboard) {
        foreach(string name in new[] { "AccountButton","MenuButton","DragHandle","PrimaryRingButton","SecondaryRingButton" }) {
            var target=dashboard.Find<Button>(name);
            var point=At(name,target.PointToScreen(new Point(target.ActualWidth/2,target.ActualHeight/2))).Point;
            IntPtr root=GetAncestor(WindowFromPoint(point),2);
            uint process;GetWindowThreadProcessId(root,out process);
            Require(process==(uint)Process.GetCurrentProcess().Id && root!=new WindowInteropHelper(dashboard.Window).Handle,
                name+" must remain available through its native input island.");
            if(name=="DragHandle") continue;
            var island=(Window)HwndSource.FromHwnd(root).RootVisual;
            bool wasExpanded=dashboard.DetailsExpanded;
            ((Button)island.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            if(name=="AccountButton" || name=="PrimaryRingButton" || name=="SecondaryRingButton") {
                string expected=name=="AccountButton"?"accounts":name=="PrimaryRingButton"?"primary":"secondary";
                Require(dashboard.QuickDetails.IsOpen && dashboard.QuickDetails.ActiveSection==expected,"Native input trigger did not open its own section.");
                // The panel must leave the trigger reachable for a real second click.
                Require(GetAncestor(WindowFromPoint(point),2)==root,"The open panel covers or captures its own trigger.");
                ((Button)island.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!dashboard.QuickDetails.IsOpen,"Second native trigger click did not close the panel during click-through.");continue;
            }
            Require(dashboard.DetailsExpanded!=wasExpanded && target.ContextMenu==null,"Ellipsis must toggle usage details directly without a menu during click-through.");
            target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(dashboard.DetailsExpanded==wasExpanded,"Second ellipsis click must restore the original expanded state.");
        }
        report.Add("PASS: account/period panels toggle during click-through; ellipsis directly expands and collapses usage details without a menu");
    }

    static async Task CheckDialogLayers(Dashboard dashboard, Settings settings) {
        dashboard.ToggleClickThrough(); // Allow an active pinned bar as implicit modal owner.
        foreach(var dialog in new[] {
            dashboard.CreateSettingsWindow(),
            new IndependentAccountWindow(dashboard.Window,settings,proposed=>{}).Window,
            PriceTableWindow.Create(dashboard.Window,ThemePalette.Get(settings.Theme))
        }) {
            Exception failure=null;
            dialog.Loaded+=delegate {
                dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate {
                    try {
                        Require(!dialog.Topmost,"Dialog inherits logical topmost: "+dialog.Title);
                        Require((Backdrop.GetWindowLong(new WindowInteropHelper(dialog).Handle,-20)&8)==0,"Dialog inherits native topmost: "+dialog.Title);
                        Require(dialog.Owner!=dashboard.Window,"Dialog is owned by the topmost status bar.");
                        Require(!IsWindowEnabled(new WindowInteropHelper(dashboard.Window).Handle),"Unowned modal dialog must still disable native dashboard input.");
                        Require(dashboard.Window.Topmost,"Opening a normal dialog must keep the status bar pinned.");
                    } catch(Exception error) { failure=error; }
                    finally { dialog.Close(); }
                }));
            };
            dashboard.Window.Activate(); // Exercise ShowDialog's implicit native owner.
            dialog.ShowDialog();
            if(failure!=null) throw failure;
            Require(dashboard.Window.IsEnabled,"Closing a modal dialog must restore dashboard input.");
        }
        var parent=dashboard.CreateSettingsWindow();
        parent.Show();
        var child=new IndependentAccountWindow(parent,settings,proposed=>{}).Window;
        Require(child.Owner==parent && !child.Topmost,"Nested account management must retain its normal settings owner.");
        child.Close();parent.Close();
        dashboard.ToggleClickThrough();
        await Task.Delay(80);
        report.Add("PASS: settings/account/price modal windows are native non-topmost; status bar stays pinned and modal input is restored");
    }

    // Feed the hook with real native hit-test coordinates, without injecting
    // input into the user's desktop. WindowFromPoint still runs cross-process.
    static void ObserveClick(Dashboard dashboard,NativePoint point,int message=0x0201) {
        var listener=typeof(Dashboard).GetField("outsideClicks",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(dashboard);
        Require(listener!=null,"Outside click hook was not installed.");
        var hook=(IntPtr)listener.GetType().GetField("hook",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(listener);
        Require(hook!=IntPtr.Zero,"Outside click hook has no native handle.");
        IntPtr data=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativePoint)));
        try {
            Marshal.StructureToPtr(point,data,false);
            listener.GetType().GetMethod("OnMouse",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(listener,new object[] {0,new IntPtr(message),data});
        } finally { Marshal.FreeHGlobal(data); }
    }
    static async Task CheckOutsideClicks(Dashboard dashboard) {
        PlaceAwayFromDesktopBar(dashboard);
        NativeRect bounds;Require(GetWindowRect(probeWindow,out bounds),"Cannot read independent probe bounds.");
        var outside=new NativePoint { X=bounds.Right-4,Y=bounds.Bottom-4 };
        uint target;GetWindowThreadProcessId(WindowFromPoint(outside),out target);
        Require(target==(uint)probe.Id,"Outside-click sample must hit an independent application.");
        foreach(bool through in new[] {true,false}) {
            if(dashboard.IsClickThrough!=through) dashboard.ToggleClickThrough();
            if(!dashboard.DetailsExpanded) dashboard.ToggleDetails();
            dashboard.Window.UpdateLayout();
            var button=dashboard.Find<Button>("PinButton");
            ObserveClick(dashboard,At("inside pin",button.PointToScreen(new Point(button.ActualWidth/2,button.ActualHeight/2))).Point);
            await Task.Delay(50);
            Require(dashboard.DetailsExpanded,"Clicking an app control must keep details open.");
            dashboard.Find<Button>("PrimaryRingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var popup=dashboard.QuickDetails.PopupWindow;popup.UpdateLayout();
            ObserveClick(dashboard,At("inside popup",popup.PointToScreen(new Point(20,20))).Point);
            await Task.Delay(50);
            Require(dashboard.QuickDetails.IsOpen && dashboard.DetailsExpanded,"Clicking a detail popup must keep app panels open.");
            ObserveClick(dashboard,outside,0x0200);await Task.Delay(50);
            Require(dashboard.DetailsExpanded,"Pointer movement must not dismiss details.");
            ObserveClick(dashboard,outside,through?0x0201:0x0204);
            await Task.Delay(80);
            Require(!dashboard.DetailsExpanded && !dashboard.QuickDetails.IsOpen && dashboard.Window.Height==56,"Outside click must collapse usage and quota panels to the status bar.");
        }
        dashboard.ToggleClickThrough();
        dashboard.ToggleDetails();PlaceAwayFromDesktopBar(dashboard);
        report.Add("PASS: outside left/right clicks dismiss details with and without click-through; app controls/popups and pointer movement preserve them");
    }

    static async Task CheckPassiveHover(Dashboard dashboard) {
        var liveTimer = (DispatcherTimer)typeof(Dashboard).GetField("hoverTimer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dashboard);
        bool running = liveTimer.IsEnabled;
        liveTimer.Stop(); // Supply deterministic screen points without moving the user's cursor.
        try {
            string day = DataReader.ChinaToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var snapshot = new Snapshot();
            snapshot.Keys.Add(new KeyUsage { Id = "hover-test", Name = "hover-test" });
            snapshot.Daily.Add(new DailyUsage { Day = day, KeyId = "hover-test", Used = 1234, Cost = 0.4321 });
            dashboard.History.Update(snapshot, "");
            dashboard.Window.UpdateLayout();
            await Task.Delay(50);
            var tip = dashboard.History.ToolTipFor(DataReader.ChinaToday);
            var cell = (Border)tip.PlacementTarget;
            Point point = cell.PointToScreen(new Point(cell.ActualWidth / 2, cell.ActualHeight / 2));
            dashboard.History.UpdatePassiveHover(point);
            await Task.Delay(110);
            dashboard.History.UpdatePassiveHover(point);
            await Task.Delay(160);
            var scale = (ScaleTransform)cell.RenderTransform;
            Require(scale.ScaleX > 1.6 && scale.ScaleY > 1.6 && cell.Effect != null && Panel.GetZIndex(cell) == 10,
                "Passive hover did not retain the heatmap scale/glow effect.");
            string content = Convert.ToString(tip.Content);
            Require(tip.IsOpen && content.Contains(day) && content.Contains("Token") && content.Contains("0.4321"),
                "Passive hover did not show the exact date, usage and cost tooltip.");
            var tipSource = PresentationSource.FromVisual(tip) as HwndSource;
            Require(tipSource != null && (Backdrop.GetWindowLong(tipSource.Handle, -20) & 0x08000020) == 0x08000020,
                "The passive tooltip must remain click-through and non-activating.");
            uint hitProcess;
            GetWindowThreadProcessId(WindowFromPoint(At("heatmap", point).Point), out hitProcess);
            Require(hitProcess == (uint)probe.Id, "Heatmap hover intercepted input intended for the underlying process.");
            report.Add("PASS: passive heatmap hover retains scale, glow and exact tooltip while native hits reach the underlying process");

            dashboard.History.UpdatePassiveHover(dashboard.Window.PointToScreen(new Point(15, 15)));
            await Task.Delay(220);
            Require(!tip.IsOpen && scale.ScaleX < 1.01 && cell.Effect == null && Panel.GetZIndex(cell) == 0,
                "Leaving the passive heatmap did not clear its tooltip and hover effect.");
            report.Add("PASS: leaving the heatmap clears passive tooltip and animation");
        } finally {
            dashboard.History.ClearHover();
            if(running && dashboard.IsClickThrough && dashboard.Window.IsVisible) liveTimer.Start();
        }
    }
    static async Task CheckCostDetails(Dashboard dashboard) {
        dashboard.Window.UpdateLayout();await Task.Delay(80);
        var target=dashboard.Find<Button>("CostDetailsButton");
        var point=At("local cost details",target.PointToScreen(new Point(target.ActualWidth/2,target.ActualHeight/2))).Point;
        IntPtr root=GetAncestor(WindowFromPoint(point),2);uint process;GetWindowThreadProcessId(root,out process);
        Require(process==(uint)Process.GetCurrentProcess().Id && root!=new WindowInteropHelper(dashboard.Window).Handle,"Local cost details must remain clickable during native click-through.");
        bool opened=false;
        var closeRequest=dashboard.Window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate {
            foreach(Window dialog in Application.Current.Windows) if(dialog.Title=="Codex Pulse · 本机参考费用") {opened=true;dialog.Close();break;}
        }));
        var island=(Window)HwndSource.FromHwnd(root).RootVisual;((Button)island.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(closeRequest.Status==DispatcherOperationStatus.Completed && opened && dashboard.Window.IsEnabled,"Native local-cost control must open its dialog and restore owner input on close.");
        report.Add("PASS: local cost details open through the native input island and restore input after closing");
    }
    static async Task CheckUsageFilter(Dashboard dashboard) {
        dashboard.Window.UpdateLayout();await Task.Delay(70);
        Require(dashboard.Window.FindName("UsageSourcePanel")==null && dashboard.Window.FindName("UsageSourceName")==null,"Redundant official source header must be removed.");
        var target=dashboard.Find<Button>("KeyButton");var point=At("usage filter",target.PointToScreen(new Point(target.ActualWidth/2,target.ActualHeight/2))).Point;
        IntPtr root=GetAncestor(WindowFromPoint(point),2);var source=HwndSource.FromHwnd(root);
        Require(source!=null && source.RootVisual is Window && root!=new WindowInteropHelper(dashboard.Window).Handle,"Usage filter lacks its native click-through input region.");
        ((Button)((Window)source.RootVisual).Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(target.ContextMenu!=null && target.ContextMenu.IsOpen,"Account filter must remain usable in the compact summary card.");target.ContextMenu.IsOpen=false;
        dashboard.TogglePin();await Task.Delay(70);dashboard.Window.UpdateLayout();
        var ordinary=dashboard.Find<Button>("KeyButton");var normal=At("ordinary filter",ordinary.PointToScreen(new Point(ordinary.ActualWidth/2,ordinary.ActualHeight/2))).Point;
        Require(GetAncestor(WindowFromPoint(normal),2)==new WindowInteropHelper(dashboard.Window).Handle,"Unpinning must restore ordinary filter input.");
        dashboard.TogglePin();await Task.Delay(70);
        report.Add("PASS: compact official usage card removes source controls and keeps account filtering available during click-through");
    }

    static Sample At(string name, Point point) {
        return new Sample { Name = name, Point = new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) } };
    }

    static async Task CheckHits(Dashboard dashboard, List<Sample> samples, bool through, string stage) {
        await Task.Delay(150);
        PlaceAwayFromDesktopBar(dashboard);
        AssertMode(dashboard, through, dashboard.Window.Topmost, stage);
        // Unpinning may change the regular-window z order. Keep the test's two
        // windows adjacent without changing their already-asserted topmost mode.
        Require(SetWindowPos(new WindowInteropHelper(dashboard.Window).Handle, dashboard.Window.Topmost ? new IntPtr(-1) : IntPtr.Zero,
            0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010), "Could not arrange dashboard for " + stage);
        await Task.Delay(100);
        AssertHits(dashboard, samples, through, stage);
    }

    static void PlaceAwayFromDesktopBar(Dashboard dashboard) {
        var area=MonitorDock.Current(dashboard.Window).WorkingArea;
        Require(SetWindowPos(new WindowInteropHelper(dashboard.Window).Handle,IntPtr.Zero,area.Left+24,area.Top+80,0,0,0x0001|0x0004|0x0010),"Cannot isolate test window from the running top-docked bar.");
    }

    static void AssertMode(Dashboard dashboard, bool through, bool pinned, string stage) {
        int style = Backdrop.GetWindowLong(new WindowInteropHelper(dashboard.Window).Handle, -20);
        Require(dashboard.IsClickThrough == through, stage + ": logical click-through differs.");
        Require((style & 0x08000020) == (through ? 0x08000020 : 0), stage + ": native click-through/no-activate bits differ.");
        Require((style & 0x80000) != 0, stage + ": layered transparency was removed.");
        Require(((style & 8) != 0) == pinned, stage + ": native topmost differs.");
    }

    static void AssertHits(Dashboard dashboard, List<Sample> samples, bool through, string stage) {
        if(dashboard.Window.IsVisible) samples = Samples(dashboard);
        IntPtr dashboardHandle = new WindowInteropHelper(dashboard.Window).Handle;
        foreach(var sample in samples) {
            IntPtr hit = WindowFromPoint(sample.Point);
            uint process;
            GetWindowThreadProcessId(hit, out process);
            IntPtr root = GetAncestor(hit, 2); // GA_ROOT; the probe button is a child HWND.
            bool pinIsland = through && dashboard.Window.IsVisible && sample.Pin;
            bool correct = pinIsland ? process == (uint)Process.GetCurrentProcess().Id && root != dashboardHandle :
                through ? process == (uint)probe.Id : root == dashboardHandle;
            if(pinIsland) Require((Backdrop.GetWindowLong(root, -20) & 0x08000020) == 0x08000000,
                stage + ": the pin input island must accept input without activation.");
            Require(correct, stage + ": " + sample.Name + " hit HWND=" + hit.ToInt64() + ", PID=" + process +
                ", expected " + (pinIsland ? "the pin input island" : through ? "probe PID=" + probe.Id : "dashboard HWND=" + dashboardHandle.ToInt64()) +
                " at " + sample.Point.X + "," + sample.Point.Y);
        }
        report.Add("PASS: " + stage + " (pin island, content, heatmap, resize edge)");
    }

    static int RunProbe(string[] args) {
        if(args.Length != 7) return 2;
        int parentId = int.Parse(args[2], CultureInfo.InvariantCulture);
        using(var form = new Forms.Form())
        using(var watch = new Forms.Timer()) {
            form.Text = "Codex Pulse independent hit-test probe";
            form.StartPosition = Forms.FormStartPosition.Manual;
            form.FormBorderStyle = Forms.FormBorderStyle.None;
            form.ShowInTaskbar = false;
            form.SetBounds(int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6]));
            form.Controls.Add(new Forms.Button { Dock = Forms.DockStyle.Fill, Text = "Independent process target\r\nNo mouse or keyboard input is injected." });
            form.Shown += delegate { File.WriteAllText(args[1], form.Handle.ToInt64().ToString(CultureInfo.InvariantCulture)); };
            watch.Interval = 1000;
            watch.Tick += delegate {
                try { using(var parent = Process.GetProcessById(parentId)) { if(parent.HasExited) form.Close(); } }
                catch(ArgumentException) { form.Close(); }
            };
            watch.Start();
            Forms.Application.Run(form);
        }
        return 0;
    }

    static void StopProbe() {
        if(probe == null) return;
        try { if(!probe.HasExited) { probe.CloseMainWindow(); if(!probe.WaitForExit(1500)) { probe.Kill(); probe.WaitForExit(1500); } } }
        catch(Exception error) { report.Add("FAIL: probe cleanup: " + error.Message); cleanupFailed = true; }
        finally { probe.Dispose(); probe = null; }
    }

    static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    static void Require(bool condition, string message) { if(!condition) throw new Exception(message); }
}
