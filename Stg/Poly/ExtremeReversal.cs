using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    /// <summary>
    /// 极端反转策略 (ExtremeReversal)
    /// 
    /// 信号识别：5维评分系统，仅保留诊断验证的正向预测条件
    ///   C1. 连续3根K线反转 (54.9% 单条件胜率)
    ///   C2. RSI 超卖/超买 (54.7%)
    ///   C3. Stochastic 极端 (54.9%)
    ///   C4. 放量反转 (52.6%)
    ///   C5. BB突破 (54.2%)
    /// 
    /// 交易逻辑：
    ///   - 每bar独立评估5维条件，达到minConfirm(默认5)时触发信号
    ///   - 同时在主交易所做对应方向的开平仓
    ///   - Polymarket下单使用signalBars计数器连续下单
    /// </summary>
    public class ExtremeReversal : PolyStgBase
    {
        private class State
        {
            public int Status { get; set; }       // 0=空仓 1=多头 2=空头
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public int SignalDir { get; set; }     // 当前信号方向: 0=无 1=看涨 2=看跌
            public int RemainBars { get; set; }    // 剩余可下单K线数
        }

        private readonly Dictionary<string, State> _stateDic = new();

        public ExtremeReversal() { }
        public ExtremeReversal(string id) : base(id) { }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 1;
            sd.UseGlobalCalc = 0;

            // ==================== 信号参数 ====================
            sd.ArgDescDic["shadowRatio"] = new ArgDesc { Text = "影线比例", Explain = "影线长度 > 实体 × 该值时触发影线信号(默认2.0)", Type = "number" };
            sd.ArgDic["shadowRatio"] = 2.0;

            sd.ArgDescDic["signalBars"] = new ArgDesc { Text = "信号持续K线数", Explain = "信号出现后连续下单的K线根数", Type = "number" };
            sd.ArgDic["signalBars"] = 5;

            // ==================== 确认过滤参数 ====================
            sd.ArgDescDic["minConfirm"] = new ArgDesc { Text = "最少条件数", Explain = "5维评分：连续K线/RSI/StochK/量比/BB，需满足的最少条件数", Type = "number" };
            sd.ArgDic["minConfirm"] = 5;

            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "RSI计算周期", Type = "number" };
            sd.ArgDic["rsiPeriod"] = 14;

            sd.ArgDescDic["rsiOversold"] = new ArgDesc { Text = "RSI超卖线", Explain = "RSI低于该值确认买入", Type = "number" };
            sd.ArgDic["rsiOversold"] = 35;

            sd.ArgDescDic["rsiOverbought"] = new ArgDesc { Text = "RSI超买线", Explain = "RSI高于该值确认卖出", Type = "number" };
            sd.ArgDic["rsiOverbought"] = 65;

            sd.ArgDescDic["volRatio"] = new ArgDesc { Text = "量比阈值", Explain = "成交量 > 20均量 × 该值时确认", Type = "number" };
            sd.ArgDic["volRatio"] = 1.2;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易方向", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["stopLoss"] = new ArgDesc { Text = "止损%", Explain = "固定止损百分比，0为不启用", Options = "0:禁用|1:启用", Type = "bool" };
            sd.ArgDic["stopLoss"] = 5.0m;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "手数", Explain = "固定手数模式下下单数量", Type = "number" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "金额", Explain = "固定金额模式下用于换算手数", Type = "number" };
            sd.ArgDic["money"] = 10000m;

            // ==================== Polymarket参数 ====================
            AddPolyArgs(sd, 5m);

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-Signal"] = "#FF9800";

            sd.MidValDic["sub0"] = 0;

            return sd;
        }

        #region OnBar

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (ArgDic == null || tu.QuoteList == null || tu.QuoteList.Count == 0) return;

            EnsurePolymarketInitialized(period);

            if (_polyService != null)
                RefreshEventIfNeeded(tq.Date, period);

            // 每个 tick 尝试 Polymarket 下单（信号持续期间）
            if (!IsBacktest && !_polyOrderPlaced
                && _stateDic.TryGetValue(tu.GetStateKey(), out var existing)
                && existing.SignalDir != 0 && existing.RemainBars > 0)
            {
                var polyNum = Convert.ToDecimal(ArgDic["polyNum"]);
                if (polyNum > 0 && TryPlacePolymarketOrder(existing.SignalDir == 1, polyNum, tq))
                    _polyOrderPlaced = true;
            }

            if (!isFinal) return;

            if (tu.QuoteList.Count < 30) return;
            var quotes = tu.QuoteList;
            var q = quotes.Last();
            decimal price = q.Close;

            double shadowRatio = Convert.ToDouble(ArgDic["shadowRatio"]);
            int signalBars = Convert.ToInt32(ArgDic["signalBars"]);
            int minConfirm = Convert.ToInt32(ArgDic["minConfirm"]);
            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            int rsiOversold = Convert.ToInt32(ArgDic["rsiOversold"]);
            int rsiOverbought = Convert.ToInt32(ArgDic["rsiOverbought"]);
            double volRatioThreshold = Convert.ToDouble(ArgDic["volRatio"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            var sk = tu.GetStateKey();
            if (!_stateDic.TryGetValue(sk, out var s))
            {
                s = new State();
                _stateDic[sk] = s;
            }

            // ── 5维评分系统（仅保留诊断验证的正向预测条件） ──
            int buyScore = 0, sellScore = 0;
            bool curBarUp = q.Close > q.Open;

            // C1: 连续3根K线反转 (54.9% 单条件胜率)
            if (quotes.Count >= 4)
            {
                int consecUp = 0, consecDown = 0;
                for (int ci = quotes.Count - 1; ci >= 1 && ci >= quotes.Count - 10; ci--)
                {
                    if (quotes[ci].Close > quotes[ci - 1].Close)
                    { if (consecDown > 0) break; consecUp++; }
                    else if (quotes[ci].Close < quotes[ci - 1].Close)
                    { if (consecUp > 0) break; consecDown++; }
                    else break;
                }
                if (consecDown >= 3) buyScore++;   // 连续下跌 → 看涨反转
                if (consecUp >= 3) sellScore++;     // 连续上涨 → 看跌反转
            }

            // C2: RSI 超卖/超买 (54.7% 单条件胜率)
            var rsiVal = quotes.GetRsi(rsiPeriod).Last().Rsi;
            if (rsiVal.HasValue)
            {
                if (rsiVal.Value < rsiOversold) buyScore++;
                if (rsiVal.Value > rsiOverbought) sellScore++;
            }

            // C3: Stochastic 极端 K < 20 / K > 80 (54.9% 单条件胜率)
            var stoch = quotes.GetStoch(14, 3, 3).Last();
            if (stoch.K.HasValue)
            {
                if (stoch.K.Value < 20) buyScore++;
                if (stoch.K.Value > 80) sellScore++;
            }

            // C4: 放量反转 (52.6% 单条件胜率)
            if (quotes.Count >= 20)
            {
                decimal avgVol = 0;
                for (int vi = quotes.Count - 20; vi < quotes.Count; vi++) avgVol += quotes[vi].Volume;
                avgVol /= 20;
                if (avgVol > 0 && q.Volume > avgVol * (decimal)volRatioThreshold)
                {
                    if (!curBarUp) buyScore++;   // 放量下跌 → 看涨反转
                    if (curBarUp) sellScore++;    // 放量上涨 → 看跌反转
                }
            }

            // C5: BB(10,2) 突破 (54.2% 单条件胜率)
            var boll = quotes.GetBollingerBands(10, 2).Last();
            if (boll.LowerBand.HasValue && boll.UpperBand.HasValue)
            {
                if ((double)price < boll.LowerBand.Value) buyScore++;
                if ((double)price > boll.UpperBand.Value) sellScore++;
            }

            // 信号判定：需达到 minConfirm 且买卖不冲突
            int barSignal = 0; // 本bar的交易方向
            if (buyScore >= minConfirm && sellScore < minConfirm) barSignal = 1;
            else if (sellScore >= minConfirm && buyScore < minConfirm) barSignal = 2;

            // mode 过滤
            if (mode == 1 && barSignal == 2) barSignal = 0;
            if (mode == 2 && barSignal == 1) barSignal = 0;

            // Poly 信号计数器：新信号出现时重置
            if (barSignal != 0)
            {
                s.SignalDir = barSignal;
                s.RemainBars = signalBars;
                _polyOrderPlaced = false;
            }
            else if (s.RemainBars > 0)
            {
                s.RemainBars--;
                if (s.RemainBars == 0) s.SignalDir = 0;
            }

            // 绘图
            double plotVal = barSignal != 0 ? (barSignal == 1 ? 1 : -1) : 0;
            Plot("sub0", "Signal", PlotType.LINE, plotVal);

            // ── 每bar独立评估交易（MoEPredict风格） ──
            var num = CalculateLots(tu.MktSymbol, price);
            if (num <= 0) return;

            // 止损检查
            var _sl = Convert.ToDecimal(ArgDic["stopLoss"]);
            if (s.Status == 1 && _sl > 0 && s.EntryPrice > 0 && price < s.EntryPrice * (1 - _sl / 100m))
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                return;
            }
            if (s.Status == 2 && _sl > 0 && s.EntryPrice > 0 && price > s.EntryPrice * (1 + _sl / 100m))
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                return;
            }

            // Step 1: 平掉上一bar的仓位
            if (s.Status == 1)
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
            }
            else if (s.Status == 2)
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
            }

            // Step 2: 仅当本bar条件满足时开仓（不依赖历史信号）
            if (barSignal == 1)
            {
                Trade(tu.MktSymbol, OrderType.BUY, price, num, period, sendMode);
                s.Status = 1; s.Num = num; s.EntryPrice = price;
            }
            else if (barSignal == 2)
            {
                Trade(tu.MktSymbol, OrderType.SELL, price, num, period, sendMode);
                s.Status = 2; s.Num = num; s.EntryPrice = price;
            }
        }

        #endregion

        #region Helpers

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

        #endregion
    }
}
