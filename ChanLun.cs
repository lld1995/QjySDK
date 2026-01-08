using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Linq;
using static Model.EnumDef;
using System.Collections.Generic;

namespace QjySDK
{
    /// <summary>
    /// 缠论交易策略
    /// 严格按照缠论定义实现：K线包含处理、分型、笔、线段、中枢、背驰、买卖点
    /// </summary>
    public class ChanLun : StgBase
    {
        // 分型类型
        internal enum FractalType
        {
            None = 0,
            Top = 1,    // 顶分型
            Bottom = 2  // 底分型
        }

        // 处理后的K线（合并包含关系后）
        internal class MergedBar
        {
            public int OriginalIndex { get; set; }  // 原始K线索引（第一根）
            public int LastOriginalIndex { get; set; }  // 原始K线索引（最后一根，用于绘制）
            public int MergedCount { get; set; }    // 合并的K线数量
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Open { get; set; }
            public decimal Close { get; set; }
            public DateTime Date { get; set; }
            public int Direction { get; set; }      // 合并方向：1向上 -1向下
        }

        // 分型结构
        internal class Fractal
        {
            public int Index { get; set; }          // 在MergedBars中的索引
            public int OriginalIndex { get; set; }  // 原始K线索引（第一根）
            public int LastOriginalIndex { get; set; }  // 原始K线索引（最后一根，用于绘制）
            public FractalType Type { get; set; }
            public decimal Price { get; set; }      // 顶分型取High，底分型取Low
            public decimal High { get; set; }       // 分型最高点
            public decimal Low { get; set; }        // 分型最低点
            public DateTime Date { get; set; }
            public bool IsConfirmed { get; set; }   // 是否已确认
        }

        // 笔结构
        internal class Stroke
        {
            public int StartIndex { get; set; }     // 起始分型在MergedBars中的索引
            public int EndIndex { get; set; }       // 结束分型在MergedBars中的索引
            public Fractal StartFractal { get; set; }
            public Fractal EndFractal { get; set; }
            public bool IsUp { get; set; }          // true为向上笔（底分型->顶分型），false为向下笔
            public decimal High { get; set; }       // 笔的最高点
            public decimal Low { get; set; }        // 笔的最低点
            public decimal MACDArea { get; set; }   // 笔对应的MACD面积（用于背驰判断）
            public int BarCount { get; set; }       // 包含的合并K线数量
        }

        // 线段结构
        internal class Segment
        {
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public Stroke StartStroke { get; set; }
            public Stroke EndStroke { get; set; }
            public List<Stroke> Strokes { get; set; }  // 构成线段的笔
            public bool IsUp { get; set; }             // 线段方向
            public decimal High { get; set; }
            public decimal Low { get; set; }
        }

        // 中枢结构
        internal class ZhongShu
        {
            public int StartIndex { get; set; }     // 起始K线索引
            public int EndIndex { get; set; }       // 结束K线索引
            public decimal ZG { get; set; }         // 中枢高点 = min(各笔高点)
            public decimal ZD { get; set; }         // 中枢低点 = max(各笔低点)
            public decimal GG { get; set; }         // 中枢区间最高点
            public decimal DD { get; set; }         // 中枢区间最低点
            public List<Stroke> Strokes { get; set; }  // 构成中枢的笔（至少3笔）
            public int Level { get; set; }          // 中枢级别
            public bool IsValid => ZD < ZG;         // 中枢有效性：ZD < ZG
        }

        // 买卖点类型
        internal enum BSPointType
        {
            None = 0,
            Buy1 = 1,   // 一买：趋势背驰后的第一个买点
            Buy2 = 2,   // 二买：一买后回调不破一买低点
            Buy3 = 3,   // 三买：离开中枢后回踩不进中枢
            Sell1 = -1, // 一卖：趋势背驰后的第一个卖点
            Sell2 = -2, // 二卖：一卖后反弹不破一卖高点
            Sell3 = -3  // 三卖：离开中枢后回抽不进中枢
        }

