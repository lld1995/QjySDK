using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Linq;
using static Model.EnumDef;
using System.Collections.Generic;

namespace QjySDK.Stg
{
    public class DowTheory : StgBase
    {
        public DowTheory()
        {
        }

        public DowTheory(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            
            // 波峰波谷识别参数
            sd.ArgDic["swingLookback"] = 5;          // 波峰波谷回溯周期
            sd.ArgDic["minSwingPercent"] = 1.0d;     // 最小波动幅度百分比
            
            // 趋势确认参数
            sd.ArgDic["confirmBars"] = 2;            // 确认所需K线数
            sd.ArgDic["breakoutPercent"] = 0.1d;    // 突破确认百分比
            
            // 风控参数
            sd.ArgDic["stopLossPercent"] = 8.0m;    // 止损百分比
            sd.ArgDic["takeProfitPercent"] = 15.0m;  // 止盈百分比
            sd.ArgDic["trailingStop"] = 1;          // 是否启用移动止损 0-否 1-是
            sd.ArgDic["trailingPercent"] = 8.0m;    // 移动止损百分比
            
            // 交易模式
            sd.ArgDic["mode"] = 0;                   // 0-双向 1-仅做多 2-仅做空
            sd.ArgDic["sendMode"] = 0;              // 0-立即 1-下个开盘
            
            // 手数控制
            sd.ArgDic["lotsMode"] = 1;              // 0-固定手数 1-固定金额
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // 参数说明
            sd.ArgDescDic["swingLookback"] = new ArgDesc() { Text = "波峰波谷回溯", Explain = "识别波峰波谷的回溯K线数" };
            sd.ArgDescDic["minSwingPercent"] = new ArgDesc() { Text = "最小波动幅度", Explain = "有效波峰波谷的最小幅度百分比" };
            sd.ArgDescDic["confirmBars"] = new ArgDesc() { Text = "确认K线数", Explain = "趋势确认所需的K线数量" };
            sd.ArgDescDic["breakoutPercent"] = new ArgDesc() { Text = "突破确认", Explain = "突破前高/低的确认百分比" };
            sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "止损触发的价格百分比" };
            sd.ArgDescDic["takeProfitPercent"] = new ArgDesc() { Text = "止盈百分比", Explain = "止盈触发的价格百分比" };
            sd.ArgDescDic["trailingStop"] = new ArgDesc() { Text = "移动止损", Explain = "0-关闭 1-开启" };
            sd.ArgDescDic["trailingPercent"] = new ArgDesc() { Text = "移动止损幅度", Explain = "从最高/低点回撤的百分比" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0-双向 1-仅做多 2-仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0-立即 1-下个开盘" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0-固定手数 1-固定金额" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;
            
            // 图表颜色
            sd.ColorDic["trend-Trend"] = "#2196F3";
            sd.ColorDic["trend-SwingHigh"] = "#F44336";
            sd.ColorDic["trend-SwingLow"] = "#4CAF50";
            
            return sd;
        }

        // 波峰波谷点
        private class SwingPoint
        {
            public int Index { get; set; }
            public decimal Price { get; set; }
            public DateTimeOffset Date { get; set; }
            public bool IsHigh { get; set; }  // true=波峰, false=波谷
        }

        // 趋势状态
        private enum TrendState
        {
            Unknown = 0,    // 未知
            Uptrend = 1,    // 上升趋势
            Downtrend = 2   // 下降趋势
        }

        // 交易状态
        private class State
        {
            public int Position { get; set; }           // 0-空仓 1-多头 2-空头
            public decimal Num { get; set; }            // 持仓数量
            public decimal EntryPrice { get; set; }     // 入场价格
            public decimal StopLoss { get; set; }       // 止损价
            public decimal TakeProfit { get; set; }     // 止盈价
            public decimal TrailingStop { get; set; }   // 移动止损价
            public decimal HighestSinceEntry { get; set; }  // 入场后最高价
            public decimal LowestSinceEntry { get; set; }   // 入场后最低价
            
