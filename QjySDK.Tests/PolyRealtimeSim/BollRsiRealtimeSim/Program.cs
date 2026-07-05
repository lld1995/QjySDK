using Common;
using Model;
using QjySDK.Stg;
using stgInterface;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using TDengine.Driver;
using TDengine.Driver.Client;
using static Model.EnumDef;

var rawSymbol = Environment.GetEnvironmentVariable("POLY_SIM_RAW_SYMBOL") ?? "COIN_FUTURES_ETHUSDT";
var mktSymbol = Environment.GetEnvironmentVariable("POLY_SIM_MKT_SYMBOL") ?? "FUTURES_ETHUSDT";
var period = Period.TIME_5M;
var pollSeconds = GetEnvInt("POLY_SIM_POLL_SECONDS", 30);
var limit = GetEnvInt("POLY_SIM_BAR_LIMIT", 300);
var outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
Directory.CreateDirectory(outDir);
var logPath = Path.Combine(outDir, $"poly_boll_realtime_sim_{DateTime.Now:yyyyMMdd_HHmmss}.log");

SetPolymarketProxyEnvironment();
Log($"START BollRsiShortReversion realtime SIMULATION raw={rawSymbol} mkt={mktSymbol} period={period} poll={pollSeconds}s limit={limit} no_real_order=true polymarket_proxy=http://127.0.0.1:7888");

var stg = new BollRsiShortReversion("BollRsiShortReversion_RealtimeSim");
var sd = stg.GetStgDesc();
var argProp = typeof(StgBase).GetProperty("ArgDic", BindingFlags.NonPublic | BindingFlags.Instance)!;
argProp.SetValue(stg, sd.ArgDic);
var argDic = (Dictionary<string, object>)argProp.GetValue(stg)!;
argDic["mode"] = 2;
argDic["sendMode"] = 0;
argDic["lotsMode"] = 0;
argDic["lots"] = 1.0m;
argDic["money"] = 10000m;
argDic["polyNum"] = 0m;
stg.IsBacktest = true;

var tu = new TableUnit
{
    MktSymbol = mktSymbol,
    Period = period,
    QuoteList = new List<SkQuote>()
};

var rtrField = typeof(StgBase).GetField("_rtr", BindingFlags.NonPublic | BindingFlags.Instance)!;
var lastFinal = DateTime.MinValue;

while (true)
{
    try
    {
        var quotes = LoadKlines(rawSymbol, period, limit);
        if (quotes.Count < 60)
        {
            Log($"WAIT insufficient_bars count={quotes.Count}");
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
            continue;
        }

        var final = quotes[^1];
        if (final.Date <= lastFinal)
        {
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
            continue;
        }

        tu.QuoteList.Clear();
        tu.QuoteList.AddRange(quotes);
        stg.OnBar(period, tu, true, null!);

        var records = (List<RemoteTradeRecord>)rtrField.GetValue(stg)!;
        var signal = "none";
        if (records.Count > 0)
        {
            foreach (var r in records.ToList())
            {
                Log($"SIM_ORDER time={final.Date:yyyy-MM-dd HH:mm:ss} symbol={r.MktSymbol} ot={r.OT} price={r.Price.ToString(CultureInfo.InvariantCulture)} num={r.Num.ToString(CultureInfo.InvariantCulture)} period={r.P} sendMode={r.SendMode}");
            }
            records.Clear();
            signal = "order";
        }

        Log($"BAR time={final.Date:yyyy-MM-dd HH:mm:ss} open={final.Open.ToString(CultureInfo.InvariantCulture)} high={final.High.ToString(CultureInfo.InvariantCulture)} low={final.Low.ToString(CultureInfo.InvariantCulture)} close={final.Close.ToString(CultureInfo.InvariantCulture)} signal={signal}");
        lastFinal = final.Date;
    }
    catch (Exception ex)
    {
        Log($"ERROR {ex.GetType().FullName}: {ex.Message}");
    }

    await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
}

void Log(string message)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
    File.AppendAllText(logPath, line + Environment.NewLine);
    Console.WriteLine(line);
}

static int GetEnvInt(string name, int defaultValue)
{
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : defaultValue;
}

static void SetPolymarketProxyEnvironment()
{
    Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:7888");
    Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:7888");
    Environment.SetEnvironmentVariable("ALL_PROXY", "http://127.0.0.1:7888");
}

static string LoadTdengineConnectionString()
{
    var candidates = new[]
    {
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test_config.json")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test_config.json")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test_config.json")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "QjySDK.Tests", "test_config.json"))
    };

    foreach (var path in candidates)
    {
        if (!File.Exists(path))
            continue;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("TDEngine").GetString() ?? string.Empty;
    }

    throw new FileNotFoundException("test_config.json not found for TDEngine connection");
}

static string ToMktSymbol(string symbol)
{
    var parts = symbol.Split('_', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length >= 3 && (parts[0].Equals("COIN", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("STOCK", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("FUTURES", StringComparison.OrdinalIgnoreCase)))
    {
        var second = parts[1].ToUpperInvariant();
        if (second == "SPOT" || second == "FUTURES")
            return string.Join("_", parts, 1, parts.Length - 1);
    }
    return symbol;
}

List<SkQuote> LoadKlines(string symbol, Period p, int barLimit)
{
    var connStr = LoadTdengineConnectionString();
    var symbolForTable = ToMktSymbol(symbol);
    var tableName = (symbolForTable + "_" + p).Replace(".", string.Empty).ToLowerInvariant();
    var quotes = new List<SkQuote>();
    var builder = new ConnectionStringBuilder(connStr);
    using var client = DbDriver.Open(builder);
    client.Exec("use finance");
    using var rows = client.Query($"select * from {tableName} order by ts desc limit {barLimit}");
    if (rows.HasRows)
    {
        while (rows.Read())
        {
            quotes.Add(new SkQuote
            {
                Date = (DateTime)rows.GetValue(0),
                Open = decimal.Parse(rows.GetValue(1).ToString()!, CultureInfo.InvariantCulture),
                Close = decimal.Parse(rows.GetValue(2).ToString()!, CultureInfo.InvariantCulture),
                Low = decimal.Parse(rows.GetValue(3).ToString()!, CultureInfo.InvariantCulture),
                High = decimal.Parse(rows.GetValue(4).ToString()!, CultureInfo.InvariantCulture),
                Amount = decimal.Parse(rows.GetValue(5).ToString()!, CultureInfo.InvariantCulture),
                Volume = decimal.Parse(rows.GetValue(6).ToString()!, CultureInfo.InvariantCulture)
            });
        }
    }
    quotes.Reverse();
    return quotes;
}
