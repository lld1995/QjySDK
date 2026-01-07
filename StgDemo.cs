using Common;
using Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QjySDK
{
	public class StgDemo : StgBase
    {
		public StgDemo(string id) : base(id)
		{
		}

		public override StgDesc GetStgDesc()
		{
            return null;
		}

		public override void OnBar(EnumDef.Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (tu.QuoteList.Count > 0)
            {
                Console.WriteLine(tu.MktSymbol + "-" + period + "-" + isFinal + ":" + tu.QuoteList.Last().ToJson());
            }
        }

        public override void OnGlobalIndicator(List<TableUnit> tableUnitList)
        {
            base.OnGlobalIndicator(tableUnitList);
            Console.WriteLine("OnGlobalIndicator");
        }

        public override void OnPeriodEnd(EnumDef.Period p, SkQuote q, string mktSymbol)
        {
            base.OnPeriodEnd(p, q, mktSymbol);
        }
    }
}
