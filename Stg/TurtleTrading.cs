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
    /// 海龟交易系统 (Turtle Trading System)
    /// 
    /// 策略原理：
    /// 海龟交易系统是Richard Dennis和William Eckhardt在1983年开发的经典趋势跟踪系统。
    /// 该系统使用唐奇安通道突破作为入场信号，采用ATR进行仓位管理和止损设置。
    /// 
    /// 核心规则：
    /// 1. 入场规则：
    ///    - 系统1（短期）：价格突破20日高点做多，突破20日低点做空
    ///    - 系统2（长期）：价格突破55日高点做多，突破55日低点做空
    /// 
    /// 2. 加仓规则：
    ///    - 每上涨/下跌0.5个ATR加仓一次
    ///    - 最多加仓4次（共5个单位）
    /// 
    /// 3. 止损规则：
    ///    - 单笔止损：2个ATR
    ///    - 整体止损：根据加仓情况动态调整
    /// 
    /// 4. 出场规则：
    ///    - 系统1：价格跌破10日低点平多，突破10日高点平空
    ///    - 系统2：价格跌破20日低点平多，突破20日高点平空
    /// 
    /// 5. 仓位管理：
    ///    - 单位头寸 = 账户权益 × 1% / (ATR × 合约乘数)
    ///    - 单品种最大4个单位，高相关品种最大6个单位，单方向最大12个单位
    /// </summary>
    public class TurtleTrading : StgBase
    {
        public TurtleTrading(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // ========== 系统选择 ==========
            sd.ArgDic["systemType"] = 1;              // 1:系统1(短期) 2:系统2(长期)

            // ========== 唐奇安通道参数 ==========
            sd.ArgDic["entryPeriod"] = 20;            // 入场通道周期（系统1:20, 系统2:55）
            sd.ArgDic["exitPeriod"] = 10;             // 出场通道周期（系统1:10, 系统2:20）

            // ========== ATR参数 ==========
            sd.ArgDic["atrPeriod"] = 20;              // ATR计算周期（N值）
            sd.ArgDic["atrStopMultiplier"] = 2.0;     // 止损ATR倍数

            // ========== 加仓参数 ==========
            sd.ArgDic["enablePyramiding"] = 1;        // 是否启用金字塔加仓 0:否 1:是
            sd.ArgDic["pyramidingATR"] = 0.5;         // 加仓间隔ATR倍数
            sd.ArgDic["maxUnits"] = 4;                // 单品种最大单位数

            // ========== 仓位管理 ==========
            sd.ArgDic["riskPerTrade"] = 0.01;         // 每笔交易风险比例（账户的1%）
            sd.ArgDic["accountEquity"] = 1000000m;    // 账户权益
            sd.ArgDic["lotsMode"] = 0;                // 0:按风险计算 1:固定手数
            sd.ArgDic["fixedLots"] = 1.0m;            // 固定手数

            // ========== 交易模式 ==========
            sd.ArgDic["mode"] = 0;                    // 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;                // 发单模式

            // ========== 过滤规则 ==========
            sd.ArgDic["useLastTradeFilter"] = 1;      // 是否使用上次交易过滤 0:否 1:是
            // 系统1规则：如果上次突破盈利，则忽略本次突破信号

            // ========== 参数说明 ==========
            sd.ArgDescDic["systemType"] = new ArgDesc() { Text = "系统类型", Explain = "1:系统1(短期20/10) 2:系统2(长期55/20)" };
            sd.ArgDescDic["entryPeriod"] = new ArgDesc() { Text = "入场周期", Explain = "唐奇安通道入场周期" };
            sd.ArgDescDic["exitPeriod"] = new ArgDesc() { Text = "出场周期", Explain = "唐奇安通道出场周期" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期(N值)" };
            sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc() { Text = "止损ATR倍数", Explain = "止损距离=ATR×倍数" };
            sd.ArgDescDic["enablePyramiding"] = new ArgDesc() { Text = "启用加仓", Explain = "0:不加仓 1:金字塔加仓" };
            sd.ArgDescDic["pyramidingATR"] = new ArgDesc() { Text = "加仓ATR间隔", Explain = "每上涨N个ATR加仓一次" };
            sd.ArgDescDic["maxUnits"] = new ArgDesc() { Text = "最大单位数", Explain = "单品种最大持仓单位" };
            sd.ArgDescDic["riskPerTrade"] = new ArgDesc() { Text = "单笔风险比例", Explain = "每笔交易占账户权益的比例" };
            sd.ArgDescDic["accountEquity"] = new ArgDesc() { Text = "账户权益", Explain = "用于计算仓位的账户权益" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0:按风险计算 1:固定手数" };
            sd.ArgDescDic["fixedLots"] = new ArgDesc() { Text = "固定手数", Explain = "固定手数模式下的手数" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易方向", Explain = "0:双向 1:仅做多 2:仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0:立即发单 1:下个开盘发单" };
            sd.ArgDescDic["useLastTradeFilter"] = new ArgDesc() { Text = "上次交易过滤", Explain = "系统1:上次盈利则忽略本次信号" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;

            // 通道颜色配置
            sd.ColorDic["main-entryUpper"] = "#F6465D";      // 入场上轨红色
            sd.ColorDic["main-entryLower"] = "#0ECB81";      // 入场下轨绿色
            sd.ColorDic["main-exitUpper"] = "#FF9800"; // 出场上轨橙色
            sd.ColorDic["main-exitLower"] = "#2196F3"; // 出场下轨蓝色
            sd.ColorDic["sub0-ATR"] = "#9C27B0";        // ATR紫色
            sd.ColorDic["main-stopLoss"] = "#E91E63";       // 止损线粉色

            return sd;
        }

        #region 内部类定义

        /// <summary>
        /// 持仓单位信息
        /// </summary>
        private class PositionUnit
        {
            public decimal EntryPrice { get; set; }       // 入场价格
            public decimal Num { get; set; }              // 持仓数量
            public decimal StopPrice { get; set; }        // 止损价格
            public DateTime EntryTime { get; set; }       // 入场时间
        }

        /// <summary>
        /// 持仓状态
        /// </summary>
        private class TurtleState
        {
            public int Direction { get; set; }            // 0:空仓 1:多头 -1:空头
            public List<PositionUnit> Units { get; set; } = new List<PositionUnit>();
            public decimal LastATR { get; set; }          // 入场时的ATR
            public decimal LastBreakoutPrice { get; set; } // 上次突破价格
            public bool LastTradeWin { get; set; }        // 上次交易是否盈利
            public bool SkipNextSignal { get; set; }      // 是否跳过下次信号（系统1过滤）

            public decimal TotalNum => Units.Sum(u => u.Num);
            public decimal AvgEntryPrice => Units.Count > 0 
                ? Units.Sum(u => u.EntryPrice * u.Num) / TotalNum 
                : 0;
        }

        #endregion

        #region 状态存储

        private Dictionary<string, TurtleState> _stateDic = new Dictionary<string, TurtleState>();

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int systemType = (int)ArgDic["systemType"];
            int entryPeriod = (int)ArgDic["entryPeriod"];
            int exitPeriod = (int)ArgDic["exitPeriod"];
            int atrPeriod = (int)ArgDic["atrPeriod"];
            double atrStopMultiplier = Convert.ToDouble(ArgDic["atrStopMultiplier"]);
            int enablePyramiding = (int)ArgDic["enablePyramiding"];
            double pyramidingATR = Convert.ToDouble(ArgDic["pyramidingATR"]);
            int maxUnits = (int)ArgDic["maxUnits"];
            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int useLastTradeFilter = (int)ArgDic["useLastTradeFilter"];

            // 根据系统类型调整参数
            if (systemType == 2)
            {
                entryPeriod = 55;
                exitPeriod = 20;
            }

            // 确保有足够的数据
            int requiredBars = Math.Max(entryPeriod, atrPeriod) + 1;
            if (tu.QuoteList.Count < requiredBars) return;

            var q = tu.QuoteList.Last();
            string stateKey = tu.GetStateKey();

            // 计算ATR（使用 Skender.Stock.Indicators）
            decimal atr = CalculateATR(tu.QuoteList, atrPeriod);
            if (atr <= 0) return;

            // 计算唐奇安通道
            var entryDonchian = tu.QuoteList.GetDonchian(entryPeriod).ToList();
            var exitDonchian = tu.QuoteList.GetDonchian(exitPeriod).ToList();
            var entryChannel = entryDonchian.Count > 0 ? entryDonchian[entryDonchian.Count - 1] : null;
            var exitChannel = exitDonchian.Count > 0 ? exitDonchian[exitDonchian.Count - 1] : null;

            // 绘制通道和ATR
            PlotChannels(entryChannel, exitChannel, atr);

            // 计算单位头寸
            decimal unitSize = CalculateUnitSize(tu, q, atr);
            if (unitSize <= 0) return;

            // 获取或创建状态
            TurtleState state = GetOrCreateState(stateKey);

            // 执行交易逻辑
            ExecuteTurtleLogic(tu, period, q, state, unitSize, atr,
                entryChannel, exitChannel, mode, sendMode,
                enablePyramiding, pyramidingATR, maxUnits,
                atrStopMultiplier, useLastTradeFilter, systemType);

        }

        #region ATR计算

        /// <summary>
        /// 计算ATR（使用 Skender.Stock.Indicators）
        /// </summary>
        private decimal CalculateATR(List<SkQuote> quoteList, int period)
        {
            var atrList = quoteList.GetAtr(period).ToList();
            int lastIdx = atrList.Count - 1;
            var atr = atrList[lastIdx].Atr;
            return atr.HasValue ? (decimal)atr.Value : 0;
        }

        #endregion


        #region 仓位计算

        /// <summary>
        /// 计算单位头寸大小
        /// 单位头寸 = 账户权益 × 风险比例 / (ATR × 合约乘数)
        /// </summary>
        private decimal CalculateUnitSize(TableUnit tu, SkQuote q, decimal atr)
        {
            int lotsMode = (int)ArgDic["lotsMode"];

            if (lotsMode == 1)
            {
                return (decimal)ArgDic["fixedLots"];
            }

            decimal accountEquity = (decimal)ArgDic["accountEquity"];
            double riskPerTrade = Convert.ToDouble(ArgDic["riskPerTrade"]);

            var symbol = GetSymbol(tu.MktSymbol);
            decimal multiplier = symbol.multiplier;

            // 单位头寸 = 账户权益 × 1% / (ATR × 合约乘数)
            decimal dollarVolatility = atr * multiplier;
            if (dollarVolatility <= 0) return 0;

            decimal unitSize = accountEquity * (decimal)riskPerTrade / dollarVolatility;

            // 根据品种类型取整
            if (symbol.symbol_type == (int)SymbolType.COIN)
            {
                unitSize = Math.Floor(unitSize * 1000) / 1000.0m;
            }
            else
            {
                unitSize = Math.Floor(unitSize);
            }

            return Math.Max(unitSize, 0);
        }

        #endregion

        #region 绘图

        /// <summary>
        /// 绘制通道和指标
        /// </summary>
        private void PlotChannels(DonchianResult entryChannel, DonchianResult exitChannel, decimal atr)
        {
            if (entryChannel != null)
            {
                Plot("main", "entryUpper", PlotType.LINE, (double)(entryChannel.UpperBand ?? 0));
                Plot("main", "entryLower", PlotType.LINE, (double)(entryChannel.LowerBand ?? 0));
            }

            if (exitChannel != null)
            {
                Plot("main", "exitUpper", PlotType.LINE, (double)(exitChannel.UpperBand ?? 0));
                Plot("main", "exitLower", PlotType.LINE, (double)(exitChannel.LowerBand ?? 0));
            }

            Plot("sub0", "ATR", PlotType.LINE, (double)atr);
        }

        #endregion

        #region 状态管理

        /// <summary>
        /// 获取或创建状态
        /// </summary>
        private TurtleState GetOrCreateState(string stateKey)
        {
            if (!_stateDic.ContainsKey(stateKey))
            {
                _stateDic[stateKey] = new TurtleState();
            }
            return _stateDic[stateKey];
        }

        #endregion

        #region 交易逻辑

        /// <summary>
        /// 执行海龟交易逻辑
        /// </summary>
        private void ExecuteTurtleLogic(TableUnit tu, Period period, SkQuote q, TurtleState state,
            decimal unitSize, decimal atr, DonchianResult entryChannel, DonchianResult exitChannel,
            int mode, int sendMode, int enablePyramiding, double pyramidingATR, int maxUnits,
            double atrStopMultiplier, int useLastTradeFilter, int systemType)
        {
            if (entryChannel == null || exitChannel == null) return;
            if (entryChannel.UpperBand == null || entryChannel.LowerBand == null) return;
            if (exitChannel.UpperBand == null || exitChannel.LowerBand == null) return;

            // 空仓状态
            if (state.Direction == 0)
            {
                HandleEntrySignal(tu, period, q, state, unitSize, atr,
                    entryChannel, mode, sendMode, atrStopMultiplier,
                    useLastTradeFilter, systemType);
            }
            // 多头持仓
            else if (state.Direction == 1)
            {
                HandleLongPosition(tu, period, q, state, unitSize, atr,
                    entryChannel, exitChannel, sendMode,
                    enablePyramiding, pyramidingATR, maxUnits, atrStopMultiplier);
            }
            // 空头持仓
            else if (state.Direction == -1)
            {
                HandleShortPosition(tu, period, q, state, unitSize, atr,
                    entryChannel, exitChannel, sendMode,
                    enablePyramiding, pyramidingATR, maxUnits, atrStopMultiplier);
            }
        }

        /// <summary>
        /// 处理入场信号
        /// </summary>
        private void HandleEntrySignal(TableUnit tu, Period period, SkQuote q, TurtleState state,
            decimal unitSize, decimal atr, DonchianResult entryChannel, int mode, int sendMode,
            double atrStopMultiplier, int useLastTradeFilter, int systemType)
        {
            decimal upperBand = (decimal)(entryChannel.UpperBand ?? 0);
            decimal lowerBand = (decimal)(entryChannel.LowerBand ?? 0);

            // 系统1过滤规则：如果上次交易盈利，则跳过本次信号
            if (systemType == 1 && useLastTradeFilter == 1 && state.SkipNextSignal)
            {
                // 检查是否有突破信号（用于重置跳过标志）
                if (q.High > upperBand || q.Low < lowerBand)
                {
                    state.SkipNextSignal = false; // 已跳过一次，下次不再跳过
                }
                return;
            }

            // 突破上轨做多
            if (q.High > upperBand && mode != 2)
            {
                decimal entryPrice = upperBand;
                decimal stopPrice = entryPrice - (decimal)atrStopMultiplier * atr;

                state.Direction = 1;
                state.LastATR = atr;
                state.LastBreakoutPrice = entryPrice;
                state.Units.Add(new PositionUnit
                {
                    EntryPrice = entryPrice,
                    Num = unitSize,
                    StopPrice = stopPrice,
                    EntryTime = q.Date
                });

                Trade(tu.MktSymbol, OrderType.BUY, entryPrice, unitSize, period, sendMode);

                // 绘制止损线
                Plot("main", "stopLoss", PlotType.LINE, (double)stopPrice);
            }
            // 突破下轨做空
            else if (q.Low < lowerBand && mode != 1)
            {
                decimal entryPrice = lowerBand;
                decimal stopPrice = entryPrice + (decimal)atrStopMultiplier * atr;

                state.Direction = -1;
                state.LastATR = atr;
                state.LastBreakoutPrice = entryPrice;
                state.Units.Add(new PositionUnit
                {
                    EntryPrice = entryPrice,
                    Num = unitSize,
                    StopPrice = stopPrice,
                    EntryTime = q.Date
                });

                Trade(tu.MktSymbol, OrderType.SELL, entryPrice, unitSize, period, sendMode);

                // 绘制止损线
                Plot("main", "stopLoss", PlotType.LINE, (double)stopPrice);
            }
        }

        /// <summary>
        /// 处理多头持仓
        /// </summary>
        private void HandleLongPosition(TableUnit tu, Period period, SkQuote q, TurtleState state,
            decimal unitSize, decimal atr, DonchianResult entryChannel, DonchianResult exitChannel,
            int sendMode, int enablePyramiding, double pyramidingATR, int maxUnits, double atrStopMultiplier)
        {
            decimal exitLower = (decimal)(exitChannel.LowerBand ?? 0);

            // 1. 检查止损
            decimal lowestStop = state.Units.Min(u => u.StopPrice);
            if (q.Low <= lowestStop)
            {
                // 触发止损，平掉所有仓位
                CloseAllPositions(tu, period, q, state, sendMode, false);
                return;
            }

            // 2. 检查出场信号（跌破出场通道下轨）
            if (q.Low < exitLower)
            {
                CloseAllPositions(tu, period, q, state, sendMode, true);
                return;
            }

            // 3. 检查加仓信号
            if (enablePyramiding == 1 && state.Units.Count < maxUnits)
            {
                decimal lastEntryPrice = state.Units.Last().EntryPrice;
                decimal pyramidThreshold = lastEntryPrice + (decimal)pyramidingATR * state.LastATR;

                if (q.High >= pyramidThreshold)
                {
                    decimal entryPrice = pyramidThreshold;
                    decimal stopPrice = entryPrice - (decimal)atrStopMultiplier * atr;

                    state.Units.Add(new PositionUnit
                    {
                        EntryPrice = entryPrice,
                        Num = unitSize,
                        StopPrice = stopPrice,
                        EntryTime = q.Date
                    });

                    Trade(tu.MktSymbol, OrderType.BUY, entryPrice, unitSize, period, sendMode);

                    // 更新所有单位的止损价（统一止损）
                    UpdateAllStopPrices(state, stopPrice, true);
                }
            }

            // 绘制当前止损线
            if (state.Units.Count > 0)
            {
                Plot("main", "stopLoss", PlotType.LINE, (double)state.Units.Max(u => u.StopPrice));
            }
        }

        /// <summary>
        /// 处理空头持仓
        /// </summary>
        private void HandleShortPosition(TableUnit tu, Period period, SkQuote q, TurtleState state,
            decimal unitSize, decimal atr, DonchianResult entryChannel, DonchianResult exitChannel,
            int sendMode, int enablePyramiding, double pyramidingATR, int maxUnits, double atrStopMultiplier)
        {
            decimal exitUpper = (decimal)(exitChannel.UpperBand ?? 0);

            // 1. 检查止损
            decimal highestStop = state.Units.Max(u => u.StopPrice);
            if (q.High >= highestStop)
            {
                // 触发止损，平掉所有仓位
                CloseAllPositions(tu, period, q, state, sendMode, false);
                return;
            }

            // 2. 检查出场信号（突破出场通道上轨）
            if (q.High > exitUpper)
            {
                CloseAllPositions(tu, period, q, state, sendMode, true);
                return;
            }

            // 3. 检查加仓信号
            if (enablePyramiding == 1 && state.Units.Count < maxUnits)
            {
                decimal lastEntryPrice = state.Units.Last().EntryPrice;
                decimal pyramidThreshold = lastEntryPrice - (decimal)pyramidingATR * state.LastATR;

                if (q.Low <= pyramidThreshold)
                {
                    decimal entryPrice = pyramidThreshold;
                    decimal stopPrice = entryPrice + (decimal)atrStopMultiplier * atr;

                    state.Units.Add(new PositionUnit
                    {
                        EntryPrice = entryPrice,
                        Num = unitSize,
                        StopPrice = stopPrice,
                        EntryTime = q.Date
                    });

                    Trade(tu.MktSymbol, OrderType.SELL, entryPrice, unitSize, period, sendMode);

                    // 更新所有单位的止损价（统一止损）
                    UpdateAllStopPrices(state, stopPrice, false);
                }
            }

            // 绘制当前止损线
            if (state.Units.Count > 0)
            {
                Plot("main", "stopLoss", PlotType.LINE, (double)state.Units.Min(u => u.StopPrice));
            }
        }

        /// <summary>
        /// 更新所有单位的止损价
        /// </summary>
        private void UpdateAllStopPrices(TurtleState state, decimal newStopPrice, bool isLong)
        {
            foreach (var unit in state.Units)
            {
                if (isLong)
                {
                    // 多头：止损价只能上移
                    if (newStopPrice > unit.StopPrice)
                    {
                        unit.StopPrice = newStopPrice;
                    }
                }
                else
                {
                    // 空头：止损价只能下移
                    if (newStopPrice < unit.StopPrice)
                    {
                        unit.StopPrice = newStopPrice;
                    }
                }
            }
        }

        /// <summary>
        /// 平掉所有仓位
        /// </summary>
        private void CloseAllPositions(TableUnit tu, Period period, SkQuote q, TurtleState state,
            int sendMode, bool isNormalExit)
        {
            decimal totalNum = state.TotalNum;
            decimal avgEntry = state.AvgEntryPrice;
            int direction = state.Direction;

            if (direction == 1)
            {
                // 平多仓
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, totalNum, period, sendMode);

                // 判断盈亏
                state.LastTradeWin = q.Close > avgEntry;
            }
            else if (direction == -1)
            {
                // 平空仓
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, totalNum, period, sendMode);

                // 判断盈亏
                state.LastTradeWin = q.Close < avgEntry;
            }

            // 系统1过滤规则：如果本次交易盈利，下次跳过信号
            if (state.LastTradeWin && isNormalExit)
            {
                state.SkipNextSignal = true;
            }
            else
            {
                state.SkipNextSignal = false;
            }

            // 重置状态
            state.Direction = 0;
            state.Units.Clear();
            state.LastATR = 0;
            state.LastBreakoutPrice = 0;
        }

        #endregion
    }
}
