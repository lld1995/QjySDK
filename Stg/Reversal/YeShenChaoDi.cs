using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Linq;
using static Model.EnumDef;
using System.Collections.Generic;

namespace QjySDK.Stg
{
	/// <summary>
	/// 叶神抄底策略
	/// 核心逻辑：
	/// 1. RSI进入超卖区域
	/// 2. 价格出现企稳反转信号（收阳线且突破前一根K线高点）
	/// 3. 可选：价格在均线下方时才开仓（更安全的抄底）
	/// 止损：跌破近期最低点或固定比例止损
	/// 止盈：RSI进入超买区域或达到目标盈利比例
	/// </summary>
	public class YeShenChaoDi : StgBase
	{
		public YeShenChaoDi()
		{
		}

		public YeShenChaoDi(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();
			
			// RSI参数
			sd.ArgDic["rsiPeriod"] = 14;
			sd.ArgDic["rsiOversold"] = 30;
			sd.ArgDic["rsiOverbought"] = 70;
			
			// 均线参数
			sd.ArgDic["emaPeriod"] = 20;
			sd.ArgDic["useEmaFilter"] = 0;  // 0: 不使用均线过滤 1: 价格需在均线下方才抄底
			
			// 止损止盈参数
			sd.ArgDic["stopLossRate"] = 0.05m;      // 止损比例 5%
			sd.ArgDic["takeProfitRate"] = 0.10m;   // 止盈比例 10%
			sd.ArgDic["useRsiExit"] = 1;            // 是否使用RSI超买止盈
			
			// 回看周期（用于寻找近期最低点）
			sd.ArgDic["lookbackPeriod"] = 10;
			
			// 加仓参数
			sd.ArgDic["maxAddNum"] = 0;
			sd.ArgDic["addThreshold"] = 0.01m;     // 加仓阈值：价格上涨1%后可加仓
			
			// 模式控制
			sd.ArgDic["mode"] = 0;      // 0:双向 1:仅做多 2:仅做空
			sd.ArgDic["sendMode"] = 0;  // 0: 立即 1: 下个开盘
			
			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			// 参数说明
			sd.ArgDescDic["rsiPeriod"] = new ArgDesc() { Text = "RSI周期", Explain = "RSI指标计算周期", Type = "number" };
			sd.ArgDescDic["rsiOversold"] = new ArgDesc() { Text = "RSI超卖线", Explain = "RSI低于此值视为超卖", Type = "number" };
			sd.ArgDescDic["rsiOverbought"] = new ArgDesc() { Text = "RSI超买线", Explain = "RSI高于此值视为超买，用于止盈", Type = "number" };
			sd.ArgDescDic["emaPeriod"] = new ArgDesc() { Text = "EMA周期", Explain = "均线周期", Type = "number" };
			sd.ArgDescDic["useEmaFilter"] = new ArgDesc() { Text = "使用均线过滤", Explain = "价格需在均线特定侧", Options = "0: 不使用|1: 价格需在均线下方才抄底", Type = "bool" };
			sd.ArgDescDic["stopLossRate"] = new ArgDesc() { Text = "止损比例", Explain = "开仓价下跌此比例后止损", Type = "number" };
			sd.ArgDescDic["takeProfitRate"] = new ArgDesc() { Text = "止盈比例", Explain = "开仓价上涨此比例后止盈", Type = "number" };
			sd.ArgDescDic["useRsiExit"] = new ArgDesc() { Text = "RSI止盈", Explain = "RSI超买/超卖时止盈", Options = "1: RSI超买时止盈|0: 不使用", Type = "bool" };
			sd.ArgDescDic["lookbackPeriod"] = new ArgDesc() { Text = "回看周期", Explain = "寻找近期最低点的周期", Type = "number" };
			sd.ArgDescDic["maxAddNum"] = new ArgDesc() { Text = "最大加仓次数", Explain = "允许的最大加仓次数", Type = "number" };
			sd.ArgDescDic["addThreshold"] = new ArgDesc() { Text = "加仓阈值", Explain = "价格上涨此比例后可加仓", Type = "number" };
			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0: 固定手数|1: 固定金额", Type = "select" };

			sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

			sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };
			
			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			sd.ColorDic["main-ema"] = "#2196F3";
			sd.ColorDic["sub1-rsi"] = "#FF9800";
			sd.ColorDic["sub1-oversold"] = "#4CAF50";
			sd.ColorDic["sub1-overbought"] = "#F44336";
			
			return sd;
		}

		private class State
		{
			public int Status { get; set; }  // 0: 空仓 1: 多头持仓 2: 空头持仓

			public decimal Num { get; set; }

			public decimal EntryPrice { get; set; }

