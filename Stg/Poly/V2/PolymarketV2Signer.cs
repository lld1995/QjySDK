using System;
using System.Linq;
using System.Numerics;
using System.Text;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer;
using Nethereum.Util;

namespace QjySDK.Stg.Poly.V2
{
    /// <summary>
    /// The complete signed order ready to be POSTed to /order.
    /// All numeric fields are kept as strings/BigInteger to preserve precision over JSON.
    /// </summary>
    public sealed class PolymarketV2SignedOrder
    {
        public long Salt { get; init; }
        public string Maker { get; init; } = "";
        public string Signer { get; init; } = "";
        public string Taker { get; init; } = PolymarketV2Constants.AddressZero;
        public string TokenId { get; init; } = "";
        public BigInteger MakerAmount { get; init; }
        public BigInteger TakerAmount { get; init; }
        public OrderSide Side { get; init; }
        public byte SignatureType { get; init; }
        public long TimestampMs { get; init; }
        public long ExpirationSec { get; init; }
        public string Metadata { get; init; } = PolymarketV2Constants.Bytes32Zero;
        public string Builder { get; init; } = PolymarketV2Constants.Bytes32Zero;
        public string Signature { get; init; } = "";
    }

    /// <summary>
    /// Constructs and signs Polymarket CTF Exchange V2 orders, including the ERC-7739 wrapping for POLY_1271 deposit wallets.
    /// Byte-exact port of src/order-utils/exchangeOrderBuilderV2.ts.
    /// </summary>
    public static class PolymarketV2Signer
    {
        private static readonly Sha3Keccack Keccak = Sha3Keccack.Current;

        private static readonly byte[] OrderTypeHash =
            Keccak.CalculateHash(Encoding.ASCII.GetBytes(PolymarketV2Constants.OrderTypeString));

        private static readonly byte[] DomainTypeHash =
            Keccak.CalculateHash(Encoding.ASCII.GetBytes(PolymarketV2Constants.EIP712DomainTypeString));

        // Wrapper for ERC-7739:
        //   TypedDataSign(Order contents,string name,string version,uint256 chainId,address verifyingContract,bytes32 salt)Order(...)
        // The encoded type string is TypedDataSign followed by Order (referenced types in lexicographic order — Order is the only ref).
        private static readonly byte[] TypedDataSignTypeHash =
            Keccak.CalculateHash(Encoding.ASCII.GetBytes(
                PolymarketV2Constants.TypedDataSignTypeString + PolymarketV2Constants.OrderTypeString));

