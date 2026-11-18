# DurableText 分段 Base 重构方案

**状态：已选择，待实施。** 将 Text Base 的裸正文改为 **VarUInt32 片段数 K，然后是 K 条独立的非空 Bare string**。首个 writer 按冻结快照的物理 Piece 顺序，一片写一段。Atelia.Binary 增加同步入口 `BareValueWriter.WriteString(ReadOnlySpan<char>)`。本次只改 Base 字面格式，Delta 的结果长度、Copy/New 指令及准确前驱语义沿用 v1。产品现状见[主线合同](mainline-integration-plan.md)，备选项及历史测量见[编码研究](text-encoding-research.md)。

依据为 DG `8675920e` 与其固定的 Storage `9ad39fc51a420e8302468533b82d8bc3a91a403e`。最小完成见证是一篇多片正文经真实仓库写出 K 段 Base，跨段小改写出既有格式 Delta，冷开逐 UTF-16 单元读回并继续编辑，同时两代普通包都能明确拒绝对方的 Text Base。

## 为什么选片段数

每条 Bare string 的头指明编码与 payload 字节长度。读端读 K 次即可知道何时结束，逐段读得的 `string.Length` 足以建立正文的 UTF-16 坐标，不再需要写者声明全文长度、读者再与之比对。代价是解码开始时不知道最终字符数，不能据头部一次精确分配平坦字符串。K 则可以提前帮助选择冷恢复时的内存表示。

设 `V(x)` 为最短 VarUInt32 字节数，N 为全文 UTF-16 单元数。非空段满足 `K ≤ N`，空正文两者同为零。给定**完全相同的分段**，新前缀总能节省 `V(N) − V(K) ≥ 0` 字节。实际常是零或一字节。与不另加字段的单条 Bare string 比，多段容器仍会支付 K 头与各段头。分段选择编码带来的空间差异通常大得多。

| 同一正文的构造（1,024 个“汉”再接 1,024 个 `a`） | K | 以 N 开头的 raw Base | 以 K 开头的 raw Base |
|---|---:|---:|---:|
| 整篇构造 | 1 | 4,100 B | 4,099 B |
| 分别提供中文、英文 | 2 | 3,078 B | 3,077 B |
| 逐位置替换，形成单字符片段 | 2,048 | 5,122 B | 5,122 B |

N 列及快照来源见[上轮研究](text-encoding-research.md)。本轮在仓外用固定普通包的 `WriteVarUInt32`、`WriteString(string)` 和 `BareValueReader` 写读 K 格式，复核上表、空串、ASCII、中英两段和拆开的代理对，共七组正确读回，另有八个畸形输入被拒绝。该验证只覆盖 wire 可表达性与尺寸，新的 span 重载、DG 产品读写及性能留到实施验收。

## Wire 与准入

```text
BaseBody := VarUInt32(K) BareString[0] … BareString[K-1]
```

- 每段独立沿用普通 Bare string 的无损 UTF-8 / UTF-16LE 规则。选码不跨段借字符。高、低代理分居两段时都能独立保真，拼接仍得到原 UTF-16 序列。允许同一内容采用不同分段。writer 写最短头，reader 沿用 Binary 对合法非首选表示、冗余 varint 及非法 UTF-8 的既有语义。
- 唯一的逻辑空表示是 K=0 且 body 在此结束，规范写出 `00`。K>0 时每段至少一个 UTF-16 单元，段内可包含 NUL。没有段数之外的全文长度或段地址，所有段同属一条完整 Base 记录。
- **采用 ABI 5 下新的 typed Base 路由 3，专指新版顶层 Text**。提议的 typed 头是 `05 03`，随后直接跟 BaseBody。旧 route 2 上解析得到的**顶层** Text 须在进入裸正文解码前明确拒绝。route 2 的 List、Array、Dictionary 仍可含 Text 类型参数。解码只有 route 3 可将顶层正文交给新版 Text reader，保留元数据图禁用业务 Text 的现有检查。Schema 继续使用既有 Text 类型表达式，ABI 和类型身份无需全局变号。[typed 编解码](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) · [Text 绑定与恢复](../../../src/DurableGraph/Runtime/Text/TextObjectBinding.cs)
- **拒绝猜格式。** `01 03 41` 在 v1 是一个 UTF-16LE 单元 U+4103，在 v2 是一段 `"A"`。二者均可完整消费。选择新建开发库或从应用来源重新导入，遇到旧 Text 时明确报错。直接传递裸 Base 的低层调用方负责同代的生产与解释。若未来需要独立交换裸 body，应由外层声明其格式。本次没有裸 body 自动识别或在线迁移。新旧 Delta 字节相同，恢复一条持久链时由其准确 Base 确定准入。

