using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    public class ReverseShadow : StgBase
    {
        public ReverseShadow()
        {
        }

        public ReverseShadow(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.ArgDic["lookbackPeriods"] = 20;
            sd.ArgDic["atrLookbackPeriods"] = 20;
            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;
            sd.ArgDic["shadowRate"] = 3m;

            //手数控制
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };


            sd.ArgDescDic["atrLookbackPeriods"] = new ArgDesc() { Text = "ATR回溯周期", Explain = "ATR计算的回溯周期", Type = "number" };


            sd.ArgDescDic["lookbackPeriods"] = new ArgDesc() { Text = "回溯周期", Explain = "指标计算回溯周期", Type = "number" };


            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };


            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };


            sd.ArgDescDic["shadowRate"] = new ArgDesc() { Text = "影线比例", Explain = "影线与实体的最小比例", Type = "number" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;

			sd.ColorDic["vol-vol"] = "#2196F3";
			return sd;
        }

        private class State
        {
            public int Status { get; set; }

            public decimal Num { get; set; }

            public decimal LastOpenPrice { get; set; }

            public int AddNum { get; set; }

            public decimal LossPrice { get; set; }
            public decimal WinPrice { get; set; }
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
                //if (tu.QuoteList.Count > observePeriod)
                {
                    int mode = Convert.ToInt32(ArgDic["mode"]);
                    int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
                    var q = tu.QuoteList[tu.QuoteList.Count - 1];
                    var q2 = tu.QuoteList[tu.QuoteList.Count - 2];
                    var shadowRate = Convert.ToDecimal(ArgDic["shadowRate"]);

                    var num = 1.0m;
                    var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
                    if (lotsMode == 1)
                    {
                        var s2 = GetSymbol(tu.MktSymbol);
                        num = (Convert.ToDecimal(ArgDic["money"]) / (q.Close * s2.multiplier * s2.margin_ratio));
                        if (s2.symbol_type == (int)SymbolType.COIN)
                        {
                            num = (int)(num * 1000) / 1000.0m;
                        }
                        else
                        {
                            num = (int)num;
                        }
                    }

                    Plot("vol", "vol", PlotType.RECTANGLE, (double?)q.Volume);

                    AtrResult atr1 = null;
                    if (s.Status == 0)
                    {
						var atr = tu.QuoteList.GetAtr(Convert.ToInt32(ArgDic["atrLookbackPeriods"])).ToList();
						atr1 = atr[atr.Count - 1];
					}
                  

                    var lowIndex = 0;
                    var highIndex = 0;
                    var lastLow = q2;
                    var lastHigh = q2;
                    var lookbackPeriods = Convert.ToInt32(ArgDic["lookbackPeriods"]);
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
                    var isDownShadow = false;
                    var isUpShadow = false;
                    var downShadow = 0m;
                    var upShadow = 0m;
					{
                        var entity = Math.Abs(q.Close - q.Open);
                        if (q.Low < lastLow.Low)
                        {
                            downShadow = q.Open - q.Low;
                            if (downShadow > shadowRate * entity)
                            {
                                isDownShadow = true;
                            }
                        }
                    }
                    {
                        var entity = Math.Abs(q.Open - q.Close);
                        if (q.High > lastHigh.High)
                        {
                            upShadow = q.High - q.Open;
                            if (upShadow > shadowRate * entity)
                            {
                                isUpShadow = true;
                            }
                        }
                    }
                    if (s.Status == 0)
                    {
                        if (isDownShadow && mode != 2)
                        {
                            var isOpen = true;
                            for (int i = tu.QuoteList.Count - 2; i >=0&& i>= tu.QuoteList.Count - 5; --i)
                            {
                                var qi = tu.QuoteList[i];
                                var entity = Math.Abs(qi.Close - qi.Open);
                                if (downShadow < entity)
                                {
                                    isOpen = false;
                                    break;
                                }
                                if (q.Volume < 2 * qi.Volume)
                                {
									isOpen = false;
									break;
								}
                            }
                            if (isOpen)
                            {
								s.Status = 1;
								s.Num = num;
								s.AddNum = 0;
								if (atr1.Atr == null)
								{
									s.LossPrice = q.Low ;
								}
                                else
                                {
									s.LossPrice = q.Low - (decimal)atr1.Atr.Value;
								}
								s.WinPrice = q.Close * 1.01m;
								Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
							}
                        }
                        else if (isUpShadow && mode != 1)
                        {
							var isOpen = true;
							for (int i = tu.QuoteList.Count - 2; i >= 0 && i >= tu.QuoteList.Count - 5; --i)
							{
								var qi = tu.QuoteList[i];
								var entity = Math.Abs(qi.Close - qi.Open);
								if (upShadow < entity)
								{
									isOpen = false;
									break;
								}
                                if (q.Volume < 2 * qi.Volume)
                                {
									isOpen = false;
									break;
								}
							}
                            if (isOpen)
                            {
								s.Status = 2;
								s.Num = num;
								s.AddNum = 0;
								if (atr1.Atr == null)
								{
                                    s.LossPrice = q.High;
								}
								else
								{
									s.LossPrice = q.High + (decimal)atr1.Atr.Value;
								}
								s.WinPrice = q.Close * 0.99m;
								Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
							}
                        }
                    }
					else if (s.Status == 1)
					{
						if (q.Close < s.LossPrice || (isUpShadow && q.Close > s.WinPrice))
						{
							var oriNum = s.Num;
							s.Status = 0;
							s.AddNum = 0;
							s.Num = 0;
							Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
						}
					}
					else if (s.Status == 2)
					{
						if (q.Close > s.LossPrice || (isDownShadow && q.Close < s.WinPrice))
						{
							var oriNum = s.Num;
							s.Status = 0;
							s.AddNum = 0;
							s.Num = 0;
							Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
						}
					}
				}
            }
        }
    }
}
