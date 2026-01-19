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
    /// 标准SMA双均线交叉交易策略
    /// 策略逻辑：
    /// - 快线上穿慢线时做多（金叉）
    /// - 快线下穿慢线时做空（死叉）
    /// - 支持趋势过滤：使用第三条长周期均线判断主趋势方向
    /// - 支持价格确认：收盘价需在均线同侧确认信号
    /// </summary>
    public class SMA : StgBase
    {
        public SMA()
        {
        }

        public SMA(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // 均线参数
            sd.ArgDic["fastPeriod"] = 10;            // 快线周期
            sd.ArgDic["slowPeriod"] = 30;            // 慢线周期
            sd.ArgDic["trendPeriod"] = 60;           // 趋势线周期（用于趋势过滤）

            // 交易控制
            sd.ArgDic["mode"] = 0;                   // 交易模式: 0双向 1仅多 2仅空
            sd.ArgDic["sendMode"] = 0;              // 发单模式: 0立即 1下个开盘
            sd.ArgDic["useTrendFilter"] = 0;        // 是否启用趋势过滤: 0关闭 1开启
            sd.ArgDic["usePriceConfirm"] = 0;       // 是否启用价格确认: 0关闭 1开启

            // 手数控制
            sd.ArgDic["lotsMode"] = 0;              // 0固定手数 1固定金额
            sd.ArgDic["lots"] = 1.0m;               // 固定手数
            sd.ArgDic["money"] = 100000m;           // 固定金额

            // 参数说明
            sd.ArgDescDic["fastPeriod"] = new ArgDesc() { Text = "快线周期", Explain = "短周期SMA，用于捕捉短期趋势" };
            sd.ArgDescDic["slowPeriod"] = new ArgDesc() { Text = "慢线周期", Explain = "长周期SMA，用于确认趋势方向" };
            sd.ArgDescDic["trendPeriod"] = new ArgDesc() { Text = "趋势线周期", Explain = "超长周期SMA，用于过滤主趋势" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0 双向交易 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即发单 1 下个开盘发单" };
            sd.ArgDescDic["useTrendFilter"] = new ArgDesc() { Text = "趋势过滤", Explain = "0 关闭 1 开启（仅在主趋势方向交易）" };
            sd.ArgDescDic["usePriceConfirm"] = new ArgDesc() { Text = "价格确认", Explain = "0 关闭 1 开启（收盘价需确认信号）" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数数量" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额数量" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 0;

            // 均线颜色配置
            sd.ColorDic["main-fast"] = "#FF5722";        // 快线橙红色
            sd.ColorDic["main-slow"] = "#00BCD4";        // 慢线青色
            sd.ColorDic["main-trend"] = "#FF9800";       // 趋势线橙色

            return sd;
        }

        private class State
        {
            public int Position { get; set; }       // 0:空仓 1:多头 2:空头
            public decimal HoldNum { get; set; }    // 持仓数量
            public decimal EntryPrice { get; set; } // 入场价格
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int fastPeriod = (int)ArgDic["fastPeriod"];
            int slowPeriod = (int)ArgDic["slowPeriod"];
            int trendPeriod = (int)ArgDic["trendPeriod"];
            int useTrendFilter = (int)ArgDic["useTrendFilter"];
            int usePriceConfirm = (int)ArgDic["usePriceConfirm"];

            // 确保有足够的数据计算所有均线
            int requiredBars = Math.Max(Math.Max(fastPeriod, slowPeriod), useTrendFilter == 1 ? trendPeriod : 0) + 2;
            if (tu.QuoteList.Count < requiredBars) return;

            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];

            var q = tu.QuoteList.Last();

            // 计算快线SMA
            var fastSmaList = tu.QuoteList.GetSma(fastPeriod).ToList();
            var fastCur = fastSmaList[fastSmaList.Count - 1];
            var fastPrev = fastSmaList[fastSmaList.Count - 2];

            // 计算慢线SMA
            var slowSmaList = tu.QuoteList.GetSma(slowPeriod).ToList();
            var slowCur = slowSmaList[slowSmaList.Count - 1];
            var slowPrev = slowSmaList[slowSmaList.Count - 2];

            if (fastCur.Sma == null || fastPrev.Sma == null ||
                slowCur.Sma == null || slowPrev.Sma == null) return;

            double fastVal = fastCur.Sma.Value;
            double fastValPrev = fastPrev.Sma.Value;
            double slowVal = slowCur.Sma.Value;
            double slowValPrev = slowPrev.Sma.Value;

            // 绘制均线
            Plot("main", "fast", PlotType.LINE, fastVal);
            Plot("main", "slow", PlotType.LINE, slowVal);

            // 趋势过滤判断
            bool trendUp = true;
            bool trendDown = true;
            if (useTrendFilter == 1)
            {
                var trendSmaList = tu.QuoteList.GetSma(trendPeriod).ToList();
                var trendCur = trendSmaList[trendSmaList.Count - 1];

                if (trendCur.Sma == null) return;

                double trendVal = trendCur.Sma.Value;
                Plot("main", "trend", PlotType.LINE, trendVal);

                // 价格在趋势线上方才允许做多
                trendUp = (double)q.Close > trendVal;
                // 价格在趋势线下方才允许做空
                trendDown = (double)q.Close < trendVal;
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
            State s;
            var stateKey = tu.GetStateKey();
            if (!_stateDic.TryGetValue(stateKey, out s))
            {
                s = new State();
                _stateDic[stateKey] = s;
            }

            // 信号判断：金叉与死叉
            bool goldenCross = fastValPrev <= slowValPrev && fastVal > slowVal;  // 金叉：快线上穿慢线
            bool deathCross = fastValPrev >= slowValPrev && fastVal < slowVal;   // 死叉：快线下穿慢线

            // 价格确认
            if (usePriceConfirm == 1)
            {
                // 金叉需要收盘价在快线上方确认
                goldenCross = goldenCross && (double)q.Close > fastVal;
                // 死叉需要收盘价在快线下方确认
                deathCross = deathCross && (double)q.Close < fastVal;
            }

            // 交易逻辑
            if (s.Position == 0)
            {
                // 空仓：寻找入场机会
                if (goldenCross && mode != 2 && trendUp)
                {
                    // 金叉做多
                    s.Position = 1;
                    s.HoldNum = num;
                    s.EntryPrice = q.Close;
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                }
                else if (deathCross && mode != 1 && trendDown)
                {
                    // 死叉做空
                    s.Position = 2;
                    s.HoldNum = num;
                    s.EntryPrice = q.Close;
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                }
            }
            else if (s.Position == 1)
            {
                // 多头持仓：检查平仓信号
                if (deathCross)
                {
                    // 死叉平多
                    var closeNum = s.HoldNum;
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, closeNum, period, sendMode);

                    // 判断是否反手做空
                    if (mode != 1 && trendDown)
                    {
                        s.Position = 2;
                        s.HoldNum = num;
                        s.EntryPrice = q.Close;
                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                    }
                    else
                    {
                        s.Position = 0;
                        s.HoldNum = 0;
                        s.EntryPrice = 0;
                    }
                }
            }
            else if (s.Position == 2)
            {
                // 空头持仓：检查平仓信号
                if (goldenCross)
                {
                    // 金叉平空
                    var closeNum = s.HoldNum;
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, closeNum, period, sendMode);

                    // 判断是否反手做多
                    if (mode != 2 && trendUp)
                    {
                        s.Position = 1;
                        s.HoldNum = num;
                        s.EntryPrice = q.Close;
                        Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                    }
                    else
                    {
                        s.Position = 0;
                        s.HoldNum = 0;
                        s.EntryPrice = 0;
                    }
                }
            }
        }
    }
}
