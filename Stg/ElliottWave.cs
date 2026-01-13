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
    /// <summary>
    /// 基于艾略特波浪理论的交易系统
    /// 核心逻辑：
    /// 1. 识别推动浪(1-2-3-4-5)和调整浪(A-B-C)
    /// 2. 在第3浪启动时做多，在C浪结束时做多
    /// 3. 使用斐波那契回撤确认浪的有效性
    /// 4. 结合ZigZag指标识别关键转折点
    /// </summary>
    public class ElliottWave : StgBase
    {
        public ElliottWave(string id) : base(id)
        {
        }

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();
            
            // ZigZag参数 - 用于识别波浪转折点
            sd.ArgDic["zigzagDepth"] = 12;           // ZigZag深度
            sd.ArgDic["zigzagDeviation"] = 5.0;     // 偏差百分比
            sd.ArgDic["minWavePoints"] = 5;          // 最小波浪点数
            
            // 斐波那契参数
            sd.ArgDic["fib382"] = 0.382;            // 38.2%回撤
            sd.ArgDic["fib500"] = 0.500;            // 50%回撤
            sd.ArgDic["fib618"] = 0.618;            // 61.8%回撤(黄金分割)
            sd.ArgDic["fib786"] = 0.786;            // 78.6%回撤
            sd.ArgDic["fib1618"] = 1.618;           // 161.8%扩展
            sd.ArgDic["fib2618"] = 2.618;           // 261.8%扩展
            
            // 波浪验证参数
            sd.ArgDic["wave2MaxRetracement"] = 0.786;  // 第2浪最大回撤(不超过第1浪起点)
            sd.ArgDic["wave4MaxRetracement"] = 0.382;  // 第4浪最大回撤(不进入第1浪区域)
            sd.ArgDic["wave3MinExtension"] = 1.0;      // 第3浪最小扩展(通常最长)
            
            // 交易参数
            sd.ArgDic["mode"] = 0;                   // 0 标准 1 仅做多 2 仅做空
            sd.ArgDic["sendMode"] = 0;              // 0 立即 1 下个开盘
            sd.ArgDic["lossRate"] = 3m;             // 止损百分比
            sd.ArgDic["profitRate"] = 6m;           // 止盈百分比
            sd.ArgDic["trailingStop"] = 1;          // 是否启用追踪止损 0否 1是
            sd.ArgDic["trailingPercent"] = 2m;      // 追踪止损百分比
            
            // 入场条件
            sd.ArgDic["entryWave"] = 3;             // 入场浪位 3=第3浪启动 5=第5浪启动
            sd.ArgDic["confirmBars"] = 2;           // 确认K线数
            
            // 手数控制
            sd.ArgDic["lotsMode"] = 0;
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 100000m;

            // 参数说明
            sd.ArgDescDic["zigzagDepth"] = new ArgDesc() { Text = "ZigZag深度", Explain = "识别转折点的回溯周期" };
            sd.ArgDescDic["zigzagDeviation"] = new ArgDesc() { Text = "ZigZag偏差", Explain = "价格变动百分比阈值" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
            sd.ArgDescDic["entryWave"] = new ArgDesc() { Text = "入场浪位", Explain = "3=第3浪启动 5=第5浪启动" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            sd.ArgDescDic["trailingStop"] = new ArgDesc() { Text = "追踪止损", Explain = "0 关闭 1 开启" };
            
            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;
            
            // 图表颜色配置
            sd.ColorDic["wave-Impulse"] = "#2ECC71";    // 推动浪绿色
            sd.ColorDic["wave-Corrective"] = "#E74C3C"; // 调整浪红色
            sd.ColorDic["wave-Pivot"] = "#F39C12";      // 转折点橙色
            sd.ColorDic["fib-Level"] = "#9B59B6";       // 斐波那契紫色
            
            return sd;
        }

        #region 数据结构定义

        /// <summary>
        /// 波浪转折点
        /// </summary>
        private class WavePivot
        {
            public int Index { get; set; }          // K线索引
            public decimal Price { get; set; }      // 价格
            public DateTimeOffset Time { get; set; } // 时间
            public bool IsHigh { get; set; }        // 是否为高点
            public int WaveNumber { get; set; }     // 波浪编号 (1-5 或 A=6,B=7,C=8)
        }

        /// <summary>
        /// 波浪结构
        /// </summary>
        private class WaveStructure
        {
            public List<WavePivot> Pivots { get; set; } = new List<WavePivot>();
            public WaveType Type { get; set; }      // 波浪类型
            public WaveDirection Direction { get; set; } // 波浪方向
            public int CurrentWave { get; set; }    // 当前所处浪位
            public bool IsValid { get; set; }       // 是否有效
            public decimal Wave1Start { get; set; } // 第1浪起点
            public decimal Wave1End { get; set; }   // 第1浪终点
            public decimal Wave2End { get; set; }   // 第2浪终点
            public decimal Wave3End { get; set; }   // 第3浪终点
            public decimal Wave4End { get; set; }   // 第4浪终点
            public decimal Wave5End { get; set; }   // 第5浪终点
        }

        private enum WaveType
        {
            Unknown,
            Impulse,    // 推动浪 1-2-3-4-5
            Corrective  // 调整浪 A-B-C
        }

        private enum WaveDirection
        {
            Unknown,
            Up,         // 上升趋势
            Down        // 下降趋势
        }

        /// <summary>
        /// 交易状态
        /// </summary>
        private class TradeState
        {
            public int Status { get; set; }         // 0空仓 1多头 2空头
            public decimal Num { get; set; }        // 持仓数量
            public decimal EntryPrice { get; set; } // 入场价格
            public decimal StopLoss { get; set; }   // 止损价
            public decimal TakeProfit { get; set; } // 止盈价
            public decimal HighestPrice { get; set; } // 持仓期间最高价(用于追踪止损)
            public decimal LowestPrice { get; set; }  // 持仓期间最低价
            public int EntryWave { get; set; }      // 入场时的浪位
            public WaveStructure? CurrentStructure { get; set; } // 当前波浪结构
        }

        #endregion

        private Dictionary<string, TradeState> _stateDic = new Dictionary<string, TradeState>();
        private Dictionary<string, List<WavePivot>> _pivotCache = new Dictionary<string, List<WavePivot>>();

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);
            
            if (!isFinal) return;
            if (tu.QuoteList.Count < 50) return; // 需要足够的历史数据

            // 获取参数
            int zigzagDepth = (int)ArgDic["zigzagDepth"];
            double zigzagDeviation = (double)ArgDic["zigzagDeviation"];
            int mode = (int)ArgDic["mode"];
            int sendMode = (int)ArgDic["sendMode"];
            decimal lossRate = (decimal)ArgDic["lossRate"];
            decimal profitRate = (decimal)ArgDic["profitRate"];
            int trailingStop = (int)ArgDic["trailingStop"];
            decimal trailingPercent = (decimal)ArgDic["trailingPercent"];
            int entryWave = (int)ArgDic["entryWave"];
            int confirmBars = (int)ArgDic["confirmBars"];

            var quotes = tu.QuoteList;
            var currentQuote = quotes.Last();
            var sk = tu.GetStateKey();

            // 获取或创建状态
            if (!_stateDic.ContainsKey(sk))
            {
                _stateDic[sk] = new TradeState();
            }
            var state = _stateDic[sk];

            // 计算手数
            decimal num = CalculateLots(tu, currentQuote);

            // 1. 识别ZigZag转折点
            var pivots = IdentifyPivots(quotes, zigzagDepth, zigzagDeviation);
            _pivotCache[sk] = pivots;

            // 2. 分析波浪结构
            var waveStructure = AnalyzeWaveStructure(pivots, quotes);

            // 3. 绘制波浪和斐波那契
            PlotWaveStructure(waveStructure, pivots);

            // 4. 生成交易信号并执行
            ExecuteTradeLogic(state, waveStructure, currentQuote, tu, period, 
                mode, sendMode, num, lossRate, profitRate, 
                trailingStop, trailingPercent, entryWave, confirmBars);
        }

        #region 波浪识别算法

        /// <summary>
        /// 识别ZigZag转折点
        /// </summary>
        private List<WavePivot> IdentifyPivots(List<SkQuote> quotes, int depth, double deviation)
        {
            var pivots = new List<WavePivot>();
            if (quotes.Count < depth * 2) return pivots;

            decimal lastPivotPrice = 0;
            bool lastWasHigh = false;
            int lastPivotIndex = -1;

            for (int i = depth; i < quotes.Count - depth; i++)
            {
                var current = quotes[i];
                bool isLocalHigh = true;
                bool isLocalLow = true;

                // 检查是否为局部高点
                for (int j = i - depth; j <= i + depth; j++)
                {
                    if (j == i) continue;
                    if (quotes[j].High >= current.High) isLocalHigh = false;
                    if (quotes[j].Low <= current.Low) isLocalLow = false;
                }

                // 验证偏差阈值
                if (isLocalHigh && lastPivotIndex >= 0)
                {
                    decimal change = Math.Abs((current.High - lastPivotPrice) / lastPivotPrice * 100);
                    if ((decimal)change < (decimal)deviation && lastWasHigh) isLocalHigh = false;
                }

                if (isLocalLow && lastPivotIndex >= 0)
                {
                    decimal change = Math.Abs((current.Low - lastPivotPrice) / lastPivotPrice * 100);
                    if ((decimal)change < (decimal)deviation && !lastWasHigh) isLocalLow = false;
                }

                // 添加转折点
                if (isLocalHigh && (!lastWasHigh || lastPivotIndex < 0))
                {
                    pivots.Add(new WavePivot
                    {
                        Index = i,
                        Price = current.High,
                        Time = current.Date,
                        IsHigh = true
                    });
                    lastPivotPrice = current.High;
                    lastWasHigh = true;
                    lastPivotIndex = i;
                }
                else if (isLocalLow && (lastWasHigh || lastPivotIndex < 0))
                {
                    pivots.Add(new WavePivot
                    {
                        Index = i,
                        Price = current.Low,
                        Time = current.Date,
                        IsHigh = false
                    });
                    lastPivotPrice = current.Low;
                    lastWasHigh = false;
                    lastPivotIndex = i;
                }
            }

            return pivots;
        }

        /// <summary>
        /// 分析波浪结构
        /// </summary>
        private WaveStructure AnalyzeWaveStructure(List<WavePivot> pivots, List<SkQuote> quotes)
        {
            var structure = new WaveStructure();
            
            if (pivots.Count < 5) 
            {
                structure.IsValid = false;
                return structure;
            }

            // 获取最近的转折点进行分析
            var recentPivots = pivots.Skip(Math.Max(0, pivots.Count - 8)).ToList();
            
            // 判断趋势方向
            structure.Direction = DetermineDirection(recentPivots);
            
            // 尝试识别推动浪模式
            if (TryIdentifyImpulseWave(recentPivots, structure))
            {
                structure.Type = WaveType.Impulse;
                structure.IsValid = ValidateImpulseWave(structure);
            }
            // 尝试识别调整浪模式
            else if (TryIdentifyCorrectiveWave(recentPivots, structure))
            {
                structure.Type = WaveType.Corrective;
                structure.IsValid = true;
            }
            else
            {
                structure.Type = WaveType.Unknown;
                structure.IsValid = false;
            }

            structure.Pivots = recentPivots;
            return structure;
        }

        /// <summary>
        /// 判断趋势方向
        /// </summary>
        private WaveDirection DetermineDirection(List<WavePivot> pivots)
        {
            if (pivots.Count < 2) return WaveDirection.Unknown;

            var highs = pivots.Where(p => p.IsHigh).ToList();
            var lows = pivots.Where(p => !p.IsHigh).ToList();

            if (highs.Count >= 2 && lows.Count >= 2)
            {
                // 高点和低点都在抬升 = 上升趋势
                bool higherHighs = highs.Last().Price > highs.First().Price;
                bool higherLows = lows.Last().Price > lows.First().Price;
                
                if (higherHighs && higherLows) return WaveDirection.Up;
                
                // 高点和低点都在降低 = 下降趋势
                bool lowerHighs = highs.Last().Price < highs.First().Price;
                bool lowerLows = lows.Last().Price < lows.First().Price;
                
                if (lowerHighs && lowerLows) return WaveDirection.Down;
            }

            return WaveDirection.Unknown;
        }

        /// <summary>
        /// 尝试识别推动浪(1-2-3-4-5)
        /// </summary>
        private bool TryIdentifyImpulseWave(List<WavePivot> pivots, WaveStructure structure)
        {
            if (pivots.Count < 5) return false;

            // 上升推动浪: 低-高-低-高-低-高 (1起点-1终点-2终点-3终点-4终点-5终点)
            // 下降推动浪: 高-低-高-低-高-低

            var lastFive = pivots.Skip(pivots.Count - 5).ToList();
            
            // 检查上升推动浪模式
            if (structure.Direction == WaveDirection.Up || structure.Direction == WaveDirection.Unknown)
            {
                if (!lastFive[0].IsHigh && lastFive[1].IsHigh && !lastFive[2].IsHigh && 
                    lastFive[3].IsHigh && !lastFive[4].IsHigh)
                {
                    // 可能是上升推动浪的1-2-3-4结构，等待第5浪
                    structure.Wave1Start = lastFive[0].Price;
                    structure.Wave1End = lastFive[1].Price;
                    structure.Wave2End = lastFive[2].Price;
                    structure.Wave3End = lastFive[3].Price;
                    structure.Wave4End = lastFive[4].Price;
                    structure.Direction = WaveDirection.Up;
                    
                    // 判断当前处于哪一浪
                    structure.CurrentWave = DetermineCurrentWave(structure, true);
                    
                    lastFive[0].WaveNumber = 1;
                    lastFive[1].WaveNumber = 1;
                    lastFive[2].WaveNumber = 2;
                    lastFive[3].WaveNumber = 3;
                    lastFive[4].WaveNumber = 4;
                    
                    return true;
                }
            }

            // 检查下降推动浪模式
            if (structure.Direction == WaveDirection.Down || structure.Direction == WaveDirection.Unknown)
            {
                if (lastFive[0].IsHigh && !lastFive[1].IsHigh && lastFive[2].IsHigh && 
                    !lastFive[3].IsHigh && lastFive[4].IsHigh)
                {
                    structure.Wave1Start = lastFive[0].Price;
                    structure.Wave1End = lastFive[1].Price;
                    structure.Wave2End = lastFive[2].Price;
                    structure.Wave3End = lastFive[3].Price;
                    structure.Wave4End = lastFive[4].Price;
                    structure.Direction = WaveDirection.Down;
                    
                    structure.CurrentWave = DetermineCurrentWave(structure, false);
                    
                    lastFive[0].WaveNumber = 1;
                    lastFive[1].WaveNumber = 1;
                    lastFive[2].WaveNumber = 2;
                    lastFive[3].WaveNumber = 3;
                    lastFive[4].WaveNumber = 4;
                    
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判断当前处于哪一浪
        /// </summary>
        private int DetermineCurrentWave(WaveStructure structure, bool isUptrend)
        {
            // 基于已识别的浪位判断当前所处位置
            if (structure.Wave4End != 0)
            {
                return 5; // 第4浪已完成，当前在第5浪
            }
            if (structure.Wave3End != 0)
            {
                return 4;
            }
            if (structure.Wave2End != 0)
            {
                return 3;
            }
            if (structure.Wave1End != 0)
            {
                return 2;
            }
            return 1;
        }

        /// <summary>
        /// 尝试识别调整浪(A-B-C)
        /// </summary>
        private bool TryIdentifyCorrectiveWave(List<WavePivot> pivots, WaveStructure structure)
        {
            if (pivots.Count < 3) return false;

            var lastThree = pivots.Skip(pivots.Count - 3).ToList();
            
            // A-B-C调整浪: 高-低-高(下跌调整) 或 低-高-低(上涨调整)
            if (lastThree[0].IsHigh && !lastThree[1].IsHigh && lastThree[2].IsHigh)
            {
                // 下跌后的调整(反弹)
                structure.Direction = WaveDirection.Up;
                structure.CurrentWave = 8; // C浪 = 8
                
                lastThree[0].WaveNumber = 6; // A
                lastThree[1].WaveNumber = 7; // B
                lastThree[2].WaveNumber = 8; // C
                
                return true;
            }
            
            if (!lastThree[0].IsHigh && lastThree[1].IsHigh && !lastThree[2].IsHigh)
            {
                // 上涨后的调整(回调)
                structure.Direction = WaveDirection.Down;
                structure.CurrentWave = 8;
                
                lastThree[0].WaveNumber = 6;
                lastThree[1].WaveNumber = 7;
                lastThree[2].WaveNumber = 8;
                
                return true;
            }

            return false;
        }

        /// <summary>
        /// 验证推动浪是否符合艾略特波浪规则
        /// </summary>
        private bool ValidateImpulseWave(WaveStructure structure)
        {
            double wave2MaxRetracement = (double)ArgDic["wave2MaxRetracement"];
            double wave4MaxRetracement = (double)ArgDic["wave4MaxRetracement"];
            double wave3MinExtension = (double)ArgDic["wave3MinExtension"];

            if (structure.Direction == WaveDirection.Up)
            {
                // 规则1: 第2浪不能回撤超过第1浪的起点
                if (structure.Wave2End <= structure.Wave1Start) return false;
                
                // 规则2: 第3浪通常是最长的，至少等于第1浪
                decimal wave1Length = structure.Wave1End - structure.Wave1Start;
                decimal wave3Length = structure.Wave3End - structure.Wave2End;
                if (wave3Length < wave1Length * (decimal)wave3MinExtension) return false;
                
                // 规则3: 第4浪不能进入第1浪的价格区域
                if (structure.Wave4End <= structure.Wave1End) return false;
                
                // 规则4: 第2浪回撤不超过第1浪的78.6%
                decimal wave2Retracement = (structure.Wave1End - structure.Wave2End) / wave1Length;
                if ((double)wave2Retracement > wave2MaxRetracement) return false;
            }
            else if (structure.Direction == WaveDirection.Down)
            {
                // 下降推动浪的验证规则(方向相反)
                if (structure.Wave2End >= structure.Wave1Start) return false;
                
                decimal wave1Length = structure.Wave1Start - structure.Wave1End;
                decimal wave3Length = structure.Wave2End - structure.Wave3End;
                if (wave3Length < wave1Length * (decimal)wave3MinExtension) return false;
                
                if (structure.Wave4End >= structure.Wave1End) return false;
            }

            return true;
        }

        #endregion

        #region 斐波那契计算

        /// <summary>
        /// 计算斐波那契回撤位
        /// </summary>
        private decimal[] CalculateFibonacciRetracement(decimal high, decimal low)
        {
            decimal range = high - low;
            return new decimal[]
            {
                high - range * (decimal)(double)ArgDic["fib382"],  // 38.2%
                high - range * (decimal)(double)ArgDic["fib500"],  // 50%
                high - range * (decimal)(double)ArgDic["fib618"],  // 61.8%
                high - range * (decimal)(double)ArgDic["fib786"]   // 78.6%
            };
        }

        /// <summary>
        /// 计算斐波那契扩展位
        /// </summary>
        private decimal[] CalculateFibonacciExtension(decimal wave1Start, decimal wave1End, decimal wave2End)
        {
            decimal wave1Length = Math.Abs(wave1End - wave1Start);
            bool isUptrend = wave1End > wave1Start;

            if (isUptrend)
            {
                return new decimal[]
                {
                    wave2End + wave1Length,                                    // 100%
                    wave2End + wave1Length * (decimal)(double)ArgDic["fib1618"], // 161.8%
                    wave2End + wave1Length * (decimal)(double)ArgDic["fib2618"]  // 261.8%
                };
            }
            else
            {
                return new decimal[]
                {
                    wave2End - wave1Length,
                    wave2End - wave1Length * (decimal)(double)ArgDic["fib1618"],
                    wave2End - wave1Length * (decimal)(double)ArgDic["fib2618"]
                };
            }
        }

        #endregion

        #region 交易执行逻辑

        /// <summary>
        /// 执行交易逻辑
        /// </summary>
        private void ExecuteTradeLogic(TradeState state, WaveStructure wave, SkQuote quote,
            TableUnit tu, Period period, int mode, int sendMode, decimal num,
            decimal lossRate, decimal profitRate, int trailingStop, decimal trailingPercent,
            int entryWave, int confirmBars)
        {
            // 空仓状态 - 寻找入场机会
            if (state.Status == 0)
            {
                if (!wave.IsValid) return;

                // 在第3浪启动时做多(上升趋势)
                if (wave.Direction == WaveDirection.Up && wave.CurrentWave == 3 && 
                    entryWave == 3 && mode != 2)
                {
                    // 验证价格突破第1浪高点
                    if (quote.Close > wave.Wave1End)
                    {
                        EnterLong(state, tu, quote, period, sendMode, num, lossRate, profitRate, wave);
                    }
                }
                // 在第5浪启动时做多
                else if (wave.Direction == WaveDirection.Up && wave.CurrentWave == 5 && 
                    entryWave == 5 && mode != 2)
                {
                    if (quote.Close > wave.Wave3End)
                    {
                        EnterLong(state, tu, quote, period, sendMode, num, lossRate, profitRate, wave);
                    }
                }
                // 在C浪结束后做多(调整浪完成)
                else if (wave.Type == WaveType.Corrective && wave.CurrentWave == 8 && 
                    wave.Direction == WaveDirection.Down && mode != 2)
                {
                    // C浪结束，准备新一轮上涨
                    var pivots = wave.Pivots;
                    if (pivots.Count >= 3)
                    {
                        var cWaveEnd = pivots.Last();
                        if (!cWaveEnd.IsHigh && quote.Close > cWaveEnd.Price * 1.01m)
                        {
                            EnterLong(state, tu, quote, period, sendMode, num, lossRate, profitRate, wave);
                        }
                    }
                }
                // 下降趋势做空逻辑
                else if (wave.Direction == WaveDirection.Down && wave.CurrentWave == 3 && 
                    entryWave == 3 && mode != 1)
                {
                    if (quote.Close < wave.Wave1End)
                    {
                        EnterShort(state, tu, quote, period, sendMode, num, lossRate, profitRate, wave);
                    }
                }
                else if (wave.Direction == WaveDirection.Down && wave.CurrentWave == 5 && 
                    entryWave == 5 && mode != 1)
                {
                    if (quote.Close < wave.Wave3End)
                    {
                        EnterShort(state, tu, quote, period, sendMode, num, lossRate, profitRate, wave);
                    }
                }
            }
            // 多头持仓 - 管理仓位
            else if (state.Status == 1)
            {
                ManageLongPosition(state, tu, quote, period, sendMode, trailingStop, trailingPercent, wave);
            }
            // 空头持仓 - 管理仓位
            else if (state.Status == 2)
            {
                ManageShortPosition(state, tu, quote, period, sendMode, trailingStop, trailingPercent, wave);
            }
        }

        /// <summary>
        /// 开多仓
        /// </summary>
        private void EnterLong(TradeState state, TableUnit tu, SkQuote quote, Period period,
            int sendMode, decimal num, decimal lossRate, decimal profitRate, WaveStructure wave)
        {
            state.Status = 1;
            state.Num = num;
            state.EntryPrice = quote.Close;
            state.HighestPrice = quote.Close;
            state.LowestPrice = quote.Close;
            state.EntryWave = wave.CurrentWave;
            state.CurrentStructure = wave;

            // 止损设在第2浪低点下方
            if (wave.Wave2End > 0)
            {
                state.StopLoss = wave.Wave2End * (1 - lossRate / 100);
            }
            else
            {
                state.StopLoss = quote.Close * (1 - lossRate / 100);
            }

            // 止盈目标使用斐波那契扩展
            if (wave.Wave1Start > 0 && wave.Wave1End > 0 && wave.Wave2End > 0)
            {
                var extensions = CalculateFibonacciExtension(wave.Wave1Start, wave.Wave1End, wave.Wave2End);
                state.TakeProfit = extensions[1]; // 161.8%扩展位
            }
            else
            {
                state.TakeProfit = quote.Close * (1 + profitRate / 100);
            }

            Trade(tu.MktSymbol, OrderType.BUY, quote.Close, num, period, sendMode);
        }

        /// <summary>
        /// 开空仓
        /// </summary>
        private void EnterShort(TradeState state, TableUnit tu, SkQuote quote, Period period,
            int sendMode, decimal num, decimal lossRate, decimal profitRate, WaveStructure wave)
        {
            state.Status = 2;
            state.Num = num;
            state.EntryPrice = quote.Close;
            state.HighestPrice = quote.Close;
            state.LowestPrice = quote.Close;
            state.EntryWave = wave.CurrentWave;
            state.CurrentStructure = wave;

            if (wave.Wave2End > 0)
            {
                state.StopLoss = wave.Wave2End * (1 + lossRate / 100);
            }
            else
            {
                state.StopLoss = quote.Close * (1 + lossRate / 100);
            }

            if (wave.Wave1Start > 0 && wave.Wave1End > 0 && wave.Wave2End > 0)
            {
                var extensions = CalculateFibonacciExtension(wave.Wave1Start, wave.Wave1End, wave.Wave2End);
                state.TakeProfit = extensions[1];
            }
            else
            {
                state.TakeProfit = quote.Close * (1 - profitRate / 100);
            }

            Trade(tu.MktSymbol, OrderType.SELL, quote.Close, num, period, sendMode);
        }

        /// <summary>
        /// 管理多头仓位
        /// </summary>
        private void ManageLongPosition(TradeState state, TableUnit tu, SkQuote quote, Period period,
            int sendMode, int trailingStop, decimal trailingPercent, WaveStructure wave)
        {
            // 更新最高价
            if (quote.High > state.HighestPrice)
            {
                state.HighestPrice = quote.High;
                
                // 追踪止损
                if (trailingStop == 1)
                {
                    decimal newStopLoss = state.HighestPrice * (1 - trailingPercent / 100);
                    if (newStopLoss > state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }

            // 止损
            if (quote.Close <= state.StopLoss)
            {
                CloseLong(state, tu, quote, period, sendMode, "止损");
                return;
            }

            // 止盈
            if (quote.Close >= state.TakeProfit)
            {
                CloseLong(state, tu, quote, period, sendMode, "止盈");
                return;
            }

            // 波浪结构变化 - 第5浪完成后平仓
            if (wave.IsValid && wave.Type == WaveType.Impulse && 
                wave.Direction == WaveDirection.Up && wave.CurrentWave >= 5)
            {
                // 检测第5浪顶部形成
                if (wave.Pivots.Count > 0 && wave.Pivots.Last().IsHigh)
                {
                    CloseLong(state, tu, quote, period, sendMode, "第5浪完成");
                }
            }
        }

        /// <summary>
        /// 管理空头仓位
        /// </summary>
        private void ManageShortPosition(TradeState state, TableUnit tu, SkQuote quote, Period period,
            int sendMode, int trailingStop, decimal trailingPercent, WaveStructure wave)
        {
            // 更新最低价
            if (quote.Low < state.LowestPrice)
            {
                state.LowestPrice = quote.Low;
                
                if (trailingStop == 1)
                {
                    decimal newStopLoss = state.LowestPrice * (1 + trailingPercent / 100);
                    if (newStopLoss < state.StopLoss)
                    {
                        state.StopLoss = newStopLoss;
                    }
                }
            }

            // 止损
            if (quote.Close >= state.StopLoss)
            {
                CloseShort(state, tu, quote, period, sendMode, "止损");
                return;
            }

            // 止盈
            if (quote.Close <= state.TakeProfit)
            {
                CloseShort(state, tu, quote, period, sendMode, "止盈");
                return;
            }

            // 第5浪完成后平仓
            if (wave.IsValid && wave.Type == WaveType.Impulse && 
                wave.Direction == WaveDirection.Down && wave.CurrentWave >= 5)
            {
                if (wave.Pivots.Count > 0 && !wave.Pivots.Last().IsHigh)
                {
                    CloseShort(state, tu, quote, period, sendMode, "第5浪完成");
                }
            }
        }

        /// <summary>
        /// 平多仓
        /// </summary>
        private void CloseLong(TradeState state, TableUnit tu, SkQuote quote, Period period,
            int sendMode, string reason)
        {
            var oriNum = state.Num;
            state.Status = 0;
            state.Num = 0;
            Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, quote.Close, oriNum, period, sendMode);
        }

        /// <summary>
        /// 平空仓
        /// </summary>
        private void CloseShort(TradeState state, TableUnit tu, SkQuote quote, Period period,
            int sendMode, string reason)
        {
            var oriNum = state.Num;
            state.Status = 0;
            state.Num = 0;
            Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, quote.Close, oriNum, period, sendMode);
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 计算手数
        /// </summary>
        private decimal CalculateLots(TableUnit tu, SkQuote quote)
        {
            var num = (decimal)ArgDic["lots"];
            var lotsMode = (int)ArgDic["lotsMode"];
            
            if (lotsMode == 1)
            {
                var symbol = GetSymbol(tu.MktSymbol);
                num = (decimal)ArgDic["money"] / (quote.Close * symbol.multiplier * symbol.margin_ratio);
                
                if (symbol.symbol_type == (int)SymbolType.COIN)
                {
                    num = (int)(num * 1000) / 1000.0m;
                }
                else
                {
                    num = (int)num;
                }
            }
            
            return num;
        }

        /// <summary>
        /// 绘制波浪结构
        /// </summary>
        private void PlotWaveStructure(WaveStructure wave, List<WavePivot> pivots)
        {
            if (!wave.IsValid || pivots.Count == 0) return;

            // 绘制当前浪位
            Plot("wave", "CurrentWave", PlotType.LINE, wave.CurrentWave);
            
            // 绘制波浪类型 (1=推动浪, 2=调整浪)
            double waveTypeVal = wave.Type == WaveType.Impulse ? 1 : (wave.Type == WaveType.Corrective ? 2 : 0);
            Plot("wave", "WaveType", PlotType.LINE, waveTypeVal);

            // 绘制趋势方向 (1=上升, -1=下降)
            double directionVal = wave.Direction == WaveDirection.Up ? 1 : (wave.Direction == WaveDirection.Down ? -1 : 0);
            Plot("wave", "Direction", PlotType.LINE, directionVal);

            // 绘制斐波那契关键位
            if (wave.Wave1Start > 0 && wave.Wave1End > 0)
            {
                decimal high = Math.Max(wave.Wave1Start, wave.Wave1End);
                decimal low = Math.Min(wave.Wave1Start, wave.Wave1End);
                var fibLevels = CalculateFibonacciRetracement(high, low);
                
                Plot("fib", "Fib382", PlotType.LINE, (double)fibLevels[0]);
                Plot("fib", "Fib618", PlotType.LINE, (double)fibLevels[2]);
            }
        }

        #endregion
    }
}
