using Common;
using QjySDK;
using System;
using System.Collections.Generic;
using Xunit;

namespace QjySDK.Tests
{
    /// <summary>
    /// ChanLun缠论策略单元测试
    /// 测试核心算法：K线包含关系、分型识别、笔重叠、背驰判断
    /// </summary>
    public class ChanLunTests
    {
        private readonly ChanLun _chanLun;

        public ChanLunTests()
        {
            _chanLun = new ChanLun("test");
        }

        #region HasContainRelation 包含关系测试

        [Fact]
        public void HasContainRelation_Bar1ContainsBar2_ReturnsTrue()
        {
            // Bar1: High=20, Low=5 包含 Bar2: High=15, Low=10
            // Bar1的高低点范围[5,20]完全包含Bar2的[10,15]
            var result = _chanLun.HasContainRelation(20m, 5m, 15m, 10m);
            Assert.True(result);
        }

        [Fact]
        public void HasContainRelation_Bar2ContainsBar1_ReturnsTrue()
        {
            // Bar2: High=20, Low=5 包含 Bar1: High=15, Low=10
            var result = _chanLun.HasContainRelation(15m, 10m, 20m, 5m);
            Assert.True(result);
        }

        [Fact]
        public void HasContainRelation_NoContain_HigherBar_ReturnsFalse()
        {
            // Bar1: High=20, Low=15 与 Bar2: High=14, Low=10 无包含关系
            // 两个K线高低点范围不重叠
            var result = _chanLun.HasContainRelation(20m, 15m, 14m, 10m);
            Assert.False(result);
        }

        [Fact]
        public void HasContainRelation_NoContain_PartialOverlap_ReturnsFalse()
        {
            // Bar1: High=20, Low=10 与 Bar2: High=25, Low=15 部分重叠但无包含
            // Bar1范围[10,20], Bar2范围[15,25]，重叠区间[15,20]
            var result = _chanLun.HasContainRelation(20m, 10m, 25m, 15m);
            Assert.False(result);
        }

        [Fact]
        public void HasContainRelation_ExactlyEqual_ReturnsTrue()
        {
            // 完全相同的K线应该算包含关系
            var result = _chanLun.HasContainRelation(20m, 10m, 20m, 10m);
            Assert.True(result);
        }

        [Fact]
        public void HasContainRelation_SameHighDifferentLow_Bar1ContainsBar2()
        {
            // Bar1: High=20, Low=5 与 Bar2: High=20, Low=10
            // Bar1包含Bar2
            var result = _chanLun.HasContainRelation(20m, 5m, 20m, 10m);
            Assert.True(result);
        }

        [Fact]
        public void HasContainRelation_SameLowDifferentHigh_Bar1ContainsBar2()
        {
            // Bar1: High=25, Low=10 与 Bar2: High=20, Low=10
            // Bar1包含Bar2
            var result = _chanLun.HasContainRelation(25m, 10m, 20m, 10m);
            Assert.True(result);
        }

        #endregion

        #region IdentifyFractal 分型识别测试

        [Fact]
        public void IdentifyFractal_TopFractal_ReturnsTop()
        {
            // 构造顶分型：中间K线高点最高
            // K1: High=100, Low=90
            // K2: High=110, Low=95 (中间，最高)
            // K3: High=105, Low=88
            var mergedBars = new List<ChanLun.MergedBar>
            {
                new ChanLun.MergedBar { High = 100m, Low = 90m },
                new ChanLun.MergedBar { High = 110m, Low = 95m },
                new ChanLun.MergedBar { High = 105m, Low = 88m }
            };

            var result = _chanLun.IdentifyFractal(mergedBars, 1);
            Assert.Equal(ChanLun.FractalType.Top, result);
        }

        [Fact]
        public void IdentifyFractal_BottomFractal_ReturnsBottom()
        {
            // 构造底分型：中间K线低点最低
            // K1: High=100, Low=90
            // K2: High=95, Low=80 (中间，最低)
            // K3: High=105, Low=85
            var mergedBars = new List<ChanLun.MergedBar>
            {
                new ChanLun.MergedBar { High = 100m, Low = 90m },
                new ChanLun.MergedBar { High = 95m, Low = 80m },
                new ChanLun.MergedBar { High = 105m, Low = 85m }
            };

            var result = _chanLun.IdentifyFractal(mergedBars, 1);
            Assert.Equal(ChanLun.FractalType.Bottom, result);
        }

        [Fact]
        public void IdentifyFractal_NoFractal_Ascending_ReturnsNone()
        {
            // 连续上升，无分型
            // K1: High=100, Low=90
            // K2: High=110, Low=100
            // K3: High=120, Low=110
            var mergedBars = new List<ChanLun.MergedBar>
            {
                new ChanLun.MergedBar { High = 100m, Low = 90m },
                new ChanLun.MergedBar { High = 110m, Low = 100m },
                new ChanLun.MergedBar { High = 120m, Low = 110m }
            };

            var result = _chanLun.IdentifyFractal(mergedBars, 1);
            Assert.Equal(ChanLun.FractalType.None, result);
        }