        // 买卖点结构
        internal class BSPoint
        {
            public BSPointType Type { get; set; }
            public int Index { get; set; }
            public decimal Price { get; set; }
            public DateTime Date { get; set; }
            public bool IsDivergence { get; set; }  // 是否背驰
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.ArgDic["minBarCount"] = 11;           // 最少K线数（至少需要形成2个分型）
            sd.ArgDic["strokeMinBars"] = 5;          // 笔的最少独立K线数（缠论标准：5根）
            sd.ArgDic["zhongshuMinStrokes"] = 3;     // 形成中枢的最少笔数
            sd.ArgDic["useZhongShu"] = 1;            // 是否使用中枢交易（0否 1是）
            sd.ArgDic["useDivergence"] = 1;          // 是否使用背驰判断（0否 1是）
            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            //sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" };
            //sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
            //sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            //sd.ArgDescDic["useZhongShu"] = new ArgDesc() { Text = "使用中枢", Explain = "0 否 1 是" };
            //sd.ArgDescDic["useDivergence"] = new ArgDesc() { Text = "使用背驰", Explain = "0 否 1 是" };
            //sd.ArgDescDic["zhongshuMinStrokes"] = new ArgDesc() { Text = "中枢最少笔数", Explain = "形成中枢所需的最少笔数，默认3" };
            //sd.ArgDescDic["strokeMinBars"] = new ArgDesc() { Text = "笔最少K线", Explain = "笔的最少独立K线数，缠论标准为5" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;
            return sd;
        }

        internal class State
        {
            public int Status { get; set; }              // 0:无持仓 1:多仓 2:空仓
            public decimal Num { get; set; }
            public List<MergedBar> MergedBars { get; set; }  // 处理后的K线
            public List<Fractal> Fractals { get; set; }      // 分型列表
            public List<Stroke> Strokes { get; set; }        // 笔列表
            public List<Segment> Segments { get; set; }      // 线段列表
            public List<ZhongShu> ZhongShus { get; set; }    // 中枢列表
            public ZhongShu CurrentZhongShu { get; set; }    // 当前中枢
            public List<BSPoint> BSPoints { get; set; }      // 买卖点列表
            public int LastProcessedIndex { get; set; }      // 最后处理的原始K线索引
            public List<MacdResult> MacdResults { get; set; } // MACD结果缓存
            public BSPoint LastBuy1 { get; set; }            // 最近的一买点
            public BSPoint LastSell1 { get; set; }           // 最近的一卖点
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		public ChanLun(string id) : base(id)
		{
		}

		#region K线包含关系处理

		/// <summary>
		/// 判断两根K线是否存在包含关系
		/// 包含关系：一根K线的高低点完全在另一根K线的高低点范围内
		/// </summary>
		internal bool HasContainRelation(decimal high1, decimal low1, decimal high2, decimal low2)
        {
            // bar1包含bar2 或 bar2包含bar1
            return (high1 >= high2 && low1 <= low2) || (high2 >= high1 && low2 <= low1);
        }

        /// <summary>
        /// 处理K线包含关系，生成合并后的K线序列
        /// 缠论规则：向上时取高高低高，向下时取低低高低
        /// </summary>
        internal void ProcessContainRelation(State state, List<SkQuote> quotes)
        {
            if (state.MergedBars == null)
                state.MergedBars = new List<MergedBar>();

            // 如果已处理过，只处理新增的K线
            int startIndex = state.LastProcessedIndex;
            if (startIndex == 0 && state.MergedBars.Count == 0)
            {
                // 第一根K线直接添加
                if (quotes.Count > 0)
                {
                    state.MergedBars.Add(new MergedBar
                    {
                        OriginalIndex = 0,
                        LastOriginalIndex = 0,
                        MergedCount = 1,
                        High = quotes[0].High,
                        Low = quotes[0].Low,
                        Open = quotes[0].Open,
                        Close = quotes[0].Close,
                        Date = quotes[0].Date,
                        Direction = 0
                    });
                    startIndex = 1;
                }
            }

            for (int i = startIndex; i < quotes.Count; i++)
            {
                var curr = quotes[i];
                
                if (state.MergedBars.Count == 0)
                {
                    state.MergedBars.Add(new MergedBar
                    {
                        OriginalIndex = i,
                        LastOriginalIndex = i,
                        MergedCount = 1,
                        High = curr.High,
                        Low = curr.Low,
                        Open = curr.Open,
                        Close = curr.Close,
                        Date = curr.Date,
                        Direction = 0
                    });
                    continue;
                }

                var last = state.MergedBars[state.MergedBars.Count - 1];

                // 判断是否存在包含关系
                if (HasContainRelation(last.High, last.Low, curr.High, curr.Low))
                {
                    // 确定合并方向
                    int direction = last.Direction;
                    if (direction == 0 && state.MergedBars.Count >= 2)
                    {
                        var prev = state.MergedBars[state.MergedBars.Count - 2];
                        direction = last.High > prev.High ? 1 : -1;
                    }
                    if (direction == 0)
                    {
                        direction = curr.High > last.High ? 1 : -1;
                    }

                    // 合并K线
                    if (direction > 0)
                    {
                        // 向上：取高高低高
                        last.High = Math.Max(last.High, curr.High);
                        last.Low = Math.Max(last.Low, curr.Low);
                    }
                    else
                    {
                        // 向下：取低低高低
                        last.High = Math.Min(last.High, curr.High);
                        last.Low = Math.Min(last.Low, curr.Low);
                    }
                    last.MergedCount++;
                    last.LastOriginalIndex = i;  // 更新为最后一根K线索引
                    last.Close = curr.Close;
                    last.Direction = direction;
                }
                else
                {
                    // 不存在包含关系，确定新K线的方向
                    int direction = curr.High > last.High ? 1 : -1;
                    
                    state.MergedBars.Add(new MergedBar
                    {
                        OriginalIndex = i,
                        LastOriginalIndex = i,
                        MergedCount = 1,
                        High = curr.High,
                        Low = curr.Low,
                        Open = curr.Open,
                        Close = curr.Close,
                        Date = curr.Date,
                        Direction = direction
                    });
                }
            }

            state.LastProcessedIndex = quotes.Count;
        }

