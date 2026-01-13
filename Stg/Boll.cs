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
	public class Boll : StgBase
	{
        public Boll(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();
			sd.ArgDic["lookbackPeriods"] = 20;
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
			sd.SubChartNum = 0;
			return sd;
		}

		private class State
		{
			public int Status { get; set; }

			public decimal Num { get; set; }

			public int AddNum { get; set; }

			public decimal LastOpenPrice { get; set; }

			public decimal LossPrice { get; set; }
			public decimal WinPrice { get; set; }
			public decimal Scene { get; set; }

			public bool AllowAtrAdd { get; set; }
			public bool AllowSmaAdd { get; set; }
			public bool IsStepClose { get; set; }

			public int HighTimes { get; set; }
			public int LowTimes { get; set; }

			public List<Peak> LowPeakList { get; set; } = new List<Peak>();
			public List<Peak> HighPeakList { get; set; } = new List<Peak>();

		}

		private class Peak
		{
			public int Index { get; set; }

			public Quote Q { get; set; }
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
					int mode = (int)ArgDic["mode"];
					int sendMode = (int)ArgDic["sendMode"];
					var q = tu.QuoteList[tu.QuoteList.Count - 1];
					var q2 = tu.QuoteList[tu.QuoteList.Count - 2];
					var bl = tu.QuoteList.GetBollingerBands(lookbackPeriods).ToList();

					var bl1 = bl[bl.Count - 1];
					var bl2 = bl[bl.Count - 2];
					Plot("main", "up", PlotType.LINE, bl1.UpperBand);
					Plot("main", "mid", PlotType.LINE, bl1.Sma);
					Plot("main", "low", PlotType.LINE, bl1.LowerBand);

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

					if (s.Status == 0)
					{
						if (q2.Close < (decimal)bl2.LowerBand && q.Close > (decimal)bl1.LowerBand)
						{
							s.Status = 1;
							s.Num = num;
							s.AddNum = 0;
							Trade(tu.MktSymbol, OrderType.BUY, q.Close, s.Num, period, sendMode);
						}
						else if (q2.Close > (decimal)bl2.UpperBand && q.Close < (decimal)bl1.UpperBand)
						{
							s.Status = 2;
							s.Num = num;
							s.AddNum = 0;
							Trade(tu.MktSymbol, OrderType.SELL, q.Close, s.Num, period, sendMode);
						}
					}
					else if (s.Status == 1)
					{
						if (q.Close>(decimal)bl1.Sma)
						{
							Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
							s.Status = 0;
							s.Num = 0;
							s.AddNum = 0;
						}
					}
					else if (s.Status == 2)
					{
						if (q.Close < (decimal)bl1.Sma)
						{
							Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
							s.Status = 0;
							s.Num = 0;
							s.AddNum = 0;
						}
					}
				}
			}
		}
	}
}
