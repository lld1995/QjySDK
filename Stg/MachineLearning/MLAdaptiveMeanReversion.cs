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
    /// 基于机器学习的自适应均值回归策略 (ML-Based Adaptive Mean Reversion Strategy)
    /// 
    /// 策略核心思想：
    /// 使用机器学习模型（GBDT）动态预测价格偏离均值后的回归概率和幅度，
    /// 结合传统技术指标实现智能化的均值回归交易。
    /// 
    /// 核心创新：
    /// 1. ML回归概率预测：使用GBDT预测价格回归均值的概率
    /// 2. ML回归幅度预测：预测回归的目标价位
    /// 3. 自适应偏离阈值：根据历史数据动态调整入场阈值
    /// 4. 市场状态分类：识别趋势/震荡市场，仅在震荡市场交易
    /// 5. 动态止损止盈：基于ML预测的回归幅度设置
    /// 
    /// 特征工程：
    /// - 价格偏离度特征（Z-Score、布林带位置等）
    /// - 动量特征（RSI、ROC、动量指标）
    /// - 波动率特征（ATR、历史波动率、布林带宽度）
    /// - 成交量特征（量比、OBV变化率）
    /// - 市场微观结构特征（高低点位置、K线形态）
    /// 
    /// 入场条件：
    /// - 价格偏离均值超过阈值
    /// - ML模型预测回归概率超过阈值
    /// - 市场状态为震荡（非趋势）
    /// 
    /// 出场条件：
    /// - 价格回归到ML预测的目标位
    /// - 动态止损触发
    /// - 最大持仓时间
    /// </summary>
    public class MLAdaptiveMeanReversion : StgBase
    {
        public MLAdaptiveMeanReversion()
        {
        }

        public MLAdaptiveMeanReversion(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 100;
            sd.SubChartNum = 3;
            sd.UseGlobalCalc = 0;

            // ==================== ML模型参数 ====================
            sd.ArgDescDic["lookback"] = new ArgDesc { Text = "特征窗口", Explain = "特征计算的回看周期" };
            sd.ArgDic["lookback"] = 20;

            sd.ArgDescDic["numTrees"] = new ArgDesc { Text = "树数量", Explain = "GBDT模型中树的数量" };
            sd.ArgDic["numTrees"] = 50;

            sd.ArgDescDic["maxDepth"] = new ArgDesc { Text = "最大深度", Explain = "决策树的最大深度" };
            sd.ArgDic["maxDepth"] = 4;

            sd.ArgDescDic["learningRate"] = new ArgDesc { Text = "学习率", Explain = "GBDT学习率" };
            sd.ArgDic["learningRate"] = 0.1;

            sd.ArgDescDic["trainPeriod"] = new ArgDesc { Text = "训练周期", Explain = "用于训练的历史K线数量" };
            sd.ArgDic["trainPeriod"] = 200;

            sd.ArgDescDic["retrainInterval"] = new ArgDesc { Text = "重训间隔", Explain = "每隔多少根K线重新训练模型" };
            sd.ArgDic["retrainInterval"] = 50;

            // ==================== 均值回归参数 ====================
            sd.ArgDescDic["maPeriod"] = new ArgDesc { Text = "均线周期", Explain = "均值计算周期" };
            sd.ArgDic["maPeriod"] = 20;

            sd.ArgDescDic["deviationThreshold"] = new ArgDesc { Text = "偏离阈值", Explain = "价格偏离均值的Z-Score阈值" };
            sd.ArgDic["deviationThreshold"] = 2.0;

            sd.ArgDescDic["useAdaptiveThreshold"] = new ArgDesc { Text = "自适应阈值", Explain = "1=ML动态调整阈值 0=固定阈值" };
            sd.ArgDic["useAdaptiveThreshold"] = 1;

            sd.ArgDescDic["minProbability"] = new ArgDesc { Text = "最小回归概率", Explain = "ML预测的最小回归概率阈值" };
            sd.ArgDic["minProbability"] = 0.6;

            // ==================== 市场状态参数 ====================
            sd.ArgDescDic["trendPeriod"] = new ArgDesc { Text = "趋势判断周期", Explain = "ADX计算周期" };
            sd.ArgDic["trendPeriod"] = 14;

            sd.ArgDescDic["trendThreshold"] = new ArgDesc { Text = "趋势阈值", Explain = "ADX超过此值视为趋势市场" };
            sd.ArgDic["trendThreshold"] = 25.0;

            sd.ArgDescDic["filterTrend"] = new ArgDesc { Text = "过滤趋势", Explain = "1=趋势市场不入场 0=不过滤" };
            sd.ArgDic["filterTrend"] = 1;

            // ==================== 风控参数 ====================
            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "ATR计算周期" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["stopLossAtr"] = new ArgDesc { Text = "止损ATR倍数", Explain = "止损距离 = ATR × 此倍数" };
            sd.ArgDic["stopLossAtr"] = 2.5;

            sd.ArgDescDic["useMLTakeProfit"] = new ArgDesc { Text = "ML止盈", Explain = "1=使用ML预测目标位 0=回归均值" };
            sd.ArgDic["useMLTakeProfit"] = 1;

            sd.ArgDescDic["takeProfitAtr"] = new ArgDesc { Text = "止盈ATR倍数", Explain = "备用止盈距离 = ATR × 此倍数" };
            sd.ArgDic["takeProfitAtr"] = 2.0;

            sd.ArgDescDic["useTrailingStop"] = new ArgDesc { Text = "移动止损", Explain = "1=启用 0=禁用" };
            sd.ArgDic["useTrailingStop"] = 1;

            sd.ArgDescDic["trailingStopAtr"] = new ArgDesc { Text = "移动止损ATR", Explain = "移动止损距离 = ATR × 此倍数" };
            sd.ArgDic["trailingStopAtr"] = 1.5;

            sd.ArgDescDic["maxHoldBars"] = new ArgDesc { Text = "最大持仓K线", Explain = "超过此数量强制平仓" };
            sd.ArgDic["maxHoldBars"] = 15;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易模式", Explain = "0=双向 1=仅做多 2=仅做空" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "0=立即 1=下个开盘" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0=固定手数 1=固定金额 2=凯利公式" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额" };
            sd.ArgDic["money"] = 10000m;

            // ==================== 颜色配置 ====================
            sd.ColorDic["main-MA"] = "#2196F3";
            sd.ColorDic["main-UpperBand"] = "#F44336";
            sd.ColorDic["main-LowerBand"] = "#4CAF50";
            sd.ColorDic["main-MLTarget"] = "#FF9800";
            sd.ColorDic["main-StopLoss"] = "#E91E63";

            sd.ColorDic["sub0-ZScore"] = "#9C27B0";
            sd.ColorDic["sub0-Threshold"] = "#607D8B";

            sd.ColorDic["sub1-Probability"] = "#2196F3";
            sd.ColorDic["sub1-MinProb"] = "#FF5722";

            sd.ColorDic["sub2-ADX"] = "#4CAF50";
            sd.ColorDic["sub2-TrendThreshold"] = "#F44336";

            sd.MidValDic["sub0"] = 0;
            sd.MidValDic["sub1"] = 50;
            sd.MidValDic["sub2"] = 25;

            return sd;
        }

        #region GBDT模型实现

        /// <summary>
        /// GBDT决策树节点
        /// </summary>
        private class TreeNode
        {
            public int FeatureIndex { get; set; } = -1;
            public double SplitValue { get; set; }
            public double LeafValue { get; set; }
            public bool IsLeaf { get; set; } = true;
            public TreeNode? Left { get; set; }
            public TreeNode? Right { get; set; }
        }

        /// <summary>
        /// GBDT回归树
        /// </summary>
        private class RegressionTree
        {
            private int _maxDepth;
            private int _minSamplesLeaf;
            private double _maxFeatures;
            private Random _random;
            private TreeNode? _root;
            private int[]? _selectedFeatures;

            public RegressionTree(int maxDepth, int minSamplesLeaf, double maxFeatures, Random random)
            {
                _maxDepth = maxDepth;
                _minSamplesLeaf = minSamplesLeaf;
                _maxFeatures = maxFeatures;
                _random = random;
            }

            public void Fit(double[][] X, double[] residuals, int[] sampleIndices)
            {
                if (X.Length == 0 || sampleIndices.Length == 0) return;

                int numFeatures = X[0].Length;
                int numSelectedFeatures = Math.Max(1, (int)(numFeatures * _maxFeatures));
                _selectedFeatures = Enumerable.Range(0, numFeatures)
                    .OrderBy(_ => _random.Next())
                    .Take(numSelectedFeatures)
                    .ToArray();

                _root = BuildTree(X, residuals, sampleIndices, 0);
            }

            private TreeNode BuildTree(double[][] X, double[] residuals, int[] indices, int depth)
            {
                var node = new TreeNode();

                double sum = 0;
                foreach (var i in indices) sum += residuals[i];
                double mean = sum / indices.Length;

                if (depth >= _maxDepth || indices.Length < 2 * _minSamplesLeaf)
                {
                    node.IsLeaf = true;
                    node.LeafValue = mean;
                    return node;
                }

                int bestFeature = -1;
                double bestThreshold = 0;
                double bestMSEReduction = 0;
                int[]? bestLeftIndices = null;
                int[]? bestRightIndices = null;

                double currentMSE = 0;
                foreach (var i in indices)
                    currentMSE += Math.Pow(residuals[i] - mean, 2);

                foreach (var f in _selectedFeatures!)
                {
                    var featureValues = indices.Select(i => (Index: i, Value: X[i][f]))
                        .OrderBy(x => x.Value)
                        .ToList();

                    double leftSum = 0;
                    int leftCount = 0;

                    for (int i = 0; i < featureValues.Count - _minSamplesLeaf; i++)
                    {
                        int idx = featureValues[i].Index;
                        leftSum += residuals[idx];
                        leftCount++;

                        if (leftCount < _minSamplesLeaf) continue;
                        int rightCount = featureValues.Count - leftCount;
                        if (rightCount < _minSamplesLeaf) break;

                        if (i < featureValues.Count - 1 && featureValues[i].Value == featureValues[i + 1].Value) continue;

                        double leftMean = leftSum / leftCount;
                        double rightSum = sum - leftSum;
                        double rightMean = rightSum / rightCount;

                        double leftMSE = 0, rightMSE = 0;
                        for (int j = 0; j <= i; j++)
                            leftMSE += Math.Pow(residuals[featureValues[j].Index] - leftMean, 2);
                        for (int j = i + 1; j < featureValues.Count; j++)
                            rightMSE += Math.Pow(residuals[featureValues[j].Index] - rightMean, 2);

                        double mseReduction = currentMSE - leftMSE - rightMSE;

                        if (mseReduction > bestMSEReduction)
                        {
                            bestMSEReduction = mseReduction;
                            bestFeature = f;
                            bestThreshold = (featureValues[i].Value + featureValues[i + 1].Value) / 2;
                            bestLeftIndices = featureValues.Take(i + 1).Select(x => x.Index).ToArray();
                            bestRightIndices = featureValues.Skip(i + 1).Select(x => x.Index).ToArray();
                        }
                    }
                }

                if (bestFeature == -1 || bestLeftIndices == null || bestRightIndices == null)
                {
                    node.IsLeaf = true;
                    node.LeafValue = mean;
                    return node;
                }

                node.IsLeaf = false;
                node.FeatureIndex = bestFeature;
                node.SplitValue = bestThreshold;
                node.Left = BuildTree(X, residuals, bestLeftIndices, depth + 1);
                node.Right = BuildTree(X, residuals, bestRightIndices, depth + 1);

                return node;
            }

            public double Predict(double[] x)
            {
                if (_root == null) return 0;
                return PredictNode(_root, x);
            }

            private double PredictNode(TreeNode node, double[] x)
            {
                if (node.IsLeaf) return node.LeafValue;
                if (x[node.FeatureIndex] <= node.SplitValue)
                    return PredictNode(node.Left!, x);
                else
                    return PredictNode(node.Right!, x);
            }
        }

        /// <summary>
        /// GBDT模型 - 用于预测回归概率和幅度
        /// </summary>
        private class GBDTModel
        {
            private List<RegressionTree> _trees;
            private int _numTrees;
            private int _maxDepth;
            private double _learningRate;
            private double _subsample;
            private double _maxFeatures;
            private double _basePrediction;
            private Random _random;

            public bool IsTrained { get; private set; }

            public GBDTModel(int numTrees, int maxDepth, double learningRate, double subsample = 0.8, double maxFeatures = 0.8)
            {
                _numTrees = numTrees;
                _maxDepth = maxDepth;
                _learningRate = learningRate;
                _subsample = subsample;
                _maxFeatures = maxFeatures;
                _trees = new List<RegressionTree>();
                _random = new Random(42);
                IsTrained = false;
            }

            public void Train(double[][] X, double[] y)
            {
                if (X.Length == 0 || y.Length == 0) return;

                _trees.Clear();
                int numSamples = X.Length;

                _basePrediction = y.Average();

                var predictions = new double[numSamples];
                for (int i = 0; i < predictions.Length; i++)
                    predictions[i] = _basePrediction;

                for (int t = 0; t < _numTrees; t++)
                {
                    var residuals = new double[numSamples];
                    for (int i = 0; i < numSamples; i++)
                        residuals[i] = y[i] - predictions[i];

                    int sampleSize = Math.Max(1, (int)(numSamples * _subsample));
                    var sampleIndices = Enumerable.Range(0, numSamples)
                        .OrderBy(_ => _random.Next())
                        .Take(sampleSize)
                        .ToArray();

                    var tree = new RegressionTree(_maxDepth, 5, _maxFeatures, _random);
                    tree.Fit(X, residuals, sampleIndices);
                    _trees.Add(tree);

                    for (int i = 0; i < numSamples; i++)
                        predictions[i] += _learningRate * tree.Predict(X[i]);

                    double mse = 0;
                    for (int i = 0; i < numSamples; i++)
                        mse += Math.Pow(y[i] - predictions[i], 2);
                    mse /= numSamples;

                    if (mse < 1e-8) break;
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
        /// 均值回归特征提取器
        /// </summary>
        private static class MeanReversionFeatureExtractor
        {
            /// <summary>
            /// 提取均值回归相关特征
            /// </summary>
            public static double[] ExtractFeatures(List<SkQuote> quotes, int lookback, int maPeriod)
            {
                if (quotes.Count < Math.Max(lookback, maPeriod) + 10) return Array.Empty<double>();

                var features = new List<double>();
                var recentQuotes = quotes.Skip(quotes.Count - lookback).ToList();
                var closes = recentQuotes.Select(q => (double)q.Close).ToList();
                var currentClose = closes.Last();

                // ==================== 1. 偏离度特征 ====================
                // 1.1 Z-Score (价格偏离均值的标准差数)
                double ma = closes.TakeLast(maPeriod).Average();
                double std = Math.Sqrt(closes.TakeLast(maPeriod).Average(c => Math.Pow(c - ma, 2)));
                double zScore = std > 0 ? (currentClose - ma) / std : 0;
                features.Add(zScore);

                // 1.2 价格相对均值的百分比偏离
                double maDeviation = ma > 0 ? (currentClose - ma) / ma : 0;
                features.Add(maDeviation);

                // 1.3 布林带位置 (0-1)
                double upperBand = ma + 2 * std;
                double lowerBand = ma - 2 * std;
                double bollPosition = (upperBand != lowerBand) ? (currentClose - lowerBand) / (upperBand - lowerBand) : 0.5;
                features.Add(bollPosition);

                // 1.4 布林带宽度（波动率指标）
                double bollWidth = ma > 0 ? (upperBand - lowerBand) / ma : 0;
                features.Add(bollWidth);

                // ==================== 2. 动量特征 ====================
                // 2.1 RSI
                double rsi = CalculateRSI(closes, 14);
                features.Add(rsi / 100.0);

                // 2.2 短期RSI
                double rsi6 = CalculateRSI(closes, 6);
                features.Add(rsi6 / 100.0);

                // 2.3 RSI偏离50的程度
                features.Add((rsi - 50) / 50.0);

                // 2.4 价格变化率 (ROC)
                for (int i = 1; i <= 5 && i < closes.Count; i++)
                {
                    double prev = closes[closes.Count - 1 - i];
                    double roc = prev > 0 ? (currentClose - prev) / prev : 0;
                    features.Add(roc);
                }

                // 2.5 动量 (当前价格 - N周期前价格)
                if (closes.Count >= 10)
                {
                    double momentum = currentClose - closes[closes.Count - 10];
                    features.Add(closes[closes.Count - 10] > 0 ? momentum / closes[closes.Count - 10] : 0);
                }
                else
                {
                    features.Add(0);
                }

                // ==================== 3. 波动率特征 ====================
                // 3.1 历史波动率
                var returns = new List<double>();
                for (int i = 1; i < closes.Count; i++)
                {
                    if (closes[i - 1] > 0)
                        returns.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
                }
                double volatility = returns.Count > 0 ? Math.Sqrt(returns.Average(r => Math.Pow(r, 2))) : 0;
                features.Add(volatility);

                // 3.2 短期波动率 vs 长期波动率
                if (returns.Count >= 10)
                {
                    double shortVol = Math.Sqrt(returns.TakeLast(5).Average(r => Math.Pow(r, 2)));
                    double longVol = Math.Sqrt(returns.Average(r => Math.Pow(r, 2)));
                    features.Add(longVol > 0 ? shortVol / longVol : 1);
                }
                else
                {
                    features.Add(1);
                }

                // 3.3 ATR百分比
                double atr = CalculateATR(recentQuotes, 14);
                features.Add(currentClose > 0 ? atr / currentClose : 0);

                // ==================== 4. 成交量特征 ====================
                var volumes = recentQuotes.Select(q => (double)q.Volume).ToList();
                double avgVolume = volumes.Average();

                // 4.1 量比
                features.Add(avgVolume > 0 ? volumes.Last() / avgVolume : 1);

                // 4.2 成交量趋势
                double volumeSlope = CalculateSlope(volumes);
                features.Add(volumeSlope);

                // 4.3 价量相关性
                double priceVolumeCorr = CalculateCorrelation(closes, volumes);
                features.Add(priceVolumeCorr);

                // ==================== 5. K线形态特征 ====================
                var lastQuote = recentQuotes.Last();

                // 5.1 实体比例
                double bodyRatio = (lastQuote.High != lastQuote.Low) ?
                    (double)(Math.Abs(lastQuote.Close - lastQuote.Open) / (lastQuote.High - lastQuote.Low)) : 0;
                features.Add(bodyRatio);

                // 5.2 上影线比例
                double upperShadow = (lastQuote.High != lastQuote.Low) ?
                    (double)((lastQuote.High - Math.Max(lastQuote.Open, lastQuote.Close)) / (lastQuote.High - lastQuote.Low)) : 0;
                features.Add(upperShadow);

                // 5.3 下影线比例
                double lowerShadow = (lastQuote.High != lastQuote.Low) ?
                    (double)((Math.Min(lastQuote.Open, lastQuote.Close) - lastQuote.Low) / (lastQuote.High - lastQuote.Low)) : 0;
                features.Add(lowerShadow);

                // 5.4 收盘价在当日范围内的位置
                double dayPosition = (lastQuote.High != lastQuote.Low) ?
                    (double)((lastQuote.Close - lastQuote.Low) / (lastQuote.High - lastQuote.Low)) : 0.5;
                features.Add(dayPosition);

                // ==================== 6. 趋势特征 ====================
                // 6.1 价格斜率
                double priceSlope = CalculateSlope(closes);
                features.Add(priceSlope);

                // 6.2 均线斜率
                var maValues = new List<double>();
                for (int i = maPeriod; i <= closes.Count; i++)
                {
                    maValues.Add(closes.Skip(i - maPeriod).Take(maPeriod).Average());
                }
                double maSlope = CalculateSlope(maValues);
                features.Add(maSlope);

                // 6.3 价格在高低点的位置
                double highest = closes.Max();
                double lowest = closes.Min();
                if (highest != lowest)
                {
                    features.Add((currentClose - lowest) / (highest - lowest));
                }
                else
                {
                    features.Add(0.5);
                }

                // ==================== 7. 均值回归历史特征 ====================
                // 7.1 过去N次偏离后的回归率
                int reversionCount = 0;
                int deviationCount = 0;
                for (int i = maPeriod + 1; i < closes.Count - 1; i++)
                {
                    double localMa = closes.Skip(i - maPeriod).Take(maPeriod).Average();
                    double localStd = Math.Sqrt(closes.Skip(i - maPeriod).Take(maPeriod).Average(c => Math.Pow(c - localMa, 2)));
                    double localZ = localStd > 0 ? (closes[i] - localMa) / localStd : 0;

                    if (Math.Abs(localZ) > 1.5)
                    {
                        deviationCount++;
                        // 检查下一根K线是否回归
                        double nextZ = localStd > 0 ? (closes[i + 1] - localMa) / localStd : 0;
                        if (Math.Abs(nextZ) < Math.Abs(localZ))
                        {
                            reversionCount++;
                        }
                    }
                }
                double historicalReversionRate = deviationCount > 0 ? (double)reversionCount / deviationCount : 0.5;
                features.Add(historicalReversionRate);

                // 7.2 连续偏离天数
                int consecutiveDeviation = 0;
                for (int i = closes.Count - 1; i >= maPeriod; i--)
                {
                    double localMa = closes.Skip(i - maPeriod).Take(maPeriod).Average();
                    double localStd = Math.Sqrt(closes.Skip(i - maPeriod).Take(maPeriod).Average(c => Math.Pow(c - localMa, 2)));
                    double localZ = localStd > 0 ? (closes[i] - localMa) / localStd : 0;

                    if (Math.Abs(localZ) > 1.0)
                    {
                        consecutiveDeviation++;
                    }
                    else
                    {
                        break;
                    }
                }
                features.Add(consecutiveDeviation / 10.0); // 归一化

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

            private static double CalculateATR(List<SkQuote> quotes, int period)
            {
                if (quotes.Count < period + 1) return 0;

                double atrSum = 0;
                for (int i = quotes.Count - period; i < quotes.Count; i++)
                {
                    double tr = Math.Max((double)(quotes[i].High - quotes[i].Low),
                        Math.Max(Math.Abs((double)(quotes[i].High - quotes[i - 1].Close)),
                            Math.Abs((double)(quotes[i].Low - quotes[i - 1].Close))));
                    atrSum += tr;
                }
                return atrSum / period;
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

            private static double CalculateCorrelation(List<double> x, List<double> y)
            {
                if (x.Count != y.Count || x.Count < 2) return 0;

                double meanX = x.Average();
                double meanY = y.Average();

                double covariance = 0;
                double varX = 0, varY = 0;

                for (int i = 0; i < x.Count; i++)
                {
                    double dx = x[i] - meanX;
                    double dy = y[i] - meanY;
                    covariance += dx * dy;
                    varX += dx * dx;
                    varY += dy * dy;
                }

                double denominator = Math.Sqrt(varX * varY);
                return denominator > 0 ? covariance / denominator : 0;
            }
        }

        #endregion

        #region 状态管理

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
            public double EntryZScore { get; set; }   // 入场时Z-Score
            public double MLTargetPrice { get; set; } // ML预测的目标价位

            public int BarCount { get; set; }         // K线计数
            public GBDTModel? ProbModel { get; set; } // 回归概率预测模型
            public GBDTModel? MagModel { get; set; }  // 回归幅度预测模型
            public int LastTrainBar { get; set; }     // 上次训练的K线索引

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
                EntryZScore = 0;
                MLTargetPrice = 0;
            }
        }

        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;

            if (ArgDic == null) return;

            // ==================== 获取参数 ====================
            int lookback = Convert.ToInt32(ArgDic["lookback"]);
            int numTrees = Convert.ToInt32(ArgDic["numTrees"]);
            int maxDepth = Convert.ToInt32(ArgDic["maxDepth"]);
            double learningRate = Convert.ToDouble(ArgDic["learningRate"]);
            int trainPeriod = Convert.ToInt32(ArgDic["trainPeriod"]);
            int retrainInterval = Convert.ToInt32(ArgDic["retrainInterval"]);

            int maPeriod = Convert.ToInt32(ArgDic["maPeriod"]);
            double deviationThreshold = Convert.ToDouble(ArgDic["deviationThreshold"]);
            int useAdaptiveThreshold = Convert.ToInt32(ArgDic["useAdaptiveThreshold"]);
            double minProbability = Convert.ToDouble(ArgDic["minProbability"]);

            int trendPeriod = Convert.ToInt32(ArgDic["trendPeriod"]);
            double trendThreshold = Convert.ToDouble(ArgDic["trendThreshold"]);
            int filterTrend = Convert.ToInt32(ArgDic["filterTrend"]);

            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            double stopLossAtr = Convert.ToDouble(ArgDic["stopLossAtr"]);
            int useMLTakeProfit = Convert.ToInt32(ArgDic["useMLTakeProfit"]);
            double takeProfitAtr = Convert.ToDouble(ArgDic["takeProfitAtr"]);
            int useTrailingStop = Convert.ToInt32(ArgDic["useTrailingStop"]);
            double trailingStopAtr = Convert.ToDouble(ArgDic["trailingStopAtr"]);
            int maxHoldBars = Convert.ToInt32(ArgDic["maxHoldBars"]);

            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            // 计算最小K线数
            int minBars = Math.Max(Math.Max(lookback + trainPeriod, maPeriod + 20), Math.Max(trendPeriod, atrPeriod) + 10);
            if (tu.QuoteList.Count < minBars) return;

            var quotes = tu.QuoteList;
            var q = quotes.Last();
            decimal currentPrice = q.Close;
            var sk = tu.GetStateKey();

            // ==================== 获取或创建状态 ====================
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];
            state.BarCount++;

            // ==================== 计算基础指标 ====================
            // ATR
            var atrList = quotes.GetAtr(atrPeriod).ToList();
            decimal atrVal = (decimal)atrList.Last().Atr.GetValueOrDefault(0);

            // 均值和标准差
            var closes = quotes.Select(q2 => (double)q2.Close).ToList();
            double ma = closes.TakeLast(maPeriod).Average();
            double std = Math.Sqrt(closes.TakeLast(maPeriod).Average(c => Math.Pow(c - ma, 2)));
            double zScore = std > 0 ? ((double)currentPrice - ma) / std : 0;

            // 布林带
            double upperBand = ma + deviationThreshold * std;
            double lowerBand = ma - deviationThreshold * std;

            // ADX (趋势强度)
            var adxList = quotes.GetAdx(trendPeriod).ToList();
            double adxVal = adxList.Last().Adx.GetValueOrDefault(0);

            // ==================== ML模型训练 ====================
            if (state.ProbModel == null)
            {
                state.ProbModel = new GBDTModel(numTrees, maxDepth, learningRate);
                state.MagModel = new GBDTModel(numTrees, maxDepth, learningRate);
            }

            bool needRetrain = !state.ProbModel.IsTrained || (state.BarCount - state.LastTrainBar >= retrainInterval);
            if (needRetrain)
            {
                var (probX, probY, magY) = PrepareTrainingData(quotes, lookback, maPeriod, trainPeriod);
                if (probX.Length > 0)
                {
                    state.ProbModel.Train(probX, probY);
                    state.MagModel!.Train(probX, magY);
                    state.LastTrainBar = state.BarCount;
                }
            }

            // ==================== ML预测 ====================
            double reversionProb = 0.5;
            double reversionMagnitude = 0;

            if (state.ProbModel != null && state.ProbModel.IsTrained)
            {
                var features = MeanReversionFeatureExtractor.ExtractFeatures(quotes, lookback, maPeriod);
                if (features.Length > 0)
                {
                    reversionProb = Sigmoid(state.ProbModel.Predict(features));
                    reversionMagnitude = state.MagModel!.Predict(features);
                }
            }

            // ==================== 绘制指标 ====================
            Plot("main", "MA", PlotType.LINE, ma);
            Plot("main", "UpperBand", PlotType.LINE, upperBand);
            Plot("main", "LowerBand", PlotType.LINE, lowerBand);

            Plot("sub0", "ZScore", PlotType.LINE, zScore);
            Plot("sub0", "Threshold", PlotType.LINE, deviationThreshold);
            Plot("sub0", "Threshold", PlotType.LINE, -deviationThreshold);

            Plot("sub2", "ADX", PlotType.LINE, adxVal);
            Plot("sub2", "TrendThreshold", PlotType.LINE, trendThreshold);

            // 绘制ML预测
            Plot("sub1", "Probability", PlotType.LINE, reversionProb * 100);
            Plot("sub1", "MinProb", PlotType.LINE, minProbability * 100);

            // 绘制止损止盈线
            if (state.Status != 0)
            {
                Plot("main", "StopLoss", PlotType.LINE, (double)state.StopLoss);
                if (state.MLTargetPrice > 0)
                {
                    Plot("main", "MLTarget", PlotType.LINE, state.MLTargetPrice);
                }
            }

            // 更新持仓状态
            if (state.Status != 0)
            {
                state.HoldBars++;
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
            else if (lotsMode == 2)
            {
                // 凯利公式：f = (p * b - q) / b
                // p = 回归概率, q = 1-p, b = 预期盈亏比
                double p = reversionProb;
                double b = takeProfitAtr / stopLossAtr;
                double kellyFraction = Math.Max(0, Math.Min(0.25, (p * b - (1 - p)) / b));

                var s2 = GetSymbol(tu.MktSymbol);
                num = (decimal)(kellyFraction * (double)((decimal)ArgDic["money"] / (q.Close * s2.multiplier * s2.margin_ratio)));
                if (s2.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }

            // ==================== 自适应阈值 ====================
            double adaptiveThreshold = deviationThreshold;
            if (useAdaptiveThreshold == 1 && state.ProbModel != null && state.ProbModel.IsTrained)
            {
                // 根据历史回归率调整阈值
                // 回归概率高时可以降低阈值，回归概率低时提高阈值
                adaptiveThreshold = deviationThreshold * (1.5 - reversionProb);
                adaptiveThreshold = Math.Max(1.5, Math.Min(3.0, adaptiveThreshold));
            }

            // ==================== 信号判断 ====================
            bool isTrending = filterTrend == 1 && adxVal > trendThreshold;

            // 做多信号：价格向下偏离 + ML预测回归概率高 + 非趋势市场
            bool longSignal = zScore < -adaptiveThreshold &&
                              reversionProb > minProbability &&
                              !isTrending &&
                              mode != 2;

            // 做空信号：价格向上偏离 + ML预测回归概率高 + 非趋势市场
            bool shortSignal = zScore > adaptiveThreshold &&
                               reversionProb > minProbability &&
                               !isTrending &&
                               mode != 1;

            // ==================== 交易逻辑 ====================
            if (state.Status == 0)
            {
                // 空仓：寻找入场信号
                if (longSignal && atrVal > 0)
                {
                    state.Status = 1;
                    state.Num = num;
                    state.EntryPrice = currentPrice;
                    state.EntryAtr = atrVal;
                    state.EntryZScore = zScore;
                    state.StopLoss = currentPrice - atrVal * (decimal)stopLossAtr;

                    // ML目标价位
                    if (useMLTakeProfit == 1 && reversionMagnitude != 0)
                    {
                        state.MLTargetPrice = (double)currentPrice * (1 + reversionMagnitude);
                        state.TakeProfit = (decimal)state.MLTargetPrice;
                    }
                    else
                    {
                        state.MLTargetPrice = ma;
                        state.TakeProfit = (decimal)ma;
                    }

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
                    state.EntryZScore = zScore;
                    state.StopLoss = currentPrice + atrVal * (decimal)stopLossAtr;

                    // ML目标价位
                    if (useMLTakeProfit == 1 && reversionMagnitude != 0)
                    {
                        state.MLTargetPrice = (double)currentPrice * (1 + reversionMagnitude);
                        state.TakeProfit = (decimal)state.MLTargetPrice;
                    }
                    else
                    {
                        state.MLTargetPrice = ma;
                        state.TakeProfit = (decimal)ma;
                    }

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

                // 止损
                if (currentPrice <= state.StopLoss)
                {
                    exitSignal = true;
                }
                // 止盈：到达ML目标或回归均值
                else if (currentPrice >= state.TakeProfit)
                {
                    exitSignal = true;
                }
                // 最大持仓时间
                else if (state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Reset();
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

                // 止损
                if (currentPrice >= state.StopLoss)
                {
                    exitSignal = true;
                }
                // 止盈：到达ML目标或回归均值
                else if (currentPrice <= state.TakeProfit)
                {
                    exitSignal = true;
                }
                // 最大持仓时间
                else if (state.HoldBars >= maxHoldBars)
                {
                    exitSignal = true;
                }

                if (exitSignal)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
                    state.Reset();
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

        /// <summary>
        /// 准备训练数据
        /// </summary>
        private (double[][] X, double[] probY, double[] magY) PrepareTrainingData(
            List<SkQuote> quotes, int lookback, int maPeriod, int trainPeriod)
        {
            var X = new List<double[]>();
            var probY = new List<double>();
            var magY = new List<double>();

            int startIdx = Math.Max(lookback + maPeriod, quotes.Count - trainPeriod);
            int endIdx = quotes.Count - 5; // 留出5根K线用于计算目标

            for (int i = startIdx; i < endIdx; i++)
            {
                var subQuotes = quotes.Take(i + 1).ToList();
                var features = MeanReversionFeatureExtractor.ExtractFeatures(subQuotes, lookback, maPeriod);

                if (features.Length == 0) continue;

                // 计算当前Z-Score
                var closes = subQuotes.Select(q => (double)q.Close).ToList();
                double ma = closes.TakeLast(maPeriod).Average();
                double std = Math.Sqrt(closes.TakeLast(maPeriod).Average(c => Math.Pow(c - ma, 2)));
                double currentZ = std > 0 ? (closes.Last() - ma) / std : 0;

                // 只对偏离较大的样本进行训练
                if (Math.Abs(currentZ) < 1.0) continue;

                // 计算未来5根K线的回归情况
                double currentClose = (double)quotes[i].Close;
                double futureClose = (double)quotes[i + 5].Close;

                // 目标1：是否发生回归（价格向均值方向移动）
                bool reverted = false;
                if (currentZ > 0) // 价格高于均值
                {
                    reverted = futureClose < currentClose; // 价格下跌
                }
                else // 价格低于均值
                {
                    reverted = futureClose > currentClose; // 价格上涨
                }
                probY.Add(reverted ? 1.0 : 0.0);

                // 目标2：回归幅度（收益率）
                double returnRate = (futureClose - currentClose) / currentClose;
                magY.Add(returnRate);

                X.Add(features);
            }

            return (X.ToArray(), probY.ToArray(), magY.ToArray());
        }

        /// <summary>
        /// Sigmoid函数，将预测值转换为概率
        /// </summary>
        private double Sigmoid(double x)
        {
            return 1.0 / (1.0 + Math.Exp(-x));
        }
    }
}
