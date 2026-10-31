using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPulse {
    public static class Json {
        public static JavaScriptSerializer New() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }; }
        public static string S(Dictionary<string, object> r, string k) { object o; return r.TryGetValue(k, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : ""; }
        public static double N(Dictionary<string, object> r, string k) { double n; return double.TryParse(S(r,k), NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0; }
        public static double? Nullable(Dictionary<string, object> r, string k) { return !r.ContainsKey(k) || r[k] == null ? (double?)null : N(r,k); }
    }

}
