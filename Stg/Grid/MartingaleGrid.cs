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
			sd.ArgDic["maxLayers"] = 3;
			sd.ArgDic["multiplier"] = 1.2;

			// 止盈止损
			sd.ArgDic["takeProfitPercent"] = 1.5m;
			sd.ArgDic["totalStopLossPercent"] = 8.0m;

			// 方向（统一方向控制参数）
			sd.ArgDic["mode"] = 1;
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			// 动态网格（默认启用：根据Common.GridSizingHelper按波动率调整加仓间距）
			sd.ArgDic["dynamicGrid"] = 1;
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["atrMultiplier"] = 1.2m;
			sd.ArgDic["minGridPercent"] = 0.2m;
			sd.ArgDic["maxGridPercent"] = 5.0m;

			// 冷却
			sd.ArgDic["cooldownBars"] = 3;

			sd.ArgDescDic["gridPercent"] = new ArgDesc() { Text = "基准网格间距%", Explain = "动态网格关闭或历史不足时使用的兜底加仓间距", Type = "number" };
			sd.ArgDescDic["maxLayers"] = new ArgDesc() { Text = "最大加仓层数", Explain = "最多加仓次数，防止无限加仓", Type = "number" };
			sd.ArgDescDic["multiplier"] = new ArgDesc() { Text = "手数倍率", Explain = "每层手数=基础手数*倍率^层数，经典马丁为2", Type = "number" };
			sd.ArgDescDic["takeProfitPercent"] = new ArgDesc() { Text = "止盈百分比", Explain = "价格高于持仓均价此百分比时止盈", Type = "number" };
			sd.ArgDescDic["totalStopLossPercent"] = new ArgDesc() { Text = "总体止损%", Explain = "价格偏离首次入场价超过此百分比时全部止损", Type = "number" };
			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "交易方向控制", Options = "1:做多(逢跌加仓)|2:做空(逢涨加仓)", Type = "select" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
			sd.ArgDescDic["dynamicGrid"] = new ArgDesc() { Text = "动态网格", Explain = "默认启用；调用Common.GridSizingHelper，基于ATR均值和真实波幅中位数计算每层加仓间距，自动适配所选K线周期和品种波动", Options = "0:关闭|1:启用动态波动率网格", Type = "bool" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "动态网格使用的波动统计周期", Type = "number" };
			sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "ATR倍率", Explain = "动态网格=波动率×倍率，并结合真实波幅中位数降低极端K线影响", Type = "number" };
			sd.ArgDescDic["minGridPercent"] = new ArgDesc() { Text = "最小网格%", Explain = "动态网格下限，避免低波动时加仓过密", Type = "number" };
			sd.ArgDescDic["maxGridPercent"] = new ArgDesc() { Text = "最大网格%", Explain = "动态网格上限，避免极端波动后加仓间距过宽", Type = "number" };
			sd.ArgDescDic["cooldownBars"] = new ArgDesc() { Text = "冷却K线数", Explain = "止损后等待N根K线再重新入场，0为不冷却", Type = "number" };


			sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };


			sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

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
			public int BlockedDir { get; set; } // 止损后封锁方向:0无 1多 2空
			public decimal StopReferencePrice { get; set; } // 止损轮首次入场价，回到该价才允许重启
			public decimal LastGridPercent { get; set; }
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
			var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
			if (lotsMode == 1)
			{
				var sym = GetSymbol(tu.MktSymbol);
				var num = Convert.ToDecimal(ArgDic["money"]) / (price * sym.multiplier * sym.margin_ratio);
				if (sym.symbol_type == (int)SymbolType.COIN)
					num = (int)(num * 1000) / 1000.0m;
				else
					num = (int)num;
				return Math.Max(num, 0.001m);
			}
			return Convert.ToDecimal(ArgDic["lots"]);
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

			decimal gridPct = Convert.ToDecimal(ArgDic["gridPercent"]);
			int dynamicGrid = ArgDic.ContainsKey("dynamicGrid") ? Convert.ToInt32(ArgDic["dynamicGrid"]) : 1;
			if (dynamicGrid == 1)
			{
				int atrPeriod = ArgDic.ContainsKey("atrPeriod") ? Convert.ToInt32(ArgDic["atrPeriod"]) : 14;
				decimal atrMultiplier = ArgDic.ContainsKey("atrMultiplier") ? Convert.ToDecimal(ArgDic["atrMultiplier"]) : 1.2m;
				decimal minGridPercent = ArgDic.ContainsKey("minGridPercent") ? Convert.ToDecimal(ArgDic["minGridPercent"]) : 0.2m;
				decimal maxGridPercent = ArgDic.ContainsKey("maxGridPercent") ? Convert.ToDecimal(ArgDic["maxGridPercent"]) : 5.0m;
				gridPct = GridSizingHelper.CalculateDynamicGridPercent(tu.QuoteList, q.Close, gridPct, atrPeriod, atrMultiplier, minGridPercent, maxGridPercent, s.LastGridPercent);
				s.LastGridPercent = gridPct;
				Plot("sub0", "GridPercent", PlotType.LINE, (double)gridPct);
			}
			decimal gridPctRatio = gridPct / 100m;
			int maxLayers = Convert.ToInt32(ArgDic["maxLayers"]);
			double multiplier = Convert.ToDouble(ArgDic["multiplier"]);
			decimal tpPct = Convert.ToDecimal(ArgDic["takeProfitPercent"]) / 100m;
			decimal slPct = Convert.ToDecimal(ArgDic["totalStopLossPercent"]) / 100m;
			// 方向：mode 1做多/2做空；兼容旧配置的 direction(0多/1空)
			int mode = 1;
			if (ArgDic.ContainsKey("mode")) mode = Convert.ToInt32(ArgDic["mode"]);
			else if (ArgDic.ContainsKey("direction")) mode = Convert.ToInt32(ArgDic["direction"]) == 1 ? 2 : 1;
			int direction = mode == 2 ? 1 : 0;
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
			int cooldownBars = Convert.ToInt32(ArgDic["cooldownBars"]);

			// 止损后不能仅靠时间重启：同方向必须先回到上一轮首次入场价，证明止损后的单边不利状态已失效
			if (s.BlockedDir == 1 && q.Close >= s.StopReferencePrice)
			{
				s.BlockedDir = 0;
				s.StopReferencePrice = 0;
			}
			else if (s.BlockedDir == 2 && q.Close <= s.StopReferencePrice)
			{
				s.BlockedDir = 0;
				s.StopReferencePrice = 0;
			}

			// 冷却期
			if (s.CooldownRemaining > 0)
			{
				s.CooldownRemaining--;
				return;
			}
			if ((direction == 0 && s.BlockedDir == 1) || (direction == 1 && s.BlockedDir == 2))
				return;

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
					s.NextGridPrice = q.Close * (1 - gridPctRatio);
				}
				else
				{
					Trade(tu.MktSymbol, OrderType.SELL, q.Close, lots, period, sendMode);
					s.NextGridPrice = q.Close * (1 + gridPctRatio);
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
						decimal stopRef = s.FirstEntryPrice;
						CloseAll(s);
						s.BlockedDir = 1;
						s.StopReferencePrice = stopRef;
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
						lots = Math.Max(lots, 0.001m);

						AddLayer(s, q.Close, lots);
						Trade(tu.MktSymbol, OrderType.BUY, q.Close, lots, period, sendMode);
						s.NextGridPrice = q.Close * (1 - gridPctRatio);
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
						decimal stopRef = s.FirstEntryPrice;
						CloseAll(s);
						s.BlockedDir = 2;
						s.StopReferencePrice = stopRef;
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
						lots = Math.Max(lots, 0.001m);

						AddLayer(s, q.Close, lots);
						Trade(tu.MktSymbol, OrderType.SELL, q.Close, lots, period, sendMode);
						s.NextGridPrice = q.Close * (1 + gridPctRatio);
					}
				}
			}
		}
	}
}