        #endregion

        #region 分型识别

        /// <summary>
        /// 在合并后的K线序列中识别分型
        /// 顶分型：中间K线高点最高且低点最高
        /// 底分型：中间K线低点最低且高点最低
        /// </summary>
        internal FractalType IdentifyFractal(List<MergedBar> mergedBars, int index)
        {
            if (index < 1 || index >= mergedBars.Count - 1)
                return FractalType.None;

            var prev = mergedBars[index - 1];
            var curr = mergedBars[index];
            var next = mergedBars[index + 1];

            // 顶分型：中间K线的高点最高（缠论标准定义）
            if (curr.High > prev.High && curr.High > next.High)
            {
                return FractalType.Top;
            }

            // 底分型：中间K线的低点最低（缠论标准定义）
            if (curr.Low < prev.Low && curr.Low < next.Low)
            {
                return FractalType.Bottom;
            }

            return FractalType.None;
        }

        /// <summary>
        /// 更新分型列表（基于合并后的K线）
        /// </summary>
        internal void UpdateFractals(State state)
        {
            if (state.Fractals == null)
                state.Fractals = new List<Fractal>();

            if (state.MergedBars == null || state.MergedBars.Count < 3)
                return;

            // 重新识别所有分型（因为K线合并可能影响之前的分型）
            var newFractals = new List<Fractal>();

            for (int i = 1; i < state.MergedBars.Count - 1; i++)
            {
                var fractalType = IdentifyFractal(state.MergedBars, i);
                if (fractalType != FractalType.None)
                {
                    var bar = state.MergedBars[i];
                    var fractal = new Fractal
                    {
                        Index = i,
                        OriginalIndex = bar.OriginalIndex,
                        LastOriginalIndex = bar.LastOriginalIndex,
                        Type = fractalType,
                        Price = fractalType == FractalType.Top ? bar.High : bar.Low,
                        High = bar.High,
                        Low = bar.Low,
                        Date = bar.Date,
                        IsConfirmed = i < state.MergedBars.Count - 1
                    };

                    // 处理连续同类型分型：保留更极端的
                    if (newFractals.Count > 0)
                    {
                        var lastFractal = newFractals[newFractals.Count - 1];
                        if (lastFractal.Type == fractalType)
                        {
                            // 同类型分型，保留更极端的
                            if (fractalType == FractalType.Top && fractal.Price > lastFractal.Price)
                            {
                                newFractals[newFractals.Count - 1] = fractal;
                            }
                            else if (fractalType == FractalType.Bottom && fractal.Price < lastFractal.Price)
                            {
                                newFractals[newFractals.Count - 1] = fractal;
                            }
                        }
                        else
                        {
                            newFractals.Add(fractal);
                        }
                    }
                    else
                    {
                        newFractals.Add(fractal);
                    }
                }
            }

            state.Fractals = newFractals;
        }

        #endregion

        #region 笔构建

