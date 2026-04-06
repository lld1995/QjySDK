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
    public class KDJ_SMA : StgBase
    {
        public KDJ_SMA()
        {
        }

        public KDJ_SMA(string id) : base(id)
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
            sd.ArgDic["smaShortPeriods"] = 10;
            sd.ArgDic["smaLongPeriods"] = 20;

            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;
            sd.ArgDic["stopLoss"] = 5.0m;

            //手数控制
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
            sd.ArgDescDic["stopLoss"] = new ArgDesc() { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
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
                    int mode = (int)ArgDic["mode"];
                    int sendMode = (int)ArgDic["sendMode"];

                    var overUp=(int)ArgDic["overUp"];
                    var overDown=(int)ArgDic["overDown"];

                    var q = tu.QuoteList.Last();

                    var smaShort=tu.QuoteList.GetSma((int)ArgDic["smaShortPeriods"]).ToList();
                    var smaLong = tu.QuoteList.GetSma((int)ArgDic["smaLongPeriods"]).ToList() ;

                    var smaShort1 = smaShort[smaShort.Count - 1];
                    var smaLong1 = smaLong[smaLong.Count - 1];

                    var kdj = tu.QuoteList.GetStoch((int)ArgDic["lookbackPeriods"], (int)ArgDic["signalPeriods"], (int)ArgDic["smoothPeriods"]).ToList();

                    var kdj1 = kdj[kdj.Count - 1];
                    var kdj2 = kdj[kdj.Count - 2];
                    var kdj3 = kdj[kdj.Count - 3];

                    Plot("kdj", "K", PlotType.LINE, kdj1.K);
                    Plot("kdj", "D", PlotType.LINE, kdj1.D);
                    Plot("kdj", "J", PlotType.LINE, kdj1.J);

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


                    if (s.Status == 0)
                    {
                        if (smaShort1.Sma>smaLong1.Sma&& kdj1.K < overDown && kdj1.D < overDown && kdj2.K < kdj2.D && kdj1.K > kdj1.D  && mode != 2)
                        {
                            s.Status = 1;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                        }
                        if (smaShort1.Sma < smaLong1.Sma && kdj1.K > overUp && kdj1.D > overUp && kdj2.K > kdj2.D && kdj1.K<kdj1.D && mode != 1)
                        {
                            s.Status = 2;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        }
                    }
                    else if (s.Status == 1)
                    {
                        // 止损检查
                        var _sl = (decimal)ArgDic["stopLoss"];
                        if (_sl > 0 && s.LastOpenPrice > 0 && q.Close < s.LastOpenPrice * (1 - _sl / 100m))
                        {
                            Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                            s.Status = 0; s.Num = 0; s.LastOpenPrice = 0;
                            return;
                        }

                        if (smaShort1.Sma < smaLong1.Sma && kdj1.K > overUp && kdj1.D > overUp && kdj2.K > kdj2.D && kdj1.K < kdj1.D && mode != 1)
                        {
                            var oriNum = s.Num;
                            s.Num = 0;
                            Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
                            s.Status = 0;

                            s.Status = 2;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        }
                    }
                    else if (s.Status == 2)
                    {
                        // 止损检查
                        var _sl2 = (decimal)ArgDic["stopLoss"];
                        if (_sl2 > 0 && s.LastOpenPrice > 0 && q.Close > s.LastOpenPrice * (1 + _sl2 / 100m))
                        {
                            Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                            s.Status = 0; s.Num = 0; s.LastOpenPrice = 0;
                            return;
                        }

                        if (smaShort1.Sma > smaLong1.Sma && kdj1.K < overDown && kdj1.D < overDown && kdj2.K < kdj2.D && kdj1.K > kdj1.D && mode != 2)
                        {
                            var oriNum = s.Num;
                            s.Num = 0;
                            Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
                            s.Status = 0;

                            s.Status = 1;
                            s.Num = num;
                            Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                        }
                    }
                }
            }
        }
    }
}
