using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Linq;
using static Model.EnumDef;
using System.Collections.Generic;
using System.Numerics;

namespace QjySDK.Stg
{
    /// <summary>
    /// RSI + 傅里叶变换交易策略
    /// 策略逻辑：
    /// - 使用傅里叶变换对价格序列进行频谱分析，识别主要周期
    /// - 结合RSI指标判断超买超卖状态
    /// - 当傅里叶分析显示价格处于周期底部且RSI超卖时做多
    /// - 当傅里叶分析显示价格处于周期顶部且RSI超买时做空
    /// </summary>
    public class RSI_Fourier : StgBase
    {
        public RSI_Fourier()
        {
        }

        public RSI_Fourier(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // RSI参数
            sd.ArgDic["useAdaptiveRsi"] = 1;         // 是否使用自适应RSI周期(0否 1是)
            sd.ArgDic["rsiPeriod"] = 14;             // RSI计算周期(自适应关闭时使用)
            sd.ArgDic["rsiPeriodMin"] = 5;           // RSI周期最小值(自适应开启时)
            sd.ArgDic["rsiPeriodMax"] = 30;          // RSI周期最大值(自适应开启时)
            sd.ArgDic["overbought"] = 70;            // 超买阈值
            sd.ArgDic["oversold"] = 30;              // 超卖阈值

            // 傅里叶变换参数
            sd.ArgDic["fftPeriod"] = 64;             // FFT分析窗口大小(必须是2的幂次)
            sd.ArgDic["dominantPeriodMin"] = 5;      // 主周期最小值
            sd.ArgDic["dominantPeriodMax"] = 32;     // 主周期最大值
            sd.ArgDic["phaseThresholdBuy"] = -0.7;   // 买入相位阈值(接近-1为周期底部)
            sd.ArgDic["phaseThresholdSell"] = 0.7;   // 卖出相位阈值(接近1为周期顶部)
            sd.ArgDic["harmonics"] = 3;              // 使用的谐波数量

            // 交易参数
            sd.ArgDic["mode"] = 0;                   // 交易模式: 0双向 1仅多 2仅空
            sd.ArgDic["sendMode"] = 0;               // 发单模式: 0立即 1下个开盘
            sd.ArgDic["signalMode"] = 0;             // 信号模式: 0 RSI+相位 1 仅相位 2 RSI确认相位
            sd.ArgDic["stopLoss"] = 5.0m;            // 止损百分比

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;               // 0固定手数 1固定金额
            sd.ArgDic["lots"] = 1.0m;                // 固定手数
            sd.ArgDic["money"] = 10000m;             // 固定金额

            // 参数说明
            sd.ArgDescDic["useAdaptiveRsi"] = new ArgDesc() { Text = "自适应RSI", Explain = "0 使用固定RSI周期 1 使用傅里叶主周期作为RSI周期" };
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc() { Text = "RSI周期", Explain = "RSI计算周期，自适应关闭时使用，通常为14" };
            sd.ArgDescDic["rsiPeriodMin"] = new ArgDesc() { Text = "RSI最小周期", Explain = "自适应RSI周期的最小值" };
            sd.ArgDescDic["rsiPeriodMax"] = new ArgDesc() { Text = "RSI最大周期", Explain = "自适应RSI周期的最大值" };
            sd.ArgDescDic["overbought"] = new ArgDesc() { Text = "超买线", Explain = "超买区域阈值，通常为70" };
            sd.ArgDescDic["oversold"] = new ArgDesc() { Text = "超卖线", Explain = "超卖区域阈值，通常为30" };
            sd.ArgDescDic["fftPeriod"] = new ArgDesc() { Text = "FFT窗口", Explain = "傅里叶变换分析窗口大小，必须是2的幂次(如32,64,128)" };
            sd.ArgDescDic["dominantPeriodMin"] = new ArgDesc() { Text = "最小周期", Explain = "识别主周期的最小值" };
            sd.ArgDescDic["dominantPeriodMax"] = new ArgDesc() { Text = "最大周期", Explain = "识别主周期的最大值" };
            sd.ArgDescDic["phaseThresholdBuy"] = new ArgDesc() { Text = "买入相位", Explain = "买入相位阈值，接近-1表示周期底部" };
            sd.ArgDescDic["phaseThresholdSell"] = new ArgDesc() { Text = "卖出相位", Explain = "卖出相位阈值，接近1表示周期顶部" };
            sd.ArgDescDic["harmonics"] = new ArgDesc() { Text = "谐波数", Explain = "用于重构信号的谐波数量" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0 双向交易 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即发单 1 下个开盘发单" };
            sd.ArgDescDic["signalMode"] = new ArgDesc() { Text = "信号模式", Explain = "0 RSI+相位综合 1 仅相位信号 2 RSI确认相位" };
            sd.ArgDescDic["stopLoss"] = new ArgDesc() { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数数量" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额数量" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 颜色配置
            sd.ColorDic["sub0-RSI"] = "#F6465D";
            sd.ColorDic["sub1-Phase"] = "#0ECB81";
            sd.ColorDic["sub1-Cycle"] = "#F0B90B";

            // 中值线配置
            sd.MidValDic["sub0"] = 50;
            sd.MidValDic["sub1"] = 0;

            return sd;
        }

        private class State
        {
            public int Status { get; set; }     // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }    // 持仓数量
            public decimal EntryPrice { get; set; }
            public bool SignalResetSinceClose { get; set; } = true;
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        /// <summary>
        /// 执行快速傅里叶变换(FFT)
        /// </summary>
        private Complex[] FFT(double[] data)
        {
            int n = data.Length;
            if (n == 1)
            {
                return new Complex[] { new Complex(data[0], 0) };
            }

            // 分离奇偶项
            double[] even = new double[n / 2];
            double[] odd = new double[n / 2];
            for (int i = 0; i < n / 2; i++)
            {
                even[i] = data[2 * i];
                odd[i] = data[2 * i + 1];
            }

            // 递归计算
            Complex[] evenFFT = FFT(even);
            Complex[] oddFFT = FFT(odd);

            // 合并结果
            Complex[] result = new Complex[n];
            for (int k = 0; k < n / 2; k++)
            {
                double angle = -2.0 * Math.PI * k / n;
                Complex w = new Complex(Math.Cos(angle), Math.Sin(angle));
                result[k] = evenFFT[k] + w * oddFFT[k];
                result[k + n / 2] = evenFFT[k] - w * oddFFT[k];
            }

            return result;
        }

        /// <summary>
        /// 找到主导周期
        /// </summary>
        private (int period, double amplitude, double phase) FindDominantCycle(Complex[] fftResult, int minPeriod, int maxPeriod)
        {
            int n = fftResult.Length;
            double maxAmplitude = 0;
            int dominantFreqIndex = 0;

            // 在指定范围内寻找最大幅度的频率分量
            for (int i = 1; i < n / 2; i++)
            {
                int period = n / i;
                if (period >= minPeriod && period <= maxPeriod)
                {
                    double amplitude = fftResult[i].Magnitude;
                    if (amplitude > maxAmplitude)
                    {
                        maxAmplitude = amplitude;
                        dominantFreqIndex = i;
                    }
                }
            }

            if (dominantFreqIndex == 0)
            {
                return (0, 0, 0);
            }

            int dominantPeriod = n / dominantFreqIndex;
            double phase = Math.Atan2(fftResult[dominantFreqIndex].Imaginary, fftResult[dominantFreqIndex].Real);

            return (dominantPeriod, maxAmplitude, phase);
        }

        /// <summary>
        /// 计算当前相位位置(-1到1，-1为底部，1为顶部)
        /// </summary>
        private double CalculatePhasePosition(Complex[] fftResult, int harmonics, int currentIndex, int n)
        {
            double reconstructed = 0;
            double maxVal = 0;
            double minVal = 0;

            // 使用前几个谐波重构信号
            List<(int freq, double amp)> topHarmonics = new List<(int, double)>();
            for (int i = 1; i < n / 2 && topHarmonics.Count < harmonics; i++)
            {
                topHarmonics.Add((i, fftResult[i].Magnitude));
            }
            topHarmonics = topHarmonics.OrderByDescending(x => x.amp).Take(harmonics).ToList();

            // 计算重构信号在当前位置的值
            foreach (var h in topHarmonics)
            {
                double freq = h.freq;
                double amp = fftResult[h.freq].Magnitude;
                double phase = Math.Atan2(fftResult[h.freq].Imaginary, fftResult[h.freq].Real);
                reconstructed += amp * Math.Cos(2 * Math.PI * freq * currentIndex / n + phase);
            }

            // 计算重构信号的范围
            for (int t = 0; t < n; t++)
            {
                double val = 0;
                foreach (var h in topHarmonics)
                {
                    double freq = h.freq;
                    double amp = fftResult[h.freq].Magnitude;
                    double phase = Math.Atan2(fftResult[h.freq].Imaginary, fftResult[h.freq].Real);
                    val += amp * Math.Cos(2 * Math.PI * freq * t / n + phase);
                }
                if (t == 0 || val > maxVal) maxVal = val;
                if (t == 0 || val < minVal) minVal = val;
            }

            // 归一化到-1到1
            if (maxVal - minVal < 0.0001) return 0;
            return 2 * (reconstructed - minVal) / (maxVal - minVal) - 1;
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            int useAdaptiveRsi = (int)ArgDic["useAdaptiveRsi"];
            int rsiPeriodFixed = (int)ArgDic["rsiPeriod"];
            int rsiPeriodMin = (int)ArgDic["rsiPeriodMin"];
            int rsiPeriodMax = (int)ArgDic["rsiPeriodMax"];
            int fftPeriod = (int)ArgDic["fftPeriod"];
            int dominantPeriodMin = (int)ArgDic["dominantPeriodMin"];
            int dominantPeriodMax = (int)ArgDic["dominantPeriodMax"];
            int harmonics = (int)ArgDic["harmonics"];

            // 确保有足够的数据(先用最大可能的RSI周期检查)
            int maxRsiPeriod = useAdaptiveRsi == 1 ? rsiPeriodMax : rsiPeriodFixed;
            int minBars = Math.Max(maxRsiPeriod + 2, fftPeriod);
            if (tu.QuoteList.Count < minBars) return;

            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int signalMode = (int)ArgDic["signalMode"];
            int overbought = (int)ArgDic["overbought"];
            int oversold = (int)ArgDic["oversold"];
            double phaseThresholdBuy = Convert.ToDouble(ArgDic["phaseThresholdBuy"]);
            double phaseThresholdSell = Convert.ToDouble(ArgDic["phaseThresholdSell"]);

            var q = tu.QuoteList.Last();

            // 准备傅里叶变换数据(使用收盘价)
            var priceData = tu.QuoteList.Skip(tu.QuoteList.Count - fftPeriod).Take(fftPeriod)
                .Select(x => (double)x.Close).ToArray();

            // 去趋势处理(减去线性趋势)
            double[] detrendedData = new double[fftPeriod];
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int i = 0; i < fftPeriod; i++)
            {
                sumX += i;
                sumY += priceData[i];
                sumXY += i * priceData[i];
                sumX2 += i * i;
            }
            double slope = (fftPeriod * sumXY - sumX * sumY) / (fftPeriod * sumX2 - sumX * sumX);
            double intercept = (sumY - slope * sumX) / fftPeriod;
            for (int i = 0; i < fftPeriod; i++)
            {
                detrendedData[i] = priceData[i] - (slope * i + intercept);
            }

            // 执行FFT
            Complex[] fftResult = FFT(detrendedData);

            // 找到主导周期
            var (dominantPeriod, amplitude, phase) = FindDominantCycle(fftResult, dominantPeriodMin, dominantPeriodMax);

            // 计算当前相位位置
            double phasePosition = CalculatePhasePosition(fftResult, harmonics, fftPeriod - 1, fftPeriod);

            // 确定RSI周期：自适应模式使用傅里叶主周期，否则使用固定周期
            int rsiPeriod = rsiPeriodFixed;
            if (useAdaptiveRsi == 1 && dominantPeriod > 0)
            {
                // 将傅里叶主周期约束在RSI周期范围内
                rsiPeriod = Math.Max(rsiPeriodMin, Math.Min(rsiPeriodMax, dominantPeriod));
            }

            // 计算RSI指标(使用动态或固定周期)
            var rsiList = tu.QuoteList.GetRsi(rsiPeriod).ToList();
            var rsi1 = rsiList[rsiList.Count - 1];
            var rsi2 = rsiList[rsiList.Count - 2];

            if (rsi1.Rsi == null || rsi2.Rsi == null) return;

            double curRsi = rsi1.Rsi.Value;
            double prevRsi = rsi2.Rsi.Value;

            // 绘制RSI和相位周期信息
            Plot("sub0", "RSI", PlotType.LINE, curRsi);
            Plot("sub1", "Phase", PlotType.LINE, phasePosition);
            Plot("sub1", "Cycle", PlotType.LINE, dominantPeriod);

            // 计算手数
            var num = (decimal)ArgDic["lots"];
            var lotsMode = (int)ArgDic["lotsMode"];
            if (lotsMode == 1)
            {
                var sym = GetSymbol(tu.MktSymbol);
                num = ((decimal)ArgDic["money"] / (q.Close * sym.multiplier * sym.margin_ratio));
                if (sym.symbol_type == (int)SymbolType.COIN)
                {
                    num = Math.Floor(num * 1000) / 1000m;
                }
                else
                {
                    num = Math.Floor(num);
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
            bool exitLongSignal = false;
            bool exitShortSignal = false;

            switch (signalMode)
            {
                case 0:
                    // 模式0：RSI+相位综合
                    // 相位处于底部区域且RSI超卖时做多
                    buySignal = phasePosition <= phaseThresholdBuy && curRsi <= oversold;
                    // 相位处于顶部区域且RSI超买时做空
                    sellSignal = phasePosition >= phaseThresholdSell && curRsi >= overbought;
                    // 多头平仓：相位到达顶部或RSI超买
                    exitLongSignal = phasePosition >= phaseThresholdSell || curRsi >= overbought;
                    // 空头平仓：相位到达底部或RSI超卖
                    exitShortSignal = phasePosition <= phaseThresholdBuy || curRsi <= oversold;
                    break;

                case 1:
                    // 模式1：仅相位信号
                    // 相位从底部区域回升做多
                    buySignal = phasePosition <= phaseThresholdBuy;
                    // 相位从顶部区域回落做空
                    sellSignal = phasePosition >= phaseThresholdSell;
                    // 多头平仓：相位到达顶部
                    exitLongSignal = phasePosition >= phaseThresholdSell;
                    // 空头平仓：相位到达底部
                    exitShortSignal = phasePosition <= phaseThresholdBuy;
                    break;

                case 2:
                    // 模式2：RSI确认相位
                    // 相位处于底部区域，且RSI从超卖回升时做多
                    buySignal = phasePosition <= phaseThresholdBuy && prevRsi < oversold && curRsi >= oversold;
                    // 相位处于顶部区域，且RSI从超买回落时做空
                    sellSignal = phasePosition >= phaseThresholdSell && prevRsi > overbought && curRsi <= overbought;
                    // 多头平仓：相位到达顶部且RSI进入超买
                    exitLongSignal = phasePosition >= phaseThresholdSell && curRsi >= overbought;
                    // 空头平仓：相位到达底部且RSI进入超卖
                    exitShortSignal = phasePosition <= phaseThresholdBuy && curRsi <= oversold;
                    break;
            }

            // 交易逻辑
            if (s.Status == 0)
            {
                if (!buySignal && !sellSignal) s.SignalResetSinceClose = true;

                // 空仓状态：寻找入场信号
                if (buySignal && mode != 2 && s.SignalResetSinceClose)
                {
                    s.Status = 1;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                }
                else if (sellSignal && mode != 1 && s.SignalResetSinceClose)
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
                var _sl = (decimal)ArgDic["stopLoss"];
                if (_sl > 0 && s.EntryPrice > 0 && q.Close < s.EntryPrice * (1 - _sl / 100m))
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                    s.SignalResetSinceClose = false;
                    return;
                }

                // 多头持仓：检查平仓信号
                if (exitLongSignal)
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
                        s.SignalResetSinceClose = false;
                    }
                }
            }
            else if (s.Status == 2)
            {
                // 止损检查
                var _sl2 = (decimal)ArgDic["stopLoss"];
                if (_sl2 > 0 && s.EntryPrice > 0 && q.Close > s.EntryPrice * (1 + _sl2 / 100m))
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                    s.SignalResetSinceClose = false;
                    return;
                }

                // 空头持仓：检查平仓信号
                if (exitShortSignal)
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
                        s.SignalResetSinceClose = false;
                    }
                }
            }
        }
    }
}
