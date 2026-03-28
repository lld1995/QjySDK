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
	/// VWAP均值回归策略 (VWAP Mean Reversion Strategy)
	/// 
	/// 核心逻辑：
	/// 结合成交量与价格，计算滚动VWAP（成交量加权平均价）加上标准差轨。
	/// 因为大型机构的算法单大多以 VWAP 为基准执行，当价格偏离 VWAP 过大时，往往会有强烈的均值回归动力。
	/// 此策略在日内短线、波段交易中拥有极高胜率。
	/// 
	/// 入场条件：
	/// 价格跌破下轨 -> 做多
	/// 价格突破上轨 -> 做空
	/// </summary>
	public class VWAPMeanReversion : StgBase
	{
		public VWAPMeanReversion()
		{
		}

		public VWAPMeanReversion(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			sd.ArgDic["lookback"] = 50;
			sd.ArgDic["stdDevMultiplier"] = 2.0;

			// 方向与发单
			sd.ArgDic["mode"] = 0; // 0:双向 1:仅做多 2:仅做空
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			// 止损
			sd.ArgDic["stopLossPercent"] = 3.0m;

			sd.ArgDescDic["lookback"] = new ArgDesc() { Text = "回溯周期", Explain = "计算VWAP和标准差的K线周期" };
			sd.ArgDescDic["stdDevMultiplier"] = new ArgDesc() { Text = "标准差倍数", Explain = "VWAP偏离的标准差倍数，用于判断均值回归触发线" };
			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "方向", Explain = "0 双向 1 仅做多 2 仅做空" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
			sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "硬止损百分比%", Explain = "避免极端单边行情不回头" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 0;

			sd.ColorDic["main-VWAP"] = "#FF9800";
			sd.ColorDic["main-UpperBand"] = "#F44336";
			sd.ColorDic["main-LowerBand"] = "#4CAF50";
			return sd;
		}

		private class TradeState
		{
			public int Status { get; set; } // 0: None, 1: Long, 2: Short
			public decimal EntryPrice { get; set; }
			public decimal TotalLots { get; set; }
		}

		private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();

		private TradeState GetOrCreateState(string key)
		{
			if (!_stateDic.ContainsKey(key))
				_stateDic[key] = new TradeState();
			return _stateDic[key];
		}

		private decimal CalcBaseLots(TableUnit tu, decimal price)
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
				return Math.Max(num, 0.001m);
			}
			return (decimal)ArgDic["lots"];
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			int lookback = (int)ArgDic["lookback"];
			if (tu.QuoteList.Count < lookback) return;

			var sk = tu.GetStateKey();
			var state = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			
			// Calcular rolling VWAP manually
			double sumVolPrice = 0;
			double sumVol = 0;
			for (int i = tu.QuoteList.Count - lookback; i < tu.QuoteList.Count; i++)
			{
				var bar = tu.QuoteList[i];
				double typicalPrice = (double)(bar.High + bar.Low + bar.Close) / 3.0;
				sumVolPrice += typicalPrice * (double)bar.Volume;
				sumVol += (double)bar.Volume;
			}

			double vwap = sumVol > 0 ? sumVolPrice / sumVol : (double)q.Close;

			// Calculate Standard Deviation from VWAP
			double varianceSum = 0;
			for (int i = tu.QuoteList.Count - lookback; i < tu.QuoteList.Count; i++)
			{
				var bar = tu.QuoteList[i];
				double typicalPrice = (double)(bar.High + bar.Low + bar.Close) / 3.0;
				varianceSum += Math.Pow(typicalPrice - vwap, 2);
			}
			double stdDev = Math.Sqrt(varianceSum / lookback);

			double mult = (double)ArgDic["stdDevMultiplier"];
			double upperBand = vwap + stdDev * mult;
			double lowerBand = vwap - stdDev * mult;

			Plot("main", "VWAP", PlotType.LINE, vwap);
			Plot("main", "UpperBand", PlotType.LINE, upperBand);
			Plot("main", "LowerBand", PlotType.LINE, lowerBand);

			int sendMode = (int)ArgDic["sendMode"];
			decimal slPct = (decimal)ArgDic["stopLossPercent"] / 100m;
			decimal lots = CalcBaseLots(tu, q.Close);

			if (state.Status == 0)
			{
				if ((double)q.Close < lowerBand)
				{
					// Buy to revert to VWAP
					Trade(tu.MktSymbol, OrderType.BUY, q.Close, lots, period, sendMode);
					state.Status = 1;
					state.EntryPrice = q.Close;
					state.TotalLots = lots;
				}
				else if ((double)q.Close > upperBand)
				{
					// Short to revert to VWAP
					Trade(tu.MktSymbol, OrderType.SELL, q.Close, lots, period, sendMode);
					state.Status = 2;
					state.EntryPrice = q.Close;
					state.TotalLots = lots;
				}
			}
			else if (state.Status == 1)
			{
				bool isClose = false;

				// Take profit when reaching VWAP
				if ((double)q.Close >= vwap)
					isClose = true;

				// Hard stop loss
				if (q.Close <= state.EntryPrice * (1 - slPct))
					isClose = true;

				if (isClose)
				{
					Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, state.TotalLots, period, sendMode);
					state.Status = 0;
					state.TotalLots = 0;
					state.EntryPrice = 0;
				}
			}
			else if (state.Status == 2)
			{
				bool isClose = false;

				// Take profit when reaching VWAP
				if ((double)q.Close <= vwap)
					isClose = true;

				// Hard stop loss
				if (q.Close >= state.EntryPrice * (1 + slPct))
					isClose = true;

				if (isClose)
				{
					Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, state.TotalLots, period, sendMode);
					state.Status = 0;
					state.TotalLots = 0;
					state.EntryPrice = 0;
				}
			}
		}
	}
}
