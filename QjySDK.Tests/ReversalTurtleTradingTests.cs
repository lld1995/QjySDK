using Common;
using QjySDK.Stg;
using System;
using System.Collections.Generic;
using Xunit;

namespace QjySDK.Tests
{
    public class ReversalTurtleTradingTests
    {
        [Fact]
        public void DefaultParameters_UseOnlyTurtleStyleExits()
        {
            var args = new ReversalTurtleTrading().GetStgDesc().ArgDic;

            Assert.False(args.ContainsKey("takeProfitATR"));
            Assert.False(args.ContainsKey("breakEvenATR"));
            Assert.False(args.ContainsKey("trailingStartATR"));
            Assert.False(args.ContainsKey("trailingATR"));
            Assert.Equal(20, Convert.ToInt32(args["exitLookbackPeriod"]));
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

            var levels = ReversalTurtleTrading.CalculateReferenceLevels(quotes, 4);

            Assert.Equal(14m, levels.LongTrigger);
            Assert.Equal(9m, levels.ShortTrigger);
            Assert.Equal(2, levels.LowestBarIndex);
            Assert.Equal(3, levels.HighestBarIndex);
        }

        [Fact]
        public void EvaluateTargetDirection_RecentBottom_StaysFlatUntilLongTriggerConfirmed()
        {
            var levels = new ReversalTurtleTrading.ReferenceLevels(
                longTrigger: 90m,
                shortTrigger: 110m,
                lowestBarIndex: 8,
                highestBarIndex: 3);

            Assert.Equal(0, ReversalTurtleTrading.EvaluateTargetDirection(90m, levels));
            Assert.Equal(1, ReversalTurtleTrading.EvaluateTargetDirection(90.01m, levels));
        }

        [Fact]
        public void EvaluateTargetDirection_RecentTop_StaysFlatUntilShortTriggerConfirmed()
        {
            var levels = new ReversalTurtleTrading.ReferenceLevels(
                longTrigger: 90m,
                shortTrigger: 110m,
                lowestBarIndex: 3,
                highestBarIndex: 8);

            Assert.Equal(0, ReversalTurtleTrading.EvaluateTargetDirection(110m, levels));
            Assert.Equal(-1, ReversalTurtleTrading.EvaluateTargetDirection(109.99m, levels));
        }

        [Fact]
        public void EvaluateTargetDirection_IgnoresOppositePriceCondition()
        {
            var recentBottom = new ReversalTurtleTrading.ReferenceLevels(90m, 110m, 8, 3);
            var recentTop = new ReversalTurtleTrading.ReferenceLevels(90m, 110m, 3, 8);

            Assert.Equal(0, ReversalTurtleTrading.EvaluateTargetDirection(80m, recentBottom));
            Assert.Equal(0, ReversalTurtleTrading.EvaluateTargetDirection(120m, recentTop));
        }

        [Fact]
        public void EvaluateTargetDirection_SameExtremeBar_StaysFlat()
        {
            var levels = new ReversalTurtleTrading.ReferenceLevels(
                longTrigger: 90m,
                shortTrigger: 110m,
                lowestBarIndex: 5,
                highestBarIndex: 5);

            Assert.Equal(0, ReversalTurtleTrading.EvaluateTargetDirection(120m, levels));
            Assert.Equal(0, ReversalTurtleTrading.EvaluateTargetDirection(80m, levels));
        }

        [Theory]
        [InlineData(1, 0, 1)]
        [InlineData(-1, 0, -1)]
        [InlineData(0, 0, 0)]
        [InlineData(1, -1, -1)]
        [InlineData(-1, 1, 1)]
        [InlineData(0, 1, 1)]
        [InlineData(0, -1, -1)]
        public void ResolvePositionDirection_UnconfirmedMaintainsPosition_ConfirmedChangesIt(
            int currentDirection, int confirmedDirection, int expectedDirection)
        {
            Assert.Equal(expectedDirection,
                ReversalTurtleTrading.ResolvePositionDirection(currentDirection, confirmedDirection));
        }

        [Fact]
        public void StructureConsumption_AllowsOnlyExtremesFormedAfterConsumedStructure()
        {
            DateTimeOffset consumed = new DateTimeOffset(2025, 1, 10, 0, 0, 0, TimeSpan.Zero);

            Assert.False(ReversalTurtleTrading.IsNewStructure(consumed, consumed));
            Assert.False(ReversalTurtleTrading.IsNewStructure(consumed, consumed.AddDays(-1)));
            Assert.True(ReversalTurtleTrading.IsNewStructure(consumed, consumed.AddDays(1)));
            Assert.True(ReversalTurtleTrading.IsNewStructure(null, consumed));
        }

        [Fact]
        public void ShouldPyramid_UsesLastEntryAndAtrSpacingForBothDirections()
        {
            Assert.False(ReversalTurtleTrading.ShouldPyramid(1, 104.99m, 100m, 10m, 0.5m, 1, 4));
            Assert.True(ReversalTurtleTrading.ShouldPyramid(1, 105m, 100m, 10m, 0.5m, 1, 4));
            Assert.False(ReversalTurtleTrading.ShouldPyramid(-1, 95.01m, 100m, 10m, 0.5m, 1, 4));
            Assert.True(ReversalTurtleTrading.ShouldPyramid(-1, 95m, 100m, 10m, 0.5m, 1, 4));
        }

        [Fact]
        public void ShouldPyramid_StopsAtConfiguredUnitLimit()
        {
            Assert.False(ReversalTurtleTrading.ShouldPyramid(1, 200m, 100m, 10m, 0.5m, 4, 4));
            Assert.False(ReversalTurtleTrading.ShouldPyramid(-1, 1m, 100m, 10m, 0.5m, 4, 4));
        }

        [Fact]
        public void CalculateReferenceLevels_RejectsInsufficientHistory()
        {
            var quotes = new List<SkQuote> { Bar(10, 8), Bar(11, 9) };

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ReversalTurtleTrading.CalculateReferenceLevels(quotes, 2));
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

            var levels = ReversalTurtleTrading.CalculateExitLevels(quotes, 3);

            Assert.Equal(8m, levels.Low);
            Assert.Equal(13m, levels.High);
        }

        private static SkQuote Bar(decimal high, decimal low, DateTime? date = null)
        {
            return new SkQuote
            {
                Date = date ?? DateTime.UtcNow,
                Open = low,
                High = high,
                Low = low,
                Close = high,
                Volume = 1,
            };
        }
    }
}
