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
    public class Dashboard {
        public Window Window;
        Settings config;
        ThemePalette palette=ThemePalette.Get("green");
        Snapshot latest;
        DispatcherTimer timer;
        DispatcherTimer hoverTimer;
        DispatcherTimer updateTimer;
        readonly AppUpdater updater=new AppUpdater();
        readonly CancellationTokenSource updateCancellation=new CancellationTokenSource();
        UpdateRelease availableUpdate;
        UpdateWindow updateWindow;
        System.Windows.Forms.ToolStripMenuItem trayUpdate;
        bool checkingUpdates,updateInstalling;
        PinInteraction pinInteraction;
        OutsideClickDismissal outsideClicks;
        PinInteraction accountInteraction,menuInteraction,dragInteraction;
        readonly List<PinInteraction> detailInteractions=new List<PinInteraction>();
        System.Windows.Forms.NotifyIcon tray;
        System.Windows.Forms.ToolStripItem trayExit;
        System.Windows.Forms.ToolStripMenuItem trayClickThrough;
        bool reading, closing, exitRequested, loaded, hiddenToTray, interactionPaused,dragging,docking;
        public string BackdropMode;
        public readonly TokenHeatmap History=new TokenHeatmap();
        public readonly QuotaRing PrimaryRing=new QuotaRing(),SecondaryRing=new QuotaRing();
        public bool DetailsExpanded { get; private set; }
        public QuotaDetailsPopup QuickDetails { get; private set; }
        public AccountSwitchController AccountSwitch { get; private set; }
        bool managingAccounts;
        public Dashboard(Settings settings) {
            config=settings;
            using (var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) Window=(Window)XamlReader.Load(s);
            Window.Width=Math.Max(Window.MinWidth,Math.Min(config.Width,SystemParameters.WorkArea.Width-24));
            Window.Height=56;Window.MaxHeight=56;
            config.MonitorDevice=MonitorDock.Resolve(config.MonitorDevice).DeviceName;
            Window.Left=config.Left;Window.Top=config.Top;
            Window.Topmost=config.Topmost;
            Window.ShowActivated=!IsClickThrough;
            Find<Grid>("HeatmapHost").Children.Add(History);
            Find<Grid>("PrimaryRingHost").Children.Add(PrimaryRing);
            Find<Grid>("SecondaryRingHost").Children.Add(SecondaryRing);
            ApplyTheme(config.Theme);
            QuickDetails=new QuotaDetailsPopup(Window,()=>config.MonitorDevice);
            AccountSwitch=new AccountSwitchController(()=>new IndependentAccounts(config),()=>new CodexDesktop(config.CodexExe));
            QuickDetails.AccountSwitch=AccountSwitch;
            QuickDetails.ManageAccounts=ShowIndependentAccounts;
            QuickDetails.RefreshSnapshot=()=>Refresh(true);
            AccountSwitch.Changed+=delegate { if(!closing) QuickDetails.Refresh(latest,palette); };
            try { Window.Icon=BitmapFrame.Create(new Uri(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pulse.ico"))); } catch { }
            Window.SourceInitialized += delegate {
                outsideClicks=new OutsideClickDismissal(Window,()=>!closing && (DetailsExpanded || QuickDetails.IsOpen),delegate {
                    QuickDetails.Close();
                    if(DetailsExpanded) ToggleDetails();
                });
                BackdropMode=Backdrop.Apply(Window,config.BackgroundOpacity);DockTopCenter();UpdateInteractionMode();
                var source=HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                if(source!=null) source.AddHook(delegate(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
                    if(message==0x02E0) Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(DockTopCenter));
                    return IntPtr.Zero;
                });
            };
            Find<Grid>("DragArea").MouseLeftButtonDown += delegate(object sender,MouseButtonEventArgs e) { if(e.OriginalSource is Button || IsInsideButton(e.OriginalSource as DependencyObject)) return; if(e.ClickCount==1) DragBar(); };
            Window.StateChanged += delegate { if(Window.WindowState==WindowState.Minimized) MinimizeToTray(); };
            Find<Button>("PinButton").Click += delegate { TogglePin(); };
            pinInteraction=new PinInteraction(Window,Find<Button>("PinButton"),TogglePin,()=>IsClickThrough && !closing);
            Find<Button>("AccountButton").Click += delegate { ShowAccounts(); };
            Find<Button>("MenuButton").Click += delegate { ToggleDetails(); };
            Find<Button>("CollapseButton").Click += delegate { ToggleDetails(); };
            Find<Button>("DragHandle").PreviewMouseLeftButtonDown += delegate(object sender,MouseButtonEventArgs e) { e.Handled=true;DragBar(); };
            accountInteraction=new PinInteraction(Window,Find<Button>("AccountButton"),ShowAccounts,()=>IsClickThrough && !closing);
            menuInteraction=new PinInteraction(Window,Find<Button>("MenuButton"),ToggleDetails,()=>IsClickThrough && !closing);
            dragInteraction=new PinInteraction(Window,Find<Button>("DragHandle"),delegate {},()=>IsClickThrough && !closing,DragBar);
            Find<Button>("RefreshButton").Click += async delegate { await Refresh(true); };
            Find<Button>("SettingsButton").Click += delegate { ShowSettings(); };
            Find<Button>("KeyButton").Click += delegate { ShowKeys(); };
            Find<Button>("CostDetailsButton").Click += delegate {var dialog=CreateCostWindow();if(dialog!=null) dialog.ShowDialog();};
            foreach(string name in new[] { "KeyButton","CollapseButton","RefreshButton","SettingsButton","CostDetailsButton" }) {
                var button=Find<Button>(name);
                detailInteractions.Add(new PinInteraction(Window,button,()=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),()=>IsClickThrough && DetailsExpanded && !closing));
            }
            foreach(string name in new[] { "PrimaryRingButton","SecondaryRingButton" }) {
                var button=Find<Button>(name);
                string section=name=="PrimaryRingButton"?"primary":"secondary";
                button.Click+=delegate { QuickDetails.Toggle(section,button,latest,palette); };
                detailInteractions.Add(new PinInteraction(Window,button,()=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),()=>IsClickThrough && !closing));
            }
            Window.KeyDown += async delegate(object sender,KeyEventArgs e) {
                if(e.Key==Key.F5) { await Refresh(true); e.Handled=true; }
                if(e.Key==Key.T && Keyboard.Modifiers==ModifierKeys.Control) { TogglePin(); e.Handled=true; }
            };
            Window.Closing += delegate(object sender,CancelEventArgs e) {
                if(!exitRequested) { e.Cancel=true; MinimizeToTray(); return; }
                closing=true;updateCancellation.Cancel();if(updateTimer!=null) updateTimer.Stop();if(outsideClicks!=null) outsideClicks.Dispose();QuickDetails.Dispose();SystemParameters.StaticPropertyChanged-=OnDesktopChanged;timer.Stop();hoverTimer.Stop();History.SetPassiveHover(false);pinInteraction.Dispose();accountInteraction.Dispose();menuInteraction.Dispose();dragInteraction.Dispose();foreach(var control in detailInteractions) control.Dispose();tray.Visible=false;tray.Dispose();Save();
            };
            SetupTray(); UpdatePin();
            timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(5) };
            timer.Tick += async delegate { await Refresh(); };
            hoverTimer=new DispatcherTimer(DispatcherPriority.Input) { Interval=TimeSpan.FromMilliseconds(33) };
            hoverTimer.Tick += delegate {
                Point cursor;if(Backdrop.TryGetCursorPosition(out cursor)) History.UpdatePassiveHover(cursor);else History.ClearHover();
            };
            Window.IsVisibleChanged += delegate { UpdatePassiveInteraction(); };
            Window.IsEnabledChanged += delegate { UpdatePassiveInteraction(); };
            Window.SizeChanged += delegate { DockTopCenter(); };
            SystemParameters.StaticPropertyChanged+=OnDesktopChanged;
            Window.Loaded += async delegate { if(loaded) return; loaded=true; await Refresh(); if(closing) return; timer.Start();DockTopCenter();if(!hiddenToTray) Show(false);Save(); };
        }
        static bool IsInsideButton(DependencyObject element) {
            while(element!=null) { if(element is Button) return true; element=VisualTreeHelper.GetParent(element); } return false;
        }
        public T Find<T>(string name) where T:class { return Window.FindName(name) as T; }
        static SolidColorBrush Brush(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
        static TextBlock Text(string text,double size,string color) { return new TextBlock { Text=text,FontSize=size,Foreground=Brush(color),VerticalAlignment=VerticalAlignment.Center }; }
        void SetupTray() {
            tray=new System.Windows.Forms.NotifyIcon { Text="Codex Pulse · 额度看板", Visible=true };
            try { tray.Icon=new System.Drawing.Icon(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"pulse.ico")); }
            catch { tray.Icon=System.Drawing.SystemIcons.Application; }
            var menu=new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示看板（恢复操作）",null,delegate { Show(); });
            menu.Items.Add("展开 / 收起用量详情",null,delegate { Show();ToggleDetails(); });
            menu.Items.Add("切换置顶",null,delegate { TogglePin(); });
            trayUpdate=(System.Windows.Forms.ToolStripMenuItem)menu.Items.Add("检查更新…",null,delegate { ShowUpdates(); });
            tray.BalloonTipClicked+=delegate {if(availableUpdate!=null) ShowUpdates();};
            trayClickThrough=(System.Windows.Forms.ToolStripMenuItem)menu.Items.Add("置顶时鼠标穿透",null,delegate { ToggleClickThrough(); });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            trayExit=menu.Items.Add("退出",null,delegate { if(AccountSwitch.Busy || managingAccounts) { Show();return; } exitRequested=true; Window.Close(); });
            tray.ContextMenuStrip=menu; tray.DoubleClick += delegate { Show(); };
        }
        public void MinimizeToTray() { hiddenToTray=true; Window.Hide(); }
        public bool IsRunningInTray { get { return !Window.IsVisible && tray.Visible && !closing && timer.IsEnabled; } }
        public void ExitFromTray() { trayExit.PerformClick(); }
        public void StartUpdateChecks() {
            if(updateTimer!=null) return;
            updateTimer=new DispatcherTimer {Interval=TimeSpan.FromHours(24)};
            updateTimer.Tick+=async delegate {await CheckUpdatesAutomatically();};
            Window.Loaded+=async delegate {updateTimer.Start();await CheckUpdatesAutomatically();};
        }
        void UpdateReady(UpdateRelease release) {
            availableUpdate=release.Version>AppUpdater.CurrentVersion?release:null;
            if(trayUpdate!=null) trayUpdate.Text=availableUpdate==null?"检查更新…":"更新到 v"+availableUpdate.VersionText+"…";
        }
        async Task CheckUpdatesAutomatically() {
            if(checkingUpdates || closing || !config.AutoCheckUpdates) return;
            checkingUpdates=true;
            try {
                var release=await updater.CheckAsync(updateCancellation.Token);
                if(closing) return;
                bool changed=release.Version>AppUpdater.CurrentVersion && (availableUpdate==null || availableUpdate.Version!=release.Version);
                UpdateReady(release);
                if(changed) tray.ShowBalloonTip(5000,"Codex Pulse 有新版本","v"+release.VersionText+" 已发布，点击查看并更新。",System.Windows.Forms.ToolTipIcon.Info);
            } catch { /* Automatic checks stay quiet when offline; manual checks explain failures. */ }
            finally {checkingUpdates=false;}
        }
        string UpdateBlocked() {
            if(closing || updateInstalling) return "正在退出或更新，请稍候。";
            if(reading || managingAccounts || AccountSwitch.Busy || AccountSwitch.Loading) return "正在刷新或管理账号，请完成后再安装更新。";
            return null;
        }
        void InstallUpdate(PreparedUpdate prepared) {
            string reason=UpdateBlocked();if(reason!=null) throw new IOException(reason);
            Save();UpdateInstaller.Launch(prepared);
            updateInstalling=true;exitRequested=true;Window.Close();
        }
        public void ShowUpdates() {
            if(closing || updateInstalling) return;
            if(updateWindow!=null) {updateWindow.Window.Activate();return;}
            var dialog=new UpdateWindow(dialogOwner(),palette,updater,availableUpdate,UpdateReady,UpdateBlocked,InstallUpdate);
            updateWindow=dialog;
            try {dialog.Window.ShowDialog();} finally {updateWindow=null;}
        }
        public bool IsClickThrough { get { return Window.Topmost && config.ClickThroughWhenPinned && !interactionPaused; } }
        public void Show() { Show(true); }
        void Show(bool restoreInteraction) {
            if(restoreInteraction) interactionPaused=true;
            UpdateInteractionMode();
            hiddenToTray=false;Window.ShowActivated=!IsClickThrough;Window.Show();Window.WindowState=WindowState.Normal;
            Backdrop.SetPinned(Window);
            if(!IsClickThrough) { Backdrop.Reveal(Window);Window.Activate(); }
        }
        public void ToggleClickThrough() {
            config.ClickThroughWhenPinned=interactionPaused || !config.ClickThroughWhenPinned;
            interactionPaused=false;UpdateInteractionMode();Save();
        }
        void UpdateInteractionMode() {
            Backdrop.SetClickThrough(Window,IsClickThrough);UpdatePin();
            UpdatePassiveInteraction();
            if(trayClickThrough!=null) {
                trayClickThrough.Checked=config.ClickThroughWhenPinned && !interactionPaused;
                trayClickThrough.Text=interactionPaused && config.ClickThroughWhenPinned ? "恢复鼠标穿透（置顶时）" : "置顶时鼠标穿透";
                tray.Text=IsClickThrough ? "Codex Pulse · 鼠标穿透中（双击恢复操作）" : "Codex Pulse · 额度看板";
            }
        }
        void UpdatePassiveInteraction() {
            if(pinInteraction!=null) pinInteraction.Sync();
            if(accountInteraction!=null) accountInteraction.Sync();
            if(menuInteraction!=null) menuInteraction.Sync();
            if(dragInteraction!=null) dragInteraction.Sync();
            foreach(var control in detailInteractions) control.Sync();
            if(hoverTimer==null) return;
            bool active=IsClickThrough && DetailsExpanded && Window.IsVisible && Window.IsEnabled && !closing;
            History.SetPassiveHover(active);
            if(active) hoverTimer.Start();else hoverTimer.Stop();
        }
        void Save() {
            config.Left=Window.Left;config.Top=Window.Top;config.Width=Window.Width;config.Height=56;
            if(DetailsExpanded) config.ExpandedHeight=Window.Height;
            try { config.Save(); } catch(Exception e) { Find<TextBlock>("SyncText").Text="设置保存失败："+e.Message;Find<Button>("MenuButton").ToolTip="设置保存失败："+e.Message; }
        }
        void DragBar() {
            dragging=true;
            try { if(IsClickThrough) Backdrop.SetClickThrough(Window,false);Window.DragMove(); }
            finally { config.MonitorDevice=MonitorDock.Current(Window).DeviceName;dragging=false;DockTopCenter();UpdateInteractionMode();Save(); }
        }
        public void DockTopCenter() {
            if(dragging || docking || closing) return;
            docking=true;
            try { config.MonitorDevice=MonitorDock.Resolve(config.MonitorDevice).DeviceName;MonitorDock.TopCenter(Window,config.MonitorDevice); }
            finally { docking=false; }
        }
        public void MoveToMonitor(string device) { config.MonitorDevice=MonitorDock.Resolve(device).DeviceName;DockTopCenter();Save(); }
        void OnDesktopChanged(object sender,PropertyChangedEventArgs e) { if(!closing) Window.Dispatcher.BeginInvoke(new Action(DockTopCenter)); }
        public void ToggleDetails() {
            QuickDetails.Close();
            if(DetailsExpanded) config.ExpandedHeight=Window.Height;
            DetailsExpanded=!DetailsExpanded;
            Find<Button>("MenuButton").ToolTip=DetailsExpanded?"收起用量与热力图":"展开用量与热力图";
            if(DetailsExpanded) {
                Window.MaxHeight=Math.Max(700,MonitorDock.WorkHeight(Window,config.MonitorDevice)-12);Window.MinHeight=700;
                Find<Grid>("DetailsPanel").Visibility=Visibility.Visible;
                Window.Height=Math.Max(700,Math.Min(config.ExpandedHeight,Window.MaxHeight));
            } else {
                History.ClearHover();Find<Grid>("DetailsPanel").Visibility=Visibility.Collapsed;
                Window.MinHeight=56;Window.Height=56;Window.MaxHeight=56;
            }
            DockTopCenter();Window.UpdateLayout();UpdatePassiveInteraction();Save();
        }
        public void TogglePin() { Window.Topmost=!Window.Topmost; Backdrop.SetPinned(Window); config.Topmost=Window.Topmost; interactionPaused=false;UpdateInteractionMode();Save(); }
        void UpdatePin() {
            Find<TextBlock>("PinText").Text=IsClickThrough ? "穿透中" : Window.Topmost ? "已置顶" : "置顶";
            Find<Button>("PinButton").ToolTip=IsClickThrough ? "点击取消置顶和穿透 (Ctrl+T)；热力图悬浮提示仍可用" : "切换窗口置顶 (Ctrl+T)；置顶后按设置启用鼠标穿透";
            Find<Button>("PinButton").Foreground=Brush(Window.Topmost ? palette.Accent : ThemePalette.Muted);
            Find<Button>("PinButton").Background=ThemePalette.Alpha(palette.Accent,Window.Topmost?(byte)36:(byte)14);
        }
        public void ApplyTheme(string id) {
            palette=ThemePalette.Get(id);palette.Apply(Window);History.SetTheme(palette);
            if(System.Windows.Application.Current!=null) foreach(var dialog in System.Windows.Application.Current.Windows.OfType<Window>().Where(w=>w!=Window && DialogTheme.IsManaged(w)).ToArray()) palette.Apply(dialog);
            BackdropMode=Backdrop.SetTint(Window,config.BackgroundOpacity);UpdatePin();
            if(QuickDetails!=null) QuickDetails.Refresh(latest,palette);
            if(latest!=null) Render();
        }
        public async Task Refresh(bool force=false) {
            if(reading || closing || updateInstalling || managingAccounts || (AccountSwitch!=null && AccountSwitch.Busy)) return;
            reading=true; Find<Button>("RefreshButton").IsEnabled=false;QuickDetails.SetRefreshing(true);
            // Copy config so a settings dialog cannot change paths in the middle of a read.
            try { var input=Json.New().Deserialize<Settings>(Json.New().Serialize(config));latest=await Task.Run(()=>new IndependentAccounts(input).Refresh(force)); if(!closing) { Render();if(QuickDetails.IsOpen && QuickDetails.ActiveSection=="accounts") await AccountSwitch.Refresh(); } }
            catch(Exception e) { if(!closing) Find<TextBlock>("SyncText").Text="刷新失败："+e.Message; }
            finally { reading=false;QuickDetails.SetRefreshing(false); if(!closing) Find<Button>("RefreshButton").IsEnabled=true; }
        }
        public static string Compact(double n) {
            if(n>=1000000000) return (n/1000000000).ToString("0.##",CultureInfo.InvariantCulture)+"B";
            if(n>=1000000) return (n/1000000).ToString("0.##",CultureInfo.InvariantCulture)+"M";
            if(n>=1000) return (n/1000).ToString("0.##",CultureInfo.InvariantCulture)+"K";
            return n.ToString("N0");
        }
        public void Render() {
            if(latest==null) return;
            if(config.UsageSource!="official") { config.UsageSource="official";Save(); }
            var view=UsageStore.Selected(latest,config.UsageSource);
            string selected=view.Id=="history"?config.SelectedKey:config.SelectedUsageAccount;
            var keys=view.Groups.Where(x=>string.IsNullOrEmpty(selected)||x.Id==selected).ToList();
            if(keys.Count==0 && view.Groups.Count>0) { if(view.Id=="history") config.SelectedKey="";else config.SelectedUsageAccount="";selected="";keys=view.Groups.ToList();Save(); }
            bool good=latest.DataError==null;
            double used=keys.Where(k=>k.UsedKnown).Sum(x=>x.Used);
            Find<TextBlock>("UsedText").Text=good && keys.Any(k=>k.UsedKnown) ? Compact(used) : "—";
            Find<TextBlock>("UsedText").ToolTip=used.ToString("N0")+" Token · "+view.Name+"\n可统计 "+keys.Count(k=>k.UsedKnown)+" / "+keys.Count+" 组。部分账号可能只返回每日用量；不同来源不相加。";
            var localCosts=LocalCostSummary.FromSnapshot(latest,selected);
            Find<TextBlock>("CostText").Text=localCosts.AmountText;
            Find<TextBlock>("CostCoverage").Text=localCosts.CoverageText;
            Find<TextBlock>("CostText").ToolTip=localCosts.Description();
            Find<Button>("CostDetailsButton").ToolTip=localCosts.Description()+"\n点击查看模型明细和未计价原因。";
            Find<TextBlock>("UsageTotalTitle").Text=view.TotalLabel;
            Find<TextBlock>("UsageDateBasis").Text="近一年 · "+view.DateBasis;
            Find<TextBlock>("LimitTitle").Text=view.Id=="history"?"历史限额余量":"订阅额度";
            Find<TextBlock>("CostTitle").Text="本机参考费用";
            Find<Button>("KeyButton").Content=(string.IsNullOrEmpty(selected)?view.Id=="history"?"全部密钥":"全部账号":keys.FirstOrDefault()==null?"选择账号":keys[0].Name)+" ▾";
            bool allLimited=keys.Count>0 && keys.All(x=>x.Limit.HasValue);
            Find<TextBlock>("RemainingText").Text=view.Id!="history"?"见账号额度窗口":!good?"暂不可用":keys.Count==0?"暂无历史":allLimited?Compact(keys.Sum(x=>x.Remaining.Value)):"未记录限额";
            Find<TextBlock>("QuotaNote").Text=!good?latest.DataError:(!string.IsNullOrEmpty(view.Error)?view.Error+"\n":"")+view.Notice;
            Find<TextBlock>("QuotaNote").ToolTip=Find<TextBlock>("QuotaNote").Text;
            Find<TextBlock>("RemainingText").ToolTip="订阅剩余百分比不能换算成绝对剩余 Token。";
            Find<TextBlock>("QuotaNote").Foreground=Brush(good?"#BAC9DE":"#E9B78A");
            RenderAccounts();History.Update(UsageStore.Chart(view),selected);
            RenderRings();
            QuickDetails.Refresh(latest,palette);
            bool ok=good;
            Find<Ellipse>("SyncDot").Fill=Brush(ok?palette.Accent:"#EDB36F");
            Find<TextBlock>("SyncText").Text=ok ? "独立账号 · "+DateTime.Now.ToString("HH:mm:ss") : "额度数据源不可用 · "+DateTime.Now.ToString("HH:mm:ss");
            Find<TextBlock>("SyncText").ToolTip=ok ? "账号额度和官方用量按间隔联网更新；用量与热力图显示所选账号的官方数据。" : (latest.DataError??"");
        }
        public void SelectAccount(string id) {
            // Account details never change the all-account aggregate on the bar.
            RenderRings();
        }
        public Window CreateCostWindow() {
            if(latest==null) return null;
            return CostDetailsWindow.Create(Window,palette,LocalCostSummary.FromSnapshot(latest,config.SelectedUsageAccount),latest.Accounts);
        }
        void RenderRings() {
            var accounts=latest.DataError==null?latest.Accounts:new List<AccountUsage>();
            Find<TextBlock>("AccountName").Text="全部 "+accounts.Count+" 账号";
            Find<Button>("AccountButton").ToolTip="全部账号同周期剩余百分比的算术平均；缺少快照的账号不计入，非绝对 Token 总额度。点击查看明细、切换直连账号并重启 Codex。";
            RenderAggregate(PrimaryRing,"Primary",QuotaSummary.For(accounts,300),"5 小时");
            RenderAggregate(SecondaryRing,"Secondary",QuotaSummary.For(accounts,10080),"7 天");
        }
        void RenderAggregate(QuotaRing ring,string name,QuotaSummary summary,string label) {
            RenderRing(ring,name,summary.Remaining.HasValue?100-summary.Remaining.Value:(double?)null,name=="Primary"?300:10080,summary.NextReset,summary.OldestCaptured,label);
            string coverage=summary.Available+"/"+summary.Total+"账号";
            Find<TextBlock>(name+"ResetText").Text=coverage+" · "+(summary.Expired>0?"含待更新":summary.Available>0?"平均剩余":"暂无快照");
            Find<TextBlock>(name+"ResetText").Foreground=Brush(summary.Expired>0?"#D7AB7D":ThemePalette.Muted);
            string info=label+"全部账号平均剩余："+(summary.Remaining.HasValue?summary.Remaining.Value.ToString("0.#")+"%":"—")+"\n有效快照 "+summary.Available+" / "+summary.Total+" 账号；缺少快照或周期不同的不计入。\n各账号等权平均，不代表绝对 Token 总量。\n最早快照："+Age(summary.OldestCaptured)+(summary.Expired>0?"\n"+summary.Expired+" 个账号重置已过，仍使用旧快照，等待额度更新。":"")+(summary.NextReset>0?"\n最近重置："+DateTimeOffset.FromUnixTimeSeconds(summary.NextReset).LocalDateTime.ToString("MM-dd HH:mm"):"");
            Find<Grid>(name+"RingHost").ToolTip=info;Find<TextBlock>(name+"ResetText").ToolTip=info;
            Find<Button>(name+"RingButton").ToolTip=info+"\n点击展开/收起对应周期详情";
        }
        static string WindowLabel(double minutes,string fallback) {
            return minutes>=1440?(minutes/1440).ToString("0.#")+" 天":minutes>=60?(minutes/60).ToString("0.#")+" 小时":minutes>0?minutes.ToString("0")+" 分钟":fallback;
        }
        void RenderRing(QuotaRing ring,string name,double? used,double minutes,long reset,long captured,string fallback) {
            ring.Update(used,palette.Accent);
            string label=WindowLabel(minutes,fallback),countdown;
            long seconds=reset-DataReader.Now;
            if(!ring.Remaining.HasValue) countdown="暂无额度快照";
            else if(reset<=0) countdown="暂无重置时间";
            else if(seconds<=0) countdown="重置已过 · 待更新";
            else if(seconds>=86400) countdown=seconds/86400+"天"+(seconds%86400)/3600+"小时后重置";
            else if(seconds>=3600) countdown=seconds/3600+"小时"+(seconds%3600)/60+"分后重置";
            else countdown=Math.Max(1,seconds/60)+"分后重置";
            Find<TextBlock>(name+"Label").Text=label+"剩余";
            Find<TextBlock>(name+"ResetText").Text=countdown;
            Find<TextBlock>(name+"ResetText").Foreground=Brush(reset>0&&seconds<=0?"#D7AB7D":ThemePalette.Muted);
            string info=label+"剩余："+(ring.Remaining.HasValue?ring.Remaining.Value.ToString("0.#")+"%":"暂无快照")+"\n"+countdown+"\n快照："+Age(captured)+"\n最近一次成功查询的额度快照";
            Find<Grid>(name+"RingHost").ToolTip=info;Find<TextBlock>(name+"ResetText").ToolTip=info;
        }
        public ContextMenu CreateAccountMenu() {
            var menu=NewMenu();
            menu.Items.Add(new MenuItem { Header="全部账号汇总 · 等权平均",IsEnabled=false });
            if(latest==null || latest.Accounts.Count==0) menu.Items.Add(new MenuItem { Header="暂无可用账户",IsEnabled=false });
            else foreach(var account in latest.Accounts) {
                var shortQuota=QuotaSummary.For(new[] { account },300);var longQuota=QuotaSummary.For(new[] { account },10080);
                var item=new MenuItem { Header=(string.IsNullOrWhiteSpace(account.Name)?"未命名账户":account.Name)+" · 5h "+(shortQuota.Remaining.HasValue?shortQuota.Remaining.Value.ToString("0")+"%":"—")+" / 7d "+(longQuota.Remaining.HasValue?longQuota.Remaining.Value.ToString("0")+"%":"—") };
                item.Click += delegate { if(!DetailsExpanded) ToggleDetails(); };menu.Items.Add(item);
            }
            return menu;
        }
        ContextMenu NewMenu() {
            var menu=new ContextMenu { Background=Brush(palette.Dialog),Foreground=Brush(ThemePalette.Text),BorderBrush=ThemePalette.Alpha(palette.Accent,70),Opacity=1,Style=(Style)Window.Resources["PopupMenuStyle"] };
            palette.ApplyResources(menu.Resources);
            menu.Resources[typeof(MenuItem)]=Window.Resources["PopupMenuItemStyle"];
            menu.Resources[typeof(Separator)]=Window.Resources["PopupSeparatorStyle"];
            return menu;
        }
        async void ShowAccounts() { QuickDetails.Toggle("accounts",Find<Button>("AccountButton"),latest,palette);if(QuickDetails.IsOpen) await AccountSwitch.Refresh(); }
        static string Age(long stamp) {
            if(stamp<=0) return "暂无快照";
            long diff=Math.Max(0,DataReader.Now-stamp);
            if(diff<60) return "刚刚更新";
            if(diff<3600) return diff/60+" 分钟前";
            if(diff<86400) return diff/3600+" 小时前";
            return diff/86400+" 天前";
        }
        void RenderAccounts() {
            var panel=Find<StackPanel>("AccountsPanel"); double offset=Find<ScrollViewer>("AccountsScroll").VerticalOffset; panel.Children.Clear();
            Find<TextBlock>("AccountCount").Text=latest.Accounts.Count+" 个账户";
            if(latest.Accounts.Count==0) { var text=Text(latest.DataError!=null ? "额度来源暂不可用" : "暂无上游账户额度快照",11,"#839AB7"); text.Margin=new Thickness(0,4,0,12);panel.Children.Add(text);return; }
            foreach(var a in latest.Accounts) {
                var wrap=new Border { CornerRadius=new CornerRadius(10), Background=Brush("#08FFFFFF"), Padding=new Thickness(11,9,11,10), Margin=new Thickness(0,0,0,7) };
                var body=new StackPanel();
                var heading=new DockPanel { Margin=new Thickness(0,0,0,9), LastChildFill=true };
                var age=Text(Age(a.Captured),9,DataReader.Now-a.Captured>600 ? "#D7AB7D" : "#A9BDD6"); DockPanel.SetDock(age,Dock.Right); heading.Children.Add(age);
                string name=string.IsNullOrWhiteSpace(a.Name)?"未命名账户":a.Name;
                var nameText=Text(name,10,"#CAD7E9"); nameText.MaxWidth=220;nameText.HorizontalAlignment=HorizontalAlignment.Left;nameText.TextTrimming=TextTrimming.CharacterEllipsis;nameText.ToolTip=name+" · "+a.Status; heading.Children.Add(nameText);body.Children.Add(heading);
                var bars=new Grid(); bars.ColumnDefinitions.Add(new ColumnDefinition()); bars.ColumnDefinitions.Add(new ColumnDefinition());
                var primary=QuotaBar(a.PrimaryUsed,a.PrimaryMinutes,a.PrimaryReset,"5 小时",a.Captured);primary.Margin=new Thickness(0,0,9,0);bars.Children.Add(primary);
                var secondary=QuotaBar(a.SecondaryUsed,a.SecondaryMinutes,a.SecondaryReset,"7 天",a.Captured);secondary.Margin=new Thickness(9,0,0,0);Grid.SetColumn(secondary,1);bars.Children.Add(secondary);
                body.Children.Add(bars);wrap.Child=body;panel.Children.Add(wrap);
                if(!string.IsNullOrEmpty(a.RefreshError)) { var error=Text(a.RefreshError,10,"#EDB36F");error.TextWrapping=TextWrapping.Wrap;error.Margin=new Thickness(0,6,0,0);body.Children.Add(error); }
            }
            Find<ScrollViewer>("AccountsScroll").ScrollToVerticalOffset(offset);
        }
        FrameworkElement QuotaBar(double? used,double minutes,long reset,string fallback,long captured) {
            string label=minutes>=1440 ? (minutes/1440).ToString("0.#")+" 天" : minutes>=60 ? (minutes/60).ToString("0.#")+" 小时" : minutes>0 ? minutes.ToString("0")+" 分钟" : fallback;
            double remaining=used.HasValue ? Math.Max(0,Math.Min(100,100-used.Value)) : 0;
            bool expired=reset>0 && reset<=DataReader.Now;
            string color=remaining<=15?"#EDAA7C":remaining<=35?"#E4C985":palette.Accent;
            var root=new StackPanel();
            var labels=new DockPanel { LastChildFill=false };
            var value=Text(!used.HasValue ? "—" : remaining.ToString("0.#")+"%",11,color);value.FontFamily=new FontFamily("Segoe UI"); DockPanel.SetDock(value,Dock.Right);labels.Children.Add(value);
            var title=Text(label+"剩余",9,"#B9C9DF");DockPanel.SetDock(title,Dock.Left);labels.Children.Add(title);root.Children.Add(labels);
            var track=new Grid { Height=4,Margin=new Thickness(0,7,0,0),ClipToBounds=true,Background=Brush("#16FFFFFF") };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(remaining,GridUnitType.Star) });track.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(100-remaining,GridUnitType.Star) });
            if(remaining>0) track.Children.Add(new Border { Background=Brush(color),CornerRadius=new CornerRadius(2) });root.Children.Add(track);
            string info=reset>0 ? DateTimeOffset.FromUnixTimeSeconds(reset).LocalDateTime.ToString("MM-dd HH:mm")+" 重置" : "暂无重置时间";
            root.ToolTip=label+" · "+(expired?"重置时间已过，等待额度更新快照":info)+"\n快照："+(captured>0?DateTimeOffset.FromUnixTimeSeconds(captured).LocalDateTime.ToString("MM-dd HH:mm:ss"):"无");
            if(expired) { var stale=Text("待更新快照",8,"#D7AB7D");stale.Margin=new Thickness(0,5,0,0);root.Children.Add(stale); }
            return root;
        }
        void ShowKeys() {
            if(latest==null) return;
            var view=UsageStore.Selected(latest,config.UsageSource);
            string selected=view.Id=="history"?config.SelectedKey:config.SelectedUsageAccount;
            var menu=NewMenu();
            var all=new MenuItem { Header=view.Id=="history"?"全部密钥":"全部账号",IsCheckable=true,IsChecked=string.IsNullOrEmpty(selected) };all.Click += delegate { if(view.Id=="history") config.SelectedKey="";else config.SelectedUsageAccount="";Save();Render(); };menu.Items.Add(all);
            foreach(var key in view.Groups) {
                var item=new MenuItem { Header=key.Name+" · "+key.Status,IsCheckable=true,IsChecked=key.Id==selected };
                string id=key.Id;item.Click += delegate { if(view.Id=="history") config.SelectedKey=id;else config.SelectedUsageAccount=id;Save();Render(); };menu.Items.Add(item);
            }
            var target=Find<Button>("KeyButton");target.ContextMenu=menu;menu.PlacementTarget=target;menu.IsOpen=true;
        }
        public Window CreateSettingsWindow() {
            var dialog=new Window { Title="Codex Pulse V0.4 设置",ShowInTaskbar=false,Width=570,SizeToContent=SizeToContent.Height,MaxHeight=SystemParameters.WorkArea.Height-60,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterScreen,Background=Brush(palette.Dialog),Foreground=Brush(ThemePalette.Text),FontFamily=Window.FontFamily };
            DialogTheme.Install(dialog,Window,palette);
            var panel=new StackPanel { Margin=new Thickness(22) };
            dialog.Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
            var updateRow=new WrapPanel {Margin=new Thickness(0,0,0,10)};
            updateRow.Children.Add(Text("当前版本 v"+AppUpdater.CurrentVersionText,12,ThemePalette.Text));
            var updateButton=new Button {Content="检查更新",Margin=new Thickness(16,-4,0,0)};updateButton.Click+=delegate {ShowUpdates();};updateRow.Children.Add(updateButton);panel.Children.Add(updateRow);
            var automaticUpdates=new CheckBox {Content="启动后自动检查新版本（每 24 小时检查一次）",IsChecked=config.AutoCheckUpdates,Margin=new Thickness(0,0,0,18)};
            panel.Children.Add(automaticUpdates);
            panel.Children.Add(Text("启动与外观",17,ThemePalette.Text));
            var auto=new CheckBox { Content="登录 Windows 后自动启动看板",Foreground=Brush(ThemePalette.Text),FontSize=12,Margin=new Thickness(0,16,0,6) };
            System.Windows.Automation.AutomationProperties.SetName(auto,"开机自启");
            panel.Children.Add(auto);
            var startupHelp=Text("仅对当前用户生效，保存后应用。",10,ThemePalette.Muted);startupHelp.Margin=new Thickness(0,0,0,18);panel.Children.Add(startupHelp);
            try { auto.IsChecked=!string.IsNullOrWhiteSpace(StartupRegistration.Read()); }
            catch(Exception e) { auto.IsEnabled=false;startupHelp.Text="启动项读取失败："+e.Message; }
            var clickThrough=new CheckBox { Content="置顶时让鼠标穿透到下方应用",IsChecked=config.ClickThroughWhenPinned,Foreground=Brush(ThemePalette.Text),FontSize=12,Margin=new Thickness(0,0,0,6) };
            System.Windows.Automation.AutomationProperties.SetName(clickThrough,"置顶时鼠标穿透");panel.Children.Add(clickThrough);
            var clickHelp=Text("右上角按钮可直接切换；热力图悬浮提示始终保留。",10,ThemePalette.Muted);clickHelp.Margin=new Thickness(0,0,0,18);panel.Children.Add(clickHelp);
            panel.Children.Add(Text("莫兰迪主题色",12,ThemePalette.Text));
            string chosen=config.Theme,savedTheme=config.Theme;
            int savedTint=config.BackgroundOpacity;
            bool applied=false;
            Slider glass=null;
            var choices=new WrapPanel { Margin=new Thickness(0,10,0,18) };panel.Children.Add(choices);
            var themeButtons=new Dictionary<string,Button>();
            Action updateChoices=delegate {
                foreach(var pair in themeButtons) {
                    bool selected=pair.Key==chosen;
                    pair.Value.BorderBrush=Brush(selected?palette.Accent:"#38FFFFFF");
                    pair.Value.BorderThickness=new Thickness(selected?2:1);
                    pair.Value.Background=selected?ThemePalette.Alpha(palette.Accent,45):Brush("#0BFFFFFF");
                }
            };
            foreach(var theme in ThemePalette.All) {
                string id=theme.Id;
                var content=new StackPanel { Orientation=Orientation.Horizontal };
                content.Children.Add(new Ellipse { Width=13,Height=13,Fill=Brush(theme.Accent),Margin=new Thickness(0,0,7,0) });
                content.Children.Add(Text(theme.Name,11,ThemePalette.Text));
                var button=new Button { Content=content,Width=120,Height=42,Padding=new Thickness(7),Margin=new Thickness(0,0,6,6) };
                System.Windows.Automation.AutomationProperties.SetName(button,theme.Name);
                button.Click += delegate {
                    chosen=id;ApplyTheme(chosen);palette.Apply(dialog);dialog.Background=Brush(palette.Dialog);
                    if(glass!=null) BackdropMode=Backdrop.SetTint(Window,(int)glass.Value);
                    updateChoices();
                };
                choices.Children.Add(button);themeButtons[id]=button;
            }
            updateChoices();
            var glassLabel=Text("背景不透明度 · "+config.BackgroundOpacity+"%",11,ThemePalette.Muted);panel.Children.Add(glassLabel);
            glass=new Slider { Minimum=0,Maximum=85,Value=config.BackgroundOpacity,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(0,10,0,8) };
            glass.ValueChanged += delegate { glassLabel.Text="背景不透明度 · "+((int)glass.Value)+"%";BackdropMode=Backdrop.SetTint(Window,(int)glass.Value); };
            panel.Children.Add(glass);
            var glassHelp=Text("越低越透明；切换配色可即时预览，取消可恢复。",10,ThemePalette.Muted);glassHelp.Margin=new Thickness(0,0,0,20);panel.Children.Add(glassHelp);
            panel.Children.Add(Text("账号与 Codex",14,ThemePalette.Text));
            var note=Text("通过本机 Codex 管理账号、查询额度与切换登录。",10,ThemePalette.Muted);note.Margin=new Thickness(0,7,0,12);panel.Children.Add(note);
            var independent=new Button { Content="账号管理",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12) };independent.Click+=delegate { ShowIndependentAccounts(); };panel.Children.Add(independent);
            TextBox codexExecutable=SettingsField(panel,"Codex 桌面程序（可留空自动查找）",config.CodexExe,false);
            panel.Children.Add(Text("模型参考价格",14,ThemePalette.Text));
            var builtin=PriceBook.Builtin();
            var pricingInfo=Text("已内置 "+builtin.Models.Count+" 个模型 · "+builtin.CheckedOn+" 核对 · 美元 / 百万 Token",11,ThemePalette.Muted);pricingInfo.Margin=new Thickness(0,8,0,8);panel.Children.Add(pricingInfo);
            var showPrices=new Button {Content="查看内置价格表",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,14)};showPrices.Click+=delegate {PriceTableWindow.Create(dialog,ThemePalette.Get(chosen)).ShowDialog();};panel.Children.Add(showPrices);
            TextBox priceTable=SettingsField(panel,"自定义价格表（可选 JSON，覆盖内置）",config.PriceTablePath,false);
            priceTable.ToolTip="无需选择文件即可使用内置价格；兼容账号库目录中的 model-prices.json。";
            string activePriceNote;PriceBook.Load(config,out activePriceNote);
            var priceHelp=Text(activePriceNote+"\n面板费用使用本机逐条日志估算，与官方累计范围不同；点击金额查看覆盖范围和未计价原因。",10,ThemePalette.Muted);priceHelp.TextWrapping=TextWrapping.Wrap;priceHelp.Margin=new Thickness(0,0,0,14);panel.Children.Add(priceHelp);
            dialog.Closed += delegate { if(!applied) { ApplyTheme(savedTheme);BackdropMode=Backdrop.SetTint(Window,savedTint); } };
            var actions=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,5,0,0) };
            var cancel=new Button { Content="取消",Padding=new Thickness(18,8,18,8),Margin=new Thickness(0,0,8,0) };cancel.Click += delegate { dialog.Close(); };actions.Children.Add(cancel);
            var save=new Button { Content="保存并应用",Padding=new Thickness(18,8,18,8),Foreground=Brush(ThemePalette.Text) };
            save.SetResourceReference(Control.BackgroundProperty,"Theme.Strong");
            save.Click += async delegate {
                string previous=null;bool startupChanged=false;
                try {
                    if(AccountSwitch.Busy || AccountSwitch.Loading) throw new IOException("正在连接或切换账号，请稍后保存设置。");
                    if(!string.IsNullOrWhiteSpace(codexExecutable.Text)) CodexDesktop.ResolveLauncher(codexExecutable.Text.Trim());
                    var proposed=Json.New().Deserialize<Settings>(Json.New().Serialize(config));
                    proposed.CodexExe=codexExecutable.Text.Trim();
                    proposed.PriceTablePath=priceTable.Text.Trim();
                    if(!string.IsNullOrEmpty(proposed.PriceTablePath)) { string reason;if(PriceBook.Load(proposed,out reason)==null) throw new IOException("模型价格表不可读或格式无效，请检查版本号和模型价格。"); }
                    proposed.BackgroundOpacity=(int)glass.Value;proposed.Theme=chosen;
                    proposed.ClickThroughWhenPinned=clickThrough.IsChecked==true;
                    proposed.AutoCheckUpdates=automaticUpdates.IsChecked==true;
                    if(auto.IsEnabled) {
                        previous=StartupRegistration.Read();proposed.AutoStart=auto.IsChecked==true;
                        if(proposed.AutoStart?!StartupRegistration.IsEnabled(previous):previous!=null) { StartupRegistration.Set(proposed.AutoStart);startupChanged=true; }
                    }
                    proposed.Save();config=proposed;AccountSwitch.Reset();applied=true;ApplyTheme(config.Theme);dialog.Close();interactionPaused=false;UpdateInteractionMode();await Refresh();
                } catch(Exception e) {
                    string rollback="";
                    if(startupChanged) try { StartupRegistration.Restore(previous); } catch(Exception restore) { rollback="\n启动项恢复失败："+restore.Message; }
                    MessageBox.Show(dialog,"设置保存失败："+e.Message+rollback,"保存失败",MessageBoxButton.OK,MessageBoxImage.Information);
                }
            };actions.Children.Add(save);panel.Children.Add(actions);
            return dialog;
        }
        void ShowSettings() { CreateSettingsWindow().ShowDialog(); }
        async void ShowIndependentAccounts() {
            if(managingAccounts || AccountSwitch.Busy || AccountSwitch.Loading || reading) { MessageBox.Show(Window,"正在查询或切换账号，请稍后打开账号管理。");return; }
            managingAccounts=true;QuickDetails.Close();
            try { new IndependentAccountWindow(dialogOwner(),config,proposed=> { var updated=Json.New().Deserialize<Settings>(Json.New().Serialize(proposed));updated.Save();config=updated;AccountSwitch.Reset(); }).Window.ShowDialog(); }
            finally { managingAccounts=false; }
            await Refresh();
        }
        TextBox SettingsField(StackPanel panel,string label,string value,bool folder) {
            panel.Children.Add(Text(label,11,"#A8BDD6"));
            var grid=new Grid { Margin=new Thickness(0,6,0,14) };grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var field=new TextBox { Text=value,Padding=new Thickness(8),FontSize=11,VerticalContentAlignment=VerticalAlignment.Center };
            grid.Children.Add(field);
            var browse=new Button { Content="浏览",Padding=new Thickness(10,6,10,6),Margin=new Thickness(8,0,0,0) };Grid.SetColumn(browse,1);grid.Children.Add(browse);
            browse.Click += delegate {
                if(folder) { using(var picker=new System.Windows.Forms.FolderBrowserDialog { Description=label,SelectedPath=field.Text }) { if(picker.ShowDialog()==System.Windows.Forms.DialogResult.OK) field.Text=picker.SelectedPath; } }
                else { var picker=new Microsoft.Win32.OpenFileDialog { Title=label,FileName=field.Text,Filter=label.Contains("价格表")?"JSON 价格表|*.json|所有文件|*.*":label.Contains("数据库")?"SQLite 数据库|*.db;*.sqlite|所有文件|*.*":"Windows 程序|*.exe|所有文件|*.*" };if(picker.ShowDialog(dialogOwner())==true) field.Text=picker.FileName; }
            };
            panel.Children.Add(grid);return field;
        }
        Window dialogOwner() { return System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(x=>x.IsActive)??Window; }
        public void Capture(string path) {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            Window.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)Window.ActualWidth,(int)Window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(Window);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(path)) png.Save(f);
        }
    }

}
