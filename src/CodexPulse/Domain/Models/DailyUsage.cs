using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPulse {
    public class DailyUsage {
        public string Day, KeyId;
        public double Used, Cost;
        public bool CostKnown=true;
    }

}
