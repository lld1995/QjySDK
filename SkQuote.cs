using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Text;

namespace Common
{
	public class CacheSkQuote
	{
		public SkQuote SQ { get; set; }

		public bool IsFinal { get; set; }

		public DateTimeOffset LastSrcDto { get; set; }
	}

    public class SkQuote : Quote
    {
		public decimal Amount { get; set; }

		public SkQuote()
		{

		}

		public SkQuote(SkQuote skq)
		{
			this.Amount = skq.Amount;
			this.Date = skq.Date;
			this.Open = skq.Open;
			this.High = skq.High;
			this.Low = skq.Low;
			this.Close = skq.Close;
			this.Volume = skq.Volume;
		}
    }
}
