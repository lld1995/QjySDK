using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    /// <summary>
    /// 混合专家预测策略 (Mixture of Experts Prediction)
    /// 
    /// 三个独立专家模型加权投票预测下一根K线涨跌，每根K线都做开平仓操作：
    /// 
    /// Expert 1 - 统计与概率模型：
    ///   - Markov链：2阶状态转移矩阵，统计涨跌序列的条件概率
    ///   - Bayesian条件概率：RSI区间 × 成交量区间 × 趋势区间，离散分箱统计
    ///   - K线形态识别：锤子线、吞没、十字星、射击之星等经典形态
    /// 
    /// Expert 2 - LSTM机器学习模型：
    ///   - 手写LSTM前向/反向传播，8维特征输入
    ///   - 在线训练 + 定期重训
    ///   - 预测下根K线收益率，映射为0-100分
    /// 
    /// Expert 3 - 量化因子模型：
    ///   - 动量因子(ROC+RSI)、趋势因子(EMA+ADX)、波动率因子(ATR+BB)
    ///   - 成交量因子(OBV+量比)、均值回归因子(偏离度)
    ///   - 5因子加权合成0-100分
    /// 
    /// 元决策：加权投票（无LLM）
    ///   finalScore = stat*w1 + lstm*w2 + quant*w3
    ///   > 50 做多，< 50 做空
    /// 
    /// 交易模式：每根K线都做开平仓
    ///   - 平掉上一根bar的仓位
    ///   - 按预测方向开新仓
    ///   - 持仓周期恒定为1根K线，无止损止盈
    /// </summary>
    public class MoEPredict : PolyStgBase
    {
        public MoEPredict()
        {
        }

        public MoEPredict(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 0;

            // ==================== 统计模型参数 ====================
            sd.ArgDescDic["markovOrder"] = new ArgDesc { Text = "Markov阶数", Explain = "Markov链的阶数(1或2)" };
            sd.ArgDic["markovOrder"] = 2;

            sd.ArgDescDic["statWeight"] = new ArgDesc { Text = "统计模型权重", Explain = "统计模型在综合得分中的权重(0-100)" };
            sd.ArgDic["statWeight"] = 30.0;

            // ==================== LSTM参数 ====================
            sd.ArgDescDic["lookback"] = new ArgDesc { Text = "回看周期", Explain = "LSTM输入序列长度" };
            sd.ArgDic["lookback"] = 10;

            sd.ArgDescDic["hiddenSize"] = new ArgDesc { Text = "隐藏层大小", Explain = "LSTM隐藏层神经元数量" };
            sd.ArgDic["hiddenSize"] = 16;

            sd.ArgDescDic["learningRate"] = new ArgDesc { Text = "学习率", Explain = "LSTM训练学习率" };
            sd.ArgDic["learningRate"] = 0.01;

            sd.ArgDescDic["epochs"] = new ArgDesc { Text = "训练轮数", Explain = "每次训练的迭代次数" };
            sd.ArgDic["epochs"] = 10;

            sd.ArgDescDic["trainPeriod"] = new ArgDesc { Text = "训练周期", Explain = "用于训练的历史K线数量" };
            sd.ArgDic["trainPeriod"] = 100;

            sd.ArgDescDic["retrainInterval"] = new ArgDesc { Text = "重训间隔", Explain = "每隔多少根K线重新训练" };
            sd.ArgDic["retrainInterval"] = 200;

            sd.ArgDescDic["lstmWeight"] = new ArgDesc { Text = "LSTM权重", Explain = "LSTM在综合得分中的权重(0-100)" };
            sd.ArgDic["lstmWeight"] = 35.0;

            // ==================== 量化因子参数 ====================
            sd.ArgDescDic["momentumWeight"] = new ArgDesc { Text = "动量因子权重", Explain = "动量因子子权重(0-100)" };
            sd.ArgDic["momentumWeight"] = 25.0;

            sd.ArgDescDic["trendWeight"] = new ArgDesc { Text = "趋势因子权重", Explain = "趋势因子子权重(0-100)" };
            sd.ArgDic["trendWeight"] = 25.0;

            sd.ArgDescDic["volatilityWeight"] = new ArgDesc { Text = "波动率因子权重", Explain = "波动率因子子权重(0-100)" };
            sd.ArgDic["volatilityWeight"] = 15.0;

            sd.ArgDescDic["volumeWeight"] = new ArgDesc { Text = "成交量因子权重", Explain = "成交量因子子权重(0-100)" };
            sd.ArgDic["volumeWeight"] = 15.0;

            sd.ArgDescDic["meanRevWeight"] = new ArgDesc { Text = "均值回归因子权重", Explain = "均值回归因子子权重(0-100)" };
            sd.ArgDic["meanRevWeight"] = 20.0;

            sd.ArgDescDic["quantWeight"] = new ArgDesc { Text = "量化因子总权重", Explain = "量化因子在综合得分中的权重(0-100)" };
            sd.ArgDic["quantWeight"] = 35.0;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["minConsistency"] = new ArgDesc { Text = "最少一致专家数", Explain = "至少N个专家同向才入场(1-3)" };
            sd.ArgDic["minConsistency"] = 3;

            sd.ArgDescDic["minObservations"] = new ArgDesc { Text = "最少观测数", Explain = "自适应表中某状态至少需要的观测次数" };
            sd.ArgDic["minObservations"] = 8;

            sd.ArgDescDic["minWinRate"] = new ArgDesc { Text = "最少胜率", Explain = "自适应表中某状态胜率超过此值才交易(0-1)" };
            sd.ArgDic["minWinRate"] = 0.80;

            sd.ArgDescDic["extremeThreshold"] = new ArgDesc { Text = "极端条件阈值", Explain = "7维极端条件至少满足N个才入场" };
            sd.ArgDic["extremeThreshold"] = 4;

            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["stopLoss"] = new ArgDesc { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDic["stopLoss"] = 5.0m;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "交易手数", Explain = "固定手数模式下的交易数量" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "交易金额", Explain = "固定金额模式下的交易金额" };
            sd.ArgDic["money"] = 10000m;

            // ==================== Polymarket参数 ====================
            AddPolyArgs(sd, 5m);

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-FinalScore"] = "#2196F3";
            sd.ColorDic["sub0-Threshold"] = "#9E9E9E";
            sd.ColorDic["sub1-StatScore"] = "#FF9800";
            sd.ColorDic["sub1-LSTMScore"] = "#9C27B0";
            sd.ColorDic["sub1-QuantScore"] = "#4CAF50";

            sd.MidValDic["sub0"] = 50;
            sd.MidValDic["sub1"] = 50;

            return sd;
        }

        #region Expert 1: 统计与概率模型

        private class StatisticalExpert
        {
            // Markov转移矩阵: key = 最近N根涨跌序列(如"UU","UD"), value = (upCount, totalCount)
            private Dictionary<string, (int up, int total)> _markov = new Dictionary<string, (int, int)>();
            private List<bool> _history = new List<bool>(); // true=涨, false=跌
            private int _markovOrder;

            // Bayesian: RSI区间(3) × Volume区间(2) × Trend区间(2) = 12格
            // key = "rsi_vol_trend", value = (upCount, totalCount)
            private Dictionary<string, (int up, int total)> _bayesian = new Dictionary<string, (int, int)>();

            public StatisticalExpert(int markovOrder)
            {
                _markovOrder = markovOrder;
            }

            /// <summary>
            /// 更新历史数据（当前bar相对于上一bar的涨跌）
            /// </summary>
            public void Update(bool isUp, double rsi, double volumeRatio, bool isUpTrend)
            {
                _history.Add(isUp);

                // 更新Markov转移矩阵
                if (_history.Count > _markovOrder)
                {
                    var stateKey = GetMarkovState(_history.Count - 1 - _markovOrder);
                    if (!_markov.ContainsKey(stateKey))
                        _markov[stateKey] = (0, 0);
                    var (up, total) = _markov[stateKey];
                    _markov[stateKey] = (isUp ? up + 1 : up, total + 1);
                }

                // 更新Bayesian
                string bayesKey = GetBayesianKey(rsi, volumeRatio, isUpTrend);
                if (!_bayesian.ContainsKey(bayesKey))
                    _bayesian[bayesKey] = (0, 0);
                var (bUp, bTotal) = _bayesian[bayesKey];
                _bayesian[bayesKey] = (isUp ? bUp + 1 : bUp, bTotal + 1);
            }

            /// <summary>
            /// 预测下一根bar上涨概率，返回 score (0-100)
            /// </summary>
            public (double score, double confidence) Predict(
                double rsi, double volumeRatio, bool isUpTrend,
                decimal open, decimal close, decimal high, decimal low,
                decimal prevOpen, decimal prevClose, decimal prevHigh, decimal prevLow)
            {
                double markovProb = 0.5;
                double markovConf = 0.0;
                double bayesProb = 0.5;
                double bayesConf = 0.0;
                double patternProb = 0.5;
                double patternConf = 0.0;

                // 1. Markov预测
                if (_history.Count >= _markovOrder)
                {
                    var stateKey = GetMarkovState(_history.Count - _markovOrder);
                    if (_markov.TryGetValue(stateKey, out var mc) && mc.total >= 5)
                    {
                        markovProb = (double)mc.up / mc.total;
                        markovConf = Math.Min(1.0, mc.total / 50.0);
                    }
                }

                // 2. Bayesian预测
                string bayesKey = GetBayesianKey(rsi, volumeRatio, isUpTrend);
                if (_bayesian.TryGetValue(bayesKey, out var bc) && bc.total >= 3)
                {
                    bayesProb = (double)bc.up / bc.total;
                    bayesConf = Math.Min(1.0, bc.total / 30.0);
                }

                // 3. K线形态识别
                var (pProb, pConf) = DetectPattern(open, close, high, low, prevOpen, prevClose, prevHigh, prevLow);
                patternProb = pProb;
                patternConf = pConf;

                // 加权合成
                double totalWeight = 0.4 + 0.35 + 0.25;
                double weightedProb = (markovProb * 0.4 + bayesProb * 0.35 + patternProb * 0.25) / totalWeight;
                double avgConf = (markovConf * 0.4 + bayesConf * 0.35 + patternConf * 0.25) / totalWeight;

                double score = weightedProb * 100.0;
                return (score, avgConf);
            }

            private string GetMarkovState(int startIdx)
            {
                var chars = new char[_markovOrder];
                for (int i = 0; i < _markovOrder; i++)
                    chars[i] = _history[startIdx + i] ? 'U' : 'D';
                return new string(chars);
            }

            private string GetBayesianKey(double rsi, double volumeRatio, bool isUpTrend)
            {
                // RSI: 0-30=0(超卖), 30-70=1(中性), 70-100=2(超买)
                int rsiZone = rsi < 30 ? 0 : (rsi > 70 ? 2 : 1);
                // Volume: <1=0(缩量), >=1=1(放量)
                int volZone = volumeRatio < 1.0 ? 0 : 1;
                // Trend: 0=下, 1=上
                int trendZone = isUpTrend ? 1 : 0;
                return $"{rsiZone}_{volZone}_{trendZone}";
            }

            /// <summary>
            /// K线形态识别
            /// </summary>
            private (double prob, double conf) DetectPattern(
                decimal open, decimal close, decimal high, decimal low,
                decimal prevOpen, decimal prevClose, decimal prevHigh, decimal prevLow)
            {
                if (high == low || prevHigh == prevLow) return (0.5, 0.0);

                decimal body = Math.Abs(close - open);
                decimal range = high - low;
                decimal prevBody = Math.Abs(prevClose - prevOpen);
                decimal prevRange = prevHigh - prevLow;
                bool isBullish = close > open;
                bool prevIsBullish = prevClose > prevOpen;

                double prob = 0.5;
                double conf = 0.0;

                // 锤子线 (Hammer) - 看涨
                decimal lowerShadow = Math.Min(open, close) - low;
                decimal upperShadow = high - Math.Max(open, close);
                if (lowerShadow > body * 2 && upperShadow < body * 0.5m && !prevIsBullish)
                {
                    prob = 0.65;
                    conf = 0.6;
                }

                // 射击之星 (Shooting Star) - 看跌
                if (upperShadow > body * 2 && lowerShadow < body * 0.5m && prevIsBullish)
                {
                    prob = 0.35;
                    conf = 0.6;
                }

                // 看涨吞没 (Bullish Engulfing)
                if (isBullish && !prevIsBullish && body > prevBody && close > prevOpen && open < prevClose)
                {
                    prob = 0.68;
                    conf = 0.7;
                }

                // 看跌吞没 (Bearish Engulfing)
                if (!isBullish && prevIsBullish && body > prevBody && open > prevClose && close < prevOpen)
                {
                    prob = 0.32;
                    conf = 0.7;
                }

                // 十字星 (Doji) - 反转信号
                if (body < range * 0.1m)
                {
                    // 上涨趋势中的十字星看跌，下跌趋势中的十字星看涨
                    if (prevIsBullish)
                    {
                        prob = 0.42;
                        conf = 0.4;
                    }
                    else
                    {
                        prob = 0.58;
                        conf = 0.4;
                    }
                }

                return (prob, conf);
            }
        }

        #endregion

        #region Expert 2: LSTM模型

        private class LSTMCell
        {
            private int _inputSize;
            private int _hiddenSize;
            private Random _random;

            private double[,] _Wf = null!, _Wi = null!, _Wc = null!, _Wo = null!;
            private double[] _Uf = null!, _Ui = null!, _Uc = null!, _Uo = null!;
            private double[] _bf = null!, _bi = null!, _bc = null!, _bo = null!;
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
                for (int i = 0; i < _hiddenSize; i++) _bf[i] = 1.0;
                ResetGradients();
            }

            private double[,] InitMatrix(int rows, int cols, double scale)
            {
                var m = new double[rows, cols];
                for (int i = 0; i < rows; i++)
                    for (int j = 0; j < cols; j++)
                        m[i, j] = (_random.NextDouble() * 2 - 1) * scale;
                return m;
            }

            private double[] InitVector(int size, double scale)
            {
                var v = new double[size];
                for (int i = 0; i < size; i++)
                    v[i] = (_random.NextDouble() * 2 - 1) * scale;
                return v;
            }

            public void ResetGradients()
            {
                _dWf = new double[_hiddenSize, _inputSize]; _dWi = new double[_hiddenSize, _inputSize];
                _dWc = new double[_hiddenSize, _inputSize]; _dWo = new double[_hiddenSize, _inputSize];
                _dUf = new double[_hiddenSize]; _dUi = new double[_hiddenSize];
                _dUc = new double[_hiddenSize]; _dUo = new double[_hiddenSize];
                _dbf = new double[_hiddenSize]; _dbi = new double[_hiddenSize];
                _dbc = new double[_hiddenSize]; _dbo = new double[_hiddenSize];
            }

            public (double[] h, double[] c, LSTMCache cache) Forward(double[] x, double[] hPrev, double[] cPrev)
            {
                var cache = new LSTMCache { x = x, hPrev = hPrev, cPrev = cPrev };
                cache.f = new double[_hiddenSize];
                cache.i = new double[_hiddenSize];
                cache.cTilde = new double[_hiddenSize];
                cache.o = new double[_hiddenSize];

                for (int k = 0; k < _hiddenSize; k++)
                {
                    double sf = _bf[k] + _Uf[k] * hPrev[k];
                    double si = _bi[k] + _Ui[k] * hPrev[k];
                    double sc = _bc[k] + _Uc[k] * hPrev[k];
                    double so = _bo[k] + _Uo[k] * hPrev[k];
                    for (int j = 0; j < _inputSize; j++)
                    {
                        sf += _Wf[k, j] * x[j];
                        si += _Wi[k, j] * x[j];
                        sc += _Wc[k, j] * x[j];
                        so += _Wo[k, j] * x[j];
                    }
                    cache.f[k] = Sigmoid(sf);
                    cache.i[k] = Sigmoid(si);
                    cache.cTilde[k] = Tanh(sc);
                    cache.o[k] = Sigmoid(so);
                }

                var c = new double[_hiddenSize];
                var h = new double[_hiddenSize];
                cache.tanhC = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                {
                    c[k] = cache.f[k] * cPrev[k] + cache.i[k] * cache.cTilde[k];
                    cache.tanhC[k] = Tanh(c[k]);
                    h[k] = cache.o[k] * cache.tanhC[k];
                }
                cache.c = c;
                return (h, c, cache);
            }

            public (double[] dhPrev, double[] dcPrev) Backward(double[] dh, double[] dcNext, LSTMCache cache)
            {
                var do_ = new double[_hiddenSize];
                var dc = new double[_hiddenSize];
                var df = new double[_hiddenSize];
                var di = new double[_hiddenSize];
                var dcTilde = new double[_hiddenSize];

                for (int k = 0; k < _hiddenSize; k++)
                {
                    do_[k] = dh[k] * cache.tanhC[k] * SigDeriv(cache.o[k]);
                    dc[k] = dh[k] * cache.o[k] * TanhDeriv(cache.tanhC[k]) + dcNext[k];
                    df[k] = dc[k] * cache.cPrev[k] * SigDeriv(cache.f[k]);
                    di[k] = dc[k] * cache.cTilde[k] * SigDeriv(cache.i[k]);
                    dcTilde[k] = dc[k] * cache.i[k] * TanhDeriv(cache.cTilde[k]);
                }

                for (int k = 0; k < _hiddenSize; k++)
                {
                    for (int j = 0; j < _inputSize; j++)
                    {
                        _dWf[k, j] += df[k] * cache.x[j];
                        _dWi[k, j] += di[k] * cache.x[j];
                        _dWc[k, j] += dcTilde[k] * cache.x[j];
                        _dWo[k, j] += do_[k] * cache.x[j];
                    }
                    _dUf[k] += df[k] * cache.hPrev[k]; _dUi[k] += di[k] * cache.hPrev[k];
                    _dUc[k] += dcTilde[k] * cache.hPrev[k]; _dUo[k] += do_[k] * cache.hPrev[k];
                    _dbf[k] += df[k]; _dbi[k] += di[k]; _dbc[k] += dcTilde[k]; _dbo[k] += do_[k];
                }

                var dhPrev = new double[_hiddenSize];
                var dcPrev = new double[_hiddenSize];
                for (int k = 0; k < _hiddenSize; k++)
                {
                    dhPrev[k] = df[k] * _Uf[k] + di[k] * _Ui[k] + dcTilde[k] * _Uc[k] + do_[k] * _Uo[k];
                    dcPrev[k] = dc[k] * cache.f[k];
                }
                return (dhPrev, dcPrev);
            }

            public void UpdateWeights(double lr, int batchSize)
            {
                double s = lr / batchSize;
                double clip = 5.0;
                for (int i = 0; i < _hiddenSize; i++)
                {
                    for (int j = 0; j < _inputSize; j++)
                    {
                        _Wf[i, j] -= s * Clip(_dWf[i, j], clip);
                        _Wi[i, j] -= s * Clip(_dWi[i, j], clip);
                        _Wc[i, j] -= s * Clip(_dWc[i, j], clip);
                        _Wo[i, j] -= s * Clip(_dWo[i, j], clip);
                    }
                    _Uf[i] -= s * Clip(_dUf[i], clip); _Ui[i] -= s * Clip(_dUi[i], clip);
                    _Uc[i] -= s * Clip(_dUc[i], clip); _Uo[i] -= s * Clip(_dUo[i], clip);
                    _bf[i] -= s * Clip(_dbf[i], clip); _bi[i] -= s * Clip(_dbi[i], clip);
                    _bc[i] -= s * Clip(_dbc[i], clip); _bo[i] -= s * Clip(_dbo[i], clip);
                }
                ResetGradients();
            }

            private static double Clip(double v, double c) => Math.Max(-c, Math.Min(c, v));
            private static double Sigmoid(double x) { if (x > 20) return 1; if (x < -20) return 0; return 1.0 / (1.0 + Math.Exp(-x)); }
            private static double Tanh(double x) { if (x > 20) return 1; if (x < -20) return -1; return Math.Tanh(x); }
            private static double SigDeriv(double s) => s * (1 - s);
            private static double TanhDeriv(double t) => 1 - t * t;
        }

        private class LSTMCache
        {
            public double[] x = Array.Empty<double>();
            public double[] hPrev = Array.Empty<double>();
            public double[] cPrev = Array.Empty<double>();
            public double[] f = Array.Empty<double>();
            public double[] i = Array.Empty<double>();
            public double[] cTilde = Array.Empty<double>();
            public double[] o = Array.Empty<double>();
            public double[] c = Array.Empty<double>();
            public double[] tanhC = Array.Empty<double>();
        }

        private class LSTMModel
        {
            private LSTMCell _cell;
            private int _inputSize, _hiddenSize;
            private double _lr;
            private int _epochs;
            private Random _rng;
            private double[] _Wy;
            private double _by;
            private double _dWySum, _dbySum;
            private double[] _fMean, _fStd;
            private double _tMean, _tStd;
            public bool IsTrained { get; private set; }
            public double LastLoss { get; private set; }

            public LSTMModel(int inputSize, int hiddenSize, double lr, int epochs)
            {
                _inputSize = inputSize; _hiddenSize = hiddenSize; _lr = lr; _epochs = epochs;
                _rng = new Random(42);
                _cell = new LSTMCell(inputSize, hiddenSize, _rng);
                double sc = Math.Sqrt(2.0 / hiddenSize);
                _Wy = new double[hiddenSize];
                for (int i = 0; i < hiddenSize; i++) _Wy[i] = (_rng.NextDouble() * 2 - 1) * sc;
                _by = 0;
                _fMean = new double[inputSize]; _fStd = new double[inputSize];
                for (int i = 0; i < inputSize; i++) _fStd[i] = 1.0;
                _tStd = 1.0;
            }

            public void Train(double[][][] seqs, double[] targets)
            {
                if (seqs.Length == 0) return;
                NormalizeData(seqs, targets);
                int seqLen = seqs[0].Length;
                for (int epoch = 0; epoch < _epochs; epoch++)
                {
                    double totalLoss = 0;
                    _cell.ResetGradients(); _dWySum = 0; _dbySum = 0;
                    var idx = Enumerable.Range(0, seqs.Length).OrderBy(_ => _rng.Next()).ToArray();
                    foreach (int ii in idx)
                    {
                        double target = (targets[ii] - _tMean) / (_tStd + 1e-8);
                        var h = new double[_hiddenSize]; var c = new double[_hiddenSize];
                        var caches = new List<LSTMCache>();
                        for (int t = 0; t < seqLen; t++)
                        {
                            var nx = Normalize(seqs[ii][t]);
                            var (hN, cN, cache) = _cell.Forward(nx, h, c);
                            h = hN; c = cN; caches.Add(cache);
                        }
                        double output = _by;
                        for (int i = 0; i < _hiddenSize; i++) output += _Wy[i] * h[i];
                        double err = output - target;
                        totalLoss += err * err;
                        var dh = new double[_hiddenSize];
                        for (int i = 0; i < _hiddenSize; i++) { dh[i] = 2 * err * _Wy[i]; _dWySum += err * h[i]; }
                        _dbySum += err;
                        var dc = new double[_hiddenSize];
                        for (int t = seqLen - 1; t >= 0; t--)
                        {
                            var (dhP, dcP) = _cell.Backward(dh, dc, caches[t]);
                            dh = dhP; dc = dcP;
                        }
                    }
                    _cell.UpdateWeights(_lr, seqs.Length);
                    double s = _lr / seqs.Length; double clip = 5.0;
                    for (int i = 0; i < _hiddenSize; i++)
                        _Wy[i] -= s * Math.Max(-clip, Math.Min(clip, _dWySum * _Wy[i] / _hiddenSize));
                    _by -= s * Math.Max(-clip, Math.Min(clip, _dbySum));
                    LastLoss = totalLoss / seqs.Length;
                    if (LastLoss < 1e-6) break;
                }
                IsTrained = true;
            }

            public double Predict(double[][] seq)
            {
                if (!IsTrained || seq.Length == 0) return 0;
                var h = new double[_hiddenSize]; var c = new double[_hiddenSize];
                foreach (var f in seq)
                {
                    var (hN, cN, _) = _cell.Forward(Normalize(f), h, c);
                    h = hN; c = cN;
                }
                double output = _by;
                for (int i = 0; i < _hiddenSize; i++) output += _Wy[i] * h[i];
                return output * _tStd + _tMean;
            }

            public double GetConfidence(double[][] seq)
            {
                if (!IsTrained || seq.Length == 0) return 0;
                var h = new double[_hiddenSize]; var c = new double[_hiddenSize];
                foreach (var f in seq)
                {
                    var (hN, cN, _) = _cell.Forward(Normalize(f), h, c);
                    h = hN; c = cN;
                }
                return Math.Min(1.0, h.Average(x => Math.Abs(x)));
            }

            private void NormalizeData(double[][][] seqs, double[] targets)
            {
                var all = new List<double[]>();
                foreach (var s in seqs) foreach (var f in s) all.Add(f);
                for (int fi = 0; fi < _inputSize; fi++)
                {
                    var vals = all.Select(x => x[fi]).ToArray();
                    _fMean[fi] = vals.Average();
                    double var_ = vals.Average(v => Math.Pow(v - _fMean[fi], 2));
                    _fStd[fi] = Math.Sqrt(var_);
                    if (_fStd[fi] < 1e-8) _fStd[fi] = 1.0;
                }
                _tMean = targets.Average();
                double tVar = targets.Average(v => Math.Pow(v - _tMean, 2));
                _tStd = Math.Sqrt(tVar);
                if (_tStd < 1e-8) _tStd = 1.0;
            }

            private double[] Normalize(double[] f)
            {
                var n = new double[f.Length];
                for (int i = 0; i < f.Length; i++) n[i] = (f[i] - _fMean[i]) / (_fStd[i] + 1e-8);
                return n;
            }
        }

        private static class FeatureExtractor
        {
            public static double[] ExtractFeatures(SkQuote cur, SkQuote? prev, double ma5, double ma10, double ma20)
            {
                var f = new List<double>();
                f.Add(prev != null && prev.Close != 0 ? (double)((cur.Close - prev.Close) / prev.Close) : 0);
                f.Add(prev != null && prev.Volume != 0 ? (double)((cur.Volume - prev.Volume) / prev.Volume) : 0);
                f.Add(cur.Low != 0 ? (double)((cur.High - cur.Low) / cur.Low) : 0);
                f.Add(cur.High != cur.Low ? (double)((cur.Close - cur.Low) / (cur.High - cur.Low)) : 0.5);
                double cl = (double)cur.Close;
                f.Add(ma5 != 0 ? (cl - ma5) / ma5 : 0);
                f.Add(ma10 != 0 ? (cl - ma10) / ma10 : 0);
                f.Add(ma20 != 0 ? (cl - ma20) / ma20 : 0);
                f.Add(cur.Open != 0 ? (double)((cur.Close - cur.Open) / cur.Open) : 0);
                return f.ToArray();
            }

            public static double[][] ExtractSequence(List<SkQuote> quotes, int lookback)
            {
                if (quotes.Count < lookback + 20) return Array.Empty<double[]>();
                var seq = new List<double[]>();
                int start = quotes.Count - lookback;
                for (int i = start; i < quotes.Count; i++)
                {
                    var prev = i > 0 ? quotes[i - 1] : null;
                    int s5 = Math.Max(0, i - 4), s10 = Math.Max(0, i - 9), s20 = Math.Max(0, i - 19);
                    double ma5 = quotes.Skip(s5).Take(i - s5 + 1).Average(q => (double)q.Close);
                    double ma10 = quotes.Skip(s10).Take(i - s10 + 1).Average(q => (double)q.Close);
                    double ma20 = quotes.Skip(s20).Take(i - s20 + 1).Average(q => (double)q.Close);
                    seq.Add(ExtractFeatures(quotes[i], prev, ma5, ma10, ma20));
                }
                return seq.ToArray();
            }
        }

        #endregion

        #region Expert 3: 量化因子模型

        private static class QuantFactorExpert
        {
            public static (double score, int bullishCount, int bearishCount) Calculate(
                List<SkQuote> quotes,
                double momentumW, double trendW, double volatilityW, double volumeW, double meanRevW)
            {
                var q = quotes.Last();
                decimal price = q.Close;

                // 指标计算
                var rsiList = quotes.GetRsi(14).ToList();
                double rsi = rsiList.Last().Rsi.GetValueOrDefault(50);

                var rocList = quotes.GetRoc(12).ToList();
                double roc = rocList.Last().Roc.GetValueOrDefault(0);

                var emaFastList = quotes.GetEma(12).ToList();
                var emaSlowList = quotes.GetEma(26).ToList();
                double emaFast = emaFastList.Last().Ema.GetValueOrDefault(0);
                double emaSlow = emaSlowList.Last().Ema.GetValueOrDefault(0);

                var adxList = quotes.GetAdx(14).ToList();
                var adxLast = adxList.Last();
                double adx = adxLast.Adx.GetValueOrDefault(0);
                double pdi = adxLast.Pdi.GetValueOrDefault(0);
                double mdi = adxLast.Mdi.GetValueOrDefault(0);

                var bollList = quotes.GetBollingerBands(20, 2).ToList();
                var bollLast = bollList.Last();
                double bollUp = bollLast.UpperBand.GetValueOrDefault(0);
                double bollLo = bollLast.LowerBand.GetValueOrDefault(0);
                double bollMid = bollLast.Sma.GetValueOrDefault(0);
                double bollW = bollUp - bollLo;

                var obvList = quotes.GetObv().ToList();
                double obv = obvList.Last().Obv;
                double obvMa = 0;
                if (obvList.Count >= 20)
                {
                    for (int i = 0; i < 20; i++) obvMa += obvList[obvList.Count - 1 - i].Obv;
                    obvMa /= 20;
                }

                double volRatio = 1.0;
                if (quotes.Count >= 20)
                {
                    decimal avgVol = 0;
                    for (int i = 0; i < 20; i++) avgVol += quotes[quotes.Count - 1 - i].Volume;
                    avgVol /= 20;
                    if (avgVol > 0) volRatio = (double)(q.Volume / avgVol);
                }

                var smaList = quotes.GetSma(20).ToList();
                double sma = smaList.Last().Sma.GetValueOrDefault((double)price);
                double deviation = sma > 0 ? ((double)price - sma) / sma * 100 : 0;

                // 1. 动量因子
                double rocScore = Math.Max(0, Math.Min(100, (roc + 10) / 20 * 100));
                double momentum = rocScore * 0.5 + rsi * 0.5;

                // 2. 趋势因子
                double emaDiff = emaSlow > 0 ? (emaFast - emaSlow) / emaSlow * 100 : 0;
                double emaScore = Math.Max(0, Math.Min(100, (emaDiff + 5) / 10 * 100));
                double adxDir = 50;
                if (adx > 20) adxDir = pdi > mdi ? 50 + Math.Min(50, adx) : 50 - Math.Min(50, adx);
                double trend = emaScore * 0.5 + adxDir * 0.5;

                // 3. 波动率因子
                double volRatioVal = bollMid > 0 ? bollW / bollMid * 100 : 5;
                double volScore = Math.Max(0, Math.Min(100, (10 - volRatioVal) / 9 * 100));
                double bollPos = bollW > 0 ? ((double)price - bollLo) / bollW * 100 : 50;
                double volatility = volScore * 0.3 + bollPos * 0.7;

                // 4. 成交量因子
                double obvScore = obvMa > 0 ? (obv > obvMa ? 70 : 30) : 50;
                double vrScore = Math.Max(0, Math.Min(100, volRatio * 50));
                double volume = obvScore * 0.6 + vrScore * 0.4;

                // 5. 均值回归因子
                double meanRev = Math.Max(0, Math.Min(100, (5 - deviation) / 10 * 100));

                // 加权合成
                double totalW = momentumW + trendW + volatilityW + volumeW + meanRevW;
                double composite = totalW > 0
                    ? (momentum * momentumW + trend * trendW + volatility * volatilityW + volume * volumeW + meanRev * meanRevW) / totalW
                    : 50;

                int bullish = (momentum > 50 ? 1 : 0) + (trend > 50 ? 1 : 0) + (volatility > 50 ? 1 : 0) + (volume > 50 ? 1 : 0) + (meanRev > 50 ? 1 : 0);
                int bearish = (momentum < 50 ? 1 : 0) + (trend < 50 ? 1 : 0) + (volatility < 50 ? 1 : 0) + (volume < 50 ? 1 : 0) + (meanRev < 50 ? 1 : 0);

                return (composite, bullish, bearish);
            }
        }

        #endregion

        #region 自适应条件概率表

        private class AdaptiveTable
        {
            // 滑动窗口: 存储最近N条记录 (stateKeys[], wasUp)
            private List<(string[] keys, bool wasUp)> _window = new List<(string[], bool)>();
            private int _windowSize;

            public AdaptiveTable(int windowSize = 500)
            {
                _windowSize = windowSize;
            }

            /// <summary>
            /// 更新: 记录上一个状态的所有层级key及结果
            /// </summary>
            public void Update(string[] stateKeys, bool nextBarUp)
            {
                if (stateKeys == null || stateKeys.Length == 0) return;
                _window.Add((stateKeys, nextBarUp));
                if (_window.Count > _windowSize)
                    _window.RemoveAt(0);
            }

            /// <summary>
            /// 用多层级key查询滑动窗口，从最具体到最泛化
            /// </summary>
            public (double upProb, int observations, int level) QueryHierarchical(
                string[] keys, int minObs)
            {
                for (int level = 0; level < keys.Length; level++)
                {
                    string targetKey = keys[level];
                    int up = 0, total = 0;
                    foreach (var (storedKeys, wasUp) in _window)
                    {
                        if (level < storedKeys.Length && storedKeys[level] == targetKey)
                        {
                            total++;
                            if (wasUp) up++;
                        }
                    }
                    if (total >= minObs)
                        return (total > 0 ? (double)up / total : 0.5, total, level);
                }
                return (0.5, 0, keys.Length);
            }
        }

        #endregion

        #region 状态管理

        private class MoEState
        {
            public int Status { get; set; }          // 0=空仓 1=多头 2=空头
            public decimal Num { get; set; }          // 持仓数量
            public decimal EntryPrice { get; set; }   // 入场价格
            public int BarCount { get; set; }         // K线计数
            public StatisticalExpert? StatExpert { get; set; }
            public LSTMModel? LstmModel { get; set; }
            public int LastTrainBar { get; set; }
            public AdaptiveTable AdapTable { get; set; } = new AdaptiveTable(800);
            public string[] PrevStateKeys { get; set; } = Array.Empty<string>();
            public decimal PrevClose { get; set; }
            public string PrevPatternKey { get; set; } = ""; // 上一bar的条件模式key
            public int PrevDirection { get; set; } // 1=预测涨, -1=预测跌, 0=无信号
            // 滚动准确率追踪
            public Queue<bool> RecentResults { get; set; } = new Queue<bool>();
            public int RollingWindowSize { get; set; } = 30;
        }

        private Dictionary<string, MoEState> _stateDic = new Dictionary<string, MoEState>();

        #endregion

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (ArgDic == null || tu.QuoteList == null || tu.QuoteList.Count == 0) return;

            EnsurePolymarketInitialized(period);

            if (_polyService != null)
                RefreshEventIfNeeded(tq.Date, period);

            // 每个 tick 尝试 Polymarket 下单（内部判断时间和价格条件）
            if (!IsBacktest && !_polyOrderPlaced
                && _stateDic.TryGetValue(tu.GetStateKey(), out var existing) && existing.Status != 0)
            {
                var polyNum = Convert.ToDecimal(ArgDic["polyNum"]);
                if (polyNum > 0 && TryPlacePolymarketOrder(existing.Status == 1, polyNum, tq))
                    _polyOrderPlaced = true;
            }

            if (!isFinal) return;

            // ==================== 读参数 ====================
            int markovOrder = Convert.ToInt32(ArgDic["markovOrder"]);
            double statWeight = Convert.ToDouble(ArgDic["statWeight"]);

            int lookback = Convert.ToInt32(ArgDic["lookback"]);
            int hiddenSize = Convert.ToInt32(ArgDic["hiddenSize"]);
            double lr = Convert.ToDouble(ArgDic["learningRate"]);
            int epochs = Convert.ToInt32(ArgDic["epochs"]);
            int trainPeriod = Convert.ToInt32(ArgDic["trainPeriod"]);
            int retrainInterval = Convert.ToInt32(ArgDic["retrainInterval"]);
            double lstmWeight = Convert.ToDouble(ArgDic["lstmWeight"]);

            double momentumW = Convert.ToDouble(ArgDic["momentumWeight"]);
            double trendW = Convert.ToDouble(ArgDic["trendWeight"]);
            double volatilityW = Convert.ToDouble(ArgDic["volatilityWeight"]);
            double volumeW = Convert.ToDouble(ArgDic["volumeWeight"]);
            double meanRevW = Convert.ToDouble(ArgDic["meanRevWeight"]);
            double quantWeight = Convert.ToDouble(ArgDic["quantWeight"]);

            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            int extremeThreshold = Convert.ToInt32(ArgDic["extremeThreshold"]);

            // 最少需要的K线数
            int minBars = Math.Max(lookback + trainPeriod + 25, 50);
            if (tu.QuoteList.Count < minBars) return;

            var quotes = tu.QuoteList;
            var q = quotes.Last();
            decimal price = q.Close;
            var sk = tu.GetStateKey();

            // ==================== 获取/创建状态 ====================
            if (!_stateDic.TryGetValue(sk, out MoEState? s) || s == null)
            {
                s = new MoEState { StatExpert = new StatisticalExpert(markovOrder) };
                _stateDic[sk] = s;
            }
            s.BarCount++;

            // ==================== 计算辅助指标 ====================
            var rsiList = quotes.GetRsi(14).ToList();
            double rsi = rsiList.Last().Rsi.GetValueOrDefault(50);

            double volumeRatio = 1.0;
            if (quotes.Count >= 20)
            {
                decimal avgVol = 0;
                for (int i = 0; i < 20; i++) avgVol += quotes[quotes.Count - 1 - i].Volume;
                avgVol /= 20;
                if (avgVol > 0) volumeRatio = (double)(q.Volume / avgVol);
            }

            var emaList = quotes.GetEma(20).ToList();
            double ema20 = emaList.Last().Ema.GetValueOrDefault((double)price);
            bool isUpTrend = (double)price > ema20;

            // 连续K线方向检测
            int consecUp = 0, consecDown = 0;
            for (int i = quotes.Count - 1; i >= 1 && i >= quotes.Count - 10; i--)
            {
                if (quotes[i].Close > quotes[i - 1].Close)
                {
                    if (consecDown > 0) break;
                    consecUp++;
                }
                else if (quotes[i].Close < quotes[i - 1].Close)
                {
                    if (consecUp > 0) break;
                    consecDown++;
                }
                else break;
            }

            // 当前bar方向
            bool curBarUp = quotes.Count >= 2 && q.Close > quotes[quotes.Count - 2].Close;

            // 更新统计专家（记录当前bar的涨跌）
            if (quotes.Count >= 2)
            {
                var prevQ = quotes[quotes.Count - 2];
                bool isUp = q.Close > prevQ.Close;
                s.StatExpert!.Update(isUp, rsi, volumeRatio, isUpTrend);
            }

            s.PrevClose = price; // 记录当前bar收盘价

            // ==================== Expert 1: 统计模型预测 ====================
            double statScore = 50;
            if (quotes.Count >= 3)
            {
                var prevQ = quotes[quotes.Count - 2];
                var (ss, _) = s.StatExpert!.Predict(
                    rsi, volumeRatio, isUpTrend,
                    q.Open, q.Close, q.High, q.Low,
                    prevQ.Open, prevQ.Close, prevQ.High, prevQ.Low);
                statScore = ss;
            }

            // ==================== Expert 2: LSTM预测 ====================
            int inputSize = 8;
            if (s.LstmModel == null)
                s.LstmModel = new LSTMModel(inputSize, hiddenSize, lr, epochs);

            if (!s.LstmModel.IsTrained || (s.BarCount - s.LastTrainBar >= retrainInterval))
            {
                var (seqs, targets) = PrepareTrainingData(quotes, lookback, trainPeriod);
                if (seqs.Length > 0)
                {
                    s.LstmModel.Train(seqs, targets);
                    s.LastTrainBar = s.BarCount;
                }
            }

            double lstmScore = 50;
            if (s.LstmModel.IsTrained)
            {
                var seq = FeatureExtractor.ExtractSequence(quotes, lookback);
                if (seq.Length > 0)
                {
                    double pred = s.LstmModel.Predict(seq);
                    lstmScore = Math.Max(0, Math.Min(100, (pred + 0.02) / 0.04 * 100));
                }
            }

            // ==================== Expert 3: 量化因子预测 ====================
            var (quantScore, bullishCount, bearishCount) = QuantFactorExpert.Calculate(
                quotes, momentumW, trendW, volatilityW, volumeW, meanRevW);

            // ==================== 加权投票 ====================
            double totalWeight = statWeight + lstmWeight + quantWeight;
            double finalScore = totalWeight > 0
                ? (statScore * statWeight + lstmScore * lstmWeight + quantScore * quantWeight) / totalWeight
                : 50;

            // ==================== 高置信度信号 + 质量过滤 ====================
            // 第1层: 7维极端条件 (基础信号)
            // 第2层: 专家共识 + 自适应表 (质量过滤)

            // ===== 7维独立极端条件 =====
            bool c1Buy = rsi < 30, c1Sell = rsi > 70;
            var stochList = quotes.GetStoch(14, 3, 3).ToList();
            double stochK = stochList.Last().K.GetValueOrDefault(50);
            bool c2Buy = stochK < 20, c2Sell = stochK > 80;
            var macdList = quotes.GetMacd(12, 26, 9).ToList();
            double macdHist = macdList.Last().Histogram.GetValueOrDefault(0);
            double macdHistPrev = macdList.Count >= 2 ? macdList[macdList.Count - 2].Histogram.GetValueOrDefault(0) : 0;
            bool c3Buy = macdHist > macdHistPrev && macdHist < 0;
            bool c3Sell = macdHist < macdHistPrev && macdHist > 0;
            bool c4Buy = consecDown >= 3, c4Sell = consecUp >= 3;
            decimal range = q.High - q.Low;
            decimal lowerWick = Math.Min(q.Open, q.Close) - q.Low;
            decimal upperWick = q.High - Math.Max(q.Open, q.Close);
            bool c5Buy = range > 0 && lowerWick > range * 0.6m;
            bool c5Sell = range > 0 && upperWick > range * 0.6m;
            bool c6Buy = volumeRatio > 1.5 && !curBarUp;
            bool c6Sell = volumeRatio > 1.5 && curBarUp;
            var bollShort = quotes.GetBollingerBands(10, 2).ToList();
            double bsLower = bollShort.Last().LowerBand.GetValueOrDefault(0);
            double bsUpper = bollShort.Last().UpperBand.GetValueOrDefault(0);
            bool c7Buy = (bsUpper - bsLower) > 0 && (double)price < bsLower;
            bool c7Sell = (bsUpper - bsLower) > 0 && (double)price > bsUpper;

            int buyExt = (c1Buy?1:0)+(c2Buy?1:0)+(c3Buy?1:0)+(c4Buy?1:0)+(c5Buy?1:0)+(c6Buy?1:0)+(c7Buy?1:0);
            int sellExt = (c1Sell?1:0)+(c2Sell?1:0)+(c3Sell?1:0)+(c4Sell?1:0)+(c5Sell?1:0)+(c6Sell?1:0)+(c7Sell?1:0);

            bool predictUp = false;
            bool predictDown = false;

            // ===== 4+/7条件 =====
            if (buyExt >= extremeThreshold)
                predictUp = true;
            else if (sellExt >= extremeThreshold)
                predictDown = true;

            // mode过滤
            if (mode == 1) predictDown = false;
            if (mode == 2) predictUp = false;

            // ==================== 绘图 ====================
            Plot("sub0", "FinalScore", PlotType.LINE, finalScore);
            Plot("sub0", "Threshold", PlotType.LINE, 50);
            Plot("sub1", "StatScore", PlotType.LINE, statScore);
            Plot("sub1", "LSTMScore", PlotType.LINE, lstmScore);
            Plot("sub1", "QuantScore", PlotType.LINE, quantScore);

            // ==================== 计算手数 ====================
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var sym = GetSymbol(tu.MktSymbol);
                num = (Convert.ToDecimal(ArgDic["money"]) / (price * sym.multiplier * sym.margin_ratio));
                if (sym.symbol_type == (int)SymbolType.COIN)
                    num = Math.Floor(num * 1000) / 1000m;
                else
                    num = Math.Floor(num);
            }
            if (num <= 0) return;

            // ==================== 每bar开平仓交易逻辑 ====================
            // 止损检查
            var _sl = Convert.ToDecimal(ArgDic["stopLoss"]);
            if (s.Status == 1 && _sl > 0 && s.EntryPrice > 0 && price < s.EntryPrice * (1 - _sl / 100m))
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                return;
            }
            if (s.Status == 2 && _sl > 0 && s.EntryPrice > 0 && price > s.EntryPrice * (1 + _sl / 100m))
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                return;
            }

            // Step 1: 平掉上一bar的仓位
            if (s.Status == 1)
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0;
                s.Num = 0;
                s.EntryPrice = 0;
            }
            else if (s.Status == 2)
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0;
                s.Num = 0;
                s.EntryPrice = 0;
            }

            // Step 2: 按预测方向开新仓
            if (predictUp)
            {
                Trade(tu.MktSymbol, OrderType.BUY, price, num, period, sendMode);
                s.Status = 1;
                s.Num = num;
                s.EntryPrice = price;
            }
            else if (predictDown)
            {
                Trade(tu.MktSymbol, OrderType.SELL, price, num, period, sendMode);
                s.Status = 2;
                s.Num = num;
                s.EntryPrice = price;
            }
        }

        /// <summary>
        /// 准备LSTM训练数据
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

                double target = (double)((quotes[i + 1].Close - quotes[i].Close) / quotes[i].Close);
                sequences.Add(sequence);
                targets.Add(target);
            }

            return (sequences.ToArray(), targets.ToArray());
        }
    }
}
