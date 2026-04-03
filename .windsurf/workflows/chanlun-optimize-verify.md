---
description: 缠论策略优化、对比验证的完整流程（适用于ChanLun和ChanLunBi）
---

# 缠论策略优化与对比验证流程

## 前置条件
- 项目路径: `e:\project\qjy\QjySdk`
- 测试项目: `QjySDK.Tests`
- 策略文件: `Stg\Pattern\ChanLun.cs`（线段级别）、`Stg\Pattern\ChanLun_bi.cs`（笔级别）
- 回测引擎: `QjySDK.Tests\BacktestEngine.cs`
- 测试文件: `QjySDK.Tests\ChanLunBacktestTests.cs`
- 基准结果: `QjySDK.Tests\baseline_results.md`
- 数据源: TDEngine，通过 `TDEngineDataLoader.LoadKlines(rawSymbol, period, limit)` 加载
- 测试品种: BTCUSDT(币), SZSE.000001(平安银行), SHSE.510300(沪深300ETF), SHFE.au2605(黄金期货)
- 测试周期: TIME_1D(日线365根), TIME_15M(15分钟线~6000-8640根)

## 步骤1: 记录当前基准（如果还没有）

运行基准测试，记录当前所有策略的指标：

// turbo
```bash
cd e:\project\qjy\QjySdk && dotnet test QjySDK.Tests --no-restore --filter "Test_ChanLun_Baseline_All" --logger "console;verbosity=detailed"
```

记录以下指标到 `baseline_results.md`：
- 交易次数(Trades)、胜率(WinRate)、总利润(Profit)、夏普率(Sharpe)
- 买卖点统计: Buy1, Sell1, Buy2, Sell2, Buy3, Sell3
- 按品种×周期×策略(ChanLun/ChanLunBi) 分组

## 步骤2: 诊断分析（定位瓶颈）

运行诊断测试查看中枢结构：

// turbo
```bash
cd e:\project\qjy\QjySdk && dotnet test QjySDK.Tests --no-restore --filter "Test_Diagnose_ZhongShu_Structure" --logger "console;verbosity=detailed"
```

关注以下问题：
- 每个品种×周期有多少个中枢(ZhongShu)？
- 相邻中枢是否满足趋势条件（strictDown/strictUp/relaxDown/relaxUp）？
- 中枢的LeaveDirection是否有值？LeaveStroke/LeaveSegment是否存在？
- MACD面积是否被正确计算？

常见瓶颈：
- **1D数据中枢太少**（<2个）→ 无法形成趋势 → 需要盘整背驰
- **趋势条件太严格**（要求ZG<ZD非重叠）→ 需要放宽到中心比较
- **背驰方向判断错误** → 检查 `IsTrendDivergenceStrict` 中 `IsUp` 比较逻辑
- **maxZhongShuDeviation** 过滤太多信号 → 考虑放宽阈值

## 步骤3: 实施优化

优化点必须同时修改两个文件（保持一致性）：
- `Stg\Pattern\ChanLun.cs` — 线段级别，ZhongShu基于Segment构建，离开用LeaveSegment
- `Stg\Pattern\ChanLun_bi.cs` — 笔级别，ZhongShu基于Stroke构建，离开用LeaveStroke

### 修改规范
1. 新增的识别方法（如 `IdentifyConsolidationBuy1`）需要标记为 `internal`
2. 方法参数统一接收 `State state`
3. 返回 `BSPoint` 或 `null`
4. 在 `UpdateBSPoints` 中集成：先尝试原方法，返回null时再尝试新方法作为fallback
5. `LastBuy1ZhongShu`/`LastSell1ZhongShu` 赋值时需要处理 fallback 情况（趋势中枢为null时取最后一个中枢）

### 构建验证

// turbo
```bash
cd e:\project\qjy\QjySdk && dotnet build QjySDK.Tests --no-restore 2>&1 | Select-String "error CS"
```

确保零编译错误。

## 步骤4: 运行对比测试

// turbo
```bash
cd e:\project\qjy\QjySdk && dotnet test QjySDK.Tests --no-restore --filter "Test_ChanLun_Baseline_All" --logger "console;verbosity=detailed"
```

## 步骤5: 结果对比分析

将新结果与 `baseline_results.md` 中的基准对比，关注：

### 必须满足的约束（红线）
- **不降低已有盈利策略的利润**（特别是 ChanLunBi BTCUSDT 15M）
- **不降低已有盈利策略的夏普率**
- **不减少已有的BSPoint信号数量**

### 期望的改进
- Buy1/Sell1 数量增加（特别是1D周期）
- Buy2/Sell2 数量增加（依赖Buy1/Sell1的产生）
- 交易次数增加
- 利润改善或亏损减少
- 夏普率改善

### 对比表格格式
```
| 指标 | Baseline | Post-Opt | 变化 |
|------|----------|----------|------|
| Buy1 | X | Y | +Z% |
```

## 步骤6: 更新基准文件

将对比结果更新到 `QjySDK.Tests\baseline_results.md`：
- 保留原始BASELINE数据
- 新增POST-OPT数据表格
- 在Key Findings中记录具体变化和分析

## 已实施的优化历史

### 优化1: 放宽趋势检测 (HasDownTrend/HasUpTrend)
- **方法**: 两阶段检查 — 先严格非重叠(ZG<ZD)，再放宽到中心下移+GG/DD递降
- **效果**: 对15M无影响（已满足严格条件），对1D无影响（只有1个中枢，2个中枢才能判断趋势）

### 优化2: 盘整背驰 (IdentifyConsolidationBuy1/Sell1)
- **方法**: 只需1个中枢，离开方向确认 + 离开笔/段突破中枢边界 + MACD面积背驰
- **效果**:
  - ChanLun segment: SZSE.000001 15M 扭亏为盈(+1.08)，SHSE.510300 15M 亏损减少62%
  - ChanLun segment: Buy1信号大幅增加(+57%~+83%)，Buy2信号从0到46/67
  - ChanLunBi stroke: 小幅增加（已有较多中枢，fallback较少触发）

## 关键代码位置参考
- `UpdateBSPoints` — 买卖点识别主入口，所有新识别逻辑在此集成
- `HasDownTrend` / `HasUpTrend` — 趋势判断，决定是否满足趋势背驰条件
- `IsTrendDivergenceStrict` — MACD面积背驰判断核心逻辑
- `IdentifyBuy1` / `IdentifySell1` — 趋势背驰一买/一卖（需要≥2个中枢）
- `IdentifyConsolidationBuy1` / `IdentifyConsolidationSell1` — 盘整背驰一买/一卖（只需1个中枢）
- `IdentifyBuy2/Sell2` — 二买/二卖（依赖LastBuy1/LastSell1）
- `IdentifyBuy3/Sell3` — 三买/三卖（依赖中枢离开方向）
- `State` 类 — 包含ZhongShus, BSPoints, LastBuy1, LastSell1, LastBuy1ZhongShu等状态
- `BacktestEngine.RunSingleSymbol` — 单品种回测入口，通过逐bar调用OnBar驱动策略
- `GetBSPointCounts()` — 提取买卖点统计的公开方法
