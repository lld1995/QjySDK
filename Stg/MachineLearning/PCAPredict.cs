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
    /// 基于PCA(主成分分析)的交易策略
    /// 策略逻辑：
    /// - 使用PCA对多维技术指标进行降维
    /// - 提取主成分作为市场状态的综合表征
    /// - 根据主成分的变化趋势和异常检测进行交易
    /// PCA特点：
    /// - 通过正交变换将相关变量转换为线性不相关的主成分
    /// - 第一主成分捕获数据最大方差方向（市场主要趋势）
    /// - 可用于降维、去噪和异常检测
    /// - 主成分得分的突变可能预示市场转折
    /// </summary>
    public class PCAPredict : StgBase
    {
        public PCAPredict()
        {
        }

        public PCAPredict(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // PCA模型参数
            sd.ArgDic["lookback"] = 20;              // 回看周期（特征窗口）
            sd.ArgDic["numComponents"] = 3;          // 保留的主成分数量
            sd.ArgDic["trainPeriod"] = 100;          // 训练周期数
            sd.ArgDic["retrainInterval"] = 50;       // 重新训练间隔
            sd.ArgDic["anomalyThreshold"] = 2.0;     // 异常检测阈值（标准差倍数）

            // 交易信号参数
            sd.ArgDic["pc1Threshold"] = 0.5;         // PC1趋势阈值
            sd.ArgDic["momentumPeriod"] = 5;         // 动量计算周期
            sd.ArgDic["signalSmooth"] = 3;           // 信号平滑周期

            // 风控参数
            sd.ArgDic["atrPeriod"] = 14;             // ATR周期
            sd.ArgDic["atrMultiplier"] = 2.0;        // ATR止损倍数
            sd.ArgDic["takeProfitMultiplier"] = 3.0; // ATR止盈倍数
            sd.ArgDic["mode"] = 0;                   // 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;               // 发单模式

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // 参数说明
            sd.ArgDescDic["lookback"] = new ArgDesc() { Text = "回看周期", Explain = "特征计算窗口长度" };
            sd.ArgDescDic["numComponents"] = new ArgDesc() { Text = "主成分数", Explain = "保留的主成分数量" };
            sd.ArgDescDic["trainPeriod"] = new ArgDesc() { Text = "训练周期", Explain = "用于训练PCA的历史K线数量" };
            sd.ArgDescDic["retrainInterval"] = new ArgDesc() { Text = "重训间隔", Explain = "每隔多少根K线重新训练模型" };
            sd.ArgDescDic["anomalyThreshold"] = new ArgDesc() { Text = "异常阈值", Explain = "异常检测的标准差倍数" };
            sd.ArgDescDic["pc1Threshold"] = new ArgDesc() { Text = "PC1阈值", Explain = "第一主成分趋势判断阈值" };
            sd.ArgDescDic["momentumPeriod"] = new ArgDesc() { Text = "动量周期", Explain = "主成分动量计算周期" };
            sd.ArgDescDic["signalSmooth"] = new ArgDesc() { Text = "信号平滑", Explain = "交易信号平滑周期" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "计算ATR的周期" };
            sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "止损倍数", Explain = "ATR止损倍数" };
            sd.ArgDescDic["takeProfitMultiplier"] = new ArgDesc() { Text = "止盈倍数", Explain = "ATR止盈倍数" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0:双向 1:仅做多 2:仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0:立即 1:下个开盘" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0:固定手数 1:固定金额" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 3;

            // 颜色配置
            sd.ColorDic["main-stopLoss"] = "#F44336";
            sd.ColorDic["main-takeProfit"] = "#4CAF50";
            sd.ColorDic["sub1-pc1"] = "#2196F3";
            sd.ColorDic["sub1-pc2"] = "#FF9800";
            sd.ColorDic["sub1-pc3"] = "#9C27B0";
            sd.ColorDic["sub2-anomaly"] = "#E91E63";
            sd.ColorDic["sub3-signal"] = "#00BCD4";

            return sd;
        }

        #region PCA模型实现

        /// <summary>
        /// PCA主成分分析模型
        /// 实现步骤：
        /// 1. 数据标准化（零均值，单位方差）
        /// 2. 计算协方差矩阵
        /// 3. 特征值分解
        /// 4. 选择前k个主成分
        /// 5. 投影到主成分空间
        /// </summary>
        private class PCAModel
        {
            private int _numComponents;
            private double[]? _mean;           // 各特征均值
            private double[]? _std;            // 各特征标准差
            private double[][]? _components;   // 主成分向量（特征向量）
            private double[]? _explainedVarianceRatio; // 各主成分解释的方差比例
            private double[]? _eigenvalues;    // 特征值
            private int _numFeatures;

            public bool IsTrained { get; private set; }
            public double[] ExplainedVarianceRatio => _explainedVarianceRatio ?? Array.Empty<double>();
            public double[][] Components => _components ?? Array.Empty<double[]>();

            public PCAModel(int numComponents)
            {
                _numComponents = numComponents;
                IsTrained = false;
            }

            /// <summary>
            /// 训练PCA模型
            /// </summary>
            public void Fit(double[][] X)
            {
                if (X.Length == 0) return;

                int n = X.Length;
                _numFeatures = X[0].Length;

                // 1. 计算均值和标准差
                _mean = new double[_numFeatures];
                _std = new double[_numFeatures];

                for (int j = 0; j < _numFeatures; j++)
                {
                    double sum = 0;
                    for (int i = 0; i < n; i++)
                        sum += X[i][j];
                    _mean[j] = sum / n;

                    double sumSq = 0;
                    for (int i = 0; i < n; i++)
                        sumSq += Math.Pow(X[i][j] - _mean[j], 2);
                    _std[j] = Math.Sqrt(sumSq / n);
                    if (_std[j] < 1e-10) _std[j] = 1; // 避免除零
                }

                // 2. 标准化数据
                var XStd = new double[n][];
                for (int i = 0; i < n; i++)
                {
                    XStd[i] = new double[_numFeatures];
                    for (int j = 0; j < _numFeatures; j++)
                        XStd[i][j] = (X[i][j] - _mean[j]) / _std[j];
                }

                // 3. 计算协方差矩阵
                var cov = new double[_numFeatures][];
                for (int i = 0; i < _numFeatures; i++)
                {
                    cov[i] = new double[_numFeatures];
                    for (int j = 0; j < _numFeatures; j++)
                    {
                        double sum = 0;
                        for (int k = 0; k < n; k++)
                            sum += XStd[k][i] * XStd[k][j];
                        cov[i][j] = sum / (n - 1);
                    }
                }

                // 4. 使用幂迭代法计算特征值和特征向量
                var eigenvectors = new List<double[]>();
                var eigenvalues = new List<double>();

                var covCopy = cov.Select(row => row.ToArray()).ToArray();

                int numComp = Math.Min(_numComponents, _numFeatures);
                for (int comp = 0; comp < numComp; comp++)
                {
                    var (eigenvalue, eigenvector) = PowerIteration(covCopy, 100);
                    eigenvalues.Add(eigenvalue);
                    eigenvectors.Add(eigenvector);

                    // 矩阵收缩：移除已找到的主成分
                    for (int i = 0; i < _numFeatures; i++)
                    {
                        for (int j = 0; j < _numFeatures; j++)
                        {
                            covCopy[i][j] -= eigenvalue * eigenvector[i] * eigenvector[j];
                        }
                    }
                }

                _eigenvalues = eigenvalues.ToArray();
                _components = eigenvectors.ToArray();

                // 5. 计算解释方差比例
                double totalVariance = _eigenvalues.Sum();
                _explainedVarianceRatio = new double[_eigenvalues.Length];
                for (int i = 0; i < _eigenvalues.Length; i++)
                    _explainedVarianceRatio[i] = totalVariance > 0 ? _eigenvalues[i] / totalVariance : 0;

                IsTrained = true;
            }

            /// <summary>
            /// 幂迭代法求最大特征值和对应特征向量
            /// </summary>
            private (double eigenvalue, double[] eigenvector) PowerIteration(double[][] matrix, int maxIter)
            {
                int n = matrix.Length;
                var v = new double[n];
                var random = new Random(42);

                // 随机初始化
                for (int i = 0; i < n; i++)
                    v[i] = random.NextDouble() - 0.5;

                // 归一化
                double norm = Math.Sqrt(v.Sum(x => x * x));
                for (int i = 0; i < n; i++)
                    v[i] /= norm;

                double eigenvalue = 0;

                for (int iter = 0; iter < maxIter; iter++)
                {
                    // 矩阵向量乘法
                    var vNew = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = 0; j < n; j++)
                            vNew[i] += matrix[i][j] * v[j];
                    }

                    // 计算特征值（Rayleigh商）
                    eigenvalue = 0;
                    for (int i = 0; i < n; i++)
                        eigenvalue += v[i] * vNew[i];

                    // 归一化
                    norm = Math.Sqrt(vNew.Sum(x => x * x));
                    if (norm < 1e-10) break;

                    for (int i = 0; i < n; i++)
                        v[i] = vNew[i] / norm;
                }

                return (eigenvalue, v);
            }

            /// <summary>
            /// 将数据投影到主成分空间
            /// </summary>
            public double[] Transform(double[] x)
            {
                if (!IsTrained || _mean == null || _std == null || _components == null)
                    return Array.Empty<double>();

                // 标准化
                var xStd = new double[_numFeatures];
                for (int j = 0; j < _numFeatures; j++)
                    xStd[j] = (x[j] - _mean[j]) / _std[j];

                // 投影
                var result = new double[_components.Length];
                for (int i = 0; i < _components.Length; i++)
                {
                    for (int j = 0; j < _numFeatures; j++)
                        result[i] += xStd[j] * _components[i][j];
                }

                return result;
            }

            /// <summary>
            /// 计算重构误差（用于异常检测）
            /// 重构误差大说明当前数据点与历史模式差异大
            /// </summary>
            public double GetReconstructionError(double[] x)
            {
                if (!IsTrained || _mean == null || _std == null || _components == null)
                    return 0;

                // 标准化
                var xStd = new double[_numFeatures];
                for (int j = 0; j < _numFeatures; j++)
                    xStd[j] = (x[j] - _mean[j]) / _std[j];

                // 投影到主成分空间
                var projected = Transform(x);

                // 重构
                var reconstructed = new double[_numFeatures];
                for (int i = 0; i < _components.Length; i++)
                {
                    for (int j = 0; j < _numFeatures; j++)
                        reconstructed[j] += projected[i] * _components[i][j];
                }

                // 计算重构误差（MSE）
                double error = 0;
                for (int j = 0; j < _numFeatures; j++)
                    error += Math.Pow(xStd[j] - reconstructed[j], 2);

                return Math.Sqrt(error / _numFeatures);
            }
        }

        #endregion

        #region 特征工程

        /// <summary>
        /// PCA特征提取器
        /// </summary>
        private static class PCAFeatureExtractor
        {
            /// <summary>
            /// 提取技术指标特征用于PCA分析
            /// </summary>
            public static double[] ExtractFeatures(List<SkQuote> quotes, int lookback)
            {
                if (quotes.Count < lookback) return Array.Empty<double>();

                var recentQuotes = quotes.Skip(quotes.Count - lookback).ToList();
                var features = new List<double>();

                // 1. 价格变化率特征（多个时间尺度）
                for (int i = 1; i <= 5 && i < recentQuotes.Count; i++)
                {
                    var prev = recentQuotes[recentQuotes.Count - 1 - i].Close;
                    var curr = recentQuotes[recentQuotes.Count - 1].Close;
                    features.Add(prev != 0 ? (double)((curr - prev) / prev) : 0);
                }

                // 2. 成交量变化率
                for (int i = 1; i <= 3 && i < recentQuotes.Count; i++)
                {
                    var prev = recentQuotes[recentQuotes.Count - 1 - i].Volume;
                    var curr = recentQuotes[recentQuotes.Count - 1].Volume;
                    features.Add(prev != 0 ? (double)((curr - prev) / prev) : 0);
                }

                // 3. K线形态特征
                var lastQuote = recentQuotes.Last();
                // 振幅
                features.Add(lastQuote.Low != 0 ? (double)((lastQuote.High - lastQuote.Low) / lastQuote.Low) : 0);
                // 实体比例
                if (lastQuote.High != lastQuote.Low)
                    features.Add((double)(Math.Abs(lastQuote.Close - lastQuote.Open) / (lastQuote.High - lastQuote.Low)));
                else
                    features.Add(0);
                // 上影线比例
                if (lastQuote.High != lastQuote.Low)
                    features.Add((double)((lastQuote.High - Math.Max(lastQuote.Open, lastQuote.Close)) / (lastQuote.High - lastQuote.Low)));
                else
                    features.Add(0);
                // 下影线比例
                if (lastQuote.High != lastQuote.Low)
                    features.Add((double)((Math.Min(lastQuote.Open, lastQuote.Close) - lastQuote.Low) / (lastQuote.High - lastQuote.Low)));
                else
                    features.Add(0);

                // 4. 收盘价相对位置 (0-1)
                if (lastQuote.High != lastQuote.Low)
                    features.Add((double)((lastQuote.Close - lastQuote.Low) / (lastQuote.High - lastQuote.Low)));
                else
                    features.Add(0.5);

                // 5. 移动平均特征
                var closes = recentQuotes.Select(q => (double)q.Close).ToList();
                double ma5 = closes.TakeLast(5).Average();
                double ma10 = closes.TakeLast(10).Average();
                double ma20 = closes.Average();
                double currentClose = (double)lastQuote.Close;

                features.Add(ma5 != 0 ? (currentClose - ma5) / ma5 : 0);
                features.Add(ma10 != 0 ? (currentClose - ma10) / ma10 : 0);
                features.Add(ma20 != 0 ? (currentClose - ma20) / ma20 : 0);
                features.Add(ma10 != 0 ? (ma5 - ma10) / ma10 : 0);
                features.Add(ma20 != 0 ? (ma10 - ma20) / ma20 : 0);

                // 6. 波动率特征
                var returns = new List<double>();
                for (int i = 1; i < closes.Count; i++)
                {
                    if (closes[i - 1] != 0)
                        returns.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
                }
                if (returns.Count > 0)
                {
                    double meanReturn = returns.Average();
                    double volatility = Math.Sqrt(returns.Average(r => Math.Pow(r - meanReturn, 2)));
                    features.Add(volatility);
                    // 偏度
                    double skewness = returns.Count > 2 ?
                        returns.Average(r => Math.Pow((r - meanReturn) / (volatility + 1e-10), 3)) : 0;
                    features.Add(skewness);
                    // 峰度
                    double kurtosis = returns.Count > 3 ?
                        returns.Average(r => Math.Pow((r - meanReturn) / (volatility + 1e-10), 4)) - 3 : 0;
                    features.Add(kurtosis);
                }
                else
                {
                    features.Add(0);
                    features.Add(0);
                    features.Add(0);
                }

                // 7. RSI特征
                double rsi = CalculateRSI(closes, 14);
                features.Add(rsi / 100.0);
                double rsi6 = CalculateRSI(closes, 6);
                features.Add(rsi6 / 100.0);

                // 8. 动量特征
                if (closes.Count >= 10)
                {
                    double momentum = closes.Last() - closes[closes.Count - 10];
                    features.Add(closes[closes.Count - 10] != 0 ? momentum / closes[closes.Count - 10] : 0);
                }
                else
                {
                    features.Add(0);
                }

                // 9. 成交量均值比
                var volumes = recentQuotes.Select(q => (double)q.Volume).ToList();
                double avgVolume = volumes.Average();
                features.Add(avgVolume != 0 ? volumes.Last() / avgVolume : 1);

                // 10. 价格趋势特征（线性回归斜率）
                double slope = CalculateSlope(closes);
                features.Add(slope);

                // 11. MACD相关特征
                double ema12 = CalculateEMA(closes, 12);
                double ema26 = CalculateEMA(closes, 26);
                double macd = ema12 - ema26;
                features.Add(currentClose != 0 ? macd / currentClose : 0);

                // 12. 布林带位置
                double stdDev = Math.Sqrt(closes.Average(c => Math.Pow(c - ma20, 2)));
                double upperBand = ma20 + 2 * stdDev;
                double lowerBand = ma20 - 2 * stdDev;
                if (upperBand != lowerBand)
                    features.Add((currentClose - lowerBand) / (upperBand - lowerBand));
                else
                    features.Add(0.5);

                // 13. 高低点特征
                double highest = closes.Max();
                double lowest = closes.Min();
                if (highest != lowest)
                {
                    features.Add((currentClose - lowest) / (highest - lowest));
                    features.Add((highest - currentClose) / (highest - lowest));
                }
                else
                {
                    features.Add(0.5);
                    features.Add(0.5);
                }

                // 14. 威廉指标 %R
                if (highest != lowest)
                    features.Add((highest - currentClose) / (highest - lowest));
                else
                    features.Add(0.5);

                // 15. 成交量趋势
                double volumeSlope = CalculateSlope(volumes);
                features.Add(volumeSlope);

                return features.ToArray();
            }

            private static double CalculateRSI(List<double> prices, int period)
            {
                if (prices.Count < period + 1) return 50;

                double gainSum = 0, lossSum = 0;
                for (int i = prices.Count - period; i < prices.Count; i++)
                {
                    double change = prices[i] - prices[i - 1];
                    if (change > 0) gainSum += change;
                    else lossSum -= change;
                }

                if (lossSum == 0) return 100;
                if (gainSum == 0) return 0;

                double rs = gainSum / lossSum;
                return 100 - (100 / (1 + rs));
            }

            private static double CalculateSlope(List<double> values)
            {
                if (values.Count < 2) return 0;

                int n = values.Count;
                double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

                for (int i = 0; i < n; i++)
                {
                    sumX += i;
                    sumY += values[i];
                    sumXY += i * values[i];
                    sumX2 += i * i;
                }

                double denominator = n * sumX2 - sumX * sumX;
                if (Math.Abs(denominator) < 1e-10) return 0;

                double slope = (n * sumXY - sumX * sumY) / denominator;
                double avgPrice = sumY / n;
                return avgPrice != 0 ? slope / avgPrice : 0;
            }

            private static double CalculateEMA(List<double> values, int period)
            {
                if (values.Count == 0) return 0;
                if (values.Count < period) period = values.Count;

                double multiplier = 2.0 / (period + 1);
                double ema = values.Take(period).Average();

                for (int i = period; i < values.Count; i++)
                {
                    ema = (values[i] - ema) * multiplier + ema;
                }

                return ema;
            }
        }

        #endregion

        #region 状态管理

        private class State
        {
            public int Status { get; set; }         // 0:空仓 1:多头 2:空头
            public decimal Num { get; set; }        // 持仓数量
            public decimal EntryPrice { get; set; } // 入场价格
            public decimal StopLoss { get; set; }   // 止损价
            public decimal TakeProfit { get; set; } // 止盈价
            public int BarCount { get; set; }       // K线计数
            public PCAModel? Model { get; set; }    // PCA模型
            public int LastTrainBar { get; set; }   // 上次训练的K线索引
            public List<double[]> PcHistory { get; set; } = new List<double[]>(); // 主成分历史
            public List<double> AnomalyHistory { get; set; } = new List<double>(); // 异常分数历史
            public List<double> SignalHistory { get; set; } = new List<double>(); // 信号历史
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int lookback = (int)ArgDic["lookback"];
            int numComponents = (int)ArgDic["numComponents"];
            int trainPeriod = (int)ArgDic["trainPeriod"];
            int retrainInterval = (int)ArgDic["retrainInterval"];
            double anomalyThreshold = Convert.ToDouble(ArgDic["anomalyThreshold"]);
            double pc1Threshold = Convert.ToDouble(ArgDic["pc1Threshold"]);
            int momentumPeriod = (int)ArgDic["momentumPeriod"];
            int signalSmooth = (int)ArgDic["signalSmooth"];
            int atrPeriod = (int)ArgDic["atrPeriod"];
            double atrMultiplier = Convert.ToDouble(ArgDic["atrMultiplier"]);
            double takeProfitMultiplier = Convert.ToDouble(ArgDic["takeProfitMultiplier"]);
            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];

            // 检查数据量
            int minBars = Math.Max(lookback + trainPeriod, atrPeriod + 1);
            if (tu.QuoteList.Count < minBars) return;

            var q = tu.QuoteList.Last();
            var sk = tu.GetStateKey();

            // 获取或创建状态
            if (!_stateDic.TryGetValue(sk, out State? s) || s == null)
            {
                s = new State();
                _stateDic[sk] = s;
            }

            s.BarCount++;

            // 计算ATR
            var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
            var atr = atrList.LastOrDefault()?.Atr;
            if (!atr.HasValue || atr.Value <= 0) return;

            decimal atrValue = (decimal)atr.Value;

            // 检查是否需要训练/重训练PCA模型
            if (s.Model == null)
            {
                s.Model = new PCAModel(numComponents);
            }

            if (!s.Model.IsTrained || (s.BarCount - s.LastTrainBar >= retrainInterval))
            {
                var trainingData = PrepareTrainingData(tu.QuoteList, lookback, trainPeriod);
                if (trainingData.Length > 0)
                {
                    s.Model.Fit(trainingData);
                    s.LastTrainBar = s.BarCount;
                }
            }

            // 提取当前特征并进行PCA变换
            double[] pcScores = Array.Empty<double>();
            double anomalyScore = 0;

            if (s.Model != null && s.Model.IsTrained)
            {
                var features = PCAFeatureExtractor.ExtractFeatures(tu.QuoteList, lookback);
                if (features.Length > 0)
                {
                    pcScores = s.Model.Transform(features);
                    anomalyScore = s.Model.GetReconstructionError(features);

                    // 记录历史
                    s.PcHistory.Add(pcScores);
                    s.AnomalyHistory.Add(anomalyScore);

                    // 保持历史长度
                    int maxHistory = Math.Max(momentumPeriod, signalSmooth) + 10;
                    if (s.PcHistory.Count > maxHistory)
                        s.PcHistory.RemoveAt(0);
                    if (s.AnomalyHistory.Count > maxHistory)
                        s.AnomalyHistory.RemoveAt(0);
                }
            }

            // 绘制主成分
            if (pcScores.Length > 0)
            {
                Plot("sub1", "pc1", PlotType.CURVE, pcScores[0]);
                if (pcScores.Length > 1)
                    Plot("sub1", "pc2", PlotType.CURVE, pcScores[1]);
                if (pcScores.Length > 2)
                    Plot("sub1", "pc3", PlotType.CURVE, pcScores[2]);
            }

            // 绘制异常分数
            Plot("sub2", "anomaly", PlotType.CURVE, anomalyScore);

            // 计算交易信号
            double signal = CalculateSignal(s, pcScores, anomalyScore, momentumPeriod, signalSmooth, anomalyThreshold);
            s.SignalHistory.Add(signal);
            if (s.SignalHistory.Count > 50)
                s.SignalHistory.RemoveAt(0);

            // 绘制信号
            Plot("sub3", "signal", PlotType.CURVE, signal);

            // 计算手数
            var num = (decimal)ArgDic["lots"];
            var lotsMode = (int)ArgDic["lotsMode"];
            if (lotsMode == 1)
            {
                var symbol = GetSymbol(tu.MktSymbol);
                num = ((decimal)ArgDic["money"] / (q.Close * symbol.multiplier * symbol.margin_ratio));
                if (symbol.symbol_type == (int)SymbolType.COIN)
                {
                    num = Math.Floor(num * 1000) / 1000m;
                }
                else
                {
                    num = Math.Floor(num);
                }
            }

            // 交易逻辑
            if (s.Status == 0)
            {
                // 空仓：根据信号入场
                // 信号 > 阈值 且 异常分数不太高（避免在异常时入场）
                bool isNormalMarket = anomalyScore < anomalyThreshold * 1.5;

                if (signal > pc1Threshold && mode != 2 && isNormalMarket)
                {
                    // 看多信号
                    s.Status = 1;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    s.StopLoss = q.Close - atrValue * (decimal)atrMultiplier;
                    s.TakeProfit = q.Close + atrValue * (decimal)takeProfitMultiplier;

                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);

                    Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                    Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);
                }
                else if (signal < -pc1Threshold && mode != 1 && isNormalMarket)
                {
                    // 看空信号
                    s.Status = 2;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    s.StopLoss = q.Close + atrValue * (decimal)atrMultiplier;
                    s.TakeProfit = q.Close - atrValue * (decimal)takeProfitMultiplier;

                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);

                    Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                    Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);
                }
            }
            else if (s.Status == 1)
            {
                // 多头持仓：检查止损止盈或异常退出
                Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);

                bool shouldExit = false;

                // 止损止盈
                if (q.Close <= s.StopLoss || q.Close >= s.TakeProfit)
                {
                    shouldExit = true;
                }
                // 异常检测退出：市场出现异常模式时平仓
                else if (anomalyScore > anomalyThreshold * 2)
                {
                    shouldExit = true;
                }
                // 信号反转退出
                else if (signal < -pc1Threshold * 0.5)
                {
                    shouldExit = true;
                }

                if (shouldExit)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0;
                    s.Num = 0;
                }
            }
            else if (s.Status == 2)
            {
                // 空头持仓：检查止损止盈或异常退出
                Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);

                bool shouldExit = false;

                // 止损止盈
                if (q.Close >= s.StopLoss || q.Close <= s.TakeProfit)
                {
                    shouldExit = true;
                }
                // 异常检测退出
                else if (anomalyScore > anomalyThreshold * 2)
                {
                    shouldExit = true;
                }
                // 信号反转退出
                else if (signal > pc1Threshold * 0.5)
                {
                    shouldExit = true;
                }

                if (shouldExit)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0;
                    s.Num = 0;
                }
            }
        }

        /// <summary>
        /// 计算交易信号
        /// 基于PC1的动量和趋势方向
        /// </summary>
        private double CalculateSignal(State s, double[] pcScores, double anomalyScore, 
            int momentumPeriod, int signalSmooth, double anomalyThreshold)
        {
            if (pcScores.Length == 0 || s.PcHistory.Count < momentumPeriod)
                return 0;

            // 1. PC1动量：当前PC1与N期前的差值
            double pc1Current = pcScores[0];
            double pc1Past = s.PcHistory[s.PcHistory.Count - momentumPeriod][0];
            double pc1Momentum = pc1Current - pc1Past;

            // 2. PC1趋势方向：最近几期的斜率
            var recentPc1 = s.PcHistory.TakeLast(signalSmooth).Select(p => p[0]).ToList();
            double pc1Slope = CalculateSlope(recentPc1);

            // 3. 综合信号：动量 + 趋势
            double signal = pc1Momentum * 0.6 + pc1Slope * 10 * 0.4;

            // 4. 异常惩罚：异常分数高时降低信号强度
            if (anomalyScore > anomalyThreshold)
            {
                signal *= Math.Max(0.1, 1 - (anomalyScore - anomalyThreshold) / anomalyThreshold);
            }

            // 5. 信号平滑
            if (s.SignalHistory.Count >= signalSmooth)
            {
                var recentSignals = s.SignalHistory.TakeLast(signalSmooth - 1).ToList();
                recentSignals.Add(signal);
                signal = recentSignals.Average();
            }

            return signal;
        }

        private double CalculateSlope(List<double> values)
        {
            if (values.Count < 2) return 0;

            int n = values.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

            for (int i = 0; i < n; i++)
            {
                sumX += i;
                sumY += values[i];
                sumXY += i * values[i];
                sumX2 += i * i;
            }

            double denominator = n * sumX2 - sumX * sumX;
            if (Math.Abs(denominator) < 1e-10) return 0;

            return (n * sumXY - sumX * sumY) / denominator;
        }

        /// <summary>
        /// 准备训练数据
        /// </summary>
        private double[][] PrepareTrainingData(List<SkQuote> quotes, int lookback, int trainPeriod)
        {
            var data = new List<double[]>();

            int startIdx = Math.Max(lookback, quotes.Count - trainPeriod);
            int endIdx = quotes.Count;

            for (int i = startIdx; i < endIdx; i++)
            {
                var subQuotes = quotes.Take(i + 1).ToList();
                var features = PCAFeatureExtractor.ExtractFeatures(subQuotes, lookback);

                if (features.Length > 0)
                {
                    data.Add(features);
                }
            }

            return data.ToArray();
        }
    }
}
