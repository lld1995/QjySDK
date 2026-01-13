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

		public Boll_Shadow(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();
			sd.ArgDic["lookbackPeriods"] = 20;
			sd.ArgDic["shadowRate"] = 2.5m;
			sd.ArgDic["bollWidthRate"] = 0.5m;
			sd.ArgDic["mode"] = 0;
			sd.ArgDic["sendMode"] = 0;

			//手数控制
			sd.ArgDic["lotsMode"] = 0;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 100000m;

			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;
			return sd;
		}

		private class State
		{
			public int Status { get; set; }

			public decimal Num { get; set; }

			public int AddNum { get; set; }

			public decimal LastOpenPrice { get; set; }

			public int ConsolidationCount { get; set; }

			/// <summary>
			/// 0 consolidation break 1 shadow
			/// </summary>
			public int OpenType { get; set; }

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
			s.LastOpenPrice = price;
		}

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
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

			if (isFinal)
			{
				int lookbackPeriods = (int)ArgDic["lookbackPeriods"];
				if (tu.QuoteList.Count >= lookbackPeriods)
				{
					var shadowRate = (decimal)ArgDic["shadowRate"];
					var bollWidthRate = (decimal)ArgDic["bollWidthRate"];
					int mode = (int)ArgDic["mode"];
					int sendMode = (int)ArgDic["sendMode"];
					var q = tu.QuoteList[tu.QuoteList.Count - 1];
					var q2 = tu.QuoteList[tu.QuoteList.Count - 2];
					var qlp = tu.QuoteList[tu.QuoteList.Count - lookbackPeriods];
					var bl = tu.QuoteList.GetBollingerBands(lookbackPeriods).ToList();

					var bl1 = bl[bl.Count - 1];
					var bl2 = bl[bl.Count - 2];
					Plot("main", "up", PlotType.LINE, bl1.UpperBand);
					Plot("main", "mid", PlotType.LINE, bl1.Sma);
					Plot("main", "low", PlotType.LINE, bl1.LowerBand);

					Plot("vol", "vol", PlotType.RECTANGLE, (double)q.Volume);

					var shadowDown = Math.Min(q.Open, q.Close) - q.Low;
					var shadowUp = q.High - Math.Max(q.Open, q.Close);
					var entity = Math.Abs(q.Close - q.Open) * shadowRate;
					if (shadowDown > entity)
					{
						Plot("main", "shadow-down", PlotType.POINT, (double)q.Low);
					}
					if (shadowUp > entity)
					{
						Plot("main", "shadow-up", PlotType.POINT, (double)q.High);
					}

					var num = (decimal)ArgDic["lots"];
					var lotsMode = (int)ArgDic["lotsMode"];
					if (lotsMode == 1)
					{
						var s2 = GetSymbol(tu.MktSymbol);
						num = ((decimal)ArgDic["money"] / (q.Close * s2.multiplier * s2.margin_ratio));
						if (s2.symbol_type == (int)SymbolType.COIN)
						{
							num = (int)(num * 1000) / 1000.0m;
						}
						else
						{
							num = (int)num;
						}
					}

					var bollWidth = bl1.UpperBand - bl1.LowerBand;
					if ((decimal)bollWidth < (decimal)bl1.Sma * bollWidthRate)
					{
						++s.ConsolidationCount;
					}
					else
					{
						s.ConsolidationCount = 0;
					}

					var InConsolidation = false;
					if (s.ConsolidationCount > 7)
					{
						InConsolidation = true;
					}

					if (s.Status == 0)
					{
						var status = 0;
						if (InConsolidation)
						{
							if (q.Close > (decimal)bl1.UpperBand)
							{
								if (q.Volume > q2.Volume)
								{
									status = 1;
								}
								else
								{
									status = 2;
								}
							}
							else if (q.Close < (decimal)bl1.LowerBand)
							{
								if (q.Volume > q2.Volume)
								{
									status = 2;
								}
								else
								{
									status = 1;
								}
							}
							if (status == 0)
							{

							}
							else
							{
								if (q.Close > qlp.Close)
								{
									status = 1;
								}
								else
								{
									status = 2;
								}
							}
						}
						else
						{
							if (shadowDown > entity && q.Close > (decimal)bl1.Sma)
							{
								status = 1;
								s.OpenType = 1;
							}
							if (shadowUp > entity && q.Close < (decimal)bl1.Sma)
							{
								status = 2;
								s.OpenType = 1;
							}
						}
						if (status == 1)
						{
							s.Status = 1;
							s.Num = num;
							s.AddNum = 0;
							s.LastOpenPrice = q.Close;
							Trade(tu.MktSymbol, OrderType.BUY, q.Close, s.Num, period, sendMode);
						}
						else if (status == 2)
						{
							s.Status = 2;
							s.Num = num;
							s.AddNum = 0;
							s.LastOpenPrice = q.Close;
							Trade(tu.MktSymbol, OrderType.SELL, q.Close, s.Num, period, sendMode);
						}
					}
					else if (s.Status == 1)
					{
						if (s.AddNum == 0 && shadowDown > entity && q.Close < (decimal)bl1.Sma)
						{
							s.OpenType = 1;
							s.Num += num;
							s.AddNum = 1;
							s.LastOpenPrice = q.Close;
							Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
						}

						var isClose = false;
						if (q.Close < s.LastOpenPrice * 0.95m)
						{
							isClose = true;
						}
						if (s.OpenType == 1)
						{
							if (q.Close > (decimal)bl1.UpperBand)
							{
								isClose = true;
							}
						}
						if (isClose)
						{
							Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
							s.Status = 0;
							s.Num = 0;
							s.AddNum = 0;
							s.OpenType = 0;
							s.LastOpenPrice = 0;
						}
					}
					else if (s.Status == 2)
					{
						if (s.AddNum == 0 && shadowUp > entity && q.Close > (decimal)bl1.Sma)
						{
							s.OpenType = 1;
							s.Num += num;
							s.AddNum = 1;
							s.LastOpenPrice = q.Close;
							Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
						}

						var isClose = false;
						if (q.Close > s.LastOpenPrice * 1.05m)
						{
							isClose = true;
						}
						if (s.OpenType == 1)
						{
							if (q.Close < (decimal)bl1.LowerBand)
							{
								isClose = true;

							}
						}
						if (isClose)
						{
							Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
							s.Status = 0;
							s.Num = 0;
							s.AddNum = 0;
							s.OpenType = 0;
							s.LastOpenPrice = 0;
						}
					}
				}
			}
		}
	}
}
