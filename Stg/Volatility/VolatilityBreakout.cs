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
	/// 波动率扩张突破策略 (Volatility Breakout - Redesigned)
	///
	/// 设计目标：在波动率收缩末端尽早捕获突破起点，让利润奔跑
	///
	/// 核心改进 (vs 旧版):
	/// 1. 不再以"BBW 跌出 grace 期"为入场前提，而是以 BBW 百分位 < 阈值持续 N 根
	///    作为压缩信号，压缩中或刚出压缩、当根 K 线突破 Donchian 通道即可入场
	///    -> 大幅提前入场点
	/// 2. 完全移除硬止盈 (atrProfitMultiplier 删除)，仅用跟踪止损 -> 让利润奔跑
	/// 3. 分阶段止损：
	///    - Stage 0 (未达 1R)：紧止损 = 入场价 - initialStopAtr × ATR (防假突破)
	///    - Stage 1 (已达 1R)：止损上移至盈亏平衡，并切换 Chandelier (High - trailAtr × ATR)
	/// 4. 失败保护：入场后 failBars 根 K 线内未达 failR R 即强制平仓
	/// 5. 金字塔加仓：达 pyramidR R 时加仓 pyramidRatio × 原仓位，整体止损上移至 BE
	/// 6. 入场过滤：当根实体 ≥ bodyExpansion × 近 N 根均实体（爆幅过滤假突破）
	/// </summary>
	public class VolatilityBreakout : StgBase
	{
		public VolatilityBreakout()
		{
		}

		public VolatilityBreakout(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// 布林带 (用于 BBW 计算压缩状态)
			sd.ArgDic["bbPeriod"] = 20;
			sd.ArgDic["bbStdDev"] = 2.0;

			// 压缩识别
			sd.ArgDic["bbwLookback"] = 100;       // BBW 百分位回溯期
			sd.ArgDic["bbwLowPct"] = 25.0;        // BBW 低于该分位 = 压缩
			sd.ArgDic["minSqueezeBars"] = 5;      // 至少持续 5 根才算"足够压缩"
			sd.ArgDic["maxGraceBars"] = 8;        // 离开压缩后 8 根 K 线内可触发入场

			// 突破识别
			sd.ArgDic["donchianPeriod"] = 10;     // 突破当前 Donchian 通道
			sd.ArgDic["bodyAvgPeriod"] = 20;
			sd.ArgDic["bodyExpansion"] = 1.5;     // 当根实体 ≥ 1.5 × 平均实体

			// ATR & 止损
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["initialStopAtr"] = 1.5;    // 紧初始止损
			sd.ArgDic["trailingAtrMult"] = 3.0;   // Stage 1 Chandelier 倍数

			// 失败保护
			sd.ArgDic["failBars"] = 5;
			sd.ArgDic["failR"] = 0.5;

			// 金字塔加仓
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

			sd.ArgDescDic["bbPeriod"] = new ArgDesc() { Text = "布林带周期", Explain = "布林带计算周期", Type = "number" };
			sd.ArgDescDic["bbStdDev"] = new ArgDesc() { Text = "布林带标准差", Explain = "布林带标准差倍数", Type = "number" };
			sd.ArgDescDic["bbwLookback"] = new ArgDesc() { Text = "BBW回溯期", Explain = "BBW百分位排名回溯周期", Type = "number" };
			sd.ArgDescDic["bbwLowPct"] = new ArgDesc() { Text = "BBW低位分位", Explain = "BBW低于该分位认定为压缩", Type = "number" };
			sd.ArgDescDic["minSqueezeBars"] = new ArgDesc() { Text = "最小压缩K线数", Explain = "BBW持续低于阈值的最小K线数", Type = "number" };
			sd.ArgDescDic["maxGraceBars"] = new ArgDesc() { Text = "压缩缓冲期", Explain = "离开压缩后允许多少根K线内触发", Type = "number" };
			sd.ArgDescDic["donchianPeriod"] = new ArgDesc() { Text = "Donchian周期", Explain = "突破检测的Donchian通道周期", Type = "number" };
			sd.ArgDescDic["bodyAvgPeriod"] = new ArgDesc() { Text = "实体均值周期", Explain = "K线实体均值计算周期", Type = "number" };
			sd.ArgDescDic["bodyExpansion"] = new ArgDesc() { Text = "实体扩张倍数", Explain = "当根实体≥此倍数×均值才算爆幅", Type = "number" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期", Type = "number" };
			sd.ArgDescDic["initialStopAtr"] = new ArgDesc() { Text = "初始止损ATR倍数", Explain = "Stage0未达1R时的紧止损", Type = "number" };
			sd.ArgDescDic["trailingAtrMult"] = new ArgDesc() { Text = "Chandelier倍数", Explain = "Stage1达1R后跟踪止损ATR倍数", Type = "number" };
			sd.ArgDescDic["failBars"] = new ArgDesc() { Text = "失败保护K线数", Explain = "入场后N根K线未达failR时强制平仓", Type = "number" };
			sd.ArgDescDic["failR"] = new ArgDesc() { Text = "失败保护R值", Explain = "失败保护的R阈值(以入场风险为单位)", Type = "number" };
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

			sd.ColorDic["main-BB_Upper"] = "#FF9800";
			sd.ColorDic["main-BB_Lower"] = "#FF9800";
			sd.ColorDic["main-DC_High"] = "#9C27B0";
			sd.ColorDic["main-DC_Low"] = "#9C27B0";
			sd.ColorDic["sub0-BBW_Pct"] = "#2196F3";
			sd.ColorDic["sub0-LowPctLine"] = "#F44336";
			sd.ColorDic["sub1-ATR"] = "#4CAF50";
			return sd;
		}

		private class State
		{
			public int Status { get; set; }           // 0=空仓 1=多头 2=空头
			public decimal EntryPrice { get; set; }
			public decimal Num { get; set; }
			public decimal InitialStop { get; set; }
			public decimal TrailingStop { get; set; }
			public decimal HighSinceEntry { get; set; }
			public decimal LowSinceEntry { get; set; }
			public decimal RUnit { get; set; }
			public int Stage { get; set; }            // 0=紧止损 1=已达1R/Chandelier
			public int BarsSinceEntry { get; set; }
			public bool PyramidDone { get; set; }
			public int SqueezeBars { get; set; }      // 当前持续压缩根数
			public int GraceBars { get; set; }        // 离开压缩后剩余缓冲根数
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

			int bbPeriod = Convert.ToInt32(ArgDic["bbPeriod"]);
			int bbwLookback = Convert.ToInt32(ArgDic["bbwLookback"]);
			int donchianPeriod = Convert.ToInt32(ArgDic["donchianPeriod"]);
			int bodyAvgPeriod = Convert.ToInt32(ArgDic["bodyAvgPeriod"]);
			int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
			int minBars = Math.Max(bbPeriod + bbwLookback, Math.Max(donchianPeriod, Math.Max(bodyAvgPeriod, atrPeriod))) + 5;
			if (tu.QuoteList.Count < minBars) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			int lastIdx = tu.QuoteList.Count - 1;

			double bbStdDev = Convert.ToDouble(ArgDic["bbStdDev"]);
			double bbwLowPct = Convert.ToDouble(ArgDic["bbwLowPct"]);
			int minSqueezeBars = Convert.ToInt32(ArgDic["minSqueezeBars"]);
			int maxGraceBars = Convert.ToInt32(ArgDic["maxGraceBars"]);
			double bodyExpansion = Convert.ToDouble(ArgDic["bodyExpansion"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

			// 布林带 + BBW
			var bbList = tu.QuoteList.GetBollingerBands(bbPeriod, bbStdDev).ToList();
			var bb = bbList[bbList.Count - 1];
			if (!bb.UpperBand.HasValue || !bb.LowerBand.HasValue || !bb.Sma.HasValue) return;
			double bbw = (bb.UpperBand.Value - bb.LowerBand.Value) / bb.Sma.Value;

			// BBW 百分位（在 bbwLookback 历史中的位置）
			var bbwHist = new List<double>();
			int bbwStart = Math.Max(0, bbList.Count - bbwLookback);
			for (int i = bbwStart; i < bbList.Count; i++)
			{
				if (bbList[i].UpperBand.HasValue && bbList[i].LowerBand.HasValue && bbList[i].Sma.HasValue && bbList[i].Sma.Value > 0)
				{
					bbwHist.Add((bbList[i].UpperBand.Value - bbList[i].LowerBand.Value) / bbList[i].Sma.Value);
				}
			}
			double bbwPct = 50.0;
			if (bbwHist.Count > 0)
			{
				int below = bbwHist.Count(x => x <= bbw);
				bbwPct = (double)below / bbwHist.Count * 100.0;
			}

			// ATR
			var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
			var atr = atrList[atrList.Count - 1];
			if (!atr.Atr.HasValue) return;
			decimal atrVal = (decimal)atr.Atr.Value;

			// Donchian (排除当根)
			decimal dcHigh = decimal.MinValue, dcLow = decimal.MaxValue;
			int dcStart = Math.Max(0, lastIdx - donchianPeriod);
			for (int i = dcStart; i < lastIdx; i++)
			{
				if (tu.QuoteList[i].High > dcHigh) dcHigh = tu.QuoteList[i].High;
				if (tu.QuoteList[i].Low < dcLow) dcLow = tu.QuoteList[i].Low;
			}

			// 平均实体（排除当根）
			decimal bodySum = 0;
			int bodyCount = 0;
			int bodyStart = Math.Max(0, lastIdx - bodyAvgPeriod);
			for (int i = bodyStart; i < lastIdx; i++)
			{
				bodySum += Math.Abs(tu.QuoteList[i].Close - tu.QuoteList[i].Open);
				bodyCount++;
			}
			decimal avgBody = bodyCount > 0 ? bodySum / bodyCount : 0;
			decimal currentBody = Math.Abs(q.Close - q.Open);

			// 压缩状态机
			bool isSqueeze = bbwPct <= bbwLowPct;
			if (isSqueeze)
			{
				s.SqueezeBars++;
				s.GraceBars = 0;
			}
			else
			{
				if (s.SqueezeBars >= minSqueezeBars)
					s.GraceBars++;
				else
					s.SqueezeBars = 0;
			}
			bool primed = s.SqueezeBars >= minSqueezeBars && s.GraceBars <= maxGraceBars;

			// 绘图
			Plot("main", "BB_Upper", PlotType.LINE, bb.UpperBand);
			Plot("main", "BB_Lower", PlotType.LINE, bb.LowerBand);
			Plot("main", "DC_High", PlotType.LINE, (double)dcHigh);
			Plot("main", "DC_Low", PlotType.LINE, (double)dcLow);
			Plot("sub0", "BBW_Pct", PlotType.LINE, bbwPct);
			Plot("sub0", "LowPctLine", PlotType.LINE, bbwLowPct);
			Plot("sub1", "ATR", PlotType.LINE, atr.Atr);

			if (s.Status == 0)
			{
				if (primed && currentBody >= (decimal)bodyExpansion * avgBody)
				{
					decimal num = CalcNum(tu, q.Close);
					if (mode != 2 && q.Close > dcHigh)
					{
						OpenLong(tu, period, q, s, num, atrVal, sendMode);
						s.SqueezeBars = 0;
						s.GraceBars = 0;
					}
					else if (mode != 1 && q.Close < dcLow)
					{
						OpenShort(tu, period, q, s, num, atrVal, sendMode);
						s.SqueezeBars = 0;
						s.GraceBars = 0;
					}
				}

				// 清理过期 grace
				if (s.GraceBars > maxGraceBars)
				{
					s.SqueezeBars = 0;
					s.GraceBars = 0;
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

			// Stage 升级：HighSinceEntry 已达 1R → 移动止损到 BE 并切 Chandelier
			if (s.Stage == 0 && s.RUnit > 0 && (s.HighSinceEntry - s.EntryPrice) >= s.RUnit)
			{
				s.Stage = 1;
				s.InitialStop = s.EntryPrice;     // 锁定盈亏平衡
				s.TrailingStop = s.EntryPrice;
			}

			// 跟踪止损
			if (s.Stage == 1)
			{
				decimal chandelier = s.HighSinceEntry - atrVal * (decimal)trailingMult;
				if (chandelier > s.TrailingStop) s.TrailingStop = chandelier;
			}
			else
			{
				s.TrailingStop = s.InitialStop;
			}

			// 金字塔加仓
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

			// 平仓判断
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
