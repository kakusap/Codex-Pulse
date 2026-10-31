using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CodexPulse {
    public sealed class ModelPrice {
        public string Model;
        public double? InputPerMillion,CachedInputPerMillion,OutputPerMillion,CacheWritePerMillion;
        public long LongContextThreshold;
        public double LongInputMultiplier=2,LongOutputMultiplier=1.5;
    }
    public sealed class PriceBook {
        public string Version,Source="",CheckedOn="";
        public List<ModelPrice> Models=new List<ModelPrice>();
        internal static PriceBook Parse(string json) {
            var book=Json.New().Deserialize<PriceBook>(json);
            if(book==null || string.IsNullOrWhiteSpace(book.Version) || book.Models==null || book.Models.Any(m=>m==null) || book.Models.Select(m=>m.Model).Distinct(StringComparer.Ordinal).Count()!=book.Models.Count) throw new IOException("价格表版本或模型列表无效。");
            foreach(var price in book.Models) {
                if(string.IsNullOrWhiteSpace(price.Model) || new[] {price.InputPerMillion,price.CachedInputPerMillion,price.OutputPerMillion}.Any(n=>!Valid(n))) throw new IOException("模型价格缺失或无效。");
                if((price.CacheWritePerMillion.HasValue && !Valid(price.CacheWritePerMillion)) || price.LongContextThreshold<0 || !Valid(price.LongInputMultiplier) || !Valid(price.LongOutputMultiplier)) throw new IOException("缓存写入或长上下文价格无效。");
            }
            return book;
        }
        static bool Valid(double? n) { return n.HasValue && n.Value>=0 && !double.IsNaN(n.Value) && !double.IsInfinity(n.Value); }
        internal static PriceBook Builtin() {
            using(var stream=typeof(PriceBook).Assembly.GetManifestResourceStream("model-prices.builtin.json")) {
                if(stream==null) throw new IOException("内置模型价格表缺失。");
                using(var reader=new StreamReader(stream)) return Parse(reader.ReadToEnd());
            }
        }
        internal static PriceBook Load(Settings settings,out string note) {
            bool explicitPath=!string.IsNullOrWhiteSpace(settings.PriceTablePath);
            string path=explicitPath?settings.PriceTablePath:Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.PulseDb)),"model-prices.json");
            try {
                bool custom=explicitPath || File.Exists(path);
                var book=custom?Parse(File.ReadAllText(path)):Builtin();
                note=(custom?"自定义参考价格：":"内置 OpenAI 参考价格：")+book.Version+"。"+
                    (custom?"":"按 Standard API 单价、默认 30 分钟缓存估算；不含加速、区域和工具费用。")+"不代表 ChatGPT 订阅实际付款。";
                return book;
            } catch {note=explicitPath?"自定义价格表不可用，参考费用暂不可用；清空路径可恢复内置价格。":"价格表不可用，参考费用暂不可用。";return null;}
        }
        internal double? Cost(LocalEntry entry) {
            var price=Models.FirstOrDefault(p=>p.Model==entry.Model);var t=entry.Tokens;
            if(price==null || t==null || !t.Breakdown || t.Cached>t.Input) return null;
            if(price.CacheWritePerMillion.HasValue && !t.CacheWrite.HasValue) return null;
            long written=t.CacheWrite??0;
            if(written<0 || written>t.Input-t.Cached || (written>0 && !price.CacheWritePerMillion.HasValue)) return null;
            double input=(t.Input-t.Cached-written)*price.InputPerMillion.Value+t.Cached*price.CachedInputPerMillion.Value+written*(price.CacheWritePerMillion??0);
            double output=t.Output*price.OutputPerMillion.Value;
            if(price.LongContextThreshold>0 && t.Input>price.LongContextThreshold) {input*=price.LongInputMultiplier;output*=price.LongOutputMultiplier;}
            double cost=(input+output)/1000000d;
            return double.IsInfinity(cost) || double.IsNaN(cost)?(double?)null:cost;
        }
        internal static string Rate(double? value) { return value.HasValue?value.Value.ToString("0.#####",CultureInfo.InvariantCulture):"—"; }
        internal string CostProblem(LocalEntry entry) {
            if(string.IsNullOrEmpty(entry.Model)) return "日志缺少模型名";
            var price=Models.FirstOrDefault(p=>p.Model==entry.Model);
            if(price==null) return "价格表未收录此模型";
            if(entry.Tokens==null || !entry.Tokens.Breakdown) return "缺少输入、缓存或输出明细";
            if(price.CacheWritePerMillion.HasValue && !entry.Tokens.CacheWrite.HasValue) return "缺少缓存写入明细";
            if(entry.Tokens.CacheWrite>0 && !price.CacheWritePerMillion.HasValue) return "缺少缓存写入单价";
            return "用量分类不完整或无效";
        }
    }
}
