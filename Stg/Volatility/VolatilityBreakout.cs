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
	/// 波动率突破策略 (Volatility Breakout Strategy)
	/// 
	/// 策略原理：
	/// 当波动率从低位收缩状态急剧扩张时，往往预示着大行情的开始。
	/// 本策略通过检测布林带宽度(BBW)的Squeeze状态和ATR突破来捕捉波动率扩张行情。
	/// 
	/// 核心逻辑：
	/// 1. Squeeze检测：BBW低于历史N周期最低值的一定倍数时，判定为波动率收缩(Squeeze)
	/// 2. 突破确认：Squeeze结束后，价格突破布林带上/下轨时入场
	/// 3. ATR过滤：当前ATR必须大于前一根K线ATR的一定倍数，确认波动率正在扩张
	/// 4. 动态止损：使用ATR倍数作为跟踪止损
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

			// 布林带参数
			sd.ArgDic["bbPeriod"] = 20;
			sd.ArgDic["bbStdDev"] = 2.0;

			// Squeeze检测参数
			sd.ArgDic["squeezeLookback"] = 60;
			sd.ArgDic["squeezeThreshold"] = 1.2;
			sd.ArgDic["squeezeGraceBars"] = 5;

			// ATR参数
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["atrExpansionRatio"] = 1.1;

			// 止损止盈
			sd.ArgDic["atrStopMultiplier"] = 2.0;
			sd.ArgDic["atrProfitMultiplier"] = 3.0;
			sd.ArgDic["useTrailingStop"] = 1;
			sd.ArgDic["trailingAtrMultiplier"] = 2.5;

			// 交易模式
			sd.ArgDic["mode"] = 0;
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			sd.ArgDescDic["bbPeriod"] = new ArgDesc() { Text = "布林带周期", Explain = "布林带计算周期" };
			sd.ArgDescDic["bbStdDev"] = new ArgDesc() { Text = "布林带标准差", Explain = "布林带标准差倍数" };
			sd.ArgDescDic["squeezeLookback"] = new ArgDesc() { Text = "Squeeze回溯期", Explain = "检测BBW最低值的回溯周期" };
			sd.ArgDescDic["squeezeThreshold"] = new ArgDesc() { Text = "Squeeze阈值", Explain = "BBW低于历史最低值*此倍数判定为Squeeze" };
			sd.ArgDescDic["squeezeGraceBars"] = new ArgDesc() { Text = "Squeeze缓冲K线", Explain = "Squeeze结束后允许多少根K线内触发突破" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "ATR计算周期" };
			sd.ArgDescDic["atrExpansionRatio"] = new ArgDesc() { Text = "ATR扩张比", Explain = "当前ATR/前一ATR超过此值确认扩张" };
			sd.ArgDescDic["atrStopMultiplier"] = new ArgDesc() { Text = "ATR止损倍数", Explain = "止损距离=ATR*此倍数" };
			sd.ArgDescDic["atrProfitMultiplier"] = new ArgDesc() { Text = "ATR止盈倍数", Explain = "止盈距离=ATR*此倍数" };
			sd.ArgDescDic["useTrailingStop"] = new ArgDesc() { Text = "跟踪止损", Explain = "0 关闭 1 启用" };
			sd.ArgDescDic["trailingAtrMultiplier"] = new ArgDesc() { Text = "跟踪止损ATR倍数", Explain = "跟踪止损距离=ATR*此倍数" };
			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 双向 1 仅做多 2 仅做空" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 2;

			sd.ColorDic["main-BB_Upper"] = "#FF9800";
			sd.ColorDic["main-BB_Lower"] = "#FF9800";
			sd.ColorDic["main-BB_Mid"] = "#9E9E9E";
			sd.ColorDic["sub0-BBW"] = "#2196F3";
			sd.ColorDic["sub0-SqueezeLine"] = "#F44336";
			sd.ColorDic["sub1-ATR"] = "#4CAF50";
			return sd;
		}

		private class State
		{
			public int Status { get; set; }
			public decimal EntryPrice { get; set; }
			public decimal StopLoss { get; set; }
			public decimal TakeProfit { get; set; }
			public decimal TrailingStop { get; set; }
			public decimal HighSinceEntry { get; set; }
			public decimal LowSinceEntry { get; set; }
			public bool WasSqueeze { get; set; }
			public int SqueezeEndBars { get; set; }
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

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			int bbPeriod = (int)ArgDic["bbPeriod"];
			int squeezeLookback = (int)ArgDic["squeezeLookback"];
			int atrPeriod = (int)ArgDic["atrPeriod"];
			int minBars = Math.Max(Math.Max(bbPeriod, squeezeLookback), atrPeriod) + 5;
			if (tu.QuoteList.Count < minBars) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);

			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			double bbStdDev = (double)ArgDic["bbStdDev"];
			double squeezeThreshold = (double)ArgDic["squeezeThreshold"];
			double atrExpansionRatio = (double)ArgDic["atrExpansionRatio"];
			double atrStopMult = (double)ArgDic["atrStopMultiplier"];
			double atrProfitMult = (double)ArgDic["atrProfitMultiplier"];
			int useTrailing = (int)ArgDic["useTrailingStop"];
			double trailingMult = (double)ArgDic["trailingAtrMultiplier"];
			int mode = (int)ArgDic["mode"];
			int sendMode = (int)ArgDic["sendMode"];

			// 计算布林带
			var bbList = tu.QuoteList.GetBollingerBands(bbPeriod, bbStdDev).ToList();
			var bb = bbList[bbList.Count - 1];

			// 计算ATR
			var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
			var atr = atrList[atrList.Count - 1];
			var atrPrev = atrList[atrList.Count - 2];

			if (!bb.UpperBand.HasValue || !bb.LowerBand.HasValue || !bb.Sma.HasValue ||
				!atr.Atr.HasValue || !atrPrev.Atr.HasValue) return;

			// 计算BBW (Bollinger Band Width)
			double bbw = (bb.UpperBand.Value - bb.LowerBand.Value) / bb.Sma.Value;

			// 计算历史BBW最低值
			double minBbw = double.MaxValue;
			int startIdx = Math.Max(0, bbList.Count - squeezeLookback);
			for (int i = startIdx; i < bbList.Count - 1; i++)
			{
				if (bbList[i].UpperBand.HasValue && bbList[i].LowerBand.HasValue && bbList[i].Sma.HasValue && bbList[i].Sma.Value > 0)
				{
					double histBbw = (bbList[i].UpperBand.Value - bbList[i].LowerBand.Value) / bbList[i].Sma.Value;
					if (histBbw < minBbw) minBbw = histBbw;
				}
			}

			bool isSqueeze = bbw <= minBbw * squeezeThreshold;
			bool atrExpanding = atr.Atr.Value > atrPrev.Atr.Value * atrExpansionRatio;

			// 绘图
			Plot("main", "BB_Upper", PlotType.LINE, bb.UpperBand);
			Plot("main", "BB_Lower", PlotType.LINE, bb.LowerBand);
			Plot("main", "BB_Mid", PlotType.LINE, bb.Sma);
			Plot("sub0", "BBW", PlotType.LINE, bbw);
			Plot("sub0", "SqueezeLine", PlotType.LINE, minBbw * squeezeThreshold);
			Plot("sub1", "ATR", PlotType.LINE, atr.Atr);

			decimal num = CalcNum(tu, q.Close);
			decimal atrVal = (decimal)atr.Atr.Value;

			int squeezeGraceBars = (int)ArgDic["squeezeGraceBars"];

			if (s.Status == 0)
			{
				// 记录Squeeze状态
				if (isSqueeze)
				{
					s.WasSqueeze = true;
					s.SqueezeEndBars = 0;
				}

				// Squeeze结束后计数
				if (s.WasSqueeze && !isSqueeze)
				{
					s.SqueezeEndBars++;
				}

				// Squeeze结束(缓冲期内) + ATR扩张 + 价格突破布林带
				bool inGrace = s.WasSqueeze && !isSqueeze && s.SqueezeEndBars <= squeezeGraceBars;
				if (inGrace && atrExpanding)
				{
					if (mode != 2 && q.Close > (decimal)bb.UpperBand.Value)
					{
						// 做多
						s.Status = 1;
						s.EntryPrice = q.Close;
						s.Num = num;
						s.StopLoss = q.Close - atrVal * (decimal)atrStopMult;
						s.TakeProfit = q.Close + atrVal * (decimal)atrProfitMult;
						s.HighSinceEntry = q.Close;
						s.TrailingStop = q.Close - atrVal * (decimal)trailingMult;
						Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
						s.WasSqueeze = false;
					}
					else if (mode != 1 && q.Close < (decimal)bb.LowerBand.Value)
					{
						// 做空
						s.Status = 2;
						s.EntryPrice = q.Close;
						s.Num = num;
						s.StopLoss = q.Close + atrVal * (decimal)atrStopMult;
						s.TakeProfit = q.Close - atrVal * (decimal)atrProfitMult;
						s.LowSinceEntry = q.Close;
						s.TrailingStop = q.Close + atrVal * (decimal)trailingMult;
						Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
						s.WasSqueeze = false;
					}
				}

				// 超过缓冲期仍无突破则重置Squeeze状态
				if (s.WasSqueeze && !isSqueeze && s.SqueezeEndBars > squeezeGraceBars)
				{
					s.WasSqueeze = false;
					s.SqueezeEndBars = 0;
				}
			}
			else if (s.Status == 1)
			{
				// 多头持仓管理
				if (q.High > s.HighSinceEntry) s.HighSinceEntry = q.High;

				if (useTrailing == 1)
				{
					decimal newTrailing = s.HighSinceEntry - atrVal * (decimal)trailingMult;
					if (newTrailing > s.TrailingStop) s.TrailingStop = newTrailing;
				}

				bool shouldClose = false;
				if (q.Close <= s.StopLoss) shouldClose = true;
				if (q.Close >= s.TakeProfit) shouldClose = true;
				if (useTrailing == 1 && q.Close <= s.TrailingStop) shouldClose = true;

				if (shouldClose)
				{
					Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Status = 0;
					s.Num = 0;
				}
			}
			else if (s.Status == 2)
			{
				// 空头持仓管理
				if (q.Low < s.LowSinceEntry) s.LowSinceEntry = q.Low;

				if (useTrailing == 1)
				{
					decimal newTrailing = s.LowSinceEntry + atrVal * (decimal)trailingMult;
					if (newTrailing < s.TrailingStop) s.TrailingStop = newTrailing;
				}

				bool shouldClose = false;
				if (q.Close >= s.StopLoss) shouldClose = true;
				if (q.Close <= s.TakeProfit) shouldClose = true;
				if (useTrailing == 1 && q.Close >= s.TrailingStop) shouldClose = true;

				if (shouldClose)
				{
					Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Status = 0;
					s.Num = 0;
				}
			}
		}
	}
}
