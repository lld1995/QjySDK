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
	/// 标准MACD交易策略
	/// 基于经典MACD指标的金叉死叉交易系统
	/// MACD = 快速EMA - 慢速EMA
	/// Signal = MACD的EMA
	/// Histogram = MACD - Signal
	/// </summary>
	public class MACDStandard : StgBase
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
			public decimal? PrevHistogram { get; set; } // 上一根K线的Histogram值
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		public MACDStandard()
		{
		}

		public MACDStandard(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// MACD参数
			sd.ArgDic["fastPeriod"] = 12;      // 快速EMA周期
			sd.ArgDic["slowPeriod"] = 26;      // 慢速EMA周期
			sd.ArgDic["signalPeriod"] = 9;     // 信号线周期

			// 交易模式
			sd.ArgDic["mode"] = 0;             // 0:双向 1:仅做多 2:仅做空
			sd.ArgDic["sendMode"] = 0;         // 0:立即 1:下个开盘

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;         // 0:固定手数 1:固定金额
			sd.ArgDic["lots"] = 1.0m;          // 固定手数
			sd.ArgDic["money"] = 10000m;       // 固定金额

			// 止损设置
			sd.ArgDic["useStopLoss"] = 0;      // 是否使用止损
			sd.ArgDic["stopLossPercent"] = 5.0m; // 止损百分比

			// 过滤设置
			sd.ArgDic["minBarCount"] = 35;     // 最少K线数（需要足够数据计算MACD）
			sd.ArgDic["useZeroFilter"] = 0;    // 是否使用零轴过滤（1:只在零轴上方做多，零轴下方做空）

			// 图表颜色配置


			sd.ArgDescDic["fastPeriod"] = new ArgDesc() { Text = "快速EMA周期", Explain = "MACD快速EMA计算周期", Type = "number" };

			sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };

			sd.ArgDescDic["minBarCount"] = new ArgDesc() { Text = "最少K线数", Explain = "策略启动所需的最少K线数量", Type = "number" };

			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };

			sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };

			sd.ArgDescDic["signalPeriod"] = new ArgDesc() { Text = "信号线周期", Explain = "MACD信号线周期", Type = "number" };

			sd.ArgDescDic["slowPeriod"] = new ArgDesc() { Text = "慢速EMA周期", Explain = "MACD慢速EMA计算周期", Type = "number" };

			sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "止损触发的价格百分比", Type = "number" };

			sd.ArgDescDic["useStopLoss"] = new ArgDesc() { Text = "启用止损", Explain = "触及止损价自动平仓", Options = "0:关闭|1:启用", Type = "bool" };

			sd.ArgDescDic["useZeroFilter"] = new ArgDesc() { Text = "零轴过滤", Explain = "仅在零轴同侧交易", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ColorDic["macd-macd"] = "#2196F3";           // MACD线颜色（蓝色）
			sd.ColorDic["macd-signal"] = "#FF9800";         // 信号线颜色（橙色）
			sd.ColorDic["macd-histogram"] = "#F6465D;#0ECB81"; // 柱状图颜色（红/绿）

			sd.MidValDic["macd"] = 0;
			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			return sd;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			var quotes = tu.QuoteList;
			if (quotes == null || quotes.Count == 0) return;

			// 获取参数
			int fastPeriod = Convert.ToInt32(ArgDic["fastPeriod"]);
			int slowPeriod = Convert.ToInt32(ArgDic["slowPeriod"]);
			int signalPeriod = Convert.ToInt32(ArgDic["signalPeriod"]);
			int minBarCount = Convert.ToInt32(ArgDic["minBarCount"]);
			int mode = Convert.ToInt32(ArgDic["mode"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
			int useZeroFilter = Convert.ToInt32(ArgDic["useZeroFilter"]);
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

			// 需要至少两根K线的MACD数据来判断交叉
			if (!currMacd.Macd.HasValue || !currMacd.Signal.HasValue)
				return;

			decimal currentMACD = (decimal)currMacd.Macd.Value;
			decimal currentSignal = (decimal)currMacd.Signal.Value;
			decimal currentHistogram = currMacd.Histogram.HasValue ? (decimal)currMacd.Histogram.Value : 0;

			// 获取上一根K线的MACD数据
			decimal? prevMACD = state.PrevMACD;
			decimal? prevSignal = state.PrevSignal;

			// 更新状态中的前值
			state.PrevMACD = currentMACD;
			state.PrevSignal = currentSignal;
			state.PrevHistogram = currentHistogram;

			// 如果没有前值，等待下一根K线
			if (!prevMACD.HasValue || !prevSignal.HasValue)
				return;

			// 计算交叉信号
			bool goldenCross = prevMACD.Value <= prevSignal.Value && currentMACD > currentSignal; // 金叉
			bool deathCross = prevMACD.Value >= prevSignal.Value && currentMACD < currentSignal;  // 死叉

			// 零轴过滤
			if (useZeroFilter == 1)
			{
				if (goldenCross && currentMACD < 0) goldenCross = false;  // 零轴下方不做多
				if (deathCross && currentMACD > 0) deathCross = false;    // 零轴上方不做空
			}

			// 当前价格
			var q = quotes.Last();
			decimal currentPrice = q.Close;

			// 计算交易手数
			decimal lots = CalculateLots(tu, q);

			// 止损检查
			if (useStopLoss == 1 && state.Position != 0)
			{
				decimal stopLossPrice = state.EntryPrice * (1 - (state.Position > 0 ? 1 : -1) * stopLossPercent / 100);
				bool stopLossTriggered = (state.Position > 0 && currentPrice <= stopLossPrice) ||
				                         (state.Position < 0 && currentPrice >= stopLossPrice);
				if (stopLossTriggered)
				{
					// 止损平仓
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
				// 金叉信号
				if (mode != 2) // 非仅做空模式
				{
					// 如果有空仓，先平仓
					if (state.Position < 0)
					{
						Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, state.Num, period, sendMode);
						state.Position = 0;
						state.Num = 0;
					}

					// 开多仓
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
				// 死叉信号
				if (mode != 1) // 非仅做多模式
				{
					// 如果有多仓，先平仓
					if (state.Position > 0)
					{
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, state.Num, period, sendMode);
						state.Position = 0;
						state.Num = 0;
					}

					// 开空仓（双向模式）
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
