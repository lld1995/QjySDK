using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    public class PolyRsiAdx : StgBase
    {
        private class State
        {
            public int Status { get; set; }
            public decimal Num { get; set; }
        }

        private readonly Dictionary<string, State> _stateDic = new();
        private PolymarketService? _polyService;
        private bool _serviceInited;
        private bool _polyOrderPlaced;
        private DateTime? _eventEndDate;
        private string? _yesTokenId;
        private string? _noTokenId;
        private string? _currentConditionId;

        public PolyRsiAdx() { }
        public PolyRsiAdx(string id) : base(id) { }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 2;
            sd.UseGlobalCalc = 0;

            sd.ArgDic["privateKey"] = "0x424183d4b934d900e63e972c7fcc205e54a07e5713d88fb7242dcbf6ac1f051f";
            sd.ArgDic["funderAddress"] = "0xCdd07f0ee6E6E705f3c157Cdf3967E29117c4b76";
            sd.ArgDic["eventTag"] = "Ethereum";

            sd.ArgDic["rsiPeriod"] = 14;
            sd.ArgDic["overbought"] = 70;
            sd.ArgDic["oversold"] = 30;
            sd.ArgDic["adxPeriod"] = 14;
            sd.ArgDic["adxThreshold"] = 25.0;
            sd.ArgDic["signalCombine"] = 0;

            sd.ArgDic["minPriceNearEnd"] = 65;
            sd.ArgDic["nearEndMinutes"] = 3;

            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;
            sd.ArgDic["polyNum"] = 5m;

            sd.ArgDescDic["privateKey"] = new ArgDesc { Text = "钱包私钥", Explain = "Polymarket 钱包私钥" };
            sd.ArgDescDic["funderAddress"] = new ArgDesc { Text = "Proxy钱包地址", Explain = "Polymarket网站 Profile 里的 Wallet Address，留空则用EOA模式" };
            sd.ArgDescDic["eventTag"] = new ArgDesc { Text = "事件标签", Explain = "默认 Ethereum；周期标签按当前K线周期自动推导（如 5M/15M/1H）" };

            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI 周期", Explain = "RSI 指标周期" };
            sd.ArgDescDic["overbought"] = new ArgDesc { Text = "RSI 超买", Explain = "RSI > 该值触发做空信号" };
            sd.ArgDescDic["oversold"] = new ArgDesc { Text = "RSI 超卖", Explain = "RSI < 该值触发做多信号" };
            sd.ArgDescDic["adxPeriod"] = new ArgDesc { Text = "ADX 周期", Explain = "ADX 指标周期" };
            sd.ArgDescDic["adxThreshold"] = new ArgDesc { Text = "ADX 阈值", Explain = "ADX > 阈值时启用方向过滤" };
            sd.ArgDescDic["signalCombine"] = new ArgDesc { Text = "信号合并", Explain = "0=任一信号 1=双重确认" };

            sd.ArgDescDic["minPriceNearEnd"] = new ArgDesc { Text = "Poly最低价(分)", Explain = "Polymarket下单条件：best ask > 该值(分)且剩余时间 < nearEndMinutes" };
            sd.ArgDescDic["nearEndMinutes"] = new ArgDesc { Text = "Poly临近结束(分钟)", Explain = "剩余时间小于该值时允许Polymarket下单" };

            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易方向", Explain = "0=双向 1=仅多 2=仅空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "0=立即 1=下个开盘" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "0=固定手数 1=固定金额" };
            sd.ArgDescDic["lots"] = new ArgDesc { Text = "手数", Explain = "固定手数模式下下单数量" };
            sd.ArgDescDic["money"] = new ArgDesc { Text = "金额", Explain = "固定金额模式下用于换算手数" };
            sd.ArgDescDic["polyNum"] = new ArgDesc { Text = "Poly下单数量", Explain = "Polymarket 下单数量(USDC)" };

            sd.ColorDic["sub0-RSI"] = "#F6465D";
            sd.ColorDic["sub1-ADX"] = "#FF9800";
            sd.ColorDic["sub1-PDI"] = "#4CAF50";
            sd.ColorDic["sub1-MDI"] = "#F44336";

            sd.MidValDic["sub0"] = 50;
            sd.MidValDic["sub1"] = 25;

            return sd;
        }

        #region OnBar

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (ArgDic == null || tu.QuoteList == null || tu.QuoteList.Count == 0) return;

            EnsurePolymarketInitialized(period);

            if (_polyService != null)
                RefreshEventIfNeeded(tq.Date, period);

            // 每个 tick 尝试 Polymarket 下单（内部判断时间和价格条件）
            if (!IsBacktest && !_polyOrderPlaced
                && _stateDic.TryGetValue(tu.GetStateKey(), out var existing) && existing.Status != 0)
            {
                var polyNum = Convert.ToDecimal(ArgDic["polyNum"]);
                if (polyNum > 0 && TryPlacePolymarketOrder(existing.Status == 1, polyNum,tq))
                    _polyOrderPlaced = true;
            }

            if (!isFinal) return;
			var q = tu.QuoteList.Last();
			// ── 以下仅在 K 线收盘时执行 ──

			int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            int adxPeriod = Convert.ToInt32(ArgDic["adxPeriod"]);
            if (tu.QuoteList.Count < Math.Max(rsiPeriod, adxPeriod) + 5) return;

            var rsi = tu.QuoteList.GetRsi(rsiPeriod).Last();
            var adx = tu.QuoteList.GetAdx(adxPeriod).Last();
            if (!rsi.Rsi.HasValue || !adx.Adx.HasValue || !adx.Pdi.HasValue || !adx.Mdi.HasValue)
                return;

            double curRsi = rsi.Rsi.Value;
            double curAdx = adx.Adx.Value;
            double pdi = adx.Pdi.Value;
            double mdi = adx.Mdi.Value;

            Plot("sub0", "RSI", PlotType.LINE, curRsi);
            Plot("sub1", "ADX", PlotType.LINE, curAdx);
            Plot("sub1", "PDI", PlotType.LINE, pdi);
            Plot("sub1", "MDI", PlotType.LINE, mdi);

            // ── 信号计算 ──
            int signalCombine = Convert.ToInt32(ArgDic["signalCombine"]);
            int overbought = Convert.ToInt32(ArgDic["overbought"]);
            int oversold = Convert.ToInt32(ArgDic["oversold"]);

            bool rsiBuy = curRsi < oversold;
            bool rsiSell = curRsi > overbought;

            var k1 = tu.QuoteList[^1];
            var k2 = tu.QuoteList[^2];
            bool kBuy = k1.Close > k1.Open && k2.Close > k2.Open;
            bool kSell = k1.Close < k1.Open && k2.Close < k2.Open;

            bool buySignal, sellSignal;
            if (signalCombine == 1)
            { buySignal = rsiBuy && kBuy; sellSignal = rsiSell && kSell; }
            else
            { buySignal = rsiBuy || kBuy; sellSignal = rsiSell || kSell; }

            if (buySignal && sellSignal)
            { buySignal = false; sellSignal = false; }

            // ADX 方向过滤
            double adxThreshold = Convert.ToDouble(ArgDic["adxThreshold"]);
            if (curAdx > adxThreshold)
            {
                if (pdi > mdi) sellSignal = false;
                else if (mdi > pdi) buySignal = false;
            }

            // 交易方向限制
            int mode = Convert.ToInt32(ArgDic["mode"]);
            if (mode == 1) sellSignal = false;
            else if (mode == 2) buySignal = false;

            // ── 执行交易 ──
            var num = CalculateLots(tu.MktSymbol, q.Close);
            if (num <= 0) return;

            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            var sk = tu.GetStateKey();
            if (!_stateDic.TryGetValue(sk, out var s))
            {
                s = new State();
                _stateDic[sk] = s;
            }

            if (s.Status == 0)
            {
                if (buySignal)
                { s.Status = 1; s.Num = num; Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode); }
                else if (sellSignal)
                { s.Status = 2; s.Num = num; Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode); }
            }
            else if (s.Status == 1 && sellSignal)
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, sendMode);
                if (mode != 1)
                { s.Status = 2; s.Num = num; Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode); }
                else
                { s.Status = 0; s.Num = 0; }
            }
            else if (s.Status == 2 && buySignal)
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, sendMode);
                if (mode != 2)
                { s.Status = 1; s.Num = num; Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode); }
                else
                { s.Status = 0; s.Num = 0; }
            }
        }

        #endregion

        #region Polymarket

        private void EnsurePolymarketInitialized(Period period)
        {
            if (_serviceInited) return;

            if (IsBacktest) return;

            var privateKey = Convert.ToString(ArgDic["privateKey"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(privateKey)) return;

            var funderAddress = Convert.ToString(ArgDic["funderAddress"]) ?? string.Empty;
            _polyService = new PolymarketService();
            _polyService.Init(privateKey, funderAddress).GetAwaiter().GetResult();

            var eventTag = Convert.ToString(ArgDic["eventTag"]) ?? "Ethereum";
            var evt = _polyService.GetLatestEventAsync(GetSlugPrefix(eventTag, period), GetSlotSeconds(period))
                .GetAwaiter().GetResult();
            ApplyEventInfo(evt);

            _polyService.StartRedeemWorker();
			_serviceInited = true;
		}

		private void RefreshEventIfNeeded(DateTime barTime, Period period)
        {
            var barTimeUtc = ToUtc(barTime);
            if (_eventEndDate.HasValue && barTimeUtc < _eventEndDate.Value.AddSeconds(-1))
                return;

            // 把旧市场入赎回队列
            if (!string.IsNullOrWhiteSpace(_currentConditionId) && _eventEndDate.HasValue)
            {
                var oldTokenIds = new[] { _yesTokenId, _noTokenId }.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray()!;
                _polyService!.EnqueueMarketRedeem(_currentConditionId, oldTokenIds, _eventEndDate.Value);
            }

            var eventTag = Convert.ToString(ArgDic["eventTag"]) ?? "Ethereum";
            var evt = _polyService!.GetLatestEventAsync(GetSlugPrefix(eventTag, period), GetSlotSeconds(period))
                .GetAwaiter().GetResult();
            ApplyEventInfo(evt);
        }

        private void ApplyEventInfo(EventInfo? evt)
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

        private bool TryPlacePolymarketOrder(bool isLongSignal, decimal size,SkQuote q)
        {
            if (_polyService == null || !_eventEndDate.HasValue)
                return false;

            var remainSeconds = (_eventEndDate.Value - DateTime.UtcNow).TotalSeconds;
            int nearEndMinutes = Convert.ToInt32(ArgDic["nearEndMinutes"]);
            if (remainSeconds >= nearEndMinutes * 60)
                return false;

            var tokenId = isLongSignal ? _yesTokenId : _noTokenId;
            if (string.IsNullOrWhiteSpace(tokenId))
                return false;

            // 先查一次价格做基本校验
            var bookInfo = _polyService.GetOrderBookAsync(new[] { tokenId }).GetAwaiter().GetResult();
            if (!bookInfo.Success || !bookInfo.TokenBooks.TryGetValue(tokenId, out var book))
                return false;

            var price = book.BestAsk;
            if (!price.HasValue || price.Value <= 0 || price.Value > 1)
                return false;

            decimal minPriceNearEnd = Convert.ToDecimal(ArgDic["minPriceNearEnd"]);
            if (price.Value * 100m <= minPriceNearEnd)
                return false;

            // 距离结束时间越远，价格上限越严格
            if (remainSeconds > 2 * 60 && price.Value * 100m >= 75m)
                return false;
            if (remainSeconds > 1 * 60 && price.Value * 100m >= 85m)
                return false;
            if(price.Value * 100m >= 90m)
            {
                return false;
            }

            if (isLongSignal)
            {
                if(q.Close - q.Open < 0.2m)
                {
					return false;
				}
			}
            else 
            {
				if (q.Close - q.Open > -0.2m)
				{
					return false;
				}
			}
            // 下单+5秒未成交自动撤单重挂，最多3次
            var result = _polyService.PlaceOrderWithRetryAsync(tokenId, Polymarket.Net.Enums.OrderSide.Buy, size)
                    .GetAwaiter().GetResult();
            return result.Success;
        }

        #endregion

        #region Helpers

        private decimal CalculateLots(string mktSymbol, decimal price)
        {
            var num = Convert.ToDecimal(ArgDic["lots"]);
            if (Convert.ToInt32(ArgDic["lotsMode"]) == 1)
            {
                var sym = GetSymbol(mktSymbol);
                num = Convert.ToDecimal(ArgDic["money"]) / (price * sym.multiplier * sym.margin_ratio);
                num = sym.symbol_type == (int)SymbolType.COIN ? (int)(num * 1000) / 1000.0m : (int)num;
            }
            return num;
        }

        private static string GetSlugPrefix(string eventTag, Period period)
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

        private static int GetSlotSeconds(Period period)
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

        private static string GetPeriodTag(Period period)
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

        private static DateTime ToUtc(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Utc) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            var asUtc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            var asLocal = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
            var now = DateTime.UtcNow;
            return (asUtc - now).Duration() <= (asLocal - now).Duration() ? asUtc : asLocal;
        }

        #endregion
    }
}
