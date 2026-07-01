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
    /// MACD+KDJ金叉共振交易系统
    /// 策略逻辑：
    /// - 做多条件：MACD金叉(DIF上穿DEA) + KDJ金叉(K上穿D) 同时满足
    /// - 做空条件：MACD死叉(DIF下穿DEA) + KDJ死叉(K下穿D) 同时满足
    /// - 支持共振窗口期：两个信号在N根K线内先后出现视为共振
    /// - 支持超买超卖过滤：KDJ在超卖区金叉更可靠，超买区死叉更可靠
    /// </summary>
    public class MACD_KDJ_Cross : StgBase
    {
        public MACD_KDJ_Cross()
        {
        }

        public MACD_KDJ_Cross(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // MACD参数
            sd.ArgDic["fastPeriods"] = 12;       // 快线周期
            sd.ArgDic["slowPeriods"] = 26;       // 慢线周期
            sd.ArgDic["signalPeriods"] = 9;      // 信号线周期

            // KDJ参数
            sd.ArgDic["kPeriod"] = 9;            // K线周期(RSV周期)
            sd.ArgDic["dPeriod"] = 3;            // D线平滑周期

            // 共振参数
            sd.ArgDic["resonanceWindow"] = 3;    // 共振窗口期(K线数)
            sd.ArgDic["useZoneFilter"] = 1;      // 是否使用超买超卖区过滤
            sd.ArgDic["overbought"] = 80;        // 超买线
            sd.ArgDic["oversold"] = 20;          // 超卖线
            sd.ArgDic["macdZeroFilter"] = 0;     // MACD零轴过滤 0:不过滤 1:零轴上方做多 2:零轴下方做空

            // 交易参数
            sd.ArgDic["mode"] = 0;               // 交易模式
            sd.ArgDic["sendMode"] = 0;           // 发单模式
            sd.ArgDic["exitMode"] = 0;           // 出场模式

            sd.ArgDic["stopLoss"] = 5.0m;          // 止损百分比

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // 参数说明
            sd.ArgDescDic["fastPeriods"] = new ArgDesc() { Text = "MACD快线", Explain = "MACD快线周期，通常为12", Type = "number" };
            sd.ArgDescDic["slowPeriods"] = new ArgDesc() { Text = "MACD慢线", Explain = "MACD慢线周期，通常为26", Type = "number" };
            sd.ArgDescDic["signalPeriods"] = new ArgDesc() { Text = "MACD信号线", Explain = "MACD信号线周期，通常为9", Type = "number" };
            sd.ArgDescDic["kPeriod"] = new ArgDesc() { Text = "KDJ K周期", Explain = "RSV计算周期，通常为9", Type = "number" };
            sd.ArgDescDic["dPeriod"] = new ArgDesc() { Text = "KDJ D周期", Explain = "D线平滑周期，通常为3", Type = "number" };
            sd.ArgDescDic["resonanceWindow"] = new ArgDesc() { Text = "共振窗口", Explain = "两个信号在N根K线内出现视为共振，0表示必须同时出现", Type = "number" };
            sd.ArgDescDic["useZoneFilter"] = new ArgDesc() { Text = "区域过滤", Explain = "KDJ超买超卖区过滤", Options = "0:不过滤|1:KDJ超买超卖区过滤", Type = "bool" };
            sd.ArgDescDic["overbought"] = new ArgDesc() { Text = "超买线", Explain = "KDJ超买区域阈值", Type = "number" };
            sd.ArgDescDic["oversold"] = new ArgDesc() { Text = "超卖线", Explain = "KDJ超卖区域阈值", Type = "number" };
            sd.ArgDescDic["macdZeroFilter"] = new ArgDesc() { Text = "零轴过滤", Explain = "MACD零轴方向过滤", Options = "0:不过滤|1:零轴上方做多|2:零轴下方做空|3:两者都启用", Type = "select" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDescDic["exitMode"] = new ArgDesc() { Text = "出场模式", Explain = "平仓触发条件", Options = "0:共振反向出场|1:任一死叉/金叉出场|2:MACD反向出场|3:KDJ反向出场", Type = "select" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
			sd.ArgDescDic["stopLoss"] = new ArgDesc() { Text = "止损%", Explain = "固定止损百分比，0为不启用", Type = "number" };

            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // MACD颜色配置
            sd.ColorDic["sub0-histogram"] = "#F6465D;#0ECB81";
            sd.ColorDic["sub0-DIF"] = "#2196F3";
            sd.ColorDic["sub0-DEA"] = "#FF9800";

            // KDJ颜色配置
            sd.ColorDic["sub1-K"] = "#F6465D";
            sd.ColorDic["sub1-D"] = "#F0B90B";
            sd.ColorDic["sub1-J"] = "#0ECB81";

            // 中值线配置
            sd.MidValDic["sub0"] = 0;
            sd.MidValDic["sub1"] = 50;

            return sd;
        }

        private class State
        {
            public int Status { get; set; }              // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }             // 持仓数量
            public decimal EntryPrice { get; set; }
            public int MacdGoldenCrossBar { get; set; }  // MACD金叉发生的K线位置
            public int MacdDeathCrossBar { get; set; }   // MACD死叉发生的K线位置
            public int KdjGoldenCrossBar { get; set; }   // KDJ金叉发生的K线位置
            public int KdjDeathCrossBar { get; set; }    // KDJ死叉发生的K线位置
            public int CurrentBar { get; set; }          // 当前K线计数
            public bool KdjGoldenInZone { get; set; }    // KDJ金叉时是否在超卖区（交叉瞬间快照）
            public bool KdjDeathInZone { get; set; }     // KDJ死叉时是否在超买区
            public bool MacdGoldenAboveZero { get; set; } // MACD金叉时DIF是否在零轴上方
            public bool MacdDeathBelowZero { get; set; } // MACD死叉时DIF是否在零轴下方
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int fastPeriods = Convert.ToInt32(ArgDic["fastPeriods"]);
            int slowPeriods = Convert.ToInt32(ArgDic["slowPeriods"]);
            int signalPeriods = Convert.ToInt32(ArgDic["signalPeriods"]);
            int kPeriod = Convert.ToInt32(ArgDic["kPeriod"]);
            int dPeriod = Convert.ToInt32(ArgDic["dPeriod"]);

            int minBars = Math.Max(slowPeriods + signalPeriods, kPeriod + dPeriod) + 2;
            if (tu.QuoteList.Count < minBars) return;

            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            int exitMode = Convert.ToInt32(ArgDic["exitMode"]);
            int resonanceWindow = Convert.ToInt32(ArgDic["resonanceWindow"]);
            int useZoneFilter = Convert.ToInt32(ArgDic["useZoneFilter"]);
            int overbought = Convert.ToInt32(ArgDic["overbought"]);
            int oversold = Convert.ToInt32(ArgDic["oversold"]);
            int macdZeroFilter = Convert.ToInt32(ArgDic["macdZeroFilter"]);

            var q = tu.QuoteList.Last();

            // 计算MACD指标
            var macd = tu.QuoteList.GetMacd(fastPeriods, slowPeriods, signalPeriods).ToList();
            var macd1 = macd[macd.Count - 1];
            var macd2 = macd[macd.Count - 2];

            if (macd1.Macd == null || macd1.Signal == null) return;
            if (macd2.Macd == null || macd2.Signal == null) return;

            double dif1 = macd1.Macd.Value;
            double dea1 = macd1.Signal.Value;
            double histogram1 = macd1.Histogram.GetValueOrDefault(0);

            double dif2 = macd2.Macd.Value;
            double dea2 = macd2.Signal.Value;

            // 计算KDJ指标
            var stoch = tu.QuoteList.GetStoch(kPeriod, dPeriod, dPeriod).ToList();
            var stoch1 = stoch[stoch.Count - 1];
            var stoch2 = stoch[stoch.Count - 2];

            if (stoch1.K == null || stoch1.D == null) return;
            if (stoch2.K == null || stoch2.D == null) return;

            double k1 = stoch1.K.Value;
            double d1 = stoch1.D.Value;
            double j1 = 3 * k1 - 2 * d1;

            double k2 = stoch2.K.Value;
            double d2 = stoch2.D.Value;
            double j2 = 3 * k2 - 2 * d2;

            // 绘制MACD指标
            Plot("sub0", "histogram", PlotType.RECTANGLE, histogram1);
            Plot("sub0", "DIF", PlotType.LINE, dif1);
            Plot("sub0", "DEA", PlotType.LINE, dea1);

            // 绘制KDJ指标
            Plot("sub1", "K", PlotType.LINE, k1);
            Plot("sub1", "D", PlotType.LINE, d1);
            Plot("sub1", "J", PlotType.LINE, j1);

            // 判断金叉死叉
            bool macdGoldenCross = dif2 <= dea2 && dif1 > dea1;  // MACD金叉
            bool macdDeathCross = dif2 >= dea2 && dif1 < dea1;   // MACD死叉
            bool kdjGoldenCross = k2 <= d2 && k1 > d1;           // KDJ金叉
            bool kdjDeathCross = k2 >= d2 && k1 < d1;            // KDJ死叉

            // 计算手数
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var s2 = GetSymbol(tu.MktSymbol);
                num = (Convert.ToDecimal(ArgDic["money"]) / (q.Close * s2.multiplier * s2.margin_ratio));
                if (s2.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }

            // 获取或创建状态
            var sk = tu.GetStateKey();
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new State()
                {
                    MacdGoldenCrossBar = -999,
                    MacdDeathCrossBar = -999,
                    KdjGoldenCrossBar = -999,
                    KdjDeathCrossBar = -999
                };
            }
            var s = _stateDic[sk];

            s.CurrentBar++;

            // 记录金叉死叉发生的K线位置（同时快照当时的区域/零轴状态）
            if (macdGoldenCross)
            {
                s.MacdGoldenCrossBar = s.CurrentBar;
                s.MacdGoldenAboveZero = dif1 >= 0;
            }
            if (macdDeathCross)
            {
                s.MacdDeathCrossBar = s.CurrentBar;
                s.MacdDeathBelowZero = dif1 <= 0;
            }
            if (kdjGoldenCross)
            {
                s.KdjGoldenCrossBar = s.CurrentBar;
                s.KdjGoldenInZone = k1 <= oversold + 20;
            }
            if (kdjDeathCross)
            {
                s.KdjDeathCrossBar = s.CurrentBar;
                s.KdjDeathInZone = k1 >= overbought - 20;
            }

            // 判断共振信号
            bool buyResonance = false;
            bool sellResonance = false;

            // 检查做多共振：MACD金叉和KDJ金叉在窗口期内
            int macdGoldenAge = s.CurrentBar - s.MacdGoldenCrossBar;
            int kdjGoldenAge = s.CurrentBar - s.KdjGoldenCrossBar;
            if (macdGoldenAge >= 0 && macdGoldenAge <= resonanceWindow &&
                kdjGoldenAge >= 0 && kdjGoldenAge <= resonanceWindow)
            {
                buyResonance = true;

                // 区域过滤：KDJ在超卖区金叉更可靠（用交叉瞬间快照，避免交叉后K值漂移导致滤掉）
                if (useZoneFilter == 1 && !s.KdjGoldenInZone)
                {
                    buyResonance = false;
                }

                // MACD零轴过滤（用交叉瞬间的DIF状态）
                if (macdZeroFilter == 1 || macdZeroFilter == 3)
                {
                    if (!s.MacdGoldenAboveZero) buyResonance = false;
                }
            }

            // 检查做空共振：MACD死叉和KDJ死叉在窗口期内
            int macdDeathAge = s.CurrentBar - s.MacdDeathCrossBar;
            int kdjDeathAge = s.CurrentBar - s.KdjDeathCrossBar;
            if (macdDeathAge >= 0 && macdDeathAge <= resonanceWindow &&
                kdjDeathAge >= 0 && kdjDeathAge <= resonanceWindow)
            {
                sellResonance = true;

                // 区域过滤：KDJ在超买区死叉更可靠（用交叉瞬间快照）
                if (useZoneFilter == 1 && !s.KdjDeathInZone)
                {
                    sellResonance = false;
                }

                // MACD零轴过滤（用交叉瞬间的DIF状态）
                if (macdZeroFilter == 2 || macdZeroFilter == 3)
                {
                    if (!s.MacdDeathBelowZero) sellResonance = false;
                }
            }

            // 出场信号判断
            bool exitLongSignal = false;
            bool exitShortSignal = false;

            switch (exitMode)
            {
                case 0:
                    // 共振反向出场
                    exitLongSignal = sellResonance;
                    exitShortSignal = buyResonance;
                    break;
                case 1:
                    // 任一死叉/金叉出场
                    exitLongSignal = macdDeathCross || kdjDeathCross;
                    exitShortSignal = macdGoldenCross || kdjGoldenCross;
                    break;
                case 2:
                    // MACD反向出场
                    exitLongSignal = macdDeathCross;
                    exitShortSignal = macdGoldenCross;
                    break;
                case 3:
                    // KDJ反向出场
                    exitLongSignal = kdjDeathCross;
                    exitShortSignal = kdjGoldenCross;
                    break;
            }

            // 交易逻辑
            if (s.Status == 0)
            {
                // 空仓状态：寻找共振入场信号
                if (buyResonance && mode != 2)
                {
                    s.Status = 1;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                    // 重置金叉记录，避免重复触发
                    s.MacdGoldenCrossBar = -999;
                    s.KdjGoldenCrossBar = -999;
                }
                else if (sellResonance && mode != 1)
                {
                    s.Status = 2;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                    // 重置死叉记录，避免重复触发
                    s.MacdDeathCrossBar = -999;
                    s.KdjDeathCrossBar = -999;
                }
            }
            else if (s.Status == 1)
            {
                // 止损检查
                var _sl = Convert.ToDecimal(ArgDic["stopLoss"]);
                if (_sl > 0 && s.EntryPrice > 0 && q.Close < s.EntryPrice * (1 - _sl / 100m))
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                    return;
                }

                // 多头持仓：检查出场信号
                if (exitLongSignal)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做空
                    if (sellResonance && mode != 1)
                    {
                        s.Status = 2;
                        s.Num = num;
                        s.EntryPrice = q.Close;
                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        s.MacdDeathCrossBar = -999;
                        s.KdjDeathCrossBar = -999;
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
                var _sl2 = Convert.ToDecimal(ArgDic["stopLoss"]);
                if (_sl2 > 0 && s.EntryPrice > 0 && q.Close > s.EntryPrice * (1 + _sl2 / 100m))
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                    return;
                }

                // 空头持仓：检查出场信号
                if (exitShortSignal)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做多
                    if (buyResonance && mode != 2)
                    {
                        s.Status = 1;
                        s.Num = num;
                        s.EntryPrice = q.Close;
                        Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                        s.MacdGoldenCrossBar = -999;
                        s.KdjGoldenCrossBar = -999;
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