        /// <summary>
        /// 更新笔列表
        /// 缠论规则：笔至少包含5根独立K线（处理后）
        /// </summary>
        internal void UpdateStrokes(State state, List<SkQuote> quotes, int strokeMinBars = 5)
        {
            if (state.Strokes == null)
                state.Strokes = new List<Stroke>();

            if (state.Fractals == null || state.Fractals.Count < 2)
                return;

            // 重新构建笔列表（因为分型可能被更新）
            var newStrokes = new List<Stroke>();
            int startIdx = 0;  // 当前笔的起点分型索引
            int originalStartIdx = 0;  // 记录原始起点，用于检测是否在内层更新过

            while (startIdx < state.Fractals.Count - 1)
            {
                var startFractal = state.Fractals[startIdx];
                originalStartIdx = startIdx;
                bool foundStroke = false;

                // 寻找有效的终点分型
                for (int j = startIdx + 1; j < state.Fractals.Count; j++)
                {
                    var endFractal = state.Fractals[j];

                    // 确保分型类型不同（顶分型和底分型交替）
                    if (startFractal.Type == endFractal.Type)
                    {
                        // 同类型分型，更新起点为更极端的那个
                        if ((startFractal.Type == FractalType.Top && endFractal.Price > startFractal.Price) ||
                            (startFractal.Type == FractalType.Bottom && endFractal.Price < startFractal.Price))
                        {
                            startFractal = endFractal;
                            startIdx = j;
                        }
                        continue;
                    }

                    // 检查笔的最少K线数（处理后的K线，包含两端）
                    int barCount = endFractal.Index - startFractal.Index + 1;
                    if (barCount < strokeMinBars)  // 标准笔定义：至少5根独立K线
                    {
                        continue;
                    }

                    // 验证笔的有效性：向上笔结束点必须高于起始点，向下笔结束点必须低于起始点
                    bool isUp = startFractal.Type == FractalType.Bottom;
                    if (isUp && endFractal.Price <= startFractal.Price)
                    {
                        continue;
                    }
                    if (!isUp && endFractal.Price >= startFractal.Price)
                    {
                        continue;
                    }

                    // 缠论要求：顶底分型之间不能存在包含关系
                    // 向上笔：底分型高点 < 顶分型低点（严格无包含）
                    // 向下笔：顶分型低点 > 底分型高点（严格无包含）
                    // 放宽条件：只要价格方向正确即可（顶更高或底更低）
                    if (isUp && startFractal.High >= endFractal.Low && endFractal.High <= startFractal.High)
                    {
                        // 存在包含且顶分型高点未突破底分型高点，跳过
                        continue;
                    }
                    if (!isUp && startFractal.Low <= endFractal.High && endFractal.Low >= startFractal.Low)
                    {
                        // 存在包含且底分型低点未突破顶分型低点，跳过
                        continue;
                    }

                    // 创建笔
                    var stroke = new Stroke
                    {
                        StartIndex = startFractal.Index,
                        EndIndex = endFractal.Index,
                        StartFractal = startFractal,
                        EndFractal = endFractal,
                        IsUp = isUp,
                        BarCount = barCount
                    };

                    // 计算笔的最高点和最低点
                    stroke.High = isUp ? endFractal.High : startFractal.High;
                    stroke.Low = isUp ? startFractal.Low : endFractal.Low;

                    // 遍历笔范围内的所有合并K线，找到真正的最高最低点
                    for (int k = startFractal.Index; k <= endFractal.Index && k < state.MergedBars.Count; k++)
                    {
                        stroke.High = Math.Max(stroke.High, state.MergedBars[k].High);
                        stroke.Low = Math.Min(stroke.Low, state.MergedBars[k].Low);
                    }

                    // 计算MACD面积（用于背驰判断）
                    stroke.MACDArea = CalculateMACDArea(state, startFractal.OriginalIndex, endFractal.OriginalIndex, quotes);

                    newStrokes.Add(stroke);
                    startIdx = j;  // 下一笔从当前笔的终点开始
                    foundStroke = true;
                    break;
                }

                // 如果没有找到有效的笔
                if (!foundStroke)
                {
                    // 如果起点在内层循环中被更新过，从更新后的起点+1继续
                    // 否则从原始起点+1继续
                    if (startIdx != originalStartIdx)
                    {
                        startIdx++;  // 从更新后的起点+1开始
                    }
                    else
                    {
                        startIdx++;  // 从原始起点+1开始
                    }
                }
            }

            state.Strokes = newStrokes;
        }

        /// <summary>
        /// 计算指定范围内的MACD面积（用于背驰判断）
        /// </summary>
        private decimal CalculateMACDArea(State state, int startIndex, int endIndex, List<SkQuote> quotes)
        {
            if (state.MacdResults == null || state.MacdResults.Count == 0)
            {
                // 计算MACD
                try
                {
                    state.MacdResults = quotes.GetMacd(12, 26, 9).ToList();
                }
                catch
                {
                    return 0;
                }
            }

            decimal area = 0;
            for (int i = startIndex; i <= endIndex && i < state.MacdResults.Count; i++)
            {
                var macd = state.MacdResults[i];
                if (macd.Histogram.HasValue)
                {
                    area += Math.Abs((decimal)macd.Histogram.Value);
                }
            }
            return area;
        }

        #endregion

        #region 中枢识别

        /// <summary>
        /// 检查两笔是否重叠
        /// </summary>
        internal bool IsStrokesOverlap(Stroke stroke1, Stroke stroke2)
        {
            return stroke1.High > stroke2.Low && stroke1.Low < stroke2.High;
        }