        /// <summary>
        /// Build and sign a CLOB V2 order.
        /// </summary>
        /// <param name="privateKeyHex">Signer EOA private key (32 bytes, with or without 0x).</param>
        /// <param name="maker">Address of the funds source (for POLY_1271 this is the deposit wallet).</param>
        /// <param name="orderSignerAddress">
        /// Signer field of the order. For SignatureType 0/1/2 this is the EOA; for POLY_1271 (3) this MUST equal maker.
        /// </param>
        /// <param name="tokenId">CLOB token id (decimal string).</param>
        /// <param name="raw">Pre-computed makerAmount/takerAmount/side (PolymarketV2OrderBuilder.Build output).</param>
        /// <param name="signatureType">0 EOA / 1 POLY_PROXY / 2 POLY_GNOSIS_SAFE / 3 POLY_1271</param>
        /// <param name="negRisk">True if the market is a NegRisk market. Selects which Exchange V2 contract domain to sign against.</param>
        /// <param name="expirationSec">Order expiration (unix seconds; 0 = no expiry). Not part of the signed struct, only the wire body.</param>
        public static PolymarketV2SignedOrder BuildAndSign(
            string privateKeyHex,
            string maker,
            string orderSignerAddress,
            string tokenId,
            PolymarketV2RawOrder raw,
            byte signatureType,
            bool negRisk,
            long expirationSec = 0,
            string? erc1271WalletForWrap = null)
        {
            if (string.IsNullOrWhiteSpace(privateKeyHex)) throw new ArgumentException("privateKey required", nameof(privateKeyHex));
            if (string.IsNullOrWhiteSpace(maker)) throw new ArgumentException("maker required", nameof(maker));
            if (string.IsNullOrWhiteSpace(orderSignerAddress)) throw new ArgumentException("orderSigner required", nameof(orderSignerAddress));
            if (string.IsNullOrWhiteSpace(tokenId)) throw new ArgumentException("tokenId required", nameof(tokenId));

            // For the canonical POLY_1271 flow signer == maker == deposit wallet. We allow callers to override
            // (set erc1271WalletForWrap explicitly) to experiment with signer = EOA while still wrapping the
            // signature against the deposit wallet contract for on-chain ERC-1271 validation.
            if (signatureType == PolymarketV2Constants.SigTypePoly1271
                && erc1271WalletForWrap == null
                && !orderSignerAddress.Equals(maker, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("POLY_1271 requires order.signer == order.maker == deposit wallet (or pass erc1271WalletForWrap explicitly)");

            var salt = PolymarketV2OrderBuilder.GenerateOrderSalt();
            var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var verifyingContract = PolymarketV2Constants.GetExchangeAddress(negRisk);

            // 1) Compute the EIP-712 struct hash for the Order.
            var contentsHash = HashOrderStruct(
                salt: salt,
                maker: maker,
                signer: orderSignerAddress,
                tokenId: BigInteger.Parse(tokenId),
                makerAmount: raw.MakerAmount,
                takerAmount: raw.TakerAmount,
                side: raw.Side == OrderSide.Buy ? PolymarketV2Constants.SideBuy : PolymarketV2Constants.SideSell,
                signatureType: signatureType,
                timestampMs: timestampMs,
                metadata: PolymarketV2Constants.Bytes32Zero,
                builder: PolymarketV2Constants.Bytes32Zero);

            // 2) Compute the app domain separator (CTF Exchange V2).
            var appDomainSep = HashAppDomain(verifyingContract, PolymarketV2Constants.PolygonChainId);

            string signatureHex;
            if (signatureType != PolymarketV2Constants.SigTypePoly1271)
            {
                // Standard EIP-712 v4 signing: digest = keccak256("\x19\x01" || appDomainSep || contentsHash)
                var digest = ComputeEip712Digest(appDomainSep, contentsHash);
                signatureHex = SignDigest(privateKeyHex, digest);
            }
            else
            {
                // ERC-7739 POLY_1271 path. The signed payload is TypedDataSign{contents, name, version, chainId, verifyingContract, salt}
                // under the CTF Exchange V2 domain (NOT the wallet domain). The wallet's name/version/chainId/verifyingContract are
                // INSIDE the message as ordinary fields. Hash of the wallet-side metadata makes the signature uniquely bound to that wallet.
                // The ERC-7739 wrapper's verifyingContract must be the contract whose isValidSignature will be
                // invoked on-chain (i.e. the deposit wallet). Default to orderSignerAddress for backwards
                // compat; allow callers to override via erc1271WalletForWrap so we can test signer = EOA.
                var wrapVerifying = erc1271WalletForWrap ?? orderSignerAddress;
                var typedDataSignStructHash = HashTypedDataSignStruct(
                    contentsHash,
                    name: PolymarketV2Constants.DepositWalletInnerDomainName,
                    version: PolymarketV2Constants.DepositWalletInnerDomainVersion,
                    chainId: PolymarketV2Constants.PolygonChainId,
                    verifyingContract: wrapVerifying,
                    saltBytes32Hex: PolymarketV2Constants.Bytes32Zero);

                var digest = ComputeEip712Digest(appDomainSep, typedDataSignStructHash);
                var innerSig = SignDigest(privateKeyHex, digest); // 65-byte 0x-prefixed hex

                // Final wire signature = innerSig (65) || appDomainSep (32) || contentsHash (32) || ORDER_TYPE_STRING (ascii) || uint16BE(len)
                var orderTypeBytes = Encoding.ASCII.GetBytes(PolymarketV2Constants.OrderTypeString);
                var lenBe = new byte[2] { (byte)(orderTypeBytes.Length >> 8), (byte)(orderTypeBytes.Length & 0xFF) };

                var combined = Concat(
                    innerSig.HexToByteArray(),
                    appDomainSep,
                    contentsHash,
                    orderTypeBytes,
                    lenBe);
                signatureHex = "0x" + combined.ToHex();
            }

            return new PolymarketV2SignedOrder
            {
                Salt = salt,
                Maker = maker,
                Signer = orderSignerAddress,
                Taker = PolymarketV2Constants.AddressZero,
                TokenId = tokenId,
                MakerAmount = raw.MakerAmount,
                TakerAmount = raw.TakerAmount,
                Side = raw.Side,
                SignatureType = signatureType,
                TimestampMs = timestampMs,
                ExpirationSec = expirationSec,
                Metadata = PolymarketV2Constants.Bytes32Zero,
                Builder = PolymarketV2Constants.Bytes32Zero,
                Signature = signatureHex,
            };
        }

        /// <summary>
        /// keccak256(abi.encode(ORDER_TYPE_HASH, salt, maker, signer, tokenId, makerAmount, takerAmount, side, signatureType, timestamp, metadata, builder)).
        /// </summary>
        private static byte[] HashOrderStruct(
            long salt, string maker, string signer, BigInteger tokenId,
            BigInteger makerAmount, BigInteger takerAmount, byte side, byte signatureType,
            long timestampMs, string metadata, string builder)
        {
            using var ms = new System.IO.MemoryStream();
            ms.Write(OrderTypeHash);
            ms.Write(EncodeUint256(new BigInteger(salt)));
            ms.Write(EncodeAddress(maker));
            ms.Write(EncodeAddress(signer));
            ms.Write(EncodeUint256(tokenId));
            ms.Write(EncodeUint256(makerAmount));
            ms.Write(EncodeUint256(takerAmount));
            ms.Write(EncodeUint256(new BigInteger(side)));          // uint8 -> 32 bytes left-padded
            ms.Write(EncodeUint256(new BigInteger(signatureType))); // uint8 -> 32 bytes left-padded
            ms.Write(EncodeUint256(new BigInteger(timestampMs)));
            ms.Write(EncodeBytes32(metadata));
            ms.Write(EncodeBytes32(builder));
            return Keccak.CalculateHash(ms.ToArray());
        }

        private static byte[] HashTypedDataSignStruct(
            byte[] contentsHash, string name, string version, int chainId, string verifyingContract, string saltBytes32Hex)
        {
            // TypedDataSign(Order contents, string name, string version, uint256 chainId, address verifyingContract, bytes32 salt)
            // Encoded as: typeHash || hashStruct(Order=contentsHash) || keccak(name) || keccak(version) || chainId || verifyingContract || salt
            using var ms = new System.IO.MemoryStream();
            ms.Write(TypedDataSignTypeHash);
            ms.Write(contentsHash); // nested struct already hashed
            ms.Write(Keccak.CalculateHash(Encoding.ASCII.GetBytes(name)));
            ms.Write(Keccak.CalculateHash(Encoding.ASCII.GetBytes(version)));
            ms.Write(EncodeUint256(new BigInteger(chainId)));
            ms.Write(EncodeAddress(verifyingContract));
            ms.Write(EncodeBytes32(saltBytes32Hex));
            return Keccak.CalculateHash(ms.ToArray());
        }

        private static byte[] HashAppDomain(string verifyingContract, int chainId)
        {
            using var ms = new System.IO.MemoryStream();
            ms.Write(DomainTypeHash);
            ms.Write(Keccak.CalculateHash(Encoding.ASCII.GetBytes(PolymarketV2Constants.CtfExchangeDomainName)));
            ms.Write(Keccak.CalculateHash(Encoding.ASCII.GetBytes(PolymarketV2Constants.CtfExchangeDomainVersion)));
            ms.Write(EncodeUint256(new BigInteger(chainId)));
            ms.Write(EncodeAddress(verifyingContract));
            return Keccak.CalculateHash(ms.ToArray());
        }

        private static byte[] ComputeEip712Digest(byte[] domainSep, byte[] structHash)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x19);
            ms.WriteByte(0x01);
            ms.Write(domainSep);
            ms.Write(structHash);
            return Keccak.CalculateHash(ms.ToArray());
        }

