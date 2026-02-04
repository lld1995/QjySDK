using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static Model.EnumDef;

namespace QjySDK.Stg
{
	/// <summary>
	/// MACD+傅里叶变换交易策略
	/// 结合MACD指标和傅里叶变换进行周期分析
	/// 傅里叶变换用于识别价格的主导周期，过滤噪音信号
	/// MACD用于确认趋势方向和入场时机
	/// </summary>
	public class MACD_Fourier : StgBase
	{
		/// <summary>
		/// 策略状态
		/// </summary>
		private class State
		{
			public int Position { get; set; }           // 0:无持仓 1:多仓 -1:空仓
			public decimal Num { get; set; }            // 持仓数量
			public decimal EntryPrice { get; set; }     // 入场价格
			public decimal? PrevMACD { get; set; }      // 上一根K线的MACD值
			public decimal? PrevSignal { get; set; }    // 上一根K线的Signal值
			public double PrevFourierTrend { get; set; } // 上一根K线的傅里叶趋势值
			public double PrevFourierCycle { get; set; } // 上一根K线的傅里叶周期值
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		public MACD_Fourier()
		{
		}

		public MACD_Fourier(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// MACD参数
			sd.ArgDic["fastPeriod"] = 12;      // 快速EMA周期
			sd.ArgDic["slowPeriod"] = 26;      // 慢速EMA周期
			sd.ArgDic["signalPeriod"] = 9;     // 信号线周期

			// 傅里叶变换参数
			sd.ArgDic["fftPeriod"] = 64;       // FFT计算周期（必须是2的幂次）
			sd.ArgDic["dominantCycles"] = 3;   // 提取的主导周期数量
			sd.ArgDic["trendThreshold"] = 0.5; // 趋势确认阈值
			sd.ArgDic["cycleWeight"] = 0.3;    // 周期信号权重

			// 交易模式
			sd.ArgDic["mode"] = 0;             // 0:双向 1:仅做多 2:仅做空
			sd.ArgDic["sendMode"] = 0;         // 0:立即 1:下个开盘

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;         // 0:固定手数 1:固定金额
			sd.ArgDic["lots"] = 1.0m;          // 固定手数
			sd.ArgDic["money"] = 10000m;       // 固定金额

			// 止损设置
			sd.ArgDic["useStopLoss"] = 0;      // 是否使用止损
			sd.ArgDic["stopLossPercent"] = 2.0m; // 止损百分比

			// 过滤设置
			sd.ArgDic["minBarCount"] = 70;     // 最少K线数（需要足够数据计算FFT）
			sd.ArgDic["useZeroFilter"] = 0;    // 是否使用零轴过滤
			sd.ArgDic["useFourierFilter"] = 1; // 是否使用傅里叶过滤

			// 图表颜色配置
			sd.ColorDic["macd-macd"] = "#2196F3";           // MACD线颜色（蓝色）
			sd.ColorDic["macd-signal"] = "#FF9800";         // 信号线颜色（橙色）
			sd.ColorDic["macd-histogram"] = "#F6465D;#0ECB81"; // 柱状图颜色（红/绿）
			sd.ColorDic["fourier-trend"] = "#9C27B0";       // 傅里叶趋势线（紫色）
			sd.ColorDic["fourier-cycle"] = "#00BCD4";       // 傅里叶周期线（青色）

			sd.MidValDic["macd"] = 0;
			sd.MidValDic["fourier"] = 0;
			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 2;

			return sd;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			if (!isFinal) return;

			var quotes = tu.QuoteList;
			if (quotes == null || quotes.Count == 0) return;

			// 获取参数
			int fastPeriod = Convert.ToInt32(ArgDic["fastPeriod"]);
			int slowPeriod = Convert.ToInt32(ArgDic["slowPeriod"]);
			int signalPeriod = Convert.ToInt32(ArgDic["signalPeriod"]);
			int fftPeriod = Convert.ToInt32(ArgDic["fftPeriod"]);
			int dominantCycles = Convert.ToInt32(ArgDic["dominantCycles"]);
			double trendThreshold = Convert.ToDouble(ArgDic["trendThreshold"]);
			double cycleWeight = Convert.ToDouble(ArgDic["cycleWeight"]);
			int minBarCount = Convert.ToInt32(ArgDic["minBarCount"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
			int useZeroFilter = Convert.ToInt32(ArgDic["useZeroFilter"]);
			int useFourierFilter = Convert.ToInt32(ArgDic["useFourierFilter"]);
			int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
			decimal stopLossPercent = Convert.ToDecimal(ArgDic["stopLossPercent"]);

			// 检查K线数量
			if (quotes.Count < minBarCount) return;

			// 获取或创建状态
			string stateKey = tu.GetStateKey();
			if (!_stateDic.TryGetValue(stateKey, out var state))
			{
				state = new State();
				_stateDic[stateKey] = state;
			}

			// 计算MACD
			var macdResults = quotes.GetMacd(fastPeriod, slowPeriod, signalPeriod).ToList();
			if (macdResults.Count == 0) return;

			var lastIndex = macdResults.Count - 1;
			var currMacd = macdResults[lastIndex];

			// 绘制MACD指标
			if (currMacd.Macd.HasValue)
				Plot("macd", "macd", PlotType.LINE, currMacd.Macd);
			if (currMacd.Signal.HasValue)
				Plot("macd", "signal", PlotType.LINE, currMacd.Signal);
			if (currMacd.Histogram.HasValue)
				Plot("macd", "histogram", PlotType.RECTANGLE, currMacd.Histogram);

			// 计算傅里叶变换
			var (fourierTrend, fourierCycle, dominantPeriod) = CalculateFourier(quotes, fftPeriod, dominantCycles);

			// 绘制傅里叶指标
			Plot("fourier", "trend", PlotType.LINE, fourierTrend);
			Plot("fourier", "cycle", PlotType.LINE, fourierCycle);

			// 需要至少两根K线的MACD数据来判断交叉
			if (!currMacd.Macd.HasValue || !currMacd.Signal.HasValue)
				return;

			decimal currentMACD = (decimal)currMacd.Macd.Value;
			decimal currentSignal = (decimal)currMacd.Signal.Value;
			decimal currentHistogram = currMacd.Histogram.HasValue ? (decimal)currMacd.Histogram.Value : 0;

			// 获取上一根K线的数据
			decimal? prevMACD = state.PrevMACD;
			decimal? prevSignal = state.PrevSignal;
			double prevFourierTrend = state.PrevFourierTrend;

			// 更新状态中的前值
			state.PrevMACD = currentMACD;
			state.PrevSignal = currentSignal;
			state.PrevFourierTrend = fourierTrend;
			state.PrevFourierCycle = fourierCycle;

			// 如果没有前值，等待下一根K线
			if (!prevMACD.HasValue || !prevSignal.HasValue)
				return;

			// 计算MACD交叉信号
			bool goldenCross = prevMACD.Value <= prevSignal.Value && currentMACD > currentSignal; // 金叉
			bool deathCross = prevMACD.Value >= prevSignal.Value && currentMACD < currentSignal;  // 死叉

			// 傅里叶趋势过滤
			bool fourierBullish = fourierTrend > trendThreshold && fourierTrend > prevFourierTrend;
			bool fourierBearish = fourierTrend < -trendThreshold && fourierTrend < prevFourierTrend;

			// 周期信号增强
			bool cycleConfirmBuy = fourierCycle > cycleWeight;
			bool cycleConfirmSell = fourierCycle < -cycleWeight;

			// 应用傅里叶过滤
			if (useFourierFilter == 1)
			{
				if (goldenCross && !fourierBullish && !cycleConfirmBuy) goldenCross = false;
				if (deathCross && !fourierBearish && !cycleConfirmSell) deathCross = false;
			}

			// 零轴过滤
			if (useZeroFilter == 1)
			{
				if (goldenCross && currentMACD < 0) goldenCross = false;
				if (deathCross && currentMACD > 0) deathCross = false;
			}

			// 当前价格
			decimal currentPrice = tq.Close;

			// 计算交易手数
			decimal lots = CalculateLots(tu, tq);

			// 止损检查
			if (useStopLoss == 1 && state.Position != 0)
			{
				decimal stopLossPrice = state.EntryPrice * (1 - (state.Position > 0 ? 1 : -1) * stopLossPercent / 100);
				bool stopLossTriggered = (state.Position > 0 && currentPrice <= stopLossPrice) ||
				                         (state.Position < 0 && currentPrice >= stopLossPrice);
				if (stopLossTriggered)
				{
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
			if (goldenCross)
			{
				if (mode != 2)
				{
					if (state.Position < 0)
					{
						Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
						state.Position = 0;
						state.Num = 0;
					}

					if (state.Position == 0)
					{
						Trade(tu.MktSymbol, OrderType.BUY, currentPrice, lots, period, sendMode);
						state.Position = 1;
						state.Num = lots;
						state.EntryPrice = currentPrice;
					}
				}
			}
			else if (deathCross)
			{
				if (mode != 1)
				{
					if (state.Position > 0)
					{
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
						state.Position = 0;
						state.Num = 0;
					}

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
		/// 计算傅里叶变换指标
		/// </summary>
		/// <param name="quotes">K线数据</param>
		/// <param name="fftPeriod">FFT周期</param>
		/// <param name="dominantCycles">主导周期数量</param>
		/// <returns>(趋势值, 周期值, 主导周期)</returns>
		private (double trend, double cycle, int dominantPeriod) CalculateFourier(List<SkQuote> quotes, int fftPeriod, int dominantCycles)
		{
			if (quotes.Count < fftPeriod)
				return (0, 0, 0);

			// 获取最近fftPeriod个收盘价
			var prices = quotes.Skip(quotes.Count - fftPeriod).Select(q => (double)q.Close).ToArray();

			// 去趋势处理：使用价格变化率
			var detrended = new double[fftPeriod];
			for (int i = 1; i < fftPeriod; i++)
			{
				detrended[i] = (prices[i] - prices[i - 1]) / prices[i - 1] * 100;
			}
			detrended[0] = detrended[1];

			// 应用汉宁窗减少频谱泄漏
			var windowed = ApplyHanningWindow(detrended);

			// 执行FFT
			var fftResult = FFT(windowed);

			// 计算功率谱
			var powerSpectrum = new double[fftPeriod / 2];
			for (int i = 0; i < fftPeriod / 2; i++)
			{
				powerSpectrum[i] = fftResult[i].Magnitude;
			}

			// 找到主导周期
			var dominantIndices = GetDominantFrequencies(powerSpectrum, dominantCycles);
			int dominantPeriod = dominantIndices.Count > 0 ? fftPeriod / (dominantIndices[0] + 1) : fftPeriod / 4;

			// 计算趋势指标：基于低频成分
			double trendValue = 0;
			int lowFreqCutoff = Math.Max(1, fftPeriod / 16);
			for (int i = 1; i <= lowFreqCutoff; i++)
			{
				trendValue += powerSpectrum[i] * Math.Sign(fftResult[i].Real);
			}
			trendValue = NormalizeValue(trendValue, -10, 10);

			// 计算周期指标：基于主导周期的相位
			double cycleValue = 0;
			foreach (var idx in dominantIndices)
			{
				if (idx > 0 && idx < fftPeriod / 2)
				{
					double phase = Math.Atan2(fftResult[idx].Imaginary, fftResult[idx].Real);
					cycleValue += Math.Sin(phase) * powerSpectrum[idx];
				}
			}
			cycleValue = NormalizeValue(cycleValue, -5, 5);

			return (trendValue, cycleValue, dominantPeriod);
		}

		/// <summary>
		/// 应用汉宁窗
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
		/// 快速傅里叶变换（Cooley-Tukey算法）
		/// </summary>
		private Complex[] FFT(double[] data)
		{
			int n = data.Length;
			var complex = data.Select(d => new Complex(d, 0)).ToArray();
			return FFTRecursive(complex);
		}

		private Complex[] FFTRecursive(Complex[] data)
		{
			int n = data.Length;
			if (n <= 1) return data;

			// 分离奇偶项
			var even = new Complex[n / 2];
			var odd = new Complex[n / 2];
			for (int i = 0; i < n / 2; i++)
			{
				even[i] = data[2 * i];
				odd[i] = data[2 * i + 1];
			}

			// 递归FFT
			var evenFFT = FFTRecursive(even);
			var oddFFT = FFTRecursive(odd);

			// 合并结果
			var result = new Complex[n];
			for (int k = 0; k < n / 2; k++)
			{
				double angle = -2 * Math.PI * k / n;
				var twiddle = new Complex(Math.Cos(angle), Math.Sin(angle));
				var t = twiddle * oddFFT[k];
				result[k] = evenFFT[k] + t;
				result[k + n / 2] = evenFFT[k] - t;
			}

			return result;
		}

		/// <summary>
		/// 获取主导频率索引
		/// </summary>
		private List<int> GetDominantFrequencies(double[] powerSpectrum, int count)
		{
			var indexed = powerSpectrum
				.Select((value, index) => new { Value = value, Index = index })
				.Where(x => x.Index > 0) // 排除直流分量
				.OrderByDescending(x => x.Value)
				.Take(count)
				.Select(x => x.Index)
				.ToList();
			return indexed;
		}

		/// <summary>
		/// 归一化值到指定范围
		/// </summary>
		private double NormalizeValue(double value, double min, double max)
		{
			if (value < min) return -1;
			if (value > max) return 1;
			return (value - min) / (max - min) * 2 - 1;
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
				num = Convert.ToDecimal(ArgDic["money"]) / (q.Close * symbol.multiplier * symbol.margin_ratio);

				if (symbol.symbol_type == (int)SymbolType.COIN)
				{
					num = Math.Floor(num * 1000) / 1000m;
				}
				else
				{
					num = Math.Floor(num);
				}
			}

			return Math.Max(num, 0.001m);
		}
	}
}
