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
	/// 波动率锥策略 (Volatility Cone Strategy)
	/// 
	/// 策略原理：
	/// 波动率锥(Volatility Cone)展示不同回溯周期下历史波动率的统计分布。
	/// 通过在多个时间窗口计算HV的分位数，判断当前波动率处于历史什么位置。
	/// 
	/// 核心逻辑：
	/// 1. 在多个窗口(短/中/长)计算历史波动率
	/// 2. 对每个窗口计算当前HV在历史中的百分位
	/// 3. 当多个窗口的百分位一致性指向极端时，产生高置信度信号
	/// 4. 短期HV极高+长期HV正常 → 短期过度波动，预期回归
	/// 5. 所有周期HV极低 → 波动率压缩，预期突破
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

			// 波动率窗口
			sd.ArgDic["shortHvPeriod"] = 10;
			sd.ArgDic["midHvPeriod"] = 30;
			sd.ArgDic["longHvPeriod"] = 60;

			// 百分位排名回溯
			sd.ArgDic["rankLookback"] = 252;

			// 信号阈值
			sd.ArgDic["extremeHighPct"] = 85.0;
			sd.ArgDic["extremeLowPct"] = 15.0;

			// 均线
			sd.ArgDic["smaPeriod"] = 50;
			sd.ArgDic["fastSmaPeriod"] = 10;

			// 止损止盈
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["stopLossAtr"] = 2.0;
			sd.ArgDic["takeProfitAtr"] = 4.0;

			// 交易模式
			sd.ArgDic["mode"] = 0;
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			sd.ArgDescDic["shortHvPeriod"] = new ArgDesc() { Text = "短期HV周期", Explain = "短期历史波动率计算窗口" };
			sd.ArgDescDic["midHvPeriod"] = new ArgDesc() { Text = "中期HV周期", Explain = "中期历史波动率计算窗口" };
			sd.ArgDescDic["longHvPeriod"] = new ArgDesc() { Text = "长期HV周期", Explain = "长期历史波动率计算窗口" };
			sd.ArgDescDic["rankLookback"] = new ArgDesc() { Text = "排名回溯期", Explain = "百分位排名的历史回溯周期" };
			sd.ArgDescDic["extremeHighPct"] = new ArgDesc() { Text = "极高百分位", Explain = "波动率百分位高于此值为极端高" };
			sd.ArgDescDic["extremeLowPct"] = new ArgDesc() { Text = "极低百分位", Explain = "波动率百分位低于此值为极端低" };
			sd.ArgDescDic["smaPeriod"] = new ArgDesc() { Text = "慢均线周期", Explain = "趋势判断长均线" };
			sd.ArgDescDic["fastSmaPeriod"] = new ArgDesc() { Text = "快均线周期", Explain = "趋势判断短均线" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期" };
			sd.ArgDescDic["stopLossAtr"] = new ArgDesc() { Text = "止损ATR倍数", Explain = "止损距离=ATR*此倍数" };
			sd.ArgDescDic["takeProfitAtr"] = new ArgDesc() { Text = "止盈ATR倍数", Explain = "止盈距离=ATR*此倍数" };
			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 双向 1 仅做多 2 仅做空" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 2;

			sd.ColorDic["main-SlowSMA"] = "#2196F3";
			sd.ColorDic["main-FastSMA"] = "#FF9800";
			sd.ColorDic["sub0-ShortHV"] = "#F44336";
			sd.ColorDic["sub0-MidHV"] = "#FF9800";
			sd.ColorDic["sub0-LongHV"] = "#4CAF50";
			sd.ColorDic["sub1-ShortPct"] = "#F44336";
			sd.ColorDic["sub1-MidPct"] = "#FF9800";
			sd.ColorDic["sub1-LongPct"] = "#4CAF50";
			return sd;
		}

		private class State
		{
			public int Status { get; set; }
			public decimal EntryPrice { get; set; }
			public decimal StopLoss { get; set; }
			public decimal TakeProfit { get; set; }
			public decimal Num { get; set; }
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
			var lotsMode = (int)ArgDic["lotsMode"];
			if (lotsMode == 1)
			{
				var sym = GetSymbol(tu.MktSymbol);
				var num = (decimal)ArgDic["money"] / (price * sym.multiplier * sym.margin_ratio);
				if (sym.symbol_type == (int)SymbolType.COIN)
					num = (int)(num * 1000) / 1000.0m;
				else
					num = (int)num;
				return num;
			}
			return (decimal)ArgDic["lots"];
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
			int belowCount = history.Count(h => h <= currentHV);
			return (double)belowCount / history.Count * 100.0;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			int shortP = (int)ArgDic["shortHvPeriod"];
			int midP = (int)ArgDic["midHvPeriod"];
			int longP = (int)ArgDic["longHvPeriod"];
			int rankLookback = (int)ArgDic["rankLookback"];
			int smaPeriod = (int)ArgDic["smaPeriod"];
			int fastSmaPeriod = (int)ArgDic["fastSmaPeriod"];
			int atrPeriod = (int)ArgDic["atrPeriod"];

			int minBars = longP + rankLookback + 10;
			if (tu.QuoteList.Count < minBars) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			int lastIdx = tu.QuoteList.Count - 1;

			double extremeHigh = (double)ArgDic["extremeHighPct"];
			double extremeLow = (double)ArgDic["extremeLowPct"];
			double stopAtr = (double)ArgDic["stopLossAtr"];
			double profitAtr = (double)ArgDic["takeProfitAtr"];
			int mode = (int)ArgDic["mode"];
			int sendMode = (int)ArgDic["sendMode"];

			// 计算三个窗口的HV和百分位
			double shortHV = CalcHV(tu.QuoteList, lastIdx, shortP);
			double midHV = CalcHV(tu.QuoteList, lastIdx, midP);
			double longHV = CalcHV(tu.QuoteList, lastIdx, longP);

			double shortPct = CalcPercentile(tu.QuoteList, lastIdx, shortP, rankLookback);
			double midPct = CalcPercentile(tu.QuoteList, lastIdx, midP, rankLookback);
			double longPct = CalcPercentile(tu.QuoteList, lastIdx, longP, rankLookback);

			// 均线
			var smaList = tu.QuoteList.GetSma(smaPeriod).ToList();
			var fastSmaList = tu.QuoteList.GetSma(fastSmaPeriod).ToList();
			var sma = smaList[smaList.Count - 1];
			var fastSma = fastSmaList[fastSmaList.Count - 1];

			// ATR
			var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
			var atr = atrList[atrList.Count - 1];

			if (!sma.Sma.HasValue || !fastSma.Sma.HasValue || !atr.Atr.HasValue) return;

			// 绘图
			Plot("main", "SlowSMA", PlotType.LINE, sma.Sma);
			Plot("main", "FastSMA", PlotType.LINE, fastSma.Sma);
			Plot("sub0", "ShortHV", PlotType.LINE, shortHV * 100);
			Plot("sub0", "MidHV", PlotType.LINE, midHV * 100);
			Plot("sub0", "LongHV", PlotType.LINE, longHV * 100);
			Plot("sub1", "ShortPct", PlotType.LINE, shortPct);
			Plot("sub1", "MidPct", PlotType.LINE, midPct);
			Plot("sub1", "LongPct", PlotType.LINE, longPct);

			decimal num = CalcNum(tu, q.Close);
			decimal atrVal = (decimal)atr.Atr.Value;

			if (s.Status == 0)
			{
				// 模式1：短期HV极高 + 长期HV正常 → 短期过度波动，逆向均值回归
				bool shortExtHigh = shortPct >= extremeHigh;
				bool longNormal = longPct >= 30 && longPct <= 70;

				if (shortExtHigh && longNormal)
				{
					// 用快慢均线判断方向
					if (mode != 2 && q.Close < (decimal)fastSma.Sma.Value && fastSma.Sma.Value < sma.Sma.Value)
					{
						// 超跌反弹做多
						s.Status = 1;
						s.EntryPrice = q.Close;
						s.Num = num;
						s.StopLoss = q.Close - atrVal * (decimal)stopAtr;
						s.TakeProfit = (decimal)fastSma.Sma.Value;
						Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
					}
					else if (mode != 1 && q.Close > (decimal)fastSma.Sma.Value && fastSma.Sma.Value > sma.Sma.Value)
					{
						// 超涨回落做空
						s.Status = 2;
						s.EntryPrice = q.Close;
						s.Num = num;
						s.StopLoss = q.Close + atrVal * (decimal)stopAtr;
						s.TakeProfit = (decimal)fastSma.Sma.Value;
						Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
					}
				}

				// 模式2：所有周期HV都极低 → 波动率压缩，突破交易
				bool allLow = shortPct <= extremeLow && midPct <= extremeLow && longPct <= extremeLow;
				if (s.Status == 0 && allLow)
				{
					if (mode != 2 && q.Close > (decimal)sma.Sma.Value && (decimal)fastSma.Sma.Value > (decimal)sma.Sma.Value)
					{
						s.Status = 1;
						s.EntryPrice = q.Close;
						s.Num = num;
						s.StopLoss = q.Close - atrVal * (decimal)stopAtr;
						s.TakeProfit = q.Close + atrVal * (decimal)profitAtr;
						Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
					}
					else if (mode != 1 && q.Close < (decimal)sma.Sma.Value && (decimal)fastSma.Sma.Value < (decimal)sma.Sma.Value)
					{
						s.Status = 2;
						s.EntryPrice = q.Close;
						s.Num = num;
						s.StopLoss = q.Close + atrVal * (decimal)stopAtr;
						s.TakeProfit = q.Close - atrVal * (decimal)profitAtr;
						Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
					}
				}
			}
			else if (s.Status == 1)
			{
				if (q.Close <= s.StopLoss || q.Close >= s.TakeProfit)
				{
					Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Status = 0;
					s.Num = 0;
				}
			}
			else if (s.Status == 2)
			{
				if (q.Close >= s.StopLoss || q.Close <= s.TakeProfit)
				{
					Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Status = 0;
					s.Num = 0;
				}
			}
		}
	}
}
