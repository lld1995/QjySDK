using Common;
using Model;
using MySql.Data.MySqlClient;
using QjySDK.Stg;
using stgInterface;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver;
using TDengine.Driver.Client;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    #region Backtest Result

    public class BacktestResult
    {
        public string StrategyName { get; set; } = "";
        public string Symbols { get; set; } = "";
        public string PeriodName { get; set; } = "";
        public int TotalBars { get; set; }
        public int TradeCount { get; set; }
        public int WinCount { get; set; }
        public int LossCount { get; set; }
        public decimal TotalProfit { get; set; }
        public decimal MaxDrawdown { get; set; }
        public double WinRate => TradeCount > 0 ? (double)WinCount / TradeCount * 100 : 0;
        public decimal AvgWin { get; set; }
        public decimal AvgLoss { get; set; }
        public double ProfitFactor => AvgLoss != 0 ? (double)(AvgWin / Math.Abs(AvgLoss)) : 0;
        public double SharpeRatio { get; set; }
        public int Buy1Count { get; set; }
        public int Sell1Count { get; set; }
        public int Buy2Count { get; set; }
        public int Sell2Count { get; set; }
        public int Buy3Count { get; set; }
        public int Sell3Count { get; set; }

        public string ToReport()
        {
            return $@"
========== {StrategyName} ==========
品种: {Symbols}
周期: {PeriodName}
K线数: {TotalBars}
交易次数: {TradeCount}
胜: {WinCount}  负: {LossCount}
胜率: {WinRate:F1}%
总收益: {TotalProfit:F2}
最大回撤: {MaxDrawdown:F2}
平均盈利: {AvgWin:F2}  平均亏损: {AvgLoss:F2}
盈亏比: {ProfitFactor:F2}
夏普率: {SharpeRatio:F4}
买卖点统计: Buy1={Buy1Count} Sell1={Sell1Count} Buy2={Buy2Count} Sell2={Sell2Count} Buy3={Buy3Count} Sell3={Sell3Count}
====================================";
        }
    }

    #endregion

    #region TestConfig — 从本地文件读取连接配置

    public static class TestConfig
    {
        private static readonly Lazy<JsonDocument> _doc = new Lazy<JsonDocument>(() =>
        {
            // 依次尝试: 测试目录 → 输出目录
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "test_config.json"),
                Path.Combine(AppContext.BaseDirectory, "test_config.json")
            };
            foreach (var p in candidates)
            {
                var full = Path.GetFullPath(p);
                if (File.Exists(full))
                    return JsonDocument.Parse(File.ReadAllText(full));
            }
            throw new FileNotFoundException(
                "test_config.json 未找到，请在 QjySDK.Tests 目录下创建该文件，格式:\n" +
                "{\n  \"TDEngine\": \"host=...;port=6030;username=root;password=xxx\",\n" +
                "  \"MySQL\": \"Server=...;Database=...;User=root;Password=xxx;Charset=utf8;Pooling=True\"\n}");
        });

        public static string TDEngine => _doc.Value.RootElement.GetProperty("TDEngine").GetString()!;
        public static string MySQL => _doc.Value.RootElement.GetProperty("MySQL").GetString()!;
    }

    #endregion

    #region SymbolLoader — 从 MySQL 加载真实 Symbol 数据

    public static class SymbolLoader
    {

        private static readonly Dictionary<string, Symbol> _cache = new Dictionary<string, Symbol>();

        /// <summary>
        /// 从 MySQL symbol 表加载真实的 multiplier / margin_ratio / symbol_type
        /// mktSymbol 格式: "SPOT_BTCUSDT" / "FUTURES_XAUUSDT" / "SPOT_SHSE.600519"
        /// </summary>
        public static Symbol Load(string mktSymbol)
        {
            if (_cache.TryGetValue(mktSymbol, out var cached))
                return cached;

            // 解析 mkt_type 和 symbol_
            var parts = mktSymbol.Split('_', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return DefaultSymbol(mktSymbol);

            // MarketType: SPOT=0, FUTURES=1
            int mktType;
            if (parts[0].Equals("SPOT", StringComparison.OrdinalIgnoreCase))
                mktType = 0;
            else if (parts[0].Equals("FUTURES", StringComparison.OrdinalIgnoreCase))
                mktType = 1;
            else
                mktType = 0;

            string symbolName = parts[1]; // e.g. "BTCUSDT", "SHSE.600519"

            try
            {
                using var conn = new MySqlConnection(TestConfig.MySQL);
                conn.Open();
                using var cmd = new MySqlCommand(
                    "SELECT multiplier, margin_ratio, symbol_type FROM symbol WHERE mkt_type=@mkt AND symbol_=@sym LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@mkt", mktType);
                cmd.Parameters.AddWithValue("@sym", symbolName);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var rawMul = reader.GetDecimal(0);
                    var rawMar = reader.GetDecimal(1);
                    var sym = new Symbol
                    {
                        multiplier = rawMul > 0 ? rawMul : 1m,
                        margin_ratio = rawMar > 0 ? rawMar : 1m,
                        symbol_type = reader.GetInt32(2)
                    };
                    _cache[mktSymbol] = sym;
                    Console.WriteLine($"[SymbolLoader] {mktSymbol} → multiplier={sym.multiplier}, margin_ratio={sym.margin_ratio}, symbol_type={sym.symbol_type}");
                    return sym;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SymbolLoader] MySQL查询失败 {mktSymbol}: {ex.Message}");
            }

            return DefaultSymbol(mktSymbol);
        }

        /// <summary>
        /// 批量预加载
        /// </summary>
        public static void PreLoad(params string[] mktSymbols)
        {
            foreach (var s in mktSymbols)
                Load(s);
        }

        private static Symbol DefaultSymbol(string mktSymbol)
        {
            Console.WriteLine($"[SymbolLoader] {mktSymbol} 未找到，使用默认值 multiplier=1, margin_ratio=1");
            var sym = new Symbol { multiplier = 1m, margin_ratio = 1m, symbol_type = 0 };
            _cache[mktSymbol] = sym;
            return sym;
        }
    }

    #endregion

    #region StgTestHelper — 反射工具

    public static class StgTestHelper
    {
        private static readonly FieldInfo _rtrField =
            typeof(StgBase).GetField("_rtr", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly FieldInfo _stcField =
            typeof(StgBase).GetField("_stc", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly PropertyInfo _argDicProp =
            typeof(StgBase).GetProperty("ArgDic", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly FieldInfo _pendingGetSymbolField =
            typeof(SimpleTcpClient).GetField("_pendingGetSymbol", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly FieldInfo _writerField =
            typeof(SimpleTcpClient).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly FieldInfo _isConnectedField =
            typeof(SimpleTcpClient).GetField("_isConnected", BindingFlags.NonPublic | BindingFlags.Instance)!;

        /// <summary>
        /// 初始化策略用于测试：设置 ArgDic，用真实 Symbol 数据 resolve GetSymbol
        /// </summary>
        public static CancellationTokenSource InitForTest(StgBase stg, params string[] mktSymbols)
        {
            // 1. 调用 GetStgDesc 获取默认参数，然后设置 ArgDic
            var sd = stg.GetStgDesc();
            if (sd != null)
            {
                _argDicProp.SetValue(stg, sd.ArgDic);
            }

            // 2. 预加载所有标的的真实 Symbol 数据
            SymbolLoader.PreLoad(mktSymbols);

            // 3. 创建 SimpleTcpClient 并注入
            var stc = new SimpleTcpClient(stg);
            var ms = new MemoryStream();
            var sw = new StreamWriter(ms) { AutoFlush = true };
            _writerField.SetValue(stc, sw);
            _isConnectedField.SetValue(stc, true);
            _stcField.SetValue(stg, stc);

            // 4. 启动后台线程用真实 Symbol 数据 resolve GetSymbol 请求
            var cts = new CancellationTokenSource();
            var pendingDict = (Dictionary<string, TaskCompletionSource<Symbol>>)_pendingGetSymbolField.GetValue(stc)!;

            Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        lock (pendingDict)
                        {
                            foreach (var kv in pendingDict.ToList())
                            {
                                if (!kv.Value.Task.IsCompleted)
                                {
                                    var realSymbol = SymbolLoader.Load(kv.Key);
                                    kv.Value.SetResult(realSymbol);
                                }
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(5, cts.Token).ConfigureAwait(false);
                }
            }, cts.Token);

            return cts;
        }

        /// <summary>
        /// 读取 _rtr 并清空，返回本轮交易记录
        /// </summary>
        public static List<RemoteTradeRecord> DrainTrades(StgBase stg)
        {
            var rtr = (List<RemoteTradeRecord>)_rtrField.GetValue(stg)!;
            var copy = new List<RemoteTradeRecord>(rtr);
            rtr.Clear();
            return copy;
        }
    }

    #endregion

    #region TDEngine 数据加载

    public static class TDEngineDataLoader
    {
        private static string ConnStr => TestConfig.TDEngine;

        /// <summary>
        /// 将 rawSymbol (COIN_FUTURES_BTCUSDT) 转为 mktSymbol (FUTURES_BTCUSDT)
        /// 如果已经是 mktSymbol 格式则直接返回
        /// </summary>
        private static string ToMktSymbol(string symbol)
        {
            var knownTypes = new[] { "COIN", "STOCK", "FUTURES" };
            var parts = symbol.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var first = parts[0].ToUpperInvariant();
                if (Array.Exists(knownTypes, t => t == first))
                {
                    var second = parts[1].ToUpperInvariant();
                    if (second == "SPOT" || second == "FUTURES")
                        return string.Join("_", parts, 1, parts.Length - 1);
                }
            }
            return symbol;
        }

        /// <summary>
        /// 从本地 TDEngine 加载日K数据
        /// </summary>
        public static List<SkQuote> LoadDailyKlines(string mktSymbol, int limitDays = 365)
        {
            return LoadKlines(mktSymbol, Period.TIME_1D, limitDays);
        }

        /// <summary>
        /// 从本地 TDEngine 加载指定周期的K线数据
        /// 支持 rawSymbol (COIN_FUTURES_BTCUSDT) 和 mktSymbol (FUTURES_BTCUSDT) 两种格式
        /// TDEngine表名格式: {mktType}_{symbol}_{period}，如 futures_btcusdt_time_1d
        /// </summary>
        public static List<SkQuote> LoadKlines(string mktSymbol, Period period, int limit = 5000)
        {
            var symbolForTable = ToMktSymbol(mktSymbol);
            var sp = symbolForTable + "_" + period;
            var tableName = sp.Replace(".", "").ToLower();

            var quotes = new List<SkQuote>();
            var builder = new ConnectionStringBuilder(ConnStr);
            using var client = DbDriver.Open(builder);
            client.Exec("use finance");

            var sql = $"select * from {tableName} order by ts desc limit {limit}";
            using var rows = client.Query(sql);
            if (rows.HasRows)
            {
                while (rows.Read())
                {
                    var q = new SkQuote();
                    q.Date = (DateTime)rows.GetValue(0);
                    q.Open = (decimal)float.Parse(rows.GetValue(1).ToString()!);
                    q.Close = (decimal)float.Parse(rows.GetValue(2).ToString()!);
                    q.Low = (decimal)float.Parse(rows.GetValue(3).ToString()!);
                    q.High = (decimal)float.Parse(rows.GetValue(4).ToString()!);
                    q.Amount = (decimal)float.Parse(rows.GetValue(5).ToString()!);
                    q.Volume = (decimal)float.Parse(rows.GetValue(6).ToString()!);
                    quotes.Add(q);
                }
            }

            quotes.Reverse(); // TDEngine desc → 正序
            return quotes;
        }

        /// <summary>
        /// 检测 TDEngine 连接是否可用
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                var builder = new ConnectionStringBuilder(ConnStr);
                using var client = DbDriver.Open(builder);
                client.Exec("use finance");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检测某个标的的日K数据是否存在且有足够数据
        /// </summary>
        public static bool HasData(string mktSymbol, int minBars = 60)
        {
            return HasData(mktSymbol, Period.TIME_1D, minBars);
        }

        /// <summary>
        /// 检测某个标的指定周期的数据是否存在且有足够数据
        /// </summary>
        public static bool HasData(string mktSymbol, Period period, int minBars = 60)
        {
            try
            {
                var quotes = LoadKlines(mktSymbol, period, minBars);
                return quotes.Count >= minBars;
            }
            catch
            {
                return false;
            }
        }
    }

    #endregion

    #region BacktestEngine — 回测引擎

    public class BacktestEngine
    {
        /// <summary>
        /// 模式A：单品种 bar-by-bar 回测（任意周期）
        /// </summary>
        public static BacktestResult RunSingleSymbol(
            StgBase stg, string mktSymbol, List<SkQuote> quotes, Period period = Period.TIME_1D)
        {
            var cts = StgTestHelper.InitForTest(stg, mktSymbol);
            try
            {
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
                    try
                    {
                        stg.OnBar(period, tu, true, null);
                    }
                    catch (Exception ex)
                    {
                        // 策略内部异常不中断回测，记录后继续
                        Console.WriteLine($"[OnBar] Bar {i} exception: {ex.Message}");
                    }

                    trades.AddRange(StgTestHelper.DrainTrades(stg));
                }

                var result = CalcResult(stg.GetType().Name, new[] { mktSymbol }, quotes.Count, trades, period);

                // 提取买卖点统计
                Dictionary<string, int> bsCounts = null;
                if (stg is ChanLun cl)
                    bsCounts = cl.GetBSPointCounts();

                if (bsCounts != null)
                {
                    result.Buy1Count = bsCounts.GetValueOrDefault("Buy1");
                    result.Sell1Count = bsCounts.GetValueOrDefault("Sell1");
                    result.Buy2Count = bsCounts.GetValueOrDefault("Buy2");
                    result.Sell2Count = bsCounts.GetValueOrDefault("Sell2");
                    result.Buy3Count = bsCounts.GetValueOrDefault("Buy3");
                    result.Sell3Count = bsCounts.GetValueOrDefault("Sell3");
                }

                return result;
            }
            finally
            {
                cts.Cancel();
            }
        }

        /// <summary>
        /// 模式B：多品种 OnGlobalIndicator + OnBar 回测（套利策略专用）
        /// </summary>
        public static BacktestResult RunMultiSymbol(
            StgBase stg, Dictionary<string, List<SkQuote>> symbolQuotes)
        {
            var cts = StgTestHelper.InitForTest(stg, symbolQuotes.Keys.ToArray());
            try
            {
                var period = Period.TIME_1D;

                // 构建 TableUnit 字典
                var tuDict = new Dictionary<string, TableUnit>();
                foreach (var kv in symbolQuotes)
                {
                    tuDict[kv.Key] = new TableUnit
                    {
                        QuoteList = new List<SkQuote>(),
                        MktSymbol = kv.Key,
                        Period = period
                    };
                }

                // 按时间对齐：取所有品种共有的时间交集
                var allDates = new SortedSet<DateTime>();
                var dateQuoteMap = new Dictionary<string, Dictionary<DateTime, SkQuote>>();
                foreach (var kv in symbolQuotes)
                {
                    var map = new Dictionary<DateTime, SkQuote>();
                    foreach (var q in kv.Value)
                        map[q.Date] = q;
                    dateQuoteMap[kv.Key] = map;
                    if (allDates.Count == 0)
                        allDates = new SortedSet<DateTime>(map.Keys);
                    else
                        allDates.IntersectWith(map.Keys);
                }

                var sortedDates = allDates.ToList();
                var trades = new List<RemoteTradeRecord>();

                for (int i = 0; i < sortedDates.Count; i++)
                {
                    var date = sortedDates[i];

                    // 添加当日数据到所有 TableUnit
                    foreach (var kv in tuDict)
                    {
                        if (dateQuoteMap[kv.Key].TryGetValue(date, out var q))
                            kv.Value.QuoteList.Add(q);
                    }

                    // OnGlobalIndicator — 套利策略核心
                    try
                    {
                        stg.OnGlobalIndicator(tuDict.Values.ToList());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OnGlobal] Bar {i} exception: {ex.Message}");
                    }

                    // OnBar for each symbol
                    foreach (var kv in tuDict)
                    {
                        try
                        {
                            stg.OnBar(period, kv.Value, true, null);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[OnBar] {kv.Key} Bar {i} exception: {ex.Message}");
                        }
                    }

                    trades.AddRange(StgTestHelper.DrainTrades(stg));
                }

                return CalcResult(
                    stg.GetType().Name,
                    symbolQuotes.Keys.ToArray(),
                    sortedDates.Count,
                    trades);
            }
            finally
            {
                cts.Cancel();
            }
        }

        /// <summary>
        /// 撮合计算：统计总收益、胜率、最大回撤、夏普率
        /// </summary>
        private static BacktestResult CalcResult(
            string strategyName, string[] symbols, int totalBars, List<RemoteTradeRecord> trades, Period period = Period.TIME_1D)
        {
            var result = new BacktestResult
            {
                StrategyName = strategyName,
                Symbols = string.Join(", ", symbols),
                PeriodName = period.ToString(),
                TotalBars = totalBars
            };

            // 按品种跟踪持仓
            var positions = new Dictionary<string, List<(OrderType ot, decimal price, decimal num)>>();

            decimal equity = 0m;
            decimal peakEquity = 0m;
            decimal maxDrawdown = 0m;
            var wins = new List<decimal>();
            var losses = new List<decimal>();

            foreach (var t in trades)
            {
                if (!positions.ContainsKey(t.MktSymbol))
                    positions[t.MktSymbol] = new List<(OrderType, decimal, decimal)>();

                var pos = positions[t.MktSymbol];

                if (t.OT == OrderType.BUY || t.OT == OrderType.SELL)
                {
                    // 开仓
                    pos.Add((t.OT, t.Price, t.Num));
                }
                else if (t.OT == OrderType.SELL_TO_COVER && pos.Count > 0)
                {
                    // 平多仓
                    var openTrades = pos.Where(p => p.ot == OrderType.BUY).ToList();
                    var totalNum = openTrades.Sum(p => p.num);
                    if (openTrades.Count > 0 && totalNum > 0)
                    {
                        var avgOpen = openTrades.Sum(p => p.price * p.num) / totalNum;
                        var pnl = (t.Price - avgOpen) * t.Num;
                        equity += pnl;

                        if (pnl > 0) wins.Add(pnl);
                        else losses.Add(pnl);

                        pos.RemoveAll(p => p.ot == OrderType.BUY);
                    }
                }
                else if (t.OT == OrderType.BUY_TO_COVER && pos.Count > 0)
                {
                    // 平空仓
                    var openTrades = pos.Where(p => p.ot == OrderType.SELL).ToList();
                    var totalNum2 = openTrades.Sum(p => p.num);
                    if (openTrades.Count > 0 && totalNum2 > 0)
                    {
                        var avgOpen = openTrades.Sum(p => p.price * p.num) / totalNum2;
                        var pnl = (avgOpen - t.Price) * t.Num;
                        equity += pnl;

                        if (pnl > 0) wins.Add(pnl);
                        else losses.Add(pnl);

                        pos.RemoveAll(p => p.ot == OrderType.SELL);
                    }
                }

                // 更新最大回撤
                if (equity > peakEquity) peakEquity = equity;
                var dd = peakEquity - equity;
                if (dd > maxDrawdown) maxDrawdown = dd;
            }

            result.TradeCount = wins.Count + losses.Count;
            result.WinCount = wins.Count;
            result.LossCount = losses.Count;
            result.TotalProfit = equity;
            result.MaxDrawdown = maxDrawdown;
            result.AvgWin = wins.Count > 0 ? wins.Average() : 0;
            result.AvgLoss = losses.Count > 0 ? losses.Average() : 0;

            // 计算夏普率 (Sharpe Ratio)
            var allPnl = new List<decimal>();
            allPnl.AddRange(wins);
            allPnl.AddRange(losses);
            if (allPnl.Count > 1)
            {
                double mean = (double)allPnl.Average();
                double variance = allPnl.Sum(p => Math.Pow((double)p - mean, 2)) / (allPnl.Count - 1);
                double stdDev = Math.Sqrt(variance);
                result.SharpeRatio = stdDev > 0 ? (mean / stdDev) * Math.Sqrt(252) : 0;
            }

            return result;
        }
    }

    #endregion
}
