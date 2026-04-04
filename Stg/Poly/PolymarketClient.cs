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
        private const string NEG_RISK_ADAPTER = "0xd91E80cF2E7be2e162c6513ceD346b7C0F5b9F95";
        private const string USDC_E_ADDRESS = "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174";
        private const string POLYGON_RPC = "https://polygon-bor-rpc.publicnode.com";
        private const string PROXY_URL = "http://127.0.0.1:7888";

        private static readonly string REDEEM_ABI = "[{\"inputs\":[{\"name\":\"collateralToken\",\"type\":\"address\"},{\"name\":\"parentCollectionId\",\"type\":\"bytes32\"},{\"name\":\"conditionId\",\"type\":\"bytes32\"},{\"name\":\"indexSets\",\"type\":\"uint256[]\"}],\"name\":\"redeemPositions\",\"outputs\":[],\"stateMutability\":\"nonpayable\",\"type\":\"function\"}]";
        private static readonly string BALANCE_OF_ABI = "[{\"inputs\":[{\"name\":\"account\",\"type\":\"address\"},{\"name\":\"id\",\"type\":\"uint256\"}],\"name\":\"balanceOf\",\"outputs\":[{\"name\":\"\",\"type\":\"uint256\"}],\"stateMutability\":\"view\",\"type\":\"function\"}]";

        private PolymarketRestClient? _restClient;
        private string? _privateKey;

        private readonly ConcurrentQueue<MarketRedeemJob> _redeemQueue = new();
        private readonly HashSet<string> _enqueuedConditionIds = new();
        private CancellationTokenSource? _redeemCts;
        private Thread? _redeemThread;

        public async Task Init(string privateKey, string funderAddress = "")
        {
            if (string.IsNullOrWhiteSpace(privateKey))
                throw new ArgumentException("privateKey is required", nameof(privateKey));

            _privateKey = privateKey;
            var l1Cred = new PolymarketL1Credential(SignType.EOA, privateKey);

            _restClient?.Dispose();
            _restClient = new PolymarketRestClient(opts =>
            {
                opts.ApiCredentials = new PolymarketCredentials(l1Cred);
                opts.Proxy = new ApiProxy("http://127.0.0.1", 7888);
            });

            // Derive L2 API credentials from L1 private key
            var l2Creds = await _restClient.ClobApi.Account.GetOrCreateApiCredentialsAsync();
            if (!l2Creds.Success)
                throw new Exception("Failed to derive L2 credentials: " + l2Creds.Error?.Message);
            _restClient.UpdateL2Credentials(l2Creds.Data);
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

                    if (string.IsNullOrWhiteSpace(result.ConditionId) && !string.IsNullOrWhiteSpace(market.ConditionId))
                        result.ConditionId = market.ConditionId;

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

        public async Task<EventInfo?> GetLatestEventAsync(string slugPrefix, int slotSeconds)
        {
            EnsureInitialized();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var aligned = ts - (ts % slotSeconds);

            var evt = await GetEventAsync($"{slugPrefix}-{aligned}");
            if (evt != null && evt.Active && !evt.Closed)
                return evt;

            evt = await GetEventAsync($"{slugPrefix}-{aligned - slotSeconds}");
            if (evt != null && evt.Active && !evt.Closed)
                return evt;

            evt = await GetEventAsync($"{slugPrefix}-{aligned + slotSeconds}");
            if (evt != null && evt.Active && !evt.Closed)
                return evt;

            return null;
        }

        public async Task<PlaceOrderResult> PlaceOrderAsync(string tokenId, OrderSide side, decimal price, decimal size, int feeRateBps = 1000)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(tokenId))
                throw new ArgumentException("tokenId is required", nameof(tokenId));

            var response = await _restClient!.ClobApi.Trading.PlaceOrderAsync(
                tokenId,
                side,
                OrderType.Limit,
                size,
                price: price,
                feeRateBps: feeRateBps);

            return new PlaceOrderResult
            {
                Success = response.Success,
                Message = response.Error?.Message,
                OrderId = response.Data?.OrderId
            };
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

                Console.WriteLine($"[PolyOrder] Attempt {attempt}/{maxRetries}: {side} {size}@{price.Value}");

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
                        Console.WriteLine($"[PolyOrder] Filled: orderId={placeResult.OrderId}");
                        return placeResult;
                    }
                }

                // 未成交，撤单
                Console.WriteLine($"[PolyOrder] Not filled after {waitMs}ms, cancelling...");
                var cancelResult = await _restClient!.ClobApi.Trading.CancelOrderAsync(placeResult.OrderId);
                if (cancelResult.Success)
                    Console.WriteLine($"[PolyOrder] Cancelled: orderId={placeResult.OrderId}");
                else
                    Console.WriteLine($"[PolyOrder] Cancel failed: {cancelResult.Error?.Message}");

                if (attempt == maxRetries)
                {
                    Console.WriteLine($"[PolyOrder] Max retries reached, giving up.");
                    return new PlaceOrderResult { Success = false, Message = "Max retries reached, order not filled" };
                }
            }

            return new PlaceOrderResult { Success = false, Message = "Unexpected" };
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
                    Proxy = new System.Net.WebProxy(PROXY_URL),
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
                    return new RedeemResult { Success = false, TransactionHash = txHash, Message = $"{sourceName}: 已提交但未确认(超时)" };

                if (receipt.Status.Value == 1)
                    return new RedeemResult { Success = true, TransactionHash = txHash };
                else
                    return new RedeemResult { Success = false, TransactionHash = txHash, Message = $"{sourceName}: 交易链上 revert" };
            }
            catch (Exception ex)
            {
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

        private async Task<Dictionary<string, BigInteger>> CheckTokenBalances(string[] tokenIds)
        {
            var result = new Dictionary<string, BigInteger>();
            if (string.IsNullOrWhiteSpace(_privateKey) || tokenIds.Length == 0)
                return result;

            try
            {
                var account = new Account(_privateKey, 137);
                var handler = new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(PROXY_URL),
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
                        result[tokenId] = balance;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PolyRedeem] Balance check error: {ex.Message}");
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
            Console.WriteLine($"[PolyRedeem] Enqueued market {conditionId[..10]}... endDate={endDate:u}");
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
            Console.WriteLine("[PolyRedeem] Worker started.");
        }

        public void StopRedeemWorker()
        {
            _redeemCts?.Cancel();
            _redeemThread = null;
            Console.WriteLine("[PolyRedeem] Worker stopped.");
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

                    // Check if market is settled via Gamma API
                    var marketsResp = _restClient!.GammaApi.GetMarketsAsync(conditionIds: new[] { job.ConditionId })
                        .GetAwaiter().GetResult();
                    PolymarketGammaMarket? settledMarket = null;
                    if (marketsResp.Success && marketsResp.Data != null)
                        settledMarket = marketsResp.Data.FirstOrDefault(m => m.ConditionId == job.ConditionId);

                    if (settledMarket == null || !settledMarket.Closed)
                    {
                        // Not settled yet, put back and wait
                        _redeemQueue.Enqueue(job);
                        Console.WriteLine($"[PolyRedeem] {job.ConditionId[..10]}... not settled yet, waiting...");
                        Thread.Sleep(10000);
                        continue;
                    }

                    // Market is settled — determine winning index sets
                    var winningIndexSets = GetWinningIndexSets(settledMarket, job.TokenIds,
                        CheckTokenBalances(job.TokenIds).GetAwaiter().GetResult());

                    if (winningIndexSets.Length == 0)
                    {
                        Console.WriteLine($"[PolyRedeem] {job.ConditionId[..10]}... no winning position or no balance, skip.");
                        continue;
                    }

                    // Try redeem: CTF first, then NegRisk fallback
                    var result = RedeemPositionsAsync(job.ConditionId, winningIndexSets, isNegRisk: false).GetAwaiter().GetResult();
                    if (!result.Success)
                        result = RedeemPositionsAsync(job.ConditionId, winningIndexSets, isNegRisk: true).GetAwaiter().GetResult();

                    if (result.Success)
                        Console.WriteLine($"[PolyRedeem] OK: {job.ConditionId[..10]}... tx={result.TransactionHash}");
                    else
                        Console.WriteLine($"[PolyRedeem] FAIL: {job.ConditionId[..10]}... err={result.Message}");

                    Thread.Sleep(5000); // pace between redeems
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PolyRedeem] Error: {ex.Message}");
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
                    Proxy = new System.Net.WebProxy(PROXY_URL),
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
                Console.WriteLine($"[PolyRedeem] Balance check error: {ex.Message}");
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
