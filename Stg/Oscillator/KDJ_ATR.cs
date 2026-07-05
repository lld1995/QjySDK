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
    public class KDJ_ATR : StgBase
    {
        public KDJ_ATR()
        {
        }

        public KDJ_ATR(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.ArgDic["lookbackPeriods"] = 14;
            sd.ArgDic["signalPeriods"] = 3;
            sd.ArgDic["smoothPeriods"] = 3;
            sd.ArgDic["overUp"] = 80;
            sd.ArgDic["overDown"] = 20;
            sd.ArgDic["atrLookbackPeriods"] = 20;
            sd.ArgDic["atrWinRate"] = 8m;
            sd.ArgDic["atrLossRate"] = 8m;

            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;

            //手数控制
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };


            sd.ArgDescDic["atrLookbackPeriods"] = new ArgDesc() { Text = "ATR回溯周期", Explain = "ATR计算的回溯周期", Type = "number" };


            sd.ArgDescDic["atrLossRate"] = new ArgDesc() { Text = "ATR止损倍数", Explain = "止损距离=ATR*此倍数", Type = "number" };


            sd.ArgDescDic["atrWinRate"] = new ArgDesc() { Text = "ATR止盈倍数", Explain = "止盈距离=ATR*此倍数", Type = "number" };


            sd.ArgDescDic["lookbackPeriods"] = new ArgDesc() { Text = "KDJ周期", Explain = "KDJ指标计算周期", Type = "number" };


            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };


            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };


            sd.ArgDescDic["overDown"] = new ArgDesc() { Text = "超卖线", Explain = "超卖区域阈值", Type = "number" };


            sd.ArgDescDic["overUp"] = new ArgDesc() { Text = "超买线", Explain = "超买区域阈值", Type = "number" };


            sd.ArgDescDic["signalPeriods"] = new ArgDesc() { Text = "信号线周期", Explain = "D线平滑周期", Type = "number" };


            sd.ArgDescDic["smoothPeriods"] = new ArgDesc() { Text = "平滑周期", Explain = "K线平滑周期", Type = "number" };
            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;
            sd.MidValDic["kdj"] = 50;
            sd.ColorDic["kdj-K"] = "#F6465D";
            sd.ColorDic["kdj-D"] = "#E0A166";
            sd.ColorDic["kdj-J"] = "#C562A6";
            return sd;
        }

        private class State
        {
            public int Status { get; set; }

            public decimal Num { get; set; }

            public decimal LastOpenPrice { get; set; }

            public int LastLowKIndex { get; set; } = -1;

            public int LastHighKIndex { get; set; } = -1;
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnSendOrder(TableUnit tu, decimal price)
        {
            base.OnSendOrder(tu, price);
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
            s.LastOpenPrice = price;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

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

            {
                if (tu.QuoteList.Count > 15)
                {
                    int mode = Convert.ToInt32(ArgDic["mode"]);
                    int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
                    var overUp = Convert.ToInt32(ArgDic["overUp"]);
                    var overDown = Convert.ToInt32(ArgDic["overDown"]);
                    var atrWinRate = Convert.ToDecimal(ArgDic["atrWinRate"]);
                    var atrLossRate = Convert.ToDecimal(ArgDic["atrLossRate"]);
                    var q = tu.QuoteList.Last();

                    var kdj = tu.QuoteList.GetStoch(Convert.ToInt32(ArgDic["lookbackPeriods"]), Convert.ToInt32(ArgDic["signalPeriods"]), Convert.ToInt32(ArgDic["smoothPeriods"])).ToList();

                    var kdj1 = kdj[kdj.Count - 1];
                    var kdj2 = kdj[kdj.Count - 2];
                    var kdj3 = kdj[kdj.Count - 3];

                    Plot("kdj", "K", PlotType.LINE, kdj1.K);
                    Plot("kdj", "D", PlotType.LINE, kdj1.D);
                    Plot("kdj", "J", PlotType.LINE, kdj1.J);

                    var num = Convert.ToDecimal(ArgDic["lots"]);
                    var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
                    if (lotsMode == 1)
                    {
                        var sym = GetSymbol(tu.MktSymbol);
                        num = (Convert.ToDecimal(ArgDic["money"]) / (q.Close * sym.multiplier * sym.margin_ratio));
                        if (sym.symbol_type == (int)SymbolType.COIN)
                        {
                            num = (int)(num * sym.scale) / (decimal)sym.scale;
                        }
                        else
                        {
                            num = (int)num;
                        }
                    }

                    AtrResult atr1 = null;
                    if (s.Status == 0)
                    {

                    }
                    else
                    {
                        var atr = tu.QuoteList.GetAtr(Convert.ToInt32(ArgDic["atrLookbackPeriods"])).ToList();
                        atr1 = atr[atr.Count - 1];
                    }

                    if (s.Status == 0)
                    {
                        if (kdj2.J < kdj1.J && kdj2.J < kdj3.J && kdj2.J < overDown-20)
                        {
                            s.LastLowKIndex = tu.QuoteList.Count - 1;
                        }
                        if (kdj2.J > kdj1.J && kdj2.J > kdj3.J && kdj2.J > overUp+20)
                        {
                            s.LastHighKIndex = tu.QuoteList.Count - 1;
                        }

                        if (kdj1.K < overDown && s.LastLowKIndex > s.LastHighKIndex && kdj2.D > kdj2.K && Math.Abs((decimal)(kdj1.K - kdj1.D)) < 1 && mode != 2)
                        {
                            var isLowest = true;
                            for (int i = tu.QuoteList.Count - 2; i >= tu.QuoteList.Count - 15 && i >= 0; i--)
                            {
                                var q2 = tu.QuoteList[i];
                                if (q.Low > q2.Low)
                                {
                                    isLowest = false;
                                    break;
                                }
                            }
                            if (isLowest)
                            {
                                s.Status = 1;
                                s.Num = num;
                                Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                            }
                        }
                        if (kdj1.K > overUp && s.LastHighKIndex > s.LastLowKIndex && kdj2.D < kdj2.K && Math.Abs((decimal)(kdj1.K - kdj1.D)) < 1 && mode != 1)
                        {
                            var isHighest = true;
                            for (int i = tu.QuoteList.Count - 2; i >= tu.QuoteList.Count - 15 && i >= 0; i--)
                            {
                                var q2 = tu.QuoteList[i];
                                if (q.High < q2.High)
                                {
                                    isHighest = false;
                                    break;
                                }
                            }
                            if (isHighest)
                            {
                                s.Status = 2;
                                s.Num = num;
                                Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                            }
                        }
                    }
                    else if (s.Status == 1)
                    {
                        int closeStatus = 0;
                        if (q.Close < s.LastOpenPrice - atrLossRate * (decimal)atr1.Atr)
                        {
                            closeStatus = 1;
                        }
                        if (q.Close > s.LastOpenPrice + atrWinRate * (decimal)atr1.Atr)
                        {
                            closeStatus = 1;
                        }
                        if (kdj1.J < overUp-20 && kdj1.K < kdj1.D && kdj2.K > kdj2.D && q.Close > s.LastOpenPrice + 2 * (decimal)atr1.Atr)
                        {
                            closeStatus = 1;
                        }
                        if (closeStatus > 0)
                        {
                            var oriNum = s.Num;
                            s.Num = 0;
                            Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
                            s.Status = 0;
                        }
                    }
                    else if (s.Status == 2)
                    {
                        int closeStatus = 0;
                        if (q.Close > s.LastOpenPrice + atrLossRate * (decimal)atr1.Atr)
                        {
                            closeStatus = 1;
                        }
                        if (q.Close < s.LastOpenPrice - atrWinRate * (decimal)atr1.Atr)
                        {
                            closeStatus = 1;
                        }
                        if (kdj1.J > overDown+20 && kdj1.K > kdj1.D && kdj2.K < kdj2.D && q.Close < s.LastOpenPrice - 2 * (decimal)atr1.Atr)
                        {
                            closeStatus = 1;
                        }
                        if (closeStatus > 0)
                        {
                            var oriNum = s.Num;
                            s.Num = 0;
                            Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
                            s.Status = 0;
                        }
                    }
                }
            }
        }
    }
}
