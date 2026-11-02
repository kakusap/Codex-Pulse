using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPulse {
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
        public bool AutoCheckUpdates = true;
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

}
