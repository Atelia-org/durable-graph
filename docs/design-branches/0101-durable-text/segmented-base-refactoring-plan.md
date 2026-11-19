# DurableText 分段 Base 重构方案

**内建文本在 `712ddf7f` 实现分段 Base。** DB-104 由注册叶的文本 codec 延续下述 raw body 编码，图上的类型登记和保存回执见[后继合同](../0104-text-editor/README.md)。 从同一冻结快照读取 UTF-16 单元总数 N 和 Piece 序列，写一次 N，再把每个非空 Piece 独立编码为普通 Bare string。冷读时一次分配最终 string，按段直接写满。Delta 继续使用现有 Copy/New 编码、UTF-16 坐标和准确前驱。[主线合同](mainline-integration-plan.md) · [选型与尺寸依据](text-encoding-research.md)

## 一个完整例子

```text
BaseBody := VarUInt32(N) NonEmptyBareString*    // 恰好耗尽这一条 body
```

| 正文与物理切分 | 裸 Base 十六进制字节 |
|---|---|
| 空正文 | `00` |
| 一段 `"AB"` | `02 05 41 42` |
| 两段 `"A"`、`"B"` | `02 03 41 03 42` |
| 分居两段的高、低代理 U+D83D、U+DE00 | `02 02 3D D8 02 00 DE` |

各段独立选无损 UTF-8 或 UTF-16LE，平局选后者。合法的非首选表示与冗余长度头沿用 Binary 现有读语义。段内允许 NUL，也能保存孤立代理。相同正文的不同切法允许产生不同字节。N 始终等于解码后的 UTF-16 单元数，盘上省略段数。与片段数前缀相比，N 的收益是可以在第一次转码前确定最终目标的容量。[Binary 字符串规则](../../../../atelia-storage/src/Binary/BareValueEncoding.Strings.cs)

**分段正文作为 DurableText 的首次发布格式。** 直接替换 Text Base 的读写实现。沿用 typed Base ABI 5、结构路由 2 和零元 `TypeExpr.Text`（11）。一段 `"A"` 的完整 typed Base 为 `05 02 0B 01 03 41`。首个本地产品提交 `fb2bef0a` 才在既有 ABI 5 上引入 Text，其父提交 `8629e0b1` 的读写器尚无这个类型。按已确认的使用范围，固定 UTF-16LE 的 Text 只存在于本地试验数据。此次调整只涉及该类型的 Base 正文，原有非 Text 数据沿既有路径读取。[typed Base](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) · [类型表达式编码](../../../src/DurableGraph.Persistence/TypeExprWireCodec.cs)