			public int AddNum { get; set; }

			public decimal LastAddPrice { get; set; }

			public decimal RecentLow { get; set; }

			public decimal RecentHigh { get; set; }
			
			public bool WasOversold { get; set; }  // 标记是否曾经进入超卖区
			
			public bool WasOverbought { get; set; }  // 标记是否曾经进入超买区
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		public override void OnSendOrder(TableUnit tu, decimal price)
		{
			base.OnSendOrder(tu, price);
			State s = null;
			var sk = tu.GetStateKey();
			if (_stateDic.ContainsKey(sk))
			{
				s = _stateDic[sk];
			}
			else
			{
				s = new State();
				_stateDic[sk] = s;
			}
			if (s.EntryPrice == 0)
			{
				s.EntryPrice = price;
			}
			s.LastAddPrice = price;
		}

		private void CalcNum(TableUnit tu, SkQuote q, Dictionary<string, object> argDic, ref decimal num)
		{
			var lotsMode = Convert.ToInt32(argDic["lotsMode"]);
			if (lotsMode == 1)
			{
				var sym = GetSymbol(tu.MktSymbol);
				num = (Convert.ToDecimal(ArgDic["money"]) / (q.Close * sym.multiplier * sym.margin_ratio));
				if (sym.symbol_type == (int)SymbolType.COIN)
				{
					num = (int)(num * sym.scale) / (decimal)sym.scale;
				}
				else
				{
					num = (int)num;
				}
			}
			else
			{
				num = Convert.ToDecimal(argDic["lots"]);
			}
		}

		private decimal FindRecentLow(List<SkQuote> quotes, int lookback)
		{
			var low = decimal.MaxValue;
			// 排除当前K线：否则 q.Close < s.RecentLow 永远不可能成立（当前K线 Low <= Close）
			var endIndex = Math.Max(0, quotes.Count - 1);
			var startIndex = Math.Max(0, endIndex - lookback);
			for (int i = startIndex; i < endIndex; i++)
			{
				if (quotes[i].Low < low)
				{
					low = quotes[i].Low;
				}
			}
			return low == decimal.MaxValue ? 0 : low;
		}

