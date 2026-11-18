# DurableText 分段 Base 重构方案

**状态：N 前缀方案已选，读端机制已验证，产品待实施。** Text Base 的裸正文由一个 VarUInt32 的 UTF-16 单元总数 N 和若干非空 Bare string 组成，段数隐含在有界 payload 中。writer 从同一冻结视图取得 N，首版按 Piece 顺序一片写一段。reader 预分配恰好 N 个单元的最终 string，逐段解码到调用方 span。Delta 继续沿用现有结果长度、Copy/New 和准确前驱语义。[当前产品合同](mainline-integration-plan.md) · [编码研究](text-encoding-research.md)

依据为 DG `10837563` 与固定的 Storage `9ad39fc51a420e8302468533b82d8bc3a91a403e`。实施验收以新库完成“多片 Base → 跨片小改 Delta → 冷开续写”，开发期旧数据可重建。仓外验证已覆盖 Binary 字面值到最终 string 的路径，DG 图保存、冷开及新普通包仍待实施验收。

## 选择 N 的依据

每段 Bare 头给出编码与 payload 字节数，DG 的 Base body 也有独立边界。因此 reader 能按字节走到结尾，N 可以专门用来决定字符目标的大小。它同时允许另一种读法，逐段保留解码 backing，最后核对单元总数。首版产品平坦恢复符合当前 ReadBase 行为。N=0 时直接验证 body 已耗尽，因为 `string.Create(0, …)` 跳过回调。

上一选项 K 表示片段数。对非空段总有 K ≤ N，换用 N 付出的空间是两个 varint 头的长度差。对之前的 2,048 单元样本，分成 1 / 2 / 2,048 段时，N 方案分别为 4,100 / 3,078 / 5,122 B，比 K 方案多 1 / 1 / 0 B。采用 N 可免去为得到平坦目标大小而预扫各段，也无需持有临时段字符串。K 仅作为内存视图已有的统计参与估值与测量。[尺寸与样本](text-encoding-research.md)

### 可复验的读端证据

在 .NET SDK 10.0.201、运行时 10.0.5 上，使用固定版 Atelia.Binary 普通包的真实 writer、reader、varint 与原始字节切片接口。实验以一个 `ref BareValueReader` 扩展方法模拟拟增加的实例重载，用 `BareValueReader` 的值副本作为 `string.Create` 的状态，静态回调内直接写最终字符串。两条路径都接收事先编码的同一 byte[]，都分配同样的最终 string。对照另用现有 ReadString() 生成每段 string，再复制进最终目标。8 次预热后量取 32 次的线程托管分配均值，并逐次校验完整内容。

| UTF-16 单元数 | 片段数 | 直接解码 B/次 | 逐段 string 再复制 B/次 | 仅创建最终 string B/次 |
|---:|---:|---:|---:|---:|
| 4,096 | 64 | 8,216 | 17,944 | 8,216 |
| 4,096 | 1,024 | 8,216 | 40,984 | 8,216 |
| 4,096 | 4,096 | 8,216 | 106,520 | 8,216 |

这组对照只测已在内存中的 Base 正文恢复为平坦 string，写入、帧读取、TextSnapshot 包装和 Delta 重放均在计量之外。单段正文若直接采用现有 ReadString() 的返回值也可以只分配最终 string。多段拼成一个平坦结果时才体现省去中间对象和复制的收益。

可将[单文件验证程序](span-decode-probe.cs)复制到含该 pin 的本地 NuGet.Config 旁，运行 `dotnet run --file span-decode-probe.cs -c Release`。其中覆盖两种实际编码、冗余头、合法非首选表示、代理对跨段、孤立代理、NUL/BOM、空值、N 过大或过小、尾部空段、非法/截断 UTF-8、剩余字符空间不足、单段字节限额，以及读取失败时的游标位置。

## Wire 与准入

```text
BaseBody := VarUInt32(N) NonEmptyBareString*     // 恰好耗尽这个 body
N        := 各段解码出的 UTF-16 单元数之和
```