        [Fact]
        public void IdentifyFractal_NoFractal_Descending_ReturnsNone()
        {
            // 连续下降，无分型
            var mergedBars = new List<ChanLun.MergedBar>
            {
                new ChanLun.MergedBar { High = 120m, Low = 110m },
                new ChanLun.MergedBar { High = 110m, Low = 100m },
                new ChanLun.MergedBar { High = 100m, Low = 90m }
            };

            var result = _chanLun.IdentifyFractal(mergedBars, 1);
            Assert.Equal(ChanLun.FractalType.None, result);
        }

        [Fact]
        public void IdentifyFractal_IndexOutOfRange_First_ReturnsNone()
        {
            var mergedBars = new List<ChanLun.MergedBar>
            {
                new ChanLun.MergedBar { High = 100m, Low = 90m },
                new ChanLun.MergedBar { High = 110m, Low = 95m },
                new ChanLun.MergedBar { High = 105m, Low = 88m }
            };

            // 索引0无法形成分型（没有前一根K线）
            var result = _chanLun.IdentifyFractal(mergedBars, 0);
            Assert.Equal(ChanLun.FractalType.None, result);
        }

        [Fact]
        public void IdentifyFractal_IndexOutOfRange_Last_ReturnsNone()
        {
            var mergedBars = new List<ChanLun.MergedBar>
            {
                new ChanLun.MergedBar { High = 100m, Low = 90m },
                new ChanLun.MergedBar { High = 110m, Low = 95m },
                new ChanLun.MergedBar { High = 105m, Low = 88m }
            };

            // 最后一根K线索引无法形成分型（没有后一根K线）
            var result = _chanLun.IdentifyFractal(mergedBars, 2);
            Assert.Equal(ChanLun.FractalType.None, result);
        }

        [Fact]
        public void IdentifyFractal_TopFractal_EqualHighOnOneSide_ReturnsNone()
        {
            // 中间K线与右侧K线高点相等，不满足严格大于条件
            var mergedBars = new List<ChanLun.MergedBar>
            {
                new ChanLun.MergedBar { High = 100m, Low = 90m },
                new ChanLun.MergedBar { High = 110m, Low = 95m },
                new ChanLun.MergedBar { High = 110m, Low = 88m }  // 与中间相等
            };

            var result = _chanLun.IdentifyFractal(mergedBars, 1);
            Assert.Equal(ChanLun.FractalType.None, result);
        }

        #endregion

        #region ProcessContainRelation K线合并测试

        [Fact]
        public void ProcessContainRelation_NoContain_AllBarsPreserved()
        {
            // 三根K线无包含关系
            var quotes = new List<SkQuote>
            {
                CreateQuote(100m, 90m, 95m, 95m),   // K1
                CreateQuote(110m, 100m, 105m, 108m), // K2 高于K1
                CreateQuote(120m, 110m, 115m, 118m)  // K3 高于K2
            };

            var state = new ChanLun.State { MergedBars = new List<ChanLun.MergedBar>() };
            _chanLun.ProcessContainRelation(state, quotes);

            Assert.Equal(3, state.MergedBars.Count);
        }

        [Fact]
        public void ProcessContainRelation_WithContain_UpDirection_MergedCorrectly()
        {
            // K2包含在K1中，向上合并（取高高低高）
            // K1: High=110, Low=90
            // K2: High=105, Low=95 (被K1包含)
            // K3: High=120, Low=100
            var quotes = new List<SkQuote>
            {
                CreateQuote(110m, 90m, 95m, 105m),  // K1
                CreateQuote(105m, 95m, 98m, 100m),  // K2 被K1包含
                CreateQuote(120m, 100m, 105m, 115m) // K3
            };

            var state = new ChanLun.State { MergedBars = new List<ChanLun.MergedBar>() };
            _chanLun.ProcessContainRelation(state, quotes);

            // K1和K2合并后只剩2根
            Assert.Equal(2, state.MergedBars.Count);
        }

        [Fact]
        public void ProcessContainRelation_EmptyQuotes_EmptyResult()
        {
            var quotes = new List<SkQuote>();
            var state = new ChanLun.State { MergedBars = new List<ChanLun.MergedBar>() };
            
            _chanLun.ProcessContainRelation(state, quotes);
            
            Assert.Empty(state.MergedBars);
        }

        [Fact]
        public void ProcessContainRelation_SingleQuote_OneBar()
        {
            var quotes = new List<SkQuote>
            {
                CreateQuote(100m, 90m, 95m, 98m)
            };

            var state = new ChanLun.State { MergedBars = new List<ChanLun.MergedBar>() };
            _chanLun.ProcessContainRelation(state, quotes);

            Assert.Single(state.MergedBars);
            Assert.Equal(100m, state.MergedBars[0].High);
            Assert.Equal(90m, state.MergedBars[0].Low);
        }

        #endregion

        #region UpdateFractals 分型更新测试