        /// <summary>
        /// 识别中枢：寻找至少3笔连续重叠的区域
        /// 缠论定义：中枢由至少3笔重叠构成，ZG=min(各笔高点)，ZD=max(各笔低点)
        /// </summary>
        private void UpdateZhongShus(State state)
        {
            if (state.Strokes == null)
                return;

            int minStrokes = (int)ArgDic["zhongshuMinStrokes"];
            if (state.Strokes.Count < minStrokes)
                return;

            if (state.ZhongShus == null)
                state.ZhongShus = new List<ZhongShu>();

            // 从第一笔开始，寻找连续重叠的笔构成中枢
            var newZhongShus = new List<ZhongShu>();
            int i = 0;

            while (i <= state.Strokes.Count - minStrokes)
            {
                // 尝试从第i笔开始构建中枢
                var candidateStrokes = new List<Stroke> { state.Strokes[i] };
                decimal zg = state.Strokes[i].High;  // min(各笔高点)
                decimal zd = state.Strokes[i].Low;   // max(各笔低点)
                decimal gg = state.Strokes[i].High;  // 最高点
                decimal dd = state.Strokes[i].Low;   // 最低点

                for (int j = i + 1; j < state.Strokes.Count; j++)
                {
                    var stroke = state.Strokes[j];
                    decimal newZg = Math.Min(zg, stroke.High);
                    decimal newZd = Math.Max(zd, stroke.Low);

                    // 检查是否仍然有重叠区间
                    if (newZd < newZg)
                    {
                        // 有重叠，加入中枢
                        candidateStrokes.Add(stroke);
                        zg = newZg;
                        zd = newZd;
                        gg = Math.Max(gg, stroke.High);
                        dd = Math.Min(dd, stroke.Low);
                    }
                    else
                    {
                        // 无重叠，中枢结束
                        break;
                    }
                }

                // 检查是否满足最少笔数要求
                if (candidateStrokes.Count >= minStrokes)
                {
                    var zhongshu = new ZhongShu
                    {
                        StartIndex = candidateStrokes[0].StartIndex,
                        EndIndex = candidateStrokes[candidateStrokes.Count - 1].EndIndex,
                        ZG = zg,
                        ZD = zd,
                        GG = gg,
                        DD = dd,
                        Strokes = new List<Stroke>(candidateStrokes),
                        Level = 0
                    };

                    // 检查是否与上一个中枢重叠（中枢扩展）
                    if (newZhongShus.Count > 0)
                    {
                        var lastZs = newZhongShus[newZhongShus.Count - 1];
                        // 如果新中枢与上一个中枢有重叠，可以合并（中枢扩展）
                        if (zhongshu.ZD < lastZs.ZG && zhongshu.ZG > lastZs.ZD)
                        {
                            // 扩展上一个中枢
                            lastZs.EndIndex = zhongshu.EndIndex;
                            lastZs.ZG = Math.Min(lastZs.ZG, zhongshu.ZG);
                            lastZs.ZD = Math.Max(lastZs.ZD, zhongshu.ZD);
                            lastZs.GG = Math.Max(lastZs.GG, zhongshu.GG);
                            lastZs.DD = Math.Min(lastZs.DD, zhongshu.DD);
                            lastZs.Strokes.AddRange(candidateStrokes.Skip(1));
                        }
                        else
                        {
                            newZhongShus.Add(zhongshu);
                        }
                    }
                    else
                    {
                        newZhongShus.Add(zhongshu);
                    }

                    // 跳过已处理的笔
                    i += candidateStrokes.Count - 1;
                }
                else
                {
                    i++;
                }
            }

            state.ZhongShus = newZhongShus;
            state.CurrentZhongShu = newZhongShus.Count > 0 ? newZhongShus[newZhongShus.Count - 1] : null;
        }

        #endregion

        #region 背驰判断

        /// <summary>
        /// 判断两笔是否存在背驰
        /// 背驰定义：同向的两笔，后一笔的MACD面积小于前一笔
        /// </summary>
        internal bool IsDivergence(Stroke stroke1, Stroke stroke2)
        {
            if (stroke1 == null || stroke2 == null)
                return false;

            // 必须是同向的笔
            if (stroke1.IsUp != stroke2.IsUp)
                return false;

            // 后一笔的MACD面积小于前一笔，且价格创新高/新低
            if (stroke1.IsUp)
            {
                // 向上笔：后一笔创新高但MACD面积减小
                return stroke2.High >= stroke1.High && stroke2.MACDArea < stroke1.MACDArea;
            }
            else
            {
                // 向下笔：后一笔创新低但MACD面积减小
                return stroke2.Low <= stroke1.Low && stroke2.MACDArea < stroke1.MACDArea;
            }
        }

