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
    /// 多因子量化策略 (Multi-Factor Quantitative Strategy)
    /// 
    /// 策略核心思想：
    /// 综合运用多个经典量化因子，通过因子加权打分系统生成交易信号。
    /// 每个因子独立计算后标准化，再按权重合成综合得分。
    /// 
    /// 五大核心因子：
    /// 1. 动量因子 (Momentum)：
    ///    - ROC (Rate of Change)：价格变化率
    ///    - RSI (Relative Strength Index)：相对强弱指标
    ///    - 动量越强，做多信号越强
    /// 
    /// 2. 趋势因子 (Trend)：
    ///    - EMA多空排列：快慢均线位置关系
    ///    - ADX趋势强度：趋势明确度
    ///    - 趋势明确时顺势交易
    /// 
    /// 3. 波动率因子 (Volatility)：
    ///    - ATR波动率：市场波动程度
    ///    - 布林带宽度：价格波动范围
    ///    - 低波动时入场，高波动时谨慎
    /// 
    /// 4. 成交量因子 (Volume)：
    ///    - OBV能量潮：量价配合度
    ///    - 成交量比率：当前量与均量对比
    ///    - 量能支撑趋势有效性
    /// 
    /// 5. 均值回归因子 (Mean Reversion)：
    ///    - 价格偏离度：价格与均线偏离程度
    ///    - 布林带位置：价格在布林带中的位置
    ///    - 超买超卖判断
    /// 
    /// 信号生成：
    /// - 综合得分 > 多头阈值：做多
    /// - 综合得分 < 空头阈值：做空
    /// - 得分在阈值之间：观望或平仓
    /// 
    /// 风控机制：
    /// - ATR动态止损止盈
    /// - 因子一致性过滤
    /// - 最大持仓时间限制
    /// </summary>
    public class MultiFactor : StgBase
    {
        public MultiFactor()
        {
        }

        public MultiFactor(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 0;

            // ==================== 动量因子参数 ====================
            sd.ArgDescDic["rocPeriod"] = new ArgDesc { Text = "ROC周期", Explain = "价格变化率计算周期" };
            sd.ArgDic["rocPeriod"] = 12;

            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "相对强弱指标周期" };
            sd.ArgDic["rsiPeriod"] = 14;

            sd.ArgDescDic["momentumWeight"] = new ArgDesc { Text = "动量因子权重", Explain = "动量因子在综合得分中的权重(0-100)" };
            sd.ArgDic["momentumWeight"] = 25.0;

            // ==================== 趋势因子参数 ====================
            sd.ArgDescDic["emaFast"] = new ArgDesc { Text = "快速EMA周期", Explain = "快速指数移动平均周期" };
            sd.ArgDic["emaFast"] = 12;

            sd.ArgDescDic["emaSlow"] = new ArgDesc { Text = "慢速EMA周期", Explain = "慢速指数移动平均周期" };
            sd.ArgDic["emaSlow"] = 26;

            sd.ArgDescDic["adxPeriod"] = new ArgDesc { Text = "ADX周期", Explain = "趋势强度指标周期" };
            sd.ArgDic["adxPeriod"] = 14;

            sd.ArgDescDic["trendWeight"] = new ArgDesc { Text = "趋势因子权重", Explain = "趋势因子在综合得分中的权重(0-100)" };
            sd.ArgDic["trendWeight"] = 25.0;

            // ==================== 波动率因子参数 ====================
            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "平均真实波幅周期" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["bollPeriod"] = new ArgDesc { Text = "布林带周期", Explain = "布林带计算周期" };
            sd.ArgDic["bollPeriod"] = 20;

            sd.ArgDescDic["bollStdDev"] = new ArgDesc { Text = "布林带标准差", Explain = "布林带标准差倍数" };
            sd.ArgDic["bollStdDev"] = 2.0;

            sd.ArgDescDic["volatilityWeight"] = new ArgDesc { Text = "波动率因子权重", Explain = "波动率因子在综合得分中的权重(0-100)" };
            sd.ArgDic["volatilityWeight"] = 15.0;

            // ==================== 成交量因子参数 ====================
            sd.ArgDescDic["volumeMaPeriod"] = new ArgDesc { Text = "成交量均线周期", Explain = "成交量移动平均周期" };
            sd.ArgDic["volumeMaPeriod"] = 20;

            sd.ArgDescDic["volumeWeight"] = new ArgDesc { Text = "成交量因子权重", Explain = "成交量因子在综合得分中的权重(0-100)" };
            sd.ArgDic["volumeWeight"] = 15.0;

            // ==================== 均值回归因子参数 ====================
            sd.ArgDescDic["deviationPeriod"] = new ArgDesc { Text = "偏离度周期", Explain = "计算价格偏离度的均线周期" };
            sd.ArgDic["deviationPeriod"] = 20;

            sd.ArgDescDic["meanReversionWeight"] = new ArgDesc { Text = "均值回归因子权重", Explain = "均值回归因子在综合得分中的权重(0-100)" };
            sd.ArgDic["meanReversionWeight"] = 20.0;

            // ==================== 信号阈值参数 ====================
            sd.ArgDescDic["longThreshold"] = new ArgDesc { Text = "做多阈值", Explain = "综合得分超过此值触发做多(0-100)" };
            sd.ArgDic["longThreshold"] = 60.0;

            sd.ArgDescDic["shortThreshold"] = new ArgDesc { Text = "做空阈值", Explain = "综合得分低于此值触发做空(0-100)" };
            sd.ArgDic["shortThreshold"] = 40.0;

            sd.ArgDescDic["exitThreshold"] = new ArgDesc { Text = "平仓阈值", Explain = "得分回归到此范围内平仓(距离中线)" };
            sd.ArgDic["exitThreshold"] = 10.0;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["stopLossAtr"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离 = ATR × 此倍数" };
            sd.ArgDic["stopLossAtr"] = 2.0;

            sd.ArgDescDic["takeProfitAtr"] = new ArgDesc { Text = "止盈ATR倍数", Explain = "止盈距离 = ATR × 此倍数" };
            sd.ArgDic["takeProfitAtr"] = 3.0;

            sd.ArgDescDic["useTrailingStop"] = new ArgDesc { Text = "启用移动止损", Explain = "1=启用 0=禁用" };
            sd.ArgDic["useTrailingStop"] = 1;

            sd.ArgDescDic["trailingStopAtr"] = new ArgDesc { Text = "移动止损ATR倍数", Explain = "移动止损距离 = ATR × 此倍数" };
            sd.ArgDic["trailingStopAtr"] = 1.5;

            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线数", Explain = "超过此数量强制平仓(0=不限制)" };
            sd.ArgDic["maxHoldBars"] = 50;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易模式", Explain = "0=双向 1=仅做多 2=仅做空" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "0=立即 1=下个开盘" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0=固定手数 1=固定金额" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额" };
            sd.ArgDic["money"] = 10000m;

            sd.ArgDescDic["factorConsistency"] = new ArgDesc { Text = "因子一致性要求", Explain = "至少N个因子同向才入场(1-5)" };
            sd.ArgDic["factorConsistency"] = 3;

            // ==================== 颜色配置 ====================
            // 主图
            sd.ColorDic["main-EMA_Fast"] = "#2196F3";
            sd.ColorDic["main-EMA_Slow"] = "#FF9800";
            sd.ColorDic["main-Boll_Upper"] = "#9C27B0";
            sd.ColorDic["main-Boll_Lower"] = "#9C27B0";
            sd.ColorDic["main-Boll_Mid"] = "#E91E63";
            sd.ColorDic["main-StopLoss"] = "#F44336";
            sd.ColorDic["main-TakeProfit"] = "#4CAF50";

            // 副图0：综合得分
            sd.ColorDic["sub0-Score"] = "#2196F3";
            sd.ColorDic["sub0-LongThreshold"] = "#4CAF50";
            sd.ColorDic["sub0-ShortThreshold"] = "#F44336";

            // 副图1：RSI
            sd.ColorDic["sub1-RSI"] = "#FF9800";

            // 中值线
            sd.MidValDic["sub0"] = 50;
            sd.MidValDic["sub1"] = 50;

            return sd;
        }

        private class FactorScore
        {
            public double Momentum { get; set; }      // 动量因子得分 (0-100)
            public double Trend { get; set; }         // 趋势因子得分 (0-100)
            public double Volatility { get; set; }    // 波动率因子得分 (0-100)
            public double Volume { get; set; }        // 成交量因子得分 (0-100)
            public double MeanReversion { get; set; } // 均值回归因子得分 (0-100)
            public double Composite { get; set; }     // 综合得分 (0-100)

            public int BullishCount => 
                (Momentum > 50 ? 1 : 0) + 
                (Trend > 50 ? 1 : 0) + 
                (Volatility > 50 ? 1 : 0) + 
                (Volume > 50 ? 1 : 0) + 
                (MeanReversion > 50 ? 1 : 0);

            public int BearishCount => 
                (Momentum < 50 ? 1 : 0) + 
                (Trend < 50 ? 1 : 0) + 
                (Volatility < 50 ? 1 : 0) + 
                (Volume < 50 ? 1 : 0) + 
                (MeanReversion < 50 ? 1 : 0);
        }

        private class TradeState
        {
            public int Status { get; set; }           // 0=空仓 1=多头 2=空头
            public decimal Num { get; set; }          // 持仓数量
            public decimal EntryPrice { get; set; }   // 入场价格
            public decimal StopLoss { get; set; }     // 止损价
            public decimal TakeProfit { get; set; }   // 止盈价
            public decimal HighestPrice { get; set; } // 持仓期间最高价
            public decimal LowestPrice { get; set; }  // 持仓期间最低价
            public int HoldBars { get; set; }         // 持仓K线数
            public decimal EntryAtr { get; set; }     // 入场时ATR

            public void Reset()
            {
                Status = 0;
                Num = 0;
                EntryPrice = 0;
                StopLoss = 0;
                TakeProfit = 0;
                HighestPrice = 0;
                LowestPrice = decimal.MaxValue;
                HoldBars = 0;
                EntryAtr = 0;
            }
        }

        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (ArgDic == null) return;

            // 获取参数
            int rocPeriod = Convert.ToInt32(ArgDic["rocPeriod"]);
            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            double momentumWeight = Convert.ToDouble(ArgDic["momentumWeight"]);

            int emaFast = Convert.ToInt32(ArgDic["emaFast"]);
            int emaSlow = Convert.ToInt32(ArgDic["emaSlow"]);
            int adxPeriod = Convert.ToInt32(ArgDic["adxPeriod"]);
            double trendWeight = Convert.ToDouble(ArgDic["trendWeight"]);

            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            int bollPeriod = Convert.ToInt32(ArgDic["bollPeriod"]);
            double bollStdDev = Convert.ToDouble(ArgDic["bollStdDev"]);
            double volatilityWeight = Convert.ToDouble(ArgDic["volatilityWeight"]);

            int volumeMaPeriod = Convert.ToInt32(ArgDic["volumeMaPeriod"]);
            double volumeWeight = Convert.ToDouble(ArgDic["volumeWeight"]);

            int deviationPeriod = Convert.ToInt32(ArgDic["deviationPeriod"]);
            double meanReversionWeight = Convert.ToDouble(ArgDic["meanReversionWeight"]);

            double longThreshold = Convert.ToDouble(ArgDic["longThreshold"]);
            double shortThreshold = Convert.ToDouble(ArgDic["shortThreshold"]);
            double exitThreshold = Convert.ToDouble(ArgDic["exitThreshold"]);

            double stopLossAtr = Convert.ToDouble(ArgDic["stopLossAtr"]);
            double takeProfitAtr = Convert.ToDouble(ArgDic["takeProfitAtr"]);
            int useTrailingStop = Convert.ToInt32(ArgDic["useTrailingStop"]);
            double trailingStopAtr = Convert.ToDouble(ArgDic["trailingStopAtr"]);
            int maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);

            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            int factorConsistency = Convert.ToInt32(ArgDic["factorConsistency"]);

            // 计算最小K线数
            int minBars = Math.Max(Math.Max(Math.Max(emaSlow, bollPeriod), Math.Max(adxPeriod, volumeMaPeriod)), 
                                   Math.Max(rocPeriod, rsiPeriod)) + 10;
            if (tu.QuoteList.Count < minBars) return;

            var quotes = tu.QuoteList;
            var q = quotes.Last();
            decimal currentPrice = q.Close;

            // ==================== 计算各项指标 ====================
            // EMA
            var emaFastList = quotes.GetEma(emaFast).ToList();
            var emaSlowList = quotes.GetEma(emaSlow).ToList();
            double emaFastVal = emaFastList.Last().Ema.GetValueOrDefault(0);
            double emaSlowVal = emaSlowList.Last().Ema.GetValueOrDefault(0);

            // ADX
            var adxList = quotes.GetAdx(adxPeriod).ToList();
            var adxLast = adxList.Last();
            double adxVal = adxLast.Adx.GetValueOrDefault(0);
            double pdi = adxLast.Pdi.GetValueOrDefault(0);
            double mdi = adxLast.Mdi.GetValueOrDefault(0);

            // RSI
            var rsiList = quotes.GetRsi(rsiPeriod).ToList();
            double rsiVal = rsiList.Last().Rsi.GetValueOrDefault(50);

            // ROC
            var rocList = quotes.GetRoc(rocPeriod).ToList();
            double rocVal = rocList.Last().Roc.GetValueOrDefault(0);

            // ATR
            var atrList = quotes.GetAtr(atrPeriod).ToList();
            decimal atrVal = (decimal)atrList.Last().Atr.GetValueOrDefault(0);

            // 布林带
            var bollList = quotes.GetBollingerBands(bollPeriod, bollStdDev).ToList();
            var bollLast = bollList.Last();
            double bollUpper = bollLast.UpperBand.GetValueOrDefault(0);
            double bollLower = bollLast.LowerBand.GetValueOrDefault(0);
            double bollMid = bollLast.Sma.GetValueOrDefault(0);
            double bollWidth = bollUpper - bollLower;

            // OBV
            var obvList = quotes.GetObv().ToList();
            double obvVal = obvList.Last().Obv;
            // 计算OBV均线
            double obvMa = 0;
            if (obvList.Count >= volumeMaPeriod)
            {
                for (int i = 0; i < volumeMaPeriod; i++)
                {
                    obvMa += obvList[obvList.Count - 1 - i].Obv;
                }
                obvMa /= volumeMaPeriod;
            }

            // 成交量比率
            double volumeRatio = 1.0;
            if (quotes.Count >= volumeMaPeriod)
            {
                decimal avgVolume = 0;
                for (int i = 0; i < volumeMaPeriod; i++)
                {
                    avgVolume += quotes[quotes.Count - 1 - i].Volume;
                }
                avgVolume /= volumeMaPeriod;
                if (avgVolume > 0)
                {
                    volumeRatio = (double)(q.Volume / avgVolume);
                }
            }

            // 价格偏离度
            var deviationMaList = quotes.GetSma(deviationPeriod).ToList();
            double deviationMa = deviationMaList.Last().Sma.GetValueOrDefault((double)currentPrice);
            double priceDeviation = deviationMa > 0 ? ((double)currentPrice - deviationMa) / deviationMa * 100 : 0;

            // ==================== 计算因子得分 ====================
            var factorScore = new FactorScore();

            // 1. 动量因子得分 (ROC + RSI)
            // ROC标准化：假设ROC在-10%到+10%之间映射到0-100
            double rocScore = Math.Max(0, Math.Min(100, (rocVal + 10) / 20 * 100));
            // RSI本身就是0-100
            double rsiScore = rsiVal;
            factorScore.Momentum = (rocScore * 0.5 + rsiScore * 0.5);

            // 2. 趋势因子得分
            // EMA排列：快线在慢线上方得分高
            double emaDiff = (emaFastVal - emaSlowVal) / emaSlowVal * 100;
            double emaScore = Math.Max(0, Math.Min(100, (emaDiff + 5) / 10 * 100));
            // ADX趋势强度：ADX越高趋势越强，配合DI方向
            double adxDirectionScore = 50;
            if (adxVal > 20)
            {
                if (pdi > mdi) adxDirectionScore = 50 + Math.Min(50, adxVal);
                else adxDirectionScore = 50 - Math.Min(50, adxVal);
            }
            factorScore.Trend = (emaScore * 0.5 + adxDirectionScore * 0.5);

            // 3. 波动率因子得分
            // 低波动率时得分高（适合入场），高波动率时得分低
            // 使用布林带宽度相对于价格的比例
            double volatilityRatio = bollWidth / bollMid * 100;
            // 假设波动率在1%-10%之间，低波动得高分
            double volScore = Math.Max(0, Math.Min(100, (10 - volatilityRatio) / 9 * 100));
            // 价格在布林带中的位置：中轨附近得分50，上轨附近得分高（做多），下轨附近得分低
            double bollPosition = bollWidth > 0 ? ((double)currentPrice - bollLower) / bollWidth * 100 : 50;
            factorScore.Volatility = (volScore * 0.3 + bollPosition * 0.7);

            // 4. 成交量因子得分
            // OBV在均线上方且成交量放大得分高
            double obvScore = obvMa > 0 ? (obvVal > obvMa ? 70 : 30) : 50;
            double volumeRatioScore = Math.Max(0, Math.Min(100, volumeRatio * 50));
            factorScore.Volume = (obvScore * 0.6 + volumeRatioScore * 0.4);

            // 5. 均值回归因子得分
            // 价格低于均线（负偏离）时做多得分高，高于均线时做空得分高
            // 这里采用逆向思维：偏离越大，回归概率越高
            // 偏离度在-5%到+5%之间映射
            double deviationScore = Math.Max(0, Math.Min(100, (5 - priceDeviation) / 10 * 100));
            factorScore.MeanReversion = deviationScore;

            // ==================== 计算综合得分 ====================
            double totalWeight = momentumWeight + trendWeight + volatilityWeight + volumeWeight + meanReversionWeight;
            if (totalWeight > 0)
            {
                factorScore.Composite = (
                    factorScore.Momentum * momentumWeight +
                    factorScore.Trend * trendWeight +
                    factorScore.Volatility * volatilityWeight +
                    factorScore.Volume * volumeWeight +
                    factorScore.MeanReversion * meanReversionWeight
                ) / totalWeight;
            }
            else
            {
                factorScore.Composite = 50;
            }

            // ==================== 绘制指标 ====================
            Plot("main", "EMA_Fast", PlotType.LINE, emaFastVal);
            Plot("main", "EMA_Slow", PlotType.LINE, emaSlowVal);
            Plot("main", "Boll_Upper", PlotType.LINE, bollUpper);
            Plot("main", "Boll_Lower", PlotType.LINE, bollLower);
            Plot("main", "Boll_Mid", PlotType.LINE, bollMid);

            Plot("sub0", "Score", PlotType.LINE, factorScore.Composite);
            Plot("sub0", "LongThreshold", PlotType.LINE, longThreshold);
            Plot("sub0", "ShortThreshold", PlotType.LINE, shortThreshold);

            Plot("sub1", "RSI", PlotType.LINE, rsiVal);

            // ==================== 获取或创建状态 ====================
            var sk = tu.GetStateKey();
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];

            // 绘制止损止盈线
            if (state.Status != 0)
            {
                Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                Plot("main", "TakeProfit", PlotType.LINE, (double)state.TakeProfit);
            }

            if (!isFinal) return;

            // 更新持仓计数
            if (state.Status != 0)
            {
                state.HoldBars++;
                // 更新最高最低价
                if (q.High > state.HighestPrice) state.HighestPrice = q.High;
                if (q.Low < state.LowestPrice) state.LowestPrice = q.Low;
            }

            // ==================== 计算手数 ====================
            var num = (decimal)ArgDic["lots"];
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
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

            // ==================== 交易逻辑 ====================
            if (state.Status == 0)
            {
                // 空仓：寻找入场信号
                bool longSignal = factorScore.Composite > longThreshold && 
                                  factorScore.BullishCount >= factorConsistency &&
                                  mode != 2;
                bool shortSignal = factorScore.Composite < shortThreshold && 
                                   factorScore.BearishCount >= factorConsistency &&
                                   mode != 1;

                if (longSignal && atrVal > 0)
                {
                    state.Status = 1;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.EntryAtr = atrVal;
                    state.StopLoss = currentPrice - atrVal * (decimal)stopLossAtr;
                    state.TakeProfit = currentPrice + atrVal * (decimal)takeProfitAtr;
                    state.HighestPrice = q.High;
                    state.LowestPrice = q.Low;
                    state.HoldBars = 0;
                    Trade(tu.MktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                }
                else if (shortSignal && atrVal > 0)
                {
                    state.Status = 2;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.EntryAtr = atrVal;
                    state.StopLoss = currentPrice + atrVal * (decimal)stopLossAtr;
                    state.TakeProfit = currentPrice - atrVal * (decimal)takeProfitAtr;
                    state.HighestPrice = q.High;
                    state.LowestPrice = q.Low;
                    state.HoldBars = 0;
                    Trade(tu.MktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
                }
            }
            else if (state.Status == 1)
            {
                // 多头持仓：检查出场条件
                bool exitSignal = false;
                string exitReason = "";

                // 止损
                if (q.Low <= state.StopLoss)
                {
                    exitSignal = true;
                    exitReason = "StopLoss";
                }
                // 止盈
                else if (q.High >= state.TakeProfit)
                {
                    exitSignal = true;
                    exitReason = "TakeProfit";
                }
                // 最大持仓时间
                else if (maxHoldBars > 0 && state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                    exitReason = "MaxHoldBars";
                }
                // 因子信号反转
                else if (factorScore.Composite < (50 - exitThreshold))
                {
                    exitSignal = true;
                    exitReason = "SignalReverse";
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                    
                    // 检查是否反手做空
                    bool shortSignal = factorScore.Composite < shortThreshold && 
                                       factorScore.BearishCount >= factorConsistency &&
                                       mode != 1 && atrVal > 0;
                    if (shortSignal && exitReason == "SignalReverse")
                    {
                        state.Status = 2;
                        state.Num = num;
                        state.EntryPrice = currentPrice;
                        state.EntryAtr = atrVal;
                        state.StopLoss = currentPrice + atrVal * (decimal)stopLossAtr;
                        state.TakeProfit = currentPrice - atrVal * (decimal)takeProfitAtr;
                        state.HighestPrice = q.High;
                        state.LowestPrice = q.Low;
                        state.HoldBars = 0;
                        Trade(tu.MktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
                    }
                    else
                    {
                        state.Reset();
                    }
                }
                else if (useTrailingStop == 1 && atrVal > 0)
                {
                    // 移动止损
                    decimal newStopLoss = state.HighestPrice - atrVal * (decimal)trailingStopAtr;
                    if (newStopLoss > state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
            else if (state.Status == 2)
            {
                // 空头持仓：检查出场条件
                bool exitSignal = false;
                string exitReason = "";

                // 止损
                if (q.High >= state.StopLoss)
                {
                    exitSignal = true;
                    exitReason = "StopLoss";
                }
                // 止盈
                else if (q.Low <= state.TakeProfit)
                {
                    exitSignal = true;
                    exitReason = "TakeProfit";
                }
                // 最大持仓时间
                else if (maxHoldBars > 0 && state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                    exitReason = "MaxHoldBars";
                }
                // 因子信号反转
                else if (factorScore.Composite > (50 + exitThreshold))
                {
                    exitSignal = true;
                    exitReason = "SignalReverse";
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                    
                    // 检查是否反手做多
                    bool longSignal = factorScore.Composite > longThreshold && 
                                      factorScore.BullishCount >= factorConsistency &&
                                      mode != 2 && atrVal > 0;
                    if (longSignal && exitReason == "SignalReverse")
                    {
                        state.Status = 1;
                        state.Num = num;
                        state.EntryPrice = currentPrice;
                        state.EntryAtr = atrVal;
                        state.StopLoss = currentPrice - atrVal * (decimal)stopLossAtr;
                        state.TakeProfit = currentPrice + atrVal * (decimal)takeProfitAtr;
                        state.HighestPrice = q.High;
                        state.LowestPrice = q.Low;
                        state.HoldBars = 0;
                        Trade(tu.MktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
                    }
                    else
                    {
                        state.Reset();
                    }
                }
                else if (useTrailingStop == 1 && atrVal > 0)
                {
                    // 移动止损
                    decimal newStopLoss = state.LowestPrice + atrVal * (decimal)trailingStopAtr;
                    if (newStopLoss < state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }
        }
    }
}
