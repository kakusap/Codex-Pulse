using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexPulse {
    internal sealed class PulseDb : SqliteDatabase {
        internal const int ApplicationId=0x4350554c;
        public PulseDb(string path) : base(path,true) {
            try {
                Query("BEGIN IMMEDIATE");
                long app=Convert.ToInt64(Query("PRAGMA application_id")[0]["application_id"]);
                long version=Convert.ToInt64(Query("PRAGMA user_version")[0]["user_version"]);
                if(app!=ApplicationId && (app!=0 || Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").Count>0))
                    throw new IOException("目标不是 Pulse 数据库，已停止写入。");
                if(version>3) throw new IOException("Pulse 数据库来自更高版本，请使用新版程序。");
                if(version==0) {
                    Query("CREATE TABLE pulse_sources(id TEXT PRIMARY KEY,path TEXT NOT NULL,imported_at INTEGER NOT NULL,summary TEXT NOT NULL)");
                    Query("CREATE TABLE pulse_keys(source_id TEXT NOT NULL,id TEXT NOT NULL,ordinal INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(source_id,id))");
                    Query("CREATE TABLE pulse_daily(source_id TEXT NOT NULL,key_id TEXT NOT NULL,day TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(source_id,key_id,day))");
                    Query("CREATE TABLE pulse_undated(source_id TEXT NOT NULL,key_id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(source_id,key_id))");
                    Query("PRAGMA application_id="+ApplicationId.ToString(CultureInfo.InvariantCulture));Query("PRAGMA user_version=1");
                }
                if(version<2) {
                    Query("CREATE TABLE independent_accounts(id TEXT PRIMARY KEY,identity TEXT NOT NULL UNIQUE,payload TEXT NOT NULL,protected_auth BLOB NOT NULL)");
                    Query("CREATE TABLE independent_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL)");
                    Query("PRAGMA user_version=2");
                }
                if(version<3) {
                    Query("CREATE TABLE usage_official(identity TEXT PRIMARY KEY,payload TEXT NOT NULL)");
                    Query("CREATE TABLE usage_official_days(identity TEXT NOT NULL,day TEXT NOT NULL,tokens INTEGER NOT NULL,seen_at INTEGER NOT NULL,PRIMARY KEY(identity,day))");
                    Query("CREATE TABLE usage_local_files(path_key TEXT PRIMARY KEY,root_key TEXT NOT NULL,state TEXT NOT NULL)");
                    Query("CREATE TABLE usage_local_events(event_key TEXT PRIMARY KEY,root_key TEXT NOT NULL,kind TEXT NOT NULL,thread_key TEXT NOT NULL,identity TEXT NOT NULL,day TEXT NOT NULL,at TEXT NOT NULL,payload TEXT NOT NULL,quality INTEGER NOT NULL)");
                    Query("CREATE INDEX usage_local_thread ON usage_local_events(thread_key,kind)");
                    Query("CREATE INDEX usage_local_day ON usage_local_events(day,identity)");
                    Query("PRAGMA user_version=3");
                }
                Query("COMMIT");
            } catch { try { Query("ROLLBACK"); } catch { }Dispose();throw; }
        }
    }
    public static class PulseStore {
        static readonly byte[] Entropy=Encoding.UTF8.GetBytes("CodexPulse.V0.2.AccountCredentials");
        public static string SourceId(string path) {
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).TrimEnd('\\','/').ToUpperInvariant()))).Replace("-","").ToLowerInvariant();
        }
        static void Validate(ReadOnlyDb db) {
            long version=Convert.ToInt64(db.Query("PRAGMA user_version")[0]["user_version"]);
            if(Convert.ToInt64(db.Query("PRAGMA application_id")[0]["application_id"])!=PulseDb.ApplicationId || version<1 || version>3)
                throw new IOException("不是兼容的 Pulse 数据库。");
        }
        public static Snapshot ReadHistory(string path,string sourceId) {
            using(var db=new ReadOnlyDb(path)) {
                Validate(db);db.Query("BEGIN");
                var sources=string.IsNullOrWhiteSpace(sourceId)?db.Query("SELECT id,imported_at FROM pulse_sources ORDER BY imported_at DESC,id LIMIT 1"):db.Query("SELECT id,imported_at FROM pulse_sources WHERE id=?",sourceId);
                if(sources.Count==0) throw new IOException("暂无已保存的历史用量。");
                var snapshot=ReadRows(db,Json.S(sources[0],"id"),(long)Json.N(sources[0],"imported_at"),true);db.Query("COMMIT");return snapshot;
            }
        }
        static Snapshot ReadRows(SqliteDatabase db,string sourceId,long importedAt,bool recentOnly) {
            var s=new Snapshot { SourceKind="independent",SourceId=sourceId,ImportedAt=importedAt,SourceNote="历史用量 · "+DateTimeOffset.FromUnixTimeSeconds(importedAt).LocalDateTime.ToString("MM-dd HH:mm"),DataPath="Pulse" };
            foreach(var row in db.Query("SELECT payload FROM pulse_keys WHERE source_id=? ORDER BY ordinal",sourceId)) s.Keys.Add(Json.New().Deserialize<KeyUsage>(Json.S(row,"payload")));
            foreach(var row in db.Query("SELECT payload FROM pulse_daily WHERE source_id=?"+(recentOnly?" AND day>=? AND day<=?":"")+" ORDER BY key_id COLLATE BINARY,day COLLATE BINARY",recentOnly?new object[] { sourceId,DataReader.ChinaToday.AddDays(-364).ToString("yyyy-MM-dd"),DataReader.ChinaToday.ToString("yyyy-MM-dd") }:new object[] { sourceId })) s.Daily.Add(Json.New().Deserialize<DailyUsage>(Json.S(row,"payload")));
            foreach(var row in db.Query("SELECT payload FROM pulse_undated WHERE source_id=? ORDER BY key_id",sourceId)) s.Undated.Add(Json.New().Deserialize<UndatedUsage>(Json.S(row,"payload")));
            return s;
        }
        internal static byte[] Unprotect(byte[] value) { return ProtectedData.Unprotect(value,Entropy,DataProtectionScope.CurrentUser); }
        internal static byte[] Protect(byte[] value) { return ProtectedData.Protect(value,Entropy,DataProtectionScope.CurrentUser); }
    }
}
