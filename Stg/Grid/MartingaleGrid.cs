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
	/// 马丁格尔网格策略 (Martingale Grid Strategy)
	/// 
	/// 策略原理：
	/// 经典马丁格尔思想与网格交易结合。价格每下跌一个网格间距就加仓，
	/// 且每层加仓手数按倍数递增，当价格反弹至成本线以上时获利平仓。
	/// 
	/// 核心逻辑：
	/// 1. 以首次入场价为基准，向下每隔一个网格间距加仓
	/// 2. 每层手数 = 基础手数 * 倍率^层数 (倍率可配置，经典马丁为2)
	/// 3. 实时计算持仓均价，价格回到均价+止盈间距时全部平仓
	/// 4. 设置最大加仓层数和总体止损保护，避免无限加仓
	/// 5. 支持做多/做空两种方向
	/// </summary>
	public class MartingaleGrid : StgBase
	{
		public MartingaleGrid()
		{
		}

		public MartingaleGrid(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// 网格参数
			sd.ArgDic["gridPercent"] = 2.0m;
			sd.ArgDic["maxLayers"] = 5;
			sd.ArgDic["multiplier"] = 1.5;

			// 止盈止损
			sd.ArgDic["takeProfitPercent"] = 1.5m;
			sd.ArgDic["totalStopLossPercent"] = 15.0m;

			// 方向
			sd.ArgDic["direction"] = 0;
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			// 冷却
			sd.ArgDic["cooldownBars"] = 0;

			sd.ArgDescDic["gridPercent"] = new ArgDesc() { Text = "网格间距%", Explain = "每层加仓的价格间距百分比" };
			sd.ArgDescDic["maxLayers"] = new ArgDesc() { Text = "最大加仓层数", Explain = "最多加仓次数，防止无限加仓" };
			sd.ArgDescDic["multiplier"] = new ArgDesc() { Text = "手数倍率", Explain = "每层手数=基础手数*倍率^层数，经典马丁为2" };
			sd.ArgDescDic["takeProfitPercent"] = new ArgDesc() { Text = "止盈百分比", Explain = "价格高于持仓均价此百分比时止盈" };
			sd.ArgDescDic["totalStopLossPercent"] = new ArgDesc() { Text = "总体止损%", Explain = "价格偏离首次入场价超过此百分比时全部止损" };
			sd.ArgDescDic["direction"] = new ArgDesc() { Text = "方向", Explain = "0 做多(逢跌加仓) 1 做空(逢涨加仓)" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
			sd.ArgDescDic["cooldownBars"] = new ArgDesc() { Text = "冷却K线数", Explain = "止损后等待N根K线再重新入场，0为不冷却" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			sd.ColorDic["main-AvgPrice"] = "#FF9800";
			sd.ColorDic["main-TPLine"] = "#4CAF50";
			sd.ColorDic["main-SLLine"] = "#F44336";
			sd.ColorDic["sub0-TotalLots"] = "#2196F3";
			return sd;
		}

		private class Layer
		{
			public decimal Price { get; set; }
			public decimal Lots { get; set; }
		}

		private class State
		{
			public bool InPosition { get; set; }
			public List<Layer> Layers { get; set; } = new List<Layer>();
			public decimal TotalLots { get; set; }
			public decimal TotalCost { get; set; }
			public decimal AvgPrice { get; set; }
			public decimal FirstEntryPrice { get; set; }
			public decimal NextGridPrice { get; set; }
			public int CooldownRemaining { get; set; }
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		private State GetOrCreateState(string key)
		{
			if (!_stateDic.ContainsKey(key))
				_stateDic[key] = new State();
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
				return Math.Max(num, 1m);
			}
			return (decimal)ArgDic["lots"];
		}

		private void AddLayer(State s, decimal price, decimal lots)
		{
			s.Layers.Add(new Layer { Price = price, Lots = lots });
			s.TotalLots += lots;
			s.TotalCost += price * lots;
			s.AvgPrice = s.TotalCost / s.TotalLots;
		}

		private void CloseAll(State s)
		{
			s.InPosition = false;
			s.Layers.Clear();
			s.TotalLots = 0;
			s.TotalCost = 0;
			s.AvgPrice = 0;
			s.FirstEntryPrice = 0;
			s.NextGridPrice = 0;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (!isFinal) return;
			if (tu.QuoteList.Count < 2) return;

			var sk = tu.GetStateKey();
			var s = GetOrCreateState(sk);
			var q = tu.QuoteList[tu.QuoteList.Count - 1];

			decimal gridPct = (decimal)ArgDic["gridPercent"] / 100m;
			int maxLayers = (int)ArgDic["maxLayers"];
			double multiplier = (double)ArgDic["multiplier"];
			decimal tpPct = (decimal)ArgDic["takeProfitPercent"] / 100m;
			decimal slPct = (decimal)ArgDic["totalStopLossPercent"] / 100m;
			int direction = (int)ArgDic["direction"];
			int sendMode = (int)ArgDic["sendMode"];
			int cooldownBars = (int)ArgDic["cooldownBars"];

			// 冷却期
			if (s.CooldownRemaining > 0)
			{
				s.CooldownRemaining--;
				return;
			}

			decimal baseLots = CalcBaseLots(tu, q.Close);

			if (!s.InPosition)
			{
				// 首次入场
				decimal lots = baseLots;
				s.InPosition = true;
				s.FirstEntryPrice = q.Close;
				AddLayer(s, q.Close, lots);

				if (direction == 0)
				{
					Trade(tu.MktSymbol, OrderType.BUY, q.Close, lots, period, sendMode);
					s.NextGridPrice = q.Close * (1 - gridPct);
				}
				else
				{
					Trade(tu.MktSymbol, OrderType.SELL, q.Close, lots, period, sendMode);
					s.NextGridPrice = q.Close * (1 + gridPct);
				}
			}
			else
			{
				// 绘制辅助线
				Plot("main", "AvgPrice", PlotType.LINE, (double)s.AvgPrice);
				if (direction == 0)
				{
					Plot("main", "TPLine", PlotType.LINE, (double)(s.AvgPrice * (1 + tpPct)));
					Plot("main", "SLLine", PlotType.LINE, (double)(s.FirstEntryPrice * (1 - slPct)));
				}
				else
				{
					Plot("main", "TPLine", PlotType.LINE, (double)(s.AvgPrice * (1 - tpPct)));
					Plot("main", "SLLine", PlotType.LINE, (double)(s.FirstEntryPrice * (1 + slPct)));
				}
				Plot("sub0", "TotalLots", PlotType.LINE, (double)s.TotalLots);

				if (direction == 0)
				{
					// 做多方向

					// 止盈：价格回到均价以上
					if (q.Close >= s.AvgPrice * (1 + tpPct))
					{
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.TotalLots, period, sendMode);
						CloseAll(s);
						return;
					}

					// 总体止损
					if (q.Close <= s.FirstEntryPrice * (1 - slPct))
					{
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.TotalLots, period, sendMode);
						CloseAll(s);
						s.CooldownRemaining = cooldownBars;
						return;
					}

					// 加仓：价格触及下一层网格
					if (q.Close <= s.NextGridPrice && s.Layers.Count < maxLayers)
					{
						int layerIdx = s.Layers.Count;
						decimal lots = baseLots * (decimal)Math.Pow(multiplier, layerIdx);
						var sym = GetSymbol(tu.MktSymbol);
						if (sym.symbol_type == (int)SymbolType.COIN)
							lots = (int)(lots * 1000) / 1000.0m;
						else
							lots = (int)lots;
						lots = Math.Max(lots, 1m);

						AddLayer(s, q.Close, lots);
						Trade(tu.MktSymbol, OrderType.BUY, q.Close, lots, period, sendMode);
						s.NextGridPrice = q.Close * (1 - gridPct);
					}
				}
				else
				{
					// 做空方向

					// 止盈
					if (q.Close <= s.AvgPrice * (1 - tpPct))
					{
						Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.TotalLots, period, sendMode);
						CloseAll(s);
						return;
					}

					// 总体止损
					if (q.Close >= s.FirstEntryPrice * (1 + slPct))
					{
						Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.TotalLots, period, sendMode);
						CloseAll(s);
						s.CooldownRemaining = cooldownBars;
						return;
					}

					// 加仓
					if (q.Close >= s.NextGridPrice && s.Layers.Count < maxLayers)
					{
						int layerIdx = s.Layers.Count;
						decimal lots = baseLots * (decimal)Math.Pow(multiplier, layerIdx);
						var sym = GetSymbol(tu.MktSymbol);
						if (sym.symbol_type == (int)SymbolType.COIN)
							lots = (int)(lots * 1000) / 1000.0m;
						else
							lots = (int)lots;
						lots = Math.Max(lots, 1m);

						AddLayer(s, q.Close, lots);
						Trade(tu.MktSymbol, OrderType.SELL, q.Close, lots, period, sendMode);
						s.NextGridPrice = q.Close * (1 + gridPct);
					}
				}
			}
		}
	}
}
