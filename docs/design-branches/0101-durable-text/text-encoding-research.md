# DurableText 的分片编码、保存决策与压缩

2026-11-18。研究基线为 DurableGraph `8fdba114c642cc0b266137308db79e24e0249685` 和 Atelia.Storage `9ad39fc51a420e8302468533b82d8bc3a91a403e`，后者与 DG 的六包 pin 一致。下文提出供验证的设计。当前产品合同仍见[主线接入记录](mainline-integration-plan.md)和[依赖来源](../../../eng/StorageDependency.props)。指向 Storage 源码的相对链接按两个仓库并列检出解析。

## 研究结论

**首选验证的是给 Atelia.Binary 增加稳定分片输入，让同一个逻辑字符串，无论来自一个 string 还是几百个切片，都产生与现有 Bare string 完全相同的字节。** DG 决定编码哪个值，Binary 负责字符编码和长度头。一个 Base 是一个值，一条合并后的 New 是另一个值，Copy 只记录前驱的 UTF-16 区间。这样既能省掉为编码拼接全文的分配，也让 Piece Table、未来的 Rope 与持久格式相互独立。

**最值得同场比较的是对已选中的 Base 或大段 New 采用独立压缩。** 两份仓库原文中，普通择短已经省了一部分空间，在其上用现成 Brotli 还能从 11,977 B 降到 3,272 B、从 3,957 B 降到 2,693 B。单字符则由 2 B 增到 3 B。两者可以组合，但是否承担压缩、复制和冷开解压成本，要看完整编辑轨迹。改换编辑树解决不了这里的编码问题。

这次核实了准确源码、四份 Git 原文的编码尺寸、真实包压缩读回、Unicode 边界和一个影响选型的 planner 反例。样本是源代码与设计文档，memo 正文的语言分布、编辑轨迹和端到端时延仍待实测。

## 现有实现中各层知道什么

| 层 | 已具备的能力 | 设计机会 |
|---|---|---|
| TextSnapshot / Piece Table | 持有不可变 string 切片，可遍历或重放。只有单片覆盖整个原 string 时，直接取全文 string 才无需复制 | 写入器可直接吃切片。同一逻辑内容的物理划分不能进入 wire |
| DG Text body | Base 写单元数和 UTF-16LE。Delta 写结果单元数、准确前驱上的 Copy 和合并后的 New。来源与前驱失配时可生成全 New。冷恢复复用 Copy 的内存切片 | 替换 Base/New 的字面编码即可。结果长度、Copy 的寻址单位、前驱验证和快照身份可继续沿用 |
| Binary Bare string | `PrepareString(string)` 扫描输入，比较无损 UTF-8 和 UTF-16LE 的 **payload** 长度，平局选 UTF-16LE。writer 支持分段输出。现有计划保存不可变 string | 把计量及输出扩展到分片输入，使连续输入也走同一套规则。输出缓冲区能分段，并不代表输入已经能分段 |
| 图级保存 | 已有业务 State 的冻结文本可以延后生成 Base。新对象、Event 及公开准备入口有各自的即时准备边界。安装使用实际记录长度 | 精确选码可以延至真正需要字节时。估值、选择、实际准备和安装须使用各自正确的长度 |

依据见 [TextPieceView](../../../src/DurableGraph/Runtime/Text/TextPieceView.cs)、[TextBodyCodec](../../../src/DurableGraph/Runtime/Text/TextBodyCodec.cs)、[Binary 编码选择](../../../../atelia-storage/src/Binary/BareValueEncoding.Strings.cs)、[Binary writer](../../../../atelia-storage/src/Binary/BareValueWriter.Strings.cs) 和 [PreparedTypedBase](../../../src/DurableGraph.Persistence/PreparedTypedBase.cs)。

## 哪些方案值得竞争

