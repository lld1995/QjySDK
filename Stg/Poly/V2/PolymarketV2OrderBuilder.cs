using System;
using System.Globalization;
using System.Numerics;

namespace QjySDK.Stg.Poly.V2
{
    /// <summary>Side of a CLOB V2 order.</summary>
    public enum OrderSide { Buy, Sell }

    /// <summary>
    /// Mirrors getOrderRawAmounts + buildOrderCreationArgs in clob-client-v2 (TS).
    /// Converts (price, size, side) into the integer makerAmount / takerAmount that the on-chain
    /// Exchange contract expects (collateral has 6 decimals; conditional token has 6 decimals).
    ///
    /// For BUY:  rawTakerAmt = size (the # of conditional tokens you want to receive)
    ///           rawMakerAmt = size * price  (the collateral you are paying)
    /// For SELL: rawMakerAmt = size (the # of conditional tokens you are providing)
    ///           rawTakerAmt = size * price  (the collateral you want to receive)
    /// </summary>
    public readonly struct PolymarketV2RawOrder
    {
        public readonly OrderSide Side;
        public readonly BigInteger MakerAmount;
        public readonly BigInteger TakerAmount;

        public PolymarketV2RawOrder(OrderSide side, BigInteger makerAmount, BigInteger takerAmount)
        {
            Side = side;
            MakerAmount = makerAmount;
            TakerAmount = takerAmount;
        }
    }

    /// <summary>Per-tick rounding rules. Mirrors ROUNDING_CONFIG in roundingConfig.ts.</summary>
    public readonly struct RoundConfig
    {
        public readonly int PriceDecimals;
        public readonly int SizeDecimals;
        public readonly int AmountDecimals;

        public RoundConfig(int price, int size, int amount) { PriceDecimals = price; SizeDecimals = size; AmountDecimals = amount; }
    }

    public static class PolymarketV2OrderBuilder
    {
        // tickSize -> rounding config
        public static RoundConfig GetRoundConfig(decimal tickSize)
        {
            if (tickSize == 0.1m) return new RoundConfig(1, 2, 3);
            if (tickSize == 0.01m) return new RoundConfig(2, 2, 4);
            if (tickSize == 0.001m) return new RoundConfig(3, 2, 5);
            if (tickSize == 0.0001m) return new RoundConfig(4, 2, 6);
            throw new ArgumentOutOfRangeException(nameof(tickSize), $"Unsupported tick size {tickSize}");
        }

        /// <summary>
        /// Returns the raw integer maker/taker amounts in collateral-token base units (10^6).
        /// </summary>
        /// <param name="tickSize">Market's minimum tick size; controls rounding precision.</param>
        public static PolymarketV2RawOrder Build(OrderSide side, decimal price, decimal size, decimal tickSize)
        {
            var cfg = GetRoundConfig(tickSize);
            var rawPrice = RoundNormal(price, cfg.PriceDecimals);

            decimal rawMaker, rawTaker;
            if (side == OrderSide.Buy)
            {
                rawTaker = RoundDown(size, cfg.SizeDecimals);
                rawMaker = rawTaker * rawPrice;
                if (DecimalPlaces(rawMaker) > cfg.AmountDecimals)
                {
                    rawMaker = RoundUp(rawMaker, cfg.AmountDecimals + 4);
                    if (DecimalPlaces(rawMaker) > cfg.AmountDecimals)
                        rawMaker = RoundDown(rawMaker, cfg.AmountDecimals);
                }
            }
            else
            {
                rawMaker = RoundDown(size, cfg.SizeDecimals);
                rawTaker = rawMaker * rawPrice;
                if (DecimalPlaces(rawTaker) > cfg.AmountDecimals)
                {
                    rawTaker = RoundUp(rawTaker, cfg.AmountDecimals + 4);
                    if (DecimalPlaces(rawTaker) > cfg.AmountDecimals)
                        rawTaker = RoundDown(rawTaker, cfg.AmountDecimals);
                }
            }

            return new PolymarketV2RawOrder(
                side,
                ToBaseUnits(rawMaker, PolymarketV2Constants.CollateralTokenDecimals),
                ToBaseUnits(rawTaker, PolymarketV2Constants.CollateralTokenDecimals));
        }

        // --- decimal helpers (mirror utilities.ts in TS client) ---

        private static decimal RoundNormal(decimal value, int decimals)
        {
            if (DecimalPlaces(value) <= decimals) return value;
            return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        }

        private static decimal RoundDown(decimal value, int decimals)
        {
            if (DecimalPlaces(value) <= decimals) return value;
            var factor = Pow10(decimals);
            return Math.Floor(value * factor) / factor;
        }

        private static decimal RoundUp(decimal value, int decimals)
        {
            if (DecimalPlaces(value) <= decimals) return value;
            var factor = Pow10(decimals);
            return Math.Ceiling(value * factor) / factor;
        }

        private static int DecimalPlaces(decimal value)
        {
            var bits = decimal.GetBits(value);
            return (bits[3] >> 16) & 0x7F;
        }

        private static decimal Pow10(int n)
        {
            decimal r = 1m;
            for (int i = 0; i < n; i++) r *= 10m;
            return r;
        }

        /// <summary>
        /// Converts a decimal amount to a BigInteger in base units (i.e. value * 10^decimals).
        /// Equivalent to viem's parseUnits.
        /// </summary>
        public static BigInteger ToBaseUnits(decimal value, int decimals)
        {
            if (value < 0) throw new ArgumentException("amount must be non-negative");

            // Use string round-trip to avoid double conversion artifacts on values like 0.1.
            // Decimal supports up to 28-29 significant digits which is more than enough for our 6-decimal output.
            var shifted = value * Pow10(decimals);
            shifted = Math.Truncate(shifted); // any fractional remainder past base units is dropped (TS parseUnits also truncates)

            // BigInteger.Parse handles decimal strings via custom format.
            var asString = shifted.ToString("F0", CultureInfo.InvariantCulture);
            return BigInteger.Parse(asString, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Random salt mirroring TS `Math.round(Math.random() * Date.now())`.
        /// We use a Crypto-safe source for entropy but cap the magnitude to stay within JSON integer safety.
        /// </summary>
        public static long GenerateOrderSalt()
        {
            // Match TS upper bound (Math.random() in [0,1) * Date.now() up to ~1.7e12 → ~1.7e12 worst case).
            // Use System.Random with cryptographically generated seed each call to stay deterministic to the order, not the call site.
            var bytes = new byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            var v = BitConverter.ToInt64(bytes, 0);
            // Clamp to positive 48-bit range so JSON parsers (TS server is JS) don't lose precision (Number.MAX_SAFE_INTEGER = 2^53-1).
            v = Math.Abs(v) & 0x0000FFFFFFFFFFFFL;
            if (v == 0) v = 1;
            return v;
        }
    }
}
