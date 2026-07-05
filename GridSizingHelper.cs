using System;
using System.Collections.Generic;
using System.Linq;

namespace Common
{
	public static class GridSizingHelper
	{
		public static decimal CalculateDynamicGridPercent(
			IList<SkQuote> quotes,
			decimal currentPrice,
			decimal fallbackGridPercent,
			int atrPeriod,
			decimal atrMultiplier,
			decimal minGridPercent,
			decimal maxGridPercent,
			decimal previousGridPercent = 0m,
			decimal smoothFactor = 0.35m)
		{
			if (maxGridPercent <= 0) maxGridPercent = 5m;
			if (minGridPercent <= 0) minGridPercent = 0.2m;
			if (maxGridPercent < minGridPercent) maxGridPercent = minGridPercent;
			if (atrPeriod < 2) atrPeriod = 14;
			if (atrMultiplier <= 0) atrMultiplier = 1.2m;

			var fallback = Clamp(fallbackGridPercent, minGridPercent, maxGridPercent);
			if (quotes == null || quotes.Count < 2 || currentPrice <= 0)
			{
				return SmoothGridPercent(previousGridPercent, fallback, smoothFactor, minGridPercent, maxGridPercent);
			}

			decimal atrPercent = CalculateAtrPercent(quotes, atrPeriod, currentPrice);
			decimal medianTrueRangePercent = CalculateMedianTrueRangePercent(quotes, atrPeriod);
			decimal raw;
			if (atrPercent > 0 && medianTrueRangePercent > 0)
			{
				raw = (atrPercent * 0.7m + medianTrueRangePercent * 0.3m) * atrMultiplier;
			}
			else if (atrPercent > 0)
			{
				raw = atrPercent * atrMultiplier;
			}
			else if (medianTrueRangePercent > 0)
			{
				raw = medianTrueRangePercent * atrMultiplier;
			}
			else
			{
				raw = fallback;
			}

			return SmoothGridPercent(previousGridPercent, Clamp(raw, minGridPercent, maxGridPercent), smoothFactor, minGridPercent, maxGridPercent);
		}

		private static decimal CalculateAtrPercent(IList<SkQuote> quotes, int period, decimal currentPrice)
		{
			if (quotes == null || quotes.Count < 2 || currentPrice <= 0) return 0;
			int start = Math.Max(1, quotes.Count - period);
			decimal sum = 0;
			int count = 0;
			for (int i = start; i < quotes.Count; i++)
			{
				decimal tr = CalculateTrueRange(quotes[i], quotes[i - 1].Close);
				if (tr > 0)
				{
					sum += tr;
					count++;
				}
			}
			return count == 0 ? 0 : sum / count / currentPrice * 100m;
		}

		private static decimal CalculateMedianTrueRangePercent(IList<SkQuote> quotes, int period)
		{
			if (quotes == null || quotes.Count < 2) return 0;
			var ranges = new List<decimal>();
			int start = Math.Max(1, quotes.Count - period);
			for (int i = start; i < quotes.Count; i++)
			{
				decimal close = quotes[i].Close;
				if (close <= 0) continue;
				decimal tr = CalculateTrueRange(quotes[i], quotes[i - 1].Close);
				if (tr > 0) ranges.Add(tr / close * 100m);
			}
			if (ranges.Count == 0) return 0;
			ranges.Sort();
			int mid = ranges.Count / 2;
			return ranges.Count % 2 == 1 ? ranges[mid] : (ranges[mid - 1] + ranges[mid]) / 2m;
		}

		private static decimal CalculateTrueRange(SkQuote quote, decimal previousClose)
		{
			decimal highLow = quote.High - quote.Low;
			decimal highPrevClose = Math.Abs(quote.High - previousClose);
			decimal lowPrevClose = Math.Abs(quote.Low - previousClose);
			return Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose));
		}

		private static decimal SmoothGridPercent(decimal previous, decimal current, decimal smoothFactor, decimal min, decimal max)
		{
			if (previous <= 0) return Clamp(current, min, max);
			if (smoothFactor <= 0) return Clamp(current, min, max);
			if (smoothFactor > 1) smoothFactor = 1;
			return Clamp(previous * (1 - smoothFactor) + current * smoothFactor, min, max);
		}

		private static decimal Clamp(decimal value, decimal min, decimal max)
		{
			if (value < min) return min;
			if (value > max) return max;
			return value;
		}
	}
}
