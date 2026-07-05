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
	/// 波动率自适应趋势策略 (Volatility Adaptive Trend - Redesigned)
	///
	/// 设计目标：通过 KAMA 斜率从"平"到"陡"的相变，捕捉趋势苏醒的早期起点
	///
	/// 核心改进 (vs 旧版):
	/// 1. 旧版要求 Close "上穿 KAMA"，往往等于趋势已经走了一段
	///    新版用 KAMA 斜率的 Z-Score / 相对变化作为入场触发：
	///    - 之前 N 根 KAMA 几乎水平 (|斜率| < flatThr)
	///    - 当根 KAMA 斜率 > activeThr (向上突破/向下突破)
	///    - 配合 ADX 上升 + 价格站在 KAMA 上 (保证方向一致)
	///    -> 在"水平→陡峭"的相变处入场，比"close 上穿 KAMA"早 2-5 根 K 线
	/// 2. 完全移除"反向穿越 KAMA 即平仓"的硬出场，改为：
	///    - Stage 0 紧止损 + 失败保护
	///    - Stage 1 KAMA 反向斜率持续 + Chandelier
	/// 3. 加仓：达 2R 后 +0.5 仓
	/// 4. 失败保护：5 根 K 线未达 0.5R 强平
	/// </summary>
	public class VolatilityAdaptiveTrend : StgBase
	{
		public VolatilityAdaptiveTrend()
		{
		}

		public VolatilityAdaptiveTrend(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// KAMA 参数
			sd.ArgDic["erPeriod"] = 10;
			sd.ArgDic["fastPeriod"] = 2;
			sd.ArgDic["slowPeriod"] = 30;

			// 斜率激活
			sd.ArgDic["slopeLookback"] = 5;          // 计算斜率回看窗口
			sd.ArgDic["flatThreshold"] = 0.0008;     // |相对斜率| < 此值 = "平"
			sd.ArgDic["activeThreshold"] = 0.0025;   // |相对斜率| > 此值 = "陡"
			sd.ArgDic["minFlatBars"] = 8;            // KAMA 在"平"状态至少持续根数

			// 趋势过滤
			sd.ArgDic["adxPeriod"] = 14;
			sd.ArgDic["adxThreshold"] = 18.0;        // 比旧版 20 略低，避免错过早起趋势
			sd.ArgDic["adxRising"] = 1;              // 要求 ADX 当前根 > 前一根

			// ATR
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["initialStopAtr"] = 1.5;
			sd.ArgDic["trailingAtrMult"] = 3.0;

			// 失败保护
			sd.ArgDic["failBars"] = 5;
			sd.ArgDic["failR"] = 0.5;

			// 出场补充
			sd.ArgDic["exitOnReverseSlope"] = 1;     // KAMA 斜率反转 N 根 → 平仓
			sd.ArgDic["reverseBars"] = 3;

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

			sd.ArgDescDic["erPeriod"] = new ArgDesc() { Text = "ER周期", Explain = "效率比计算周期", Type = "number" };
			sd.ArgDescDic["fastPeriod"] = new ArgDesc() { Text = "快速周期", Explain = "KAMA快速常数对应周期", Type = "number" };
			sd.ArgDescDic["slowPeriod"] = new ArgDesc() { Text = "慢速周期", Explain = "KAMA慢速常数对应周期", Type = "number" };
			sd.ArgDescDic["slopeLookback"] = new ArgDesc() { Text = "斜率回看", Explain = "计算KAMA斜率的回看根数", Type = "number" };
			sd.ArgDescDic["flatThreshold"] = new ArgDesc() { Text = "平阈值", Explain = "|相对斜率|低于此值视为KAMA水平", Type = "number" };
			sd.ArgDescDic["activeThreshold"] = new ArgDesc() { Text = "陡阈值", Explain = "|相对斜率|高于此值视为KAMA陡峭", Type = "number" };
			sd.ArgDescDic["minFlatBars"] = new ArgDesc() { Text = "最小水平根数", Explain = "KAMA水平态至少持续根数", Type = "number" };
			sd.ArgDescDic["adxPeriod"] = new ArgDesc() { Text = "ADX周期", Explain = "ADX趋势强度周期", Type = "number" };
			sd.ArgDescDic["adxThreshold"] = new ArgDesc() { Text = "ADX阈值", Explain = "ADX高于此值确认有趋势", Type = "number" };
			sd.ArgDescDic["adxRising"] = new ArgDesc() { Text = "ADX上升过滤", Explain = "要求ADX当根高于前一根", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期", Type = "number" };
			sd.ArgDescDic["initialStopAtr"] = new ArgDesc() { Text = "初始止损ATR倍数", Explain = "Stage0未达1R时的紧止损", Type = "number" };
			sd.ArgDescDic["trailingAtrMult"] = new ArgDesc() { Text = "Chandelier倍数", Explain = "Stage1达1R后跟踪止损ATR倍数", Type = "number" };
			sd.ArgDescDic["failBars"] = new ArgDesc() { Text = "失败保护K线数", Explain = "入场后N根K线未达failR时强制平仓", Type = "number" };
			sd.ArgDescDic["failR"] = new ArgDesc() { Text = "失败保护R值", Explain = "失败保护的R阈值", Type = "number" };
			sd.ArgDescDic["exitOnReverseSlope"] = new ArgDesc() { Text = "斜率反转出场", Explain = "KAMA斜率反向持续N根则平仓", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ArgDescDic["reverseBars"] = new ArgDesc() { Text = "反转持续根数", Explain = "斜率反向持续多少根才平仓", Type = "number" };
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

			sd.ColorDic["main-KAMA"] = "#FF9800";
			sd.ColorDic["sub0-Slope"] = "#2196F3";
			sd.ColorDic["sub0-FlatLine"] = "#9E9E9E";
			sd.ColorDic["sub0-ActiveLine"] = "#F44336";
			sd.ColorDic["sub1-ADX"] = "#9C27B0";
			sd.ColorDic["sub1-ADXThreshold"] = "#9E9E9E";
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
			public List<double> KamaSeries { get; set; } = new List<double>();
			public int FlatBars { get; set; }
			public int ReverseBars { get; set; }
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
					num = (int)(num * sym.scale) / (decimal)sym.scale;
				else
					num = (int)num;
				return num;
			}
			return Convert.ToDecimal(ArgDic["lots"]);
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			int erPeriod = Convert.ToInt32(ArgDic["erPeriod"]);
			int fastP = Convert.ToInt32(ArgDic["fastPeriod"]);
			int slowP = Convert.ToInt32(ArgDic["slowPeriod"]);
			int slopeLookback = Convert.ToInt32(ArgDic["slopeLookback"]);
			int adxPeriod = Convert.ToInt32(ArgDic["adxPeriod"]);
			int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
			int minBars = Math.Max(erPeriod + slopeLookback * 2, Math.Max(adxPeriod, atrPeriod)) + 10;
			if (tu.QuoteList.Count < minBars) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			int lastIdx = tu.QuoteList.Count - 1;

			double flatThr = Convert.ToDouble(ArgDic["flatThreshold"]);
			double activeThr = Convert.ToDouble(ArgDic["activeThreshold"]);
			int minFlatBars = Convert.ToInt32(ArgDic["minFlatBars"]);
			double adxThreshold = Convert.ToDouble(ArgDic["adxThreshold"]);
			int adxRising = Convert.ToInt32(ArgDic["adxRising"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

			// KAMA 增量计算
			double er = CalcEfficiencyRatio(tu.QuoteList, lastIdx, erPeriod);
			double fastSC = 2.0 / (fastP + 1);
			double slowSC = 2.0 / (slowP + 1);
			double sc = er * (fastSC - slowSC) + slowSC;
			sc = sc * sc;

			double prevKama = s.KamaSeries.Count > 0 ? s.KamaSeries[s.KamaSeries.Count - 1] : (double)q.Close;
			double kama = prevKama + sc * ((double)q.Close - prevKama);
			s.KamaSeries.Add(kama);
			if (s.KamaSeries.Count > 1000) s.KamaSeries.RemoveAt(0);

			// 计算斜率（相对变化）
			double slopeNow = 0, slopePrev = 0;
			if (s.KamaSeries.Count > slopeLookback * 2 + 1)
			{
				int n = s.KamaSeries.Count - 1;
				double ka = s.KamaSeries[n];
				double kb = s.KamaSeries[n - slopeLookback];
				double kc = s.KamaSeries[n - slopeLookback * 2];
				if (kb != 0) slopeNow = (ka - kb) / Math.Abs(kb);
				if (kc != 0) slopePrev = (kb - kc) / Math.Abs(kc);
			}

			// "平"状态计数（前一段 KAMA 是否水平）
			if (Math.Abs(slopePrev) < flatThr) s.FlatBars++;
			else s.FlatBars = 0;

			// ATR
			var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
			var atr = atrList[atrList.Count - 1];
			if (!atr.Atr.HasValue) return;
			decimal atrVal = (decimal)atr.Atr.Value;

			// ADX
			var adxList = tu.QuoteList.GetAdx(adxPeriod).ToList();
			var adx = adxList[adxList.Count - 1];
			var adxPrev = adxList[adxList.Count - 2];
			if (!adx.Adx.HasValue || !adxPrev.Adx.HasValue) return;
			bool hasTrend = adx.Adx.Value >= adxThreshold;
			bool adxOk = adxRising == 0 || adx.Adx.Value > adxPrev.Adx.Value;

			// 绘图
			Plot("main", "KAMA", PlotType.LINE, kama);
			Plot("sub0", "Slope", PlotType.LINE, slopeNow * 1000);
			Plot("sub0", "FlatLine", PlotType.LINE, flatThr * 1000);
			Plot("sub0", "ActiveLine", PlotType.LINE, activeThr * 1000);
			Plot("sub1", "ADX", PlotType.LINE, adx.Adx);
			Plot("sub1", "ADXThreshold", PlotType.LINE, adxThreshold);

			if (s.Status == 0)
			{
				bool wasFlat = s.FlatBars >= minFlatBars;
				bool nowSteepUp = slopeNow > activeThr;
				bool nowSteepDown = slopeNow < -activeThr;
				bool aboveKama = (double)q.Close > kama;
				bool belowKama = (double)q.Close < kama;

				if (wasFlat && hasTrend && adxOk)
				{
					decimal num = CalcNum(tu, q.Close);
					if (mode != 2 && nowSteepUp && aboveKama)
					{
						OpenLong(tu, period, q, s, num, atrVal, sendMode);
						s.FlatBars = 0;
					}
					else if (mode != 1 && nowSteepDown && belowKama)
					{
						OpenShort(tu, period, q, s, num, atrVal, sendMode);
						s.FlatBars = 0;
					}
				}
			}
			else if (s.Status == 1)
			{
				ManageLong(tu, period, q, s, atrVal, sendMode, slopeNow);
			}
			else if (s.Status == 2)
			{
				ManageShort(tu, period, q, s, atrVal, sendMode, slopeNow);
			}
		}

		private double CalcEfficiencyRatio(List<SkQuote> quotes, int endIdx, int period)
		{
			if (endIdx < period) return 0;
			double direction = Math.Abs((double)(quotes[endIdx].Close - quotes[endIdx - period].Close));
			double volatility = 0;
			for (int i = endIdx - period + 1; i <= endIdx; i++)
			{
				volatility += Math.Abs((double)(quotes[i].Close - quotes[i - 1].Close));
			}
			return volatility > 0 ? direction / volatility : 0;
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
			s.ReverseBars = 0;
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
			s.ReverseBars = 0;
			Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
		}

		private void ManageLong(TableUnit tu, Period period, SkQuote q, State s, decimal atrVal, int sendMode, double slopeNow)
		{
			s.BarsSinceEntry++;
			if (q.High > s.HighSinceEntry) s.HighSinceEntry = q.High;

			double trailingMult = Convert.ToDouble(ArgDic["trailingAtrMult"]);
			int failBars = Convert.ToInt32(ArgDic["failBars"]);
			double failR = Convert.ToDouble(ArgDic["failR"]);
			int enablePyramid = Convert.ToInt32(ArgDic["enablePyramid"]);
			double pyramidR = Convert.ToDouble(ArgDic["pyramidR"]);
			double pyramidRatio = Convert.ToDouble(ArgDic["pyramidRatio"]);
			int exitOnReverse = Convert.ToInt32(ArgDic["exitOnReverseSlope"]);
			int reverseBars = Convert.ToInt32(ArgDic["reverseBars"]);
			double flatThr = Convert.ToDouble(ArgDic["flatThreshold"]);

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

			// 斜率反转出场
			if (slopeNow < -flatThr) s.ReverseBars++;
			else s.ReverseBars = 0;

			bool failTimeout = s.Stage == 0 && s.BarsSinceEntry >= failBars && s.RUnit > 0
				&& (q.Close - s.EntryPrice) < (decimal)failR * s.RUnit;
			bool stopHit = q.Close <= s.TrailingStop;
			bool reverseExit = exitOnReverse == 1 && s.Stage == 1 && s.ReverseBars >= reverseBars;

			if (stopHit || failTimeout || reverseExit)
			{
				Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
				ResetState(s);
			}
		}

		private void ManageShort(TableUnit tu, Period period, SkQuote q, State s, decimal atrVal, int sendMode, double slopeNow)
		{
			s.BarsSinceEntry++;
			if (q.Low < s.LowSinceEntry) s.LowSinceEntry = q.Low;

			double trailingMult = Convert.ToDouble(ArgDic["trailingAtrMult"]);
			int failBars = Convert.ToInt32(ArgDic["failBars"]);
			double failR = Convert.ToDouble(ArgDic["failR"]);
			int enablePyramid = Convert.ToInt32(ArgDic["enablePyramid"]);
			double pyramidR = Convert.ToDouble(ArgDic["pyramidR"]);
			double pyramidRatio = Convert.ToDouble(ArgDic["pyramidRatio"]);
			int exitOnReverse = Convert.ToInt32(ArgDic["exitOnReverseSlope"]);
			int reverseBars = Convert.ToInt32(ArgDic["reverseBars"]);
			double flatThr = Convert.ToDouble(ArgDic["flatThreshold"]);

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

			if (slopeNow > flatThr) s.ReverseBars++;
			else s.ReverseBars = 0;

			bool failTimeout = s.Stage == 0 && s.BarsSinceEntry >= failBars && s.RUnit > 0
				&& (s.EntryPrice - q.Close) < (decimal)failR * s.RUnit;
			bool stopHit = q.Close >= s.TrailingStop;
			bool reverseExit = exitOnReverse == 1 && s.Stage == 1 && s.ReverseBars >= reverseBars;

			if (stopHit || failTimeout || reverseExit)
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
			s.ReverseBars = 0;
		}
	}
}