        /// <summary>
        /// 在笔列表中寻找同向的前一笔（用于背驰比较）
        /// </summary>
        private Stroke FindPreviousSameDirectionStroke(List<Stroke> strokes, int currentIndex)
        {
            if (currentIndex < 2 || strokes.Count <= currentIndex)
                return null;

            var current = strokes[currentIndex];
            // 向前查找同向的笔（跳过一笔）
            for (int i = currentIndex - 2; i >= 0; i -= 2)
            {
                if (strokes[i].IsUp == current.IsUp)
                    return strokes[i];
            }
            return null;
        }

        #endregion

        #region 买卖点识别

        /// <summary>
        /// 识别买卖点
        /// </summary>
        private void UpdateBSPoints(State state, List<SkQuote> quotes)
        {
            if (state.BSPoints == null)
                state.BSPoints = new List<BSPoint>();

            if (state.Strokes == null || state.Strokes.Count < 3)
                return;

            bool useDivergence = (int)ArgDic["useDivergence"] == 1;
            var lastStroke = state.Strokes[state.Strokes.Count - 1];
            var prevStroke = state.Strokes.Count >= 2 ? state.Strokes[state.Strokes.Count - 2] : null;
            var currentPrice = quotes.Last().Close;

            // 一买：向下趋势背驰后的第一个买点
            if (!lastStroke.IsUp && prevStroke != null && !prevStroke.IsUp)
            {
                var prevSameDir = FindPreviousSameDirectionStroke(state.Strokes, state.Strokes.Count - 1);
                if (prevSameDir != null)
                {
                    bool isDivergence = !useDivergence || IsDivergence(prevSameDir, lastStroke);
                    if (isDivergence && lastStroke.Low <= prevSameDir.Low)
                    {
                        // 确认一买：向下笔结束，形成底分型
                        var buy1 = new BSPoint
                        {
                            Type = BSPointType.Buy1,
                            Index = lastStroke.EndIndex,
                            Price = lastStroke.Low,
                            Date = lastStroke.EndFractal.Date,
                            IsDivergence = isDivergence
                        };
                        state.LastBuy1 = buy1;
                        state.BSPoints.Add(buy1);
                    }
                }
            }

            // 一卖：向上趋势背驰后的第一个卖点
            if (lastStroke.IsUp && prevStroke != null && prevStroke.IsUp)
            {
                var prevSameDir = FindPreviousSameDirectionStroke(state.Strokes, state.Strokes.Count - 1);
                if (prevSameDir != null)
                {
                    bool isDivergence = !useDivergence || IsDivergence(prevSameDir, lastStroke);
                    if (isDivergence && lastStroke.High >= prevSameDir.High)
                    {
                        // 确认一卖：向上笔结束，形成顶分型
                        var sell1 = new BSPoint
                        {
                            Type = BSPointType.Sell1,
                            Index = lastStroke.EndIndex,
                            Price = lastStroke.High,
                            Date = lastStroke.EndFractal.Date,
                            IsDivergence = isDivergence
                        };
                        state.LastSell1 = sell1;
                        state.BSPoints.Add(sell1);
                    }
                }
            }

            // 二买：一买后回调不破一买低点
            if (state.LastBuy1 != null && !lastStroke.IsUp && prevStroke != null && prevStroke.IsUp)
            {
                if (lastStroke.Low > state.LastBuy1.Price)
                {
                    var buy2 = new BSPoint
                    {
                        Type = BSPointType.Buy2,
                        Index = lastStroke.EndIndex,
                        Price = lastStroke.Low,
                        Date = lastStroke.EndFractal.Date,
                        IsDivergence = false
                    };
                    state.BSPoints.Add(buy2);
                }
            }

            // 二卖：一卖后反弹不破一卖高点
            if (state.LastSell1 != null && lastStroke.IsUp && prevStroke != null && !prevStroke.IsUp)
            {
                if (lastStroke.High < state.LastSell1.Price)
                {
                    var sell2 = new BSPoint
                    {
                        Type = BSPointType.Sell2,
                        Index = lastStroke.EndIndex,
                        Price = lastStroke.High,
                        Date = lastStroke.EndFractal.Date,
                        IsDivergence = false
                    };
                    state.BSPoints.Add(sell2);
                }
            }

            // 三买：离开中枢向上后回踩不进中枢
            if (state.CurrentZhongShu != null && state.CurrentZhongShu.IsValid)
            {
                var zs = state.CurrentZhongShu;
                // 向上离开中枢后回踩
                if (prevStroke != null && prevStroke.IsUp && !lastStroke.IsUp)
                {
                    // 前一笔向上突破中枢，当前笔向下回踩不进中枢
                    if (prevStroke.High > zs.ZG && lastStroke.Low >= zs.ZG)
                    {
                        var buy3 = new BSPoint
                        {
                            Type = BSPointType.Buy3,
                            Index = lastStroke.EndIndex,
                            Price = lastStroke.Low,
                            Date = lastStroke.EndFractal.Date,
                            IsDivergence = false
                        };
                        state.BSPoints.Add(buy3);
                    }
                }

                // 三卖：离开中枢向下后回抽不进中枢
                if (prevStroke != null && !prevStroke.IsUp && lastStroke.IsUp)
                {
                    // 前一笔向下突破中枢，当前笔向上回抽不进中枢
                    if (prevStroke.Low < zs.ZD && lastStroke.High <= zs.ZD)
                    {
                        var sell3 = new BSPoint
                        {
                            Type = BSPointType.Sell3,
                            Index = lastStroke.EndIndex,
                            Price = lastStroke.High,
                            Date = lastStroke.EndFractal.Date,
                            IsDivergence = false
                        };
                        state.BSPoints.Add(sell3);
                    }
                }
            }
        }

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal)
                return;