| 值 / 切分 | v2 裸 Base 的规范字节 |
|---|---|
| 空正文 | `00` |
| 一段 `"A"` | `01 03 41` |
| 一段 `"AB"` | `01 05 41 42` |
| 两段 `"A"`、`"B"` | `02 03 41 03 42` |
| 两段 `"A"`、`"中"` | `02 03 41 02 2D 4E` |
| 分开的 U+D83D、U+DE00 | `02 02 3D D8 02 00 DE` |

## 施工顺序

### 1. Atelia.Binary 增加单切片写入

保留 `WriteString(string)` 对 null 的既有处理，以及 `WriteString(in StringEncodingPlan)` 对已选编码和原不可变 string 的复用。把现有择短计算提为接受 span 的内部方法，让 string 和新入口共享同一 header 算法及有界输出内核。span 重载在一次调用内选择、写头、写 payload，输入 backing 在返回前须稳定。不创建 substring，不把 span 存入可逸出的 plan。现有两条 payload 写入函数已接受 span。[选码](../../../../atelia-storage/src/Binary/BareValueEncoding.Strings.cs) · [writer](../../../../atelia-storage/src/Binary/BareValueWriter.Strings.cs) · [既有 goldens](../../../../atelia-storage/tests/Binary.Tests/StringTests.cs)

上游先证明对任意一个切片，新入口的输出与现有 string 入口对同一组 UTF-16 单元的输出逐字节相等。覆盖空 span、中间切片、孤立代理和切片外恰有配对字符、大于内部输出缓冲的输入，以及现有 writer/plan 的错误语义。通过 Storage 的源码和包流程产生新候选，把 DG 的六包版本与源码 revision 一起固定到该候选。[依赖约定](../../storage-dependency.md) · [当前 pin](../../../eng/StorageDependency.props)

### 2. DG 写入与规划

在 `TextBodyCodec.PrepareBase` 上，从**同一个冻结视图**取得 `PieceCount` 和每个非空 `piece.Memory.Span`，写 K，再依次调用 span 重载。K 是实际输出的段数。未来若 writer 合段，头部须随写入计划取数。当前保存接受阶段的 Rebind / flatten 仍以其既有条件安装，不能提前整理 live editor 来改变此次捕获的 K。将新 typed 头接到即时与延迟两条 Base 路径，包含初次保存、强制 Base、未变对象的维护写和 Event 全 Base。[Text codec](../../../src/DurableGraph/Runtime/Text/TextBodyCodec.cs) · [编辑器接受](../../../src/DurableGraph/DurableText.cs) · [延迟 Base](../../../src/DurableGraph.Persistence/PreparedTypedBase.cs)

图级规划沿用廉价 O(1) 估值。首版建议 raw 取 `V(K) + (long)K + 2L*N`，每段头先估 1 B，typed 头与记录封套由上层各加一次。这个数既可能偏高也可能偏低。删除现行基于精确 UTF-16LE 长度的 `EstimateBaseSize > Array.MaxLength` 硬拒绝。实际写出时仍按真实输出检查整数转换、缓冲和整批帧的容量，发布失败沿既有边界处理。选择小 Delta 或跳过未变文本时，估值不得遍历片段或重新选码。安装 H 继续使用实际记录字节。Delta New 仍是 v1 编码，其长度不能直接当作新版 Base 精确值。[图级选择](../../../src/DurableGraph.Persistence/ObjectWritePlanner.cs) · [真实字节的安装](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs)

### 3. DG 读端一次顺序消费

