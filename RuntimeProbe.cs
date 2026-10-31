using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodexPulse {
    public sealed class RuntimeProbeReport {
        public string Version="",RuntimePath="",Error="";
        public long CheckedAt=DataReader.Now;
        public bool LoginSchema,DeviceLoginSchema,AccountReadSchema,RateLimitsSchema,UsageSchema;
        public bool IsolatedHomeA,IsolatedHomeB;
        public string Credentials="file/keyring/auto/ephemeral：需在 V0.3 对目标账号实际验证；本次不读取现用登录文件。";
        public string LiveLogin="未执行真实登录；浏览器登录、续期与真实多账号查询留待 V0.3 验证。";
        public string RateLimitCall="未执行",UsageCall="未执行";
        public string Display() {
            return "Codex "+Version+"\n"+(string.IsNullOrEmpty(Error)?"协议检查完成":"检查未完成："+Error)+"\n"+
                "协议声明：登录 "+Flag(LoginSchema)+"；设备码 "+Flag(DeviceLoginSchema)+"；账号读取 "+Flag(AccountReadSchema)+"；额度 "+Flag(RateLimitsSchema)+"；账号用量 "+Flag(UsageSchema)+"。\n"+
                "两个文件凭据空白环境隔离检查："+(IsolatedHomeA && IsolatedHomeB?"通过（均未继承当前账号）":"未通过或未完成")+"。\n"+
                "额度调用："+RateLimitCall+"；用量调用："+UsageCall+"。\n"+LiveLogin;
        }
        static string Flag(bool supported) { return supported?"支持":"未声明"; }
    }
    public static class RuntimeProbe {
        static ProcessStartInfo StartInfo(string runtime,string arguments,string home) {
            var info=new ProcessStartInfo(runtime,arguments) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=home };
            info.EnvironmentVariables["CODEX_HOME"]=home;
            foreach(string key in new[] { "OPENAI_API_KEY","CODEX_API_KEY","OPENAI_ACCESS_TOKEN","ACCESS_TOKEN","CODEX_AUTH_JSON","OPENAI_IDENTITY_TOKEN_FILE","OPENAI_FEDERATION_RULE_ID" }) info.EnvironmentVariables.Remove(key);
            return info;
        }
        static string Command(string runtime,string args,string home,int timeout) {
            using(var process=Process.Start(StartInfo(runtime,args,home))) {
                var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(timeout)) { process.Kill();process.WaitForExit();throw new IOException("Codex 协议生成超时。"); }
                Task.WaitAll(output,error);
                if(process.ExitCode!=0) throw new IOException("Codex 不支持本次协议检查或运行失败。");
                return output.Result;
            }
        }
        public static string ResolveRuntime(Settings settings) {
            string launcher=CodexDesktop.ResolveLauncher(settings.CodexExe),root=Path.GetDirectoryName(launcher);
            foreach(string candidate in new[] { Path.Combine(root,"app","resources","codex.exe"),Path.Combine(root,"resources","codex.exe") }) if(File.Exists(candidate)) return candidate;
            throw new IOException("未找到 Codex 桌面程序附带的命令行运行时。");
        }
        public static RuntimeProbeReport Check(Settings settings,string workRoot) {
            string root=Path.Combine(Path.GetFullPath(workRoot),"probe-"+Guid.NewGuid().ToString("N"));
            string homeA=Path.Combine(root,"home-a"),homeB=Path.Combine(root,"home-b"),schema=Path.Combine(root,"schema");
            Directory.CreateDirectory(homeA);Directory.CreateDirectory(homeB);
            var report=new RuntimeProbeReport();
            try {
                string runtime=ResolveRuntime(settings);report.RuntimePath=runtime;
                report.Version=Command(runtime,"--version",homeA,10000).Trim();
                Command(runtime,"app-server generate-json-schema --experimental --out \""+schema+"\"",homeA,30000);
                // Schemas contain no credentials. Inspect each file separately to
                // avoid exceeding the serializer size limit on newer runtimes.
                string schemaText=string.Join("\n",Directory.GetFiles(schema,"*.json",SearchOption.AllDirectories).Select(File.ReadAllText));
                report.LoginSchema=schemaText.Contains("\"account/login/start\"");report.DeviceLoginSchema=report.LoginSchema && schemaText.Contains("\"chatgptDeviceCode\"");
                report.AccountReadSchema=schemaText.Contains("\"account/read\"");report.RateLimitsSchema=schemaText.Contains("\"account/rateLimits/read\"");report.UsageSchema=schemaText.Contains("\"account/usage/read\"");
                report.IsolatedHomeA=Isolated(runtime,homeA,report);
                report.IsolatedHomeB=Isolated(runtime,homeB,null);
            } catch { report.Error="运行时检查失败或超时；请检查 Codex 安装。未修改当前账号。"; }
            // Store only the summarized report, never raw auth RPC replies.
            Directory.CreateDirectory(workRoot);File.WriteAllText(Path.Combine(workRoot,"runtime-report.json"),Json.New().Serialize(report),new UTF8Encoding(false));
            return report;
        }
        static bool Isolated(string runtime,string home,RuntimeProbeReport report) {
            using(var process=Process.Start(StartInfo(runtime,"app-server --listen stdio:// -c cli_auth_credentials_store=\"file\"",home))) {
                var stderr=process.StandardError.ReadToEndAsync();
                try {
                    Send(process,new { id=1,method="initialize",@params=new { clientInfo=new { name="codex_pulse_probe",version="0.2.0" },capabilities=new { experimentalApi=true } } });
                    Reply(process,1);Send(process,new { method="initialized" });
                    Send(process,new { id=2,method="account/read",@params=new { refreshToken=false } });
                    var account=Reply(process,2);object result,value;
                    var body=account.TryGetValue("result",out result)?result as Dictionary<string,object>:null;
                    bool isolated=body!=null && body.TryGetValue("account",out value) && value==null;
                    if(report!=null) {
                        Send(process,new { id=3,method="account/rateLimits/read",@params=new {} });report.RateLimitCall=CallState(Reply(process,3));
                        Send(process,new { id=4,method="account/usage/read",@params=new {} });report.UsageCall=CallState(Reply(process,4));
                    }
                    return isolated;
                } finally {
                    process.StandardInput.Close();if(!process.WaitForExit(1500)) { process.Kill();process.WaitForExit(3000); }
                    if(stderr.IsCompleted) { var ignored=stderr.Result; }
                }
            }
        }
        static void Send(Process process,object message) { process.StandardInput.WriteLine(Json.New().Serialize(message));process.StandardInput.Flush(); }
        static Dictionary<string,object> Reply(Process process,int id) {
            var deadline=DateTime.UtcNow.AddSeconds(12);
            while(DateTime.UtcNow<deadline) {
                var line=process.StandardOutput.ReadLineAsync();
                int remaining=Math.Max(1,(int)(deadline-DateTime.UtcNow).TotalMilliseconds);
                if(!line.Wait(remaining) || line.Result==null) throw new IOException("Codex app-server 未响应。");
                Dictionary<string,object> message;
                try { message=Json.New().Deserialize<Dictionary<string,object>>(line.Result); } catch { continue; }
                if(message!=null && Json.S(message,"id")==id.ToString()) return message;
            }
            throw new IOException("Codex app-server 超时。");
        }
        static string CallState(Dictionary<string,object> response) {
            object error;
            if(!response.TryGetValue("error",out error) || error==null) return "已响应（空白账号环境）";
            var details=error as Dictionary<string,object>;
            if(details!=null && Json.N(details,"code")==-32601) return "此运行时未实现";
            return "已返回错误（空白环境未登录；真实账号查询尚未验证）";
        }
    }
}