            // 道氏理论状态
            public TrendState Trend { get; set; }       // 当前趋势
            public List<SwingPoint> SwingPoints { get; set; } = new List<SwingPoint>();
            public decimal LastSwingHigh { get; set; }  // 最近波峰
            public decimal LastSwingLow { get; set; }   // 最近波谷
            public decimal PrevSwingHigh { get; set; }  // 前一波峰
            public decimal PrevSwingLow { get; set; }   // 前一波谷
            public int ConfirmCount { get; set; }       // 确认计数
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            
            if (!isFinal) return;
            
            int swingLookback = (int)ArgDic["swingLookback"];
            int minBars = swingLookback * 2 + 5;
            
            if (tu.QuoteList.Count < minBars) return;

            // 获取参数
            double minSwingPercent = (double)ArgDic["minSwingPercent"];
            int confirmBars = (int)ArgDic["confirmBars"];
            double breakoutPercent = (double)ArgDic["breakoutPercent"];
            decimal stopLossPercent = (decimal)ArgDic["stopLossPercent"];
            decimal takeProfitPercent = (decimal)ArgDic["takeProfitPercent"];
            int trailingStopEnabled = (int)ArgDic["trailingStop"];
            decimal trailingPercent = (decimal)ArgDic["trailingPercent"];
            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];

            var q = tu.QuoteList.Last();
            
            // 获取或创建状态
            var sk = tu.GetStateKey();
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new State();
            }
            var s = _stateDic[sk];

            // 识别波峰波谷
            UpdateSwingPoints(tu.QuoteList, s, swingLookback, minSwingPercent);
            
            // 判断趋势
            var newTrend = DetermineTrend(s);
            
            // 绘制趋势指标
            PlotTrendIndicator(s, newTrend);
            
            // 检测趋势变化
            bool trendChanged = newTrend != s.Trend && newTrend != TrendState.Unknown;
            if (trendChanged)
            {
                s.Trend = newTrend;
                s.ConfirmCount = 0;
            }
            
            // 计算手数
            decimal num = CalculateLots(tu.MktSymbol, q.Close);

            // 更新移动止损
            if (s.Position != 0 && trailingStopEnabled == 1)
            {
                UpdateTrailingStop(s, q, trailingPercent);
            }

            // 检查止损止盈
            if (s.Position != 0)
            {
                if (CheckStopLossTakeProfit(s, q, tu.MktSymbol, period, sendMode))
                {
                    return;
                }
            }

