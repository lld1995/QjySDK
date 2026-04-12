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

			// 是否启用动态网格（根据ATR调整网格间距）
			sd.ArgDic["dynamicGrid"] = 0;
			sd.ArgDic["atrPeriod"] = 14;

			// 止损参数
			sd.ArgDic["useStopLoss"] = 1;
			sd.ArgDic["stopLossPercent"] = 10.0m;

			// 网格重置参数（网格随时间变动）
			sd.ArgDic["autoRecenter"] = 1;
			sd.ArgDic["recenterBars"] = 20;

			sd.ArgDescDic["basePrice"] = new ArgDesc() { Text = "基准价格", Explain = "0表示使用第一个K线收盘价", Type = "number" };
			sd.ArgDescDic["gridPercent"] = new ArgDesc() { Text = "网格间距%", Explain = "每格价格变动百分比", Type = "number" };
			sd.ArgDescDic["gridCount"] = new ArgDesc() { Text = "网格数量", Explain = "上下各多少格", Type = "number" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
			sd.ArgDescDic["dynamicGrid"] = new ArgDesc() { Text = "动态网格", Explain = "根据ATR自动调整网格间距", Options = "0:关闭|1:启用ATR动态调整", Type = "bool" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "动态网格使用的ATR周期", Type = "number" };
			sd.ArgDescDic["useStopLoss"] = new ArgDesc() { Text = "启用止损", Explain = "触及止损价自动平仓", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "价格偏离基准超过此百分比时全部止损", Type = "number" };
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
				var symbol = GetSymbol(tu.MktSymbol);
				num = (Convert.ToDecimal(ArgDic["money"]) / (currentPrice * symbol.multiplier * symbol.margin_ratio));
				if (symbol.symbol_type == (int)SymbolType.COIN)
				{
					num = (int)(num * 1000) / 1000.0m;
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

			decimal gridPercent = Convert.ToDecimal(ArgDic["gridPercent"]);
			int gridCount = Convert.ToInt32(ArgDic["gridCount"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
			int dynamicGrid = Convert.ToInt32(ArgDic["dynamicGrid"]);
			int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
			int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
			decimal stopLossPercent = Convert.ToDecimal(ArgDic["stopLossPercent"]);
			int autoRecenter = Convert.ToInt32(ArgDic["autoRecenter"]);
			int recenterBars = Convert.ToInt32(ArgDic["recenterBars"]);

			// 动态网格：使用ATR计算网格间距
			if (dynamicGrid == 1 && tu.QuoteList.Count >= atrPeriod)
			{
				var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();
				var atr = atrList[atrList.Count - 1];
				if (atr.Atr.HasValue)
				{
					// ATR占价格的百分比作为网格间距
					gridPercent = (decimal)(atr.Atr.Value / (double)q.Close * 100);
					gridPercent = Math.Max(0.5m, Math.Min(5m, gridPercent)); // 限制在0.5%-5%之间
					Plot("sub0", "GridPercent", PlotType.LINE, (double)gridPercent);
				}
			}

			// 止损后停止交易
			if (s.IsStopped) return;

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
			if (useStopLoss == 1 && s.TotalPosition > 0)
			{
				decimal deviation = Math.Abs(q.Close - s.BasePrice) / s.BasePrice * 100m;
				if (deviation >= stopLossPercent)
				{
					Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.TotalPosition, period, sendMode);
					s.TotalPosition = 0;
					s.IsStopped = true;
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

			// 绘制当前持仓
			Plot("sub0", "Position", PlotType.LINE, (double)s.TotalPosition);
		}
	}
}
