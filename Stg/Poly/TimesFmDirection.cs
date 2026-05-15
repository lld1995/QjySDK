using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static Model.EnumDef;

namespace QjySDK.Stg
{
    /// <summary>
    /// TimesFM 方向预测策略 (TimesFmDirection)
    ///
    /// 调用本地 TimesFM Serving API 预测下一根K线方向，依赖 ETH/BTC/SOL 5min 实证回测：
    ///   - 8 窗 / 4161 样本 / 10 个月跨度 / cat_time_only 协变量
    ///   - 过滤规则: |predRet| >= 0.30 * ATRP
    ///   - 实测胜率 57.03%, 95% CI [55.5%, 58.5%]
    ///   - 最长历史连亏 8 (出现 3 次/4161 注), 9+ 从未发生
    ///
    /// 信号识别：
    ///   1) 维护 contextLen 长度的滑动 closes 窗口
    ///   2) 每根完结bar调用 /forecast_with_covariates，传入 hour_of_day + day_of_week 时间分类变量
    ///   3) 预测下一根close, 计算 log(predClose/lastClose) 并与 atrThresholdK * ATRP 比较
    ///   4) 通过过滤的方向作为本bar信号
    ///
    /// 交易逻辑：
    ///   - 每bar独立评估：先平上一bar仓位，再按本bar信号开新仓 (1根K线持仓)
    ///   - Polymarket下单使用signalBars计数器连续下单 (复用ExtremeReversal模式)
    /// </summary>
    public class TimesFmDirection : PolyStgBase
    {
        private class State
        {
            public int Status { get; set; }       // 0=空仓 1=多头 2=空头
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public int SignalDir { get; set; }    // 当前信号方向: 0=无 1=看涨 2=看跌
            public int RemainBars { get; set; }   // 剩余可下单K线数
        }

        private readonly Dictionary<string, State> _stateDic = new();

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly JsonSerializerOptions _jso = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public TimesFmDirection() { }
        public TimesFmDirection(string id) : base(id) { }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.MaxSymbolNum = 1000;
            sd.SubChartNum = 1;
            sd.UseGlobalCalc = 0;

            // ==================== TimesFM 参数 ====================
            sd.ArgDescDic["timesFmUrl"] = new ArgDesc { Text = "TimesFM地址", Explain = "TimesFM Serving API base URL" };
            sd.ArgDic["timesFmUrl"] = "http://192.168.191.4:1234";

            sd.ArgDescDic["contextLen"] = new ArgDesc { Text = "上下文长度", Explain = "TimesFM 输入历史K线根数 (回测最优=384)" };
            sd.ArgDic["contextLen"] = 384;

            sd.ArgDescDic["xregMode"] = new ArgDesc { Text = "xreg模式", Explain = "TimesFM 协变量混合模式", Options = "xreg + timesfm|xreg|timesfm + xreg|timesfm|xreg only", Type = "select" };
            sd.ArgDic["xregMode"] = "xreg + timesfm";

            sd.ArgDescDic["causalBatchSize"] = new ArgDesc { Text = "因果批大小", Explain = "xreg按批计算，使用当前窗口+历史窗口组成批次；1为单窗口，32对齐回测批大小" };
            sd.ArgDic["causalBatchSize"] = 32;

            // ==================== 信号过滤参数 ====================
            sd.ArgDescDic["atrPeriod"] = new ArgDesc { Text = "ATR周期", Explain = "用于计算ATRP的周期" };
            sd.ArgDic["atrPeriod"] = 14;

            sd.ArgDescDic["atrThresholdK"] = new ArgDesc { Text = "ATRP阈值倍数", Explain = "|predRet| >= k * ATRP 时才触发信号 (回测最优 0.30)" };
            sd.ArgDic["atrThresholdK"] = 0.30;

            sd.ArgDescDic["signalBars"] = new ArgDesc { Text = "信号持续K线数", Explain = "信号出现后允许Polymarket连续下单的K线根数" };
            sd.ArgDic["signalBars"] = 5;

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
            AddPolyArgs(sd, 5m);

            // ==================== 颜色配置 ====================
            sd.ColorDic["sub0-PredRet"] = "#2196F3";
            sd.ColorDic["sub0-Threshold"] = "#FF5722";
            sd.ColorDic["sub0-Signal"] = "#4CAF50";

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

            int contextLen = Convert.ToInt32(ArgDic["contextLen"]);
            int atrPeriod = Convert.ToInt32(ArgDic["atrPeriod"]);
            int causalBatchSize = ArgDic.ContainsKey("causalBatchSize")
                ? Math.Max(1, Convert.ToInt32(ArgDic["causalBatchSize"]))
                : 32;
            // 至少需要 contextLen 根做输入 + atrPeriod 根做 ATR
            int minBars = Math.Max(contextLen + causalBatchSize - 1, atrPeriod) + 5;
            if (tu.QuoteList.Count < minBars) return;

            var quotes = tu.QuoteList;
            var q = quotes.Last();
            decimal price = q.Close;

