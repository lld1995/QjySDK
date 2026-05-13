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
using Xunit;
using Xunit.Abstractions;
using static Model.EnumDef;

namespace QjySDK.Tests
{
    public class TimesFmCausalBatchMatrixTests
    {
        private readonly ITestOutputHelper _output;

        public TimesFmCausalBatchMatrixTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private const string TimesFmUrl = "http://192.168.191.4:1234";
        private const string RawSymbol = "COIN_FUTURES_ETHUSDT";
        private const int ContextLen = 384;
        private const int AtrPeriod = 14;
        private const double AtrThresholdK = 0.30;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        private static readonly JsonSerializerOptions _jso = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        [Fact]
        public void Run_CausalBatchMatrix()
        {
            var cfg = MatrixConfig.FromEnvironment();
            var outDir = Path.GetFullPath(Path.Combine(KlineCache.CacheDirectory, "..", "TimesFmValidation"));
            Directory.CreateDirectory(outDir);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string summaryPath = Path.Combine(outDir, $"timesfm_causal_matrix_{cfg.Scope}_{stamp}_summary.csv");
            string detailPath = Path.Combine(outDir, $"timesfm_causal_matrix_{cfg.Scope}_{stamp}_detail.csv");
            string logPath = Path.Combine(outDir, $"timesfm_causal_matrix_{cfg.Scope}_{stamp}.log");

            void Log(string msg)
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                _output.WriteLine(line);
                File.AppendAllText(logPath, line + Environment.NewLine);
            }

            Assert.True(IsTimesFmAvailable(), $"TimesFM 不可达: {TimesFmUrl}/health");

            var quotes = KlineCache.LoadFromCacheOnly(RawSymbol, Period.TIME_5M, cfg.BarLimit)
                         ?? (TDEngineDataLoader.IsAvailable()
                             ? KlineCache.LoadKlines(RawSymbol, Period.TIME_5M, cfg.BarLimit)
                             : null);
            Assert.NotNull(quotes);
            Assert.True(quotes!.Count > ContextLen + cfg.MaxBatchSize + cfg.WindowSize + 10);

            var closes = quotes.Select(q => (double)q.Close).ToArray();
            var hourArr = quotes.Select(q => (object)q.Date.Hour).ToArray();
            var dowArr = quotes.Select(q => (object)(int)q.Date.DayOfWeek).ToArray();
            var atrpRaw = quotes.GetAtr(AtrPeriod).Select(r => r.Atrp).ToList();
            var windows = BuildWindows(quotes.Count, cfg.WindowCount, cfg.WindowSize, cfg.MaxBatchSize);

            Log($"scope={cfg.Scope}, bars={quotes.Count}, time={quotes[0].Date:yyyy-MM-dd HH:mm} -> {quotes[^1].Date:yyyy-MM-dd HH:mm}");
            Log($"windows={windows.Count}, windowSize={cfg.WindowSize}, causalBatchSizes={string.Join('|', cfg.CausalBatchSizes)}, leakyForwardBatchSizes={string.Join('|', cfg.LeakyForwardBatchSizes)}");
            Log($"summary={summaryPath}");
            Log($"detail={detailPath}");

            using var summary = new StreamWriter(summaryPath, false);
            using var detail = new StreamWriter(detailPath, false);
            summary.WriteLine("scope,window,start_idx,end_idx,start_time,end_time,mode,total,valid,pass,hit,coverage,win_rate,max_loss_streak,max_win_streak,avg_abs_pred_ret,avg_abs_actual_ret,avg_abs_diff_vs_b1,max_abs_diff_vs_b1,sign_diff_vs_b1,seconds");
            detail.WriteLine("scope,window,mode,idx,time,prev,actual,pred,pred_ret,actual_ret,atrp,threshold,kept,hit");