            int minBarCount = (int)ArgDic["minBarCount"];
            if (tu.QuoteList.Count < minBarCount)
                return;

            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            var q = tu.QuoteList.Last();

            // 获取或创建状态
            State s = null;
            var sk = tu.GetStateKey();
            if (_stateDic.ContainsKey(sk))
            {
                s = _stateDic[sk];
            }
            else
            {
                s = new State();
                s.MergedBars = new List<MergedBar>();
                s.Fractals = new List<Fractal>();
                s.Strokes = new List<Stroke>();
                s.Segments = new List<Segment>();
                s.ZhongShus = new List<ZhongShu>();
                s.BSPoints = new List<BSPoint>();
                s.LastProcessedIndex = 0;
                _stateDic[sk] = s;
            }

            // 步骤1：K线包含关系处理
            ProcessContainRelation(s, tu.QuoteList);

            // 步骤2：分型识别（基于合并后的K线）
            UpdateFractals(s);

            // 步骤3：笔构建
            UpdateStrokes(s, tu.QuoteList);

            // 步骤4：中枢识别
            UpdateZhongShus(s);

            // 步骤5：买卖点识别
            UpdateBSPoints(s, tu.QuoteList);

            // 绘制最近的分型
            if (s.Fractals != null && s.Fractals.Count > 0)
            {
                var lastFractal = s.Fractals[s.Fractals.Count - 1];
                if (lastFractal.IsConfirmed)
                {
                    if (lastFractal.Type == FractalType.Top)
                    {
                        Plot("main", "fractal_top", PlotType.POINT, (double)lastFractal.Price);
                    }
                    else
                    {
                        Plot("main", "fractal_bottom", PlotType.POINT, (double)lastFractal.Price);
                    }
                }
            }

            // 绘制笔
            if (s.Strokes != null && s.Strokes.Count > 0)
            {
                int currentBarIndex = tu.QuoteList.Count - 1;
                foreach (var stroke in s.Strokes)
                {
                    var extra = new PlotLineSegmentExtra
                    {
                        StartOffsetIndex = currentBarIndex - stroke.StartFractal.LastOriginalIndex,
                        EndOffsetIndex = currentBarIndex - stroke.EndFractal.LastOriginalIndex,
                        Val1 = tu.QuoteList[stroke.StartFractal.LastOriginalIndex].Close,
                        Val2 = tu.QuoteList[stroke.EndFractal.LastOriginalIndex].Close
                    };
                    Plot("main", "bi", PlotType.LINE_SEGMENT, 0, extra);
                }
            }

            // 绘制当前中枢
            if (s.CurrentZhongShu != null && s.CurrentZhongShu.IsValid)
            {
                var zs = s.CurrentZhongShu;
                Plot("main", "zhongshu_zg", PlotType.LINE, (double)zs.ZG);
                Plot("main", "zhongshu_zd", PlotType.LINE, (double)zs.ZD);
            }

