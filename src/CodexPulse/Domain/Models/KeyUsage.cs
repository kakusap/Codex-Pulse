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

}
