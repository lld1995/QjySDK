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
	public class MACD_Deviate : StgBase
	{
		public MACD_Deviate()
		{
		}

		public MACD_Deviate(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();
            sd.ArgDic["lookbackPeriods"] = 14;
            sd.ArgDic["fastPeriods"] = 12;
            sd.ArgDic["slowPeriods"] = 26;
            sd.ArgDic["signalPeriods"] = 9;
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

			sd.ColorDic["macd-histogram"] = "#F6465D;#0ECB81";
			sd.ColorDic["macd-macd"] = "#2196F3";
			sd.ColorDic["macd-signal"] = "#FF9800";
			sd.MidValDic["macd"] = 0;
			return sd;
		}

		private class State
		{
			public int Status { get; set; }

			public decimal Num { get; set; }
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();
		private List<int> FindExtremes(List<SkQuote> arr, bool findHighs, int lookbackPeriods)
		{
			var extremes = new List<int>();

			for (int i = 2; i < arr.Count; i++)
			{
				if (findHighs)
				{
					// 局部高点
					if (arr[i - 1].High > arr[i - 2].High && arr[i - 1].High > arr[i].High)
					{
						var high = true;
						for (int j = i - 2; j >= 0 && j > i - lookbackPeriods; j--)
						{
							if (arr[i - 1].High < arr[j].High)
							{
								high = false;
								break;
							}
						}
						if (high)
						{
							extremes.Add(i - 1);
						}
					}
				}
				else
				{
					// 局部低点
					if (arr[i - 1].Low < arr[i - 2].Low && arr[i - 1].Low < arr[i].Low)
					{
						var low = true;
						for (int j = i - 2; j >= 0 && j > i - lookbackPeriods; j--)
						{
							if (arr[i - 1].Low > arr[j].Low)
							{
								low = false;
								break;
							}
						}
						if (low)
						{
							extremes.Add(i - 1);
						}
					}
				}
			}
			return extremes;
		}
		private List<int> FindExtremes(double?[] arr, bool findHighs, int lookbackPeriods)
		{
			var extremes = new List<int>();

			for (int i = 2; i < arr.Length - 2; i++)
			{
				if (findHighs)
				{
					// 局部高点
					if (arr[i - 1] > arr[i] && arr[i - 1] > arr[i - 2])
					{
						var high = true;
						for (int j = i - 2; j >= 0 && j > i - lookbackPeriods; j--)
						{
							if (arr[i - 1] < arr[j])
							{
								high = false;
								break;
							}
						}
						if (high)
						{
							extremes.Add(i - 1);
						}
					}
				}
				else
				{
					// 局部低点
					if (arr[i - 1] < arr[i] && arr[i - 1] < arr[i - 2])
					{
						var low = true;
						for (int j = i - 2; j >= 0 && j > i - lookbackPeriods; j--)
						{
							if (arr[i - 1] > arr[j])
							{
								low = false;
								break;
							}
						}
						if (low)
						{
							extremes.Add(i - 1);
						}
					}
				}
			}

			return extremes;
		}
		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);
			if (isFinal)
			{
				if (tu.QuoteList.Count > 1)
				{
					int mode = (int)ArgDic["mode"];
					int sendMode = (int)ArgDic["sendMode"];
					var q = tu.QuoteList.Last();

					var lookbackPeriods = (int)ArgDic["lookbackPeriods"];

                    var macd = tu.QuoteList.GetMacd((int)ArgDic["fastPeriods"], (int)ArgDic["slowPeriods"], (int)ArgDic["signalPeriods"]).ToList();
                    var macd1 = macd[macd.Count - 1];

                    Plot("macd", "histogram", PlotType.RECTANGLE, macd1.Histogram);
                    Plot("macd", "macd", PlotType.LINE, macd1.Macd);
                    Plot("macd", "signal", PlotType.LINE, macd1.Signal);

                    var highList = FindExtremes(tu.QuoteList, true, lookbackPeriods);
					var lowList = FindExtremes(tu.QuoteList, false, lookbackPeriods);
					var macdArr = macd.Select(d => d.Macd).ToArray();
					var macdHighList = FindExtremes(macdArr, true, lookbackPeriods);
					var macdLowList = FindExtremes(macdArr, false, lookbackPeriods);

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
						s = new State();
						_stateDic[sk] = s;
					}

					if (s.Status == 0)
					{
						if (lowList.Count > 1 && macdLowList.Count > 1)
						{
							var li1 = lowList[lowList.Count - 1];
							var li2 = lowList[lowList.Count - 2];
							var l1 = tu.QuoteList[li1];
							var l2 = tu.QuoteList[li2];

							var mli1 = macdLowList[macdLowList.Count - 1];
							var mli2 = macdLowList[macdLowList.Count - 2];
							var ml1 = macd[mli1];
							var ml2 = macd[mli2];


							if (l1.Low < l2.Low && ml1.Macd > ml2.Macd && mode != 2)
							{
								s.Status = 1;
								s.Num = num;
								Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
							}
						}
						if (highList.Count > 1 && macdHighList.Count > 1)
						{
							var hi1 = highList[highList.Count - 1];
							var hi2 = highList[highList.Count - 2];
							var h1 = tu.QuoteList[hi1];
							var h2 = tu.QuoteList[hi2];

							var mhi1 = macdHighList[macdHighList.Count - 1];
							var mhi2 = macdHighList[macdHighList.Count - 2];
							var mh1 = macd[mhi1];
							var mh2 = macd[mhi2];

							if (h1.High > h2.High && mh1.Macd < mh2.Macd && mode != 1)
							{
								s.Status = 2;
								s.Num = num;
								Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
							}
						}
					}
					else if (s.Status == 1)
					{
						if (highList.Count > 1 && macdHighList.Count > 1)
						{
                            var hi1 = highList[highList.Count - 1];
                            var hi2 = highList[highList.Count - 2];
                            var h1 = tu.QuoteList[hi1];
                            var h2 = tu.QuoteList[hi2];

                            var mhi1 = macdHighList[macdHighList.Count - 1];
                            var mhi2 = macdHighList[macdHighList.Count - 2];
                            var mh1 = macd[mhi1];
                            var mh2 = macd[mhi2];


                            if (h1.High > h2.High && mh1.Macd < mh2.Macd)
							{
								var oriNum = s.Num;
								Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);

								if (mode != 1)
								{
									s.Status = 2;
									s.Num = num;
									Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
								}
								else
								{
									s.Status = 0;
									s.Num = 0;
								}
							}
						}
					}
					else if (s.Status == 2)
					{
						if (lowList.Count > 1 && macdLowList.Count > 1)
						{
                            var li1 = lowList[lowList.Count - 1];
                            var li2 = lowList[lowList.Count - 2];
                            var l1 = tu.QuoteList[li1];
                            var l2 = tu.QuoteList[li2];

                            var mli1 = macdLowList[macdLowList.Count - 1];
                            var mli2 = macdLowList[macdLowList.Count - 2];
                            var ml1 = macd[mli1];
                            var ml2 = macd[mli2];

                            if (l1.Low < l2.Low && ml1.Macd > ml2.Macd)
							{
								var oriNum = s.Num;
								Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
								if (mode != 2)
								{
									s.Status = 1;
									s.Num = num;
									Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
								}
								else
								{
									s.Status = 0;
									s.Num = 0;
								}
							}
						}
					}
				}
			}
		}
	}
}
