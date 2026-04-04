using System;
using TDengine.Driver;
using TDengine.Driver.Client;
using Xunit;
using Xunit.Abstractions;

namespace QjySDK.Tests
{
    public class TDEngineDebugTests
    {
        private readonly ITestOutputHelper _output;
        public TDEngineDebugTests(ITestOutputHelper output) { _output = output; }

        private static string GetTableName(object val)
        {
            if (val is byte[] bytes)
                return System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            return val?.ToString() ?? "";
        }

        [Fact]
        public void ListTables()
        {
            var builder = new ConnectionStringBuilder(TestConfig.TDEngine);
            using var client = DbDriver.Open(builder);
            client.Exec("use finance");
            using var rows = client.Query("show tables");
            int count = 0;
            while (rows.Read() && count < 100)
            {
                _output.WriteLine(GetTableName(rows.GetValue(0)));
                count++;
            }
            _output.WriteLine($"Total shown: {count}");
        }

        [Fact]
        public void SearchChanLunTables()
        {
            var builder = new ConnectionStringBuilder(TestConfig.TDEngine);
            using var client = DbDriver.Open(builder);
            client.Exec("use finance");
            var targets = new[] { "btcusdt", "szse", "shse", "shfe", "000001", "510300", "au2" };
            using var rows = client.Query("show tables");
            while (rows.Read())
            {
                var name = GetTableName(rows.GetValue(0));
                foreach (var t in targets)
                {
                    if (name.Contains(t, StringComparison.OrdinalIgnoreCase))
                    {
                        _output.WriteLine(name);
                        break;
                    }
                }
            }
        }
    }
}
