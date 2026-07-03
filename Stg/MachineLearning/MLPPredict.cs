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
    /// 基于MLP(多层感知机)的价格预测交易策略
    /// 策略逻辑：
    /// - 使用MLP神经网络预测下一根K线的价格走势
    /// - 预测上涨则做多，预测下跌则做空
    /// - 结合ATR进行止损止盈管理
    /// MLP特点：
    /// - 多层全连接神经网络
    /// - 使用ReLU激活函数
    /// - 反向传播算法训练
    /// - 支持批量归一化和Dropout
    /// </summary>
    public class MLPPredict : StgBase
    {
        public MLPPredict()
        {
        }

        public MLPPredict(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // MLP模型参数
            sd.ArgDic["lookback"] = 20;              // 回看周期（特征窗口）
            sd.ArgDic["hiddenLayers"] = "64,32,16";  // 隐藏层结构（逗号分隔）
            sd.ArgDic["learningRate"] = 0.001;       // 学习率
            sd.ArgDic["epochs"] = 100;               // 训练轮数
            sd.ArgDic["batchSize"] = 32;             // 批量大小
            sd.ArgDic["dropout"] = 0.2;              // Dropout比率
            sd.ArgDic["l2Lambda"] = 0.001;           // L2正则化系数
            sd.ArgDic["trainPeriod"] = 200;          // 训练周期数
            sd.ArgDic["retrainInterval"] = 50;       // 重新训练间隔

            // 交易参数
            sd.ArgDic["threshold"] = 0.001;          // 预测阈值（涨跌幅度）
            sd.ArgDic["atrPeriod"] = 14;             // ATR周期
            sd.ArgDic["atrMultiplier"] = 2.0;        // ATR止损倍数
            sd.ArgDic["takeProfitMultiplier"] = 3.0; // ATR止盈倍数
            sd.ArgDic["stopCooldownBars"] = 5;       // 止损后冷却K线数
            sd.ArgDic["mode"] = 0;                   // 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;               // 发单模式

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // 参数说明
            sd.ArgDescDic["lookback"] = new ArgDesc() { Text = "回看周期", Explain = "特征计算窗口长度", Type = "number" };
            sd.ArgDescDic["hiddenLayers"] = new ArgDesc() { Text = "隐藏层结构", Explain = "各隐藏层神经元数量，逗号分隔", Type = "number" };
            sd.ArgDescDic["learningRate"] = new ArgDesc() { Text = "学习率", Explain = "模型训练学习率", Type = "number" };
            sd.ArgDescDic["epochs"] = new ArgDesc() { Text = "训练轮数", Explain = "训练迭代次数", Type = "number" };
            sd.ArgDescDic["batchSize"] = new ArgDesc() { Text = "批量大小", Explain = "每批训练样本数", Type = "number" };
            sd.ArgDescDic["dropout"] = new ArgDesc() { Text = "Dropout", Explain = "Dropout比率防止过拟合", Type = "number" };
            sd.ArgDescDic["l2Lambda"] = new ArgDesc() { Text = "L2正则化", Explain = "L2正则化系数", Type = "number" };
            sd.ArgDescDic["trainPeriod"] = new ArgDesc() { Text = "训练周期", Explain = "用于训练的历史K线数量", Type = "number" };
            sd.ArgDescDic["retrainInterval"] = new ArgDesc() { Text = "重训间隔", Explain = "每隔多少根K线重新训练模型", Type = "number" };
            sd.ArgDescDic["threshold"] = new ArgDesc() { Text = "预测阈值", Explain = "预测涨跌幅超过此值才交易", Type = "number" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "计算ATR的周期", Type = "number" };
            sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "止损倍数", Explain = "ATR止损倍数", Type = "number" };
            sd.ArgDescDic["takeProfitMultiplier"] = new ArgDesc() { Text = "止盈倍数", Explain = "ATR止盈倍数", Type = "number" };
            sd.ArgDescDic["stopCooldownBars"] = new ArgDesc() { Text = "止损重入保护", Explain = "止损后至少等待N根K线，且预测方向必须回中性/反向后才允许同向重新开仓,0为不冷却", Type = "number" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };

            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 颜色配置
            sd.ColorDic["main-stopLoss"] = "#F44336";
            sd.ColorDic["main-takeProfit"] = "#4CAF50";
            sd.ColorDic["sub1-prediction"] = "#2196F3";
            sd.ColorDic["sub2-loss"] = "#FF9800";

            return sd;
        }

        #region MLP神经网络实现

        /// <summary>
        /// MLP层（全连接层）
        /// </summary>
        private class DenseLayer
        {
            public double[,] Weights { get; private set; }
            public double[] Biases { get; private set; }
            public double[,] WeightGradients { get; private set; }
            public double[] BiasGradients { get; private set; }
            public double[] LastInput { get; private set; }
            public double[] LastOutput { get; private set; }
            public double[] LastPreActivation { get; private set; }
            public bool UseReLU { get; set; }
            public double DropoutRate { get; set; }
            public bool[] DropoutMask { get; private set; }

            private int _inputSize;
            private int _outputSize;
            private Random _random;

            public DenseLayer(int inputSize, int outputSize, Random random, bool useReLU = true, double dropoutRate = 0)
            {
                _inputSize = inputSize;
                _outputSize = outputSize;
                _random = random;
                UseReLU = useReLU;
                DropoutRate = dropoutRate;

                // He初始化
                double stddev = Math.Sqrt(2.0 / inputSize);
                Weights = new double[inputSize, outputSize];
                Biases = new double[outputSize];
                WeightGradients = new double[inputSize, outputSize];
                BiasGradients = new double[outputSize];
                LastInput = new double[inputSize];
                LastOutput = new double[outputSize];
                LastPreActivation = new double[outputSize];
                DropoutMask = new bool[outputSize];

                for (int i = 0; i < inputSize; i++)
                {
                    for (int j = 0; j < outputSize; j++)
                    {
                        Weights[i, j] = NextGaussian(random) * stddev;
                    }
                }
            }

            private double NextGaussian(Random random)
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = 1.0 - random.NextDouble();
                return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            }

            public double[] Forward(double[] input, bool training = true)
            {
                Array.Copy(input, LastInput, input.Length);

                for (int j = 0; j < _outputSize; j++)
                {
                    double sum = Biases[j];
                    for (int i = 0; i < _inputSize; i++)
                    {
                        sum += input[i] * Weights[i, j];
                    }
                    LastPreActivation[j] = sum;

                    // 激活函数
                    if (UseReLU)
                        LastOutput[j] = Math.Max(0, sum);
                    else
                        LastOutput[j] = sum; // 输出层不使用激活

                    // Dropout
                    if (training && DropoutRate > 0)
                    {
                        DropoutMask[j] = _random.NextDouble() > DropoutRate;
                        if (!DropoutMask[j])
                            LastOutput[j] = 0;
                        else
                            LastOutput[j] /= (1 - DropoutRate); // 缩放
                    }
                    else
                    {
                        DropoutMask[j] = true;
                    }
                }

                return LastOutput;
            }

            public double[] Backward(double[] outputGradient, double l2Lambda)
            {
                var inputGradient = new double[_inputSize];

                for (int j = 0; j < _outputSize; j++)
                {
                    double grad = outputGradient[j];

                    // Dropout梯度
                    if (!DropoutMask[j])
                    {
                        grad = 0;
                    }
                    else if (DropoutRate > 0)
                    {
                        grad /= (1 - DropoutRate);
                    }

                    // ReLU梯度
                    if (UseReLU && LastPreActivation[j] <= 0)
                        grad = 0;

                    BiasGradients[j] += grad;

                    for (int i = 0; i < _inputSize; i++)
                    {
                        WeightGradients[i, j] += grad * LastInput[i] + l2Lambda * Weights[i, j];
                        inputGradient[i] += grad * Weights[i, j];
                    }
                }

                return inputGradient;
            }

            public void UpdateWeights(double learningRate, int batchSize)
            {
                for (int i = 0; i < _inputSize; i++)
                {
                    for (int j = 0; j < _outputSize; j++)
                    {
                        Weights[i, j] -= learningRate * WeightGradients[i, j] / batchSize;
                        WeightGradients[i, j] = 0;
                    }
                }

                for (int j = 0; j < _outputSize; j++)
                {
                    Biases[j] -= learningRate * BiasGradients[j] / batchSize;
                    BiasGradients[j] = 0;
                }
            }
        }

        /// <summary>
        /// MLP多层感知机模型
        /// </summary>
        private class MLPModel
        {
            private List<DenseLayer> _layers;
            private double _learningRate;
            private double _l2Lambda;
            private int _epochs;
            private int _batchSize;
            private Random _random;
            private double _lastLoss;

            public bool IsTrained { get; private set; }
            public double LastLoss => _lastLoss;

            public MLPModel(int inputSize, int[] hiddenSizes, double learningRate, int epochs, 
                int batchSize, double dropout, double l2Lambda)
            {
                _learningRate = learningRate;
                _l2Lambda = l2Lambda;
                _epochs = epochs;
                _batchSize = batchSize;
                _random = new Random(42);
                _layers = new List<DenseLayer>();

                // 构建网络层
                int prevSize = inputSize;
                for (int i = 0; i < hiddenSizes.Length; i++)
                {
                    _layers.Add(new DenseLayer(prevSize, hiddenSizes[i], _random, true, dropout));
                    prevSize = hiddenSizes[i];
                }

                // 输出层（无激活函数，无Dropout）
                _layers.Add(new DenseLayer(prevSize, 1, _random, false, 0));

                IsTrained = false;
            }

            public void Train(double[][] X, double[] y)
            {
                if (X.Length == 0 || y.Length == 0) return;

                int numSamples = X.Length;

                // 数据标准化
                var (normalizedX, means, stds) = Normalize(X);

                for (int epoch = 0; epoch < _epochs; epoch++)
                {
                    // 打乱数据
                    var indices = Enumerable.Range(0, numSamples).OrderBy(_ => _random.Next()).ToArray();

                    double totalLoss = 0;
                    int batchCount = 0;

                    for (int batchStart = 0; batchStart < numSamples; batchStart += _batchSize)
                    {
                        int batchEnd = Math.Min(batchStart + _batchSize, numSamples);
                        int actualBatchSize = batchEnd - batchStart;

                        for (int b = batchStart; b < batchEnd; b++)
                        {
                            int idx = indices[b];

                            // 前向传播
                            double[] output = Forward(normalizedX[idx], true);

                            // 计算损失（MSE）
                            double error = output[0] - y[idx];
                            totalLoss += error * error;

                            // 反向传播
                            var grad = new double[] { 2 * error };
                            for (int l = _layers.Count - 1; l >= 0; l--)
                            {
                                grad = _layers[l].Backward(grad, _l2Lambda);
                            }
                        }

                        // 更新权重
                        foreach (var layer in _layers)
                        {
                            layer.UpdateWeights(_learningRate, actualBatchSize);
                        }

                        batchCount++;
                    }

                    _lastLoss = totalLoss / numSamples;

                    // 早停
                    if (_lastLoss < 1e-8) break;
                }

                IsTrained = true;
            }

            private double[] Forward(double[] input, bool training)
            {
                double[] current = input;
                foreach (var layer in _layers)
                {
                    current = layer.Forward(current, training);
                }
                return current;
            }

            public double Predict(double[] x, double[] means, double[] stds)
            {
                if (!IsTrained) return 0;

                // 标准化输入
                var normalized = new double[x.Length];
                for (int i = 0; i < x.Length; i++)
                {
                    normalized[i] = stds[i] > 1e-10 ? (x[i] - means[i]) / stds[i] : 0;
                }

                var output = Forward(normalized, false);
                return output[0];
            }

            public double Predict(double[] x)
            {
                if (!IsTrained) return 0;
                var output = Forward(x, false);
                return output[0];
            }

            private (double[][], double[], double[]) Normalize(double[][] X)
            {
                if (X.Length == 0) return (X, Array.Empty<double>(), Array.Empty<double>());

                int numFeatures = X[0].Length;
                var means = new double[numFeatures];
                var stds = new double[numFeatures];

                // 计算均值
                for (int j = 0; j < numFeatures; j++)
                {
                    means[j] = X.Average(x => x[j]);
                }

                // 计算标准差
                for (int j = 0; j < numFeatures; j++)
                {
                    double sumSq = X.Sum(x => Math.Pow(x[j] - means[j], 2));
                    stds[j] = Math.Sqrt(sumSq / X.Length);
                    if (stds[j] < 1e-10) stds[j] = 1;
                }

                // 标准化
                var normalized = new double[X.Length][];
                for (int i = 0; i < X.Length; i++)
                {
                    normalized[i] = new double[numFeatures];
                    for (int j = 0; j < numFeatures; j++)
                    {
                        normalized[i][j] = (X[i][j] - means[j]) / stds[j];
                    }
                }

                return (normalized, means, stds);
            }
        }

        #endregion

        #region 特征工程

        /// <summary>
        /// MLP特征提取器
        /// </summary>
        private static class MLPFeatureExtractor
        {
            /// <summary>
            /// 提取技术指标特征
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
                }
                else
                {
                    features.Add(0);
                    features.Add(0);
                }

                // 7. RSI特征
                double rsi = CalculateRSI(closes, 14);
                features.Add(rsi / 100.0);

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

                // 11. 趋势方向编码
                double shortTrend = ma5 > ma10 ? 1 : (ma5 < ma10 ? -1 : 0);
                features.Add(shortTrend);
                double midTrend = ma10 > ma20 ? 1 : (ma10 < ma20 ? -1 : 0);
                features.Add(midTrend);

                // 12. MACD相关特征
                double ema12 = CalculateEMA(closes, 12);
                double ema26 = CalculateEMA(closes, 26);
                double macd = ema12 - ema26;
                features.Add(currentClose != 0 ? macd / currentClose : 0);

                // 13. 布林带位置
                double stdDev = Math.Sqrt(closes.Average(c => Math.Pow(c - ma20, 2)));
                double upperBand = ma20 + 2 * stdDev;
                double lowerBand = ma20 - 2 * stdDev;
                if (upperBand != lowerBand)
                    features.Add((currentClose - lowerBand) / (upperBand - lowerBand));
                else
                    features.Add(0.5);

                // 14. 价格加速度（二阶导数）
                if (closes.Count >= 3)
                {
                    double v1 = closes[closes.Count - 1] - closes[closes.Count - 2];
                    double v2 = closes[closes.Count - 2] - closes[closes.Count - 3];
                    double acceleration = v1 - v2;
                    features.Add(currentClose != 0 ? acceleration / currentClose : 0);
                }
                else
                {
                    features.Add(0);
                }

                // 15. 历史收益率序列（用于时序特征）
                for (int i = 0; i < Math.Min(10, returns.Count); i++)
                {
                    features.Add(returns[returns.Count - 1 - i]);
                }
                // 填充不足的部分
                for (int i = returns.Count; i < 10; i++)
                {
                    features.Add(0);
                }

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
            public int CooldownRemaining { get; set; } // 止损后剩余冷却K线数
            public int BlockedDir { get; set; }     // 止损后被封锁的方向:0无 1多 2空
            public int BarCount { get; set; }       // K线计数
            public MLPModel? Model { get; set; }    // MLP模型
            public int LastTrainBar { get; set; }   // 上次训练的K线索引
            public double[] FeatureMeans { get; set; } = Array.Empty<double>();
            public double[] FeatureStds { get; set; } = Array.Empty<double>();
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int lookback = Convert.ToInt32(ArgDic["lookback"]);
            string hiddenLayersStr = ArgDic["hiddenLayers"].ToString() ?? "64,32,16";
            int[] hiddenLayers = hiddenLayersStr.Split(',').Select(s => int.Parse(s.Trim())).ToArray();
            double learningRate = Convert.ToDouble(ArgDic["learningRate"]);
            int epochs = Convert.ToInt32(ArgDic["epochs"]);
            int batchSize = Convert.ToInt32(ArgDic["batchSize"]);
            double dropout = Convert.ToDouble(ArgDic["dropout"]);
            double l2Lambda = Convert.ToDouble(ArgDic["l2Lambda"]);
            int trainPeriod = Convert.ToInt32(ArgDic["trainPeriod"]);
            int retrainInterval = Convert.ToInt32(ArgDic["retrainInterval"]);
            double threshold = Convert.ToDouble(ArgDic["threshold"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            double atrMultiplier = Convert.ToDouble(ArgDic["atrMultiplier"]);
            double takeProfitMultiplier = Convert.ToDouble(ArgDic["takeProfitMultiplier"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

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

            // 检查是否需要训练/重训练
            bool needTrain = s.Model == null || !s.Model.IsTrained || 
                (s.BarCount - s.LastTrainBar >= retrainInterval);

            if (needTrain)
            {
                var (X, y) = PrepareTrainingData(tu.QuoteList, lookback, trainPeriod);
                if (X.Length > 0)
                {
                    // 计算特征统计量用于标准化
                    int numFeatures = X[0].Length;
                    s.FeatureMeans = new double[numFeatures];
                    s.FeatureStds = new double[numFeatures];

                    for (int j = 0; j < numFeatures; j++)
                    {
                        s.FeatureMeans[j] = X.Average(x => x[j]);
                        double sumSq = X.Sum(x => Math.Pow(x[j] - s.FeatureMeans[j], 2));
                        s.FeatureStds[j] = Math.Sqrt(sumSq / X.Length);
                        if (s.FeatureStds[j] < 1e-10) s.FeatureStds[j] = 1;
                    }

                    // 标准化训练数据
                    var normalizedX = new double[X.Length][];
                    for (int i = 0; i < X.Length; i++)
                    {
                        normalizedX[i] = new double[numFeatures];
                        for (int j = 0; j < numFeatures; j++)
                        {
                            normalizedX[i][j] = (X[i][j] - s.FeatureMeans[j]) / s.FeatureStds[j];
                        }
                    }

                    s.Model = new MLPModel(numFeatures, hiddenLayers, learningRate, epochs, 
                        batchSize, dropout, l2Lambda);
                    s.Model.Train(normalizedX, y);
                    s.LastTrainBar = s.BarCount;
                }
            }

            // 预测
            double prediction = 0;
            if (s.Model != null && s.Model.IsTrained)
            {
                var features = MLPFeatureExtractor.ExtractFeatures(tu.QuoteList, lookback);
                if (features.Length > 0 && s.FeatureMeans.Length == features.Length)
                {
                    // 标准化特征
                    var normalizedFeatures = new double[features.Length];
                    for (int i = 0; i < features.Length; i++)
                    {
                        normalizedFeatures[i] = (features[i] - s.FeatureMeans[i]) / s.FeatureStds[i];
                    }
                    prediction = s.Model.Predict(normalizedFeatures);
                }
            }

            // 绘制预测信号
            Plot("sub1", "prediction", PlotType.CURVE, prediction * 100);

            // 绘制训练损失
            if (s.Model != null && s.Model.IsTrained)
            {
                Plot("sub2", "loss", PlotType.CURVE, s.Model.LastLoss * 1000);
            }

            // 计算手数
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var symbol = GetSymbol(tu.MktSymbol);
                num = (Convert.ToDecimal(ArgDic["money"]) / (q.Close * symbol.multiplier * symbol.margin_ratio));
                if (symbol.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }

            // 交易逻辑
            if (s.Status == 0)
            {
                // 止损后冷却期：冷却未结束时递减计数并跳过本根K线的开仓判断
                if (s.CooldownRemaining > 0)
                {
                    s.CooldownRemaining--;
                }
                else
                {
                    // 信号重置再武装：被封锁方向的入场信号不再成立时解除封锁
if (s.BlockedDir == 1 && prediction <= 0)
                    {
                        s.BlockedDir = 0;
                    }
else if (s.BlockedDir == 2 && prediction >= 0)
                    {
                        s.BlockedDir = 0;
                    }

                    // 空仓：根据预测信号入场（被封锁方向禁止开仓）
                    if (prediction > threshold && mode != 2 && s.BlockedDir != 1)
                {
                    // 预测上涨，做多
                    s.Status = 1;
                    s.Num = num;
                    s.EntryPrice = q.Close;
                    s.StopLoss = q.Close - atrValue * (decimal)atrMultiplier;
                    s.TakeProfit = q.Close + atrValue * (decimal)takeProfitMultiplier;

                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);

                    Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                    Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);
                }
                    else if (prediction < -threshold && mode != 1 && s.BlockedDir != 2)
                {
                    // 预测下跌，做空
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
            }
            else if (s.Status == 1)
            {
                // 多头持仓：检查止损止盈
                Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);

                bool shouldExit = false;
                bool stopLossHit = false;
                if (q.Close <= s.StopLoss)
                {
                    shouldExit = true;
                    stopLossHit = true; // 止损出场
                }
                else if (q.Close >= s.TakeProfit)
                {
                    shouldExit = true;
                }

                if (shouldExit)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0;
                    s.Num = 0;
                    if (stopLossHit)
                    {
                        // 止损后进入冷却期，并封锁多头方向直至多头信号重置
                        s.CooldownRemaining = Convert.ToInt32(ArgDic["stopCooldownBars"]);
                        s.BlockedDir = 1;
                    }
                }
            }
            else if (s.Status == 2)
            {
                // 空头持仓：检查止损止盈
                Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);

                bool shouldExit = false;
                bool stopLossHit = false;
                if (q.Close >= s.StopLoss)
                {
                    shouldExit = true;
                    stopLossHit = true; // 止损出场
                }
                else if (q.Close <= s.TakeProfit)
                {
                    shouldExit = true;
                }

                if (shouldExit)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                    s.Status = 0;
                    s.Num = 0;
                    if (stopLossHit)
                    {
                        // 止损后进入冷却期，并封锁空头方向直至空头信号重置
                        s.CooldownRemaining = Convert.ToInt32(ArgDic["stopCooldownBars"]);
                        s.BlockedDir = 2;
                    }
                }
            }
        }

        /// <summary>
        /// 准备训练数据
        /// </summary>
        private (double[][] X, double[] y) PrepareTrainingData(List<SkQuote> quotes, int lookback, int trainPeriod)
        {
            var X = new List<double[]>();
            var y = new List<double>();

            int startIdx = Math.Max(lookback, quotes.Count - trainPeriod);
            int endIdx = quotes.Count - 1;

            for (int i = startIdx; i < endIdx; i++)
            {
                var subQuotes = quotes.Take(i + 1).ToList();
                var features = MLPFeatureExtractor.ExtractFeatures(subQuotes, lookback);

                if (features.Length == 0) continue;

                // 目标：下一根K线的收益率
                var currentClose = quotes[i].Close;
                var nextClose = quotes[i + 1].Close;
                double target = (double)((nextClose - currentClose) / currentClose);

                X.Add(features);
                y.Add(target);
            }

            return (X.ToArray(), y.ToArray());
        }
    }
}
