using System;
using System.Collections.Generic;
using System.Text;
using static Model.EnumDef;

namespace Model
{
    public class Symbol
    {
        public decimal margin_ratio { get; set; }
        public decimal multiplier { get; set; }
        public int symbol_type { get; set; }
    }
}
