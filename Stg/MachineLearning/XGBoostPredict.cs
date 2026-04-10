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
    /// 基于XGBoost的价格预测交易策略
    /// 策略逻辑：
    /// - 使用XGBoost（梯度提升决策树）预测下一根K线的价格走势
    /// - 预测上涨则做多，预测下跌则做空
    /// - 结合ATR进行止损止盈管理
    /// XGBoost特点：
    /// - 使用二阶泰勒展开优化目标函数
    /// - 支持正则化防止过拟合
    /// - 使用直方图加速和列采样
    /// - 支持缺失值处理
    /// </summary>
    public class XGBoostPredict : StgBase
    {
        public XGBoostPredict()
        {
        }

        public XGBoostPredict(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // XGBoost模型参数
            sd.ArgDic["lookback"] = 20;              // 回看周期（特征窗口）
            sd.ArgDic["numTrees"] = 100;             // 树的数量（n_estimators）
            sd.ArgDic["maxDepth"] = 6;               // 树的最大深度
            sd.ArgDic["learningRate"] = 0.1;         // 学习率（eta）
            sd.ArgDic["lambda"] = 1.0;               // L2正则化系数
            sd.ArgDic["gamma"] = 0.0;                // 最小分裂增益
            sd.ArgDic["minChildWeight"] = 1.0;       // 叶子节点最小权重
            sd.ArgDic["subsample"] = 0.8;            // 行采样比例
            sd.ArgDic["colsampleByTree"] = 0.8;      // 列采样比例
            sd.ArgDic["trainPeriod"] = 200;          // 训练周期数
            sd.ArgDic["retrainInterval"] = 50;       // 重新训练间隔

            // 交易参数
            sd.ArgDic["threshold"] = 0.001;          // 预测阈值（涨跌幅度）
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
            sd.ArgDescDic["numTrees"] = new ArgDesc() { Text = "树数量", Explain = "XGBoost中树的数量" };
            sd.ArgDescDic["maxDepth"] = new ArgDesc() { Text = "最大深度", Explain = "决策树的最大深度" };
            sd.ArgDescDic["learningRate"] = new ArgDesc() { Text = "学习率", Explain = "模型训练学习率(eta)" };
            sd.ArgDescDic["lambda"] = new ArgDesc() { Text = "L2正则化", Explain = "L2正则化系数(lambda)" };
            sd.ArgDescDic["gamma"] = new ArgDesc() { Text = "最小增益", Explain = "分裂所需的最小增益" };
            sd.ArgDescDic["minChildWeight"] = new ArgDesc() { Text = "最小权重", Explain = "叶子节点最小Hessian权重" };
            sd.ArgDescDic["subsample"] = new ArgDesc() { Text = "行采样", Explain = "每棵树的样本采样比例" };
            sd.ArgDescDic["colsampleByTree"] = new ArgDesc() { Text = "列采样", Explain = "每棵树的特征采样比例" };
            sd.ArgDescDic["trainPeriod"] = new ArgDesc() { Text = "训练周期", Explain = "用于训练的历史K线数量" };
            sd.ArgDescDic["retrainInterval"] = new ArgDesc() { Text = "重训间隔", Explain = "每隔多少根K线重新训练模型" };
            sd.ArgDescDic["threshold"] = new ArgDesc() { Text = "预测阈值", Explain = "预测涨跌幅超过此值才交易" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "计算ATR的周期" };
            sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "止损倍数", Explain = "ATR止损倍数" };
            sd.ArgDescDic["takeProfitMultiplier"] = new ArgDesc() { Text = "止盈倍数", Explain = "ATR止盈倍数" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "0:双向 1:仅做多 2:仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0:立即 1:下个开盘" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0:固定手数 1:固定金额" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 颜色配置
            sd.ColorDic["main-stopLoss"] = "#F44336";
            sd.ColorDic["main-takeProfit"] = "#4CAF50";
            sd.ColorDic["sub1-prediction"] = "#2196F3";
            sd.ColorDic["sub2-importance"] = "#FF9800";

            return sd;
        }

        #region XGBoost模型实现

        /// <summary>
        /// XGBoost决策树节点
        /// </summary>
        private class XGBTreeNode
        {
            public int FeatureIndex { get; set; } = -1;
            public double SplitValue { get; set; }
            public double LeafValue { get; set; }
            public bool IsLeaf { get; set; } = true;
            public XGBTreeNode? Left { get; set; }
            public XGBTreeNode? Right { get; set; }
        }

        /// <summary>
        /// XGBoost决策树
        /// </summary>
        private class XGBTree
        {
            private int _maxDepth;
            private double _lambda;
            private double _gamma;
            private double _minChildWeight;
            private double _colsample;
            private Random _random;
            private XGBTreeNode? _root;
            private int[]? _selectedFeatures;
            private int _numFeatures;

            public XGBTree(int maxDepth, double lambda, double gamma, double minChildWeight, double colsample, Random random)
            {
                _maxDepth = maxDepth;
                _lambda = lambda;
                _gamma = gamma;
                _minChildWeight = minChildWeight;
                _colsample = colsample;
                _random = random;
            }

            public void Fit(double[][] X, double[] gradients, double[] hessians, int[] sampleIndices)
            {
                if (X.Length == 0 || sampleIndices.Length == 0) return;

                _numFeatures = X[0].Length;

                // 列采样：随机选择特征子集
                int numSelectedFeatures = Math.Max(1, (int)(_numFeatures * _colsample));
                _selectedFeatures = Enumerable.Range(0, _numFeatures)
                    .OrderBy(_ => _random.Next())
                    .Take(numSelectedFeatures)
                    .ToArray();

                _root = BuildTree(X, gradients, hessians, sampleIndices, 0);
            }

            private XGBTreeNode BuildTree(double[][] X, double[] gradients, double[] hessians, int[] indices, int depth)
            {
                var node = new XGBTreeNode();

                // 计算当前节点的梯度和Hessian之和
                double gradSum = 0, hessSum = 0;
                foreach (var i in indices)
                {
                    gradSum += gradients[i];
                    hessSum += hessians[i];
                }

                // 检查是否应该成为叶子节点
                if (depth >= _maxDepth || indices.Length < 2 || hessSum < _minChildWeight)
                {
                    node.IsLeaf = true;
                    node.LeafValue = -gradSum / (hessSum + _lambda);
                    return node;
                }

                // 寻找最佳分裂点
                int bestFeature = -1;
                double bestThreshold = 0;
                double bestGain = 0;
                int[]? bestLeftIndices = null;
                int[]? bestRightIndices = null;

                foreach (var f in _selectedFeatures!)
                {
                    // 获取该特征的所有值并排序
                    var featureValues = indices.Select(i => (Index: i, Value: X[i][f]))
                        .OrderBy(x => x.Value)
                        .ToList();

                    double leftGradSum = 0, leftHessSum = 0;
                    double rightGradSum = gradSum, rightHessSum = hessSum;

                    for (int i = 0; i < featureValues.Count - 1; i++)
                    {
                        int idx = featureValues[i].Index;
                        leftGradSum += gradients[idx];
                        leftHessSum += hessians[idx];
                        rightGradSum -= gradients[idx];
                        rightHessSum -= hessians[idx];

                        // 跳过相同值
                        if (featureValues[i].Value == featureValues[i + 1].Value) continue;

                        // 检查最小权重约束
                        if (leftHessSum < _minChildWeight || rightHessSum < _minChildWeight) continue;

                        // 计算分裂增益 (XGBoost公式)
                        double gain = 0.5 * (
                            (leftGradSum * leftGradSum) / (leftHessSum + _lambda) +
                            (rightGradSum * rightGradSum) / (rightHessSum + _lambda) -
                            (gradSum * gradSum) / (hessSum + _lambda)
                        ) - _gamma;

                        if (gain > bestGain)
                        {
                            bestGain = gain;
                            bestFeature = f;
                            bestThreshold = (featureValues[i].Value + featureValues[i + 1].Value) / 2;
                            bestLeftIndices = featureValues.Take(i + 1).Select(x => x.Index).ToArray();
                            bestRightIndices = featureValues.Skip(i + 1).Select(x => x.Index).ToArray();
                        }
                    }
                }

                // 如果没有找到有效分裂，返回叶子节点
                if (bestFeature == -1 || bestLeftIndices == null || bestRightIndices == null)
                {
                    node.IsLeaf = true;
                    node.LeafValue = -gradSum / (hessSum + _lambda);
                    return node;
                }

                // 创建分裂节点
                node.IsLeaf = false;
                node.FeatureIndex = bestFeature;
                node.SplitValue = bestThreshold;
                node.Left = BuildTree(X, gradients, hessians, bestLeftIndices, depth + 1);
                node.Right = BuildTree(X, gradients, hessians, bestRightIndices, depth + 1);

                return node;
            }

            public double Predict(double[] x)
            {
                if (_root == null) return 0;
                return PredictNode(_root, x);
            }

            private double PredictNode(XGBTreeNode node, double[] x)
            {
                if (node.IsLeaf) return node.LeafValue;

                if (x[node.FeatureIndex] <= node.SplitValue)
                    return PredictNode(node.Left!, x);
                else
                    return PredictNode(node.Right!, x);
            }

            public int[] GetUsedFeatures()
            {
                var features = new HashSet<int>();
                CollectFeatures(_root, features);
                return features.ToArray();
            }

            private void CollectFeatures(XGBTreeNode? node, HashSet<int> features)
            {
                if (node == null || node.IsLeaf) return;
                features.Add(node.FeatureIndex);
                CollectFeatures(node.Left, features);
                CollectFeatures(node.Right, features);
            }
        }

        /// <summary>
        /// XGBoost模型
        /// 特点：
        /// 1. 使用二阶泰勒展开（梯度+Hessian）
        /// 2. 正则化（L2 + 分裂增益阈值gamma）
        /// 3. 行采样和列采样
        /// 4. 贪婪算法寻找最佳分裂点
        /// </summary>
        private class XGBoostModel
        {
            private List<XGBTree> _trees;
            private int _numTrees;
            private int _maxDepth;
            private double _learningRate;
            private double _lambda;
            private double _gamma;
            private double _minChildWeight;
            private double _subsample;
            private double _colsample;
            private double _basePrediction;
            private Random _random;
            private double[]? _featureImportance;

            public bool IsTrained { get; private set; }
            public double[] FeatureImportance => _featureImportance ?? Array.Empty<double>();

            public XGBoostModel(int numTrees, int maxDepth, double learningRate, double lambda,
                double gamma, double minChildWeight, double subsample, double colsample)
            {
                _numTrees = numTrees;
                _maxDepth = maxDepth;
                _learningRate = learningRate;
                _lambda = lambda;
                _gamma = gamma;
                _minChildWeight = minChildWeight;
                _subsample = subsample;
                _colsample = colsample;
                _trees = new List<XGBTree>();
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
                        // MSE损失: L = (y - pred)^2
                        // 梯度: dL/dpred = 2 * (pred - y) -> 简化为 (pred - y)
                        gradients[i] = predictions[i] - y[i];
                        // Hessian: d^2L/dpred^2 = 2 -> 简化为 1
                        hessians[i] = 1.0;
                    }

                    // 行采样
                    int sampleSize = Math.Max(1, (int)(numSamples * _subsample));
                    var sampleIndices = Enumerable.Range(0, numSamples)
                        .OrderBy(_ => _random.Next())
                        .Take(sampleSize)
                        .ToArray();

                    // 训练新树
                    var tree = new XGBTree(_maxDepth, _lambda, _gamma, _minChildWeight, _colsample, _random);
                    tree.Fit(X, gradients, hessians, sampleIndices);
                    _trees.Add(tree);

                    // 更新所有样本的预测
                    for (int i = 0; i < numSamples; i++)
                        predictions[i] += _learningRate * tree.Predict(X[i]);

                    // 更新特征重要性
                    var usedFeatures = tree.GetUsedFeatures();
                    foreach (var f in usedFeatures)
                    {
                        if (f >= 0 && f < numFeatures)
                            _featureImportance[f] += 1.0;
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
        /// XGBoost特征提取器
        /// </summary>
        private static class XGBFeatureExtractor
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
                // 短期RSI
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

                // 11. 趋势方向特征
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

                // 15. 高低点特征
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

                // 16. 威廉指标 %R
                if (highest != lowest)
                    features.Add((highest - currentClose) / (highest - lowest));
                else
                    features.Add(0.5);

                // 17. 成交量趋势
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
            public XGBoostModel? Model { get; set; }   // XGBoost模型
            public int LastTrainBar { get; set; }   // 上次训练的K线索引
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            // 获取参数
            int lookback = Convert.ToInt32(ArgDic["lookback"]);
            int numTrees = Convert.ToInt32(ArgDic["numTrees"]);
            int maxDepth = Convert.ToInt32(ArgDic["maxDepth"]);
            double learningRate = Convert.ToDouble(ArgDic["learningRate"]);
            double lambda = Convert.ToDouble(ArgDic["lambda"]);
            double gamma = Convert.ToDouble(ArgDic["gamma"]);
            double minChildWeight = Convert.ToDouble(ArgDic["minChildWeight"]);
            double subsample = Convert.ToDouble(ArgDic["subsample"]);
            double colsample = Convert.ToDouble(ArgDic["colsampleByTree"]);
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
            if (s.Model == null)
            {
                s.Model = new XGBoostModel(numTrees, maxDepth, learningRate, lambda, gamma, minChildWeight, subsample, colsample);
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
                var features = XGBFeatureExtractor.ExtractFeatures(tu.QuoteList, lookback);
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
                var features = XGBFeatureExtractor.ExtractFeatures(subQuotes, lookback);

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
