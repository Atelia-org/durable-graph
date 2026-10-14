# DB-048：List 差分算法与编辑原语调研

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0048-list-delta-algorithm-research.md`，正文保留当片合同与历史验收含义。
> 阅读范围：本轮为文献/源码研究，没有候选性能实验；用户后续静态比较与统一区间 codec 选择已进入 DB-049，默认竞争后继为 DB-051。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：**Research / Proposed**，2026-09-09。完成前人工作、当前源码及候选交叉审查；尚未进行候选性能实验，未选择最终算法或持久编码。
> 基线：[DB-047](../0047-list-content-object-slice/README.md)，产品提交 `fac4981`。本轮仅研究和记录，不实施产品变更。
> 后续用户反馈已收敛并实施为 [DB-049](../0049-list-range-delta-and-matcher-trial-slice/README.md)：静态 StateEquals、统一区间 codec、三种可配置 writer 与领域编辑历史重放；冷读优化降为最低优先级。本文保留调研依据，当前实现与结果从 DB-049 进入。

## 1. 问题与初步结论

输入是同 exact `ListLayout` 的两份 owned `FrozenListState<TState>`，目标是生成精确恢复新状态的 Object Delta，
改善头插、中插、删除造成的位置错位，同时保留 inline struct 的字段级 Delta 优势。
不是重建用户曾调用的 List 方法，也不要求修改领域容器或维护操作日志。

初步推荐分开选择三个问题：

1. **如何匹配旧、新元素/区间**：先比较廉价局部重同步与有界 Myers，哈希区间匹配作为挑战者。
2. **如何表达结果**：优先验证“新元素区间＋旧区间复用及稀疏元素 Patch”；按输出顺序构造新状态，源坐标始终指向 immutable prior。
3. **如何判断两个元素相等**：必须计入真实持久语义和成本；现有 `PrepareDelta` 不是廉价无分配的任意匹配比较器。

最小成功见证：插入/删除不再重写未变长后缀；稀疏 struct 修改仍使用子 Delta；
重复和退化输入正确、有明确工作预算；在相同策略参数下报告真实链恢复成本。
在这些实验完成前，不宣称某个算法“最快”“最省”或已达到产品施工验收。

## 2. 已核实的项目基础

| 当前事实 | 代码依据 | 对本轮的约束 |
|---|---|---|
| List Base 为 count＋元素 Base；Delta 为 newCount＋共同位置稀疏子 Delta＋新增尾部 Base | [ListStateReader / ListStateBody](../../../src/DurableGraph/Runtime/Containers/ListStateReader.cs) | DB-047 是正确且有竞争力的稀疏更新基线，不能只拿头插用例与之比较 |
| `TState : unmanaged`；引用槽是 ObjectId，inline 槽是嵌套 DTO | [FrozenListState](../../../src/DurableGraph/Runtime/Containers/FrozenListState.cs)、[StateValueBinding](../../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs) | 列表 diff 不读取被引用对象内容，不调用领域 Equals，也不沿引用递归匹配 |
| 每次 Apply 分配新元素 buffer；reader 逐条 Apply 历史 Delta | [ListStateReader](../../../src/DurableGraph/Runtime/Containers/ListStateReader.cs) | 紧凑 patch 不会自动消除每层整表物化成本 |
| Capture 后全部 live Base 提前编码，再准备 existing 对象 Delta | [CaptureSession](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs)、[ListObjectBinding](../../../src/DurableGraph/Runtime/Containers/ListObjectBinding.cs) | Diff 加速不等于 Commit 同比例加速；Base/Capture 成本必须单列 |
| H 为对象版本 payload 累计字节；策略根据 H、B、D 产生 Base 动机 | [ObjectVersionChain](../../../src/DurableGraph.Storage/ObjectVersionChain.cs)、[策略](../../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs) | H 不度量整表复制 CPU、分配或全部物理 I/O |
| ListLayout 包含 codec version，当前只接受 1 | [ListLayout](../../../src/DurableGraph/Schema/ListLayout.cs) | 更换 patch 语法必须显式改变解释版本，不能悄悄重解释原版本 |

## 3. 前人工作：借用机制，区分其优化目标

以下为 2026-09-09 核实的论文、规范和官方实现；源码主分支链接是调研时的材料，不承诺其未来不变。
右栏是对 DurableGraph 的推论，不是文献声称已验证本项目。

| 材料 | 已有机制 | 可借用之处与限制 |
|---|---|---|
| [Myers 原论文](https://neil.fraser.name/writing/diff/myers.pdf)，§3、图 2 | 最短插入/删除脚本；`O((n+m)d)`；算法接受搜索上限 MAX；另有线性空间变体 | 少量分散插删的主候选。`d` 是插入、删除元素数，与策略 D 字节数不同；最短脚本不保证最小字节数 |
| [Git patience](https://raw.githubusercontent.com/git/git/master/xdiff/xpatience.c) | 使用唯一元素建立有序锚点，递归处理间隙；缺少锚点时回退 | 不应默认 List 像源码文件一样存在足够多唯一行；重复 ID、null、小整数会削弱锚点 |
| [JGit HistogramDiff](https://archive.eclipse.org/jgit/site/5.8.1.202007141445-r/org.eclipse.jgit/apidocs/org/eclipse/jgit/diff/HistogramDiff.html) | 将锚点扩展至低频元素，并限制候选链长度、配置 fallback | 借鉴频次预算与退化处理；不把它误当通用线性或最优保证 |
| [rsync 作者技术报告](https://rsync.samba.org/tech_report/node2.html) | 旧文件分块；新文件任意偏移寻找匹配；输出旧块引用或 literal | 借鉴插入后重同步；两份状态已在内存，无需复制远端校验和交换协议 |
| [VCDIFF RFC 3284](https://www.rfc-editor.org/rfc/rfc3284)，§1、§3 | 匹配算法与格式分离；ADD/COPY/RUN 按目标顺序构建结果，可引用 source 或此前 target | 借鉴 source 区间复用；本轮不需要 target-copy、地址缓存、二级压缩等完整机制 |
| [xdelta3 官方实现](https://raw.githubusercontent.com/jmacd/xdelta/main/xdelta3/xdelta3.c)，STRING-MATCH / INSTRUCTION SELECTION | 分开寻找匹配和选择指令，考虑重叠、延迟匹配及编码代价 | “找到最长匹配”不等于“得到最省字节指令”；不直接搬入完整二进制压缩器 |
| [VS Code Myers](https://raw.githubusercontent.com/microsoft/vscode/main/src/vs/editor/common/diff/defaultLinesDiffComputer/algorithms/myersDiffAlgorithm.ts) | 实现使用 wall-clock timeout，超时交付粗差分 | 借鉴正确回退；改用确定性工作预算是本项目的适配建议，不追求文本可读性或 move 高亮 |
| [JSON Patch RFC 6902](https://www.rfc-editor.org/rfc/rfc6902)，§4 | add/remove/replace/move/copy 操作依次改变文档；数组插删改变后续索引 | 这是合法的另一种坐标模型；本项目已有 immutable prior 和新 buffer，可避免引入可变索引语义 |

不直接采用文本库的主要理由不是“List 太特殊而无法 diff”，而是元素大小不同、存在子 Delta，
且我们优化保存和恢复而非人类阅读。Myers 的 equality-oracle 假设也必须替换成真实元素成本。

## 4. 评价指标的优先级

| 优先级 | 坚持 | 允许放宽 |
|---|---|---|
| 必须 | `Apply(Diff(old,new),old)=new`，精确保留 Count/顺序/重复项；输入不变 | 不必还原用户实际操作过程 |
| 必须 | 引用按 ObjectId、浮点按位、inline 按 exact 持久槽；准确 NoChange | 不提供领域 comparer 或业务身份推断 |
| 必须 | 有可解释的搜索/内存预算和正确 fallback；畸形 patch 拒绝 | 不保证所有输入的最优压缩 |
| 高 | 常见插删的尺寸、生成 CPU/分配、稀疏字段更新质量 | 无真实 SLO 时不预设任意毫秒阈值或性能倍数 |
| 最低（用户后续裁决） | 读取正确性仍为硬条件；冷读性能只作观察 | 不把冷读优化作为本片门槛，不改策略、Frame cache 或引入链 accumulator |
| 中 | 小而清楚的 grammar、确定行为、易于检查的失败条件 | 不要求所有实现产生唯一最短字节串 |
| 暂缓 | 专门 Move、全局最小字节脚本、OT/CRDT、跨对象内容压缩 | 重排先求正确、有界，并利用已有区间复用能力 |

没有业务编辑分布和存取频率时，不给时间、字节、内存凭空分配权重。
先比较是否存在在多个指标上同时占优的候选，再明确每个取舍的适用输入。

### 4.1 小 Delta 与长链的耦合

如果头插从长后缀改写缩为少量描述，H 增长可能显著变慢，现有策略可能更晚选择 Base；
当前 reader 却依然对每条 Delta 物化完整列表。因此“单个 Delta 更小”不推出“冷读更快”。

用户已明确接受单次加载较慢，冷读优化为最低优先级。相同编辑序列/相同 X/Y 的实际链可记录为背景数据，
固定链实验不再是前置门槛；保存端 CPU/分配与实际字节决定本轮主要取舍。
仍要求精确恢复和基本合法性，不据此直接加入最大链深参数、更换策略或优化恢复器。

## 5. 推荐优先验证的编辑模型

语义上先考虑两类输出区间，下面名称是示意，不是已冻结的 API 或字节码：

```text
NewRange(count, element Base bodies)
ReuseRange(oldStart, count, sparse local element Deltas)
```

按指令顺序填充新 buffer。ReuseRange 从 immutable prior 拷贝一段，再修改其中少量位置；
没有子 Delta 时就是纯复制。持久编码可以将纯 Copy 单独给一个 opcode，
形成 `Copy / CopyAndPatch / New` 三种码，以省掉空 patch 列表；是否值得待测。

例：旧列表 `[A,B,C,D]`，新列表 `[A,X,B,C*,D]`，其中 `C*` 是 C 的字段更新：

```text
newCount = 5
ReuseRange(oldStart=0, count=1, patches=[])
NewRange(count=1, bases=[X])
ReuseRange(oldStart=1, count=3, patches=[(localIndex=1, Delta(C,C*))])
```

此处 C 的旧索引始终为 2，不随前面插入 X 而改变。
元素可以是 ObjectId；复用 ID 不会读取其对象 body，也不会创建新的被引用领域实例。

| 用户观察到的变化 | 模型如何表达 |
|---|---|
| 插入 | 在两个复用区间之间输出 NewRange |
| 删除 | 旧元素没有被输出；无需 Delete 指令 |
| 替换 | 新元素 Base，或者旧元素＋子 Delta，依据实际编码成本选择 |
| 搬移 | 按不同顺序复用旧区间；无需独立 Move 指令 |
| 复制重复块 | 多次复用同一旧区间；源不被消费 |
| struct 小字段变化 | 复用区间内的稀疏子 Delta |
| 清空 | newCount=0，无输出区间 |

“grammar 能表达搬移”不等于首版 matcher 一定找到它。首版可只产生单调的 source 区间，
后续匹配策略改进无需让 decoder 理解新的搜索算法。不提供 target-copy、跨对象/跨多代 source、
独立 RUN、反向执行或用户操作日志；source 仍是现有 ObjectVersion 指定的那一个 prior 状态。

### 5.1 为什么推荐稀疏 Patch 区间

最初候选是 `Copy / New / Patch连续变化段`。交叉审查发现，每个孤立变化都要拆出 Copy 和 Patch 的范围头，
会放大 DB-047 已很紧凑的稀疏更新。即使保留“按原位置配对”算法，也不能消除新 grammar 的逐点头部成本。

稀疏 Patch 区间可以用一个 ReuseRange 表达整个共同长度及全部变化，随后只补新增尾部；
相对 DB-047，主要多出区间头，而不是为每个变化重复范围头。该项是结构分析，尚无最终字节数或性能排名。
纯 Copy 特码、local index 使用绝对值还是间隔、op/count 是否合码均待小实验决定，不先建设通用指令框架。

另一个有效候选是单调 old cursor 的 `Keep/Skip/Insert/Patch`：可节省源地址，对本轮插删目标已足够，
decoder 也能顺序填新 buffer，并不必然需要在可变列表上原地操作。其限制是不能直接反向/重复复用 source；
将它保留为格式成本对照，不能仅因不善于 Move 就预先淘汰。

### 5.2 必须闭合的局部规则

- newCount 有界；每个输出区间长度正数；source 范围、输出和加法严格校验，输出恰好填满 newCount。
- ReuseRange 的 source 可重复、可乱序，但只能来自 prior；不读取尚未构造的 target。
- 子 patch 索引相对于本区间，严格递增、不重复、不得越界；每个子 Delta 必须实际改变所选旧元素。
  现有 scalar/inline Apply 拒绝无变化子 Delta，不能为未变元素塞“空 Delta”。
- 缺省未写旧元素与输出 Literal 都不能绕过最终完整引用验证；layout 跨版本仍走现有 Base/Upgrade 边界。
- hash 只筛选候选，相等必须验证。非 canonical 整数、截断、超量输出、非法标签及尾随数据拒绝。
- 无变化由旧新状态是否逐位置相同确定，不能用“有指令”“发生重排”推断；重复值可以形成净 NoChange。
- writer 合并相邻可合并区间以节约头部；reader 不证明全局最短脚本。不可变输入在失败后仍可复用。
  两段 ReuseRange 只有 source 也恰好连续时才可合并，后段局部 patch 索引增加前段长度；合法的重复、重叠、逆序 source 不能机械合并。

每段只填写自己的输出范围，总复制/新值数量受 newCount 约束；乱序复用不意味着每条指令复制整份 prior。
因此这种语义可按输出状态大小线性物化，再加稀疏子 Delta 的应用成本；历史链仍有逐层物化成本。

## 6. 匹配算法候选与选码

| 候选 | 核心过程 | 优势 | 可接受的弱点与预算 |
|---|---|---|---|
| A：局部重同步 | 剥离公共前后缀；在有限窗口内找重新相等的片段；间隙按位置 Patch/New | 实现小；单处插删由前后缀已能解决；作为速度和复杂度对照 | 远距离/重复歧义可能错失好匹配；限制窗口、总候选和验证工作 |
| B：有界 Myers | 公共前后缀＋有界最短插删搜索；未匹配间隙按相对位置尝试 nested Patch | 首要较强候选；自然处理多处分散插删；不要求唯一锚点 | 大改/重排可能超限；限制搜索层、trace 内存及实际比较工作，超限回退 |
| C：有界哈希区间匹配 | 对旧元素或短块建索引，扫描新状态寻找可复用范围；频次限制并精确验证 | 能发现非单调块搬移、重复复用；作为进一步挑战者 | token/index 成本，重复热点、碰撞、匹配重叠；不承诺无条件线性或最优 |

Myers 通常简写的 `O((n+m)d)` 默认比较廉价；计入 `d=0` 的初始扫描应写 `O((n+m)(d+1))`，本项目还须再计元素比较成本。
只保存每层有效 frontier 的常见 trace 写法可达 `O(d²)`，不能把“找到长度”和“恢复完整脚本”的内存混为一谈；
首片先以可审查的预算封顶，不因论文存在高级变体就一并实现。

三种候选尽量共用同一试验 grammar，避免把 matcher 改进和格式改进混在一个数字里。
对齐仅提出 source 配对，编码器再用实际范围头、New Base bytes 与已准备子 Delta bytes 选择便宜表达，
并合并可合并片段。不把编辑操作数最少当作最终成本函数，也不做全局带权最优搜索。
参与备选成本比较的所有被丢弃 payload 都计入时间、分配；最终选中的 PreparedDelta 继续复用，不再次编码。

预算耗尽后可将未解决区域按位置 Reuse/Patch 或 New 表达，结果仍精确。
保持一个便宜的整段位置候选作为质量对照有价值，但是否每次都生成两份候选要看额外编码成本；
不能只承诺“取较小者”而把较大者的 CPU/内存藏起来。
最后 Base/Delta 仍交现有策略决定；本层不因搜索失败伪造 NoChange 或 Delta 尺寸。

共同盲区：每个大 struct 都改一点、同时发生插入时，可能没有任何完全相等的锚点。
exact 匹配无法推断业务上的元素身份；允许回退且必须纳入评测。要改善该类输入，未来可比较有界偏移候选及真实子 Delta 成本，
但不直接新增用户 key、相似度回调或假设某个字段是 ID。

## 7. 元素相等与融合 PrepareDelta 的关系

[IStateOps](../../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs) 没有 equality/hash。
[PreparedDeltaBody](../../../src/DurableGraph.Serialization/PreparedDeltaBody.cs) 是有 owned bytes 的对象；
[生成的 struct PrepareDelta](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericState.cs) 还会创建缓冲、递归准备子 Delta。
将它反复用作任意两位置的 equality oracle，会产生大量弃用编码，不能当作理想 Myers 的廉价比较。

调研得到两个可行方向；后续方案已选择静态逐字段 StateEquals，以下保留原比较依据：

| 方向 | 一致性与改动面 | 必须计入的成本 |
|---|---|---|
| canonical 元素 Base bytes＋offset，可加 hash | 直接复用 WriteBase；同 exact 槽按编码字节精确比较，hash 命中再比较切片；适合先做局部试验 | 双端编码、偏移表、hash、切片比较和 scratch；当前 PreparedBase 没有元素边界，prior Base 也未保留，不能假装这些材料已免费存在 |
| 无分配的 exact 元素比较能力，必要时再加 fingerprint | 新的匹配搜索消费者可以正当需要独立比较；标量/ID/SG 字段判断应共用语义定义，最终配对子 Delta 仍融合生成 | 接口、SG、builtin 和测试维护面；要验证与 PrepareDelta.HasChanges 完全一致，不能各自演进成两套规则 |

DB-049 直接实现无分配的静态 StateEquals；canonical bytes 可作测试参照，不先为生产匹配编码双端 Base。
引用元素比较 ObjectId；浮点保留 bit pattern；struct 只看持久字段。
`unmanaged` 不授权用 CLR 原始内存代替持久内容：padding、非持久布局均不是语义。
精确相等与 Count 可以回答列表 NoChange；最终变化 pair 的 bytes/尺寸依然由融合 PrepareDelta 提供。

预算要覆盖搜索次数、候选验证字节、子 Delta 准备及 scratch，而非只限制 d。
它是工作量上界，不是 wall-clock 保证；实验超时保护可以单独存在，不宜先使机器负载决定持久输出。
还须区分必需的线性准备/精确 NoChange/完整回退成本与额外搜索预算：整体是基础成本加预算内候选成本，
并非给很小的搜索预算就能免去完整扫描。预算耗尽但尚不确定是否相等时，必须完成精确判断或复用已有证明，不能直接标 changed。

## 8. 下一阶段的最小实验与裁决

用户后续接受通过同领域编辑历史、独立 Repository 对比少量 writer；已在 DB-049 形成产品与实验的联合施工安排。
本节矩阵作为验收素材保留，不同时建设大型 benchmark 平台、tracking 容器或新策略。

1. **语义与比较能力**：静态 StateEquals、区间语义 roundtrip 与畸形输入；纯 Copy 与稀疏 Patch 示例字节。
2. **候选实现**：Position/A/B 共用一套 codec；C 暂缓。固定 grammar 比 matcher，记录回退率和总代价。
3. **领域历史重放**：独立库运行相同编辑与 X/Y，依据保存端实测判断默认 matcher；冷读只作正确性和背景观测。

输入矩阵不必穷举所有组合，但必须覆盖下表的独立风险：

| 维度 | 素材 |
|---|---|
| 规模 | 小列表、约千项、约十万项；同时限制总状态字节，不只按项数放大大 struct |
| 元素 | 不同编码宽度整数、ObjectId、NaN/±0、小/大 inline、泛型嵌套 DTO、零字节状态 |
| 编辑 | unchanged/改后撤销、append/truncate/clear、头中插删、多处混合、稀疏字段更新、插入＋所有元素小修改 |
| 重排与重复 | rotate/reverse/shuffle、块搬移/复制；全相同、全 null、交替值、小字母表、高唯一率；hash 强制碰撞 |
| 恢复 | 多代 Base/Delta、共享引用、child-only 更新 List NoChange、invalid patch、错误边界与预算回退；预算提前耗尽但实际完全相等 |

分别记录 tokenization、匹配、子 Delta 编码、总 Diff；CPU/分配/峰值 scratch/比较与验证工作。
尺寸同时列 raw body、完整 D 上界、实际写入 D、Base/Delta 决策；Apply 单独测量。
再列 Capture、全量 PrepareBase、planning 的链读取、append，避免局部成本被转移出统计边界。

正确性见证用精确持久语义验证，不以同一有 bug 的 matcher 自证；随机小序列可独立物化检查，
小输入可用简单 DP 比较编辑质量，但不要求生产输出等于其最短脚本。
若额外进行固定深度链实验，须与 actual policy 路径分列；它不再是本片必做的冷读性能选型任务。

最终裁决应回答：最小候选是否已取得主要插删收益，强候选多花的复杂度是否有实测回报，
稀疏 struct 是否退化，静态比较是否成为主成本；更长 Delta 链仅记录，不抢占保存优化的优先级。
目前缺少真实业务负载，不设置虚构的综合评分或速度承诺。

## 9. 后续施工边界

新 grammar 不得冒用 List codec 1；本项目尚无需要兼容的部署数据，不以本研究为由自动建立旧格式读取/迁移层。
后续合同须明确新 codec 与 fresh fixture 的安排，保留 Base 指定 exact 表示、Delta 继承该表示的既有规则。
匹配算法变更本身不改变 Schema 身份；只有解码语法/解释变化才要求相应版本处理。

不在本研究中修改 public List API、ObjectId、SchemaStore/StateStore 分层、Upgrade 所有权或策略参数。
本轮没有候选 benchmark 结果，后续实现授权与最终格式裁决仍需分别明确。
