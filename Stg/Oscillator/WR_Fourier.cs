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
    /// WR+傅里叶变换交易策略
    /// 策略逻辑：
    /// - 使用威廉指标(WR)识别超买超卖区域
    /// - 使用傅里叶变换分析价格周期，提取主周期信号
    /// - 结合WR信号和傅里叶周期预测进行交易决策
    /// - 当WR超卖且傅里叶预测上涨时做多
    /// - 当WR超买且傅里叶预测下跌时做空
    /// </summary>
    public class WR_Fourier : StgBase
    {
        public WR_Fourier()
        {
        }

        public WR_Fourier(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // WR参数
            sd.ArgDic["wrPeriod"] = 14;              // WR计算周期
            sd.ArgDic["overbought"] = -20;           // 超买阈值(WR范围-100到0)
            sd.ArgDic["oversold"] = -80;             // 超卖阈值

            // 傅里叶变换参数
            sd.ArgDic["fftPeriod"] = 64;             // FFT分析窗口大小(需为2的幂次)
            sd.ArgDic["harmonics"] = 3;              // 保留的主要谐波数量
            sd.ArgDic["predictBars"] = 5;            // 预测未来K线数

            // 交易参数
            sd.ArgDic["mode"] = 0;                   // 交易模式: 0双向 1仅多 2仅空
            sd.ArgDic["sendMode"] = 0;               // 发单模式: 0立即 1下个开盘
            sd.ArgDic["signalMode"] = 0;             // 信号模式: 0 WR+FFT组合 1 仅WR 2 仅FFT
            sd.ArgDic["stopLoss"] = 5.0m;            // 止损百分比

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;               // 0固定手数 1固定金额
            sd.ArgDic["lots"] = 1.0m;                // 固定手数
            sd.ArgDic["money"] = 10000m;             // 固定金额

            // 参数说明
            sd.ArgDescDic["wrPeriod"] = new ArgDesc() { Text = "WR周期", Explain = "威廉指标计算周期，通常为14" };
            sd.ArgDescDic["overbought"] = new ArgDesc() { Text = "超买线", Explain = "超买区域阈值，通常为-20" };
            sd.ArgDescDic["oversold"] = new ArgDesc() { Text = "超卖线", Explain = "超卖区域阈值，通常为-80" };
            sd.ArgDescDic["fftPeriod"] = new ArgDesc() { Text = "FFT窗口", Explain = "傅里叶变换分析窗口大小，需为2的幂次(32,64,128等)" };
            sd.ArgDescDic["harmonics"] = new ArgDesc() { Text = "谐波数量", Explain = "保留的主要谐波数量，用于滤波和预测" };
            sd.ArgDescDic["predictBars"] = new ArgDesc() { Text = "预测K线数", Explain = "使用傅里叶预测未来的K线数量" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0 双向交易 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即发单 1 下个开盘发单" };
            sd.ArgDescDic["signalMode"] = new ArgDesc() { Text = "信号模式", Explain = "0 WR+FFT组合 1 仅WR信号 2 仅FFT信号" };
            sd.ArgDescDic["stopLoss"] = new ArgDesc() { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数数量" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额数量" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 颜色配置
            sd.ColorDic["sub0-WR"] = "#F6465D";
            sd.ColorDic["sub1-FFT"] = "#0ECB81";
            sd.ColorDic["sub1-Predict"] = "#F0B90B";

            // 中值线配置
            sd.MidValDic["sub0"] = -50;

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
        /// 计算威廉指标(WR)
        /// WR = (最高价 - 收盘价) / (最高价 - 最低价) * -100
        /// </summary>
        private double CalculateWR(List<SkQuote> quotes, int period)
        {
            if (quotes.Count < period) return -50;

            var recentQuotes = quotes.Skip(quotes.Count - period).ToList();
            decimal highestHigh = recentQuotes.Max(q => q.High);
            decimal lowestLow = recentQuotes.Min(q => q.Low);
            decimal close = quotes.Last().Close;

            if (highestHigh == lowestLow) return -50;

            return (double)((highestHigh - close) / (highestHigh - lowestLow) * -100);
        }

        /// <summary>
        /// 执行快速傅里叶变换(FFT)
        /// </summary>
        private Complex[] FFT(double[] data)
        {
            int n = data.Length;
            if (n == 1) return new Complex[] { new Complex(data[0], 0) };

            // 确保长度为2的幂次
            int m = 1;
            while (m < n) m <<= 1;
            if (m != n)
            {
                // 补零到2的幂次
                var padded = new double[m];
                Array.Copy(data, padded, n);
                data = padded;
                n = m;
            }

            // 递归FFT
            var even = new double[n / 2];
            var odd = new double[n / 2];
            for (int i = 0; i < n / 2; i++)
            {
                even[i] = data[2 * i];
                odd[i] = data[2 * i + 1];
            }

            var evenFFT = FFT(even);
            var oddFFT = FFT(odd);

            var result = new Complex[n];
            for (int k = 0; k < n / 2; k++)
            {
                double angle = -2 * Math.PI * k / n;
                var w = new Complex(Math.Cos(angle), Math.Sin(angle));
                result[k] = evenFFT[k] + w * oddFFT[k];
                result[k + n / 2] = evenFFT[k] - w * oddFFT[k];
            }

            return result;
        }

        /// <summary>
        /// 执行逆傅里叶变换(IFFT)
        /// </summary>
        private double[] IFFT(Complex[] data)
        {
            int n = data.Length;
            
            // 共轭
            var conjugate = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                conjugate[i] = Complex.Conjugate(data[i]);
            }

            // 转换为double数组进行FFT
            var realPart = new double[n];
            for (int i = 0; i < n; i++)
            {
                realPart[i] = conjugate[i].Real;
            }

            // 对共轭进行FFT
            var fftResult = FFT(realPart);

            // 再次共轭并除以n
            var result = new double[n];
            for (int i = 0; i < n; i++)
            {
                result[i] = fftResult[i].Real / n;
            }

            return result;
        }

        /// <summary>
        /// 使用傅里叶变换分析价格并预测趋势
        /// 返回值: 正数表示预测上涨，负数表示预测下跌
        /// </summary>
        private (double filteredValue, double predictedTrend) AnalyzeWithFFT(List<SkQuote> quotes, int fftPeriod, int harmonics, int predictBars)
        {
            if (quotes.Count < fftPeriod) return (0, 0);

            // 提取收盘价序列
            var prices = quotes.Skip(quotes.Count - fftPeriod).Select(q => (double)q.Close).ToArray();

            // 去趋势化(减去均值)
            double mean = prices.Average();
            var detrended = prices.Select(p => p - mean).ToArray();

            // 执行FFT
            var fftResult = FFT(detrended);

            // 保留主要谐波(低频成分)，滤除高频噪声
            var filtered = new Complex[fftResult.Length];
            for (int i = 0; i < fftResult.Length; i++)
            {
                // 保留DC分量和前harmonics个谐波
                if (i <= harmonics || i >= fftResult.Length - harmonics)
                {
                    filtered[i] = fftResult[i];
                }
                else
                {
                    filtered[i] = Complex.Zero;
                }
            }

            // 逆变换得到滤波后的信号
            var filteredSignal = IFFT(filtered);
            double currentFiltered = filteredSignal[filteredSignal.Length - 1] + mean;

            // 预测未来趋势
            // 通过分析主要谐波的相位来预测
            double predictedChange = 0;
            for (int h = 1; h <= harmonics && h < fftResult.Length / 2; h++)
            {
                double amplitude = fftResult[h].Magnitude / fftPeriod;
                double phase = fftResult[h].Phase;
                double frequency = 2 * Math.PI * h / fftPeriod;

                // 预测未来predictBars个周期后的值
                double currentPhase = frequency * (fftPeriod - 1) + phase;
                double futurePhase = frequency * (fftPeriod - 1 + predictBars) + phase;

                double currentValue = amplitude * Math.Cos(currentPhase);
                double futureValue = amplitude * Math.Cos(futurePhase);

                predictedChange += (futureValue - currentValue);
            }

            return (currentFiltered, predictedChange);
        }

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            int wrPeriod = (int)ArgDic["wrPeriod"];
            int fftPeriod = (int)ArgDic["fftPeriod"];
            int harmonics = (int)ArgDic["harmonics"];
            int predictBars = (int)ArgDic["predictBars"];
            int signalMode = (int)ArgDic["signalMode"];

            // 确保有足够的数据
            int minBars = Math.Max(wrPeriod, fftPeriod) + 2;
            if (tu.QuoteList.Count < minBars) return;

            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            int overbought = (int)ArgDic["overbought"];
            int oversold = (int)ArgDic["oversold"];

            var q = tu.QuoteList.Last();

            // 计算WR指标
            double curWR = CalculateWR(tu.QuoteList, wrPeriod);
            double prevWR = CalculateWR(tu.QuoteList.Take(tu.QuoteList.Count - 1).ToList(), wrPeriod);

            // 绘制WR
            Plot("sub0", "WR", PlotType.LINE, curWR);

            // 傅里叶分析
            var (filteredValue, predictedTrend) = AnalyzeWithFFT(tu.QuoteList, fftPeriod, harmonics, predictBars);

            // 绘制傅里叶滤波值和预测趋势
            Plot("sub1", "FFT", PlotType.LINE, filteredValue);
            Plot("sub1", "Predict", PlotType.LINE, filteredValue + predictedTrend);

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

            // WR信号
            bool wrBuy = prevWR < oversold && curWR >= oversold;      // WR从超卖区回升
            bool wrSell = prevWR > overbought && curWR <= overbought; // WR从超买区回落
            bool wrOverbought = curWR >= overbought;
            bool wrOversold = curWR <= oversold;

            // FFT信号
            bool fftBuy = predictedTrend > 0;   // 傅里叶预测上涨
            bool fftSell = predictedTrend < 0;  // 傅里叶预测下跌

            switch (signalMode)
            {
                case 0:
                    // 模式0：WR+FFT组合信号
                    // WR超卖回升 + FFT预测上涨 = 做多
                    buySignal = wrBuy && fftBuy;
                    // WR超买回落 + FFT预测下跌 = 做空
                    sellSignal = wrSell && fftSell;
                    // 多头平仓：WR超买或FFT预测下跌
                    exitLongSignal = wrOverbought || fftSell;
                    // 空头平仓：WR超卖或FFT预测上涨
                    exitShortSignal = wrOversold || fftBuy;
                    break;

                case 1:
                    // 模式1：仅WR信号
                    buySignal = wrBuy;
                    sellSignal = wrSell;
                    exitLongSignal = wrOverbought;
                    exitShortSignal = wrOversold;
                    break;

                case 2:
                    // 模式2：仅FFT信号
                    // FFT趋势反转做多
                    buySignal = fftBuy && predictedTrend > Math.Abs((double)q.Close) * 0.001;
                    // FFT趋势反转做空
                    sellSignal = fftSell && Math.Abs(predictedTrend) > Math.Abs((double)q.Close) * 0.001;
                    exitLongSignal = fftSell;
                    exitShortSignal = fftBuy;
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
