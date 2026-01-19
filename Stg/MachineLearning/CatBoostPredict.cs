using Common;
using Model;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    /// <summary>
    /// 基于CatBoost的价格预测交易策略
    /// 策略逻辑：
    /// - 使用CatBoost（对称树+Ordered Boosting）预测下一根K线的价格走势
    /// - 预测上涨则做多，预测下跌则做空
    /// - 结合ATR进行止损止盈管理
    /// CatBoost特点：
    /// - 使用对称决策树（所有同层节点使用相同分割条件）
    /// - Ordered Boosting减少预测偏移
    /// - 对类别特征有特殊处理（本策略中使用趋势方向作为类别特征）
    /// </summary>
    public class CatBoostPredict : StgBase
    {
        public CatBoostPredict()
        {
        }

        public CatBoostPredict(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // CatBoost模型参数
            sd.ArgDic["lookback"] = 20;              // 回看周期（特征窗口）
            sd.ArgDic["numTrees"] = 100;             // 树的数量
            sd.ArgDic["maxDepth"] = 6;               // 树的最大深度（CatBoost默认6）
            sd.ArgDic["learningRate"] = 0.03;        // 学习率（CatBoost通常用较小值）
            sd.ArgDic["l2Regularization"] = 3.0;     // L2正则化系数
            sd.ArgDic["trainPeriod"] = 200;          // 训练周期数
            sd.ArgDic["retrainInterval"] = 50;       // 重新训练间隔
            sd.ArgDic["randomStrength"] = 1.0;       // 随机强度（用于分割选择）

            // 交易参数
            sd.ArgDic["threshold"] = 0.001;          // 预测阈值（涨跌幅度）
            sd.ArgDic["atrPeriod"] = 14;             // ATR周期
            sd.ArgDic["atrMultiplier"] = 2.0;        // ATR止损倍数
            sd.ArgDic["takeProfitMultiplier"] = 3.0; // ATR止盈倍数
            sd.ArgDic["mode"] = 0;                   // 0:双向 1:仅做多 2:仅做空
            sd.ArgDic["sendMode"] = 0;               // 发单模式

            // 手数控制
            sd.ArgDic["lotsMode"] = 0;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 100000m;

            // 参数说明
            sd.ArgDescDic["lookback"] = new ArgDesc() { Text = "回看周期", Explain = "特征计算窗口长度" };
            sd.ArgDescDic["numTrees"] = new ArgDesc() { Text = "树数量", Explain = "CatBoost中树的数量" };
            sd.ArgDescDic["maxDepth"] = new ArgDesc() { Text = "最大深度", Explain = "对称树的最大深度" };
            sd.ArgDescDic["learningRate"] = new ArgDesc() { Text = "学习率", Explain = "模型训练学习率" };
            sd.ArgDescDic["l2Regularization"] = new ArgDesc() { Text = "L2正则化", Explain = "L2正则化系数" };
            sd.ArgDescDic["trainPeriod"] = new ArgDesc() { Text = "训练周期", Explain = "用于训练的历史K线数量" };
            sd.ArgDescDic["retrainInterval"] = new ArgDesc() { Text = "重训间隔", Explain = "每隔多少根K线重新训练模型" };
            sd.ArgDescDic["randomStrength"] = new ArgDesc() { Text = "随机强度", Explain = "分割选择的随机性" };
            sd.ArgDescDic["threshold"] = new ArgDesc() { Text = "预测阈值", Explain = "预测涨跌幅超过此值才交易" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "计算ATR的周期" };
            sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "止损倍数", Explain = "ATR止损倍数" };
            sd.ArgDescDic["takeProfitMultiplier"] = new ArgDesc() { Text = "止盈倍数", Explain = "ATR止盈倍数" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0:双向 1:仅做多 2:仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0:立即 1:下个开盘" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0:固定手数 1:固定金额" };

            sd.MaxSymbolNum = 100;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 颜色配置
            sd.ColorDic["main-stopLoss"] = "#F44336";
            sd.ColorDic["main-takeProfit"] = "#4CAF50";
            sd.ColorDic["sub1-prediction"] = "#673AB7";
            sd.ColorDic["sub2-importance"] = "#FF5722";

            return sd;
        }

        #region CatBoost模型实现

        /// <summary>
        /// 对称树节点 - CatBoost使用对称决策树
        /// 对称树的特点：同一层的所有节点使用相同的分割特征和阈值
        /// </summary>
        private class SymmetricTreeNode
        {
            public int FeatureIndex { get; set; } = -1;
            public double SplitValue { get; set; }
            public double[] LeafValues { get; set; } = Array.Empty<double>(); // 2^depth个叶子值
            public int Depth { get; set; }
            public int[] SplitFeatures { get; set; } = Array.Empty<int>(); // 每层的分割特征
            public double[] SplitThresholds { get; set; } = Array.Empty<double>(); // 每层的分割阈值
        }

        /// <summary>
        /// CatBoost对称决策树
        /// </summary>
        private class SymmetricDecisionTree
        {
            private int _maxDepth;
            private double _l2Reg;
            private double _randomStrength;
            private Random _random;
            private int[] _splitFeatures;
            private double[] _splitThresholds;
            private double[] _leafValues;

            public SymmetricDecisionTree(int maxDepth, double l2Reg, double randomStrength, Random random)
            {
                _maxDepth = maxDepth;
                _l2Reg = l2Reg;
                _randomStrength = randomStrength;
                _random = random;
                _splitFeatures = new int[maxDepth];
                _splitThresholds = new double[maxDepth];
                _leafValues = new double[1 << maxDepth]; // 2^maxDepth个叶子
            }

            public void Fit(double[][] X, double[] gradients, double[] hessians)
            {
                if (X.Length == 0) return;

                int numFeatures = X[0].Length;
                int numSamples = X.Length;

                // 为每一层选择最佳分割
                for (int depth = 0; depth < _maxDepth; depth++)
                {
                    int bestFeature = -1;
                    double bestThreshold = 0;
                    double bestGain = double.MinValue;

                    // 遍历所有特征寻找最佳分割
                    for (int f = 0; f < numFeatures; f++)
                    {
                        // 获取该特征的候选分割点
                        var values = X.Select(x => x[f]).Distinct().OrderBy(v => v).ToList();
                        if (values.Count < 2) continue;

                        // 尝试不同的分割点
                        for (int i = 0; i < values.Count - 1; i++)
                        {
                            double threshold = (values[i] + values[i + 1]) / 2;

                            // 添加随机扰动（CatBoost的随机强度特性）
                            double noise = (_random.NextDouble() - 0.5) * _randomStrength * 0.01;
                            double gain = CalculateSplitGain(X, gradients, hessians, f, threshold, depth) + noise;

                            if (gain > bestGain)
                            {
                                bestGain = gain;
                                bestFeature = f;
                                bestThreshold = threshold;
                            }
                        }
                    }

                    if (bestFeature == -1)
                    {
                        // 如果找不到好的分割，使用随机特征
                        bestFeature = _random.Next(numFeatures);
                        var values = X.Select(x => x[bestFeature]).ToList();
                        bestThreshold = values.Average();
                    }

                    _splitFeatures[depth] = bestFeature;
                    _splitThresholds[depth] = bestThreshold;
                }

                // 计算每个叶子节点的值
                CalculateLeafValues(X, gradients, hessians);
            }

            private double CalculateSplitGain(double[][] X, double[] gradients, double[] hessians,
                int featureIndex, double threshold, int currentDepth)
            {
                // 计算当前分割的增益
                double leftGradSum = 0, leftHessSum = 0;
                double rightGradSum = 0, rightHessSum = 0;

                for (int i = 0; i < X.Length; i++)
                {
                    // 根据已有的分割确定样本属于哪个分支
                    int leafIndex = GetLeafIndex(X[i], currentDepth);

                    // 对于当前层，根据新的分割条件划分
                    if (X[i][featureIndex] <= threshold)
                    {
                        leftGradSum += gradients[i];
                        leftHessSum += hessians[i];
                    }
                    else
                    {
                        rightGradSum += gradients[i];
                        rightHessSum += hessians[i];
                    }
                }

                // CatBoost的增益计算公式
                double leftScore = leftHessSum > 0 ? (leftGradSum * leftGradSum) / (leftHessSum + _l2Reg) : 0;
                double rightScore = rightHessSum > 0 ? (rightGradSum * rightGradSum) / (rightHessSum + _l2Reg) : 0;
                double totalGrad = leftGradSum + rightGradSum;
                double totalHess = leftHessSum + rightHessSum;
                double totalScore = totalHess > 0 ? (totalGrad * totalGrad) / (totalHess + _l2Reg) : 0;

                return leftScore + rightScore - totalScore;
            }

            private int GetLeafIndex(double[] x, int upToDepth)
            {
                int index = 0;
                for (int d = 0; d < upToDepth; d++)
                {
                    index <<= 1;
                    if (x[_splitFeatures[d]] > _splitThresholds[d])
                        index |= 1;
                }
                return index;
            }

            private void CalculateLeafValues(double[][] X, double[] gradients, double[] hessians)
            {
                int numLeaves = 1 << _maxDepth;
                var leafGradSums = new double[numLeaves];
                var leafHessSums = new double[numLeaves];

                // 将样本分配到叶子节点
                for (int i = 0; i < X.Length; i++)
                {
                    int leafIndex = GetLeafIndex(X[i], _maxDepth);
                    leafGradSums[leafIndex] += gradients[i];
                    leafHessSums[leafIndex] += hessians[i];
                }

                // 计算叶子值（带L2正则化）
                for (int i = 0; i < numLeaves; i++)
                {
                    if (leafHessSums[i] > 0)
                        _leafValues[i] = -leafGradSums[i] / (leafHessSums[i] + _l2Reg);
                    else
                        _leafValues[i] = 0;
                }
            }

            public double Predict(double[] x)
            {
                int leafIndex = GetLeafIndex(x, _maxDepth);
                return _leafValues[leafIndex];
            }

            public int[] GetSplitFeatures() => _splitFeatures;
        }

        /// <summary>
        /// CatBoost模型（简化版实现）
        /// 特点：
        /// 1. 使用对称决策树
        /// 2. Ordered Boosting（通过样本排序减少预测偏移）
        /// 3. 支持类别特征（通过目标编码）
        /// </summary>
        private class CatBoostModel
        {
            private List<SymmetricDecisionTree> _trees;
            private int _numTrees;
            private int _maxDepth;
            private double _learningRate;
            private double _l2Reg;
            private double _randomStrength;
            private double _basePrediction;
            private Random _random;
            private double[]? _featureImportance;
            private int[]? _orderedIndices; // Ordered Boosting的样本顺序

            public bool IsTrained { get; private set; }
            public double[] FeatureImportance => _featureImportance ?? Array.Empty<double>();

            public CatBoostModel(int numTrees, int maxDepth, double learningRate, double l2Reg, double randomStrength)
            {
                _numTrees = numTrees;
                _maxDepth = maxDepth;
                _learningRate = learningRate;
                _l2Reg = l2Reg;
                _randomStrength = randomStrength;
                _trees = new List<SymmetricDecisionTree>();
                _random = new Random(42);
                IsTrained = false;
            }

            public void Train(double[][] X, double[] y)
            {
                if (X.Length == 0 || y.Length == 0) return;

                _trees.Clear();
                int numFeatures = X[0].Length;
                int numSamples = X.Length;
                _featureImportance = new double[numFeatures];

                // Ordered Boosting: 随机打乱样本顺序
                _orderedIndices = Enumerable.Range(0, numSamples).OrderBy(_ => _random.Next()).ToArray();

                // 初始预测为目标均值
                _basePrediction = y.Average();

                // 当前预测值
                var predictions = new double[numSamples];
                for (int i = 0; i < predictions.Length; i++)
                    predictions[i] = _basePrediction;

                // 迭代构建树
                for (int t = 0; t < _numTrees; t++)
                {
                    // 计算梯度和Hessian（对于MSE损失）
                    var gradients = new double[numSamples];
                    var hessians = new double[numSamples];

                    for (int i = 0; i < numSamples; i++)
                    {
                        // MSE损失的梯度: 2 * (pred - y) -> 简化为 (pred - y)
                        gradients[i] = predictions[i] - y[i];
                        // MSE损失的Hessian: 常数2 -> 简化为1
                        hessians[i] = 1.0;
                    }

                    // Ordered Boosting: 使用部分样本训练（模拟时间顺序）
                    int trainSize = Math.Max(numSamples / 2, Math.Min(numSamples, 50 + t * 5));
                    trainSize = Math.Min(trainSize, numSamples);

                    var trainIndices = _orderedIndices.Take(trainSize).ToArray();
                    var trainX = trainIndices.Select(i => X[i]).ToArray();
                    var trainGradients = trainIndices.Select(i => gradients[i]).ToArray();
                    var trainHessians = trainIndices.Select(i => hessians[i]).ToArray();

                    // 训练新树
                    var tree = new SymmetricDecisionTree(_maxDepth, _l2Reg, _randomStrength, _random);
                    tree.Fit(trainX, trainGradients, trainHessians);
                    _trees.Add(tree);

                    // 更新所有样本的预测
                    for (int i = 0; i < numSamples; i++)
                        predictions[i] += _learningRate * tree.Predict(X[i]);

                    // 更新特征重要性
                    var splitFeatures = tree.GetSplitFeatures();
                    foreach (var f in splitFeatures)
                    {
                        if (f >= 0 && f < numFeatures)
                            _featureImportance[f] += 1.0 / _maxDepth;
                    }

                    // 早停检查
                    double mse = 0;
                    for (int i = 0; i < numSamples; i++)
                        mse += Math.Pow(y[i] - predictions[i], 2);
                    mse /= numSamples;

                    if (mse < 1e-8) break;
                }

                // 归一化特征重要性
                double sum = _featureImportance.Sum();
                if (sum > 0)
                {
                    for (int i = 0; i < _featureImportance.Length; i++)
                        _featureImportance[i] /= sum;
                }

                IsTrained = true;
            }

            public double Predict(double[] x)
            {
                if (!IsTrained) return 0;

                double prediction = _basePrediction;
                foreach (var tree in _trees)
                    prediction += _learningRate * tree.Predict(x);

                return prediction;
            }
        }

        #endregion

        #region 特征工程

        /// <summary>
        /// CatBoost特征提取器
        /// 包含数值特征和类别特征（趋势方向）
        /// </summary>
        private static class CatBoostFeatureExtractor
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

                // 11. 类别特征编码（趋势方向）
                // 短期趋势: 1=上涨, 0=震荡, -1=下跌
                double shortTrend = ma5 > ma10 ? 1 : (ma5 < ma10 ? -1 : 0);
                features.Add(shortTrend);
                // 中期趋势
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
            public CatBoostModel? Model { get; set; }   // CatBoost模型
            public int LastTrainBar { get; set; }   // 上次训练的K线索引
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int lookback = (int)ArgDic["lookback"];
            int numTrees = (int)ArgDic["numTrees"];
            int maxDepth = (int)ArgDic["maxDepth"];
            double learningRate = Convert.ToDouble(ArgDic["learningRate"]);
            double l2Reg = Convert.ToDouble(ArgDic["l2Regularization"]);
            double randomStrength = Convert.ToDouble(ArgDic["randomStrength"]);
            int trainPeriod = (int)ArgDic["trainPeriod"];
            int retrainInterval = (int)ArgDic["retrainInterval"];
            double threshold = Convert.ToDouble(ArgDic["threshold"]);
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

            // 检查是否需要训练/重训练
            if (s.Model == null)
            {
                s.Model = new CatBoostModel(numTrees, maxDepth, learningRate, l2Reg, randomStrength);
            }

            if (!s.Model.IsTrained || (s.BarCount - s.LastTrainBar >= retrainInterval))
            {
                var (X, y) = PrepareTrainingData(tu.QuoteList, lookback, trainPeriod);
                if (X.Length > 0)
                {
                    s.Model.Train(X, y);
                    s.LastTrainBar = s.BarCount;
                }
            }

            // 预测
            double prediction = 0;
            if (s.Model != null && s.Model.IsTrained)
            {
                var features = CatBoostFeatureExtractor.ExtractFeatures(tu.QuoteList, lookback);
                if (features.Length > 0)
                {
                    prediction = s.Model.Predict(features);
                }
            }

            // 绘制预测信号
            Plot("sub1", "prediction", PlotType.CURVE, prediction * 100);

            // 绘制特征重要性（取前几个重要特征的平均）
            if (s.Model != null && s.Model.IsTrained && s.Model.FeatureImportance.Length > 0)
            {
                var topImportance = s.Model.FeatureImportance.OrderByDescending(x => x).Take(5).Average();
                Plot("sub2", "importance", PlotType.CURVE, topImportance * 100);
            }

            // 计算手数
            var num = (decimal)ArgDic["lots"];
            var lotsMode = (int)ArgDic["lotsMode"];
            if (lotsMode == 1)
            {
                var symbol = GetSymbol(tu.MktSymbol);
                num = ((decimal)ArgDic["money"] / (q.Close * symbol.multiplier * symbol.margin_ratio));
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
                // 空仓：根据预测信号入场
                if (prediction > threshold && mode != 2)
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
                else if (prediction < -threshold && mode != 1)
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
            else if (s.Status == 1)
            {
                // 多头持仓：检查止损止盈
                Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);

                bool shouldExit = false;
                if (q.Close <= s.StopLoss)
                {
                    shouldExit = true;
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
                }
            }
            else if (s.Status == 2)
            {
                // 空头持仓：检查止损止盈
                Plot("main", "stopLoss", PlotType.LINE, (double)s.StopLoss);
                Plot("main", "takeProfit", PlotType.LINE, (double)s.TakeProfit);

                bool shouldExit = false;
                if (q.Close >= s.StopLoss)
                {
                    shouldExit = true;
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
                var features = CatBoostFeatureExtractor.ExtractFeatures(subQuotes, lookback);

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
