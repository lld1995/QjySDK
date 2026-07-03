using Common;
using Model;
using Skender.Stock.Indicators;
using stgInterface;
using System;
using System.Linq;
using static Model.EnumDef;
using System.Collections.Generic;

namespace QjySDK.Stg
{
    public class ElliottWave : StgBase
    {
        public ElliottWave() { }

        public ElliottWave(string id) : base(id) { }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            sd.ArgDic["zigzagDepth"] = 5;
            sd.ArgDic["zigzagDeviation"] = 3.0;
            sd.ArgDic["mode"] = 0;
            sd.ArgDic["sendMode"] = 0;
            sd.ArgDic["lossRate"] = 5m;
            sd.ArgDic["profitRate"] = 20m;
            sd.ArgDic["trailingStop"] = 1;
            sd.ArgDic["trailingPercent"] = 20m;
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;
            sd.ArgDescDic["zigzagDepth"] = new ArgDesc() { Text = "ZigZag深度", Explain = "波峰波谷识别的回溯周期", Type = "number" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "交易方向控制", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };

            sd.ArgDescDic["lossRate"] = new ArgDesc() { Text = "止损比例", Explain = "止损触发的价格百分比", Type = "number" };

            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };

            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };

            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

            sd.ArgDescDic["profitRate"] = new ArgDesc() { Text = "止盈比例", Explain = "止盈触发的价格百分比", Type = "number" };

            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };

            sd.ArgDescDic["trailingPercent"] = new ArgDesc() { Text = "移动止损幅度", Explain = "从最高/低点回撤的百分比", Type = "number" };

            sd.ArgDescDic["trailingStop"] = new ArgDesc() { Text = "移动止损", Explain = "跟踪最高/低点调整止损", Options = "0:关闭|1:启用", Type = "bool" };

            sd.ArgDescDic["zigzagDeviation"] = new ArgDesc() { Text = "ZigZag偏差", Explain = "ZigZag最小波动幅度百分比", Type = "number" };
            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 开仓点颜色配置
            sd.ColorDic["main-W3"] = "#FF9800";      // 浪3开仓点橙色
            sd.ColorDic["main-W5"] = "#E91E63";      // 浪5开仓点粉色
            sd.ColorDic["main-WC"] = "#00BCD4";      // 浪C开仓点青色

            // 副图颜色配置
            sd.ColorDic["sub1-Wave"] = "#2196F3";    // 当前浪蓝色
            sd.ColorDic["sub1-Valid"] = "#0ECB81";   // 有效性绿色
            sd.ColorDic["sub1-Trend"] = "#FF9800";   // 趋势橙色
            sd.ColorDic["sub1-Type"] = "#9C27B0";    // 类型紫色

            sd.ArgDescDic["stopCooldownBars"] = new ArgDesc() { Text = "止损冷却K线数", Explain = "止损后N根K线内禁止开仓,设0关闭", Type = "number" };
            sd.ArgDic["stopCooldownBars"] = 5;

            return sd;
        }

        private enum WaveType { None, Impulse, Corrective }

        private class WaveState
        {
            public WaveType Type { get; set; }
            public int CurrentWave { get; set; }  // 1-5 for impulse, 6=A, 7=B, 8=C for corrective
            public bool IsUpTrend { get; set; }
            public bool IsValid { get; set; }
            public decimal Wave0 { get; set; }
            public decimal Wave1 { get; set; }
            public decimal Wave2 { get; set; }
            public decimal Wave3 { get; set; }
            public decimal Wave4 { get; set; }
            public decimal Wave5 { get; set; }
            public decimal WaveA { get; set; }
            public decimal WaveB { get; set; }
            public decimal WaveC { get; set; }
            public int LastPivotIndex { get; set; }  // Track last processed pivot index to avoid reprocessing
            
            // Wave lengths for validation
            public decimal Wave1Length => Math.Abs(Wave1 - Wave0);
            public decimal Wave2Length => Math.Abs(Wave2 - Wave1);
            public decimal Wave3Length => Math.Abs(Wave3 - Wave2);
            public decimal Wave4Length => Math.Abs(Wave4 - Wave3);
            public decimal Wave5Length => Math.Abs(Wave5 - Wave4);
        }

        private class TradeState
        {
            public int Status { get; set; }
            public decimal Num { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal StopLoss { get; set; }
            public decimal TakeProfit { get; set; }
            public decimal HighestPrice { get; set; }
            public decimal LowestPrice { get; set; }
            public int BlockedDir { get; set; }       // 止损后被封锁的方向:0无 1多 2空
            public int CooldownRemaining { get; set; } // 止损后剩余冷却K线数
        }

        private class Pivot
        {
            public int Index { get; set; }
            public decimal Price { get; set; }
            public bool IsHigh { get; set; }
        }

        private Dictionary<string, TradeState> _tradeDic = new Dictionary<string, TradeState>();
        private Dictionary<string, WaveState> _waveDic = new Dictionary<string, WaveState>();
        private Dictionary<string, List<Pivot>> _pivotHistoryDic = new Dictionary<string, List<Pivot>>();
        private const int MaxPivotHistory = 20; // Keep last 20 pivots for backtracking

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            if (!isFinal) return;
            if (tu.QuoteList.Count < 50) return;

            var quotes = tu.QuoteList;
            var currentQuote = quotes.Last();
            var sk = tu.GetStateKey();

            if (!_tradeDic.ContainsKey(sk)) _tradeDic[sk] = new TradeState();
            if (!_waveDic.ContainsKey(sk)) _waveDic[sk] = new WaveState();

            var trade = _tradeDic[sk];
            var wave = _waveDic[sk];

            int zigzagDepth = Convert.ToInt32(ArgDic["zigzagDepth"]);
            double zigzagDeviation = Convert.ToDouble(ArgDic["zigzagDeviation"]);
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);
            decimal lossRate = Convert.ToDecimal(ArgDic["lossRate"]);
            decimal profitRate = Convert.ToDecimal(ArgDic["profitRate"]);
            int trailingStop = Convert.ToInt32(ArgDic["trailingStop"]);
            decimal trailingPercent = Convert.ToDecimal(ArgDic["trailingPercent"]);
            decimal num = CalculateLots(tu, currentQuote);

            var pivots = FindPivots(quotes, zigzagDepth, zigzagDeviation);
            
            // Maintain pivot history for backtracking
            if (!_pivotHistoryDic.ContainsKey(sk)) _pivotHistoryDic[sk] = new List<Pivot>();
            UpdatePivotHistory(_pivotHistoryDic[sk], pivots);
            
            bool isNewPivot = UpdateWaveStateMachine(wave, pivots, _pivotHistoryDic[sk]);
            PlotWaveInfo(wave, isNewPivot);
            ExecuteTrade(trade, wave, currentQuote, tu, period, mode, sendMode, num, lossRate, profitRate, trailingStop, trailingPercent);
        }

        private List<Pivot> FindPivots(List<SkQuote> quotes, int depth, double deviation)
        {
            var pivots = new List<Pivot>();
            if (quotes.Count < depth * 2) return pivots;
            decimal devPercent = (decimal)deviation / 100m;

            for (int i = depth; i < quotes.Count - 1; i++)
            {
                bool isHigh = true;
                bool isLow = true;
                for (int j = i - depth; j < i; j++)
                {
                    if (quotes[j].High >= quotes[i].High) isHigh = false;
                    if (quotes[j].Low <= quotes[i].Low) isLow = false;
                }
                if (i < quotes.Count - 1)
                {
                    if (quotes[i + 1].High > quotes[i].High) isHigh = false;
                    if (quotes[i + 1].Low < quotes[i].Low) isLow = false;
                }

                if (isHigh && (pivots.Count == 0 || !pivots.Last().IsHigh))
                {
                    if (pivots.Count == 0 || Math.Abs(quotes[i].High - pivots.Last().Price) / pivots.Last().Price >= devPercent)
                        pivots.Add(new Pivot { Index = i, Price = quotes[i].High, IsHigh = true });
                }
                else if (isHigh && pivots.Count > 0 && pivots.Last().IsHigh && quotes[i].High > pivots.Last().Price)
                {
                    pivots[pivots.Count - 1] = new Pivot { Index = i, Price = quotes[i].High, IsHigh = true };
                }
                else if (isLow && (pivots.Count == 0 || pivots.Last().IsHigh))
                {
                    if (pivots.Count == 0 || Math.Abs(quotes[i].Low - pivots.Last().Price) / pivots.Last().Price >= devPercent)
                        pivots.Add(new Pivot { Index = i, Price = quotes[i].Low, IsHigh = false });
                }
                else if (isLow && pivots.Count > 0 && !pivots.Last().IsHigh && quotes[i].Low < pivots.Last().Price)
                {
                    pivots[pivots.Count - 1] = new Pivot { Index = i, Price = quotes[i].Low, IsHigh = false };
                }
            }
            return pivots;
        }

        private void UpdatePivotHistory(List<Pivot> history, List<Pivot> currentPivots)
        {
            if (currentPivots.Count == 0) return;
            
            var latestPivot = currentPivots.Last();
            if (history.Count == 0 || history.Last().Index < latestPivot.Index)
            {
                history.Add(latestPivot);
                // Keep only the last MaxPivotHistory pivots
                while (history.Count > MaxPivotHistory)
                    history.RemoveAt(0);
            }
            else if (history.Count > 0 && history.Last().Index == latestPivot.Index)
            {
                // Update the last pivot if same index but different price
                history[history.Count - 1] = latestPivot;
            }
        }

        private bool UpdateWaveStateMachine(WaveState wave, List<Pivot> pivots, List<Pivot> pivotHistory)
        {
            if (pivots.Count < 3) { wave.IsValid = false; return false; }

            // Get the latest pivot
            var latestPivot = pivots.Last();
            
            // Only process if this is a new pivot (by index)
            if (latestPivot.Index <= wave.LastPivotIndex) return false;
            wave.LastPivotIndex = latestPivot.Index;

            // Determine trend from recent pivots
            if (pivots.Count >= 4)
            {
                var recentHighs = pivots.Where(p => p.IsHigh).TakeLast(2).ToList();
                var recentLows = pivots.Where(p => !p.IsHigh).TakeLast(2).ToList();
                if (recentHighs.Count >= 2 && recentLows.Count >= 2)
                {
                    bool upTrend = recentHighs.Last().Price > recentHighs.First().Price && recentLows.Last().Price > recentLows.First().Price;
                    bool downTrend = recentHighs.Last().Price < recentHighs.First().Price && recentLows.Last().Price < recentLows.First().Price;
                    
                    // Trend change - try to transition waves properly
                    if ((upTrend && !wave.IsUpTrend && wave.CurrentWave > 0) || (downTrend && wave.IsUpTrend && wave.CurrentWave > 0))
                    {
                        // Impulse wave completed, start corrective wave
                        if (wave.Type == WaveType.Impulse && wave.CurrentWave >= 5)
                        {
                            StartCorrectiveWave(wave, latestPivot);
                            return true;
                        }
                        // Pattern invalidated - backtrack to find new wave start
                        if (!BacktrackAndRestart(wave, pivotHistory, upTrend))
                        {
                            // If backtrack fails, start fresh from latest pivot
                            StartFreshWave(wave, latestPivot, upTrend);
                        }
                    }
                    if (upTrend) wave.IsUpTrend = true;
                    else if (downTrend) wave.IsUpTrend = false;
                }
            }

            // Process based on current wave type
            bool wasValid = wave.IsValid;
            if (wave.Type == WaveType.Corrective)
            {
                ProcessCorrectiveWave(wave, latestPivot);
            }
            else
            {
                ProcessImpulseWave(wave, latestPivot);
            }
            
            // If wave became invalid during processing, try to backtrack
            if (wasValid && !wave.IsValid && pivotHistory.Count >= 3)
            {
                if (BacktrackAndRestart(wave, pivotHistory, wave.IsUpTrend))
                {
                    // Successfully found alternative count
                    wave.IsValid = true;
                }
            }
            
            return true;
        }
        
        private bool BacktrackAndRestart(WaveState wave, List<Pivot> pivotHistory, bool isUpTrend)
        {
            // Try to find a valid wave pattern starting from recent pivots
            // Start from the 2nd most recent pivot and work backwards
            for (int startIdx = pivotHistory.Count - 2; startIdx >= 0 && startIdx >= pivotHistory.Count - 6; startIdx--)
            {
                var startPivot = pivotHistory[startIdx];
                
                // For uptrend, we need to start from a low; for downtrend, from a high
                if ((isUpTrend && startPivot.IsHigh) || (!isUpTrend && !startPivot.IsHigh))
                    continue;
                
                // Try to build a valid wave sequence from this starting point
                if (TryBuildWaveFromPivots(wave, pivotHistory, startIdx, isUpTrend))
                {
                    return true;
                }
            }
            return false;
        }
        
        private bool TryBuildWaveFromPivots(WaveState wave, List<Pivot> pivotHistory, int startIdx, bool isUpTrend)
        {
            if (startIdx + 2 >= pivotHistory.Count) return false;
            
            var p0 = pivotHistory[startIdx];
            var p1 = pivotHistory[startIdx + 1];
            var p2 = pivotHistory[startIdx + 2];
            
            if (isUpTrend)
            {
                // Validate: p0 is low, p1 is high > p0, p2 is low > p0 and < p1
                if (p0.IsHigh || !p1.IsHigh || p2.IsHigh) return false;
                if (p1.Price <= p0.Price) return false;
                if (p2.Price <= p0.Price || p2.Price >= p1.Price) return false;
                
                // Check Wave 2 retracement ratio
                decimal wave1Len = p1.Price - p0.Price;
                decimal wave2Retracement = p1.Price - p2.Price;
                decimal retracementRatio = wave2Retracement / wave1Len;
                if (retracementRatio < 0.382m || retracementRatio > 0.786m) return false;
                
                // Valid wave 1-2 found, set up state
                wave.Type = WaveType.Impulse;
                wave.IsUpTrend = true;
                wave.IsValid = true;
                wave.Wave0 = p0.Price;
                wave.Wave1 = p1.Price;
                wave.Wave2 = p2.Price;
                wave.Wave3 = 0; wave.Wave4 = 0; wave.Wave5 = 0;
                wave.WaveA = 0; wave.WaveB = 0; wave.WaveC = 0;
                wave.CurrentWave = 3;
                wave.LastPivotIndex = p2.Index;
                return true;
            }
            else
            {
                // Validate: p0 is high, p1 is low < p0, p2 is high < p0 and > p1
                if (!p0.IsHigh || p1.IsHigh || !p2.IsHigh) return false;
                if (p1.Price >= p0.Price) return false;
                if (p2.Price >= p0.Price || p2.Price <= p1.Price) return false;
                
                // Check Wave 2 retracement ratio
                decimal wave1Len = p0.Price - p1.Price;
                decimal wave2Retracement = p2.Price - p1.Price;
                decimal retracementRatio = wave2Retracement / wave1Len;
                if (retracementRatio < 0.382m || retracementRatio > 0.786m) return false;
                
                // Valid wave 1-2 found, set up state
                wave.Type = WaveType.Impulse;
                wave.IsUpTrend = false;
                wave.IsValid = true;
                wave.Wave0 = p0.Price;
                wave.Wave1 = p1.Price;
                wave.Wave2 = p2.Price;
                wave.Wave3 = 0; wave.Wave4 = 0; wave.Wave5 = 0;
                wave.WaveA = 0; wave.WaveB = 0; wave.WaveC = 0;
                wave.CurrentWave = 3;
                wave.LastPivotIndex = p2.Index;
                return true;
            }
        }
        
        private void StartFreshWave(WaveState wave, Pivot pivot, bool isUpTrend)
        {
            wave.Type = WaveType.None;
            wave.CurrentWave = 0;
            wave.IsValid = true;
            wave.IsUpTrend = isUpTrend;
            wave.Wave0 = 0; wave.Wave1 = 0; wave.Wave2 = 0; wave.Wave3 = 0; wave.Wave4 = 0; wave.Wave5 = 0;
            wave.WaveA = 0; wave.WaveB = 0; wave.WaveC = 0;
            
            // Initialize with the current pivot if it matches the expected type
            if ((isUpTrend && !pivot.IsHigh) || (!isUpTrend && pivot.IsHigh))
            {
                wave.Wave0 = pivot.Price;
                wave.CurrentWave = 1;
                wave.Type = WaveType.Impulse;
            }
        }

        private void ResetWaveValues(WaveState wave)
        {
            wave.Wave0 = 0; wave.Wave1 = 0; wave.Wave2 = 0; wave.Wave3 = 0; wave.Wave4 = 0; wave.Wave5 = 0;
            wave.WaveA = 0; wave.WaveB = 0; wave.WaveC = 0;
        }

        private void StartCorrectiveWave(WaveState wave, Pivot pivot)
        {
            wave.Type = WaveType.Corrective;
            wave.CurrentWave = 6; // A wave
            wave.IsValid = true;
            if (wave.IsUpTrend)
            {
                wave.WaveA = pivot.Price; // A wave is a low in uptrend correction
            }
            else
            {
                wave.WaveA = pivot.Price; // A wave is a high in downtrend correction
            }
            wave.WaveB = 0;
            wave.WaveC = 0;
        }

        private void ProcessImpulseWave(WaveState wave, Pivot pivot)
        {
            if (wave.IsUpTrend)
            {
                ProcessUpImpulse(wave, pivot);
            }
            else
            {
                ProcessDownImpulse(wave, pivot);
            }
        }

        private void ProcessUpImpulse(WaveState wave, Pivot pivot)
        {
            switch (wave.CurrentWave)
            {
                case 0: // Looking for wave 0 (start point - a low)
                    if (!pivot.IsHigh)
                    {
                        wave.Wave0 = pivot.Price;
                        wave.CurrentWave = 1;
                        wave.Type = WaveType.Impulse;
                        wave.IsValid = true;
                    }
                    break;

                case 1: // In wave 1, looking for wave 1 end (a high)
                    if (pivot.IsHigh && pivot.Price > wave.Wave0)
                    {
                        wave.Wave1 = pivot.Price;
                        wave.CurrentWave = 2;
                    }
                    // If we get another low lower than Wave0, update Wave0
                    else if (!pivot.IsHigh && pivot.Price < wave.Wave0)
                    {
                        wave.Wave0 = pivot.Price;
                    }
                    break;

                case 2: // In wave 2, looking for wave 2 end (a low)
                    if (!pivot.IsHigh)
                    {
                        // Wave 2 must not go below Wave 0
                        if (pivot.Price > wave.Wave0 && pivot.Price < wave.Wave1)
                        {
                            decimal wave1Len = wave.Wave1 - wave.Wave0;
                            decimal wave2Retracement = wave.Wave1 - pivot.Price;
                            decimal retracementRatio = wave2Retracement / wave1Len;
                            
                            // Wave 2 retracement should be 38.2% - 78.6% of Wave 1
                            if (retracementRatio >= 0.382m && retracementRatio <= 0.786m)
                            {
                                wave.Wave2 = pivot.Price;
                                wave.CurrentWave = 3;
                            }
                        }
                        else if (pivot.Price <= wave.Wave0)
                        {
                            // Violation: mark invalid but don't go backwards
                            wave.IsValid = false;
                        }
                    }
                    // If we get a higher high, update Wave1 but stay in wave 2
                    else if (pivot.IsHigh && pivot.Price > wave.Wave1)
                    {
                        wave.Wave1 = pivot.Price;
                    }
                    break;

                case 3: // In wave 3, looking for wave 3 end (a high)
                    if (pivot.IsHigh && pivot.Price > wave.Wave1)
                    {
                        decimal potentialWave3 = pivot.Price;
                        decimal wave3Len = potentialWave3 - wave.Wave2;
                        decimal wave1Len = wave.Wave1 - wave.Wave0;
                        
                        // Wave 3 should be at least 1.0x of Wave 1 (typically 1.618x)
                        // Allow some tolerance for market variations
                        if (wave3Len >= wave1Len * 0.618m)
                        {
                            wave.Wave3 = potentialWave3;
                            wave.CurrentWave = 4;
                        }
                    }
                    // Stay in wave 3, don't update wave 2
                    break;

                case 4: // In wave 4, looking for wave 4 end (a low)
                    if (!pivot.IsHigh)
                    {
                        // Wave 4 must not overlap Wave 1 territory
                        if (pivot.Price > wave.Wave1 && pivot.Price < wave.Wave3)
                        {
                            decimal wave4Len = wave.Wave3 - pivot.Price;
                            decimal wave3Len = wave.Wave3 - wave.Wave2;
                            
                            // Wave 4 retracement should be 23.6% - 61.8% of Wave 3
                            decimal retracementRatio = wave4Len / wave3Len;
                            if (retracementRatio >= 0.236m && retracementRatio <= 0.618m)
                            {
                                wave.Wave4 = pivot.Price;
                                wave.CurrentWave = 5;
                            }
                        }
                        else if (pivot.Price <= wave.Wave1)
                        {
                            // Violation of rule 3: wave 4 overlaps wave 1
                            wave.IsValid = false;
                        }
                    }
                    // Stay in wave 4, don't update wave 3
                    break;

                case 5: // In wave 5, looking for wave 5 end (a high)
                    if (pivot.IsHigh && pivot.Price > wave.Wave3)
                    {
                        decimal potentialWave5 = pivot.Price;
                        decimal wave5Len = potentialWave5 - wave.Wave4;
                        decimal wave1Len = wave.Wave1 - wave.Wave0;
                        decimal wave3Len = wave.Wave3 - wave.Wave2;
                        
                        // Rule: Wave 3 cannot be the shortest among 1, 3, 5
                        // Wave 5 typically equals Wave 1 or 0.618x of Wave 1
                        if (wave5Len >= wave1Len * 0.382m && wave5Len <= wave1Len * 2.618m)
                        {
                            // Validate Wave 3 is not the shortest
                            if (wave3Len >= wave1Len && wave3Len >= wave5Len * 0.618m)
                            {
                                wave.Wave5 = potentialWave5;
                                // Impulse complete, prepare for correction
                            }
                            else
                            {
                                // Wave 3 is too short - invalid pattern
                                wave.IsValid = false;
                            }
                        }
                    }
                    // Stay in wave 5, don't update wave 4
                    break;
            }
        }

        private void ProcessDownImpulse(WaveState wave, Pivot pivot)
        {
            switch (wave.CurrentWave)
            {
                case 0:
                    if (pivot.IsHigh)
                    {
                        wave.Wave0 = pivot.Price;
                        wave.CurrentWave = 1;
                        wave.Type = WaveType.Impulse;
                        wave.IsValid = true;
                    }
                    break;

                case 1:
                    if (!pivot.IsHigh && pivot.Price < wave.Wave0)
                    {
                        wave.Wave1 = pivot.Price;
                        wave.CurrentWave = 2;
                    }
                    else if (pivot.IsHigh && pivot.Price > wave.Wave0)
                    {
                        wave.Wave0 = pivot.Price;
                    }
                    break;

                case 2:
                    if (pivot.IsHigh)
                    {
                        if (pivot.Price < wave.Wave0 && pivot.Price > wave.Wave1)
                        {
                            decimal wave1Len = wave.Wave0 - wave.Wave1;
                            decimal wave2Retracement = pivot.Price - wave.Wave1;
                            decimal retracementRatio = wave2Retracement / wave1Len;
                            
                            // Wave 2 retracement should be 38.2% - 78.6% of Wave 1
                            if (retracementRatio >= 0.382m && retracementRatio <= 0.786m)
                            {
                                wave.Wave2 = pivot.Price;
                                wave.CurrentWave = 3;
                            }
                        }
                        else if (pivot.Price >= wave.Wave0)
                        {
                            // Violation: mark invalid but don't go backwards
                            wave.IsValid = false;
                        }
                    }
                    else if (!pivot.IsHigh && pivot.Price < wave.Wave1)
                    {
                        wave.Wave1 = pivot.Price;
                    }
                    break;

                case 3:
                    if (!pivot.IsHigh && pivot.Price < wave.Wave1)
                    {
                        decimal potentialWave3 = pivot.Price;
                        decimal wave3Len = wave.Wave2 - potentialWave3;
                        decimal wave1Len = wave.Wave0 - wave.Wave1;
                        
                        // Wave 3 should be at least 0.618x of Wave 1 (typically 1.618x)
                        if (wave3Len >= wave1Len * 0.618m)
                        {
                            wave.Wave3 = potentialWave3;
                            wave.CurrentWave = 4;
                        }
                    }
                    // Stay in wave 3, don't update wave 2
                    break;

                case 4:
                    if (pivot.IsHigh)
                    {
                        if (pivot.Price < wave.Wave1 && pivot.Price > wave.Wave3)
                        {
                            decimal wave4Len = pivot.Price - wave.Wave3;
                            decimal wave3Len = wave.Wave2 - wave.Wave3;
                            
                            // Wave 4 retracement should be 23.6% - 61.8% of Wave 3
                            decimal retracementRatio = wave4Len / wave3Len;
                            if (retracementRatio >= 0.236m && retracementRatio <= 0.618m)
                            {
                                wave.Wave4 = pivot.Price;
                                wave.CurrentWave = 5;
                            }
                        }
                        else if (pivot.Price >= wave.Wave1)
                        {
                            wave.IsValid = false;
                        }
                    }
                    // Stay in wave 4, don't update wave 3
                    break;

                case 5:
                    if (!pivot.IsHigh && pivot.Price < wave.Wave3)
                    {
                        decimal potentialWave5 = pivot.Price;
                        decimal wave5Len = wave.Wave4 - potentialWave5;
                        decimal wave1Len = wave.Wave0 - wave.Wave1;
                        decimal wave3Len = wave.Wave2 - wave.Wave3;
                        
                        // Wave 5 typically equals Wave 1 or 0.618x of Wave 1
                        if (wave5Len >= wave1Len * 0.382m && wave5Len <= wave1Len * 2.618m)
                        {
                            // Validate Wave 3 is not the shortest
                            if (wave3Len >= wave1Len && wave3Len >= wave5Len * 0.618m)
                            {
                                wave.Wave5 = potentialWave5;
                            }
                            else
                            {
                                wave.IsValid = false;
                            }
                        }
                    }
                    // Stay in wave 5, don't update wave 4
                    break;
            }
        }

        private void ProcessCorrectiveWave(WaveState wave, Pivot pivot)
        {
            // ABC correction after uptrend: A down, B up, C down
            // ABC correction after downtrend: A up, B down, C up
            bool afterUptrend = wave.IsUpTrend;

            switch (wave.CurrentWave)
            {
                case 6: // A wave in progress
                    if (afterUptrend)
                    {
                        // A wave ends at a low
                        if (!pivot.IsHigh)
                        {
                            wave.WaveA = pivot.Price;
                        }
                        else if (pivot.IsHigh && wave.WaveA > 0)
                        {
                            // B wave starts
                            wave.WaveB = pivot.Price;
                            wave.CurrentWave = 7;
                        }
                    }
                    else
                    {
                        if (pivot.IsHigh)
                        {
                            wave.WaveA = pivot.Price;
                        }
                        else if (!pivot.IsHigh && wave.WaveA > 0)
                        {
                            wave.WaveB = pivot.Price;
                            wave.CurrentWave = 7;
                        }
                    }
                    break;

                case 7: // B wave in progress
                    if (afterUptrend)
                    {
                        if (pivot.IsHigh && pivot.Price > wave.WaveB)
                        {
                            wave.WaveB = pivot.Price;
                        }
                        else if (!pivot.IsHigh)
                        {
                            wave.WaveC = pivot.Price;
                            wave.CurrentWave = 8;
                        }
                    }
                    else
                    {
                        if (!pivot.IsHigh && pivot.Price < wave.WaveB)
                        {
                            wave.WaveB = pivot.Price;
                        }
                        else if (pivot.IsHigh)
                        {
                            wave.WaveC = pivot.Price;
                            wave.CurrentWave = 8;
                        }
                    }
                    break;

                case 8: // C wave in progress
                    if (afterUptrend)
                    {
                        if (!pivot.IsHigh && pivot.Price < wave.WaveC)
                        {
                            wave.WaveC = pivot.Price;
                        }
                        else if (pivot.IsHigh)
                        {
                            // Correction complete, start new impulse from C wave end
                            decimal waveCPrice = wave.WaveC;
                            wave.IsUpTrend = true;
                            ResetWaveValues(wave);
                            wave.Wave0 = waveCPrice;
                            wave.CurrentWave = 1;
                            wave.Type = WaveType.Impulse;
                            wave.IsValid = true;
                        }
                    }
                    else
                    {
                        if (pivot.IsHigh && pivot.Price > wave.WaveC)
                        {
                            wave.WaveC = pivot.Price;
                        }
                        else if (!pivot.IsHigh)
                        {
                            // Correction complete, start new impulse from C wave end
                            decimal waveCPrice = wave.WaveC;
                            wave.IsUpTrend = false;
                            ResetWaveValues(wave);
                            wave.Wave0 = waveCPrice;
                            wave.CurrentWave = 1;
                            wave.Type = WaveType.Impulse;
                            wave.IsValid = true;
                        }
                    }
                    break;
            }
        }

        private void ExecuteTrade(TradeState trade, WaveState wave, SkQuote quote, TableUnit tu, Period period, int mode, int sendMode, decimal num, decimal lossRate, decimal profitRate, int trailingStop, decimal trailingPercent)
        {
            if (trade.Status == 0)
            {
                // 止损后冷却期
                if (trade.CooldownRemaining > 0)
                {
                    trade.CooldownRemaining--;
                    return;
                }

                // 信号重置再武装：波浪结构失效时解除封锁
                if (trade.BlockedDir > 0 && !wave.IsValid)
                    trade.BlockedDir = 0;

                if (!wave.IsValid) return;

                // Impulse wave trading
                if (wave.Type == WaveType.Impulse)
                {
                    if (wave.IsUpTrend && mode != 2 && trade.BlockedDir != 1)
                    {
                        // Enter long on wave 3 breakout
                        if (wave.CurrentWave == 3 && wave.Wave1 > 0 && wave.Wave2 > 0 && quote.Close > wave.Wave1)
                            OpenLong(trade, tu, quote, period, sendMode, num, wave, lossRate, profitRate);
                        // Enter long on wave 5 breakout
                        else if (wave.CurrentWave == 5 && wave.Wave3 > 0 && wave.Wave4 > 0 && quote.Close > wave.Wave3)
                            OpenLong(trade, tu, quote, period, sendMode, num, wave, lossRate, profitRate);
                    }
                    else if (!wave.IsUpTrend && mode != 1 && trade.BlockedDir != 2)
                    {
                        if (wave.CurrentWave == 3 && wave.Wave1 > 0 && wave.Wave2 > 0 && quote.Close < wave.Wave1)
                            OpenShort(trade, tu, quote, period, sendMode, num, wave, lossRate, profitRate);
                        else if (wave.CurrentWave == 5 && wave.Wave3 > 0 && wave.Wave4 > 0 && quote.Close < wave.Wave3)
                            OpenShort(trade, tu, quote, period, sendMode, num, wave, lossRate, profitRate);
                    }
                }
                // Corrective wave trading - enter after C wave completes
                else if (wave.Type == WaveType.Corrective && wave.CurrentWave == 8)
                {
                    if (wave.IsUpTrend && mode != 2 && trade.BlockedDir != 1 && wave.WaveC > 0 && wave.WaveB > 0)
                    {
                        // After downward correction, look for reversal
                        if (quote.Close > wave.WaveB)
                            OpenLong(trade, tu, quote, period, sendMode, num, wave, lossRate, profitRate);
                    }
                    else if (!wave.IsUpTrend && mode != 1 && trade.BlockedDir != 2 && wave.WaveC > 0 && wave.WaveB > 0)
                    {
                        if (quote.Close < wave.WaveB)
                            OpenShort(trade, tu, quote, period, sendMode, num, wave, lossRate, profitRate);
                    }
                }
            }
            else if (trade.Status == 1) ManageLong(trade, tu, quote, period, sendMode, trailingStop, trailingPercent);
            else if (trade.Status == 2) ManageShort(trade, tu, quote, period, sendMode, trailingStop, trailingPercent);
        }

        private void OpenLong(TradeState trade, TableUnit tu, SkQuote quote, Period period, int sendMode, decimal num, WaveState wave, decimal lossRate, decimal profitRate)
        {
            trade.Status = 1; trade.Num = num; trade.EntryPrice = quote.Close; trade.HighestPrice = quote.Close;
            decimal stopLevel = wave.Type == WaveType.Impulse ? wave.Wave2 : wave.WaveC;
            trade.StopLoss = stopLevel > 0 ? stopLevel * 0.99m : quote.Close * (1 - lossRate / 100);
            decimal wave1Len = wave.Wave1 > 0 && wave.Wave0 > 0 ? wave.Wave1 - wave.Wave0 : quote.Close * profitRate / 100;
            trade.TakeProfit = quote.Close + wave1Len * 1.618m;
            Trade(tu.MktSymbol, OrderType.BUY, quote.Close, num, period, sendMode);
        }

        private void OpenShort(TradeState trade, TableUnit tu, SkQuote quote, Period period, int sendMode, decimal num, WaveState wave, decimal lossRate, decimal profitRate)
        {
            trade.Status = 2; trade.Num = num; trade.EntryPrice = quote.Close; trade.LowestPrice = quote.Close;
            decimal stopLevel = wave.Type == WaveType.Impulse ? wave.Wave2 : wave.WaveC;
            trade.StopLoss = stopLevel > 0 ? stopLevel * 1.01m : quote.Close * (1 + lossRate / 100);
            decimal wave1Len = wave.Wave0 > 0 && wave.Wave1 > 0 ? wave.Wave0 - wave.Wave1 : quote.Close * profitRate / 100;
            trade.TakeProfit = quote.Close - wave1Len * 1.618m;
            Trade(tu.MktSymbol, OrderType.SELL, quote.Close, num, period, sendMode);
        }

        private void ManageLong(TradeState trade, TableUnit tu, SkQuote quote, Period period, int sendMode, int trailingStop, decimal trailingPercent)
        {
            if (quote.High > trade.HighestPrice)
            {
                trade.HighestPrice = quote.High;
                if (trailingStop == 1) { decimal newStop = trade.HighestPrice * (1 - trailingPercent / 100); if (newStop > trade.StopLoss) trade.StopLoss = newStop; }
            }
            if (quote.Close <= trade.StopLoss)
            { var oriNum = trade.Num; trade.Status = 0; trade.Num = 0; Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, quote.Close, oriNum, period, sendMode); trade.BlockedDir = 1; trade.CooldownRemaining = Convert.ToInt32(ArgDic["stopCooldownBars"]); }
            else if (quote.Close >= trade.TakeProfit)
            { var oriNum = trade.Num; trade.Status = 0; trade.Num = 0; Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, quote.Close, oriNum, period, sendMode); }
        }

        private void ManageShort(TradeState trade, TableUnit tu, SkQuote quote, Period period, int sendMode, int trailingStop, decimal trailingPercent)
        {
            if (quote.Low < trade.LowestPrice)
            {
                trade.LowestPrice = quote.Low;
                if (trailingStop == 1) { decimal newStop = trade.LowestPrice * (1 + trailingPercent / 100); if (newStop < trade.StopLoss) trade.StopLoss = newStop; }
            }
            if (quote.Close >= trade.StopLoss)
            { var oriNum = trade.Num; trade.Status = 0; trade.Num = 0; Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, quote.Close, oriNum, period, sendMode); trade.BlockedDir = 2; trade.CooldownRemaining = Convert.ToInt32(ArgDic["stopCooldownBars"]); }
            else if (quote.Close <= trade.TakeProfit)
            { var oriNum = trade.Num; trade.Status = 0; trade.Num = 0; Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, quote.Close, oriNum, period, sendMode); }
        }

        private decimal CalculateLots(TableUnit tu, SkQuote quote)
        {
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            if (lotsMode == 1)
            {
                var symbol = GetSymbol(tu.MktSymbol);
                num = Convert.ToDecimal(ArgDic["money"]) / (quote.Close * symbol.multiplier * symbol.margin_ratio);
                num = symbol.symbol_type == (int)SymbolType.COIN ? (int)(num * 1000) / 1000.0m : (int)num;
            }
            return num;
        }

        private void PlotWaveInfo(WaveState wave, bool isNewPivot)
        {
            Plot("sub1", "Wave", PlotType.LINE, wave.CurrentWave);
            Plot("sub1", "Valid", PlotType.LINE, wave.IsValid ? 1 : 0);
            Plot("sub1", "Trend", PlotType.LINE, wave.IsUpTrend ? 1 : -1);
            Plot("sub1", "Type", PlotType.LINE, wave.Type == WaveType.Impulse ? 1 : (wave.Type == WaveType.Corrective ? -1 : 0));

            if (wave.IsValid && isNewPivot)
            {
                if (wave.Type == WaveType.Impulse)
                {
                    if (wave.CurrentWave == 3 && wave.Wave2 > 0) Plot("main", "W3", PlotType.POINT, (double)wave.Wave2);
                    if (wave.CurrentWave == 5 && wave.Wave4 > 0) Plot("main", "W5", PlotType.POINT, (double)wave.Wave4);
                }
                else if (wave.Type == WaveType.Corrective)
                {
                    if (wave.CurrentWave == 8 && wave.WaveC > 0) Plot("main", "WC", PlotType.POINT, (double)wave.WaveC);
                }
            }
        }
    }
}
