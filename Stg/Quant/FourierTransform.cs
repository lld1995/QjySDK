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
	/// 傅里叶变换交易策略
	/// 
	/// 核心原理：
	/// 1. 对价格序列进行去趋势处理，提取周期性波动
	/// 2. 应用汉宁窗减少频谱泄漏
	/// 3. 执行FFT分析，识别主导周期
	/// 4. 基于主导周期的相位预测价格拐点
	/// 5. 结合功率谱强度过滤弱信号
	/// 
	/// 信号生成：
	/// - 当主导周期相位接近谷底且功率足够强时，产生买入信号
	/// - 当主导周期相位接近峰顶且功率足够强时，产生卖出信号
	/// </summary>
	public class FourierTransform : StgBase
	{
		/// <summary>
		/// 策略状态
		/// </summary>
		private class State
		{
			public int Position { get; set; }              // 0:无持仓 1:多仓 -1:空仓
			public decimal Num { get; set; }               // 持仓数量
			public decimal EntryPrice { get; set; }        // 入场价格
			public double PrevPhase { get; set; }          // 上一根K线的主导相位
			public double PrevPower { get; set; }          // 上一根K线的功率
			public int DominantPeriod { get; set; }        // 主导周期
			public List<double> PhaseHistory { get; set; } // 相位历史（用于信号确认）
			public bool Initialized { get; set; }          // 是否已初始化
			public int CooldownBars { get; set; }           // 冷却期剩余K线数
			public int HoldingBars { get; set; }            // 已持仓K线数
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		public FourierTransform()
		{
		}

		public FourierTransform(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// FFT核心参数
			sd.ArgDic["fftPeriod"] = 64;              // FFT分析周期（必须是2的幂次）
			sd.ArgDic["minCyclePeriod"] = 8;          // 最小周期（过滤高频噪声）
			sd.ArgDic["maxCyclePeriod"] = 32;         // 最大周期（过滤超低频）

			// 信号参数
			sd.ArgDic["powerThreshold"] = 0.05;       // 功率阈值（0-1，过滤弱周期）
			sd.ArgDic["phaseChangeThreshold"] = 0.05; // 相位变化阈值（过滤微小噪声信号）
			sd.ArgDic["confirmBars"] = 2;             // 信号确认K线数
			sd.ArgDic["cooldownBars"] = 3;            // 平仓后冷却K线数

			// 交易模式
			sd.ArgDic["mode"] = 0;                    // 0:双向 1:仅做多 2:仅做空
			sd.ArgDic["sendMode"] = 0;                // 0:立即 1:下个开盘

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;                // 0:固定手数 1:固定金额
			sd.ArgDic["lots"] = 1.0m;                 // 固定手数
			sd.ArgDic["money"] = 10000m;              // 固定金额

			// 止损止盈
			sd.ArgDic["useStopLoss"] = 1;             // 是否使用止损
			sd.ArgDic["stopLossPercent"] = 5.0m;      // 止损百分比
			sd.ArgDic["useTakeProfit"] = 0;           // 是否使用止盈
			sd.ArgDic["takeProfitPercent"] = 10.0m;   // 止盈百分比
			sd.ArgDic["minHoldBars"] = 20;             // 最小持仓K线数（之前不平仓）
			sd.ArgDic["_useDetrend"] = 1;              // 使用变化率去趋势（保留趋势持仓特性）

			// 图表颜色配置


			sd.ArgDescDic["confirmBars"] = new ArgDesc() { Text = "确认K线数", Explain = "信号确认所需K线数量", Type = "number" };

			sd.ArgDescDic["cooldownBars"] = new ArgDesc() { Text = "冷却K线数", Explain = "平仓后冷却等待的K线数", Type = "number" };

			sd.ArgDescDic["fftPeriod"] = new ArgDesc() { Text = "FFT周期", Explain = "FFT分析周期（必须是2的幂次）", Type = "number" };

			sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };

			sd.ArgDescDic["maxCyclePeriod"] = new ArgDesc() { Text = "最大周期", Explain = "过滤超低频的最大周期", Type = "number" };

			sd.ArgDescDic["minCyclePeriod"] = new ArgDesc() { Text = "最小周期", Explain = "过滤高频噪声的最小周期", Type = "number" };

			sd.ArgDescDic["minHoldBars"] = new ArgDesc() { Text = "最小持仓K线数", Explain = "持仓期间不平仓的最小K线数", Type = "number" };

			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };

			sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

			sd.ArgDescDic["phaseChangeThreshold"] = new ArgDesc() { Text = "相位变化阈值", Explain = "相位变化的触发阈值", Type = "number" };

			sd.ArgDescDic["powerThreshold"] = new ArgDesc() { Text = "功率阈值", Explain = "过滤弱周期的功率阈值(0-1)", Type = "number" };

			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };

			sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "止损触发的价格百分比", Type = "number" };

			sd.ArgDescDic["takeProfitPercent"] = new ArgDesc() { Text = "止盈百分比", Explain = "止盈触发的价格百分比", Type = "number" };

			sd.ArgDescDic["useStopLoss"] = new ArgDesc() { Text = "启用止损", Explain = "触及止损价自动平仓", Options = "0:关闭|1:启用", Type = "bool" };

			sd.ArgDescDic["useTakeProfit"] = new ArgDesc() { Text = "启用止盈", Explain = "触及止盈价自动平仓", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ColorDic["fft-phase"] = "#2196F3";     // 相位线颜色（蓝色）
			sd.ColorDic["fft-power"] = "#FF9800";     // 功率线颜色（橙色）
			sd.ColorDic["fft-cycle"] = "#9C27B0";     // 周期线颜色（紫色）

			sd.MidValDic["fft"] = 0;
			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			return sd;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			var quotes = tu.QuoteList;
			if (quotes == null || quotes.Count == 0) return;

			// 获取参数
			int fftPeriod = Convert.ToInt32(ArgDic["fftPeriod"]);
			int minCyclePeriod = Convert.ToInt32(ArgDic["minCyclePeriod"]);
			int maxCyclePeriod = Convert.ToInt32(ArgDic["maxCyclePeriod"]);
			double powerThreshold = Convert.ToDouble(ArgDic["powerThreshold"]);
			double phaseChangeThreshold = ArgDic.ContainsKey("phaseChangeThreshold") 
				? Convert.ToDouble(ArgDic["phaseChangeThreshold"]) : 0.03;
			int confirmBars = Convert.ToInt32(ArgDic["confirmBars"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
			int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
			decimal stopLossPercent = Convert.ToDecimal(ArgDic["stopLossPercent"]);
			int useTakeProfit = Convert.ToInt32(ArgDic["useTakeProfit"]);
			decimal takeProfitPercent = Convert.ToDecimal(ArgDic["takeProfitPercent"]);
			int cooldownBars = Convert.ToInt32(ArgDic["cooldownBars"]);
			int minHoldBars = Convert.ToInt32(ArgDic["minHoldBars"]);

			// 确保fftPeriod是2的幂次
			fftPeriod = NextPowerOfTwo(fftPeriod);

			// 检查K线数量
			if (quotes.Count < fftPeriod + 10) return;

			// 获取或创建状态
			string stateKey = tu.GetStateKey();
			if (!_stateDic.TryGetValue(stateKey, out var state))
			{
				state = new State
				{
					PhaseHistory = new List<double>(),
					Initialized = false
				};
				_stateDic[stateKey] = state;
			}

			// 提取价格数据
			var prices = quotes.Skip(quotes.Count - fftPeriod).Select(q => (double)q.Close).ToArray();

			// 去趋势处理
			bool useOldDetrend = ArgDic.ContainsKey("_useDetrend") && Convert.ToInt32(ArgDic["_useDetrend"]) == 1;
			var detrended = useOldDetrend ? Detrend(prices) : DetrendLinear(prices);

			// 应用汉宁窗
			var windowed = ApplyHanningWindow(detrended);

			// 执行FFT
			var fftResult = PerformFFT(windowed);

			// 计算功率谱
			var powerSpectrum = CalculatePowerSpectrum(fftResult);

			// 找到主导周期（在指定范围内）
			int minFreqIdx = Math.Max(1, fftPeriod / maxCyclePeriod);
			int maxFreqIdx = Math.Min(fftPeriod / 2 - 1, fftPeriod / minCyclePeriod);
			var (dominantIdx, dominantPower) = FindDominantFrequency(powerSpectrum, minFreqIdx, maxFreqIdx);

			// 计算主导周期
			int dominantPeriod = dominantIdx > 0 ? fftPeriod / dominantIdx : fftPeriod / 4;

			// 计算当前相位（-pi到pi）
			double currentPhase = dominantIdx > 0 
				? Math.Atan2(fftResult[dominantIdx].Imaginary, fftResult[dominantIdx].Real)
				: 0;

			// 归一化相位到-1至1（便于判断极值）
			double normalizedPhase = currentPhase / Math.PI;

			// 归一化功率（相对于总功率）
			double totalPower = powerSpectrum.Skip(1).Take(fftPeriod / 2 - 1).Sum();
			double normalizedPower = totalPower > 0 ? dominantPower / totalPower : 0;

			// 更新相位历史
			state.PhaseHistory.Add(normalizedPhase);
			if (state.PhaseHistory.Count > confirmBars + 1)
				state.PhaseHistory.RemoveAt(0);

			// 绘制指标
			Plot("fft", "phase", PlotType.LINE, normalizedPhase);
			Plot("fft", "power", PlotType.LINE, normalizedPower);
			Plot("fft", "cycle", PlotType.LINE, dominantPeriod);

			// 获取上一根K线的相位（更新前）
			double prevPhase = state.PrevPhase;
			bool hasPrevPhase = state.Initialized;
			
			// 更新状态
			state.PrevPhase = normalizedPhase;
			state.PrevPower = normalizedPower;
			state.DominantPeriod = dominantPeriod;

			// 首次运行，等待数据积累
			if (!state.Initialized)
			{
				state.Initialized = true;
				return;
			}
			
			// 如果没有有效的上一根相位，跳过信号判断
			if (!hasPrevPhase) return;

			// 冷却期递减
			if (state.CooldownBars > 0)
				state.CooldownBars--;

			// 持仓时递增持仓K线数
			if (state.Position != 0)
				state.HoldingBars++;

			// 当前价格
			decimal currentPrice = quotes.Last().Close;

			// 止损止盈检查
			if (state.Position != 0 && state.HoldingBars >= minHoldBars)
			{
				bool shouldClose = false;

				// 止损检查
				if (useStopLoss == 1)
				{
					decimal stopPrice = state.Position > 0
						? state.EntryPrice * (1 - stopLossPercent / 100)
						: state.EntryPrice * (1 + stopLossPercent / 100);

					if ((state.Position > 0 && currentPrice <= stopPrice) ||
					    (state.Position < 0 && currentPrice >= stopPrice))
						shouldClose = true;
				}

				// 止盈检查
				if (useTakeProfit == 1 && !shouldClose)
				{
					decimal profitPrice = state.Position > 0
						? state.EntryPrice * (1 + takeProfitPercent / 100)
						: state.EntryPrice * (1 - takeProfitPercent / 100);

					if ((state.Position > 0 && currentPrice >= profitPrice) ||
					    (state.Position < 0 && currentPrice <= profitPrice))
						shouldClose = true;
				}

				if (shouldClose)
				{
					ClosePosition(tu.MktSymbol, state, currentPrice, period, sendMode, cooldownBars);
					return;
				}
			}

			// 信号判断：基于相位方向变化
			bool powerStrong = normalizedPower >= powerThreshold;

			// 计算相位变化（处理环绕）
			double phaseDiff = normalizedPhase - prevPhase;
			if (phaseDiff < -1) phaseDiff += 2;  // 从pi到-pi
			if (phaseDiff > 1) phaseDiff -= 2;   // 从-pi到pi

			// 买入信号：相位从负到正穿越，或在负区域且上升
			bool buySignal = powerStrong && (
				(prevPhase < 0 && normalizedPhase >= 0) ||  // 向上穿越零线
				(normalizedPhase < 0 && phaseDiff > phaseChangeThreshold)  // 负区域上升
			);

			// 卖出信号：相位从正到负穿越，或在正区域且下降
			bool sellSignal = powerStrong && (
				(prevPhase > 0 && normalizedPhase <= 0) ||  // 向下穿越零线
				(normalizedPhase > 0 && phaseDiff < -phaseChangeThreshold)  // 正区域下降
			);

			// 计算交易手数
			decimal lots = CalculateLots(tu, quotes.Last());

			// 交易逻辑
			if (buySignal && mode != 2)
			{
				// 平空仓
				if (state.Position < 0 && state.HoldingBars >= minHoldBars)
				{
					ClosePosition(tu.MktSymbol, state, currentPrice, period, sendMode, cooldownBars);
				}

				// 开多仓（冷却期内不开仓）
				if (state.Position == 0 && state.CooldownBars == 0)
				{
					Trade(tu.MktSymbol, OrderType.BUY, currentPrice, lots, period, sendMode);
					state.Position = 1;
					state.Num = lots;
					state.EntryPrice = currentPrice;
					state.HoldingBars = 0;
				}
			}
			else if (sellSignal && mode != 1)
			{
				// 平多仓
				if (state.Position > 0 && state.HoldingBars >= minHoldBars)
				{
					ClosePosition(tu.MktSymbol, state, currentPrice, period, sendMode, cooldownBars);
				}

				// 开空仓（双向模式，冷却期内不开仓）
				if (state.Position == 0 && mode == 0 && state.CooldownBars == 0)
				{
					Trade(tu.MktSymbol, OrderType.SELL, currentPrice, lots, period, sendMode);
					state.Position = -1;
					state.Num = lots;
					state.EntryPrice = currentPrice;
					state.HoldingBars = 0;
				}
			}
		}

		/// <summary>
		/// 平仓
		/// </summary>
		private void ClosePosition(string mktSymbol, State state, decimal price, Period period, int sendMode, int cooldownBars = 0)
		{
			if (state.Position > 0)
			{
				Trade(mktSymbol, OrderType.SELL_TO_COVER, price, state.Num, period, sendMode);
			}
			else if (state.Position < 0)
			{
				Trade(mktSymbol, OrderType.BUY_TO_COVER, price, state.Num, period, sendMode);
			}
			state.Position = 0;
			state.Num = 0;
			state.EntryPrice = 0;
			state.CooldownBars = cooldownBars;  // 设置冷却期
		}

		/// <summary>
		/// 去趋势处理：使用价格变化率
		/// </summary>
		private double[] Detrend(double[] prices)
		{
			int n = prices.Length;
			var result = new double[n];
			
			for (int i = 1; i < n; i++)
			{
				if (prices[i - 1] != 0)
					result[i] = (prices[i] - prices[i - 1]) / prices[i - 1] * 100;
				else
					result[i] = 0;
			}
			result[0] = result[1];
			
			return result;
		}

		/// <summary>
		/// 线性回归去趋势（保留周期结构）
		/// </summary>
		private double[] DetrendLinear(double[] prices)
		{
			int n = prices.Length;
			double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
			for (int i = 0; i < n; i++)
			{
				sumX += i;
				sumY += prices[i];
				sumXY += i * prices[i];
				sumX2 += (double)i * i;
			}
			double denom = n * sumX2 - sumX * sumX;
			if (Math.Abs(denom) < 1e-10) return new double[n];
			double slope = (n * sumXY - sumX * sumY) / denom;
			double intercept = (sumY - slope * sumX) / n;

			var result = new double[n];
			for (int i = 0; i < n; i++)
			{
				result[i] = prices[i] - (slope * i + intercept);
			}
			return result;
		}

		/// <summary>
		/// 应用汉宁窗减少频谱泄漏
		/// </summary>
		private double[] ApplyHanningWindow(double[] data)
		{
			int n = data.Length;
			var result = new double[n];
			
			for (int i = 0; i < n; i++)
			{
				double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
				result[i] = data[i] * window;
			}
			
			return result;
		}

		/// <summary>
		/// 执行快速傅里叶变换
		/// </summary>
		private Complex[] PerformFFT(double[] data)
		{
			int n = data.Length;
			var complex = data.Select(d => new Complex(d, 0)).ToArray();
			FFT(complex, false);
			return complex;
		}

		/// <summary>
		/// Cooley-Tukey FFT算法（迭代版本）
		/// </summary>
		private void FFT(Complex[] data, bool inverse)
		{
			int n = data.Length;
			if (n <= 1) return;

			// 位反转排列
			int bits = (int)Math.Log2(n);
			for (int i = 0; i < n; i++)
			{
				int j = BitReverse(i, bits);
				if (j > i)
				{
					var temp = data[i];
					data[i] = data[j];
					data[j] = temp;
				}
			}

			// 蝶形运算
			for (int len = 2; len <= n; len *= 2)
			{
				double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
				var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));

				for (int i = 0; i < n; i += len)
				{
					var w = Complex.One;
					for (int j = 0; j < len / 2; j++)
					{
						var u = data[i + j];
						var v = data[i + j + len / 2] * w;
						data[i + j] = u + v;
						data[i + j + len / 2] = u - v;
						w *= wlen;
					}
				}
			}

			// 逆变换需要除以n
			if (inverse)
			{
				for (int i = 0; i < n; i++)
					data[i] /= n;
			}
		}

		/// <summary>
		/// 位反转
		/// </summary>
		private int BitReverse(int x, int bits)
		{
			int result = 0;
			for (int i = 0; i < bits; i++)
			{
				result = (result << 1) | (x & 1);
				x >>= 1;
			}
			return result;
		}

		/// <summary>
		/// 计算功率谱
		/// </summary>
		private double[] CalculatePowerSpectrum(Complex[] fftResult)
		{
			int n = fftResult.Length;
			var power = new double[n / 2];
			
			for (int i = 0; i < n / 2; i++)
			{
				power[i] = fftResult[i].Magnitude * fftResult[i].Magnitude;
			}
			
			return power;
		}

		/// <summary>
		/// 在指定频率范围内找到主导频率
		/// </summary>
		private (int index, double power) FindDominantFrequency(double[] powerSpectrum, int minIdx, int maxIdx)
		{
			int bestIdx = minIdx;
			double bestPower = 0;
			
			for (int i = minIdx; i <= maxIdx && i < powerSpectrum.Length; i++)
			{
				if (powerSpectrum[i] > bestPower)
				{
					bestPower = powerSpectrum[i];
					bestIdx = i;
				}
			}
			
			return (bestIdx, bestPower);
		}

		/// <summary>
		/// 在指定频率范围内找到前N个谐波
		/// </summary>
		private List<(int freqIdx, double amplitude, double phase, double power)> FindTopHarmonics(
			double[] powerSpectrum, Complex[] fftResult, int minIdx, int maxIdx, int count)
		{
			var candidates = new List<(int idx, double power)>();
			for (int i = minIdx; i <= maxIdx && i < powerSpectrum.Length; i++)
			{
				candidates.Add((i, powerSpectrum[i]));
			}

			return candidates
				.OrderByDescending(c => c.power)
				.Take(count)
				.Select(c => (
					freqIdx: c.idx,
					amplitude: fftResult[c.idx].Magnitude,
					phase: Math.Atan2(fftResult[c.idx].Imaginary, fftResult[c.idx].Real),
					power: c.power
				))
				.ToList();
		}

		/// <summary>
		/// 使用谐波在指定位置重构信号
		/// </summary>
		private double ReconstructSignalAt(
			List<(int freqIdx, double amplitude, double phase, double power)> harmonics,
			int position, int fftPeriod)
		{
			double value = 0;
			foreach (var h in harmonics)
			{
				value += h.amplitude * Math.Cos(2 * Math.PI * h.freqIdx * position / fftPeriod + h.phase);
			}
			return value;
		}

		/// <summary>
		/// 计算指数移动平均线
		/// </summary>
		private double CalculateEMA(List<SkQuote> quotes, int emaPeriod)
		{
			if (quotes.Count < emaPeriod) return (double)quotes[quotes.Count - 1].Close;
			double multiplier = 2.0 / (emaPeriod + 1);
			double ema = (double)quotes[quotes.Count - emaPeriod].Close;
			for (int i = quotes.Count - emaPeriod + 1; i < quotes.Count; i++)
			{
				ema = ((double)quotes[i].Close - ema) * multiplier + ema;
			}
			return ema;
		}

		/// <summary>
		/// 获取下一个2的幂次
		/// </summary>
		private int NextPowerOfTwo(int n)
		{
			int power = 1;
			while (power < n)
				power *= 2;
			return power;
		}

		/// <summary>
		/// 计算交易手数
		/// </summary>
		private decimal CalculateLots(TableUnit tu, SkQuote q)
		{
			var num = Convert.ToDecimal(ArgDic["lots"]);
			int lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);

			if (lotsMode == 1)
			{
				var sym = GetSymbol(tu.MktSymbol);
				decimal divisor = q.Close * sym.multiplier * sym.margin_ratio;
				
				if (divisor > 0)
				{
					num = Convert.ToDecimal(ArgDic["money"]) / divisor;

					if (sym.symbol_type == (int)SymbolType.COIN)
						num = (int)(num * sym.scale) / (decimal)sym.scale;
					else
						num = Math.Floor(num);
				}
			}

			return Math.Max(num, 0);
		}
	}
}
