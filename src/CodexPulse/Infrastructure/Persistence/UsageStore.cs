using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexPulse {
    public sealed class UsageView {
        public string Id,Name,Notice="",DateBasis="北京时间",Error;
        public string TotalLabel="可统计 Token";
        public bool HasData,HasDaily;
        public List<KeyUsage> Groups=new List<KeyUsage>();
        public List<DailyUsage> Daily=new List<DailyUsage>();
        public List<UndatedUsage> Undated=new List<UndatedUsage>();
        public LocalCostSummary Costs=new LocalCostSummary();
    }
    public sealed class OfficialUsage {
        public long? Lifetime;
        public bool HasDaily;
        public long LastSuccess,NextAttempt,LastTotalAt,LastDailyAt;
        public string Error="";
    }
    public static class UsageStore {
        internal static string Hash(string value) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant(); }
        internal static long? Count(Dictionary<string,object> row,string key) {
            object value;if(!row.TryGetValue(key,out value) || value==null) return null;
            double number;if(!double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number) || double.IsNaN(number) || double.IsInfinity(number) || number<0 || number>9007199254740991d || Math.Floor(number)!=number) throw new InvalidDataException("用量数字无效，保留旧数据。");
            return (long)number;
        }
        public static OfficialUsage OfficialState(string database,string identity) {
            using(var db=new ReadOnlyDb(database)) { var row=db.Query("SELECT payload FROM usage_official WHERE identity=?",identity).FirstOrDefault();return row==null?new OfficialUsage():Json.New().Deserialize<OfficialUsage>(Json.S(row,"payload")); }
        }
        public static void SaveOfficial(string database,string identity,Dictionary<string,object> response) {
            var summary=AccountCredential.Object(response,"summary");long? total=Count(summary,"lifetimeTokens");
            object raw;bool hasDaily=response.TryGetValue("dailyUsageBuckets",out raw) && raw!=null;
            var days=new Dictionary<string,long>(StringComparer.Ordinal);
            if(hasDaily) {
                if(!(raw is IEnumerable) || raw is string || raw is Dictionary<string,object>) throw new IOException("每日用量格式无效。");
                foreach(object entry in (IEnumerable)raw) {
                    var row=entry as Dictionary<string,object>;if(row==null) throw new IOException("每日用量格式无效。");
                    string day=Json.S(row,"startDate");DateTime date;long? tokens=Count(row,"tokens");
                    if(!DateTime.TryParseExact(day,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date) || !tokens.HasValue || days.ContainsKey(day)) throw new IOException("每日用量日期重复或数据无效，保留旧数据。");
                    days[day]=tokens.Value;
                }
            }
            var previous=OfficialState(database,identity);
            var state=new OfficialUsage { Lifetime=total??previous.Lifetime,HasDaily=hasDaily||previous.HasDaily,LastSuccess=DataReader.Now,NextAttempt=DataReader.Now+300,
                LastTotalAt=total.HasValue?DataReader.Now:previous.LastTotalAt,LastDailyAt=hasDaily?DataReader.Now:previous.LastDailyAt,
                Error=(!total.HasValue?"本次官方累计未返回；":"")+(!hasDaily?"本次官方每日数据未返回；":"")+"官方日期时区未披露，不与本机记录相加。" };
            using(var db=new PulseDb(database)) {
                db.Query("BEGIN IMMEDIATE");try {
                    db.Query("INSERT OR REPLACE INTO usage_official VALUES(?,?)",identity,Json.New().Serialize(state));
                    foreach(var day in days) db.Query("INSERT OR REPLACE INTO usage_official_days VALUES(?,?,?,?)",identity,day.Key,day.Value,DataReader.Now);
                    db.Query("COMMIT");
                } catch { db.Query("ROLLBACK");throw; }
            }
        }
        public static void OfficialFailure(string database,string identity,string message) {
            var state=OfficialState(database,identity);state.NextAttempt=DataReader.Now+300;state.Error=message+" 已有用量快照保留。";
            using(var db=new PulseDb(database)) db.Query("INSERT OR REPLACE INTO usage_official VALUES(?,?)",identity,Json.New().Serialize(state));
        }
        public static List<UsageView> Read(Settings settings,Snapshot snapshot) {
            var official=new UsageView { Id="official",Name="官方账号",DateBasis="官方日期",Notice="官方累计与每日活动范围可能不同；每日日期沿用官方返回值，时区未披露。" };
            using(var db=new ReadOnlyDb(settings.PulseDb)) {
                foreach(var account in snapshot.Accounts) {
                    var state=OfficialState(settings.PulseDb,account.AccountIdentity);
                    var rows=db.Query("SELECT day,tokens FROM usage_official_days WHERE identity=? ORDER BY day",account.AccountIdentity);
                    bool has=state.Lifetime.HasValue || rows.Count>0;
                    official.Groups.Add(new KeyUsage { Id=account.AccountIdentity,Name=account.Name,Status=has?"官方快照 · "+Stamp(Math.Max(state.LastTotalAt,state.LastDailyAt)):"尚无用量",Used=state.Lifetime.HasValue?state.Lifetime.Value:rows.Sum(r=>Json.N(r,"tokens")),UsedKnown=has,CostKnown=false });
                    foreach(var day in rows) official.Daily.Add(new DailyUsage { KeyId=account.AccountIdentity,Day=Json.S(day,"day"),Used=Json.N(day,"tokens"),CostKnown=false });
                    official.HasData|=has;official.HasDaily|=state.HasDaily;
                    if(!state.Lifetime.HasValue || !state.HasDaily || state.Error.Contains("保留") || state.Error.Contains("未返回")) official.Notice+="\n"+(string.IsNullOrEmpty(account.Name)?"账号":account.Name)+"："+(string.IsNullOrEmpty(state.Error)?"尚未取得官方用量。":state.Error);
                }
            }
            if(!official.HasData) official.Error="官方账号用量尚不可用，可刷新后重试。";
            else if(!official.HasDaily) official.Error="官方未返回每日用量；累计仍可查看，热力图暂无每日记录。";
            var local=LocalUsage.Read(settings,snapshot.Accounts);
            if(!string.IsNullOrEmpty(snapshot.UsageNotice)) {local.Notice+="\n"+snapshot.UsageNotice;local.Costs.CollectionWarning+="\n"+snapshot.UsageNotice;}
            return new List<UsageView> { official,local };
        }
        internal static string Stamp(long seconds) { return seconds<=0?"时间未知":DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm"); }
        public static UsageView Selected(Snapshot snapshot,string source) {
            return snapshot.UsageViews.FirstOrDefault(v=>v.Id=="official")??new UsageView { Id="official",Name="官方账号",DateBasis="官方日期",Error="暂无账号用量，可刷新后重试。" };
        }
        public static Snapshot Chart(UsageView view) {
            return new Snapshot { Keys=view.Groups,Daily=view.Daily,Undated=view.Undated,HistoryError=view.Error,UsageDateBasis=view.DateBasis,UsageNotice=view.Notice };
        }
    }
}
