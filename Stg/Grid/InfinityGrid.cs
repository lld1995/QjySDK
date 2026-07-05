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
	/// 无限网格策略 (Infinity Grid Strategy)
	/// 
	/// 策略原理：
	/// 等比例(几何)网格策略，每格价格按固定百分比递增/递减。
	/// 适合长期运行的宽幅波动品种（如加密货币），天然适配价格跨度大的场景。
	/// 
	/// 核心逻辑：
	/// 1. 网格按等比例分布：price[i] = price[i-1] * (1 + ratio)
	/// 2. 每格投入等额资金（而非等量手数），实现几何均衡
	/// 3. 无上下界限制，价格涨跌均可持续交易（理论上"无限"）
	/// 4. 价格下跌买入时自动计算对应手数，上涨卖出时卖出对应手数
	/// 5. 可选设置最大持仓金额保护
	/// </summary>
	public class InfinityGrid : StgBase
	{
		public InfinityGrid()
		{
		}

		public InfinityGrid(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();

			// 交易方向：1做多网格（逢跌买入/逢涨卖出） 2做空网格（逢涨卖空/逢跌买回）
			sd.ArgDic["mode"] = 1;

			// 网格参数
			sd.ArgDic["gridRatio"] = 1.5m;
			sd.ArgDic["investPerGrid"] = 1000m;

			// 动态网格（默认启用：根据Common.GridSizingHelper按波动率调整几何网格比例）
			sd.ArgDic["dynamicGrid"] = 1;
			sd.ArgDic["atrPeriod"] = 14;
			sd.ArgDic["atrMultiplier"] = 1.2m;
			sd.ArgDic["minGridPercent"] = 0.2m;
			sd.ArgDic["maxGridPercent"] = 5.0m;

			// 保护参数
			sd.ArgDic["maxTotalInvest"] = 50000m;
			sd.ArgDic["lowerPriceLimit"] = 0m;
			sd.ArgDic["upperPriceLimit"] = 0m;

			// 止损
			sd.ArgDic["useStopLoss"] = 1;
			sd.ArgDic["stopLossPercent"] = 10.0m;
			sd.ArgDic["resumeAfterStopLoss"] = 0;

			// 发单
			sd.ArgDic["sendMode"] = 0;

			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易模式", Explain = "交易方向控制", Options = "1:做多网格|2:做空网格", Type = "select" };
			sd.ArgDescDic["gridRatio"] = new ArgDesc() { Text = "基准网格比例%", Explain = "动态网格关闭或历史不足时使用的兜底几何网格比例", Type = "number" };
			sd.ArgDescDic["investPerGrid"] = new ArgDesc() { Text = "每格投入金额", Explain = "每个网格投入的金额(非手数)", Type = "number" };
			sd.ArgDescDic["dynamicGrid"] = new ArgDesc() { Text = "动态网格", Explain = "默认启用；调用Common.GridSizingHelper，基于ATR均值和真实波幅中位数计算几何网格比例，自动适配所选K线周期和品种波动", Options = "0:关闭|1:启用动态波动率网格", Type = "bool" };
			sd.ArgDescDic["atrPeriod"] = new ArgDesc() { Text = "ATR周期", Explain = "动态网格使用的波动统计周期", Type = "number" };
			sd.ArgDescDic["atrMultiplier"] = new ArgDesc() { Text = "ATR倍率", Explain = "动态网格=波动率×倍率，并结合真实波幅中位数降低极端K线影响", Type = "number" };
			sd.ArgDescDic["minGridPercent"] = new ArgDesc() { Text = "最小网格%", Explain = "动态网格下限，避免低波动时网格过密", Type = "number" };
			sd.ArgDescDic["maxGridPercent"] = new ArgDesc() { Text = "最大网格%", Explain = "动态网格上限，避免极端波动后网格过宽", Type = "number" };
			sd.ArgDescDic["maxTotalInvest"] = new ArgDesc() { Text = "最大总投入", Explain = "持仓总投入金额上限，0为不限", Type = "number" };
			sd.ArgDescDic["lowerPriceLimit"] = new ArgDesc() { Text = "价格下限", Explain = "低于此价格停止买入，0为不限", Type = "number" };
			sd.ArgDescDic["upperPriceLimit"] = new ArgDesc() { Text = "价格上限", Explain = "高于此价格停止卖出，0为不限", Type = "number" };
			sd.ArgDescDic["useStopLoss"] = new ArgDesc() { Text = "启用止损", Explain = "触及止损价自动平仓", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "价格偏离基准超过此百分比时全部止损", Type = "number" };
			sd.ArgDescDic["resumeAfterStopLoss"] = new ArgDesc() { Text = "止损后重建", Explain = "止损后价格回到止损阈值内时允许重新建立网格；默认关闭以保持止损后停机保护", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };

			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 2;

			sd.ColorDic["sub0-Position"] = "#2196F3";
			sd.ColorDic["sub0-TotalInvest"] = "#FF9800";
			sd.ColorDic["sub1-PnL"] = "#4CAF50";
			return sd;
		}

		private class FilledGrid
		{
			public decimal Price { get; set; }
			public decimal Lots { get; set; }
			public decimal Invest { get; set; }
		}

		private class State
		{
			public bool Initialized { get; set; }
			public decimal BasePrice { get; set; }
			public decimal LastPrice { get; set; }
			public int CurrentGridIndex { get; set; }
			public List<FilledGrid> Holdings { get; set; } = new List<FilledGrid>();
			public decimal TotalInvest { get; set; }
			public decimal TotalPosition { get; set; }
			public decimal RealizedPnL { get; set; }
			public bool IsStopped { get; set; }
			public decimal LastGridPercent { get; set; }
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		private State GetOrCreateState(string key)
		{
			if (!_stateDic.ContainsKey(key))
				_stateDic[key] = new State();
			return _stateDic[key];
		}

		private decimal GetGridPrice(decimal basePrice, decimal ratio, int index)
		{
			return basePrice * (decimal)Math.Pow((double)(1 + ratio / 100m), index);
		}

		private int GetGridIndex(decimal basePrice, decimal ratio, decimal price)
		{
			if (price <= 0 || basePrice <= 0) return 0;
			double r = (double)(ratio / 100m);
			return (int)Math.Round(Math.Log((double)(price / basePrice)) / Math.Log(1 + r));
		}

		private decimal CalcLots(TableUnit tu, decimal price, decimal investAmount)
		{
			var sym = GetSymbol(tu.MktSymbol);
			decimal num = investAmount / (price * sym.multiplier * sym.margin_ratio);
			if (sym.symbol_type == (int)SymbolType.COIN)
				num = (int)(num * 1000) / 1000.0m;
			else
				num = (int)num;
			return Math.Max(num, 0);
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
			decimal gridRatio = Convert.ToDecimal(ArgDic["gridRatio"]);
			int dynamicGrid = ArgDic.ContainsKey("dynamicGrid") ? Convert.ToInt32(ArgDic["dynamicGrid"]) : 1;
			if (dynamicGrid == 1)
			{
				int atrPeriod = ArgDic.ContainsKey("atrPeriod") ? Convert.ToInt32(ArgDic["atrPeriod"]) : 14;
				decimal atrMultiplier = ArgDic.ContainsKey("atrMultiplier") ? Convert.ToDecimal(ArgDic["atrMultiplier"]) : 1.2m;
				decimal minGridPercent = ArgDic.ContainsKey("minGridPercent") ? Convert.ToDecimal(ArgDic["minGridPercent"]) : 0.2m;
				decimal maxGridPercent = ArgDic.ContainsKey("maxGridPercent") ? Convert.ToDecimal(ArgDic["maxGridPercent"]) : 5.0m;
				gridRatio = GridSizingHelper.CalculateDynamicGridPercent(tu.QuoteList, q.Close, gridRatio, atrPeriod, atrMultiplier, minGridPercent, maxGridPercent, s.LastGridPercent);
				s.LastGridPercent = gridRatio;
			}
			decimal investPerGrid = Convert.ToDecimal(ArgDic["investPerGrid"]);
			decimal maxTotalInvest = Convert.ToDecimal(ArgDic["maxTotalInvest"]);
			decimal lowerLimit = Convert.ToDecimal(ArgDic["lowerPriceLimit"]);
			decimal upperLimit = Convert.ToDecimal(ArgDic["upperPriceLimit"]);
			int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
			decimal stopLossPct = Convert.ToDecimal(ArgDic["stopLossPercent"]);
			int resumeAfterStopLoss = ArgDic.ContainsKey("resumeAfterStopLoss") ? Convert.ToInt32(ArgDic["resumeAfterStopLoss"]) : 0;
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

			// 默认保持止损后停机；显式启用后，价格回到止损阈值内才重建网格，避免刚止损即反复开仓。
			if (s.IsStopped)
			{
				if (resumeAfterStopLoss != 1 || s.BasePrice <= 0)
					return;

				decimal deviation = Math.Abs(q.Close - s.BasePrice) / s.BasePrice * 100m;
				if (useStopLoss == 1 && deviation >= stopLossPct)
					return;

				s.IsStopped = false;
				s.Initialized = false;
				s.CurrentGridIndex = 0;
				s.LastPrice = 0;
			}

			if (!s.Initialized)
			{
				s.BasePrice = q.Close;
				s.CurrentGridIndex = 0;
				s.LastPrice = q.Close;
				s.Initialized = true;
				return;
			}

			// 计算当前价格所在的网格索引
			int currentIdx = GetGridIndex(s.BasePrice, gridRatio, q.Close);
			int prevIdx = GetGridIndex(s.BasePrice, gridRatio, q2.Close);

			// 绘图
			Plot("sub0", "Position", PlotType.LINE, (double)s.TotalPosition);
			Plot("sub0", "TotalInvest", PlotType.LINE, (double)s.TotalInvest);

			// 计算浮动盈亏（做空方向盈亏反号）
			decimal dirSign = mode == 2 ? -1m : 1m;
			decimal unrealizedPnL = 0;
			foreach (var h in s.Holdings)
			{
				unrealizedPnL += (q.Close - h.Price) * h.Lots * dirSign;
			}
			Plot("sub1", "PnL", PlotType.LINE, (double)(s.RealizedPnL + unrealizedPnL));

			// 绘制附近网格线
			for (int i = currentIdx - 3; i <= currentIdx + 3; i++)
			{
				decimal gp = GetGridPrice(s.BasePrice, gridRatio, i);
				string name = i >= 0 ? $"G_P{i}" : $"G_N{Math.Abs(i)}";
				Plot("main", name, PlotType.LINE, (double)gp);
			}

			// 止损检查：价格偏离基准超过止损百分比，全部平仓
			if (useStopLoss == 1 && s.Holdings.Count > 0)
			{
				decimal deviation = Math.Abs(q.Close - s.BasePrice) / s.BasePrice * 100m;
				if (deviation >= stopLossPct)
				{
					foreach (var h in s.Holdings)
					{
						Trade(tu.MktSymbol, mode == 2 ? OrderType.BUY_TO_COVER : OrderType.SELL_TO_COVER, q.Close, h.Lots, period, sendMode);
						s.RealizedPnL += (q.Close - h.Price) * h.Lots * dirSign;
					}
					s.Holdings.Clear();
					s.TotalPosition = 0;
					s.TotalInvest = 0;
					s.IsStopped = true;
					return;
				}
			}

			if (mode != 2)
			{
				// 做多网格
				// 价格向下穿越网格线 → 买入
				if (currentIdx < prevIdx)
				{
					for (int i = prevIdx - 1; i >= currentIdx; i--)
					{
						decimal gridPrice = GetGridPrice(s.BasePrice, gridRatio, i);

						// 检查价格下限
						if (lowerLimit > 0 && q.Close < lowerLimit) break;

						// 检查最大投入
						if (maxTotalInvest > 0 && s.TotalInvest >= maxTotalInvest) break;

						decimal lots = CalcLots(tu, q.Close, investPerGrid);
						if (lots <= 0) continue;

						Trade(tu.MktSymbol, OrderType.BUY, q.Close, lots, period, sendMode);
						s.Holdings.Add(new FilledGrid
						{
							Price = q.Close,
							Lots = lots,
							Invest = investPerGrid
						});
						s.TotalPosition += lots;
						s.TotalInvest += investPerGrid;
					}
				}
				// 价格向上穿越网格线 → 卖出(FIFO)
				else if (currentIdx > prevIdx)
				{
					for (int i = prevIdx + 1; i <= currentIdx; i++)
					{
						// 检查价格上限
						if (upperLimit > 0 && q.Close > upperLimit) break;

						// 卖出最早买入的一层
						if (s.Holdings.Count > 0)
						{
							var oldest = s.Holdings[0];
							Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oldest.Lots, period, sendMode);
							s.RealizedPnL += (q.Close - oldest.Price) * oldest.Lots;
							s.TotalPosition -= oldest.Lots;
							s.TotalInvest -= oldest.Invest;
							s.Holdings.RemoveAt(0);
						}
					}
				}
			}
			else
			{
				// 做空网格（Holdings 此处记录空头层，TotalPosition 为负）
				// 价格向上穿越网格线 → 卖空
				if (currentIdx > prevIdx)
				{
					for (int i = prevIdx + 1; i <= currentIdx; i++)
					{
						// 检查价格上限
						if (upperLimit > 0 && q.Close > upperLimit) break;

						// 检查最大投入
						if (maxTotalInvest > 0 && s.TotalInvest >= maxTotalInvest) break;

						decimal lots = CalcLots(tu, q.Close, investPerGrid);
						if (lots <= 0) continue;

						Trade(tu.MktSymbol, OrderType.SELL, q.Close, lots, period, sendMode);
						s.Holdings.Add(new FilledGrid
						{
							Price = q.Close,
							Lots = lots,
							Invest = investPerGrid
						});
						s.TotalPosition -= lots;
						s.TotalInvest += investPerGrid;
					}
				}
				// 价格向下穿越网格线 → 买回(FIFO)
				else if (currentIdx < prevIdx)
				{
					for (int i = prevIdx - 1; i >= currentIdx; i--)
					{
						// 检查价格下限
						if (lowerLimit > 0 && q.Close < lowerLimit) break;

						// 买回最早卖空的一层
						if (s.Holdings.Count > 0)
						{
							var oldest = s.Holdings[0];
							Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oldest.Lots, period, sendMode);
							s.RealizedPnL += (oldest.Price - q.Close) * oldest.Lots;
							s.TotalPosition += oldest.Lots;
							s.TotalInvest -= oldest.Invest;
							s.Holdings.RemoveAt(0);
						}
					}
				}
			}

			s.LastPrice = q.Close;
			s.CurrentGridIndex = currentIdx;
		}
	}
}
