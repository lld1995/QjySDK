using System;
using System.Linq;
using System.Threading.Tasks;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using Polymarket.Net;
using Polymarket.Net.Clients;
using Polymarket.Net.Enums;
using Polymarket.Net.Objects.Models;
using Xunit;
using Xunit.Abstractions;

namespace QjySDK.Tests
{
    public class PolymarketQueryTests
    {
        private readonly ITestOutputHelper _output;
        public PolymarketQueryTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task QueryOrdersAndBalance()
        {
            var privateKey = QjySDK.Stg.PolySecrets.PrivateKey;
            var l1Cred = new PolymarketL1Credential(SignType.EOA, privateKey);
            using var client = new PolymarketRestClient(opts =>
            {
                opts.ApiCredentials = new PolymarketCredentials(l1Cred);
                opts.Proxy = new ApiProxy("http://127.0.0.1", 7888);
            });

            var l2Creds = await client.ClobApi.Account.GetOrCreateApiCredentialsAsync();
            if (!l2Creds.Success)
            {
                _output.WriteLine($"L2 creds failed: {l2Creds.Error?.Message}");
                _output.WriteLine("Trying to derive credentials another way...");
                // Try delete and recreate
                await client.ClobApi.Account.DeleteApiKeyAsync();
                l2Creds = await client.ClobApi.Account.GetOrCreateApiCredentialsAsync();
            }
            if (!l2Creds.Success)
            {
                _output.WriteLine($"L2 creds still failed: {l2Creds.Error?.Message}. Aborting.");
                return;
            }
            client.UpdateL2Credentials(l2Creds.Data);
            _output.WriteLine("L2 credentials OK.\n");

            // Open orders
            _output.WriteLine("=== Open Orders ===");
            var openOrders = await client.ClobApi.Trading.GetOpenOrdersAsync();
            if (openOrders.Success && openOrders.Data?.Data != null)
            {
                var list = openOrders.Data.Data.ToList();
                _output.WriteLine($"Count: {list.Count}");
                foreach (var o in list)
                    foreach (var prop in o.GetType().GetProperties())
                        _output.WriteLine($"  {prop.Name} = {prop.GetValue(o)}");
            }
            else
                _output.WriteLine($"Failed: {openOrders.Error?.Message}");

            // Balance
            _output.WriteLine("\n=== Balance (Collateral) ===");
            var bal = await client.ClobApi.Account.GetBalanceAllowanceAsync(AssetType.Collateral);
            if (bal.Success && bal.Data != null)
                foreach (var prop in bal.Data.GetType().GetProperties())
                    _output.WriteLine($"  {prop.Name} = {prop.GetValue(bal.Data)}");
            else
                _output.WriteLine($"Failed: {bal.Error?.Message}");

            // Conditional token balance
            _output.WriteLine("\n=== Balance (Conditional) ===");
            var condBal = await client.ClobApi.Account.GetBalanceAllowanceAsync(AssetType.Conditional);
            if (condBal.Success && condBal.Data != null)
                foreach (var prop in condBal.Data.GetType().GetProperties())
                    _output.WriteLine($"  {prop.Name} = {prop.GetValue(condBal.Data)}");
            else
                _output.WriteLine($"Failed: {condBal.Error?.Message}");

            // User trades history
            _output.WriteLine("\n=== User Trades ===");
            var trades = await client.ClobApi.Trading.GetUserTradesAsync();
            if (trades.Success && trades.Data != null)
            {
                foreach (var prop in trades.Data.GetType().GetProperties())
                {
                    var val = prop.GetValue(trades.Data);
                    if (val is System.Collections.IEnumerable enumerable && val is not string)
                    {
                        _output.WriteLine($"  {prop.Name}:");
                        foreach (var item in enumerable)
                        {
                            foreach (var p in item.GetType().GetProperties())
                                _output.WriteLine($"    {p.Name} = {p.GetValue(item)}");
                            _output.WriteLine("    ---");
                        }
                    }
                    else
                        _output.WriteLine($"  {prop.Name} = {val}");
                }
            }
            else
                _output.WriteLine($"Failed: {trades.Error?.Message}");
        }

