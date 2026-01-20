<p align="center">
  <img src="logo.ico" alt="泉金盈 Logo" width="120" height="120">
</p>

<h1 align="center">泉金盈 - SDK</h1>

<p align="center">
  <strong>专业量化策略开发工具包</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-v1.0.0-blue.svg" alt="Version">
  <img src="https://img.shields.io/badge/.NET-9.0-purple.svg" alt=".NET">
  <img src="https://img.shields.io/badge/license-Commercial-red.svg" alt="License">
</p>

---

## 目录

- [概述](#概述)
- [泉金盈客户端功能](#泉金盈客户端功能)
- [SDK 功能特性](#sdk-功能特性)
- [系统要求](#系统要求)
- [快速开始](#快速开始)
- [核心类说明](#核心类说明)
- [API 参考](#api-参考)
- [示例代码](#示例代码)
- [技术支持](#技术支持)

---

## 概述

**泉金盈策略开发 SDK** 是泉金盈量化交易平台的官方策略开发工具包，为开发者提供标准化的策略开发接口。通过本 SDK，您可以快速开发、调试并部署自定义量化交易策略至泉金盈平台。

### 核心优势

- **标准化接口** - 统一的策略开发规范，降低学习成本
- **实时数据** - 毫秒级行情推送，支持多周期 K 线数据
- **多市场支持** - 覆盖加密货币、股票、期货等交易市场
- **图表绑定** - 内置绑定绘图接口，策略信号可视化展示
- **技术指标** - 集成 Skender.Stock.Indicators 指标库
- **无偏离设计** - 交易信号与画图必须在当根K线下更新，确保回测与实时一致性

---

## 泉金盈客户端功能

本 SDK 需配合泉金盈客户端使用。泉金盈客户端是一款专业的智能量化交易平台，提供以下核心功能：

### 自选列表

| 功能 | 描述 |
|------|------|
| **品种管理** | 添加、删除、排序自选品种 |
| **实时行情** | 实时推送价格变动与涨跌幅 |
| **快速筛选** | 支持按名称、代码快速过滤 |

<p align="center">
  <img src="images/zixuan.png" alt="自选列表" width="800">
</p>

### 策略中心

| 功能 | 描述 |
|------|------|
| **策略市场** | 海量公开策略，一键订阅使用 |
| **策略回测** | 历史数据回测验证策略效果 |
| **参数调优** | 智能参数优化与调参建议 |
| **策略上传** | 支持上传自研策略至云端 |

<p align="center">
  <img src="images/celuezhongxin.png" alt="策略中心" width="800">
</p>

### 我的策略

| 功能 | 描述 |
|------|------|
| **云端策略** | 云端托管运行的策略管理 |
| **本地策略** | 本地开发调试的策略管理 |
| **运行控制** | 启动、暂停、停止策略运行 |
| **绩效报告** | 详细的策略绩效与收益报告 |
| **交易明细** | 完整的交易订单记录查询 |

<p align="center">
  <img src="images/wodecelue1.png" alt="我的策略" width="800">
</p>
<p align="center">
  <img src="images/wodecelue2.png" alt="策略详情" width="800">
</p>
<p align="center">
  <img src="images/wodecelue3.png" alt="策略图表" width="800">
</p>
<p align="center">
  <img src="images/wodecelue4.png" alt="交易明细" width="800">
</p>

### 实时监控

| 功能 | 描述 |
|------|------|
| **持仓汇总** | 按品种汇总显示持仓情况 |
| **盈亏分析** | 实时浮动盈亏与收益率计算 |
| **策略关联** | 快速跳转至关联策略详情 |

<p align="center">
  <img src="images/shishijiankong1.png" alt="实时监控" width="800">
</p>
<p align="center">
  <img src="images/shishijiankong2.png" alt="监控详情" width="800">
</p>

### 消息中心

| 功能 | 描述 |
|------|------|
| **系统通知** | 平台公告与系统消息 |
| **交易提醒** | 策略信号与成交通知 |
| **消息管理** | 已读标记与消息删除 |

<p align="center">
  <img src="images/xiaoxizhongxin.png" alt="消息中心" width="800">
</p>

### 系统设置

| 功能 | 描述 |
|------|------|
| **语言切换** | 中文/英文界面切换 |
| **版本更新** | 在线检查与下载更新 |
| **问题反馈** | 建议、Bug 反馈提交 |

<p align="center">
  <img src="images/xitongshezhi.png" alt="系统设置" width="800">
</p>

---

## SDK 功能特性

| 功能 | 描述 |
|------|------|
| **K 线回调** | 支持 1 秒至日线多周期 K 线数据回调 |
| **交易下单** | 买入、卖出、平仓等订单类型支持 |
| **品种查询** | 获取交易品种详细信息 |
| **图表绑定** | 支持绑定曲线、矩形、文本等图形元素 |
| **参数配置** | 灵活的策略参数定义与配置 |

### 支持的周期

| 周期 | 枚举值 |
|------|--------|
| 1 秒 | `Period.TIME_1S` |
| 1 分钟 | `Period.TIME_1M` |
| 5 分钟 | `Period.TIME_5M` |
| 15 分钟 | `Period.TIME_15M` |
| 30 分钟 | `Period.TIME_30M` |
| 1 小时 | `Period.TIME_1H` |
| 2 小时 | `Period.TIME_2H` |
| 4 小时 | `Period.TIME_4H` |
| 日线 | `Period.TIME_1D` |

---

## 系统要求

| 项目 | 要求 |
|------|------|
| **.NET Runtime** | 9.0 或更高版本 |
| **操作系统** | Windows 10+ / macOS 10.15+ / Linux |
| **泉金盈客户端** | 需安装并运行泉金盈桌面客户端 |

---

## 快速开始

### 1. 克隆仓库

```bash
git clone https://github.com/lld1995/QjySDK.git
cd QjySDK
```

### 2. 还原依赖

```bash
dotnet restore
```

### 3. 编写策略

```csharp
using QjySDK.Stg;
using Model;

public class MyStrategy : StgBase
{
    public MyStrategy(string id) : base(id) { }

    public override StgDesc GetStgDesc()
    {
        return null; // 返回策略描述，或 null 使用默认配置
    }

    public override void OnBar(EnumDef.Period period, TableUnit tu, bool isFinal, SkQuote tq)
    {
        // 在此处理 K 线数据
        if (isFinal)
        {
            Console.WriteLine($"{tu.MktSymbol} - {period}: Close={tq.Close}");
        }
    }
}
```

### 4. 运行策略

```csharp
var strategy = new MyStrategy("my-strategy-id");
await strategy.Run();
```

> **注意**: 运行策略前请确保泉金盈客户端已启动并登录。

### 5. 在客户端创建本地策略

在泉金盈客户端中创建本地策略，关联您开发的策略程序：

<p align="center">
  <img src="images/bendicelue.png" alt="本地策略创建" width="800">
</p>

---

## 核心类说明

### StgBase

策略基类，所有自定义策略需继承此类。

| 成员 | 类型 | 描述 |
|------|------|------|
| `Id` | `string` | 策略唯一标识 |
| `ArgDic` | `Dictionary<string, object>` | 策略参数字典 |
| `GetStgDesc()` | 抽象方法 | 返回策略描述信息 |
| `OnBar()` | 虚方法 | K 线数据回调 |
| `OnPeriodEnd()` | 虚方法 | 周期结束回调 |
| `OnGlobalIndicator()` | 虚方法 | 全局指标计算回调 |
| `OnSendOrder()` | 虚方法 | 下单回调 |

### StgDesc

策略描述类，用于定义策略参数与配置。

| 属性 | 类型 | 描述 |
|------|------|------|
| `ArgDic` | `Dictionary<string, object>` | 参数默认值 |
| `ArgDescDic` | `Dictionary<string, ArgDesc>` | 参数描述 |
| `MaxSymbolNum` | `int` | 最大品种数量 |
| `SubChartNum` | `int` | 副图数量 |
| `ColorDic` | `Dictionary<string, string>` | 颜色配置 |

### TableUnit

K 线数据容器。

| 属性 | 类型 | 描述 |
|------|------|------|
| `MktSymbol` | `string` | 品种标识 |
| `Period` | `Period` | 周期 |
| `QuoteList` | `List<SkQuote>` | K 线数据列表 |

### SkQuote

单根 K 线数据。

| 属性 | 类型 | 描述 |
|------|------|------|
| `Open` | `decimal` | 开盘价 |
| `High` | `decimal` | 最高价 |
| `Low` | `decimal` | 最低价 |
| `Close` | `decimal` | 收盘价 |
| `Volume` | `decimal` | 成交量 |

### OnBar 回调说明

`OnBar` 方法的 `isFinal` 参数含义：

| isFinal | 说明 |
|---------|------|
| `true` | K 线已完结，数据会追加至 `QuoteList` |
| `false` | 实时 Tick 数据，仅支持实时行情，不追加至历史列表 |

> **注意**: 当 `isFinal=false` 时为实时 Tick 推送，适用于需要逐笔行情的高频策略。

---

## API 参考

### 交易方法

```csharp
// 下单交易
void Trade(string mktSymbol, OrderType ot, decimal price, decimal num, Period p, int sendMode)
```

**参数说明**:
- `mktSymbol`: 品种标识
- `ot`: 订单类型 (BUY / SELL / BUY_TO_COVER / SELL_TO_COVER)
- `price`: 价格
- `num`: 数量
- `p`: 周期
- `sendMode`: 发送模式

### 绑定方法

```csharp
// 绑定图形元素
void Plot(string chartName, string name, PlotType pt, double? val, object extra = null)
```

**PlotType 枚举**:
- `LINE` - 直线
- `CURVE` - 曲线
- `RECTANGLE` - 矩形
- `XLINE` - 水平线
- `POINT` - 点
- `TEXT` - 文本
- `LINE_SEGMENT` - 线段

### 品种查询

```csharp
// 异步获取品种信息
Task<Symbol> GetSymbolAsync(string mktSymbol)

// 同步获取品种信息
Symbol GetSymbol(string mktSymbol)
```

---

## 策略示例展示

以下是基于本 SDK 开发的策略在泉金盈客户端中的运行效果：

### MACD-布林带偏离策略

<p align="center">
  <img src="images/MACD-布林带偏离策略.png" alt="MACD-布林带偏离策略" width="800">
</p>

### 三重因子共振策略

<p align="center">
  <img src="images/三重因子共振.png" alt="三重因子共振策略" width="800">
</p>

### RSI 背离与布林带策略

<p align="center">
  <img src="images/相对强弱指数（RSI）背离与布林带.png" alt="RSI背离与布林带策略" width="800">
</p>

### 缠论笔交易策略

<p align="center">
  <img src="images/缠论笔交易策略.png" alt="缠论笔交易策略" width="800">
</p>

---

## 示例代码

### 简单均线策略

```csharp
public class MaStrategy : StgBase
{
    public MaStrategy(string id) : base(id) { }

    public override StgDesc GetStgDesc()
    {
        var desc = new StgDesc();
        desc.ArgDic["period"] = 20;
        return desc;
    }

    public override void OnBar(EnumDef.Period period, TableUnit tu, bool isFinal, SkQuote tq)
    {
        if (!isFinal || tu.QuoteList.Count < 20) return;

        var closes = tu.QuoteList.TakeLast(20).Select(q => (double)q.Close);
        var ma = closes.Average();

        Plot("main", "MA20", PlotType.CURVE, ma);

        if (tq.Close > (decimal)ma)
        {
            Trade(tu.MktSymbol, OrderType.BUY, tq.Close, 1, period, 0);
        }
    }
}
```

---

## 技术支持

| 渠道 | 联系方式 |
|------|----------|
| **官方网站** | [https://www.quanjinying.com](https://www.quanjinying.com) |
| **技术支持** | support@quanjinying.com |
| **商务合作** | business@quanjinying.com |

---

## 许可证

Copyright © 2024-2026 泉金盈 版权所有

本 SDK 为商业软件，仅限泉金盈平台注册用户使用。未经授权不得复制、修改或分发。

---

<p align="center">
  <sub>Powered by 泉金盈</sub>
</p>
