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
    /// Andromeda交易系统
    /// 
    /// 策略原理：
    /// Andromeda是由Petros Development Corp开发的经典趋势跟踪系统。
    /// 该系统结合了多重时间框架的移动平均线、动量指标和波动率过滤器，
    /// 旨在捕捉中长期趋势并过滤掉市场噪音。
    /// 
    /// 核心规则：
    /// 1. 趋势识别：
    ///    - 使用长期EMA(50)确定主趋势方向
    ///    - 使用中期EMA(20)作为趋势确认
    ///    - 使用短期EMA(10)作为入场触发
    /// 
    /// 2. 入场规则：
    ///    - 多头：价格在长期EMA之上，短期EMA上穿中期EMA，且动量为正
    ///    - 空头：价格在长期EMA之下，短期EMA下穿中期EMA，且动量为负
    /// 
    /// 3. 波动率过滤：
    ///    - 使用ATR判断市场波动状态
    ///    - 波动率过低时不入场（避免盘整市）
    ///    - 波动率过高时减少仓位（控制风险）
    /// 
    /// 4. 动量确认：
    ///    - 使用ROC(动量变化率)确认趋势强度
    ///    - 动量方向必须与交易方向一致
    /// 
    /// 5. 出场规则：
    ///    - 趋势反转：短期EMA反向穿越中期EMA
    ///    - 跟踪止损：基于ATR的动态止损
    ///    - 时间止损：持仓超过最大周期数自动平仓
    /// 
    /// 6. 仓位管理：
    ///    - 基于ATR的波动率调整仓位
    ///    - 单笔风险不超过账户的设定比例
    /// </summary>
    public class Andromeda : StgBase
    {
        public Andromeda(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // ========== EMA参数 ==========
            sd.ArgDic["fastEMA"] = 10;                // 快速EMA周期
            sd.ArgDic["midEMA"] = 20;                 // 中速EMA周期
            sd.ArgDic["slowEMA"] = 50;                // 慢速EMA周期

            // ========== 动量参数 ==========
            sd.ArgDic["rocPeriod"] = 12;              // ROC动量周期
            sd.ArgDic["rocThreshold"] = 0.0;          // ROC阈值（绝对值）

            // ========== ATR参数 ==========
            sd.ArgDic["atrPeriod"] = 14;              // ATR计算周期
            sd.ArgDic["atrStopMultiplier"] = 2.5;     // 止损ATR倍数
            sd.ArgDic["trailingATRMultiplier"] = 2.0; // 跟踪止损ATR倍数

            // ========== 波动率过滤 ==========
            sd.ArgDic["volatilityFilterPeriod"] = 20; // 波动率过滤周期
            sd.ArgDic["minVolatilityRatio"] = 0.5;    // 最小波动率比率（相对于平均ATR）
            sd.ArgDic["maxVolatilityRatio"] = 2.5;    // 最大波动率比率

            // ========== 仓位管理 ==========
            sd.ArgDic["riskPerTrade"] = 0.02;         // 每笔交易风险比例（账户的2%）
            sd.ArgDic["accountEquity"] = 1000000m;    // 账户权益
            sd.ArgDic["lotsMode"] = 0;                // 0:按风险计算 1:固定手数
            sd.ArgDic["fixedLots"] = 1.0m;            // 固定手数

            // ========== 时间过滤 ==========
            sd.ArgDic["maxHoldingBars"] = 100;        // 最大持仓周期数（0=不限制）

            // ========== 交易模式 ==========
            sd.ArgDic["mode"] = 0;                    // 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;                // 发单模式

            // ========== 入场确认 ==========
            sd.ArgDic["requirePriceAboveEMA"] = 1;    // 是否要求价格在EMA正确一侧 0:否 1:是
            sd.ArgDic["requireMomentum"] = 1;         // 是否要求动量确认 0:否 1:是
            sd.ArgDic["requireVolatilityFilter"] = 1; // 是否启用波动率过滤 0:否 1:是

            // ========== 参数说明 ==========
            sd.ArgDescDic["fastEMA"] = new ArgDesc() { Text = "快速EMA", Explain = "短期趋势周期" };
            sd.ArgDescDic["midEMA"] = new ArgDesc() { Text = "中速EMA", Explain = "中期趋势周期" };
            sd.ArgDescDic["slowEMA"] = new ArgDesc() { Text = "慢速EMA", Explain = "长期趋势周期" };
            sd.ArgDescDic["rocPeriod"] = new ArgDesc() { Text = "ROC周期", Explain = "动量变化率计算周期" };
            sd.ArgDescDic["rocThreshold"] = new ArgDesc() { Text = "ROC阈值", Explain = "动量确认的最小绝对值" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期" };
            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc() { Text = "初始止损ATR倍数", Explain = "初始止损距离=ATR×倍数" };
            sd.ArgDescDic["trailingATRMultiplier"] = new ArgDesc() { Text = "跟踪止损ATR倍数", Explain = "跟踪止损距离=ATR×倍数" };
            sd.ArgDescDic["volatilityFilterPeriod"] = new ArgDesc() { Text = "波动率过滤周期", Explain = "计算平均ATR的周期" };
            sd.ArgDescDic["minVolatilityRatio"] = new ArgDesc() { Text = "最小波动率比率", Explain = "当前ATR/平均ATR的最小值" };
            sd.ArgDescDic["maxVolatilityRatio"] = new ArgDesc() { Text = "最大波动率比率", Explain = "当前ATR/平均ATR的最大值" };
            sd.ArgDescDic["riskPerTrade"] = new ArgDesc() { Text = "单笔风险比例", Explain = "每笔交易占账户权益的比例" };
            sd.ArgDescDic["accountEquity"] = new ArgDesc() { Text = "账户权益", Explain = "用于计算仓位的账户权益" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0:按风险计算 1:固定手数" };
            sd.ArgDescDic["fixedLots"] = new ArgDesc() { Text = "固定手数", Explain = "固定手数模式下的手数" };
            sd.ArgDescDic["maxHoldingBars"] = new ArgDesc() { Text = "最大持仓周期", Explain = "持仓超过此周期数自动平仓(0=不限制)" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易方向", Explain = "0:双向 1:仅做多 2:仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0:立即发单 1:下个开盘发单" };
            sd.ArgDescDic["requirePriceAboveEMA"] = new ArgDesc() { Text = "价格位置确认", Explain = "0:不要求 1:要求价格在EMA正确一侧" };
            sd.ArgDescDic["requireMomentum"] = new ArgDesc() { Text = "动量确认", Explain = "0:不要求 1:要求动量方向一致" };
            sd.ArgDescDic["requireVolatilityFilter"] = new ArgDesc() { Text = "波动率过滤", Explain = "0:不启用 1:启用波动率过滤" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 颜色配置
            sd.ColorDic["main-FastEMA"] = "#FF5722";      // 快速EMA橙红色
            sd.ColorDic["main-MidEMA"] = "#4ECDC4";       // 中速EMA青色
            sd.ColorDic["main-SlowEMA"] = "#45B7D1";      // 慢速EMA蓝色
            sd.ColorDic["sub0-ROC"] = "#9B59B6";           // ROC紫色
            sd.ColorDic["sub1-ATR"] = "#F39C12";           // ATR橙色
            sd.ColorDic["sub1-AvgATR"] = "#E67E22";        // 平均ATR橙色
            sd.ColorDic["main-StopLoss"] = "#E74C3C";          // 止损线红色

            return sd;
        }

        #region 内部类定义

        /// <summary>
        /// 持仓状态
        /// </summary>
        private class AndromedaState
        {
            public int Direction { get; set; }            // 0:空仓 1:多头 -1:空头
            public decimal EntryPrice { get; set; }       // 入场价格
            public decimal Num { get; set; }              // 持仓数量
            public decimal InitialStopPrice { get; set; } // 初始止损价格
            public decimal TrailingStopPrice { get; set; }// 跟踪止损价格
            public decimal HighestPrice { get; set; }     // 持仓期间最高价（多头）
            public decimal LowestPrice { get; set; }      // 持仓期间最低价（空头）
            public DateTime EntryTime { get; set; }       // 入场时间
            public int HoldingBars { get; set; }          // 持仓周期数

            // EMA交叉状态
            public bool PrevFastAboveMid { get; set; }    // 上一周期快速EMA是否在中速EMA之上
        }

        /// <summary>
        /// 技术指标数据
        /// </summary>
        private class IndicatorData
        {
            public decimal FastEMA { get; set; }
            public decimal MidEMA { get; set; }
            public decimal SlowEMA { get; set; }
            public decimal ROC { get; set; }
            public decimal ATR { get; set; }
            public decimal AvgATR { get; set; }           // 平均ATR（用于波动率过滤）
            public decimal VolatilityRatio { get; set; } // 波动率比率
        }

        #endregion

        #region 状态存储

        private Dictionary<string, AndromedaState> _stateDic = new Dictionary<string, AndromedaState>();
        private Dictionary<string, List<decimal>> _atrHistoryDic = new Dictionary<string, List<decimal>>();

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int fastEMAPeriod = (int)ArgDic["fastEMA"];
            int midEMAPeriod = (int)ArgDic["midEMA"];
            int slowEMAPeriod = (int)ArgDic["slowEMA"];
            int rocPeriod = (int)ArgDic["rocPeriod"];
            double rocThreshold = Convert.ToDouble(ArgDic["rocThreshold"]);
            int atrPeriod = (int)ArgDic["atrPeriod"];
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);
            double trailingATRMultiplier = Convert.ToDouble(ArgDic["trailingATRMultiplier"]);
            int volatilityFilterPeriod = (int)ArgDic["volatilityFilterPeriod"];
            double minVolatilityRatio = Convert.ToDouble(ArgDic["minVolatilityRatio"]);
            double maxVolatilityRatio = Convert.ToDouble(ArgDic["maxVolatilityRatio"]);
            int maxHoldingBars = (int)ArgDic["maxHoldingBars"];
            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int requirePriceAboveEMA = (int)ArgDic["requirePriceAboveEMA"];
            int requireMomentum = (int)ArgDic["requireMomentum"];
            int requireVolatilityFilter = (int)ArgDic["requireVolatilityFilter"];

            // 确保有足够的数据
            int requiredBars = Math.Max(Math.Max(slowEMAPeriod, rocPeriod), atrPeriod) + volatilityFilterPeriod + 1;
            if (tu.QuoteList.Count < requiredBars) return;

            var q = tu.QuoteList.Last();
            string stateKey = tu.GetStateKey();

            // 计算技术指标
            var indicators = CalculateIndicators(stateKey, tu.QuoteList, 
                fastEMAPeriod, midEMAPeriod, slowEMAPeriod, 
                rocPeriod, atrPeriod, volatilityFilterPeriod);

            if (indicators == null || indicators.ATR <= 0) return;

            // 绘制指标
            PlotIndicators(indicators);

            // 计算仓位大小
            decimal positionSize = CalculatePositionSize(tu, q, indicators.ATR);
            if (positionSize <= 0) return;

            // 获取或创建状态
            AndromedaState state = GetOrCreateState(stateKey);

            // 执行交易逻辑
            ExecuteAndromedaLogic(tu, period, q, state, positionSize, indicators,
                mode, sendMode, atrStopMultiplier, trailingATRMultiplier,
                minVolatilityRatio, maxVolatilityRatio, rocThreshold,
                maxHoldingBars, requirePriceAboveEMA, requireMomentum, requireVolatilityFilter);

        }

        #region 指标计算

        /// <summary>
        /// 计算所有技术指标（使用 Skender.Stock.Indicators）
        /// </summary>
        private IndicatorData CalculateIndicators(string stateKey, List<SkQuote> quoteList,
            int fastPeriod, int midPeriod, int slowPeriod, int rocPeriod, int atrPeriod, int volatilityPeriod)
        {
            var indicators = new IndicatorData();
            int lastIdx = quoteList.Count - 1;

            // 使用 Skender.Stock.Indicators 计算 EMA
            var emaFastList = quoteList.GetEma(fastPeriod).ToList();
            var emaMidList = quoteList.GetEma(midPeriod).ToList();
            var emaSlowList = quoteList.GetEma(slowPeriod).ToList();

            var fastEma = emaFastList[lastIdx].Ema;
            var midEma = emaMidList[lastIdx].Ema;
            var slowEma = emaSlowList[lastIdx].Ema;

            indicators.FastEMA = fastEma.HasValue ? (decimal)fastEma.Value : 0;
            indicators.MidEMA = midEma.HasValue ? (decimal)midEma.Value : 0;
            indicators.SlowEMA = slowEma.HasValue ? (decimal)slowEma.Value : 0;

            // 使用 Skender.Stock.Indicators 计算 ROC
            var rocList = quoteList.GetRoc(rocPeriod).ToList();
            var roc = rocList[lastIdx].Roc;
            indicators.ROC = roc.HasValue ? (decimal)roc.Value : 0;

            // 使用 Skender.Stock.Indicators 计算 ATR
            var atrList = quoteList.GetAtr(atrPeriod).ToList();
            var atr = atrList[lastIdx].Atr;
            indicators.ATR = atr.HasValue ? (decimal)atr.Value : 0;

            // 计算平均ATR和波动率比率
            if (!_atrHistoryDic.ContainsKey(stateKey))
            {
                _atrHistoryDic[stateKey] = new List<decimal>();
            }
            var atrHistory = _atrHistoryDic[stateKey];
            atrHistory.Add(indicators.ATR);

            while (atrHistory.Count > volatilityPeriod * 2)
            {
                atrHistory.RemoveAt(0);
            }

            if (atrHistory.Count >= volatilityPeriod)
            {
                indicators.AvgATR = atrHistory.Skip(atrHistory.Count - volatilityPeriod).Average();
                indicators.VolatilityRatio = indicators.AvgATR > 0 
                    ? indicators.ATR / indicators.AvgATR 
                    : 1.0m;
            }
            else
            {
                indicators.AvgATR = indicators.ATR;
                indicators.VolatilityRatio = 1.0m;
            }

            return indicators;
        }

        #endregion

        #region 仓位计算

        /// <summary>
        /// 计算仓位大小
        /// </summary>
        private decimal CalculatePositionSize(TableUnit tu, SkQuote q, decimal atr)
        {
            int lotsMode = (int)ArgDic["lotsMode"];

            if (lotsMode == 1)
            {
                return (decimal)ArgDic["fixedLots"];
            }

            decimal accountEquity = (decimal)ArgDic["accountEquity"];
            double riskPerTrade = Convert.ToDouble(ArgDic["riskPerTrade"]);
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);

            var symbol = GetSymbol(tu.MktSymbol);
            decimal multiplier = symbol.multiplier;

            // 风险金额 = 账户权益 × 风险比例
            decimal riskAmount = accountEquity * (decimal)riskPerTrade;

            // 止损距离 = ATR × 止损倍数
            decimal stopDistance = atr * (decimal)atrStopMultiplier;

            // 每手风险 = 止损距离 × 合约乘数
            decimal riskPerLot = stopDistance * multiplier;

            if (riskPerLot <= 0) return 0;

            // 仓位 = 风险金额 / 每手风险
            decimal positionSize = riskAmount / riskPerLot;

            // 根据品种类型取整
            if (symbol.symbol_type == (int)SymbolType.COIN)
            {
                positionSize = Math.Floor(positionSize * 1000) / 1000.0m;
            }
            else
            {
                positionSize = Math.Floor(positionSize);
            }

            return Math.Max(positionSize, 0);
        }

        #endregion

        #region 绘图

        /// <summary>
        /// 绘制技术指标
        /// </summary>
        private void PlotIndicators(IndicatorData indicators)
        {
            // 主图绘制EMA
            Plot("main", "FastEMA", PlotType.LINE, (double)indicators.FastEMA);
            Plot("main", "MidEMA", PlotType.LINE, (double)indicators.MidEMA);
            Plot("main", "SlowEMA", PlotType.LINE, (double)indicators.SlowEMA);

            // 副图1绘制ROC
            Plot("sub0", "ROC", PlotType.LINE, (double)indicators.ROC);
            Plot("sub0", "ZeroLine", PlotType.LINE, 0);

            // 副图2绘制ATR和波动率
            Plot("sub1", "ATR", PlotType.LINE, (double)indicators.ATR);
            Plot("sub1", "AvgATR", PlotType.LINE, (double)indicators.AvgATR);
        }

        #endregion

        #region 状态管理

        /// <summary>
        /// 获取或创建状态
        /// </summary>
        private AndromedaState GetOrCreateState(string stateKey)
        {
            if (!_stateDic.ContainsKey(stateKey))
            {
                _stateDic[stateKey] = new AndromedaState();
            }
            return _stateDic[stateKey];
        }

        #endregion

        #region 交易逻辑

        /// <summary>
        /// 执行Andromeda交易逻辑
        /// </summary>
        private void ExecuteAndromedaLogic(TableUnit tu, Period period, SkQuote q, AndromedaState state,
            decimal positionSize, IndicatorData indicators, int mode, int sendMode,
            double atrStopMultiplier, double trailingATRMultiplier,
            double minVolatilityRatio, double maxVolatilityRatio, double rocThreshold,
            int maxHoldingBars, int requirePriceAboveEMA, int requireMomentum, int requireVolatilityFilter)
        {
            // 判断EMA交叉
            bool fastAboveMid = indicators.FastEMA > indicators.MidEMA;
            bool crossUp = fastAboveMid && !state.PrevFastAboveMid;   // 金叉
            bool crossDown = !fastAboveMid && state.PrevFastAboveMid; // 死叉

            // 空仓状态
            if (state.Direction == 0)
            {
                HandleEntrySignal(tu, period, q, state, positionSize, indicators,
                    mode, sendMode, atrStopMultiplier, minVolatilityRatio, maxVolatilityRatio,
                    rocThreshold, requirePriceAboveEMA, requireMomentum, requireVolatilityFilter,
                    crossUp, crossDown);
            }
            // 多头持仓
            else if (state.Direction == 1)
            {
                HandleLongPosition(tu, period, q, state, indicators, sendMode,
                    trailingATRMultiplier, maxHoldingBars, crossDown);
            }
            // 空头持仓
            else if (state.Direction == -1)
            {
                HandleShortPosition(tu, period, q, state, indicators, sendMode,
                    trailingATRMultiplier, maxHoldingBars, crossUp);
            }

            // 更新EMA交叉状态
            state.PrevFastAboveMid = fastAboveMid;
        }

        /// <summary>
        /// 处理入场信号
        /// </summary>
        private void HandleEntrySignal(TableUnit tu, Period period, SkQuote q, AndromedaState state,
            decimal positionSize, IndicatorData indicators, int mode, int sendMode,
            double atrStopMultiplier, double minVolatilityRatio, double maxVolatilityRatio,
            double rocThreshold, int requirePriceAboveEMA, int requireMomentum, int requireVolatilityFilter,
            bool crossUp, bool crossDown)
        {
            // 波动率过滤
            if (requireVolatilityFilter == 1)
            {
                if (indicators.VolatilityRatio < (decimal)minVolatilityRatio)
                {
                    return; // 波动率过低，不入场
                }

                // 波动率过高时减少仓位
                if (indicators.VolatilityRatio > (decimal)maxVolatilityRatio)
                {
                    positionSize = positionSize * (decimal)maxVolatilityRatio / indicators.VolatilityRatio;
                }
            }

            // 做多信号
            if (crossUp && mode != 2)
            {
                bool priceCondition = requirePriceAboveEMA == 0 || q.Close > indicators.SlowEMA;
                bool momentumCondition = requireMomentum == 0 || indicators.ROC > (decimal)rocThreshold;

                if (priceCondition && momentumCondition)
                {
                    decimal entryPrice = q.Close;
                    decimal stopPrice = entryPrice - (decimal)atrStopMultiplier * indicators.ATR;

                    state.Direction = 1;
                    state.EntryPrice = entryPrice;
                    state.Num = positionSize;
                    state.InitialStopPrice = stopPrice;
                    state.TrailingStopPrice = stopPrice;
                    state.HighestPrice = entryPrice;
                    state.EntryTime = q.Date;
                    state.HoldingBars = 0;

                    Trade(tu.MktSymbol, OrderType.BUY, entryPrice, positionSize, period, sendMode);

                    // 绘制止损线和入场信号
                    Plot("main", "StopLoss", PlotType.LINE, (double)stopPrice);
                }
            }
            // 做空信号
            else if (crossDown && mode != 1)
            {
                bool priceCondition = requirePriceAboveEMA == 0 || q.Close < indicators.SlowEMA;
                bool momentumCondition = requireMomentum == 0 || indicators.ROC < -(decimal)rocThreshold;

                if (priceCondition && momentumCondition)
                {
                    decimal entryPrice = q.Close;
                    decimal stopPrice = entryPrice + (decimal)atrStopMultiplier * indicators.ATR;

                    state.Direction = -1;
                    state.EntryPrice = entryPrice;
                    state.Num = positionSize;
                    state.InitialStopPrice = stopPrice;
                    state.TrailingStopPrice = stopPrice;
                    state.LowestPrice = entryPrice;
                    state.EntryTime = q.Date;
                    state.HoldingBars = 0;

                    Trade(tu.MktSymbol, OrderType.SELL, entryPrice, positionSize, period, sendMode);

                    // 绘制止损线
                    Plot("main", "StopLoss", PlotType.LINE, (double)stopPrice);
                }
            }
        }

        /// <summary>
        /// 处理多头持仓
        /// </summary>
        private void HandleLongPosition(TableUnit tu, Period period, SkQuote q, AndromedaState state,
            IndicatorData indicators, int sendMode, double trailingATRMultiplier, int maxHoldingBars, bool crossDown)
        {
            state.HoldingBars++;

            // 更新最高价
            if (q.High > state.HighestPrice)
            {
                state.HighestPrice = q.High;

                // 更新跟踪止损
                decimal newTrailingStop = state.HighestPrice - (decimal)trailingATRMultiplier * indicators.ATR;
                if (newTrailingStop > state.TrailingStopPrice)
                {
                    state.TrailingStopPrice = newTrailingStop;
                }
            }

            // 取较高的止损价
            decimal effectiveStop = Math.Max(state.InitialStopPrice, state.TrailingStopPrice);

            // 1. 检查止损
            if (q.Low <= effectiveStop)
            {
                ClosePosition(tu, period, q, state, sendMode, "止损");
                return;
            }

            // 2. 检查趋势反转（死叉）
            if (crossDown)
            {
                ClosePosition(tu, period, q, state, sendMode, "趋势反转");
                return;
            }

            // 3. 检查时间止损
            if (maxHoldingBars > 0 && state.HoldingBars >= maxHoldingBars)
            {
                ClosePosition(tu, period, q, state, sendMode, "时间止损");
                return;
            }

            // 绘制当前止损线
            Plot("main", "StopLoss", PlotType.LINE, (double)effectiveStop);
        }

        /// <summary>
        /// 处理空头持仓
        /// </summary>
        private void HandleShortPosition(TableUnit tu, Period period, SkQuote q, AndromedaState state,
            IndicatorData indicators, int sendMode, double trailingATRMultiplier, int maxHoldingBars, bool crossUp)
        {
            state.HoldingBars++;

            // 更新最低价
            if (q.Low < state.LowestPrice)
            {
                state.LowestPrice = q.Low;

                // 更新跟踪止损
                decimal newTrailingStop = state.LowestPrice + (decimal)trailingATRMultiplier * indicators.ATR;
                if (newTrailingStop < state.TrailingStopPrice)
                {
                    state.TrailingStopPrice = newTrailingStop;
                }
            }

            // 取较低的止损价
            decimal effectiveStop = Math.Min(state.InitialStopPrice, state.TrailingStopPrice);

            // 1. 检查止损
            if (q.High >= effectiveStop)
            {
                ClosePosition(tu, period, q, state, sendMode, "止损");
                return;
            }

            // 2. 检查趋势反转（金叉）
            if (crossUp)
            {
                ClosePosition(tu, period, q, state, sendMode, "趋势反转");
                return;
            }

            // 3. 检查时间止损
            if (maxHoldingBars > 0 && state.HoldingBars >= maxHoldingBars)
            {
                ClosePosition(tu, period, q, state, sendMode, "时间止损");
                return;
            }

            // 绘制当前止损线
            Plot("main", "StopLoss", PlotType.LINE, (double)effectiveStop);
        }

        /// <summary>
        /// 平仓
        /// </summary>
        private void ClosePosition(TableUnit tu, Period period, SkQuote q, AndromedaState state,
            int sendMode, string reason)
        {
            if (state.Direction == 1)
            {
                // 平多仓
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, state.Num, period, sendMode);
            }
            else if (state.Direction == -1)
            {
                // 平空仓
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, state.Num, period, sendMode);
            }

            // 重置状态
            state.Direction = 0;
            state.EntryPrice = 0;
            state.Num = 0;
            state.InitialStopPrice = 0;
            state.TrailingStopPrice = 0;
            state.HighestPrice = 0;
            state.LowestPrice = 0;
            state.HoldingBars = 0;
        }

        #endregion
    }
}
