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
	/// 趋势网格策略 (Trend Grid Strategy)
	/// 
	/// 策略原理：
	/// 将趋势判断与网格交易结合。通过EMA判断趋势方向，
	/// 上升趋势仅执行做多网格（逢跌买入/逢涨卖出），
	/// 下降趋势仅执行做空网格（逢涨卖出/逢跌买回）。
	/// 
	/// 核心逻辑：
	/// 1. 使用快慢EMA交叉判断趋势方向
	/// 2. 上升趋势：价格每下跌一格买入，每上涨一格卖出，底仓保留
	/// 3. 下降趋势：价格每上涨一格卖出，每下跌一格买回，保持空头底仓
	/// 4. 趋势反转时清仓并切换方向
	/// 5. 支持ATR动态网格间距
	/// </summary>
	public class TrendGrid : StgBase
	{
		public TrendGrid()
		{
		}

		public TrendGrid(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// 交易方向控制
			sd.ArgDic["mode"] = 0;

			// 趋势判断
			sd.ArgDic["fastEmaPeriod"] = 20;
			sd.ArgDic["slowEmaPeriod"] = 60;
			sd.ArgDic["trendGapPercent"] = 1.0;

			// 网格参数
			sd.ArgDic["gridPercent"] = 2.0m;
			sd.ArgDic["gridCount"] = 3;
			sd.ArgDic["sendMode"] = 0;

			// 动态网格
			sd.ArgDic["dynamicGrid"] = 0;
			sd.ArgDic["atrPeriod"] = 14;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			// 止损
			sd.ArgDic["useStopLoss"] = 1;
			sd.ArgDic["stopLossPercent"] = 6.0m;

			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "交易方向控制，被禁用方向的趋势视为中性区（清仓且不建网格）", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
			sd.ArgDescDic["fastEmaPeriod"] = new ArgDesc() { Text = "快EMA周期", Explain = "趋势判断快速均线", Type = "number" };
			sd.ArgDescDic["slowEmaPeriod"] = new ArgDesc() { Text = "慢EMA周期", Explain = "趋势判断慢速均线", Type = "number" };
			sd.ArgDescDic["trendGapPercent"] = new ArgDesc() { Text = "趋势确认间距%", Explain = "快EMA与慢EMA差距超过慢EMA的此百分比才确认趋势，低于则为中性区", Type = "number" };
			sd.ArgDescDic["gridPercent"] = new ArgDesc() { Text = "网格间距%", Explain = "每格价格变动百分比", Type = "number" };
			sd.ArgDescDic["gridCount"] = new ArgDesc() { Text = "网格数量", Explain = "单方向网格层数", Type = "number" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
			sd.ArgDescDic["dynamicGrid"] = new ArgDesc() { Text = "动态网格", Explain = "根据ATR自动调整网格间距", Options = "0:关闭|1:启用ATR动态调整", Type = "bool" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "动态网格使用的ATR周期", Type = "number" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
			sd.ArgDescDic["useStopLoss"] = new ArgDesc() { Text = "启用止损", Explain = "触及止损价自动平仓", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "持仓总亏损超过此百分比时全部平仓", Type = "number" };


			sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };


			sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			sd.ColorDic["main-FastEMA"] = "#FF9800";
			sd.ColorDic["main-SlowEMA"] = "#2196F3";
			sd.ColorDic["sub0-Position"] = "#4CAF50";
			return sd;
		}

		private class GridLevel
		{
			public decimal Price { get; set; }
			public int Level { get; set; }
			public bool Filled { get; set; }
		}

		private class State
		{
			public int Trend { get; set; } // 0=无 1=上升 2=下降
			public decimal BasePrice { get; set; }
			public List<GridLevel> Grids { get; set; } = new List<GridLevel>();
			public decimal TotalPosition { get; set; }
			public bool Initialized { get; set; }
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		private State GetOrCreateState(string key)
		{
			if (!_stateDic.ContainsKey(key))
				_stateDic[key] = new State();
			return _stateDic[key];
		}

		private decimal CalcLots(TableUnit tu, decimal price)
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

		private void BuildGrid(State s, decimal basePrice, decimal gridPct, int gridCount)
		{
			s.BasePrice = basePrice;
			s.Grids.Clear();
			for (int i = -gridCount; i <= gridCount; i++)
			{
				s.Grids.Add(new GridLevel
				{
					Level = i,
					Price = basePrice * (1 + i * gridPct / 100m),
					Filled = false
				});
			}
		}

		private void CloseAllPositions(State s, TableUnit tu, SkQuote q, Period period, int sendMode)
		{
			if (s.TotalPosition > 0)
			{
				Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.TotalPosition, period, sendMode);
			}
			else if (s.TotalPosition < 0)
			{
				Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, Math.Abs(s.TotalPosition), period, sendMode);
			}
			s.TotalPosition = 0;
			s.Grids.Clear();
			s.Initialized = false;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;

			int fastP = Convert.ToInt32(ArgDic["fastEmaPeriod"]);
			int slowP = Convert.ToInt32(ArgDic["slowEmaPeriod"]);
			int minBars = slowP + 5;
			if (tu.QuoteList.Count < minBars) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			var q2 = tu.QuoteList[tu.QuoteList.Count - 2];

			decimal gridPct = Convert.ToDecimal(ArgDic["gridPercent"]);
			int gridCount = Convert.ToInt32(ArgDic["gridCount"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
			int dynamicGrid = Convert.ToInt32(ArgDic["dynamicGrid"]);
			int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
			int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
			decimal stopLossPct = Convert.ToDecimal(ArgDic["stopLossPercent"]);

			// 动态网格
			if (dynamicGrid == 1 && tu.QuoteList.Count >= atrPeriod)
			{
				var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
				var atr = atrList[atrList.Count - 1];
				if (atr.Atr.HasValue)
				{
					gridPct = (decimal)(atr.Atr.Value / (double)q.Close * 100);
					gridPct = Math.Max(0.5m, Math.Min(5m, gridPct));
				}
			}

			// 趋势判断
			var fastEmaList = tu.QuoteList.GetEma(fastP).ToList();
			var slowEmaList = tu.QuoteList.GetEma(slowP).ToList();
			var fastEma = fastEmaList[fastEmaList.Count - 1];
			var slowEma = slowEmaList[slowEmaList.Count - 1];

			if (!fastEma.Ema.HasValue || !slowEma.Ema.HasValue) return;

			Plot("main", "FastEMA", PlotType.LINE, fastEma.Ema);
			Plot("main", "SlowEMA", PlotType.LINE, slowEma.Ema);

			double trendGapPct = Convert.ToDouble(ArgDic["trendGapPercent"]) / 100.0;
			double emaGap = (fastEma.Ema.Value - slowEma.Ema.Value) / slowEma.Ema.Value;
			int newTrend;
			if (emaGap > trendGapPct) newTrend = 1;       // 明确上升趋势
			else if (emaGap < -trendGapPct) newTrend = 2;  // 明确下降趋势
			else newTrend = 0;                              // 中性区，不交易

			// 方向控制：被禁用方向的趋势按中性区处理（清仓且不建网格）
			int mode = ArgDic.ContainsKey("mode") ? Convert.ToInt32(ArgDic["mode"]) : 0;
			if (mode == 1 && newTrend == 2) newTrend = 0;
			if (mode == 2 && newTrend == 1) newTrend = 0;

			// 趋势反转或进入中性区 → 清仓
			if (s.Trend != 0 && s.Trend != newTrend)
			{
				CloseAllPositions(s, tu, q, period, sendMode);
			}
			s.Trend = newTrend;

			// 中性区不建网格
			if (newTrend == 0) return;

			// 初始化网格
			if (!s.Initialized)
			{
				BuildGrid(s, q.Close, gridPct, gridCount);
				s.Initialized = true;
			}

			decimal num = CalcLots(tu, q.Close);

			// 止损检查
			if (useStopLoss == 1 && s.TotalPosition != 0)
			{
				decimal deviation = Math.Abs(q.Close - s.BasePrice) / s.BasePrice * 100m;
				bool shouldStop = false;
				if (s.Trend == 1 && q.Close < s.BasePrice && deviation >= stopLossPct) shouldStop = true;
				if (s.Trend == 2 && q.Close > s.BasePrice && deviation >= stopLossPct) shouldStop = true;
				if (shouldStop)
				{
					CloseAllPositions(s, tu, q, period, sendMode);
					s.Trend = newTrend;
					return;
				}
			}

			// 绘制网格线
			foreach (var gl in s.Grids)
			{
				string lineName = gl.Level >= 0 ? $"Grid_U{gl.Level}" : $"Grid_D{Math.Abs(gl.Level)}";
				Plot("main", lineName, PlotType.LINE, (double)gl.Price);
			}

			if (s.Trend == 1)
			{
				// 上升趋势：做多网格
				foreach (var gl in s.Grids.OrderBy(x => x.Level))
				{
					// 价格向下穿越 → 买入
					if (!gl.Filled && q2.Close > gl.Price && q.Close <= gl.Price)
					{
						Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
						gl.Filled = true;
						s.TotalPosition += num;
					}
					// 价格向上穿越 → 卖出(平部分仓)
					else if (gl.Filled && gl.Level > s.Grids.Min(x => x.Level) && q2.Close < gl.Price && q.Close >= gl.Price)
					{
						var lower = s.Grids.Where(x => x.Level < gl.Level && x.Filled).OrderByDescending(x => x.Level).FirstOrDefault();
						if (lower != null && s.TotalPosition > num)
						{
							Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, num, period, sendMode);
							lower.Filled = false;
							s.TotalPosition -= num;
						}
					}
				}
			}
			else
			{
				// 下降趋势：做空网格
				foreach (var gl in s.Grids.OrderByDescending(x => x.Level))
				{
					// 价格向上穿越 → 卖空
					if (!gl.Filled && q2.Close < gl.Price && q.Close >= gl.Price)
					{
						Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
						gl.Filled = true;
						s.TotalPosition -= num;
					}
					// 价格向下穿越 → 买回(平部分空仓)
					else if (gl.Filled && gl.Level < s.Grids.Max(x => x.Level) && q2.Close > gl.Price && q.Close <= gl.Price)
					{
						var upper = s.Grids.Where(x => x.Level > gl.Level && x.Filled).OrderBy(x => x.Level).FirstOrDefault();
						if (upper != null && s.TotalPosition < -num)
						{
							Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, num, period, sendMode);
							upper.Filled = false;
							s.TotalPosition += num;
						}
					}
				}
			}

			Plot("sub0", "Position", PlotType.LINE, (double)s.TotalPosition);
		}
	}
}
