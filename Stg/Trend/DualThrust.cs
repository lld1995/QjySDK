using Common;
using Model;
using stgInterface;
using System;
using System.Linq;
using static Model.EnumDef;
using System.Collections.Generic;
using Skender.Stock.Indicators;

namespace QjySDK.Stg
{
    /// <summary>
    /// Dual Thrust 标准日内突破策略
    /// 
    /// 策略原理：
    /// Dual Thrust是Michael Chalek开发的经典日内突破系统。
    /// 使用前N日的价格数据计算动态突破区间，当日价格突破区间时入场。
    /// 
    /// Range计算：
    /// - HH = N日最高价的最大值
    /// - HC = N日收盘价的最大值
    /// - LC = N日收盘价的最小值
    /// - LL = N日最低价的最小值
    /// - Range = Max(HH - LC, HC - LL)
    /// 
    /// 通道计算：
    /// - 上轨 = 当日开盘价 + K1 × Range
    /// - 下轨 = 当日开盘价 - K2 × Range
    /// 
    /// 入场规则：
    /// - 价格突破上轨 -> 做多
    /// - 价格突破下轨 -> 做空
    /// 
    /// 出场规则：
    /// - 收盘前平仓（日内策略）
    /// - 或反向信号平仓
    /// - 可选：固定止损/止盈
    /// </summary>
    public class DualThrust : StgBase
    {
        public DualThrust()
        {
        }