1. 从有界 body 读取 VarUInt32 K。一条非空 Bare string 至少占 **2 B**（至少 1 B 头、1 B payload），若 `K > RemainingCount / 2` 直接拒绝。先作这个宽度安全的比较，再转 int。K=0 时要求正好耗尽，直接返回空正文。
2. 读恰好 K 次 `ReadString()`。沿用 Binary 的逐段头长度、物理剩余字节和严格 UTF-8 检查。拒绝解得的空段，用实际 `part.Length` 累计逻辑长度并遵守现有 Text / CLR 长度边界。最后 `EnsureFullyConsumed()`。这次累加本来就用于建立 TextSnapshot。它无需再和盘上的第二个长度字段核对。缺段、越界、非法内容与尾部多余数据均按读取失败处理，失败不修改任何已完成帧。[Binary reader](../../../../atelia-storage/src/Binary/BareValueReader.Strings.cs)
3. K=1 可直接使用解出的 string。K 较小时，为每个非空结果建立一段解码 backing，通过现有 `FromOwnedPieces` 建立冻结视图。K 较大时，从一开始将逐段解出的 string 依次追加到 StringBuilder，再一次物化，避免保留 K 个 string 和描述符。两条路都不按未经验证的 K 预分配大列表或字符缓冲。首版可以沿用现有冷重放的 4,096 片内存界限作为分流点，超过仍是合法输入。大 K 分支仍有每段临时 string、builder 和一次终态物化的费用，应单独量取。若该分支成为实测热点，再评估 Binary 的直接解码到目标 API。[视图构造](../../../src/DurableGraph/Runtime/Text/TextPieceView.cs) · [当前冷重放](../../../src/DurableGraph/Runtime/Text/TextBodyCodec.cs)

若写入错误或损坏恰好形成另一条合法正文，读端不能据 K 推出原意。额外存 N 也只能发现其中会改变总长的一部分。这里检查的是序列是否能完整解释。对象版本仍校验准确前驱，存储层继续按既有协议校验帧。

## 实施验收

| 范围 | 最小可观察结果 |
|---|---|
| Binary API 与 DG Base | 新 span 与旧 string goldens 一致。经真实捕获写出上述 v2 goldens。127/128 段验证 K 的 varint 边界，单个长段验证外层头只按 K、段内 Bare 头按该段 payload 计。不同编辑史可产生不同 Base，读回的 UTF-16 单元逐一相等 |
| 序列与冷恢复 | 空段的两种 Bare 表示、未终止头、巨 K 配短体、少段、尾部、多余合法段、截断字面和非法 UTF-8 都失败。4,096 / 4,097 小段均恢复，随后的 Copy 横跨旧段仍正确。跨段代理、孤立代理、NUL 及 CR/LF 原样恢复 |
| 表示选择与图语义 | 将“估值等于编码长度”的旧测试改为分别验证软估值、实际字节与 H。小改选 Delta 时全文 Base 编码计数为零，既有 Delta golden 逐字节相等，强制 Base、未变维护、Event、故障后丢弃、共用 CLR builder 仍满足原合同 |
| 准入与交付 | 使用**支持 Text v1** 的旧普通包和新普通包，分别以全新进程建立真实含 Text 的图。各自同代能冷开续写，双向交叉在实际加载目标 Text 时明确失败。补同字节异义反例、非 Text 对照以及嵌套 Text 的类型解析。记录 DG 和 Storage 的准确包来源 |
| 成本对照 | 固定相同冻结快照，比较 v1 与 v2 的首次和强制 Base、后续小 Delta 以及冷开。报告 K、正文单元数、实际 Base/Delta 字节、选择结果、分配和时间。少量大段和大量小段分列。区分真实策略对照与固定表示对照，估值不同可能改变所选记录 |

验收入口为 [TextBodyCodecTests](../../../tests/DurableGraph.Tests/TextBodyCodecTests.cs)、[planner 回归](../../../tests/DurableGraph.Persistence.Tests/ObjectRevisionPlannerTests.cs)、[Repository 集成](../../../tests/DurableGraph.Persistence.Tests/DurableTextIntegrationTests.cs)、[产品成本见证](../../../tests/DurableGraph.Persistence.Tests/DurableTextProductProbeTests.cs)和[普通包消费者](../../../experiments/PackageConsumerProbe/DurableTextConsumer/README.md)。完成两个仓库对应的构建、相关测试及普通包验证后，再把[主线合同](mainline-integration-plan.md)更新为实施事实。
