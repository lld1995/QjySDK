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
    /// 标准RSI交易策略
    /// 策略逻辑：
    /// - RSI从超卖区回升时做多
    /// - RSI从超买区回落时做空
    /// - 支持多种信号模式：超买超卖反转、中轴穿越、双RSI交叉
    /// </summary>
    public class RSI : StgBase
    {
        public RSI()
        {
        }

        public RSI(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // RSI参数
            sd.ArgDic["rsiPeriod"] = 14;             // RSI计算周期
            sd.ArgDic["rsiPeriod2"] = 7;             // 短周期RSI(双RSI模式使用)
            sd.ArgDic["overbought"] = 70;            // 超买阈值
            sd.ArgDic["oversold"] = 30;              // 超卖阈值
            sd.ArgDic["midLine"] = 50;               // 中轴线
            sd.ArgDic["mode"] = 0;                   // 交易模式: 0双向 1仅多 2仅空
            sd.ArgDic["sendMode"] = 0;               // 发单模式: 0立即 1下个开盘
            sd.ArgDic["signalMode"] = 0;             // 信号模式
            sd.ArgDic["stopLoss"] = 5.0m;             // 止损百分比

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;               // 0固定手数 1固定金额
            sd.ArgDic["lots"] = 1.0m;                // 固定手数
            sd.ArgDic["money"] = 10000m;            // 固定金额

            // 参数说明
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc() { Text = "RSI周期", Explain = "RSI计算周期，通常为14" };
            sd.ArgDescDic["rsiPeriod2"] = new ArgDesc() { Text = "RSI短周期", Explain = "双RSI模式的短周期，通常为7" };
            sd.ArgDescDic["overbought"] = new ArgDesc() { Text = "超买线", Explain = "超买区域阈值，通常为70" };
            sd.ArgDescDic["oversold"] = new ArgDesc() { Text = "超卖线", Explain = "超卖区域阈值，通常为30" };
            sd.ArgDescDic["midLine"] = new ArgDesc() { Text = "中轴线", Explain = "RSI中轴线，通常为50" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0 双向交易 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即发单 1 下个开盘发单" };
            sd.ArgDescDic["signalMode"] = new ArgDesc() { Text = "信号模式", Explain = "0 超买超卖反转 1 中轴穿越 2 双RSI交叉" };
            sd.ArgDescDic["stopLoss"] = new ArgDesc() { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数数量" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额数量" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;

            // RSI颜色配置
            sd.ColorDic["sub0-RSI"] = "#F6465D";
            sd.ColorDic["sub0-RSI2"] = "#0ECB81";

            // 中值线配置
            sd.MidValDic["sub0"] = 50;

            return sd;
        }

        private class State
        {
            public int Status { get; set; }     // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }    // 持仓数量
            public decimal EntryPrice { get; set; } // 入场价格
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            int rsiPeriod = (int)ArgDic["rsiPeriod"];
            int rsiPeriod2 = (int)ArgDic["rsiPeriod2"];
            int signalMode = (int)ArgDic["signalMode"];

            // 确保有足够的数据
            int minBars = signalMode == 2 ? Math.Max(rsiPeriod, rsiPeriod2) + 2 : rsiPeriod + 2;
            if (tu.QuoteList.Count < minBars) return;

            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int overbought = (int)ArgDic["overbought"];
            int oversold = (int)ArgDic["oversold"];
            int midLine = (int)ArgDic["midLine"];

            var q = tu.QuoteList.Last();

            // 计算RSI指标
            var rsiList = tu.QuoteList.GetRsi(rsiPeriod).ToList();
            var rsi1 = rsiList[rsiList.Count - 1];
            var rsi2 = rsiList[rsiList.Count - 2];

            if (rsi1.Rsi == null || rsi2.Rsi == null) return;

            double curRsi = rsi1.Rsi.Value;
            double prevRsi = rsi2.Rsi.Value;

            // 绘制主RSI
            Plot("sub0", "RSI", PlotType.LINE, curRsi);

            // 双RSI模式需要计算短周期RSI
            double curRsi2 = 0;
            double prevRsi2 = 0;
            if (signalMode == 2)
            {
                var rsiList2 = tu.QuoteList.GetRsi(rsiPeriod2).ToList();
                var rsi2_1 = rsiList2[rsiList2.Count - 1];
                var rsi2_2 = rsiList2[rsiList2.Count - 2];

                if (rsi2_1.Rsi == null || rsi2_2.Rsi == null) return;

                curRsi2 = rsi2_1.Rsi.Value;
                prevRsi2 = rsi2_2.Rsi.Value;

                // 绘制短周期RSI
                Plot("sub0", "RSI2", PlotType.LINE, curRsi2);
            }

            // 计算手数
            var num = (decimal)ArgDic["lots"];
            var lotsMode = (int)ArgDic["lotsMode"];
            if (lotsMode == 1)
            {
                var sym = GetSymbol(tu.MktSymbol);
                num = ((decimal)ArgDic["money"] / (q.Close * sym.multiplier * sym.margin_ratio));
                if (sym.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }

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
                _stateDic[sk] = s;
            }

            // 信号判断
            bool buySignal = false;
            bool sellSignal = false;
            bool exitLongSignal = false;
            bool exitShortSignal = false;

            switch (signalMode)
            {
                case 0:
                    // 模式0：超买超卖反转
                    // RSI从超卖区向上突破做多
                    buySignal = prevRsi < oversold && curRsi >= oversold;
                    // RSI从超买区向下突破做空
                    sellSignal = prevRsi > overbought && curRsi <= overbought;
                    // 多头平仓：RSI进入超买区
                    exitLongSignal = curRsi >= overbought;
                    // 空头平仓：RSI进入超卖区
                    exitShortSignal = curRsi <= oversold;
                    break;

                case 1:
                    // 模式1：中轴穿越
                    // RSI上穿中轴做多
                    buySignal = prevRsi < midLine && curRsi >= midLine;
                    // RSI下穿中轴做空
                    sellSignal = prevRsi > midLine && curRsi <= midLine;
                    // 多头平仓：RSI下穿中轴
                    exitLongSignal = prevRsi > midLine && curRsi <= midLine;
                    // 空头平仓：RSI上穿中轴
                    exitShortSignal = prevRsi < midLine && curRsi >= midLine;
                    break;

                case 2:
                    // 模式2：双RSI交叉
                    // 短周期RSI上穿长周期RSI做多
                    buySignal = prevRsi2 <= prevRsi && curRsi2 > curRsi;
                    // 短周期RSI下穿长周期RSI做空
                    sellSignal = prevRsi2 >= prevRsi && curRsi2 < curRsi;
                    // 多头平仓：短周期RSI下穿长周期RSI
                    exitLongSignal = prevRsi2 >= prevRsi && curRsi2 < curRsi;
                    // 空头平仓：短周期RSI上穿长周期RSI
                    exitShortSignal = prevRsi2 <= prevRsi && curRsi2 > curRsi;
                    break;
            }

            // 交易逻辑
            if (s.Status == 0)
            {
                // 空仓状态：寻找入场信号
                if (buySignal && mode != 2)
                {
                    s.Status = 1;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                }
                else if (sellSignal && mode != 1)
                {
                    s.Status = 2;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                }
            }
            else if (s.Status == 1)
            {
                // 止损检查
                var sl = (decimal)ArgDic["stopLoss"];
                if (sl > 0 && s.EntryPrice > 0 && q.Close < s.EntryPrice * (1 - sl / 100m))
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                    return;
                }

                // 多头持仓：检查平仓信号
                if (exitLongSignal)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做空
                    if (sellSignal && mode != 1)
                    {
                        s.Status = 2;
                        s.Num = num;
                        s.EntryPrice = q.Close;
                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                    }
                    else
                    {
                        s.Status = 0;
                        s.Num = 0;
                        s.EntryPrice = 0;
                    }
                }
            }
            else if (s.Status == 2)
            {
                // 止损检查
                var sl2 = (decimal)ArgDic["stopLoss"];
                if (sl2 > 0 && s.EntryPrice > 0 && q.Close > s.EntryPrice * (1 + sl2 / 100m))
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                    return;
                }

                // 空头持仓：检查平仓信号
                if (exitShortSignal)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做多
                    if (buySignal && mode != 2)
                    {
                        s.Status = 1;
                        s.Num = num;
                        s.EntryPrice = q.Close;
                        Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                    }
                    else
                    {
                        s.Status = 0;
                        s.Num = 0;
                        s.EntryPrice = 0;
                    }
                }
            }
        }
    }
}