		private decimal FindRecentHigh(List<SkQuote> quotes, int lookback)
		{
			var high = decimal.MinValue;
			// 排除当前K线：否则 q.Close > s.RecentHigh 会被当前K线 High >= Close 恒压住
			var endIndex = Math.Max(0, quotes.Count - 1);
			var startIndex = Math.Max(0, endIndex - lookback);
			for (int i = startIndex; i < endIndex; i++)
			{
				if (quotes[i].High > high)
				{
					high = quotes[i].High;
				}
			}
			return high == decimal.MinValue ? 0 : high;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;
			
			State s = null;
			var sk = tu.GetStateKey();
			if (_stateDic.ContainsKey(sk))
			{
				s = _stateDic[sk];
			}
			else
			{
				s = new State();
				_stateDic[sk] = s;
			}

			var rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
			var emaPeriod = Convert.ToInt32(ArgDic["emaPeriod"]);
			var minBars = Math.Max(rsiPeriod, emaPeriod) + 5;
			
			if (tu.QuoteList.Count < minBars) return;

			var rsiOversold = Convert.ToInt32(ArgDic["rsiOversold"]);
			var rsiOverbought = Convert.ToInt32(ArgDic["rsiOverbought"]);
			var useEmaFilter = Convert.ToInt32(ArgDic["useEmaFilter"]);
			var stopLossRate = Convert.ToDecimal(ArgDic["stopLossRate"]);
			var takeProfitRate = Convert.ToDecimal(ArgDic["takeProfitRate"]);
			var useRsiExit = Convert.ToInt32(ArgDic["useRsiExit"]);
			var lookbackPeriod = Convert.ToInt32(ArgDic["lookbackPeriod"]);
			var maxAddNum = Convert.ToInt32(ArgDic["maxAddNum"]);
			var addThreshold = Convert.ToDecimal(ArgDic["addThreshold"]);
			var mode = Convert.ToInt32(ArgDic["mode"]);
			var sendMode = Convert.ToInt32(ArgDic["sendMode"]);

			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			var q1 = tu.QuoteList[tu.QuoteList.Count - 2];

			var num = 1m;
			CalcNum(tu, q, ArgDic, ref num);

			// 计算指标
			var rsiList = tu.QuoteList.GetRsi(rsiPeriod).ToList();
			var rsi = rsiList[rsiList.Count - 1];
			var rsi1 = rsiList[rsiList.Count - 2];

			var emaList = tu.QuoteList.GetEma(emaPeriod).ToList();
			var ema = emaList[emaList.Count - 1];

			// 绘制指标
			Plot("main", "ema", PlotType.LINE, ema.Ema);
			Plot("sub1", "rsi", PlotType.LINE, rsi.Rsi);
			Plot("sub1", "oversold", PlotType.LINE, rsiOversold);
			Plot("sub1", "overbought", PlotType.LINE, rsiOverbought);

			// 更新近期高低点
			s.RecentLow = FindRecentLow(tu.QuoteList, lookbackPeriod);
			s.RecentHigh = FindRecentHigh(tu.QuoteList, lookbackPeriod);

			// 检查RSI状态
			if (rsi.Rsi.HasValue && rsi.Rsi < rsiOversold)
			{
				s.WasOversold = true;
			}
			if (rsi.Rsi.HasValue && rsi.Rsi > rsiOverbought)
			{
				s.WasOverbought = true;
			}

			if (s.Status == 0)
			{
				// 空仓状态 - 寻找抄底机会
				if (rsi.Rsi.HasValue && rsi1.Rsi.HasValue && ema.Ema.HasValue)
				{
					// 抄底条件：
					// 1. 之前RSI曾进入超卖区
					// 2. 当前RSI从超卖区回升（RSI > 前一根RSI 且 前一根RSI < 超卖线）
					// 3. 收阳线（Close > Open）
					// 4. 突破前一根K线高点
					// 5. 可选：价格在均线下方
					
					var isOversoldRecovery = s.WasOversold && rsi.Rsi > rsi1.Rsi && rsi1.Rsi < rsiOversold;
					var isBullishCandle = q.Close > q.Open;
					var isBreakout = q.Close > q1.High;
					var isUnderEma = useEmaFilter == 0 || q.Close < (decimal)ema.Ema;

					if (isOversoldRecovery && isBullishCandle && isBreakout && isUnderEma && mode != 2)
					{
						s.Status = 1;
						s.Num = num;
						s.AddNum = 0;
						s.WasOversold = false;
						Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
					}
					// 做空：双向或仅做空模式
					else if (mode != 1)
					{
						var isOverboughtRecovery = s.WasOverbought && rsi.Rsi < rsi1.Rsi && rsi1.Rsi > rsiOverbought;
						var isBearishCandle = q.Close < q.Open;
						var isBreakdown = q.Close < q1.Low;
						var isAboveEma = useEmaFilter == 0 || q.Close > (decimal)ema.Ema;

						if (isOverboughtRecovery && isBearishCandle && isBreakdown && isAboveEma)
						{
							s.Status = 2;
							s.Num = num;
							s.AddNum = 0;
							s.WasOverbought = false;
							Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
						}
					}
				}
			}
			else if (s.Status == 1)
			{
				// 多头持仓 - 管理仓位
				
				// 加仓逻辑
				if (s.AddNum < maxAddNum && q.Close > s.LastAddPrice * (1 + addThreshold))
				{
					s.Num += num;
					s.AddNum++;
					Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
				}

				// 止损条件
				var stopLossPrice = s.EntryPrice * (1 - stopLossRate);
				var isStopLoss = q.Close < stopLossPrice || q.Close < s.RecentLow;

				// 止盈条件
				var takeProfitPrice = s.EntryPrice * (1 + takeProfitRate);
				var isTakeProfit = q.Close > takeProfitPrice;
				var isRsiExit = useRsiExit == 1 && rsi.Rsi.HasValue && rsi.Rsi > rsiOverbought;

				if (isStopLoss || isTakeProfit || isRsiExit)
				{
					Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Status = 0;
					s.Num = 0;
					s.EntryPrice = 0;
					s.AddNum = 0;
					s.LastAddPrice = 0;
				}
			}
			else if (s.Status == 2)
			{
				// 空头持仓 - 管理仓位（双向模式）
				
				// 加仓逻辑
				if (s.AddNum < maxAddNum && q.Close < s.LastAddPrice * (1 - addThreshold))
				{
					s.Num += num;
					s.AddNum++;
					Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
				}

				// 止损条件
				var stopLossPrice = s.EntryPrice * (1 + stopLossRate);
				var isStopLoss = q.Close > stopLossPrice || q.Close > s.RecentHigh;

				// 止盈条件
				var takeProfitPrice = s.EntryPrice * (1 - takeProfitRate);
				var isTakeProfit = q.Close < takeProfitPrice;
				var isRsiExit = useRsiExit == 1 && rsi.Rsi.HasValue && rsi.Rsi < rsiOversold;

				if (isStopLoss || isTakeProfit || isRsiExit)
				{
					Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Status = 0;
					s.Num = 0;
					s.EntryPrice = 0;
					s.AddNum = 0;
					s.LastAddPrice = 0;
				}
			}
		}
	}
}
