using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CodexPulse {
    public sealed class UpdateWindow {
        public Window Window {get;private set;}
        readonly AppUpdater service;
        readonly Action<UpdateRelease> found;
        readonly Func<string> blocked;
        readonly Action<PreparedUpdate> install;
        readonly TextBlock status,version;
        readonly TextBox notes;
        readonly Button check,update,cancel;
        readonly ProgressBar progress;
        CancellationTokenSource cancellation;
        UpdateRelease latest;
        PreparedUpdate prepared;
        bool busy,closed,handoff;
        public UpdateWindow(Window owner,ThemePalette theme,AppUpdater service,UpdateRelease cached,Action<UpdateRelease> found,Func<string> blocked,Action<PreparedUpdate> install) {
            this.service=service;this.found=found;this.blocked=blocked;this.install=install;
            Window=new Window {Title="Codex Pulse · 版本更新",Width=570,Height=Math.Min(620,SystemParameters.WorkArea.Height-60),MinHeight=380,
                Owner=DialogTheme.OwnerFor(owner),ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterScreen,ResizeMode=ResizeMode.CanResize,FontFamily=owner.FontFamily};
            DialogTheme.Install(Window,owner,theme);
            var layout=new DockPanel {Margin=new Thickness(22)};Window.Content=layout;
            var heading=new StackPanel();DockPanel.SetDock(heading,Dock.Top);layout.Children.Add(heading);
            version=new TextBlock {Text="当前版本 v"+AppUpdater.CurrentVersionText,FontSize=19};heading.Children.Add(version);
            status=new TextBlock {Text="准备检查更新",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)};heading.Children.Add(status);
            heading.Children.Add(new TextBlock {Text="更新会重启 Codex Pulse，现有账号、设置和用量记录保留。",FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
            var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
            progress=new ProgressBar {Height=5,Minimum=0,Maximum=100,Visibility=Visibility.Collapsed,Margin=new Thickness(0,12,0,12)};footer.Children.Add(progress);
            var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};footer.Children.Add(buttons);
            var page=new Button {Content="发布页",Margin=new Thickness(0,0,8,0)};page.Click+=delegate {System.Diagnostics.Process.Start(AppUpdater.ReleasesUrl);};buttons.Children.Add(page);
            check=new Button {Content="检查更新",Margin=new Thickness(0,0,8,0)};check.Click+=async delegate {await Check();};buttons.Children.Add(check);
            update=new Button {Content="下载并重启更新",IsEnabled=false,Margin=new Thickness(0,0,8,0)};update.Click+=async delegate {await Install();};buttons.Children.Add(update);
            cancel=new Button {Content="关闭"};cancel.Click+=delegate {if(busy && cancellation!=null) cancellation.Cancel();else Window.Close();};buttons.Children.Add(cancel);
            notes=new TextBox {IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalContentAlignment=VerticalAlignment.Top,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(10)};layout.Children.Add(notes);
            Window.Closing+=delegate {closed=true;if(!handoff && cancellation!=null) cancellation.Cancel();Cleanup();};
            Window.Loaded+=async delegate {if(cached!=null) ShowRelease(cached);else await Check();};
        }
        void Cleanup() {if(prepared!=null && !handoff) {try {Directory.Delete(prepared.Directory,true);} catch {} prepared=null;}}
        void Busy(bool value) {
            busy=value;check.IsEnabled=!value;update.IsEnabled=!value && latest!=null && latest.Version>AppUpdater.CurrentVersion;
            cancel.Content=value?"取消":"关闭";progress.Visibility=value?Visibility.Visible:Visibility.Collapsed;
        }
        void ShowRelease(UpdateRelease release) {
            latest=release;found(release);
            bool newer=release.Version>AppUpdater.CurrentVersion;
            version.Text="当前 v"+AppUpdater.CurrentVersionText+(newer?"  →  v"+release.VersionText:"");
            status.Text=newer?"发现新版本，可下载并更新。":"当前已是最新版本。";
            notes.Text=release.Notes;update.IsEnabled=newer;
        }
        async Task Check() {
            if(busy) return;
            cancellation=new CancellationTokenSource();Busy(true);status.Text="正在检查 GitHub 最新稳定版…";progress.IsIndeterminate=true;
            try {var release=await service.CheckAsync(cancellation.Token);if(!closed) {Cleanup();ShowRelease(release);}}
            catch(Exception error) {if(!closed) status.Text=AppUpdater.FriendlyError(error);}
            finally {cancellation.Dispose();cancellation=null;if(!closed) Busy(false);}
        }
        async Task Install() {
            if(busy || latest==null || latest.Version<=AppUpdater.CurrentVersion) return;
            string reason=blocked();if(reason!=null) {status.Text=reason;return;}
            cancellation=new CancellationTokenSource();Busy(true);progress.IsIndeterminate=false;progress.Value=0;
            try {
                if(prepared==null) {
                    status.Text="正在下载并校验更新…";
                    prepared=await service.PrepareAsync(latest,new Progress<int>(value=>{if(!closed) {progress.Value=value;status.Text="正在下载并校验更新… "+value+"%";}}),cancellation.Token);
                }
                if(closed) {Cleanup();return;}
                cancellation.Token.ThrowIfCancellationRequested();
                reason=blocked();if(reason!=null) {status.Text=reason+"\n更新包已准备好，稍后点击安装即可。";update.Content="安装并重启";return;}
                status.Text="校验通过，正在重启更新…";
                // The callback launches the helper before allowing the application
                // to exit. A launch failure keeps this window and the old app alive.
                handoff=true;
                try {install(prepared);} catch {handoff=false;throw;}
            } catch(Exception error) {if(!closed) status.Text=AppUpdater.FriendlyError(error);}
            finally {cancellation.Dispose();cancellation=null;if(!closed) Busy(false);}
        }
    }
}
