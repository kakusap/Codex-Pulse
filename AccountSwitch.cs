using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPulse {
    public sealed class DirectProfile {
        public string Mode, AccountId, Home;
    }
    public interface IAccountProfiles {
        DirectProfile Get();
        HashSet<string> Candidates();
        DirectProfile Apply(string accountId,string home);
    }
    public interface ICodexDesktop {
        void Prepare();
        void Restart();
    }
    public sealed class CodexDesktop : ICodexDesktop {
        readonly string configured;
        string launcher,desktopDirectory;
        public CodexDesktop(string configured) { this.configured=configured; }
        public void Prepare() {
            launcher=ResolveLauncher(configured);
            desktopDirectory=Path.GetDirectoryName(launcher);
            if(File.Exists(Path.Combine(desktopDirectory,"app","resources","app.asar")) || Directory.Exists(Path.Combine(desktopDirectory,"app","resources"))) desktopDirectory=Path.Combine(desktopDirectory,"app");
        }
        public static string ResolveLauncher(string configured) {
            if(!string.IsNullOrWhiteSpace(configured)) {
                string path=Path.GetFullPath(configured);
                if(!File.Exists(path) || !IsDesktopDirectory(Path.GetDirectoryName(path)) ||
                    !(Path.GetFileName(path).Equals("Codex.exe",StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("LaunchCodex.exe",StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("请在设置中选择 Codex 桌面程序，不能选择 resources 中的 codex.exe 命令行程序。");
                return path;
            }
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach(string path in new[] { Path.Combine(local,"Programs","Codex","Codex.exe"),Path.Combine(local,"Programs","Codex","app","Codex.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Codex","Codex.exe") })
                if(File.Exists(path) && IsDesktopDirectory(Path.GetDirectoryName(path))) return path;
            foreach(string name in new[] { "Codex","ChatGPT" }) foreach(var process in Process.GetProcessesByName(name)) using(process) {
                try { string path=process.MainModule.FileName;if(process.SessionId==Process.GetCurrentProcess().SessionId && IsDesktopDirectory(Path.GetDirectoryName(path))) return path; } catch(System.ComponentModel.Win32Exception) { }
            }
            throw new IOException("未找到 Codex 桌面程序，请在设置中选择 Codex 程序路径。");
        }
        static bool IsDesktopDirectory(string directory) {
            return File.Exists(Path.Combine(directory,"chrome_elf.dll")) && Directory.Exists(Path.Combine(directory,"resources")) ||
                File.Exists(Path.Combine(directory,"app","chrome_elf.dll")) && Directory.Exists(Path.Combine(directory,"app","resources"));
        }
        public static bool IsDesktopProcess(string path,string directory) {
            string file=Path.GetFileName(path);
            return (file.Equals("Codex.exe",StringComparison.OrdinalIgnoreCase) || file.Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);
        }
        List<Process> Running() {
            var result=new List<Process>();int session=Process.GetCurrentProcess().SessionId;
            foreach(string name in new[] { "Codex","ChatGPT" }) foreach(var process in Process.GetProcessesByName(name)) {
                try { if(process.SessionId==session && IsDesktopProcess(process.MainModule.FileName,desktopDirectory)) { result.Add(process);continue; } }
                catch(System.ComponentModel.Win32Exception) { } catch(InvalidOperationException) { }
                process.Dispose();
            }
            return result;
        }
        public void Restart() {
            if(launcher==null) throw new InvalidOperationException("尚未检查 Codex 桌面程序路径。");
            var processes=Running();
            try {
                foreach(var process in processes) try { if(!process.HasExited && process.MainWindowHandle!=IntPtr.Zero) process.CloseMainWindow(); } catch(InvalidOperationException) { }
                var deadline=DateTime.UtcNow.AddSeconds(8);
                while(DateTime.UtcNow<deadline && processes.Any(p=>!p.HasExited)) Thread.Sleep(200);
                // Restrict termination to these exact desktop executable paths in this
                // Windows session. Never match all codex*, CLI or other ChatGPT installs.
                using(var current=Process.GetCurrentProcess()) foreach(var process in processes) {
                    if(process.Id==current.Id) throw new IOException("不能重启当前看板进程。");
                    try { if(!process.HasExited) { process.Kill();if(!process.WaitForExit(5000)) throw new IOException("Codex 桌面进程未能退出。"); } } catch(InvalidOperationException) { }
                }
            } finally { foreach(var process in processes) process.Dispose(); }
            using(var started=Process.Start(new ProcessStartInfo(launcher) { UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(launcher) })) { }
            var until=DateTime.UtcNow.AddSeconds(15);
            do {
                var running=Running();bool found=running.Count>0;foreach(var process in running) process.Dispose();
                if(found) return;
                Thread.Sleep(300);
            } while(DateTime.UtcNow<until);
            throw new IOException("已发出启动请求，但未检测到 Codex 桌面进程，请检查程序路径或手动打开 Codex。");
        }
    }
    public sealed class AccountSwitchController {
        readonly Func<IAccountProfiles> profiles;
        readonly Func<ICodexDesktop> desktop;
        bool operationMessage;
        public string CurrentAccountId="",BusyAccountId="",Message="正在读取 Codex 连接状态…";
        public bool Busy,Loading,Ready,HasError;
        public HashSet<string> Candidates=new HashSet<string>();
        public event Action Changed;
        public AccountSwitchController(Func<IAccountProfiles> profiles,Func<ICodexDesktop> desktop) { this.profiles=profiles;this.desktop=desktop; }
        void Notify() { if(Changed!=null) Changed(); }
        void Accept(DirectProfile profile) { CurrentAccountId=profile.Mode=="direct_account"?profile.AccountId:""; }
        public void Reset() { if(Busy || Loading) return;operationMessage=false;Ready=false;HasError=false;CurrentAccountId="";Candidates.Clear();Message="正在读取连接状态…";Notify(); }
        public async Task Refresh() {
            if(Busy || Loading) return;
            Loading=true;Notify();
            try {
                var client=profiles();
                var profile=await Task.Run(()=>client.Get());var candidates=await Task.Run(()=>client.Candidates());
                Accept(profile);Candidates=candidates;Ready=true;
                if(!operationMessage) { HasError=false;Message=profile.Mode=="direct_account"?"当前通过所选账号直接连接 OpenAI":"切换后将直接连接 OpenAI，并自动重启 Codex"; }
            } catch(Exception e) { Ready=false;CurrentAccountId="";Message=e.Message;HasError=true; }
            finally { Loading=false;Notify(); }
        }
        public async Task Switch(AccountUsage account) {
            if(Busy || Loading) return;
            if(account==null || account.Status!="active") { HasError=true;Message="此账号不可用，请在账号管理中重新登录或启用。";Notify();return; }
            Busy=true;BusyAccountId=account.Id;operationMessage=true;HasError=false;Message="正在检查并切换账号…";Notify();
            IAccountProfiles client=null;bool applied=false,reconcile=false;
            try {
                client=profiles();var runtime=desktop();
                await Task.Run(()=>runtime.Prepare());
                var profile=await Task.Run(()=>client.Get());
                var candidates=await Task.Run(()=>client.Candidates());
                if(!candidates.Contains(account.Id)) throw new IOException("当前账号来源中没有这个可用账号，请刷新后重新登录。");
                var result=await Task.Run(()=>client.Apply(account.Id,profile.Home));
                if(result.Mode!="direct_account" || result.AccountId!=account.Id || !string.Equals(Path.GetFullPath(result.Home).TrimEnd('\\','/'),Path.GetFullPath(profile.Home).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase)) throw new IOException("账号服务尚未确认目标账号的直连状态，请刷新后重试。");
                Accept(result);Candidates=candidates;Ready=true;applied=true;
                Message="账号已切换，正在重启 Codex…";Notify();
                await Task.Run(()=>runtime.Restart());
                var completion=client as IProfileCompletion;if(completion!=null) await Task.Run(()=>completion.Confirm(account.Id));
                Message="已切换到 "+(string.IsNullOrWhiteSpace(account.Name)?"所选账号":account.Name)+"，Codex 已重新启动";
            } catch(Exception e) {
                HasError=true;Message=(applied?"账号已切换，但重启失败。可点击重新应用并重启。":"切换未完成：")+e.Message;
                reconcile=!applied && client!=null;
            }
            if(reconcile) try { Accept(await Task.Run(()=>client.Get())); } catch { Ready=false;CurrentAccountId=""; }
            Busy=false;BusyAccountId="";Notify();
        }
    }
}
