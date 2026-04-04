using Common;
using Model;
using QjySDK.Stg;
using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class StrategyBacktestTests
    {
        private readonly ITestOutputHelper _output;

        public StrategyBacktestTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private void SkipIfNoData(params string[] symbols)
        {
            if (!TDEngineDataLoader.IsAvailable())
                Assert.Fail("[SKIP] TDEngine 不可用 (192.168.0.28:6030)");

            foreach (var s in symbols)
            {
                if (!TDEngineDataLoader.HasData(s, 60))
                    Assert.Fail($"[SKIP] 标的 {s} 数据不足60条");
            }
        }

        private void OutputResult(BacktestResult r)
        {
            _output.WriteLine(r.ToReport());
        }

        #region ==================== 套利策略 (7) ====================

        [Fact]
        public void Test_PairTrading()
        {
            var syms = new[] { "SPOT_BTCUSDT", "SPOT_ETHUSDT" };
            SkipIfNoData(syms);

            var data = new Dictionary<string, List<SkQuote>>();
            foreach (var s in syms) data[s] = TDEngineDataLoader.LoadDailyKlines(s, 2000);

            var stg = new PairTrading();
            var result = BacktestEngine.RunMultiSymbol(stg, data);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_CointegrationArbitrage()
        {
            // 现货+期货对更容易产生协整关系，2000条提供充足的回归窗口
            var syms = new[] { "SPOT_BTCUSDT", "FUTURES_BTCUSDT", "SPOT_ETHUSDT", "FUTURES_ETHUSDT" };
            SkipIfNoData(syms);

            var data = new Dictionary<string, List<SkQuote>>();
            foreach (var s in syms) data[s] = TDEngineDataLoader.LoadDailyKlines(s, 2000);

            var stg = new CointegrationArbitrage();
            var result = BacktestEngine.RunMultiSymbol(stg, data);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_ButterflyArbitrage()
        {
            var syms = new[] { "SPOT_BTCUSDT", "SPOT_ETHUSDT", "SPOT_LTCUSDT" };
            SkipIfNoData(syms);

            var data = new Dictionary<string, List<SkQuote>>();
            foreach (var s in syms) data[s] = TDEngineDataLoader.LoadDailyKlines(s, 2000);

            var stg = new ButterflyArbitrage();
            var result = BacktestEngine.RunMultiSymbol(stg, data);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_LeadLagArbitrage()
        {
            // BTC领先ETH领先LTC，大币种领先小币种；加期货提供更多领先滞后模式
            var syms = new[] { "SPOT_BTCUSDT", "SPOT_ETHUSDT", "SPOT_LTCUSDT", "FUTURES_BTCUSDT", "FUTURES_ETHUSDT" };
            SkipIfNoData(syms);

            var data = new Dictionary<string, List<SkQuote>>();
            foreach (var s in syms) data[s] = TDEngineDataLoader.LoadDailyKlines(s, 2000);

            var stg = new LeadLagArbitrage();
            var result = BacktestEngine.RunMultiSymbol(stg, data);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_CrossSymbolMomentum()
        {
            var syms = new[] { "SPOT_BTCUSDT", "SPOT_ETHUSDT", "SPOT_LTCUSDT", "FUTURES_ETHUSDT" };
            SkipIfNoData(syms);

            var data = new Dictionary<string, List<SkQuote>>();
            foreach (var s in syms) data[s] = TDEngineDataLoader.LoadDailyKlines(s, 2000);

            var stg = new CrossSymbolMomentum();
            var result = BacktestEngine.RunMultiSymbol(stg, data);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_SpreadArbitrage()
        {
            var syms = new[] { "SPOT_BTCUSDT", "SPOT_ETHUSDT", "SPOT_LTCUSDT", "FUTURES_ETHUSDT" };
            SkipIfNoData(syms);

            var data = new Dictionary<string, List<SkQuote>>();
            foreach (var s in syms) data[s] = TDEngineDataLoader.LoadDailyKlines(s, 2000);

            var stg = new SpreadArbitrage();
            var result = BacktestEngine.RunMultiSymbol(stg, data);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_MeanReversionBasket()
        {
            var syms = new[] { "SPOT_BTCUSDT", "SPOT_ETHUSDT", "SPOT_LTCUSDT", "FUTURES_ETHUSDT" };
            SkipIfNoData(syms);

            var data = new Dictionary<string, List<SkQuote>>();
            foreach (var s in syms) data[s] = TDEngineDataLoader.LoadDailyKlines(s, 2000);

            var stg = new MeanReversionBasket();
            var result = BacktestEngine.RunMultiSymbol(stg, data);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        #endregion

        #region ==================== 波动率策略 (4) ====================

        [Fact]
        public void Test_VolatilityBreakout()
        {
            // BTC期货 2385条，squeezeLookback=120需要多个Squeeze周期
            var sym = "FUTURES_BTCUSDT";
            SkipIfNoData(sym);

            var quotes = TDEngineDataLoader.LoadDailyKlines(sym, 2000);
            var stg = new VolatilityBreakout();
            var result = BacktestEngine.RunSingleSymbol(stg, sym, quotes);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_VolatilityMeanReversion()
        {
            // BTC 2961条日K，加2000条满足hvRankPeriod=252的需求
            var sym = "SPOT_BTCUSDT";
            SkipIfNoData(sym);

            var quotes = TDEngineDataLoader.LoadDailyKlines(sym, 2000);
            var stg = new VolatilityMeanReversion();
            var result = BacktestEngine.RunSingleSymbol(stg, sym, quotes);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_VolatilityCone()
        {
            // minBars=longP(60)+rankLookback(252)+10=322, 需要2000条才能评估足够多的bar
            var sym = "FUTURES_BTCUSDT";
            SkipIfNoData(sym);

            var quotes = TDEngineDataLoader.LoadDailyKlines(sym, 2000);
            var stg = new VolatilityCone();
            var result = BacktestEngine.RunSingleSymbol(stg, sym, quotes);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_VolatilityAdaptiveTrend()
        {
            // BTC 2000条日K，趋势明显，ADX容易超过20
            var sym = "SPOT_BTCUSDT";
            SkipIfNoData(sym);

            var quotes = TDEngineDataLoader.LoadDailyKlines(sym, 2000);
            var stg = new VolatilityAdaptiveTrend();
            var result = BacktestEngine.RunSingleSymbol(stg, sym, quotes);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        #endregion

        #region ==================== 网格策略 (4) ====================

        [Fact]
        public void Test_GridTrading()
        {
            var sym = "FUTURES_BTCUSDT";
            SkipIfNoData(sym);

            var quotes = TDEngineDataLoader.LoadDailyKlines(sym, 2000);
            var stg = new GridTrading();
            var result = BacktestEngine.RunSingleSymbol(stg, sym, quotes);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_MartingaleGrid()
        {
            var sym = "FUTURES_BTCUSDT";
            SkipIfNoData(sym);

            var quotes = TDEngineDataLoader.LoadDailyKlines(sym, 2000);
            var stg = new MartingaleGrid();
            var result = BacktestEngine.RunSingleSymbol(stg, sym, quotes);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_TrendGrid()
        {
            var sym = "FUTURES_BTCUSDT";
            SkipIfNoData(sym);

            var quotes = TDEngineDataLoader.LoadDailyKlines(sym, 2000);
            var stg = new TrendGrid();
            var result = BacktestEngine.RunSingleSymbol(stg, sym, quotes);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        [Fact]
        public void Test_InfinityGrid()
        {
            var sym = "FUTURES_BTCUSDT";
            SkipIfNoData(sym);

            var quotes = TDEngineDataLoader.LoadDailyKlines(sym, 2000);
            var stg = new InfinityGrid();
            var result = BacktestEngine.RunSingleSymbol(stg, sym, quotes);
            OutputResult(result);

            Assert.True(result.TotalBars > 0, "应有回测数据");
        }

        #endregion

        #region ==================== 混合专家策略 ====================

        [Fact]
        public void Test_MoEPredict_ETH_5M()
        {
            var sym = "SPOT_ETHUSDT";
            var period = Period.TIME_5M;

            if (!TDEngineDataLoader.IsAvailable())
                Assert.Fail("[SKIP] TDEngine 不可用");
            if (!TDEngineDataLoader.HasData(sym, period, 500))
                Assert.Fail($"[SKIP] {sym} 5M 数据不足500条");

            var quotes = TDEngineDataLoader.LoadKlines(sym, period, 2000);
            _output.WriteLine($"加载 {sym} {period} K线: {quotes.Count} 条");

            var stg = new MoEPredict();
            var result = BacktestEngine.RunSingleSymbol(stg, sym, quotes, period);
            OutputResult(result);

            Assert.True(result.TradeCount > 0, "应有交易");
            Assert.True(result.WinRate > 55, $"胜率 {result.WinRate:F1}% 未达到55%目标");
        }

        #endregion
    }
}
