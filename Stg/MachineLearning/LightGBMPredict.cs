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
    /// 基于LightGBM的价格预测交易策略
    /// 策略逻辑：
    /// - 使用简化的梯度提升决策树(GBDT)预测下一根K线的价格走势
    /// - 预测上涨则做多，预测下跌则做空
    /// - 结合ATR进行止损止盈管理
    /// </summary>
    public class LightGBMPredict : StgBase
    {
        public LightGBMPredict()
        {
        }

        public LightGBMPredict(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // LightGBM模型参数
            sd.ArgDic["lookback"] = 20;              // 回看周期（特征窗口）
            sd.ArgDic["numTrees"] = 50;              // 树的数量
            sd.ArgDic["maxDepth"] = 5;               // 树的最大深度
            sd.ArgDic["learningRate"] = 0.1;         // 学习率
            sd.ArgDic["minSamplesLeaf"] = 5;         // 叶子节点最小样本数
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
            sd.ArgDescDic["lookback"] = new ArgDesc() { Text = "回看周期", Explain = "特征计算窗口长度", Type = "number" };
            sd.ArgDescDic["numTrees"] = new ArgDesc() { Text = "树数量", Explain = "GBDT中树的数量", Type = "number" };
            sd.ArgDescDic["maxDepth"] = new ArgDesc() { Text = "最大深度", Explain = "每棵树的最大深度", Type = "number" };
            sd.ArgDescDic["learningRate"] = new ArgDesc() { Text = "学习率", Explain = "模型训练学习率", Type = "number" };
            sd.ArgDescDic["minSamplesLeaf"] = new ArgDesc() { Text = "叶子最小样本", Explain = "叶子节点最小样本数", Type = "number" };
            sd.ArgDescDic["trainPeriod"] = new ArgDesc() { Text = "训练周期", Explain = "用于训练的历史K线数量", Type = "number" };
            sd.ArgDescDic["retrainInterval"] = new ArgDesc() { Text = "重训间隔", Explain = "每隔多少根K线重新训练模型", Type = "number" };
            sd.ArgDescDic["threshold"] = new ArgDesc() { Text = "预测阈值", Explain = "预测涨跌幅超过此值才交易", Type = "number" };
            sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "计算ATR的周期", Type = "number" };
            sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "止损倍数", Explain = "ATR止损倍数", Type = "number" };
            sd.ArgDescDic["takeProfitMultiplier"] = new ArgDesc() { Text = "止盈倍数", Explain = "ATR止盈倍数", Type = "number" };
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
            sd.ColorDic["sub1-prediction"] = "#9C27B0";
            sd.ColorDic["sub2-importance"] = "#FF9800";

            return sd;
        }

        #region LightGBM模型实现

        /// <summary>
        /// 决策树节点
        /// </summary>
        private class TreeNode
        {
            public int FeatureIndex { get; set; } = -1;
            public double SplitValue { get; set; }
            public double LeafValue { get; set; }
            public TreeNode? Left { get; set; }
            public TreeNode? Right { get; set; }
            public bool IsLeaf => Left == null && Right == null;
        }

        /// <summary>
        /// 梯度提升决策树
        /// </summary>
        private class DecisionTree
        {
            private TreeNode? _root;
            private int _maxDepth;
            private int _minSamplesLeaf;
            private Random _random;

            public DecisionTree(int maxDepth, int minSamplesLeaf, Random random)
            {
                _maxDepth = maxDepth;
                _minSamplesLeaf = minSamplesLeaf;
                _random = random;
            }

            public void Fit(double[][] X, double[] residuals)
            {
                var indices = Enumerable.Range(0, X.Length).ToList();
                _root = BuildTree(X, residuals, indices, 0);
            }

            private TreeNode BuildTree(double[][] X, double[] residuals, List<int> indices, int depth)
            {
                var node = new TreeNode();

                // 检查停止条件
                if (depth >= _maxDepth || indices.Count < _minSamplesLeaf * 2)
                {
                    node.LeafValue = indices.Count > 0 ? indices.Average(i => residuals[i]) : 0;
                    return node;
                }

                // 寻找最佳分割
                int bestFeature = -1;
                double bestSplit = 0;
                double bestGain = double.MinValue;
                List<int>? bestLeft = null;
                List<int>? bestRight = null;

                int numFeatures = X[0].Length;
                // 随机选择部分特征（类似随机森林的特征采样）
                int featuresToCheck = Math.Max(1, (int)Math.Sqrt(numFeatures));
                var featureIndices = Enumerable.Range(0, numFeatures)
                    .OrderBy(_ => _random.Next())
                    .Take(featuresToCheck)
                    .ToList();

                foreach (int f in featureIndices)
                {
                    // 获取该特征的所有值并排序
                    var values = indices.Select(i => X[i][f]).Distinct().OrderBy(v => v).ToList();
                    if (values.Count < 2) continue;

                    // 尝试不同的分割点
                    for (int i = 0; i < values.Count - 1; i++)
                    {
                        double split = (values[i] + values[i + 1]) / 2;
                        var left = indices.Where(idx => X[idx][f] <= split).ToList();
                        var right = indices.Where(idx => X[idx][f] > split).ToList();

                        if (left.Count < _minSamplesLeaf || right.Count < _minSamplesLeaf)
                            continue;

                        double gain = CalculateGain(residuals, indices, left, right);
                        if (gain > bestGain)
                        {
                            bestGain = gain;
                            bestFeature = f;
                            bestSplit = split;
                            bestLeft = left;
                            bestRight = right;
                        }
                    }
                }

                // 如果找不到好的分割，返回叶子节点
                if (bestFeature == -1 || bestLeft == null || bestRight == null)
                {
                    node.LeafValue = indices.Count > 0 ? indices.Average(i => residuals[i]) : 0;
                    return node;
                }

                node.FeatureIndex = bestFeature;
                node.SplitValue = bestSplit;
                node.Left = BuildTree(X, residuals, bestLeft, depth + 1);
                node.Right = BuildTree(X, residuals, bestRight, depth + 1);

                return node;
            }

            private double CalculateGain(double[] residuals, List<int> parent, List<int> left, List<int> right)
            {
                if (left.Count == 0 || right.Count == 0) return double.MinValue;

                double parentVar = CalculateVariance(residuals, parent);
                double leftVar = CalculateVariance(residuals, left);
                double rightVar = CalculateVariance(residuals, right);

                double leftWeight = (double)left.Count / parent.Count;
                double rightWeight = (double)right.Count / parent.Count;

                return parentVar - (leftWeight * leftVar + rightWeight * rightVar);
            }

            private double CalculateVariance(double[] values, List<int> indices)
            {
                if (indices.Count == 0) return 0;
                double mean = indices.Average(i => values[i]);
                return indices.Average(i => Math.Pow(values[i] - mean, 2));
            }

            public double Predict(double[] x)
            {
                if (_root == null) return 0;
                return PredictNode(_root, x);
            }

            private double PredictNode(TreeNode node, double[] x)
            {
                if (node.IsLeaf)
                    return node.LeafValue;

                if (x[node.FeatureIndex] <= node.SplitValue)
                    return node.Left != null ? PredictNode(node.Left, x) : 0;
                else
                    return node.Right != null ? PredictNode(node.Right, x) : 0;
            }
        }

        /// <summary>
        /// LightGBM模型（简化版GBDT）
        /// </summary>
        private class LightGBMModel
        {
            private List<DecisionTree> _trees;
            private int _numTrees;
            private int _maxDepth;
            private int _minSamplesLeaf;
            private double _learningRate;
            private double _basePrediction;
            private Random _random;
            private double[]? _featureImportance;

            public bool IsTrained { get; private set; }
            public double[] FeatureImportance => _featureImportance ?? Array.Empty<double>();

            public LightGBMModel(int numTrees, int maxDepth, int minSamplesLeaf, double learningRate)
            {
                _numTrees = numTrees;
                _maxDepth = maxDepth;
                _minSamplesLeaf = minSamplesLeaf;
                _learningRate = learningRate;
                _trees = new List<DecisionTree>();
                _random = new Random(42);
                IsTrained = false;
            }

            public void Train(double[][] X, double[] y)
            {
                if (X.Length == 0 || y.Length == 0) return;

                _trees.Clear();
                int numFeatures = X[0].Length;
                _featureImportance = new double[numFeatures];

                // 初始预测为目标均值
                _basePrediction = y.Average();

                // 当前预测值
                var predictions = new double[y.Length];
                for (int i = 0; i < predictions.Length; i++)
                    predictions[i] = _basePrediction;

                // 迭代构建树
                for (int t = 0; t < _numTrees; t++)
                {
                    // 计算残差（负梯度）
                    var residuals = new double[y.Length];
                    for (int i = 0; i < y.Length; i++)
                        residuals[i] = y[i] - predictions[i];

                    // 训练新树拟合残差
                    var tree = new DecisionTree(_maxDepth, _minSamplesLeaf, _random);
                    tree.Fit(X, residuals);
                    _trees.Add(tree);

                    // 更新预测
                    for (int i = 0; i < predictions.Length; i++)
                        predictions[i] += _learningRate * tree.Predict(X[i]);

                    // 早停检查
                    double mse = 0;
                    for (int i = 0; i < y.Length; i++)
                        mse += Math.Pow(y[i] - predictions[i], 2);
                    mse /= y.Length;

                    if (mse < 1e-8) break;
                }

                // 计算特征重要性（基于使用频率的简化版本）
                CalculateFeatureImportance(X);

                IsTrained = true;
            }

            private void CalculateFeatureImportance(double[][] X)
            {
                if (_featureImportance == null || X.Length == 0) return;

                // 通过扰动每个特征来估计重要性
                int numFeatures = X[0].Length;
                var baselinePredictions = X.Select(x => Predict(x)).ToArray();
                double baselineVar = CalculateVariance(baselinePredictions);

                for (int f = 0; f < numFeatures; f++)
                {
                    // 打乱该特征
                    var shuffledX = X.Select(x => (double[])x.Clone()).ToArray();
                    var featureValues = X.Select(x => x[f]).OrderBy(_ => _random.Next()).ToArray();
                    for (int i = 0; i < shuffledX.Length; i++)
                        shuffledX[i][f] = featureValues[i];

                    var shuffledPredictions = shuffledX.Select(x => Predict(x)).ToArray();
                    double shuffledVar = CalculateVariance(shuffledPredictions);

                    // 重要性 = 预测方差的变化
                    _featureImportance[f] = Math.Max(0, baselineVar - shuffledVar);
                }

                // 归一化
                double sum = _featureImportance.Sum();
                if (sum > 0)
                {
                    for (int i = 0; i < _featureImportance.Length; i++)
                        _featureImportance[i] /= sum;
                }
            }

            private double CalculateVariance(double[] values)
            {
                if (values.Length == 0) return 0;
                double mean = values.Average();
                return values.Average(v => Math.Pow(v - mean, 2));
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
        /// 特征提取器
        /// </summary>
        private static class FeatureExtractor
        {
            /// <summary>
            /// 提取技术指标特征
            /// </summary>
            public static double[] ExtractFeatures(List<SkQuote> quotes, int lookback)
            {
                if (quotes.Count < lookback) return Array.Empty<double>();

                var recentQuotes = quotes.Skip(quotes.Count - lookback).ToList();
                var features = new List<double>();

                // 1. 价格变化率特征
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

                // 3. 高低价振幅
                var lastQuote = recentQuotes.Last();
                features.Add(lastQuote.Low != 0 ? (double)((lastQuote.High - lastQuote.Low) / lastQuote.Low) : 0);

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
                }
                else
                {
                    features.Add(0);
                }

                // 7. RSI特征
                double rsi = CalculateRSI(closes, 14);
                features.Add(rsi / 100.0); // 归一化到0-1

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
                // 归一化斜率
                double avgPrice = sumY / n;
                return avgPrice != 0 ? slope / avgPrice : 0;
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
            public LightGBMModel? Model { get; set; }   // LightGBM模型
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
            int minSamplesLeaf = Convert.ToInt32(ArgDic["minSamplesLeaf"]);
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
                s.Model = new LightGBMModel(numTrees, maxDepth, minSamplesLeaf, learningRate);
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
                var features = FeatureExtractor.ExtractFeatures(tu.QuoteList, lookback);
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
                var features = FeatureExtractor.ExtractFeatures(subQuotes, lookback);

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
