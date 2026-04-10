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
    /// 标准KDJ交易策略
    /// 策略逻辑：
    /// - K线上穿D线且J值从超卖区回升时做多
    /// - K线下穿D线且J值从超买区回落时做空
    /// - 支持金叉死叉、超买超卖区域过滤
    /// </summary>
    public class KDJ : StgBase
    {
        public KDJ()
        {
        }

        public KDJ(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            
            // KDJ参数
            sd.ArgDic["kPeriod"] = 9;            // K线周期(RSV周期)
            sd.ArgDic["dPeriod"] = 3;            // D线平滑周期
            sd.ArgDic["jMultiplier"] = 3;        // J值乘数
            sd.ArgDic["overbought"] = 80;        // 超买线
            sd.ArgDic["oversold"] = 20;          // 超卖线
            sd.ArgDic["mode"] = 0;               // 交易模式
            sd.ArgDic["sendMode"] = 0;           // 发单模式
            sd.ArgDic["signalMode"] = 0;         // 信号模式
            sd.ArgDic["stopLoss"] = 5.0m;          // 止损百分比

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // 参数说明
            sd.ArgDescDic["kPeriod"] = new ArgDesc() { Text = "K周期", Explain = "RSV计算周期，通常为9" };
            sd.ArgDescDic["dPeriod"] = new ArgDesc() { Text = "D周期", Explain = "D线平滑周期，通常为3" };
            sd.ArgDescDic["jMultiplier"] = new ArgDesc() { Text = "J乘数", Explain = "J值计算乘数，J=3K-2D" };
            sd.ArgDescDic["overbought"] = new ArgDesc() { Text = "超买线", Explain = "超买区域阈值" };
            sd.ArgDescDic["oversold"] = new ArgDesc() { Text = "超卖线", Explain = "超卖区域阈值" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
            sd.ArgDescDic["signalMode"] = new ArgDesc() { Text = "信号模式", Explain = "0 金叉死叉 1 超买超卖区金叉死叉 2 J值极值反转" };
            sd.ArgDescDic["stopLoss"] = new ArgDesc() { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 1;
            
            // KDJ颜色配置
            sd.ColorDic["sub0-K"] = "#F6465D";
            sd.ColorDic["sub0-D"] = "#F0B90B";
            sd.ColorDic["sub0-J"] = "#0ECB81";

            // 中值线配置
            sd.MidValDic["sub0"] = 50;

            return sd;
        }

        private class State
        {
            public int Status { get; set; }     // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }    // 持仓数量
            public decimal EntryPrice { get; set; } // 入场价格
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            
            if (!isFinal) return;
            
            int kPeriod = Convert.ToInt32(ArgDic["kPeriod"]);
            int dPeriod = Convert.ToInt32(ArgDic["dPeriod"]);
            
            if (tu.QuoteList.Count < kPeriod + dPeriod + 1) return;

            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            int signalMode = Convert.ToInt32(ArgDic["signalMode"]);
            int overbought = Convert.ToInt32(ArgDic["overbought"]);
            int oversold = Convert.ToInt32(ArgDic["oversold"]);

            var q = tu.QuoteList.Last();
            
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

            // 绘制KDJ指标
            Plot("sub0", "K", PlotType.LINE, k1);
            Plot("sub0", "D", PlotType.LINE, d1);
            Plot("sub0", "J", PlotType.LINE, j1);

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

            // 信号判断
            bool buySignal = false;
            bool sellSignal = false;

            switch (signalMode)
            {
                case 0:
                    // 模式0：标准金叉死叉
                    // K上穿D做多
                    buySignal = k2 <= d2 && k1 > d1;
                    // K下穿D做空
                    sellSignal = k2 >= d2 && k1 < d1;
                    break;

                case 1:
                    // 模式1：超买超卖区金叉死叉
                    // 超卖区K上穿D做多
                    buySignal = k2 <= d2 && k1 > d1 && k1 < oversold;
                    // 超买区K下穿D做空
                    sellSignal = k2 >= d2 && k1 < d1 && k1 > overbought;
                    break;

                case 2:
                    // 模式2：J值极值反转
                    // J值从超卖区(<0)回升做多
                    buySignal = j2 < 0 && j1 >= 0;
                    // J值从超买区(>100)回落做空
                    sellSignal = j2 > 100 && j1 <= 100;
                    break;
            }

            // 交易逻辑
            if (s.Status == 0)
            {
                // 空仓状态：寻找入场信号
                if (buySignal && mode != 2)
                {
                    s.Status = 1;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                }
                else if (sellSignal && mode != 1)
                {
                    s.Status = 2;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                }
            }
            else if (s.Status == 1)
            {
                // 止损检查
                var stopLoss = Convert.ToDecimal(ArgDic["stopLoss"]);
                if (stopLoss > 0 && s.EntryPrice > 0 && q.Close < s.EntryPrice * (1 - stopLoss / 100m))
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
                    s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                    return;
                }

                // 多头持仓：死叉平仓
                bool exitSignal = false;
                
                switch (signalMode)
                {
                    case 0:
                    case 1:
                        // K下穿D平仓
                        exitSignal = k2 >= d2 && k1 < d1;
                        break;
                    case 2:
                        // J值进入超买区(>100)平仓
                        exitSignal = j1 > 100;
                        break;
                }

                if (exitSignal)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做空
                    if (sellSignal && mode != 1)
                    {
                        s.Status = 2;
                        s.Num = num;
                        s.EntryPrice = q.Close;
                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
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
                var stopLoss2 = Convert.ToDecimal(ArgDic["stopLoss"]);
                if (stopLoss2 > 0 && s.EntryPrice > 0 && q.Close > s.EntryPrice * (1 + stopLoss2 / 100m))
                {
                    var oriNum2 = s.Num;
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum2, period, sendMode);
                    s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                    return;
                }

                // 空头持仓：金叉平仓
                bool exitSignal = false;
                
                switch (signalMode)
                {
                    case 0:
                    case 1:
                        // K上穿D平仓
                        exitSignal = k2 <= d2 && k1 > d1;
                        break;
                    case 2:
                        // J值进入超卖区(<0)平仓
                        exitSignal = j1 < 0;
                        break;
                }

                if (exitSignal)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做多
                    if (buySignal && mode != 2)
                    {
                        s.Status = 1;
                        s.Num = num;
                        s.EntryPrice = q.Close;
                        Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
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