| 方案 | 主要收益 | 代价、反例与采用条件 |
|---|---|---|
| 固定 UTF-16LE | 单遍复制，UTF-16 单元数直接给出精确 Base 字节数，解码便宜 | 英文和代码正文常接近两倍。保留为速度和复杂度的基线 |
| 只在输出时拼成 string，再用现成 Bare | 足以验证新格式。单片整串可直接传原值 | 大 Base、多片 New 会多一次全文或整段复制。很适合做与分片实现**逐字节相同**的控制组 |
| **整个逻辑值跨片择短** | 直接复用普通 string 的规则，无全文中间 string，普通 string 的读端已能解析该字面值 | 通常要读两遍输入，第一次选码，第二次写。混合内容只能选一种，少数孤立代理可使整段回退 UTF-16LE。建议先做 |
| **规范逻辑块各自择短** | 将语言区域或孤立代理的影响限制在局部。在给定块规则下，保存结果与内存 piece 边界无关 | 块数个头，纯 ASCII 或很短的 New 可能更大。要规定代理对跨**逻辑块**如何处理，改变规则必须重算成绩。只在有局部性证据时推进 |
| **择短后，对完整 Base / 大 New 单独尝试压缩** | 利用重复词和结构，收益可超过换字符编码。独立流保持一个准确前驱的恢复模型 | 需要读时解压、原始长度限额和写时工作内存。大小无法由字符数精确推知。小 New 走普通 Bare。高熵或过短输入有回退成本。是优先挑战者 |
| Latin-1 / UTF-8 / UTF-16LE 三选 | 大量 U+0080…00FF 的高熵正文也可降至每单元 1 B | 中英代码样本缺少这个动机，混入一个范围外字符整段就需换码。需要新的明确格式判别和严格可表示性检查。仅在相应语料占比高时值得测 |

还有两个会改变排序的特例。若经常出现“长 ASCII 加极少孤立代理”，局部块或能直接表示孤立代理的 UTF-8 变体有实质优势。后者需要独立识别和验证，不能传给严格的 UTF-8 reader。若目标是跨版本消除重复，现有 Copy 已经跳过未改正文。上跨版本压缩字典或共享物理块会改变依赖、冷开与历史保留，只有独立压缩后仍反复写入大量相同字面内容时，才有理由承担这种改造。

把 Piece 的 backing 全改成 UTF-8 也是更底层的选择。它可让以字节为入口和出口的应用少转码，但现有 LLM 工具交给编辑器的是 .NET string，快照查找、Regex 和 Copy 都以 UTF-16 坐标工作。随机按单元切片会需要扫描或额外的字节偏移索引。切开代理对以及孤立代理还需要专门表示。若未来主要负载变成从 UTF-8 字节接收、搜索再原样发给消费者，应重新比较整个内存与 IO 路径。就现有接口而言，先扩展 Binary 的输入即可获得落盘节省。

### 用四份原文核算尺寸

取上述 DG 提交中的完整 Git blob，严格按 UTF-8 解码，保留换行和开头可能存在的 U+FEFF。单位为字节。固定列是当前 Text raw Base。整段列是让 Base 本身成为一个 Bare string。块列是一个总 UTF-16 长度加若干 Bare string。表中不计 typed 头、对象记录及共享图帧。

| 原文（仓库相对路径） | UTF-16 单元 | 固定 | 整段择短 | 每 256 单元 | 每 1,024 单元 |
|---|---:|---:|---:|---:|---:|
| `README.md` | 30,798 | 61,599 | 50,268 | **49,664** | 50,044 |
| `docs/usage-guide/application-design.md` | 3,819 | 7,640 | 7,640 | **7,580** | 7,648 |
| `src/DurableGraph/TextSnapshot.cs` | 4,863 | 9,728 | **4,865** | 4,903 | 4,875 |
| `src/DurableGraph/Runtime/Text/TextBodyCodec.cs` | 11,974 | 23,950 | **11,977** | 12,070 | 12,000 |

复算只需 `git cat-file blob <提交>:<路径>` 的原始字节。设 `V(x)` 为最短 VarUInt32 长度，`N` 为 UTF-16 单元数，`U` 为严格 UTF-8 字节数。固定列为 `V(N)+2N`。若 UTF-8 合法且 `U<2N`，Bare 为 `V(2U+1)+U`，否则为 `V(2N)+2N`（表内大小均在两种表示支持域内）。块列沿 UTF-16 单元等长切分，最后一块可短，分别套用 Bare，再加 `V(N)`。本次分别用 PowerShell 与 Python 按 Git 原文复算了四行。

三个反例解释了为何没有默认块长可从上表直接推出。

- 65,536 个 ASCII 单元，整段 65,539 B，256 块 66,051 B。只将中间一个字符换成孤立高代理，整段变成 131,075 B，256 块仅 66,307 B。
- 同样的 1,024 个“汉”和 1,024 个 `a`，两种排列的整段结果都是 4,098 B。“先汉后英”分成 256 块是 3,090 B，逐字交替则是 4,114 B。对这批无代理的字符，直方图足以判断整段大小，预测分块收益还需要局部性。
- 255 个 `a`、一个 emoji、再加 255 个 `a`，整段 516 B，按 256 个 UTF-16 单元硬切是 1,030 B。硬切把合法代理对分成两半，两块都回退。另一条合理规则是将边界挪一个单元以保住有效代理对，需把该规则、末块行为及其头开销单独验证。

