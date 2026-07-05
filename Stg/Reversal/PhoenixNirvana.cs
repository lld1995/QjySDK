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
    /// 凤凰涅槃 (Phoenix Nirvana) —— 凤凰反转的"挂账回本"变体
    ///
    /// 与凤凰反转(PhoenixReversal)的全部区别集中在平仓处理，且所有平仓一律全进全出
    /// （服务端会计模型只在全平时结算利润，部分平仓做成本冲减会扭曲均价——已实测踩坑）：
    ///   1. 任何出场时刻若整体亏损：不发平仓单（仓位原样保留），转入"挂起"状态停止活跃管理；
    ///   2. 挂起处置由 MA5/MA120 正偏离接管——"卖在强处"落袋盈利：
    ///      - 正偏离 ≥ devFullPct：涨势过热，只要整体不亏立即全平；
    ///      - 正偏离 0~devFullPct：要求盈利从 profitTargetPct 随偏离线性递减至0
    ///        （偏离越多，平得越多越快）；
    ///      - 偏离不利（&lt;0）：扛住等待，不平仓；
    ///   3. 持仓/挂起期间同向新信号：加仓延续（盈利中金字塔/挂起中摊低，
    ///      addTimes 控制次数）。
    ///
    /// 风险提示：纯扛单设计，无任何亏损平仓出口——依赖大方向判断，
    /// 单边大趋势中浮亏无上限（回测中 ETH 式行情曾致双向 -11616），务必控制单品种资金占比。
    ///
    /// 设计定位：在使用者已经确定大趋势方向之后运行（通过 mode 参数锁定方向），
    /// 捕捉趋势中的逆向波动衰竭点，顺大势入场。
    /// 只做多时在暴跌衰竭处抄底；只做空时在逼空衰竭处摸顶。
    ///
    /// 双引擎：
    ///   - 狙击引擎：8类独立反转证据共振评分（乖离Z分极值/布林假突破回收/RSI拐头/
    ///     RSI背离(记2分)/恐慌长影线/连阴连阳衰竭/量能高潮/IBS极值），
    ///     共振分达标且价格偏离均值超过1个标准差、并由收盘价确认反转才入场。
    ///     低频高信念：信念仓位（分数越高仓位越大）+ 金字塔摊平。
    ///   - 脉冲引擎：Larry Connors RSI(2) 均值回归 + IBS 双确认 + EMA100趋势过滤，
    ///     抓趋势中的浅回调，TPS式分批建仓（走弱加仓摊低均价）。
    ///     该类系统在股指上有75%+胜率的长期公开验证记录。
    ///
    /// 制度检测（方差比 + OU半衰期）：
    ///   - 方差比VR(5)>1.2 说明行情处于趋势态（动量主导），反转策略此时不开新仓；
    ///   - OU半衰期估计该品种统计意义上的回归速度，动态决定最长持仓时长，
    ///     避免"回归慢的品种被时间止损过早踢出、回归快的品种傻拿"。
    ///
    /// 出场（为持仓周期与盈利空间优化）：
    ///   - 初始止损 = 均价 ± min(atrMult×ATR, maxStopPct%×价格)，浮盈1ATR推保本；
    ///   - 脉冲：强势出场（站上10期均线或RSI2>75）且浮盈达最低利润门槛才走，
    ///     追踪仓模式下只平一半锁胜率，剩余吊灯止损追趋势；
    ///   - 狙击：中轨部分止盈推保本 → 吊灯移动止损 → 对侧轨道清仓；
    ///   - 时间止损（半衰期动态）兜底，止损后冷却防连续挨打。
    ///
    /// 注：入场确认等待机制与卡尔曼滤波Z分曾实现并经40品种消融回测验证为负贡献，已移除。
    /// 微观参数（证据阈值/指标周期等）经消融回测固化为内部常量，不再暴露。
    /// </summary>
    public class PhoenixNirvana : StgBase
    {
        public PhoenixNirvana()
        {
        }

        public PhoenixNirvana(string id) : base(id)
        {
        }

        // ==================== 内部固化常量（经40品种消融回测确定） ====================

        // 布林带与乖离
        private const int BollPeriod = 20;
        private const double StdDevMult = 2.0;
        private const double ZScoreExtreme = 2.0;   // 证据①乖离极值阈值
        private const double ZGate = 1.0;           // 狙击入场位置门槛（偏离均值N个标准差）
        private const double MinBandWidth = 0.008;  // 窄幅死水过滤

        // RSI与背离
        private const int RsiPeriod = 14;
        private const int RsiOversold = 30;
        private const int RsiOverbought = 70;
        private const int DivLookback = 40;         // 背离pivot回溯
        private const int DivRecent = 8;            // 背离时效

        // K线形态与量能
        private const double ShadowRatio = 0.55;    // 恐慌影线占比
        private const int StreakBars = 4;           // 连阴连阳衰竭根数
        private const int VolPeriod = 20;
        private const double VolMult = 1.8;         // 量能高潮倍数
        private const double IbsExtreme = 0.15;     // IBS极值证据阈值

        // 风控
        private const int AtrPeriod = 14;
        private const double BreakEvenAtr = 1.0;    // 浮盈N倍ATR推保本
        private const double PartialRatio = 0.5;    // 部分止盈比例
        private const int SniperMaxAdds = 2;        // 狙击金字塔加仓次数
        private const double AddStepAtr = 1.2;      // 狙击加仓ATR步长
        private const double AddRatio = 0.5;        // 狙击加仓量比例
        private const int SniperMaxHold = 30;       // 狙击最长持仓（制度检测开启时动态覆盖）
        private const int CooldownBars = 3;         // 止损后冷却

        // 脉冲引擎
        private const int PulseRsiPeriod = 2;       // Connors快RSI周期
        private const double PulseRsiExit = 75.0;   // 快RSI强势出场阈值
        private const double PulseIbsBuy = 0.25;    // IBS入场阈值
        private const int PulseExitLen = 10;        // 强势出场均线周期
        private const int PulseTrendLen = 100;      // 趋势过滤EMA周期

        // 制度检测
        private const int QuantWindow = 60;         // 半衰期/方差比估计窗口
        private const double VrMax = 1.2;           // 方差比上限（>该值为趋势态）
        private const double HlHoldMult = 2.0;      // 动态持仓=半衰期×该倍数

        // 通道破位触发
        private const int BreachLen = 20;           // 破位通道回溯K线数（唐奇安同源）

        // 涅槃挂起处理：MA5/MA120偏离度评估
        // 服务端会计模型只在全平时结算利润（部分平仓做成本冲减会扭曲均价），
        // 因此所有平仓一律全进全出，"偏离越多平仓越多"表达为动态可接受平仓线。
        private const int MaFast = 5;               // 快均线周期（短期趋势）
        private const int MaSlow = 120;             // 慢均线周期（长期趋势基准）
        private const int RefillBars = 20;          // 加仓配额恢复周期：快速配额用尽后每N根K线恢复一次信号响应
                                                    // （无硬止损时死仓永不了结，配额须可再生否则信号永久死亡）

        public override StgDesc GetStgDesc()
        {
            var sd = new StgDesc();

            // 引擎
            sd.ArgDic["minScore"] = 3;              // 狙击引擎入场最低共振分（满分9，背离记2分）
            sd.ArgDic["pulseMode"] = 1;             // 脉冲引擎：0关闭 1双确认 2任一
            sd.ArgDic["useBreach"] = 1;             // 通道破位触发：创20根新低/新高即入场（事件驱动，无门槛，证据分调制仓位）
            sd.ArgDic["pulseRsiBuy"] = 10.0;        // 脉冲快RSI超卖入场阈值（参与度由破位触发兜底，脉冲专注高质量信号）
            sd.ArgDic["allowAdds"] = 0;             // 加仓开关：0禁止 1允许（狙击金字塔+脉冲TPS分批）
            sd.ArgDic["pulseRunner"] = 1;           // 脉冲追踪仓开关
            sd.ArgDic["useRegime"] = 1;             // 制度检测（方差比门控+半衰期动态持仓）

            // 涅槃核心：止损转挂起，MA5/MA120正偏离用于"卖在强处"落袋盈利
            sd.ArgDic["devFullPct"] = 5.0;          // 正偏离达该百分比=涨势过热，只要不亏立即全平
            sd.ArgDic["profitTargetPct"] = 5.0;     // 正偏离为0时要求的盈利，随偏离增大线性递减至0（偏离越多平得越快）
            sd.ArgDic["addTimes"] = 5;              // 持仓/挂起期间同向新信号加仓延续的最大次数（0为忽略新信号）

            // 出场风格：涅槃固定为波段型全进全出（服务端会计只在全平时结算利润，部分平仓会扭曲均价）

            // 风控
            sd.ArgDic["atrMult"] = 3.3;             // 初始止损ATR倍数（波段型建议3.3，胜率型建议2.2）
            sd.ArgDic["maxStopPct"] = 6.0;          // 止损距离上限（波段型建议6，胜率型建议4；0不限制）
            sd.ArgDic["trailAtrMult"] = 1.5;        // 吊灯移动止损ATR倍数
            sd.ArgDic["minProfitAtr"] = 0.5;        // 强势出场最低利润门槛（ATR倍数）
            sd.ArgDic["pulseMaxHold"] = 20;         // 脉冲最长持仓K线数（制度检测开启时动态覆盖）

            // 交易模式
            sd.ArgDic["mode"] = 0;                  // 0:双向 1:仅做多 2:仅做空（确定大趋势后设置）
            sd.ArgDic["sendMode"] = 0;              // 发单模式：0立即 1下个开盘

            // 手数控制
            sd.ArgDic["lotsMode"] = 1;              // 0:固定手数 1:固定金额
            sd.ArgDic["lots"] = 1.0m;
            sd.ArgDic["money"] = 10000m;

            // 参数说明
            sd.ArgDescDic["devFullPct"] = new ArgDesc() { Text = "过热偏离%", Explain = "挂起仓的落袋节奏：MA5正向偏离MA120达该百分比=涨势过热，只要整体不亏立即全平（卖在强处）；偏离不利时扛住等待不平仓，默认5", Type = "number" };
            sd.ArgDescDic["profitTargetPct"] = new ArgDesc() { Text = "盈利要求%", Explain = "正偏离为0时要求的盈利百分比，随偏离增大线性递减至0——偏离越多平得越快越多，默认5", Type = "number" };
            sd.ArgDescDic["addTimes"] = new ArgDesc() { Text = "信号加仓次数", Explain = "持仓（活跃或挂起）期间同向新信号加仓延续的快速配额；用尽后每20根K线自动恢复一次响应资格（信号永远不会永久失效），全平后重置，默认5", Type = "number" };
            sd.ArgDescDic["minScore"] = new ArgDesc() { Text = "狙击共振分", Explain = "8类证据共振评分（满分9，背离记2分）达到该值狙击引擎才入场。3=均衡，4=高胜率低频（回测89%），默认3", Type = "number" };
            sd.ArgDescDic["pulseMode"] = new ArgDesc() { Text = "脉冲引擎", Explain = "Connors RSI2+IBS浅回调引擎，交易频率的主要来源", Options = "0:关闭|1:RSI2与IBS双确认|2:任一满足", Type = "select" };
            sd.ArgDescDic["useBreach"] = new ArgDesc() { Text = "通道破位触发", Explain = "收盘创20根K线新低/新高即入场（唐奇安式事件驱动，自适应品种波动分布），不受任何过滤器限制保证参与度，共振证据分调制仓位大小而非阻止入场", Options = "0:关闭|1:开启", Type = "bool" };
            sd.ArgDescDic["pulseRsiBuy"] = new ArgDesc() { Text = "脉冲灵敏度", Explain = "快RSI(2)低于该值触发脉冲做多（空头对称用100-该值）。越大信号越多，默认10", Type = "number" };
            sd.ArgDescDic["allowAdds"] = new ArgDesc() { Text = "加仓开关", Explain = "允许后狙击引擎金字塔摊平最多2次、脉冲引擎TPS分批1次；禁止时全部一次性入场（回测显示禁止更优）", Options = "0:禁止加仓|1:允许加仓", Type = "bool" };
            sd.ArgDescDic["pulseRunner"] = new ArgDesc() { Text = "脉冲追踪仓", Explain = "强势出场只平一半锁胜率，剩余推保本+吊灯止损追趋势（提升收益），全平模式胜率更高", Options = "0:全平|1:留追踪仓", Type = "bool" };
            sd.ArgDescDic["useRegime"] = new ArgDesc() { Text = "制度检测", Explain = "方差比检验判断趋势态/均值回归态，趋势态禁开反转仓；OU半衰期动态决定持仓时长", Options = "0:关闭|1:开启", Type = "bool" };
            sd.ArgDescDic["atrMult"] = new ArgDesc() { Text = "止损ATR倍数", Explain = "初始止损距离=ATR×该倍数。波段型建议3.3（给整段行情空间），胜率型建议2.2", Type = "number" };
            sd.ArgDescDic["maxStopPct"] = new ArgDesc() { Text = "止损上限%", Explain = "止损距离不超过价格的N%，限制高波动品种单笔尾部亏损。波段型建议6，胜率型建议4，0为不限制", Type = "number" };
            sd.ArgDescDic["trailAtrMult"] = new ArgDesc() { Text = "吊灯止损倍数", Explain = "追踪阶段移动止损=极值收盘±ATR×该倍数。小=锁利快，大=让利润跑，默认1.5", Type = "number" };
            sd.ArgDescDic["minProfitAtr"] = new ArgDesc() { Text = "最低利润门槛", Explain = "浮盈不足N倍ATR时不执行强势出场，避免无利润的快进快出，默认0.5", Type = "number" };
            sd.ArgDescDic["pulseMaxHold"] = new ArgDesc() { Text = "脉冲持仓上限", Explain = "脉冲持仓超过N根K线未兑现即离场（制度检测开启时由半衰期动态覆盖），默认20", Type = "number" };
            sd.ArgDescDic["mode"] = new ArgDesc() { Text = "交易方向", Explain = "确定大趋势后锁定方向（顺势单向是本策略的设计场景）", Options = "0:双向|1:仅做多|2:仅做空", Type = "select" };
            sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "下单执行时机", Options = "0:立即|1:下个开盘", Type = "select" };
            sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "手数计算方式", Options = "0:固定手数|1:固定金额", Type = "select" };
            sd.ArgDescDic["lots"] = new ArgDesc() { Text = "手数", Explain = "固定手数", Type = "number" };
            sd.ArgDescDic["money"] = new ArgDesc() { Text = "金额", Explain = "固定金额", Type = "number" };

            sd.MaxSymbolNum = 1000;
            sd.UseGlobalCalc = 0;
            sd.SubChartNum = 2;

            // 颜色配置
            sd.ColorDic["main-upper"] = "#FF5722";       // 上轨橙红
            sd.ColorDic["main-middle"] = "#FF9800";      // 中轨橙色
            sd.ColorDic["main-lower"] = "#2196F3";       // 下轨蓝色
            sd.ColorDic["main-stopLoss"] = "#E74C3C";    // 止损线红色
            sd.ColorDic["sub0-rsi"] = "#9B59B6";         // RSI紫色
            sd.ColorDic["sub1-bullScore"] = "#26A69A";   // 多头共振分青绿
            sd.ColorDic["sub1-bearScore"] = "#EF5350";   // 空头共振分红色

            sd.MidValDic["rsi"] = 50;
            sd.MidValDic["bullScore"] = 3;

            return sd;
        }

        /// <summary>
        /// 持仓状态
        /// </summary>
        private class State
        {
            public int Status { get; set; }             // 0:空仓 1:多头 2:空头
            public int EntryPath { get; set; }          // 1:狙击 2:脉冲
            public decimal Num { get; set; }            // 当前持仓数量
            public decimal BaseNum { get; set; }        // 首仓数量（加仓计算基准）
            public decimal AvgPrice { get; set; }       // 持仓均价
            public decimal LastFillPrice { get; set; }  // 最近一次成交价（加仓步长基准）
            public decimal StopLoss { get; set; }       // 止损价格
            public decimal ExtremeClose { get; set; }   // 入场以来最有利收盘价（吊灯止损基准）
            public int BarsInTrade { get; set; }        // 持仓K线数
            public int AddCount { get; set; }           // 已加仓次数
            public bool PartialExited { get; set; }     // 是否已部分止盈
            public int CooldownRemain { get; set; }     // 剩余冷却K线数
            public int NeedReset { get; set; }          // 止损后的周期复位守卫：1多头待复位 2空头待复位
            public decimal Realized { get; set; }       // 本轮已实现盈亏（诊断与冷却判定用）
            public bool WaitRecover { get; set; }       // 挂起等回本：亏损出场后停止活跃管理，持仓等待
            public int WaitAdds { get; set; }           // 挂起期间同向新信号加仓延续次数
        }

        private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

        // 诊断统计：分引擎胜负、出场原因、持仓周期（回测/复盘用）
        private Dictionary<string, int> _diag = new Dictionary<string, int>();
        private void Diag(string key)
        {
            if (_diag.ContainsKey(key)) _diag[key]++;
            else _diag[key] = 1;
        }
        public Dictionary<string, int> GetDiagCounts() => _diag;

        public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
        {
            base.OnBar(period, tu, isFinal, tq);

            if (!isFinal) return;

            int minDataCount = Math.Max(Math.Max(PulseTrendLen, QuantWindow * 2), DivLookback) + 5;
            if (tu.QuoteList.Count < minDataCount) return;

            // 获取参数
            int minScore = Convert.ToInt32(ArgDic["minScore"]);
            int pulseMode = Convert.ToInt32(ArgDic["pulseMode"]);
            int useBreach = Convert.ToInt32(ArgDic["useBreach"]);
            double pulseRsiBuy = Convert.ToDouble(ArgDic["pulseRsiBuy"]);
            int allowAdds = Convert.ToInt32(ArgDic["allowAdds"]);
            int pulseAdds = allowAdds == 1 ? 1 : 0;
            int sniperAdds = allowAdds == 1 ? SniperMaxAdds : 0;
            int pulseRunner = Convert.ToInt32(ArgDic["pulseRunner"]);
            int useRegime = Convert.ToInt32(ArgDic["useRegime"]);
            double atrMult = Convert.ToDouble(ArgDic["atrMult"]);
            double maxStopPct = Convert.ToDouble(ArgDic["maxStopPct"]);
            double trailAtrMult = Convert.ToDouble(ArgDic["trailAtrMult"]);
            double minProfitAtr = Convert.ToDouble(ArgDic["minProfitAtr"]);
            int pulseMaxHold = Convert.ToInt32(ArgDic["pulseMaxHold"]);
            int exitStyle = 1;  // 涅槃固定波段型全进全出（部分平仓与服务端会计不兼容）
            int sarMode = 0;  // 涅槃双槽位架构不支持SAR反手（会写错槽位），固定关闭
            int mode = Convert.ToInt32(ArgDic["mode"]);
            int sendMode = Convert.ToInt32(ArgDic["sendMode"]);

            var q = tu.QuoteList.Last();
            var prevQ = tu.QuoteList[tu.QuoteList.Count - 2];

            // 计算指标
            var bollList = tu.QuoteList.GetBollingerBands(BollPeriod, StdDevMult).ToList();
            var boll = bollList[bollList.Count - 1];
            var prevBoll = bollList[bollList.Count - 2];
            var rsiList = tu.QuoteList.GetRsi(RsiPeriod).ToList();
            var rsi = rsiList[rsiList.Count - 1];
            var prevRsi = rsiList[rsiList.Count - 2];
            var atrList = tu.QuoteList.GetAtr(AtrPeriod).ToList();
            var atr = atrList[atrList.Count - 1];

            // 脉冲引擎指标：快RSI、强势出场均线、趋势过滤EMA
            double? rsiFast = null;
            decimal pulseExitSma = 0;
            decimal? pulseTrendEma = null;
            if (pulseMode != 0)
            {
                var rsiFastList = tu.QuoteList.GetRsi(PulseRsiPeriod).ToList();
                rsiFast = rsiFastList[rsiFastList.Count - 1].Rsi;
                pulseExitSma = tu.QuoteList.Skip(tu.QuoteList.Count - PulseExitLen).Average(x => x.Close);
                var emaList = tu.QuoteList.GetEma(PulseTrendLen).ToList();
                var emaVal = emaList[emaList.Count - 1].Ema;
                if (emaVal != null) pulseTrendEma = (decimal)emaVal.Value;
            }
            double ibs = q.High > q.Low ? (double)((q.Close - q.Low) / (q.High - q.Low)) : 0.5;

            // 数据有效性检查
            if (boll.UpperBand == null || boll.LowerBand == null || boll.Sma == null) return;
            if (prevBoll.UpperBand == null || prevBoll.LowerBand == null || prevBoll.Sma == null) return;
            if (rsi.Rsi == null || prevRsi.Rsi == null || atr.Atr == null) return;

            decimal upper = (decimal)boll.UpperBand.Value;
            decimal middle = (decimal)boll.Sma.Value;
            decimal lower = (decimal)boll.LowerBand.Value;
            decimal prevUpper = (decimal)prevBoll.UpperBand.Value;
            decimal prevLower = (decimal)prevBoll.LowerBand.Value;
            double rsiValue = rsi.Rsi.Value;
            double prevRsiValue = prevRsi.Rsi.Value;
            decimal atrValue = (decimal)atr.Atr.Value;
            if (atrValue <= 0 || middle <= 0) return;

            // 乖离Z分：布林带半宽即 StdDevMult 个标准差
            decimal halfBand = (upper - lower) / 2m;
            double z = halfBand > 0 ? (double)((q.Close - middle) / halfBand) * StdDevMult : 0;

            // 带宽过滤
            double bandWidth = (double)((upper - lower) / middle);
            bool bandWidthOk = bandWidth >= MinBandWidth;

            // ===== 制度检测：方差比门控 + OU半衰期动态持仓 =====
            bool regimeOk = true;
            int effPulseMaxHold = pulseMaxHold;
            int effMaxHold = SniperMaxHold;
            if (useRegime == 1)
            {
                double vr = VarianceRatio(tu.QuoteList, QuantWindow, 5);
                regimeOk = vr <= VrMax;
                double halfLife = HalfLife(tu.QuoteList, QuantWindow);
                if (halfLife > 0)
                {
                    int dyn = (int)Math.Ceiling(halfLife * HlHoldMult);
                    effPulseMaxHold = Math.Clamp(dyn, 8, 60);
                    effMaxHold = Math.Clamp(dyn * 2, 15, 80);
                }
            }

            // ==================== 八重证据共振评分 ====================

            int bullScore = 0;
            int bearScore = 0;

            // ① 乖离极值
            if (z <= -ZScoreExtreme) bullScore++;
            if (z >= ZScoreExtreme) bearScore++;

            // ② 布林轨道穿刺回收（前一根收于轨道外或刺穿轨道，本根收回轨道内）
            if ((prevQ.Close <= prevLower || prevQ.Low <= prevLower) && q.Close > lower && q.Close < middle) bullScore++;
            if ((prevQ.Close >= prevUpper || prevQ.High >= prevUpper) && q.Close < upper && q.Close > middle) bearScore++;

            // ③ RSI超卖/超买且拐头
            if (rsiValue < RsiOversold && rsiValue > prevRsiValue) bullScore++;
            if (rsiValue > RsiOverbought && rsiValue < prevRsiValue) bearScore++;

            // ④ RSI背离（最强证据，记2分）
            if (HasBullishDivergence(tu.QuoteList, rsiList, DivLookback, DivRecent)) bullScore += 2;
            if (HasBearishDivergence(tu.QuoteList, rsiList, DivLookback, DivRecent)) bearScore += 2;

            // ⑤ 恐慌长影线
            decimal range = q.High - q.Low;
            if (range > 0)
            {
                decimal lowerShadow = Math.Min(q.Open, q.Close) - q.Low;
                decimal upperShadow = q.High - Math.Max(q.Open, q.Close);
                if ((double)(lowerShadow / range) >= ShadowRatio) bullScore++;
                if ((double)(upperShadow / range) >= ShadowRatio) bearScore++;
            }

            // ⑥ 连阴/连阳衰竭（截至前一根的连续同向收盘）
            int downStreak = 0, upStreak = 0;
            for (int i = tu.QuoteList.Count - 2; i > 0; i--)
            {
                if (tu.QuoteList[i].Close < tu.QuoteList[i - 1].Close) downStreak++;
                else break;
            }
            for (int i = tu.QuoteList.Count - 2; i > 0; i--)
            {
                if (tu.QuoteList[i].Close > tu.QuoteList[i - 1].Close) upStreak++;
                else break;
            }
            if (downStreak >= StreakBars) bullScore++;
            if (upStreak >= StreakBars) bearScore++;

            // ⑦ 量能高潮
            var volWindow = tu.QuoteList.Skip(tu.QuoteList.Count - 1 - VolPeriod).Take(VolPeriod).ToList();
            decimal avgVol = volWindow.Count > 0 ? volWindow.Average(x => x.Volume) : 0;
            if (avgVol > 0 && q.Volume >= avgVol * (decimal)VolMult)
            {
                if (q.Close < middle) bullScore++;
                if (q.Close > middle) bearScore++;
            }

            // ⑧ IBS极值
            if (ibs <= IbsExtreme) bullScore++;
            if (ibs >= 1 - IbsExtreme) bearScore++;

            // 绘图
            Plot("main", "upper", PlotType.LINE, (double)upper);
            Plot("main", "middle", PlotType.LINE, (double)middle);
            Plot("main", "lower", PlotType.LINE, (double)lower);
            Plot("sub0", "rsi", PlotType.LINE, rsiValue);
            Plot("sub1", "bullScore", PlotType.LINE, bullScore);
            Plot("sub1", "bearScore", PlotType.LINE, bearScore);

            // 获取或创建状态：双槽位——多头槽与空头槽各自独立运行完整状态机，
            // 一侧长期挂起不会屏蔽另一侧的信号（服务端BuyNum/SellNum本就分开记账）
            var sk = tu.GetStateKey();
            if (!_stateDic.TryGetValue(sk + "_L", out State? sLong) || sLong == null)
            {
                sLong = new State();
                _stateDic[sk + "_L"] = sLong;
            }
            if (!_stateDic.TryGetValue(sk + "_S", out State? sShort) || sShort == null)
            {
                sShort = new State();
                _stateDic[sk + "_S"] = sShort;
            }

            // 绘制止损线（优先画活跃管理中的一侧）
            if (sLong.Status != 0 && !sLong.WaitRecover && sLong.StopLoss > 0)
                Plot("main", "stopLoss", PlotType.LINE, (double)sLong.StopLoss);
            else if (sShort.Status != 0 && !sShort.WaitRecover && sShort.StopLoss > 0)
                Plot("main", "stopLoss", PlotType.LINE, (double)sShort.StopLoss);

            // 持仓时间占比诊断
            Diag("bars_total");
            if (sLong.Status != 0 || sShort.Status != 0) Diag("bars_inpos");

            double devFullPct = Convert.ToDouble(ArgDic["devFullPct"]);
            double profitTargetPct = Convert.ToDouble(ArgDic["profitTargetPct"]);

            // ==================== 信号计算（全状态共享：空仓开仓/持仓加仓/挂起延续） ====================

            // 通道破位（事件驱动，与唐奇安同源）
            bool rawBreachLong = false, rawBreachShort = false;
            if (useBreach == 1)
            {
                decimal chLow = decimal.MaxValue, chHigh = decimal.MinValue;
                for (int i = tu.QuoteList.Count - 1 - BreachLen; i <= tu.QuoteList.Count - 2; i++)
                {
                    if (tu.QuoteList[i].Low < chLow) chLow = tu.QuoteList[i].Low;
                    if (tu.QuoteList[i].High > chHigh) chHigh = tu.QuoteList[i].High;
                }
                rawBreachLong = mode != 2 && q.Close < chLow;
                rawBreachShort = mode != 1 && q.Close > chHigh;
                if (rawBreachLong && rawBreachShort) { rawBreachLong = false; rawBreachShort = false; }
            }

            // 狙击引擎（位置门槛+共振分+收盘确认+制度门控）
            bool rawSniperLong = regimeOk && bandWidthOk && mode != 2
                && z <= -ZGate && bullScore >= minScore && q.Close > prevQ.Close;
            bool rawSniperShort = regimeOk && bandWidthOk && mode != 1
                && z >= ZGate && bearScore >= minScore && q.Close < prevQ.Close;
            if (rawSniperLong && rawSniperShort)
            {
                if (bullScore >= bearScore) rawSniperShort = false;
                else rawSniperLong = false;
            }

            // 脉冲引擎（狙击未触发时）
            bool rawPulseLong = false, rawPulseShort = false;
            if (regimeOk && bandWidthOk && !rawSniperLong && !rawSniperShort && pulseMode != 0 && rsiFast != null)
            {
                bool rsiLongOk = rsiFast.Value <= pulseRsiBuy;
                bool rsiShortOk = rsiFast.Value >= 100 - pulseRsiBuy;
                bool ibsLongOk = ibs <= PulseIbsBuy;
                bool ibsShortOk = ibs >= 1 - PulseIbsBuy;
                bool trendLongOk = pulseTrendEma == null || q.Close > pulseTrendEma.Value;
                bool trendShortOk = pulseTrendEma == null || q.Close < pulseTrendEma.Value;

                rawPulseLong = mode != 2 && trendLongOk
                    && (pulseMode == 1 ? rsiLongOk && ibsLongOk : rsiLongOk || ibsLongOk);
                rawPulseShort = mode != 1 && trendShortOk
                    && (pulseMode == 1 ? rsiShortOk && ibsShortOk : rsiShortOk || ibsShortOk);
                if (rawPulseLong && rawPulseShort) { rawPulseLong = false; rawPulseShort = false; }
            }

            bool sigLong = rawSniperLong || rawPulseLong || rawBreachLong;
            bool sigShort = rawSniperShort || rawPulseShort || rawBreachShort;

            // MA5/MA120偏离度：短期趋势相对长期基准的偏离百分比（挂起处置的核心评估）
            decimal maFast = tu.QuoteList.Skip(tu.QuoteList.Count - MaFast).Average(x => x.Close);
            decimal maSlow = tu.QuoteList.Skip(tu.QuoteList.Count - MaSlow).Average(x => x.Close);
            double devPct = maSlow > 0 ? (double)((maFast - maSlow) / maSlow) * 100.0 : 0;

            // ==================== 交易逻辑（双槽位：多空各自独立的完整状态机） ====================

            void ProcessSide(State s, bool isLong)
            {
                bool sig = isLong ? sigLong : sigShort;
                int needResetDir = isLong ? 1 : 2;
                int score = isLong ? bullScore : bearScore;

                if (s.Status == 0)
                {
                    // 冷却期递减
                    bool inCooldown = s.CooldownRemain > 0;
                    if (inCooldown) s.CooldownRemain--;

                    // 周期复位守卫：止损后同方向再入场需等这一轮走弱/走强周期结束
                    if (s.NeedReset == needResetDir
                        && (rsiFast != null
                            ? (isLong ? rsiFast.Value >= 50 : rsiFast.Value <= 50)
                            : (isLong ? q.Close >= middle : q.Close <= middle)))
                    {
                        s.NeedReset = 0;
                    }

                    if (inCooldown || s.NeedReset == needResetDir) return;

                    // 净额化守卫：对向仓位尚未削完时不开新仓（先抵消，再决定是否开仓）
                    if (isLong ? sShort.Status != 0 : sLong.Status != 0) return;

                    // ===== 入场（优先级：狙击 > 脉冲 > 破位）=====
                    if (isLong ? rawSniperLong : rawSniperShort)
                    {
                        // 信念仓位：共振分每超出门槛1分仓位加25%，封顶2倍
                        var baseNum = CalculateLots(tu, q);
                        decimal factor = Math.Min(1m + 0.25m * (score - minScore), 2m);
                        decimal num = NormalizeLots(tu, baseNum * factor);
                        if (num <= 0) return;
                        OpenPosition(s, tu, q, isLong, 1, num, atrValue, atrMult, maxStopPct, period, sendMode);
                    }
                    else if (isLong ? rawPulseLong : rawPulseShort)
                    {
                        // TPS分批：首仓为总仓位的 1/(1+加仓次数)
                        var baseNum = CalculateLots(tu, q);
                        decimal num = pulseAdds > 0 ? NormalizeLots(tu, baseNum / (1 + pulseAdds)) : baseNum;
                        if (num <= 0) return;
                        OpenPosition(s, tu, q, isLong, 2, num, atrValue, atrMult, maxStopPct, period, sendMode);
                    }
                    else if (isLong ? rawBreachLong : rawBreachShort)
                    {
                        // 破位入场：证据分调制仓位（凯利式——弱信号调仓位而非挡参与）
                        var baseNum = CalculateLots(tu, q);
                        decimal factor = Math.Min(1m + 0.25m * score, 2m);
                        decimal num = NormalizeLots(tu, baseNum * factor);
                        if (num <= 0) return;
                        Diag("breach_entry");
                        OpenPosition(s, tu, q, isLong, 2, num, atrValue, atrMult, maxStopPct, period, sendMode);
                    }
                }
                else if (s.WaitRecover)
                {
                    ProcessWaitRecover(s, tu, q, isLong, sig, devPct, devFullPct, profitTargetPct,
                        atrValue, atrMult, maxStopPct, period, sendMode);
                }
                else
                {
                    // ===== 活跃持仓期的同向新信号：仅在整体盈利时金字塔加仓 =====
                    // （盈利中顺势加码；逆势摊低只允许在挂起状态由偏离度护栏管控，防马丁式滚雪球）
                    bool addSpaced = Math.Abs(q.Close - s.LastFillPrice) >= atrValue;
                    bool addBudgetOk = s.WaitAdds < Convert.ToInt32(ArgDic["addTimes"]) || s.BarsInTrade >= RefillBars;
                    if (sig && addSpaced && InProfit(s, q, isLong) && addBudgetOk)
                    {
                        var addNum = CalculateLots(tu, q);
                        if (addNum > 0)
                        {
                            s.AvgPrice = (s.AvgPrice * s.Num + q.Close * addNum) / (s.Num + addNum);
                            s.Num += addNum;
                            s.BaseNum = addNum;
                            s.LastFillPrice = q.Close;
                            s.BarsInTrade = 0;
                            s.WaitAdds++;
                            // 止损重锚到新价位（均价已摊低，风控随行情更新）
                            decimal dist = StopDistance(q.Close, atrValue, atrMult, maxStopPct);
                            s.StopLoss = isLong ? q.Close - dist : q.Close + dist;
                            Diag("signal_add");
                            Trade(tu.MktSymbol, isLong ? OrderType.BUY : OrderType.SELL, q.Close, addNum, period, sendMode);
                            return;
                        }
                    }

                    if (exitStyle == 1)
                        ProcessSwingExit(s, tu, q, isLong, rsiFast, upper, lower,
                            atrValue, atrMult, maxStopPct, rsiValue, pulseAdds, sniperAdds,
                            sarMode, mode, sendMode, period);
                    else if (s.EntryPath == 2)
                        ProcessPulseExit(s, tu, q, isLong, rsiFast, pulseExitSma, effPulseMaxHold,
                            pulseAdds, pulseRunner, pulseRsiBuy, upper, lower, atrValue, atrMult, maxStopPct,
                            trailAtrMult, minProfitAtr, sendMode, period);
                    else if (isLong)
                        ProcessLongPosition(s, tu, q, upper, middle, atrValue, atrMult, maxStopPct,
                            trailAtrMult, effMaxHold, rsiValue, sniperAdds, sendMode, period);
                    else
                        ProcessShortPosition(s, tu, q, lower, middle, atrValue, atrMult, maxStopPct,
                            trailAtrMult, effMaxHold, rsiValue, sniperAdds, sendMode, period);
                }
            }

            // ===== 信号净额化（逐份抵消）：反向信号按其开仓量削减对向持仓一份 =====
            // 挂起仓可能多次加仓累积，不一口气全平——每个反向信号消化一份，
            // 对向仓位削完之前本方向不开新仓（先抵消，再决定是否开仓）
            if (sigLong && sShort.Status != 0) NetReduce(sShort, tu, q, false, period, sendMode);
            if (sigShort && sLong.Status != 0) NetReduce(sLong, tu, q, true, period, sendMode);

            // 多头槽与空头槽各跑一遍：一侧挂起绝不屏蔽另一侧的信号（mode方向锁已在raw信号中生效）
            ProcessSide(sLong, true);
            ProcessSide(sShort, false);
        }

        /// <summary>
        /// 净额化削减：对向信号出现时，按该信号的开仓量削减本槽仓位一份（盈亏照实了结）；
        /// 削减至清空时结束本轮持仓。累积的挂起大仓由反向信号逐份消化。
        /// </summary>
        private void NetReduce(State s, TableUnit tu, SkQuote q, bool isLong, Period period, int sendMode)
        {
            if (s.Num <= 0)
            {
                ResetState(s);
                return;
            }

            var unit = CalculateLots(tu, q);
            decimal cover = Math.Min(unit, s.Num);
            if (cover <= 0) return;

            Trade(tu.MktSymbol, isLong ? OrderType.SELL_TO_COVER : OrderType.BUY_TO_COVER,
                q.Close, cover, period, sendMode);
            s.Realized += isLong ? (q.Close - s.AvgPrice) * cover : (s.AvgPrice - q.Close) * cover;
            s.Num -= cover;
            Diag("net_reduce");

            if (s.Num < 0.001m)
            {
                Diag("exit_net");
                Diag((s.EntryPath == 2 ? "pulse" : "sniper") + (s.Realized > 0 ? "_win" : "_loss"));
                if (_diag.ContainsKey("hold_sum")) _diag["hold_sum"] += s.BarsInTrade;
                else _diag["hold_sum"] = s.BarsInTrade;
                Diag("hold_n");
                ResetState(s);
            }
        }

        /// <summary>
        /// 当前持仓整体是否盈利（已实现+浮动）
        /// </summary>
        private bool InProfit(State s, SkQuote q, bool isLong)
        {
            decimal unreal = isLong ? (q.Close - s.AvgPrice) * s.Num : (s.AvgPrice - q.Close) * s.Num;
            return s.Realized + unreal > 0;
        }

        /// <summary>
        /// 挂起处置（MA5/MA120正偏离用于"卖在强处"落袋盈利，全部为全进全出平仓）：
        ///   正偏离 ≥ devFullPct → 涨势过热，只要整体不亏立即全平；
        ///   正偏离 0~devFullPct → 要求盈利从 profitTargetPct 随偏离线性递减至0
        ///   （偏离越多，平得越多越快）；
        ///   偏离不利（&lt;0）→ 扛住等待，不平仓；
        ///   同向新信号 → 加仓延续（摊低均价，恢复活跃管理）。
        /// 注意：无任何亏损平仓出口（纯扛单），单边大趋势中浮亏无上限。
        /// </summary>
        private void ProcessWaitRecover(State s, TableUnit tu, SkQuote q, bool isLong, bool sameDirSignal,
            double devPct, double devFullPct, double profitTargetPct,
            decimal atrValue, double atrMult, double maxStopPct, Period period, int sendMode)
        {
            s.BarsInTrade++;

            // 有利偏离度：多头视角MA5高于MA120为正；空头对称取反
            double favorDev = isLong ? devPct : -devPct;

            // 同向新信号：加仓延续——摊低均价并恢复活跃管理（服务端同仓位自动合并均价）
            // 间距保护（防连续K线堆仓）：价格比上次成交差0.5×ATR，或距上次成交已过5根K线，二者满足其一即可响应
            bool renewSpaced = (isLong
                    ? q.Close <= s.LastFillPrice - atrValue * 0.5m
                    : q.Close >= s.LastFillPrice + atrValue * 0.5m)
                || s.BarsInTrade >= 5;
            // 配额可再生：快速配额(addTimes)用尽后每RefillBars根K线恢复一次响应，信号永远不会永久死亡
            bool renewBudgetOk = s.WaitAdds < Convert.ToInt32(ArgDic["addTimes"]) || s.BarsInTrade >= RefillBars;
            if (sameDirSignal && renewSpaced && renewBudgetOk)
            {
                var addNum = CalculateLots(tu, q);
                if (addNum > 0)
                {
                    s.AvgPrice = (s.AvgPrice * s.Num + q.Close * addNum) / (s.Num + addNum);
                    s.Num += addNum;
                    s.BaseNum = addNum;
                    s.LastFillPrice = q.Close;
                    s.ExtremeClose = q.Close;
                    s.BarsInTrade = 0;
                    s.AddCount = 0;
                    s.PartialExited = false;
                    s.WaitAdds++;
                    s.WaitRecover = false;
                    s.EntryPath = 2;
                    decimal dist = StopDistance(q.Close, atrValue, atrMult, maxStopPct);
                    s.StopLoss = isLong ? q.Close - dist : q.Close + dist;
                    Diag("renew");
                    Trade(tu.MktSymbol, isLong ? OrderType.BUY : OrderType.SELL, q.Close, addNum, period, sendMode);
                    return;
                }
            }

            // 偏离不利：扛住等待，不平仓
            if (favorDev < 0) return;

            // 正偏离落袋："卖在强处"——要求盈利从 profitTargetPct 随偏离线性递减至0，
            // 偏离越多平得越快；偏离≥devFullPct 时只要不亏即全平
            double reqPct = devFullPct > 0
                ? profitTargetPct * Math.Max(0.0, 1.0 - favorDev / devFullPct)
                : 0.0;
            bool hit = isLong
                ? q.Close >= s.AvgPrice * (1m + (decimal)reqPct / 100m)
                : q.Close <= s.AvgPrice * (1m - (decimal)reqPct / 100m);
            if (hit && InProfit(s, q, isLong))
            {
                FinalizeClose(s, tu, isLong, q.Close, "dev_take", period, sendMode, 0);
            }
        }

        /// <summary>
        /// 建仓并设置初始止损
        /// </summary>
        private void OpenPosition(State s, TableUnit tu, SkQuote q, bool isLong, int path, decimal num,
            decimal atrValue, double atrMult, double maxStopPct, Period period, int sendMode)
        {
            s.Status = isLong ? 1 : 2;
            s.EntryPath = path;
            s.Num = num;
            s.BaseNum = num;
            s.AvgPrice = q.Close;
            s.LastFillPrice = q.Close;
            s.ExtremeClose = q.Close;
            s.BarsInTrade = 0;
            s.AddCount = 0;
            s.PartialExited = false;
            decimal dist = StopDistance(q.Close, atrValue, atrMult, maxStopPct);
            s.StopLoss = isLong ? q.Close - dist : q.Close + dist;
            Diag(path == 1 ? "sniper_entry" : "pulse_entry");
            Trade(tu.MktSymbol, isLong ? OrderType.BUY : OrderType.SELL, q.Close, num, period, sendMode);
        }

        /// <summary>
        /// 狙击多头持仓管理：止损 → 时间止损 → 金字塔摊平 → 保本推进 → 中轨部分止盈 → 吊灯移动止损 → 对侧轨道清仓
        /// </summary>
        private void ProcessLongPosition(State s, TableUnit tu, SkQuote q,
            decimal upper, decimal middle, decimal atrValue, double atrMult, double maxStopPct,
            double trailAtrMult, int maxHoldBars, double rsiValue, int sniperAdds, int sendMode, Period period)
        {
            s.BarsInTrade++;
            if (q.Close > s.ExtremeClose) s.ExtremeClose = q.Close;

            // 止损（触发后进入冷却）
            if (q.Close <= s.StopLoss)
            {
                FinalizeClose(s, tu, true, q.Close, "stop", period, sendMode, CooldownBars);
                return;
            }

            // 时间止损：回归论点失效，认错离场
            if (!s.PartialExited && maxHoldBars > 0 && s.BarsInTrade >= maxHoldBars)
            {
                FinalizeClose(s, tu, true, q.Close, "time", period, sendMode, 0);
                return;
            }

            // 金字塔摊平：价格再度极端化且RSI仍超卖
            if (!s.PartialExited && s.AddCount < sniperAdds
                && q.Close <= s.LastFillPrice - atrValue * (decimal)AddStepAtr
                && rsiValue < RsiOversold)
            {
                decimal addNum = NormalizeLots(tu, s.BaseNum * (decimal)AddRatio);
                if (addNum > 0)
                {
                    s.AvgPrice = (s.AvgPrice * s.Num + q.Close * addNum) / (s.Num + addNum);
                    s.Num += addNum;
                    s.LastFillPrice = q.Close;
                    s.AddCount++;
                    s.StopLoss = s.AvgPrice - StopDistance(s.AvgPrice, atrValue, atrMult, maxStopPct);
                    Diag("sniper_add");
                    Trade(tu.MktSymbol, OrderType.BUY, q.Close, addNum, period, sendMode);
                }
            }

            // 半程保本：浮盈达N倍ATR后止损推至持仓均价
            if (q.Close >= s.AvgPrice + atrValue * (decimal)BreakEvenAtr && s.AvgPrice > s.StopLoss)
            {
                s.StopLoss = s.AvgPrice;
            }

            // 部分止盈：触及中轨了结一半，止损推至保本
            if (!s.PartialExited && q.Close >= middle)
            {
                decimal exitNum = NormalizeLots(tu, s.Num * (decimal)PartialRatio);
                if (exitNum > 0 && exitNum < s.Num)
                {
                    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, exitNum, period, sendMode);
                    s.Realized += (q.Close - s.AvgPrice) * exitNum;
                    s.Num -= exitNum;
                    s.PartialExited = true;
                    s.StopLoss = Math.Max(s.StopLoss, s.AvgPrice);
                    Diag("exit_partial");
                }
                else
                {
                    FinalizeClose(s, tu, true, q.Close, "target", period, sendMode, 0);
                    return;
                }
            }

            // 部分止盈后：吊灯式移动止损锁住利润
            if (s.PartialExited)
            {
                decimal chandelier = s.ExtremeClose - atrValue * (decimal)trailAtrMult;
                if (chandelier > s.StopLoss) s.StopLoss = chandelier;
            }

            // 对侧轨道清仓
            if (q.Close >= upper && s.Num > 0)
            {
                FinalizeClose(s, tu, true, q.Close, "target", period, sendMode, 0);
            }
        }

        /// <summary>
        /// 狙击空头持仓管理（与多头对称）
        /// </summary>
        private void ProcessShortPosition(State s, TableUnit tu, SkQuote q,
            decimal lower, decimal middle, decimal atrValue, double atrMult, double maxStopPct,
            double trailAtrMult, int maxHoldBars, double rsiValue, int sniperAdds, int sendMode, Period period)
        {
            s.BarsInTrade++;
            if (s.ExtremeClose == 0 || q.Close < s.ExtremeClose) s.ExtremeClose = q.Close;

            // 止损（触发后进入冷却）
            if (q.Close >= s.StopLoss)
            {
                FinalizeClose(s, tu, false, q.Close, "stop", period, sendMode, CooldownBars);
                return;
            }

            // 时间止损
            if (!s.PartialExited && maxHoldBars > 0 && s.BarsInTrade >= maxHoldBars)
            {
                FinalizeClose(s, tu, false, q.Close, "time", period, sendMode, 0);
                return;
            }

            // 金字塔摊平：价格再度极端化且RSI仍超买
            if (!s.PartialExited && s.AddCount < sniperAdds
                && q.Close >= s.LastFillPrice + atrValue * (decimal)AddStepAtr
                && rsiValue > RsiOverbought)
            {
                decimal addNum = NormalizeLots(tu, s.BaseNum * (decimal)AddRatio);
                if (addNum > 0)
                {
                    s.AvgPrice = (s.AvgPrice * s.Num + q.Close * addNum) / (s.Num + addNum);
                    s.Num += addNum;
                    s.LastFillPrice = q.Close;
                    s.AddCount++;
                    s.StopLoss = s.AvgPrice + StopDistance(s.AvgPrice, atrValue, atrMult, maxStopPct);
                    Diag("sniper_add");
                    Trade(tu.MktSymbol, OrderType.SELL, q.Close, addNum, period, sendMode);
                }
            }

            // 半程保本
            if (q.Close <= s.AvgPrice - atrValue * (decimal)BreakEvenAtr && s.AvgPrice < s.StopLoss)
            {
                s.StopLoss = s.AvgPrice;
            }

            // 部分止盈：触及中轨了结一半，止损推至保本
            if (!s.PartialExited && q.Close <= middle)
            {
                decimal exitNum = NormalizeLots(tu, s.Num * (decimal)PartialRatio);
                if (exitNum > 0 && exitNum < s.Num)
                {
                    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, exitNum, period, sendMode);
                    s.Realized += (s.AvgPrice - q.Close) * exitNum;
                    s.Num -= exitNum;
                    s.PartialExited = true;
                    s.StopLoss = Math.Min(s.StopLoss, s.AvgPrice);
                    Diag("exit_partial");
                }
                else
                {
                    FinalizeClose(s, tu, false, q.Close, "target", period, sendMode, 0);
                    return;
                }
            }

            // 部分止盈后：吊灯式移动止损
            if (s.PartialExited)
            {
                decimal chandelier = s.ExtremeClose + atrValue * (decimal)trailAtrMult;
                if (chandelier < s.StopLoss) s.StopLoss = chandelier;
            }

            // 对侧轨道清仓
            if (q.Close <= lower && s.Num > 0)
            {
                FinalizeClose(s, tu, false, q.Close, "target", period, sendMode, 0);
            }
        }

        /// <summary>
        /// 脉冲持仓管理：止损 → 追踪仓吊灯推进 → TPS分批加仓 → 利润门槛过滤的强势出场 → 时间止损。
        /// 入场于短线恐慌，出场于强势恢复；追踪仓模式下强势时只平一半，剩余追趋势。
        /// </summary>
        private void ProcessPulseExit(State s, TableUnit tu, SkQuote q, bool isLong,
            double? rsiFast, decimal pulseExitSma, int pulseMaxHold,
            int pulseAdds, int pulseRunner, double pulseRsiBuy, decimal upper, decimal lower,
            decimal atrValue, double atrMult, double maxStopPct, double trailAtrMult, double minProfitAtr,
            int sendMode, Period period)
        {
            s.BarsInTrade++;
            if (isLong ? q.Close > s.ExtremeClose : q.Close < s.ExtremeClose) s.ExtremeClose = q.Close;

            // 止损（触发后进入冷却；追踪仓阶段的止损通常已是盈利离场）
            bool stopHit = isLong ? q.Close <= s.StopLoss : q.Close >= s.StopLoss;
            if (stopHit)
            {
                FinalizeClose(s, tu, isLong, q.Close, "stop", period, sendMode, CooldownBars);
                return;
            }

            // ===== 追踪仓阶段：吊灯止损推进 + 对侧轨道清仓 =====
            if (s.PartialExited)
            {
                decimal chandelier = isLong
                    ? s.ExtremeClose - atrValue * (decimal)trailAtrMult
                    : s.ExtremeClose + atrValue * (decimal)trailAtrMult;
                if (isLong ? chandelier > s.StopLoss : chandelier < s.StopLoss)
                    s.StopLoss = chandelier;

                bool target = isLong ? q.Close >= upper : q.Close <= lower;
                if (target)
                    FinalizeClose(s, tu, isLong, q.Close, "target", period, sendMode, 0);
                return;
            }

            // TPS式分批加仓：价格较上次成交继续走弱且快RSI仍在弱势区
            if (s.AddCount < pulseAdds
                && (isLong ? q.Close < s.LastFillPrice : q.Close > s.LastFillPrice)
                && rsiFast != null && (isLong ? rsiFast.Value < 50 : rsiFast.Value > 50))
            {
                decimal addNum = s.BaseNum;
                if (addNum > 0)
                {
                    s.AvgPrice = (s.AvgPrice * s.Num + q.Close * addNum) / (s.Num + addNum);
                    s.Num += addNum;
                    s.LastFillPrice = q.Close;
                    s.AddCount++;
                    decimal stopDist = StopDistance(s.AvgPrice, atrValue, atrMult, maxStopPct);
                    s.StopLoss = isLong ? s.AvgPrice - stopDist : s.AvgPrice + stopDist;
                    Diag("pulse_add");
                    Trade(tu.MktSymbol, isLong ? OrderType.BUY : OrderType.SELL, q.Close, addNum, period, sendMode);
                }
            }

            // 强势出场：收盘站上/跌破短均线，或快RSI到达对侧极值；
            // 最低利润门槛：浮盈不足时不出场，避免无利润的快进快出（时间止损兜底）
            bool profitOk = isLong
                ? q.Close >= s.AvgPrice + atrValue * (decimal)minProfitAtr
                : q.Close <= s.AvgPrice - atrValue * (decimal)minProfitAtr;
            bool strength = isLong ? q.Close >= pulseExitSma : q.Close <= pulseExitSma;
            if (!strength && rsiFast != null)
                strength = isLong ? rsiFast.Value >= PulseRsiExit : rsiFast.Value <= 100 - PulseRsiExit;
            strength = strength && profitOk;

            // 时间止损
            bool timeout = pulseMaxHold > 0 && s.BarsInTrade >= pulseMaxHold;

            if (strength)
            {
                // 追踪仓模式：只平一半锁定胜率，剩余推保本+吊灯追趋势
                decimal exitNum = Math.Round(s.Num * 0.5m, 3);
                if (pulseRunner == 1 && exitNum > 0 && exitNum < s.Num)
                {
                    Trade(tu.MktSymbol, isLong ? OrderType.SELL_TO_COVER : OrderType.BUY_TO_COVER, q.Close, exitNum, period, sendMode);
                    s.Realized += isLong ? (q.Close - s.AvgPrice) * exitNum : (s.AvgPrice - q.Close) * exitNum;
                    s.Num -= exitNum;
                    s.PartialExited = true;
                    s.ExtremeClose = q.Close;
                    if (isLong ? s.AvgPrice > s.StopLoss : s.AvgPrice < s.StopLoss)
                        s.StopLoss = s.AvgPrice;
                    Diag("exit_partial");
                }
                else
                {
                    FinalizeClose(s, tu, isLong, q.Close, "strength", period, sendMode, 0);
                }
            }
            else if (timeout)
            {
                FinalizeClose(s, tu, isLong, q.Close, "time", period, sendMode, 0);
            }
        }

        /// <summary>
        /// 波段型出场（唐奇安反转式）：持满仓位，只在止损或触及对侧布林轨道时离场。
        /// 无部分止盈、无吊灯、无保本推——让单笔利润吃满整段回归行情。
        /// 胜率低于胜率型出场，但单笔盈亏比大。保留两引擎各自的加仓逻辑。
        /// </summary>
        private void ProcessSwingExit(State s, TableUnit tu, SkQuote q, bool isLong,
            double? rsiFast, decimal upper, decimal lower,
            decimal atrValue, double atrMult, double maxStopPct, double rsiValue, int pulseAdds, int sniperAdds,
            int sarMode, int mode, int sendMode, Period period)
        {
            s.BarsInTrade++;

            // 止损（触发后进入冷却）
            bool stopHit = isLong ? q.Close <= s.StopLoss : q.Close >= s.StopLoss;
            if (stopHit)
            {
                FinalizeClose(s, tu, isLong, q.Close, "stop", period, sendMode, CooldownBars);
                return;
            }

            // 加仓：狙击金字塔（ATR步长+RSI仍极端）/ 脉冲TPS（走弱+快RSI仍弱势）
            bool addOk;
            int maxAdds;
            decimal addNum;
            if (s.EntryPath == 1)
            {
                maxAdds = sniperAdds;
                addNum = NormalizeLots(tu, s.BaseNum * (decimal)AddRatio);
                addOk = (isLong
                        ? q.Close <= s.LastFillPrice - atrValue * (decimal)AddStepAtr
                        : q.Close >= s.LastFillPrice + atrValue * (decimal)AddStepAtr)
                    && (isLong ? rsiValue < RsiOversold : rsiValue > RsiOverbought);
            }
            else
            {
                maxAdds = pulseAdds;
                addNum = s.BaseNum;
                addOk = (isLong ? q.Close < s.LastFillPrice : q.Close > s.LastFillPrice)
                    && rsiFast != null && (isLong ? rsiFast.Value < 50 : rsiFast.Value > 50);
            }
            if (s.AddCount < maxAdds && addOk && addNum > 0)
            {
                s.AvgPrice = (s.AvgPrice * s.Num + q.Close * addNum) / (s.Num + addNum);
                s.Num += addNum;
                s.LastFillPrice = q.Close;
                s.AddCount++;
                decimal stopDist = StopDistance(s.AvgPrice, atrValue, atrMult, maxStopPct);
                s.StopLoss = isLong ? s.AvgPrice - stopDist : s.AvgPrice + stopDist;
                Diag(s.EntryPath == 1 ? "sniper_add" : "pulse_add");
                Trade(tu.MktSymbol, isLong ? OrderType.BUY : OrderType.SELL, q.Close, addNum, period, sendMode);
            }

            // 对侧轨道清仓：吃满整段回归
            bool bandHit = isLong ? q.Close >= upper : q.Close <= lower;
            if (bandHit)
            {
                FinalizeClose(s, tu, isLong, q.Close, "target", period, sendMode, 0);
                // 涅槃：若目标位仍亏损被转入挂起（仓位未平），不可反手
                if (s.Status != 0) return;

                // SAR反手：目标出场即刻反向开仓，永远在场吃回程（受mode方向约束，单向模式下不反手）
                bool canFlip = sarMode == 1 && (isLong ? mode != 1 : mode != 2);
                if (canFlip)
                {
                    var num = CalculateLots(tu, q);
                    if (num > 0)
                    {
                        Diag("flip_entry");
                        OpenPosition(s, tu, q, !isLong, 2, num, atrValue, atrMult, maxStopPct, period, sendMode);
                    }
                }
                return;
            }

            // 超长持仓兜底
            if (s.BarsInTrade >= 80)
            {
                FinalizeClose(s, tu, isLong, q.Close, "time", period, sendMode, 0);
            }
        }

        /// <summary>
        /// 涅槃平仓拦截（所有出场的唯一汇聚点）：
        /// 整体亏损 → 不平仓，转挂账等待回本；整体盈利 → 正常平仓 + 同向挂账按价位评估去库存
        /// </summary>
        private void FinalizeClose(State s, TableUnit tu, bool isLong, decimal price, string reason,
            Period period, int sendMode, int cooldownBars)
        {
            // 涅槃铁律：亏损状态不平仓，并入同方向挂账池（均价核算）
            decimal unreal = s.Num > 0
                ? (isLong ? (price - s.AvgPrice) * s.Num : (s.AvgPrice - price) * s.Num)
                : 0;
            if (s.Realized + unreal <= 0)
            {
                // 挂起：不发任何单（仓位原样保留在服务端），停止活跃管理，转入偏离度处置状态
                s.WaitRecover = true;
                s.StopLoss = 0;
                s.ExtremeClose = price;
                Diag("hang");
                return;
            }

            if (s.Num > 0)
            {
                Trade(tu.MktSymbol, isLong ? OrderType.SELL_TO_COVER : OrderType.BUY_TO_COVER, price, s.Num, period, sendMode);
                s.Realized += unreal;
            }
            Diag("exit_" + reason);
            Diag((s.EntryPath == 2 ? "pulse" : "sniper") + "_win");
            if (_diag.ContainsKey("hold_sum")) _diag["hold_sum"] += s.BarsInTrade;
            else _diag["hold_sum"] = s.BarsInTrade;
            Diag("hold_n");
            ResetState(s);
        }


        /// <summary>
        /// OU过程半衰期：Δy对y(t-1)做OLS回归，系数b&lt;0时半衰期=-ln2/ln(1+b)。
        /// 给出该品种当前统计意义上"偏离回归一半所需的K线数"，返回-1表示无均值回归特征。
        /// </summary>
        private double HalfLife(List<SkQuote> quotes, int window)
        {
            int n = Math.Min(window, quotes.Count - 1);
            if (n < 20) return -1;
            int start = quotes.Count - 1 - n;

            double meanY = 0, meanD = 0;
            for (int i = start; i < quotes.Count - 1; i++)
            {
                meanY += (double)quotes[i].Close;
                meanD += (double)(quotes[i + 1].Close - quotes[i].Close);
            }
            meanY /= n;
            meanD /= n;

            double cov = 0, varY = 0;
            for (int i = start; i < quotes.Count - 1; i++)
            {
                double y = (double)quotes[i].Close - meanY;
                double d = (double)(quotes[i + 1].Close - quotes[i].Close) - meanD;
                cov += y * d;
                varY += y * y;
            }
            if (varY <= 0) return -1;
            double b = cov / varY;
            if (b >= 0) return -1;
            return -Math.Log(2) / Math.Log(1 + Math.Max(b, -0.99));
        }

        /// <summary>
        /// 方差比检验 VR(k)：k期收益方差 / (k×单期收益方差)。
        /// 随机游走≈1，&lt;1为均值回归态（适合反转），&gt;1为趋势态（动量主导，反转危险）。
        /// </summary>
        private double VarianceRatio(List<SkQuote> quotes, int window, int k)
        {
            int n = Math.Min(window, quotes.Count - 1);
            if (n < k * 4) return 1;
            int start = quotes.Count - 1 - n;

            var rets = new List<double>(n);
            for (int i = start; i < quotes.Count - 1; i++)
            {
                double p0 = (double)quotes[i].Close;
                double p1 = (double)quotes[i + 1].Close;
                if (p0 > 0 && p1 > 0) rets.Add(Math.Log(p1 / p0));
            }
            if (rets.Count < k * 4) return 1;

            double m = rets.Average();
            double v1 = rets.Sum(r => (r - m) * (r - m)) / (rets.Count - 1);
            if (v1 <= 0) return 1;

            var retsK = new List<double>(rets.Count - k + 1);
            for (int i = 0; i + k <= rets.Count; i++)
            {
                double sum = 0;
                for (int j = 0; j < k; j++) sum += rets[i + j];
                retsK.Add(sum);
            }
            double mk = retsK.Average();
            double vk = retsK.Sum(r => (r - mk) * (r - mk)) / Math.Max(1, retsK.Count - 1);
            return vk / (k * v1);
        }

        /// <summary>
        /// 止损距离：ATR倍数与价格百分比上限取小值，限制高波动品种的单笔尾部亏损
        /// </summary>
        private decimal StopDistance(decimal price, decimal atrValue, double atrMult, double maxStopPct)
        {
            decimal dist = atrValue * (decimal)atrMult;
            if (maxStopPct > 0)
            {
                decimal cap = price * (decimal)maxStopPct / 100m;
                if (cap < dist) dist = cap;
            }
            return dist;
        }

        /// <summary>
        /// 底背离检测：最近两个价格pivot低点，价格创新低而RSI抬高
        /// </summary>
        private bool HasBullishDivergence(List<SkQuote> quotes, List<RsiResult> rsiList, int lookback, int recent)
        {
            var pivots = FindPivots(quotes, false, lookback);
            if (pivots.Count < 2) return false;
            int p1 = pivots[pivots.Count - 2];
            int p2 = pivots[pivots.Count - 1];
            if (quotes.Count - 1 - p2 > recent) return false;
            if (rsiList[p1].Rsi == null || rsiList[p2].Rsi == null) return false;
            return quotes[p2].Low < quotes[p1].Low
                && rsiList[p2].Rsi.Value > rsiList[p1].Rsi.Value + 1
                && rsiList[p2].Rsi.Value < 50;
        }

        /// <summary>
        /// 顶背离检测：最近两个价格pivot高点，价格创新高而RSI走低
        /// </summary>
        private bool HasBearishDivergence(List<SkQuote> quotes, List<RsiResult> rsiList, int lookback, int recent)
        {
            var pivots = FindPivots(quotes, true, lookback);
            if (pivots.Count < 2) return false;
            int p1 = pivots[pivots.Count - 2];
            int p2 = pivots[pivots.Count - 1];
            if (quotes.Count - 1 - p2 > recent) return false;
            if (rsiList[p1].Rsi == null || rsiList[p2].Rsi == null) return false;
            return quotes[p2].High > quotes[p1].High
                && rsiList[p2].Rsi.Value < rsiList[p1].Rsi.Value - 1
                && rsiList[p2].Rsi.Value > 50;
        }

        /// <summary>
        /// 在最近 lookback 根K线内寻找2-bar分型pivot（左右各2根确认）
        /// </summary>
        private List<int> FindPivots(List<SkQuote> quotes, bool findHighs, int lookback)
        {
            var pivots = new List<int>();
            int start = Math.Max(2, quotes.Count - lookback);
            for (int i = start; i < quotes.Count - 2; i++)
            {
                if (findHighs)
                {
                    if (quotes[i].High >= quotes[i - 1].High && quotes[i].High >= quotes[i - 2].High
                        && quotes[i].High > quotes[i + 1].High && quotes[i].High > quotes[i + 2].High)
                    {
                        pivots.Add(i);
                    }
                }
                else
                {
                    if (quotes[i].Low <= quotes[i - 1].Low && quotes[i].Low <= quotes[i - 2].Low
                        && quotes[i].Low < quotes[i + 1].Low && quotes[i].Low < quotes[i + 2].Low)
                    {
                        pivots.Add(i);
                    }
                }
            }
            return pivots;
        }

        /// <summary>
        /// 计算交易手数
        /// </summary>
        private decimal CalculateLots(TableUnit tu, SkQuote q)
        {
            var num = Convert.ToDecimal(ArgDic["lots"]);
            var lotsMode = Convert.ToInt32(ArgDic["lotsMode"]);
            var sym = GetSymbol(tu.MktSymbol);

            if (lotsMode == 1)
            {
                num = Convert.ToDecimal(ArgDic["money"]) / (q.Close * sym.multiplier * sym.margin_ratio);
            }

            return NormalizeLots(tu, num);
        }

        private decimal NormalizeLots(TableUnit tu, decimal num)
        {
            var sym = GetSymbol(tu.MktSymbol);
            return sym.symbol_type == (int)SymbolType.COIN
                ? (int)(num * sym.scale) / (decimal)sym.scale
                : Math.Floor(num);
        }

        /// <summary>
        /// 重置状态（冷却计数由调用方按需设置）
        /// </summary>
        private void ResetState(State s)
        {
            s.Status = 0;
            s.EntryPath = 0;
            s.Num = 0;
            s.BaseNum = 0;
            s.AvgPrice = 0;
            s.LastFillPrice = 0;
            s.StopLoss = 0;
            s.ExtremeClose = 0;
            s.BarsInTrade = 0;
            s.AddCount = 0;
            s.PartialExited = false;
            s.Realized = 0;
            s.WaitRecover = false;
            s.WaitAdds = 0;
        }
    }
}
