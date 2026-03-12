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
    /// KDJ + RSI 共振策略
    /// 
    /// 策略核心思想：
    /// 利用 KDJ 和 RSI 两个震荡指标的共振信号进行交易。
    /// 当两个指标同时发出相同方向的信号时，认为信号更加可靠。
    /// 
    /// 指标体系：
    /// 1. KDJ (随机指标)：
    ///    - K线：RSV的N日移动平均
    ///    - D线：K值的M日移动平均
    ///    - J线：3*K - 2*D
    ///    - K/D金叉（K上穿D）产生做多信号
    ///    - K/D死叉（K下穿D）产生做空信号
    ///    - 超买区（>80）和超卖区（<20）
    /// 
    /// 2. RSI (相对强弱指标)：
    ///    - RSI > 50 表示多头力量较强
    ///    - RSI < 50 表示空头力量较强
    ///    - 超买区（>70）和超卖区（<30）
    /// 
    /// 入场条件：
    /// 【做多】
    ///    - KDJ金叉（K上穿D）
    ///    - RSI > RSI多头阈值（默认50）
    ///    - KDJ处于超卖区反弹或中性区域
    ///    - 可选：J值从超卖区回升
    /// 
    /// 【做空】
    ///    - KDJ死叉（K下穿D）
    ///    - RSI < RSI空头阈值（默认50）
    ///    - KDJ处于超买区回落或中性区域
    ///    - 可选：J值从超买区回落
    /// 
    /// 出场条件：
    /// 1. KDJ反向交叉
    /// 2. RSI进入极端区域后反转
    /// 3. ATR动态止损/止盈
    /// 4. 最大持仓时间限制
    /// </summary>
    public class KDJ_RSI : StgBase
    {
        // KDJ参数
        private int _kdjPeriod;
        private int _kdjSignalPeriod;
        private decimal _kdjOverbought;
        private decimal _kdjOversold;

        // RSI参数
        private int _rsiPeriod;
        private decimal _rsiBullThreshold;
        private decimal _rsiBearThreshold;
        private decimal _rsiOverbought;
        private decimal _rsiOversold;

        // ATR止损止盈参数
        private int _atrPeriod;
        private decimal _stopLossAtrMultiplier;
        private decimal _takeProfitAtrMultiplier;

        // 交易参数
        private int _lotsMode;
        private decimal _lots;
        private decimal _money;
        private bool _useTrailingStop;
        private decimal _trailingStopAtrMultiplier;
        private int _maxHoldBars;

        // 共振模式
        private int _resonanceMode;

        // 状态管理
        private Dictionary<string, TradeState> _stateDict = new Dictionary<string, TradeState>();

        public KDJ_RSI()
        {
        }

        public KDJ_RSI(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 0;

            // KDJ参数
            sd.ArgDescDic["kdjPeriod"] = new ArgDesc { Text = "KDJ周期", Explain = "KDJ指标的计算周期（通常为9）" };
            sd.ArgDic["kdjPeriod"] = 9;

            sd.ArgDescDic["kdjSignalPeriod"] = new ArgDesc { Text = "KDJ信号周期", Explain = "K和D的平滑周期（通常为3）" };
            sd.ArgDic["kdjSignalPeriod"] = 3;

            sd.ArgDescDic["kdjOverbought"] = new ArgDesc { Text = "KDJ超买线", Explain = "KDJ超买区域阈值" };
            sd.ArgDic["kdjOverbought"] = 80.0;

            sd.ArgDescDic["kdjOversold"] = new ArgDesc { Text = "KDJ超卖线", Explain = "KDJ超卖区域阈值" };
            sd.ArgDic["kdjOversold"] = 20.0;

            // RSI参数
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "RSI指标的计算周期（通常为14）" };
            sd.ArgDic["rsiPeriod"] = 14;

            sd.ArgDescDic["rsiBullThreshold"] = new ArgDesc { Text = "RSI多头阈值", Explain = "RSI大于此值时允许做多" };
            sd.ArgDic["rsiBullThreshold"] = 50.0;

            sd.ArgDescDic["rsiBearThreshold"] = new ArgDesc { Text = "RSI空头阈值", Explain = "RSI小于此值时允许做空" };
            sd.ArgDic["rsiBearThreshold"] = 50.0;

            sd.ArgDescDic["rsiOverbought"] = new ArgDesc { Text = "RSI超买线", Explain = "RSI超买区域阈值" };
            sd.ArgDic["rsiOverbought"] = 70.0;

            sd.ArgDescDic["rsiOversold"] = new ArgDesc { Text = "RSI超卖线", Explain = "RSI超卖区域阈值" };
            sd.ArgDic["rsiOversold"] = 30.0;

            // ATR止损止盈参数
            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR指标的计算周期" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["stopLossAtrMultiplier"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离 = ATR × 此倍数" };
            sd.ArgDic["stopLossAtrMultiplier"] = 2.0;

            sd.ArgDescDic["takeProfitAtrMultiplier"] = new ArgDesc { Text = "止盈ATR倍数", Explain = "止盈距离 = ATR × 此倍数" };
            sd.ArgDic["takeProfitAtrMultiplier"] = 3.0;

            // 交易参数
            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0=固定手数 1=固定金额" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "固定手数", Explain = "每次交易的固定手数" };
            sd.ArgDic["lots"] = 1.0;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "固定金额", Explain = "每次交易的固定金额" };
            sd.ArgDic["money"] = 10000.0;

            sd.ArgDescDic["useTrailingStop"] = new ArgDesc { Text = "启用移动止损", Explain = "是否启用移动止损(1=启用,0=禁用)" };
            sd.ArgDic["useTrailingStop"] = 1;

            sd.ArgDescDic["trailingStopAtrMultiplier"] = new ArgDesc { Text = "移动止损ATR倍数", Explain = "移动止损距离 = ATR × 此倍数" };
            sd.ArgDic["trailingStopAtrMultiplier"] = 1.5;

            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "持仓超过此数量K线强制平仓（0=不限制）" };
            sd.ArgDic["maxHoldBars"] = 0;

            // 共振模式
            sd.ArgDescDic["resonanceMode"] = new ArgDesc { Text = "共振模式", Explain = "0=标准共振 1=严格共振(需RSI也在超卖/超买区)" };
            sd.ArgDic["resonanceMode"] = 0;

            // 颜色配置
            sd.ColorDic["sub0-K"] = "#2196F3";      // K线蓝色
            sd.ColorDic["sub0-D"] = "#FF9800";      // D线橙色
            sd.ColorDic["sub0-J"] = "#9C27B0";      // J线紫色
            sd.ColorDic["sub1-RSI"] = "#4CAF50";    // RSI绿色
            sd.ColorDic["main-StopLoss"] = "#F44336";
            sd.ColorDic["main-TakeProfit"] = "#8BC34A";

            // 副图中线
            sd.MidValDic["sub0"] = 50;  // KDJ中线
            sd.MidValDic["sub1"] = 50;  // RSI中线

            return sd;
        }

        private void InitParams()
        {
            _kdjPeriod = Convert.ToInt32(ArgDic["kdjPeriod"]);
            _kdjSignalPeriod = Convert.ToInt32(ArgDic["kdjSignalPeriod"]);
            _kdjOverbought = Convert.ToDecimal(ArgDic["kdjOverbought"]);
            _kdjOversold = Convert.ToDecimal(ArgDic["kdjOversold"]);

            _rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            _rsiBullThreshold = Convert.ToDecimal(ArgDic["rsiBullThreshold"]);
            _rsiBearThreshold = Convert.ToDecimal(ArgDic["rsiBearThreshold"]);
            _rsiOverbought = Convert.ToDecimal(ArgDic["rsiOverbought"]);
            _rsiOversold = Convert.ToDecimal(ArgDic["rsiOversold"]);

            _atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            _stopLossAtrMultiplier = Convert.ToDecimal(ArgDic["stopLossAtrMultiplier"]);
            _takeProfitAtrMultiplier = Convert.ToDecimal(ArgDic["takeProfitAtrMultiplier"]);

            _lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            _lots = Convert.ToDecimal(ArgDic["lots"]);
            _money = Convert.ToDecimal(ArgDic["money"]);
            _useTrailingStop = Convert.ToInt32(ArgDic["useTrailingStop"]) == 1;
            _trailingStopAtrMultiplier = Convert.ToDecimal(ArgDic["trailingStopAtrMultiplier"]);
            _maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);

            _resonanceMode = Convert.ToInt32(ArgDic["resonanceMode"]);
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

            if (ArgDic == null) return;

            InitParams();

            var quotes = tu.QuoteList;
            int minBars = Math.Max(Math.Max(_kdjPeriod, _rsiPeriod), _atrPeriod) + _kdjSignalPeriod + 5;
            if (quotes == null || quotes.Count < minBars) return;

            var stateKey = tu.GetStateKey();
            if (!_stateDict.ContainsKey(stateKey))
            {
                _stateDict[stateKey] = new TradeState();
            }
            var state = _stateDict[stateKey];

            // 计算KDJ指标 (使用Stoch指标)
            var stochList = quotes.GetStoch(_kdjPeriod, _kdjSignalPeriod, _kdjSignalPeriod).ToList();
            // 计算RSI指标
            var rsiList = quotes.GetRsi(_rsiPeriod).ToList();

            int lastIdx = quotes.Count - 1;
            int prevIdx = lastIdx - 1;

            var stochCurr = stochList[lastIdx];
            var stochPrev = stochList[prevIdx];
            var rsiCurr = rsiList[lastIdx];
            var rsiPrev = rsiList[prevIdx];

            if (!stochCurr.K.HasValue || !stochCurr.D.HasValue ||
                !stochPrev.K.HasValue || !stochPrev.D.HasValue ||
                !rsiCurr.Rsi.HasValue || !rsiPrev.Rsi.HasValue)
                return;

            // 计算ATR
            decimal atr = CalculateATR(quotes, _atrPeriod);
            if (atr <= 0) return;

            // 提取当前值（使用最后一根K线）
            var lastBar = quotes[lastIdx];
            decimal currentPrice = lastBar.Close;
            decimal currentHigh = lastBar.High;
            decimal currentLow = lastBar.Low;

            double kCurr = stochCurr.K.Value;
            double dCurr = stochCurr.D.Value;
            double jCurr = 3 * kCurr - 2 * dCurr;  // J = 3K - 2D

            double kPrev = stochPrev.K.Value;
            double dPrev = stochPrev.D.Value;
            double jPrev = 3 * kPrev - 2 * dPrev;

            double rsiValue = rsiCurr.Rsi.Value;
            double rsiPrevValue = rsiPrev.Rsi.Value;

            // 绘制指标
            Plot("sub0", "K", PlotType.LINE, kCurr);
            Plot("sub0", "D", PlotType.LINE, dCurr);
            Plot("sub0", "J", PlotType.LINE, jCurr);
            Plot("sub1", "RSI", PlotType.LINE, rsiValue);

            // 绘制止损止盈线
            if (state.HasPosition)
            {
                Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfit);
            }

            // 更新持仓计数
            if (state.HasPosition)
            {
                state.HoldBars++;
            }

            // 检测KDJ交叉
            bool kdjGoldenCross = kPrev <= dPrev && kCurr > dCurr;  // K上穿D（金叉）
            bool kdjDeathCross = kPrev >= dPrev && kCurr < dCurr;   // K下穿D（死叉）

            if (state.HasPosition)
            {
                HandleExitLogic(state, tu.MktSymbol, period, currentPrice, currentHigh, currentLow,
                    atr, kCurr, dCurr, jCurr, rsiValue, kdjGoldenCross, kdjDeathCross);
            }
            else
            {
                HandleEntryLogic(state, tu.MktSymbol, period, currentPrice, currentHigh, currentLow,
                    atr, kCurr, dCurr, jCurr, rsiValue, kdjGoldenCross, kdjDeathCross);
            }
        }

        private void HandleEntryLogic(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal currentHigh, decimal currentLow,
            decimal atr, double k, double d, double j, double rsi,
            bool kdjGoldenCross, bool kdjDeathCross)
        {
            // 做多条件：KDJ金叉 + RSI确认
            if (kdjGoldenCross)
            {
                bool rsiConfirm = rsi > (double)_rsiBullThreshold;

                // 严格模式：要求RSI也在超卖区反弹
                if (_resonanceMode == 1)
                {
                    rsiConfirm = rsiConfirm && rsi < (double)_rsiOverbought;
                }

                // KDJ不在极端超买区
                bool kdjValid = k < (double)_kdjOverbought;

                if (rsiConfirm && kdjValid)
                {
                    OpenLongPosition(state, mktSymbol, currentPrice, atr, period);
                }
            }
            // 做空条件：KDJ死叉 + RSI确认
            else if (kdjDeathCross)
            {
                bool rsiConfirm = rsi < (double)_rsiBearThreshold;

                // 严格模式：要求RSI也在超买区回落
                if (_resonanceMode == 1)
                {
                    rsiConfirm = rsiConfirm && rsi > (double)_rsiOversold;
                }

                // KDJ不在极端超卖区
                bool kdjValid = k > (double)_kdjOversold;

                if (rsiConfirm && kdjValid)
                {
                    OpenShortPosition(state, mktSymbol, currentPrice, atr, period);
                }
            }
        }

        private void HandleExitLogic(TradeState state, string mktSymbol, Period period,
            decimal currentPrice, decimal currentHigh, decimal currentLow,
            decimal atr, double k, double d, double j, double rsi,
            bool kdjGoldenCross, bool kdjDeathCross)
        {
            // 最大持仓时间检查
            if (_maxHoldBars > 0 && state.HoldBars >= _maxHoldBars)
            {
                ClosePosition(state, mktSymbol, currentPrice, period, "MaxHoldBars");
                return;
            }

            if (state.IsLong)
            {
                // 更新最高价
                if (currentHigh > state.HighestPrice)
                {
                    state.HighestPrice = currentHigh;
                }

                // 止损检查
                if (currentLow <= state.StopLoss)
                {
                    ClosePosition(state, mktSymbol, state.StopLoss, period, "StopLoss");
                    return;
                }

                // 止盈检查
                if (currentHigh >= state.TakeProfit)
                {
                    ClosePosition(state, mktSymbol, state.TakeProfit, period, "TakeProfit");
                    return;
                }

                // KDJ死叉：趋势反转信号
                if (kdjDeathCross)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "KdjCross");
                    return;
                }

                // RSI超买后回落
                if (rsi < (double)_rsiBearThreshold && k > (double)_kdjOverbought)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "RsiOverbought");
                    return;
                }

                // 移动止损
                if (_useTrailingStop)
                {
                    decimal newStopLoss = state.HighestPrice - atr * _trailingStopAtrMultiplier;
                    if (newStopLoss > state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
            else // 空头持仓
            {
                // 更新最低价
                if (currentLow < state.LowestPrice)
                {
                    state.LowestPrice = currentLow;
                }

                // 止损检查
                if (currentHigh >= state.StopLoss)
                {
                    ClosePosition(state, mktSymbol, state.StopLoss, period, "StopLoss");
                    return;
                }

                // 止盈检查
                if (currentLow <= state.TakeProfit)
                {
                    ClosePosition(state, mktSymbol, state.TakeProfit, period, "TakeProfit");
                    return;
                }

                // KDJ金叉：趋势反转信号
                if (kdjGoldenCross)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "KdjCross");
                    return;
                }

                // RSI超卖后反弹
                if (rsi > (double)_rsiBullThreshold && k < (double)_kdjOversold)
                {
                    ClosePosition(state, mktSymbol, currentPrice, period, "RsiOversold");
                    return;
                }

                // 移动止损
                if (_useTrailingStop)
                {
                    decimal newStopLoss = state.LowestPrice + atr * _trailingStopAtrMultiplier;
                    if (newStopLoss < state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
        }

        private void OpenLongPosition(TradeState state, string mktSymbol, decimal price, decimal atr, Period period)
        {
            decimal num = CalculateLots(mktSymbol, price);
            Trade(mktSymbol, OrderType.BUY, price, num, period, 0);

            state.HasPosition = true;
            state.IsLong = true;
            state.EntryPrice = price;
            state.PositionSize = num;
            state.EntryAtr = atr;
            state.StopLoss = price - atr * _stopLossAtrMultiplier;
            state.TakeProfit = price + atr * _takeProfitAtrMultiplier;
            state.HighestPrice = price;
            state.LowestPrice = price;
            state.HoldBars = 0;
        }

        private void OpenShortPosition(TradeState state, string mktSymbol, decimal price, decimal atr, Period period)
        {
            decimal num = CalculateLots(mktSymbol, price);
            Trade(mktSymbol, OrderType.SELL, price, num, period, 0);

            state.HasPosition = true;
            state.IsLong = false;
            state.EntryPrice = price;
            state.PositionSize = num;
            state.EntryAtr = atr;
            state.StopLoss = price + atr * _stopLossAtrMultiplier;
            state.TakeProfit = price - atr * _takeProfitAtrMultiplier;
            state.HighestPrice = price;
            state.LowestPrice = price;
            state.HoldBars = 0;
        }

        private void ClosePosition(TradeState state, string mktSymbol, decimal price, Period period, string reason)
        {
            OrderType ot = state.IsLong ? OrderType.SELL_TO_COVER : OrderType.BUY_TO_COVER;
            Trade(mktSymbol, ot, price, state.PositionSize, period, 0);

            state.Reset();
        }

        /// <summary>
        /// 计算交易手数
        /// </summary>
        private decimal CalculateLots(string mktSymbol, decimal price)
        {
            decimal num = _lots;

            if (_lotsMode == 1)
            {
                var symbol = GetSymbol(mktSymbol);
                num = _money / (price * symbol.multiplier * symbol.margin_ratio);

                if (symbol.symbol_type == (int)SymbolType.COIN)
                {
                    num = Math.Floor(num * 1000) / 1000.0m;
                }
                else
                {
                    num = Math.Floor(num);
                }
            }

            return num;
        }

        /// <summary>
        /// 计算ATR（使用 Skender.Stock.Indicators）
        /// </summary>
        private decimal CalculateATR(List<SkQuote> quotes, int period)
        {
            if (quotes.Count < period + 1)
                return 0;

            var atrList = quotes.GetAtr(period).ToList();
            int lastIdx = atrList.Count - 1;
            var atr = atrList[lastIdx].Atr;
            return atr.HasValue ? (decimal)atr.Value : 0;
        }

        private class TradeState
        {
            public bool HasPosition { get; set; } = false;
            public bool IsLong { get; set; } = false;
            public decimal EntryPrice { get; set; } = 0;
            public decimal PositionSize { get; set; } = 0;
            public decimal EntryAtr { get; set; } = 0;
            public decimal StopLoss { get; set; } = 0;
            public decimal TakeProfit { get; set; } = 0;
            public decimal HighestPrice { get; set; } = 0;
            public decimal LowestPrice { get; set; } = decimal.MaxValue;
            public int HoldBars { get; set; } = 0;

            public void Reset()
            {
                HasPosition = false;
                IsLong = false;
                EntryPrice = 0;
                PositionSize = 0;
                EntryAtr = 0;
                StopLoss = 0;
                TakeProfit = 0;
                HighestPrice = 0;
                LowestPrice = decimal.MaxValue;
                HoldBars = 0;
            }
        }
    }
}