短 New 更能说明费用落在哪里。含 1 B opcode，排除共同的 Delta 总长与 Copy，写单个 ASCII 的固定 / Bare 成本为 4 / 3 B，单个“汉”为 4 / 4 B，64 个“汉”为 130 / 131 B。最后一例因 Bare 头的 `2N` 先跨越 varint 边界。已有 1 Mi 单元产品轨迹初始为全 `a`，文本初次记录为 2,097,163 B，随后 32 次小改合计仅 1,044 B。这条轨迹首先检验 Base。大段插入、整体重写才检验 New。[原轨迹与逐次数据](mainline-performance.md) · [负载实现](../../../tests/DurableGraph.Persistence.Tests/DurableTextProductProbeTests.cs)

### 压缩确有空间收益，时间成本还要单独测

用固定 pin 的实际 `Atelia.Binary` 普通包写出字节，比较普通 Bare 与三种**完整 controlled string 封套**。每个 controlled 结果均核对计划长度并读回原 UTF-16 内容，6 个输入共 18 次。枚举 `ValueCompression.Brotli` 在该版本内用 quality 3、window 22。这里每个完整输入独立压一次。

| 输入 | 普通 Bare | Controlled None | 尝试 LZ4 block | 尝试 Brotli |
|---|---:|---:|---:|---:|
| 上表的 TextBodyCodec.cs | 11,977 | 11,978 | 4,963 | 3,272 |
| 本片 mainline-integration-plan.md | 3,957 | 3,958 | 3,158 | 2,693 |
| 单字符 `x` | 2 | 3 | 3 | 3 |
| 95 个互异可打印 ASCII | 97 | 98 | 98 | 98 |
| 固定种子可打印 ASCII，4,096 单元 | 4,098 | 4,099 | 4,099 | 3,419 |
| 重复混合段落加随机尾部，2,048 单元 | 2,434 | 2,435 | 564 | 506 |

复验时，对同一正文执行 `WriteString(PrepareString(text))`。再分别执行 `PrepareControlledString(text, None/Lz4Block/Brotli)`、`WritePreparedValue(plan)`，量取 `WrittenCount` 并用 `ReadControlledString` 回读。随机样本使用 uint xorshift32，种子 `0x13579BDF`，依次左移 13、右移 17、左移 5 后分别异或，每轮取 `32 + state % 95`。混合样本为 `"Memo 世界: edit αβ; line\r\n"` 重复 64 次，加随机样本前 512 单元。前两行采用同一 DG 提交的 Git 原文。短样本及随机 ASCII 的 LZ4 都选择 Raw，仍需付控制字节。当前 controlled 准备会拥有原始 body、尝试压缩并为获选结果保有字节。表内数字没有计入这些分配，也没有计入 Save 或冷开时延。若在产品中混用普通和压缩字面，需用明确的外层格式区分，例如为压缩 New 分配单独 opcode，让小 New 只支付普通 Bare 的成本。其中的压缩封套还应排除 null。[准备与回退](../../../../atelia-storage/src/Binary/BareValueEncoding.Controlled.cs) · [压缩方法与限制](../../../../atelia-storage/src/Binary/ControlledValueCodecs.cs)

## 跨仓接口与读写合同

**建议先实现一个同步写入入口，沿用普通 Bare string 的字节语法。** 择短依赖整个逻辑值，即使 sink 能回填长度，在最终选择翻转时先前写出的字符也得转码重写。冻结输入支持重读，通常比同时缓冲两份候选或改造 Frame writer 简单。单片先复用同一内核。多片可以直接传稳定的、按索引取得 `ReadOnlyMemory<char>` 的片段窗口，让 DG 的冻结视图和 New 区间提供轻量适配器。另一种签名是 `WriteString(in ReadOnlySequence<char> value)`。它适合已持有 sequence 的消费者，但 DG 现有 Piece 数组通常需为它另建 O(p) 个链节点。用含数百个短片段的分配数据决定参数形状即可，两套公共入口没有必要同时引入。直接使用一次性 `IEnumerable` 则无法保证计量和输出看到同一序列。

