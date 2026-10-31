using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CodexPulse {
    public sealed class AccountLease : IDisposable {
        readonly Mutex gate;
        public AccountLease(string database) {
            gate=new Mutex(false,"Local\\CodexPulseAccount-"+PulseStore.SourceId(database));
            bool held=false;try { held=gate.WaitOne(5000); } catch(AbandonedMutexException) { held=true; }
            if(!held) { gate.Dispose();throw new IOException("账号操作正在进行，请稍后重试。"); }
        }
        public void Dispose() { gate.ReleaseMutex();gate.Dispose(); }
    }
    public sealed class AccountCredential {
        internal Dictionary<string,object> Auth;
        public string Identity,AccountId,UserId,Email,Plan;
        public long Expires;
        public static AccountCredential Parse(string json) {
            try {
                var auth=Json.New().Deserialize<Dictionary<string,object>>(json);
                var tokens=Object(auth,"tokens");string access=Json.S(tokens,"access_token"),id=Json.S(tokens,"id_token");
                var claims=Claims(access);var identity=Claims(id);var openai=Object(claims,"https://api.openai.com/auth");
                string accountId=Json.S(tokens,"account_id");if(string.IsNullOrWhiteSpace(accountId)) accountId=Json.S(openai,"chatgpt_account_id");
                string user=Json.S(identity,"sub");if(string.IsNullOrWhiteSpace(user)) user=Json.S(claims,"sub");
                if(string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(id)) throw new IOException();
                string claimedAccount=Json.S(openai,"chatgpt_account_id");
                if(!string.IsNullOrEmpty(claimedAccount) && claimedAccount!=accountId) throw new IOException();
                return new AccountCredential { Auth=auth,AccountId=accountId,UserId=user,Identity=accountId+"|"+user,Email=Json.S(identity,"email"),Plan=Json.S(openai,"chatgpt_plan_type"),Expires=(long)Json.N(claims,"exp") };
            } catch { throw new IOException("账号凭据格式或账号标识不兼容，请重新登录。"); }
        }
        public static Dictionary<string,object> Object(Dictionary<string,object> value,string key) { object result;return value!=null && value.TryGetValue(key,out result) && result is Dictionary<string,object>?(Dictionary<string,object>)result:new Dictionary<string,object>(); }
        static Dictionary<string,object> Claims(string jwt) { string value=jwt.Split('.')[1].Replace('-','+').Replace('_','/');return Json.New().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Convert.FromBase64String(value.PadRight((value.Length+3)/4*4,'=')))); }
        internal string JsonValue(bool refresh) {
            var auth=Json.New().Deserialize<Dictionary<string,object>>(Json.New().Serialize(Auth));auth["OPENAI_API_KEY"]=null;auth["auth_mode"]="chatgpt";
            if(!refresh) { Object(auth,"tokens")["refresh_token"]="";auth["last_refresh"]=DateTime.UtcNow.ToString("o"); }
            return Json.New().Serialize(auth);
        }
    }
    public static class IndependentStore {
        public static void Ensure(string path) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));using(var db=new PulseDb(path)) { } }
        public static List<AccountUsage> Accounts(string path) {
            Ensure(path);using(var db=new ReadOnlyDb(path)) return db.Query("SELECT payload FROM independent_accounts ORDER BY rowid").Select(row=>Json.New().Deserialize<AccountUsage>(Json.S(row,"payload"))).ToList();
        }
        internal static AccountCredential Credential(string path,string id) {
            using(var db=new ReadOnlyDb(path)) {
                var row=db.Query("SELECT protected_auth FROM independent_accounts WHERE id=?",id).FirstOrDefault();if(row==null) throw new IOException("未找到独立账号。");
                byte[] bytes=PulseStore.Unprotect((byte[])row["protected_auth"]);try { return AccountCredential.Parse(Encoding.UTF8.GetString(bytes)); } finally { Array.Clear(bytes,0,bytes.Length); }
            }
        }
        internal static AccountUsage Save(string path,AccountUsage account,AccountCredential credential) {
            byte[] bytes=Encoding.UTF8.GetBytes(credential.JsonValue(true));byte[] encrypted;
            try { encrypted=PulseStore.Protect(bytes); } finally { Array.Clear(bytes,0,bytes.Length); }
            account.AccountIdentity=credential.Identity;account.Plan=credential.Plan;
            using(var db=new PulseDb(path)) db.Query("INSERT OR REPLACE INTO independent_accounts(id,identity,payload,protected_auth) VALUES(?,?,?,?)",account.Id,credential.Identity,Json.New().Serialize(account),encrypted);
            return account;
        }
        internal static void SaveStatus(string path,AccountUsage account) { using(var db=new PulseDb(path)) db.Query("UPDATE independent_accounts SET payload=? WHERE id=?",Json.New().Serialize(account),account.Id); }
        public static void Remove(string path,string id) {
            using(var lease=new AccountLease(path)) using(var db=new PulseDb(path)) db.Query("DELETE FROM independent_accounts WHERE id=?",id);
        }
    }
}
