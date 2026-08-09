using Model;
using QjySDK.Stg;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class ReversalTurtleRealDataTests
    {
        private readonly ITestOutputHelper _output;

        public ReversalTurtleRealDataTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static readonly (string rawSymbol, string mktSymbol)[] Symbols =
        {
            ("COIN_FUTURES_ETHUSDT", "FUTURES_ETHUSDT"),
            ("COIN_FUTURES_LTCUSDT", "FUTURES_LTCUSDT"),
            ("STOCK_SPOT_SHSE.600989", "SPOT_SHSE.600989"),
            ("STOCK_SPOT_SHSE.510300", "SPOT_SHSE.510300"),
            ("STOCK_SPOT_SHSE.518880", "SPOT_SHSE.518880"),
            ("STOCK_SPOT_SZSE.159915", "SPOT_SZSE.159915"),
        };

        private static readonly (string name, Func<StgBase> create, Dictionary<string, object> args)[] Strategies =
        {
            ("ReversalTurtle", () => new TrendAwareReversalTurtleTrading(), new Dictionary<string, object>()),
            ("Turtle", () => new TurtleTrading(), Args(("systemType", 1), ("useLastTradeFilter", 0))),
            ("Donchian", () => new DonchianChannel(), new Dictionary<string, object>()),
        };

        [Fact]
        [Trait("Category", "RealData")]
        public void CompareStrategiesAcrossTdengineData()
        {
            var periods = new[]
            {
                (period: Period.TIME_1D, limit: 1500),
                (period: Period.TIME_15M, limit: 3000),
            };
            var aggregates = Strategies.ToDictionary(x => x.name, _ => new Aggregate());
            int dataSets = 0;

            foreach (var (rawSymbol, mktSymbol) in Symbols)
            {
                foreach (var (period, limit) in periods)
                {
                    var quotes = TDEngineDataLoader.LoadKlines(rawSymbol, period, limit);
                    if (quotes.Count < 100)
                    {
                        _output.WriteLine($"SKIP,{rawSymbol},{period},bars={quotes.Count}");
                        continue;
                    }

                    dataSets++;
                    foreach (var (name, create, args) in Strategies)
                    {
                        var r = BacktestEngine.RunSingleSymbol(create(), mktSymbol, quotes, period, args);
                        aggregates[name].Add(r);
                        _output.WriteLine(
                            $"ROW,{name},{rawSymbol},{period},{quotes.Count},trades={r.TradeCount},winRate={r.WinRate:F2},profit={r.TotalProfit:F2},annualized={r.AnnualizedReturn:F2}%,drawdown={r.MaxDrawdown:F2},drawdownRate={r.MaxDrawdownRate:F2}%,pf={r.ProfitFactor:F3},sharpe={r.SharpeRatio:F4}");
                    }
                }
            }

            foreach (var (name, _, _) in Strategies)
            {
                var a = aggregates[name];
                _output.WriteLine(
                    $"TOTAL,{name},sets={a.DataSets},profitable={a.ProfitableSets},trades={a.Trades},wins={a.Wins},winRate={a.WinRate:F2},profit={a.Profit:F2},avgAnnualized={a.AverageAnnualizedReturn:F2}%,avgDrawdown={a.AverageDrawdown:F2},avgDrawdownRate={a.AverageDrawdownRate:F2}%,avgSharpe={a.AverageSharpe:F4}");
            }

            Assert.True(dataSets >= 4, $"TDengine有效数据集不足：{dataSets}");
            Assert.All(aggregates.Values, aggregate => Assert.True(aggregate.Trades > 0, "对比策略在真实数据上没有产生交易"));
        }

        private static Dictionary<string, object> Args(params (string key, object value)[] values)
        {
            return values.ToDictionary(x => x.key, x => x.value);
        }

        private sealed class Aggregate
        {
            public int DataSets { get; private set; }
            public int ProfitableSets { get; private set; }
            public int Trades { get; private set; }
            public int Wins { get; private set; }
            public decimal Profit { get; private set; }
            public decimal DrawdownSum { get; private set; }
            public double DrawdownRateSum { get; private set; }
            public double SharpeSum { get; private set; }
            public double AnnualizedReturnSum { get; private set; }
            public double WinRate => Trades > 0 ? 100.0 * Wins / Trades : 0;
            public decimal AverageDrawdown => DataSets > 0 ? DrawdownSum / DataSets : 0;
            public double AverageDrawdownRate => DataSets > 0 ? DrawdownRateSum / DataSets : 0;
            public double AverageSharpe => DataSets > 0 ? SharpeSum / DataSets : 0;
            public double AverageAnnualizedReturn => DataSets > 0 ? AnnualizedReturnSum / DataSets : 0;

            public void Add(BacktestResult result)
            {
                DataSets++;
                if (result.TotalProfit > 0) ProfitableSets++;
                Trades += result.TradeCount;
                Wins += result.WinCount;
                Profit += result.TotalProfit;
                DrawdownSum += result.MaxDrawdown;
                DrawdownRateSum += result.MaxDrawdownRate;
                SharpeSum += result.SharpeRatio;
                AnnualizedReturnSum += result.AnnualizedReturn;
            }
        }
    }
}
