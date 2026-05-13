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
    public abstract class OneBarReversionBase : StgBase
    {
        protected OneBarReversionBase() { }
        protected OneBarReversionBase(string id) : base(id) { }

        protected class State
        {
            public int Status { get; set; }
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
        }

        private readonly Dictionary<string, State> _stateDic = new();

        protected static void AddCommonArgs(StgDesc sd, int defaultMode)
        {
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 1;
            sd.UseGlobalCalc = 0;
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易方向", Explain = "交易方向控制", Options = "0:双向|1:仅多|2:仅空", Type = "select" };
            sd.ArgDic["mode"] = defaultMode;
            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;
            sd.ArgDescDic["stopLoss"] = new ArgDesc { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDic["stopLoss"] = 5.0m;
            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDescDic["lots"] = new ArgDesc { Text = "手数", Explain = "固定手数模式下下单数量" };
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDescDic["money"] = new ArgDesc { Text = "金额", Explain = "固定金额模式下用于换算手数" };
            sd.ArgDic["money"] = 10000m;
            sd.ArgDescDic["polyNum"] = new ArgDesc { Text = "Poly下单数量", Explain = "Polymarket 下单数量(USDC)，该候选仅输出方向信号" };
            sd.ArgDic["polyNum"] = 0m;
            sd.ColorDic["sub0-Signal"] = "#FF9800";
            sd.MidValDic["sub0"] = 0;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (isFinal && ArgDic != null && tu.QuoteList != null && tu.QuoteList.Count >= MinBars)
            {
                var quotes = tu.QuoteList;
                var q = quotes.Last();
                var price = q.Close;
                var mode = Convert.ToInt32(ArgDic["mode"]);
                var sendMode = Convert.ToInt32(ArgDic["sendMode"]);
                var signal = GetSignal(quotes);
                if (mode == 1 && signal == 2)
                    signal = 0;
                if (mode == 2 && signal == 1)
                    signal = 0;

                Plot("sub0", "Signal", PlotType.LINE, signal == 1 ? 1 : signal == 2 ? -1 : 0);

                var sk = tu.GetStateKey();
                if (!_stateDic.TryGetValue(sk, out var s))
                {
                    s = new State();
                    _stateDic[sk] = s;
                }

                var num = CalculateLots(tu.MktSymbol, price);
                if (num > 0)
                {
                    var stopLoss = Convert.ToDecimal(ArgDic["stopLoss"]);
                    var stopped = false;
                    if (s.Status == 1 && stopLoss > 0 && s.EntryPrice > 0 && price < s.EntryPrice * (1 - stopLoss / 100m))
                    {
                        Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, price, s.Num, period, sendMode);
                        s.Status = 0;
                        s.Num = 0;
                        s.EntryPrice = 0;
                        stopped = true;
                    }
                    if (!stopped && s.Status == 2 && stopLoss > 0 && s.EntryPrice > 0 && price > s.EntryPrice * (1 + stopLoss / 100m))
                    {
                        Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, price, s.Num, period, sendMode);
                        s.Status = 0;
                        s.Num = 0;
                        s.EntryPrice = 0;
                        stopped = true;
                    }
                    if (!stopped)
                    {
                        if (s.Status == 1)
                        {
                            Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, price, s.Num, period, sendMode);
                            s.Status = 0;
                            s.Num = 0;
                            s.EntryPrice = 0;
                        }
                        else if (s.Status == 2)
                        {
                            Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, price, s.Num, period, sendMode);
                            s.Status = 0;
                            s.Num = 0;
                            s.EntryPrice = 0;
                        }

                        if (signal == 1)
                        {
                            Trade(tu.MktSymbol, OrderType.BUY, price, num, period, sendMode);
                            s.Status = 1;
                            s.Num = num;
                            s.EntryPrice = price;
                        }
                        else if (signal == 2)
                        {
                            Trade(tu.MktSymbol, OrderType.SELL, price, num, period, sendMode);
                            s.Status = 2;
                            s.Num = num;
                            s.EntryPrice = price;
                        }
                    }
                }
            }
        }

        protected abstract int MinBars { get; }

        protected abstract int GetSignal(List<SkQuote> quotes);

        protected static double CalcRsi(List<SkQuote> quotes, int period)
        {
            if (quotes.Count <= period)
                return 50.0;
            var gain = 0.0;
            var loss = 0.0;
            for (var i = quotes.Count - period; i < quotes.Count; i++)
            {
                var diff = (double)(quotes[i].Close - quotes[i - 1].Close);
                if (diff > 0)
                    gain += diff;
                else
                    loss -= diff;
            }
            if (loss <= 0)
                return gain > 0 ? 100.0 : 50.0;
            var rs = gain / loss;
            return 100.0 - 100.0 / (1.0 + rs);
        }

        protected static double? CalcEma(List<SkQuote> quotes, int period)
        {
            if (quotes.Count < period)
                return null;
            var start = quotes.Count - period;
            var ema = quotes.Skip(start).Take(period).Average(q => (double)q.Close);
            var k = 2.0 / (period + 1.0);
            for (var i = start + 1; i < quotes.Count; i++)
                ema = (double)quotes[i].Close * k + ema * (1.0 - k);
            return ema;
        }

        protected static (double lower, double upper)? CalcBoll(List<SkQuote> quotes, int period, double stdDev)
        {
            if (quotes.Count < period)
                return null;
            var closes = quotes.Skip(quotes.Count - period).Take(period).Select(q => (double)q.Close).ToArray();
            var mean = closes.Average();
            var variance = closes.Sum(v => Math.Pow(v - mean, 2)) / period;
            var std = Math.Sqrt(variance);
            return (mean - std * stdDev, mean + std * stdDev);
        }

        private decimal CalculateLots(string mktSymbol, decimal price)
        {
            var num = Convert.ToDecimal(ArgDic["lots"]);
            if (Convert.ToInt32(ArgDic["lotsMode"]) == 1)
            {
                var sym = GetSymbol(mktSymbol);
                num = Convert.ToDecimal(ArgDic["money"]) / (price * sym.multiplier * sym.margin_ratio);
                num = sym.symbol_type == (int)SymbolType.COIN ? (int)(num * 1000) / 1000.0m : (int)num;
            }
            return num;
        }
    }

    public class RsiFastReversion : OneBarReversionBase
    {
        public RsiFastReversion() { }
        public RsiFastReversion(string id) : base(id) { }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "快速RSI周期" };
            sd.ArgDic["rsiPeriod"] = 3;
            sd.ArgDescDic["rsiOversold"] = new ArgDesc { Text = "RSI超卖线", Explain = "RSI低于该值看涨" };
            sd.ArgDic["rsiOversold"] = 10;
            sd.ArgDescDic["rsiOverbought"] = new ArgDesc { Text = "RSI超买线", Explain = "RSI高于该值看跌" };
            sd.ArgDic["rsiOverbought"] = 90;
            sd.ArgDescDic["emaPeriod"] = new ArgDesc { Text = "EMA过滤周期", Explain = "趋势/逆趋势过滤EMA周期" };
            sd.ArgDic["emaPeriod"] = 200;
            sd.ArgDescDic["emaFilter"] = new ArgDesc { Text = "EMA过滤", Explain = "0不过滤，1顺势，2逆势", Options = "0:不过滤|1:顺势|2:逆势", Type = "select" };
            sd.ArgDic["emaFilter"] = 2;
            AddCommonArgs(sd, 0);
            return sd;
        }

        protected override int MinBars => Math.Max(Convert.ToInt32(ArgDic["emaPeriod"]) + 20, 220);

        protected override int GetSignal(List<SkQuote> quotes)
        {
            var q = quotes.Last();
            var rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            var rsiLow = Convert.ToInt32(ArgDic["rsiOversold"]);
            var rsiHigh = Convert.ToInt32(ArgDic["rsiOverbought"]);
            var emaPeriod = Convert.ToInt32(ArgDic["emaPeriod"]);
            var emaFilter = Convert.ToInt32(ArgDic["emaFilter"]);
            var rsi = quotes.GetRsi(rsiPeriod).Last().Rsi.GetValueOrDefault(50);
            var ema = quotes.GetEma(emaPeriod).Last().Ema;
            var signal = 0;
            if (rsi <= rsiLow)
                signal = 1;
            else if (rsi >= rsiHigh)
                signal = 2;
            if (signal != 0 && ema.HasValue)
            {
                if (emaFilter == 1 && signal == 1 && (double)q.Close < ema.Value)
                    signal = 0;
                if (emaFilter == 1 && signal == 2 && (double)q.Close > ema.Value)
                    signal = 0;
                if (emaFilter == 2 && signal == 1 && (double)q.Close > ema.Value)
                    signal = 0;
                if (emaFilter == 2 && signal == 2 && (double)q.Close < ema.Value)
                    signal = 0;
            }
            return signal;
        }
    }

    public class BollRsiShortReversion : OneBarReversionBase
    {
        public BollRsiShortReversion() { }
        public BollRsiShortReversion(string id) : base(id) { }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "RSI计算周期" };
            sd.ArgDic["rsiPeriod"] = 14;
            sd.ArgDescDic["rsiOversold"] = new ArgDesc { Text = "RSI超卖线", Explain = "RSI低于该值看涨" };
            sd.ArgDic["rsiOversold"] = 35;
            sd.ArgDescDic["rsiOverbought"] = new ArgDesc { Text = "RSI超买线", Explain = "RSI高于该值看跌" };
            sd.ArgDic["rsiOverbought"] = 65;
            sd.ArgDescDic["bollPeriod"] = new ArgDesc { Text = "布林周期", Explain = "布林带周期" };
            sd.ArgDic["bollPeriod"] = 20;
            sd.ArgDescDic["bollStdDev"] = new ArgDesc { Text = "布林标准差", Explain = "布林带标准差倍数" };
            sd.ArgDic["bollStdDev"] = 1.8;
            AddCommonArgs(sd, 2);
            return sd;
        }

        protected override int MinBars => Math.Max(Convert.ToInt32(ArgDic["bollPeriod"]) + 20, 50);

        protected override int GetSignal(List<SkQuote> quotes)
        {
            var q = quotes.Last();
            var rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            var rsiLow = Convert.ToInt32(ArgDic["rsiOversold"]);
            var rsiHigh = Convert.ToInt32(ArgDic["rsiOverbought"]);
            var bollPeriod = Convert.ToInt32(ArgDic["bollPeriod"]);
            var bollStdDev = Convert.ToDouble(ArgDic["bollStdDev"]);
            var rsi = quotes.GetRsi(rsiPeriod).Last().Rsi.GetValueOrDefault(50);
            var boll = quotes.GetBollingerBands(bollPeriod, bollStdDev).Last();
            var signal = 0;
            if (boll.LowerBand.HasValue && boll.UpperBand.HasValue)
            {
                if ((double)q.Close < boll.LowerBand.Value && rsi <= rsiLow)
                    signal = 1;
                else if ((double)q.Close > boll.UpperBand.Value && rsi >= rsiHigh)
                    signal = 2;
            }
            return signal;
        }
    }
}