            var swAll = System.Diagnostics.Stopwatch.StartNew();
            for (int w = 0; w < windows.Count; w++)
            {
                var win = windows[w];
                var targets = Enumerable.Range(win.StartIdx, win.EndIdx - win.StartIdx + 1).ToList();
                string windowName = $"W{w + 1:00}";
                Log($"{windowName} start idx=[{win.StartIdx},{win.EndIdx}] time={quotes[win.StartIdx].Date:yyyy-MM-dd HH:mm}->{quotes[win.EndIdx].Date:yyyy-MM-dd HH:mm}");

                var predByMode = new Dictionary<string, Dictionary<int, double>>();
                foreach (int batchSize in cfg.CausalBatchSizes)
                {
                    string mode = $"causal_b{batchSize}";
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    predByMode[mode] = RunCausalMode(mode, batchSize, targets, closes, hourArr, dowArr, Log);
                    sw.Stop();
                    WriteModeResult(cfg.Scope, windowName, win, mode, targets, predByMode[mode], predByMode, closes, atrpRaw, quotes, summary, detail, sw.Elapsed.TotalSeconds);
                }

                foreach (int batchSize in cfg.LeakyForwardBatchSizes)
                {
                    string mode = $"leaky_forward_b{batchSize}";
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    predByMode[mode] = RunForwardMode(mode, batchSize, targets, closes, hourArr, dowArr, Log);
                    sw.Stop();
                    WriteModeResult(cfg.Scope, windowName, win, mode, targets, predByMode[mode], predByMode, closes, atrpRaw, quotes, summary, detail, sw.Elapsed.TotalSeconds);
                }

                summary.Flush();
                detail.Flush();
                Log($"{windowName} done elapsed={swAll.Elapsed.TotalMinutes:F1}m");
            }

            swAll.Stop();
            Log($"all done elapsed={swAll.Elapsed.TotalMinutes:F1}m");
        }

        private static List<WindowSpec> BuildWindows(int count, int windowCount, int windowSize, int maxBatchSize)
        {
            int minTarget = ContextLen + maxBatchSize;
            int maxTarget = count - 1;
            int maxStart = maxTarget - windowSize + 1;
            var windows = new List<WindowSpec>();
            if (windowCount <= 1 || maxStart <= minTarget)
            {
                int start = Math.Max(minTarget, maxStart);
                windows.Add(new WindowSpec { StartIdx = start, EndIdx = start + windowSize - 1 });
                return windows;
            }

            for (int i = 0; i < windowCount; i++)
            {
                double ratio = (double)i / (windowCount - 1);
                int start = minTarget + (int)Math.Round((maxStart - minTarget) * ratio);
                windows.Add(new WindowSpec { StartIdx = start, EndIdx = start + windowSize - 1 });
            }
            return windows;
        }

        private static Dictionary<int, double> RunCausalMode(string mode, int batchSize, List<int> targets, double[] closes, object[] hourArr, object[] dowArr, Action<string> log)
        {
            var preds = new Dictionary<int, double>();
            for (int t = 0; t < targets.Count; t++)
            {
                int targetIdx = targets[t];
                var inputs = new List<List<double>>(batchSize);
                var hours = new List<List<object>>(batchSize);
                var dows = new List<List<object>>(batchSize);
                for (int p = targetIdx - batchSize + 1; p <= targetIdx; p++)
                {
                    var w = BuildWindow(p, closes, hourArr, dowArr);
                    inputs.Add(w.Input);
                    hours.Add(w.Hour);
                    dows.Add(w.Dow);
                }

                var req = BuildReq(inputs, hours, dows);
                var pf = BatchForecast(req);
                preds[targetIdx] = ReadPred(pf, batchSize - 1);
                if ((t + 1) % 25 == 0 || t + 1 == targets.Count)
                    log($"  {mode}: {t + 1}/{targets.Count}");
            }
            return preds;
        }

