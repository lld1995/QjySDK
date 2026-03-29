using System;
using System.Linq;
using System.Threading.Tasks;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using Polymarket.Net;
using Polymarket.Net.Clients;
using Polymarket.Net.Enums;
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
            var privateKey = "0x424183d4b934d900e63e972c7fcc205e54a07e5713d88fb7242dcbf6ac1f051f";
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

        [Fact]
        public async Task RedeemViaQueue()
        {
            var privateKey = "0x424183d4b934d900e63e972c7fcc205e54a07e5713d88fb7242dcbf6ac1f051f";
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
