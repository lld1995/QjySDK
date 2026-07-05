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
	/// 波动率锥策略 (Volatility Cone - Redesigned)
	///
	/// 设计目标：当短/中/长三个时间窗口的 HV 同时处于历史极低位时，
	/// 这是统计意义上最强的"压缩共振"信号——后续大概率出现大行情。
	/// 在共振压缩期内，价格突破 N 期 Donchian 通道即跟随入场。
	///
	/// 核心改进 (vs 旧版):
	/// 1. 移除"短HV极高+长HV正常"的逆向均值回归分支——这与"捕获大行情起点"目标矛盾
	///    专注共振压缩 → 突破方向
	/// 2. 完全移除硬止盈
	/// 3. Cone Ready 状态可保持 maxConeBars 根 K 线，期间任意时刻突破都触发入场
	/// 4. 入场用 Donchian 通道突破替代单纯均线交叉
	/// 5. 失败保护 + 金字塔加仓 + 分阶段止损
	/// </summary>
	public class VolatilityCone : StgBase
	{
		public VolatilityCone()
		{
		}

		public VolatilityCone(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// HV 多窗口
			sd.ArgDic["shortHvPeriod"] = 10;
			sd.ArgDic["midHvPeriod"] = 30;
			sd.ArgDic["longHvPeriod"] = 60;
			sd.ArgDic["rankLookback"] = 252;
			sd.ArgDic["coneLowPct"] = 20.0;          // 三窗口都 ≤ 20 分位 = 共振压缩
			sd.ArgDic["maxConeBars"] = 30;            // 共振信号有效期

			// 突破识别
			sd.ArgDic["donchianPeriod"] = 20;
			sd.ArgDic["fastSmaPeriod"] = 20;
			sd.ArgDic["slowSmaPeriod"] = 50;          // 趋势方向过滤

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

			sd.ArgDescDic["shortHvPeriod"] = new ArgDesc() { Text = "短期HV周期", Explain = "短期历史波动率窗口", Type = "number" };
			sd.ArgDescDic["midHvPeriod"] = new ArgDesc() { Text = "中期HV周期", Explain = "中期历史波动率窗口", Type = "number" };
			sd.ArgDescDic["longHvPeriod"] = new ArgDesc() { Text = "长期HV周期", Explain = "长期历史波动率窗口", Type = "number" };
			sd.ArgDescDic["rankLookback"] = new ArgDesc() { Text = "排名回溯期", Explain = "百分位排名的历史回溯", Type = "number" };
			sd.ArgDescDic["coneLowPct"] = new ArgDesc() { Text = "共振低位分位", Explain = "三窗口百分位都低于此值=共振压缩", Type = "number" };
			sd.ArgDescDic["maxConeBars"] = new ArgDesc() { Text = "共振有效期", Explain = "Cone Ready信号有效根数", Type = "number" };
			sd.ArgDescDic["donchianPeriod"] = new ArgDesc() { Text = "Donchian周期", Explain = "突破检测的通道周期", Type = "number" };
			sd.ArgDescDic["fastSmaPeriod"] = new ArgDesc() { Text = "快均线周期", Explain = "趋势方向过滤的快均线", Type = "number" };
			sd.ArgDescDic["slowSmaPeriod"] = new ArgDesc() { Text = "慢均线周期", Explain = "趋势方向过滤的慢均线", Type = "number" };
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

			sd.ColorDic["main-FastSMA"] = "#FF9800";
			sd.ColorDic["main-SlowSMA"] = "#2196F3";
			sd.ColorDic["main-DC_High"] = "#9C27B0";
			sd.ColorDic["main-DC_Low"] = "#9C27B0";
			sd.ColorDic["sub0-ShortPct"] = "#F44336";
			sd.ColorDic["sub0-MidPct"] = "#FF9800";
			sd.ColorDic["sub0-LongPct"] = "#4CAF50";
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
			public bool ConeReady { get; set; }       // 共振压缩信号当前是否生效
			public int ConeBars { get; set; }          // 信号已持续根数
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

		private double CalcPercentile(List<SkQuote> quotes, int endIdx, int hvPeriod, int rankLookback)
		{
			double currentHV = CalcHV(quotes, endIdx, hvPeriod);
			var history = new List<double>();
			int start = Math.Max(hvPeriod, endIdx - rankLookback);
			for (int i = start; i <= endIdx; i++)
			{
				history.Add(CalcHV(quotes, i, hvPeriod));
			}
			if (history.Count == 0) return 50.0;
			int below = history.Count(h => h <= currentHV);
			return (double)below / history.Count * 100.0;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			int shortP = Convert.ToInt32(ArgDic["shortHvPeriod"]);
			int midP = Convert.ToInt32(ArgDic["midHvPeriod"]);
			int longP = Convert.ToInt32(ArgDic["longHvPeriod"]);
			int rankLookback = Convert.ToInt32(ArgDic["rankLookback"]);
			int donchianPeriod = Convert.ToInt32(ArgDic["donchianPeriod"]);
			int fastSmaP = Convert.ToInt32(ArgDic["fastSmaPeriod"]);
			int slowSmaP = Convert.ToInt32(ArgDic["slowSmaPeriod"]);
			int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
			int minBars = longP + rankLookback + 10;
			if (tu.QuoteList.Count < minBars) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			int lastIdx = tu.QuoteList.Count - 1;

			double coneLowPct = Convert.ToDouble(ArgDic["coneLowPct"]);
			int maxConeBars = Convert.ToInt32(ArgDic["maxConeBars"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

			// 三窗口 HV 百分位
			double shortPct = CalcPercentile(tu.QuoteList, lastIdx, shortP, rankLookback);
			double midPct = CalcPercentile(tu.QuoteList, lastIdx, midP, rankLookback);
			double longPct = CalcPercentile(tu.QuoteList, lastIdx, longP, rankLookback);

			bool allLow = shortPct <= coneLowPct && midPct <= coneLowPct && longPct <= coneLowPct;

			// SMA & ATR
			var fastSmaList = tu.QuoteList.GetSma(fastSmaP).ToList();
			var slowSmaList = tu.QuoteList.GetSma(slowSmaP).ToList();
			var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
			var fastSma = fastSmaList[fastSmaList.Count - 1];
			var slowSma = slowSmaList[slowSmaList.Count - 1];
			var atr = atrList[atrList.Count - 1];
			if (!fastSma.Sma.HasValue || !slowSma.Sma.HasValue || !atr.Atr.HasValue) return;
			decimal atrVal = (decimal)atr.Atr.Value;

			// Donchian (排除当根)
			decimal dcHigh = decimal.MinValue, dcLow = decimal.MaxValue;
			int dcStart = Math.Max(0, lastIdx - donchianPeriod);
			for (int i = dcStart; i < lastIdx; i++)
			{
				if (tu.QuoteList[i].High > dcHigh) dcHigh = tu.QuoteList[i].High;
				if (tu.QuoteList[i].Low < dcLow) dcLow = tu.QuoteList[i].Low;
			}

			// 共振压缩状态机
			if (allLow)
			{
				s.ConeReady = true;
				s.ConeBars = 0;
			}
			else if (s.ConeReady)
			{
				s.ConeBars++;
				if (s.ConeBars > maxConeBars) s.ConeReady = false;
			}

			// 绘图
			Plot("main", "FastSMA", PlotType.LINE, fastSma.Sma);
			Plot("main", "SlowSMA", PlotType.LINE, slowSma.Sma);
			Plot("main", "DC_High", PlotType.LINE, (double)dcHigh);
			Plot("main", "DC_Low", PlotType.LINE, (double)dcLow);
			Plot("sub0", "ShortPct", PlotType.LINE, shortPct);
			Plot("sub0", "MidPct", PlotType.LINE, midPct);
			Plot("sub0", "LongPct", PlotType.LINE, longPct);
			Plot("sub1", "ATR", PlotType.LINE, atr.Atr);

			if (s.Status == 0)
			{
				if (s.ConeReady)
				{
					decimal num = CalcNum(tu, q.Close);
					bool fastAboveSlow = fastSma.Sma.Value > slowSma.Sma.Value;
					bool fastBelowSlow = fastSma.Sma.Value < slowSma.Sma.Value;

					if (mode != 2 && q.Close > dcHigh && fastAboveSlow)
					{
						OpenLong(tu, period, q, s, num, atrVal, sendMode);
						s.ConeReady = false;
					}
					else if (mode != 1 && q.Close < dcLow && fastBelowSlow)
					{
						OpenShort(tu, period, q, s, num, atrVal, sendMode);
						s.ConeReady = false;
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
