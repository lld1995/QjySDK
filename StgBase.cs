using Common;
using Model;
using OpenAI.Graders;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Model.EnumDef;

namespace QjySDK
{
    public abstract class StgBase
    {
        public StgBase(string id)
        {
            Id = id;
        }

		public abstract StgDesc GetStgDesc();
		protected Dictionary<string, object> ArgDic { get; set; }
		public string Id;
        private SimpleTcpClient _stc = null;

        private List<RemoteTradeRecord> _rtr = new List<RemoteTradeRecord>();
        private List<PlotRecord> _pr = new List<PlotRecord>();
        public virtual void OnPeriodEnd(Period p, SkQuote q, string mktSymbol)
        {

        }

        public virtual void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {

        }

        public virtual void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {

        }

        public async Task PushAndClear()
        {
            var dic = new Dictionary<string, object>();
            dic["oper"] = "rc";
            dic["id"] = Id;
            dic["rtr"] = _rtr;
            dic["pr"] = _pr;
            await _stc.SendMessageAsync(dic.ToJson());
            _rtr.Clear();
            _pr.Clear();
        }

        public void Trade(string mktSymbol, OrderType ot, decimal price, decimal num, Period p, int sendMode)
        {
            var rtr = new RemoteTradeRecord();
            rtr.MktSymbol = mktSymbol;
            rtr.OT = ot;
            rtr.Price = price;
            rtr.Num = num;
            rtr.P = p;
            rtr.SendMode = sendMode;
            _rtr.Add(rtr);
        }

        public void Plot(string chartName, string name, PlotType pt, double val, object extra = null)
        {
            var pr = new PlotRecord();
            pr.ChartName = chartName;
            pr.Name = name;
            pr.PT = pt;
            pr.Val = (decimal)val;
            pr.Extra= extra;
            _pr.Add(pr);
        }

        public async Task Run()
        {
            var sd=GetStgDesc();
            if (sd == null)
            {

            }
            else
            {
                ArgDic = sd.ArgDic;
            }
            _stc = new SimpleTcpClient(this);
            await _stc.ConnectAsync("127.0.0.1", 30898);

            var dic = new Dictionary<string, object>();
            dic["oper"] = "start";
            dic["id"] = Id;
            dic["sd"] = sd;
            await _stc.SendMessageAsync(dic.ToJson());
        }
    }
}
