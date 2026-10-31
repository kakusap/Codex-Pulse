using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPulse {
    public class KeyUsage {
        public string Id, Name, Status;
        public double Used, Cost;
        public bool UsedKnown=true,CostKnown=true;
        public double? Limit;
        public double? Remaining { get { return Limit.HasValue ? Math.Max(0, Limit.Value - Used) : (double?)null; } }
    }
    public class AccountUsage {
        public string Id, Name, Status;
        public string CredentialState;
        public string RefreshError="",Plan="",AccountIdentity="";
        public string CredentialOrigin="";
        public long CheckedAt,RetryAfter;
        public double? PrimaryUsed, SecondaryUsed;
        public long PrimaryReset, SecondaryReset, Captured;
        public double PrimaryMinutes, SecondaryMinutes;
    }
    public class DailyUsage {
        public string Day, KeyId;
        public double Used, Cost;
        public bool CostKnown=true;
    }
    public class UndatedUsage {
        public string KeyId;
        public double Used;
    }
    public class Snapshot {
        public List<KeyUsage> Keys = new List<KeyUsage>();
        public List<AccountUsage> Accounts = new List<AccountUsage>();
        public List<DailyUsage> Daily = new List<DailyUsage>();
        public List<UndatedUsage> Undated = new List<UndatedUsage>();
        public string HistoryError;
        public string DataError;
        public DateTime ReadAt = DateTime.Now;
        public string DataPath;
        public string SourceKind="independent", SourceId="",SourceNote="";
        public long ImportedAt;
        public List<UsageView> UsageViews=new List<UsageView>();
        public string UsageDateBasis="北京时间",UsageNotice="";
    }
    public class Settings {
        public string CodexExe = "";
        public string PulseDb = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","pulse.db");
        public string PulseSourceId = "";
        public string CodexHome = "";
        public int AccountRefreshSeconds = 180;
        public string UsageSource="auto",SelectedUsageAccount="",PriceTablePath="";
        public bool CollectLocalUsage=true;
        public string SelectedKey = "";
        public string SelectedAccount = "";
        public string MonitorDevice = "";
        public bool Topmost = true;
        public bool ClickThroughWhenPinned = true;
        public bool AutoStart = false;
        public string Theme = "green";
        public int BackgroundOpacity = 85, LayoutVersion = 6;
        public double Left = -1, Top = -1, Width = 560, Height = 56, ExpandedHeight = 730;
        public static string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "settings.json");
        public static Settings Load() {
            try {
                string raw=File.ReadAllText(FilePath);
                var settings=Json.New().Deserialize<Settings>(raw) ?? new Settings();
                if (!Json.New().Deserialize<Dictionary<string,object>>(raw).ContainsKey("LayoutVersion") || settings.LayoutVersion<5) {
                    settings.ExpandedHeight=Math.Max(settings.Height,730);settings.Width=620;settings.Height=88;
                    settings.BackgroundOpacity=85;
                }
                settings.BackgroundOpacity=Math.Max(0,Math.Min(85,settings.BackgroundOpacity));
                if(settings.LayoutVersion<6) { settings.Width=560;settings.Height=56;settings.SelectedAccount=""; }
                settings.Theme=ThemePalette.Get(settings.Theme).Id;
                settings.LayoutVersion=6;
                return settings;
            }
            catch { return new Settings(); }
        }
        public void Save() {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string pending = FilePath + ".tmp";
            File.WriteAllText(pending, Json.New().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(pending, FilePath, null); else File.Move(pending, FilePath);
        }
    }
    public static class Json {
        public static JavaScriptSerializer New() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }; }
        public static string S(Dictionary<string, object> r, string k) { object o; return r.TryGetValue(k, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : ""; }
        public static double N(Dictionary<string, object> r, string k) { double n; return double.TryParse(S(r,k), NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0; }
        public static double? Nullable(Dictionary<string, object> r, string k) { return !r.ContainsKey(k) || r[k] == null ? (double?)null : N(r,k); }
    }
    public static class DataReader {
        public static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } }
        public static DateTime ChinaToday { get { return DateTime.SpecifyKind(DateTime.UtcNow.AddHours(8).Date,DateTimeKind.Unspecified); } }
        public static Snapshot Read(Settings config) {
            return new IndependentAccounts(config).Read();
        }
    }
}
