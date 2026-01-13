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
    public class DonchianReverse : StgBase
    {
        public DonchianReverse(string id) : base(id)
        {
        }

		public override StgDesc GetStgDesc()
        {
            var sd=new StgDesc();
            sd.ArgDic["lookbackPeriods"] = 20;
            sd.ArgDic["lossRate"] = 0.01m;
            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDic["highBound"] = 0;
            sd.ArgDic["lowBound"] = 0;

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
            sd.ColorDic["macd-macd"] = "#BA55D3";
            sd.ColorDic["macd-signal"] = "";
            sd.ColorDic["macd-histogram"] = "#F6465D;#0ECB81";
			return sd;
        }

        private class State
        {
            public int Status { get;set; }

            public decimal LossPrice { get; set; }

            public decimal Num { get; set; }
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		public override void OnBar(Period period, TableUnit tu, bool isFinal,SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (isFinal)
			{
				if (tu.QuoteList.Count > 1)
				{
					int mode = (int)ArgDic["mode"];
					int sendMode = (int)ArgDic["sendMode"];
                    var q = tu.QuoteList.Last();
					var dca = tu.QuoteList.GetDonchian((int)ArgDic["lookbackPeriods"]).ToList();

					var dca1 = dca[dca.Count - 1];
					Plot("main", "up", PlotType.LINE, (double?)dca1.UpperBand);
					Plot("main", "low", PlotType.LINE, (double?)dca1.LowerBand);

                    var dca2 = dca[dca.Count - 2];
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

					State s = null;
                    var sk = tu.GetStateKey();
                    if (_stateDic.ContainsKey(sk))
                    {
                        s = _stateDic[sk];
                    }
                    else
                    {
                        s=new State();
                        _stateDic[sk] = s;
                    }

					if (s.Status == 0)
					{
						if (q.Close > dca2.UpperBand && mode!=1)
						{
                            s.Num = num;
                            s.Status = 2;
                            s.LossPrice = q.Close * (1 + (decimal)ArgDic["lossRate"]);
                            Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                        }
						if (q.Close < dca2.LowerBand && mode != 2)
						{
                            s.Num = num;
                            s.Status = 1;
                            s.LossPrice = q.Close * (1 - (decimal)ArgDic["lossRate"]);
                            Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                        }
					}
					else if (s.Status == 1)
					{
                        if (s.Num > 0)
                        {
                            int close = 0;
                            if (q.Close < s.LossPrice)
                            {
                                close = 1;
                            }
                            if (q.Close > dca2.UpperBand)
                            {
                                close = 2;
                            }
                            if (close > 0)
                            {
								var oriNum = s.Num;
								s.Num =0;
                                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
                            }
                            if (close > 1)
                            {
                                if (mode != 1)
                                {
                                    s.Num = num;
                                    s.Status = 2;
									s.LossPrice = q.Close * (1 + (decimal)ArgDic["lossRate"]);
                                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                                }
                                else
                                {
                                    s.Status = 0;
                                }
                            }
                        }
                        else
                        {
                            if (q.Close > dca2.Centerline)
                            {
                                s.Status = 0;
                            }
                        }
                    }
					else if (s.Status == 2)
					{
                        if (s.Num > 0)
                        {
                            int close = 0;
                            if (q.Close > s.LossPrice)
                            {
                                close = 1;
                            }
                            if (q.Close < dca2.LowerBand)
                            {
                                close = 2;
                            }
                            if (close > 0)
                            {
								var oriNum = s.Num;
								s.Num = 0;
                                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
                            }
                            if (close > 1)
                            {
                                if (mode != 2)
                                {
                                    s.Num = num;
                                    s.Status = 1;
									s.LossPrice = q.Close * (1 - (decimal)ArgDic["lossRate"]);
                                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
                                }
                                else
                                {
                                    s.Status = 0;
                                }
                            }
                        }
                        else
                        {
                            if (q.Close < dca2.Centerline)
                            {
                                s.Status = 0;
                            }
                        }
                    }
				}
			}
		}
    }
}
