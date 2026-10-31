using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexPulse {
    // An owned, non-capturing window lets the same trigger receive the second
    // click. ContextMenu's automatic outside-click dismissal can reopen it.
    public sealed class QuotaDetailsPopup : IDisposable {
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongW",SetLastError=true)] static extern int SetWindowLong(IntPtr hwnd,int index,int value);
        readonly Window owner;
        readonly Func<string> monitor;
        FrameworkElement anchor;
        ScrollViewer detailsScroll;
        Button refreshButton;
        RotateTransform refreshRotation;
        bool syncing,disposed;
        public Window PopupWindow { get; private set; }
        public string ActiveSection { get; private set; }
        public bool IsOpen { get { return PopupWindow!=null && PopupWindow.IsVisible; } }
        public AccountSwitchController AccountSwitch { get; set; }
        public Action ManageAccounts { get; set; }
        public Func<Task> RefreshSnapshot { get; set; }
        public bool IsRefreshing { get; private set; }
        public QuotaDetailsPopup(Window owner,Func<string> monitor) {
            this.owner=owner;this.monitor=monitor;ActiveSection="";
            owner.LocationChanged+=OnLayout;owner.SizeChanged+=OnSize;
            owner.IsVisibleChanged+=OnState;owner.IsEnabledChanged+=OnState;
        }
        public void Toggle(string section,FrameworkElement trigger,Snapshot snapshot,ThemePalette theme) {
            bool same=IsOpen && ActiveSection==section;Close();
            if(same || disposed) return;
            ActiveSection=section;anchor=trigger;
            PopupWindow=new Window { Title="Codex Pulse · "+Title(section),Owner=owner,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
                AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,SizeToContent=SizeToContent.Height,
                Width=380,MaxHeight=Math.Max(150,MonitorDock.WorkHeight(owner,monitor())-70),WindowStartupLocation=WindowStartupLocation.Manual,
                Left=owner.Left,Top=owner.Top+56,FontFamily=owner.FontFamily };
            var popup=PopupWindow;
            popup.Resources[typeof(Button)]=owner.Resources[typeof(Button)];
            popup.SourceInitialized+=delegate {
                var handle=new WindowInteropHelper(popup).Handle;int style=GetWindowLong(handle,-20);
                if(SetWindowLong(handle,-20,(style|0x08000000|0x80)&~0x20)==0) throw new Win32Exception(Marshal.GetLastWin32Error());
            };
            popup.Closed+=delegate { if(PopupWindow==popup) { StopRefreshAnimation();PopupWindow=null;ActiveSection="";anchor=null;detailsScroll=null;refreshButton=null;refreshRotation=null; } };
            var layout=new DockPanel { LastChildFill=true };
            var header=new DockPanel { LastChildFill=true,Margin=new Thickness(0,0,0,5) };DockPanel.SetDock(header,Dock.Top);layout.Children.Add(header);
            if(section=="primary") {
                refreshRotation=new RotateTransform();
                var icon=new TextBlock { Text="\uE72C",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=14,RenderTransformOrigin=new Point(.5,.5),RenderTransform=refreshRotation };
                refreshButton=new Button { Content=icon,Width=28,Height=28,Padding=new Thickness(6),Margin=new Thickness(8,-5,-4,-1),Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=ThemePalette.Brush(theme.Accent) };
                System.Windows.Automation.AutomationProperties.SetName(refreshButton,"刷新额度快照");
                refreshButton.Click+=async delegate { if(!IsRefreshing && RefreshSnapshot!=null) await RefreshSnapshot(); };
                DockPanel.SetDock(refreshButton,Dock.Right);header.Children.Add(refreshButton);UpdateRefreshButton();
            }
            var heading=Text(Title(section),13,ThemePalette.Brush(ThemePalette.Text));heading.FontWeight=FontWeights.SemiBold;heading.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(heading);
            detailsScroll=new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };layout.Children.Add(detailsScroll);
            popup.Content=new Border { Background=ThemePalette.Brush(theme.Dialog),BorderBrush=ThemePalette.Alpha(theme.Accent,70),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9),Padding=new Thickness(14),Child=layout };
            Refresh(snapshot,theme);popup.Show();popup.UpdateLayout();Sync();
        }
        public void SetRefreshing(bool value) {
            if(IsRefreshing==value) return;
            IsRefreshing=value;UpdateRefreshButton();
        }
        void UpdateRefreshButton() {
            if(refreshButton==null) return;
            refreshButton.IsEnabled=!IsRefreshing && RefreshSnapshot!=null;
            refreshButton.ToolTip=IsRefreshing?"正在刷新额度快照…":"刷新额度快照 (F5)";
            StopRefreshAnimation();
            if(IsRefreshing) refreshRotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(.9)) { RepeatBehavior=RepeatBehavior.Forever });
        }
        void StopRefreshAnimation() { if(refreshRotation!=null) { refreshRotation.BeginAnimation(RotateTransform.AngleProperty,null);refreshRotation.Angle=0; } }
        static string Title(string section) { return section=="primary"?"5 小时额度详情":section=="secondary"?"7 天额度详情":"全部账号明细"; }
        static TextBlock Text(string value,double size,Brush color) { return new TextBlock { Text=value,FontSize=size,Foreground=color,TextWrapping=TextWrapping.Wrap }; }
        public void Refresh(Snapshot snapshot,ThemePalette theme) {
            if(PopupWindow==null || detailsScroll==null) return;
            theme.Apply(PopupWindow);
            var chrome=(Border)PopupWindow.Content;chrome.Background=ThemePalette.Brush(theme.Dialog);chrome.BorderBrush=ThemePalette.Alpha(theme.Accent,70);
            if(refreshButton!=null) refreshButton.Foreground=ThemePalette.Brush(theme.Accent);
            var content=new StackPanel();
            var hint=Text("再次点击同一处收起 · 点击另一处切换",10,ThemePalette.Brush(ThemePalette.Muted));hint.Margin=new Thickness(0,5,0,12);content.Children.Add(hint);
            if(ActiveSection=="accounts" && ManageAccounts!=null) {
                var manage=new Button { Content="账号管理 / 添加账号",HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,0,0,10) };manage.Click+=delegate { ManageAccounts(); };content.Children.Add(manage);
            }
            if(ActiveSection=="accounts" && AccountSwitch!=null) {
                var connection=Text(AccountSwitch.Message,11,ThemePalette.Brush(AccountSwitch.HasError?"#EDB36F":theme.Accent));
                connection.Margin=new Thickness(0,0,0,10);content.Children.Add(connection);
                var restartHint=Text("点击切换会重启 Codex，进行中的任务可能中断。",10,ThemePalette.Brush(ThemePalette.Muted));
                restartHint.Margin=new Thickness(0,0,0,10);content.Children.Add(restartHint);
                if(!AccountSwitch.Ready && !AccountSwitch.Busy) {
                    var retry=new Button { Content=AccountSwitch.Loading?"正在连接…":"刷新连接状态",IsEnabled=!AccountSwitch.Loading,Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,0,0,10) };
                    retry.Click+=async delegate { await AccountSwitch.Refresh(); };content.Children.Add(retry);
                }
            }
            if(snapshot==null || snapshot.Accounts.Count==0 || snapshot.DataError!=null) content.Children.Add(Text(snapshot==null?"正在读取快照":snapshot.DataError??"暂无账号快照",11,ThemePalette.Brush(ThemePalette.Muted)));
            else {
                if(ActiveSection!="accounts") {
                    var total=QuotaSummary.For(snapshot.Accounts,ActiveSection=="primary"?300:10080);
                    var summary=Text("全部账号平均剩余 "+Percent(total)+" · 有效快照 "+total.Available+" / "+total.Total,11,ThemePalette.Brush(theme.Accent));summary.Margin=new Thickness(0,0,0,10);content.Children.Add(summary);
                }
                foreach(var account in snapshot.Accounts) {
                    var body=new StackPanel();
                    var header=new DockPanel { LastChildFill=true };body.Children.Add(header);
                    var name=Text(string.IsNullOrWhiteSpace(account.Name)?"未命名账号":account.Name,11,ThemePalette.Brush(ThemePalette.Text));name.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(name);
                    if(ActiveSection=="accounts") {
                        if(AccountSwitch!=null) {
                            var target=account;
                            bool current=AccountSwitch.CurrentAccountId==account.Id;
                            if(current) { var badge=Text("● 当前使用中",10,ThemePalette.Brush("#73DDB4"));badge.ToolTip="当前直连账号";badge.Margin=new Thickness(0,5,0,0);body.Children.Add(badge); }
                            bool candidate=AccountSwitch.Ready && AccountSwitch.Candidates.Contains(account.Id) && account.Status=="active";
                            var change=new Button { Content=AccountSwitch.BusyAccountId==account.Id?"正在切换并重启…":account.Status!="active"?"需要重新登录":!AccountSwitch.Ready?"等待连接":!candidate?"无可用登录凭据":current?"重新应用并重启":"切换并重启 Codex",
                                IsEnabled=candidate && !AccountSwitch.Busy && !AccountSwitch.Loading,HorizontalAlignment=HorizontalAlignment.Right,FontSize=11,Padding=new Thickness(10,6,10,6),Margin=new Thickness(8,0,0,0),
                                Foreground=ThemePalette.Brush(ThemePalette.Text),ToolTip="直接连接 OpenAI，并自动重新启动 Codex 桌面应用" };
                            System.Windows.Automation.AutomationProperties.SetName(change,"切换账号 "+account.Name);
                            change.Click+=async delegate { await AccountSwitch.Switch(target); };DockPanel.SetDock(change,Dock.Right);header.Children.Insert(0,change);
                        }
                        var values=Text(WindowLabel(account.PrimaryMinutes,"主窗口")+"剩余 "+Remaining(account.PrimaryUsed)+"     "+WindowLabel(account.SecondaryMinutes,"次窗口")+"剩余 "+Remaining(account.SecondaryUsed),11,ThemePalette.Brush(theme.Accent));values.Margin=new Thickness(0,7,0,0);body.Children.Add(values);
                        var status=Text((account.Status??"未知状态")+" · "+SnapshotAge(account.Captured),10,ThemePalette.Brush(ThemePalette.Muted));status.Margin=new Thickness(0,5,0,0);body.Children.Add(status);
                    } else {
                        var quota=QuotaSummary.For(new[] { account },ActiveSection=="primary"?300:10080);
                        var value=Text("剩余 "+Percent(quota)+" · "+Reset(quota),12,ThemePalette.Brush(theme.Accent));value.Margin=new Thickness(0,7,0,0);body.Children.Add(value);
                        var age=Text(SnapshotAge(account.Captured),10,ThemePalette.Brush(ThemePalette.Muted));age.Margin=new Thickness(0,5,0,0);body.Children.Add(age);
                    }
                    if(!string.IsNullOrEmpty(account.RefreshError)) { var error=Text(account.RefreshError,10,ThemePalette.Brush("#EDB36F"));error.Margin=new Thickness(0,6,0,0);body.Children.Add(error); }
                    content.Children.Add(new Border { Child=body,Background=ThemePalette.Alpha(theme.Accent,18),CornerRadius=new CornerRadius(7),Padding=new Thickness(10),Margin=new Thickness(0,0,0,6) });
                }
            }
            double offset=detailsScroll.VerticalOffset;detailsScroll.Content=content;
            detailsScroll.ScrollToVerticalOffset(offset);PopupWindow.UpdateLayout();Sync();
        }
        static string Percent(QuotaSummary quota) { return quota.Remaining.HasValue?quota.Remaining.Value.ToString("0.#")+"%":"—"; }
        static string Remaining(double? used) { return used.HasValue?(100-used.Value).ToString("0.#")+"%":"—"; }
        static string WindowLabel(double minutes,string fallback) { return minutes>=1440?(minutes/1440).ToString("0.#")+" 天":minutes>=60?(minutes/60).ToString("0.#")+" 小时":minutes>0?minutes.ToString("0")+" 分钟":fallback; }
        static string SnapshotAge(long captured) {
            if(captured<=0) return "暂无快照";
            long age=Math.Max(0,DataReader.Now-captured);
            return age<60?"快照刚刚更新":age<3600?"快照 "+age/60+" 分钟前":age<86400?"快照 "+age/3600+" 小时前":"快照 "+age/86400+" 天前";
        }
        static string Reset(QuotaSummary quota) {
            if(!quota.Remaining.HasValue) return "暂无有效快照";
            if(quota.Expired>0) return "重置已过，等待更新";
            if(quota.NextReset==0) return "暂无重置时间";
            var left=TimeSpan.FromSeconds(Math.Max(0,quota.NextReset-DataReader.Now));
            return left.TotalDays>=1?(int)left.TotalDays+"天"+left.Hours+"小时后重置":left.TotalHours>=1?(int)left.TotalHours+"小时"+left.Minutes+"分后重置":Math.Max(1,(int)left.TotalMinutes)+"分后重置";
        }
        public void Sync() {
            if(syncing || !IsOpen || anchor==null) return;
            syncing=true;try { MonitorDock.PlaceBelow(PopupWindow,owner,anchor,monitor()); } finally { syncing=false; }
        }
        public void Close() { if(PopupWindow!=null) PopupWindow.Close(); }
        void OnLayout(object sender,EventArgs e) { Sync(); }
        void OnSize(object sender,SizeChangedEventArgs e) { Sync(); }
        void OnState(object sender,DependencyPropertyChangedEventArgs e) { if(!owner.IsVisible || !owner.IsEnabled) Close(); }
        public void Dispose() { if(disposed) return;disposed=true;Close();owner.LocationChanged-=OnLayout;owner.SizeChanged-=OnSize;owner.IsVisibleChanged-=OnState;owner.IsEnabledChanged-=OnState; }
    }
}
