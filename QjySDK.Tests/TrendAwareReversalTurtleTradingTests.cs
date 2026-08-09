using Common;
using QjySDK.Stg;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace QjySDK.Tests
{
    public class TrendAwareReversalTurtleTradingTests
    {
        [Fact]
        public void DefaultParameters_UseOnlyTurtleStyleExits()
        {
            var args = new TrendAwareReversalTurtleTrading().GetStgDesc().ArgDic;

            Assert.False(args.ContainsKey("takeProfitATR"));
            Assert.False(args.ContainsKey("breakEvenATR"));
            Assert.False(args.ContainsKey("trailingStartATR"));
            Assert.False(args.ContainsKey("trailingATR"));
            Assert.Equal(10, Convert.ToInt32(args["lookbackPeriod"]));
            Assert.Equal(20, Convert.ToInt32(args["exitLookbackPeriod"]));
            Assert.Equal(3m, Convert.ToDecimal(args["stopATR"]));
            Assert.Equal(0.12m, Convert.ToDecimal(args["trendSlopeThreshold"]));
            Assert.Equal(0, Convert.ToInt32(args["enablePyramiding"]));
        }

        [Fact]
        public void CalculateReferenceLevels_ExcludesCurrentBarAndUsesMostRecentEqualExtreme()
        {
            var quotes = new List<SkQuote>
            {
                Bar(10, 8),
                Bar(15, 5),
                Bar(14, 5),
                Bar(20, 9),
                Bar(100, 1), // 当前K线不得进入参考窗口
            };

            var levels = TrendAwareReversalTurtleTrading.CalculateReferenceLevels(quotes, 4);

            Assert.Equal(14m, levels.LongTrigger);
            Assert.Equal(9m, levels.ShortTrigger);
            Assert.Equal(2, levels.LowestBarIndex);
            Assert.Equal(3, levels.HighestBarIndex);
        }

        [Fact]
        public void EvaluateTargetDirection_RecentBottom_StaysFlatUntilLongTriggerConfirmed()
        {
            var levels = new TrendAwareReversalTurtleTrading.ReferenceLevels(
                longTrigger: 90m,
                shortTrigger: 110m,
                lowestBarIndex: 8,
                highestBarIndex: 3);

            Assert.Equal(0, TrendAwareReversalTurtleTrading.EvaluateTargetDirection(90m, levels));
            Assert.Equal(1, TrendAwareReversalTurtleTrading.EvaluateTargetDirection(90.01m, levels));
        }

        [Fact]
        public void EvaluateTargetDirection_StrictSwingFilterDoesNotSuppressTurtleSignal()
        {
            var levels = new TrendAwareReversalTurtleTrading.ReferenceLevels(
                longTrigger: 90m,
                shortTrigger: 110m,
                lowestBarIndex: 8,
                highestBarIndex: 3,
                longStructureAllowed: false);

            Assert.Equal(1, TrendAwareReversalTurtleTrading.EvaluateTargetDirection(100m, levels));
        }

        [Theory]
        [InlineData(4, 6, true)]
        [InlineData(6, 4, false)]
        [InlineData(5, 5, false)]
        public void HasNonDescendingRecentSwingLows_RequiresStrictlyHigherConfirmedBottom(
            int previousBottom, int latestBottom, bool expected)
        {
            var quotes = new List<SkQuote>
            {
                Bar(12, 10), Bar(12, 9), Bar(12, previousBottom), Bar(12, 9), Bar(12, 10),
                Bar(12, 9), Bar(12, latestBottom), Bar(12, 9), Bar(12, 10),
            };

            Assert.Equal(expected,
                TrendAwareReversalTurtleTrading.HasNonDescendingRecentSwingLows(quotes, 0, quotes.Count));
        }

        [Fact]
        public void HasNonDescendingRecentSwingLows_BlocksLongWhenFewerThanTwoBottomsExist()
        {
            var quotes = new List<SkQuote>
            {
                Bar(12, 10), Bar(12, 9), Bar(12, 5), Bar(12, 9), Bar(12, 10),
            };

            Assert.False(TrendAwareReversalTurtleTrading.HasNonDescendingRecentSwingLows(quotes, 0, quotes.Count));
        }

        [Fact]
        public void HasNonDescendingRecentSwingLows_UnconfirmedNextBottomDoesNotEnableLong()
        {
            var quotes = new List<SkQuote>
            {
                Bar(12, 10), Bar(12, 9), Bar(12, 5), Bar(12, 9), Bar(12, 10),
                Bar(12, 9), Bar(12, 7), Bar(12, 8),
            };

            Assert.False(TrendAwareReversalTurtleTrading.HasNonDescendingRecentSwingLows(quotes, 0, quotes.Count));
        }

        [Fact]
        public void EvaluateTargetDirection_RecentTop_StaysFlatUntilShortTriggerConfirmed()
        {
            var levels = new TrendAwareReversalTurtleTrading.ReferenceLevels(
                longTrigger: 90m,
                shortTrigger: 110m,
                lowestBarIndex: 3,
                highestBarIndex: 8);

            Assert.Equal(0, TrendAwareReversalTurtleTrading.EvaluateTargetDirection(110m, levels));
            Assert.Equal(-1, TrendAwareReversalTurtleTrading.EvaluateTargetDirection(109.99m, levels));
        }

        [Theory]
        [InlineData(16, 14, true)]
        [InlineData(14, 16, false)]
        [InlineData(15, 15, false)]
        public void HasNonAscendingRecentSwingHighs_RequiresStrictlyLowerConfirmedTop(
            int previousTop, int latestTop, bool expected)
        {
            var quotes = new List<SkQuote>
            {
                Bar(10, 8), Bar(11, 8), Bar(previousTop, 8), Bar(11, 8), Bar(10, 8),
                Bar(11, 8), Bar(latestTop, 8), Bar(11, 8), Bar(10, 8),
            };

            Assert.Equal(expected,
                TrendAwareReversalTurtleTrading.HasNonAscendingRecentSwingHighs(quotes, 0, quotes.Count));
        }

        [Fact]
        public void HasNonAscendingRecentSwingHighs_BlocksShortWhenFewerThanTwoTopsExist()
        {
            var quotes = new List<SkQuote>
            {
                Bar(10, 8), Bar(11, 8), Bar(15, 8), Bar(11, 8), Bar(10, 8),
            };

            Assert.False(TrendAwareReversalTurtleTrading.HasNonAscendingRecentSwingHighs(quotes, 0, quotes.Count));
        }

        [Fact]
        public void EvaluateTargetDirection_IgnoresOppositePriceCondition()
        {
            var recentBottom = new TrendAwareReversalTurtleTrading.ReferenceLevels(90m, 110m, 8, 3);
            var recentTop = new TrendAwareReversalTurtleTrading.ReferenceLevels(90m, 110m, 3, 8);

            Assert.Equal(0, TrendAwareReversalTurtleTrading.EvaluateTargetDirection(80m, recentBottom));
            Assert.Equal(0, TrendAwareReversalTurtleTrading.EvaluateTargetDirection(120m, recentTop));
        }

        [Fact]
        public void EvaluateTargetDirection_SameExtremeBar_StaysFlat()
        {
            var levels = new TrendAwareReversalTurtleTrading.ReferenceLevels(
                longTrigger: 90m,
                shortTrigger: 110m,
                lowestBarIndex: 5,
                highestBarIndex: 5);

            Assert.Equal(0, TrendAwareReversalTurtleTrading.EvaluateTargetDirection(120m, levels));
            Assert.Equal(0, TrendAwareReversalTurtleTrading.EvaluateTargetDirection(80m, levels));
        }

        [Fact]
        public void ClassifyTrend_PreservesGlobalDirectionAndDetectsStrongReversal()
        {
            var following = TrendAwareReversalTurtleTrading.ClassifyTrend(
                TrendBars(100m, 0.5m, 14, 0.8m), 14, 120, 0.08m);
            var reversing = TrendAwareReversalTurtleTrading.ClassifyTrend(
                TrendBars(100m, 0.5m, 14, -1.2m), 14, 120, 0.08m);

            Assert.Equal(1, following.GlobalDirection);
            Assert.Equal(1, following.RecentDirection);
            Assert.Equal(TrendAwareReversalTurtleTrading.TrendRegime.Following, following.Regime);
            Assert.Equal(1, reversing.GlobalDirection);
            Assert.Equal(-1, reversing.RecentDirection);
            Assert.Equal(TrendAwareReversalTurtleTrading.TrendRegime.Reversing, reversing.Regime);
        }

        [Fact]
        public void ClassifyTrend_NewHighForcesFollowingUptrend()
        {
            var bars = TrendBars(100m, 0.02m, 14, 0.01m);
            decimal previousGlobalHigh = bars.Take(bars.Count - 14).Max(p => p.High);
            for (int i = bars.Count - 14; i < bars.Count; i++)
            {
                decimal close = previousGlobalHigh + 1m + (i - (bars.Count - 14)) * 0.1m;
                bars[i] = Bar(close + 0.5m, close - 0.5m, close);
            }

            var trend = TrendAwareReversalTurtleTrading.ClassifyTrend(bars, 14, 120, 0.08m);
            Assert.Equal(TrendAwareReversalTurtleTrading.TrendRegime.Following, trend.Regime);
            Assert.Equal(1, trend.GlobalDirection);
        }

        [Fact]
        public void ResolveMarketAction_Following_ImmediatelyAlignsWithConfirmedTrend()
        {
            var following = new TrendAwareReversalTurtleTrading.TrendAssessment(
                TrendAwareReversalTurtleTrading.TrendRegime.Following, 1, 1, 100m, 200m, 150m);

            Assert.Equal(1, TrendAwareReversalTurtleTrading.ResolveMarketAction(
                0, 1, false, 0, following, 0, 1, 120m, 180m, 150m, 150m).TargetDirection);
            Assert.Equal(0, TrendAwareReversalTurtleTrading.ResolveMarketAction(
                -1, -1, false, 0, following, -1, 1, 120m, 180m, 150m, 150m).TargetDirection);
        }

        [Fact]
        public void ResolveMarketAction_Pullback_ClosesWithoutDirectSupportReentry()
        {
            var pullback = new TrendAwareReversalTurtleTrading.TrendAssessment(
                TrendAwareReversalTurtleTrading.TrendRegime.Pullback, 1, -1, 100m, 200m, 150m);

            var close = TrendAwareReversalTurtleTrading.ResolveMarketAction(
                1, 1, false, 0, pullback, -1, -1, 125m, 180m, 150m, 140m);
            Assert.Equal(0, close.TargetDirection);
            Assert.False(close.WaitingPullbackReentry);

            var stillFlatAtSupport = TrendAwareReversalTurtleTrading.ResolveMarketAction(
                0, close.TrendDirection, true, 125m,
                pullback, 0, 0, 125m, 180m, 150m, 124m);
            Assert.Equal(0, stillFlatAtSupport.TargetDirection);
            Assert.False(stillFlatAtSupport.WaitingPullbackReentry);
        }

        [Fact]
        public void ResolveMarketAction_Sideways_RequiresBoundaryAndTurtleStructure()
        {
            var sideways = new TrendAwareReversalTurtleTrading.TrendAssessment(
                TrendAwareReversalTurtleTrading.TrendRegime.Sideways, 0, 0, 100m, 200m, 150m);

            Assert.Equal(0, TrendAwareReversalTurtleTrading.ResolveMarketAction(
                0, 0, false, 0, sideways, 1, 1, 110m, 190m, 150m, 130m).TargetDirection);
            Assert.Equal(1, TrendAwareReversalTurtleTrading.ResolveMarketAction(
                0, 0, false, 0, sideways, 1, 1, 110m, 190m, 150m, 124m).TargetDirection);
            Assert.Equal(-1, TrendAwareReversalTurtleTrading.ResolveMarketAction(
                1, 0, false, 0, sideways, -1, -1, 110m, 190m, 176m, 150m).TargetDirection);
        }

        [Fact]
        public void ResolveMarketAction_Reversing_ImmediatelySwitchesAndRemembersNewTrend()
        {
            var reversing = new TrendAwareReversalTurtleTrading.TrendAssessment(
                TrendAwareReversalTurtleTrading.TrendRegime.Reversing, 1, -1, 100m, 200m, 90m);

            var action = TrendAwareReversalTurtleTrading.ResolveMarketAction(
                1, 1, false, 0, reversing, 0, 0, 110m, 190m, 95m, 85m);
            Assert.Equal(-1, action.TargetDirection);
            Assert.Equal(-1, action.TrendDirection);
            Assert.False(action.ConsumesTurtleSignal);
        }

        [Theory]
        [InlineData(1, 100, false, 101, 99, true)]
        [InlineData(1, 100, false, 101, 100, false)]
        [InlineData(-1, 100, false, 101, 99, true)]
        [InlineData(-1, 100, false, 100, 99, false)]
        [InlineData(1, 100, true, 100, 100, true)]
        public void UpdateStopReentryPullbackSeen_UsesOppositeIntrabarExtreme(
            int pendingDirection, decimal exitPrice, bool alreadySeen,
            decimal currentHigh, decimal currentLow, bool expected)
        {
            Assert.Equal(expected, TrendAwareReversalTurtleTrading.UpdateStopReentryPullbackSeen(
                pendingDirection, exitPrice, alreadySeen, currentHigh, currentLow));
        }

        [Theory]
        [InlineData(1, 105, false, 1, 106, 99, 0)]
        [InlineData(1, 105, true, 1, 105, 99, 0)]
        [InlineData(1, 105, true, 1, 105.01, 99, 1)]
        [InlineData(-1, 95, false, -1, 101, 94, 0)]
        [InlineData(-1, 95, true, -1, 101, 95, 0)]
        [InlineData(-1, 95, true, -1, 101, 94.99, -1)]
        [InlineData(1, 105, true, -1, 106, 99, 0)]
        [InlineData(-1, 95, true, 1, 101, 94, 0)]
        public void ResolveStopReentryDirection_RequiresPriorBarPullbackThenStrictIntrabarBreakAndSameTrend(
            int pendingDirection, decimal exitBarExtreme, bool pullbackSeenBeforeCurrentBar,
            int trendDirection, decimal currentHigh, decimal currentLow, int expected)
        {
            Assert.Equal(expected, TrendAwareReversalTurtleTrading.ResolveStopReentryDirection(
                pendingDirection, exitBarExtreme, pullbackSeenBeforeCurrentBar,
                trendDirection, currentHigh, currentLow));
        }

        [Fact]
        public void FindLatestConfirmedSwingLevel_ReturnsSupportAndResistance()
        {
            var quotes = new List<SkQuote>
            {
                Bar(10, 8), Bar(11, 7), Bar(12, 5), Bar(11, 7), Bar(10, 8),
                Bar(15, 9), Bar(20, 10), Bar(15, 9), Bar(14, 8),
            };

            Assert.Equal(5m, TrendAwareReversalTurtleTrading.FindLatestConfirmedSwingLevel(
                quotes, 0, quotes.Count, 1));
            Assert.Equal(20m, TrendAwareReversalTurtleTrading.FindLatestConfirmedSwingLevel(
                quotes, 0, quotes.Count, -1));
        }

        [Fact]
        public void StructureConsumption_AllowsOnlyExtremesFormedAfterConsumedStructure()
        {
            DateTimeOffset consumed = new DateTimeOffset(2025, 1, 10, 0, 0, 0, TimeSpan.Zero);

            Assert.False(TrendAwareReversalTurtleTrading.IsNewStructure(consumed, consumed));
            Assert.False(TrendAwareReversalTurtleTrading.IsNewStructure(consumed, consumed.AddDays(-1)));
            Assert.True(TrendAwareReversalTurtleTrading.IsNewStructure(consumed, consumed.AddDays(1)));
            Assert.True(TrendAwareReversalTurtleTrading.IsNewStructure(null, consumed));
        }

        [Fact]
        public void ShouldPyramid_UsesLastEntryAndAtrSpacingForBothDirections()
        {
            Assert.False(TrendAwareReversalTurtleTrading.ShouldPyramid(1, 104.99m, 100m, 10m, 0.5m, 1, 4));
            Assert.True(TrendAwareReversalTurtleTrading.ShouldPyramid(1, 105m, 100m, 10m, 0.5m, 1, 4));
            Assert.False(TrendAwareReversalTurtleTrading.ShouldPyramid(-1, 95.01m, 100m, 10m, 0.5m, 1, 4));
            Assert.True(TrendAwareReversalTurtleTrading.ShouldPyramid(-1, 95m, 100m, 10m, 0.5m, 1, 4));
        }

        [Fact]
        public void ShouldPyramid_StopsAtConfiguredUnitLimit()
        {
            Assert.False(TrendAwareReversalTurtleTrading.ShouldPyramid(1, 200m, 100m, 10m, 0.5m, 4, 4));
            Assert.False(TrendAwareReversalTurtleTrading.ShouldPyramid(-1, 1m, 100m, 10m, 0.5m, 4, 4));
        }

        [Fact]
        public void CalculateReferenceLevels_RejectsInsufficientHistory()
        {
            var quotes = new List<SkQuote> { Bar(10, 8), Bar(11, 9) };

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TrendAwareReversalTurtleTrading.CalculateReferenceLevels(quotes, 2));
        }

        [Fact]
        public void CalculateExitLevels_ExcludesCurrentBar()
        {
            var quotes = new List<SkQuote>
            {
                Bar(12m, 10m),
                Bar(13m, 8m),
                Bar(11m, 9m),
                Bar(30m, 1m),
            };

            var levels = TrendAwareReversalTurtleTrading.CalculateExitLevels(quotes, 2);

            Assert.Equal(8m, levels.Low);
            Assert.Equal(13m, levels.High);
        }

        private static List<SkQuote> TrendBars(decimal start, decimal globalStep,
            int recentCount, decimal recentStep)
        {
            var bars = new List<SkQuote>();
            decimal close = start;
            for (int i = 0; i < 60; i++)
            {
                close += globalStep;
                bars.Add(Bar(close + 0.5m, close - 0.5m, close));
            }
            for (int i = 0; i < recentCount; i++)
            {
                close += recentStep;
                bars.Add(Bar(close + 0.5m, close - 0.5m, close));
            }
            return bars;
        }

        private static SkQuote Bar(decimal high, decimal low, decimal? close = null, DateTime? date = null)
        {
            return new SkQuote
            {
                Date = date ?? DateTime.UtcNow,
                Open = low,
                High = high,
                Low = low,
                Close = close ?? high,
                Volume = 1,
            };
        }
    }
}
