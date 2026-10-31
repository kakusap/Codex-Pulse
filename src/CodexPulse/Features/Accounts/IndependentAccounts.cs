using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexPulse {
    public interface IProfileCompletion { void Confirm(string id); }
    public sealed class IndependentAccounts : IAccountProfiles,IProfileCompletion {
        readonly Settings settings;
        readonly IndependentRuntime runtime;
        public IndependentAccounts(Settings settings) : this(settings,null) { }
        public IndependentAccounts(Settings settings,Func<string,ICodexRpc> sessions) { this.settings=settings;runtime=new IndependentRuntime(settings,sessions); }
        public DirectProfile Get() {
            using(var lease=new AccountLease(settings.PulseDb)) {
                var files=new ProfileFiles(settings);files.Recover();var credential=files.Current();
                string id=credential==null?"":IndependentStore.Accounts(settings.PulseDb).Where(a=>a.AccountIdentity==credential.Identity).Select(a=>a.Id).FirstOrDefault()??"";
                return new DirectProfile { Home=files.DirectoryPath,Mode=string.IsNullOrEmpty(id) || !runtime.IsDirect(files.Config())?"unmanaged":"direct_account",AccountId=id };
            }
        }
        public HashSet<string> Candidates() { return new HashSet<string>(IndependentStore.Accounts(settings.PulseDb).Where(a=>a.Status=="active").Select(a=>a.Id)); }
        public int CheckSwitchCompatibility() {
            using(var lease=new AccountLease(settings.PulseDb)) {
                var files=new ProfileFiles(settings);int count=0;
                foreach(var account in IndependentStore.Accounts(settings.PulseDb).Where(a=>a.Status=="active")) {
                    runtime.DirectConfig(files.Config(),IndependentStore.Credential(settings.PulseDb,account.Id));count++;
                }
                return count;
            }
        }
        AccountCredential Fresh(AccountUsage account,bool renew) {
            var stored=IndependentStore.Credential(settings.PulseDb,account.Id);var live=new ProfileFiles(settings).Current();
            bool active=live!=null && live.Identity==stored.Identity;
            if(active) { stored=live;IndependentStore.Save(settings.PulseDb,account,stored); }
            if(stored.Expires<=DataReader.Now+120 && renew) {
                if(active) {
                    if(stored.Expires>DataReader.Now) return stored;
                    throw new AccountOperationException("refresh","当前账号由 Codex 负责续期；等待其更新登录或重新登录，未并发刷新凭据。");
                }
                CheckFileOwner();
                stored=runtime.Renew(stored);IndependentStore.Save(settings.PulseDb,account,stored);
            }
            return stored;
        }
        void CheckFileOwner() {
            string mode=Json.S(runtime.ReadConfig(new ProfileFiles(settings).Config()),"cli_auth_credentials_store");
            if(mode=="keyring" || mode=="auto" || mode=="ephemeral") throw new AccountOperationException("refresh","当前 Codex 使用系统或内存凭据，无法安全确认续期归属。请在独立账号管理中重新登录，再执行首次切换；不会读取或清除系统凭据。");
        }
        public Snapshot Read() { return Refresh(false); }
        public Snapshot Refresh(bool force) {
            IndependentStore.Ensure(settings.PulseDb);
            Snapshot snapshot;
            try { snapshot=PulseStore.ReadHistory(settings.PulseDb,settings.PulseSourceId); } catch { snapshot=new Snapshot(); }
            snapshot.Accounts.Clear();snapshot.SourceKind="independent";snapshot.SourceNote="独立账号额度与用量";
            using(var lease=new AccountLease(settings.PulseDb)) {
                var accounts=IndependentStore.Accounts(settings.PulseDb);
                foreach(var account in accounts) {
                    if(account.Status=="removed" || (!force && account.Status=="needs_login")) { snapshot.Accounts.Add(account);continue; }
                    bool quotaDue=force || account.RetryAfter<=DataReader.Now;
                    bool usageDue=force || UsageStore.OfficialState(settings.PulseDb,account.AccountIdentity).NextAttempt<=DataReader.Now;
                    AccountCredential credential=null;
                    if(quotaDue) try {
                        credential=Fresh(account,true);
                        Dictionary<string,object> quota;
                        try { quota=runtime.Quota(credential); }
                        catch(AccountOperationException e) {
                            var live=new ProfileFiles(settings).Current();
                            if(e.Kind!="login" || (live!=null && live.Identity==credential.Identity)) throw;
                            CheckFileOwner();credential=runtime.Renew(credential);IndependentStore.Save(settings.PulseDb,account,credential);quota=runtime.Quota(credential);
                        }
                        IndependentRuntime.ApplyQuota(account,quota);account.Status="active";account.CredentialState="verified";account.RefreshError="";account.CheckedAt=DataReader.Now;
                        account.RetryAfter=DataReader.Now+Math.Max(60,settings.AccountRefreshSeconds);
                    } catch(Exception e) {
                        var error=e as AccountOperationException;string kind=error==null?"runtime":error.Kind;
                        account.RefreshError=error==null?"账号查询未完成，请检查运行时或重新登录。":error.Message;
                        account.RetryAfter=DataReader.Now+(kind=="limited"?300:120);
                        if(kind=="login") { account.Status="needs_login";account.CredentialState="needs_login"; }
                        // Captured, percentages, windows and reset times remain the
                        // last successful snapshot on every failure path.
                    }
                    if(usageDue && account.Status=="active") try {
                        if(credential==null) credential=Fresh(account,true);
                        UsageStore.SaveOfficial(settings.PulseDb,account.AccountIdentity,runtime.Usage(credential));
                    } catch(Exception e) {
                        var operation=e as AccountOperationException;
                        UsageStore.OfficialFailure(settings.PulseDb,account.AccountIdentity,operation==null?"官方账号用量暂不可用，请稍后重试或选择本机记录。":operation.Message);
                    }
                    IndependentStore.SaveStatus(settings.PulseDb,account);snapshot.Accounts.Add(account);
                }
            }
            try { LocalUsage.Scan(settings,force); } catch { snapshot.UsageNotice="本机用量采集未完成，已有数据保留。"; }
            snapshot.UsageViews=UsageStore.Read(settings,snapshot);
            return snapshot;
        }
        public DirectProfile Apply(string accountId,string home) {
            using(var lease=new AccountLease(settings.PulseDb)) {
                var files=new ProfileFiles(settings);if(!string.Equals(files.DirectoryPath,Path.GetFullPath(home),StringComparison.OrdinalIgnoreCase)) throw new IOException("目标 Codex 目录发生变化。");
                var account=IndependentStore.Accounts(settings.PulseDb).FirstOrDefault(a=>a.Id==accountId && a.Status=="active");if(account==null) throw new IOException("此独立账号不可用，请先重新登录。");
                var credential=Fresh(account,true);IndependentRuntime.ApplyQuota(account,runtime.Quota(credential));IndependentStore.SaveStatus(settings.PulseDb,account);
                byte[] original=files.Config(),config=runtime.DirectConfig(original,credential);
                files.Apply(credential,original,config,null);
                return new DirectProfile { Home=home,Mode="direct_account",AccountId=accountId };
            }
        }
        public void Confirm(string id) {
            var profile=Get();if(profile.AccountId!=id) throw new IOException("重启后的 Codex 账号与目标不一致，请检查目录配置并重新应用。");
        }
    }
}
