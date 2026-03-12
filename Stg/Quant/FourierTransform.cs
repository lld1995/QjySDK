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
			sd.ArgDic["powerThreshold"] = 0.1;        // 功率阈值（0-1，过滤弱周期）
			sd.ArgDic["phaseChangeThreshold"] = 0.03; // 相位变化阈值
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
			sd.ArgDic["minHoldBars"] = 10;             // 最小持仓K线数（之前不平仓）

			// 图表颜色配置
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
			double phaseChangeThreshold = Convert.ToDouble(ArgDic["phaseChangeThreshold"]);
			int confirmBars = Convert.ToInt32(ArgDic["confirmBars"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
			int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
			decimal stopLossPercent = Convert.ToDecimal(ArgDic["stopLossPercent"]);
			int useTakeProfit = Convert.ToInt32(ArgDic["useTakeProfit"]);
			decimal takeProfitPercent = Convert.ToDecimal(ArgDic["takeProfitPercent"]);

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

			// 去趋势处理：计算价格变化率
			var detrended = Detrend(prices);

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

			// 计算当前相位（-π 到 π）
			double currentPhase = dominantIdx > 0 
				? Math.Atan2(fftResult[dominantIdx].Imaginary, fftResult[dominantIdx].Real)
				: 0;

			// 归一化相位到 -1 到 1（便于判断极值）
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

			// 获取上一根K线的相位（在更新之前）
			double prevPhase = state.PrevPhase;
			bool hasPrevPhase = state.Initialized;  // 只有初始化后才有有效的prevPhase
			
			// 更新状态
			state.PrevPhase = normalizedPhase;
			state.PrevPower = normalizedPower;
			state.DominantPeriod = dominantPeriod;

			// 首次运行，等待积累数据
			if (!state.Initialized)
			{
				state.Initialized = true;  // 第一根K线后就初始化
				return;
			}
			
			// 如果没有有效的上一根相位，跳过信号判断
			if (!hasPrevPhase) return;

			// 获取冷却期和最小持仓参数
			int cooldownBars = Convert.ToInt32(ArgDic["cooldownBars"]);
			int minHoldBars = Convert.ToInt32(ArgDic["minHoldBars"]);
			
			// 冷却期递减
			if (state.CooldownBars > 0)
				state.CooldownBars--;
			
			// 持仓时递增持仓K线数
			if (state.Position != 0)
				state.HoldingBars++;

			// 当前价格
			var q = quotes.Last();
			decimal currentPrice = q.Close;

			// 止损止盈检查（只有达到最小持仓时间后才检查）
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
					{
						shouldClose = true;
					}
				}

				// 止盈检查
				if (useTakeProfit == 1 && !shouldClose)
				{
					decimal profitPrice = state.Position > 0
						? state.EntryPrice * (1 + takeProfitPercent / 100)
						: state.EntryPrice * (1 - takeProfitPercent / 100);
					
					if ((state.Position > 0 && currentPrice >= profitPrice) ||
					    (state.Position < 0 && currentPrice <= profitPrice))
					{
						shouldClose = true;
					}
				}

				if (shouldClose)
				{
					ClosePosition(tu.MktSymbol, state, currentPrice, period, sendMode, cooldownBars);
					return;
				}
			}

			// 信号判断：基于相位方向变化
			bool powerStrong = normalizedPower >= powerThreshold;
			
			// 计算相位变化（处理跳变）
			double phaseDiff = normalizedPhase - prevPhase;
			if (phaseDiff < -1) phaseDiff += 2;  // 从π跳到-π
			if (phaseDiff > 1) phaseDiff -= 2;   // 从-π跳到π
			
			// 买入信号：相位从负向正穿越，或在负区域且上升
			bool buySignal = powerStrong && (
			    (prevPhase < 0 && normalizedPhase >= 0) ||  // 穿越零线向上
			    (normalizedPhase < 0 && phaseDiff > phaseChangeThreshold)  // 负区域上升
			);
			
			// 卖出信号：相位从正向负穿越，或在正区域且下降
			bool sellSignal = powerStrong && (
			    (prevPhase > 0 && normalizedPhase <= 0) ||  // 穿越零线向下
			    (normalizedPhase > 0 && phaseDiff < -phaseChangeThreshold)  // 正区域下降
			);

			// 计算交易手数
			decimal lots = CalculateLots(tu, q);

			// 交易逻辑
			if (buySignal && mode != 2)
			{
				// 平空仓（只有达到最小持仓时间后才平仓）
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
					state.HoldingBars = 0;  // 重置持仓K线数
				}
			}
			else if (sellSignal && mode != 1)
			{
				// 平多仓（只有达到最小持仓时间后才平仓）
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
					state.HoldingBars = 0;  // 重置持仓K线数
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
			int dominantIdx = minIdx;
			double maxPower = 0;

			for (int i = minIdx; i <= maxIdx && i < powerSpectrum.Length; i++)
			{
				if (powerSpectrum[i] > maxPower)
				{
					maxPower = powerSpectrum[i];
					dominantIdx = i;
				}
			}

			return (dominantIdx, maxPower);
		}

		/// <summary>
		/// 检查相位是否持续上升
		/// </summary>
		private bool IsPhaseRising(List<double> history)
		{
			if (history.Count < 2) return false;
			
			for (int i = 1; i < history.Count; i++)
			{
				// 处理相位跳变（从π跳到-π）
				double diff = history[i] - history[i - 1];
				if (diff < -1) diff += 2; // 跳变修正
				if (diff < 0) return false;
			}
			return true;
		}

		/// <summary>
		/// 检查相位是否持续下降
		/// </summary>
		private bool IsPhaseFalling(List<double> history)
		{
			if (history.Count < 2) return false;
			
			for (int i = 1; i < history.Count; i++)
			{
				// 处理相位跳变（从-π跳到π）
				double diff = history[i] - history[i - 1];
				if (diff > 1) diff -= 2; // 跳变修正
				if (diff > 0) return false;
			}
			return true;
		}

		/// <summary>
		/// 检查相位是否大致上升（放宽条件：允许小幅回调）
		/// </summary>
		private bool IsPhaseGenerallyRising(List<double> history)
		{
			if (history.Count < 2) return true; // 数据不足时默认通过
			
			// 计算总体变化
			double totalChange = history[history.Count - 1] - history[0];
			// 处理相位跳变
			if (totalChange < -1) totalChange += 2;
			
			// 只要总体趋势向上即可
			return totalChange >= -0.1;
		}

		/// <summary>
		/// 检查相位是否大致下降（放宽条件：允许小幅反弹）
		/// </summary>
		private bool IsPhaseGenerallyFalling(List<double> history)
		{
			if (history.Count < 2) return true; // 数据不足时默认通过
			
			// 计算总体变化
			double totalChange = history[history.Count - 1] - history[0];
			// 处理相位跳变
			if (totalChange > 1) totalChange -= 2;
			
			// 只要总体趋势向下即可
			return totalChange <= 0.1;
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
				var symbol = GetSymbol(tu.MktSymbol);
				decimal divisor = q.Close * symbol.multiplier * symbol.margin_ratio;
				
				if (divisor > 0)
				{
					num = Convert.ToDecimal(ArgDic["money"]) / divisor;

					if (symbol.symbol_type == (int)SymbolType.COIN)
						num = Math.Floor(num * 1000) / 1000m;
					else
						num = Math.Floor(num);
				}
			}

			return Math.Max(num, 0);
		}
	}
}
