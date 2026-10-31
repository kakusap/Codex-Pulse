using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPulse {
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

}
