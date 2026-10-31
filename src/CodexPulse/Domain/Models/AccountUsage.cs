using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexPulse {
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

}
