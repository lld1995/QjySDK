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
	/// 波动率均值回归策略 (Volatility Mean Reversion Strategy)
	/// 
	/// 策略原理：
	/// 波动率具有均值回归特性——高波动率倾向于回落，低波动率倾向于回升。
	/// 本策略计算历史波动率(HV)的百分位排名，在波动率处于极端位置时交易。
	/// 
	/// 核心逻辑：
	/// 1. 计算N周期历史波动率(HV)，即收益率的标准差
	/// 2. 计算HV在过去M周期中的百分位排名
	/// 3. HV百分位极高(>90%)时，预期波动率回落，价格回归均线 → 逆向交易
	/// 4. HV百分位极低(<10%)时，预期波动率扩张，等待方向突破 → 顺势交易
	/// 5. 结合RSI辅助判断超买超卖方向
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

			// 波动率参数
			sd.ArgDic["hvPeriod"] = 20;
			sd.ArgDic["hvRankPeriod"] = 252;

			// 百分位阈值
			sd.ArgDic["highVolPercentile"] = 90.0;
			sd.ArgDic["lowVolPercentile"] = 10.0;

			// RSI参数
			sd.ArgDic["rsiPeriod"] = 14;
			sd.ArgDic["rsiOverbought"] = 70.0;
			sd.ArgDic["rsiOversold"] = 30.0;

			// 均线回归参数
			sd.ArgDic["smaPeriod"] = 20;

			// 止损止盈
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["stopLossAtr"] = 2.0;
			sd.ArgDic["takeProfitAtr"] = 2.5;

			// 交易模式
			sd.ArgDic["mode"] = 0;
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			sd.ArgDescDic["hvPeriod"] = new ArgDesc() { Text = "HV周期", Explain = "历史波动率计算周期" };
			sd.ArgDescDic["hvRankPeriod"] = new ArgDesc() { Text = "HV排名周期", Explain = "百分位排名的回溯周期" };
			sd.ArgDescDic["highVolPercentile"] = new ArgDesc() { Text = "高波动百分位", Explain = "HV百分位高于此值触发逆向交易" };
			sd.ArgDescDic["lowVolPercentile"] = new ArgDesc() { Text = "低波动百分位", Explain = "HV百分位低于此值等待突破" };
			sd.ArgDescDic["rsiPeriod"] = new ArgDesc() { Text = "RSI周期", Explain = "RSI指标计算周期" };
			sd.ArgDescDic["rsiOverbought"] = new ArgDesc() { Text = "RSI超买", Explain = "RSI超买线" };
			sd.ArgDescDic["rsiOversold"] = new ArgDesc() { Text = "RSI超卖", Explain = "RSI超卖线" };
			sd.ArgDescDic["smaPeriod"] = new ArgDesc() { Text = "均线周期", Explain = "均值回归目标均线周期" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期" };
			sd.ArgDescDic["stopLossAtr"] = new ArgDesc() { Text = "止损ATR倍数", Explain = "止损距离=ATR*此倍数" };
			sd.ArgDescDic["takeProfitAtr"] = new ArgDesc() { Text = "止盈ATR倍数", Explain = "止盈距离=ATR*此倍数" };
			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 双向 1 仅做多 2 仅做空" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 2;

			sd.ColorDic["main-SMA"] = "#2196F3";
			sd.ColorDic["sub0-HV"] = "#FF9800";
			sd.ColorDic["sub0-HighLine"] = "#F44336";
			sd.ColorDic["sub0-LowLine"] = "#4CAF50";
			sd.ColorDic["sub1-RSI"] = "#9C27B0";
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

		/// <summary>
		/// 计算历史波动率 (收益率标准差 * sqrt(252) 年化)
		/// </summary>
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
			double stdDev = Math.Sqrt(sumSq / (returns.Count - 1));
			return stdDev * Math.Sqrt(252);
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			int hvPeriod = (int)ArgDic["hvPeriod"];
			int hvRankPeriod = (int)ArgDic["hvRankPeriod"];
			int rsiPeriod = (int)ArgDic["rsiPeriod"];
			int smaPeriod = (int)ArgDic["smaPeriod"];
			int atrPeriod = (int)ArgDic["atrPeriod"];
			int minBars = Math.Max(hvPeriod + hvRankPeriod, Math.Max(smaPeriod, Math.Max(rsiPeriod, atrPeriod))) + 5;
			if (tu.QuoteList.Count < minBars) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];

			double highVolPct = (double)ArgDic["highVolPercentile"];
			double lowVolPct = (double)ArgDic["lowVolPercentile"];
			double rsiOB = (double)ArgDic["rsiOverbought"];
			double rsiOS = (double)ArgDic["rsiOversold"];
			double stopAtr = (double)ArgDic["stopLossAtr"];
			double profitAtr = (double)ArgDic["takeProfitAtr"];
			int mode = (int)ArgDic["mode"];
			int sendMode = (int)ArgDic["sendMode"];

			// 计算当前HV
			int lastIdx = tu.QuoteList.Count - 1;
			double currentHV = CalcHV(tu.QuoteList, lastIdx, hvPeriod);

			// 计算HV百分位排名
			var hvHistory = new List<double>();
			for (int i = lastIdx - hvRankPeriod; i <= lastIdx; i++)
			{
				if (i >= hvPeriod)
				{
					double hv = CalcHV(tu.QuoteList, i, hvPeriod);
					hvHistory.Add(hv);
				}
			}

			double percentile = 50.0;
			if (hvHistory.Count > 0)
			{
				int belowCount = hvHistory.Count(h => h <= currentHV);
				percentile = (double)belowCount / hvHistory.Count * 100.0;
			}

			// 计算RSI
			var rsiList = tu.QuoteList.GetRsi(rsiPeriod).ToList();
			var rsi = rsiList[rsiList.Count - 1];

			// 计算SMA
			var smaList = tu.QuoteList.GetSma(smaPeriod).ToList();
			var sma = smaList[smaList.Count - 1];

			// 计算ATR
			var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
			var atr = atrList[atrList.Count - 1];

			if (!rsi.Rsi.HasValue || !sma.Sma.HasValue || !atr.Atr.HasValue) return;

			// 计算百分位对应的阈值线
			var sortedHV = hvHistory.OrderBy(x => x).ToList();
			double highThreshold = sortedHV.Count > 0 ? sortedHV[(int)(sortedHV.Count * highVolPct / 100.0)] : 0;
			double lowThreshold = sortedHV.Count > 0 ? sortedHV[Math.Max(0, (int)(sortedHV.Count * lowVolPct / 100.0))] : 0;

			// 绘图
			Plot("main", "SMA", PlotType.LINE, sma.Sma);
			Plot("sub0", "HV", PlotType.LINE, currentHV * 100);
			Plot("sub0", "HighLine", PlotType.LINE, highThreshold * 100);
			Plot("sub0", "LowLine", PlotType.LINE, lowThreshold * 100);
			Plot("sub1", "RSI", PlotType.LINE, rsi.Rsi);

			decimal num = CalcNum(tu, q.Close);
			decimal atrVal = (decimal)atr.Atr.Value;

			if (s.Status == 0)
			{
				// 高波动率 + RSI极端 → 逆向均值回归交易
				if (percentile >= highVolPct)
				{
					if (mode != 2 && rsi.Rsi.Value <= rsiOS && q.Close < (decimal)sma.Sma.Value)
					{
						// RSI超卖 + 价格低于均线 → 做多回归
						s.Status = 1;
						s.EntryPrice = q.Close;
						s.Num = num;
						s.StopLoss = q.Close - atrVal * (decimal)stopAtr;
						s.TakeProfit = (decimal)sma.Sma.Value; // 目标回归到均线
						Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
					}
					else if (mode != 1 && rsi.Rsi.Value >= rsiOB && q.Close > (decimal)sma.Sma.Value)
					{
						// RSI超买 + 价格高于均线 → 做空回归
						s.Status = 2;
						s.EntryPrice = q.Close;
						s.Num = num;
						s.StopLoss = q.Close + atrVal * (decimal)stopAtr;
						s.TakeProfit = (decimal)sma.Sma.Value;
						Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
					}
				}
				// 低波动率 + 价格突破 → 顺势突破交易
				else if (percentile <= lowVolPct)
				{
					if (mode != 2 && q.Close > (decimal)sma.Sma.Value && rsi.Rsi.Value > 50)
					{
						s.Status = 1;
						s.EntryPrice = q.Close;
						s.Num = num;
						s.StopLoss = q.Close - atrVal * (decimal)stopAtr;
						s.TakeProfit = q.Close + atrVal * (decimal)profitAtr;
						Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
					}
					else if (mode != 1 && q.Close < (decimal)sma.Sma.Value && rsi.Rsi.Value < 50)
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