1. **输入拥有者。** 从计量开始到输出结束，片段顺序和字符 backing 必须稳定。DG 已有冻结快照可满足。借入的 `ReadOnlyMemory<char>` 可以指向可变数组，不宜赋予它现有 `StringEncodingPlan` 跨调用复用不可变 string 的承诺。若调用方无法保证稳定，入口需先取得自有副本。
2. **两遍遍历。** Binary 按逻辑顺序扫描，严格计算合法 UTF-8 字节数，跨空片保留待配对高代理。选 UTF-8 时第二遍写出也要跨片保留编码状态，在逻辑值末尾才 flush。UTF-16LE 路线直接按单元输出。已有 Binary 用例覆盖输出缓冲边界。本轮另用四个短输入的全部 65 种连续切分验证了输入分片的字节一致性。两者在正式实现中都应与连续 string 的 golden 对照。[现有 string goldens](../../../../atelia-storage/tests/Binary.Tests/StringTests.cs) · [输出分块测试](../../../../atelia-storage/tests/Binary.Tests/ChunkedOutputTests.cs)
3. **建议的 DG wire。** Base 直接是一条 Bare string。Delta 保留结果的 UTF-16 单元数。Copy 保持原指令，New 后接一条非空 Bare string。New 的贡献由解码后的单元数决定，因而不用同时保存单元数和字节数。相邻 New 和来源连续的 Copy 仍按现规则合并。若 Copy 末尾是高代理、New 开头是低代理，New 单独选择可无损保存孤立低代理的表示，解码拼回后全文仍是原来的合法代理对。不同物理切法要求同字节。不同的逻辑 Copy/New 计划可以合法地产生不同 Delta。
4. **拒绝损坏且控制分配。** Bare 读端目前对 UTF-8 严格，对有效的非首选编码和冗余 varint 宽容。建议沿用 Binary 的字面值读语义，DG 继续校验操作、非零 New、前驱范围、结果单元数和完整消费。现有 `ReadString(maxPayloadByteCount)` 只限制字节。保留整条 Delta 预检时，Binary 需提供不生成 string 的验证和已解码单元计数，DG 据此检查字节范围、严格 UTF-8 和剩余输出单元，重放时才生成 string。带双限额的 ReadString 可保护分配上界，但预检若调用它仍会分配。目前重放可能中途触及片数上限，从头退回平坦恢复。已解码的字面可能再次处理。可先测这项开销，再决定是否预判回退或复用字面。Base 也要在 CLR/应用限额内解码。压缩另限已存与解压后字节数。[读端](../../../../atelia-storage/src/Binary/BareValueReader.Strings.cs) · [DG 冷重放](../../../src/DurableGraph/Runtime/Text/TextBodyCodec.cs)
5. **格式切换。** 当前 `"A"` 的 Text Base 是 `01 41 00`，Bare 是 `03 41`。即使 Binary 的旧 string reader 能读新的字面值，已经认识 DurableText v1 的 DG 也要区分两代 Text body。无须给每条 Delta 加编码器/内存类型标签。可给新 Text Base 分配专用 typed route，从该 Base 起整链遵循新语法。若选择重建开发库，可在打开端统一提升格式版本，两者取其一。专用 route 可保留普通类型路径。最小方案是明确拒绝旧 Text。若要允许读旧并续写，现有 typed 解码只向下传 Layout 与 body，新旧又同为 Text。需将格式代际传给恢复及写入决策，在第一次需要以新语法写旧正文时强制新 Base，然后才允许新 Delta。验收以**本次已有 Text v1 的包**做交叉读负控，源码联调后更新 Storage pin，并用新普通包冷开续写。在开放 Store 中继续遵守完成帧只追加。[typed 路由](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) · [包验收入口](../../../experiments/PackageConsumerProbe/DurableTextConsumer/README.md)

## 估算会改变保存选择

当前 planner 先判断完整记录的 `B <= D`，再以 `H + D > α × B` 为已改对象选维护候选，依放大率排序，在全图软预算内决定写入。这里 `H` 是前驱实际重建 payload 字节，`B` 是 Base 估值，`D` 为实际 Delta 记录大小。若 ASCII 占主导，`2N` 对择短 Base 可高估近一倍。它能让维护动机从约 5 倍实际 Base 推至约 10 倍，还能改变别的对象获得预算的顺序。安装后的 H 必须继续取实际新记录。[规划器](../../../src/DurableGraph.Persistence/ObjectWritePlanner.cs) · [选择策略](../../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs) · [实际 H 的安装](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs)

**最值得防的误选是全 New。** 将长度 N 的一篇 ASCII 全文替换成另一篇等长 ASCII，Base 与全 New 的核心字面完全相同，Delta 还多出前驱和指令。若拿约 2N 的估值与约 N 的实际 Delta 比，仍可能选择依赖前驱的 Delta。当 Base 和全 New 使用同一种字面编码及压缩方式时，Delta 准备已经量出整条字面，便可转交这个**已知精确长度**给 Base 并分别计算两条记录的封套，获选后还可能复用已编码的字节。只改 Base 或仅给 Base 压缩时，旧 New 的长度不足以推导新 Base 的长度，只能复用已有的字符统计或另作估算。小改继续 O(1) 估算，避免为了选择一个小 Delta 而扫描整篇 Base。无需为这个反例给所有类型添加 Count 接口。

