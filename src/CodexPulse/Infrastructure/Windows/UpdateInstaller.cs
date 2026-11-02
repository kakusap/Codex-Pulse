using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace CodexPulse {
    public sealed class UpdatePlan {
        public int ParentId;
        public long ParentStartedUtc;
        public string InstallExe,OriginalHash,ExecutableHash,IconHash,Version;
        public string[] RestartArgs;
    }
    public static class UpdateInstaller {
        public static string Quote(string value) {
            var result=new StringBuilder("\"");int slashes=0;
            foreach(char c in value) {
                if(c=='\\') {slashes++;continue;}
                if(c=='\"') {result.Append('\\',slashes*2+1);result.Append(c);}
                else {result.Append('\\',slashes);result.Append(c);}
                slashes=0;
            }
            result.Append('\\',slashes*2);return result.Append('"').ToString();
        }
        internal static string[] RestartArguments(string[] args) {
            var result=new System.Collections.Generic.List<string>();
            for(int i=0;i<args.Length;i++) if(new[] {"--settings","--pulse-db","--codex-home"}.Contains(args[i]) && i+1<args.Length) {result.Add(args[i]);result.Add(args[++i]);}
            return result.ToArray();
        }
        public static void Launch(PreparedUpdate update) {
            string installed=Process.GetCurrentProcess().MainModule.FileName;
            // Check write access while the current application can still report errors.
            string probe=Path.Combine(Path.GetDirectoryName(installed),".pulse-update-"+Guid.NewGuid().ToString("N"));
            using(File.Create(probe)) {} File.Delete(probe);
            string helper=Path.Combine(update.Directory,"update-helper.exe");
            File.Copy(installed,helper,false);
            var plan=new UpdatePlan {ParentId=Process.GetCurrentProcess().Id,ParentStartedUtc=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                InstallExe=installed,OriginalHash=AppUpdater.Hash(installed),ExecutableHash=AppUpdater.Hash(update.Executable),IconHash=AppUpdater.Hash(update.Icon),Version=update.Version,
                RestartArgs=RestartArguments(Environment.GetCommandLineArgs().Skip(1).ToArray())};
            string path=Path.Combine(update.Directory,"update-plan.json");
            File.WriteAllText(path,Json.New().Serialize(plan),new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo {FileName=helper,Arguments="--apply-update "+Quote(path),WorkingDirectory=update.Directory,UseShellExecute=false,CreateNoWindow=true});
        }
        internal static void Replace(string source,string destination) {
            string temporary=destination+".update-"+Guid.NewGuid().ToString("N");
            try {
                File.Copy(source,temporary,false);
                for(int attempt=0;;attempt++) {
                    try {if(File.Exists(destination)) File.Replace(temporary,destination,null);else File.Move(temporary,destination);break;}
                    catch(IOException) {if(attempt>=49) throw;Thread.Sleep(100);}
                }
            } finally {if(File.Exists(temporary)) File.Delete(temporary);}
        }
        // Only these two application files participate in the transaction. Runtime
        // databases, settings, credentials, docs and startup registry are untouched.
        internal static void InstallFiles(string directory,string installed,Action<int> beforeReplace=null) {
            string icon=Path.Combine(Path.GetDirectoryName(installed),"pulse.ico");
            string backup=Path.Combine(directory,"backup");Directory.CreateDirectory(backup);
            bool hadIcon=File.Exists(icon),exeChanged=false,iconChanged=false;
            File.Copy(installed,Path.Combine(backup,"CodexPulse.exe"),true);
            if(hadIcon) File.Copy(icon,Path.Combine(backup,"pulse.ico"),true);
            try {
                if(beforeReplace!=null) beforeReplace(0);
                Replace(Path.Combine(directory,"CodexPulse.exe"),installed);exeChanged=true;
                if(beforeReplace!=null) beforeReplace(1);
                Replace(Path.Combine(directory,"pulse.ico"),icon);iconChanged=true;
            } catch {
                if(exeChanged) Replace(Path.Combine(backup,"CodexPulse.exe"),installed);
                if(iconChanged) {if(hadIcon) Replace(Path.Combine(backup,"pulse.ico"),icon);else File.Delete(icon);}
                throw;
            }
        }
        internal static void Validate(UpdatePlan plan,string directory) {
            Version version;
            if(plan==null || plan.ParentId<=0 || !Path.IsPathRooted(plan.InstallExe??"") || !string.Equals(Path.GetExtension(plan.InstallExe),".exe",StringComparison.OrdinalIgnoreCase) || !Version.TryParse(plan.Version,out version)) throw new IOException("更新计划无效。");
            string executable=Path.Combine(directory,"CodexPulse.exe"),icon=Path.Combine(directory,"pulse.ico");
            if(string.Equals(Path.GetDirectoryName(Path.GetFullPath(plan.InstallExe)),Path.GetFullPath(directory).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) throw new IOException("更新目录与安装目录不能相同。");
            var assembly=AssemblyName.GetAssemblyName(executable);
            if(assembly.Name!="CodexPulse" || !AppUpdater.SameVersion(assembly.Version,version) || AppUpdater.Hash(executable)!=plan.ExecutableHash || AppUpdater.Hash(icon)!=plan.IconHash) throw new IOException("待安装程序的版本或校验值发生变化。");
            if(AppUpdater.Hash(plan.InstallExe)!=plan.OriginalHash) throw new IOException("已安装程序已发生变化，请重新检查更新。");
        }
        static void Restart(UpdatePlan plan) {
            Process.Start(new ProcessStartInfo {FileName=plan.InstallExe,Arguments=string.Join(" ",(plan.RestartArgs??new string[0]).Select(Quote)),WorkingDirectory=Path.GetDirectoryName(plan.InstallExe),UseShellExecute=true});
        }
        public static int Run(string planPath) {
            string directory=AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            bool parentExited=false,installed=false;UpdatePlan plan=null;
            try {
                if(!string.Equals(Path.GetFullPath(planPath),Path.Combine(directory,"update-plan.json"),StringComparison.OrdinalIgnoreCase)) throw new IOException("更新计划不在辅助程序目录。");
                plan=Json.New().Deserialize<UpdatePlan>(File.ReadAllText(planPath));Validate(plan,directory);
                Process parent=null;
                try {parent=Process.GetProcessById(plan.ParentId);} catch(ArgumentException) {}
                if(parent!=null) using(parent) {
                    if(parent.StartTime.ToUniversalTime().Ticks!=plan.ParentStartedUtc || !string.Equals(parent.MainModule.FileName,plan.InstallExe,StringComparison.OrdinalIgnoreCase)) throw new IOException("更新进程身份不匹配。");
                    if(!parent.WaitForExit(120000)) throw new IOException("旧程序尚未退出，未替换程序文件。");
                }
                parentExited=true;Validate(plan,directory);InstallFiles(directory,plan.InstallExe);installed=true;
                try {Restart(plan);} catch {
                    // A failed restart restores the last working application.
                    string backup=Path.Combine(directory,"backup");
                    Replace(Path.Combine(backup,"CodexPulse.exe"),plan.InstallExe);
                    if(File.Exists(Path.Combine(backup,"pulse.ico"))) Replace(Path.Combine(backup,"pulse.ico"),Path.Combine(Path.GetDirectoryName(plan.InstallExe),"pulse.ico"));
                    installed=false;throw;
                }
                File.WriteAllText(Path.Combine(directory,"update-result.txt"),"success "+plan.Version);return 0;
            } catch(Exception error) {
                try {File.WriteAllText(Path.Combine(directory,"update-result.txt"),"failed: "+error.Message);} catch {}
                if(parentExited && !installed && plan!=null) try {Restart(plan);} catch {}
                System.Windows.MessageBox.Show("更新未完成："+error.Message+"\n原账号和设置保留。请重试或从 GitHub 发布页手动下载。","Codex Pulse 更新");return 1;
            }
        }
    }
}
