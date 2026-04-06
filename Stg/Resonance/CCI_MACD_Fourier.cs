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
    /// CCI + MACD + 傅里叶变换交易策略
    /// 策略逻辑：
    /// - 使用傅里叶变换对价格序列进行频谱分析，识别主要周期和相位
    /// - 结合CCI指标判断超买超卖状态和趋势强度
    /// - 结合MACD指标判断趋势方向和动量
    /// - 三者共振时产生交易信号：
    ///   * 做多：傅里叶相位处于底部 + CCI超卖回升 + MACD金叉或柱状图由负转正
    ///   * 做空：傅里叶相位处于顶部 + CCI超买回落 + MACD死叉或柱状图由正转负
    /// </summary>
    public class CCI_MACD_Fourier : StgBase
    {
        public CCI_MACD_Fourier()
        {
        }

        public CCI_MACD_Fourier(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // CCI参数
            sd.ArgDic["cciPeriod"] = 20;              // CCI计算周期
            sd.ArgDic["cciOverbought"] = 100;         // CCI超买阈值
            sd.ArgDic["cciOversold"] = -100;          // CCI超卖阈值

            // MACD参数
            sd.ArgDic["macdFast"] = 12;               // MACD快线周期
            sd.ArgDic["macdSlow"] = 26;               // MACD慢线周期
            sd.ArgDic["macdSignal"] = 9;              // MACD信号线周期

            // 傅里叶变换参数
            sd.ArgDic["fftPeriod"] = 64;              // FFT分析窗口大小(必须是2的幂次)
            sd.ArgDic["dominantPeriodMin"] = 5;       // 主周期最小值
            sd.ArgDic["dominantPeriodMax"] = 32;      // 主周期最大值
            sd.ArgDic["phaseThresholdBuy"] = -0.6;    // 买入相位阈值(接近-1为周期底部)
            sd.ArgDic["phaseThresholdSell"] = 0.6;    // 卖出相位阈值(接近1为周期顶部)
            sd.ArgDic["harmonics"] = 3;               // 使用的谐波数量

            // 交易参数
            sd.ArgDic["mode"] = 0;                    // 交易模式: 0双向 1仅多 2仅空
            sd.ArgDic["sendMode"] = 0;                // 发单模式: 0立即 1下个开盘
            sd.ArgDic["signalMode"] = 0;              // 信号模式: 0 三指标共振 1 CCI+MACD 2 CCI+傅里叶 3 MACD+傅里叶
            sd.ArgDic["stopLoss"] = 5.0m;              // 止损百分比

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;                // 0固定手数 1固定金额
            sd.ArgDic["lots"] = 1.0m;                 // 固定手数
            sd.ArgDic["money"] = 10000m;              // 固定金额

            // 参数说明
            sd.ArgDescDic["cciPeriod"] = new ArgDesc() { Text = "CCI周期", Explain = "CCI计算周期，通常为20" };
            sd.ArgDescDic["cciOverbought"] = new ArgDesc() { Text = "CCI超买线", Explain = "CCI超买区域阈值，通常为100" };
            sd.ArgDescDic["cciOversold"] = new ArgDesc() { Text = "CCI超卖线", Explain = "CCI超卖区域阈值，通常为-100" };
            sd.ArgDescDic["macdFast"] = new ArgDesc() { Text = "MACD快线", Explain = "MACD快速EMA周期，通常为12" };
            sd.ArgDescDic["macdSlow"] = new ArgDesc() { Text = "MACD慢线", Explain = "MACD慢速EMA周期，通常为26" };
            sd.ArgDescDic["macdSignal"] = new ArgDesc() { Text = "MACD信号线", Explain = "MACD信号线周期，通常为9" };
            sd.ArgDescDic["fftPeriod"] = new ArgDesc() { Text = "FFT窗口", Explain = "傅里叶变换分析窗口大小，必须是2的幂次(如32,64,128)" };
            sd.ArgDescDic["dominantPeriodMin"] = new ArgDesc() { Text = "最小周期", Explain = "识别主周期的最小值" };
            sd.ArgDescDic["dominantPeriodMax"] = new ArgDesc() { Text = "最大周期", Explain = "识别主周期的最大值" };
            sd.ArgDescDic["phaseThresholdBuy"] = new ArgDesc() { Text = "买入相位", Explain = "买入相位阈值，接近-1表示周期底部" };
            sd.ArgDescDic["phaseThresholdSell"] = new ArgDesc() { Text = "卖出相位", Explain = "卖出相位阈值，接近1表示周期顶部" };
            sd.ArgDescDic["harmonics"] = new ArgDesc() { Text = "谐波数", Explain = "用于重构信号的谐波数量" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0 双向交易 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即发单 1 下个开盘发单" };
            sd.ArgDescDic["signalMode"] = new ArgDesc() { Text = "信号模式", Explain = "0 三指标共振 1 CCI+MACD 2 CCI+傅里叶 3 MACD+傅里叶" };
            sd.ArgDescDic["stopLoss"] = new ArgDesc() { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数数量" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额数量" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 3;

            // 颜色配置
            sd.ColorDic["sub0-CCI"] = "#F6465D";
            sd.ColorDic["sub1-MACD"] = "#0ECB81";
            sd.ColorDic["sub1-Signal"] = "#F0B90B";
            sd.ColorDic["sub1-Histogram"] = "#7B61FF";
            sd.ColorDic["sub2-Phase"] = "#00BFFF";
            sd.ColorDic["sub2-Cycle"] = "#FF69B4";

            // 中值线配置
            sd.MidValDic["sub0"] = 0;
            sd.MidValDic["sub1"] = 0;
            sd.MidValDic["sub2"] = 0;

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

            int cciPeriod = (int)ArgDic["cciPeriod"];
            int macdFast = (int)ArgDic["macdFast"];
            int macdSlow = (int)ArgDic["macdSlow"];
            int macdSignal = (int)ArgDic["macdSignal"];
            int fftPeriod = (int)ArgDic["fftPeriod"];
            int dominantPeriodMin = (int)ArgDic["dominantPeriodMin"];
            int dominantPeriodMax = (int)ArgDic["dominantPeriodMax"];
            int harmonics = (int)ArgDic["harmonics"];

            // 确保有足够的数据
            int minBars = Math.Max(Math.Max(cciPeriod + 2, macdSlow + macdSignal + 2), fftPeriod);
            if (tu.QuoteList.Count < minBars) return;

            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int signalMode = (int)ArgDic["signalMode"];
            int cciOverbought = (int)ArgDic["cciOverbought"];
            int cciOversold = (int)ArgDic["cciOversold"];
            double phaseThresholdBuy = Convert.ToDouble(ArgDic["phaseThresholdBuy"]);
            double phaseThresholdSell = Convert.ToDouble(ArgDic["phaseThresholdSell"]);

            var q = tu.QuoteList.Last();

            // 计算CCI指标
            var cciList = tu.QuoteList.GetCci(cciPeriod).ToList();
            var cci1 = cciList[cciList.Count - 1];
            var cci2 = cciList[cciList.Count - 2];

            if (cci1.Cci == null || cci2.Cci == null) return;

            double curCci = cci1.Cci.Value;
            double prevCci = cci2.Cci.Value;

            // 绘制CCI
            Plot("sub0", "CCI", PlotType.LINE, curCci);

            // 计算MACD指标
            var macdList = tu.QuoteList.GetMacd(macdFast, macdSlow, macdSignal).ToList();
            var macd1 = macdList[macdList.Count - 1];
            var macd2 = macdList[macdList.Count - 2];

            if (macd1.Macd == null || macd1.Signal == null || macd1.Histogram == null) return;
            if (macd2.Macd == null || macd2.Signal == null || macd2.Histogram == null) return;

            double curMacd = macd1.Macd.Value;
            double curSignal = macd1.Signal.Value;
            double curHistogram = macd1.Histogram.Value;
            double prevMacd = macd2.Macd.Value;
            double prevSignal = macd2.Signal.Value;
            double prevHistogram = macd2.Histogram.Value;

            // 绘制MACD
            Plot("sub1", "MACD", PlotType.LINE, curMacd);
            Plot("sub1", "Signal", PlotType.LINE, curSignal);
            Plot("sub1", "Histogram", PlotType.RECTANGLE, curHistogram);

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

            // 绘制相位和周期信息
            Plot("sub2", "Phase", PlotType.LINE, phasePosition);
            Plot("sub2", "Cycle", PlotType.LINE, dominantPeriod);

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
            bool cciOversoldSignal = prevCci < cciOversold && curCci >= cciOversold;  // CCI从超卖回升
            bool cciOverboughtSignal = prevCci > cciOverbought && curCci <= cciOverbought;  // CCI从超买回落
            bool cciInOversold = curCci <= cciOversold;  // CCI处于超卖区
            bool cciInOverbought = curCci >= cciOverbought;  // CCI处于超买区

            bool macdGoldenCross = prevMacd < prevSignal && curMacd >= curSignal;  // MACD金叉
            bool macdDeathCross = prevMacd > prevSignal && curMacd <= curSignal;   // MACD死叉
            bool macdHistogramTurnPositive = prevHistogram < 0 && curHistogram >= 0;  // 柱状图由负转正
            bool macdHistogramTurnNegative = prevHistogram > 0 && curHistogram <= 0;  // 柱状图由正转负
            bool macdBullish = curMacd > curSignal || curHistogram > 0;  // MACD多头
            bool macdBearish = curMacd < curSignal || curHistogram < 0;  // MACD空头

            bool phaseAtBottom = phasePosition <= phaseThresholdBuy;  // 相位处于底部
            bool phaseAtTop = phasePosition >= phaseThresholdSell;    // 相位处于顶部

            bool buySignal = false;
            bool sellSignal = false;
            bool exitLongSignal = false;
            bool exitShortSignal = false;

            switch (signalMode)
            {
                case 0:
                    // 模式0：三指标共振
                    // 做多：相位底部 + CCI超卖回升 + MACD金叉或柱状图转正
                    buySignal = phaseAtBottom && (cciOversoldSignal || cciInOversold) && (macdGoldenCross || macdHistogramTurnPositive || macdBullish);
                    // 做空：相位顶部 + CCI超买回落 + MACD死叉或柱状图转负
                    sellSignal = phaseAtTop && (cciOverboughtSignal || cciInOverbought) && (macdDeathCross || macdHistogramTurnNegative || macdBearish);
                    // 多头平仓：相位到达顶部或CCI超买或MACD死叉
                    exitLongSignal = phaseAtTop || cciInOverbought || macdDeathCross;
                    // 空头平仓：相位到达底部或CCI超卖或MACD金叉
                    exitShortSignal = phaseAtBottom || cciInOversold || macdGoldenCross;
                    break;

                case 1:
                    // 模式1：CCI + MACD
                    // 做多：CCI超卖回升 + MACD金叉或柱状图转正
                    buySignal = (cciOversoldSignal || cciInOversold) && (macdGoldenCross || macdHistogramTurnPositive);
                    // 做空：CCI超买回落 + MACD死叉或柱状图转负
                    sellSignal = (cciOverboughtSignal || cciInOverbought) && (macdDeathCross || macdHistogramTurnNegative);
                    // 多头平仓：CCI超买或MACD死叉
                    exitLongSignal = cciInOverbought || macdDeathCross;
                    // 空头平仓：CCI超卖或MACD金叉
                    exitShortSignal = cciInOversold || macdGoldenCross;
                    break;

                case 2:
                    // 模式2：CCI + 傅里叶
                    // 做多：相位底部 + CCI超卖回升
                    buySignal = phaseAtBottom && (cciOversoldSignal || cciInOversold);
                    // 做空：相位顶部 + CCI超买回落
                    sellSignal = phaseAtTop && (cciOverboughtSignal || cciInOverbought);
                    // 多头平仓：相位到达顶部或CCI超买
                    exitLongSignal = phaseAtTop || cciInOverbought;
                    // 空头平仓：相位到达底部或CCI超卖
                    exitShortSignal = phaseAtBottom || cciInOversold;
                    break;

                case 3:
                    // 模式3：MACD + 傅里叶
                    // 做多：相位底部 + MACD金叉或柱状图转正
                    buySignal = phaseAtBottom && (macdGoldenCross || macdHistogramTurnPositive);
                    // 做空：相位顶部 + MACD死叉或柱状图转负
                    sellSignal = phaseAtTop && (macdDeathCross || macdHistogramTurnNegative);
                    // 多头平仓：相位到达顶部或MACD死叉
                    exitLongSignal = phaseAtTop || macdDeathCross;
                    // 空头平仓：相位到达底部或MACD金叉
                    exitShortSignal = phaseAtBottom || macdGoldenCross;
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
