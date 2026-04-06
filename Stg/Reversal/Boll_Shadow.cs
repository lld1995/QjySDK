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
	public class Boll_Shadow : StgBase
	{
		public Boll_Shadow()
		{
		}

		public Boll_Shadow(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();
			sd.ArgDic["lookbackPeriods"] = 20;
			sd.ArgDic["shadowRate"] = 2.0m;
			sd.ArgDic["bollWidthRate"] = 0.5m;
			sd.ArgDic["mode"] = 0;
			sd.ArgDic["sendMode"] = 0;
			sd.ArgDic["trailingAtrMult"] = 2.0m;
			sd.ArgDic["maxHoldBars"] = 30;
			sd.ArgDic["consolidationBars"] = 7;

			// 优化开关
			sd.ArgDic["useTrendFilter"] = 1;      // OPT-1: MA斜率趋势过滤
			sd.ArgDic["useVolumeFilter"] = 1;     // OPT-2: 影线反转成交量过滤
			sd.ArgDic["useBandEdge"] = 0;         // OPT-3: 影线信号靠近布林带边缘
			sd.ArgDic["useBreakConfirm"] = 1;     // OPT-4: 盘整突破最小幅度
			sd.ArgDic["useEntryHighFix"] = 0;     // OPT-5: 追踪止损入场价修正

			//手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			sd.ColorDic["main-up"] = "#FF5722";
			sd.ColorDic["main-mid"] = "#FF9800";
			sd.ColorDic["main-low"] = "#2196F3";
			sd.ColorDic["vol-vol"] = "#2196F3";
			sd.ColorDic["main-shadow-down"] = "#00BCD4";
			sd.ColorDic["main-shadow-up"] = "#E91E63";
			return sd;
		}

		private class State
		{
			public int Status { get; set; }

			public decimal Num { get; set; }

			public decimal EntryPrice { get; set; }

			public decimal HighestSinceEntry { get; set; }

			public decimal LowestSinceEntry { get; set; }

			public int HoldBars { get; set; }

			public int ConsolidationCount { get; set; }

			/// <summary>
			/// 0 consolidation break 1 shadow
			/// </summary>
			public int OpenType { get; set; }

			public void Reset()
			{
				Status = 0;
				Num = 0;
				EntryPrice = 0;
				HighestSinceEntry = 0;
				LowestSinceEntry = decimal.MaxValue;
				HoldBars = 0;
				OpenType = 0;
			}
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

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
				s.LowestSinceEntry = decimal.MaxValue;
				_stateDic[sk] = s;
			}

			int lookbackPeriods = (int)ArgDic["lookbackPeriods"];
			int atrPeriod = 14;
			if (tu.QuoteList.Count < Math.Max(lookbackPeriods, atrPeriod) + 1) return;

			var shadowRate = (decimal)ArgDic["shadowRate"];
			var bollWidthRate = (decimal)ArgDic["bollWidthRate"];
			int mode = (int)ArgDic["mode"];
			int sendMode = (int)ArgDic["sendMode"];
			var trailingAtrMult = (decimal)ArgDic["trailingAtrMult"];
			int maxHoldBars = (int)ArgDic["maxHoldBars"];
			int consolidationBars = (int)ArgDic["consolidationBars"];
			int useTrendFilter = (int)ArgDic["useTrendFilter"];
			int useVolumeFilter = (int)ArgDic["useVolumeFilter"];
			int useBandEdge = (int)ArgDic["useBandEdge"];
			int useBreakConfirm = (int)ArgDic["useBreakConfirm"];
			int useEntryHighFix = (int)ArgDic["useEntryHighFix"];

			// 周期自适应参数：日线用短持仓，分钟线用宽止损+长持仓
			if (period == Period.TIME_1D)
			{
				maxHoldBars = Math.Min(maxHoldBars, 15);
			}
			else if (period == Period.TIME_15M || period == Period.TIME_5M || period == Period.TIME_1M || period == Period.TIME_30M)
			{
				trailingAtrMult = Math.Max(trailingAtrMult, 3.0m);
				maxHoldBars = Math.Max(maxHoldBars, 50);
			}

			var q = tu.QuoteList[tu.QuoteList.Count - 1];
			var q2 = tu.QuoteList[tu.QuoteList.Count - 2];
			var bl = tu.QuoteList.GetBollingerBands(lookbackPeriods).ToList();
			var atrList = tu.QuoteList.GetAtr(atrPeriod).ToList();

			var bl1 = bl[bl.Count - 1];
			Plot("main", "up", PlotType.LINE, bl1.UpperBand);
			Plot("main", "mid", PlotType.LINE, bl1.Sma);
			Plot("main", "low", PlotType.LINE, bl1.LowerBand);
			Plot("vol", "vol", PlotType.RECTANGLE, (double)q.Volume);

			var atrVal = atrList[atrList.Count - 1].Atr;
			if (!atrVal.HasValue || atrVal.Value <= 0) return;
			decimal atr = (decimal)atrVal.Value;

			// [FIX-1] Doji过滤：实体太小时用ATR*0.1作为最小阈值
			var shadowDown = Math.Min(q.Open, q.Close) - q.Low;
			var shadowUp = q.High - Math.Max(q.Open, q.Close);
			var entityRaw = Math.Abs(q.Close - q.Open);
			var minEntity = atr * 0.1m;
			var entity = Math.Max(entityRaw, minEntity) * shadowRate;

			if (shadowDown > entity)
				Plot("main", "shadow-down", PlotType.POINT, (double)q.Low);
			if (shadowUp > entity)
				Plot("main", "shadow-up", PlotType.POINT, (double)q.High);

			// 计算手数
			var num = (decimal)ArgDic["lots"];
			var lotsMode = (int)ArgDic["lotsMode"];
			if (lotsMode == 1)
			{
				var s2 = GetSymbol(tu.MktSymbol);
				num = ((decimal)ArgDic["money"] / (q.Close * s2.multiplier * s2.margin_ratio));
				if (s2.symbol_type == (int)SymbolType.COIN)
					num = (int)(num * 1000) / 1000.0m;
				else
					num = (int)num;
			}

			// 盘整检测
			var bollWidth = bl1.UpperBand - bl1.LowerBand;
			if ((decimal)bollWidth < (decimal)bl1.Sma * bollWidthRate)
				++s.ConsolidationCount;
			else
				s.ConsolidationCount = 0;

			var inConsolidation = s.ConsolidationCount > consolidationBars;

			// ========== 持仓管理 ==========
			if (s.Status != 0)
			{
				s.HoldBars++;
				if (s.Status == 1)
				{
					if (q.High > s.HighestSinceEntry) s.HighestSinceEntry = q.High;
				}
				else
				{
					if (q.Low < s.LowestSinceEntry) s.LowestSinceEntry = q.Low;
				}
			}

			if (s.Status == 1)
			{
				var isClose = false;

				// [FIX-2] 硬止损5%
				if (q.Close < s.EntryPrice * 0.95m)
					isClose = true;

				// [FIX-3] 追踪止损：从最高点回撤超过 trailingAtrMult * ATR
				if (s.HighestSinceEntry > s.EntryPrice && q.Close < s.HighestSinceEntry - atr * trailingAtrMult)
					isClose = true;

				// [FIX-4] 影线反转用布林上轨止盈
				if (s.OpenType == 1 && q.Close > (decimal)bl1.UpperBand)
					isClose = true;

				// [FIX-6] 最大持仓时间
				if (s.HoldBars >= maxHoldBars)
					isClose = true;

				if (isClose)
				{
					Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Reset();
				}
			}
			else if (s.Status == 2)
			{
				var isClose = false;

				// [FIX-2] 硬止损5%
				if (q.Close > s.EntryPrice * 1.05m)
					isClose = true;

				// [FIX-3] 追踪止损：从最低点反弹超过 trailingAtrMult * ATR
				if (s.LowestSinceEntry < s.EntryPrice && q.Close > s.LowestSinceEntry + atr * trailingAtrMult)
					isClose = true;

				// [FIX-4] 影线反转用布林下轨止盈
				if (s.OpenType == 1 && q.Close < (decimal)bl1.LowerBand)
					isClose = true;

				// [FIX-6] 最大持仓时间
				if (s.HoldBars >= maxHoldBars)
					isClose = true;

				if (isClose)
				{
					Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
					s.Reset();
				}
			}

			// ========== 开仓逻辑 ==========
			if (s.Status == 0)
			{
				var status = 0;
				var openType = 0;

				// [OPT-1] MA斜率趋势过滤：布林中轨(SMA)斜率判断趋势方向
				var bl2 = bl[bl.Count - 2];
				var smaSlope = bl1.Sma - bl2.Sma; // >0上升趋势 <0下降趋势

				if (inConsolidation)
				{
					// [FIX-5] 盘整突破：只做顺势放量突破
					var breakUp = q.Close > (decimal)bl1.UpperBand && q.Volume > q2.Volume;
					var breakDn = q.Close < (decimal)bl1.LowerBand && q.Volume > q2.Volume;

					// [OPT-4] 盘整突破最小幅度：突破需超过带外0.5*ATR
					if (useBreakConfirm == 1)
					{
						breakUp = breakUp && q.Close > (decimal)bl1.UpperBand + atr * 0.5m;
						breakDn = breakDn && q.Close < (decimal)bl1.LowerBand - atr * 0.5m;
					}

					if (breakUp) { status = 1; openType = 0; }
					else if (breakDn) { status = 2; openType = 0; }
				}
				else
				{
					// 影线反转
					var buySignal = shadowDown > entity && q.Close > (decimal)bl1.Sma;
					var sellSignal = shadowUp > entity && q.Close < (decimal)bl1.Sma;

					// [OPT-1] MA斜率趋势过滤：做多要求SMA不下降，做空要求SMA不上升
					if (useTrendFilter == 1)
					{
						if (smaSlope < 0) buySignal = false;
						if (smaSlope > 0) sellSignal = false;
					}

					// [OPT-2] 影线反转成交量过滤：要求放量
					if (useVolumeFilter == 1)
					{
						if (q.Volume <= q2.Volume) { buySignal = false; sellSignal = false; }
					}

					// [OPT-3] 影线信号靠近布林带边缘：买入要求close在中轨到下轨之间的上半部分
					if (useBandEdge == 1)
					{
						var bandMid = ((decimal)bl1.UpperBand + (decimal)bl1.LowerBand) / 2m;
						if (buySignal && q.Close > bandMid) buySignal = false;   // 买入信号要在下半区
						if (sellSignal && q.Close < bandMid) sellSignal = false; // 卖出信号要在上半区
					}

					if (buySignal) { status = 1; openType = 1; }
					else if (sellSignal) { status = 2; openType = 1; }
				}

				if (status == 1 && mode != 2)
				{
					s.Status = 1;
					s.Num = num;
					s.EntryPrice = q.Close;
					// [OPT-5] 追踪止损入场价修正：用入场价而非当根K线极值
					s.HighestSinceEntry = useEntryHighFix == 1 ? q.Close : q.High;
					s.LowestSinceEntry = useEntryHighFix == 1 ? q.Close : q.Low;
					s.HoldBars = 0;
					s.OpenType = openType;
					Trade(tu.MktSymbol, OrderType.BUY, q.Close, s.Num, period, sendMode);
				}
				else if (status == 2 && mode != 1)
				{
					s.Status = 2;
					s.Num = num;
					s.EntryPrice = q.Close;
					s.HighestSinceEntry = useEntryHighFix == 1 ? q.Close : q.High;
					s.LowestSinceEntry = useEntryHighFix == 1 ? q.Close : q.Low;
					s.HoldBars = 0;
					s.OpenType = openType;
					Trade(tu.MktSymbol, OrderType.SELL, q.Close, s.Num, period, sendMode);
				}
			}
		}
	}
}
