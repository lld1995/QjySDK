using System;

namespace QjySDK.Stg.Poly.V2
{
    /// <summary>
    /// Polymarket CLOB V2 protocol constants. Mirrors clob-client-v2 (TS) for byte-exact compatibility.
    ///
    /// Source references (paths in the official TS client repo):
    ///   - src/config.ts                                   (Exchange V2 addresses on Polygon)
    ///   - src/order-utils/model/ctfExchangeV2TypedData.ts (Domain name/version + Order struct)
    ///   - src/order-utils/model/signatureTypeV2.ts        (SignatureType enum)
    ///   - src/order-utils/exchangeOrderBuilderV2.ts       (Order EIP-712 + POLY_1271 ERC-7739 wrap)
    /// </summary>
    public static class PolymarketV2Constants
    {
        public const string CtfExchangeDomainName = "Polymarket CTF Exchange";
        public const string CtfExchangeDomainVersion = "2";

        // Polygon mainnet (chainId 137) CTF Exchange V2 contracts.
        // Binary (non-NegRisk) markets use ExchangeV2; NegRisk markets use NegRiskExchangeV2.
        public const string ExchangeV2 = "0xE111180000d2663C0091e4f400237545B87B996B";
        public const string NegRiskExchangeV2 = "0xe2222d279d744050d28e00520010520000310F59";

        // pUSD / USDC.e collateral on Polygon (V1+V2 share the same token decimals).
        public const int CollateralTokenDecimals = 6;

        // Order EIP-712 type string. Fields and order MUST match the contract.
        // expiration is intentionally NOT included — V2 keeps it in the wire body but excludes it from the signed struct.
        public const string OrderTypeString =
            "Order(uint256 salt,address maker,address signer,uint256 tokenId,uint256 makerAmount,uint256 takerAmount,uint8 side,uint8 signatureType,uint256 timestamp,bytes32 metadata,bytes32 builder)";

        public const string EIP712DomainTypeString =
            "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)";

        // TypedDataSign wrapper for ERC-7739 (POLY_1271). The wrapper concatenates the Order type after TypedDataSign
        // so the full encoded type string is: TypedDataSign + Order.
        public const string TypedDataSignTypeString =
            "TypedDataSign(Order contents,string name,string version,uint256 chainId,address verifyingContract,bytes32 salt)";

        // Wallet domain used inside the ERC-7739 wrap. Always these literals, regardless of which exchange the order targets.
        public const string DepositWalletInnerDomainName = "DepositWallet";
        public const string DepositWalletInnerDomainVersion = "1";

        // SignatureType wire enum (uint8 on-chain, signatureType field in JSON order body)
        public const byte SigTypeEoa = 0;
        public const byte SigTypePolyProxy = 1;
        public const byte SigTypePolyGnosisSafe = 2;
        public const byte SigTypePoly1271 = 3;

        // Side encoding: BUY=0, SELL=1 in the signed struct; "BUY"/"SELL" in the wire JSON.
        public const byte SideBuy = 0;
        public const byte SideSell = 1;

        public const string Bytes32Zero = "0x0000000000000000000000000000000000000000000000000000000000000000";
        public const string AddressZero = "0x0000000000000000000000000000000000000000";

        public const int PolygonChainId = 137;

        public const string ClobHost = "https://clob.polymarket.com";
        public const string PostOrderPath = "/order";
        public const string CancelOrderPath = "/order";

        /// <summary>
        /// Selects the verifying contract for the order's EIP-712 domain based on whether the market is a NegRisk market.
        /// </summary>
        public static string GetExchangeAddress(bool negRisk)
            => negRisk ? NegRiskExchangeV2 : ExchangeV2;
    }

    /// <summary>Time-in-force for V2 orders. See OpenAPI spec /order request body.</summary>
    public enum PolymarketV2OrderType
    {
        GTC, // Good Till Cancel  (limit order resting on book)
        FOK, // Fill Or Kill      (market order, fully fill or cancel)
        GTD, // Good Till Date    (limit order with expiration)
        FAK, // Fill And Kill     (market order, partial fill ok, rest cancel)
    }
}
