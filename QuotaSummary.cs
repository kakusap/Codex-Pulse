using System;
using System.Collections.Generic;

namespace CodexPulse {
    // The quota API exposes percentages, not comparable absolute subscription capacities.
    // Each account contributes one equal-weight percentage to its matching window.
    public sealed class QuotaSummary {
        public int Total,Available,Expired;
        public double? Remaining;
        public long NextReset,OldestCaptured;
        public static QuotaSummary For(IEnumerable<AccountUsage> accounts,double minutes) {
            var result=new QuotaSummary();double sum=0;
            foreach(var account in accounts) {
                result.Total++;
                double? used=null;long reset=0;
                if(account.PrimaryMinutes==minutes || (account.PrimaryMinutes<=0 && minutes==300)) { used=account.PrimaryUsed;reset=account.PrimaryReset; }
                else if(account.SecondaryMinutes==minutes || (account.SecondaryMinutes<=0 && minutes==10080)) { used=account.SecondaryUsed;reset=account.SecondaryReset; }
                if(!used.HasValue || double.IsNaN(used.Value) || double.IsInfinity(used.Value)) continue;
                result.Available++;sum+=Math.Max(0,Math.Min(100,100-used.Value));
                if(account.Captured>0 && (result.OldestCaptured==0 || account.Captured<result.OldestCaptured)) result.OldestCaptured=account.Captured;
                if(reset>0 && reset<=DataReader.Now) result.Expired++;
                else if(reset>DataReader.Now && (result.NextReset==0 || reset<result.NextReset)) result.NextReset=reset;
            }
            if(result.Available>0) result.Remaining=sum/result.Available;
            return result;
        }
    }
}
