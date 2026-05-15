using Common;
using Model;
using QjySDK;
using QjySDK.Stg;
using stgInterface;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    /// <summary>
    /// 验证 BollRsiShortReversion 每笔 trade.price 是否等于触发它那根 bar 的 Close。
    /// 通过 backtest 模拟（本地撮合，与服务端 TCP/Redis 解耦），策略代码与实盘一致。
    /// </summary>
    public class BollRsiAlignmentTests
    {
        private readonly ITestOutputHelper _output;
        private const string RawSymbol = "COIN_FUTURES_ETHUSDT";
        private const string MktSymbol = "FUTURES_ETHUSDT";

        public BollRsiAlignmentTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private record BarTradePair(int BarIdx, DateTime Dt, decimal Open, decimal High, decimal Low, decimal Close, OrderType OT, decimal Price, decimal Num);

        [Fact]
        public void BollRsiShortReversion_TradePriceMustMatchBarClose()
        {
            Assert.True(TDEngineDataLoader.IsAvailable(), "TDEngine 不可用");
            var quotes = TDEngineDataLoader.LoadKlines(RawSymbol, Period.TIME_5M, 3000);
            Assert.True(quotes.Count > 100, $"loaded {quotes.Count} bars");
            _output.WriteLine($"loaded {quotes.Count} bars, {quotes[0].Date:yyyy-MM-dd HH:mm} -> {quotes[^1].Date:yyyy-MM-dd HH:mm}");

            var stg = new BollRsiShortReversion();
            var cts = StgTestHelper.InitForTest(stg, MktSymbol);
            try
            {
                // 默认 mode=0 双向，sendMode=0 立即；按当前 GetStgDesc() 默认值
                var tu = new TableUnit
                {
                    MktSymbol = MktSymbol,
                    QuoteList = new List<SkQuote>(),
                    Period = Period.TIME_5M,
                };

                var pairs = new List<BarTradePair>();

                for (int i = 0; i < quotes.Count; i++)
                {
                    var q = quotes[i];
                    tu.QuoteList.Add(q);
                    stg.OnBar(Period.TIME_5M, tu, true, null);
                    var trades = StgTestHelper.DrainTrades(stg);
                    foreach (var t in trades)
                    {
                        pairs.Add(new BarTradePair(i, q.Date, q.Open, q.High, q.Low, q.Close, t.OT, t.Price, t.Num));
                    }
                }

                _output.WriteLine($"total trades = {pairs.Count}");
                int mismatch = 0;
                int shown = 0;
                foreach (var p in pairs)
                {
                    if (p.Price != p.Close)
                    {
                        ++mismatch;
                        if (shown < 20)
                        {
                            _output.WriteLine($"MISMATCH bar#{p.BarIdx} dt={p.Dt:yyyy-MM-dd HH:mm:ss} O={p.Open} H={p.High} L={p.Low} C={p.Close} -> {p.OT} @ {p.Price} num={p.Num}");
                            ++shown;
                        }
                    }
                }

                _output.WriteLine($"trades total={pairs.Count} mismatch={mismatch}");

                // 同时把每一笔成交时间和价格打印到 CSV 方便用户对照 K 线图
                var outDir = Path.Combine(AppContext.BaseDirectory, "_align_out");
                Directory.CreateDirectory(outDir);
                var csvPath = Path.Combine(outDir, $"bollrsi_trades_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                using (var sw = new StreamWriter(csvPath))
                {
                    sw.WriteLine("bar_idx,dt,open,high,low,close,ot,price,num,price_minus_close");
                    foreach (var p in pairs)
                    {
                        sw.WriteLine($"{p.BarIdx},{p.Dt:yyyy-MM-dd HH:mm:ss},{p.Open},{p.High},{p.Low},{p.Close},{p.OT},{p.Price},{p.Num},{p.Price - p.Close}");
                    }
                }
                _output.WriteLine($"csv saved: {csvPath}");

                Assert.Equal(0, mismatch);
            }
            finally
            {
                cts.Cancel();
            }
        }
    }
}
