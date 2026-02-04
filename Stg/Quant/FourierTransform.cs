using Common;
using Model;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static Model.EnumDef;

namespace QjySDK.Stg
{
	/// <summary>
	/// 傅里叶变换交易策略
	/// 使用快速傅里叶变换(FFT)分析价格周期，识别主导周期并预测价格走势
	/// 核心原理：
	/// 1. 对价格序列进行FFT分解，提取主要频率成分
	/// 2. 通过逆FFT重建平滑的价格趋势
	/// 3. 基于重建信号的斜率和相位判断买卖时机
	/// </summary>
	public class FourierTransform : StgBase
	{
		/// <summary>
		/// 策略状态
		/// </summary>
		private class State
		{
			public int Position { get; set; }           // 0:无持仓 1:多仓 -1:空仓
			public decimal Num { get; set; }            // 持仓数量
			public decimal EntryPrice { get; set; }     // 入场价格
			public double? PrevTrend { get; set; }      // 上一根K线的趋势值
			public double? PrevSlope { get; set; }      // 上一根K线的斜率
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

			// FFT参数
			sd.ArgDic["fftPeriod"] = 64;           // FFT分析周期（必须是2的幂次）
			sd.ArgDic["harmonics"] = 5;            // 保留的谐波数量（主要频率成分）
			sd.ArgDic["smoothFactor"] = 3;         // 平滑因子

			// 信号参数
			sd.ArgDic["slopeThreshold"] = 0.001;   // 斜率阈值，过滤小幅波动
			sd.ArgDic["signalDelay"] = 1;          // 信号延迟确认周期

			// 交易模式
			sd.ArgDic["mode"] = 0;                 // 0:双向 1:仅做多 2:仅做空
			sd.ArgDic["sendMode"] = 0;             // 0:立即 1:下个开盘

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;             // 0:固定手数 1:固定金额
			sd.ArgDic["lots"] = 1.0m;              // 固定手数
			sd.ArgDic["money"] = 10000m;           // 固定金额

			// 止损设置
			sd.ArgDic["useStopLoss"] = 0;          // 是否使用止损
			sd.ArgDic["stopLossPercent"] = 2.0m;   // 止损百分比

			// 过滤设置
			sd.ArgDic["minBarCount"] = 128;        // 最少K线数（需要足够数据进行FFT）

			// 图表颜色配置
			sd.ColorDic["fft-trend"] = "#2196F3";       // 趋势线颜色（蓝色）
			sd.ColorDic["fft-slope"] = "#FF9800";       // 斜率线颜色（橙色）
			sd.ColorDic["fft-signal"] = "#F6465D;#0ECB81"; // 信号颜色（红/绿）

			sd.MidValDic["fft"] = 0;
			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			return sd;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			if (!isFinal) return;

			var quotes = tu.QuoteList;
			if (quotes == null || quotes.Count == 0) return;

