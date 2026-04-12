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

			// 网格参数
			sd.ArgDic["gridRatio"] = 1.5m;
			sd.ArgDic["investPerGrid"] = 1000m;

			// 保护参数
			sd.ArgDic["maxTotalInvest"] = 50000m;
			sd.ArgDic["lowerPriceLimit"] = 0m;
			sd.ArgDic["upperPriceLimit"] = 0m;

			// 止损
			sd.ArgDic["useStopLoss"] = 1;
			sd.ArgDic["stopLossPercent"] = 10.0m;

			// 发单
			sd.ArgDic["sendMode"] = 0;

			sd.ArgDescDic["gridRatio"] = new ArgDesc() { Text = "网格比例%", Explain = "相邻网格价格比例百分比，如1.5表示每格间距1.5%", Type = "number" };
			sd.ArgDescDic["investPerGrid"] = new ArgDesc() { Text = "每格投入金额", Explain = "每个网格投入的金额(非手数)", Type = "number" };
			sd.ArgDescDic["maxTotalInvest"] = new ArgDesc() { Text = "最大总投入", Explain = "持仓总投入金额上限，0为不限", Type = "number" };
			sd.ArgDescDic["lowerPriceLimit"] = new ArgDesc() { Text = "价格下限", Explain = "低于此价格停止买入，0为不限", Type = "number" };
			sd.ArgDescDic["upperPriceLimit"] = new ArgDesc() { Text = "价格上限", Explain = "高于此价格停止卖出，0为不限", Type = "number" };
			sd.ArgDescDic["useStopLoss"] = new ArgDesc() { Text = "启用止损", Explain = "触及止损价自动平仓", Options = "0:关闭|1:启用", Type = "bool" };
			sd.ArgDescDic["stopLossPercent"] = new ArgDesc() { Text = "止损百分比", Explain = "价格偏离基准超过此百分比时全部止损", Type = "number" };
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

			decimal gridRatio = Convert.ToDecimal(ArgDic["gridRatio"]);
			decimal investPerGrid = Convert.ToDecimal(ArgDic["investPerGrid"]);
			decimal maxTotalInvest = Convert.ToDecimal(ArgDic["maxTotalInvest"]);
			decimal lowerLimit = Convert.ToDecimal(ArgDic["lowerPriceLimit"]);
			decimal upperLimit = Convert.ToDecimal(ArgDic["upperPriceLimit"]);
			int useStopLoss = Convert.ToInt32(ArgDic["useStopLoss"]);
			decimal stopLossPct = Convert.ToDecimal(ArgDic["stopLossPercent"]);
			int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

			// 止损后停止交易
			if (s.IsStopped) return;

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

			// 计算浮动盈亏
			decimal unrealizedPnL = 0;
			foreach (var h in s.Holdings)
			{
				unrealizedPnL += (q.Close - h.Price) * h.Lots;
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
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, h.Lots, period, sendMode);
						s.RealizedPnL += (q.Close - h.Price) * h.Lots;
					}
					s.Holdings.Clear();
					s.TotalPosition = 0;
					s.TotalInvest = 0;
					s.IsStopped = true;
					return;
				}
			}

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

			s.LastPrice = q.Close;
			s.CurrentGridIndex = currentIdx;
		}
	}
}
