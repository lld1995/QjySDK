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
    /// RSI + 布林带 + 傅里叶变换综合交易策略
    /// 策略逻辑：
    /// - 使用傅里叶变换识别价格的主要周期和当前相位位置
    /// - 结合布林带判断价格是否处于超买/超卖区域
    /// - 使用RSI确认动量状态
    /// - 三重信号共振时入场，提高交易胜率
    /// 
    /// 入场条件：
    /// - 做多：价格触及/跌破布林带下轨 + RSI超卖 + 傅里叶相位处于周期底部
    /// - 做空：价格触及/突破布林带上轨 + RSI超买 + 傅里叶相位处于周期顶部
    /// 
    /// 出场条件：
    /// - 多头：价格回归中轨或触及上轨，或RSI进入超买区域
    /// - 空头：价格回归中轨或触及下轨，或RSI进入超卖区域
    /// </summary>
    public class RSI_Boll_Fourier : StgBase
    {
        public RSI_Boll_Fourier()
        {
        }

        public RSI_Boll_Fourier(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // RSI参数
            sd.ArgDic["rsiPeriod"] = 14;             // RSI计算周期
            sd.ArgDic["rsiOverbought"] = 70;         // RSI超买阈值
            sd.ArgDic["rsiOversold"] = 30;           // RSI超卖阈值

            // 布林带参数
            sd.ArgDic["bollPeriod"] = 20;            // 布林带周期
            sd.ArgDic["bollStdDev"] = 2.0;           // 标准差倍数

            // 傅里叶变换参数
            sd.ArgDic["fftPeriod"] = 64;             // FFT分析窗口大小(必须是2的幂次)
            sd.ArgDic["dominantPeriodMin"] = 5;      // 主周期最小值
            sd.ArgDic["dominantPeriodMax"] = 32;     // 主周期最大值
            sd.ArgDic["phaseThresholdBuy"] = -0.6;   // 买入相位阈值(接近-1为周期底部)
            sd.ArgDic["phaseThresholdSell"] = 0.6;   // 卖出相位阈值(接近1为周期顶部)
            sd.ArgDic["harmonics"] = 3;              // 使用的谐波数量

            // 交易参数
            sd.ArgDic["mode"] = 0;                   // 交易模式: 0双向 1仅多 2仅空
            sd.ArgDic["sendMode"] = 0;               // 发单模式: 0立即 1下个开盘
            sd.ArgDic["signalMode"] = 0;             // 信号模式: 0 三重共振 1 双重共振(RSI+布林) 2 双重共振(布林+傅里叶)
            sd.ArgDic["exitMode"] = 0;               // 平仓模式: 0 回归中轨 1 反向突破
            sd.ArgDic["stopLoss"] = 5.0m;               // 止损百分比

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;               // 0固定手数 1固定金额
            sd.ArgDic["lots"] = 1.0m;                // 固定手数
            sd.ArgDic["money"] = 10000m;             // 固定金额

            // 参数说明
            sd.ArgDescDic["rsiPeriod"] = new ArgDesc() { Text = "RSI周期", Explain = "RSI计算周期，通常为14" };
            sd.ArgDescDic["rsiOverbought"] = new ArgDesc() { Text = "RSI超买线", Explain = "RSI超买区域阈值，通常为70" };
            sd.ArgDescDic["rsiOversold"] = new ArgDesc() { Text = "RSI超卖线", Explain = "RSI超卖区域阈值，通常为30" };
            sd.ArgDescDic["bollPeriod"] = new ArgDesc() { Text = "布林带周期", Explain = "布林带计算周期，通常为20" };
            sd.ArgDescDic["bollStdDev"] = new ArgDesc() { Text = "标准差倍数", Explain = "布林带上下轨的标准差倍数，通常为2" };
            sd.ArgDescDic["fftPeriod"] = new ArgDesc() { Text = "FFT窗口", Explain = "傅里叶变换分析窗口大小，必须是2的幂次(如32,64,128)" };
            sd.ArgDescDic["dominantPeriodMin"] = new ArgDesc() { Text = "最小周期", Explain = "识别主周期的最小值" };
            sd.ArgDescDic["dominantPeriodMax"] = new ArgDesc() { Text = "最大周期", Explain = "识别主周期的最大值" };
            sd.ArgDescDic["phaseThresholdBuy"] = new ArgDesc() { Text = "买入相位", Explain = "买入相位阈值，接近-1表示周期底部" };
            sd.ArgDescDic["phaseThresholdSell"] = new ArgDesc() { Text = "卖出相位", Explain = "卖出相位阈值，接近1表示周期顶部" };
            sd.ArgDescDic["harmonics"] = new ArgDesc() { Text = "谐波数", Explain = "用于重构信号的谐波数量" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0 双向交易 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即发单 1 下个开盘发单" };
            sd.ArgDescDic["signalMode"] = new ArgDesc() { Text = "信号模式", Explain = "0 三重共振(RSI+布林+傅里叶) 1 双重共振(RSI+布林) 2 双重共振(布林+傅里叶)" };
            sd.ArgDescDic["exitMode"] = new ArgDesc() { Text = "平仓模式", Explain = "0 回归中轨平仓 1 反向突破平仓" };
            sd.ArgDescDic["stopLoss"] = new ArgDesc() { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数数量" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额数量" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 颜色配置
            sd.ColorDic["main-upper"] = "#FF5722";
            sd.ColorDic["main-middle"] = "#FF9800";
            sd.ColorDic["main-lower"] = "#2196F3";
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

            // 获取参数
            int rsiPeriod = (int)ArgDic["rsiPeriod"];
            int bollPeriod = (int)ArgDic["bollPeriod"];
            int fftPeriod = (int)ArgDic["fftPeriod"];
            int dominantPeriodMin = (int)ArgDic["dominantPeriodMin"];
            int dominantPeriodMax = (int)ArgDic["dominantPeriodMax"];
            int harmonics = (int)ArgDic["harmonics"];

            // 确保有足够的数据
            int minBars = Math.Max(Math.Max(rsiPeriod + 2, bollPeriod + 1), fftPeriod);
            if (tu.QuoteList.Count < minBars) return;

            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int signalMode = (int)ArgDic["signalMode"];
            int exitMode = (int)ArgDic["exitMode"];
            int rsiOverbought = (int)ArgDic["rsiOverbought"];
            int rsiOversold = (int)ArgDic["rsiOversold"];
            double bollStdDev = Convert.ToDouble(ArgDic["bollStdDev"]);
            double phaseThresholdBuy = Convert.ToDouble(ArgDic["phaseThresholdBuy"]);
            double phaseThresholdSell = Convert.ToDouble(ArgDic["phaseThresholdSell"]);

            var q = tu.QuoteList.Last();

            // ========== 计算RSI指标 ==========
            var rsiList = tu.QuoteList.GetRsi(rsiPeriod).ToList();
            var rsi1 = rsiList[rsiList.Count - 1];
            var rsi2 = rsiList[rsiList.Count - 2];

            if (rsi1.Rsi == null || rsi2.Rsi == null) return;

            double curRsi = rsi1.Rsi.Value;
            double prevRsi = rsi2.Rsi.Value;

            // 绘制RSI
            Plot("sub0", "RSI", PlotType.LINE, curRsi);

            // ========== 计算布林带指标 ==========
            var bollList = tu.QuoteList.GetBollingerBands(bollPeriod, bollStdDev).ToList();
            var boll1 = bollList[bollList.Count - 1];
            var boll2 = bollList[bollList.Count - 2];

            if (boll1.UpperBand == null || boll1.LowerBand == null || boll1.Sma == null) return;
            if (boll2.UpperBand == null || boll2.LowerBand == null || boll2.Sma == null) return;

            decimal upper = (decimal)boll1.UpperBand.Value;
            decimal middle = (decimal)boll1.Sma.Value;
            decimal lower = (decimal)boll1.LowerBand.Value;
            decimal prevUpper = (decimal)boll2.UpperBand.Value;
            decimal prevMiddle = (decimal)boll2.Sma.Value;
            decimal prevLower = (decimal)boll2.LowerBand.Value;
            var prevClose = tu.QuoteList[tu.QuoteList.Count - 2].Close;

            // 绘制布林带
            Plot("main", "upper", PlotType.LINE, boll1.UpperBand);
            Plot("main", "middle", PlotType.LINE, boll1.Sma);
            Plot("main", "lower", PlotType.LINE, boll1.LowerBand);

            // ========== 计算傅里叶变换 ==========
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
            Plot("sub1", "Phase", PlotType.LINE, phasePosition);
            Plot("sub1", "Cycle", PlotType.LINE, dominantPeriod);

            // ========== 计算手数 ==========
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

            // ========== 信号判断 ==========
            // RSI信号
            bool rsiOversoldSignal = curRsi <= rsiOversold;
            bool rsiOverboughtSignal = curRsi >= rsiOverbought;

            // 布林带信号
            bool bollLowerSignal = q.Close <= lower || (prevClose > prevLower && q.Close < lower);
            bool bollUpperSignal = q.Close >= upper || (prevClose < prevUpper && q.Close > upper);

            // 傅里叶相位信号
            bool phaseBottomSignal = phasePosition <= phaseThresholdBuy;
            bool phaseTopSignal = phasePosition >= phaseThresholdSell;

            // 综合信号
            bool buySignal = false;
            bool sellSignal = false;
            bool exitLongSignal = false;
            bool exitShortSignal = false;

            switch (signalMode)
            {
                case 0:
                    // 模式0：三重共振(RSI+布林+傅里叶)
                    buySignal = rsiOversoldSignal && bollLowerSignal && phaseBottomSignal;
                    sellSignal = rsiOverboughtSignal && bollUpperSignal && phaseTopSignal;
                    break;

                case 1:
                    // 模式1：双重共振(RSI+布林)
                    buySignal = rsiOversoldSignal && bollLowerSignal;
                    sellSignal = rsiOverboughtSignal && bollUpperSignal;
                    break;

                case 2:
                    // 模式2：双重共振(布林+傅里叶)
                    buySignal = bollLowerSignal && phaseBottomSignal;
                    sellSignal = bollUpperSignal && phaseTopSignal;
                    break;
            }

            // 平仓信号
            if (exitMode == 0)
            {
                // 模式0：回归中轨平仓
                exitLongSignal = (prevClose <= prevMiddle && q.Close > middle) || rsiOverboughtSignal;
                exitShortSignal = (prevClose >= prevMiddle && q.Close < middle) || rsiOversoldSignal;
            }
            else
            {
                // 模式1：反向突破平仓
                exitLongSignal = bollUpperSignal || (phaseTopSignal && rsiOverboughtSignal);
                exitShortSignal = bollLowerSignal || (phaseBottomSignal && rsiOversoldSignal);
            }

            // ========== 交易逻辑 ==========
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
                var _sl = (decimal)ArgDic["stopLoss"];
                if (_sl > 0 && s.EntryPrice > 0 && q.Close < s.EntryPrice * (1 - _sl / 100m))
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0; s.Num = 0; s.EntryPrice = 0;
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
                    }
                }
            }
        }
    }
}