- 每段独立沿用普通 Bare string 的无损 UTF-8 / UTF-16LE 规则。选码不跨段借字符。高、低代理分居两段时都能独立保真，拼接仍得到原 UTF-16 序列。允许同一内容采用不同分段。writer 写最短头，reader 沿用 Binary 对合法非首选表示、冗余 varint 及非法 UTF-8 的既有语义。
- 空正文是 N=0 且 body 在此结束，规范写出 `00`。每段至少一个 UTF-16 单元，段内可包含 NUL。盘上省略段数和段地址，所有段同属一条完整 Base 记录。
- **采用 ABI 5 下新的 typed Base 路由 3，专指新版顶层 Text**。提议的 typed 头是 `05 03`，随后直接跟 BaseBody。旧 route 2 上解析得到的**顶层** Text 须在进入裸正文解码前明确拒绝。route 2 的 List、Array、Dictionary 仍可含 Text 类型参数。解码只有 route 3 可将顶层正文交给新版 Text reader，保留元数据图禁用业务 Text 的现有检查。Schema 继续使用既有 Text 类型表达式，ABI 和类型身份无需全局变号。[typed 编解码](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) · [Text 绑定与恢复](../../../src/DurableGraph/Runtime/Text/TextObjectBinding.cs)
- **拒绝猜格式。** `01 03 41` 在 v1 是一个 UTF-16LE 单元 U+4103，在 v2 是一段 `"A"`。二者均可完整消费。选择新建开发库或从应用来源重新导入，遇到旧 Text 时明确报错。直接传递裸 Base 的低层调用方负责同代的生产与解释。若未来需要独立交换裸 body，应由外层声明其格式。本次没有裸 body 自动识别或在线迁移。新旧 Delta 字节相同，恢复一条持久链时由其准确 Base 确定准入。

| 值 / 切分 | v2 裸 Base 的规范字节 |
|---|---|
| 空正文 | `00` |
| 一段 `"A"` | `01 03 41` |
| 一段 `"AB"` | `02 05 41 42` |
| 两段 `"A"`、`"B"` | `02 03 41 03 42` |
| 两段 `"A"`、`"中"` | `02 03 41 02 2D 4E` |
| 分开的 U+D83D、U+DE00 | `02 02 3D D8 02 00 DE` |

## 施工顺序

### 1. Atelia.Binary 提供同步 span 读写

保留 `WriteString(string)` 对 null 的既有处理，以及 `WriteString(in StringEncodingPlan)` 对已选编码和原不可变 string 的复用。把现有择短计算提为接受 span 的内部方法，让 string 和新入口共享同一 header 算法及有界输出内核。span 重载在一次调用内选择、写头、写 payload，输入 backing 在返回前须稳定。不创建 substring，不把 span 存入可逸出的 plan。现有两条 payload 写入函数已接受 span。[选码](../../../../atelia-storage/src/Binary/BareValueEncoding.Strings.cs) · [writer](../../../../atelia-storage/src/Binary/BareValueWriter.Strings.cs) · [既有 goldens](../../../../atelia-storage/tests/Binary.Tests/StringTests.cs)

读入口采用 `int ReadString(scoped Span<char> destination, int maxPayloadByteCount = int.MaxValue)`，返回写入的 UTF-16 单元数，成功时仅改写目标的前缀，游标越过恰好一条 Bare string。普通 Binary 允许返回 0，DG 在段这一层拒绝空值。与返回 string 的入口共用读取头、限额及取得有界 payload 的逻辑。源在调用期间须稳定，目标不得与输入字节重叠。

- UTF-16LE 先检查 `payload.Length / 2` 是否能放进目标，再按小端读出原始单元。UTF-8 使用 `Utf8.ToUtf16`，设置 `replaceInvalidSequences: false` 和 `isFinalBlock: true`。一次转码取得消耗字节与写入单元，只在状态为 Done 且输入全耗尽时成功。两条路都无需 GetCharCount 预扫或中间字符串。
- 在 reader 副本上读取，成功才提交游标。字节限额、截断、非法编码沿用现有错误语义。目标过短报告针对 destination 的 ArgumentException。单遍 UTF-8 转码失败时目标内可能已有前缀，失败不承诺回滚目标。DG 使用 string.Create 的私有目标，回调抛错时未完成的 string 不会交给调用方。不要依赖短目标之后尚未读取的 UTF-8 后缀已经过验证。[Binary 原读端](../../../../atelia-storage/src/Binary/BareValueReader.Strings.cs)

上游测试需对照既有 string 入口的逐字节/逐单元结果，覆盖切片边界、空 span、代理项和输入/输出边界，将已验证的游标与失败合同写入实例 API 测试。通过 Storage 的源码和包流程产生新候选，把 DG 的六包版本与源码 revision 一起固定到该候选。[依赖约定](../../storage-dependency.md) · [当前 pin](../../../eng/StorageDependency.props)

### 2. DG 写入与规划

在 `TextBodyCodec.PrepareBase` 上，从**同一个冻结视图**取得逻辑长度 N 和每个非空 `piece.Memory.Span`，写 N，再依次调用 span 重载。未来若 writer 合段，N 保持不变。当前保存接受阶段的 Rebind / flatten 仍以其既有条件安装，不能提前整理 live editor 来改变此次捕获的内容和切片。将新 typed 头接到即时与延迟两条 Base 路径，包含初次保存、强制 Base、未变对象的维护写和 Event 全 Base。[Text codec](../../../src/DurableGraph/Runtime/Text/TextBodyCodec.cs) · [编辑器接受](../../../src/DurableGraph/DurableText.cs) · [延迟 Base](../../../src/DurableGraph.Persistence/PreparedTypedBase.cs)

