using System;
using System.Collections.Generic;
using System.Text;
using static Model.EnumDef;

namespace stgInterface
{
	public class PlotRecord
	{
		public string ChartName { get; set; }

		public string Name { get; set; }

		public PlotType PT { get; set; }

		public decimal Val { get; set; }

        public object Extra { get; set; }
    }
}
