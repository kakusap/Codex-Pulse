using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexPulse {
    public sealed class IndependentAccountWindow {
        public Window Window { get; private set; }
        public bool Busy { get; private set; }
        public IndependentAccountWindow(Window owner,Settings current,Action<Settings> apply) {
            var settings=Json.New().Deserialize<Settings>(Json.New().Serialize(current));var theme=ThemePalette.Get(settings.Theme);
            Window=new Window { Title="Codex Pulse · 账号管理",Owner=owner,ShowInTaskbar=false,Width=640,SizeToContent=SizeToContent.Height,MaxHeight=SystemParameters.WorkArea.Height-60,WindowStartupLocation=WindowStartupLocation.CenterScreen,ResizeMode=ResizeMode.NoResize,Background=ThemePalette.Brush(theme.Dialog),Foreground=ThemePalette.Brush(ThemePalette.Text),FontFamily=owner.FontFamily,Topmost=owner.Topmost };
            DialogTheme.Install(Window,owner,theme);
            var panel=new StackPanel { Margin=new Thickness(22) };Window.Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
            panel.Children.Add(Label("独立账号与额度",17));
            panel.Children.Add(Label("登录、额度与用量使用本机 Codex；展开看板可分别查看官方用量、本机记录与迁移历史。",11));
            panel.Children.Add(Label("Codex 账号目录（留空使用 CODEX_HOME 或用户 .codex 目录）",10));
            var codexHome=new TextBox { Text=settings.CodexHome,Padding=new Thickness(7),Margin=new Thickness(0,4,0,8),ToolTip="填写 Codex 的账号与配置目录，不是程序路径。留空自动使用默认目录；点击“保存账号目录”时生效。" };panel.Children.Add(codexHome);
            var accounts=new ListBox { MinHeight=100,MaxHeight=220,Margin=new Thickness(0,5,0,8),DisplayMemberPath="Display",SelectedValuePath="Id" };panel.Children.Add(accounts);
            var status=Label("账号独立保存在本机。",11);panel.Children.Add(status);
            var progress=Label("",11);panel.Children.Add(progress);
            var actions=new WrapPanel();panel.Children.Add(actions);
            Button add=Button("添加账号（浏览器）"),device=Button("设备码登录"),relogin=Button("重新登录所选账号"),remove=Button("从看板移除"),refresh=Button("刷新全部额度"),enable=Button("保存账号目录"),restore=Button("恢复上次切换前配置");
            foreach(var button in new[] { add,device,relogin,remove,refresh,enable,restore }) actions.Children.Add(button);
            add.ToolTip="打开官方登录网页，添加独立账号；不会立即切换当前 Codex。";
            device.ToolTip="通过官方设备码登录添加账号，适用于网页回调不便的情况。";
            relogin.ToolTip="先选择一个账号，再更新它的登录凭据。登录的账号必须与所选账号一致。";
            remove.ToolTip="移除所选账号在看板中的独立记录和凭据；不会注销 OpenAI 账号或退出当前 Codex。";
            refresh.ToolTip="立即向官方查询所有独立账号的额度；失败时保留旧快照并显示原因。";
            enable.ToolTip="保存上方账号目录；不会立即切换或重启 Codex。";
            restore.ToolTip="恢复上次切换前备份的 Codex 配置及登录文件；需要重新启动 Codex 才生效。";
            var cancel=Button("取消登录");cancel.IsEnabled=false;panel.Children.Add(cancel);CancellationTokenSource cancellation=null;
            cancel.ToolTip="取消正在等待的网页或设备码登录；没有进行登录时不可用。";
            Action reload=delegate {
                string selected=Convert.ToString(accounts.SelectedValue);
                accounts.ItemsSource=IndependentStore.Accounts(settings.PulseDb).Select(a=>new Row { Id=a.Id,Display=(string.IsNullOrEmpty(a.Name)?"未命名":a.Name)+" · "+a.Status+" · "+(a.Captured>0?DateTimeOffset.FromUnixTimeSeconds(a.Captured).LocalDateTime.ToString("MM-dd HH:mm"):"未查询")+(string.IsNullOrEmpty(a.RefreshError)?"":"\n"+a.RefreshError) }).ToList();
                accounts.SelectedValue=selected;
            };
            try { reload(); } catch { status.Text="独立账号库尚不可用，请检查 Pulse 数据路径。"; }
            Action<bool> busy=value=> { Busy=value;actions.IsEnabled=!value;codexHome.IsEnabled=!value;accounts.IsEnabled=!value;cancel.IsEnabled=value && cancellation!=null; };
            Action prepare=delegate { settings.CodexHome=codexHome.Text.Trim();ProfileFiles.Home(settings); };
            Window.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e) { if(Busy) { if(cancellation!=null) cancellation.Cancel();e.Cancel=true;status.Text="正在结束账号操作，请稍候…"; } };
            cancel.Click+=delegate { if(cancellation!=null) cancellation.Cancel(); };
            Func<string,bool,Task> login=async (replace,useDevice)=> {
                try { prepare(); } catch(Exception e) { status.Text=e.Message;return; }
                cancellation=new CancellationTokenSource();busy(true);progress.Text="正在创建隔离登录环境…";
                try {
                    var token=cancellation.Token;
                    await Task.Run(()=>new IndependentRuntime(settings,null).Login(replace,useDevice,prompt=>Window.Dispatcher.BeginInvoke(new Action(()=> {
                        progress.Text=prompt.Device?"在浏览器输入设备码："+prompt.Code:"请在浏览器完成登录；此窗口将自动更新。";
                        try { Process.Start(new ProcessStartInfo(prompt.Url) { UseShellExecute=true }); } catch { progress.Text+="\n无法打开浏览器，请检查默认浏览器设置。"; }
                    })),token));
                    status.Text="账号已保存，可在全部账号明细中切换并重启 Codex。";reload();
                } catch(Exception e) { status.Text=e is AccountOperationException?e.Message:"登录未完成，请检查运行时或重新尝试。"; }
                finally { cancellation.Dispose();cancellation=null;busy(false); }
            };
            add.Click+=async delegate { await login(null,false); };
            device.Click+=async delegate { await login(null,true); };
            relogin.Click+=async delegate { string id=Convert.ToString(accounts.SelectedValue);if(string.IsNullOrEmpty(id)) { status.Text="请先选择一个账号。";return; }await login(id,false); };
            refresh.Click+=async delegate { busy(true);try { prepare();var result=await Task.Run(()=>new IndependentAccounts(settings).Refresh(true));reload();status.Text="已检查 "+result.Accounts.Count+" 个账号，失败账号保留旧快照并显示原因。"; } catch { status.Text="额度查询未完成，请检查登录、网络和运行时。"; } finally { busy(false); } };
            remove.Click+=delegate { string id=Convert.ToString(accounts.SelectedValue);if(string.IsNullOrEmpty(id)) { status.Text="请先选择一个账号。";return; }try { IndependentStore.Remove(settings.PulseDb,id);reload();status.Text="已从看板移除。当前 Codex 登录和已保存的历史保持不变。"; } catch(Exception e) { status.Text="移除失败："+e.Message; } };
            enable.Click+=delegate {
                try { prepare();apply(settings);status.Text="账号目录已保存。关闭此窗口后自动查询额度。"; }
                catch(Exception e) { status.Text="账号目录保存失败："+e.Message; }
            };
            restore.Click+=delegate {
                try { prepare();using(var lease=new AccountLease(settings.PulseDb)) new ProfileFiles(settings).RestoreLatest();status.Text="已恢复上次切换前的配置与登录文件。重新启动 Codex 后生效。"; }
                catch(Exception e) { status.Text="恢复未完成："+e.Message; }
            };
        }
        sealed class Row { public string Id { get; set; } public string Display { get; set; } }
        static TextBlock Label(string text,double size) { return new TextBlock { Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,8) }; }
        static Button Button(string text) { return new Button { Content=text,Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,0,7,7) }; }
    }
}