        public DualThrust(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // 核心参数
            sd.ArgDic["lookbackDays"] = 4;        // 回溯天数N
            sd.ArgDic["k1"] = 0.5;                // 上轨系数K1
            sd.ArgDic["k2"] = 0.5;                // 下轨系数K2

            // 交易模式
            sd.ArgDic["mode"] = 0;                // 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;            // 发单模式
            sd.ArgDic["exitMode"] = 0;            // 0:收盘平仓 1:反向信号平仓 2:持仓过夜

            // 止损止盈参数
            sd.ArgDic["useStopLoss"] = 0;         // 是否使用止损 0:否 1:是
            sd.ArgDic["stopLossRatio"] = 0.05;    // 止损比例(相对入场价)
            sd.ArgDic["useTakeProfit"] = 0;       // 是否使用止盈 0:否 1:是
            sd.ArgDic["takeProfitRatio"] = 0.10;  // 止盈比例(相对入场价)

            // 时间过滤
            sd.ArgDic["startHour"] = 9;           // 开始交易时间(小时)
            sd.ArgDic["startMinute"] = 30;        // 开始交易时间(分钟)
            sd.ArgDic["endHour"] = 14;            // 停止开仓时间(小时)
            sd.ArgDic["endMinute"] = 45;          // 停止开仓时间(分钟)
            sd.ArgDic["closeHour"] = 14;          // 收盘平仓时间(小时)
            sd.ArgDic["closeMinute"] = 55;        // 收盘平仓时间(分钟)

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;            // 0:固定手数 1:固定金额
            sd.ArgDic["lots"] = 1.0m;             // 固定手数
            sd.ArgDic["money"] = 10000m;         // 固定金额

            // 参数说明
            sd.ArgDescDic["lookbackDays"] = new ArgDesc() { Text = "回溯天数", Explain = "计算Range使用的历史天数N", Type = "number" };
            sd.ArgDescDic["k1"] = new ArgDesc() { Text = "上轨系数K1", Explain = "上轨 = 开盘价 + K1 × Range", Type = "number" };
            sd.ArgDescDic["k2"] = new ArgDesc() { Text = "下轨系数K2", Explain = "下轨 = 开盘价 - K2 × Range", Type = "number" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即发单|1:下个开盘发单", Type = "select" };
            sd.ArgDescDic["exitMode"] = new ArgDesc() { Text = "平仓模式", Explain = "平仓触发条件", Options = "0:收盘平仓|1:反向信号平仓|2:持仓过夜", Type = "select" };
            sd.ArgDescDic["useStopLoss"] = new ArgDesc() { Text = "使用止损", Explain = "触及止损价自动平仓", Options = "0:不使用|1:使用固定比例止损", Type = "bool" };
            sd.ArgDescDic["stopLossRatio"] = new ArgDesc() { Text = "止损比例", Explain = "相对入场价的止损比例", Type = "number" };
            sd.ArgDescDic["useTakeProfit"] = new ArgDesc() { Text = "使用止盈", Explain = "触及止盈价自动平仓", Options = "0:不使用|1:使用固定比例止盈", Type = "bool" };
            sd.ArgDescDic["takeProfitRatio"] = new ArgDesc() { Text = "止盈比例", Explain = "相对入场价的止盈比例", Type = "number" };
            sd.ArgDescDic["startHour"] = new ArgDesc() { Text = "开始时间(时)", Explain = "允许开仓的开始小时", Type = "number" };
            sd.ArgDescDic["startMinute"] = new ArgDesc() { Text = "开始时间(分)", Explain = "允许开仓的开始分钟", Type = "number" };
            sd.ArgDescDic["endHour"] = new ArgDesc() { Text = "停止时间(时)", Explain = "停止开仓的小时", Type = "number" };
            sd.ArgDescDic["endMinute"] = new ArgDesc() { Text = "停止时间(分)", Explain = "停止开仓的分钟", Type = "number" };
            sd.ArgDescDic["closeHour"] = new ArgDesc() { Text = "平仓时间(时)", Explain = "收盘平仓的小时", Type = "number" };
            sd.ArgDescDic["closeMinute"] = new ArgDesc() { Text = "平仓时间(分)", Explain = "收盘平仓的分钟", Type = "number" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "固定手数", Explain = "每次交易的固定手数", Type = "number" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "固定金额", Explain = "每次交易的固定金额", Type = "number" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 0;

            // 通道颜色配置
            sd.ColorDic["main-upper"] = "#FF5722";   // 上轨橙红色
            sd.ColorDic["main-lower"] = "#2196F3";   // 下轨蓝色
            sd.ColorDic["main-open"] = "#FF9800";    // 开盘价橙色

            return sd;
        }

        /// <summary>
        /// 持仓状态
        /// </summary>
        private class State
        {
            public int Status { get; set; }           // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }          // 持仓数量
            public decimal EntryPrice { get; set; }   // 入场价格
            public DateTime EntryTime { get; set; }   // 入场时间
            public int BlockedDir { get; set; }       // 止损封锁方向 0:无 1:禁开多 2:禁开空
        }

        /// <summary>
        /// 每日通道数据
        /// </summary>
        private class DailyChannel
        {
            public DateTime Date { get; set; }        // 日期
            public decimal OpenPrice { get; set; }    // 当日开盘价
            public decimal UpperBand { get; set; }    // 上轨
            public decimal LowerBand { get; set; }    // 下轨
            public decimal Range { get; set; }        // Range值
            public bool IsValid { get; set; }         // 是否有效
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();
        private Dictionary<string, DailyChannel> _channelDic = new Dictionary<string, DailyChannel>();
        private Dictionary<string, List<SkQuote>> _dailyQuoteDic = new Dictionary<string, List<SkQuote>>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int lookbackDays = Convert.ToInt32(ArgDic["lookbackDays"]);
            double k1 = Convert.ToDouble(ArgDic["k1"]);
            double k2 = Convert.ToDouble(ArgDic["k2"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            int exitMode = Convert.ToInt32(ArgDic["exitMode"]);
            int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
            double stopLossRatio = Convert.ToDouble(ArgDic["stopLossRatio"]);
            int useTakeProfit = Convert.ToInt32(ArgDic["useTakeProfit"]);
            double takeProfitRatio = Convert.ToDouble(ArgDic["takeProfitRatio"]);
            int startHour = Convert.ToInt32(ArgDic["startHour"]);
            int startMinute = Convert.ToInt32(ArgDic["startMinute"]);
            int endHour = Convert.ToInt32(ArgDic["endHour"]);
            int endMinute = Convert.ToInt32(ArgDic["endMinute"]);
            int closeHour = Convert.ToInt32(ArgDic["closeHour"]);
            int closeMinute = Convert.ToInt32(ArgDic["closeMinute"]);

            // 确保有足够的数据
            if (tu.QuoteList.Count < lookbackDays + 1) return;

            var q = tu.QuoteList.Last();
            string stateKey = tu.GetStateKey();

            // 更新每日K线数据并计算通道
            UpdateDailyData(stateKey, tu.QuoteList, lookbackDays, k1, k2);

            // 获取当日通道
            DailyChannel channel = GetDailyChannel(stateKey, q.Date.Date);
            if (channel == null || !channel.IsValid) return;

            // 绘制通道
            Plot("main", "upper", PlotType.LINE, (double)channel.UpperBand);
            Plot("main", "lower", PlotType.LINE, (double)channel.LowerBand);
            Plot("main", "open", PlotType.LINE, (double)channel.OpenPrice);

            // 计算手数
            decimal num = CalculateLots(tu, q);
            if (num <= 0) return;

            // 获取或创建状态
            State s = GetOrCreateState(stateKey);

            // 时间检查
            int currentTime = q.Date.Hour * 100 + q.Date.Minute;
            int startTime = startHour * 100 + startMinute;
            int endTime = endHour * 100 + endMinute;
            int closeTime = closeHour * 100 + closeMinute;

            bool canOpen = currentTime >= startTime && currentTime <= endTime;
            bool shouldClose = exitMode == 0 && currentTime >= closeTime;

            // 执行交易逻辑
            ExecuteTradeLogic(tu, period, q, s, num, channel,
                mode, sendMode, exitMode, canOpen, shouldClose,
                useStopLoss, stopLossRatio, useTakeProfit, takeProfitRatio);
        }

        /// <summary>
        /// 更新每日数据并计算通道
        /// </summary>
        private void UpdateDailyData(string stateKey, List<SkQuote> quoteList, int lookbackDays, double k1, double k2)
        {
            // 获取或创建日线数据列表
            if (!_dailyQuoteDic.ContainsKey(stateKey))
            {
                _dailyQuoteDic[stateKey] = new List<SkQuote>();
            }

            var dailyQuotes = _dailyQuoteDic[stateKey];
            var currentQuote = quoteList.Last();
            var currentDate = currentQuote.Date.Date;

            // 构建日线数据（从分钟/小时K线聚合）
            RebuildDailyQuotes(stateKey, quoteList);

            dailyQuotes = _dailyQuoteDic[stateKey];

            // 如果日线数据不足，无法计算
            if (dailyQuotes.Count < lookbackDays) return;

            // 检查是否需要更新当日通道
            string channelKey = stateKey + "_" + currentDate.ToString("yyyyMMdd");
            if (_channelDic.ContainsKey(channelKey) && _channelDic[channelKey].IsValid)
            {
                return; // 当日通道已计算
            }

            // 获取前N日数据（不包含当日）
            var historyQuotes = dailyQuotes
                .Where(x => x.Date.Date < currentDate)
                .OrderByDescending(x => x.Date)
                .Take(lookbackDays)
                .ToList();

            if (historyQuotes.Count < lookbackDays) return;

            // 计算Range
            decimal HH = historyQuotes.Max(x => x.High);   // N日最高价
            decimal LL = historyQuotes.Min(x => x.Low);    // N日最低价
            decimal HC = historyQuotes.Max(x => x.Close);  // N日最高收盘价
            decimal LC = historyQuotes.Min(x => x.Close);  // N日最低收盘价

            decimal range = Math.Max(HH - LC, HC - LL);

            // 获取当日开盘价
            var todayQuotes = quoteList.Where(x => x.Date.Date == currentDate).OrderBy(x => x.Date).ToList();
            if (todayQuotes.Count == 0) return;

            decimal openPrice = todayQuotes.First().Open;

            // 计算上下轨
            decimal upperBand = openPrice + (decimal)k1 * range;
            decimal lowerBand = openPrice - (decimal)k2 * range;

            // 保存通道数据
            _channelDic[channelKey] = new DailyChannel
            {
                Date = currentDate,
                OpenPrice = openPrice,
                UpperBand = upperBand,
                LowerBand = lowerBand,
                Range = range,
                IsValid = true
            };
        }

        /// <summary>
        /// 从分钟K线重建日线数据
        /// </summary>
        private void RebuildDailyQuotes(string stateKey, List<SkQuote> quoteList)
        {
            var dailyQuotes = new List<SkQuote>();

            var groupedByDate = quoteList.GroupBy(x => x.Date.Date);

            foreach (var group in groupedByDate.OrderBy(x => x.Key))
            {
                var dayQuotes = group.OrderBy(x => x.Date).ToList();
                if (dayQuotes.Count == 0) continue;

                var dailyQuote = new SkQuote
                {
                    Date = group.Key,
                    Open = dayQuotes.First().Open,
                    High = dayQuotes.Max(x => x.High),
                    Low = dayQuotes.Min(x => x.Low),
                    Close = dayQuotes.Last().Close,
                    Volume = dayQuotes.Sum(x => x.Volume)
                };

                dailyQuotes.Add(dailyQuote);
            }

            _dailyQuoteDic[stateKey] = dailyQuotes;
        }

        /// <summary>
        /// 获取指定日期的通道
        /// </summary>
        private DailyChannel GetDailyChannel(string stateKey, DateTime date)
        {
            string channelKey = stateKey + "_" + date.ToString("yyyyMMdd");
            if (_channelDic.ContainsKey(channelKey))
            {
                return _channelDic[channelKey];
            }
            return null;
        }

        /// <summary>
        /// 计算交易手数
        /// </summary>
        private decimal CalculateLots(TableUnit tu, SkQuote q)
        {
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            var num = Convert.ToDecimal(ArgDic["lots"]);

            if (lotsMode == 1)
            {
                var sym = GetSymbol(tu.MktSymbol);
                num = Convert.ToDecimal(ArgDic["money"]) / (q.Close * sym.multiplier * sym.margin_ratio);

                if (sym.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * sym.scale) / (decimal)sym.scale;
                }
                else
                {
                    num = Math.Floor(num);
                }
            }

            return num;
        }

        /// <summary>
        /// 获取或创建持仓状态
        /// </summary>
        private State GetOrCreateState(string stateKey)
        {
            if (!_stateDic.ContainsKey(stateKey))
            {
                _stateDic[stateKey] = new State();
            }
            return _stateDic[stateKey];
        }

        /// <summary>
        /// 执行交易逻辑
        /// </summary>
        private void ExecuteTradeLogic(TableUnit tu, Period period, SkQuote q, State s, decimal num,
            DailyChannel channel, int mode, int sendMode, int exitMode, bool canOpen, bool shouldClose,
            int useStopLoss, double stopLossRatio, int useTakeProfit, double takeProfitRatio)
        {
            // 收盘平仓检查
            if (shouldClose && s.Status != 0)
            {
                ClosePosition(tu, period, q, s, sendMode);
                return;
            }

            if (s.Status == 0)
            {
                // 空仓状态：寻找入场信号
                if (canOpen)
                {
                    HandleEntrySignal(tu, period, q, s, num, channel, mode, sendMode);
                }
            }
            else if (s.Status == 1)
            {
                // 多头持仓
                HandleLongPosition(tu, period, q, s, num, channel, mode, sendMode, exitMode,
                    useStopLoss, stopLossRatio, useTakeProfit, takeProfitRatio, canOpen);
            }
            else if (s.Status == 2)
            {
                // 空头持仓
                HandleShortPosition(tu, period, q, s, num, channel, mode, sendMode, exitMode,
                    useStopLoss, stopLossRatio, useTakeProfit, takeProfitRatio, canOpen);
            }
        }

        /// <summary>
        /// 处理入场信号
        /// </summary>
        private void HandleEntrySignal(TableUnit tu, Period period, SkQuote q, State s, decimal num,
            DailyChannel channel, int mode, int sendMode)
        {
            // 止损后信号重置(re-arm)：价格先回到通道内，才解除同向封锁，再次突破才算新信号
            if (s.BlockedDir == 1 && q.Close <= channel.UpperBand)
                s.BlockedDir = 0;
            else if (s.BlockedDir == 2 && q.Close >= channel.LowerBand)
                s.BlockedDir = 0;

            // 价格突破上轨 -> 做多（止损封锁同方向时不入场）
            if (q.Close > channel.UpperBand && mode != 2 && s.BlockedDir != 1)
            {
                s.Status = 1;
                s.Num = num;
                s.EntryPrice = q.Close;
                s.EntryTime = q.Date;
                Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
            }
            // 价格突破下轨 -> 做空（止损封锁同方向时不入场）
            else if (q.Close < channel.LowerBand && mode != 1 && s.BlockedDir != 2)
            {
                s.Status = 2;
                s.Num = num;
                s.EntryPrice = q.Close;
                s.EntryTime = q.Date;
                Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
            }
        }

        /// <summary>
        /// 处理多头持仓
        /// </summary>
        private void HandleLongPosition(TableUnit tu, Period period, SkQuote q, State s, decimal num,
            DailyChannel channel, int mode, int sendMode, int exitMode,
            int useStopLoss, double stopLossRatio, int useTakeProfit, double takeProfitRatio, bool canOpen)
        {
            bool shouldExit = false;
            bool shouldReverse = false;
            bool stopLossHit = false;

            // 检查止损
            if (useStopLoss == 1)
            {
                decimal stopPrice = s.EntryPrice * (1 - (decimal)stopLossRatio);
                if (q.Close <= stopPrice)
                {
                    shouldExit = true;
                    stopLossHit = true;
                }
            }

            // 检查止盈
            if (!shouldExit && useTakeProfit == 1)
            {
                decimal targetPrice = s.EntryPrice * (1 + (decimal)takeProfitRatio);
                if (q.Close >= targetPrice)
                {
                    shouldExit = true;
                }
            }

            // 检查反向信号（exitMode == 1时）
            if (!shouldExit && exitMode == 1)
            {
                if (q.Close < channel.LowerBand)
                {
                    shouldExit = true;
                    if (mode != 1) // 允许做空
                    {
                        shouldReverse = true;
                    }
                }
            }

            if (shouldExit)
            {
                var oriNum = s.Num;
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                if (shouldReverse && canOpen)
                {
                    // 反手做空
                    s.Status = 2;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    s.EntryTime = q.Date;
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                }
                else
                {
                    // 止损出场时封锁同方向，防止止损后立即同向重开
                    if (stopLossHit) s.BlockedDir = 1;
                    ResetState(s);
                }
            }
        }

        /// <summary>
        /// 处理空头持仓
        /// </summary>
        private void HandleShortPosition(TableUnit tu, Period period, SkQuote q, State s, decimal num,
            DailyChannel channel, int mode, int sendMode, int exitMode,
            int useStopLoss, double stopLossRatio, int useTakeProfit, double takeProfitRatio, bool canOpen)
        {
            bool shouldExit = false;
            bool shouldReverse = false;
            bool stopLossHit = false;

            // 检查止损
            if (useStopLoss == 1)
            {
                decimal stopPrice = s.EntryPrice * (1 + (decimal)stopLossRatio);
                if (q.Close >= stopPrice)
                {
                    shouldExit = true;
                    stopLossHit = true;
                }
            }

            // 检查止盈
            if (!shouldExit && useTakeProfit == 1)
            {
                decimal targetPrice = s.EntryPrice * (1 - (decimal)takeProfitRatio);
                if (q.Close <= targetPrice)
                {
                    shouldExit = true;
                }
            }

            // 检查反向信号（exitMode == 1时）
            if (!shouldExit && exitMode == 1)
            {
                if (q.Close > channel.UpperBand)
                {
                    shouldExit = true;
                    if (mode != 2) // 允许做多
                    {
                        shouldReverse = true;
                    }
                }
            }

            if (shouldExit)
            {
                var oriNum = s.Num;
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                if (shouldReverse && canOpen)
                {
                    // 反手做多
                    s.Status = 1;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    s.EntryTime = q.Date;
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                }
                else
                {
                    // 止损出场时封锁同方向，防止止损后立即同向重开
                    if (stopLossHit) s.BlockedDir = 2;
                    ResetState(s);
                }
            }
        }

        /// <summary>
        /// 平仓
        /// </summary>
        private void ClosePosition(TableUnit tu, Period period, SkQuote q, State s, int sendMode)
        {
            if (s.Status == 1)
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
            }
            else if (s.Status == 2)
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
            }
            ResetState(s);
        }

        /// <summary>
        /// 重置状态
        /// </summary>
        private void ResetState(State s)
        {
            s.Status = 0;
            s.Num = 0;
            s.EntryPrice = 0;
            s.EntryTime = DateTime.MinValue;
        }
    }
}
