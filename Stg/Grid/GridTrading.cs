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
	public class GridTrading : StgBase
	{
		public GridTrading()
		{
		}

		public GridTrading(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();
			// 交易方向：1做多网格（逢跌买入/逢涨卖出） 2做空网格（逢涨卖空/逢跌买回）
			sd.ArgDic["mode"] = 1;
			// 网格基准价格（0表示使用第一个K线收盘价作为基准）
			sd.ArgDic["basePrice"] = 0m;
			// 网格间距百分比
			sd.ArgDic["gridPercent"] = 2.0m;
			// 网格数量（上下各多少格）
			sd.ArgDic["gridCount"] = 3;
			// 发单模式
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			// 是否启用动态网格（默认启用：根据Common.GridSizingHelper按波动率调整网格间距）
			sd.ArgDic["dynamicGrid"] = 1;
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["atrMultiplier"] = 1.2m;
			sd.ArgDic["minGridPercent"] = 0.2m;
			sd.ArgDic["maxGridPercent"] = 5.0m;

			// 止损参数
			sd.ArgDic["useStopLoss"] = 1;
			sd.ArgDic["stopLossPercent"] = 10.0m;
			sd.ArgDic["stopCooldownBars"] = 5;

			// 网格重置参数（网格随时间变动）
			sd.ArgDic["autoRecenter"] = 1;
			sd.ArgDic["recenterBars"] = 20;

			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "交易方向控制", Options = "1:做多网格|2:做空网格", Type = "select" };
			sd.ArgDescDic["basePrice"] = new ArgDesc() { Text = "基准价格", Explain = "0表示使用第一个K线收盘价", Type = "number" };
			sd.ArgDescDic["gridPercent"] = new ArgDesc() { Text = "基准网格间距%", Explain = "动态网格关闭或历史不足时使用的兜底网格间距", Type = "number" };
			sd.ArgDescDic["gridCount"] = new ArgDesc() { Text = "网格数量", Explain = "上下各多少格", Type = "number" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
			sd.ArgDescDic["dynamicGrid"] = new ArgDesc() { Text = "动态网格", Explain = "默认启用；调用Common.GridSizingHelper，基于ATR均值和真实波幅中位数计算网格大小，自动适配所选K线周期和品种波动", Options = "0:关闭|1:启用动态波动率网格", Type = "bool" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "动态网格使用的波动统计周期", Type = "number" };
			sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "ATR倍率", Explain = "动态网格=波动率×倍率，并结合真实波幅中位数降低极端K线影响", Type = "number" };
			sd.ArgDescDic["minGridPercent"] = new ArgDesc() { Text = "最小网格%", Explain = "动态网格下限，避免低波动时网格过密", Type = "number" };
			sd.ArgDescDic["maxGridPercent"] = new ArgDesc() { Text = "最大网格%", Explain = "动态网格上限，避免极端波动后网格过宽", Type = "number" };
			sd.ArgDescDic["useStopLoss"] = new ArgDesc() { Text = "启用止损", Explain = "触及止损价自动平仓", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "价格偏离基准超过此百分比时全部止损", Type = "number" };
			sd.ArgDescDic["stopCooldownBars"] = new ArgDesc() { Text = "止损重入保护", Explain = "止损后至少等待N根K线，且价格必须回到旧止损带内才允许重建网格（0表示止损后永不重入）", Type = "number" };
			sd.ArgDescDic["autoRecenter"] = new ArgDesc() { Text = "自动重置网格", Explain = "定期以当前价格为中心重建", Options = "0:关闭|1:启用，每隔N根K线以当前价格为中心重建网格", Type = "bool" };
			sd.ArgDescDic["recenterBars"] = new ArgDesc() { Text = "重置周期", Explain = "每隔多少根K线重新以当前价格为中心重建网格", Type = "number" };


			sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };


			sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			sd.ColorDic["sub0-GridPercent"] = "#FF9800";
			sd.ColorDic["sub0-Position"] = "#2196F3";
			return sd;
		}

		private class GridLevel
		{
			public decimal Price { get; set; }
			public int Level { get; set; }
			public bool IsBought { get; set; }
		}

		private class State
		{
			public decimal BasePrice { get; set; }
			public bool IsInitialized { get; set; }
			public int CurrentLevel { get; set; }
			public decimal TotalPosition { get; set; }
			public List<GridLevel> GridLevels { get; set; } = new List<GridLevel>();
			public decimal LastGridPercent { get; set; }
			public bool IsStopped { get; set; }
			public decimal StoppedBasePrice { get; set; }
			public int CooldownRemaining { get; set; }
			public int BarsSinceRecenter { get; set; }
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		private State GetOrCreateState(string stateKey)
		{
			if (!_stateDic.ContainsKey(stateKey))
			{
				_stateDic[stateKey] = new State();
			}
			return _stateDic[stateKey];
		}

		private void InitializeGrid(State s, decimal basePrice, decimal gridPercent, int gridCount)
		{
			s.BasePrice = basePrice;
			s.LastGridPercent = gridPercent;
			s.GridLevels.Clear();
			s.CurrentLevel = 0;
			s.TotalPosition = 0;

			// 创建网格层级：负数为下方网格（买入区），正数为上方网格（卖出区）
			for (int i = -gridCount; i <= gridCount; i++)
			{
				var level = new GridLevel
				{
					Level = i,
					Price = basePrice * (1 + i * gridPercent / 100m),
					IsBought = false
				};
				s.GridLevels.Add(level);
			}

			s.IsInitialized = true;
		}

		private decimal CalculateLots(TableUnit tu, decimal currentPrice)
		{
			var num = Convert.ToDecimal(ArgDic["lots"]);
			var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
			if (lotsMode == 1)
			{
				var sym = GetSymbol(tu.MktSymbol);
				num = (Convert.ToDecimal(ArgDic["money"]) / (currentPrice * sym.multiplier * sym.margin_ratio));
				if (sym.symbol_type == (int)SymbolType.COIN)
				{
					num = (int)(num * sym.scale) / (decimal)sym.scale;
				}
				else
				{
					num = (int)num;
				}
			}
			return num;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);

			if (!isFinal) return;
			if (tu.QuoteList.Count < 2) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);

			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			var q2 = tu.QuoteList[tu.QuoteList.Count - 2];

			int mode = ArgDic.ContainsKey("mode") ? Convert.ToInt32(ArgDic["mode"]) : 1;
			decimal gridPercent = Convert.ToDecimal(ArgDic["gridPercent"]);
			int gridCount = Convert.ToInt32(ArgDic["gridCount"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
			int dynamicGrid = Convert.ToInt32(ArgDic["dynamicGrid"]);
			int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
			int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
			decimal stopLossPercent = Convert.ToDecimal(ArgDic["stopLossPercent"]);
			int autoRecenter = Convert.ToInt32(ArgDic["autoRecenter"]);
			int recenterBars = Convert.ToInt32(ArgDic["recenterBars"]);

				// 动态网格：默认使用Common.GridSizingHelper按当前周期波动率计算网格间距
				if (dynamicGrid == 1)
				{
					decimal atrMultiplier = ArgDic.ContainsKey("atrMultiplier") ? Convert.ToDecimal(ArgDic["atrMultiplier"]) : 1.2m;
					decimal minGridPercent = ArgDic.ContainsKey("minGridPercent") ? Convert.ToDecimal(ArgDic["minGridPercent"]) : 0.2m;
					decimal maxGridPercent = ArgDic.ContainsKey("maxGridPercent") ? Convert.ToDecimal(ArgDic["maxGridPercent"]) : 5.0m;
					gridPercent = GridSizingHelper.CalculateDynamicGridPercent(tu.QuoteList, q.Close, gridPercent, atrPeriod, atrMultiplier, minGridPercent, maxGridPercent, s.LastGridPercent);
					Plot("sub0", "GridPercent", PlotType.LINE, (double)gridPercent);
				}

			// 止损冷却期处理：不能仅靠时间重启，必须先回到旧基准价的止损带内
			int stopCooldownBars = Convert.ToInt32(ArgDic["stopCooldownBars"]);
			if (s.IsStopped)
			{
				if (stopCooldownBars <= 0) return; // 0表示永不重入
				if (s.StoppedBasePrice <= 0) s.StoppedBasePrice = s.BasePrice;
				if (s.StoppedBasePrice > 0)
				{
					decimal resetDeviation = Math.Abs(q.Close - s.StoppedBasePrice) / s.StoppedBasePrice * 100m;
					if (resetDeviation >= stopLossPercent) return;
				}
				s.CooldownRemaining--;
				if (s.CooldownRemaining > 0) return;
				// 信号已回到旧止损带内且冷却结束，才以当前价重建网格
				s.IsStopped = false;
				s.StoppedBasePrice = 0;
				InitializeGrid(s, q.Close, gridPercent, gridCount);
				s.BarsSinceRecenter = 0;
			}

			// 初始化网格
			if (!s.IsInitialized)
			{
				decimal basePrice = Convert.ToDecimal(ArgDic["basePrice"]);
				if (basePrice <= 0)
				{
					basePrice = q.Close;
				}
				InitializeGrid(s, basePrice, gridPercent, gridCount);
			}

			// 如果网格间距变化超过20%，重新初始化网格
			if (dynamicGrid == 1 && s.LastGridPercent > 0)
			{
				decimal percentChange = Math.Abs(gridPercent - s.LastGridPercent) / s.LastGridPercent;
				if (percentChange > 0.2m)
				{
					InitializeGrid(s, q.Close, gridPercent, gridCount);
				}
			}

			// 止损检查：价格偏离基准超过止损百分比，全部平仓
			if (useStopLoss == 1 && s.TotalPosition != 0)
			{
				decimal deviation = Math.Abs(q.Close - s.BasePrice) / s.BasePrice * 100m;
				if (deviation >= stopLossPercent)
				{
					if (s.TotalPosition > 0)
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.TotalPosition, period, sendMode);
					else
						Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, Math.Abs(s.TotalPosition), period, sendMode);
					s.TotalPosition = 0;
					s.IsStopped = true;
					s.StoppedBasePrice = s.BasePrice;
					s.CooldownRemaining = Convert.ToInt32(ArgDic["stopCooldownBars"]);
					foreach (var gl in s.GridLevels) gl.IsBought = false;
					return;
				}
			}

			// 网格自动重置：每隔N根K线以当前价格为中心重建网格
			if (autoRecenter == 1 && recenterBars > 0)
			{
				s.BarsSinceRecenter++;
				if (s.BarsSinceRecenter >= recenterBars)
				{
					// 先平掉所有持仓
					if (s.TotalPosition > 0)
					{
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.TotalPosition, period, sendMode);
					}
					else if (s.TotalPosition < 0)
					{
						Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, Math.Abs(s.TotalPosition), period, sendMode);
					}
					// 重建网格
					InitializeGrid(s, q.Close, gridPercent, gridCount);
					s.BarsSinceRecenter = 0;
				}
			}

			// 绘制网格线
			foreach (var gl in s.GridLevels)
			{
				string lineName = gl.Level >= 0 ? $"Grid_U{gl.Level}" : $"Grid_D{Math.Abs(gl.Level)}";
				Plot("main", lineName, PlotType.LINE, (double)gl.Price);
			}

			// 绘制基准价格
			Plot("main", "BasePrice", PlotType.LINE, (double)s.BasePrice);

			decimal currentPrice = q.Close;
			decimal num = CalculateLots(tu, currentPrice);

			// 检查价格穿越网格
			if (mode != 2)
			{
				// 做多网格：逢跌买入，逢涨平仓
				foreach (var gl in s.GridLevels.OrderBy(x => x.Level))
				{
					// 价格从上向下穿越网格线 - 买入
					if (!gl.IsBought && q2.Close > gl.Price && q.Close <= gl.Price)
					{
						Trade(tu.MktSymbol, OrderType.BUY, currentPrice, num, period, sendMode);
						gl.IsBought = true;
						s.TotalPosition += num;
						s.CurrentLevel = gl.Level;
					}
					// 价格从下向上穿越网格线 - 平掉下方最高已买入网格的仓位
					else if (q2.Close < gl.Price && q.Close >= gl.Price && s.TotalPosition > 0)
					{
						var highestBoughtBelow = s.GridLevels
							.Where(x => x.IsBought && x.Level < gl.Level)
							.OrderByDescending(x => x.Level)
							.FirstOrDefault();

						if (highestBoughtBelow != null)
						{
							var sellNum = Math.Min(num, s.TotalPosition);
							Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, currentPrice, sellNum, period, sendMode);
							highestBoughtBelow.IsBought = false;
							s.TotalPosition -= sellNum;
							s.CurrentLevel = gl.Level;
						}
					}
				}
			}
			else
			{
				// 做空网格：逢涨卖空，逢跌买回（IsBought 此处表示该格已卖空）
				foreach (var gl in s.GridLevels.OrderByDescending(x => x.Level))
				{
					// 价格从下向上穿越网格线 - 卖空
					if (!gl.IsBought && q2.Close < gl.Price && q.Close >= gl.Price)
					{
						Trade(tu.MktSymbol, OrderType.SELL, currentPrice, num, period, sendMode);
						gl.IsBought = true;
						s.TotalPosition -= num;
						s.CurrentLevel = gl.Level;
					}
					// 价格从上向下穿越网格线 - 平掉上方最低已卖空网格的仓位
					else if (q2.Close > gl.Price && q.Close <= gl.Price && s.TotalPosition < 0)
					{
						var lowestSoldAbove = s.GridLevels
							.Where(x => x.IsBought && x.Level > gl.Level)
							.OrderBy(x => x.Level)
							.FirstOrDefault();

						if (lowestSoldAbove != null)
						{
							var coverNum = Math.Min(num, Math.Abs(s.TotalPosition));
							Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, currentPrice, coverNum, period, sendMode);
							lowestSoldAbove.IsBought = false;
							s.TotalPosition += coverNum;
							s.CurrentLevel = gl.Level;
						}
					}
				}
			}

			// 绘制当前持仓
			Plot("sub0", "Position", PlotType.LINE, (double)s.TotalPosition);
		}
	}
}