        private static Dictionary<int, double> RunForwardMode(string mode, int batchSize, List<int> targets, double[] closes, object[] hourArr, object[] dowArr, Action<string> log)
        {
            var preds = new Dictionary<int, double>();
            int done = 0;
            for (int start = 0; start < targets.Count; start += batchSize)
            {
                int take = Math.Min(batchSize, targets.Count - start);
                var inputs = new List<List<double>>(take);
                var hours = new List<List<object>>(take);
                var dows = new List<List<object>>(take);
                for (int k = 0; k < take; k++)
                {
                    var w = BuildWindow(targets[start + k], closes, hourArr, dowArr);
                    inputs.Add(w.Input);
                    hours.Add(w.Hour);
                    dows.Add(w.Dow);
                }

                var req = BuildReq(inputs, hours, dows);
                var pf = BatchForecast(req);
                for (int k = 0; k < take; k++)
                    preds[targets[start + k]] = ReadPred(pf, k);
                done += take;
                log($"  {mode}: {done}/{targets.Count}");
            }
            return preds;
        }

        private static void WriteModeResult(string scope, string windowName, WindowSpec win, string mode, List<int> targets,
            Dictionary<int, double> preds, Dictionary<string, Dictionary<int, double>> predByMode, double[] closes,
            List<double?> atrpRaw, List<SkQuote> quotes, StreamWriter summary, StreamWriter detail, double seconds)
        {
            var score = Score(targets, preds, closes, atrpRaw);
            var diff = CompareToBaseline(targets, preds, predByMode.TryGetValue("causal_b1", out var b1) ? b1 : null, closes);
            summary.WriteLine(string.Join(',',
                Csv(scope),
                Csv(windowName),
                win.StartIdx.ToString(CultureInfo.InvariantCulture),
                win.EndIdx.ToString(CultureInfo.InvariantCulture),
                Csv(quotes[win.StartIdx].Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Csv(quotes[win.EndIdx].Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Csv(mode),
                score.Total.ToString(CultureInfo.InvariantCulture),
                score.Valid.ToString(CultureInfo.InvariantCulture),
                score.Pass.ToString(CultureInfo.InvariantCulture),
                score.Hit.ToString(CultureInfo.InvariantCulture),
                score.Coverage.ToString("F6", CultureInfo.InvariantCulture),
                score.WinRate.ToString("F6", CultureInfo.InvariantCulture),
                score.MaxLossStreak.ToString(CultureInfo.InvariantCulture),
                score.MaxWinStreak.ToString(CultureInfo.InvariantCulture),
                score.AvgAbsPredRet.ToString("F8", CultureInfo.InvariantCulture),
                score.AvgAbsActualRet.ToString("F8", CultureInfo.InvariantCulture),
                diff.AvgAbsDiff.ToString("F8", CultureInfo.InvariantCulture),
                diff.MaxAbsDiff.ToString("F8", CultureInfo.InvariantCulture),
                diff.SignDiff.ToString(CultureInfo.InvariantCulture),
                seconds.ToString("F2", CultureInfo.InvariantCulture)));

            foreach (var row in BuildDetailRows(scope, windowName, mode, targets, preds, closes, atrpRaw, quotes))
                detail.WriteLine(row);
        }

        private static IEnumerable<string> BuildDetailRows(string scope, string windowName, string mode, List<int> targets,
            Dictionary<int, double> preds, double[] closes, List<double?> atrpRaw, List<SkQuote> quotes)
        {
            foreach (int i in targets)
            {
                preds.TryGetValue(i, out var pred);
                double prev = closes[i - 1];
                double actual = closes[i];
                double predRet = pred > 0 && prev > 0 ? Math.Log(pred / prev) : double.NaN;
                double actualRet = actual > 0 && prev > 0 ? Math.Log(actual / prev) : double.NaN;
                double atrp = atrpRaw[i - 1].HasValue ? atrpRaw[i - 1]!.Value / 100.0 : 0.0;
                double threshold = AtrThresholdK * atrp;
                bool kept = !double.IsNaN(predRet) && atrp > 0 && Math.Abs(predRet) >= threshold;
                bool hit = kept && Math.Sign(predRet) == Math.Sign(actualRet) && actualRet != 0;
                yield return string.Join(',',
                    Csv(scope),
                    Csv(windowName),
                    Csv(mode),
                    i.ToString(CultureInfo.InvariantCulture),
                    Csv(quotes[i].Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    prev.ToString("F8", CultureInfo.InvariantCulture),
                    actual.ToString("F8", CultureInfo.InvariantCulture),
                    pred.ToString("F8", CultureInfo.InvariantCulture),
                    predRet.ToString("F10", CultureInfo.InvariantCulture),
                    actualRet.ToString("F10", CultureInfo.InvariantCulture),
                    atrp.ToString("F10", CultureInfo.InvariantCulture),
                    threshold.ToString("F10", CultureInfo.InvariantCulture),
                    kept ? "1" : "0",
                    hit ? "1" : "0");
            }
        }

        private static ScoreResult Score(List<int> targets, Dictionary<int, double> preds, double[] closes, List<double?> atrpRaw)
        {
            int valid = 0;
            int pass = 0;
            int hit = 0;
            int lossStreak = 0;
            int winStreak = 0;
            int maxLossStreak = 0;
            int maxWinStreak = 0;
            double sumAbsPredRet = 0;
            double sumAbsActualRet = 0;

            foreach (int i in targets)
            {
                if (!preds.TryGetValue(i, out var pred) || double.IsNaN(pred) || pred <= 0)
                    continue;
                double prev = closes[i - 1];
                double actual = closes[i];
                double predRet = Math.Log(pred / prev);
                double actualRet = Math.Log(actual / prev);
                double atrp = atrpRaw[i - 1].HasValue ? atrpRaw[i - 1]!.Value / 100.0 : 0.0;
                valid++;
                sumAbsPredRet += Math.Abs(predRet);
                sumAbsActualRet += Math.Abs(actualRet);
                if (atrp > 0 && Math.Abs(predRet) >= AtrThresholdK * atrp)
                {
                    pass++;
                    bool isHit = Math.Sign(predRet) == Math.Sign(actualRet) && actualRet != 0;
                    if (isHit)
                    {
                        hit++;
                        winStreak++;
                        lossStreak = 0;
                    }
                    else
                    {
                        lossStreak++;
                        winStreak = 0;
                    }
                    if (lossStreak > maxLossStreak)
                        maxLossStreak = lossStreak;
                    if (winStreak > maxWinStreak)
                        maxWinStreak = winStreak;
                }
            }

            return new ScoreResult
            {
                Total = targets.Count,
                Valid = valid,
                Pass = pass,
                Hit = hit,
                Coverage = targets.Count > 0 ? (double)pass / targets.Count : 0,
                WinRate = pass > 0 ? (double)hit / pass : 0,
                MaxLossStreak = maxLossStreak,
                MaxWinStreak = maxWinStreak,
                AvgAbsPredRet = valid > 0 ? sumAbsPredRet / valid : 0,
                AvgAbsActualRet = valid > 0 ? sumAbsActualRet / valid : 0,
            };
        }

        private static DiffResult CompareToBaseline(List<int> targets, Dictionary<int, double> preds, Dictionary<int, double>? baseline, double[] closes)
        {
            if (baseline == null)
                return new DiffResult();

            int valid = 0;
            int signDiff = 0;
            double sumAbsDiff = 0;
            double maxAbsDiff = 0;
            foreach (int i in targets)
            {
                if (!preds.TryGetValue(i, out var pred) || !baseline.TryGetValue(i, out var b))
                    continue;
                if (double.IsNaN(pred) || double.IsNaN(b))
                    continue;
                double diff = Math.Abs(pred - b);
                sumAbsDiff += diff;
                if (diff > maxAbsDiff)
                    maxAbsDiff = diff;
                double prev = closes[i - 1];
                if (Math.Sign(Math.Log(pred / prev)) != Math.Sign(Math.Log(b / prev)))
                    signDiff++;
                valid++;
            }

            return new DiffResult
            {
                AvgAbsDiff = valid > 0 ? sumAbsDiff / valid : 0,
                MaxAbsDiff = maxAbsDiff,
                SignDiff = signDiff,
            };
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

        private static CovariatesForecastRequest BuildReq(List<List<double>> inputs, List<List<object>> hourCat, List<List<object>> dowCat)
        {
            return new CovariatesForecastRequest
            {
                Inputs = inputs,
                DynamicCategoricalCovariates = new Dictionary<string, List<List<object>>>
                {
                    ["hour_of_day"] = hourCat,
                    ["day_of_week"] = dowCat,
                },
                XRegMode = "xreg + timesfm",
                NormalizeXregTargetPerInput = true,
                Ridge = 0.0,
                MaxContext = ContextLen,
                MaxHorizon = 8,
                NormalizeInputs = true,
            };
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

        private static double ReadPred(List<List<double>>? pf, int index)
        {
            if (pf == null || index < 0 || index >= pf.Count || pf[index].Count == 0)
                return double.NaN;
            return pf[index][0];
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

        private class MatrixConfig
        {
            public string Scope { get; set; } = "smoke";
            public int BarLimit { get; set; } = 3000;
            public int WindowCount { get; set; } = 3;
            public int WindowSize { get; set; } = 80;
            public List<int> CausalBatchSizes { get; set; } = new List<int> { 1, 8, 32 };
            public List<int> LeakyForwardBatchSizes { get; set; } = new List<int>();
            public int MaxBatchSize => Math.Max(CausalBatchSizes.Max(), LeakyForwardBatchSizes.Count > 0 ? LeakyForwardBatchSizes.Max() : 1);

            public static MatrixConfig FromEnvironment()
            {
                string scope = (Environment.GetEnvironmentVariable("TIMESFM_MATRIX_SCOPE") ?? "smoke").Trim().ToLowerInvariant();
                bool includeLeakyForward = (Environment.GetEnvironmentVariable("TIMESFM_INCLUDE_LEAKY_FORWARD") ?? "").Trim() == "1";
                if (scope == "full")
                {
                    return new MatrixConfig
                    {
                        Scope = "full",
                        BarLimit = 3000,
                        WindowCount = 8,
                        WindowSize = 200,
                        CausalBatchSizes = new List<int> { 1, 4, 8, 16, 32 },
                        LeakyForwardBatchSizes = includeLeakyForward ? new List<int> { 32 } : new List<int>(),
                    };
                }
                if (scope == "medium")
                {
                    return new MatrixConfig
                    {
                        Scope = "medium",
                        BarLimit = 3000,
                        WindowCount = 4,
                        WindowSize = 100,
                        CausalBatchSizes = new List<int> { 1, 8, 16, 32 },
                        LeakyForwardBatchSizes = includeLeakyForward ? new List<int> { 32 } : new List<int>(),
                    };
                }
                return new MatrixConfig
                {
                    LeakyForwardBatchSizes = includeLeakyForward ? new List<int> { 32 } : new List<int>(),
                };
            }
        }

        private class WindowSpec
        {
            public int StartIdx { get; set; }
            public int EndIdx { get; set; }
        }

        private class WindowData
        {
            public List<double> Input { get; set; } = new();
            public List<object> Hour { get; set; } = new();
            public List<object> Dow { get; set; } = new();
        }

        private class ScoreResult
        {
            public int Total { get; set; }
            public int Valid { get; set; }
            public int Pass { get; set; }
            public int Hit { get; set; }
            public double Coverage { get; set; }
            public double WinRate { get; set; }
            public int MaxLossStreak { get; set; }
            public int MaxWinStreak { get; set; }
            public double AvgAbsPredRet { get; set; }
            public double AvgAbsActualRet { get; set; }
        }

        private class DiffResult
        {
            public double AvgAbsDiff { get; set; }
            public double MaxAbsDiff { get; set; }
            public int SignDiff { get; set; }
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
