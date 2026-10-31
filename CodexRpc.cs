using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexPulse {
    public sealed class AccountOperationException : IOException {
        public readonly string Kind;
        public AccountOperationException(string kind,string message) : base(message) { Kind=kind; }
    }
    public sealed class PrivateHome : IDisposable {
        public readonly string Path;
        readonly string root;
        FileStream lease;
        public PrivateHome(string root) {
            this.root=System.IO.Path.GetFullPath(root);Directory.CreateDirectory(this.root);
            // A crashed owner releases its lock. Only GUID-named homes with our
            // marker are eligible for cleanup; live operations remain untouched.
            foreach(string stale in Directory.GetDirectories(this.root,"account-*")) {
                Guid parsed;
                if(!Guid.TryParseExact(System.IO.Path.GetFileName(stale).Substring(8),"N",out parsed) || (File.GetAttributes(stale)&FileAttributes.ReparsePoint)!=0) continue;
                string marker=System.IO.Path.Combine(stale,".pulse-lock");if(!File.Exists(marker)) continue;
                try { using(var claim=new FileStream(marker,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { }Directory.Delete(stale,true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
            }
            Path=System.IO.Path.Combine(this.root,"account-"+Guid.NewGuid().ToString("N"));
            var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.CreateDirectory(Path,security);
            lease=new FileStream(System.IO.Path.Combine(Path,".pulse-lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        }
        public void Dispose() {
            if(!System.IO.Path.GetFullPath(Path).StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("临时账号目录越界。");
            // Only the newly created, private home is removed. Never the user's CODEX_HOME.
            string auth=System.IO.Path.Combine(Path,"auth.json");
            if(lease!=null) { lease.Dispose();lease=null; }
            for(int attempt=0;attempt<20;attempt++) {
                try { if(File.Exists(auth)) File.Delete(auth);if(Directory.Exists(Path)) Directory.Delete(Path,true);return; }
                catch(IOException) { if(attempt==19) throw;Thread.Sleep(100); }
                catch(UnauthorizedAccessException) { if(attempt==19) throw;Thread.Sleep(100); }
            }
        }
    }
    internal sealed class ChildProcessGroup : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct Basic { public long PerProcessTime,PerJobTime;public uint Flags;public UIntPtr Minimum,Maximum;public uint ActiveLimit;public UIntPtr Affinity;public uint Priority,Scheduling; }
        [StructLayout(LayoutKind.Sequential)] struct Counters { public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic Basic;public Counters Counters;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory; }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes,string name);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int info,ref Extended value,uint length);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        IntPtr handle;
        public ChildProcessGroup(Process process) {
            handle=CreateJobObject(IntPtr.Zero,null);var limits=new Extended();limits.Basic.Flags=0x2000;
            if(handle==IntPtr.Zero || !SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf(typeof(Extended))) || !AssignProcessToJobObject(handle,process.Handle)) {
                Dispose();try { process.Kill(); } catch { }throw new IOException("无法隔离 Codex 子进程，已停止操作。");
            }
        }
        public void Dispose() { if(handle!=IntPtr.Zero) { CloseHandle(handle);handle=IntPtr.Zero; } }
    }
    public interface ICodexRpc : IDisposable {
        Dictionary<string,object> Call(string method,object parameters,int timeout);
        bool WaitLogin(string loginId,CancellationToken cancel);
    }
    public sealed class CodexRpc : ICodexRpc {
        readonly Process process;
        readonly Task<string> stderr;
        readonly ChildProcessGroup group;
        readonly Queue<Dictionary<string,object>> notifications=new Queue<Dictionary<string,object>>();
        int sequence;
        public CodexRpc(Settings settings,string home) {
            var info=new ProcessStartInfo(RuntimeProbe.ResolveRuntime(settings),"app-server --listen stdio://") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=home };
            info.EnvironmentVariables["CODEX_HOME"]=home;
            foreach(string key in new[] { "OPENAI_API_KEY","CODEX_API_KEY","OPENAI_ACCESS_TOKEN","ACCESS_TOKEN","CODEX_AUTH_JSON","OPENAI_IDENTITY_TOKEN_FILE","OPENAI_FEDERATION_RULE_ID" }) info.EnvironmentVariables.Remove(key);
            process=Process.Start(info);group=new ChildProcessGroup(process);stderr=process.StandardError.ReadToEndAsync();
            try { Call("initialize",new { clientInfo=new { name="codex_pulse",title="Codex Pulse",version="0.4.0" },capabilities=new { experimentalApi=true } },12000);Send(new { method="initialized" }); }
            catch { Dispose();throw; }
        }
        void Send(object value) { process.StandardInput.WriteLine(Json.New().Serialize(value));process.StandardInput.Flush(); }
        Dictionary<string,object> Read(int timeout) {
            var read=process.StandardOutput.ReadLineAsync();
            if(!read.Wait(Math.Max(1,timeout))) throw new AccountOperationException("network","Codex 服务响应超时，请稍后重试。");
            if(read.Result==null) throw new AccountOperationException("runtime","Codex 服务已退出，请检查运行时和配置。");
            try { return Json.New().Deserialize<Dictionary<string,object>>(read.Result); } catch { throw new AccountOperationException("runtime","Codex 返回了无效响应。"); }
        }
        static void CheckError(Dictionary<string,object> message) {
            var error=AccountCredential.Object(message,"error");if(error.Count==0) return;
            string detail=Json.S(error,"message").ToLowerInvariant();
            if(detail.Contains("legacy") && detail.Contains("profile")) throw new AccountOperationException("config","此 Codex 版本不支持配置中的旧版 profile 选择。请先在 Codex 中迁移配置；当前账号和配置未改动。");
            if(detail.Contains("429") || detail.Contains("rate limit")) throw new AccountOperationException("limited","服务暂时限流，保留旧快照并稍后重试。");
            if(detail.Contains("401") || detail.Contains("refresh token") || detail.Contains("unauthorized") || detail.Contains("not authenticated")) throw new AccountOperationException("login","登录已失效或需要重新认证，请重新登录此账号。");
            if(detail.Contains("403") || detail.Contains("forbidden")) throw new AccountOperationException("permission","账号无权访问此服务，请检查订阅或工作区权限。");
            if(Json.N(error,"code")==-32601) throw new AccountOperationException("runtime","当前 Codex 运行时不支持此操作，请更新 Codex。");
            throw new AccountOperationException("network","Codex 未能完成请求，请检查网络、登录状态或配置。旧快照已保留。");
        }
        public Dictionary<string,object> Call(string method,object parameters,int timeout) {
            int id=++sequence;Send(new { id=id,method=method,@params=parameters });var deadline=DateTime.UtcNow.AddMilliseconds(timeout);
            while(DateTime.UtcNow<deadline) {
                var message=Read((int)(deadline-DateTime.UtcNow).TotalMilliseconds);
                if(Json.S(message,"id")==id.ToString()) { CheckError(message);return AccountCredential.Object(message,"result"); }
                if(message.ContainsKey("method")) { if(notifications.Count<100) notifications.Enqueue(message); }
            }
            throw new AccountOperationException("network","Codex 服务响应超时。");
        }
        public bool WaitLogin(string loginId,CancellationToken cancel) {
            var deadline=DateTime.UtcNow.AddMinutes(5);Task<string> pending=null;
            while(DateTime.UtcNow<deadline) {
                if(cancel.IsCancellationRequested) {
                    Send(new { id=++sequence,method="account/login/cancel",@params=new { loginId=loginId } });return false;
                }
                Dictionary<string,object> message;
                if(notifications.Count>0) message=notifications.Dequeue();
                else {
                    if(pending==null) pending=process.StandardOutput.ReadLineAsync();
                    if(!pending.Wait(250)) continue;
                    if(pending.Result==null) throw new AccountOperationException("runtime","登录服务已退出。");
                    message=Json.New().Deserialize<Dictionary<string,object>>(pending.Result);pending=null;
                }
                if(Json.S(message,"method")!="account/login/completed") continue;
                var args=AccountCredential.Object(message,"params");if(Json.S(args,"loginId")!=loginId) continue;
                object success;return args.TryGetValue("success",out success) && object.Equals(success,true);
            }
            throw new AccountOperationException("login","登录等待超时，请重新开始登录。");
        }
        public void Dispose() {
            try { process.StandardInput.Close();if(!process.WaitForExit(1500)) { process.Kill();process.WaitForExit(3000); } } catch(InvalidOperationException) { } finally { group.Dispose();process.Dispose(); }
            // Raw stderr and RPC error bodies are never logged; they may carry tokens.
            if(stderr.IsCompleted) { var ignored=stderr.Result; }
        }
    }
}
