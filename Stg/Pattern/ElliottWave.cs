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
            sd.ArgDic["lossRate"] = 3m;
            sd.ArgDic["profitRate"] = 20m;
            sd.ArgDic["trailingStop"] = 1;
            sd.ArgDic["trailingPercent"] = 20m;
            sd.ArgDic["lotsMode"] = 1;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;
            sd.ArgDescDic["zigzagDepth"] = new ArgDesc() { Text = "ZigZag Depth", Explain = "Depth for pivot detection" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "Mode", Explain = "0 Standard 1 Long Only 2 Short Only" };
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
        }

        private class Pivot
        {
            public int Index { get; set; }
            public decimal Price { get; set; }
            public bool IsHigh { get; set; }
        }

        private Dictionary<string, TradeState> _tradeDic = new Dictionary<string, TradeState>();
        private Dictionary<string, WaveState> _waveDic = new Dictionary<string, WaveState>();

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

            int zigzagDepth = (int)ArgDic["zigzagDepth"];
            double zigzagDeviation = (double)ArgDic["zigzagDeviation"];
            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            decimal lossRate = (decimal)ArgDic["lossRate"];
            decimal profitRate = (decimal)ArgDic["profitRate"];
            int trailingStop = (int)ArgDic["trailingStop"];
            decimal trailingPercent = (decimal)ArgDic["trailingPercent"];
            decimal num = CalculateLots(tu, currentQuote);

            var pivots = FindPivots(quotes, zigzagDepth, zigzagDeviation);
            bool isNewPivot = UpdateWaveStateMachine(wave, pivots);
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

        private bool UpdateWaveStateMachine(WaveState wave, List<Pivot> pivots)
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
                    
                    // Trend change resets wave count
                    if ((upTrend && !wave.IsUpTrend && wave.CurrentWave > 0) || (downTrend && wave.IsUpTrend && wave.CurrentWave > 0))
                    {
                        // Impulse wave completed, start corrective wave
                        if (wave.Type == WaveType.Impulse && wave.CurrentWave >= 5)
                        {
                            StartCorrectiveWave(wave, latestPivot);
                            return true;
                        }
                        // Otherwise reset
                        ResetWave(wave);
                    }
                    if (upTrend) wave.IsUpTrend = true;
                    else if (downTrend) wave.IsUpTrend = false;
                }
            }

            // Process based on current wave type
            if (wave.Type == WaveType.Corrective)
            {
                ProcessCorrectiveWave(wave, latestPivot);
            }
            else
            {
                ProcessImpulseWave(wave, latestPivot);
            }
            return true;
        }

        private void ResetWave(WaveState wave)
        {
            wave.Type = WaveType.None;
            wave.CurrentWave = 0;
            wave.IsValid = false;
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
                            wave.Wave2 = pivot.Price;
                            wave.CurrentWave = 3;
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
                        wave.Wave3 = pivot.Price;
                        wave.CurrentWave = 4;
                    }
                    // Stay in wave 3, don't update wave 2
                    break;

                case 4: // In wave 4, looking for wave 4 end (a low)
                    if (!pivot.IsHigh)
                    {
                        // Wave 4 must not overlap Wave 1 territory
                        if (pivot.Price > wave.Wave1 && pivot.Price < wave.Wave3)
                        {
                            wave.Wave4 = pivot.Price;
                            wave.CurrentWave = 5;
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
                        wave.Wave5 = pivot.Price;
                        // Impulse complete, prepare for correction
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
                            wave.Wave2 = pivot.Price;
                            wave.CurrentWave = 3;
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
                        wave.Wave3 = pivot.Price;
                        wave.CurrentWave = 4;
                    }
                    // Stay in wave 3, don't update wave 2
                    break;

                case 4:
                    if (pivot.IsHigh)
                    {
                        if (pivot.Price < wave.Wave1 && pivot.Price > wave.Wave3)
                        {
                            wave.Wave4 = pivot.Price;
                            wave.CurrentWave = 5;
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
                        wave.Wave5 = pivot.Price;
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
                            // Correction complete, may start new impulse
                            wave.IsUpTrend = true;
                            ResetWave(wave);
                            wave.Wave0 = wave.WaveC;
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
                            wave.IsUpTrend = false;
                            ResetWave(wave);
                            wave.Wave0 = wave.WaveC;
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
                if (!wave.IsValid) return;

                // Impulse wave trading
                if (wave.Type == WaveType.Impulse)
                {
                    if (wave.IsUpTrend && mode != 2)
                    {
                        // Enter long on wave 3 breakout
                        if (wave.CurrentWave == 3 && wave.Wave1 > 0 && wave.Wave2 > 0 && quote.Close > wave.Wave1)
                            OpenLong(trade, tu, quote, period, sendMode, num, wave, lossRate, profitRate);
                        // Enter long on wave 5 breakout
                        else if (wave.CurrentWave == 5 && wave.Wave3 > 0 && wave.Wave4 > 0 && quote.Close > wave.Wave3)
                            OpenLong(trade, tu, quote, period, sendMode, num, wave, lossRate, profitRate);
                    }
                    else if (!wave.IsUpTrend && mode != 1)
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
                    if (wave.IsUpTrend && mode != 2 && wave.WaveC > 0 && wave.WaveB > 0)
                    {
                        // After downward correction, look for reversal
                        if (quote.Close > wave.WaveB)
                            OpenLong(trade, tu, quote, period, sendMode, num, wave, lossRate, profitRate);
                    }
                    else if (!wave.IsUpTrend && mode != 1 && wave.WaveC > 0 && wave.WaveB > 0)
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
            if (quote.Close <= trade.StopLoss || quote.Close >= trade.TakeProfit)
            { var oriNum = trade.Num; trade.Status = 0; trade.Num = 0; Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, quote.Close, oriNum, period, sendMode); }
        }

        private void ManageShort(TradeState trade, TableUnit tu, SkQuote quote, Period period, int sendMode, int trailingStop, decimal trailingPercent)
        {
            if (quote.Low < trade.LowestPrice)
            {
                trade.LowestPrice = quote.Low;
                if (trailingStop == 1) { decimal newStop = trade.LowestPrice * (1 + trailingPercent / 100); if (newStop < trade.StopLoss) trade.StopLoss = newStop; }
            }
            if (quote.Close >= trade.StopLoss || quote.Close <= trade.TakeProfit)
            { var oriNum = trade.Num; trade.Status = 0; trade.Num = 0; Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, quote.Close, oriNum, period, sendMode); }
        }

        private decimal CalculateLots(TableUnit tu, SkQuote quote)
        {
            var num = (decimal)ArgDic["lots"];
            var lotsMode = (int)ArgDic["lotsMode"];
            if (lotsMode == 1)
            {
                var symbol = GetSymbol(tu.MktSymbol);
                num = (decimal)ArgDic["money"] / (quote.Close * symbol.multiplier * symbol.margin_ratio);
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
