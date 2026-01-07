using Model;
using System;
using System.Collections.Generic;
using System.Text;
using static Model.EnumDef;

namespace stgInterface
{
    public class RemoteTradeRecord
    {

        public decimal Price { get; set; }

        public EnumDef.OrderType OT { get; set; }

        public string MktSymbol { get; set; }

        public decimal Num { get; set; }

        public Period P { get; set; }

        public int SendMode { get; set; }
    }
}
