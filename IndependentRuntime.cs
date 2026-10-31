using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CodexPulse {
    public sealed class LoginPrompt {
        public string Url,Code;
        public bool Device;
    }
    public sealed class IndependentRuntime {
        readonly Settings settings;
        readonly Func<string,ICodexRpc> sessions;
        public IndependentRuntime(Settings settings,Func<string,ICodexRpc> sessions) { this.settings=settings;this.sessions=sessions??(home=>new CodexRpc(settings,home)); }
        internal string PrivateRoot { get { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.PulseDb)),"private-accounts"); } }
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
        static void Configure(string home) { File.WriteAllText(Path.Combine(home,"config.toml"),"model_provider = \"openai\"\ncli_auth_credentials_store = \"file\"\nforced_login_method = \"chatgpt\"\n",Utf8); }
        internal AccountCredential Renew(AccountCredential credential) {
            using(var home=new PrivateHome(PrivateRoot)) {
                Configure(home.Path);File.WriteAllText(Path.Combine(home.Path,"auth.json"),credential.JsonValue(true),Utf8);
                using(var rpc=sessions(home.Path)) rpc.Call("account/read",new { refreshToken=true },25000);
                var updated=AccountCredential.Parse(File.ReadAllText(Path.Combine(home.Path,"auth.json")));
                if(updated.Identity!=credential.Identity) throw new AccountOperationException("login","续期返回了不同账号，已停止应用。");
                return updated;
            }
        }
        internal Dictionary<string,object> Quota(AccountCredential credential) {
            return ReadMetric(credential,"account/rateLimits/read");
        }
        internal Dictionary<string,object> Usage(AccountCredential credential) { return ReadMetric(credential,"account/usage/read"); }
        Dictionary<string,object> ReadMetric(AccountCredential credential,string method) {
            using(var home=new PrivateHome(PrivateRoot)) {
                Configure(home.Path);
                // Queries never get a refresh token. Only Renew() may rotate one,
                // under the single Pulse lease and only for an inactive identity.
                File.WriteAllText(Path.Combine(home.Path,"auth.json"),credential.JsonValue(false),Utf8);
                using(var rpc=sessions(home.Path)) {
                    var account=AccountCredential.Object(rpc.Call("account/read",new { refreshToken=false },12000),"account");
                    if(Json.S(account,"type")!="chatgpt") throw new AccountOperationException("login","隔离账号未登录，请重新登录。");
                    return rpc.Call(method,new {},20000);
                }
            }
        }
        public AccountUsage Login(string replaceId,bool device,Action<LoginPrompt> show,CancellationToken cancel) {
            using(var lease=new AccountLease(settings.PulseDb)) using(var home=new PrivateHome(PrivateRoot)) {
                Configure(home.Path);
                using(var rpc=sessions(home.Path)) {
                    var result=rpc.Call("account/login/start",device?(object)new { type="chatgptDeviceCode" }:new { type="chatgpt" },20000);
                    string id=Json.S(result,"loginId"),url=Json.S(result,device?"verificationUrl":"authUrl");
                    Uri parsed;if(!Uri.TryCreate(url,UriKind.Absolute,out parsed) || parsed.Scheme!="https" || (parsed.Host!="auth.openai.com" && parsed.Host!="chatgpt.com")) throw new AccountOperationException("login","登录服务返回了未知的登录地址。");
                    show(new LoginPrompt { Url=url,Code=Json.S(result,"userCode"),Device=device });
                    if(!rpc.WaitLogin(id,cancel)) throw new AccountOperationException("cancel","登录已取消或未完成。");
                    var credential=AccountCredential.Parse(File.ReadAllText(Path.Combine(home.Path,"auth.json")));
                    var accounts=IndependentStore.Accounts(settings.PulseDb);
                    var replacing=accounts.FirstOrDefault(a=>a.Id==replaceId);
                    if(replacing!=null && replacing.AccountIdentity!=credential.Identity) throw new AccountOperationException("login","登录的账号与所选账号不同，请用“添加账号”保存新的账号。");
                    var account=replacing??accounts.FirstOrDefault(a=>a.AccountIdentity==credential.Identity)??new AccountUsage { Id=Guid.NewGuid().ToString("N"),Name=string.IsNullOrWhiteSpace(credential.Email)?"新账号":credential.Email };
                    account.Status="active";account.CredentialState="verified";account.CredentialOrigin="independent_login";account.RefreshError="";account.RetryAfter=0;
                    return IndependentStore.Save(settings.PulseDb,account,credential);
                }
            }
        }
        internal byte[] DirectConfig(byte[] existing,AccountCredential credential=null) {
            using(var home=new PrivateHome(PrivateRoot)) {
                // Use the official TOML writer against a staging file, preserving
                // comments, unrelated keys, profiles, projects and provider definitions.
                string candidate=Path.Combine(home.Path,"config.toml");File.WriteAllBytes(candidate,existing??new byte[0]);
                using(var rpc=sessions(home.Path)) {
                    rpc.Call("config/batchWrite",new { filePath=candidate,reloadUserConfig=false,edits=new object[] {
                        Edit("model_provider","openai"),Edit("cli_auth_credentials_store","file"),Edit("forced_login_method","chatgpt"),Edit("model_catalog_json",null),Edit("chatgpt_base_url",null)
                    } },15000);
                }
                string profile;
                using(var rpc=sessions(home.Path)) {
                    var config=AccountCredential.Object(rpc.Call("config/read",new { includeLayers=false },12000),"config");profile=Json.S(config,"profile");
                    if(!string.IsNullOrWhiteSpace(profile)) {
                        if(profile.IndexOfAny(new[] { '.','"','\\' })>=0) throw new IOException("当前 Codex profile 名称包含特殊字符，请先在 Codex 中切回默认配置。");
                        rpc.Call("config/batchWrite",new { filePath=Path.Combine(home.Path,"config.toml"),reloadUserConfig=false,edits=new object[] { Edit("profiles."+profile+".model_provider","openai"),Edit("profiles."+profile+".model_catalog_json",null) } },15000);
                    }
                }
                // Parse the final file through a fresh runtime before it can replace
                // the user's file. Enforced workspace restrictions remain untouched.
                if(credential!=null) File.WriteAllText(Path.Combine(home.Path,"auth.json"),credential.JsonValue(false),Utf8);
                using(var rpc=sessions(home.Path)) {
                    var config=AccountCredential.Object(rpc.Call("config/read",new { includeLayers=false },12000),"config");
                    if(Json.S(config,"model_provider")!="openai") throw new IOException("直连配置校验失败，未修改当前配置。");
                    string required=Json.S(config,"forced_chatgpt_workspace_id");
                    if(credential!=null && !string.IsNullOrWhiteSpace(required) && required!=credential.AccountId) throw new IOException("所选账号与配置要求的工作区不符，未修改当前账号。");
                    if(credential!=null && Json.S(AccountCredential.Object(rpc.Call("account/read",new { refreshToken=false },12000),"account"),"type")!="chatgpt") throw new IOException("目标登录与现有配置限制不兼容，未修改当前账号。");
                }
                return File.ReadAllBytes(Path.Combine(home.Path,"config.toml"));
            }
        }
        internal Dictionary<string,object> ReadConfig(byte[] config) {
            using(var home=new PrivateHome(PrivateRoot)) {
                if(config!=null) File.WriteAllBytes(Path.Combine(home.Path,"config.toml"),config);
                using(var rpc=sessions(home.Path)) {
                    return AccountCredential.Object(rpc.Call("config/read",new { includeLayers=false },12000),"config");
                }
            }
        }
        internal bool IsDirect(byte[] config) {
            var value=ReadConfig(config);string provider=Json.S(value,"model_provider"),profile=Json.S(value,"profile");
            if(!string.IsNullOrWhiteSpace(profile)) {
                string selected=Json.S(AccountCredential.Object(AccountCredential.Object(value,"profiles"),profile),"model_provider");if(!string.IsNullOrEmpty(selected)) provider=selected;
            }
            return provider=="" || provider=="openai";
        }
        static object Edit(string key,object value) { return new { keyPath=key,value=value,mergeStrategy="replace" }; }
        internal static void ApplyQuota(AccountUsage account,Dictionary<string,object> response) {
            var limits=AccountCredential.Object(response,"rateLimits");var buckets=AccountCredential.Object(response,"rateLimitsByLimitId");
            if(buckets.ContainsKey("codex")) limits=AccountCredential.Object(buckets,"codex");
            if(limits.Count==0) throw new AccountOperationException("permission","服务未返回可用的 Codex 额度窗口，旧快照已保留。");
            var primary=AccountCredential.Object(limits,"primary");var secondary=AccountCredential.Object(limits,"secondary");
            double pm=Json.N(primary,"windowDurationMins"),sm=Json.N(secondary,"windowDurationMins");
            double? pu=Json.Nullable(primary,"usedPercent"),su=Json.Nullable(secondary,"usedPercent");
            if((pu.HasValue && (pu<0 || pu>100 || pm<=0)) || (su.HasValue && (su<0 || su>100 || sm<=0))) throw new AccountOperationException("runtime","服务返回的额度窗口无效，旧快照已保留。");
            account.PrimaryUsed=pu;account.SecondaryUsed=su;account.PrimaryMinutes=pm;account.SecondaryMinutes=sm;
            account.PrimaryReset=(long)Json.N(primary,"resetsAt");account.SecondaryReset=(long)Json.N(secondary,"resetsAt");account.Captured=DataReader.Now;
        }
    }
}
