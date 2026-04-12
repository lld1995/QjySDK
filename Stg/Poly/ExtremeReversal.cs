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
    /// <summary>
    /// 极端反转策略 (ExtremeReversal)
    /// 
    /// 信号识别：5维评分系统，仅保留诊断验证的正向预测条件
    ///   C1. 连续3根K线反转 (54.9% 单条件胜率)
    ///   C2. RSI 超卖/超买 (54.7%)
    ///   C3. Stochastic 极端 (54.9%)
    ///   C4. 放量反转 (52.6%)
    ///   C5. BB突破 (54.2%)
    /// 
    /// 交易逻辑：
    ///   - 每bar独立评估5维条件，达到minConfirm(默认5)时触发信号
    ///   - 同时在主交易所做对应方向的开平仓
    ///   - Polymarket下单使用signalBars计数器连续下单
    /// </summary>
    public class ExtremeReversal : StgBase
    {
        private class State
        {
            public int Status { get; set; }       // 0=空仓 1=多头 2=空头
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public int SignalDir { get; set; }     // 当前信号方向: 0=无 1=看涨 2=看跌
            public int RemainBars { get; set; }    // 剩余可下单K线数
        }

        private readonly Dictionary<string, State> _stateDic = new();
        private PolymarketService? _polyService;
        private bool _serviceInited;
        private bool _polyOrderPlaced;
        private DateTime? _eventEndDate;
        private string? _yesTokenId;
        private string? _noTokenId;
        private string? _currentConditionId;

        public ExtremeReversal() { }
        public ExtremeReversal(string id) : base(id) { }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 1;
            sd.UseGlobalCalc = 0;

            // ==================== 信号参数 ====================
            sd.ArgDescDic["shadowRatio"] = new ArgDesc { Text = "影线比例", Explain = "影线长度 > 实体 × 该值时触发影线信号(默认2.0)" };
            sd.ArgDic["shadowRatio"] = 2.0;

            sd.ArgDescDic["signalBars"] = new ArgDesc { Text = "信号持续K线数", Explain = "信号出现后连续下单的K线根数" };
            sd.ArgDic["signalBars"] = 5;

            // ==================== 确认过滤参数 ====================
            sd.ArgDescDic["minConfirm"] = new ArgDesc { Text = "最少条件数", Explain = "5维评分：连续K线/RSI/StochK/量比/BB，需满足的最少条件数" };
            sd.ArgDic["minConfirm"] = 5;

            sd.ArgDescDic["rsiPeriod"] = new ArgDesc { Text = "RSI周期", Explain = "RSI计算周期" };
            sd.ArgDic["rsiPeriod"] = 14;

            sd.ArgDescDic["rsiOversold"] = new ArgDesc { Text = "RSI超卖线", Explain = "RSI低于该值确认买入" };
            sd.ArgDic["rsiOversold"] = 40;

            sd.ArgDescDic["rsiOverbought"] = new ArgDesc { Text = "RSI超买线", Explain = "RSI高于该值确认卖出" };
            sd.ArgDic["rsiOverbought"] = 60;

            sd.ArgDescDic["volRatio"] = new ArgDesc { Text = "量比阈值", Explain = "成交量 > 20均量 × 该值时确认" };
            sd.ArgDic["volRatio"] = 1.5;

            // ==================== 交易参数 ====================
            sd.ArgDescDic["mode"] = new ArgDesc { Text = "交易方向", Explain = "交易方向控制", Options = "0:双向|1:仅多|2:仅空", Type = "select" };
            sd.ArgDic["mode"] = 0;

            sd.ArgDescDic["sendMode"] = new ArgDesc { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDic["sendMode"] = 0;

            sd.ArgDescDic["stopLoss"] = new ArgDesc { Text = "止损%", Explain = "固定止损百分比，0为不启用" };
            sd.ArgDic["stopLoss"] = 5.0m;

            sd.ArgDescDic["lotsMode"] = new ArgDesc { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDic["lotsMode"] = 1;

            sd.ArgDescDic["lots"] = new ArgDesc { Text = "手数", Explain = "固定手数模式下下单数量" };
            sd.ArgDic["lots"] = 1.0m;

            sd.ArgDescDic["money"] = new ArgDesc { Text = "金额", Explain = "固定金额模式下用于换算手数" };
            sd.ArgDic["money"] = 10000m;

            // ==================== Polymarket参数 ====================
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

            sd.ArgDescDic["polyNum"] = new ArgDesc { Text = "Poly下单数量", Explain = "Polymarket 下单数量(USDC)" };
            sd.ArgDic["polyNum"] = 5m;

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-Signal"] = "#FF9800";

            sd.MidValDic["sub0"] = 0;

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

            // 每个 tick 尝试 Polymarket 下单（信号持续期间）
            if (!IsBacktest && !_polyOrderPlaced
                && _stateDic.TryGetValue(tu.GetStateKey(), out var existing)
                && existing.SignalDir != 0 && existing.RemainBars > 0)
            {
                var polyNum = Convert.ToDecimal(ArgDic["polyNum"]);
                if (polyNum > 0 && TryPlacePolymarketOrder(existing.SignalDir == 1, polyNum, tq))
                    _polyOrderPlaced = true;
            }

            if (!isFinal) return;

            if (tu.QuoteList.Count < 30) return;
            var quotes = tu.QuoteList;
            var q = quotes.Last();
            decimal price = q.Close;

            double shadowRatio = Convert.ToDouble(ArgDic["shadowRatio"]);
            int signalBars = Convert.ToInt32(ArgDic["signalBars"]);
            int minConfirm = Convert.ToInt32(ArgDic["minConfirm"]);
            int rsiPeriod = Convert.ToInt32(ArgDic["rsiPeriod"]);
            int rsiOversold = Convert.ToInt32(ArgDic["rsiOversold"]);
            int rsiOverbought = Convert.ToInt32(ArgDic["rsiOverbought"]);
            double volRatioThreshold = Convert.ToDouble(ArgDic["volRatio"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            var sk = tu.GetStateKey();
            if (!_stateDic.TryGetValue(sk, out var s))
            {
                s = new State();
                _stateDic[sk] = s;
            }

            // ── 5维评分系统（仅保留诊断验证的正向预测条件） ──
            int buyScore = 0, sellScore = 0;
            bool curBarUp = q.Close > q.Open;

            // C1: 连续3根K线反转 (54.9% 单条件胜率)
            if (quotes.Count >= 4)
            {
                int consecUp = 0, consecDown = 0;
                for (int ci = quotes.Count - 1; ci >= 1 && ci >= quotes.Count - 10; ci--)
                {
                    if (quotes[ci].Close > quotes[ci - 1].Close)
                    { if (consecDown > 0) break; consecUp++; }
                    else if (quotes[ci].Close < quotes[ci - 1].Close)
                    { if (consecUp > 0) break; consecDown++; }
                    else break;
                }
                if (consecDown >= 3) buyScore++;   // 连续下跌 → 看涨反转
                if (consecUp >= 3) sellScore++;     // 连续上涨 → 看跌反转
            }

            // C2: RSI 超卖/超买 (54.7% 单条件胜率)
            var rsiVal = quotes.GetRsi(rsiPeriod).Last().Rsi;
            if (rsiVal.HasValue)
            {
                if (rsiVal.Value < rsiOversold) buyScore++;
                if (rsiVal.Value > rsiOverbought) sellScore++;
            }

            // C3: Stochastic 极端 K < 20 / K > 80 (54.9% 单条件胜率)
            var stoch = quotes.GetStoch(14, 3, 3).Last();
            if (stoch.K.HasValue)
            {
                if (stoch.K.Value < 20) buyScore++;
                if (stoch.K.Value > 80) sellScore++;
            }

            // C4: 放量反转 (52.6% 单条件胜率)
            if (quotes.Count >= 20)
            {
                decimal avgVol = 0;
                for (int vi = quotes.Count - 20; vi < quotes.Count; vi++) avgVol += quotes[vi].Volume;
                avgVol /= 20;
                if (avgVol > 0 && q.Volume > avgVol * (decimal)volRatioThreshold)
                {
                    if (!curBarUp) buyScore++;   // 放量下跌 → 看涨反转
                    if (curBarUp) sellScore++;    // 放量上涨 → 看跌反转
                }
            }

            // C5: BB(10,2) 突破 (54.2% 单条件胜率)
            var boll = quotes.GetBollingerBands(10, 2).Last();
            if (boll.LowerBand.HasValue && boll.UpperBand.HasValue)
            {
                if ((double)price < boll.LowerBand.Value) buyScore++;
                if ((double)price > boll.UpperBand.Value) sellScore++;
            }

            // 信号判定：需达到 minConfirm 且买卖不冲突
            int barSignal = 0; // 本bar的交易方向
            if (buyScore >= minConfirm && sellScore < minConfirm) barSignal = 1;
            else if (sellScore >= minConfirm && buyScore < minConfirm) barSignal = 2;

            // mode 过滤
            if (mode == 1 && barSignal == 2) barSignal = 0;
            if (mode == 2 && barSignal == 1) barSignal = 0;

            // Poly 信号计数器：新信号出现时重置
            if (barSignal != 0)
            {
                s.SignalDir = barSignal;
                s.RemainBars = signalBars;
                _polyOrderPlaced = false;
            }
            else if (s.RemainBars > 0)
            {
                s.RemainBars--;
                if (s.RemainBars == 0) s.SignalDir = 0;
            }

            // 绘图
            double plotVal = barSignal != 0 ? (barSignal == 1 ? 1 : -1) : 0;
            Plot("sub0", "Signal", PlotType.LINE, plotVal);

            // ── 每bar独立评估交易（MoEPredict风格） ──
            var num = CalculateLots(tu.MktSymbol, price);
            if (num <= 0) return;

            // 止损检查
            var _sl = Convert.ToDecimal(ArgDic["stopLoss"]);
            if (s.Status == 1 && _sl > 0 && s.EntryPrice > 0 && price < s.EntryPrice * (1 - _sl / 100m))
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                return;
            }
            if (s.Status == 2 && _sl > 0 && s.EntryPrice > 0 && price > s.EntryPrice * (1 + _sl / 100m))
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
                return;
            }

            // Step 1: 平掉上一bar的仓位
            if (s.Status == 1)
            {
                Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
            }
            else if (s.Status == 2)
            {
                Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, price, s.Num, period, sendMode);
                s.Status = 0; s.Num = 0; s.EntryPrice = 0;
            }

            // Step 2: 仅当本bar条件满足时开仓（不依赖历史信号）
            if (barSignal == 1)
            {
                Trade(tu.MktSymbol, OrderType.BUY, price, num, period, sendMode);
                s.Status = 1; s.Num = num; s.EntryPrice = price;
            }
            else if (barSignal == 2)
            {
                Trade(tu.MktSymbol, OrderType.SELL, price, num, period, sendMode);
                s.Status = 2; s.Num = num; s.EntryPrice = price;
            }
        }

        #endregion

        #region Polymarket

        private void EnsurePolymarketInitialized(Period period)
        {
            if (_serviceInited) return;
            if (IsBacktest) return;

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
        }

        private void RefreshEventIfNeeded(DateTime barTime, Period period)
        {
            var barTimeUtc = ToUtc(barTime);
            if (_eventEndDate.HasValue && barTimeUtc < _eventEndDate.Value.AddSeconds(-1))
                return;

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

        private bool TryPlacePolymarketOrder(bool isLongSignal, decimal size, SkQuote q)
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

            var bookInfo = _polyService.GetOrderBookAsync(new[] { tokenId }).GetAwaiter().GetResult();
            if (!bookInfo.Success || !bookInfo.TokenBooks.TryGetValue(tokenId, out var book))
                return false;

            var price = book.BestAsk;
            if (!price.HasValue || price.Value <= 0 || price.Value > 1)
                return false;

            decimal minPriceNearEnd = Convert.ToDecimal(ArgDic["minPriceNearEnd"]);
            if (price.Value * 100m <= minPriceNearEnd)
                return false;

            if (remainSeconds > 2 * 60 && price.Value * 100m >= 75m)
                return false;
            if (remainSeconds > 1 * 60 && price.Value * 100m >= 85m)
                return false;
            if (price.Value * 100m >= 90m)
                return false;

            if (isLongSignal)
            {
                if (q.Close - q.Open < 0.2m)
                    return false;
            }
            else
            {
                if (q.Close - q.Open > -0.2m)
                    return false;
            }

            var result = _polyService.PlaceOrderAsync(tokenId, Polymarket.Net.Enums.OrderSide.Buy, price.Value, size)
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
