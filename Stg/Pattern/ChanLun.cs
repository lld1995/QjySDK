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
	/// 缠论交易策略
	/// 严格按照缠论定义实现：K线包含处理、分型、笔、线段、中枢、背驰、买卖点
	/// </summary>
	public class ChanLun : StgBase
	{
		public ChanLun()
		{
		}

		public ChanLun(string id) : base(id)
		{
		}

		// 分型类型
		internal enum FractalType
		{
			None = 0,
			Top = 1,    // 顶分型
			Bottom = 2  // 底分型
		}

		// 处理后的K线（合并包含关系后）
		internal class MergedBar
		{
			public int OriginalIndex { get; set; }  // 原始K线索引（第一根）
			public int LastOriginalIndex { get; set; }  // 原始K线索引（最后一根，用于绘制）
			public int MergedCount { get; set; }    // 合并的K线数量
			public decimal High { get; set; }
			public decimal Low { get; set; }
			public int HighOriginalIndex { get; set; }  // 最高点对应的原始K线索引
			public int LowOriginalIndex { get; set; }   // 最低点对应的原始K线索引
			public decimal Open { get; set; }
			public decimal Close { get; set; }
			public DateTime Date { get; set; }
			public int Direction { get; set; }      // 合并方向：1向上 -1向下
		}

		// 分型结构
		internal class Fractal
		{
			public int Index { get; set; }          // 在MergedBars中的索引
			public int OriginalIndex { get; set; }  // 原始K线索引（第一根）
			public int LastOriginalIndex { get; set; }  // 原始K线索引（最后一根，用于绘制）
			public FractalType Type { get; set; }
			public decimal Price { get; set; }      // 顶分型取High，底分型取Low
			public decimal High { get; set; }       // 分型最高点
			public decimal Low { get; set; }        // 分型最低点
			public decimal Open { get; set; }       // 分型中间K线开盘价
			public decimal Close { get; set; }      // 分型中间K线收盘价
			public DateTime Date { get; set; }
			public bool IsConfirmed { get; set; }   // 是否已确认

			/// <summary>
			/// 计算实体大小（|Close - Open|）
			/// </summary>
			public decimal BodySize => Math.Abs(Close - Open);

			/// <summary>
			/// 计算上影线长度
			/// </summary>
			public decimal UpperShadow => High - Math.Max(Open, Close);

			/// <summary>
			/// 计算下影线长度
			/// </summary>
			public decimal LowerShadow => Math.Min(Open, Close) - Low;
		}

		// 笔结构
		internal class Stroke
		{
			public int StartIndex { get; set; }     // 起始分型在MergedBars中的索引
			public int EndIndex { get; set; }       // 结束分型在MergedBars中的索引
			public Fractal StartFractal { get; set; }
			public Fractal EndFractal { get; set; }
			public bool IsUp { get; set; }          // true为向上笔（底分型->顶分型），false为向下笔
			public decimal High { get; set; }       // 笔的最高点
			public decimal Low { get; set; }        // 笔的最低点
			public decimal MACDArea { get; set; }   // 笔对应的MACD面积（用于背驰判断）
			public int BarCount { get; set; }       // 包含的合并K线数量
		}

		// 线段结构
		internal class Segment
		{
			public int StartIndex { get; set; }
			public int EndIndex { get; set; }
			public Stroke StartStroke { get; set; }
			public Stroke EndStroke { get; set; }
			public List<Stroke> Strokes { get; set; }  // 构成线段的笔
			public bool IsUp { get; set; }             // 线段方向
			public decimal High { get; set; }
			public decimal Low { get; set; }
		}

		// 中枢状态枚举
		internal enum ZhongShuStatus
		{
			Forming = 0,    // 形成中（未满3段）
			Confirmed = 1,  // 已确认（满3段，ZG > ZD）
			Extending = 2,  // 延伸中（后续段在区间内）
			Left = 3,       // 已离开（有段突破边界）
			Ended = 4,      // 已结束（回抽确认离开）
			Upgraded = 5    // 已升级（9段升级或扩展升级）
		}

		// 走势类型枚举
		internal enum TrendType
		{
			Unknown = 0,    // 未知
			Consolidation = 1,  // 盘整（只有一个中枢）
			UpTrend = 2,    // 上涨趋势（至少两个向上中枢，中枢间无重叠）
			DownTrend = 3   // 下跌趋势（至少两个向下中枢，中枢间无重叠）
		}

		// 中枢结构（严格按照缠论定义）
		// 中枢 = 至少三个连续次级别走势类型的共同重叠价格区间
		// ZD = max(L₁, L₂, L₃) 中枢下沿
		// ZG = min(H₁, H₂, H₃) 中枢上沿
		// 中枢成立充要条件：ZG > ZD
		internal class ZhongShu
		{
			public int Id { get; set; }             // 中枢唯一标识
			public int StartIndex { get; set; }     // 起始K线索引
			public int EndIndex { get; set; }       // 结束K线索引
			public decimal ZG { get; set; }         // 中枢上沿 = min(各段高点)
			public decimal ZD { get; set; }         // 中枢下沿 = max(各段低点)
			public decimal GG { get; set; }         // 中枢波动区间最高点
			public decimal DD { get; set; }         // 中枢波动区间最低点
			public List<Segment>? Segments { get; set; }  // 构成中枢的线段（至少3段）
			public int Level { get; set; }          // 中枢级别（0=当前级别，1=升级后）
			public ZhongShuStatus Status { get; set; }  // 中枢状态
			public int SegmentCount => Segments?.Count ?? 0;  // 段数
			public bool IsValid => ZG > ZD;         // 中枢有效性：ZG > ZD（重叠区间有宽度）
			public DateTime FormTime { get; set; }  // 中枢形成时间（第三段完成时）
			public int? LeaveDirection { get; set; } // 离开方向：1=向上，-1=向下，null=未离开
			public Segment? LeaveSegment { get; set; } // 离开段
			public Segment? PullbackSegment { get; set; } // 回抽段
			public bool IsPullbackConfirmed { get; set; } // 回抽是否确认（第三类买卖点）
			public int Direction { get; set; }      // 中枢方向：1=向上中枢（进入段向上），-1=向下中枢（进入段向下）
		}

		// 买卖点类型
		internal enum BSPointType
		{
			None = 0,
			Buy1 = 1,   // 一买：趋势背驰后的第一个买点
			Buy2 = 2,   // 二买：一买后回调不破一买低点
			Buy3 = 3,   // 三买：离开中枢后回踩不进中枢
			Sell1 = -1, // 一卖：趋势背驰后的第一个卖点
			Sell2 = -2, // 二卖：一卖后反弹不破一卖高点
			Sell3 = -3  // 三卖：离开中枢后回抽不进中枢
		}

		// 买卖点结构
		internal class BSPoint
		{
			public BSPointType Type { get; set; }
			public int Index { get; set; }
			public decimal Price { get; set; }
			public DateTime Date { get; set; }
			public bool IsDivergence { get; set; }  // 是否背驰
		}

		public override StgDesc GetStgDesc()
		{
			var sd = new StgDesc();
			sd.ArgDic["minBarCount"] = 11;           // 最少K线数（至少需要形成2个分型）
			sd.ArgDic["strokeMinBars"] = 5;          // 笔的最少独立K线数（缠论标准：5根）
			sd.ArgDic["zhongshuMinStrokes"] = 3;     // 形成中枢的最少笔数
			sd.ArgDic["useZhongShu"] = 1;            // 是否使用中枢交易（0否 1是）
			sd.ArgDic["useDivergence"] = 1;          // 是否使用背驰判断（0否 1是）
			sd.ArgDic["mode"] = 0;
			sd.ArgDic["sendMode"] = 0;

			// 手数控制
			sd.ArgDic["lotsMode"] = 1;
			sd.ArgDic["lots"] = 1.0m;
			sd.ArgDic["money"] = 10000m;

			// 止损设置（线段级别信号稀疏，依赖买卖点信号自然反转，不使用百分比止损）
			sd.ArgDic["useStopLoss"] = 1;        // 是否使用止损（0否 1是）
			sd.ArgDic["stopLossPercent"] = 5.0m;  // 硬止损5%（统一基准）

			// 移动止损设置（关闭，避免截断趋势盈利）
			sd.ArgDic["useTrailingStop"] = 0;
			sd.ArgDic["trailingActivatePercent"] = 5.0m;
			sd.ArgDic["trailingStopPercent"] = 3.0m;

			// 信号过期设置
			sd.ArgDic["signalExpiryBars"] = 100;  // 一买/一卖信号过期K线数（超过后不再派生二买/二卖）

			// 中枢回归平仓
			sd.ArgDic["useZhongShuExit"] = 0;    // 关闭中枢退出
			sd.ArgDic["minHoldBarsForExit"] = 0;

			// 中枢偏离限制（优化2：放宽为10%，减少过滤掉有效信号）
			sd.ArgDic["maxZhongShuDeviation"] = 20.0m; // 开仓点距离中枢的最大偏离比例（百分比，放宽至20%减少过滤高波动品种信号）

			// 交易冷却期（线段级别信号已足够稀疏，不需要冷却过滤）
			sd.ArgDic["tradeCooldownBars"] = 0;  // 平仓后冷却K线数（冷却期内仅Buy1/Sell1可开仓）

			// Buy3/Sell3反转限制：Buy3/Sell3不能反转已有仓位，只能从空仓开仓
			sd.ArgDic["noReversalOnBuy3Sell3"] = 0;  // 0否 1是

			// 中枢回归平仓范围：0=仅Buy3/Sell3 1=Buy2/Buy3/Sell2/Sell3 2=所有买卖点
			sd.ArgDic["zhongShuExitScope"] = 0;

            //sd.ArgDescDic["mode"] = new ArgDesc() { Text = "模式", Explain = "0 标准 1 仅做多 2 仅做空" };
            //sd.ArgDescDic["sendMode"] = new ArgDesc() { Text = "发单模式", Explain = "0 立即 1 下个开盘" };
            //sd.ArgDescDic["lotsMode"] = new ArgDesc() { Text = "手数模式", Explain = "0 固定手数 1 固定金额" };
            //sd.ArgDescDic["useZhongShu"] = new ArgDesc() { Text = "使用中枢", Explain = "0 否 1 是" };
            //sd.ArgDescDic["useDivergence"] = new ArgDesc() { Text = "使用背驰", Explain = "0 否 1 是" };
            //sd.ArgDescDic["zhongshuMinStrokes"] = new ArgDesc() { Text = "中枢最少笔数", Explain = "形成中枢所需的最少笔数，默认3" };
            //sd.ArgDescDic["strokeMinBars"] = new ArgDesc() { Text = "笔最少K线", Explain = "笔的最少独立K线数，缠论标准为5" };

            sd.ColorDic["macd-macd"] = "#BA55D3";
            sd.ColorDic["macd-signal"] = "";
            sd.ColorDic["macd-histogram"] = "#F6465D;#0ECB81";
            sd.ColorDic["main-fractal_top"] = "#FF9800";
            sd.ColorDic["main-fractal_bottom"] = "#00BCD4";
            sd.ColorDic["main-bi_up"] = "#FF5722";
            sd.ColorDic["main-bi_down"] = "#2196F3";
            sd.ColorDic["main-zhongshu_zg"] = "#E91E63";
            sd.ColorDic["main-zhongshu_zd"] = "#00CED1";
            sd.MidValDic["macd"] = 0;
            sd.MaxSymbolNum = 1000;
			sd.UseGlobalCalc = 0;
			sd.SubChartNum = 1;
			return sd;
		}

		internal class State
		{
			public int Status { get; set; }              // 0:无持仓 1:多仓 2:空仓
			public decimal Num { get; set; }
			public decimal EntryPrice { get; set; }      // 入场价格（用于止损计算）
			public List<MergedBar> MergedBars { get; set; }  // 处理后的K线
			public List<Fractal> Fractals { get; set; }      // 分型列表
			public List<Stroke> Strokes { get; set; }        // 笔列表
			public List<Segment> Segments { get; set; }      // 线段列表
			public List<ZhongShu> ZhongShus { get; set; }    // 中枢列表
			public ZhongShu CurrentZhongShu { get; set; }    // 当前中枢
			public TrendType CurrentTrendType { get; set; }  // 当前走势类型
			public List<BSPoint> BSPoints { get; set; }      // 买卖点列表
			public int LastProcessedIndex { get; set; }      // 最后处理的原始K线索引
			public List<MacdResult> MacdResults { get; set; } // MACD结果缓存
			public BSPoint LastBuy1 { get; set; }            // 最近的一买点
			public BSPoint LastSell1 { get; set; }           // 最近的一卖点
			public int LastDrawOriIndex { get; set; }
			public ZhongShu? LastBuy1ZhongShu { get; set; }    // 产生一买时的中枢（用于二买判断）
			public ZhongShu? LastSell1ZhongShu { get; set; }   // 产生一卖时的中枢（用于二卖判断）
			public int LastTradedBSPointIndex { get; set; } = -1;  // 最后交易的买卖点索引（防止重复触发）
			public ZhongShu? LastConfirmedDrawZhongShu { get; set; }  // 上次绘制的已确认中枢（用于防止中枢回退）
			public int LastConfirmedSegmentCount { get; set; }  // 上次确认中枢时的线段数量
			public decimal MaxProfitPrice { get; set; }      // 持仓期间最有利价格（用于移动止损）
			public int EntryBarIndex { get; set; }           // 入场时的K线索引（用于信号过期）
			public int LastBuy1BarIndex { get; set; } = -1;  // 一买产生时的K线索引
			public int LastSell1BarIndex { get; set; } = -1; // 一卖产生时的K线索引
			public ZhongShu EntryZhongShu { get; set; }      // 入场时的中枢（用于中枢回归平仓）
			public BSPointType? EntryBSPointType { get; set; }  // 入场时的买卖点类型（用于区分平仓逻辑）
			public int LastExitBarIndex { get; set; } = -1;       // 上次平仓时的K线索引（用于交易冷却）
		}

		private Dictionary<string, State> _stateDic = new Dictionary<string, State>();

		internal Dictionary<string, int> GetBSPointCounts()
		{
			var counts = new Dictionary<string, int>
			{
				{ "Buy1", 0 }, { "Sell1", 0 }, { "Buy2", 0 }, { "Sell2", 0 }, { "Buy3", 0 }, { "Sell3", 0 }
			};
			foreach (var kv in _stateDic)
			{
				if (kv.Value.BSPoints == null) continue;
				foreach (var bp in kv.Value.BSPoints)
				{
					switch (bp.Type)
					{
						case BSPointType.Buy1: counts["Buy1"]++; break;
						case BSPointType.Sell1: counts["Sell1"]++; break;
						case BSPointType.Buy2: counts["Buy2"]++; break;
						case BSPointType.Sell2: counts["Sell2"]++; break;
						case BSPointType.Buy3: counts["Buy3"]++; break;
						case BSPointType.Sell3: counts["Sell3"]++; break;
					}
				}
			}
			return counts;
		}

		#region K线包含关系处理

		/// <summary>
		/// 判断两根K线是否存在包含关系
		/// </summary>
		internal bool HasContainRelation(decimal high1, decimal low1, decimal high2, decimal low2)
		{
			return (high1 >= high2 && low1 <= low2) || (high2 >= high1 && low2 <= low1);
		}

		/// <summary>
		/// 比较两根K线的方向：返回1=向上，-1=向下，0=无法判断（完全相等）
		/// </summary>
		internal int CompareDirection(decimal high1, decimal low1, decimal high2, decimal low2)
		{
			if (high2 > high1)
				return 1;
			else if (high2 < high1)
				return -1;
			else if (low2 > low1)
				return 1;
			else if (low2 < low1)
				return -1;
			else
				return 0;  // 完全相等
		}

		/// <summary>
		/// 判断两根K线是否存在包含关系，并返回合并方向
		/// </summary>
		/// <param name="mergedBars">合并K线列表</param>
		/// <param name="lastIndex">上一根合并K线的索引</param>
		/// <param name="currHigh">当前K线的高点</param>
		/// <param name="currLow">当前K线的低点</param>
		/// <returns>0=不存在包含关系，1=向上合并，-1=向下合并</returns>
		internal int GetContainDirection(List<MergedBar> mergedBars, int lastIndex, decimal currHigh, decimal currLow)
		{
			if (lastIndex < 0 || lastIndex >= mergedBars.Count)
				return 0;

			var last = mergedBars[lastIndex];

			// 判断是否存在包含关系
			if (!HasContainRelation(last.High, last.Low, currHigh, currLow))
				return 0;

			// 确定合并方向：优先使用上一根K线的方向
			if (last.Direction != 0)
				return last.Direction;

			// 向前查找方向
			for (int i = lastIndex - 1; i >= 0; i--)
			{
				var prev = mergedBars[i];
				var curr = mergedBars[i + 1];
				int dir = CompareDirection(prev.High, prev.Low, curr.High, curr.Low);
				if (dir != 0)
					return dir;
			}

			// 所有历史K线都完全相等，用当前K线与上一根比较
			int fallbackDir = CompareDirection(last.High, last.Low, currHigh, currLow);
			return fallbackDir;  // 最终兆底允许direction为0，后续确认方向时再修改
		}

		/// <summary>
		/// 处理K线包含关系，生成合并后的K线序列
		/// 缠论规则：向上时取高高低高，向下时取低低高低
		/// </summary>
		internal void ProcessContainRelation(State state, List<SkQuote> quotes)
		{
			if (state.MergedBars == null)
				state.MergedBars = new List<MergedBar>();

			// 如果已处理过，只处理新增的K线
			int startIndex = state.LastProcessedIndex;
			if (startIndex == 0 && state.MergedBars.Count == 0)
			{
				// 第一根K线直接添加
				if (quotes.Count > 0)
				{
					state.MergedBars.Add(new MergedBar
					{
						OriginalIndex = 0,
						LastOriginalIndex = 0,
						MergedCount = 1,
						High = quotes[0].High,
						Low = quotes[0].Low,
						HighOriginalIndex = 0,
						LowOriginalIndex = 0,
						Open = quotes[0].Open,
						Close = quotes[0].Close,
						Date = quotes[0].Date,
						Direction = 0
					});
					startIndex = 1;
				}
			}

			for (int i = startIndex; i < quotes.Count; i++)
			{
				var curr = quotes[i];
				var last = state.MergedBars[state.MergedBars.Count - 1];

				// 判断包含关系并获取合并方向
				int direction = GetContainDirection(state.MergedBars, state.MergedBars.Count - 1, curr.High, curr.Low);
				if (direction != 0)
				{
					// 合并K线
					if (direction > 0)
					{
						// 向上：取高高低高
						if (curr.High > last.High)
						{
							last.High = curr.High;
							last.HighOriginalIndex = i;
						}
						if (curr.Low > last.Low)
						{
							last.Low = curr.Low;
							last.LowOriginalIndex = i;
						}
					}
					else
					{
						// 向下：取低低高低
						if (curr.High < last.High)
						{
							last.High = curr.High;
							last.HighOriginalIndex = i;
						}
						if (curr.Low < last.Low)
						{
							last.Low = curr.Low;
							last.LowOriginalIndex = i;
						}
					}
					last.MergedCount++;
					last.LastOriginalIndex = i;  // 更新为最后一根K线索引
					last.Close = curr.Close;
					last.Direction = direction;
				}
				else
				{
					// 不存在包含关系，确定新K线的方向
					int newDirection;
					if (curr.High > last.High)
						newDirection = 1;
					else if (curr.Low < last.Low)
						newDirection = -1;
					else
						newDirection = 0;  // High相等时用Low判断

					if (newDirection != 0)
					{
						// 回填之前direction为0的K线
						for (int j = state.MergedBars.Count - 1; j >= 0; j--)
						{
							if (state.MergedBars[j].Direction == 0)
								state.MergedBars[j].Direction = newDirection;
							else
								break;
						}
					}


					state.MergedBars.Add(new MergedBar
					{
						OriginalIndex = i,
						LastOriginalIndex = i,
						MergedCount = 1,
						High = curr.High,
						Low = curr.Low,
						HighOriginalIndex = i,
						LowOriginalIndex = i,
						Open = curr.Open,
						Close = curr.Close,
						Date = curr.Date,
						Direction = newDirection
					});
				}
			}

			state.LastProcessedIndex = quotes.Count;
		}

		#endregion

		#region 分型识别

		/// <summary>
		/// 在合并后的K线序列中识别分型
		/// 顶分型：中间K线高点最高且低点最高
		/// 底分型：中间K线低点最低且高点最低
		/// </summary>
		internal FractalType IdentifyFractal(List<MergedBar> mergedBars, int index)
		{
			if (index < 1 || index >= mergedBars.Count - 1)
				return FractalType.None;

			var prev = mergedBars[index - 1];
			var curr = mergedBars[index];
			var next = mergedBars[index + 1];

			// 顶分型：中间K线的高点最高且低点最高（缠论标准定义）
			if (curr.High > prev.High && curr.High > next.High
				&& curr.Low >= prev.Low && curr.Low >= next.Low)
			{
				return FractalType.Top;
			}

			// 底分型：中间K线的低点最低且高点最低（缠论标准定义）
			if (curr.Low < prev.Low && curr.Low < next.Low
				&& curr.High <= prev.High && curr.High <= next.High)
			{
				return FractalType.Bottom;
			}

			return FractalType.None;
		}

		/// <summary>
		/// 更新分型列表（基于合并后的K线）
		/// </summary>
		internal void UpdateFractals(State state)
		{
			if (state.Fractals == null)
				state.Fractals = new List<Fractal>();

			if (state.MergedBars == null || state.MergedBars.Count < 3)
				return;

			// 重新识别所有分型（因为K线合并可能影响之前的分型）
			var newFractals = new List<Fractal>();

			for (int i = 1; i < state.MergedBars.Count - 1; i++)
			{
				var fractalType = IdentifyFractal(state.MergedBars, i);
				if (fractalType != FractalType.None)
				{
					var bar = state.MergedBars[i];
					var fractal = new Fractal
					{
						Index = i,
						OriginalIndex = bar.OriginalIndex,
						LastOriginalIndex = fractalType == FractalType.Top ? bar.HighOriginalIndex : bar.LowOriginalIndex,
						Type = fractalType,
						Price = fractalType == FractalType.Top ? bar.High : bar.Low,
						High = bar.High,
						Low = bar.Low,
						Open = bar.Open,
						Close = bar.Close,
						Date = bar.Date,
						IsConfirmed = i < state.MergedBars.Count - 2
					};

					// 处理连续同类型分型：保留更极端的
					if (newFractals.Count > 0)
					{
						var lastFractal = newFractals[newFractals.Count - 1];
						if (lastFractal.Type == fractalType)
						{
							// 同类型分型，保留更极端的
							if (fractalType == FractalType.Top && fractal.Price > lastFractal.Price)
							{
								newFractals[newFractals.Count - 1] = fractal;
							}
							else if (fractalType == FractalType.Bottom && fractal.Price < lastFractal.Price)
							{
								newFractals[newFractals.Count - 1] = fractal;
							}
						}
						else
						{
							// 不同类型分型，检查是否共用K线
							// 分型由3根K线组成：Index-1, Index, Index+1
							// 两个分型不能共用K线，即 lastFractal.Index + 1 < fractal.Index - 1
							// 也就是 fractal.Index - lastFractal.Index > 2
							if (fractal.Index - lastFractal.Index > 2)
							{
								newFractals.Add(fractal);
							}
							else
							{
								// 共用K线时，综合比较极值、影线和实体决定保留哪个分型
								bool replaceWithNew = false;
								if (lastFractal.Type == FractalType.Top && fractalType == FractalType.Bottom)
								{
									// 顶后底共用K线：综合评估
									// 1. 极值比较：底分型低点是否突破顶分型低点
									bool priceBreak = fractal.Price < lastFractal.Low;
									// 2. 影线比较：底分型下影线越长越有效（表示下方有支撑）
									bool betterShadow = fractal.LowerShadow > lastFractal.LowerShadow;
									// 3. 实体比较：底分型实体越大越有效（表示反转力度强）
									bool betterBody = fractal.BodySize > lastFractal.BodySize;
									// 4. 收盘位置：底分型收盘价越高越好（阳线收盘）
									bool betterClose = fractal.Close > fractal.Open;

									// 综合判断：极值突破优先，否则看影线和实体
									if (priceBreak)
									{
										replaceWithNew = true;
									}
									else
									{
										// 极值相近时，综合影线和实体判断
										int score = 0;
										if (betterShadow) score++;
										if (betterBody) score++;
										if (betterClose) score++;
										replaceWithNew = score >= 2;
									}
								}
								else if (lastFractal.Type == FractalType.Bottom && fractalType == FractalType.Top)
								{
									// 底后顶共用K线：综合评估
									// 1. 极值比较：顶分型高点是否突破底分型高点
									bool priceBreak = fractal.Price > lastFractal.High;
									// 2. 影线比较：顶分型上影线越长越有效（表示上方有压力）
									bool betterShadow = fractal.UpperShadow > lastFractal.UpperShadow;
									// 3. 实体比较：顶分型实体越大越有效（表示反转力度强）
									bool betterBody = fractal.BodySize > lastFractal.BodySize;
									// 4. 收盘位置：顶分型收盘价越低越好（阴线收盘）
									bool betterClose = fractal.Close < fractal.Open;

									// 综合判断：极值突破优先，否则看影线和实体
									if (priceBreak)
									{
										replaceWithNew = true;
									}
									else
									{
										// 极值相近时，综合影线和实体判断
										int score = 0;
										if (betterShadow) score++;
										if (betterBody) score++;
										if (betterClose) score++;
										replaceWithNew = score >= 2;
									}
								}

								if (replaceWithNew)
								{
									newFractals[newFractals.Count - 1] = fractal;
								}
								// 否则保留之前的分型
							}
						}
					}
					else
					{
						newFractals.Add(fractal);
					}
				}
			}

			state.Fractals = newFractals;
		}

		#endregion

		#region 笔构建

		/// <summary>
		/// 更新笔列表
		/// 缠论规则：笔至少包含5根独立K线（处理后的K线，包含两端）
		/// </summary>
		internal void UpdateStrokes(State state, List<SkQuote> quotes, int strokeMinBars = 5)
		{
			if (state.Strokes == null)
				state.Strokes = new List<Stroke>();

			if (state.Fractals == null || state.Fractals.Count < 2)
				return;

			// 重新构建笔列表（因为分型可能被更新）
			var newStrokes = new List<Stroke>();
			int startIdx = 0;  // 当前笔的起点分型索引
			int originalStartIdx = 0;  // 记录原始起点，用于检测是否在内层更新过

			while (startIdx < state.Fractals.Count - 1)
			{
				var startFractal = state.Fractals[startIdx];
				originalStartIdx = startIdx;
				bool foundStroke = false;

				// 起点分型必须已确认
				if (!startFractal.IsConfirmed)
				{
					startIdx++;
					continue;
				}

				// 寻找有效的终点分型
				for (int j = startIdx + 1; j < state.Fractals.Count; j++)
				{
					var endFractal = state.Fractals[j];

					// 终点分型必须已确认
					if (!endFractal.IsConfirmed)
					{
						continue;
					}

					// 确保分型类型不同（顶分型和底分型交替）
					if (startFractal.Type == endFractal.Type)
					{
						// 同类型分型，更新起点为更极端的那个
						if ((startFractal.Type == FractalType.Top && endFractal.Price > startFractal.Price) ||
							(startFractal.Type == FractalType.Bottom && endFractal.Price < startFractal.Price))
						{
							// 如果已有笔，更新最后一笔的终点为新的更极端分型
							if (newStrokes.Count > 0)
							{
								var lastStroke = newStrokes[newStrokes.Count - 1];
								// 重新计算K线数并验证
								int newBarCount = endFractal.Index - lastStroke.StartFractal.Index + 1;
								if (newBarCount >= strokeMinBars)
								{
									lastStroke.EndFractal = endFractal;
									lastStroke.EndIndex = endFractal.Index;
									lastStroke.BarCount = newBarCount;
									// 重新计算High/Low
									if (lastStroke.IsUp)
									{
										lastStroke.High = endFractal.High;
									}
									else
									{
										lastStroke.Low = endFractal.Low;
									}
									// 只有成功更新笔时，才更新起点
									startFractal = endFractal;
									startIdx = j;
								}
							}
							else
							{
								// 没有已有笔时，直接更新起点
								startFractal = endFractal;
								startIdx = j;
							}
						}
						continue;
					}

					// 检查笔的最少K线数（处理后的K线，包含两端）
					int barCount = endFractal.Index - startFractal.Index + 1;
					if (barCount < strokeMinBars)  // 标准笔定义：至少5根独立K线
					{
						continue;
					}

					// 验证笔的有效性：向上笔结束点必须高于起始点，向下笔结束点必须低于起始点
					bool isUp = startFractal.Type == FractalType.Bottom;
					if (isUp && endFractal.Price <= startFractal.Price)
					{
						continue;
					}
					if (!isUp && endFractal.Price >= startFractal.Price)
					{
						continue;
					}

					// 缠论要求：顶底分型之间不能存在包含关系
					// 向上笔：底分型高点 < 顶分型低点（严格无包含）
					// 向下笔：顶分型低点 > 底分型高点（严格无包含）
					// 放宽条件：只要价格方向正确即可（顶更高或底更低）
					if (isUp && startFractal.High >= endFractal.Low && endFractal.High <= startFractal.High)
					{
						// 存在包含且顶分型高点未突破底分型高点，跳过
						continue;
					}
					if (!isUp && startFractal.Low <= endFractal.High && endFractal.Low >= startFractal.Low)
					{
						// 存在包含且底分型低点未突破顶分型低点，跳过
						continue;
					}

					// 创建笔
					var stroke = new Stroke
					{
						StartIndex = startFractal.Index,
						EndIndex = endFractal.Index,
						StartFractal = startFractal,
						EndFractal = endFractal,
						IsUp = isUp,
						BarCount = barCount
					};

					// 计算笔的最高点和最低点
					stroke.High = isUp ? endFractal.High : startFractal.High;
					stroke.Low = isUp ? startFractal.Low : endFractal.Low;

					// 遍历笔范围内的所有合并K线，找到真正的最高最低点
					for (int k = startFractal.Index; k <= endFractal.Index && k < state.MergedBars.Count; k++)
					{
						stroke.High = Math.Max(stroke.High, state.MergedBars[k].High);
						stroke.Low = Math.Min(stroke.Low, state.MergedBars[k].Low);
					}

					// 计算MACD面积（用于背驰判断）
					stroke.MACDArea = CalculateMACDArea(state, startFractal.OriginalIndex, endFractal.OriginalIndex, quotes, isUp);

					newStrokes.Add(stroke);
					startIdx = j;  // 下一笔从当前笔的终点开始
					foundStroke = true;
					break;
				}

				// 如果没有找到有效的笔
				if (!foundStroke)
				{
					// 如果起点在内层循环中被更新过，从更新后的起点+1继续
					// 否则从原始起点+1继续
					if (startIdx != originalStartIdx)
					{
						startIdx++;  // 从更新后的起点+1开始
					}
					else
					{
						startIdx++;  // 从原始起点+1开始
					}
				}
			}

			state.Strokes = newStrokes;
		}

		/// <summary>
		/// 计算指定范围内的MACD面积（用于背驰判断）
		/// </summary>
		/// <param name="isUp">笔的方向：true为向上笔，false为向下笔</param>
		private decimal CalculateMACDArea(State state, int startIndex, int endIndex, List<SkQuote> quotes, bool isUp)
		{
			if (state.MacdResults == null)
			{
				return 0;
			}
			decimal area = 0;
			for (int i = startIndex; i <= endIndex && i < state.MacdResults.Count; i++)
			{
				var macd = state.MacdResults[i];
				if (macd.Histogram.HasValue)
				{
					var value = (decimal)macd.Histogram.Value;
					// 向上笔只累加正值（红柱），向下笔只累加负值的绝对值（绿柱）
					if (isUp && value > 0)
						area += value;
					else if (!isUp && value < 0)
						area += Math.Abs(value);
				}
			}
			return area;
		}

		#endregion

		#region 线段构建

		/// <summary>
		/// 更新线段列表
		/// 缠论定义：线段由至少3笔构成，通过特征序列分型确定线段终点
		/// 线段是连续不重叠的次级别走势类型
		/// </summary>
		internal void UpdateSegments(State state)
		{
			if (state.Segments == null)
				state.Segments = new List<Segment>();

			if (state.Strokes == null || state.Strokes.Count < 3)
				return;

			var newSegments = new List<Segment>();
			int i = 0;

			while (i + 2 < state.Strokes.Count)
			{
				var startStroke = state.Strokes[i];
				bool isUp = startStroke.IsUp;
				
				// 收集构成当前线段的所有笔（至少3笔）
				var segmentStrokes = new List<Stroke> { startStroke };
				decimal segmentHigh = startStroke.High;
				decimal segmentLow = startStroke.Low;
				int j = i + 1;
				
				// 寻找线段终点：通过特征序列分型判断
				while (j < state.Strokes.Count)
				{
					var currentStroke = state.Strokes[j];
					segmentStrokes.Add(currentStroke);
					segmentHigh = Math.Max(segmentHigh, currentStroke.High);
					segmentLow = Math.Min(segmentLow, currentStroke.Low);
					
					// 至少需要3笔才能形成线段
					if (segmentStrokes.Count >= 3)
					{
						// 检查是否形成线段终点（特征序列分型）
						if (j >= 2)
						{
							var prevSameDir = FindPreviousSameDirectionStrokeInList(segmentStrokes, segmentStrokes.Count - 1);
							if (prevSameDir != null)
							{
								bool isSegmentEnd = false;
								if (isUp && !currentStroke.IsUp && currentStroke.Low < prevSameDir.Low)
								{
									isSegmentEnd = true;
								}
								else if (!isUp && currentStroke.IsUp && currentStroke.High > prevSameDir.High)
								{
									isSegmentEnd = true;
								}
								
								if (isSegmentEnd)
								{
									segmentStrokes.RemoveAt(segmentStrokes.Count - 1);
									segmentHigh = segmentStrokes.Max(s => s.High);
									segmentLow = segmentStrokes.Min(s => s.Low);
									break;
								}
							}
						}
					}
					j++;
				}
				
				// 如果收集到至少3笔，创建线段
				if (segmentStrokes.Count >= 3)
				{
					var endStroke = segmentStrokes[segmentStrokes.Count - 1];
					var segment = new Segment
					{
						StartIndex = startStroke.StartIndex,
						EndIndex = endStroke.EndIndex,
						StartStroke = startStroke,
						EndStroke = endStroke,
						Strokes = segmentStrokes,
						IsUp = isUp,
						High = segmentHigh,
						Low = segmentLow
					};
					newSegments.Add(segment);
					i += segmentStrokes.Count - 1;
				}
				else
				{
					i++;
				}
			}
			state.Segments = newSegments;
		}

		/// <summary>
		/// 在笔列表中查找同向的前一笔
		/// </summary>
		private Stroke FindPreviousSameDirectionStrokeInList(List<Stroke> strokes, int currentIndex)
		{
			if (currentIndex < 2)
				return null;

			var current = strokes[currentIndex];
			// 向前查找同向的笔（跳过一笔）
			for (int k = currentIndex - 2; k >= 0; k -= 2)
			{
				if (strokes[k].IsUp == current.IsUp)
					return strokes[k];
			}
			return null;
		}

		#endregion

		#region 中枢识别（严格按照缠论定义）

		private int _zhongShuIdCounter = 0;

		/// <summary>
		/// 计算三段的共同重叠区间
		/// ZD = max(L₁, L₂, L₃) 中枢下沿
		/// ZG = min(H₁, H₂, H₃) 中枢上沿
		/// </summary>
		internal (decimal ZD, decimal ZG, bool IsValid) CalculateThreeSegmentOverlap(Segment s1, Segment s2, Segment s3)
		{
			decimal zd = Math.Max(Math.Max(s1.Low, s2.Low), s3.Low);
			decimal zg = Math.Min(Math.Min(s1.High, s2.High), s3.High);
			return (zd, zg, zg > zd);
		}

		/// <summary>
		/// 检查段是否与中枢区间有重叠（延伸判定）
		/// 缠论定义：后续段与中枢区间[ZD, ZG]有交集即为延伸
		/// 包括触及边界的情况
		/// </summary>
		internal bool IsSegmentOverlapWithZhongShu(Segment segment, ZhongShu zs)
		{
			// 段与中枢区间有重叠：段的高点 >= 中枢下沿 且 段的低点 <= 中枢上沿
			// 使用 >= 和 <= 确保触及边界也算有交集
			return segment.High >= zs.ZD && segment.Low <= zs.ZG;
		}

		/// <summary>
		/// 检查段是否触及中枢边界（震荡）
		/// </summary>
		internal bool IsSegmentTouchingBoundary(Segment segment, ZhongShu zs)
		{
			bool hasOverlap = segment.High > zs.ZD && segment.Low < zs.ZG;
			bool notFullyInside = segment.High > zs.ZG || segment.Low < zs.ZD;
			return hasOverlap && notFullyInside;
		}

		/// <summary>
		/// 检查段是否离开中枢
		/// </summary>
		internal (bool IsLeft, int Direction) CheckSegmentLeave(Segment segment, ZhongShu zs)
		{
			if (segment.Low > zs.ZG) return (true, 1);   // 向上离开
			if (segment.High < zs.ZD) return (true, -1); // 向下离开
			return (false, 0);
		}

		/// <summary>
		/// 检查回抽是否确认离开（第三类买卖点）
		/// </summary>
		internal bool CheckPullbackConfirm(Segment pullbackSegment, ZhongShu zs)
		{
			if (zs.LeaveDirection == null) return false;
			if (zs.LeaveDirection == 1) return pullbackSegment.Low >= zs.ZG;
			return pullbackSegment.High <= zs.ZD;
		}

		/// <summary>
		/// 检查两个中枢是否可以扩展合并
		/// </summary>
		internal bool CanZhongShuExpand(ZhongShu zs1, ZhongShu zs2)
		{
			decimal overlapHigh = Math.Min(zs1.ZG, zs2.ZG);
			decimal overlapLow = Math.Max(zs1.ZD, zs2.ZD);
			return overlapHigh > overlapLow;
		}

		/// <summary>
		/// 合并两个中枢（扩展升级）
		/// 缠论定义：两个中枢区间[ZD, ZG]有重叠时，合并为更高级别中枢
		/// 合并后的中枢区间是两个中枢区间的交集
		/// </summary>
		internal ZhongShu MergeZhongShus(ZhongShu zs1, ZhongShu zs2)
		{
			var mergedSegments = new List<Segment>();
			if (zs1.Segments != null) mergedSegments.AddRange(zs1.Segments);
			if (zs2.Segments != null) mergedSegments.AddRange(zs2.Segments);

			// 合并后的中枢区间：两个中枢区间[ZD, ZG]的交集
			// ZG = min(zs1.ZG, zs2.ZG) 取较小的上沿
			// ZD = max(zs1.ZD, zs2.ZD) 取较大的下沿
			decimal newZG = Math.Min(zs1.ZG, zs2.ZG);
			decimal newZD = Math.Max(zs1.ZD, zs2.ZD);
			// 波动区间GG/DD取两个中枢的并集
			decimal newGG = Math.Max(zs1.GG, zs2.GG);
			decimal newDD = Math.Min(zs1.DD, zs2.DD);

			return new ZhongShu
			{
				Id = ++_zhongShuIdCounter,
				StartIndex = Math.Min(zs1.StartIndex, zs2.StartIndex),
				EndIndex = Math.Max(zs1.EndIndex, zs2.EndIndex),
				ZG = newZG,
				ZD = newZD,
				GG = newGG,
				DD = newDD,
				Segments = mergedSegments,
				Level = zs1.Level + 1,
				Status = ZhongShuStatus.Upgraded,
				FormTime = zs2.FormTime,
				Direction = zs1.Direction  // 继承第一个中枢的方向
			};
		}

		/// <summary>
		/// 中枢识别主方法（严格按照缠论定义的五步法）
		/// </summary>
		internal void UpdateZhongShus(State state, int minSegments = 3)
		{
			if (state.Segments == null || state.Segments.Count < minSegments)
			{
				if (state.ZhongShus == null)
					state.ZhongShus = new List<ZhongShu>();
				return;
			}

			if (state.ZhongShus == null)
				state.ZhongShus = new List<ZhongShu>();

			var newZhongShus = new List<ZhongShu>();
			int segmentIndex = 0;

			while (segmentIndex <= state.Segments.Count - minSegments)
			{
				var s1 = state.Segments[segmentIndex];
				var s2 = state.Segments[segmentIndex + 1];
				var s3 = state.Segments[segmentIndex + 2];

				var (zd, zg, isValid) = CalculateThreeSegmentOverlap(s1, s2, s3);

				if (!isValid)
				{
					segmentIndex++;
					continue;
				}

				var zhongshu = new ZhongShu
				{
					Id = ++_zhongShuIdCounter,
					StartIndex = s1.StartIndex,
					EndIndex = s3.EndIndex,
					ZG = zg,
					ZD = zd,
					GG = Math.Max(Math.Max(s1.High, s2.High), s3.High),
					DD = Math.Min(Math.Min(s1.Low, s2.Low), s3.Low),
					Segments = new List<Segment> { s1, s2, s3 },
					Level = 0,
					Status = ZhongShuStatus.Confirmed,
					FormTime = DateTime.Now,
					Direction = s1.IsUp ? 1 : -1  // 中枢方向由进入段（第一段）决定
				};

				int nextIndex = segmentIndex + 3;
				while (nextIndex < state.Segments.Count)
				{
					var nextSegment = state.Segments[nextIndex];
					var (isLeft, direction) = CheckSegmentLeave(nextSegment, zhongshu);

					if (isLeft)
					{
						zhongshu.Status = ZhongShuStatus.Left;
						zhongshu.LeaveDirection = direction;
						zhongshu.LeaveSegment = nextSegment;

						if (nextIndex + 1 < state.Segments.Count)
						{
							var pullbackSegment = state.Segments[nextIndex + 1];
							zhongshu.PullbackSegment = pullbackSegment;
							if (CheckPullbackConfirm(pullbackSegment, zhongshu))
							{
								zhongshu.Status = ZhongShuStatus.Ended;
								zhongshu.IsPullbackConfirmed = true;
							}
						}
						break;
					}

					if (IsSegmentOverlapWithZhongShu(nextSegment, zhongshu))
					{
						zhongshu.Segments.Add(nextSegment);
						zhongshu.EndIndex = nextSegment.EndIndex;
						zhongshu.GG = Math.Max(zhongshu.GG, nextSegment.High);
						zhongshu.DD = Math.Min(zhongshu.DD, nextSegment.Low);
						zhongshu.Status = ZhongShuStatus.Extending;

						if (zhongshu.SegmentCount >= 9)
						{
							zhongshu.Level++;
							zhongshu.Status = ZhongShuStatus.Upgraded;
						}
						nextIndex++;
					}
					else
					{
						// 段不与中枢重叠且未离开，可能是新中枢的开始
						// 结束当前中枢的延伸判定，让后续段尝试形成新中枢
						break;
					}
				}

				// 将当前中枢加入列表，检查是否可以与前一个中枢扩展合并
				if (newZhongShus.Count > 0)
				{
					var lastZs = newZhongShus[newZhongShus.Count - 1];
					if (CanZhongShuExpand(lastZs, zhongshu))
					{
						// 两个中枢波动区间有重叠，合并为更高级别中枢
						var mergedZs = MergeZhongShus(lastZs, zhongshu);
						newZhongShus[newZhongShus.Count - 1] = mergedZs;
					}
					else
					{
						// 两个中枢无重叠，形成新中枢（中枢新生）
						zhongshu.Status = ZhongShuStatus.Confirmed;
						newZhongShus.Add(zhongshu);
					}
				}
				else
				{
					newZhongShus.Add(zhongshu);
				}

				// 更新段索引：从当前中枢结束后的下一段开始尝试新中枢
				// 如果中枢已离开，从离开段开始尝试新中枢
				int prevStartIndex = segmentIndex;
				int candidateStartIndex;

				if (zhongshu.LeaveSegment != null)
				{
					// 找到离开段在Segments中的索引，从离开段开始尝试新中枢
					int leaveIndex = state.Segments.IndexOf(zhongshu.LeaveSegment);
					candidateStartIndex = leaveIndex >= 0 ? leaveIndex : nextIndex;
				}
				else
				{
					// 中枢未离开时，从中枢最后一段的下一段开始尝试新中枢
					var lastSegmentInZhongShu = zhongshu.Segments[zhongshu.Segments.Count - 1];
					int lastSegmentIndex = state.Segments.IndexOf(lastSegmentInZhongShu);
					candidateStartIndex = lastSegmentIndex >= 0 ? lastSegmentIndex + 1 : nextIndex;
				}

				// 确保一定前进，避免死循环
				segmentIndex = Math.Max(prevStartIndex + 1, candidateStartIndex);
			}
			state.ZhongShus = newZhongShus;
			state.CurrentZhongShu = newZhongShus.Count > 0 ? newZhongShus[newZhongShus.Count - 1] : null;
		}

		/// <summary>
		/// 获取中枢的第三类买卖点
		/// </summary>
		internal BSPointType GetThirdBSPoint(ZhongShu zs)
		{
			if (zs == null || !zs.IsPullbackConfirmed) return BSPointType.None;
			if (zs.LeaveDirection == 1) return BSPointType.Buy3;
			if (zs.LeaveDirection == -1) return BSPointType.Sell3;
			return BSPointType.None;
		}

		/// <summary>
		/// 判断当前走势类型（趋势/盘整）
		/// 缠论定义：
		/// - 盘整：只有一个中枢的走势
		/// - 上涨趋势：至少两个中枢，后一个中枢ZD > 前一个中枢ZG（中枢区间无重叠，向上排列）
		/// - 下跌趋势：至少两个中枢，后一个中枢ZG < 前一个中枢ZD（中枢区间无重叠，向下排列）
		/// </summary>
		internal TrendType DetermineTrendType(List<ZhongShu> zhongShus)
		{
			if (zhongShus == null || zhongShus.Count == 0)
				return TrendType.Unknown;

			if (zhongShus.Count == 1)
				return TrendType.Consolidation;

			// 检查最近两个中枢的关系
			var lastZs = zhongShus[zhongShus.Count - 1];
			var prevZs = zhongShus[zhongShus.Count - 2];

			// 上涨趋势：后一个中枢完全在前一个中枢上方（中枢区间无重叠）
			// 缠论定义：后一个中枢的中枢下沿(ZD) > 前一个中枢的中枢上沿(ZG)
			if (lastZs.ZD > prevZs.ZG)
				return TrendType.UpTrend;

			// 下跌趋势：后一个中枢完全在前一个中枢下方（中枢区间无重叠）
			// 缠论定义：后一个中枢的中枢上沿(ZG) < 前一个中枢的中枢下沿(ZD)
			if (lastZs.ZG < prevZs.ZD)
				return TrendType.DownTrend;

			// 中枢有重叠，仍为盘整
			return TrendType.Consolidation;
		}

		/// <summary>
		/// 更新当前走势类型
		/// </summary>
		internal void UpdateTrendType(State state)
		{
			state.CurrentTrendType = DetermineTrendType(state.ZhongShus);
		}

		#endregion

		#region 背驰判断

		/// <summary>
		/// 判断两笔是否存在背驰（笔级别背驰）
		/// 背驰定义：同向的两笔，后一笔的MACD面积小于前一笔
		/// </summary>
		internal bool IsDivergence(Stroke stroke1, Stroke stroke2)
		{
			if (stroke1 == null || stroke2 == null)
				return false;

			// 必须是同向的笔
			if (stroke1.IsUp != stroke2.IsUp)
				return false;

			// 后一笔的MACD面积小于前一笔，且价格创新高/新低
			if (stroke1.IsUp)
			{
				// 向上笔：后一笔创新高但MACD面积减小
				return stroke2.High >= stroke1.High && stroke2.MACDArea < stroke1.MACDArea;
			}
			else
			{
				// 向下笔：后一笔创新低但MACD面积减小
				return stroke2.Low <= stroke1.Low && stroke2.MACDArea < stroke1.MACDArea;
			}
		}

		/// <summary>
		/// 判断两段是否存在背驰（线段级别背驰）
		/// 缠论定义：同向的两段，后一段的MACD面积小于前一段
		/// </summary>
		internal bool IsSegmentDivergence(Segment seg1, Segment seg2)
		{
			if (seg1 == null || seg2 == null)
				return false;

			// 必须是同向的段
			if (seg1.IsUp != seg2.IsUp)
				return false;

			// 计算段的MACD面积（累加所有笔的MACD面积）
			decimal area1 = seg1.Strokes?.Sum(s => s.MACDArea) ?? 0;
			decimal area2 = seg2.Strokes?.Sum(s => s.MACDArea) ?? 0;

			if (seg1.IsUp)
			{
				// 向上段：后一段创新高但MACD面积减小
				return seg2.High >= seg1.High && area2 < area1;
			}
			else
			{
				// 向下段：后一段创新低但MACD面积减小
				return seg2.Low <= seg1.Low && area2 < area1;
			}
		}

		/// <summary>
		/// 趋势背驰判断：比较离开中枢的走势与进入中枢的走势
		/// 缠论定义：在趋势中，离开最后一个中枢的走势与进入该中枢的走势比较
		/// </summary>
		internal bool IsTrendDivergence(ZhongShu zs)
		{
			if (zs == null || zs.Segments == null || zs.Segments.Count < 3)
				return false;

			if (zs.LeaveSegment == null)
				return false;

			var leaveSegment = zs.LeaveSegment;

			// 找中枢内与离开段同向的第一段作为进入段
			Segment entrySegment = null;
			foreach (var s in zs.Segments)
			{
				if (s.IsUp == leaveSegment.IsUp)
				{
					entrySegment = s;
					break;
				}
			}

			if (entrySegment == null)
				return false;

			return IsSegmentDivergence(entrySegment, leaveSegment);
		}

		/// <summary>
		/// 在笔列表中寻找同向的前一笔（用于背驰比较）
		/// </summary>
		private Stroke FindPreviousSameDirectionStroke(List<Stroke> strokes, int currentIndex)
		{
			if (currentIndex < 2 || strokes.Count <= currentIndex)
				return null;

			var current = strokes[currentIndex];
			// 向前查找同向的笔（跳过一笔）
			for (int i = currentIndex - 2; i >= 0; i -= 2)
			{
				if (strokes[i].IsUp == current.IsUp)
					return strokes[i];
			}
			return null;
		}

		#endregion

		#region 买卖点识别（严格按照缠论定义）

		/// <summary>
		/// 判断是否存在下跌趋势（至少两个向下中枢，中枢间无重叠）
		/// </summary>
		internal bool HasDownTrend(List<ZhongShu> zhongShus, out ZhongShu lastZhongShu, out ZhongShu prevZhongShu)
		{
			lastZhongShu = null;
			prevZhongShu = null;
			
			if (zhongShus == null || zhongShus.Count < 2)
				return false;
			
			// 第一轮：严格定义（中枢无重叠）
			for (int i = zhongShus.Count - 1; i >= 1; i--)
			{
				var curr = zhongShus[i];
				var prev = zhongShus[i - 1];
				if (curr.ZG < prev.ZD)
				{
					lastZhongShu = curr;
					prevZhongShu = prev;
					return true;
				}
			}
			
			// 第二轮：放宽定义 — 中枢中心下移且GG/DD递降
			for (int i = zhongShus.Count - 1; i >= 1; i--)
			{
				var curr = zhongShus[i];
				var prev = zhongShus[i - 1];
				decimal currCenter = (curr.ZG + curr.ZD) / 2;
				decimal prevCenter = (prev.ZG + prev.ZD) / 2;
				if (currCenter < prevCenter && curr.GG < prev.GG && curr.DD < prev.DD)
				{
					lastZhongShu = curr;
					prevZhongShu = prev;
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// 判断是否存在上涨趋势（至少两个向上中枢，中枢间无重叠）
		/// </summary>
		internal bool HasUpTrend(List<ZhongShu> zhongShus, out ZhongShu lastZhongShu, out ZhongShu prevZhongShu)
		{
			lastZhongShu = null;
			prevZhongShu = null;
			
			if (zhongShus == null || zhongShus.Count < 2)
				return false;
			
			// 第一轮：严格定义（中枢无重叠）
			for (int i = zhongShus.Count - 1; i >= 1; i--)
			{
				var curr = zhongShus[i];
				var prev = zhongShus[i - 1];
				if (curr.ZD > prev.ZG)
				{
					lastZhongShu = curr;
					prevZhongShu = prev;
					return true;
				}
			}
			
			// 第二轮：放宽定义 — 中枢中心上移且GG/DD递升
			for (int i = zhongShus.Count - 1; i >= 1; i--)
			{
				var curr = zhongShus[i];
				var prev = zhongShus[i - 1];
				decimal currCenter = (curr.ZG + curr.ZD) / 2;
				decimal prevCenter = (prev.ZG + prev.ZD) / 2;
				if (currCenter > prevCenter && curr.GG > prev.GG && curr.DD > prev.DD)
				{
					lastZhongShu = curr;
					prevZhongShu = prev;
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// 获取中枢的进入段（a段）- 第一段
		/// </summary>
		internal Segment GetEntrySegment(ZhongShu zs)
		{
			if (zs == null || zs.Segments == null || zs.Segments.Count == 0)
				return null;
			return zs.Segments[0];
		}

		/// <summary>
		/// 计算段的MACD面积（用于背驰判断）
		/// </summary>
		internal decimal CalculateSegmentMACDArea(Segment segment)
		{
			if (segment == null || segment.Strokes == null)
				return 0;
			return segment.Strokes.Sum(s => s.MACDArea);
		}

		/// <summary>
		/// 判断离开段与进入段是否存在趋势背驰（严格定义）
		/// 缠论定义：离开段（c段）与进入段（a段）相比，力度减弱
		/// 力度判断：MACD面积/高度缩小
		/// </summary>
		internal bool IsTrendDivergenceStrict(ZhongShu zs)
		{
			var leaveSegment = zs?.LeaveSegment;
			if (leaveSegment == null || zs.Segments == null || zs.Segments.Count == 0)
				return false;

			// 找中枢内与离开段同向的第一段作为进入段（缠论定义：比较同向的进入段与离开段力度）
			Segment entrySegment = null;
			foreach (var s in zs.Segments)
			{
				if (s.IsUp == leaveSegment.IsUp)
				{
					entrySegment = s;
					break;
				}
			}

			if (entrySegment == null)
				return false;
			
			// 计算MACD面积
			decimal entryArea = CalculateSegmentMACDArea(entrySegment);
			decimal leaveArea = CalculateSegmentMACDArea(leaveSegment);
			
			// 背驰条件：离开段MACD面积小于进入段
			return leaveArea < entryArea;
		}

		/// <summary>
		/// 检查买卖点是否已存在（避免重复添加）
		/// </summary>
		private bool BSPointExists(State state, BSPointType type, int index)
		{
			if (state.BSPoints == null)
				return false;
			return state.BSPoints.Any(p => p.Type == type && p.Index == index);
		}

		/// <summary>
		/// 识别第一类买点（1B）
		/// 定义：某级别下跌趋势中，最后一个中枢后次级别走势类型向下离开中枢，创出新低且发生背驰的转折点
		/// 判断标准：
		/// 1. 必须存在两个以上同向中枢构成的下跌趋势
		/// 2. 离开段（c段）与进入段（a段）相比出现趋势背驰（力度减弱）
		/// </summary>
		internal BSPoint IdentifyBuy1(State state)
		{
			// 检查是否存在下跌趋势（至少两个向下中枢）
			if (!HasDownTrend(state.ZhongShus, out var lastZs, out var prevZs))
				return null;
			
			// 检查最后一个中枢是否已向下离开
			if (lastZs.LeaveDirection != -1)
				return null;
			
			// 检查是否创新低（低于中枢下沿）
			var leaveSegment = lastZs.LeaveSegment;
			if (leaveSegment == null || leaveSegment.Low >= lastZs.ZD)
				return null;
			
			// 检查趋势背驰
			if (!IsTrendDivergenceStrict(lastZs))
				return null;
			
			// 确认一买点
			return new BSPoint
			{
				Type = BSPointType.Buy1,
				Index = leaveSegment.EndIndex,
				Price = leaveSegment.Low,
				Date = leaveSegment.EndStroke?.EndFractal?.Date ?? DateTime.Now,
				IsDivergence = true
			};
		}

		/// <summary>
		/// 识别第一类卖点（1S）
		/// 定义：某级别上涨趋势中，最后一个中枢后次级别走势类型向上离开中枢，创出新高且发生背驰的转折点
		/// 判断标准：
		/// 1. 必须存在两个以上同向中枢构成的上涨趋势
		/// 2. 离开段（c段）与进入段（a段）相比出现趋势背驰（力度减弱）
		/// </summary>
		internal BSPoint IdentifySell1(State state)
		{
			// 检查是否存在上涨趋势（至少两个向上中枢）
			if (!HasUpTrend(state.ZhongShus, out var lastZs, out var prevZs))
				return null;
			
			// 检查最后一个中枢是否已向上离开
			if (lastZs.LeaveDirection != 1)
				return null;
			
			// 检查是否创新高（高于中枢上沿）
			var leaveSegment = lastZs.LeaveSegment;
			if (leaveSegment == null || leaveSegment.High <= lastZs.ZG)
				return null;
			
			// 检查趋势背驰
			if (!IsTrendDivergenceStrict(lastZs))
				return null;
			
			// 确认一卖点
			return new BSPoint
			{
				Type = BSPointType.Sell1,
				Index = leaveSegment.EndIndex,
				Price = leaveSegment.High,
				Date = leaveSegment.EndStroke?.EndFractal?.Date ?? DateTime.Now,
				IsDivergence = true
			};
		}

		/// <summary>
		/// 识别盘整背驰一买点（线段级别）
		/// 定义：单个中枢的盘整走势中，离开段向下突破中枢且MACD面积小于进入段，形成背驰转折
		/// </summary>
		internal BSPoint IdentifyConsolidationBuy1(State state)
		{
			if (state.ZhongShus == null || state.ZhongShus.Count == 0)
				return null;

			// 趋势背驰已处理，这里只处理无趋势的情况
			if (HasDownTrend(state.ZhongShus, out _, out _))
				return null;

			var lastZs = state.ZhongShus[state.ZhongShus.Count - 1];

			if (lastZs.LeaveDirection != -1)
				return null;

			var leaveSegment = lastZs.LeaveSegment;
			if (leaveSegment == null || leaveSegment.Low >= lastZs.ZD)
				return null;

			if (!IsTrendDivergenceStrict(lastZs))
				return null;

			return new BSPoint
			{
				Type = BSPointType.Buy1,
				Index = leaveSegment.EndIndex,
				Price = leaveSegment.Low,
				Date = leaveSegment.EndStroke?.EndFractal?.Date ?? DateTime.Now,
				IsDivergence = true
			};
		}

		/// <summary>
		/// 识别盘整背驰一卖点（线段级别）
		/// 定义：单个中枢的盘整走势中，离开段向上突破中枢且MACD面积小于进入段，形成背驰转折
		/// </summary>
		internal BSPoint IdentifyConsolidationSell1(State state)
		{
			if (state.ZhongShus == null || state.ZhongShus.Count == 0)
				return null;

			if (HasUpTrend(state.ZhongShus, out _, out _))
				return null;

			var lastZs = state.ZhongShus[state.ZhongShus.Count - 1];

			if (lastZs.LeaveDirection != 1)
				return null;

			var leaveSegment = lastZs.LeaveSegment;
			if (leaveSegment == null || leaveSegment.High <= lastZs.ZG)
				return null;

			if (!IsTrendDivergenceStrict(lastZs))
				return null;

			return new BSPoint
			{
				Type = BSPointType.Sell1,
				Index = leaveSegment.EndIndex,
				Price = leaveSegment.High,
				Date = leaveSegment.EndStroke?.EndFractal?.Date ?? DateTime.Now,
				IsDivergence = true
			};
		}

		/// <summary>
		/// Identify Buy2 (2B)
		/// </summary>
		internal BSPoint IdentifyBuy2(State state, Stroke pullbackStroke, Fractal latestFractal)
		{
			if (pullbackStroke == null || pullbackStroke.IsUp)
				return null;
			
			if (state.LastBuy1 == null)
				return null;
			
			bool hasFractalConfirm = latestFractal != null && 
								  latestFractal.Type == FractalType.Bottom && latestFractal.IsConfirmed;
			
			bool hasSubDivergence = false;
			int pullbackIdx = state.Strokes?.IndexOf(pullbackStroke) ?? -1;
			if (state.Strokes != null && state.Strokes.Count >= 3)
			{
				if (pullbackIdx >= 2)
				{
					var prevSameDir = state.Strokes[pullbackIdx - 2];
					if (!prevSameDir.IsUp && pullbackStroke.MACDArea < prevSameDir.MACDArea)
					{
						hasSubDivergence = true;
					}
				}
			}
			
			if (!hasFractalConfirm && !hasSubDivergence)
				return null;
			
			if (pullbackStroke.Low <= state.LastBuy1.Price)
				return null;
			
			if (state.LastBuy1ZhongShu != null && pullbackStroke.Low <= state.LastBuy1ZhongShu.ZG)
				return null;
			
			return new BSPoint
			{
				Type = BSPointType.Buy2,
				Index = pullbackStroke.EndIndex,
				Price = pullbackStroke.Low,
				Date = pullbackStroke.EndFractal?.Date ?? DateTime.Now,
				IsDivergence = hasSubDivergence
			};
		}

		/// <summary>
		/// Identify Sell2 (2S)
		/// </summary>
		internal BSPoint IdentifySell2(State state, Stroke pullbackStroke, Fractal latestFractal)
		{
			if (pullbackStroke == null || !pullbackStroke.IsUp)
				return null;
			
			if (state.LastSell1 == null)
				return null;
			
			bool hasFractalConfirm = latestFractal != null && 
								  latestFractal.Type == FractalType.Top && latestFractal.IsConfirmed;
			
			bool hasSubDivergence = false;
			int pullbackIdx = state.Strokes?.IndexOf(pullbackStroke) ?? -1;
			if (pullbackIdx >= 2)
			{
				var prevSameDir = state.Strokes[pullbackIdx - 2];
				if (prevSameDir.IsUp && pullbackStroke.MACDArea < prevSameDir.MACDArea)
				{
					hasSubDivergence = true;
				}
			}
			
			if (!hasFractalConfirm && !hasSubDivergence)
				return null;
			
			if (pullbackStroke.High >= state.LastSell1.Price)
				return null;

			if (state.LastSell1ZhongShu != null && pullbackStroke.High >= state.LastSell1ZhongShu.ZD)
				return null;
			
			return new BSPoint
			{
				Type = BSPointType.Sell2,
				Index = pullbackStroke.EndIndex,
				Price = pullbackStroke.High,
				Date = pullbackStroke.EndFractal?.Date ?? DateTime.Now,
				IsDivergence = hasSubDivergence
			};
		}

		/// <summary>
		/// Identify Buy3 (3B)
		/// </summary>
		internal BSPoint IdentifyBuy3(State state, Stroke pullbackStroke, ZhongShu zhongShu, Fractal latestFractal)
		{
			if (pullbackStroke == null || pullbackStroke.IsUp)
				return null;
			if (zhongShu == null || !zhongShu.IsValid)
				return null;
			if (zhongShu.LeaveDirection != 1)
				return null;
			
			bool hasFractalConfirm = latestFractal != null && 
				latestFractal.Type == FractalType.Bottom && latestFractal.IsConfirmed;
			bool hasSubDivergence = false;
			if (state.Strokes != null && state.Strokes.Count >= 3)
			{
				int idx = state.Strokes.IndexOf(pullbackStroke);
				if (idx >= 2 && !state.Strokes[idx - 2].IsUp && pullbackStroke.MACDArea < state.Strokes[idx - 2].MACDArea)
					hasSubDivergence = true;
			}
			if (!hasFractalConfirm || !hasSubDivergence)
				return null;
			if (pullbackStroke.Low < zhongShu.ZG)
				return null;
			return new BSPoint
			{
				Type = BSPointType.Buy3,
				Index = pullbackStroke.EndIndex,
				Price = pullbackStroke.Low,
				Date = pullbackStroke.EndFractal?.Date ?? DateTime.Now,
				IsDivergence = hasSubDivergence
			};
		}

		/// <summary>
		/// Identify Sell3 (3S)
		/// </summary>
		internal BSPoint IdentifySell3(State state, Stroke pullbackStroke, ZhongShu zhongShu, Fractal latestFractal)
		{
			if (pullbackStroke == null || !pullbackStroke.IsUp)
				return null;
			if (zhongShu == null || !zhongShu.IsValid)
				return null;
			if (zhongShu.LeaveDirection != -1)
				return null;
			
			bool hasFractalConfirm = latestFractal != null && 
				latestFractal.Type == FractalType.Top && latestFractal.IsConfirmed;
			bool hasSubDivergence = false;
			if (state.Strokes != null && state.Strokes.Count >= 3)
			{
				int idx = state.Strokes.IndexOf(pullbackStroke);
				if (idx >= 2 && state.Strokes[idx - 2].IsUp && pullbackStroke.MACDArea < state.Strokes[idx - 2].MACDArea)
					hasSubDivergence = true;
			}
			if (!hasFractalConfirm || !hasSubDivergence)
				return null;
			if (pullbackStroke.High > zhongShu.ZD)
				return null;
			return new BSPoint
			{
				Type = BSPointType.Sell3,
				Index = pullbackStroke.EndIndex,
				Price = pullbackStroke.High,
				Date = pullbackStroke.EndFractal?.Date ?? DateTime.Now,
				IsDivergence = hasSubDivergence
			};
		}


		/// <summary>
		/// 识别买卖点主方法
		/// </summary>
		private void UpdateBSPoints(State state, List<SkQuote> quotes)
		{
			if (state.BSPoints == null)
				state.BSPoints = new List<BSPoint>();

			// 获取当前笔
			Stroke currentStroke = null;
			Stroke prevStroke = null;
			if (state.Strokes != null && state.Strokes.Count > 0)
			{
				currentStroke = state.Strokes[state.Strokes.Count - 1];
				if (state.Strokes.Count > 1)
					prevStroke = state.Strokes[state.Strokes.Count - 2];
			}
			if (currentStroke == null)
				return;

			// 获取趋势中枢信息
			ZhongShu lastDownTrendZs = null;
			ZhongShu lastUpTrendZs = null;
			ZhongShu prevDownTrendZs = null;
			ZhongShu prevUpTrendZs = null;
			
			// 获取下跌趋势中枢
			HasDownTrend(state.ZhongShus, out lastDownTrendZs, out prevDownTrendZs);
			// 获取上涨趋势中枢
			HasUpTrend(state.ZhongShus, out lastUpTrendZs, out prevUpTrendZs);
			
			// ========== 第一类买卖点识别 ==========
			// 一买：下跌趋势背驰点
			var buy1 = IdentifyBuy1(state);
			// 盘整背驰一买：无趋势时，单个中枢离开背驰
			if (buy1 == null)
				buy1 = IdentifyConsolidationBuy1(state);
			if (buy1 != null && !BSPointExists(state, BSPointType.Buy1, buy1.Index))
			{
				state.BSPoints.Add(buy1);
				state.LastBuy1 = buy1;
				// 记录产生一买时的中枢，用于二买判断
				state.LastBuy1ZhongShu = lastDownTrendZs ?? (state.ZhongShus?.Count > 0 ? state.ZhongShus[state.ZhongShus.Count - 1] : null);
				// 一买出现表示趋势反转，清除之前的一卖信号，防止错误触发二卖
				state.LastSell1 = null;
				state.LastSell1ZhongShu = null;
			}

			// 一卖：上涨趋势背驰点
			var sell1 = IdentifySell1(state);
			// 盘整背驰一卖：无趋势时，单个中枢离开背驰
			if (sell1 == null)
				sell1 = IdentifyConsolidationSell1(state);
			if (sell1 != null && !BSPointExists(state, BSPointType.Sell1, sell1.Index))
			{
				state.BSPoints.Add(sell1);
				state.LastSell1 = sell1;
				// 记录产生一卖时的中枢，用于二卖判断
				state.LastSell1ZhongShu = lastUpTrendZs ?? (state.ZhongShus?.Count > 0 ? state.ZhongShus[state.ZhongShus.Count - 1] : null);
				// 一卖出现表示趋势反转，清除之前的一买信号，防止错误触发二买
				state.LastBuy1 = null;
				state.LastBuy1ZhongShu = null;
			}

			// ========== 第二类买卖点识别 ==========
			// 获取最新分型用于提前识别
			Fractal latestFractal = null;
			if (state.Fractals != null && state.Fractals.Count > 0)
			{
				latestFractal = state.Fractals[state.Fractals.Count - 1];
			}

			// 二买：一买后回调笔形成底分型确认时识别
			if (state.LastBuy1 != null)
			{
				// 优先检查当前笔（如果是向下笔）
				if (!currentStroke.IsUp && currentStroke.EndIndex > state.LastBuy1.Index)
				{
					var buy2 = IdentifyBuy2(state, currentStroke, latestFractal);
					if (buy2 != null && !BSPointExists(state, BSPointType.Buy2, buy2.Index))
					{
						state.BSPoints.Add(buy2);
					}
				}
				// 也检查前一笔（如果是向下笔且当前笔未产生二买）
				else if (prevStroke != null && !prevStroke.IsUp && prevStroke.EndIndex > state.LastBuy1.Index)
				{
					var buy2 = IdentifyBuy2(state, prevStroke, latestFractal);
					if (buy2 != null && !BSPointExists(state, BSPointType.Buy2, buy2.Index))
					{
						state.BSPoints.Add(buy2);
					}
				}
			}

			// 二卖：一卖后反弹笔形成顶分型确认时识别
			if (state.LastSell1 != null)
			{
				// 优先检查当前笔（如果是向上笔）
				if (currentStroke.IsUp && currentStroke.EndIndex > state.LastSell1.Index)
				{
					var sell2 = IdentifySell2(state, currentStroke, latestFractal);
					if (sell2 != null && !BSPointExists(state, BSPointType.Sell2, sell2.Index))
					{
						state.BSPoints.Add(sell2);
					}
				}
				// 也检查前一笔（如果是向上笔且当前笔未产生二卖）
				else if (prevStroke != null && prevStroke.IsUp && prevStroke.EndIndex > state.LastSell1.Index)
				{
					var sell2 = IdentifySell2(state, prevStroke, latestFractal);
					if (sell2 != null && !BSPointExists(state, BSPointType.Sell2, sell2.Index))
					{
						state.BSPoints.Add(sell2);
					}
				}
			}

			// ========== 第三类买卖点识别 ==========
			// 三买：中枢向上离开后回踩不进中枢
			if (state.CurrentZhongShu != null && !currentStroke.IsUp)
			{
				var buy3 = IdentifyBuy3(state, currentStroke, state.CurrentZhongShu, latestFractal);
				if (buy3 != null && !BSPointExists(state, BSPointType.Buy3, buy3.Index))
				{
					state.BSPoints.Add(buy3);
				}
			}

			// 三卖：中枢向下离开后反弹不进中枢
			if (state.CurrentZhongShu != null && currentStroke.IsUp)
			{
				var sell3 = IdentifySell3(state, currentStroke, state.CurrentZhongShu, latestFractal);
				if (sell3 != null && !BSPointExists(state, BSPointType.Sell3, sell3.Index))
				{
					state.BSPoints.Add(sell3);
				}
			}
		}

		#endregion

		public override void OnBar(Period period, TableUnit tu, bool isFinal, SkQuote tq)
		{
			base.OnBar(period, tu, isFinal, tq);

			if (!isFinal)
				return;

			int minBarCount = (int)ArgDic["minBarCount"];
			if (tu.QuoteList.Count < minBarCount)
				return;

			int mode = (int)ArgDic["mode"];
			int sendMode = (int)ArgDic["sendMode"];
			var q = tu.QuoteList.Last();

			// 获取或创建状态
			State s = null;
			var sk = tu.GetStateKey();
			if (_stateDic.ContainsKey(sk))
			{
				s = _stateDic[sk];
			}
			else
			{
				s = new State();
				s.MergedBars = new List<MergedBar>();
				s.Fractals = new List<Fractal>();
				s.Strokes = new List<Stroke>();
				s.Segments = new List<Segment>();
				s.ZhongShus = new List<ZhongShu>();
				s.BSPoints = new List<BSPoint>();
				s.LastProcessedIndex = 0;
				_stateDic[sk] = s;
			}

			// 步骤1：K线包含关系处理
			ProcessContainRelation(s, tu.QuoteList);

			// 步骤2：分型识别（基于合并后的K线）
			UpdateFractals(s);

			// 计算MACD（用于背驰判断）
			try
			{
				var macd = tu.QuoteList.GetMacd(12, 26, 9).ToList();
				s.MacdResults = macd;
				var macd1 = macd[macd.Count - 1];

				Plot("macd", "histogram", PlotType.RECTANGLE, (double)macd1.Histogram);
				Plot("macd", "macd", PlotType.LINE, (double)macd1.Macd);
				Plot("macd", "signal", PlotType.LINE, (double)macd1.Signal);
			}
			catch
			{
				s.MacdResults = null;
			}

			// 步骤3：笔构建
			UpdateStrokes(s, tu.QuoteList);

			// 步骤4：线段构建
			UpdateSegments(s);

			// 步骤5：中枢识别（基于线段）
			UpdateZhongShus(s);

			// 步骤6：趋势/盘整判定
			UpdateTrendType(s);

			// 步骤7：买卖点识别
			UpdateBSPoints(s, tu.QuoteList);

			// 记录一买/一卖产生时的K线索引（用于信号过期）
			if (s.LastBuy1 != null && s.LastBuy1BarIndex < 0)
				s.LastBuy1BarIndex = tu.QuoteList.Count - 1;
			if (s.LastSell1 != null && s.LastSell1BarIndex < 0)
				s.LastSell1BarIndex = tu.QuoteList.Count - 1;

			// 信号过期由交易选择阶段的signalExpiryBars过滤器处理，不在此处清除LastBuy1/LastSell1
			// 以避免过早阻止Buy2/Sell2的识别

			// 绘制最近的分型
			if (s.Fractals != null && s.Fractals.Count > 0)
			{
				var lastFractal = s.Fractals[s.Fractals.Count - 1];
				if (lastFractal.IsConfirmed)
				{
					if (lastFractal.Type == FractalType.Top)
					{
						Plot("main", "fractal_top", PlotType.POINT, (double)lastFractal.Price);
					}
					else
					{
						Plot("main", "fractal_bottom", PlotType.POINT, (double)lastFractal.Price);
					}
				}
			}

			// 绘制笔（在最后一根bar上绘制所有笔）
			if (s.Strokes != null && s.Strokes.Count>0 && s.Strokes.Count > 0)
			{
				int drawBarIndex = tu.QuoteList.Count - 1;
				var stroke = s.Strokes[s.Strokes.Count-1];
				if (stroke.EndFractal.LastOriginalIndex > s.LastDrawOriIndex)
				{
					var extra = new PlotLineSegmentExtra
					{
						StartOffsetIndex = drawBarIndex - stroke.StartFractal.LastOriginalIndex,
						EndOffsetIndex = drawBarIndex - stroke.EndFractal.LastOriginalIndex,
						Val1 = stroke.StartFractal.Price,
						Val2 = stroke.EndFractal.Price
					};
					Plot("main", stroke.IsUp ? "bi_up" : "bi_down", PlotType.LINE_SEGMENT, (double)q.Close, extra);
					s.LastDrawOriIndex = stroke.EndFractal.LastOriginalIndex;
				}
			}

			// 绘制当前中枢（防止中枢回退）
			// 只有在线段数量增加时才更新绘制的中枢，避免未形成新线段时中枢值跳回前一个
			if (s.CurrentZhongShu != null && s.CurrentZhongShu.IsValid)
			{
				int currentSegmentCount = s.Segments?.Count ?? 0;
				
				// 判断是否应该更新绘制的中枢
				bool shouldUpdateDrawZhongShu = false;
				if (s.LastConfirmedDrawZhongShu == null)
				{
					// 首次绘制中枢
					shouldUpdateDrawZhongShu = true;
				}
				else if (currentSegmentCount > s.LastConfirmedSegmentCount)
				{
					// 线段数量增加了，可以更新中枢
					shouldUpdateDrawZhongShu = true;
				}
				else if (s.CurrentZhongShu.Id == s.LastConfirmedDrawZhongShu.Id)
				{
					// 同一个中枢，允许更新（可能是中枢延伸）
					shouldUpdateDrawZhongShu = true;
				}
				
				if (shouldUpdateDrawZhongShu)
				{
					s.LastConfirmedDrawZhongShu = s.CurrentZhongShu;
					s.LastConfirmedSegmentCount = currentSegmentCount;
				}
				
				// 使用稳定的中枢进行绘制
				var zsToPlot = s.LastConfirmedDrawZhongShu ?? s.CurrentZhongShu;
				Plot("main", "zhongshu_zg", PlotType.LINE, (double)zsToPlot.ZG);
				Plot("main", "zhongshu_zd", PlotType.LINE, (double)zsToPlot.ZD);
			}
			else if (s.LastConfirmedDrawZhongShu != null && s.LastConfirmedDrawZhongShu.IsValid)
			{
				// 当前没有有效中枢，但之前有确认的中枢，继续绘制之前的
				Plot("main", "zhongshu_zg", PlotType.LINE, (double)s.LastConfirmedDrawZhongShu.ZG);
				Plot("main", "zhongshu_zd", PlotType.LINE, (double)s.LastConfirmedDrawZhongShu.ZD);
			}

			// 计算手数
			var num = (decimal)ArgDic["lots"];
			var lotsMode = (int)ArgDic["lotsMode"];
			if (lotsMode == 1)
			{
				var s2 = GetSymbol(tu.MktSymbol);
				num = ((decimal)ArgDic["money"] / (q.Close * s2.multiplier * s2.margin_ratio));
				if (s2.symbol_type == (int)SymbolType.COIN)
				{
					num = (int)(num * 1000) / 1000.0m;
				}
				else
				{
					num = (int)num;
				}
			}

			// 读取参数
			bool useStopLoss = (int)ArgDic["useStopLoss"] == 1;
			decimal stopLossPercent = (decimal)ArgDic["stopLossPercent"];
			bool useTrailingStop = (int)ArgDic["useTrailingStop"] == 1;
			decimal trailingActivatePercent = (decimal)ArgDic["trailingActivatePercent"];
			decimal trailingStopPercent = (decimal)ArgDic["trailingStopPercent"];
			int signalExpiryBars = (int)ArgDic["signalExpiryBars"];
			bool useZhongShuExit = (int)ArgDic["useZhongShuExit"] == 1;
			decimal maxDeviation = (decimal)ArgDic["maxZhongShuDeviation"];
			int tradeCooldownBars = (int)ArgDic["tradeCooldownBars"];
			int zhongShuExitScope = (int)ArgDic["zhongShuExitScope"];
			int minHoldBarsForExit = (int)ArgDic["minHoldBarsForExit"];
			var currentPrice = q.Close;
			int currentBarIndex = tu.QuoteList.Count - 1;

			// [优化3] 移动止损：更新最有利价格
			if (s.Status != 0 && s.EntryPrice > 0)
			{
				if (s.Status == 1)
					s.MaxProfitPrice = Math.Max(s.MaxProfitPrice, currentPrice);
				else if (s.Status == 2)
					s.MaxProfitPrice = s.MaxProfitPrice == 0 ? currentPrice : Math.Min(s.MaxProfitPrice, currentPrice);
			}

			// 止损检查（优先于其他交易逻辑）
			if (s.Status != 0 && s.EntryPrice > 0)
			{
				bool exitTriggered = false;

				if (s.Status == 1)  // 多仓
				{
					// [优化3] 移动止损优先
					if (useTrailingStop && s.MaxProfitPrice > 0)
					{
						decimal profitPercent = (s.MaxProfitPrice - s.EntryPrice) / s.EntryPrice * 100;
						if (profitPercent >= trailingActivatePercent)
						{
							decimal trailingStopPrice = s.MaxProfitPrice * (1 - trailingStopPercent / 100);
							if (currentPrice <= trailingStopPrice)
								exitTriggered = true;
						}
					}
					// 固定止损
					if (!exitTriggered && useStopLoss)
					{
						decimal stopLossPrice = s.EntryPrice * (1 - stopLossPercent / 100);
						if (currentPrice <= stopLossPrice)
							exitTriggered = true;
					}
				}
				else if (s.Status == 2)  // 空仓
				{
					// [优化3] 移动止损优先
					if (useTrailingStop && s.MaxProfitPrice > 0)
					{
						decimal profitPercent = (s.EntryPrice - s.MaxProfitPrice) / s.EntryPrice * 100;
						if (profitPercent >= trailingActivatePercent)
						{
							decimal trailingStopPrice = s.MaxProfitPrice * (1 + trailingStopPercent / 100);
							if (currentPrice >= trailingStopPrice)
								exitTriggered = true;
						}
					}
					// 固定止损
					if (!exitTriggered && useStopLoss)
					{
						decimal stopLossPrice = s.EntryPrice * (1 + stopLossPercent / 100);
						if (currentPrice >= stopLossPrice)
							exitTriggered = true;
					}
				}

				// [优化5] 中枢回归平仓：根据zhongShuExitScope决定哪些买卖点入场的仓位触发中枢回归平仓
				if (!exitTriggered && useZhongShuExit && s.EntryZhongShu != null && s.EntryZhongShu.IsValid
					&& (minHoldBarsForExit <= 0 || currentBarIndex - s.EntryBarIndex >= minHoldBarsForExit))
				{
					bool inScope = false;
					if (zhongShuExitScope == 0)
						inScope = s.EntryBSPointType == BSPointType.Buy3 || s.EntryBSPointType == BSPointType.Sell3;
					else if (zhongShuExitScope == 1)
						inScope = s.EntryBSPointType == BSPointType.Buy2 || s.EntryBSPointType == BSPointType.Sell2
							  || s.EntryBSPointType == BSPointType.Buy3 || s.EntryBSPointType == BSPointType.Sell3;
					else if (zhongShuExitScope == 2)
						inScope = true;
					if (inScope && currentPrice >= s.EntryZhongShu.ZD && currentPrice <= s.EntryZhongShu.ZG)
					{
						// 仅在亏损时触发中枢退出，保护盈利趋势
						bool isLosing = (s.Status == 1 && currentPrice < s.EntryPrice) || (s.Status == 2 && currentPrice > s.EntryPrice);
						if (isLosing)
							exitTriggered = true;
					}
				}

				if (exitTriggered)
				{
					var oriNum = s.Num;
					if (s.Status == 1)
						Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
					else
						Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
					s.Status = 0;
					s.Num = 0;
					s.EntryPrice = 0;
					s.MaxProfitPrice = 0;
					s.EntryZhongShu = null;
					s.EntryBSPointType = null;
					s.LastExitBarIndex = currentBarIndex;
					return;
				}
			}

			// 基于买卖点的交易逻辑
			if (s.BSPoints == null || s.BSPoints.Count == 0)
				return;

			// [优化1] BSPoint优先级选择：从未交易的买卖点中选优先级最高的
			BSPoint bestBSPoint = null;
			int bestPriority = int.MaxValue;
			for (int bsi = s.BSPoints.Count - 1; bsi >= 0; bsi--)
			{
				var bp = s.BSPoints[bsi];
				if (bp.Index <= s.LastTradedBSPointIndex)
					break;

				// [优化4] 信号过期：跳过基于过期一买/一卖派生的二买/二卖
				if (signalExpiryBars > 0)
				{
					if ((bp.Type == BSPointType.Buy2) && s.LastBuy1BarIndex >= 0 && currentBarIndex - s.LastBuy1BarIndex > signalExpiryBars)
						continue;
					if ((bp.Type == BSPointType.Sell2) && s.LastSell1BarIndex >= 0 && currentBarIndex - s.LastSell1BarIndex > signalExpiryBars)
						continue;
				}

				int pri;
				switch (bp.Type)
				{
					case BSPointType.Buy1: case BSPointType.Sell1: pri = 1; break;
					case BSPointType.Buy2: case BSPointType.Sell2: pri = 2; break;
					case BSPointType.Buy3: case BSPointType.Sell3: pri = 3; break;
					default: pri = 99; break;
				}
				if (pri < bestPriority)
				{
					bestPriority = pri;
					bestBSPoint = bp;
				}
			}

			if (bestBSPoint == null)
				return;

			// 标记所有未交易的买卖点为已处理（防止低优先级信号在下一个bar再次触发）
			s.LastTradedBSPointIndex = s.BSPoints[s.BSPoints.Count - 1].Index;

			bool isBuyPoint = bestBSPoint.Type == BSPointType.Buy1 || 
							  bestBSPoint.Type == BSPointType.Buy2 || 
							  bestBSPoint.Type == BSPointType.Buy3;
			bool isSellPoint = bestBSPoint.Type == BSPointType.Sell1 || 
							   bestBSPoint.Type == BSPointType.Sell2 || 
							   bestBSPoint.Type == BSPointType.Sell3;

			// [优化6] 交易冷却期：平仓后N根K线内，仅允许Buy1/Sell1开仓，过滤低质量信号
			if (tradeCooldownBars > 0 && s.LastExitBarIndex >= 0
				&& currentBarIndex - s.LastExitBarIndex <= tradeCooldownBars)
			{
				bool isHighPriority = bestBSPoint.Type == BSPointType.Buy1 || bestBSPoint.Type == BSPointType.Sell1;
				if (!isHighPriority)
					return;
			}

			// [优化7] Buy3/Sell3反转限制：Buy3/Sell3只能从空仓开仓，不能反转已有仓位
			// 避免低优先级信号频繁翻转仓位导致过度交易
			bool noReversalOnBuy3Sell3 = (int)ArgDic["noReversalOnBuy3Sell3"] == 1;
			if (noReversalOnBuy3Sell3)
			{
				bool isBuy3Sell3 = bestBSPoint.Type == BSPointType.Buy3 || bestBSPoint.Type == BSPointType.Sell3;
				if (isBuy3Sell3 && s.Status != 0)
					return;
			}

			// [优化2] 检查开仓点是否偏离中枢太远（放宽至10%）
			if (maxDeviation > 0 && s.CurrentZhongShu != null && s.CurrentZhongShu.IsValid)
			{
				decimal zhongShuCenter = (s.CurrentZhongShu.ZG + s.CurrentZhongShu.ZD) / 2;
				decimal deviationPercent = Math.Abs(q.Close - zhongShuCenter) / zhongShuCenter * 100;
				if (deviationPercent > maxDeviation)
					return;
			}

			if (isBuyPoint && mode != 2)
			{
				if (s.Status == 2)
				{
					var oriNum = s.Num;
					Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, oriNum, period, sendMode);
					s.LastExitBarIndex = currentBarIndex;
				}
				
				if (s.Status != 1)
				{
					s.Status = 1;
					s.Num = num;
					s.EntryPrice = q.Close;
					s.MaxProfitPrice = q.Close;
					s.EntryBarIndex = currentBarIndex;
					s.EntryZhongShu = s.CurrentZhongShu;
					s.EntryBSPointType = bestBSPoint.Type;
					Trade(tu.MktSymbol, OrderType.BUY, q.Close, num, period, sendMode);
				}
			}
			else if (isSellPoint && mode != 1)
			{
				if (s.Status == 1)
				{
					var oriNum = s.Num;
					Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, oriNum, period, sendMode);
					s.LastExitBarIndex = currentBarIndex;
				}
				
				if (s.Status != 2)
				{
					s.Status = 2;
					s.Num = num;
					s.EntryPrice = q.Close;
					s.MaxProfitPrice = q.Close;
					s.EntryBarIndex = currentBarIndex;
					s.EntryZhongShu = s.CurrentZhongShu;
					s.EntryBSPointType = bestBSPoint.Type;
					Trade(tu.MktSymbol, OrderType.SELL, q.Close, num, period, sendMode);
				}
			}
		}
	}
}