            // 交易逻辑
            if (s.Position == 0)
            {
                // 空仓时寻找入场机会
                if (s.Trend == TrendState.Uptrend && mode != 2)
                {
                    // 上升趋势中，价格回调到前一波谷附近后反弹，突破前高时做多
                    if (IsValidLongEntry(s, q, breakoutPercent))
                    {
                        s.ConfirmCount++;
                        if (s.ConfirmCount >= confirmBars)
                        {
                            EnterLong(s, tu.MktSymbol, q, num, period, sendMode, stopLossPercent, takeProfitPercent);
                        }
                    }
                    else
                    {
                        s.ConfirmCount = 0;
                    }
                }
                else if (s.Trend == TrendState.Downtrend && mode != 1)
                {
                    // 下降趋势中，价格反弹到前一波峰附近后回落，跌破前低时做空
                    if (IsValidShortEntry(s, q, breakoutPercent))
                    {
                        s.ConfirmCount++;
                        if (s.ConfirmCount >= confirmBars)
                        {
                            EnterShort(s, tu.MktSymbol, q, num, period, sendMode, stopLossPercent, takeProfitPercent);
                        }
                    }
                    else
                    {
                        s.ConfirmCount = 0;
                    }
                }
            }
            else if (s.Position == 1)
            {
                // 持有多头时检查趋势反转
                if (s.Trend == TrendState.Downtrend || IsTrendBroken(s, q, true))
                {
                    // 趋势反转，平多
                    ExitLong(s, tu.MktSymbol, q, period, sendMode);
                    
                    // 如果允许做空，反手
                    if (mode != 1 && s.Trend == TrendState.Downtrend)
                    {
                        EnterShort(s, tu.MktSymbol, q, num, period, sendMode, stopLossPercent, takeProfitPercent);
                    }
                }
            }
            else if (s.Position == 2)
            {
                // 持有空头时检查趋势反转
                if (s.Trend == TrendState.Uptrend || IsTrendBroken(s, q, false))
                {
                    // 趋势反转，平空
                    ExitShort(s, tu.MktSymbol, q, period, sendMode);
                    
                    // 如果允许做多，反手
                    if (mode != 2 && s.Trend == TrendState.Uptrend)
                    {
                        EnterLong(s, tu.MktSymbol, q, num, period, sendMode, stopLossPercent, takeProfitPercent);
                    }
                }
            }
        }

        // 更新波峰波谷点
        private void UpdateSwingPoints(List<SkQuote> quotes, State s, int lookback, double minPercent)
        {
            int count = quotes.Count;
            if (count < lookback * 2 + 1) return;

            // 检查是否形成新的波峰
            int pivotIndex = count - 1 - lookback;
            var pivotBar = quotes[pivotIndex];
            
            bool isSwingHigh = true;
            bool isSwingLow = true;
            
            for (int i = pivotIndex - lookback; i <= pivotIndex + lookback; i++)
            {
                if (i == pivotIndex || i < 0 || i >= count) continue;
                
                if (quotes[i].High >= pivotBar.High)
                    isSwingHigh = false;
                if (quotes[i].Low <= pivotBar.Low)
                    isSwingLow = false;
            }

            // 检查最小波动幅度
            if (isSwingHigh && s.SwingPoints.Count > 0)
            {
                var lastPoint = s.SwingPoints.Last();
                double change = (double)((pivotBar.High - lastPoint.Price) / lastPoint.Price * 100);
                if (Math.Abs(change) < minPercent)
                    isSwingHigh = false;
            }
            
            if (isSwingLow && s.SwingPoints.Count > 0)
            {
                var lastPoint = s.SwingPoints.Last();
                double change = (double)((pivotBar.Low - lastPoint.Price) / lastPoint.Price * 100);
                if (Math.Abs(change) < minPercent)
                    isSwingLow = false;
            }

            // 添加新的波峰波谷
            if (isSwingHigh)
            {
                // 如果上一个点也是波峰，取较高者
                if (s.SwingPoints.Count > 0 && s.SwingPoints.Last().IsHigh)
                {
                    if (pivotBar.High > s.SwingPoints.Last().Price)
                    {
                        s.SwingPoints.RemoveAt(s.SwingPoints.Count - 1);
                        AddSwingPoint(s, pivotIndex, pivotBar.High, pivotBar.Date, true);
                    }
                }
                else
                {
                    AddSwingPoint(s, pivotIndex, pivotBar.High, pivotBar.Date, true);
                }
            }
            else if (isSwingLow)
            {
                // 如果上一个点也是波谷，取较低者
                if (s.SwingPoints.Count > 0 && !s.SwingPoints.Last().IsHigh)
                {
                    if (pivotBar.Low < s.SwingPoints.Last().Price)
                    {
                        s.SwingPoints.RemoveAt(s.SwingPoints.Count - 1);
                        AddSwingPoint(s, pivotIndex, pivotBar.Low, pivotBar.Date, false);
                    }
                }
                else
                {
                    AddSwingPoint(s, pivotIndex, pivotBar.Low, pivotBar.Date, false);
                }
            }

            // 保留最近的波峰波谷点（最多20个）
            while (s.SwingPoints.Count > 20)
            {
                s.SwingPoints.RemoveAt(0);
            }

            // 更新最近的波峰波谷价格
            UpdateLastSwings(s);
        }

        private void AddSwingPoint(State s, int index, decimal price, DateTimeOffset date, bool isHigh)
        {
            s.SwingPoints.Add(new SwingPoint
            {
                Index = index,
                Price = price,
                Date = date,
                IsHigh = isHigh
            });
        }

        private void UpdateLastSwings(State s)
        {
            var highs = s.SwingPoints.Where(p => p.IsHigh).ToList();
            var lows = s.SwingPoints.Where(p => !p.IsHigh).ToList();

            if (highs.Count >= 2)
            {
                s.LastSwingHigh = highs[highs.Count - 1].Price;
                s.PrevSwingHigh = highs[highs.Count - 2].Price;
            }
            else if (highs.Count == 1)
            {
                s.LastSwingHigh = highs[0].Price;
                s.PrevSwingHigh = highs[0].Price;
            }

            if (lows.Count >= 2)
            {
                s.LastSwingLow = lows[lows.Count - 1].Price;
                s.PrevSwingLow = lows[lows.Count - 2].Price;
            }
            else if (lows.Count == 1)
            {
                s.LastSwingLow = lows[0].Price;
                s.PrevSwingLow = lows[0].Price;
            }
        }

        // 判断趋势（道氏理论核心）
        private TrendState DetermineTrend(State s)
        {
            if (s.SwingPoints.Count < 4) return TrendState.Unknown;

            var highs = s.SwingPoints.Where(p => p.IsHigh).OrderBy(p => p.Index).ToList();
            var lows = s.SwingPoints.Where(p => !p.IsHigh).OrderBy(p => p.Index).ToList();

            if (highs.Count < 2 || lows.Count < 2) return TrendState.Unknown;

            // 获取最近两个波峰和波谷
            var lastHigh = highs[highs.Count - 1];
            var prevHigh = highs[highs.Count - 2];
            var lastLow = lows[lows.Count - 1];
            var prevLow = lows[lows.Count - 2];

            // 上升趋势：更高的高点(HH) + 更高的低点(HL)
            bool higherHigh = lastHigh.Price > prevHigh.Price;
            bool higherLow = lastLow.Price > prevLow.Price;

            // 下降趋势：更低的高点(LH) + 更低的低点(LL)
            bool lowerHigh = lastHigh.Price < prevHigh.Price;
            bool lowerLow = lastLow.Price < prevLow.Price;

            if (higherHigh && higherLow)
                return TrendState.Uptrend;
            
            if (lowerHigh && lowerLow)
                return TrendState.Downtrend;

            // 保持当前趋势
            return s.Trend;
        }

        // 检查多头入场条件
        private bool IsValidLongEntry(State s, SkQuote q, double breakoutPercent)
        {
            if (s.LastSwingHigh == 0 || s.LastSwingLow == 0) return false;
            
            // 价格突破前一波峰
            decimal breakoutLevel = s.PrevSwingHigh * (1 + (decimal)breakoutPercent / 100);
            return q.Close > breakoutLevel;
        }

        // 检查空头入场条件
        private bool IsValidShortEntry(State s, SkQuote q, double breakoutPercent)
        {
            if (s.LastSwingHigh == 0 || s.LastSwingLow == 0) return false;
            
            // 价格跌破前一波谷
            decimal breakoutLevel = s.PrevSwingLow * (1 - (decimal)breakoutPercent / 100);
            return q.Close < breakoutLevel;
        }

        // 检查趋势是否被打破
        private bool IsTrendBroken(State s, SkQuote q, bool isLong)
        {
            if (isLong)
            {
                // 多头持仓时，价格跌破最近波谷表示趋势可能反转
                return q.Close < s.LastSwingLow;
            }
            else
            {
                // 空头持仓时，价格突破最近波峰表示趋势可能反转
                return q.Close > s.LastSwingHigh;
            }
        }

        // 计算手数
        private decimal CalculateLots(string mktSymbol, decimal price)
        {
            var lotsMode = (int)ArgDic["lotsMode"];
            var num = (decimal)ArgDic["lots"];
            
            if (lotsMode == 1)
            {
                var symbol = GetSymbol(mktSymbol);
                num = (decimal)ArgDic["money"] / (price * symbol.multiplier * symbol.margin_ratio);
                if (symbol.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }
            
            return num;
        }

        // 更新移动止损
        private void UpdateTrailingStop(State s, SkQuote q, decimal trailingPercent)
        {
            if (s.Position == 1)
            {
                // 多头：跟踪最高价
                if (q.High > s.HighestSinceEntry)
                {
                    s.HighestSinceEntry = q.High;
                    decimal newStop = s.HighestSinceEntry * (1 - trailingPercent / 100);
                    if (newStop > s.TrailingStop)
                    {
                        s.TrailingStop = newStop;
                    }
                }
            }
            else if (s.Position == 2)
            {
                // 空头：跟踪最低价
                if (q.Low < s.LowestSinceEntry || s.LowestSinceEntry == 0)
                {
                    s.LowestSinceEntry = q.Low;
                    decimal newStop = s.LowestSinceEntry * (1 + trailingPercent / 100);
                    if (newStop < s.TrailingStop || s.TrailingStop == 0)
                    {
                        s.TrailingStop = newStop;
                    }
                }
            }
        }

        // 检查止损止盈
        private bool CheckStopLossTakeProfit(State s, SkQuote q, string mktSymbol, Period period, int sendMode)
        {
            if (s.Position == 1)
            {
                // 多头止损
                decimal effectiveStop = Math.Max(s.StopLoss, s.TrailingStop);
                if (q.Close <= effectiveStop)
                {
                    Trade(mktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    ResetPosition(s);
                    return true;
                }
                // 多头止盈
                if (q.Close >= s.TakeProfit)
                {
                    Trade(mktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    ResetPosition(s);
                    return true;
                }
            }
            else if (s.Position == 2)
            {
                // 空头止损
                decimal effectiveStop = s.TrailingStop > 0 ? Math.Min(s.StopLoss, s.TrailingStop) : s.StopLoss;
                if (q.Close >= effectiveStop)
                {
                    Trade(mktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                    ResetPosition(s);
                    return true;
                }
                // 空头止盈
                if (q.Close <= s.TakeProfit)
                {
                    Trade(mktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                    ResetPosition(s);
                    return true;
                }
            }
            return false;
        }

        // 做多入场
        private void EnterLong(State s, string mktSymbol, SkQuote q, decimal num, Period period, int sendMode, decimal stopLossPercent, decimal takeProfitPercent)
        {
            s.Position = 1;
            s.Num = num;
            s.EntryPrice = q.Close;
            s.StopLoss = q.Close * (1 - stopLossPercent / 100);
            s.TakeProfit = q.Close * (1 + takeProfitPercent / 100);
            s.HighestSinceEntry = q.High;
            s.TrailingStop = s.StopLoss;
            s.ConfirmCount = 0;
            
            Trade(mktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
        }

        // 做空入场
        private void EnterShort(State s, string mktSymbol, SkQuote q, decimal num, Period period, int sendMode, decimal stopLossPercent, decimal takeProfitPercent)
        {
            s.Position = 2;
            s.Num = num;
            s.EntryPrice = q.Close;
            s.StopLoss = q.Close * (1 + stopLossPercent / 100);
            s.TakeProfit = q.Close * (1 - takeProfitPercent / 100);
            s.LowestSinceEntry = q.Low;
            s.TrailingStop = s.StopLoss;
            s.ConfirmCount = 0;
            
            Trade(mktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
        }

        // 平多
        private void ExitLong(State s, string mktSymbol, SkQuote q, Period period, int sendMode)
        {
            Trade(mktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
            ResetPosition(s);
        }

        // 平空
        private void ExitShort(State s, string mktSymbol, SkQuote q, Period period, int sendMode)
        {
            Trade(mktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
            ResetPosition(s);
        }

        // 重置持仓状态
        private void ResetPosition(State s)
        {
            s.Position = 0;
            s.Num = 0;
            s.EntryPrice = 0;
            s.StopLoss = 0;
            s.TakeProfit = 0;
            s.TrailingStop = 0;
            s.HighestSinceEntry = 0;
            s.LowestSinceEntry = 0;
            s.ConfirmCount = 0;
        }

        // 绘制趋势指标
        private void PlotTrendIndicator(State s, TrendState trend)
        {
            // 趋势值：1=上升 0=震荡 -1=下降
            double trendValue = trend == TrendState.Uptrend ? 1 : (trend == TrendState.Downtrend ? -1 : 0);
            Plot("trend", "Trend", PlotType.LINE, trendValue);
            
            // 绘制波峰波谷（价格水平，绘制在主图）
            if (s.LastSwingHigh > 0)
                Plot("main", "SwingHigh", PlotType.LINE, (double)s.LastSwingHigh);
            if (s.LastSwingLow > 0)
                Plot("main", "SwingLow", PlotType.LINE, (double)s.LastSwingLow);
        }
    }
}