            string timesFmUrl = Convert.ToString(ArgDic["timesFmUrl"]) ?? "http://192.168.191.4:1234";
            string xregMode = Convert.ToString(ArgDic["xregMode"]) ?? "xreg + timesfm";
            double atrThresholdK = Convert.ToDouble(ArgDic["atrThresholdK"]);
            int signalBars = Convert.ToInt32(ArgDic["signalBars"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            var sk = tu.GetStateKey();
            if (!_stateDic.TryGetValue(sk, out var s))
            {
                s = new State();
                _stateDic[sk] = s;
            }

            // ── 调用 TimesFM 预测下一根 close ──
            int barSignal = 0;
            double predRet = 0;
            double threshold = 0;
            try
            {
                int currentTargetIdx = quotes.Count;
                int firstTargetIdx = currentTargetIdx - causalBatchSize + 1;
                var batchInputs = new List<List<double>>(causalBatchSize);
                var batchHourCat = new List<List<object>>(causalBatchSize);
                var batchDowCat = new List<List<object>>(causalBatchSize);

                for (int targetIdx = firstTargetIdx; targetIdx <= currentTargetIdx; targetIdx++)
                {
                    int startIdx = targetIdx - contextLen;
                    var inputs = new List<double>(contextLen);
                    var hourCat = new List<object>(contextLen + 1);
                    var dowCat = new List<object>(contextLen + 1);

                    for (int i = startIdx; i < targetIdx; i++)
                    {
                        inputs.Add((double)quotes[i].Close);
                        var dt = quotes[i].Date;
                        hourCat.Add(dt.Hour);
                        dowCat.Add((int)dt.DayOfWeek);
                    }

                    var targetDt = targetIdx < quotes.Count
                        ? quotes[targetIdx].Date
                        : quotes[^1].Date.AddSeconds(GetSlotSeconds(period));
                    hourCat.Add(targetDt.Hour);
                    dowCat.Add((int)targetDt.DayOfWeek);

                    batchInputs.Add(inputs);
                    batchHourCat.Add(hourCat);
                    batchDowCat.Add(dowCat);
                }

                var req = new CovariatesForecastRequest
                {
                    Inputs = batchInputs,
                    DynamicCategoricalCovariates = new Dictionary<string, List<List<object>>>
                    {
                        ["hour_of_day"] = batchHourCat,
                        ["day_of_week"] = batchDowCat,
                    },
                    XRegMode = xregMode,
                    NormalizeXregTargetPerInput = true,
                    Ridge = 0.0,
                    MaxContext = contextLen,
                    MaxHorizon = 8,
                    NormalizeInputs = true,
                };
                var resp = PostForecast(timesFmUrl, req);

                if (resp != null && resp.PointForecast != null && resp.PointForecast.Count > 0
                    && resp.PointForecast[^1].Count > 0)
                {
                    double predClose = resp.PointForecast[^1][0];
                    double lastClose = (double)price;
                    if (predClose > 0 && lastClose > 0)
                    {
                        predRet = Math.Log(predClose / lastClose);

                        // 计算 ATRP (decimal): atr.Atrp 是百分比表示
                        var atrList = quotes.GetAtr(atrPeriod).ToList();
                        var atrLast = atrList[^1];
                        double atrp = atrLast.Atrp.HasValue ? atrLast.Atrp.Value / 100.0 : 0.0;
                        threshold = atrThresholdK * atrp;

                        if (atrp > 0 && Math.Abs(predRet) >= threshold)
                            barSignal = predRet > 0 ? 1 : 2;
                    }
                }
            }
            catch (Exception ex)
            {
                // TimesFM 不可用时静默跳过本bar，不阻断主交易所
                Console.WriteLine($"[TimesFmDirection] forecast failed: {ex.Message}");
                barSignal = 0;
            }

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
            Plot("sub0", "PredRet", PlotType.LINE, predRet);
            Plot("sub0", "Threshold", PlotType.LINE, threshold);
            Plot("sub0", "Signal", PlotType.LINE, barSignal == 1 ? 1 : (barSignal == 2 ? -1 : 0));

            // ── 每bar独立评估交易（MoEPredict风格：每bar开平一次） ──
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

        #region TimesFM HTTP

        private static ForecastResponse? PostForecast(string baseUrl, CovariatesForecastRequest req)
        {
            var url = baseUrl.TrimEnd('/') + "/forecast_with_covariates";
            var resp = _http.PostAsJsonAsync(url, req, _jso).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            return resp.Content.ReadFromJsonAsync<ForecastResponse>(_jso).GetAwaiter().GetResult();
        }

        private class CovariatesForecastRequest
        {
            [JsonPropertyName("inputs")]
            public List<List<double>> Inputs { get; set; } = new();

            [JsonPropertyName("dynamic_categorical_covariates")]
            public Dictionary<string, List<List<object>>>? DynamicCategoricalCovariates { get; set; }

            [JsonPropertyName("xreg_mode")]
            public string XRegMode { get; set; } = "xreg + timesfm";

            [JsonPropertyName("normalize_xreg_target_per_input")]
            public bool NormalizeXregTargetPerInput { get; set; } = true;

            [JsonPropertyName("ridge")]
            public double Ridge { get; set; } = 0.0;

            [JsonPropertyName("max_context")]
            public int MaxContext { get; set; } = 384;

            [JsonPropertyName("max_horizon")]
            public int MaxHorizon { get; set; } = 1;

            [JsonPropertyName("normalize_inputs")]
            public bool NormalizeInputs { get; set; } = true;
        }

        private class ForecastResponse
        {
            [JsonPropertyName("point_forecast")]
            public List<List<double>>? PointForecast { get; set; }
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

        #endregion
    }
}