        [Fact]
        public void UpdateFractals_WithTopAndBottomFractals_IdentifiesBoth()
        {
            // 构造包含顶分型和底分型的K线序列
            // 先上升形成顶分型，再下降形成底分型
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 0, LastOriginalIndex = 0 },
                    new ChanLun.MergedBar { High = 115m, Low = 105m, OriginalIndex = 1, LastOriginalIndex = 1 }, // 顶分型
                    new ChanLun.MergedBar { High = 110m, Low = 100m, OriginalIndex = 2, LastOriginalIndex = 2 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 3, LastOriginalIndex = 3 },   // 底分型
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 4, LastOriginalIndex = 4 }
                },
                Fractals = new List<ChanLun.Fractal>()
            };

            _chanLun.UpdateFractals(state);

            Assert.Equal(2, state.Fractals.Count);
            Assert.Equal(ChanLun.FractalType.Top, state.Fractals[0].Type);
            Assert.Equal(ChanLun.FractalType.Bottom, state.Fractals[1].Type);
        }

        [Fact]
        public void UpdateFractals_ConsecutiveSameTypeFractals_KeepsMoreExtreme()
        {
            // 构造连续两个顶分型的场景：K1是顶分型，K2不是分型，K3是更高的顶分型
            // K2不是分型的条件：
            // - 不是顶分型：K2.High <= K1.High 或 K2.High <= K3.High
            // - 不是底分型：K2.Low >= K1.Low 或 K2.Low >= K3.Low
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    // K0: 起始K线
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 0, LastOriginalIndex = 0 },
                    // K1: 顶分型1 (High=115 > K0.High=100 且 > K2.High=112)
                    new ChanLun.MergedBar { High = 115m, Low = 100m, OriginalIndex = 1, LastOriginalIndex = 1 },
                    // K2: 不是分型 (High=112 < K1.High=115, Low=102 > K1.Low=100 所以不是底分型)
                    new ChanLun.MergedBar { High = 112m, Low = 102m, OriginalIndex = 2, LastOriginalIndex = 2 },
                    // K3: 顶分型2 (High=120 > K2.High=112 且 > K4.High=115)
                    new ChanLun.MergedBar { High = 120m, Low = 105m, OriginalIndex = 3, LastOriginalIndex = 3 },
                    // K4: 确认K3顶分型
                    new ChanLun.MergedBar { High = 115m, Low = 100m, OriginalIndex = 4, LastOriginalIndex = 4 }
                },
                Fractals = new List<ChanLun.Fractal>()
            };

            _chanLun.UpdateFractals(state);

            // K1=Top(115), K2不是分型, K3=Top(120)
            // 连续两个顶分型，应该只保留更高的那个(K3, Price=120)
            Assert.Single(state.Fractals);
            Assert.Equal(ChanLun.FractalType.Top, state.Fractals[0].Type);
            Assert.Equal(120m, state.Fractals[0].Price);
        }

        [Fact]
        public void UpdateFractals_LessThan3Bars_NoFractals()
        {
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    new ChanLun.MergedBar { High = 100m, Low = 90m },
                    new ChanLun.MergedBar { High = 110m, Low = 100m }
                },
                Fractals = new List<ChanLun.Fractal>()
            };

            _chanLun.UpdateFractals(state);

            Assert.Empty(state.Fractals);
        }

        #endregion

        #region IsStrokesOverlap 笔重叠测试

        [Fact]
        public void IsStrokesOverlap_Overlapping_ReturnsTrue()
        {
            // 两笔重叠
            var stroke1 = new ChanLun.Stroke { High = 110m, Low = 100m };
            var stroke2 = new ChanLun.Stroke { High = 115m, Low = 105m };

            var result = _chanLun.IsStrokesOverlap(stroke1, stroke2);
            Assert.True(result);
        }

        [Fact]
        public void IsStrokesOverlap_NotOverlapping_ReturnsFalse()
        {
            // 两笔不重叠
            var stroke1 = new ChanLun.Stroke { High = 100m, Low = 90m };
            var stroke2 = new ChanLun.Stroke { High = 120m, Low = 110m };

            var result = _chanLun.IsStrokesOverlap(stroke1, stroke2);
            Assert.False(result);
        }

        [Fact]
        public void IsStrokesOverlap_TouchingButNotOverlapping_ReturnsFalse()
        {
            // 两笔刚好接触但不重叠（边界情况）
            var stroke1 = new ChanLun.Stroke { High = 100m, Low = 90m };
            var stroke2 = new ChanLun.Stroke { High = 110m, Low = 100m };

            var result = _chanLun.IsStrokesOverlap(stroke1, stroke2);
            Assert.False(result);
        }

        [Fact]
        public void IsStrokesOverlap_OneContainsAnother_ReturnsTrue()
        {
            // 一笔完全包含另一笔
            var stroke1 = new ChanLun.Stroke { High = 120m, Low = 80m };
            var stroke2 = new ChanLun.Stroke { High = 110m, Low = 90m };

            var result = _chanLun.IsStrokesOverlap(stroke1, stroke2);
            Assert.True(result);
        }

        #endregion

        #region IsDivergence 背驰判断测试

        [Fact]
        public void IsDivergence_UpStroke_NewHighWithSmallerMACD_ReturnsTrue()
        {
            // 向上笔背驰：新高但MACD面积减小
            var stroke1 = new ChanLun.Stroke
            {
                IsUp = true,
                High = 100m,
                Low = 80m,
                MACDArea = 50m
            };
            var stroke2 = new ChanLun.Stroke
            {
                IsUp = true,
                High = 110m,  // 创新高
                Low = 90m,
                MACDArea = 30m  // MACD面积减小
            };

            var result = _chanLun.IsDivergence(stroke1, stroke2);
            Assert.True(result);
        }

        [Fact]
        public void IsDivergence_DownStroke_NewLowWithSmallerMACD_ReturnsTrue()
        {
            // 向下笔背驰：新低但MACD面积减小
            var stroke1 = new ChanLun.Stroke
            {
                IsUp = false,
                High = 100m,
                Low = 80m,
                MACDArea = 50m
            };
            var stroke2 = new ChanLun.Stroke
            {
                IsUp = false,
                High = 90m,
                Low = 70m,  // 创新低
                MACDArea = 30m  // MACD面积减小
            };

            var result = _chanLun.IsDivergence(stroke1, stroke2);
            Assert.True(result);
        }

        [Fact]
        public void IsDivergence_DifferentDirection_ReturnsFalse()
        {
            // 方向不同，不是背驰
            var stroke1 = new ChanLun.Stroke { IsUp = true, High = 100m, Low = 80m, MACDArea = 50m };
            var stroke2 = new ChanLun.Stroke { IsUp = false, High = 90m, Low = 70m, MACDArea = 30m };

            var result = _chanLun.IsDivergence(stroke1, stroke2);
            Assert.False(result);
        }

        [Fact]
        public void IsDivergence_NoNewHigh_ReturnsFalse()
        {
            // 向上笔但没有创新高，不是背驰
            var stroke1 = new ChanLun.Stroke { IsUp = true, High = 100m, Low = 80m, MACDArea = 50m };
            var stroke2 = new ChanLun.Stroke { IsUp = true, High = 95m, Low = 85m, MACDArea = 30m };

            var result = _chanLun.IsDivergence(stroke1, stroke2);
            Assert.False(result);
        }

        [Fact]
        public void IsDivergence_LargerMACD_ReturnsFalse()
        {
            // 创新高但MACD面积增大，不是背驰
            var stroke1 = new ChanLun.Stroke { IsUp = true, High = 100m, Low = 80m, MACDArea = 30m };
            var stroke2 = new ChanLun.Stroke { IsUp = true, High = 110m, Low = 90m, MACDArea = 50m };

            var result = _chanLun.IsDivergence(stroke1, stroke2);
            Assert.False(result);
        }

        [Fact]
        public void IsDivergence_NullStroke_ReturnsFalse()
        {
            var stroke1 = new ChanLun.Stroke { IsUp = true, High = 100m, Low = 80m, MACDArea = 50m };

            Assert.False(_chanLun.IsDivergence(null, stroke1));
            Assert.False(_chanLun.IsDivergence(stroke1, null));
            Assert.False(_chanLun.IsDivergence(null, null));
        }

        [Fact]
        public void IsDivergence_EqualHigh_StillDivergence()
        {
            // 等于前高也算创新高（>=条件）
            var stroke1 = new ChanLun.Stroke { IsUp = true, High = 100m, Low = 80m, MACDArea = 50m };
            var stroke2 = new ChanLun.Stroke { IsUp = true, High = 100m, Low = 90m, MACDArea = 30m };

            var result = _chanLun.IsDivergence(stroke1, stroke2);
            Assert.True(result);
        }

        #endregion

        #region ZhongShu 中枢有效性测试

        [Fact]
        public void ZhongShu_IsValid_WhenZDLessThanZG_ReturnsTrue()
        {
            var zhongshu = new ChanLun.ZhongShu
            {
                ZG = 110m,  // 中枢高点
                ZD = 100m   // 中枢低点 < 中枢高点
            };

            Assert.True(zhongshu.IsValid);
        }

        [Fact]
        public void ZhongShu_IsValid_WhenZDEqualsZG_ReturnsFalse()
        {
            var zhongshu = new ChanLun.ZhongShu
            {
                ZG = 100m,
                ZD = 100m
            };

            Assert.False(zhongshu.IsValid);
        }

        [Fact]
        public void ZhongShu_IsValid_WhenZDGreaterThanZG_ReturnsFalse()
        {
            var zhongshu = new ChanLun.ZhongShu
            {
                ZG = 100m,
                ZD = 110m  // 无效中枢
            };

            Assert.False(zhongshu.IsValid);
        }

        #endregion

        #region UpdateStrokes 笔构建测试

        [Fact]
        public void UpdateStrokes_ValidUpStroke_CreatesStroke()
        {
            // 构造一个有效的向上笔：底分型 -> 顶分型，至少5根K线
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 0, LastOriginalIndex = 0 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 1, LastOriginalIndex = 1 },  // 底分型
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 2, LastOriginalIndex = 2 },
                    new ChanLun.MergedBar { High = 110m, Low = 100m, OriginalIndex = 3, LastOriginalIndex = 3 },
                    new ChanLun.MergedBar { High = 120m, Low = 110m, OriginalIndex = 4, LastOriginalIndex = 4 },
                    new ChanLun.MergedBar { High = 125m, Low = 115m, OriginalIndex = 5, LastOriginalIndex = 5 }, // 顶分型
                    new ChanLun.MergedBar { High = 120m, Low = 110m, OriginalIndex = 6, LastOriginalIndex = 6 }
                },
                Fractals = new List<ChanLun.Fractal>(),
                Strokes = new List<ChanLun.Stroke>()
            };

            _chanLun.UpdateFractals(state);
            _chanLun.UpdateStrokes(state, new List<SkQuote>(), 5);

            // 应该有1笔向上笔
            Assert.Single(state.Strokes);
            Assert.True(state.Strokes[0].IsUp);
            Assert.Equal(1, state.Strokes[0].StartIndex);  // 底分型位置
            Assert.Equal(5, state.Strokes[0].EndIndex);    // 顶分型位置
        }

        [Fact]
        public void UpdateStrokes_ValidDownStroke_CreatesStroke()
        {
            // 构造一个有效的向下笔：顶分型 -> 底分型
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    new ChanLun.MergedBar { High = 110m, Low = 100m, OriginalIndex = 0, LastOriginalIndex = 0 },
                    new ChanLun.MergedBar { High = 120m, Low = 110m, OriginalIndex = 1, LastOriginalIndex = 1 }, // 顶分型
                    new ChanLun.MergedBar { High = 115m, Low = 105m, OriginalIndex = 2, LastOriginalIndex = 2 },
                    new ChanLun.MergedBar { High = 105m, Low = 95m, OriginalIndex = 3, LastOriginalIndex = 3 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 4, LastOriginalIndex = 4 },
                    new ChanLun.MergedBar { High = 90m, Low = 80m, OriginalIndex = 5, LastOriginalIndex = 5 },   // 底分型
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 6, LastOriginalIndex = 6 }
                },
                Fractals = new List<ChanLun.Fractal>(),
                Strokes = new List<ChanLun.Stroke>()
            };

            _chanLun.UpdateFractals(state);
            _chanLun.UpdateStrokes(state, new List<SkQuote>(), 5);

            Assert.Single(state.Strokes);
            Assert.False(state.Strokes[0].IsUp);  // 向下笔
            Assert.Equal(1, state.Strokes[0].StartIndex);
            Assert.Equal(5, state.Strokes[0].EndIndex);
        }

        [Fact]
        public void UpdateStrokes_InsufficientBars_NoStroke()
        {
            // K线数不足5根，不能形成笔
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 0, LastOriginalIndex = 0 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 1, LastOriginalIndex = 1 },  // 底分型
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 2, LastOriginalIndex = 2 },
                    new ChanLun.MergedBar { High = 105m, Low = 95m, OriginalIndex = 3, LastOriginalIndex = 3 }, // 顶分型（只有3根K线间隔）
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 4, LastOriginalIndex = 4 }
                },
                Fractals = new List<ChanLun.Fractal>(),
                Strokes = new List<ChanLun.Stroke>()
            };

            _chanLun.UpdateFractals(state);
            _chanLun.UpdateStrokes(state, new List<SkQuote>(), 5);

            // 分型间距只有3根K线，不满足最少5根的要求
            Assert.Empty(state.Strokes);
        }

        [Fact]
        public void UpdateStrokes_ConsecutiveStrokes_AreConnected()
        {
            // 验证连续的笔是首尾相连的（上一笔的终点是下一笔的起点）
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    // 第一笔：向上（底分型->顶分型）
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 0, LastOriginalIndex = 0 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 1, LastOriginalIndex = 1 },   // 底分型1
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 2, LastOriginalIndex = 2 },
                    new ChanLun.MergedBar { High = 110m, Low = 100m, OriginalIndex = 3, LastOriginalIndex = 3 },
                    new ChanLun.MergedBar { High = 115m, Low = 105m, OriginalIndex = 4, LastOriginalIndex = 4 },
                    new ChanLun.MergedBar { High = 125m, Low = 115m, OriginalIndex = 5, LastOriginalIndex = 5 },
                    new ChanLun.MergedBar { High = 130m, Low = 120m, OriginalIndex = 6, LastOriginalIndex = 6 }, // 顶分型1
                    // 第二笔：向下（顶分型->底分型）
                    new ChanLun.MergedBar { High = 120m, Low = 110m, OriginalIndex = 7, LastOriginalIndex = 7 },
                    new ChanLun.MergedBar { High = 110m, Low = 100m, OriginalIndex = 8, LastOriginalIndex = 8 },
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 9, LastOriginalIndex = 9 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 10, LastOriginalIndex = 10 },
                    new ChanLun.MergedBar { High = 90m, Low = 80m, OriginalIndex = 11, LastOriginalIndex = 11 }, // 底分型2
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 12, LastOriginalIndex = 12 }
                },
                Fractals = new List<ChanLun.Fractal>(),
                Strokes = new List<ChanLun.Stroke>()
            };

            _chanLun.UpdateFractals(state);
            _chanLun.UpdateStrokes(state, new List<SkQuote>(), 5);

            // 验证笔的连续性
            Assert.True(state.Strokes.Count >= 2, $"期望至少2笔，实际{state.Strokes.Count}笔");
            
            // 验证第一笔是向上笔
            Assert.True(state.Strokes[0].IsUp);
            
            // 验证第二笔是向下笔
            Assert.False(state.Strokes[1].IsUp);
            
            // 验证连续性：第一笔的终点 == 第二笔的起点
            Assert.Equal(state.Strokes[0].EndIndex, state.Strokes[1].StartIndex);
        }

        [Fact]
        public void UpdateStrokes_AlternatingDirection_UpDownUp()
        {
            // 验证笔的方向交替：上->下->上
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    // 构造3笔的K线序列
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 0, LastOriginalIndex = 0 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 1, LastOriginalIndex = 1 },   // 底分型1
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 2, LastOriginalIndex = 2 },
                    new ChanLun.MergedBar { High = 110m, Low = 100m, OriginalIndex = 3, LastOriginalIndex = 3 },
                    new ChanLun.MergedBar { High = 120m, Low = 110m, OriginalIndex = 4, LastOriginalIndex = 4 },
                    new ChanLun.MergedBar { High = 130m, Low = 120m, OriginalIndex = 5, LastOriginalIndex = 5 },
                    new ChanLun.MergedBar { High = 135m, Low = 125m, OriginalIndex = 6, LastOriginalIndex = 6 }, // 顶分型1
                    new ChanLun.MergedBar { High = 125m, Low = 115m, OriginalIndex = 7, LastOriginalIndex = 7 },
                    new ChanLun.MergedBar { High = 115m, Low = 105m, OriginalIndex = 8, LastOriginalIndex = 8 },
                    new ChanLun.MergedBar { High = 105m, Low = 95m, OriginalIndex = 9, LastOriginalIndex = 9 },
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 10, LastOriginalIndex = 10 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 11, LastOriginalIndex = 11 }, // 底分型2
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 12, LastOriginalIndex = 12 },
                    new ChanLun.MergedBar { High = 110m, Low = 100m, OriginalIndex = 13, LastOriginalIndex = 13 },
                    new ChanLun.MergedBar { High = 120m, Low = 110m, OriginalIndex = 14, LastOriginalIndex = 14 },
                    new ChanLun.MergedBar { High = 130m, Low = 120m, OriginalIndex = 15, LastOriginalIndex = 15 },
                    new ChanLun.MergedBar { High = 140m, Low = 130m, OriginalIndex = 16, LastOriginalIndex = 16 }, // 顶分型2
                    new ChanLun.MergedBar { High = 135m, Low = 125m, OriginalIndex = 17, LastOriginalIndex = 17 }
                },
                Fractals = new List<ChanLun.Fractal>(),
                Strokes = new List<ChanLun.Stroke>()
            };

            _chanLun.UpdateFractals(state);
            _chanLun.UpdateStrokes(state, new List<SkQuote>(), 5);

            // 验证有3笔
            Assert.True(state.Strokes.Count >= 3, $"期望至少3笔，实际{state.Strokes.Count}笔");
            
            // 验证方向交替：上、下、上
            Assert.True(state.Strokes[0].IsUp);   // 第1笔向上
            Assert.False(state.Strokes[1].IsUp);  // 第2笔向下
            Assert.True(state.Strokes[2].IsUp);   // 第3笔向上
        }

        [Fact]
        public void UpdateStrokes_StrokePosition_MatchesFractalPosition()
        {
            // 验证笔的起止位置与分型位置一致
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 0, LastOriginalIndex = 0 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 1, LastOriginalIndex = 1 },  // 底分型 Index=1
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 2, LastOriginalIndex = 2 },
                    new ChanLun.MergedBar { High = 110m, Low = 100m, OriginalIndex = 3, LastOriginalIndex = 3 },
                    new ChanLun.MergedBar { High = 120m, Low = 110m, OriginalIndex = 4, LastOriginalIndex = 4 },
                    new ChanLun.MergedBar { High = 125m, Low = 115m, OriginalIndex = 5, LastOriginalIndex = 5 },
                    new ChanLun.MergedBar { High = 130m, Low = 120m, OriginalIndex = 6, LastOriginalIndex = 6 }, // 顶分型 Index=6
                    new ChanLun.MergedBar { High = 125m, Low = 115m, OriginalIndex = 7, LastOriginalIndex = 7 }
                },
                Fractals = new List<ChanLun.Fractal>(),
                Strokes = new List<ChanLun.Stroke>()
            };

            _chanLun.UpdateFractals(state);
            _chanLun.UpdateStrokes(state, new List<SkQuote>(), 5);

            Assert.Single(state.Strokes);
            var stroke = state.Strokes[0];
            
            // 验证起始分型是底分型
            Assert.Equal(ChanLun.FractalType.Bottom, stroke.StartFractal.Type);
            Assert.Equal(1, stroke.StartIndex);
            
            // 验证结束分型是顶分型
            Assert.Equal(ChanLun.FractalType.Top, stroke.EndFractal.Type);
            Assert.Equal(6, stroke.EndIndex);
            
            // 验证笔的K线数量
            Assert.Equal(6, stroke.BarCount);  // EndIndex - StartIndex + 1 = 6 - 1 + 1 = 6
        }

        [Fact]
        public void UpdateStrokes_UpStroke_HigherEnd()
        {
            // 验证向上笔：终点价格高于起点价格
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 0, LastOriginalIndex = 0 },
                    new ChanLun.MergedBar { High = 95m, Low = 85m, OriginalIndex = 1, LastOriginalIndex = 1 },  // 底分型 Price=85
                    new ChanLun.MergedBar { High = 100m, Low = 90m, OriginalIndex = 2, LastOriginalIndex = 2 },
                    new ChanLun.MergedBar { High = 110m, Low = 100m, OriginalIndex = 3, LastOriginalIndex = 3 },
                    new ChanLun.MergedBar { High = 120m, Low = 110m, OriginalIndex = 4, LastOriginalIndex = 4 },
                    new ChanLun.MergedBar { High = 130m, Low = 120m, OriginalIndex = 5, LastOriginalIndex = 5 },
                    new ChanLun.MergedBar { High = 140m, Low = 130m, OriginalIndex = 6, LastOriginalIndex = 6 }, // 顶分型 Price=140
                    new ChanLun.MergedBar { High = 135m, Low = 125m, OriginalIndex = 7, LastOriginalIndex = 7 }
                },
                Fractals = new List<ChanLun.Fractal>(),
                Strokes = new List<ChanLun.Stroke>()
            };

            _chanLun.UpdateFractals(state);
            _chanLun.UpdateStrokes(state, new List<SkQuote>(), 5);

            Assert.Single(state.Strokes);
            var stroke = state.Strokes[0];
            
            Assert.True(stroke.IsUp);
            // 向上笔：终点(顶分型)价格 > 起点(底分型)价格
            Assert.True(stroke.EndFractal.Price > stroke.StartFractal.Price);
        }

        [Fact]
        public void UpdateStrokes_NoFractals_NoStrokes()
        {
            // 没有分型时不能形成笔
            var state = new ChanLun.State
            {
                MergedBars = new List<ChanLun.MergedBar>
                {
                    new ChanLun.MergedBar { High = 100m, Low = 90m },
                    new ChanLun.MergedBar { High = 110m, Low = 100m }
                },
                Fractals = new List<ChanLun.Fractal>(),
                Strokes = new List<ChanLun.Stroke>()
            };

            _chanLun.UpdateStrokes(state, new List<SkQuote>(), 5);

            Assert.Empty(state.Strokes);
        }

        [Fact]
        public void UpdateStrokes_OnlyOneFractal_NoStrokes()
        {
            // 只有一个分型时不能形成笔
            var state = new ChanLun.State
            {
                Fractals = new List<ChanLun.Fractal>
                {
                    new ChanLun.Fractal { Index = 1, Type = ChanLun.FractalType.Bottom, Price = 85m }
                },
                Strokes = new List<ChanLun.Stroke>()
            };

            _chanLun.UpdateStrokes(state, new List<SkQuote>(), 5);

            Assert.Empty(state.Strokes);
        }

        #endregion

        #region UpdateZhongShus 中枢生成测试

        [Fact]
        public void UpdateZhongShus_ThreeOverlappingStrokes_CreatesZhongShu()
        {
            // 构造3笔重叠的场景，形成中枢
            // 笔1: [100, 120] 向上
            // 笔2: [105, 115] 向下 (与笔1重叠区间 [105, 115])
            // 笔3: [108, 118] 向上 (与前两笔重叠区间 [108, 115])
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 120m, Low = 100m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 115m, Low = 105m, IsUp = false },
                    new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 118m, Low = 108m, IsUp = true }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.Single(state.ZhongShus);
            var zs = state.ZhongShus[0];
            // ZG = min(各笔高点) = min(120, 115, 118) = 115
            Assert.Equal(115m, zs.ZG);
            // ZD = max(各笔低点) = max(100, 105, 108) = 108
            Assert.Equal(108m, zs.ZD);
            // 验证中枢有效性
            Assert.True(zs.IsValid);
        }

        [Fact]
        public void UpdateZhongShus_ZGAndZD_CalculatedCorrectly()
        {
            // 验证ZG和ZD的计算正确性
            // ZG = min(各笔高点), ZD = max(各笔低点)
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 130m, Low = 100m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 125m, Low = 95m, IsUp = false },
                    new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 120m, Low = 90m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 15, EndIndex = 20, High = 115m, Low = 85m, IsUp = false }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.Single(state.ZhongShus);
            var zs = state.ZhongShus[0];
            // ZG = min(130, 125, 120, 115) = 115
            Assert.Equal(115m, zs.ZG);
            // ZD = max(100, 95, 90, 85) = 100
            Assert.Equal(100m, zs.ZD);
        }

        [Fact]
        public void UpdateZhongShus_GGAndDD_CalculatedCorrectly()
        {
            // 验证GG(最高点)和DD(最低点)的计算
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 130m, Low = 100m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 125m, Low = 95m, IsUp = false },
                    new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 135m, Low = 90m, IsUp = true }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.Single(state.ZhongShus);
            var zs = state.ZhongShus[0];
            // GG = max(各笔高点) = max(130, 125, 135) = 135
            Assert.Equal(135m, zs.GG);
            // DD = min(各笔低点) = min(100, 95, 90) = 90
            Assert.Equal(90m, zs.DD);
        }

        [Fact]
        public void UpdateZhongShus_NoOverlap_NoZhongShu()
        {
            // 笔之间无重叠，不形成中枢
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 100m, Low = 80m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 120m, Low = 105m, IsUp = false }, // 无重叠
                    new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 140m, Low = 125m, IsUp = true }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.Empty(state.ZhongShus);
        }

        [Fact]
        public void UpdateZhongShus_InsufficientStrokes_NoZhongShu()
        {
            // 笔数不足3笔，不形成中枢
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 120m, Low = 100m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 115m, Low = 105m, IsUp = false }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.Empty(state.ZhongShus);
        }

        [Fact]
        public void UpdateZhongShus_ZhongShuContainsAllStrokes()
        {
            // 验证中枢包含所有参与的笔
            var stroke1 = new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 120m, Low = 100m, IsUp = true };
            var stroke2 = new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 115m, Low = 105m, IsUp = false };
            var stroke3 = new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 118m, Low = 108m, IsUp = true };

            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke> { stroke1, stroke2, stroke3 },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.Single(state.ZhongShus);
            Assert.Equal(3, state.ZhongShus[0].Strokes.Count);
        }

        [Fact]
        public void UpdateZhongShus_StartAndEndIndex_Correct()
        {
            // 验证中枢的起止索引正确
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 120m, Low = 100m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 115m, Low = 105m, IsUp = false },
                    new ChanLun.Stroke { StartIndex = 15, EndIndex = 25, High = 118m, Low = 108m, IsUp = true }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.Single(state.ZhongShus);
            // 起始索引 = 第一笔的起始索引
            Assert.Equal(5, state.ZhongShus[0].StartIndex);
            // 结束索引 = 最后一笔的结束索引
            Assert.Equal(25, state.ZhongShus[0].EndIndex);
        }

        [Fact]
        public void UpdateZhongShus_FourOverlappingStrokes_ExtendedZhongShu()
        {
            // 4笔都重叠，形成一个扩展的中枢
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 120m, Low = 100m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 118m, Low = 102m, IsUp = false },
                    new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 116m, Low = 104m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 15, EndIndex = 20, High = 114m, Low = 106m, IsUp = false }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.Single(state.ZhongShus);
            Assert.Equal(4, state.ZhongShus[0].Strokes.Count);
        }

        [Fact]
        public void UpdateZhongShus_TwoSeparateZhongShus()
        {
            // 两组不重叠的3笔，形成两个独立的中枢
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    // 第一个中枢
                    new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 120m, Low = 100m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 115m, Low = 105m, IsUp = false },
                    new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 118m, Low = 108m, IsUp = true },
                    // 跳跃到更高位置（第二个中枢）
                    new ChanLun.Stroke { StartIndex = 15, EndIndex = 20, High = 200m, Low = 180m, IsUp = false },
                    new ChanLun.Stroke { StartIndex = 20, EndIndex = 25, High = 195m, Low = 185m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 25, EndIndex = 30, High = 198m, Low = 188m, IsUp = false }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.Equal(2, state.ZhongShus.Count);
            // 验证第一个中枢
            Assert.Equal(0, state.ZhongShus[0].StartIndex);
            Assert.Equal(15, state.ZhongShus[0].EndIndex);
            // 验证第二个中枢
            Assert.Equal(15, state.ZhongShus[1].StartIndex);
            Assert.Equal(30, state.ZhongShus[1].EndIndex);
        }

        [Fact]
        public void UpdateZhongShus_NullStrokes_NoException()
        {
            // Strokes为null时不抛异常
            var state = new ChanLun.State
            {
                Strokes = null,
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            // 不应该抛出异常，ZhongShus保持原状
        }

        [Fact]
        public void UpdateZhongShus_CurrentZhongShu_SetToLast()
        {
            // 验证CurrentZhongShu被设置为最后一个中枢
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 120m, Low = 100m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 115m, Low = 105m, IsUp = false },
                    new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 118m, Low = 108m, IsUp = true }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            Assert.NotNull(state.CurrentZhongShu);
            Assert.Equal(state.ZhongShus[0], state.CurrentZhongShu);
        }

        [Fact]
        public void UpdateZhongShus_OverlapRange_IsValid()
        {
            // 验证中枢的重叠区间 [ZD, ZG] 有效
            var state = new ChanLun.State
            {
                Strokes = new List<ChanLun.Stroke>
                {
                    new ChanLun.Stroke { StartIndex = 0, EndIndex = 5, High = 120m, Low = 100m, IsUp = true },
                    new ChanLun.Stroke { StartIndex = 5, EndIndex = 10, High = 115m, Low = 105m, IsUp = false },
                    new ChanLun.Stroke { StartIndex = 10, EndIndex = 15, High = 118m, Low = 108m, IsUp = true }
                },
                ZhongShus = new List<ChanLun.ZhongShu>()
            };

            _chanLun.UpdateZhongShus(state, 3);

            var zs = state.ZhongShus[0];
            // ZD < ZG 表示有效的重叠区间
            Assert.True(zs.ZD < zs.ZG, $"ZD({zs.ZD}) should be less than ZG({zs.ZG})");
            Assert.True(zs.IsValid);
        }

        #endregion

        #region 辅助方法

        private static SkQuote CreateQuote(decimal high, decimal low, decimal open, decimal close)
        {
            return new SkQuote
            {
                High = high,
                Low = low,
                Open = open,
                Close = close,
                Date = DateTime.Now,
                Volume = 1000
            };
        }

        #endregion
    }
}
