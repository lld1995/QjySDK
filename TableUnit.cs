using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Model.EnumDef;

namespace Common
{
    public partial class TableUnit
    {
        public List<SkQuote> QuoteList { get; set; }

        public string MktSymbol { get; set; }

        public Period Period { get; set; }

		public string GetStateKey() { return MktSymbol + "_" + Period; }
	}
}
