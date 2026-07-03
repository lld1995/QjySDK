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
    /// 基于LSTM的价格预测交易策略
    /// 策略逻辑：
    /// - 使用简化的LSTM神经网络预测下一根K线的价格走势
    /// - 预测上涨则做多，预测下跌则做空
    /// - 结合ATR进行止损止盈管理
    /// </summary>
    public class LSTMPredict : StgBase
    {
        public LSTMPredict()
        {
        }

        public LSTMPredict(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // LSTM模型参数
            sd.ArgDic["lookback"] = 20;              // 回看周期（序列长度）
            sd.ArgDic["hiddenSize"] = 32;            // 隐藏层大小
            sd.ArgDic["learningRate"] = 0.01;        // 学习率
            sd.ArgDic["epochs"] = 50;                // 训练轮数
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
            sd.ArgDescDic["lookback"] = new ArgDesc() { Text = "回看周期", Explain = "LSTM输入序列长度", Type = "number" };
            sd.ArgDescDic["hiddenSize"] = new ArgDesc() { Text = "隐藏层大小", Explain = "LSTM隐藏层神经元数量", Type = "number" };
            sd.ArgDescDic["learningRate"] = new ArgDesc() { Text = "学习率", Explain = "模型训练学习率", Type = "number" };
            sd.ArgDescDic["epochs"] = new ArgDesc() { Text = "训练轮数", Explain = "每次训练的迭代次数", Type = "number" };
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
            sd.ColorDic["sub1-prediction"] = "#9C27B0";
            sd.ColorDic["sub2-confidence"] = "#FF9800";

            return sd;
        }

        #region LSTM模型实现

        /// <summary>
        /// LSTM单元
        /// </summary>
        private class LSTMCell
        {
            private int _inputSize;
            private int _hiddenSize;
            private Random _random;

            // 遗忘门参数
            private double[,] _Wf = null!;  // 输入权重
            private double[] _Uf = null!;   // 隐藏状态权重
            private double[] _bf = null!;   // 偏置

            // 输入门参数
            private double[,] _Wi = null!;
            private double[] _Ui = null!;
            private double[] _bi = null!;

            // 候选记忆参数
            private double[,] _Wc = null!;
            private double[] _Uc = null!;
            private double[] _bc = null!;

            // 输出门参数
            private double[,] _Wo = null!;
            private double[] _Uo = null!;
            private double[] _bo = null!;

            // 梯度累积
            private double[,] _dWf = null!, _dWi = null!, _dWc = null!, _dWo = null!;
            private double[] _dUf = null!, _dUi = null!, _dUc = null!, _dUo = null!;
            private double[] _dbf = null!, _dbi = null!, _dbc = null!, _dbo = null!;

            public LSTMCell(int inputSize, int hiddenSize, Random random)
            {
                _inputSize = inputSize;
                _hiddenSize = hiddenSize;
                _random = random;

                InitializeWeights();
            }

            private void InitializeWeights()
            {
                double scale = Math.Sqrt(2.0 / (_inputSize + _hiddenSize));

                _Wf = InitMatrix(_hiddenSize, _inputSize, scale);
                _Uf = InitVector(_hiddenSize, scale);
                _bf = new double[_hiddenSize];

                _Wi = InitMatrix(_hiddenSize, _inputSize, scale);
                _Ui = InitVector(_hiddenSize, scale);
                _bi = new double[_hiddenSize];

                _Wc = InitMatrix(_hiddenSize, _inputSize, scale);
                _Uc = InitVector(_hiddenSize, scale);
                _bc = new double[_hiddenSize];

                _Wo = InitMatrix(_hiddenSize, _inputSize, scale);
                _Uo = InitVector(_hiddenSize, scale);
                _bo = new double[_hiddenSize];

                // 初始化遗忘门偏置为1（帮助记忆长期依赖）
                for (int i = 0; i < _hiddenSize; i++)
                    _bf[i] = 1.0;

                ResetGradients();
            }

            private double[,] InitMatrix(int rows, int cols, double scale)
            {
                var matrix = new double[rows, cols];
                for (int i = 0; i < rows; i++)
                    for (int j = 0; j < cols; j++)
                        matrix[i, j] = (_random.NextDouble() * 2 - 1) * scale;
                return matrix;
            }

            private double[] InitVector(int size, double scale)
            {
                var vector = new double[size];
                for (int i = 0; i < size; i++)
                    vector[i] = (_random.NextDouble() * 2 - 1) * scale;
                return vector;
            }

            public void ResetGradients()
            {
                _dWf = new double[_hiddenSize, _inputSize];
                _dWi = new double[_hiddenSize, _inputSize];
                _dWc = new double[_hiddenSize, _inputSize];
                _dWo = new double[_hiddenSize, _inputSize];

                _dUf = new double[_hiddenSize];
                _dUi = new double[_hiddenSize];
                _dUc = new double[_hiddenSize];
                _dUo = new double[_hiddenSize];

                _dbf = new double[_hiddenSize];
                _dbi = new double[_hiddenSize];
                _dbc = new double[_hiddenSize];
                _dbo = new double[_hiddenSize];
            }

            /// <summary>
            /// 前向传播
            /// </summary>
            public (double[] h, double[] c, LSTMCache cache) Forward(double[] x, double[] hPrev, double[] cPrev)
            {
                var cache = new LSTMCache
                {
                    x = x,
                    hPrev = hPrev,
                    cPrev = cPrev
                };

                // 遗忘门: f = sigmoid(Wf * x + Uf * hPrev + bf)
                cache.f = new double[_hiddenSize];
                for (int i = 0; i < _hiddenSize; i++)
                {
                    double sum = _bf[i] + _Uf[i] * hPrev[i];
                    for (int j = 0; j < _inputSize; j++)
                        sum += _Wf[i, j] * x[j];
                    cache.f[i] = Sigmoid(sum);
                }

                // 输入门: i = sigmoid(Wi * x + Ui * hPrev + bi)
                cache.i = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                {
                    double sum = _bi[k] + _Ui[k] * hPrev[k];
                    for (int j = 0; j < _inputSize; j++)
                        sum += _Wi[k, j] * x[j];
                    cache.i[k] = Sigmoid(sum);
                }

                // 候选记忆: cTilde = tanh(Wc * x + Uc * hPrev + bc)
                cache.cTilde = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                {
                    double sum = _bc[k] + _Uc[k] * hPrev[k];
                    for (int j = 0; j < _inputSize; j++)
                        sum += _Wc[k, j] * x[j];
                    cache.cTilde[k] = Tanh(sum);
                }

                // 输出门: o = sigmoid(Wo * x + Uo * hPrev + bo)
                cache.o = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                {
                    double sum = _bo[k] + _Uo[k] * hPrev[k];
                    for (int j = 0; j < _inputSize; j++)
                        sum += _Wo[k, j] * x[j];
                    cache.o[k] = Sigmoid(sum);
                }

                // 新的记忆状态: c = f * cPrev + i * cTilde
                var c = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                    c[k] = cache.f[k] * cPrev[k] + cache.i[k] * cache.cTilde[k];
                cache.c = c;

                // 新的隐藏状态: h = o * tanh(c)
                var h = new double[_hiddenSize];
                cache.tanhC = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                {
                    cache.tanhC[k] = Tanh(c[k]);
                    h[k] = cache.o[k] * cache.tanhC[k];
                }

                return (h, c, cache);
            }

            /// <summary>
            /// 反向传播
            /// </summary>
            public (double[] dhPrev, double[] dcPrev) Backward(double[] dh, double[] dcNext, LSTMCache cache)
            {
                // 输出门梯度
                var do_ = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                    do_[k] = dh[k] * cache.tanhC[k] * SigmoidDerivative(cache.o[k]);

                // 记忆状态梯度
                var dc = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                    dc[k] = dh[k] * cache.o[k] * TanhDerivative(cache.tanhC[k]) + dcNext[k];

                // 遗忘门梯度
                var df = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                    df[k] = dc[k] * cache.cPrev[k] * SigmoidDerivative(cache.f[k]);

                // 输入门梯度
                var di = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                    di[k] = dc[k] * cache.cTilde[k] * SigmoidDerivative(cache.i[k]);

                // 候选记忆梯度
                var dcTilde = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                    dcTilde[k] = dc[k] * cache.i[k] * TanhDerivative(cache.cTilde[k]);

                // 累积权重梯度
                for (int k = 0; k < _hiddenSize; k++)
                {
                    for (int j = 0; j < _inputSize; j++)
                    {
                        _dWf[k, j] += df[k] * cache.x[j];
                        _dWi[k, j] += di[k] * cache.x[j];
                        _dWc[k, j] += dcTilde[k] * cache.x[j];
                        _dWo[k, j] += do_[k] * cache.x[j];
                    }
                    _dUf[k] += df[k] * cache.hPrev[k];
                    _dUi[k] += di[k] * cache.hPrev[k];
                    _dUc[k] += dcTilde[k] * cache.hPrev[k];
                    _dUo[k] += do_[k] * cache.hPrev[k];

                    _dbf[k] += df[k];
                    _dbi[k] += di[k];
                    _dbc[k] += dcTilde[k];
                    _dbo[k] += do_[k];
                }

                // 计算传递给前一时间步的梯度
                var dhPrev = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                {
                    dhPrev[k] = df[k] * _Uf[k] + di[k] * _Ui[k] + dcTilde[k] * _Uc[k] + do_[k] * _Uo[k];
                }

                var dcPrev = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                    dcPrev[k] = dc[k] * cache.f[k];

                return (dhPrev, dcPrev);
            }

            /// <summary>
            /// 更新权重
            /// </summary>
            public void UpdateWeights(double learningRate, int batchSize)
            {
                double scale = learningRate / batchSize;
                double clipValue = 5.0;

                for (int i = 0; i < _hiddenSize; i++)
                {
                    for (int j = 0; j < _inputSize; j++)
                    {
                        _Wf[i, j] -= scale * Clip(_dWf[i, j], clipValue);
                        _Wi[i, j] -= scale * Clip(_dWi[i, j], clipValue);
                        _Wc[i, j] -= scale * Clip(_dWc[i, j], clipValue);
                        _Wo[i, j] -= scale * Clip(_dWo[i, j], clipValue);
                    }
                    _Uf[i] -= scale * Clip(_dUf[i], clipValue);
                    _Ui[i] -= scale * Clip(_dUi[i], clipValue);
                    _Uc[i] -= scale * Clip(_dUc[i], clipValue);
                    _Uo[i] -= scale * Clip(_dUo[i], clipValue);

                    _bf[i] -= scale * Clip(_dbf[i], clipValue);
                    _bi[i] -= scale * Clip(_dbi[i], clipValue);
                    _bc[i] -= scale * Clip(_dbc[i], clipValue);
                    _bo[i] -= scale * Clip(_dbo[i], clipValue);
                }

                ResetGradients();
            }

            private double Clip(double value, double clipValue)
            {
                return Math.Max(-clipValue, Math.Min(clipValue, value));
            }

            private double Sigmoid(double x)
            {
                if (x > 20) return 1.0;
                if (x < -20) return 0.0;
                return 1.0 / (1.0 + Math.Exp(-x));
            }

            private double SigmoidDerivative(double sigmoidX)
            {
                return sigmoidX * (1 - sigmoidX);
            }

            private double Tanh(double x)
            {
                if (x > 20) return 1.0;
                if (x < -20) return -1.0;
                return Math.Tanh(x);
            }

            private double TanhDerivative(double tanhX)
            {
                return 1 - tanhX * tanhX;
            }
        }

        /// <summary>
        /// LSTM缓存（用于反向传播）
        /// </summary>
        private class LSTMCache
        {
            public double[] x { get; set; } = Array.Empty<double>();
            public double[] hPrev { get; set; } = Array.Empty<double>();
            public double[] cPrev { get; set; } = Array.Empty<double>();
            public double[] f { get; set; } = Array.Empty<double>();
            public double[] i { get; set; } = Array.Empty<double>();
            public double[] cTilde { get; set; } = Array.Empty<double>();
            public double[] o { get; set; } = Array.Empty<double>();
            public double[] c { get; set; } = Array.Empty<double>();
            public double[] tanhC { get; set; } = Array.Empty<double>();
        }

        /// <summary>
        /// LSTM模型
        /// </summary>
        private class LSTMModel
        {
            private LSTMCell _lstmCell;
            private int _inputSize;
            private int _hiddenSize;
            private double _learningRate;
            private int _epochs;
            private Random _random;

            // 输出层参数
            private double[] _Wy;
            private double _by;
            private double _dWy_sum;
            private double _dby_sum;

            // 数据标准化参数
            private double[] _featureMean;
            private double[] _featureStd;
            private double _targetMean;
            private double _targetStd;

            public bool IsTrained { get; private set; }
            public double LastLoss { get; private set; }

            public LSTMModel(int inputSize, int hiddenSize, double learningRate, int epochs)
            {
                _inputSize = inputSize;
                _hiddenSize = hiddenSize;
                _learningRate = learningRate;
                _epochs = epochs;
                _random = new Random(42);

                _lstmCell = new LSTMCell(inputSize, hiddenSize, _random);

                // 初始化输出层
                double scale = Math.Sqrt(2.0 / hiddenSize);
                _Wy = new double[hiddenSize];
                for (int i = 0; i < hiddenSize; i++)
                    _Wy[i] = (_random.NextDouble() * 2 - 1) * scale;
                _by = 0;

                _featureMean = new double[inputSize];
                _featureStd = new double[inputSize];
                for (int i = 0; i < inputSize; i++)
                    _featureStd[i] = 1.0;
                _targetStd = 1.0;

                IsTrained = false;
            }

            /// <summary>
            /// 训练模型
            /// </summary>
            public void Train(double[][][] sequences, double[] targets)
            {
                if (sequences.Length == 0 || targets.Length == 0) return;

                // 标准化数据
                NormalizeData(sequences, targets);

                int seqLen = sequences[0].Length;

                for (int epoch = 0; epoch < _epochs; epoch++)
                {
                    double totalLoss = 0;
                    _lstmCell.ResetGradients();
                    _dWy_sum = 0;
                    _dby_sum = 0;

                    // 打乱训练顺序
                    var indices = Enumerable.Range(0, sequences.Length).OrderBy(_ => _random.Next()).ToArray();

                    foreach (int idx in indices)
                    {
                        var sequence = sequences[idx];
                        double target = (targets[idx] - _targetMean) / (_targetStd + 1e-8);

                        // 前向传播
                        var h = new double[_hiddenSize];
                        var c = new double[_hiddenSize];
                        var caches = new List<LSTMCache>();

                        for (int t = 0; t < seqLen; t++)
                        {
                            var normalizedX = NormalizeFeatures(sequence[t]);
                            var (hNew, cNew, cache) = _lstmCell.Forward(normalizedX, h, c);
                            h = hNew;
                            c = cNew;
                            caches.Add(cache);
                        }

                        // 计算输出
                        double output = _by;
                        for (int i = 0; i < _hiddenSize; i++)
                            output += _Wy[i] * h[i];

                        // 计算损失
                        double error = output - target;
                        totalLoss += error * error;

                        // 反向传播
                        var dh = new double[_hiddenSize];
                        for (int i = 0; i < _hiddenSize; i++)
                        {
                            dh[i] = 2 * error * _Wy[i];
                            _dWy_sum += error * h[i];
                        }
                        _dby_sum += error;

                        var dc = new double[_hiddenSize];
                        for (int t = seqLen - 1; t >= 0; t--)
                        {
                            var (dhPrev, dcPrev) = _lstmCell.Backward(dh, dc, caches[t]);
                            dh = dhPrev;
                            dc = dcPrev;
                        }
                    }

                    // 更新权重
                    _lstmCell.UpdateWeights(_learningRate, sequences.Length);

                    double clipValue = 5.0;
                    double scale = _learningRate / sequences.Length;
                    for (int i = 0; i < _hiddenSize; i++)
                        _Wy[i] -= scale * Clip(_dWy_sum * _Wy[i] / _hiddenSize, clipValue);
                    _by -= scale * Clip(_dby_sum, clipValue);

                    LastLoss = totalLoss / sequences.Length;

                    // 早停
                    if (LastLoss < 1e-6) break;
                }

                IsTrained = true;
            }

            private double Clip(double value, double clipValue)
            {
                return Math.Max(-clipValue, Math.Min(clipValue, value));
            }

            /// <summary>
            /// 标准化数据
            /// </summary>
            private void NormalizeData(double[][][] sequences, double[] targets)
            {
                // 计算特征均值和标准差
                var allFeatures = new List<double[]>();
                foreach (var seq in sequences)
                    foreach (var features in seq)
                        allFeatures.Add(features);

                for (int f = 0; f < _inputSize; f++)
                {
                    var values = allFeatures.Select(x => x[f]).ToArray();
                    _featureMean[f] = values.Average();
                    double variance = values.Average(v => Math.Pow(v - _featureMean[f], 2));
                    _featureStd[f] = Math.Sqrt(variance);
                    if (_featureStd[f] < 1e-8) _featureStd[f] = 1.0;
                }

                // 计算目标均值和标准差
                _targetMean = targets.Average();
                double targetVariance = targets.Average(v => Math.Pow(v - _targetMean, 2));
                _targetStd = Math.Sqrt(targetVariance);
                if (_targetStd < 1e-8) _targetStd = 1.0;
            }

            private double[] NormalizeFeatures(double[] features)
            {
                var normalized = new double[features.Length];
                for (int i = 0; i < features.Length; i++)
                    normalized[i] = (features[i] - _featureMean[i]) / (_featureStd[i] + 1e-8);
                return normalized;
            }

            /// <summary>
            /// 预测
            /// </summary>
            public double Predict(double[][] sequence)
            {
                if (!IsTrained || sequence.Length == 0) return 0;

                var h = new double[_hiddenSize];
                var c = new double[_hiddenSize];

                foreach (var features in sequence)
                {
                    var normalizedX = NormalizeFeatures(features);
                    var (hNew, cNew, _) = _lstmCell.Forward(normalizedX, h, c);
                    h = hNew;
                    c = cNew;
                }

                double output = _by;
                for (int i = 0; i < _hiddenSize; i++)
                    output += _Wy[i] * h[i];

                // 反标准化
                return output * _targetStd + _targetMean;
            }

            /// <summary>
            /// 获取预测置信度（基于隐藏状态的激活程度）
            /// </summary>
            public double GetConfidence(double[][] sequence)
            {
                if (!IsTrained || sequence.Length == 0) return 0;

                var h = new double[_hiddenSize];
                var c = new double[_hiddenSize];

                foreach (var features in sequence)
                {
                    var normalizedX = NormalizeFeatures(features);
                    var (hNew, cNew, _) = _lstmCell.Forward(normalizedX, h, c);
                    h = hNew;
                    c = cNew;
                }

                // 计算隐藏状态的平均激活程度
                double avgActivation = h.Average(x => Math.Abs(x));
                return Math.Min(1.0, avgActivation);
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
            /// 提取单个时间步的特征
            /// </summary>
            public static double[] ExtractFeatures(SkQuote current, SkQuote? previous, double ma5, double ma10, double ma20)
            {
                var features = new List<double>();

                // 1. 价格变化率
                if (previous != null && previous.Close != 0)
                    features.Add((double)((current.Close - previous.Close) / previous.Close));
                else
                    features.Add(0);

                // 2. 成交量变化率
                if (previous != null && previous.Volume != 0)
                    features.Add((double)((current.Volume - previous.Volume) / previous.Volume));
                else
                    features.Add(0);

                // 3. 高低价振幅
                if (current.Low != 0)
                    features.Add((double)((current.High - current.Low) / current.Low));
                else
                    features.Add(0);

                // 4. 收盘价相对位置 (0-1)
                if (current.High != current.Low)
                    features.Add((double)((current.Close - current.Low) / (current.High - current.Low)));
                else
                    features.Add(0.5);

                // 5. 相对于均线的位置
                double close = (double)current.Close;
                features.Add(ma5 != 0 ? (close - ma5) / ma5 : 0);
                features.Add(ma10 != 0 ? (close - ma10) / ma10 : 0);
                features.Add(ma20 != 0 ? (close - ma20) / ma20 : 0);

                // 8. 开盘价与收盘价关系
                if (current.Open != 0)
                    features.Add((double)((current.Close - current.Open) / current.Open));
                else
                    features.Add(0);

                return features.ToArray();
            }

            /// <summary>
            /// 提取序列特征
            /// </summary>
            public static double[][] ExtractSequence(List<SkQuote> quotes, int lookback)
            {
                if (quotes.Count < lookback + 20) return Array.Empty<double[]>();

                var sequence = new List<double[]>();
                int startIdx = quotes.Count - lookback;

                for (int i = startIdx; i < quotes.Count; i++)
                {
                    var current = quotes[i];
                    var previous = i > 0 ? quotes[i - 1] : null;

                    // 计算移动平均
                    int ma5Start = Math.Max(0, i - 4);
                    int ma10Start = Math.Max(0, i - 9);
                    int ma20Start = Math.Max(0, i - 19);

                    double ma5 = quotes.Skip(ma5Start).Take(i - ma5Start + 1).Average(q => (double)q.Close);
                    double ma10 = quotes.Skip(ma10Start).Take(i - ma10Start + 1).Average(q => (double)q.Close);
                    double ma20 = quotes.Skip(ma20Start).Take(i - ma20Start + 1).Average(q => (double)q.Close);

                    var features = ExtractFeatures(current, previous, ma5, ma10, ma20);
                    sequence.Add(features);
                }

                return sequence.ToArray();
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
            public LSTMModel? Model { get; set; }   // LSTM模型
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
            int hiddenSize = Convert.ToInt32(ArgDic["hiddenSize"]);
            double learningRate = Convert.ToDouble(ArgDic["learningRate"]);
            int epochs = Convert.ToInt32(ArgDic["epochs"]);
            int trainPeriod = Convert.ToInt32(ArgDic["trainPeriod"]);
            int retrainInterval = Convert.ToInt32(ArgDic["retrainInterval"]);
            double threshold = Convert.ToDouble(ArgDic["threshold"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            double atrMultiplier = Convert.ToDouble(ArgDic["atrMultiplier"]);
            double takeProfitMultiplier = Convert.ToDouble(ArgDic["takeProfitMultiplier"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            // 检查数据量
            int minBars = Math.Max(lookback + trainPeriod + 20, atrPeriod + 1);
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

            // 特征维度（与ExtractFeatures返回的特征数量一致）
            int inputSize = 8;

            // 检查是否需要训练/重训练
            if (s.Model == null)
            {
                s.Model = new LSTMModel(inputSize, hiddenSize, learningRate, epochs);
            }

            if (!s.Model.IsTrained || (s.BarCount - s.LastTrainBar >= retrainInterval))
            {
                var (sequences, targets) = PrepareTrainingData(tu.QuoteList, lookback, trainPeriod);
                if (sequences.Length > 0)
                {
                    s.Model.Train(sequences, targets);
                    s.LastTrainBar = s.BarCount;
                }
            }

            // 预测
            double prediction = 0;
            double confidence = 0;
            if (s.Model != null && s.Model.IsTrained)
            {
                var sequence = FeatureExtractor.ExtractSequence(tu.QuoteList, lookback);
                if (sequence.Length > 0)
                {
                    prediction = s.Model.Predict(sequence);
                    confidence = s.Model.GetConfidence(sequence);
                }
            }

            // 绘制预测信号
            Plot("sub1", "prediction", PlotType.CURVE, prediction * 100);
            Plot("sub2", "confidence", PlotType.CURVE, confidence * 100);

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
        private (double[][][] sequences, double[] targets) PrepareTrainingData(List<SkQuote> quotes, int lookback, int trainPeriod)
        {
            var sequences = new List<double[][]>();
            var targets = new List<double>();

            int startIdx = Math.Max(lookback + 20, quotes.Count - trainPeriod);
            int endIdx = quotes.Count - 1;

            for (int i = startIdx; i < endIdx; i++)
            {
                var subQuotes = quotes.Take(i + 1).ToList();
                var sequence = FeatureExtractor.ExtractSequence(subQuotes, lookback);

                if (sequence.Length == 0) continue;

                // 目标：下一根K线的收益率
                var currentClose = quotes[i].Close;
                var nextClose = quotes[i + 1].Close;
                double target = (double)((nextClose - currentClose) / currentClose);

                sequences.Add(sequence);
                targets.Add(target);
            }

            return (sequences.ToArray(), targets.ToArray());
        }
    }
}