        /// <summary>
        /// 真实下单验证：用极小金额 ($1 notional) 在一个活跃市场挂一笔远离行情的限价买单，
        /// 验证 V2 签名通过、orderId 返回，然后立刻撤单清理。
        /// 需要 poly_secrets.txt 配好 PRIVATE_KEY，钱包至少有 ~$1 pUSD/USDC.e 余额。
        /// </summary>
        [Fact]
        public async Task PlaceMinimumOrder_RealApi()
        {
            var privateKey = QjySDK.Stg.PolySecrets.PrivateKey;
            var funder = QjySDK.Stg.PolySecrets.FunderAddress;
            Assert.False(string.IsNullOrWhiteSpace(privateKey), "PolySecrets.PrivateKey is empty - configure poly_secrets.txt");
            _output.WriteLine($"PrivateKey set: yes  FunderAddress: {(string.IsNullOrWhiteSpace(funder) ? "(empty, using EOA signing)" : funder)}");

            // 用 PolymarketService 走我们封装的 PlaceOrderAsync（V2 签名路径）
            var svc = new QjySDK.Stg.PolymarketService();
            await svc.Init(privateKey, funder);

            // 同时建一个原始 client 用来查 Gamma / 撤单（使用同样的 SignType 以便 L2 凭据匹配）
            var signType = string.IsNullOrWhiteSpace(funder) ? SignType.EOA : SignType.Email;
            var l1Cred = new PolymarketL1Credential(signType, privateKey, funder ?? string.Empty);
            using var raw = new PolymarketRestClient(opts =>
            {
                opts.ApiCredentials = new PolymarketCredentials(l1Cred);
                opts.Proxy = new ApiProxy("http://127.0.0.1", 7888);
            });
            // Prefer cached L2 creds (poly_secrets.txt) - Polymarket disallows creating a 2nd API key for the same EOA.
            if (!string.IsNullOrWhiteSpace(QjySDK.Stg.PolySecrets.PolyApiKey)
                && !string.IsNullOrWhiteSpace(QjySDK.Stg.PolySecrets.PolyApiSecret)
                && !string.IsNullOrWhiteSpace(QjySDK.Stg.PolySecrets.PolyApiPassphrase))
            {
                raw.UpdateL2Credentials(new PolymarketCreds
                {
                    ApiKey = QjySDK.Stg.PolySecrets.PolyApiKey,
                    Secret = QjySDK.Stg.PolySecrets.PolyApiSecret,
                    Passphrase = QjySDK.Stg.PolySecrets.PolyApiPassphrase,
                });
            }
            else
            {
                var l2 = await raw.ClobApi.Account.GetOrCreateApiCredentialsAsync();
                Assert.True(l2.Success, $"L2 creds failed: {l2.Error?.Message}");
                raw.UpdateL2Credentials(l2.Data!);
            }

            // 1) 找一个接受订单的活跃市场
            _output.WriteLine("=== Discovering an active market ===");
            var marketsResp = await raw.GammaApi.GetMarketsAsync(closed: false, limit: 100);
            Assert.True(marketsResp.Success, $"GetMarkets failed: {marketsResp.Error?.Message}");
            Assert.NotNull(marketsResp.Data);

            var picked = marketsResp.Data!
                .Where(m => m.Active && !m.Closed && m.AcceptingOrders && m.EnableOrderBook
                            && m.ClobTokenIds != null && m.ClobTokenIds.Length > 0)
                .FirstOrDefault();
            Assert.NotNull(picked);
            var tokenId = picked!.ClobTokenIds!.First(t => !string.IsNullOrWhiteSpace(t));
            _output.WriteLine($"Market: {picked.Question}");
            _output.WriteLine($"  slug={picked.Slug}");
            _output.WriteLine($"  marketId={picked.MarketId}");
            _output.WriteLine($"  token={tokenId}");
            _output.WriteLine($"  tick={picked.OrderPriceMinTickQuantity}  minQty={picked.OrderMinQuantity}");

            // 2) 获取盘口，确认当前最优卖价远高于我们的报价，防止意外成交
            var book = await svc.GetOrderBookAsync(new[] { tokenId });
            Assert.True(book.Success, $"OrderBook failed: {book.Message}");
            Assert.True(book.TokenBooks.ContainsKey(tokenId), "no book for token");
            var info = book.TokenBooks[tokenId];
            _output.WriteLine($"  bestBid={info.BestBid}  bestAsk={info.BestAsk}");

            // 3) 构造极小订单：最小 tick 价 * 最小份数，保证 notional 接近 $1（Polymarket 最小 notional 通常为 $1）
            decimal tick = picked.OrderPriceMinTickQuantity > 0 ? picked.OrderPriceMinTickQuantity : 0.01m;
            decimal price = tick; // 1 cent，最低有效报价
            decimal minQty = picked.OrderMinQuantity > 0 ? picked.OrderMinQuantity : 5m;
            decimal size = Math.Max(minQty, Math.Ceiling(1m / price)); // 保证 notional ≥ $1
            _output.WriteLine($"Placing BUY @ {price}  size={size}  notional=${(price * size):F4}");

            // 安全检查：最优卖价应远高于我们的买价，否则跳过避免成交
            if (info.BestAsk.HasValue && info.BestAsk.Value <= price * 2m)
            {
                _output.WriteLine($"Skipping: bestAsk={info.BestAsk} too close to our price={price}, avoid fill");
                return;
            }

            // 4) 调用 V2 PlaceOrderAsync
            var placeResult = await svc.PlaceOrderAsync(tokenId, OrderSide.Buy, price, size);
            _output.WriteLine($"Place result: Success={placeResult.Success}  orderId={placeResult.OrderId}  msg={placeResult.Message}");
            Assert.True(placeResult.Success, $"PlaceOrderAsync failed: {placeResult.Message}");
            Assert.False(string.IsNullOrWhiteSpace(placeResult.OrderId), "OrderId should not be empty on success");

            // 5) 撤单清理（即便后面断言失败也撤掉）
            try
            {
                await Task.Delay(500);
                var cancel = await raw.ClobApi.Trading.CancelOrderAsync(placeResult.OrderId!);
                _output.WriteLine($"Cancel: Success={cancel.Success}  msg={cancel.Error?.Message}");
                Assert.True(cancel.Success, $"Cancel failed: {cancel.Error?.Message}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cancel exception: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 一次性辅助测试：删掉旧的 V1 时代 API key（绑定到 EOA 的），让 Polymarket 重新生成
        /// 一个绑定到 deposit wallet 的新 key。运行后会打印新 (key/secret/passphrase)，
        /// 把这三个值粘回 poly_secrets.txt 即可。
        /// </summary>
        [Fact]
        public async Task RotateApiKeyForDepositWallet()
        {
            var privateKey = QjySDK.Stg.PolySecrets.PrivateKey;
            var funder = QjySDK.Stg.PolySecrets.FunderAddress;
            Assert.False(string.IsNullOrWhiteSpace(privateKey), "PRIVATE_KEY missing in poly_secrets.txt");

            var l1Cred = new PolymarketL1Credential(SignType.Email, privateKey, funder ?? "");
            using var client = new PolymarketRestClient(opts =>
            {
                opts.ApiCredentials = new PolymarketCredentials(l1Cred);
                opts.Proxy = new ApiProxy("http://127.0.0.1", 7888);
            });

            // 直接尝试用不同 nonce 创建新 key（POST /auth/api-key 只需要 L1 auth，不需要 L2）。
            // 默认 nonce=0 已被旧 V1 key 占用。
            for (int nonce = 1; nonce <= 5; nonce++)
            {
                _output.WriteLine($"\n=== Trying nonce={nonce} ===");
                var create = await client.ClobApi.Account.CreateApiCredentialsAsync(nonce: nonce);
                _output.WriteLine($"  create: success={create.Success}  err={create.Error?.Message}");
                if (create.Success && create.Data != null && !string.IsNullOrEmpty(create.Data.ApiKey))
                {
                    _output.WriteLine("\n>>> NEW credentials (paste into poly_secrets.txt) <<<");
                    _output.WriteLine($"POLY_API_KEY={create.Data.ApiKey}");
                    _output.WriteLine($"POLY_API_SECRET={create.Data.Secret}");
                    _output.WriteLine($"POLY_API_PASSPHRASE={create.Data.Passphrase}");
                    return;
                }
            }
            Assert.Fail("failed to create new API key with nonces 1-5");
        }

        /// <summary>
        /// Reads the deposit wallet's `owner()` function on-chain to identify which EOA controls it.
        /// Standard ERC-1967 deposit wallets expose owner() returning the EOA owner used by ERC-1271
        /// isValidSignature. Knowing this tells us which private key must sign POLY_1271 orders.
        /// </summary>
        [Fact]
        public async Task ReadDepositWalletOwner()
        {
            var deposit = QjySDK.Stg.PolySecrets.FunderAddress;
            Assert.False(string.IsNullOrWhiteSpace(deposit));

            var handler = new System.Net.Http.HttpClientHandler { Proxy = new System.Net.WebProxy("http://127.0.0.1:7888"), UseProxy = true };
            var httpClient = new System.Net.Http.HttpClient(handler);
            var rpcClient = new Nethereum.JsonRpc.Client.RpcClient(new Uri("https://polygon-bor-rpc.publicnode.com"), httpClient);
            var web3 = new Nethereum.Web3.Web3(rpcClient);

            // owner()
            var ownerCall = await web3.Eth.Transactions.Call.SendRequestAsync(new Nethereum.RPC.Eth.DTOs.CallInput { To = deposit, Data = "0x8da5cb5b" });
            _output.WriteLine($"owner(): {ownerCall}");

            // sessionSignerAuthorizedUntil(address) selector = bytes4(keccak256("sessionSignerAuthorizedUntil(address)"))
            var sel = Nethereum.Util.Sha3Keccack.Current.CalculateHash(System.Text.Encoding.ASCII.GetBytes("sessionSignerAuthorizedUntil(address)"));
            var selector = "0x" + BitConverter.ToString(sel, 0, 4).Replace("-", "").ToLowerInvariant();
            _output.WriteLine($"sessionSignerAuthorizedUntil selector: {selector}");

            var devCode = QjySDK.Stg.PolySecrets.PolyDevCode;
            var devAddr = !string.IsNullOrWhiteSpace(devCode) ? new Nethereum.Signer.EthECKey(devCode).GetPublicAddress() : null;
            var eoa = new Nethereum.Signer.EthECKey(QjySDK.Stg.PolySecrets.PrivateKey).GetPublicAddress();

            // Print event topics so we can filter logs externally
            string[] sigs = { "SessionSignerAuthorized(address,uint256)", "SessionSignerRevoked(address)",
                "OwnershipTransferred(address,address)", "Initialized(uint64)", "Upgraded(address)",
                "SessionSignerAuthorized(address,uint256,bytes32)" };
            foreach (var s in sigs)
            {
                var h = Nethereum.Util.Sha3Keccack.Current.CalculateHash(System.Text.Encoding.ASCII.GetBytes(s));
                _output.WriteLine($"  topic0 {s} = 0x{BitConverter.ToString(h).Replace("-", "").ToLowerInvariant()}");
            }

            var candidates = new (string Label, string Addr)[]
            {
                ("EOA",        eoa),
                ("DevWallet",  devAddr ?? "0x0000000000000000000000000000000000000000"),
                ("OwnerKey",   "0x35159d11752d6905c0592adf9cc1f532ef08a710"),
            };
            foreach (var (label, addr) in candidates)
            {
                var data = selector + addr.Substring(2).PadLeft(64, '0').ToLowerInvariant();
                try
                {
                    var r = await web3.Eth.Transactions.Call.SendRequestAsync(new Nethereum.RPC.Eth.DTOs.CallInput { To = deposit, Data = data });
                    var v = System.Numerics.BigInteger.Parse("0" + r.Substring(2), System.Globalization.NumberStyles.HexNumber);
                    _output.WriteLine($"sessionSignerAuthorizedUntil({label} {addr}) = {v}  ({(v > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)v).ToString("u") : "NOT AUTHORIZED")})");
                }
                catch (Exception e) { _output.WriteLine($"{label} ERR: {e.Message.Split('\n')[0]}"); }
            }
        }

        /// <summary>
        /// Final attempt: use the FRESH API key we just created (bound to MetaMask EOA, owner of deposit wallet)
        /// to place a POLY_1271 order signed by MetaMask PK.
        /// </summary>
        [Fact]
        public async Task NewAcct_PlaceOrderWithFreshApiKey()
        {
            // Secrets are loaded from poly_secrets.txt ONLY (gitignored) — never hardcode them here.
            var DepositWallet = QjySDK.Stg.PolySecrets.FunderAddress;
            var MetaMaskPk    = QjySDK.Stg.PolySecrets.PrivateKey;
            var ApiKey     = QjySDK.Stg.PolySecrets.PolyApiKey;
            var ApiSecret  = QjySDK.Stg.PolySecrets.PolyApiSecret;
            var Passphrase = QjySDK.Stg.PolySecrets.PolyApiPassphrase;
            var eoa = new Nethereum.Signer.EthECKey(MetaMaskPk).GetPublicAddress();

            using var rawClient = new PolymarketRestClient(opts =>
            {
                opts.ApiCredentials = new PolymarketCredentials(new PolymarketL1Credential(SignType.EOA, MetaMaskPk));
                opts.Proxy = new ApiProxy("http://127.0.0.1", 7888);
            });
            var marketsResp = await rawClient.GammaApi.GetMarketsAsync(closed: false, limit: 100);
            Assert.True(marketsResp.Success);
            var picked = marketsResp.Data!
                .Where(m => m.Active && !m.Closed && m.AcceptingOrders && m.EnableOrderBook
                         && m.ClobTokenIds != null && m.ClobTokenIds.Length > 0
                         && !m.NegativeRiskOther)
                .First();
            var tokenId = picked.ClobTokenIds!.First(t => !string.IsNullOrWhiteSpace(t));
            decimal tick = picked.OrderPriceMinTickQuantity > 0 ? picked.OrderPriceMinTickQuantity : 0.01m;
            decimal minQty = picked.OrderMinQuantity > 0 ? picked.OrderMinQuantity : 5m;
            decimal size = Math.Max(minQty, Math.Ceiling(1m / tick));
            _output.WriteLine($"Market: {picked.Question?.Substring(0, Math.Min(60, picked.Question.Length))}  tick={tick}  size={size}");

            var rawOrder = QjySDK.Stg.Poly.V2.PolymarketV2OrderBuilder.Build(QjySDK.Stg.Poly.V2.OrderSide.Buy, tick, size, tick);
            var signed = QjySDK.Stg.Poly.V2.PolymarketV2Signer.BuildAndSign(
                privateKeyHex: MetaMaskPk,
                maker: DepositWallet,
                orderSignerAddress: DepositWallet,
                tokenId: tokenId,
                raw: rawOrder,
                signatureType: QjySDK.Stg.Poly.V2.PolymarketV2Constants.SigTypePoly1271,
                negRisk: picked.NegativeRiskOther,
                erc1271WalletForWrap: DepositWallet);

            using var oc = new QjySDK.Stg.Poly.V2.PolymarketV2OrderClient(proxyUrl: "http://127.0.0.1:7888")
            {
                OnDebug = m => _output.WriteLine("DBG " + m),
            };

            // POLY_ADDRESS = EOA (the API key is bound to EOA per L1 auth).
            var resp = await oc.PostOrderAsync(
                signed,
                apiOwner: ApiKey,
                signerAddress: eoa,
                apiKey: ApiKey, apiSecretB64Url: ApiSecret, passphrase: Passphrase,
                orderType: QjySDK.Stg.Poly.V2.PolymarketV2OrderType.GTC);
            _output.WriteLine($"POST /order: success={resp.Success}  orderId={resp.OrderId}");
            _output.WriteLine($"            err={resp.ErrorMessage}");
            _output.WriteLine($"            raw={resp.RawBody}");

            if (resp.Success && !string.IsNullOrWhiteSpace(resp.OrderId))
            {
                await Task.Delay(500);
                var (ok, body) = await oc.CancelOrderAsync(resp.OrderId, eoa, ApiKey, ApiSecret, Passphrase);
                _output.WriteLine($"Cancel: ok={ok}  body={body}");
            }
        }

        [Fact]
        public void NewAcct_VerifyMetaMaskPkDerivation()
        {
            // Private key loaded from poly_secrets.txt ONLY (gitignored) — never hardcode it here.
            var pk = QjySDK.Stg.PolySecrets.PrivateKey;
            const string expected = "0xd0B9d16F9543670dcB739b37357F7562a5d734A2";
            var derived = new Nethereum.Signer.EthECKey(pk).GetPublicAddress();
            _output.WriteLine($"derived:  {derived}");
            _output.WriteLine($"expected: {expected}");
            Assert.Equal(expected, derived, ignoreCase: true);
        }

        [Fact]
        public async Task RedeemViaQueue()
        {
            var privateKey = QjySDK.Stg.PolySecrets.PrivateKey;
            var svc = new QjySDK.Stg.PolymarketService();
            await svc.Init(privateKey);

            _output.WriteLine("=== Testing queue-based redeem ===");
            // Enqueue a test market (will be processed by background worker)
            svc.EnqueueMarketRedeem("0xtest_condition_id", new[] { "12345" }, DateTime.UtcNow.AddSeconds(-1));
            svc.StartRedeemWorker();

            // Let the worker run briefly
            await Task.Delay(15000);
            svc.StopRedeemWorker();

            _output.WriteLine("Queue redeem test completed.");
        }
    }
}
