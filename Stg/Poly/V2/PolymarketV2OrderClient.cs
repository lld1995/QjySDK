using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QjySDK.Stg.Poly.V2
{
    public sealed class PolymarketV2OrderResult
    {
        public bool Success { get; set; }
        public string? OrderId { get; set; }
        public string? Status { get; set; }       // "live" | "matched" | "delayed"
        public string? ErrorMessage { get; set; }
        public string? RawBody { get; set; }
    }

    /// <summary>
    /// Thin HTTP client for the V2 /order endpoint. Builds the JSON wire body and POSTs with L2 HMAC headers.
    /// Caller is responsible for signing the order (see PolymarketV2Signer).
    /// </summary>
    public sealed class PolymarketV2OrderClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _host;

        public PolymarketV2OrderClient(string host = PolymarketV2Constants.ClobHost, string? proxyUrl = null)
        {
            _host = host.TrimEnd('/');

            HttpMessageHandler handler;
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                handler = new HttpClientHandler
                {
                    Proxy = new WebProxy(proxyUrl),
                    UseProxy = true,
                };
            }
            else
            {
                handler = new HttpClientHandler();
            }

            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public Action<string>? OnDebug { get; set; }

        /// <summary>
        /// GET /auth/api-keys — returns the list of API keys (and their bound addresses) for the
        /// caller's L2 auth context. Useful for diagnosing the "order signer != API KEY address" error.
        /// </summary>
        public async Task<string> GetApiKeysRawAsync(
            string signerAddress, string apiKey, string apiSecretB64Url, string passphrase,
            CancellationToken ct = default)
        {
            const string path = "/auth/api-keys";
            var headers = PolymarketV2L2Auth.BuildHeaders(
                signerAddress, apiKey, apiSecretB64Url, passphrase, method: "GET", requestPath: path, body: null);
            using var req = new HttpRequestMessage(HttpMethod.Get, _host + path);
            ApplyL2Headers(req, headers);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return $"status={(int)resp.StatusCode}  body={body}";
        }

        public async Task<PolymarketV2OrderResult> PostOrderAsync(
            PolymarketV2SignedOrder order,
            string apiOwner,                  // POLY_API_KEY value (uuid)
            string signerAddress,             // EOA address (also goes into POLY_ADDRESS header)
            string apiKey,
            string apiSecretB64Url,
            string passphrase,
            PolymarketV2OrderType orderType = PolymarketV2OrderType.GTC,
            bool postOnly = false,
            bool deferExec = false,
            CancellationToken ct = default)
        {
            var payload = BuildPayload(order, apiOwner, orderType, postOnly, deferExec);
            var body = JsonSerializer.Serialize(payload, _jsonOpts);

            var headers = PolymarketV2L2Auth.BuildHeaders(
                signerAddress, apiKey, apiSecretB64Url, passphrase,
                method: "POST",
                requestPath: PolymarketV2Constants.PostOrderPath,
                body: body);

            using var req = new HttpRequestMessage(HttpMethod.Post, _host + PolymarketV2Constants.PostOrderPath);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            ApplyL2Headers(req, headers);

            OnDebug?.Invoke($"POST {PolymarketV2Constants.PostOrderPath}  POLY_ADDRESS={signerAddress}  POLY_API_KEY={apiKey}\n  body={body}");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var respText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OnDebug?.Invoke($"response status={(int)resp.StatusCode}  body={respText}");

            var result = new PolymarketV2OrderResult { RawBody = respText };

            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(respText);
                    var root = doc.RootElement;
                    result.Success = root.TryGetProperty("success", out var s) && s.GetBoolean();
                    if (root.TryGetProperty("orderID", out var oid)) result.OrderId = oid.GetString();
                    if (root.TryGetProperty("status", out var st)) result.Status = st.GetString();
                    if (root.TryGetProperty("errorMsg", out var em)) result.ErrorMessage = em.GetString();
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = "parse response failed: " + ex.Message;
                }
            }
            else
            {
                result.Success = false;
                try
                {
                    using var doc = JsonDocument.Parse(respText);
                    if (doc.RootElement.TryGetProperty("error", out var e)) result.ErrorMessage = e.GetString();
                    else result.ErrorMessage = $"HTTP {(int)resp.StatusCode}: {respText}";
                }
                catch
                {
                    result.ErrorMessage = $"HTTP {(int)resp.StatusCode}: {respText}";
                }
            }

            return result;
        }

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };

        private static object BuildPayload(PolymarketV2SignedOrder o, string owner, PolymarketV2OrderType orderType, bool postOnly, bool deferExec)
        {
            // Match the official TS client's wire body. Notably omits `taker` for V2 orders (TS sets it from
            // an undefined OrderV2.taker → JSON.stringify drops it). Including taker for POLY_1271 makes the
            // server respond with "the order signer address has to be the address of the API KEY".
            return new
            {
                deferExec = deferExec,
                postOnly = postOnly,
                order = new
                {
                    salt = o.Salt,
                    // Lowercase the on-the-wire addresses. The EIP-712 contents hash uses raw bytes
                    // (case-insensitive), so signature verification is unaffected. The CLOB server,
                    // however, stores API-key bound addresses lowercase (Polymarket developer page
                    // displays them lowercase) and uses case-sensitive string comparison against the
                    // wire body's `signer` field — checksum-cased addresses fail with
                    // "the order signer address has to be the address of the API KEY".
                    maker = (o.Maker ?? "").ToLowerInvariant(),
                    signer = (o.Signer ?? "").ToLowerInvariant(),
                    tokenId = o.TokenId,
                    makerAmount = o.MakerAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    takerAmount = o.TakerAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    side = o.Side == OrderSide.Buy ? "BUY" : "SELL",
                    signatureType = (int)o.SignatureType,
                    timestamp = o.TimestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    expiration = o.ExpirationSec.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    metadata = o.Metadata,
                    builder = o.Builder,
                    signature = o.Signature,
                },
                owner = owner,
                orderType = orderType.ToString(),
            };
        }

        /// <summary>
        /// Calls GET /balance-allowance/update?asset_type=COLLATERAL&amp;signature_type=3 to sync the deposit wallet's
        /// pUSD balance and register the API key on the server as a POLY_1271 user. Must be called once after
        /// onboarding/before the first deposit-wallet order — see "Deposit Wallets" guide step 5.
        /// </summary>
        public async Task<bool> SyncBalanceAllowanceAsync(
            string signerAddress, string apiKey, string apiSecretB64Url, string passphrase,
            int signatureType = 3, string assetType = "COLLATERAL", string? tokenId = null,
            CancellationToken ct = default)
        {
            // IMPORTANT: HMAC signature is computed over the path WITHOUT the query string (the official
            // TS client uses `requestPath: "/balance-allowance/update"` and passes params separately).
            // Including the query in the HMAC produces 401 "Unauthorized/Invalid api key".
            const string hmacPath = "/balance-allowance/update";
            var query = $"?asset_type={assetType}&signature_type={signatureType}";
            if (!string.IsNullOrWhiteSpace(tokenId)) query += "&token_id=" + tokenId;

            var headers = PolymarketV2L2Auth.BuildHeaders(
                signerAddress, apiKey, apiSecretB64Url, passphrase,
                method: "GET", requestPath: hmacPath, body: null);

            using var req = new HttpRequestMessage(HttpMethod.Get, _host + hmacPath + query);
            ApplyL2Headers(req, headers);
            OnDebug?.Invoke($"GET {hmacPath + query}  POLY_ADDRESS={signerAddress}  POLY_API_KEY={apiKey}");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OnDebug?.Invoke($"  status={(int)resp.StatusCode}  body={body}");
            return resp.IsSuccessStatusCode;
        }

        /// <summary>
        /// Cancel a single live order by its orderID. Per the official TS client this is a DELETE
        /// to /order with body { orderID: "..." } and L2 HMAC computed over method=DELETE, path=/order, body.
        /// </summary>
        public async Task<(bool Success, string RawBody)> CancelOrderAsync(
            string orderId,
            string signerAddress, string apiKey, string apiSecretB64Url, string passphrase,
            CancellationToken ct = default)
        {
            const string path = "/order";
            var body = JsonSerializer.Serialize(new { orderID = orderId }, _jsonOpts);
            var headers = PolymarketV2L2Auth.BuildHeaders(
                signerAddress, apiKey, apiSecretB64Url, passphrase,
                method: "DELETE", requestPath: path, body: body);
            using var req = new HttpRequestMessage(HttpMethod.Delete, _host + path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            ApplyL2Headers(req, headers);
            OnDebug?.Invoke($"DELETE {path}  POLY_ADDRESS={signerAddress}  body={body}");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            OnDebug?.Invoke($"  status={(int)resp.StatusCode}  body={respBody}");
            return (resp.IsSuccessStatusCode, respBody);
        }

        private static void ApplyL2Headers(HttpRequestMessage req, PolymarketL2Headers h)
        {
            req.Headers.TryAddWithoutValidation("POLY_ADDRESS", h.Address);
            req.Headers.TryAddWithoutValidation("POLY_SIGNATURE", h.Signature);
            req.Headers.TryAddWithoutValidation("POLY_TIMESTAMP", h.Timestamp);
            req.Headers.TryAddWithoutValidation("POLY_API_KEY", h.ApiKey);
            req.Headers.TryAddWithoutValidation("POLY_PASSPHRASE", h.Passphrase);
        }

        public void Dispose() => _http.Dispose();
    }
}
