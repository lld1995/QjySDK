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
	/// 波动率自适应趋势策略 (Volatility Adaptive Trend Strategy)
	/// 
	/// 策略原理：
	/// 基于Kaufman自适应均线(KAMA)思想，根据当前波动率水平动态调整趋势策略参数。
	/// 高波动环境下使用慢参数（宽止损、长均线），低波动环境下使用快参数（窄止损、短均线）。
	/// 
	/// 核心逻辑：
	/// 1. 计算效率比(Efficiency Ratio)：方向变化/总波动，衡量趋势效率
	/// 2. 根据ER动态计算自适应均线(AMA)
	/// 3. 根据ATR百分位动态调整止损倍数和入场灵敏度
	/// 4. 高波动 → 宽止损避免假突破；低波动 → 窄止损紧跟趋势
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

			// 自适应均线参数
			sd.ArgDic["erPeriod"] = 10;
			sd.ArgDic["fastPeriod"] = 2;
			sd.ArgDic["slowPeriod"] = 30;

			// ATR波动率参数
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["atrRankPeriod"] = 100;

			// 自适应止损范围 (根据波动率在min和max之间线性插值)
			sd.ArgDic["minStopAtr"] = 1.5;
			sd.ArgDic["maxStopAtr"] = 4.0;

			// 趋势确认
			sd.ArgDic["trendSmaPeriod"] = 50;
			sd.ArgDic["adxPeriod"] = 14;
			sd.ArgDic["adxThreshold"] = 20.0;

			// 交易模式
			sd.ArgDic["mode"] = 0;
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			sd.ArgDescDic["erPeriod"] = new ArgDesc() { Text = "ER周期", Explain = "效率比计算周期" };
			sd.ArgDescDic["fastPeriod"] = new ArgDesc() { Text = "快速周期", Explain = "自适应均线快速常数对应周期" };
			sd.ArgDescDic["slowPeriod"] = new ArgDesc() { Text = "慢速周期", Explain = "自适应均线慢速常数对应周期" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期" };
			sd.ArgDescDic["atrRankPeriod"] = new ArgDesc() { Text = "ATR排名周期", Explain = "ATR百分位排名回溯" };
			sd.ArgDescDic["minStopAtr"] = new ArgDesc() { Text = "最小止损ATR倍数", Explain = "低波动时的止损ATR倍数" };
			sd.ArgDescDic["maxStopAtr"] = new ArgDesc() { Text = "最大止损ATR倍数", Explain = "高波动时的止损ATR倍数" };
			sd.ArgDescDic["trendSmaPeriod"] = new ArgDesc() { Text = "趋势均线周期", Explain = "长期趋势判断均线" };
			sd.ArgDescDic["adxPeriod"] = new ArgDesc() { Text = "ADX周期", Explain = "ADX趋势强度周期" };
			sd.ArgDescDic["adxThreshold"] = new ArgDesc() { Text = "ADX阈值", Explain = "ADX高于此值确认有趋势" };
			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 双向 1 仅做多 2 仅做空" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 2;

			sd.ColorDic["main-AMA"] = "#FF9800";
			sd.ColorDic["main-TrendSMA"] = "#2196F3";
			sd.ColorDic["sub0-ADX"] = "#9C27B0";
			sd.ColorDic["sub0-ADXThreshold"] = "#9E9E9E";
			sd.ColorDic["sub1-StopAtr"] = "#F44336";
			sd.ColorDic["sub1-AtrPct"] = "#4CAF50";
			return sd;
		}

		private class State
		{
			public int Status { get; set; }
			public decimal EntryPrice { get; set; }
			public decimal StopLoss { get; set; }
			public decimal HighSinceEntry { get; set; }
			public decimal LowSinceEntry { get; set; }
			public decimal Num { get; set; }
			public double AMA { get; set; }
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

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			int erPeriod = Convert.ToInt32(ArgDic["erPeriod"]);
			int fastP = Convert.ToInt32(ArgDic["fastPeriod"]);
			int slowP = Convert.ToInt32(ArgDic["slowPeriod"]);
			int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
			int atrRankPeriod = Convert.ToInt32(ArgDic["atrRankPeriod"]);
			int trendSmaPeriod = Convert.ToInt32(ArgDic["trendSmaPeriod"]);
			int adxPeriod = Convert.ToInt32(ArgDic["adxPeriod"]);

			int minBars = Math.Max(Math.Max(erPeriod + atrRankPeriod, trendSmaPeriod), adxPeriod) + 10;
			if (tu.QuoteList.Count < minBars) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			int lastIdx = tu.QuoteList.Count - 1;

			double minStopAtr = Convert.ToDouble(ArgDic["minStopAtr"]);
			double maxStopAtr = Convert.ToDouble(ArgDic["maxStopAtr"]);
			double adxThreshold = Convert.ToDouble(ArgDic["adxThreshold"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

			// 计算效率比 (Efficiency Ratio)
			double direction = Math.Abs((double)(q.Close - tu.QuoteList[lastIdx - erPeriod].Close));
			double volatility = 0;
			for (int i = lastIdx - erPeriod + 1; i <= lastIdx; i++)
			{
				volatility += Math.Abs((double)(tu.QuoteList[i].Close - tu.QuoteList[i - 1].Close));
			}
			double er = volatility > 0 ? direction / volatility : 0;

			// 自适应均线 (AMA)
			double fastSC = 2.0 / (fastP + 1);
			double slowSC = 2.0 / (slowP + 1);
			double sc = er * (fastSC - slowSC) + slowSC;
			sc = sc * sc; // 平方使反应更灵敏

			if (s.AMA == 0)
				s.AMA = (double)q.Close;
			else
				s.AMA = s.AMA + sc * ((double)q.Close - s.AMA);

			// 计算ATR
			var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
			var atr = atrList[atrList.Count - 1];
			if (!atr.Atr.HasValue) return;

			// ATR百分位排名
			var atrHistory = new List<double>();
			int atrStart = Math.Max(0, atrList.Count - atrRankPeriod);
			for (int i = atrStart; i < atrList.Count; i++)
			{
				if (atrList[i].Atr.HasValue) atrHistory.Add(atrList[i].Atr.Value);
			}
			double atrPct = 50.0;
			if (atrHistory.Count > 0)
			{
				int belowCount = atrHistory.Count(a => a <= atr.Atr.Value);
				atrPct = (double)belowCount / atrHistory.Count * 100.0;
			}

			// 根据ATR百分位动态计算止损倍数 (线性插值)
			double volRatio = atrPct / 100.0;
			double adaptiveStopMult = minStopAtr + (maxStopAtr - minStopAtr) * volRatio;

			// 趋势SMA
			var smaList = tu.QuoteList.GetSma(trendSmaPeriod).ToList();
			var trendSma = smaList[smaList.Count - 1];

			// ADX
			var adxList = tu.QuoteList.GetAdx(adxPeriod).ToList();
			var adx = adxList[adxList.Count - 1];

			if (!trendSma.Sma.HasValue || !adx.Adx.HasValue) return;

			bool hasTrend = adx.Adx.Value >= adxThreshold;

			// 绘图
			Plot("main", "AMA", PlotType.LINE, s.AMA);
			Plot("main", "TrendSMA", PlotType.LINE, trendSma.Sma);
			Plot("sub0", "ADX", PlotType.LINE, adx.Adx);
			Plot("sub0", "ADXThreshold", PlotType.LINE, adxThreshold);
			Plot("sub1", "StopAtr", PlotType.LINE, adaptiveStopMult);
			Plot("sub1", "AtrPct", PlotType.LINE, atrPct);

			decimal num = CalcNum(tu, q.Close);
			decimal atrVal = (decimal)atr.Atr.Value;
			decimal adaptiveStop = atrVal * (decimal)adaptiveStopMult;

			if (s.Status == 0)
			{
				if (!hasTrend) return;

				// 价格上穿AMA + 趋势向上 → 做多
				var prevQ = tu.QuoteList[lastIdx - 1];
				bool crossAboveAMA = (double)prevQ.Close <= s.AMA && (double)q.Close > s.AMA;
				bool crossBelowAMA = (double)prevQ.Close >= s.AMA && (double)q.Close < s.AMA;
				bool aboveTrendSMA = q.Close > (decimal)trendSma.Sma.Value;
				bool belowTrendSMA = q.Close < (decimal)trendSma.Sma.Value;

				if (mode != 2 && crossAboveAMA && aboveTrendSMA)
				{
					s.Status = 1;
					s.EntryPrice = q.Close;
					s.Num = num;
					s.StopLoss = q.Close - adaptiveStop;
					s.HighSinceEntry = q.Close;
					Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
				}
				else if (mode != 1 && crossBelowAMA && belowTrendSMA)
				{
					s.Status = 2;
					s.EntryPrice = q.Close;
					s.Num = num;
					s.StopLoss = q.Close + adaptiveStop;
					s.LowSinceEntry = q.Close;
					Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
				}
			}
			else if (s.Status == 1)
			{
				if (q.High > s.HighSinceEntry) s.HighSinceEntry = q.High;

				// 自适应跟踪止损
				decimal newStop = s.HighSinceEntry - adaptiveStop;
				if (newStop > s.StopLoss) s.StopLoss = newStop;

				// 反向穿越AMA也平仓
				if (q.Close <= s.StopLoss || (double)q.Close < s.AMA)
				{
					Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Status = 0;
					s.Num = 0;
				}
			}
			else if (s.Status == 2)
			{
				if (q.Low < s.LowSinceEntry) s.LowSinceEntry = q.Low;

				decimal newStop = s.LowSinceEntry + adaptiveStop;
				if (newStop < s.StopLoss) s.StopLoss = newStop;

				if (q.Close >= s.StopLoss || (double)q.Close > s.AMA)
				{
					Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Status = 0;
					s.Num = 0;
				}
			}
		}
	}
}