图级规划沿用廉价 O(1) 估值。首版建议 raw 取 `V(N) + (long)K + 2L*N`，每段头先估 1 B，typed 头与记录封套由上层各加一次。这个数既可能偏高也可能偏低。删除现行基于精确 UTF-16LE 长度的 `EstimateBaseSize > Array.MaxLength` 硬拒绝。实际写出时仍按真实输出检查整数转换、缓冲和整批帧的容量，发布失败沿既有边界处理。选择小 Delta 或跳过未变文本时，估值不得遍历片段或重新选码。安装 H 继续使用实际记录字节。Delta New 仍是 v1 编码，其长度不能直接当作新版 Base 精确值。[图级选择](../../../src/DurableGraph.Persistence/ObjectWritePlanner.cs) · [真实字节的安装](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs)

### 3. DG 冷读直接构造最终字符串

1. 在有界 Base body 上读取 VarUInt32 N，按原有 Text/CLR 支持上限检查。另验 `N <= RemainingCount` 后才分配，因为两种编码每个输出单元平均至少占一个 payload 字节（非 BMP 的 UTF-8 为 4 字节、2 个单元）。这是必要的廉价条件，完整形状在解码时验证。N=0 单独要求 reader.End。
2. 对非空文本，调用 `string.Create(N, unread, static (destination, state) => …)`。在回调中反复把目标剩余 span 交给 Binary 新入口，直到这个 Base 的输入耗尽。每次必须写入正数，按其返回值推进目标。目标太小在 DG 层报告非法 Text。输入已耗尽但目标尚有剩余也失败。整个循环分别以 body 的字节边界、目标的 UTF-16 边界约束读写。
3. 成功后将最终 string 直接交给 `TextSnapshot.FromString`。传入 `ReadBase(ref reader)` 的 reader 与回调状态是不同值副本，须在成功后使外层 reader 也越过完整 body（例如在局部游标先取出全部剩余有界切片作 state，成功才提交）。保留现有冷重放的 Copy 复用和片数保护。[当前冷读代码](../../../src/DurableGraph/Runtime/Text/TextBodyCodec.cs)

## 实施验收

| 范围 | 最小可观察结果 |
|---|---|
| Binary API 与 DG Base | 新写端与既有 string goldens 一致，新读端在足够大的目标内逐单元吻合。经真实捕获写出上述 v2 goldens。127/128 单元验证 N 的 varint 边界，同一内容不同段数共用同一个 N。不同编辑史可产生不同 Base，读回的 UTF-16 单元逐一相等 |
| 序列与冷恢复 | 空段的两种 Bare 表示、未终止头、巨 N 配短体、输出不足 N、未耗尽尾部、超出 N 的合法段、截断字面和非法 UTF-8 都失败。大量小段直接恢复成最终 string，随后的 Copy 横跨旧段仍正确。跨段代理、孤立代理、NUL 及 CR/LF 原样恢复 |
| 表示选择与图语义 | 将“估值等于编码长度”的旧测试改为分别验证软估值、实际字节与 H。小改选 Delta 时全文 Base 编码计数为零，既有 Delta golden 逐字节相等，强制 Base、未变维护、Event、故障后丢弃、共用 CLR builder 仍满足原合同 |
| 准入与交付 | 以新普通包建新库，跨进程冷开并续写。保留同字节异义的路由拒绝用例、非 Text 对照及嵌套 Text 类型解析。开发期旧库可重建，本次不设旧数据读取、迁移或跨版本包矩阵门槛。记录 DG 和 Storage 的准确包来源 |
| 成本对照 | 固定相同冻结快照，比较 v1 与 v2 的首次和强制 Base、后续小 Delta 以及冷开。报告 K、正文单元数、实际 Base/Delta 字节、选择结果、分配和时间。少量大段和大量小段分列。区分真实策略对照与固定表示对照，估值不同可能改变所选记录 |

验收入口为 [TextBodyCodecTests](../../../tests/DurableGraph.Tests/TextBodyCodecTests.cs)、[planner 回归](../../../tests/DurableGraph.Persistence.Tests/ObjectRevisionPlannerTests.cs)、[Repository 集成](../../../tests/DurableGraph.Persistence.Tests/DurableTextIntegrationTests.cs)、[产品成本见证](../../../tests/DurableGraph.Persistence.Tests/DurableTextProductProbeTests.cs)和[普通包消费者](../../../experiments/PackageConsumerProbe/DurableTextConsumer/README.md)。完成两个仓库对应的构建、相关测试及普通包验证后，再把[主线合同](mainline-integration-plan.md)更新为实施事实。
