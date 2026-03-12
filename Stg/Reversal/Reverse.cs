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
	public class Reverse : StgBase
	{
		public Reverse()
		{
		}

		public Reverse(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();
			sd.ArgDic["lookbackPeriods"] = 20;
			sd.ArgDic["atrLookbackPeriods"] = 20;
			sd.ArgDic["emaLookbackPeriods"] = 30;
			sd.ArgDic["observePeriod"] = 10;
			sd.ArgDic["observeMinRate"] = 0.01m;
			sd.ArgDic["incRate"] = 0.005m;
			sd.ArgDic["lossRate"] = 0.03m;
			sd.ArgDic["maxAddNum"] = 0;
			sd.ArgDic["mode"] = 0;
			sd.ArgDic["sendMode"] = 0;

			//手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			sd.ArgDescDic["observePeriod"] = new ArgDesc() { Text = "观测周期", Explain = "止盈时当前CLOSE必须大于：观测周期CLOSE*(1+-观测最小幅度)" };
			sd.ArgDescDic["observeMinRate"] = new ArgDesc() { Text = "观测最小幅度", Explain = "止盈时当前CLOSE必须大于：观测周期CLOSE*(1+-观测最小幅度)" };
			sd.ArgDescDic["incRate"] = new ArgDesc() { Text = "增长比率", Explain = "止盈时当前CLOSE必须大于：最近开仓价*(1+止盈次数*增长比率)" };
			sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" };
			sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
			sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
			sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			sd.ColorDic["main-ema"] = "#2196F3";
			return sd;
		}

		private class State
		{
			public int Status { get; set; }

			public decimal Num { get; set; }

			public decimal LastOpenPrice { get; set; }

			public int AddNum { get; set; }

			public int ExitHighIndex { get; set; }

			public int ExitLowIndex { get; set; }

			public decimal EmaLossPrice { get; set; }

			public decimal MaxDiff { get; set; }

			public int StopNum { get; set; }
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

		private void CalcNum(TableUnit tu, SkQuote q, Dictionary<string, object> ArgDic,ref decimal num)
		{
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
			else
			{
				num = (decimal)ArgDic["lots"];
            }
        }

		private void AtrAdd(int maxAddNum,Period period, OrderType ot,AtrResult atr1,SkQuote q, TableUnit tu,decimal num,int sendMode,State s)
		{
            if (atr1.Atr.HasValue)
            {
                if (ot == OrderType.BUY)
                {
                    if (q.Close > s.LastOpenPrice + (decimal?)atr1.Atr && s.AddNum < maxAddNum)
                    {
                        s.Num += num;
                        ++s.AddNum;
                        Trade(tu.MktSymbol, ot, q.Close, num, period, sendMode);
                    }
                }
                else if (ot == OrderType.SELL)
                {
                    if (q.Close < s.LastOpenPrice - (decimal?)atr1.Atr && s.AddNum < maxAddNum)
                    {
                        s.Num += num;
                        ++s.AddNum;
                        Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
                    }
                }
            }
        }

		private void Open(Period period, OrderType ot, int mode, SkQuote q, TableUnit tu, decimal num, int sendMode, State s)
		{
			if (s.Status == 0)
			{
                if (ot == OrderType.BUY && mode != 2)
                {
                    s.Status = 1;
                }
                if (ot == OrderType.SELL && mode != 1)
                {
                    s.Status = 2;
                }
				if (s.Status > 0)
				{
                    s.Num = num;
                    s.AddNum = 0;
                    s.EmaLossPrice = 0;
                    s.MaxDiff = 0;
                    s.StopNum = 1;
                    Trade(tu.MktSymbol, ot, q.Close, num, period, sendMode);
                }
            }
        }

		private void Close(Period period, OrderType ot,int closeStatus, int mode,SkQuote q, TableUnit tu, decimal num, int sendMode, State s,int lowIndex,int highIndex)
		{
			if (closeStatus > 0)
			{
                s.ExitLowIndex = lowIndex;
                s.ExitHighIndex = highIndex;

                var oriNum = s.Num;
                if (closeStatus == 3)
                {
                    oriNum = num;
                    s.Num -= num;
                }
                else
                {
                    s.Num = 0;
                }
                Trade(tu.MktSymbol, ot, q.Close, oriNum, period, sendMode);

                if (s.Num <= 0)
                {
                    s.Status = 0;
                    s.AddNum = 0;
                }

                if (closeStatus == 1)
                {
					//reverse open
                    OrderType ot2 = OrderType.NONE;
                    if (mode != 1 && ot == OrderType.SELL_TO_COVER)
                    {
						ot2 = OrderType.SELL;
                    }
                    if (mode != 2 && ot == OrderType.BUY_TO_COVER)
                    {
                        ot2 = OrderType.BUY;
                    }
                    if (ot2 == OrderType.NONE)
                    {

                    }
                    else
                    {
						Open(period, ot2, mode, q, tu, num, sendMode, s);
                    }
                }
            }
        }

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
				_stateDic[sk] = s;
			}

			{
				var observePeriod = (int)ArgDic["observePeriod"];
				if (tu.QuoteList.Count > observePeriod)
				{
					int mode = (int)ArgDic["mode"];
					int sendMode = (int)ArgDic["sendMode"];
					int maxAddNum = (int)ArgDic["maxAddNum"];
					var incRate=(decimal)ArgDic["incRate"];
					var observeMinRate = (decimal)ArgDic["observeMinRate"];
					var q = tu.QuoteList[tu.QuoteList.Count - 1];
					var q2 = tu.QuoteList[tu.QuoteList.Count - 2];
					var qFar = tu.QuoteList[tu.QuoteList.Count - observePeriod];

					var num = 1m;
					CalcNum(tu, q, ArgDic, ref num);

                    AtrResult atr1 = null;
					if (s.Status == 0)
					{

					}
					else
					{
						var atr = tu.QuoteList.GetAtr((int)ArgDic["atrLookbackPeriods"]).ToList();
						atr1 = atr[atr.Count - 1];
					}
					var ema = tu.QuoteList.GetEma((int)ArgDic["emaLookbackPeriods"]).ToList();
					var ema1 = ema[ema.Count - 1];
					Plot("main", "ema", PlotType.LINE, ema1.Ema);

					var lowIndex = 0;
					var highIndex = 0;
					var lastLow = q2;
					var lastHigh = q2;
					var lookbackPeriods = (int)ArgDic["lookbackPeriods"];
					for (int i = tu.QuoteList.Count - 3; i >= 0 && i >= tu.QuoteList.Count - lookbackPeriods; --i)
					{
						var q3 = tu.QuoteList[i];
						if (q3.Low < lastLow.Low)
						{
							lastLow = q3;
							lowIndex = i;
						}
						if (q3.High > lastHigh.High)
						{
							lastHigh = q3;
							highIndex = i;
						}
					}

					var lastOpenPrice = s.LastOpenPrice;
					if (s.Status == 0)
					{
						if (lowIndex > highIndex)
						{
							if (lastLow == q2 || lowIndex == s.ExitLowIndex)
							{

							}
							else
							{
								if (q.Close > lastLow.High)
								{
									Open(period, OrderType.BUY, mode, q, tu, num, sendMode, s);
                                }
							}
						}
						else
						{
							if (lastHigh == q2 || highIndex == s.ExitHighIndex)
							{

							}
							else
							{
								if (q.Close < lastHigh.Low)
								{
                                    Open(period, OrderType.SELL, mode, q, tu, num, sendMode, s);
                                }
							}
						}
					}
					else if (s.Status == 1)
					{
						AtrAdd(maxAddNum, period, OrderType.BUY, atr1, q, tu, num, sendMode, s);

						int closeStatus = 0;
						var diff = q.Close - qFar.Close;
						if (s.MaxDiff == 0)
						{
							s.MaxDiff = diff;
						}
						else
						{
							if (diff < s.MaxDiff && q.Close > qFar.Close * (1+ observeMinRate) && q.Close > s.LastOpenPrice * (1 + incRate * s.StopNum) && s.Num > num)
							{
								closeStatus = 3;
								++s.StopNum;
							}
						}
						if (diff > s.MaxDiff)
						{
							s.MaxDiff = diff;
						}

						if (q.Close < lastLow.Low && q.Close < lastOpenPrice * (1 - (decimal)ArgDic["lossRate"]))
						{
							closeStatus = 1;
						}
						if (q.Close < (decimal)ema1.Ema)
						{
							if (s.EmaLossPrice == 0)
							{
								s.EmaLossPrice = (decimal)ema1.Ema * (1 - (decimal)ArgDic["lossRate"]);
							}
						}
						else
						{
							s.EmaLossPrice = 0;
						}
						if (s.EmaLossPrice > 0)
						{
							if (q.Close < s.EmaLossPrice)
							{
								closeStatus = 2;
							}
						}

						if (q.Close < s.EmaLossPrice)
						{
							closeStatus = 2;
						}
						Close(period, OrderType.SELL_TO_COVER,closeStatus,mode,q,tu,num,sendMode,s,lowIndex,highIndex);
					}
					else if (s.Status == 2)
					{
                        AtrAdd(maxAddNum, period, OrderType.SELL, atr1, q, tu, num, sendMode, s);

                        int closeStatus = 0;
						var diff = q.Close - qFar.Close;
						if (s.MaxDiff == 0)
						{
							s.MaxDiff = diff;
						}
						else
						{
							if (diff > s.MaxDiff && q.Close < qFar.Close * (1 - observeMinRate) && q.Close < s.LastOpenPrice * (1 - incRate * s.StopNum) && s.Num > num)
							{
								closeStatus = 3;
								++s.StopNum;
							}
						}
						if (diff < s.MaxDiff)
						{
							s.MaxDiff = diff;
						}

						if (q.Close > lastHigh.High && q.Close > lastOpenPrice * (1 + (decimal)ArgDic["lossRate"]))
						{
							closeStatus = 1;
						}

						if (q.Close > (decimal)ema1.Ema)
						{
							if (s.EmaLossPrice == 0)
							{
								s.EmaLossPrice = (decimal)ema1.Ema * (1 + (decimal)ArgDic["lossRate"]);
							}
						}
						else
						{
							s.EmaLossPrice = 0;
						}
						if (s.EmaLossPrice > 0)
						{
							if (q.Close > s.EmaLossPrice)
							{
								closeStatus = 2;
							}
						}
                        Close(period, OrderType.BUY_TO_COVER, closeStatus, mode, q, tu, num, sendMode, s, lowIndex, highIndex);
                    }
				}
			}
		}
	}
}
