using Common;
using Model;
using QjySDK.Stg;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    /// <summary>
    /// 海龟/唐奇安趋势策略回测：全量基线 + 参数变体扫描 + 样本外窗口切分验证
    /// （正式口径：BacktestEngine.RunSingleSymbol，含浮盈；结果落盘支持断点续跑）
    /// </summary>
    public class TurtleTests
    {
        private readonly ITestOutputHelper _output;

        public TurtleTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static readonly (string name, string rawSymbol)[] AllSymbols = new[]
        {
            ("600989", "STOCK_SPOT_SHSE.600989"), ("ETH", "COIN_FUTURES_ETHUSDT"),
            ("XAU", "COIN_FUTURES_XAUUSDT"), ("LTC", "COIN_FUTURES_LTCUSDT"),
            ("lh2605", "FUTURES_FUTURES_DCE.lh2605"), ("fu2605", "FUTURES_FUTURES_SHFE.fu2605"),
            ("m2605", "FUTURES_FUTURES_DCE.m2605"), ("600549", "STOCK_SPOT_SHSE.600549"),
            ("c2605", "FUTURES_FUTURES_DCE.c2605"), ("000657", "STOCK_SPOT_SZSE.000657"),
            ("510300", "STOCK_SPOT_SHSE.510300"), ("600089", "STOCK_SPOT_SHSE.600089"),
            ("300136", "STOCK_SPOT_SZSE.300136"), ("002539", "STOCK_SPOT_SZSE.002539"),
            ("600999", "STOCK_SPOT_SHSE.600999"), ("600459", "STOCK_SPOT_SHSE.600459"),
            ("002338", "STOCK_SPOT_SZSE.002338"), ("003029", "STOCK_SPOT_SZSE.003029"),
            ("601116", "STOCK_SPOT_SHSE.601116"), ("600621", "STOCK_SPOT_SHSE.600621"),
            ("159915", "STOCK_SPOT_SZSE.159915"), ("518880", "STOCK_SPOT_SHSE.518880"),
            ("601360", "STOCK_SPOT_SHSE.601360"), ("159869", "STOCK_SPOT_SZSE.159869"),
            ("000563", "STOCK_SPOT_SZSE.000563"), ("510760", "STOCK_SPOT_SHSE.510760"),
            ("601952", "STOCK_SPOT_SHSE.601952"), ("002176", "STOCK_SPOT_SZSE.002176"),
            ("603918", "STOCK_SPOT_SHSE.603918"), ("002229", "STOCK_SPOT_SZSE.002229"),
            ("002165", "STOCK_SPOT_SZSE.002165"), ("000799", "STOCK_SPOT_SZSE.000799"),
            ("600111", "STOCK_SPOT_SHSE.600111"), ("600536", "STOCK_SPOT_SHSE.600536"),
            ("300454", "STOCK_SPOT_SZSE.300454"), ("002672", "STOCK_SPOT_SZSE.002672"),
            ("302132", "STOCK_SPOT_SZSE.302132"), ("002112", "STOCK_SPOT_SZSE.002112"),
            ("002261", "STOCK_SPOT_SZSE.002261"), ("002792", "STOCK_SPOT_SZSE.002792"),
        };

        private static readonly Period[] TestPeriods = { Period.TIME_1D, Period.TIME_15M };

        private static readonly Dictionary<Period, int> PeriodLimits = new()
        {
            { Period.TIME_1D, 365 },
            { Period.TIME_15M, 8640 }
        };

        private static string RawToMkt(string rawSymbol)
        {
            var strs = rawSymbol.Split(new char[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (strs.Length >= 3)
                return strs[1] + "_" + string.Join("_", strs, 2, strs.Length - 2);
            return rawSymbol;
        }

        private static Dictionary<string, object> V(params (string k, object v)[] kvs)
        {
            var d = new Dictionary<string, object>();
            foreach (var (k, v) in kvs) d[k] = v;
            return d;
        }

        // ==================== 结果落盘（断点续跑） ====================

        private static readonly string CkptPath =
            Path.Combine(Path.GetTempPath(), "qjy_turtle_results.csv");

        private static Dictionary<string, (int trades, int wins, decimal profit, decimal dd)> LoadCkpt()
        {
            var dic = new Dictionary<string, (int, int, decimal, decimal)>();
            if (!File.Exists(CkptPath)) return dic;
            foreach (var line in File.ReadAllLines(CkptPath))
            {
                var p = line.Split(',');
                if (p.Length < 5) continue;
                dic[p[0]] = (int.Parse(p[1]), int.Parse(p[2]), decimal.Parse(p[3]), decimal.Parse(p[4]));
            }
            return dic;
        }

        private static void AppendCkpt(string key, int trades, int wins, decimal profit, decimal dd)
        {
            File.AppendAllText(CkptPath, $"{key},{trades},{wins},{profit},{dd}{Environment.NewLine}");
        }

        // ==================== 变体运行器（正式口径） ====================

        private void RunVariant(string vName, Dictionary<string, object> ov, Period[] periods)
        {
            RunCore(vName, () => new TurtleTrading(), ov, periods);
        }

        private void RunCore(string vName, Func<StgBase> make, Dictionary<string, object> ov, Period[] periods)
        {
            var ckpt = LoadCkpt();
            var results = new List<(Period period, int trades, int wins, decimal profit)>();
            foreach (var (name, rawSymbol) in AllSymbols)
            {
                foreach (var period in periods)
                {
                    string key = $"{vName}|{rawSymbol}|{period}";
                    if (ckpt.TryGetValue(key, out var cached))
                    {
                        results.Add((period, cached.trades, cached.wins, cached.profit));
                        continue;
                    }
                    var quotes = KlineCache.LoadFromCacheOnly(rawSymbol, period, PeriodLimits[period]);
                    if (quotes == null || quotes.Count < 60) continue;
                    try
                    {
                        var stg = make();
                        var r = BacktestEngine.RunSingleSymbol(stg, RawToMkt(rawSymbol), quotes, period, ov);
                        AppendCkpt(key, r.TradeCount, r.WinCount, r.TotalProfit, r.MaxDrawdown);
                        results.Add((period, r.TradeCount, r.WinCount, r.TotalProfit));
                    }
                    catch { }
                }
            }
            foreach (var period in periods)
            {
                var pr = results.Where(x => x.period == period).ToList();
                var trades = pr.Sum(x => x.trades);
                var wins = pr.Sum(x => x.wins);
                var profit = pr.Sum(x => x.profit);
                var profitable = pr.Count(x => x.profit > 0);
                _output.WriteLine($"{vName,-16} [{period}] trades:{trades} win:{(trades > 0 ? 100.0 * wins / trades : 0):F1}% profit:{profit:F2} profitable:{profitable}/{pr.Count}");
            }
            _output.WriteLine($"{vName,-16} [TOTAL] trades:{results.Sum(x => x.trades)} profit:{results.Sum(x => x.profit):F2}");
        }

        // ==================== 变体定义 ====================

        private static readonly List<(string name, Dictionary<string, object> ov)> Variants = new()
        {
            // 经典海龟（理论原版）。所有变体显式指定 systemType/enablePyramiding，不依赖策略默认值
            ("classic_dual", V(("systemType", 0), ("enablePyramiding", 0))), // 双系统 20/10 + 55/20，无加仓，盈利过滤
            ("classic_s1", V(("systemType", 1), ("enablePyramiding", 0))),   // 仅系统1 20/10
            ("classic_s2", V(("systemType", 2), ("enablePyramiding", 0))),   // 仅系统2 55/20
            ("dual_pyr", V(("systemType", 0), ("enablePyramiding", 1))),     // 双系统 + 0.5N金字塔（原版海龟含加仓）
            ("s2_pyr", V(("systemType", 2), ("enablePyramiding", 1))),
            // 实验室已验证的通道参数（突破75），套海龟资金管理
            ("don75", V(("systemType", 1), ("entryPeriod", 75), ("exitPeriod", 25), ("useLastTradeFilter", 0), ("enablePyramiding", 0))),
            ("don75_pyr", V(("systemType", 1), ("entryPeriod", 75), ("exitPeriod", 25), ("useLastTradeFilter", 0), ("enablePyramiding", 1))),
        };

        // ==================== 测试入口 ====================

        /// <summary>
        /// 日线快速扫描：7个变体 × 40品种（先用日线选型，再上15M全量）
        /// </summary>
        [Fact]
        public void Turtle_Sweep_Daily()
        {
            foreach (var (vName, ov) in Variants)
                RunVariant(vName, ov, new[] { Period.TIME_1D });
        }

        /// <summary>
        /// 经典海龟双系统全量基线（1D + 15M，正式口径）
        /// </summary>
        [Fact]
        public void Turtle_Baseline_Full()
        {
            RunVariant("classic_dual", V(("systemType", 0), ("enablePyramiding", 0)), TestPeriods);
        }

        /// <summary>
        /// 金字塔双系统全量基线（1D + 15M）
        /// </summary>
        [Fact]
        public void Turtle_DualPyr_Full()
        {
            RunVariant("dual_pyr", V(("systemType", 0), ("enablePyramiding", 1)), TestPeriods);
        }

        /// <summary>
        /// 唐奇安75+海龟资金管理全量基线（1D + 15M）
        /// </summary>
        [Fact]
        public void Turtle_Don75Pyr_Full()
        {
            RunVariant("don75_pyr",
                V(("systemType", 1), ("entryPeriod", 75), ("exitPeriod", 25), ("useLastTradeFilter", 0), ("enablePyramiding", 1)),
                TestPeriods);
        }

        private static Dictionary<string, object> S2PyrArgs =>
            V(("systemType", 2), ("enablePyramiding", 1));

        /// <summary>
        /// 系统2+金字塔全量基线（1D + 15M）：日线扫描的最优配置
        /// </summary>
        [Fact]
        public void Turtle_S2Pyr_Full()
        {
            RunVariant("s2_pyr", S2PyrArgs, TestPeriods);
        }

        /// <summary>
        /// 敞口公平对照：money=2500×4单位=峰值1万，与单仓策略的10000对齐
        /// </summary>
        [Fact]
        public void Turtle_S2Pyr_FairExposure_Daily()
        {
            RunVariant("s2pyr_m2500",
                V(("systemType", 2), ("enablePyramiding", 1), ("money", 2500m)),
                new[] { Period.TIME_1D });
        }

        // ==================== 全趋势策略台架（各自默认参数，同一口径） ====================

        private static readonly List<(string label, Func<StgBase> make)> TrendBench = new()
        {
            ("bench_Turtle", () => new TurtleTrading()),        // 定型默认=s2_pyr
            ("bench_Aberration", () => new Aberration()),
            ("bench_Andromeda", () => new Andromeda()),
            ("bench_DonchianATR", () => new DonchianATR()),
            ("bench_DonchianChannel", () => new DonchianChannel()),
            ("bench_DualThrust", () => new DualThrust()),
            ("bench_EMA_ADX", () => new EMA_ADX()),
            ("bench_EMA_ADX_DI", () => new EMA_ADX_DI()),
            ("bench_EMA_Standard", () => new EMA_Standard()),
            ("bench_MACDStandard", () => new MACDStandard()),
            ("bench_MACD_Fourier", () => new MACD_Fourier()),
            ("bench_MACross", () => new MACross()),
            ("bench_MomentumBreakout", () => new MomentumBreakout()),
            ("bench_RUMI", () => new RUMI()),
            ("bench_SMA", () => new SMA()),
            ("bench_Fourier", () => new FourierTransform()),    // 历史+8741 最强非趋势目录对手
            ("bench_ChanLunBi", () => new ChanLunBi()),         // 历史+9727
            ("bench_VolAdaptiveTrend", () => new VolatilityAdaptiveTrend()),
            ("bench_VolBreakout", () => new VolatilityBreakout()),
        };

        /// <summary>
        /// 全趋势策略日线台架（快速排名）
        /// </summary>
        [Fact]
        public void Bench_Trend_Daily()
        {
            foreach (var (label, make) in TrendBench)
                RunCore(label, make, null, new[] { Period.TIME_1D });
        }

        /// <summary>
        /// 全趋势策略 1D+15M 全量台架（慢，建议后台跑，结果落盘断点续跑）
        /// </summary>
        [Fact]
        public void Bench_Trend_Full()
        {
            foreach (var (label, make) in TrendBench)
                RunCore(label, make, null, TestPeriods);
        }

        // ==================== 样本外验证：前后窗口切分 ====================

        private void RunWindow(string vName, Dictionary<string, object> ov, bool takeFirst)
        {
            string win = takeFirst ? "early" : "late";
            var ckpt = LoadCkpt();
            var results = new List<(Period period, int trades, int wins, decimal profit)>();
            foreach (var (name, rawSymbol) in AllSymbols)
            {
                foreach (var period in TestPeriods)
                {
                    string key = $"{vName}|{win}|{rawSymbol}|{period}";
                    if (ckpt.TryGetValue(key, out var cached))
                    {
                        results.Add((period, cached.trades, cached.wins, cached.profit));
                        continue;
                    }
                    var full = KlineCache.LoadFromCacheOnly(rawSymbol, period, PeriodLimits[period]);
                    if (full == null || full.Count < 250) continue;
                    int take = period == Period.TIME_1D ? 250 : 5000;
                    if (take > full.Count) take = full.Count;
                    var quotes = takeFirst
                        ? full.Take(take).ToList()
                        : full.Skip(full.Count - take).ToList();
                    if (quotes.Count < 200) continue;
                    try
                    {
                        var stg = new TurtleTrading();
                        var r = BacktestEngine.RunSingleSymbol(stg, RawToMkt(rawSymbol), quotes, period, ov);
                        AppendCkpt(key, r.TradeCount, r.WinCount, r.TotalProfit, r.MaxDrawdown);
                        results.Add((period, r.TradeCount, r.WinCount, r.TotalProfit));
                    }
                    catch { }
                }
            }
            foreach (var period in TestPeriods)
            {
                var pr = results.Where(x => x.period == period).ToList();
                var trades = pr.Sum(x => x.trades);
                var wins = pr.Sum(x => x.wins);
                var profit = pr.Sum(x => x.profit);
                var profitable = pr.Count(x => x.profit > 0);
                _output.WriteLine($"{vName}[{win}] [{period}] trades:{trades} win:{(trades > 0 ? 100.0 * wins / trades : 0):F1}% profit:{profit:F2} profitable:{profitable}/{pr.Count}");
            }
            _output.WriteLine($"{vName}[{win}] [TOTAL] trades:{results.Sum(x => x.trades)} profit:{results.Sum(x => x.profit):F2}");
        }

        [Fact]
        public void OOS_Turtle_S2Pyr_Early() { RunWindow("s2_pyr", S2PyrArgs, true); }

        [Fact]
        public void OOS_Turtle_S2Pyr_Late() { RunWindow("s2_pyr", S2PyrArgs, false); }
    }
}
