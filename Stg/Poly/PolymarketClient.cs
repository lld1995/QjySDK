using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using Nethereum.JsonRpc.Client;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Hex.HexTypes;
using Polymarket.Net;
using Polymarket.Net.Clients;
using Polymarket.Net.Enums;
using Polymarket.Net.Objects.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QjySDK.Stg
{
    public class PolymarketService
    {
        private const string CTF_ADDRESS = "0x4D97DCd97eC945f40cF65F87097ACe5EA0476045";
        private const string NEG_RISK_ADAPTER = "0xd91E80cF2E7be2e162c6513ceD06f1dD0dA35296";
        private const string USDC_E_ADDRESS = "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174";
        private const string POLYGON_RPC = "https://polygon-bor-rpc.publicnode.com";
        private const string PROXY_URL = "http://127.0.0.1:7888";

        private static readonly string REDEEM_ABI = "[{\"inputs\":[{\"name\":\"collateralToken\",\"type\":\"address\"},{\"name\":\"parentCollectionId\",\"type\":\"bytes32\"},{\"name\":\"conditionId\",\"type\":\"bytes32\"},{\"name\":\"indexSets\",\"type\":\"uint256[]\"}],\"name\":\"redeemPositions\",\"outputs\":[],\"stateMutability\":\"nonpayable\",\"type\":\"function\"}]";
        private static readonly string BALANCE_OF_ABI = "[{\"inputs\":[{\"name\":\"account\",\"type\":\"address\"},{\"name\":\"id\",\"type\":\"uint256\"}],\"name\":\"balanceOf\",\"outputs\":[{\"name\":\"\",\"type\":\"uint256\"}],\"stateMutability\":\"view\",\"type\":\"function\"}]";

        private PolymarketRestClient? _restClient;
        private string? _privateKey;
        private string? _signerAddress;            // EOA address derived from _privateKey
        private string? _funderAddress;            // Resolved funder used as order maker (proxy / safe / deposit wallet)
        private byte _signatureType;               // 0 EOA / 1 POLY_PROXY / 2 SAFE / 3 POLY_1271
        private QjySDK.Stg.Poly.V2.PolymarketV2OrderClient? _v2OrderClient;

        private string _proxyUrl = PROXY_URL;

        private readonly ConcurrentQueue<MarketRedeemJob> _redeemQueue = new();
        private readonly HashSet<string> _enqueuedConditionIds = new();
        private CancellationTokenSource? _redeemCts;
        private Thread? _redeemThread;

        public async Task Init(string privateKey, string funderAddress = "", string? proxyUrl = null)
        {
            if (string.IsNullOrWhiteSpace(privateKey))
                throw new ArgumentException("privateKey is required", nameof(privateKey));

            _privateKey = privateKey;
            _funderAddress = funderAddress;
            _signerAddress = new Nethereum.Signer.EthECKey(privateKey).GetPublicAddress();

            _proxyUrl = string.IsNullOrWhiteSpace(proxyUrl) ? PROXY_URL : proxyUrl.Trim();

            // SignType is critical post-V2:
            //   - EOA:   funds live on the raw private-key address (rare for real users)
            //   - Email: account created via Polymarket email/Magic login → orders signed for the Email proxy wallet
            //   - Proxy: account created via Polymarket Gnosis Safe proxy (browser wallet)
            // When funderAddress is supplied we default to Email-style proxy signing, which matches the
            // common "Polymarket website wallet" scenario; callers can still pass an empty funder to fall back to EOA.
            var signType = string.IsNullOrWhiteSpace(funderAddress) ? SignType.EOA : SignType.Email;
            var l1Cred = new PolymarketL1Credential(signType, privateKey, funderAddress ?? string.Empty);

            _restClient?.Dispose();
            _restClient = new PolymarketRestClient(opts =>
            {
                opts.ApiCredentials = new PolymarketCredentials(l1Cred);
                var proxy = new Uri(_proxyUrl);
                opts.Proxy = new ApiProxy(proxy.Host, proxy.Port);
            });

            // L2 API credentials handling:
            //   1) If user provided cached POLY_API_KEY/SECRET/PASSPHRASE in poly_secrets.txt -> use them directly.
            //      Polymarket only allows one API key per EOA; once created, /auth/api-key returns
            //      "Could not create api key" on subsequent calls. Caching avoids redundant L1 round-trips
            //      and works when the SDK's GetOrCreate happens to fall through.
            //   2) Otherwise fall back to deriving via L1 EIP-712 (ClobAuthDomain).
            if (!string.IsNullOrWhiteSpace(PolySecrets.PolyApiKey)
                && !string.IsNullOrWhiteSpace(PolySecrets.PolyApiSecret)
                && !string.IsNullOrWhiteSpace(PolySecrets.PolyApiPassphrase))
            {
                _restClient.UpdateL2Credentials(new PolymarketCreds
                {
                    ApiKey = PolySecrets.PolyApiKey,
                    Secret = PolySecrets.PolyApiSecret,
                    Passphrase = PolySecrets.PolyApiPassphrase,
                });
            }
            else
            {
                var l2Creds = await _restClient.ClobApi.Account.GetOrCreateApiCredentialsAsync();
                if (!l2Creds.Success)
                    throw new Exception("Failed to derive L2 credentials: " + l2Creds.Error?.Message);
                _restClient.UpdateL2Credentials(l2Creds.Data);
            }

            // Detect the funder wallet type on-chain. Post-V2 (2026-04-28), new Polymarket Email/Magic accounts
            // are deployed as ERC-1967 proxy "deposit wallets" with a recognizable bytecode pattern (PUSH32 = 0x7f).
            // These require POLY_1271 (signatureType=3) + ERC-7739 wrapped signatures, which JKorf SDK 3.0.0 does NOT support;
            // we route those orders through PolymarketV2OrderClient with our own signer.
            _signatureType = await DetectSignatureTypeAsync(signType, funderAddress ?? string.Empty);
            if (_signatureType == QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePoly1271)
            {
                _v2OrderClient = new QjySDK.Stg.Poly.V2.PolymarketV2OrderClient(proxyUrl: _proxyUrl)
                {
                    OnDebug = msg => PolyLog.Order("V2DBG " + msg),
                };
                PolyLog.Order($"Init: detected POLY_1271 deposit wallet funder={funderAddress}; will route orders through custom V2 signer.");
            }
            else
            {
                PolyLog.Order($"Init: signatureType={_signatureType}; will use JKorf SDK PlaceOrderAsync.");
            }
        }

        private async Task<byte> DetectSignatureTypeAsync(SignType jkorfSignType, string funderAddress)
        {
            // EOA path: no funder, no on-chain wallet
            if (string.IsNullOrWhiteSpace(funderAddress))
                return QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypeEoa;

            // Try to detect the wallet contract type by inspecting its bytecode.
            // - Polymarket V2 deposit wallets: ERC-1967 proxy with `0x363d3d373d3d363d7f...` prefix (PUSH32 opcode = 0x7f at offset 9)
            // - Polymarket POLY_PROXY (1) / GnosisSafe (2): different prefixes; we don't try to disambiguate them here since
            //   JKorf SDK handles both via SignType.Email / SignType.Proxy uniformly.
            try
            {
                var handler = new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(_proxyUrl),
                    UseProxy = true,
                };
                using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var body = "{\"jsonrpc\":\"2.0\",\"method\":\"eth_getCode\",\"params\":[\"" + funderAddress + "\",\"latest\"],\"id\":1}";
                var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(POLYGON_RPC, content);
                var text = await resp.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                var code = doc.RootElement.GetProperty("result").GetString() ?? "0x";

                if (code.Length <= 2)
                {
                    // No code at funder; treat as POLY_PROXY (JKorf still produces a valid wire payload).
                    return QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePolyProxy;
                }

                // Deposit wallet pattern: ERC-1967 proxy emitted by Solady LibClone uses PUSH32 (0x7f) very early.
                // The first 10 bytes are `0x363d3d373d3d363d7f` (calldatacopy + setup + PUSH32).
                var lower = code.ToLowerInvariant();
                if (lower.StartsWith("0x363d3d373d3d363d7f"))
                {
                    return QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePoly1271;
                }

                // EIP-7702 delegated EOA (Pectra): code = 0xef0100 || delegateAddress (23 bytes).
                // Polymarket 新版 deposit 钱包用 7702 委托到 ERC-1271 实现，下单仍走 POLY_1271 (sigType=3)
                // + ERC-7739 包装签名(owner EOA 私钥签)。不识别会落到 POLY_PROXY → "maker address not allowed"。
                if (lower.StartsWith("0xef0100"))
                {
                    return QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePoly1271;
                }

                // Other contract wallets: assume legacy POLY_PROXY / Safe path (JKorf handles).
                return jkorfSignType == SignType.Proxy
                    ? QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePolyGnosisSafe
                    : QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePolyProxy;
            }
            catch (Exception ex)
            {
                PolyLog.Order($"DetectSignatureType: failed to query chain code, falling back to POLY_PROXY. err={ex.Message}");
                return QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePolyProxy;
            }
        }

        public async Task<EventInfo?> GetEventAsync(string slug)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(slug))
                return null;

            var response = await _restClient!.GammaApi.GetEventBySlugAsync(slug, includeChat: null, includeTemplate: null);
            if (!response.Success || response.Data == null)
                return null;

            var evt = response.Data;
            var result = new EventInfo
            {
                Id = evt.Id ?? string.Empty,
                Slug = evt.Slug ?? string.Empty,
                Title = evt.Title ?? string.Empty,
                StartDate = evt.StartDate,
                EndDate = evt.EndDate,
                Active = evt.Active,
                Closed = evt.Closed
            };

            var tokenIds = new List<string>();
            if (evt.Markets != null)
            {
                foreach (var market in evt.Markets)
                {
                    if (market.ClobTokenIds != null)
                        tokenIds.AddRange(market.ClobTokenIds.Where(t => !string.IsNullOrWhiteSpace(t)));

                    // V2 Gamma API renamed ConditionId -> MarketId on PolymarketGammaMarket.
                    if (string.IsNullOrWhiteSpace(result.ConditionId) && !string.IsNullOrWhiteSpace(market.MarketId))
                        result.ConditionId = market.MarketId;

                    if (market.OutcomePrices != null)
                    {
                        var prices = market.OutcomePrices.ToList();
                        if (prices.Count > 0) result.YesPrice = prices[0];
                        if (prices.Count > 1) result.NoPrice = prices[1];
                    }
                }
            }
            result.ClobTokenIds = tokenIds.Distinct().ToArray();
            return result;
        }

        /// <summary>
        /// 查找当前可交易事件：优先当前 slot，其次最近的未来 slot (+1..+lookAheadSlots)，
        /// 最后回退到刚结束的 slot (-1)。lookAheadSlots 默认为 5——因为有些 Polymarket 事件
        /// 会比预定时间提前几个 slot 上架，宽窗口能避免在 slug-prefix-T+1 不存在时直接放弃。
        /// </summary>
        public async Task<EventInfo?> GetLatestEventAsync(string slugPrefix, int slotSeconds, int lookAheadSlots = 5)
        {
            EnsureInitialized();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var aligned = ts - (ts % slotSeconds);

            // 1) current slot
            var evt = await GetEventAsync($"{slugPrefix}-{aligned}");
            if (evt != null && evt.Active && !evt.Closed)
                return evt;

            // 2) +1..+lookAheadSlots (early-listed future slots, nearest first)
            for (int i = 1; i <= lookAheadSlots; i++)
            {
                evt = await GetEventAsync($"{slugPrefix}-{aligned + slotSeconds * i}");
                if (evt != null && evt.Active && !evt.Closed)
                    return evt;
            }

            // 3) just-ended slot fallback
            evt = await GetEventAsync($"{slugPrefix}-{aligned - slotSeconds}");
            if (evt != null && evt.Active && !evt.Closed)
                return evt;

            return null;
        }

        /// <summary>
        /// 仅向前查找最近一个 active 未关闭的未来事件 (+1..+maxLookAheadSlots)，跳过当前 slot。
        /// 用于"当前事件还在进行中，但下一期可能已经提前上架"的预取场景。
        /// </summary>
        public async Task<EventInfo?> GetUpcomingEventAsync(string slugPrefix, int slotSeconds, int maxLookAheadSlots = 5)
        {
            EnsureInitialized();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var aligned = ts - (ts % slotSeconds);

            for (int i = 1; i <= maxLookAheadSlots; i++)
            {
                var evt = await GetEventAsync($"{slugPrefix}-{aligned + slotSeconds * i}");
                if (evt != null && evt.Active && !evt.Closed)
                    return evt;
            }
            return null;
        }

        public async Task<PlaceOrderResult> PlaceOrderAsync(string tokenId, OrderSide side, decimal price, decimal size, int feeRateBps = 0)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(tokenId))
                throw new ArgumentException("tokenId is required", nameof(tokenId));

            // CLOB V2 (since 2026-04-28) removed FeeRateBps from the order schema.
            _ = feeRateBps;

            var tokenTag = tokenId.Length > 8 ? tokenId.Substring(0, 8) + "..." : tokenId;
            PolyLog.Order($"PlaceOrderAsync request token={tokenTag} side={side} price={price} size={size} sigType={_signatureType}");

            PlaceOrderResult result;
            if (_signatureType == QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePoly1271)
            {
                result = await PlaceOrderV2Poly1271Async(tokenId, side, price, size);
            }
            else
            {
                var response = await _restClient!.ClobApi.Trading.PlaceOrderAsync(
                    tokenId, side, OrderType.Limit, size, price: price);
                result = new PlaceOrderResult
                {
                    Success = response.Success,
                    Message = response.Error?.Message,
                    OrderId = response.Data?.OrderId,
                };
            }

            PolyLog.Order($"PlaceOrderAsync result token={tokenTag} success={result.Success} orderId={result.OrderId} msg={result.Message}");
            return result;
        }

        /// <summary>
        /// V2 POLY_1271 (deposit wallet) path: builds and signs the order locally per clob-client-v2,
        /// then POSTs to /order via PolymarketV2OrderClient. JKorf SDK 3.0.0 does not support signatureType=3.
        /// </summary>
        private bool _v2BalanceAllowanceSynced = false;

        private async Task<PlaceOrderResult> PlaceOrderV2Poly1271Async(string tokenId, OrderSide side, decimal price, decimal size)
        {
            if (_v2OrderClient == null) throw new InvalidOperationException("V2 order client not initialized");
            if (string.IsNullOrWhiteSpace(_privateKey)) throw new InvalidOperationException("private key missing");
            if (string.IsNullOrWhiteSpace(_funderAddress)) throw new InvalidOperationException("funder address missing for POLY_1271");

            // ✅ 已验证可用(2026-06-19)：canonical POLY_1271 流程——
            //   maker == signer == deposit wallet(_funderAddress)，ERC-7739 wrap(内域 "DepositWallet"/v1,
            //   verifyingContract = deposit wallet)，由 owner EOA(_privateKey) 直接签名；L2 鉴权用 minted
            //   POLY_API_KEY、POLY_ADDRESS=EOA。/order 返回 success + orderId。
            //   关键前提：_funderAddress 必须是真正的 deposit-wallet 代理合约(Solady ERC-1967,
            //   code 前缀 0x363d3d373d3d363d7f…)，即 Polymarket "API-only" 那个地址；若误填成 7702 委托 EOA
            //   会报 "the order signer address has to be the address of the API KEY"。
            var eoaAddr = new Nethereum.Signer.EthECKey(_privateKey).GetPublicAddress();

            // 首单前同步一次 CLOB 余额缓存，让 API key 注册为该 signer 的用户。幂等，仅按服务生命周期做一次。
            if (!_v2BalanceAllowanceSynced)
            {
                var ok = await _v2OrderClient.SyncBalanceAllowanceAsync(
                    signerAddress: eoaAddr,
                    apiKey: PolySecrets.PolyApiKey,
                    apiSecretB64Url: PolySecrets.PolyApiSecret,
                    passphrase: PolySecrets.PolyApiPassphrase);
                PolyLog.Order($"V2 balance-allowance/update (POLY_ADDRESS=EOA {eoaAddr}): success={ok}");
                _v2BalanceAllowanceSynced = ok;
            }

            // Look up market metadata to determine tick size and NegRisk flag.
            var (tickSize, negRisk) = await ResolveMarketTickAndNegRiskAsync(tokenId);

            var rawSide = side == OrderSide.Buy ? QjySDK.Stg.Poly.V2.OrderSide.Buy : QjySDK.Stg.Poly.V2.OrderSide.Sell;
            var raw = QjySDK.Stg.Poly.V2.PolymarketV2OrderBuilder.Build(rawSide, price, size, tickSize);

            var signed = QjySDK.Stg.Poly.V2.PolymarketV2Signer.BuildAndSign(
                privateKeyHex: _privateKey!,
                maker: _funderAddress!,
                orderSignerAddress: _funderAddress!,
                tokenId: tokenId,
                raw: raw,
                signatureType: QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePoly1271,
                negRisk: negRisk,
                erc1271WalletForWrap: _funderAddress!);

            var resp = await _v2OrderClient.PostOrderAsync(
                signed,
                apiOwner: PolySecrets.PolyApiKey,
                signerAddress: eoaAddr,              // L2 POLY_ADDRESS = EOA(API key 绑定地址)
                apiKey: PolySecrets.PolyApiKey,
                apiSecretB64Url: PolySecrets.PolyApiSecret,
                passphrase: PolySecrets.PolyApiPassphrase,
                orderType: QjySDK.Stg.Poly.V2.PolymarketV2OrderType.GTC);

            return new PlaceOrderResult
            {
                Success = resp.Success,
                OrderId = resp.OrderId,
                Message = resp.Success ? null : (resp.ErrorMessage ?? resp.RawBody),
            };
        }

        /// <summary>Look up the market's tickSize and NegRisk flag via Gamma (best-effort; defaults are 0.01 + binary).</summary>
        private async Task<(decimal tickSize, bool negRisk)> ResolveMarketTickAndNegRiskAsync(string tokenId)
        {
            // NOTE: read raw Gamma JSON. The SDK model (PolymarketGammaMarket) only exposes `negRiskOther`
            // (JSON negRiskOther) — that is NOT the negRisk flag and is false even on NegRisk markets.
            // Signing a NegRisk order against the binary exchange domain → "invalid POLY_1271 signature:
            // signature does not match order hash". The real flag is `negRisk` (and `negRiskMarketID`).
            try
            {
                var handler = new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(_proxyUrl),
                    UseProxy = true
                };
                using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                var json = await http.GetStringAsync($"https://gamma-api.polymarket.com/markets?clob_token_ids={tokenId}");
                var arr = Newtonsoft.Json.Linq.JArray.Parse(json);
                if (arr.Count > 0)
                {
                    var m = arr[0];
                    bool negRisk = (bool?)m["negRisk"]
                        ?? !string.IsNullOrWhiteSpace((string?)m["negRiskMarketID"]);
                    decimal tick = (decimal?)m["orderPriceMinTickSize"] ?? 0.01m;
                    if (tick <= 0) tick = 0.01m;
                    PolyLog.Order($"ResolveMarketTickAndNegRisk: token={(tokenId.Length > 8 ? tokenId.Substring(0, 8) : tokenId)}... tick={tick} negRisk={negRisk}");
                    return (tick, negRisk);
                }
            }
            catch (Exception ex)
            {
                PolyLog.Order($"ResolveMarketTickAndNegRisk: lookup failed, defaulting tick=0.01 negRisk=false. err={ex.Message}");
            }
            return (0.01m, false);
        }

        public async Task<PlaceOrderResult> PlaceOrderWithRetryAsync(string tokenId, OrderSide side, decimal size, int maxRetries = 3, int waitMs = 5000, int feeRateBps = 1000)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(tokenId))
                throw new ArgumentException("tokenId is required", nameof(tokenId));

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                // 获取最新价格
                var bookInfo = await GetOrderBookAsync(new[] { tokenId });
                if (!bookInfo.Success || !bookInfo.TokenBooks.TryGetValue(tokenId, out var book))
                    return new PlaceOrderResult { Success = false, Message = "Failed to get order book" };

                var price = side == OrderSide.Buy ? book.BestAsk : book.BestBid;
                if (!price.HasValue || price.Value <= 0 || price.Value > 1)
                    return new PlaceOrderResult { Success = false, Message = "Invalid price from order book" };

                PolyLog.Order($"Attempt {attempt}/{maxRetries}: {side} {size}@{price.Value}");

                var placeResult = await PlaceOrderAsync(tokenId, side, price.Value, size, feeRateBps);
                if (!placeResult.Success || string.IsNullOrWhiteSpace(placeResult.OrderId))
                    return placeResult;

                // 等待成交
                await Task.Delay(waitMs);

                // 检查订单状态
                var orderStatus = await _restClient!.ClobApi.Trading.GetOrderAsync(placeResult.OrderId);
                if (orderStatus.Success && orderStatus.Data != null)
                {
                    if (orderStatus.Data.Status == Polymarket.Net.Enums.OrderStatus.Matched)
                    {
                        PolyLog.Order($"Filled: orderId={placeResult.OrderId}");
                        return placeResult;
                    }
                }

                // 未成交，撤单
                PolyLog.Order($"Not filled after {waitMs}ms, cancelling orderId={placeResult.OrderId}");
                var cancelResult = await _restClient!.ClobApi.Trading.CancelOrderAsync(placeResult.OrderId);
                if (cancelResult.Success)
                    PolyLog.Order($"Cancelled: orderId={placeResult.OrderId}");
                else
                    PolyLog.Order($"Cancel failed: orderId={placeResult.OrderId} err={cancelResult.Error?.Message}");

                if (attempt == maxRetries)
                {
                    PolyLog.Order("Max retries reached, giving up.");
                    return new PlaceOrderResult { Success = false, Message = "Max retries reached, order not filled" };
                }
            }

            return new PlaceOrderResult { Success = false, Message = "Unexpected" };
        }

        /// <summary>
        /// 查 CLOB 市场:返回是否已结算(closed)及每个 token 的当前价。
        /// 未结算时 price 为当前中间价;已结算时 price 为 0/1(此时盘口为空,用它显示结果)。
        /// </summary>
        public async Task<(bool closed, Dictionary<string, decimal> prices)> GetClobMarketPricesAsync(string conditionId)
        {
            var prices = new Dictionary<string, decimal>();
            if (string.IsNullOrWhiteSpace(conditionId)) return (false, prices);
            try
            {
                var handler = new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(_proxyUrl),
                    UseProxy = true
                };
                using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                var json = await http.GetStringAsync($"https://clob.polymarket.com/markets/{conditionId}");
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                bool closed = (bool?)obj["closed"] ?? false;
                if (obj["tokens"] is Newtonsoft.Json.Linq.JArray tokens)
                {
                    foreach (var t in tokens)
                    {
                        var tid = (string?)t["token_id"];
                        var price = (decimal?)t["price"];
                        if (!string.IsNullOrWhiteSpace(tid) && price.HasValue)
                            prices[tid!] = price.Value;
                    }
                }
                return (closed, prices);
            }
            catch { return (false, prices); }
        }

        public async Task<OrderBookInfo> GetOrderBookAsync(string[] tokenIds)
        {
            EnsureInitialized();

            var response = await _restClient!.ClobApi.ExchangeData.GetOrderBooksAsync(tokenIds);
            var result = new OrderBookInfo
            {
                Success = response.Success,
                Message = response.Error?.Message
            };

            if (!response.Success || response.Data == null)
                return result;

            foreach (var book in response.Data)
            {
                if (string.IsNullOrWhiteSpace(book.TokenId))
                    continue;

                result.TokenBooks[book.TokenId] = new TokenBookInfo
                {
                    BestBid = book.Bids?.LastOrDefault()?.Price,
                    BestAsk = book.Asks?.LastOrDefault()?.Price
                };
            }

            return result;
        }

        public async Task<RedeemResult> RedeemPositionsAsync(string conditionId, BigInteger[] indexSets, bool isNegRisk = false)
        {
            if (string.IsNullOrWhiteSpace(_privateKey))
                throw new InvalidOperationException("PolymarketService is not initialized");

            var sourceName = isNegRisk ? "NegRisk" : "CTF";
            try
            {
                var hexStr = conditionId.StartsWith("0x") ? conditionId.Substring(2) : conditionId;
                var conditionIdBytes = Convert.FromHexString(hexStr);

                // Step 1: Pre-check if condition is resolved on-chain
                var preCheckError = await PreCheckConditionResolved(hexStr);
                if (preCheckError != null)
                    return new RedeemResult { Success = false, Message = preCheckError };

                // Step 2: Send on-chain transaction
                var account = new Account(_privateKey, 137);
                var handler = new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(_proxyUrl),
                    UseProxy = true
                };
                var httpClient = new System.Net.Http.HttpClient(handler);
                var rpcClient = new RpcClient(new Uri(POLYGON_RPC), httpClient);
                var web3 = new Web3(account, rpcClient);

                var contractAddress = isNegRisk ? NEG_RISK_ADAPTER : CTF_ADDRESS;
                var contract = web3.Eth.GetContract(REDEEM_ABI, contractAddress);
                var redeemFunc = contract.GetFunction("redeemPositions");

                var parentCollectionId = new byte[32];

                var txHash = await redeemFunc.SendTransactionAsync(
                    account.Address,
                    new HexBigInteger(300000),
                    null,
                    USDC_E_ADDRESS,
                    parentCollectionId,
                    conditionIdBytes,
                    indexSets);

                // Step 3: Wait for receipt
                var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                int waitCount = 0;
                while (receipt == null && waitCount < 20)
                {
                    await Task.Delay(2000);
                    receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
                    waitCount++;
                }

                if (receipt == null)
                {
                    PolyLog.Redeem($"{sourceName} tx submitted but no receipt(timeout) txHash={txHash} cond={hexStr}");
                    return new RedeemResult { Success = false, TransactionHash = txHash, Message = $"{sourceName}: 已提交但未确认(超时)" };
                }

                if (receipt.Status.Value == 1)
                {
                    PolyLog.Redeem($"{sourceName} OK txHash={txHash} cond={hexStr} indexSets=[{string.Join(",", indexSets)}]");
                    return new RedeemResult { Success = true, TransactionHash = txHash };
                }
                else
                {
                    PolyLog.Redeem($"{sourceName} REVERT txHash={txHash} cond={hexStr}");
                    return new RedeemResult { Success = false, TransactionHash = txHash, Message = $"{sourceName}: 交易链上 revert" };
                }
            }
            catch (Exception ex)
            {
                PolyLog.Redeem($"{sourceName} EXCEPTION cond={conditionId} err={ex.Message}");
                return new RedeemResult { Success = false, Message = $"{sourceName}: {ex.Message}" };
            }
        }

        // Pre-check: query CTF.payoutDenominator(conditionId) to verify condition is resolved on-chain.
        // If payoutDenominator == 0, oracle hasn't reported results yet → skip redemption.
        private async Task<string?> PreCheckConditionResolved(string conditionIdHex)
        {
            using var httpClient = new System.Net.Http.HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var calldata = "0xdd34de67" + conditionIdHex.ToLowerInvariant();
            var rpcBody = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                jsonrpc = "2.0",
                method = "eth_call",
                @params = new object[] {
                    new { to = CTF_ADDRESS, data = calldata, gas = "0x30D40" },
                    "latest"
                },
                id = 1
            });

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var resp = await httpClient.PostAsync(POLYGON_RPC,
                        new System.Net.Http.StringContent(rpcBody, Encoding.UTF8, "application/json"));
                    var json = await resp.Content.ReadAsStringAsync();
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(json);

                    if (obj["error"] != null)
                        return "预检RPC错误";

                    var result = obj["result"]?.ToString() ?? "0x";
                    var cleaned = result.StartsWith("0x") ? result.Substring(2) : result;
                    cleaned = cleaned.TrimStart('0');
                    if (string.IsNullOrEmpty(cleaned) || cleaned == "0")
                        return "oracle 尚未报告结果(payoutDenominator=0)";

                    return null; // condition is resolved, can redeem
                }
                catch
                {
                    if (attempt == 0) { await Task.Delay(1000); continue; }
                    return "预检网络错误(跳过以保护nonce)";
                }
            }
            return "预检网络错误(跳过以保护nonce)";
        }

        private BigInteger[] GetWinningIndexSets(PolymarketGammaMarket market, string[] jobTokenIds, Dictionary<string, BigInteger> balances)
        {
            var winningIndexSets = new List<BigInteger>();
            var clobTokenIds = market.ClobTokenIds;
            var outcomePrices = market.OutcomePrices;

            if (clobTokenIds == null || outcomePrices == null)
                return Array.Empty<BigInteger>();

            for (int i = 0; i < clobTokenIds.Length && i < outcomePrices.Length; i++)
            {
                var tokenId = clobTokenIds[i];
                if (string.IsNullOrWhiteSpace(tokenId)) continue;

                // outcome price > 0 means this side won (1 = full win)
                if (outcomePrices[i] <= 0) continue;

                // check user holds this token
                if (balances.TryGetValue(tokenId, out var bal) && bal > 0)
                {
                    // index set: outcome 0 -> 1 (binary 01), outcome 1 -> 2 (binary 10)
                    winningIndexSets.Add(new BigInteger(1 << i));
                }
            }

            return winningIndexSets.ToArray();
        }

        // Once a condition is resolved on-chain, determine which held outcome(s) won using the
        // Polymarket data-api. Gamma stops returning short-lived markets after they end, so its
        // outcomePrices are unavailable here. An asset is a winning, claimable position when
        // redeemable==true && curPrice>0 && size>0. Position i in tokenIds maps to CTF index set (1<<i).
        private BigInteger[] GetWinningIndexSetsFromDataApi(string conditionId, string[] tokenIds)
        {
            var holder = !string.IsNullOrWhiteSpace(_funderAddress)
                ? _funderAddress!
                : new Account(_privateKey, 137).Address;
            try
            {
                var handler = new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(_proxyUrl),
                    UseProxy = true
                };
                using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                var url = $"https://data-api.polymarket.com/positions?user={holder}&sizeThreshold=0";
                var json = http.GetStringAsync(url).GetAwaiter().GetResult();
                var arr = Newtonsoft.Json.Linq.JArray.Parse(json);

                var winners = new List<BigInteger>();
                for (int i = 0; i < tokenIds.Length; i++)
                {
                    var tok = tokenIds[i];
                    if (string.IsNullOrWhiteSpace(tok)) continue;
                    var pos = arr.FirstOrDefault(p =>
                        (string?)p["asset"] == tok
                        && string.Equals((string?)p["conditionId"], conditionId, StringComparison.OrdinalIgnoreCase));
                    if (pos == null) continue;

                    var redeemable = (bool?)pos["redeemable"] ?? false;
                    var curPrice = (decimal?)pos["curPrice"] ?? 0m;
                    var size = (decimal?)pos["size"] ?? 0m;
                    if (redeemable && curPrice > 0 && size > 0)
                        winners.Add(new BigInteger(1 << i));
                }
                return winners.ToArray();
            }
            catch (Exception ex)
            {
                PolyLog.Redeem($"data-api winner check error: {ex.Message}");
                return Array.Empty<BigInteger>();
            }
        }

        private async Task<Dictionary<string, BigInteger>> CheckTokenBalances(string[] tokenIds)
        {
            var result = new Dictionary<string, BigInteger>();
            if (string.IsNullOrWhiteSpace(_privateKey) || tokenIds.Length == 0)
                return result;

            try
            {
                var account = new Account(_privateKey, 137);
                // Positions are held by the funder/deposit wallet (proxy), not the signer EOA.
                // Querying the EOA's balanceOf returns 0 for proxy-wallet setups (sigType 1/2/3).
                var holder = !string.IsNullOrWhiteSpace(_funderAddress) ? _funderAddress! : account.Address;
                var handler = new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(_proxyUrl),
                    UseProxy = true
                };
                var httpClient = new System.Net.Http.HttpClient(handler);
                var rpcClient = new RpcClient(new Uri(POLYGON_RPC), httpClient);
                var web3 = new Web3(account, rpcClient);

                var contract = web3.Eth.GetContract(BALANCE_OF_ABI, CTF_ADDRESS);
                var balanceOfFunc = contract.GetFunction("balanceOf");

                foreach (var tokenId in tokenIds)
                {
                    if (string.IsNullOrWhiteSpace(tokenId)) continue;
                    try
                    {
                        var tokenIdBig = BigInteger.Parse(tokenId);
                        var balance = await balanceOfFunc.CallAsync<BigInteger>(holder, tokenIdBig);
                        result[tokenId] = balance;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                PolyLog.Redeem($"Balance check error: {ex.Message}");
            }
            return result;
        }

        // ═══════════════════════════════════════════
        //  Background redeem queue
        // ═══════════════════════════════════════════
        public void EnqueueMarketRedeem(string? conditionId, string[]? tokenIds, DateTime endDate)
        {
            if (string.IsNullOrWhiteSpace(conditionId)) return;
            lock (_enqueuedConditionIds)
            {
                if (!_enqueuedConditionIds.Add(conditionId)) return; // already queued
            }
            _redeemQueue.Enqueue(new MarketRedeemJob
            {
                ConditionId = conditionId,
                TokenIds = tokenIds ?? Array.Empty<string>(),
                EndDate = endDate,
                EnqueuedAt = DateTime.UtcNow
            });
            PolyLog.Redeem($"Enqueued market cond={conditionId[..10]}... tokens=[{string.Join(",", tokenIds ?? Array.Empty<string>())}] endDate={endDate:u}");
        }

        public void StartRedeemWorker()
        {
            if (_redeemThread != null) return;
            _redeemCts = new CancellationTokenSource();
            _redeemThread = new Thread(() => RedeemWorkerLoop(_redeemCts.Token))
            {
                IsBackground = true,
                Name = "PolyRedeemWorker"
            };
            _redeemThread.Start();
            PolyLog.Redeem("Worker started.");
        }

        public void StopRedeemWorker()
        {
            _redeemCts?.Cancel();
            _redeemThread = null;
            PolyLog.Redeem("Worker stopped.");
        }

        private void RedeemWorkerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_redeemQueue.TryDequeue(out var job))
                    {
                        Thread.Sleep(5000);
                        continue;
                    }

                    // Wait until endDate has passed
                    if (DateTime.UtcNow < job.EndDate)
                    {
                        _redeemQueue.Enqueue(job);
                        Thread.Sleep(5000);
                        continue;
                    }

                    // Settlement is authoritative ON-CHAIN (payoutDenominator != 0), NOT via Gamma.
                    // Gamma's /markets?condition_ids= stops returning short-lived markets once they end
                    // (verified: returns []), so the old `settledMarket == null || !Closed` gate looped
                    // forever even though the condition was already resolved on-chain.
                    var condHex = job.ConditionId.StartsWith("0x") ? job.ConditionId.Substring(2) : job.ConditionId;
                    var resolveErr = PreCheckConditionResolved(condHex).GetAwaiter().GetResult();
                    if (resolveErr != null)
                    {
                        // Not resolved on-chain yet (or transient RPC error) — put back and wait.
                        _redeemQueue.Enqueue(job);
                        PolyLog.Redeem($"{job.ConditionId[..10]}... not resolved on-chain yet ({resolveErr}), waiting...");
                        Thread.Sleep(10000);
                        continue;
                    }

                    // Resolved on-chain — determine winning index sets via data-api (Gamma no longer serves the market).
                    var balances = CheckTokenBalances(job.TokenIds).GetAwaiter().GetResult();
                    var winningIndexSets = GetWinningIndexSetsFromDataApi(job.ConditionId, job.TokenIds);
                    PolyLog.Redeem($"{job.ConditionId[..10]}... resolved. balances=[{string.Join(",", balances.Select(kv => $"{(kv.Key.Length > 6 ? kv.Key.Substring(0, 6) : kv.Key)}:{kv.Value}"))}] winningIndexSets=[{string.Join(",", winningIndexSets)}]");

                    if (winningIndexSets.Length == 0)
                    {
                        PolyLog.Redeem($"{job.ConditionId[..10]}... no winning position or no balance, skip.");
                        continue;
                    }

                    // Proxy/deposit-wallet positions are held by the funder (proxy), not the signer EOA.
                    // CTF.redeemPositions only redeems msg.sender's own positions, so an EOA-sent redeem
                    // cannot claim them. Skip the on-chain submit until the wallet-exec/relayer path exists,
                    // rather than burning gas on a tx that reverts/no-ops.
                    if (!string.IsNullOrWhiteSpace(_funderAddress)
                        && !string.Equals(_funderAddress, _signerAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        PolyLog.Redeem($"{job.ConditionId[..10]}... WINNING indexSets=[{string.Join(",", winningIndexSets)}] but positions are held by funder proxy ({_funderAddress}), not signer EOA; EOA cannot redeem them. Needs wallet-exec/relayer path — skipping on-chain submit.");
                        continue;
                    }

                    // Try redeem: CTF first, then NegRisk fallback
                    PolyLog.Redeem($"{job.ConditionId[..10]}... try redeem CTF indexSets=[{string.Join(",", winningIndexSets)}]");
                    var result = RedeemPositionsAsync(job.ConditionId, winningIndexSets, isNegRisk: false).GetAwaiter().GetResult();
                    if (!result.Success)
                    {
                        PolyLog.Redeem($"{job.ConditionId[..10]}... CTF failed, fallback NegRisk. firstErr={result.Message}");
                        result = RedeemPositionsAsync(job.ConditionId, winningIndexSets, isNegRisk: true).GetAwaiter().GetResult();
                    }

                    if (result.Success)
                        PolyLog.Redeem($"OK: cond={job.ConditionId[..10]}... tx={result.TransactionHash}");
                    else
                        PolyLog.Redeem($"FAIL: cond={job.ConditionId[..10]}... err={result.Message}");

                    Thread.Sleep(5000); // pace between redeems
                }
                catch (Exception ex)
                {
                    PolyLog.Redeem($"Worker error: {ex.Message}");
                    Thread.Sleep(10000);
                }
            }
        }

        private async Task<bool> CheckOnChainBalance(string[] tokenIds)
        {
            if (string.IsNullOrWhiteSpace(_privateKey) || tokenIds.Length == 0)
                return false;

            try
            {
                var account = new Account(_privateKey, 137);
                var handler = new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(_proxyUrl),
                    UseProxy = true
                };
                var httpClient = new System.Net.Http.HttpClient(handler);
                var rpcClient = new RpcClient(new Uri(POLYGON_RPC), httpClient);
                var web3 = new Web3(account, rpcClient);

                var contract = web3.Eth.GetContract(BALANCE_OF_ABI, CTF_ADDRESS);
                var balanceOfFunc = contract.GetFunction("balanceOf");

                foreach (var tokenId in tokenIds)
                {
                    if (string.IsNullOrWhiteSpace(tokenId)) continue;
                    try
                    {
                        var tokenIdBig = BigInteger.Parse(tokenId);
                        var balance = await balanceOfFunc.CallAsync<BigInteger>(account.Address, tokenIdBig);
                        if (balance > 0) return true;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                PolyLog.Redeem($"Balance check error: {ex.Message}");
            }
            return false;
        }

        private void EnsureInitialized()
        {
            if (_restClient == null)
                throw new InvalidOperationException("PolymarketService is not initialized");
        }
    }

    public class EventInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string[] ClobTokenIds { get; set; } = Array.Empty<string>();
        public string? ConditionId { get; set; }
        public decimal YesPrice { get; set; }
        public decimal NoPrice { get; set; }
        public bool Active { get; set; }
        public bool Closed { get; set; }
    }

    public class MarketRedeemJob
    {
        public string ConditionId { get; set; } = string.Empty;
        public string[] TokenIds { get; set; } = Array.Empty<string>();
        public DateTime EndDate { get; set; }
        public DateTime EnqueuedAt { get; set; }
    }

    public class PlaceOrderResult
    {
        public bool Success { get; set; }
        public string? OrderId { get; set; }
        public string? Message { get; set; }
    }

    public class OrderBookInfo
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, TokenBookInfo> TokenBooks { get; set; } = new Dictionary<string, TokenBookInfo>();
    }

    public class TokenBookInfo
    {
        public decimal? BestBid { get; set; }
        public decimal? BestAsk { get; set; }
    }

    public class RedeemResult
    {
        public bool Success { get; set; }
        public string? TransactionHash { get; set; }
        public string? ConditionId { get; set; }
        public string? Message { get; set; }
    }
}
