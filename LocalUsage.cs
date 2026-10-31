using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexPulse {
    public sealed class TokenNumbers {
        public long Total,Input,Cached,Output;
        public long? CacheWrite;
        public bool Breakdown;
        internal static TokenNumbers Parse(Dictionary<string,object> value) {
            long? input=UsageStore.Count(value,"input_tokens"),output=UsageStore.Count(value,"output_tokens"),total=UsageStore.Count(value,"total_tokens"),cached=UsageStore.Count(value,"cached_input_tokens");
            if(!total.HasValue && (!input.HasValue || !output.HasValue)) return null;
            if(cached.HasValue && input.HasValue && cached>input) throw new InvalidDataException("缓存用量超过输入用量。");
            long? write=UsageStore.Count(value,"cache_write_input_tokens")??UsageStore.Count(value,"cache_write_tokens");
            if(write.HasValue && input.HasValue && write>input-(cached??0)) throw new InvalidDataException("缓存写入用量超过未命中的输入用量。");
            return new TokenNumbers { Total=total??checked(input.Value+output.Value),Input=input??0,Output=output??0,Cached=cached??0,CacheWrite=write,Breakdown=input.HasValue && output.HasValue && cached.HasValue };
        }
    }
    public sealed class LocalEntry {
        public TokenNumbers Tokens;
        public string Model="",Day,At,Identity="";
        public long? CumulativeTotal;
    }
    public sealed class LocalCursor {
        public int ParserVersion;
        public long Offset;
        public int HeaderLength;
        public string HeaderHash="",Anchor="",Thread="",Identity="",Model="";
        public TokenNumbers Previous;
    }
    public sealed class LocalScanReport {
        public long CheckedAt;
        public int Files,Pending,Invalid,Events;
        public string Error="";
    }
    public static class LocalUsage {
        const int MaxLine=2*1024*1024,FileBudget=4*1024*1024,ScanBudget=32*1024*1024;
        static string Root(Settings settings) { return UsageStore.Hash(ProfileFiles.Home(settings).TrimEnd('\\').ToUpperInvariant()); }
        static IEnumerable<string> Files(string root) {
            if(!Directory.Exists(root)) yield break;var pending=new Stack<string>();pending.Push(root);
            while(pending.Count>0) {
                string directory=pending.Pop();string[] files,children;
                try { files=Directory.GetFiles(directory,"*.jsonl");children=Directory.GetDirectories(directory); } catch(UnauthorizedAccessException) { continue; } catch(IOException) { continue; }
                foreach(string file in files) if((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0) yield return file;
                foreach(string child in children) if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0) pending.Push(child);
            }
        }
        static string ChunkHash(FileStream stream,long offset,int length) {
            stream.Position=offset;byte[] bytes=new byte[length];int read=0,n;
            while(read<length && (n=stream.Read(bytes,read,length-read))>0) read+=n;
            return UsageStore.Hash(Convert.ToBase64String(bytes,0,read));
        }
        public static LocalScanReport Scan(Settings settings,bool force) {
            IndependentStore.Ensure(settings.PulseDb);string root=Root(settings),key="usage.scan."+root;
            using(var lease=new AccountLease(settings.PulseDb)) {
                var previous=ScanState(settings.PulseDb,key);
                if(!settings.CollectLocalUsage || (!force && DataReader.Now-previous.CheckedAt<15)) return previous;
                var report=new LocalScanReport { CheckedAt=DataReader.Now };var identities=new HashSet<string>(IndependentStore.Accounts(settings.PulseDb).Select(a=>a.AccountIdentity));
                string home=ProfileFiles.Home(settings);long bytes=0;
                try {
                    var paths=Files(Path.Combine(home,"sessions")).Concat(Files(Path.Combine(home,"archived_sessions"))).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToList();
                    foreach(string path in paths) {
                        if(bytes>=ScanBudget) { report.Pending++;continue; }
                        try { bytes+=ScanFile(settings.PulseDb,root,path,identities,report);report.Files++; }
                        catch(IOException) { report.Pending++;report.Error="部分记录暂被占用或尚未写完，将继续采集。"; }
                        catch(UnauthorizedAccessException) { report.Pending++;report.Error="部分本机记录无法读取，请检查目录权限。"; }
                    }
                } catch { report.Error="本机用量采集未完成，已有记录保留。"; }
                using(var db=new PulseDb(settings.PulseDb)) db.Query("INSERT OR REPLACE INTO independent_meta VALUES(?,?)",key,Json.New().Serialize(report));
                return report;
            }
        }
        static LocalScanReport ScanState(string database,string key) {
            using(var db=new ReadOnlyDb(database)) { var row=db.Query("SELECT value FROM independent_meta WHERE key=?",key).FirstOrDefault();return row==null?new LocalScanReport():Json.New().Deserialize<LocalScanReport>(Json.S(row,"value")); }
        }
        static long ScanFile(string database,string root,string path,HashSet<string> identities,LocalScanReport report) {
            string key=UsageStore.Hash(root+"|"+Path.GetFullPath(path).ToUpperInvariant());
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) using(var db=new PulseDb(database)) {
                var row=db.Query("SELECT state FROM usage_local_files WHERE path_key=?",key).FirstOrDefault();
                var state=row==null?new LocalCursor():Json.New().Deserialize<LocalCursor>(Json.S(row,"state"));
                long length=stream.Length;
                bool reset=state.ParserVersion!=1 || state.Offset>length || (state.HeaderLength>0 && (length<state.HeaderLength || ChunkHash(stream,0,state.HeaderLength)!=state.HeaderHash)) ||
                    (state.Offset>0 && ChunkHash(stream,Math.Max(0,state.Offset-128),(int)Math.Min(128,state.Offset))!=state.Anchor);
                if(reset) state=new LocalCursor { ParserVersion=1 };
                if(state.HeaderLength==0 && length>0) { state.HeaderLength=(int)Math.Min(256,length);state.HeaderHash=ChunkHash(stream,0,state.HeaderLength); }
                stream.Position=state.Offset;long start=state.Offset,processed=state.Offset;int lineBytes=0;bool oversized=false,stop=false;
                var buffer=new byte[64*1024];var line=new MemoryStream();
                db.Query("BEGIN IMMEDIATE");
                try {
                    int read;
                    while(!stop && (read=stream.Read(buffer,0,buffer.Length))>0) for(int index=0;index<read;index++) {
                        byte b=buffer[index];processed++;lineBytes++;
                        if(b!=10) { if(!oversized && lineBytes<=MaxLine) line.WriteByte(b);else oversized=true;continue; }
                        if(!oversized) {
                            string text=Encoding.UTF8.GetString(line.GetBuffer(),0,(int)line.Length).TrimEnd('\r').TrimStart('\uFEFF');
                            try { ProcessLine(db,root,state,text,identities,report); }
                            catch(InvalidDataException) { report.Invalid++; }
                            catch(ArgumentException) { report.Invalid++; }
                            catch(InvalidOperationException) { report.Invalid++; }
                            catch(FormatException) { report.Invalid++; }
                            catch(OverflowException) { report.Invalid++; }
                        } else report.Invalid++;
                        state.Offset=processed;line.SetLength(0);lineBytes=0;oversized=false;
                        if(processed-start>=FileBudget) { stop=true;break; }
                    }
                    if(state.Offset<length) report.Pending++;
                    state.Anchor=ChunkHash(stream,Math.Max(0,state.Offset-128),(int)Math.Min(128,state.Offset));
                    db.Query("INSERT OR REPLACE INTO usage_local_files VALUES(?,?,?)",key,root,Json.New().Serialize(state));db.Query("COMMIT");
                } catch { db.Query("ROLLBACK");throw; } finally { line.Dispose(); }
                return processed-start;
            }
        }
        static void ProcessLine(SqliteDatabase db,string root,LocalCursor state,string text,HashSet<string> identities,LocalScanReport report) {
            if(text.IndexOf("token_usage_record",StringComparison.Ordinal)<0 && text.IndexOf("token_count",StringComparison.Ordinal)<0 && text.IndexOf("session_meta",StringComparison.Ordinal)<0 && text.IndexOf("turn_context",StringComparison.Ordinal)<0) return;
            var row=Json.New().Deserialize<Dictionary<string,object>>(text);if(row==null) return;string type=Json.S(row,"type");var payload=AccountCredential.Object(row,"payload");
            if(type=="session_meta") {
                state.Thread=Json.S(payload,"session_id");if(state.Thread=="") state.Thread=Json.S(payload,"id");
                string identity=Json.S(payload,"account_identity");
                if(identity=="") { string account=Json.S(payload,"chatgpt_account_id"),user=Json.S(payload,"user_id");if(account!="" && user!="") identity=account+"|"+user; }
                state.Identity=identities.Contains(identity)?identity:"";return;
            }
            if(type=="turn_context") { state.Model=Json.S(payload,"model");return; }
            TokenNumbers numbers=null;string kind,thread=state.Thread;int quality=2;
            if(type=="token_usage_record") {
                kind="request";numbers=TokenNumbers.Parse(AccountCredential.Object(payload,"usage"));string explicitThread=Json.S(payload,"thread_id");if(explicitThread!="") thread=explicitThread;
            } else if(type=="event_msg" && Json.S(payload,"type")=="token_count") {
                kind="cumulative";var info=AccountCredential.Object(payload,"info");var current=TokenNumbers.Parse(AccountCredential.Object(info,"total_token_usage"));if(current==null) return;
                var last=state.Previous;state.Previous=current;
                if(last==null || current.Total<last.Total) { numbers=TokenNumbers.Parse(AccountCredential.Object(info,"last_token_usage"));quality=1;if(numbers!=null && numbers.Total>current.Total) numbers=null; }
                else if(current.Total>last.Total) numbers=new TokenNumbers { Total=current.Total-last.Total,Input=Math.Max(0,current.Input-last.Input),Cached=Math.Max(0,current.Cached-last.Cached),Output=Math.Max(0,current.Output-last.Output),CacheWrite=current.CacheWrite.HasValue && last.CacheWrite.HasValue && current.CacheWrite>=last.CacheWrite?current.CacheWrite-last.CacheWrite:null,Breakdown=current.Breakdown && last.Breakdown && current.Input>=last.Input && current.Cached>=last.Cached && current.Output>=last.Output };
            } else return;
            if(numbers==null || numbers.Total<=0 || string.IsNullOrEmpty(thread)) return;
            DateTimeOffset timestamp;if(!DateTimeOffset.TryParse(Json.S(row,"timestamp"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out timestamp)) { report.Invalid++;return; }
            string at=timestamp.UtcDateTime.ToString("o"),day=timestamp.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            string response=Json.S(payload,"response_id");
            string signature=kind=="request"?(response!=""?response:thread+"|"+Json.S(payload,"turn_id")+"|"+at+"|"+StableNumbers(numbers)):thread+"|"+at+"|"+StableNumbers(state.Previous);
            string eventKey=UsageStore.Hash(root+"|"+kind+"|"+signature);
            var entry=new LocalEntry { Tokens=numbers,Model=state.Model,Day=day,At=at,Identity=state.Identity,
                CumulativeTotal=kind=="request"?UsageStore.Count(AccountCredential.Object(payload,"thread_token_usage"),"total_tokens"):(long?)state.Previous.Total };
            var existing=db.Query("SELECT payload,quality FROM usage_local_events WHERE event_key=?",eventKey).FirstOrDefault();
            if(existing==null) db.Query("INSERT INTO usage_local_events VALUES(?,?,?,?,?,?,?,?,?)",eventKey,root,kind,thread,state.Identity,day,at,Json.New().Serialize(entry),quality);
            else {
                var old=Json.New().Deserialize<LocalEntry>(Json.S(existing,"payload"));int oldQuality=(int)Json.N(existing,"quality");
                bool enrich=quality==oldQuality && StableNumbers(old.Tokens)==StableNumbers(numbers) && !old.Tokens.CacheWrite.HasValue && numbers.CacheWrite.HasValue;
                if(quality>oldQuality || enrich) {
                    if(string.IsNullOrEmpty(entry.Identity)) entry.Identity=old.Identity;
                    if(string.IsNullOrEmpty(entry.Model)) entry.Model=old.Model;
                    db.Query("UPDATE usage_local_events SET identity=?,day=?,at=?,payload=?,quality=? WHERE event_key=?",entry.Identity,day,at,Json.New().Serialize(entry),quality,eventKey);
                }
            }
            report.Events++;
        }
        // Keep pre-cache-write event identities stable during parser upgrades.
        static string StableNumbers(TokenNumbers n) { return Json.New().Serialize(new {n.Total,n.Input,n.Cached,n.Output,n.Breakdown}); }
        public static UsageView Read(Settings settings,List<AccountUsage> accounts) {
            var view=new UsageView { Id="local",Name="本机记录",DateBasis="北京时间",TotalLabel="本机已记录 Token",HasDaily=true };
            string priceNote;var prices=PriceBook.Load(settings,out priceNote);var names=accounts.GroupBy(a=>a.AccountIdentity).ToDictionary(g=>g.Key,g=>g.First().Name);
            var entries=new List<LocalEntry>();
            using(var db=new ReadOnlyDb(settings.PulseDb)) {
                var records=db.Query("SELECT kind,thread_key,payload FROM usage_local_events WHERE root_key=? ORDER BY at,event_key",Root(settings));
                foreach(var thread in records.GroupBy(r=>Json.S(r,"thread_key"))) {
                    var requests=thread.Where(r=>Json.S(r,"kind")=="request").Select(r=>Json.New().Deserialize<LocalEntry>(Json.S(r,"payload"))).ToList();entries.AddRange(requests);
                    DateTimeOffset resetAfter=DateTimeOffset.MinValue;LocalEntry previous=null;
                    foreach(var record in thread.Where(r=>Json.S(r,"kind")=="cumulative")) {
                        var entry=Json.New().Deserialize<LocalEntry>(Json.S(record,"payload"));
                        // Dual-format Codex logs emit a cumulative notification after
                        // the response. Keep older cumulative-only history and remove
                        // just the overlapping counter intervals in the same flush window.
                        if(previous!=null && entry.CumulativeTotal<previous.CumulativeTotal) resetAfter=DateTimeOffset.Parse(previous.At);
                        previous=entry;
                        var nearby=requests.Where(r=>DateTimeOffset.Parse(r.At)>resetAfter && Math.Abs((DateTimeOffset.Parse(r.At)-DateTimeOffset.Parse(entry.At)).TotalSeconds)<=300).ToList();
                        long covered=0;
                        if(entry.CumulativeTotal.HasValue) {
                            long end=entry.CumulativeTotal.Value,start=end-entry.Tokens.Total,left=-1,right=-1;
                            var spans=nearby.Where(r=>r.CumulativeTotal.HasValue).Select(r=>new[] {Math.Max(start,r.CumulativeTotal.Value-r.Tokens.Total),Math.Min(end,r.CumulativeTotal.Value)}).Where(s=>s[1]>s[0]).OrderBy(s=>s[0]);
                            foreach(var span in spans) { if(left<0) { left=span[0];right=span[1]; }else if(span[0]<=right) right=Math.Max(right,span[1]);else { covered+=right-left;left=span[0];right=span[1]; } }
                            if(left>=0) covered+=right-left;
                        }
                        if(covered==0 && nearby.Any(r=>!r.CumulativeTotal.HasValue && r.Tokens.Total==entry.Tokens.Total && r.Tokens.Input==entry.Tokens.Input && r.Tokens.Output==entry.Tokens.Output && r.Tokens.Cached==entry.Tokens.Cached)) covered=entry.Tokens.Total;
                        if(covered>=entry.Tokens.Total) continue;
                        if(covered>0) { entry.Tokens.Total-=covered;entry.Tokens.Breakdown=false; }
                        entries.Add(entry);
                    }
                }
            }
            foreach(var group in entries.GroupBy(e=>string.IsNullOrEmpty(e.Identity)?"unassigned":e.Identity)) {
                string name;bool unassigned=group.Key=="unassigned";
                if(!names.TryGetValue(group.Key,out name)) name=unassigned?"未归属账号":"已移除账号 · "+UsageStore.Hash(group.Key).Substring(0,6);
                var costs=group.Select(e=>prices==null?null:prices.Cost(e)).ToList();
                view.Groups.Add(new KeyUsage { Id=group.Key,Name=name,Status=unassigned?"日志缺少可靠账号标识":"本机记录",Used=group.Sum(e=>(double)e.Tokens.Total),Cost=costs.Where(c=>c.HasValue).Sum(c=>c.Value),CostKnown=costs.All(c=>c.HasValue) });
                foreach(var daily in group.GroupBy(e=>e.Day)) {
                    var dailyCosts=daily.Select(e=>prices==null?null:prices.Cost(e)).ToList();
                    view.Daily.Add(new DailyUsage { KeyId=group.Key,Day=daily.Key,Used=daily.Sum(e=>(double)e.Tokens.Total),Cost=dailyCosts.Where(c=>c.HasValue).Sum(c=>c.Value),CostKnown=dailyCosts.All(c=>c.HasValue) });
                }
            }
            var scan=ScanState(settings.PulseDb,"usage.scan."+Root(settings));
            var costsSummary=new LocalCostSummary {PriceVersion=prices==null?"":prices.Version,PriceNotice=priceNote,CollectedAt=scan.CheckedAt,PendingFiles=scan.Pending,SkippedLines=scan.Invalid,Paused=!settings.CollectLocalUsage,CollectionWarning=scan.Error};
            var priced=entries.Select(e=>new {Entry=e,Cost=prices==null?null:prices.Cost(e)}).ToList();
            foreach(var group in priced.GroupBy(e=>new {Identity=string.IsNullOrEmpty(e.Entry.Identity)?"unassigned":e.Entry.Identity,Model=e.Entry.Model??"",Problem=e.Cost.HasValue?"":prices==null?"价格表不可用":prices.CostProblem(e.Entry)})) {
                costsSummary.Slices.Add(new LocalCostSlice {Identity=group.Key.Identity,Model=group.Key.Model,Problem=group.Key.Problem,Records=group.Count(),Tokens=group.Sum(e=>(double)e.Entry.Tokens.Total),Cost=group.Where(e=>e.Cost.HasValue).Sum(e=>e.Cost.Value),Priced=group.All(e=>e.Cost.HasValue),FirstDay=group.Min(e=>e.Entry.Day),LastDay=group.Max(e=>e.Entry.Day)});
            }
            view.Costs=costsSummary;
            view.HasData=entries.Count>0;view.Notice="只统计本机保留的记录；同线程双格式记录按累计区间去重（5 分钟写入窗口）。首条累计仅计本次用量，不回填缺失历史。\n未可靠识别账号的记录归入“未归属”，不套用当前登录。\n"+priceNote+
                (scan.Pending>0?"\n仍有 "+scan.Pending+" 个记录文件待继续采集。":"")+(scan.Invalid>0?"\n有无效或过大的记录被跳过。":"")+(string.IsNullOrEmpty(scan.Error)?"":"\n"+scan.Error);
            if(!settings.CollectLocalUsage) view.Notice+="\n本机采集已暂停，显示已保存记录。";
            view.Notice+="\n最近采集："+UsageStore.Stamp(scan.CheckedAt)+"（北京时间）。";
            if(!view.HasData) view.Error="尚无可统计的本机用量记录。";
            return view;
        }
    }
}