			// 获取参数
			int fftPeriod = Convert.ToInt32(ArgDic["fftPeriod"]);
			int harmonics = Convert.ToInt32(ArgDic["harmonics"]);
			int smoothFactor = Convert.ToInt32(ArgDic["smoothFactor"]);
			double slopeThreshold = Convert.ToDouble(ArgDic["slopeThreshold"]);
			int signalDelay = Convert.ToInt32(ArgDic["signalDelay"]);
			int minBarCount = Convert.ToInt32(ArgDic["minBarCount"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
			int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
			decimal stopLossPercent = Convert.ToDecimal(ArgDic["stopLossPercent"]);

			// 确保fftPeriod是2的幂次
			fftPeriod = NextPowerOfTwo(fftPeriod);

			// 检查K线数量
			if (quotes.Count < Math.Max(minBarCount, fftPeriod + smoothFactor)) return;

			// 获取或创建状态
			string stateKey = tu.GetStateKey();
			if (!_stateDic.TryGetValue(stateKey, out var state))
			{
				state = new State();
				_stateDic[stateKey] = state;
			}

			// 提取价格数据用于FFT分析
			var priceData = quotes.Skip(quotes.Count - fftPeriod).Take(fftPeriod)
				.Select(q => (double)q.Close).ToArray();

			// 执行FFT分析
			var fftResult = PerformFFT(priceData);

			// 滤波：只保留主要谐波
			var filteredFFT = FilterHarmonics(fftResult, harmonics);

			// 逆FFT重建趋势
			var trendData = PerformInverseFFT(filteredFFT);

			// 获取当前趋势值和斜率
			double currentTrend = trendData[trendData.Length - 1];
			double prevTrendValue = trendData.Length > 1 ? trendData[trendData.Length - 2] : currentTrend;
			double currentSlope = currentTrend - prevTrendValue;

			// 归一化斜率用于绘图
			double normalizedSlope = currentSlope / Math.Abs(priceData.Average()) * 100;

			// 绘制指标
			Plot("fft", "trend", PlotType.LINE, currentTrend);
			Plot("fft", "slope", PlotType.LINE, normalizedSlope);

			// 获取上一根K线的斜率
			double? prevSlope = state.PrevSlope;

			// 更新状态中的前值
			state.PrevTrend = currentTrend;
			state.PrevSlope = currentSlope;

			// 如果没有前值，等待下一根K线
			if (!prevSlope.HasValue)
				return;

			// 计算交易信号
			bool buySignal = prevSlope.Value <= slopeThreshold && currentSlope > slopeThreshold;   // 斜率由负转正
			bool sellSignal = prevSlope.Value >= -slopeThreshold && currentSlope < -slopeThreshold; // 斜率由正转负

			// 当前价格
			decimal currentPrice = tq.Close;

			// 计算交易手数
			decimal lots = CalculateLots(currentPrice);

			// 止损检查
			if (useStopLoss == 1 && state.Position != 0)
			{
				decimal stopLossPrice = state.EntryPrice * (1 - (state.Position > 0 ? 1 : -1) * stopLossPercent / 100);
				bool stopLossTriggered = (state.Position > 0 && currentPrice <= stopLossPrice) ||
				                         (state.Position < 0 && currentPrice >= stopLossPrice);
				if (stopLossTriggered)
				{
					// 止损平仓
					if (state.Position > 0)
					{
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
					}
					else
					{
						Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
					}
					state.Position = 0;
					state.Num = 0;
					state.EntryPrice = 0;
					return;
				}
			}

			// 交易逻辑
			if (buySignal)
			{
				// 买入信号
				if (mode != 2) // 非仅做空模式
				{
					// 如果有空仓，先平仓
					if (state.Position < 0)
					{
						Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
						state.Position = 0;
						state.Num = 0;
					}

					// 开多仓
					if (state.Position == 0)
					{
						Trade(tu.MktSymbol, OrderType.BUY, currentPrice, lots, period, sendMode);
						state.Position = 1;
						state.Num = lots;
						state.EntryPrice = currentPrice;
					}
				}
			}
			else if (sellSignal)
			{
				// 卖出信号
				if (mode != 1) // 非仅做多模式
				{
					// 如果有多仓，先平仓
					if (state.Position > 0)
					{
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
						state.Position = 0;
						state.Num = 0;
					}

					// 开空仓（双向模式）
					if (state.Position == 0 && mode == 0)
					{
						Trade(tu.MktSymbol, OrderType.SELL, currentPrice, lots, period, sendMode);
						state.Position = -1;
						state.Num = lots;
						state.EntryPrice = currentPrice;
					}
				}
			}
		}

		/// <summary>
		/// 执行快速傅里叶变换
		/// </summary>
		private Complex[] PerformFFT(double[] data)
		{
			int n = data.Length;
			Complex[] result = new Complex[n];

			// 将实数数据转换为复数
			for (int i = 0; i < n; i++)
			{
				result[i] = new Complex(data[i], 0);
			}

			// Cooley-Tukey FFT算法
			FFT(result, false);

			return result;
		}

		/// <summary>
		/// 执行逆快速傅里叶变换
		/// </summary>
		private double[] PerformInverseFFT(Complex[] data)
		{
			int n = data.Length;
			Complex[] result = new Complex[n];
			Array.Copy(data, result, n);

			// 逆FFT
			FFT(result, true);

			// 提取实部并归一化
			double[] output = new double[n];
			for (int i = 0; i < n; i++)
			{
				output[i] = result[i].Real / n;
			}

			return output;
		}

		/// <summary>
		/// Cooley-Tukey FFT算法实现
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

			// Cooley-Tukey迭代
			for (int len = 2; len <= n; len *= 2)
			{
				double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
				Complex wlen = new Complex(Math.Cos(angle), Math.Sin(angle));

				for (int i = 0; i < n; i += len)
				{
					Complex w = Complex.One;
					for (int j = 0; j < len / 2; j++)
					{
						Complex u = data[i + j];
						Complex v = data[i + j + len / 2] * w;
						data[i + j] = u + v;
						data[i + j + len / 2] = u - v;
						w *= wlen;
					}
				}
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
		/// 滤波：只保留指定数量的主要谐波
		/// </summary>
		private Complex[] FilterHarmonics(Complex[] fftData, int harmonics)
		{
			int n = fftData.Length;
			Complex[] filtered = new Complex[n];

			// 保留DC分量和前harmonics个谐波
			for (int i = 0; i < n; i++)
			{
				if (i <= harmonics || i >= n - harmonics)
				{
					filtered[i] = fftData[i];
				}
				else
				{
					filtered[i] = Complex.Zero;
				}
			}

			return filtered;
		}

		/// <summary>
		/// 获取下一个2的幂次
		/// </summary>
		private int NextPowerOfTwo(int n)
		{
			int power = 1;
			while (power < n)
			{
				power *= 2;
			}
			return power;
		}

		/// <summary>
		/// 计算交易手数
		/// </summary>
		private decimal CalculateLots(decimal price)
		{
			int lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
			if (lotsMode == 0)
			{
				return Convert.ToDecimal(ArgDic["lots"]);
			}
			else
			{
				decimal money = Convert.ToDecimal(ArgDic["money"]);
				if (price > 0)
					return Math.Floor(money / price * 100) / 100; // 保留两位小数
				return 1;
			}
		}
	}
}