            // 计算手数
            var num = (decimal)ArgDic["lots"];
            var lotsMode = (int)ArgDic["lotsMode"];
            if (lotsMode == 1)
            {
                var s2 = GetSymbol(tu.MktSymbol);
                num = ((decimal)ArgDic["money"] / (q.Close * s2.multiplier * s2.margin_ratio));
                if (s2.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }

            // 交易逻辑：基于买卖点和笔方向变化
            bool useZhongShu = (int)ArgDic["useZhongShu"] == 1;
            var currentPrice = q.Close;

            if (s.Strokes == null || s.Strokes.Count < 2)
                return;

            var lastStroke = s.Strokes[s.Strokes.Count - 1];
            var prevStroke = s.Strokes[s.Strokes.Count - 2];

            // 检查最新的买卖点
            BSPoint latestBSPoint = null;
            if (s.BSPoints != null && s.BSPoints.Count > 0)
            {
                latestBSPoint = s.BSPoints[s.BSPoints.Count - 1];
            }

            if (useZhongShu && s.CurrentZhongShu != null && s.CurrentZhongShu.IsValid)
            {
                // 基于中枢和买卖点的交易逻辑
                var zs = s.CurrentZhongShu;

                if (s.Status == 0)  // 无持仓状态
                {
                    // 三买：向上突破中枢后回踩不破中枢高点
                    if (prevStroke.IsUp && !lastStroke.IsUp &&
                        prevStroke.High > zs.ZG && lastStroke.Low >= zs.ZG &&
                        mode != 2)
                    {
                        s.Status = 1;
                        s.Num = num;
                        Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                    }
                    // 三卖：向下突破中枢后回抽不破中枢低点
                    else if (!prevStroke.IsUp && lastStroke.IsUp &&
                             prevStroke.Low < zs.ZD && lastStroke.High <= zs.ZD &&
                             mode != 1)
                    {
                        s.Status = 2;
                        s.Num = num;
                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                    }
                    // 一买：向下笔结束，形成底分型，且在中枢下方
                    else if (!prevStroke.IsUp && lastStroke.IsUp &&
                             currentPrice < zs.ZD && mode != 2)
                    {
                        // 检查是否有背驰
                        bool hasDivergence = latestBSPoint != null && 
                                            latestBSPoint.Type == BSPointType.Buy1 &&
                                            latestBSPoint.IsDivergence;
                        if (hasDivergence || (int)ArgDic["useDivergence"] == 0)
                        {
                            s.Status = 1;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                        }
                    }
                    // 一卖：向上笔结束，形成顶分型，且在中枢上方
                    else if (prevStroke.IsUp && !lastStroke.IsUp &&
                             currentPrice > zs.ZG && mode != 1)
                    {
                        // 检查是否有背驰
                        bool hasDivergence = latestBSPoint != null && 
                                            latestBSPoint.Type == BSPointType.Sell1 &&
                                            latestBSPoint.IsDivergence;
                        if (hasDivergence || (int)ArgDic["useDivergence"] == 0)
                        {
                            s.Status = 2;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        }
                    }
                }
                else if (s.Status == 1)  // 多仓状态
                {
                    // 向上笔转为向下笔，平多
                    if (prevStroke.IsUp && !lastStroke.IsUp)
                    {
                        var oriNum = s.Num;
                        Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                        // 如果价格跌破中枢低点，可以考虑开空
                        if (currentPrice < zs.ZD && mode != 1)
                        {
                            s.Status = 2;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        }
                        else
                        {
                            s.Status = 0;
                            s.Num = 0;
                        }
                    }
                }
                else if (s.Status == 2)  // 空仓（做空）状态
                {
                    // 向下笔转为向上笔，平空
                    if (!prevStroke.IsUp && lastStroke.IsUp)
                    {
                        var oriNum = s.Num;
                        Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                        // 如果价格突破中枢高点，可以考虑开多
                        if (currentPrice > zs.ZG && mode != 2)
                        {
                            s.Status = 1;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                        }
                        else
                        {
                            s.Status = 0;
                            s.Num = 0;
                        }
                    }
                }
            }
            else
            {
                // 基于笔的方向变化的交易逻辑（不使用中枢）
                if (s.Status == 0)  // 无持仓状态
                {
                    // 向下笔转为向上笔，买入信号
                    if (!prevStroke.IsUp && lastStroke.IsUp && mode != 2)
                    {
                        s.Status = 1;
                        s.Num = num;
                        Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                    }
                    // 向上笔转为向下笔，卖出信号
                    else if (prevStroke.IsUp && !lastStroke.IsUp && mode != 1)
                    {
                        s.Status = 2;
                        s.Num = num;
                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                    }
                }
                else if (s.Status == 1)  // 多仓状态
                {
                    // 向上笔转为向下笔，平多开空
                    if (prevStroke.IsUp && !lastStroke.IsUp)
                    {
                        var oriNum = s.Num;
                        Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                        if (mode != 1)
                        {
                            s.Status = 2;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        }
                        else
                        {
                            s.Status = 0;
                            s.Num = 0;
                        }
                    }
                }
                else if (s.Status == 2)  // 空仓（做空）状态
                {
                    // 向下笔转为向上笔，平空开多
                    if (!prevStroke.IsUp && lastStroke.IsUp)
                    {
                        var oriNum = s.Num;
                        Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                        if (mode != 2)
                        {
                            s.Status = 1;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                        }
                        else
                        {
                            s.Status = 0;
                            s.Num = 0;
                        }
                    }
                }
            }
        }
    }
}