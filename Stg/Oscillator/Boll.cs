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
    /// 标准布林带交易策略
    /// 策略逻辑：
    /// - 价格突破上轨做多，突破下轨做空
    /// - 价格回归中轨平仓
    /// </summary>
    public class Boll : StgBase
    {
        public Boll()
        {
        }

        public Boll(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            
            // 布林带参数
            sd.ArgDic["period"] = 20;           // 布林带周期
            sd.ArgDic["stdDev"] = 2.0;          // 标准差倍数
            sd.ArgDic["mode"] = 0;              // 交易模式
            sd.ArgDic["sendMode"] = 0;          // 发单模式
            sd.ArgDic["exitMode"] = 0;          // 平仓模式

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // 参数说明
            sd.ArgDescDic["period"] = new ArgDesc() { Text = "布林带周期", Explain = "计算布林带的周期数" };
            sd.ArgDescDic["stdDev"] = new ArgDesc() { Text = "标准差倍数", Explain = "布林带上下轨的标准差倍数" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
            sd.ArgDescDic["exitMode"] = new ArgDesc() { Text = "平仓模式", Explain = "0 回归中轨平仓 1 反向突破平仓" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 0;
            
            // 布林带颜色配置
            sd.ColorDic["main-upper"] = "#FF5722";
            sd.ColorDic["main-middle"] = "#FF9800";
            sd.ColorDic["main-lower"] = "#2196F3";

            return sd;
        }

        private class State
        {
            public int Status { get; set; }     // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }    // 持仓数量
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            
            if (!isFinal) return;
            
            int bollPeriod = (int)ArgDic["period"];
            if (tu.QuoteList.Count < bollPeriod + 1) return;

            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int exitMode = (int)ArgDic["exitMode"];
            double stdDev = Convert.ToDouble(ArgDic["stdDev"]);

            var q = tu.QuoteList.Last();
            var boll = tu.QuoteList.GetBollingerBands(bollPeriod, stdDev).ToList();
            var boll1 = boll[boll.Count - 1];
            var boll2 = boll[boll.Count - 2];

            // 绘制布林带
            Plot("main", "upper", PlotType.LINE, boll1.UpperBand);
            Plot("main", "middle", PlotType.LINE, boll1.Sma);
            Plot("main", "lower", PlotType.LINE, boll1.LowerBand);

            if (boll1.UpperBand == null || boll1.LowerBand == null || boll1.Sma == null) return;
            if (boll2.UpperBand == null || boll2.LowerBand == null || boll2.Sma == null) return;

            // 计算手数
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

            decimal upper = (decimal)boll1.UpperBand.Value;
            decimal middle = (decimal)boll1.Sma.Value;
            decimal lower = (decimal)boll1.LowerBand.Value;
            decimal prevUpper = (decimal)boll2.UpperBand.Value;
            decimal prevMiddle = (decimal)boll2.Sma.Value;
            decimal prevLower = (decimal)boll2.LowerBand.Value;

            var prevClose = tu.QuoteList[tu.QuoteList.Count - 2].Close;

            // 交易逻辑
            if (s.Status == 0)
            {
                // 空仓状态：寻找入场信号
                // 价格从下方突破上轨 -> 做多
                if (prevClose <= prevUpper && q.Close > upper && mode != 2)
                {
                    s.Status = 1;
                    s.Num = num;
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                }
                // 价格从上方突破下轨 -> 做空
                else if (prevClose >= prevLower && q.Close < lower && mode != 1)
                {
                    s.Status = 2;
                    s.Num = num;
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                }
            }
            else if (s.Status == 1)
            {
                // 多头持仓
                bool shouldExit = false;
                
                if (exitMode == 0)
                {
                    // 模式0：价格回归中轨平仓
                    shouldExit = prevClose >= prevMiddle && q.Close < middle;
                }
                else
                {
                    // 模式1：价格跌破下轨平仓
                    shouldExit = prevClose >= prevLower && q.Close < lower;
                }

                if (shouldExit)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做空
                    if (exitMode == 1 && mode != 1)
                    {
                        // 反向突破模式下，跌破下轨直接反手做空
                        s.Status = 2;
                        s.Num = num;
                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                    }
                    else
                    {
                        s.Status = 0;
                        s.Num = 0;
                    }
                }
            }
            else if (s.Status == 2)
            {
                // 空头持仓
                bool shouldExit = false;
                
                if (exitMode == 0)
                {
                    // 模式0：价格回归中轨平仓
                    shouldExit = prevClose <= prevMiddle && q.Close > middle;
                }
                else
                {
                    // 模式1：价格突破上轨平仓
                    shouldExit = prevClose <= prevUpper && q.Close > upper;
                }

                if (shouldExit)
                {
                    var oriNum = s.Num;
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);

                    // 判断是否反手做多
                    if (exitMode == 1 && mode != 2)
                    {
                        // 反向突破模式下，突破上轨直接反手做多
                        s.Status = 1;
                        s.Num = num;
                        Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                    }
                    else
                    {
                        s.Status = 0;
                        s.Num = 0;
                    }
                }
            }
        }
    }
}
