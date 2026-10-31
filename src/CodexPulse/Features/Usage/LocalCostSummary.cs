using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CodexPulse {
    public sealed class LocalCostSlice {
        public string Identity,Model,Problem,FirstDay,LastDay;
        public int Records;
        public double Tokens,Cost;
        public bool Priced;
    }
    public sealed class LocalCostSummary {
        public string PriceVersion="",PriceNotice="",CollectionWarning="",Scope="全部本机记录";
        public long CollectedAt;
        public int PendingFiles,SkippedLines,ExcludedUnassigned;
        public bool Paused;
        public List<LocalCostSlice> Slices=new List<LocalCostSlice>();
        public int Records {get {return Slices.Sum(s=>s.Records);}}
        public int PricedRecords {get {return Slices.Where(s=>s.Priced).Sum(s=>s.Records);}}
        public int UnassignedRecords {get {return Slices.Where(s=>s.Identity=="unassigned").Sum(s=>s.Records);}}
        public double RecordedTokens {get {return Slices.Sum(s=>s.Tokens);}}
        public double PricedTokens {get {return Slices.Where(s=>s.Priced).Sum(s=>s.Tokens);}}
        public double Cost {get {return Slices.Where(s=>s.Priced).Sum(s=>s.Cost);}}
        public bool HasAmount {get {return PricedRecords>0;}}
        public bool Partial {get {return PricedRecords<Records || PendingFiles>0 || SkippedLines>0 || !string.IsNullOrEmpty(CollectionWarning);}}
        public string FirstDay {get {return Slices.Count==0?"":Slices.Min(s=>s.FirstDay);}}
        public string LastDay {get {return Slices.Count==0?"":Slices.Max(s=>s.LastDay);}}
        public string AmountText {get {return !HasAmount?"—":(Cost>0 && Cost<.01?"<$0.01":"$"+Cost.ToString("N2",CultureInfo.InvariantCulture))+(Partial?"（部分）":"");}}
        public string CoverageText {get {return Records==0?(Scope=="全部本机记录"?"暂无本机明细":"暂无可归属的本机明细"):PricedRecords+" / "+Records+" 条已计价"+(UnassignedRecords>0?" · 含未归属":"");}}
        public LocalCostSummary ForAccount(string identity,string accountName) {
            return new LocalCostSummary {Slices=Slices.Where(s=>string.IsNullOrEmpty(identity) || s.Identity==identity).ToList(),PriceVersion=PriceVersion,PriceNotice=PriceNotice,CollectedAt=CollectedAt,PendingFiles=PendingFiles,SkippedLines=SkippedLines,Paused=Paused,CollectionWarning=CollectionWarning,
                Scope=string.IsNullOrEmpty(identity)?"全部本机记录":"账号："+(string.IsNullOrEmpty(accountName)?"所选账号":accountName),ExcludedUnassigned=string.IsNullOrEmpty(identity)?0:UnassignedRecords};
        }
        public string Description() {
            return "范围："+Scope+"。仅按本机保留的逐条日志估算，与上方官方累计 Token 范围不同，不是订阅账单。\n"+
                (HasAmount?"已计价金额："+Cost.ToString("N6",CultureInfo.InvariantCulture)+" USD。":"暂无可计价记录，金额未知。")+"本机已记录 "+RecordedTokens.ToString("N0")+" Token，其中 "+PricedTokens.ToString("N0")+" Token 已计价；"+PricedRecords+" / "+Records+" 条记录。\n"+
                (Records==0?"所选范围暂无可可靠归属的本机明细。\n":"记录日期："+FirstDay+" 至 "+LastDay+"（北京时间）。\n")+
                (UnassignedRecords>0?"含 "+UnassignedRecords+" 条未归属记录，未将它们套用到当前账号。\n":"")+
                (ExcludedUnassigned>0?"另有 "+ExcludedUnassigned+" 条未归属记录未计入此账号，可选择“全部账号”查看本机合计。\n":"")+
                (PricedRecords<Records?"未计价的 "+(Records-PricedRecords)+" 条记录不视为零费用。\n":"")+
                (PendingFiles>0?"仍有 "+PendingFiles+" 个文件待继续采集。\n":"")+(SkippedLines>0?"采集时跳过了无效或过大的日志行。\n":"")+
                (Paused?"本机采集已暂停，显示已有记录。\n":"")+(string.IsNullOrEmpty(CollectionWarning)?"":CollectionWarning+"\n")+
                "价格版本："+(string.IsNullOrEmpty(PriceVersion)?"不可用":PriceVersion)+"。\n"+PriceNotice+"\n最近采集："+UsageStore.Stamp(CollectedAt)+"（北京时间）。";
        }
        public static LocalCostSummary FromSnapshot(Snapshot snapshot,string identity) {
            var view=snapshot.UsageViews.FirstOrDefault(v=>v.Id=="local");var costs=view==null?new LocalCostSummary():view.Costs;
            var account=snapshot.Accounts.FirstOrDefault(a=>a.AccountIdentity==identity);
            return costs.ForAccount(identity,account==null?"":account.Name);
        }
    }
}
