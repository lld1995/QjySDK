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
	/// 波动率均值回归策略 (Volatility Mean Reversion - Redesigned)
	///
	/// 设计目标：抓"波动率峰值过后的二次浪"
	/// 当 HV 经历过极高位 (峰值≥peakPct)、随后开始向均值回归时，
	/// 行情往往进入"高位整理 + 二次发力"阶段——这正是大行情的中段起点。
	/// 注意：均值回归指的是"波动率"向均值回归，而不是"价格"向均值回归。
	/// 价格上我们做的是"二次趋势"。
	///
	/// 核心改进 (vs 旧版):
	/// 1. 旧版的"逆向均值回归"分支 (高HV + RSI超买/超卖反向交易) 与"捕大行情"目标矛盾，
	///    被完全替换为"波动率回归后的趋势延续"逻辑
	/// 2. 旧版的"低HV + RSI>50做多"突破逻辑过于简单且与 VolatilityBreakout 重叠，
	///    被替换为更精准的"HV 峰值回落 + 价格突破整理高/低点"
	/// 3. 入场条件：
	///    - 过去 N 根 K 线内出现过 HV ≥ peakPct (经历过波动率峰值)
	///    - 当前 HV 已回落到 normalPct 区间 (波动率正在均值回归)
	///    - 价格突破"峰值之后整理区"的 Donchian 通道
	///    - 趋势方向与峰值之前的趋势一致 (二次浪沿原方向)
	/// 4. 共享框架：紧止损 → 1R → Chandelier，失败保护，金字塔加仓，无硬止盈
	/// </summary>
	public class VolatilityMeanReversion : StgBase
	{
		public VolatilityMeanReversion()
		{
		}

		public VolatilityMeanReversion(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// HV 参数
			sd.ArgDic["hvPeriod"] = 20;
			sd.ArgDic["hvRankPeriod"] = 252;
			sd.ArgDic["peakPct"] = 80.0;             // 视为"波动率峰值"
			sd.ArgDic["normalPctMax"] = 60.0;        // 当前HV回落到 normal 区间上限
			sd.ArgDic["peakLookbackBars"] = 25;      // 在过去 N 根 K 线内出现过峰值

			// 突破识别
			sd.ArgDic["donchianPeriod"] = 15;
			sd.ArgDic["trendSmaPeriod"] = 50;        // 二次浪需与中长期趋势一致

			// ATR & 止损
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["initialStopAtr"] = 1.5;
			sd.ArgDic["trailingAtrMult"] = 3.0;

			// 失败保护
			sd.ArgDic["failBars"] = 5;
			sd.ArgDic["failR"] = 0.5;

			// 金字塔
			sd.ArgDic["enablePyramid"] = 0;
			sd.ArgDic["pyramidR"] = 2.0;
			sd.ArgDic["pyramidRatio"] = 0.5;

			// 交易模式
			sd.ArgDic["mode"] = 0;
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			sd.ArgDescDic["hvPeriod"] = new ArgDesc() { Text = "HV周期", Explain = "历史波动率计算周期", Type = "number" };
			sd.ArgDescDic["hvRankPeriod"] = new ArgDesc() { Text = "HV排名周期", Explain = "百分位排名回溯周期", Type = "number" };
			sd.ArgDescDic["peakPct"] = new ArgDesc() { Text = "峰值分位", Explain = "HV高于此值视为波动率峰值", Type = "number" };
			sd.ArgDescDic["normalPctMax"] = new ArgDesc() { Text = "回归分位上限", Explain = "当前HV回落至此值以下=已回归", Type = "number" };
			sd.ArgDescDic["peakLookbackBars"] = new ArgDesc() { Text = "峰值回看", Explain = "在过去N根K线内出现过峰值才有效", Type = "number" };
			sd.ArgDescDic["donchianPeriod"] = new ArgDesc() { Text = "Donchian周期", Explain = "突破检测的整理区通道周期", Type = "number" };
			sd.ArgDescDic["trendSmaPeriod"] = new ArgDesc() { Text = "趋势均线周期", Explain = "中长期趋势方向SMA", Type = "number" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期", Type = "number" };
			sd.ArgDescDic["initialStopAtr"] = new ArgDesc() { Text = "初始止损ATR倍数", Explain = "Stage0未达1R时的紧止损", Type = "number" };
			sd.ArgDescDic["trailingAtrMult"] = new ArgDesc() { Text = "Chandelier倍数", Explain = "Stage1达1R后跟踪止损ATR倍数", Type = "number" };
			sd.ArgDescDic["failBars"] = new ArgDesc() { Text = "失败保护K线数", Explain = "入场后N根K线未达failR时强制平仓", Type = "number" };
			sd.ArgDescDic["failR"] = new ArgDesc() { Text = "失败保护R值", Explain = "失败保护的R阈值", Type = "number" };
			sd.ArgDescDic["enablePyramid"] = new ArgDesc() { Text = "金字塔加仓", Explain = "达pyramidR后加仓一次", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ArgDescDic["pyramidR"] = new ArgDesc() { Text = "加仓R阈值", Explain = "盈利达此R时触发加仓", Type = "number" };
			sd.ArgDescDic["pyramidRatio"] = new ArgDesc() { Text = "加仓比例", Explain = "加仓 = 原仓位 × 此比例", Type = "number" };
			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
			sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };
			sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 2;

			sd.ColorDic["main-TrendSMA"] = "#2196F3";
			sd.ColorDic["main-DC_High"] = "#9C27B0";
			sd.ColorDic["main-DC_Low"] = "#9C27B0";
			sd.ColorDic["sub0-HV_Pct"] = "#FF9800";
			sd.ColorDic["sub0-PeakLine"] = "#F44336";
			sd.ColorDic["sub0-NormalLine"] = "#4CAF50";
			sd.ColorDic["sub1-ATR"] = "#9C27B0";
			return sd;
		}

		private class State
		{
			public int Status { get; set; }
			public decimal EntryPrice { get; set; }
			public decimal Num { get; set; }
			public decimal InitialStop { get; set; }
			public decimal TrailingStop { get; set; }
			public decimal HighSinceEntry { get; set; }
			public decimal LowSinceEntry { get; set; }
			public decimal RUnit { get; set; }
			public int Stage { get; set; }
			public int BarsSinceEntry { get; set; }
			public bool PyramidDone { get; set; }
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		private State GetOrCreateState(string key)
		{
			if (!_stateDic.ContainsKey(key))
				_stateDic[key] = new State();
			return _stateDic[key];
		}

		private decimal CalcNum(TableUnit tu, decimal price)
		{
			var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
			if (lotsMode == 1)
			{
				var sym = GetSymbol(tu.MktSymbol);
				var num = Convert.ToDecimal(ArgDic["money"]) / (price * sym.multiplier * sym.margin_ratio);
				if (sym.symbol_type == (int)SymbolType.COIN)
					num = (int)(num * 1000) / 1000.0m;
				else
					num = (int)num;
				return num;
			}
			return Convert.ToDecimal(ArgDic["lots"]);
		}

		private double CalcHV(List<SkQuote> quotes, int endIdx, int period)
		{
			if (endIdx < period) return 0;
			var returns = new List<double>();
			for (int i = endIdx - period + 1; i <= endIdx; i++)
			{
				if (quotes[i - 1].Close > 0)
				{
					double ret = Math.Log((double)(quotes[i].Close / quotes[i - 1].Close));
					returns.Add(ret);
				}
			}
			if (returns.Count < 2) return 0;
			double mean = returns.Average();
			double sumSq = returns.Sum(r => (r - mean) * (r - mean));
			return Math.Sqrt(sumSq / (returns.Count - 1)) * Math.Sqrt(252);
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			int hvPeriod = Convert.ToInt32(ArgDic["hvPeriod"]);
			int hvRankPeriod = Convert.ToInt32(ArgDic["hvRankPeriod"]);
			int peakLookbackBars = Convert.ToInt32(ArgDic["peakLookbackBars"]);
			int donchianPeriod = Convert.ToInt32(ArgDic["donchianPeriod"]);
			int trendSmaP = Convert.ToInt32(ArgDic["trendSmaPeriod"]);
			int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
			int minBars = hvPeriod + hvRankPeriod + peakLookbackBars + 5;
			if (tu.QuoteList.Count < minBars) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			int lastIdx = tu.QuoteList.Count - 1;

			double peakPct = Convert.ToDouble(ArgDic["peakPct"]);
			double normalPctMax = Convert.ToDouble(ArgDic["normalPctMax"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

			// 计算 HV 历史序列 (用于排名)
			int histStart = Math.Max(hvPeriod, lastIdx - hvRankPeriod);
			var hvHistory = new List<double>();
			for (int i = histStart; i <= lastIdx; i++)
			{
				hvHistory.Add(CalcHV(tu.QuoteList, i, hvPeriod));
			}
			double currentHV = hvHistory[hvHistory.Count - 1];
			int below = hvHistory.Count(h => h <= currentHV);
			double currentPct = (double)below / hvHistory.Count * 100.0;

			// 检查最近 peakLookbackBars 根 K 线内有没有 HV ≥ peakPct
			bool sawPeak = false;
			int recentStart = Math.Max(0, hvHistory.Count - peakLookbackBars);
			for (int i = recentStart; i < hvHistory.Count; i++)
			{
				int belowI = hvHistory.Count(h => h <= hvHistory[i]);
				double pctI = (double)belowI / hvHistory.Count * 100.0;
				if (pctI >= peakPct) { sawPeak = true; break; }
			}

			// SMA & ATR
			var smaList = tu.QuoteList.GetSma(trendSmaP).ToList();
			var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
			var sma = smaList[smaList.Count - 1];
			var atr = atrList[atrList.Count - 1];
			if (!sma.Sma.HasValue || !atr.Atr.HasValue) return;
			decimal atrVal = (decimal)atr.Atr.Value;

			// 推断峰值前趋势方向：用 peakLookbackBars 前的 SMA 和 2×peakLookbackBars 前的 SMA 比较斜率
			int smaAtPeakIdx = Math.Max(0, smaList.Count - 1 - peakLookbackBars);
			int smaBeforePeakIdx = Math.Max(0, smaList.Count - 1 - peakLookbackBars * 2);
			var smaAtPeak = smaList[smaAtPeakIdx];
			var smaBeforePeak = smaList[smaBeforePeakIdx];
			bool trendUpAtPeak = smaAtPeak.Sma.HasValue && smaBeforePeak.Sma.HasValue
				&& smaAtPeak.Sma.Value > smaBeforePeak.Sma.Value;
			bool trendDownAtPeak = smaAtPeak.Sma.HasValue && smaBeforePeak.Sma.HasValue
				&& smaAtPeak.Sma.Value < smaBeforePeak.Sma.Value;

			// Donchian (排除当根)
			decimal dcHigh = decimal.MinValue, dcLow = decimal.MaxValue;
			int dcStart = Math.Max(0, lastIdx - donchianPeriod);
			for (int i = dcStart; i < lastIdx; i++)
			{
				if (tu.QuoteList[i].High > dcHigh) dcHigh = tu.QuoteList[i].High;
				if (tu.QuoteList[i].Low < dcLow) dcLow = tu.QuoteList[i].Low;
			}

			// 绘图
			Plot("main", "TrendSMA", PlotType.LINE, sma.Sma);
			Plot("main", "DC_High", PlotType.LINE, (double)dcHigh);
			Plot("main", "DC_Low", PlotType.LINE, (double)dcLow);
			Plot("sub0", "HV_Pct", PlotType.LINE, currentPct);
			Plot("sub0", "PeakLine", PlotType.LINE, peakPct);
			Plot("sub0", "NormalLine", PlotType.LINE, normalPctMax);
			Plot("sub1", "ATR", PlotType.LINE, atr.Atr);

			if (s.Status == 0)
			{
				bool hvRegressed = sawPeak && currentPct <= normalPctMax;
				bool aboveTrend = (double)q.Close > sma.Sma.Value;
				bool belowTrend = (double)q.Close < sma.Sma.Value;

				if (hvRegressed)
				{
					decimal num = CalcNum(tu, q.Close);
					if (mode != 2 && q.Close > dcHigh && aboveTrend && trendUpAtPeak)
					{
						OpenLong(tu, period, q, s, num, atrVal, sendMode);
					}
					else if (mode != 1 && q.Close < dcLow && belowTrend && trendDownAtPeak)
					{
						OpenShort(tu, period, q, s, num, atrVal, sendMode);
					}
				}
			}
			else if (s.Status == 1)
			{
				ManageLong(tu, period, q, s, atrVal, sendMode);
			}
			else if (s.Status == 2)
			{
				ManageShort(tu, period, q, s, atrVal, sendMode);
			}
		}

		private void OpenLong(TableUnit tu, Period period, SkQuote q, State s, decimal num, decimal atrVal, int sendMode)
		{
			double initialStopAtr = Convert.ToDouble(ArgDic["initialStopAtr"]);
			s.Status = 1;
			s.EntryPrice = q.Close;
			s.Num = num;
			s.InitialStop = q.Close - atrVal * (decimal)initialStopAtr;
			s.TrailingStop = s.InitialStop;
			s.RUnit = q.Close - s.InitialStop;
			s.HighSinceEntry = q.Close;
			s.LowSinceEntry = q.Close;
			s.Stage = 0;
			s.BarsSinceEntry = 0;
			s.PyramidDone = false;
			Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
		}

		private void OpenShort(TableUnit tu, Period period, SkQuote q, State s, decimal num, decimal atrVal, int sendMode)
		{
			double initialStopAtr = Convert.ToDouble(ArgDic["initialStopAtr"]);
			s.Status = 2;
			s.EntryPrice = q.Close;
			s.Num = num;
			s.InitialStop = q.Close + atrVal * (decimal)initialStopAtr;
			s.TrailingStop = s.InitialStop;
			s.RUnit = s.InitialStop - q.Close;
			s.HighSinceEntry = q.Close;
			s.LowSinceEntry = q.Close;
			s.Stage = 0;
			s.BarsSinceEntry = 0;
			s.PyramidDone = false;
			Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
		}

		private void ManageLong(TableUnit tu, Period period, SkQuote q, State s, decimal atrVal, int sendMode)
		{
			s.BarsSinceEntry++;
			if (q.High > s.HighSinceEntry) s.HighSinceEntry = q.High;

			double trailingMult = Convert.ToDouble(ArgDic["trailingAtrMult"]);
			int failBars = Convert.ToInt32(ArgDic["failBars"]);
			double failR = Convert.ToDouble(ArgDic["failR"]);
			int enablePyramid = Convert.ToInt32(ArgDic["enablePyramid"]);
			double pyramidR = Convert.ToDouble(ArgDic["pyramidR"]);
			double pyramidRatio = Convert.ToDouble(ArgDic["pyramidRatio"]);

			if (s.Stage == 0 && s.RUnit > 0 && (s.HighSinceEntry - s.EntryPrice) >= s.RUnit)
			{
				s.Stage = 1;
				s.InitialStop = s.EntryPrice;
				s.TrailingStop = s.EntryPrice;
			}

			if (s.Stage == 1)
			{
				decimal chandelier = s.HighSinceEntry - atrVal * (decimal)trailingMult;
				if (chandelier > s.TrailingStop) s.TrailingStop = chandelier;
			}
			else
			{
				s.TrailingStop = s.InitialStop;
			}

			if (enablePyramid == 1 && !s.PyramidDone && s.RUnit > 0
				&& (s.HighSinceEntry - s.EntryPrice) >= (decimal)pyramidR * s.RUnit)
			{
				decimal addNum = s.Num * (decimal)pyramidRatio;
				if (addNum > 0)
				{
					Trade(tu.MktSymbol, OrderType.BUY, q.Close, addNum, period, sendMode);
					s.Num += addNum;
					s.PyramidDone = true;
					if (s.InitialStop < s.EntryPrice) s.InitialStop = s.EntryPrice;
				}
			}

			bool failTimeout = s.Stage == 0 && s.BarsSinceEntry >= failBars && s.RUnit > 0
				&& (q.Close - s.EntryPrice) < (decimal)failR * s.RUnit;
			bool stopHit = q.Close <= s.TrailingStop;

			if (stopHit || failTimeout)
			{
				Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
				ResetState(s);
			}
		}

		private void ManageShort(TableUnit tu, Period period, SkQuote q, State s, decimal atrVal, int sendMode)
		{
			s.BarsSinceEntry++;
			if (q.Low < s.LowSinceEntry) s.LowSinceEntry = q.Low;

			double trailingMult = Convert.ToDouble(ArgDic["trailingAtrMult"]);
			int failBars = Convert.ToInt32(ArgDic["failBars"]);
			double failR = Convert.ToDouble(ArgDic["failR"]);
			int enablePyramid = Convert.ToInt32(ArgDic["enablePyramid"]);
			double pyramidR = Convert.ToDouble(ArgDic["pyramidR"]);
			double pyramidRatio = Convert.ToDouble(ArgDic["pyramidRatio"]);

			if (s.Stage == 0 && s.RUnit > 0 && (s.EntryPrice - s.LowSinceEntry) >= s.RUnit)
			{
				s.Stage = 1;
				s.InitialStop = s.EntryPrice;
				s.TrailingStop = s.EntryPrice;
			}

			if (s.Stage == 1)
			{
				decimal chandelier = s.LowSinceEntry + atrVal * (decimal)trailingMult;
				if (chandelier < s.TrailingStop) s.TrailingStop = chandelier;
			}
			else
			{
				s.TrailingStop = s.InitialStop;
			}

			if (enablePyramid == 1 && !s.PyramidDone && s.RUnit > 0
				&& (s.EntryPrice - s.LowSinceEntry) >= (decimal)pyramidR * s.RUnit)
			{
				decimal addNum = s.Num * (decimal)pyramidRatio;
				if (addNum > 0)
				{
					Trade(tu.MktSymbol, OrderType.SELL, q.Close, addNum, period, sendMode);
					s.Num += addNum;
					s.PyramidDone = true;
					if (s.InitialStop > s.EntryPrice) s.InitialStop = s.EntryPrice;
				}
			}

			bool failTimeout = s.Stage == 0 && s.BarsSinceEntry >= failBars && s.RUnit > 0
				&& (s.EntryPrice - q.Close) < (decimal)failR * s.RUnit;
			bool stopHit = q.Close >= s.TrailingStop;

			if (stopHit || failTimeout)
			{
				Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
				ResetState(s);
			}
		}

		private void ResetState(State s)
		{
			s.Status = 0;
			s.Num = 0;
			s.Stage = 0;
			s.BarsSinceEntry = 0;
			s.PyramidDone = false;
			s.RUnit = 0;
		}
	}
}
