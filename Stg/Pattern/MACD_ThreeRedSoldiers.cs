using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    /// <summary>
    /// MACD + 红三兵 组合交易策略
    /// 
    /// 核心理念：
    /// 结合MACD动量指标与红三兵K线形态，捕捉趋势反转或延续的高概率入场点。
    /// 红三兵形态确认多头力量，MACD提供动量确认和过滤。
    /// 
    /// 红三兵形态定义：
    /// 1. 连续三根阳线
    /// 2. 每根阳线的收盘价高于前一根
    /// 3. 每根阳线的开盘价在前一根实体范围内
    /// 4. 每根阳线的实体较为饱满（上下影线不宜过长）
    /// 
    /// 绿三兵（三只乌鸦）形态定义：
    /// 1. 连续三根阴线
    /// 2. 每根阴线的收盘价低于前一根
    /// 3. 每根阴线的开盘价在前一根实体范围内
    /// 4. 每根阴线的实体较为饱满
    /// 
    /// 入场条件：
    /// 【做多】
    ///    - 出现红三兵形态
    ///    - MACD金叉 或 MACD > Signal（多头动量）
    ///    - 可选：MACD柱状图递增
    ///    - 可选：MACD在零轴附近或上方
    /// 
    /// 【做空】
    ///    - 出现绿三兵（三只乌鸦）形态
    ///    - MACD死叉 或 MACD < Signal（空头动量）
    ///    - 可选：MACD柱状图递减
    ///    - 可选：MACD在零轴附近或下方
    /// 
    /// 出场条件：
    /// 1. 反向信号
    /// 2. 止损/止盈触发
    /// 3. MACD反向交叉
    /// </summary>
    public class MACD_ThreeRedSoldiers : StgBase
    {
        /// <summary>
        /// 策略状态
        /// </summary>
        private class State
        {
            public int Position { get; set; }           // 0:无持仓 1:多仓 -1:空仓
            public decimal Num { get; set; }            // 持仓数量
            public decimal EntryPrice { get; set; }     // 入场价格
            public decimal StopLoss { get; set; }       // 止损价
            public decimal TakeProfit { get; set; }     // 止盈价
            public decimal? PrevMACD { get; set; }      // 上一根K线的MACD值
            public decimal? PrevSignal { get; set; }    // 上一根K线的Signal值
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public MACD_ThreeRedSoldiers()
        {
        }

        public MACD_ThreeRedSoldiers(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // ========== MACD参数 ==========
            sd.ArgDescDic["fastPeriod"] = new ArgDesc { Text = "MACD快线周期", Explain = "快速EMA的计算周期" };
            sd.ArgDic["fastPeriod"] = 12;

            sd.ArgDescDic["slowPeriod"] = new ArgDesc { Text = "MACD慢线周期", Explain = "慢速EMA的计算周期" };
            sd.ArgDic["slowPeriod"] = 26;

            sd.ArgDescDic["signalPeriod"] = new ArgDesc { Text = "MACD信号线周期", Explain = "Signal线的EMA周期" };
            sd.ArgDic["signalPeriod"] = 9;

            // ========== 红三兵形态参数 ==========
            sd.ArgDescDic["minBodyRatio"] = new ArgDesc { Text = "最小实体比例", Explain = "实体占整根K线的最小比例(0-1)，用于过滤影线过长的K线" };
            sd.ArgDic["minBodyRatio"] = 0.6;

            sd.ArgDescDic["maxShadowRatio"] = new ArgDesc { Text = "最大影线比例", Explain = "单边影线占实体的最大比例，过滤影线过长的K线" };
            sd.ArgDic["maxShadowRatio"] = 0.5;

            sd.ArgDescDic["minBodySize"] = new ArgDesc { Text = "最小实体大小(%)", Explain = "实体占收盘价的最小百分比，过滤过小的K线" };
            sd.ArgDic["minBodySize"] = 0.1;

            // ========== MACD过滤参数 ==========
            sd.ArgDescDic["macdMode"] = new ArgDesc { Text = "MACD确认模式", Explain = "0=金叉死叉 1=DIF与DEA位置关系 2=柱状图方向 3=综合模式" };
            sd.ArgDic["macdMode"] = 3;

            sd.ArgDescDic["useZeroAxisFilter"] = new ArgDesc { Text = "零轴过滤", Explain = "1=启用(多头需MACD接近或高于零轴) 0=禁用" };
            sd.ArgDic["useZeroAxisFilter"] = 0;

            sd.ArgDescDic["zeroAxisTolerance"] = new ArgDesc { Text = "零轴容差", Explain = "MACD距离零轴的容差范围" };
            sd.ArgDic["zeroAxisTolerance"] = 0.0;

            sd.ArgDescDic["useHistogramConfirm"] = new ArgDesc { Text = "柱状图确认", Explain = "1=要求柱状图方向与信号一致 0=禁用" };
            sd.ArgDic["useHistogramConfirm"] = 1;

            // ========== 交易参数 ==========
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易模式", Explain = "0=双向 1=仅做多 2=仅做空" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "下单模式", Explain = "0=立即下单 1=下根K线开盘下单" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0=固定手数 1=固定金额" };
            sd.ArgDic["lotsMode"] = 0;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "固定手数", Explain = "每次交易的固定手数" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "固定金额", Explain = "每次交易的固定金额" };
            sd.ArgDic["money"] = 10000m;

            // ========== 止损止盈参数 ==========
            sd.ArgDescDic["useStopLoss"] = new ArgDesc { Text = "启用止损", Explain = "1=启用 0=禁用" };
            sd.ArgDic["useStopLoss"] = 1;

            sd.ArgDescDic["stopLossPercent"] = new ArgDesc { Text = "止损百分比", Explain = "止损距离占入场价的百分比" };
            sd.ArgDic["stopLossPercent"] = 2.0m;

            sd.ArgDescDic["useTakeProfit"] = new ArgDesc { Text = "启用止盈", Explain = "1=启用 0=禁用" };
            sd.ArgDic["useTakeProfit"] = 1;

            sd.ArgDescDic["takeProfitPercent"] = new ArgDesc { Text = "止盈百分比", Explain = "止盈距离占入场价的百分比" };
            sd.ArgDic["takeProfitPercent"] = 4.0m;

            sd.ArgDescDic["useMacdExit"] = new ArgDesc { Text = "MACD反向出场", Explain = "1=MACD反向交叉时平仓 0=禁用" };
            sd.ArgDic["useMacdExit"] = 1;

            // ========== 其他参数 ==========
            sd.ArgDescDic["minBarCount"] = new ArgDesc { Text = "最少K线数", Explain = "策略启动所需的最少K线数量" };
            sd.ArgDic["minBarCount"] = 35;

            // ========== 图表配置 ==========
            sd.ColorDic["macd-MACD"] = "#2196F3";           // MACD线颜色（蓝色）
            sd.ColorDic["macd-Signal"] = "#FF9800";         // 信号线颜色（橙色）
            sd.ColorDic["macd-Histogram"] = "#F6465D;#0ECB81"; // 柱状图颜色（红/绿）
            sd.ColorDic["main-StopLoss"] = "#E91E63";       // 止损线颜色
            sd.ColorDic["main-TakeProfit"] = "#8BC34A";     // 止盈线颜色

            sd.MidValDic["macd"] = 0;
            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;

            return sd;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            if (!isFinal) return;

            var quotes = tu.QuoteList;
            if (quotes == null || quotes.Count == 0) return;

            // 获取参数
            int fastPeriod = Convert.ToInt32(ArgDic["fastPeriod"]);
            int slowPeriod = Convert.ToInt32(ArgDic["slowPeriod"]);
            int signalPeriod = Convert.ToInt32(ArgDic["signalPeriod"]);
            int minBarCount = Convert.ToInt32(ArgDic["minBarCount"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            int macdMode = Convert.ToInt32(ArgDic["macdMode"]);
            int useZeroAxisFilter = Convert.ToInt32(ArgDic["useZeroAxisFilter"]);
            double zeroAxisTolerance = Convert.ToDouble(ArgDic["zeroAxisTolerance"]);
            int useHistogramConfirm = Convert.ToInt32(ArgDic["useHistogramConfirm"]);
            int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
            decimal stopLossPercent = Convert.ToDecimal(ArgDic["stopLossPercent"]);
            int useTakeProfit = Convert.ToInt32(ArgDic["useTakeProfit"]);
            decimal takeProfitPercent = Convert.ToDecimal(ArgDic["takeProfitPercent"]);
            int useMacdExit = Convert.ToInt32(ArgDic["useMacdExit"]);
            double minBodyRatio = Convert.ToDouble(ArgDic["minBodyRatio"]);
            double maxShadowRatio = Convert.ToDouble(ArgDic["maxShadowRatio"]);
            double minBodySize = Convert.ToDouble(ArgDic["minBodySize"]);

            // 检查K线数量
            if (quotes.Count < minBarCount) return;

            // 获取或创建状态
            string stateKey = tu.GetStateKey();
            if (!_stateDic.TryGetValue(stateKey, out var state))
            {
                state = new State();
                _stateDic[stateKey] = state;
            }

            // 计算MACD
            var macdResults = quotes.GetMacd(fastPeriod, slowPeriod, signalPeriod).ToList();
            if (macdResults.Count == 0) return;

            var lastIndex = macdResults.Count - 1;
            var currMacd = macdResults[lastIndex];
            var prevMacdResult = lastIndex > 0 ? macdResults[lastIndex - 1] : null;

            // 绘制MACD指标
            if (currMacd.Macd.HasValue)
                Plot("macd", "MACD", PlotType.LINE, currMacd.Macd);
            if (currMacd.Signal.HasValue)
                Plot("macd", "Signal", PlotType.LINE, currMacd.Signal);
            if (currMacd.Histogram.HasValue)
                Plot("macd", "Histogram", PlotType.RECTANGLE, currMacd.Histogram);

            // 绘制止损止盈线
            if (state.Position != 0)
            {
                if (useStopLoss == 1)
                    Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                if (useTakeProfit == 1)
                    Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfit);
            }

            // 检查MACD数据有效性
            if (!currMacd.Macd.HasValue || !currMacd.Signal.HasValue)
                return;

            decimal currentMACD = (decimal)currMacd.Macd.Value;
            decimal currentSignal = (decimal)currMacd.Signal.Value;
            decimal currentHistogram = currMacd.Histogram.HasValue ? (decimal)currMacd.Histogram.Value : 0;

            // 获取上一根K线的MACD数据
            decimal? prevMACD = state.PrevMACD;
            decimal? prevSignal = state.PrevSignal;

            // 更新状态中的前值
            state.PrevMACD = currentMACD;
            state.PrevSignal = currentSignal;

            // 如果没有前值，等待下一根K线
            if (!prevMACD.HasValue || !prevSignal.HasValue)
                return;

            // 当前价格
            decimal currentPrice = tq.Close;

            // 检测红三兵和绿三兵形态
            bool threeRedSoldiers = DetectThreeRedSoldiers(quotes, minBodyRatio, maxShadowRatio, minBodySize);
            bool threeBlackCrows = DetectThreeBlackCrows(quotes, minBodyRatio, maxShadowRatio, minBodySize);

            // MACD信号判断
            bool macdBullish = false;
            bool macdBearish = false;
            bool macdGoldenCross = prevMACD.Value <= prevSignal.Value && currentMACD > currentSignal;
            bool macdDeathCross = prevMACD.Value >= prevSignal.Value && currentMACD < currentSignal;

            switch (macdMode)
            {
                case 0: // 金叉死叉模式
                    macdBullish = macdGoldenCross;
                    macdBearish = macdDeathCross;
                    break;
                case 1: // DIF与DEA位置关系
                    macdBullish = currentMACD > currentSignal;
                    macdBearish = currentMACD < currentSignal;
                    break;
                case 2: // 柱状图方向
                    if (prevMacdResult != null && prevMacdResult.Histogram.HasValue)
                    {
                        decimal prevHistogram = (decimal)prevMacdResult.Histogram.Value;
                        macdBullish = currentHistogram > prevHistogram && currentHistogram > 0;
                        macdBearish = currentHistogram < prevHistogram && currentHistogram < 0;
                    }
                    break;
                case 3: // 综合模式：DIF>DEA 或 金叉
                    macdBullish = (currentMACD > currentSignal) || macdGoldenCross;
                    macdBearish = (currentMACD < currentSignal) || macdDeathCross;
                    break;
            }

            // 零轴过滤
            if (useZeroAxisFilter == 1)
            {
                if (macdBullish && (double)currentMACD < -zeroAxisTolerance)
                    macdBullish = false;
                if (macdBearish && (double)currentMACD > zeroAxisTolerance)
                    macdBearish = false;
            }

            // 柱状图确认
            if (useHistogramConfirm == 1 && prevMacdResult != null && prevMacdResult.Histogram.HasValue)
            {
                decimal prevHistogram = (decimal)prevMacdResult.Histogram.Value;
                if (macdBullish && currentHistogram <= prevHistogram)
                    macdBullish = false;
                if (macdBearish && currentHistogram >= prevHistogram)
                    macdBearish = false;
            }

            // 计算交易手数
            decimal lots = CalculateLots(currentPrice);

            // 止损止盈检查
            if (state.Position != 0)
            {
                bool exitByStopLoss = false;
                bool exitByTakeProfit = false;
                bool exitByMacd = false;

                // 止损检查
                if (useStopLoss == 1)
                {
                    if (state.Position > 0 && currentPrice <= state.StopLoss)
                        exitByStopLoss = true;
                    if (state.Position < 0 && currentPrice >= state.StopLoss)
                        exitByStopLoss = true;
                }

                // 止盈检查
                if (useTakeProfit == 1)
                {
                    if (state.Position > 0 && currentPrice >= state.TakeProfit)
                        exitByTakeProfit = true;
                    if (state.Position < 0 && currentPrice <= state.TakeProfit)
                        exitByTakeProfit = true;
                }

                // MACD反向出场
                if (useMacdExit == 1)
                {
                    if (state.Position > 0 && macdDeathCross)
                        exitByMacd = true;
                    if (state.Position < 0 && macdGoldenCross)
                        exitByMacd = true;
                }

                // 执行平仓
                if (exitByStopLoss || exitByTakeProfit || exitByMacd)
                {
                    if (state.Position > 0)
                    {
                        Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                    }
                    else
                    {
                        Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                    }
                    state.Position = 0;
                    state.Num = 0;
                    state.EntryPrice = 0;
                    state.StopLoss = 0;
                    state.TakeProfit = 0;
                    return;
                }
            }

            // 入场逻辑
            // 做多条件：红三兵 + MACD多头信号
            if (threeRedSoldiers && macdBullish && mode != 2)
            {
                // 如果有空仓，先平仓
                if (state.Position < 0)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Position = 0;
                    state.Num = 0;
                }

                // 开多仓
                if (state.Position == 0)
                {
                    Trade(tu.MktSymbol, OrderType.BUY, currentPrice, lots, period, sendMode);
                    state.Position = 1;
                    state.Num = lots;
                    state.EntryPrice = currentPrice;
                    state.StopLoss = currentPrice * (1 - stopLossPercent / 100);
                    state.TakeProfit = currentPrice * (1 + takeProfitPercent / 100);
                }
            }
            // 做空条件：绿三兵 + MACD空头信号
            else if (threeBlackCrows && macdBearish && mode != 1)
            {
                // 如果有多仓，先平仓
                if (state.Position > 0)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Position = 0;
                    state.Num = 0;
                }

                // 开空仓（双向模式）
                if (state.Position == 0 && mode == 0)
                {
                    Trade(tu.MktSymbol, OrderType.SELL, currentPrice, lots, period, sendMode);
                    state.Position = -1;
                    state.Num = lots;
                    state.EntryPrice = currentPrice;
                    state.StopLoss = currentPrice * (1 + stopLossPercent / 100);
                    state.TakeProfit = currentPrice * (1 - takeProfitPercent / 100);
                }
            }
        }

        /// <summary>
        /// 检测红三兵形态
        /// </summary>
        /// <param name="quotes">K线数据</param>
        /// <param name="minBodyRatio">最小实体比例</param>
        /// <param name="maxShadowRatio">最大影线比例</param>
        /// <param name="minBodySize">最小实体大小百分比</param>
        /// <returns>是否出现红三兵形态</returns>
        private bool DetectThreeRedSoldiers(List<SkQuote> quotes, double minBodyRatio, double maxShadowRatio, double minBodySize)
        {
            if (quotes.Count < 3) return false;

            var bar1 = quotes[quotes.Count - 3]; // 第一根
            var bar2 = quotes[quotes.Count - 2]; // 第二根
            var bar3 = quotes[quotes.Count - 1]; // 第三根（当前）

            // 检查是否都是阳线
            if (bar1.Close <= bar1.Open) return false;
            if (bar2.Close <= bar2.Open) return false;
            if (bar3.Close <= bar3.Open) return false;

            // 检查收盘价递增
            if (bar2.Close <= bar1.Close) return false;
            if (bar3.Close <= bar2.Close) return false;

            // 检查开盘价在前一根实体范围内（允许一定容差）
            if (bar2.Open < bar1.Open || bar2.Open > bar1.Close) return false;
            if (bar3.Open < bar2.Open || bar3.Open > bar2.Close) return false;

            // 检查实体比例和影线
            if (!IsValidBullishBar(bar1, minBodyRatio, maxShadowRatio, minBodySize)) return false;
            if (!IsValidBullishBar(bar2, minBodyRatio, maxShadowRatio, minBodySize)) return false;
            if (!IsValidBullishBar(bar3, minBodyRatio, maxShadowRatio, minBodySize)) return false;

            return true;
        }

        /// <summary>
        /// 检测绿三兵（三只乌鸦）形态
        /// </summary>
        /// <param name="quotes">K线数据</param>
        /// <param name="minBodyRatio">最小实体比例</param>
        /// <param name="maxShadowRatio">最大影线比例</param>
        /// <param name="minBodySize">最小实体大小百分比</param>
        /// <returns>是否出现绿三兵形态</returns>
        private bool DetectThreeBlackCrows(List<SkQuote> quotes, double minBodyRatio, double maxShadowRatio, double minBodySize)
        {
            if (quotes.Count < 3) return false;

            var bar1 = quotes[quotes.Count - 3]; // 第一根
            var bar2 = quotes[quotes.Count - 2]; // 第二根
            var bar3 = quotes[quotes.Count - 1]; // 第三根（当前）

            // 检查是否都是阴线
            if (bar1.Close >= bar1.Open) return false;
            if (bar2.Close >= bar2.Open) return false;
            if (bar3.Close >= bar3.Open) return false;

            // 检查收盘价递减
            if (bar2.Close >= bar1.Close) return false;
            if (bar3.Close >= bar2.Close) return false;

            // 检查开盘价在前一根实体范围内（允许一定容差）
            if (bar2.Open > bar1.Open || bar2.Open < bar1.Close) return false;
            if (bar3.Open > bar2.Open || bar3.Open < bar2.Close) return false;

            // 检查实体比例和影线
            if (!IsValidBearishBar(bar1, minBodyRatio, maxShadowRatio, minBodySize)) return false;
            if (!IsValidBearishBar(bar2, minBodyRatio, maxShadowRatio, minBodySize)) return false;
            if (!IsValidBearishBar(bar3, minBodyRatio, maxShadowRatio, minBodySize)) return false;

            return true;
        }

        /// <summary>
        /// 检查阳线是否有效（实体饱满、影线不过长）
        /// </summary>
        private bool IsValidBullishBar(SkQuote bar, double minBodyRatio, double maxShadowRatio, double minBodySize)
        {
            decimal body = bar.Close - bar.Open;
            decimal totalRange = bar.High - bar.Low;
            decimal upperShadow = bar.High - bar.Close;
            decimal lowerShadow = bar.Open - bar.Low;

            if (totalRange <= 0) return false;
            if (body <= 0) return false;

            // 实体比例检查
            double bodyRatio = (double)(body / totalRange);
            if (bodyRatio < minBodyRatio) return false;

            // 影线比例检查
            if (body > 0)
            {
                double upperShadowRatio = (double)(upperShadow / body);
                double lowerShadowRatio = (double)(lowerShadow / body);
                if (upperShadowRatio > maxShadowRatio) return false;
                if (lowerShadowRatio > maxShadowRatio) return false;
            }

            // 最小实体大小检查
            double bodySizePercent = (double)(body / bar.Close * 100);
            if (bodySizePercent < minBodySize) return false;

            return true;
        }

        /// <summary>
        /// 检查阴线是否有效（实体饱满、影线不过长）
        /// </summary>
        private bool IsValidBearishBar(SkQuote bar, double minBodyRatio, double maxShadowRatio, double minBodySize)
        {
            decimal body = bar.Open - bar.Close;
            decimal totalRange = bar.High - bar.Low;
            decimal upperShadow = bar.High - bar.Open;
            decimal lowerShadow = bar.Close - bar.Low;

            if (totalRange <= 0) return false;
            if (body <= 0) return false;

            // 实体比例检查
            double bodyRatio = (double)(body / totalRange);
            if (bodyRatio < minBodyRatio) return false;

            // 影线比例检查
            if (body > 0)
            {
                double upperShadowRatio = (double)(upperShadow / body);
                double lowerShadowRatio = (double)(lowerShadow / body);
                if (upperShadowRatio > maxShadowRatio) return false;
                if (lowerShadowRatio > maxShadowRatio) return false;
            }

            // 最小实体大小检查
            double bodySizePercent = (double)(body / bar.Open * 100);
            if (bodySizePercent < minBodySize) return false;

            return true;
        }

        /// <summary>
        /// 计算交易手数
        /// </summary>
        private decimal CalculateLots(decimal price)
        {
            int lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 0)
            {
                return Convert.ToDecimal(ArgDic["lots"]);
            }
            else
            {
                decimal money = Convert.ToDecimal(ArgDic["money"]);
                if (price > 0)
                    return Math.Floor(money / price * 100) / 100;
                return 1;
            }
        }
    }
}