        /// <summary>
        /// Signs a 32-byte digest with the given EOA private key and returns a 65-byte 0x-prefixed signature (r||s||v),
        /// where v ∈ {27, 28}.
        /// </summary>
        public static string SignDigest(string privateKeyHex, byte[] digest)
        {
            if (digest.Length != 32) throw new ArgumentException("digest must be 32 bytes");
            var key = new EthECKey(privateKeyHex);
            var sig = key.SignAndCalculateV(digest);

            // Nethereum returns sig.V as a single byte representing 27/28 OR 0/1 depending on path; normalize to 27/28.
            byte v = sig.V[0];
            if (v < 27) v = (byte)(v + 27);

            var r = PadLeft(sig.R, 32);
            var s = PadLeft(sig.S, 32);

            var bytes = new byte[65];
            Buffer.BlockCopy(r, 0, bytes, 0, 32);
            Buffer.BlockCopy(s, 0, bytes, 32, 32);
            bytes[64] = v;
            return "0x" + bytes.ToHex();
        }

        // --- ABI encoding helpers (just enough for our types) ---

        public static byte[] EncodeUint256(BigInteger value)
        {
            if (value.Sign < 0) throw new ArgumentException("uint256 cannot be negative");
            var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            return PadLeft(raw, 32);
        }

        public static byte[] EncodeAddress(string addressHex)
        {
            var bytes = addressHex.RemoveHexPrefix().HexToByteArray();
            if (bytes.Length != 20) throw new ArgumentException($"address must be 20 bytes, got {bytes.Length} ({addressHex})");
            return PadLeft(bytes, 32);
        }

        public static byte[] EncodeBytes32(string hex)
        {
            var bytes = hex.RemoveHexPrefix().HexToByteArray();
            if (bytes.Length != 32) throw new ArgumentException($"bytes32 must be exactly 32 bytes, got {bytes.Length} ({hex})");
            return bytes;
        }

        private static byte[] PadLeft(byte[] data, int length)
        {
            if (data.Length == length) return data;
            if (data.Length > length) throw new ArgumentException("data too long for pad");
            var padded = new byte[length];
            Buffer.BlockCopy(data, 0, padded, length - data.Length, data.Length);
            return padded;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (var p in parts) total += p.Length;
            var result = new byte[total];
            int off = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, result, off, p.Length); off += p.Length; }
            return result;
        }
    }
}
