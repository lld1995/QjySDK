using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Util;

namespace QjySDK.Stg.Poly.V2
{
    /// <summary>
    /// L1 authentication for Polymarket CLOB. Signs the ClobAuth EIP-712 message and POSTs
    /// to /auth/api-key.
    ///
    /// For deposit wallet users we want the resulting API key to be bound to the deposit wallet
    /// (not the EOA), so that POLY_1271 orders pass the server-side check
    /// "order.signer must equal API KEY's address". To do this we set
    /// `value.address = depositWalletAddress` and `POLY_ADDRESS = depositWalletAddress`. The
    /// server validates the signature via ERC-1271 (the deposit wallet's `isValidSignature`
    /// returns OK for ECDSA signatures by its owner EOA) and then binds the new key to the
    /// deposit wallet.
    /// </summary>
    public sealed class PolymarketV2L1Auth : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _host;

        private const string CLOB_DOMAIN_NAME = "ClobAuthDomain";
        private const string CLOB_VERSION = "1";
        private const string MSG_TO_SIGN = "This message attests that I control the given wallet";

        private const string CLOB_AUTH_TYPE_STRING = "ClobAuth(address address,string timestamp,uint256 nonce,string message)";

        // EIP-712 type hashes (precomputed at startup).
        private static readonly byte[] EIP712_DOMAIN_TYPEHASH = Sha3Keccack.Current.CalculateHash(
            Encoding.ASCII.GetBytes("EIP712Domain(string name,string version,uint256 chainId)"));
        private static readonly byte[] CLOB_AUTH_TYPEHASH = Sha3Keccack.Current.CalculateHash(
            Encoding.ASCII.GetBytes(CLOB_AUTH_TYPE_STRING));
        // ERC-7739 wrap for ClobAuth: TypedDataSign concatenated with referenced types (ClobAuth alphabetically first/only).
        private static readonly byte[] TYPED_DATA_SIGN_CLOBAUTH_TYPEHASH = Sha3Keccack.Current.CalculateHash(
            Encoding.ASCII.GetBytes(
                "TypedDataSign(ClobAuth contents,string name,string version,uint256 chainId,address verifyingContract,bytes32 salt)"
                + CLOB_AUTH_TYPE_STRING));

