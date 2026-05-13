using Common;
using Model;
using Skender.Stock.Indicators;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class TimesFmMonthly2026Tests
    {
        private readonly ITestOutputHelper _output;
        private const string TimesFmUrl = "http://192.168.191.4:1234";
        private const string RawSymbol = "COIN_FUTURES_ETHUSDT";
        private const int ContextLen = 384;
        private const int AtrPeriod = 14;
        private const int CausalBatchSize = 32;
        private const int BarLimit = 60000;
        private const double AtrThresholdK = 0.30;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        private static readonly JsonSerializerOptions _jso = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public TimesFmMonthly2026Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Run_2026_Monthly_5M_CausalB32()
        {
            Assert.True(IsTimesFmAvailable(), $"TimesFM 不可达: {TimesFmUrl}/health");
            Assert.True(TDEngineDataLoader.IsAvailable(), "TDEngine 不可用，无法拉取 2026 全量 5M 数据");

            var quotes = TDEngineDataLoader.LoadKlines(RawSymbol, Period.TIME_5M, BarLimit);
            Assert.NotNull(quotes);
            Assert.True(quotes.Count > ContextLen + CausalBatchSize + 10);

            var outDir = Path.GetFullPath(Path.Combine(KlineCache.CacheDirectory, "..", "TimesFmValidation"));
            Directory.CreateDirectory(outDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var maxTargetsPerMonth = GetMaxTargetsPerMonth();
            var monthFilter = GetMonthFilter();
            var sampleTag = maxTargetsPerMonth > 0 ? $"sample{maxTargetsPerMonth}" : "full";
            var monthTag = monthFilter.HasValue ? monthFilter.Value.ToString("yyyyMM", CultureInfo.InvariantCulture) : "all";
            var summaryPath = Path.Combine(outDir, $"timesfm_monthly_2026_5m_causal_b32_{monthTag}_{sampleTag}_{stamp}_summary.csv");
            var detailPath = Path.Combine(outDir, $"timesfm_monthly_2026_5m_causal_b32_{monthTag}_{sampleTag}_{stamp}_detail.csv");
            var logPath = Path.Combine(outDir, $"timesfm_monthly_2026_5m_causal_b32_{monthTag}_{sampleTag}_{stamp}.log");

            void Log(string msg)
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                _output.WriteLine(line);
                File.AppendAllText(logPath, line + Environment.NewLine);
            }

            var closes = quotes.Select(q => (double)q.Close).ToArray();
            var hourArr = quotes.Select(q => (object)q.Date.Hour).ToArray();
            var dowArr = quotes.Select(q => (object)(int)q.Date.DayOfWeek).ToArray();
            var atrpRaw = quotes.GetAtr(AtrPeriod).Select(r => r.Atrp).ToList();
            var minTarget = ContextLen + CausalBatchSize - 1;

            using var summary = new StreamWriter(summaryPath, false);
            using var detail = new StreamWriter(detailPath, false);
            summary.WriteLine("strategy,mode,month,source_total,total,valid,pass,hit,coverage,win_rate,max_loss_streak,max_win_streak,start,end,seconds");
            detail.WriteLine("strategy,mode,month,idx,time,prev,actual,pred,pred_ret,actual_ret,atrp,threshold,kept,hit");
            summary.Flush();
            detail.Flush();

            Log($"bars={quotes.Count}, range={quotes[0].Date:yyyy-MM-dd HH:mm}->{quotes[^1].Date:yyyy-MM-dd HH:mm}");
            Log($"summary={summaryPath}");
            Log($"detail={detailPath}");
            Log($"mode=causal_b{CausalBatchSize}, contextLen={ContextLen}, atrThresholdK={AtrThresholdK:F2}");
            Log($"parallel={GetMaxDegreeOfParallelism()}");
            Log($"maxTargetsPerMonth={(maxTargetsPerMonth > 0 ? maxTargetsPerMonth.ToString(CultureInfo.InvariantCulture) : "full")}");
            Log($"month={(monthFilter.HasValue ? monthFilter.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture) : "all")}");

            var monthGroups = Enumerable.Range(minTarget, quotes.Count - minTarget)
                .Where(i => quotes[i].Date.Year == 2026)
                .GroupBy(i => new DateTime(quotes[i].Date.Year, quotes[i].Date.Month, 1))
                .Where(g => !monthFilter.HasValue || g.Key == monthFilter.Value)
                .OrderBy(g => g.Key)
                .ToList();
            Assert.True(monthGroups.Count > 0, "没有找到匹配月份的数据");

            foreach (var group in monthGroups)
            {
                var allTargets = group.ToList();
                var targets = SampleTargets(allTargets, maxTargetsPerMonth);
                var score = new ScoreState();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var rows = new ScoredRow[targets.Count];
                var done = 0;
                var options = new ParallelOptions { MaxDegreeOfParallelism = GetMaxDegreeOfParallelism() };
                Log($"month={group.Key:yyyy-MM}, sourceTargets={allTargets.Count}, evalTargets={targets.Count}, start={quotes[targets[0]].Date:yyyy-MM-dd HH:mm}, end={quotes[targets[^1]].Date:yyyy-MM-dd HH:mm}");

                Parallel.For(0, targets.Count, options, n =>
                {
                    int targetIdx = targets[n];
                    var pred = ForecastCausal(targetIdx, closes, hourArr, dowArr);
                    rows[n] = ScoreOne(group.Key, targetIdx, pred, closes, atrpRaw, quotes);

                    var current = Interlocked.Increment(ref done);
                    if (current % 50 == 0 || current == targets.Count)
                        Log($"  {group.Key:yyyy-MM}: {current}/{targets.Count}");
                });

                foreach (var row in rows)
                {
                    score.Add(row);
                    detail.WriteLine(row.Line);
                }
                detail.Flush();

                sw.Stop();
                summary.WriteLine(string.Join(',',
                    Csv("TimesFmDirection"),
                    Csv($"causal_b{CausalBatchSize}"),
                    group.Key.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    allTargets.Count.ToString(CultureInfo.InvariantCulture),
                    score.Total.ToString(CultureInfo.InvariantCulture),
                    score.Valid.ToString(CultureInfo.InvariantCulture),
                    score.Pass.ToString(CultureInfo.InvariantCulture),
                    score.Hit.ToString(CultureInfo.InvariantCulture),
                    score.Coverage.ToString("F6", CultureInfo.InvariantCulture),
                    score.WinRate.ToString("F6", CultureInfo.InvariantCulture),
                    score.MaxLossStreak.ToString(CultureInfo.InvariantCulture),
                    score.MaxWinStreak.ToString(CultureInfo.InvariantCulture),
                    Csv(quotes[targets[0]].Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    Csv(quotes[targets[^1]].Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)));
                summary.Flush();
                Log($"done {group.Key:yyyy-MM}: pass={score.Pass}/{score.Total}, hit={score.Hit}, win={score.WinRate:P2}, cov={score.Coverage:P2}, seconds={sw.Elapsed.TotalSeconds:F1}");
            }
        }

        private static List<int> SampleTargets(List<int> targets, int maxTargets)
        {
            if (maxTargets <= 0 || targets.Count <= maxTargets)
                return targets;

            var sampled = new List<int>(maxTargets);
            if (maxTargets == 1)
            {
                sampled.Add(targets[targets.Count / 2]);
                return sampled;
            }

            for (int i = 0; i < maxTargets; i++)
            {
                int idx = (int)Math.Round((double)i * (targets.Count - 1) / (maxTargets - 1));
                sampled.Add(targets[idx]);
            }
            return sampled;
        }

        private static double ForecastCausal(int targetIdx, double[] closes, object[] hourArr, object[] dowArr)
        {
            var inputs = new List<List<double>>(CausalBatchSize);
            var hours = new List<List<object>>(CausalBatchSize);
            var dows = new List<List<object>>(CausalBatchSize);

            for (int p = targetIdx - CausalBatchSize + 1; p <= targetIdx; p++)
            {
                var window = BuildWindow(p, closes, hourArr, dowArr);
                inputs.Add(window.Input);
                hours.Add(window.Hour);
                dows.Add(window.Dow);
            }

            var req = new CovariatesForecastRequest
            {
                Inputs = inputs,
                DynamicCategoricalCovariates = new Dictionary<string, List<List<object>>>
                {
                    ["hour_of_day"] = hours,
                    ["day_of_week"] = dows,
                },
                XRegMode = "xreg + timesfm",
                NormalizeXregTargetPerInput = true,
                Ridge = 0.0,
                MaxContext = ContextLen,
                MaxHorizon = 8,
                NormalizeInputs = true,
            };

            var pf = BatchForecast(req);
            if (pf == null || pf.Count < CausalBatchSize || pf[^1].Count == 0)
                return double.NaN;
            return pf[^1][0];
        }

        private static WindowData BuildWindow(int targetIdx, double[] closes, object[] hourArr, object[] dowArr)
        {
            var input = new List<double>(ContextLen);
            var hour = new List<object>(ContextLen + 1);
            var dow = new List<object>(ContextLen + 1);
            for (int k = targetIdx - ContextLen; k < targetIdx; k++)
            {
                input.Add(closes[k]);
                hour.Add(hourArr[k]);
                dow.Add(dowArr[k]);
            }
            hour.Add(hourArr[targetIdx]);
            dow.Add(dowArr[targetIdx]);
            return new WindowData { Input = input, Hour = hour, Dow = dow };
        }

        private static List<List<double>>? BatchForecast(CovariatesForecastRequest req)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    var url = TimesFmUrl.TrimEnd('/') + "/forecast_with_covariates";
                    var resp = _http.PostAsJsonAsync(url, req, _jso, cts.Token).GetAwaiter().GetResult();
                    resp.EnsureSuccessStatusCode();
                    var body = resp.Content.ReadFromJsonAsync<ForecastResponse>(_jso, cts.Token).GetAwaiter().GetResult();
                    return body?.PointForecast;
                }
                catch
                {
                    if (attempt == 2)
                        return null;
                    Thread.Sleep(500);
                }
            }
            return null;
        }

        private static ScoredRow ScoreOne(DateTime month, int targetIdx, double pred, double[] closes, List<double?> atrpRaw, List<SkQuote> quotes)
        {
            double prev = closes[targetIdx - 1];
            double actual = closes[targetIdx];
            double predRet = pred > 0 && prev > 0 ? Math.Log(pred / prev) : double.NaN;
            double actualRet = actual > 0 && prev > 0 ? Math.Log(actual / prev) : double.NaN;
            double atrp = atrpRaw[targetIdx - 1].HasValue ? atrpRaw[targetIdx - 1]!.Value / 100.0 : 0.0;
            double threshold = AtrThresholdK * atrp;
            bool valid = !double.IsNaN(predRet) && atrp > 0;
            bool kept = valid && Math.Abs(predRet) >= threshold;
            bool hit = kept && Math.Sign(predRet) == Math.Sign(actualRet) && actualRet != 0;

            var line = string.Join(',',
                Csv("TimesFmDirection"),
                Csv($"causal_b{CausalBatchSize}"),
                month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                targetIdx.ToString(CultureInfo.InvariantCulture),
                Csv(quotes[targetIdx].Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                prev.ToString("F8", CultureInfo.InvariantCulture),
                actual.ToString("F8", CultureInfo.InvariantCulture),
                pred.ToString("F8", CultureInfo.InvariantCulture),
                predRet.ToString("F10", CultureInfo.InvariantCulture),
                actualRet.ToString("F10", CultureInfo.InvariantCulture),
                atrp.ToString("F10", CultureInfo.InvariantCulture),
                threshold.ToString("F10", CultureInfo.InvariantCulture),
                kept ? "1" : "0",
                hit ? "1" : "0");
            return new ScoredRow { Line = line, Valid = valid, Kept = kept, Hit = hit };
        }

        private static int GetMaxDegreeOfParallelism()
        {
            var raw = Environment.GetEnvironmentVariable("TIMESFM_MONTHLY_PARALLEL");
            var value = 4;
            if (int.TryParse(raw, out var parsed))
                value = Math.Clamp(parsed, 1, 32);
            return value;
        }

        private static int GetMaxTargetsPerMonth()
        {
            var raw = Environment.GetEnvironmentVariable("TIMESFM_MONTHLY_MAX_TARGETS");
            var value = 0;
            if (int.TryParse(raw, out var parsed))
                value = Math.Clamp(parsed, 0, 20000);
            return value;
        }

        private static DateTime? GetMonthFilter()
        {
            var raw = Environment.GetEnvironmentVariable("TIMESFM_MONTH");
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            if (DateTime.TryParseExact(raw.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
                return new DateTime(month.Year, month.Month, 1);

            throw new InvalidOperationException("TIMESFM_MONTH 格式应为 yyyy-MM");
        }

        private static bool IsTimesFmAvailable()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var resp = http.GetAsync(TimesFmUrl.TrimEnd('/') + "/health").GetAwaiter().GetResult();
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static string Csv(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private class ScoreState
        {
            public int Total { get; set; }
            public int Valid { get; set; }
            public int Pass { get; set; }
            public int Hit { get; set; }
            public int LossStreak { get; set; }
            public int WinStreak { get; set; }
            public int MaxLossStreak { get; set; }
            public int MaxWinStreak { get; set; }
            public double Coverage => Total > 0 ? (double)Pass / Total : 0;
            public double WinRate => Pass > 0 ? (double)Hit / Pass : 0;

            public void Add(ScoredRow row)
            {
                Total++;
                if (row.Valid)
                    Valid++;
                if (row.Kept)
                {
                    Pass++;
                    if (row.Hit)
                    {
                        Hit++;
                        WinStreak++;
                        LossStreak = 0;
                    }
                    else
                    {
                        LossStreak++;
                        WinStreak = 0;
                    }
                    if (LossStreak > MaxLossStreak)
                        MaxLossStreak = LossStreak;
                    if (WinStreak > MaxWinStreak)
                        MaxWinStreak = WinStreak;
                }
            }
        }

        private class ScoredRow
        {
            public string Line { get; set; } = "";
            public bool Valid { get; set; }
            public bool Kept { get; set; }
            public bool Hit { get; set; }
        }

        private class WindowData
        {
            public List<double> Input { get; set; } = new();
            public List<object> Hour { get; set; } = new();
            public List<object> Dow { get; set; } = new();
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
            public int MaxHorizon { get; set; } = 8;

            [JsonPropertyName("normalize_inputs")]
            public bool NormalizeInputs { get; set; } = true;
        }

        private class ForecastResponse
        {
            [JsonPropertyName("point_forecast")]
            public List<List<double>>? PointForecast { get; set; }
        }
    }
}
