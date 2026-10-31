using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexPulse {
    public sealed class ProfileFiles {
        readonly string home,journalRoot;
        readonly string journal;
        public ProfileFiles(Settings settings) {
            home=Home(settings);journalRoot=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.PulseDb)),"profile-backups");
            journal=Path.Combine(journalRoot,PulseStore.SourceId(home)+".pending");
        }
        public static string Home(Settings settings) {
            string path=settings.CodexHome;
            if(string.IsNullOrWhiteSpace(path)) path=Environment.GetEnvironmentVariable("CODEX_HOME");
            if(string.IsNullOrWhiteSpace(path)) path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
            path=Path.GetFullPath(path);
            if(path.TrimEnd('\\')==Path.GetPathRoot(path).TrimEnd('\\')) throw new IOException("Codex 账号目录不能是磁盘根目录。");
            return path;
        }
        public string DirectoryPath { get { return home; } }
        public AccountCredential Current() {
            string path=Path.Combine(home,"auth.json");if(!File.Exists(path)) return null;
            try { return AccountCredential.Parse(File.ReadAllText(path)); } catch { return null; }
        }
        public byte[] Config() { string path=Path.Combine(home,"config.toml");return File.Exists(path)?File.ReadAllBytes(path):null; }
        public static string Hash(byte[] data) { if(data==null) return "missing";using(var sha=SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(data)); }
        static byte[] ReadOptional(string path) { return File.Exists(path)?File.ReadAllBytes(path):null; }
        static byte[] Decode(string value) { return value=="missing"?null:Convert.FromBase64String(value); }
        static string Encode(byte[] value) { return value==null?"missing":Convert.ToBase64String(value); }
        internal static void WriteAtomic(string path,byte[] bytes) {
            string temporary=path+".pulse-"+Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temporary,bytes);if(File.Exists(path)) File.Replace(temporary,path,null);else File.Move(temporary,path); }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        void SaveJournal(Dictionary<string,object> value) {
            Directory.CreateDirectory(journalRoot);byte[] plain=Encoding.UTF8.GetBytes(Json.New().Serialize(value));
            try { WriteAtomic(journal,PulseStore.Protect(plain)); } finally { Array.Clear(plain,0,plain.Length); }
        }
        Dictionary<string,object> LoadJournal() {
            byte[] plain=PulseStore.Unprotect(File.ReadAllBytes(journal));try { return Json.New().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(plain)); } finally { Array.Clear(plain,0,plain.Length); }
        }
        public void Recover() {
            if(!File.Exists(journal)) return;var record=LoadJournal();
            if(Json.S(record,"home")!=home) throw new IOException("恢复日志不属于当前 Codex 目录，未应用恢复。");
            if(Json.S(record,"stage")=="applied") { Archive();return; }
            try { Restore(record); } finally { Archive(); }
        }
        void Archive() { File.Move(journal,Path.Combine(journalRoot,PulseStore.SourceId(home)+"-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".backup")); }
        void Restore(Dictionary<string,object> record) {
            bool conflict=false;
            foreach(string key in new[] { "auth","config" }) {
                string path=Path.Combine(home,key=="auth"?"auth.json":"config.toml");byte[] before=Decode(Json.S(record,"before_"+key));
                string hash=Hash(ReadOptional(path));
                if(hash!=Hash(before) && hash!=Json.S(record,"after_"+key)) { conflict=true;continue; }
                if(before==null) { if(File.Exists(path)) File.Delete(path); } else WriteAtomic(path,before);
            }
            if(conflict) throw new IOException("已恢复本次写入的文件；其他程序的新修改保持不变。加密备份已保留。");
        }
        public void Apply(AccountCredential credential,byte[] originalConfig,byte[] config,Action afterConfig) {
            Recover();Directory.CreateDirectory(home);
            string authPath=Path.Combine(home,"auth.json"),configPath=Path.Combine(home,"config.toml");
            byte[] beforeAuth=ReadOptional(authPath),beforeConfig=ReadOptional(configPath),auth=Encoding.UTF8.GetBytes(credential.JsonValue(true));
            if(Hash(originalConfig)!=Hash(beforeConfig)) throw new IOException("Codex 配置在预检查后发生变化，请重试。");
            var record=new Dictionary<string,object> { {"home",home},{"stage","prepared"},{"before_auth",Encode(beforeAuth)},{"before_config",Encode(beforeConfig)},{"after_auth",Hash(auth)},{"after_config",Hash(config)} };
            SaveJournal(record);
            try {
                if(Hash(ReadOptional(authPath))!=Hash(beforeAuth)) throw new IOException("Codex 登录状态发生变化，请重试。");
                WriteAtomic(configPath,config);if(afterConfig!=null) afterConfig();
                if(Hash(ReadOptional(authPath))!=Hash(beforeAuth)) throw new IOException("Codex 在切换期间更新了凭据，请重试。");
                WriteAtomic(authPath,auth);
                var current=Current();if(current==null || current.Identity!=credential.Identity) throw new IOException("写入后的账号校验失败。");
                record["stage"]="applied";SaveJournal(record);Archive();
            } catch {
                bool restored=true;try { Restore(record); } catch { restored=false; }
                Archive();if(!restored) throw new IOException("账号切换未完成；已恢复本次写入，保留其他程序的新修改和加密备份，请刷新后重试。");
                throw;
            } finally { Array.Clear(auth,0,auth.Length); }
        }
        public void RestoreLatest() {
            Recover();if(!Directory.Exists(journalRoot)) throw new IOException("没有可恢复的备份。");
            string latest=Directory.GetFiles(journalRoot,PulseStore.SourceId(home)+"-*.backup").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();if(latest==null) throw new IOException("没有可恢复的备份。");
            byte[] plain=PulseStore.Unprotect(File.ReadAllBytes(latest));Dictionary<string,object> record;
            try { record=Json.New().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(plain)); } finally { Array.Clear(plain,0,plain.Length); }
            // Backups are per home. Store explicit home identity before restoring.
            if(Json.S(record,"home")!=home) throw new IOException("此备份不属于当前 Codex 目录，不能自动恢复。");
            Restore(record);
        }
    }
}
