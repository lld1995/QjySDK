using Common;
using Model;
using stgInterface;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    /// <summary>
    /// Polymarket 策略基类：抽取 Poly 初始化、事件刷新、限价吃单等共用逻辑。
    /// 派生类（ExtremeReversal / MoEPredict / TimesFmDirection / OneBarReversionBase）
    /// 通过调用受保护成员复用同一套 Polymarket 接入流程。
    /// </summary>
    public abstract class PolyStgBase : StgBase
    {
        protected PolyStgBase() { }
        protected PolyStgBase(string id) : base(id) { }

        protected PolymarketService? _polyService;
        protected bool _serviceInited;
        protected bool _polyOrderPlaced;
        protected DateTime? _eventEndDate;
        protected string? _yesTokenId;
        protected string? _noTokenId;
        protected string? _currentConditionId;

        /// <summary>
        /// 注册 Polymarket 相关公共参数（私钥/代理钱包/事件标签/价格阈值/下单数量）。
        /// </summary>
        protected static void AddPolyArgs(StgDesc sd, decimal defaultPolyNum)
        {
            sd.ArgDescDic["privateKey"] = new ArgDesc { Text = "钱包私钥", Explain = "Polymarket 钱包私钥，留空则从 poly_secrets.txt 读取" };
            sd.ArgDic["privateKey"] = "";

            sd.ArgDescDic["funderAddress"] = new ArgDesc { Text = "Proxy钱包地址", Explain = "Polymarket网站 Profile 里的 Wallet Address，留空则从 poly_secrets.txt 读取" };
            sd.ArgDic["funderAddress"] = "";

            sd.ArgDescDic["eventTag"] = new ArgDesc { Text = "事件标签", Explain = "默认 Ethereum；周期标签按当前K线周期自动推导（如 5M/15M/1H）" };
            sd.ArgDic["eventTag"] = "Ethereum";

            sd.ArgDescDic["minPriceNearEnd"] = new ArgDesc { Text = "Poly最低价(分)", Explain = "Polymarket下单条件：best ask > 该值(分)且剩余时间 < nearEndMinutes" };
            sd.ArgDic["minPriceNearEnd"] = 65;

            sd.ArgDescDic["nearEndMinutes"] = new ArgDesc { Text = "Poly临近结束(分钟)", Explain = "剩余时间小于该值时允许Polymarket下单" };
            sd.ArgDic["nearEndMinutes"] = 3;

            sd.ArgDescDic["polyNum"] = new ArgDesc { Text = "Poly下单数量", Explain = "Polymarket 下单数量(USDC)，0则不下单" };
            sd.ArgDic["polyNum"] = defaultPolyNum;
        }

        protected void EnsurePolymarketInitialized(Period period)
        {
            if (_serviceInited) return;
            if (IsBacktest) return;
            if (ArgDic == null) return;

            var privateKey = Convert.ToString(ArgDic["privateKey"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(privateKey)) privateKey = PolySecrets.PrivateKey;
            if (string.IsNullOrWhiteSpace(privateKey)) return;

            var funderAddress = Convert.ToString(ArgDic["funderAddress"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(funderAddress)) funderAddress = PolySecrets.FunderAddress;
            _polyService = new PolymarketService();
            _polyService.Init(privateKey, funderAddress).GetAwaiter().GetResult();

            var eventTag = Convert.ToString(ArgDic["eventTag"]) ?? "Ethereum";
            var evt = _polyService.GetLatestEventAsync(GetSlugPrefix(eventTag, period), GetSlotSeconds(period))
                .GetAwaiter().GetResult();
            ApplyEventInfo(evt);

            _polyService.StartRedeemWorker();
            _serviceInited = true;
            PolyLog.Event($"{GetType().Name} init done eventTag={eventTag} period={period} cond={_currentConditionId} endDate={(_eventEndDate.HasValue ? _eventEndDate.Value.ToString("u") : "-")} yes={Trim(_yesTokenId)} no={Trim(_noTokenId)}");
        }

        private static string Trim(string? s) => string.IsNullOrEmpty(s) ? "-" : (s.Length > 8 ? s.Substring(0, 8) + "..." : s);

        protected void RefreshEventIfNeeded(DateTime barTime, Period period)
        {
            if (ArgDic == null || _polyService == null) return;

            var barTimeUtc = ToUtc(barTime);
            if (_eventEndDate.HasValue && barTimeUtc < _eventEndDate.Value.AddSeconds(-1))
                return;

            // 旧市场入赎回队列
            if (!string.IsNullOrWhiteSpace(_currentConditionId) && _eventEndDate.HasValue)
            {
                var oldTokenIds = new[] { _yesTokenId, _noTokenId }.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray()!;
                _polyService.EnqueueMarketRedeem(_currentConditionId, oldTokenIds, _eventEndDate.Value);
            }

            var eventTag = Convert.ToString(ArgDic["eventTag"]) ?? "Ethereum";
            var evt = _polyService.GetLatestEventAsync(GetSlugPrefix(eventTag, period), GetSlotSeconds(period))
                .GetAwaiter().GetResult();
            ApplyEventInfo(evt);
            PolyLog.Event($"{GetType().Name} event refreshed barTime={barTime:u} cond={_currentConditionId} endDate={(_eventEndDate.HasValue ? _eventEndDate.Value.ToString("u") : "-")} yes={Trim(_yesTokenId)} no={Trim(_noTokenId)}");
        }

        protected void ApplyEventInfo(EventInfo? evt)
        {
            if (evt == null) return;

            if (evt.EndDate > DateTime.MinValue)
                _eventEndDate = ToUtc(evt.EndDate);

            _currentConditionId = evt.ConditionId;

            if (evt.ClobTokenIds is { Length: > 0 })
            {
                _yesTokenId = evt.ClobTokenIds[0];
                _noTokenId = evt.ClobTokenIds.Length > 1 ? evt.ClobTokenIds[1] : null;
            }

            _polyOrderPlaced = false;
        }

        /// <summary>
        /// 在 Polymarket 上以 BestAsk 限价吃单（实质等价于市价吃单）。
        /// 会先检查事件剩余时间、价格档位、K线方向等过滤条件。
        /// </summary>
        protected bool TryPlacePolymarketOrder(bool isLongSignal, decimal size, SkQuote q)
        {
            var stgName = GetType().Name;
            var sideTag = isLongSignal ? "YES" : "NO";
            if (_polyService == null || !_eventEndDate.HasValue || ArgDic == null)
            {
                PolyLog.Order($"{stgName} skip: poly not ready (svc={(_polyService != null)} endDate={_eventEndDate.HasValue} args={(ArgDic != null)}) bar={q.Date:u} side={sideTag}");
                return false;
            }

            var remainSeconds = (_eventEndDate.Value - DateTime.UtcNow).TotalSeconds;
            int nearEndMinutes = Convert.ToInt32(ArgDic["nearEndMinutes"]);
            if (remainSeconds >= nearEndMinutes * 60)
            {
                PolyLog.Order($"{stgName} skip: remain={remainSeconds:F0}s >= nearEnd={nearEndMinutes * 60}s bar={q.Date:u} side={sideTag}");
                return false;
            }

            var tokenId = isLongSignal ? _yesTokenId : _noTokenId;
            if (string.IsNullOrWhiteSpace(tokenId))
            {
                PolyLog.Order($"{stgName} skip: tokenId empty bar={q.Date:u} side={sideTag}");
                return false;
            }

            var bookInfo = _polyService.GetOrderBookAsync(new[] { tokenId }).GetAwaiter().GetResult();
            if (!bookInfo.Success || !bookInfo.TokenBooks.TryGetValue(tokenId, out var book))
            {
                PolyLog.Order($"{stgName} skip: orderbook failed success={bookInfo.Success} msg={bookInfo.Message} bar={q.Date:u} side={sideTag}");
                return false;
            }

            var price = book.BestAsk;
            if (!price.HasValue || price.Value <= 0 || price.Value > 1)
            {
                PolyLog.Order($"{stgName} skip: invalid bestAsk={price} bar={q.Date:u} side={sideTag}");
                return false;
            }

            decimal minPriceNearEnd = Convert.ToDecimal(ArgDic["minPriceNearEnd"]);
            if (price.Value * 100m <= minPriceNearEnd)
            {
                PolyLog.Order($"{stgName} skip: bestAsk={price.Value * 100m:F1}c <= minPriceNearEnd={minPriceNearEnd}c bar={q.Date:u} side={sideTag}");
                return false;
            }

            if (remainSeconds > 2 * 60 && price.Value * 100m >= 75m)
            {
                PolyLog.Order($"{stgName} skip: bestAsk={price.Value * 100m:F1}c >= 75c & remain>{2}m ({remainSeconds:F0}s) bar={q.Date:u} side={sideTag}");
                return false;
            }
            if (remainSeconds > 1 * 60 && price.Value * 100m >= 85m)
            {
                PolyLog.Order($"{stgName} skip: bestAsk={price.Value * 100m:F1}c >= 85c & remain>{1}m ({remainSeconds:F0}s) bar={q.Date:u} side={sideTag}");
                return false;
            }
            if (price.Value * 100m >= 90m)
            {
                PolyLog.Order($"{stgName} skip: bestAsk={price.Value * 100m:F1}c >= 90c bar={q.Date:u} side={sideTag}");
                return false;
            }

            if (isLongSignal)
            {
                if (q.Close - q.Open < 0.2m)
                {
                    PolyLog.Order($"{stgName} skip: long but body={q.Close - q.Open:F4} < 0.2 bar={q.Date:u}");
                    return false;
                }
            }
            else
            {
                if (q.Close - q.Open > -0.2m)
                {
                    PolyLog.Order($"{stgName} skip: short but body={q.Close - q.Open:F4} > -0.2 bar={q.Date:u}");
                    return false;
                }
            }

            PolyLog.Order($"{stgName} place: bar={q.Date:u} side={sideTag} cond={_currentConditionId} token={Trim(tokenId)} price={price.Value} size={size} remain={remainSeconds:F0}s body={q.Close - q.Open:F4}");
            var result = _polyService.PlaceOrderAsync(tokenId, Polymarket.Net.Enums.OrderSide.Buy, price.Value, size)
                    .GetAwaiter().GetResult();
            PolyLog.Order($"{stgName} placed: success={result.Success} orderId={result.OrderId} msg={result.Message}");
            return result.Success;
        }

        protected static string GetSlugPrefix(string eventTag, Period period)
        {
            var asset = eventTag.ToLowerInvariant() switch
            {
                "ethereum" => "eth",
                "bitcoin" => "btc",
                "solana" => "sol",
                "bnb" => "bnb",
                "xrp" => "xrp",
                "dogecoin" or "doge" => "doge",
                "hyperliquid" or "hype" => "hype",
                _ => eventTag.ToLowerInvariant()
            };
            return $"{asset}-updown-{GetPeriodTag(period).ToLowerInvariant()}";
        }

        protected static int GetSlotSeconds(Period period)
        {
            var match = Regex.Match(GetPeriodTag(period), @"(\d+)([MHD])", RegexOptions.IgnoreCase);
            if (!match.Success) return 300;
            int num = int.Parse(match.Groups[1].Value);
            return match.Groups[2].Value.ToUpperInvariant() switch
            {
                "M" => num * 60,
                "H" => num * 3600,
                "D" => num * 86400,
                _ => 300
            };
        }

        protected static string GetPeriodTag(Period period)
        {
            var text = period.ToString().ToUpperInvariant();
            var match = Regex.Match(text, @"(\d+)([MHDW])");
            if (match.Success) return $"{match.Groups[1].Value}{match.Groups[2].Value}";
            if (text.Contains("MIN")) return "1M";
            if (text.Contains("HOUR")) return "1H";
            if (text.Contains("DAY")) return "1D";
            if (text.Contains("WEEK")) return "1W";
            return "5M";
        }

        protected static DateTime ToUtc(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            var asUtc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            var asLocal = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
            var now = DateTime.UtcNow;
            return (asUtc - now).Duration() <= (asLocal - now).Duration() ? asUtc : asLocal;
        }
    }
}