        public PolymarketV2L1Auth(string host = PolymarketV2Constants.ClobHost, string? proxyUrl = null)
        {
            _host = host.TrimEnd('/');
            HttpClientHandler handler;
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                var proxyUri = proxyUrl.Contains("://") ? new Uri(proxyUrl) : new Uri("http://" + proxyUrl);
                handler = new HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(proxyUri),
                    UseProxy = true,
                };
            }
            else handler = new HttpClientHandler();
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public sealed class ApiCredsResult
        {
            public string ApiKey { get; init; } = "";
            public string Secret { get; init; } = "";
            public string Passphrase { get; init; } = "";
            public bool Success { get; init; }
            public int HttpStatus { get; init; }
            public string RawBody { get; init; } = "";
            public string? Error { get; init; }
        }

        /// <summary>
        /// Signs the ClobAuth message and POSTs /auth/api-key. Returns parsed credentials.
        /// </summary>
        /// <param name="privateKeyHex">EOA private key (with or without 0x prefix).</param>
        /// <param name="addressForAuth">Address to claim ownership of in the signed message AND in the
        /// POLY_ADDRESS header. Use the EOA address for normal users, or the deposit wallet address
        /// to bind the new API key to the deposit wallet (relies on ERC-1271 server-side validation).</param>
        /// <param name="nonce">Nonce. Each (address, nonce) pair maps to one API key. Default 0.</param>
        /// <param name="chainId">Chain id (137 for mainnet).</param>
        public Task<ApiCredsResult> CreateApiKeyAsync(string privateKeyHex, string addressForAuth, long nonce = 0, int chainId = 137, bool erc7739Wrap = false, string? walletForWrap = null, CancellationToken ct = default)
            => InvokeAuthEndpointAsync(HttpMethod.Post, "/auth/api-key", privateKeyHex, addressForAuth, nonce, chainId, erc7739Wrap, walletForWrap, ct);

        public Task<ApiCredsResult> DeriveApiKeyAsync(string privateKeyHex, string addressForAuth, long nonce = 0, int chainId = 137, bool erc7739Wrap = false, string? walletForWrap = null, CancellationToken ct = default)
            => InvokeAuthEndpointAsync(HttpMethod.Get, "/auth/derive-api-key", privateKeyHex, addressForAuth, nonce, chainId, erc7739Wrap, walletForWrap, ct);

        private async Task<ApiCredsResult> InvokeAuthEndpointAsync(
            HttpMethod method,
            string path,
            string privateKeyHex,
            string addressForAuth,
            long nonce,
            int chainId,
            bool erc7739Wrap,
            string? walletForWrap,
            CancellationToken ct)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            string sig;
            if (!erc7739Wrap)
            {
                var digest = ComputeClobAuthDigest(addressForAuth, ts, nonce, chainId);
                sig = PolymarketV2Signer.SignDigest(privateKeyHex, digest);
            }
            else
            {
                sig = BuildErc7739WrappedSignature(privateKeyHex, addressForAuth, ts, nonce, chainId, walletForWrap ?? addressForAuth);
            }

            using var req = new HttpRequestMessage(method, _host + path);
            req.Headers.TryAddWithoutValidation("POLY_ADDRESS", addressForAuth);
            req.Headers.TryAddWithoutValidation("POLY_SIGNATURE", sig);
            req.Headers.TryAddWithoutValidation("POLY_TIMESTAMP", ts.ToString());
            req.Headers.TryAddWithoutValidation("POLY_NONCE", nonce.ToString());

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode)
            {
                return new ApiCredsResult { Success = false, HttpStatus = status, RawBody = body, Error = body };
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string Get(string k) => root.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";
                var apiKey = Get("apiKey");
                var secret = Get("secret");
                var pass = Get("passphrase");
                return new ApiCredsResult
                {
                    Success = !string.IsNullOrEmpty(apiKey),
                    HttpStatus = status,
                    RawBody = body,
                    ApiKey = apiKey,
                    Secret = secret,
                    Passphrase = pass,
                    Error = string.IsNullOrEmpty(apiKey) ? "empty key in response" : null,
                };
            }
            catch (Exception ex)
            {
                return new ApiCredsResult { Success = false, HttpStatus = status, RawBody = body, Error = "parse: " + ex.Message };
            }
        }

        /// <summary>
        /// Computes the 32-byte EIP-712 digest for the ClobAuth message: keccak256("\x19\x01" ‖ domainSep ‖ structHash).
        /// </summary>
        private static byte[] ComputeClobAuthDigest(string address, long timestamp, long nonce, int chainId)
        {
            var domainSep = ComputeClobAuthDomainSep(chainId);
            var structHash = ComputeClobAuthStructHash(address, timestamp, nonce);
            return Sha3Keccack.Current.CalculateHash(Concat(new byte[] { 0x19, 0x01 }, domainSep, structHash));
        }

        private static byte[] ComputeClobAuthDomainSep(int chainId)
        {
            return Sha3Keccack.Current.CalculateHash(Concat(
                EIP712_DOMAIN_TYPEHASH,
                Sha3Keccack.Current.CalculateHash(Encoding.ASCII.GetBytes(CLOB_DOMAIN_NAME)),
                Sha3Keccack.Current.CalculateHash(Encoding.ASCII.GetBytes(CLOB_VERSION)),
                EncodeUint256((ulong)chainId)));
        }

        private static byte[] ComputeClobAuthStructHash(string address, long timestamp, long nonce)
        {
            var addrBytes = address.RemoveHexPrefix().HexToByteArray();
            if (addrBytes.Length != 20) throw new ArgumentException("address must be 20 bytes");
            var addrPadded = new byte[32];
            Buffer.BlockCopy(addrBytes, 0, addrPadded, 12, 20);

            return Sha3Keccack.Current.CalculateHash(Concat(
                CLOB_AUTH_TYPEHASH,
                addrPadded,
                Sha3Keccack.Current.CalculateHash(Encoding.ASCII.GetBytes(timestamp.ToString())),
                EncodeUint256((ulong)nonce),
                Sha3Keccack.Current.CalculateHash(Encoding.ASCII.GetBytes(MSG_TO_SIGN))));
        }

        /// <summary>
        /// Builds an ERC-7739 wrapped signature for the ClobAuth message, intended for ERC-1271 smart-contract wallets
        /// (Polymarket deposit wallets). The signature layout is:
        ///   innerSig (65) || appDomainSep (32) || contentsHash (32) || contentsType (ascii) || uint16BE(contentsTypeLen)
        /// where innerSig is ECDSA over keccak256("\x19\x01" || appDomainSep || TypedDataSign-structHash).
        /// </summary>
        private static string BuildErc7739WrappedSignature(string privateKeyHex, string address, long timestamp, long nonce, int chainId, string walletAddress)
        {
            var appDomainSep = ComputeClobAuthDomainSep(chainId);
            var contentsHash = ComputeClobAuthStructHash(address, timestamp, nonce);

            // TypedDataSign struct hash: typeHash || contentsHash || keccak(name) || keccak(version) || chainId || verifyingContract || salt
            var walletAddrPadded = new byte[32];
            var walletBytes = walletAddress.RemoveHexPrefix().HexToByteArray();
            Buffer.BlockCopy(walletBytes, 0, walletAddrPadded, 12, 20);

            var typedDataSignStructHash = Sha3Keccack.Current.CalculateHash(Concat(
                TYPED_DATA_SIGN_CLOBAUTH_TYPEHASH,
                contentsHash,
                Sha3Keccack.Current.CalculateHash(Encoding.ASCII.GetBytes(PolymarketV2Constants.DepositWalletInnerDomainName)),    // "DepositWallet"
                Sha3Keccack.Current.CalculateHash(Encoding.ASCII.GetBytes(PolymarketV2Constants.DepositWalletInnerDomainVersion)), // "1"
                EncodeUint256((ulong)chainId),
                walletAddrPadded,
                new byte[32]));                                                                                  // salt = 0

            var digest = Sha3Keccack.Current.CalculateHash(Concat(new byte[] { 0x19, 0x01 }, appDomainSep, typedDataSignStructHash));
            var innerSigHex = PolymarketV2Signer.SignDigest(privateKeyHex, digest);
            var innerSig = innerSigHex.RemoveHexPrefix().HexToByteArray();

            var contentsTypeBytes = Encoding.ASCII.GetBytes(CLOB_AUTH_TYPE_STRING);
            var lenBe = new byte[2] { (byte)(contentsTypeBytes.Length >> 8), (byte)(contentsTypeBytes.Length & 0xFF) };

            return "0x" + Concat(innerSig, appDomainSep, contentsHash, contentsTypeBytes, lenBe).ToHex();
        }

        private static byte[] EncodeUint256(ulong value)
        {
            var b = new byte[32];
            for (int i = 0; i < 8; i++) b[31 - i] = (byte)((value >> (8 * i)) & 0xFF);
            return b;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (var p in parts) total += p.Length;
            var result = new byte[total];
            int offset = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, result, offset, p.Length);
                offset += p.Length;
            }
            return result;
        }

        public void Dispose() => _http.Dispose();
    }
}
