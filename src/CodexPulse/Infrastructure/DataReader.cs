using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPulse {
    public static class DataReader {
        public static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } }
        public static DateTime ChinaToday { get { return DateTime.SpecifyKind(DateTime.UtcNow.AddHours(8).Date,DateTimeKind.Unspecified); } }
        public static Snapshot Read(Settings config) {
            return new IndependentAccounts(config).Read();
        }
    }
}