**含旧 Text 的测试库用新 writer 重新生成。** 裸正文 `01 03 41` 在旧本地实现中表示 U+4103，在分段实现中表示 `"A"`，两边均能合法耗尽输入。新 reader 始终按分段语法解释 Text Base，不能靠试读或尾部校验辨认旧夹具。源码联调与普通包测试各自使用匹配的构建与数据。用后续普通提交落地即可，重写 Git 历史也不会改变已有测试文件中的字节。[原型数据兼容约定](../../DurableGraph-target-design-v0.md#原型迭代与数据兼容)

## 两个 Binary 重载

| Binary 入口 | 调用方可依赖的行为 |
|---|---|
| `void WriteString(ReadOnlySpan<char> value)` | 同步读取传入的切片，与对相同 UTF-16 序列调用现有 string writer 逐字节一致。切片外的字符不参与选码。空 span 仍是合法的普通空字符串 |
| `int ReadString(scoped Span<char> destination)` | 成功时消耗恰好一条 Bare string，返回实际写入的 UTF-16 单元数，只写目标前缀。失败时游标仍在原处，目标前缀可能已经改写。源须稳定，目标与输入不能重叠 |

写端复用按 span 选 header 的内部逻辑及现有有界 payload writer。原 string 重载继续拒绝 null，已有 `StringEncodingPlan` 仍绑定不可变原串。读端共用头解析、整数范围与 payload 截取。原有返回 string 的 `ReadString(int maxPayloadByteCount)` 继续为 HeadStore 等调用方执行其字节限额。DG 已只传一条有界 body 和剩余字符目标，新 span 重载只接收目标。[Binary writer](../../../../atelia-storage/src/Binary/BareValueWriter.Strings.cs) · [Binary reader](../../../../atelia-storage/src/Binary/BareValueReader.Strings.cs) · [现有限额消费者](../../../../atelia-storage/src/HeadStore/Internal/Tags/TagMetadataCodec.cs)

Binary 在局部 reader 副本上解析，成功才推进调用方游标。UTF-16LE 分支检查目标容量并按小端读原始单元。UTF-8 分支用 `Utf8.ToUtf16` 严格完成本段转码（禁用替换、final block），校验成功状态及完整输入消费。目标不足报针对 destination 的 `ArgumentException`。截断、非法编码沿用原读端错误语义。单遍转换可能先写入有效前缀再报错，API 不保证回滚调用方的内存。

## DG 只负责正文边界与保存计划

**读取完整 Base。** 私有入口改为 `ReadBase(ReadOnlySpan<byte> body)`。唯一产品调用方 `TextObjectReader` 已从 `IStateBodySource.GetBody(0)` 取得完整切片，随后为每个 Delta 另建 reader。Base 的游标仅在方法内部有用。[调用点](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:src/DurableGraph/Runtime/Text/TextObjectBinding.cs") · [当前 codec](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:src/DurableGraph/Runtime/Text/TextBodyCodec.cs")

1. 读 N，先校验现有 Text/CLR 长度上限和 `N <= RemainingCount`，再分配。两种合法编码的 payload 字节数都至少等于输出单元数，因此巨大的 N 配极短输入可提前拒绝。N=0 时立即要求输入耗尽，`string.Create(0, …)` 会跳过回调。
2. 对正 N，调用 `string.Create(N, reader, static …)`。在回调里**按有界 body 读到末尾**，每次向剩余目标读一条 Bare string 并要求写入量大于零。若仅以“目标已填满”结束，`01 03 41 00` 就会错误地忽略尾部空段。目标不足在 DG 层表示非法正文长度。
3. 输入耗尽后剩余目标必须为空。将成功生成的 string 直接交给 `TextSnapshot.FromString`。回调失败时私有 string 不会交付给使用者。按完整 body 解码和按单条 Bare string 推进公共游标各有一个明确的消费边界。

**写入选中的 Base。** 在 `PrepareBase` 以冻结视图的 Length 写 N，顺序将每个非空 `piece.Memory.Span` 交给 Binary。正常 State 保留先规划、选中才编码的路径，新对象、Event 和其他即时准备入口复用同一格式。保存成功后的 Rebind/整理沿现有安装时机执行。[冻结与接受](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:src/DurableGraph/DurableText.cs") · [即时及延迟 Base](../../../src/DurableGraph.Persistence/PreparedTypedBase.cs)

**规划使用估值，安装使用实写。** raw Base 的 O(1) 估值为 `V(N) + K + 2L*N`，V 是 VarUInt32 的字节宽度，K 取冻结视图已有的片数，作为每段约 1 B 头的估计。估值可能偏高或偏低。容量由实际写出过程、记录封套及帧约束把关。typed 头和记录封套各加一次，安装后的重建字节 H 从实际记录累计。小改选 Delta 时免除全文 Base 的选码和编码。[保存选择](../../../src/DurableGraph.Persistence/ObjectWritePlanner.cs) · [实际 H](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs)

## 产品与验收

DurableGraph 产品提交 `712ddf7f` 固定 Storage `3f780ad9`。集成测试从 64 片正文起步，核对实际 Base 的每个段、准确前驱和重建字节，然后小改、关闭、冷开续写，并读回原版本。普通包消费者以三个独立 backing 构造初始正文，分别在三个进程执行首次保存、冷开续写、只读历史复核，核对实际加载的 DLL。[依赖 pin](../../../eng/StorageDependency.props) · [普通包结果与复现](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:experiments/PackageConsumerProbe/DurableTextConsumer/README.md")

2026-11-18，DG 源码模式根 solution 的 Release 构建为 0 警告、0 错误，四个测试项目共通过 3,299 项（Persistence 1,250、Serialization 133、Storage 36、Runtime/Generator 1,880），无失败或跳过。构建日志和四份 TRX 位于 `W:/DurableTextSegmentedBase/source-final/`。上游新接口的边界用例另见 [Binary 测试](../../../../atelia-storage/tests/Binary.Tests/StringSpanTests.cs)。

在产品路径的 40 次最终采样中，64 Ki 与 1 Mi 单元正文的直接解码均只分配最终 string 和 280 B 的快照等包装。相同正文每 16 个单元一片时仍观察到相同分配，每段长度头则增加了实际写入量。计量范围从内存中的 Base body 到还原的正文对象，图恢复和磁盘延迟另计。[分段产品成本与原始数据](segmented-base-performance.md)

| 证明什么 | 最小见证 |
|---|---|
| Text 使用原有封套，既有 ABI 5 类型仍可读 | 固定一条完整的分段 Text golden（见上例），更新旧 Text Base 字节断言。沿用原有非 Text ABI 5 夹具，验证原字节仍可解码，并运行既有嵌套类型与非法路由回归。[结构夹具](../../../tests/DurableGraph.Persistence.Tests/MetaBootstrapBuiltinListTests.cs) · [Schema 夹具](../../../tests/DurableGraph.Persistence.Tests/MetaBootstrapSchemaClosureTests.cs) |
| 字符、长度与错误合同 | Binary 以原 string 入口作 oracle，检验切片、代理、目标前缀和失败游标。DG 检验 N 的 127/128 边界、空正文与尾部、空段、非法/截断字面、输出长于或短于 N、不同切分的等值读回。[Binary goldens](../../../../atelia-storage/tests/Binary.Tests/StringTests.cs) · [Text codec 回归](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:tests/DurableGraph.Tests/TextBodyCodecTests.cs") |
| 保存与恢复保持原有因果关系 | 在既有回归上分别断言估值与实写 H，小改选 Delta 时 Base 编码次数为零，Delta golden 不变，准确前驱、维护写、Event、失败与发布后安装沿用原语义。[planner 回归](../../../tests/DurableGraph.Persistence.Tests/ObjectRevisionPlannerTests.cs) · [集成测试](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:tests/DurableGraph.Persistence.Tests/DurableTextIntegrationTests.cs") |
| 分段真正节省中间对象 | 用相同正文分别制成少量大段和大量小段，记录实际产品 Base 字节与直接冷解码分配。组件计量单列最终 string，从而辨别是否重新引入每段临时 string。现有 eager/deferred 对照用于同一 wire 的准备策略。两路若因估值而合法选出不同记录，应分开报告选择，只有控制住表示时才比较记录哈希。[产品 probe](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:tests/DurableGraph.Persistence.Tests/DurableTextProductProbeTests.cs") |

读端已有独立机制证据。在 .NET SDK 10.0.201 / 运行时 10.0.5、Storage pin `9ad39fc` 上，同一 4,096 单元正文切成 64、1,024、4,096 段，直接读每次均分配 **8,216 B**，等于最终 string。逐段返回 string 再复制分别为 17,944、40,984、106,520 B。范围只含内存 payload 到 string。探针的扩展方法另带实验用字节限额参数，产品 span 签名以本文为准。可在该 pin 的本地 NuGet.Config 旁执行 `dotnet run --file span-decode-probe.cs -c Release`。[可复验证据](span-decode-probe.cs)