常规小改若以后仍观察到误选，无压缩时合法最短字面 payload 的界为 `N..2N`，头和对象封套另计。可只在 Base/Delta 接近且有机会翻转的对象上精计。压缩会破坏这个下界，也会让固定倍率估值偏离更远。试验应记录预测/实写和预算结果。若直接用未压缩逻辑大小选、选完才压缩，应明说改了选择依据，并用同轨迹逐次对照。禁止用规划估值当硬拒绝条件，编码、typed body、整批记录和物理帧都应在各自边界检查实际尺寸。当前固定编码可精确预检的 `EstimateBaseSize > Array.MaxLength` 在换码后不能原样照搬。

### 如果计量真的占时，优先试逻辑全文统计

整段无损择短可以精确增量计量。设 W 为独立计每个 UTF-16 单元的 UTF-8 权重之和（ASCII 为 1，低于 0x800 为 2，其余含代理为 3），S 为代理单元数，Q 为全文相邻高低代理对数。合法 UTF-8 当且仅当 `S == 2Q`，合法时 `U = W − 2Q`。计数和中间大小用足够宽的整数。

把 A、B 两段连接，W 和 S 直接相加。Q 相加，再计 A 末尾与 B 开头是否正好形成一对。替换时只需减去删除段、加上插入段，再修正接缝，省去扫描两侧的长保留切片。批次中相接的编辑要合并计算接缝，避免重复计数。大删除可改为扫描较短的剩余结果。本轮用 ASCII、é、中、高代理、低代理组成的全部长度 0–4 输入（781 个）核对严格 UTF-8 oracle。枚举每个删除区间和长度 0–2 的替换，共 334,521 次，接缝更新结果均与完整重算一致。这验证了单替换公式，尚未验证产品批处理或性能。

只有在强制或频繁 Base 的全链测量显示计数扫描显著时，才值得给冻结 View 加懒统计并在编辑后维护。它有两个限制。第一，即使最终只输出很短的 Delta，已激活的统计也会给编辑添加工作。第二，全文合法性无法推断任意 New 子区间是否合法。逐 Piece 的摘要同样不能免费回答任意切割后的子段长度。引入 backing 索引须另计常驻内存。两遍分片法始终是可与之比较的基线。

## 下一步如何得出产品选择

建议用一轮有边界的实验，先实现相同 wire 的“拼接复用”和“分片复用”，与当前固定编码并列。分别打开“只改 Base”和“同时改 New”，从同一条 Save 轨迹区分收益来源。压缩只挑一个快方法在完整路径上挑战，LZ4 可先试。上表的 Brotli 作为更重但更小的尺寸参照。固定逻辑块、全文统计和额外字符集分别等待相应反例在真实负载中出现。

| 负载 | 优先观察的差别 |
|---|---|
| memo 风格短/中正文、英文代码、中英分区和交替文本，每次 Save 改一个字符 | 后续选择小 Delta 时全文扫描/编码次数。新增分配、完整 Save 时间、Base/Delta 总量 |
| 集中插入多个大段、多片 New、全文替换、来源失配 | 全 New 是否多背前驱，字面编码及合并分配，前驱地址和各次真实 payload |
| 周期性强制 Base、多对象争预算、长链后冷开和旧版分叉 | 周期尖峰、累计 H、实际链深、冷恢复时间与分配、受预算影响的其他对象 |
| 单片和数百片同内容、跨片代理、孤立代理、截断/超限、冷重放退回平坦路径 | 与普通 Bare 的逐字节 golden、原始 UTF-16 读回、失败前分配、完整消费 |

选择时分别看**首次写、后续全部 Save、完整冷开**。同时报告对象 payload 总字节、库文件增长和托管分配。固定编码小改本来就很便宜，分片候选须保住选择小 Delta 时跳过全文扫描的收益。压缩只有在代表性历史中继续显著省字节，并且写入停顿与冷开成本在使用方预算内，才足以改变默认。若热点落在已选 Base 的多次 byte[] 复制，先试 DG 内部精确分配和所有权转移。直接向 Frame lease 流式写会跨越全批准备和故障边界。若在对象记录下方压缩整帧，H 可以沿用解压后每对象记录的 payload 字节，此时同一共享帧的实际压缩读取字节无法精确归属到某一个对象。应另测整帧 IO。
