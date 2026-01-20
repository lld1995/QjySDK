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
    /// <summary>
    /// 三因子共振交易系统 (MA趋势 + MACD动量 + OBV成交量)
    /// 
    /// 策略核心逻辑：
    /// 1. MA趋势因子：价格站上MA且MA向上为多头趋势，反之为空头趋势
    /// 2. MACD动量因子：DIF上穿DEA为多头动量，DIF下穿DEA为空头动量
    /// 3. OBV成交量因子：OBV上穿其均线为量能支撑多头，反之支撑空头
    /// 
    /// 入场条件：三因子同向共振
    /// - 做多：价格>MA且MA上行 + MACD金叉/DIF>DEA + OBV>OBV_MA
    /// - 做空：价格<MA且MA下行 + MACD死叉/DIF<DEA + OBV<OBV_MA
    /// 
    /// 出场条件：可配置多种模式
    /// </summary>
    public class ThreeFactorResonance : StgBase
    {
        public ThreeFactorResonance()
        {
        }

        public ThreeFactorResonance(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // MA趋势参数
            sd.ArgDic["maPeriod"] = 20;              // MA周期
            sd.ArgDic["maType"] = 0;                 // MA类型 0:SMA 1:EMA
            sd.ArgDic["maSlopePeriod"] = 3;          // MA斜率计算周期

            // MACD动量参数
            sd.ArgDic["macdFast"] = 12;              // MACD快线周期
            sd.ArgDic["macdSlow"] = 26;              // MACD慢线周期
            sd.ArgDic["macdSignal"] = 9;             // MACD信号线周期
            sd.ArgDic["macdMode"] = 0;               // MACD判断模式 0:金叉死叉 1:DIF与DEA位置关系 2:柱状图方向

            // OBV成交量参数
            sd.ArgDic["obvMaPeriod"] = 20;           // OBV均线周期
            sd.ArgDic["obvMode"] = 0;                // OBV判断模式 0:OBV与均线位置 1:OBV金叉死叉

            // 共振参数
            sd.ArgDic["resonanceMode"] = 0;          // 共振模式 0:三因子同时满足 1:至少两因子满足
            sd.ArgDic["resonanceWindow"] = 3;        // 共振窗口期(K线数)

            // 交易参数
            sd.ArgDic["mode"] = 0;                   // 交易模式 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;               // 发单模式
            sd.ArgDic["exitMode"] = 0;               // 出场模式 0:反向共振 1:任一因子反转 2:MA反转 3:MACD反转

            // 手数控制
            sd.ArgDic["lotsMode"] = 0;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 100000m;

            // 参数说明
            sd.ArgDescDic["maPeriod"] = new ArgDesc() { Text = "MA周期", Explain = "趋势判断的均线周期" };
            sd.ArgDescDic["maType"] = new ArgDesc() { Text = "MA类型", Explain = "0 简单移动平均SMA 1 指数移动平均EMA" };
            sd.ArgDescDic["maSlopePeriod"] = new ArgDesc() { Text = "MA斜率周期", Explain = "计算MA方向的回溯周期" };
            sd.ArgDescDic["macdFast"] = new ArgDesc() { Text = "MACD快线", Explain = "MACD快线周期，通常为12" };
            sd.ArgDescDic["macdSlow"] = new ArgDesc() { Text = "MACD慢线", Explain = "MACD慢线周期，通常为26" };
            sd.ArgDescDic["macdSignal"] = new ArgDesc() { Text = "MACD信号线", Explain = "MACD信号线周期，通常为9" };
            sd.ArgDescDic["macdMode"] = new ArgDesc() { Text = "MACD模式", Explain = "0 金叉死叉 1 DIF与DEA位置 2 柱状图方向" };
            sd.ArgDescDic["obvMaPeriod"] = new ArgDesc() { Text = "OBV均线周期", Explain = "OBV的移动平均周期" };
            sd.ArgDescDic["obvMode"] = new ArgDesc() { Text = "OBV模式", Explain = "0 OBV与均线位置 1 OBV金叉死叉" };
            sd.ArgDescDic["resonanceMode"] = new ArgDesc() { Text = "共振模式", Explain = "0 三因子同时满足 1 至少两因子满足" };
            sd.ArgDescDic["resonanceWindow"] = new ArgDesc() { Text = "共振窗口", Explain = "信号在N根K线内出现视为共振" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0 双向交易 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
            sd.ArgDescDic["exitMode"] = new ArgDesc() { Text = "出场模式", Explain = "0 反向共振 1 任一因子反转 2 MA反转 3 MACD反转" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // MA颜色配置 (主图)
            sd.ColorDic["main-MA"] = "#FF9800";

            // MACD颜色配置 (副图0)
            sd.ColorDic["sub0-Histogram"] = "#F6465D;#0ECB81";
            sd.ColorDic["sub0-DIF"] = "#2196F3";
            sd.ColorDic["sub0-DEA"] = "#FF9800";

            // OBV颜色配置 (副图1)
            sd.ColorDic["sub1-OBV"] = "#2196F3";
            sd.ColorDic["sub1-OBV_MA"] = "#FF9800";

            // 中值线配置
            sd.MidValDic["sub0"] = 0;

            return sd;
        }

        private class State
        {
            public int Status { get; set; }              // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }             // 持仓数量
            public int CurrentBar { get; set; }          // 当前K线计数

            // 各因子信号记录
            public int MaBullBar { get; set; }           // MA多头信号K线
            public int MaBearBar { get; set; }           // MA空头信号K线
            public int MacdBullBar { get; set; }         // MACD多头信号K线
            public int MacdBearBar { get; set; }         // MACD空头信号K线
            public int ObvBullBar { get; set; }          // OBV多头信号K线
            public int ObvBearBar { get; set; }          // OBV空头信号K线

            // 当前因子状态
            public bool MaBullish { get; set; }          // MA当前是否多头
            public bool MacdBullish { get; set; }        // MACD当前是否多头
            public bool ObvBullish { get; set; }         // OBV当前是否多头
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int maPeriod = (int)ArgDic["maPeriod"];
            int maType = (int)ArgDic["maType"];
            int maSlopePeriod = (int)ArgDic["maSlopePeriod"];
            int macdFast = (int)ArgDic["macdFast"];
            int macdSlow = (int)ArgDic["macdSlow"];
            int macdSignal = (int)ArgDic["macdSignal"];
            int macdMode = (int)ArgDic["macdMode"];
            int obvMaPeriod = (int)ArgDic["obvMaPeriod"];
            int obvMode = (int)ArgDic["obvMode"];
            int resonanceMode = (int)ArgDic["resonanceMode"];
            int resonanceWindow = (int)ArgDic["resonanceWindow"];
            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int exitMode = (int)ArgDic["exitMode"];

            // 计算最小K线数
            int minBars = Math.Max(Math.Max(maPeriod + maSlopePeriod, macdSlow + macdSignal), obvMaPeriod) + 5;
            if (tu.QuoteList.Count < minBars) return;

            var q = tu.QuoteList.Last();

            // ==================== 计算MA趋势因子 ====================
            double ma1, ma2, maPrev;
            if (maType == 1)
            {
                var emaList = tu.QuoteList.GetEma(maPeriod).ToList();
                ma1 = emaList[emaList.Count - 1].Ema.GetValueOrDefault(0);
                ma2 = emaList[emaList.Count - 2].Ema.GetValueOrDefault(0);
                maPrev = emaList[emaList.Count - 1 - maSlopePeriod].Ema.GetValueOrDefault(0);
            }
            else
            {
                var smaList = tu.QuoteList.GetSma(maPeriod).ToList();
                ma1 = smaList[smaList.Count - 1].Sma.GetValueOrDefault(0);
                ma2 = smaList[smaList.Count - 2].Sma.GetValueOrDefault(0);
                maPrev = smaList[smaList.Count - 1 - maSlopePeriod].Sma.GetValueOrDefault(0);
            }

            if (ma1 == 0) return;

            // MA趋势判断：价格在MA上方且MA向上 = 多头
            bool priceAboveMa = (double)q.Close > ma1;
            bool maRising = ma1 > maPrev;
            bool maBullish = priceAboveMa && maRising;
            bool maBearish = !priceAboveMa && !maRising;

            // 绘制MA
            Plot("main", "MA", PlotType.LINE, ma1);

            // ==================== 计算MACD动量因子 ====================
            var macdList = tu.QuoteList.GetMacd(macdFast, macdSlow, macdSignal).ToList();
            var macd1 = macdList[macdList.Count - 1];
            var macd2 = macdList[macdList.Count - 2];

            if (macd1.Macd == null || macd1.Signal == null) return;
            if (macd2.Macd == null || macd2.Signal == null) return;

            double dif1 = macd1.Macd.Value;
            double dea1 = macd1.Signal.Value;
            double histogram1 = macd1.Histogram.GetValueOrDefault(0);
            double dif2 = macd2.Macd.Value;
            double dea2 = macd2.Signal.Value;
            double histogram2 = macd2.Histogram.GetValueOrDefault(0);

            // MACD信号判断
            bool macdGoldenCross = dif2 <= dea2 && dif1 > dea1;
            bool macdDeathCross = dif2 >= dea2 && dif1 < dea1;
            bool macdBullish = false;
            bool macdBearish = false;

            switch (macdMode)
            {
                case 0: // 金叉死叉模式
                    macdBullish = macdGoldenCross;
                    macdBearish = macdDeathCross;
                    break;
                case 1: // DIF与DEA位置关系
                    macdBullish = dif1 > dea1;
                    macdBearish = dif1 < dea1;
                    break;
                case 2: // 柱状图方向
                    macdBullish = histogram1 > histogram2 && histogram1 > 0;
                    macdBearish = histogram1 < histogram2 && histogram1 < 0;
                    break;
            }

            // 绘制MACD
            Plot("sub0", "Histogram", PlotType.RECTANGLE, histogram1);
            Plot("sub0", "DIF", PlotType.LINE, dif1);
            Plot("sub0", "DEA", PlotType.LINE, dea1);

            // ==================== 计算OBV成交量因子 ====================
            var obvList = tu.QuoteList.GetObv().ToList();
            if (obvList.Count < obvMaPeriod + 2) return;

            double obv1 = obvList[obvList.Count - 1].Obv;
            double obv2 = obvList[obvList.Count - 2].Obv;

            // 计算OBV的移动平均
            double obvMaSum = 0;
            for (int i = 0; i < obvMaPeriod; i++)
            {
                obvMaSum += obvList[obvList.Count - 1 - i].Obv;
            }
            double obvMa1 = obvMaSum / obvMaPeriod;

            double obvMaSumPrev = 0;
            for (int i = 0; i < obvMaPeriod; i++)
            {
                obvMaSumPrev += obvList[obvList.Count - 2 - i].Obv;
            }
            double obvMa2 = obvMaSumPrev / obvMaPeriod;

            // OBV信号判断
            bool obvGoldenCross = obv2 <= obvMa2 && obv1 > obvMa1;
            bool obvDeathCross = obv2 >= obvMa2 && obv1 < obvMa1;
            bool obvBullish = false;
            bool obvBearish = false;

            switch (obvMode)
            {
                case 0: // OBV与均线位置
                    obvBullish = obv1 > obvMa1;
                    obvBearish = obv1 < obvMa1;
                    break;
                case 1: // OBV金叉死叉
                    obvBullish = obvGoldenCross;
                    obvBearish = obvDeathCross;
                    break;
            }

            // 绘制OBV
            Plot("sub1", "OBV", PlotType.LINE, obv1);
            Plot("sub1", "OBV_MA", PlotType.LINE, obvMa1);

            // ==================== 计算手数 ====================
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

            // ==================== 获取或创建状态 ====================
            var sk = tu.GetStateKey();
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new State()
                {
                    MaBullBar = -999,
                    MaBearBar = -999,
                    MacdBullBar = -999,
                    MacdBearBar = -999,
                    ObvBullBar = -999,
                    ObvBearBar = -999
                };
            }
            var s = _stateDic[sk];

            s.CurrentBar++;

            // 更新因子状态
            s.MaBullish = maBullish;
            s.MacdBullish = macdBullish || (macdMode != 0 && dif1 > dea1);
            s.ObvBullish = obvBullish || (obvMode != 1 && obv1 > obvMa1);

            // 记录信号发生的K线位置
            if (maBullish && !maBearish) s.MaBullBar = s.CurrentBar;
            if (maBearish && !maBullish) s.MaBearBar = s.CurrentBar;
            if (macdBullish) s.MacdBullBar = s.CurrentBar;
            if (macdBearish) s.MacdBearBar = s.CurrentBar;
            if (obvBullish) s.ObvBullBar = s.CurrentBar;
            if (obvBearish) s.ObvBearBar = s.CurrentBar;

            // ==================== 判断共振信号 ====================
            bool buyResonance = false;
            bool sellResonance = false;

            // 计算各因子信号年龄
            int maBullAge = s.CurrentBar - s.MaBullBar;
            int maBearAge = s.CurrentBar - s.MaBearBar;
            int macdBullAge = s.CurrentBar - s.MacdBullBar;
            int macdBearAge = s.CurrentBar - s.MacdBearBar;
            int obvBullAge = s.CurrentBar - s.ObvBullBar;
            int obvBearAge = s.CurrentBar - s.ObvBearBar;

            // 判断因子是否在窗口期内有效
            bool maBullValid = maBullAge >= 0 && maBullAge <= resonanceWindow;
            bool maBearValid = maBearAge >= 0 && maBearAge <= resonanceWindow;
            bool macdBullValid = macdBullAge >= 0 && macdBullAge <= resonanceWindow;
            bool macdBearValid = macdBearAge >= 0 && macdBearAge <= resonanceWindow;
            bool obvBullValid = obvBullAge >= 0 && obvBullAge <= resonanceWindow;
            bool obvBearValid = obvBearAge >= 0 && obvBearAge <= resonanceWindow;

            // 对于位置关系模式，使用当前状态而非信号
            if (macdMode == 1) macdBullValid = dif1 > dea1;
            if (macdMode == 1) macdBearValid = dif1 < dea1;
            if (obvMode == 0) obvBullValid = obv1 > obvMa1;
            if (obvMode == 0) obvBearValid = obv1 < obvMa1;

            // MA始终使用当前状态
            maBullValid = maBullish;
            maBearValid = maBearish;

            if (resonanceMode == 0)
            {
                // 三因子同时满足
                buyResonance = maBullValid && macdBullValid && obvBullValid;
                sellResonance = maBearValid && macdBearValid && obvBearValid;
            }
            else
            {
                // 至少两因子满足
                int bullCount = (maBullValid ? 1 : 0) + (macdBullValid ? 1 : 0) + (obvBullValid ? 1 : 0);
                int bearCount = (maBearValid ? 1 : 0) + (macdBearValid ? 1 : 0) + (obvBearValid ? 1 : 0);
                buyResonance = bullCount >= 2;
                sellResonance = bearCount >= 2;
            }

            // ==================== 出场信号判断 ====================
            bool exitLongSignal = false;
            bool exitShortSignal = false;

            switch (exitMode)
            {
                case 0: // 反向共振出场
                    exitLongSignal = sellResonance;
                    exitShortSignal = buyResonance;
                    break;
                case 1: // 任一因子反转出场
                    exitLongSignal = maBearish || macdDeathCross || obvDeathCross;
                    exitShortSignal = maBullish || macdGoldenCross || obvGoldenCross;
                    break;
                case 2: // MA反转出场
                    exitLongSignal = maBearish;
                    exitShortSignal = maBullish;
                    break;
                case 3: // MACD反转出场
                    exitLongSignal = macdDeathCross || (macdMode == 1 && dif1 < dea1);
                    exitShortSignal = macdGoldenCross || (macdMode == 1 && dif1 > dea1);
                    break;
            }

            // ==================== 交易逻辑 ====================
            if (s.Status == 0)
            {
                // 空仓状态：寻找共振入场信号
                if (buyResonance && mode != 2)
                {
                    s.Status = 1;
                    s.Num = num;
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                    // 重置信号记录
                    ResetBullSignals(s);
                }
                else if (sellResonance && mode != 1)
                {
                    s.Status = 2;
                    s.Num = num;
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                    // 重置信号记录
                    ResetBearSignals(s);
                }
            }
            else if (s.Status == 1)
            {
                // 多头持仓：检查出场信号
                if (exitLongSignal)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做空
                    if (sellResonance && mode != 1)
                    {
                        s.Status = 2;
                        s.Num = num;
                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        ResetBearSignals(s);
                    }
                    else
                    {
                        s.Status = 0;
                        s.Num = 0;
                    }
                }
            }
            else if (s.Status == 2)
            {
                // 空头持仓：检查出场信号
                if (exitShortSignal)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做多
                    if (buyResonance && mode != 2)
                    {
                        s.Status = 1;
                        s.Num = num;
                        Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                        ResetBullSignals(s);
                    }
                    else
                    {
                        s.Status = 0;
                        s.Num = 0;
                    }
                }
            }
        }

        private void ResetBullSignals(State s)
        {
            s.MaBullBar = -999;
            s.MacdBullBar = -999;
            s.ObvBullBar = -999;
        }

        private void ResetBearSignals(State s)
        {
            s.MaBearBar = -999;
            s.MacdBearBar = -999;
            s.ObvBearBar = -999;
        }
    }
}
