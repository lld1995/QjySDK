using Common;
using Model;
using QjySDK.Stg;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    /// <summary>
    /// Poly 子目录所有策略的横向对比回测。
    ///
    /// 流程:
    ///   1. 用 KlineCache 从 TDEngine 加载历史K线 (第一次去DB, 后续直接用本地缓存文件)
    ///   2. 对每个策略调用 BacktestEngine.RunSingleSymbol, 跑同一组K线
    ///   3. IsBacktest=true (默认) 自动跳过 Polymarket 网络连接
    ///   4. polyNum=0 + 主交易所交易由 BacktestEngine 统计 PnL / 胜率 / 回撤 / 夏普
    ///   5. 打印对比表, 排出最强策略
    /// </summary>
    public class PolyStrategyComparisonTests
    {
        private readonly ITestOutputHelper _output;

        public PolyStrategyComparisonTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // 默认对比配置: ETH 永续, 5M (与 TimesFM 回测一致). 改这里就能切其他品种/周期
        private const string RawSymbol = "COIN_FUTURES_ETHUSDT";
        private static readonly Period[] TestPeriods = { Period.TIME_5M };
        private static readonly Dictionary<Period, int> BarLimits = new()
        {
            { Period.TIME_5M, 3000 },    // ~10 天 (跑 TimesFmDirection 时受限于 HTTP ~50ms/bar)
            { Period.TIME_15M, 2000 },   // ~21 天
            { Period.TIME_1H, 1500 },    // ~62 天
        };

        // 公共参数覆盖: 固定 1 手, 不要 lotsMode=1 (那个会触发 GetSymbol 异步等待), 关掉 Poly 下单
        private static readonly Dictionary<string, object> CommonOverrides = new()
        {
            ["lotsMode"] = 0,
            ["lots"] = 1.0m,
            ["polyNum"] = 0m,         // 即使 IsBacktest=false 也禁止下单
            ["sendMode"] = 0,
        };

        // 候选策略列表 (Poly 子目录下所有派生自 StgBase 的) 
        // 每项: (名称, 工厂方法, 该策略特有的覆盖)
        private static readonly List<(string name, Func<StgBase> factory, Dictionary<string, object>? overrides)>
            Candidates = new()
            {
                ("ExtremeReversal", () => new ExtremeReversal(), null),
                ("MoEPredict",       () => new MoEPredict(),       null),
                ("TimesFmDirection", () => new TimesFmDirection(), new Dictionary<string, object>
                {
                    // TimesFM 默认 384 上下文, 保持不变. 若 5M URL 改过请用 --override
                    ["timesFmUrl"] = "http://192.168.191.4:1234",
                    ["atrThresholdK"] = 0.30,
                }),
            };

        private static string RawToMkt(string rawSymbol)
        {
            var strs = rawSymbol.Split(new char[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (strs.Length >= 3)
                return strs[1] + "_" + string.Join("_", strs, 2, strs.Length - 2);
            return rawSymbol;
        }

        /// <summary>
        /// Step 1: 预热 K 线缓存 (仅首次需要 TDEngine, 后续从 KlineCache/*.json 读取).
        /// 如果对应 raw_symbol/period 已经在缓存里则跳过.
        /// </summary>
        [Fact]
        public void Step1_WarmCache()
        {
            _output.WriteLine($"缓存目录: {KlineCache.CacheDirectory}\n");
            foreach (var period in TestPeriods)
            {
                int limit = BarLimits.TryGetValue(period, out var l) ? l : 6000;
                if (KlineCache.HasCache(RawSymbol, period))
                {
                    var cached = KlineCache.LoadFromCacheOnly(RawSymbol, period, limit);
                    _output.WriteLine($"[CACHE] {RawSymbol} {period}: {cached?.Count} bars (已存在)");
                    continue;
                }
                if (!TDEngineDataLoader.IsAvailable())
                {
                    _output.WriteLine($"[SKIP] {RawSymbol} {period}: TDEngine 不可达且无缓存");
                    continue;
                }
                var quotes = KlineCache.LoadKlines(RawSymbol, period, limit);
                _output.WriteLine($"[FETCH] {RawSymbol} {period}: {quotes.Count} bars 已写入缓存");
            }
        }

        /// <summary>
        /// Step 2: Poly 策略横向对比 — 同一组K线、同样的 lots 设置, 比较 PnL/胜率/回撤/夏普.
        /// 不连 Polymarket. 不连 TimesFM 时该策略会全部跳过, 但其他两个仍能跑.
        /// </summary>
        [Fact]
        public void Step2_CompareAllPolyStrategies()
        {
            // 同时把进度写到磁盘文件, 方便从外部 tail
            var progressLog = System.IO.Path.Combine(KlineCache.CacheDirectory, "..", "poly_compare_progress.log");
            void Log(string msg)
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                _output.WriteLine(line);
                try { System.IO.File.AppendAllText(progressLog, line + Environment.NewLine); } catch { }
            }
            try { System.IO.File.WriteAllText(progressLog, ""); } catch { }

            bool timesFmAvailable = IsTimesFmAvailable();
            Log($"TimesFM /health: {(timesFmAvailable ? "OK" : "不可达 (TimesFmDirection 将产生 0 笔)")}");
            Log($"对比品种: {RawSymbol}");
            Log($"对比周期: {string.Join(", ", TestPeriods)}");

            var allResults = new List<(string strategy, Period period, BacktestResult r, double seconds)>();

            foreach (var period in TestPeriods)
            {
                int limit = BarLimits.TryGetValue(period, out var l) ? l : 6000;
                var quotes = KlineCache.LoadFromCacheOnly(RawSymbol, period, limit)
                             ?? (TDEngineDataLoader.IsAvailable()
                                    ? KlineCache.LoadKlines(RawSymbol, period, limit)
                                    : null);
                if (quotes == null || quotes.Count < 500)
                {
                    Log($"[SKIP] {period}: 数据不足 ({quotes?.Count ?? 0} bars)");
                    continue;
                }

                Log($"========== {period}  K线={quotes.Count}  ==========");

                foreach (var (name, factory, extraOverrides) in Candidates)
                {
                    // TimesFM 不可达时跳过 TimesFmDirection (否则会产生大量异常日志拖慢回测)
                    if (name == "TimesFmDirection" && !timesFmAvailable)
                    {
                        Log($"  [SKIP] {name} (TimesFM 不可达)");
                        continue;
                    }
                    Log($"  [START] {name} ...");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    BacktestResult? r = null;
                    try
                    {
                        var stg = factory();
                        var merged = MergeOverrides(CommonOverrides, extraOverrides);
                        r = RunWithOverrides(stg, RawToMkt(RawSymbol), quotes, period, merged);
                    }
                    catch (Exception ex)
                    {
                        Log($"  [ERROR] {name}: {ex.GetType().Name}: {ex.Message}");
                    }
                    sw.Stop();
                    if (r != null)
                    {
                        allResults.Add((name, period, r, sw.Elapsed.TotalSeconds));
                        Log($"  {name,-18} trades={r.TradeCount,4}  win={r.WinRate,5:F1}%  pnl={r.TotalProfit,+12:F2}  dd={r.MaxDrawdown,10:F2}  sharpe={r.SharpeRatio,7:F3}  pf={r.ProfitFactor,5:F2}  [{sw.Elapsed.TotalSeconds:F1}s]");
                    }
                }
            }

            PrintComparisonTable(allResults);
            PrintRanking(allResults);
        }

        // ===================== 辅助方法 =====================

        private static Dictionary<string, object> MergeOverrides(
            Dictionary<string, object> baseO, Dictionary<string, object>? extra)
        {
            var merged = new Dictionary<string, object>(baseO);
            if (extra != null)
                foreach (var kv in extra) merged[kv.Key] = kv.Value;
            return merged;
        }

        private static BacktestResult RunWithOverrides(StgBase stg, string mktSymbol,
            List<SkQuote> quotes, Period period, Dictionary<string, object> overrides)
        {
            var cts = StgTestHelper.InitForTest(stg, mktSymbol);
            try
            {
                var argDicProp = typeof(StgBase).GetProperty("ArgDic",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                var argDic = (Dictionary<string, object>)argDicProp.GetValue(stg)!;
                foreach (var kv in overrides)
                {
                    if (argDic.ContainsKey(kv.Key)) argDic[kv.Key] = kv.Value;
                }

                var tu = new TableUnit
                {
                    QuoteList = new List<SkQuote>(),
                    MktSymbol = mktSymbol,
                    Period = period
                };

                var trades = new List<RemoteTradeRecord>();
                for (int i = 0; i < quotes.Count; i++)
                {
                    tu.QuoteList.Add(quotes[i]);
                    try { stg.OnBar(period, tu, true, null); }
                    catch { }
                    trades.AddRange(StgTestHelper.DrainTrades(stg));
                }

                var lastPrices = new Dictionary<string, decimal> { { mktSymbol, quotes.Last().Close } };
                var calcMethod = typeof(BacktestEngine).GetMethod("CalcResult",
                    BindingFlags.NonPublic | BindingFlags.Static)!;
                return (BacktestResult)calcMethod.Invoke(null,
                    new object[] { stg.GetType().Name, new[] { mktSymbol }, quotes.Count, trades, period, lastPrices })!;
            }
            finally { cts.Cancel(); }
        }

        private static bool IsTimesFmAvailable()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var resp = http.GetAsync("http://192.168.191.4:1234/health").GetAwaiter().GetResult();
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private void PrintComparisonTable(
            List<(string strategy, Period period, BacktestResult r, double seconds)> results)
        {
            _output.WriteLine("\n========== 全表对比 ==========\n");
            _output.WriteLine($"{"策略",-18} {"周期",-10} {"交易",6} {"胜率",7} {"PnL",12} {"回撤",12} {"夏普",8} {"盈亏比",7} {"耗时",6}");
            _output.WriteLine(new string('-', 100));
            foreach (var g in results.GroupBy(x => x.period))
            {
                foreach (var item in g.OrderByDescending(x => x.r.TotalProfit))
                {
                    var marker = item.r.TotalProfit > 0 ? "+" : "";
                    _output.WriteLine($"{item.strategy,-18} {item.period,-10} {item.r.TradeCount,6} {item.r.WinRate,6:F1}% {marker}{item.r.TotalProfit,11:F2} {item.r.MaxDrawdown,12:F2} {item.r.SharpeRatio,8:F3} {item.r.ProfitFactor,7:F2} {item.seconds,5:F1}s");
                }
                _output.WriteLine(new string('-', 100));
            }
        }

        private void PrintRanking(
            List<(string strategy, Period period, BacktestResult r, double seconds)> results)
        {
            _output.WriteLine("\n========== 策略综合排名 (按总PnL汇总) ==========\n");
            var ranking = results
                .GroupBy(x => x.strategy)
                .Select(g => new
                {
                    Strategy = g.Key,
                    TotalPnl = g.Sum(x => x.r.TotalProfit),
                    TotalTrades = g.Sum(x => x.r.TradeCount),
                    OverallWin = g.Sum(x => x.r.TradeCount) > 0
                        ? 100.0 * g.Sum(x => x.r.WinCount) / g.Sum(x => x.r.TradeCount) : 0.0,
                    MaxDD = g.Max(x => x.r.MaxDrawdown),
                    AvgSharpe = g.Average(x => x.r.SharpeRatio)
                })
                .OrderByDescending(x => x.TotalPnl)
                .ToList();

            _output.WriteLine($"{"排名",-4} {"策略",-18} {"PnL汇总",12} {"总交易",7} {"总胜率",7} {"最大回撤",12} {"平均夏普",10}");
            _output.WriteLine(new string('-', 80));
            int rank = 1;
            foreach (var x in ranking)
            {
                _output.WriteLine($"{rank++,-4} {x.Strategy,-18} {x.TotalPnl,12:F2} {x.TotalTrades,7} {x.OverallWin,6:F1}% {x.MaxDD,12:F2} {x.AvgSharpe,10:F3}");
            }

            if (ranking.Count > 0)
            {
                _output.WriteLine($"\n🏆 综合最强: **{ranking[0].Strategy}** (PnL={ranking[0].TotalPnl:F2}, 胜率={ranking[0].OverallWin:F1}%)");
            }
        }
    }
}
