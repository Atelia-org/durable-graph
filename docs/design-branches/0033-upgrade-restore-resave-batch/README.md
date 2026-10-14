# DB-033：升级、领域恢复与增量续写的连续施工计划

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0033-upgrade-restore-resave-batch.md`，正文保留当片合同与历史验收含义。
> 阅读范围：标量/string 单对象升级、无构造器 Restore 与固定 Parent 续写的当片证据见 §10；原 LoadedWorld/宿主 Append 路径由后续会话及 DB-087 替换，本文不是当前施工计划。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

状态：**Chosen / Implemented，G0–G6 全部验收通过**；2026-09-07。
本批经用户 Goal 授权实施，交付范围限下述标量/string 升级恢复与续写闭环。
规划基线 `1c0557f`，实施前规划提交 `fb85313`；实际实现与验收见 §10。
产品事实入口：[PROJECT-STATE](../../../src/PROJECT-STATE.md)。

## 1. 一个可观察的总目标

让现有标量/string 领域模型完成：

```text
指定旧 Revision + 显式 WorldId
  → 完整 stored-exact DTO 目录
  → 单对象升级为 current DTO
  → 无构造器分配 + readonly Hydrate
  → 用户修改 World
  → 保持原 ID/Parent 的冻结保存计划
  → 宿主调用既有 Storage.Append
  → 关闭重开，从返回地址再次 Load
```

第一轮即使用户不修改字段，仍存活的升级对象也写 current Base；第二轮从新地址加载后，
无变化不再产生升级重写，有变化回到正常同版 Delta/既有 Base 预算策略。
最终交付真实产品 Load/Prepare 接缝及生成代码，不以 test-only 手写协调器代替核心职责。

这比单做 Upgrade 或 Restore 多闭合了“加载后的增量相对于谁”；又不要求一起设计 branch/head 发布。
本批仍不是完整 Repository Commit：WorldId 由宿主显式提供，Append 返回候选地址，不是发布凭据。

## 2. 决策来源与范围

| 来源 | 本批承接的约束 |
|---|---|
| 用户已确认的目标 | 冻结 Versioned DTO；Base-only exact 类型；旧链完整还原后才 Upgrade；升级对象下次仍 live 必须 Base；统一 string 身份、Empty 规范化 |
| 用户已裁剪的 MVP | 单 World；Upgrade 只转换单对象字段、不访问其他对象、不创造持久新对象；无 Transient hook；无 boxed value 对象支持 |
| 用户已采纳的恢复方向 | 无需无参构造器；RuntimeHelpers.GetUninitializedObject 分配；SG Hydrate；readonly 实例字段优先 UnsafeAccessor，包括声明于基类的 private 字段 |
| 当前产品事实 | DB-032 能读完整历史 DTO 目录；同版 Prepare/策略/Append 已有；新 DTO Upgrade、Restore、加载基线导入尚无产品实现 |
| 本批推荐收窄 | 现有 13 标量/string、非泛型同编译 class 链；固定加载所得 World 实例；不替换/清空根；Load/Prepare 不就地推进 Parent |

长期约束只维护在[目标设计](../../DurableGraph-target-design-v0.md)，相关技术证据见
[readonly 物化实验](../../DurableGraph-lab-notebook.md#2026-09-07无构造器分配与-readonly-实例字段写入)。

明确不包含：一般 durable 对象互引/多态/循环 Capture，struct、泛型、数组/BCL、新标量种类，
跨程序集继承，持久根格式，Branch/Ref/manifest、发布及崩溃恢复协议，Artifact/Derived，
Transient hook、ID 回收池、缓存/内存峰值优化、AOT 交付。
readonly 字段的类型也限于本批已支持类型；手写 readonly 循环见证不能当作一般对象图已实现。
这些是本批外事项，不是否决其长期方向。

## 3. 先核对这些真实接缝

| 源码/测试 | 当前事实与本批要接的地方 |
|---|---|
| [StateReaderBinding](../../../src/DurableGraph/Runtime/Binding/StateReaderBinding.cs)、[RevisionDecoder](../../../src/DurableGraph.Persistence/RevisionDecoder.cs) | 只得到 stored-exact DTO；扩展版本归一化与 current 模型能力，不抹掉历史 exact 验证 |
| [SG BinaryBody](../../../src/DurableGraph.Generator/DurableSchemaGenerator.BinaryBody.cs) | 有各版 DTO/body、稳定 ReaderVn 与 current Preparation；新 Upgrade/Allocate/Hydrate/加载后 Capture 应复用这些知识 |
| [SG 主体](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs) | 旧 UpgradeVnToVm 属于 legacy Snapshot；readonly 诊断同时服务旧路径，不能全局删除后让旧反序列化赋值无法编译 |
| [CaptureSession](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs)、[CaptureContext](../../../src/DurableGraph/Runtime/Capture/CaptureContext.cs) | Current 是内存来源；Prepare 要求稳定 preparation/Schema；无持久导入，不接受把 DecodedRevision 直接装成 Current |
| [CapturedRevisionPlanner](../../../src/DurableGraph.Persistence/CapturedRevisionPlanner.cs) | 要求 previous 完整覆盖 Parent，并拒绝 survivor 跨 Schema；要新增受控迁移分支，不能整体移除校验 |
| [PreparedObject](../../../src/DurableGraph.Persistence/PreparedObject.cs) | 已有 BaseOnlyUpdate，可消费合法迁移，不必重写策略 |
| [NormalizedGraphMaterializationProbe（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:tests/DurableGraph.Tests/NormalizedGraphMaterializationProbe.cs") | 可复用先分配后填充的思想；不是新 DTO 产品入口 |
| [生成冷读集成](../../../tests/DurableGraph.Tests/DecodedRevisionGeneratorTests.cs) | 现成真实 RBF/history 测试基础，可扩成升级续写闭环 |

代码事实优先于本文预估的类型名称；下一轮先读完整 PROJECT-STATE，再读本文和表内涉及的文件。
DB-032 的历史验证是 800/800；下轮必须观察自己的基线，不把此数字当作新执行结果。

## 4. 本批的最小状态模型

### 4.1 一个受控加载 owner

暂称 LoadedWorld；对外入口示意为 Load(store, schemas, revisionAddress, worldId, models)。
实际名称、参数组织及可见性在 G0 收敛，但以下职责固定：

- 同时拥有 exact Parent、原完整 source membership/source Schema 摘要、归一化 current DTO 基线、
  每对象 RequiresRewrite、World 实例、实例到 ID 的反向绑定和本会话分配游标。
- 由受控 Load 建立这些对应关系，不公开接受任意 DTO/Parent 组合的导入构造器或 setter。
- 完整 source ID 集合上的 DTO 可升级为 current；即使某对象因升级删边不再可达，也不能把它的
  source membership 事实抹掉。只保留一份归一化比较基线及必要来源元数据，不长期维护两份可接受的
  stored/current DTO 基线。实现可以分开容器表达不同角色，但不制造第二个发布权威。
- 不是 CapturedGraph 的假 roots 包装。若复用 CapturedObject/现有容器，应准确说明 source-live
  与 current-reachable 的差异；不能为了沿用名称而伪造 Capture 来源。
- 只交付指定 ID 的 exact current World 实例，其字段目前只能是标量/string。拒绝 ID 0、缺失、
  string 根或不匹配的领域类型；不猜最小 ID 或“唯一像 World 的对象”。空根、替换根另片处理。

本批可只物化 World 及其 current string 依赖；完整 source 目录仍须完成既定的解码/升级/引用校验。
多模型族归一化不要求暴露多根 API，不能因某 source 行目前不可达就绕过未知 reader/非法升级。

### 4.2 Prepare 不推进基线

LoadedWorld.Prepare（暂名）从固定 World 实例捕获、准备 owned bytes，并产生绑定原 Parent/WorldId
的计划。调用方不能给这份计划替换 Parent；可保持 internal low-level planner，但须有实际公开消费者。
每次临时 Capture 在成功/失败后都能释放，推荐内部 finally Discard；计划内容独立于之后领域变动。
重复 Prepare 可以烧掉新 ID，不保证未提交新增实例得到相同号码；原加载基线与重写义务始终不变。

宿主使用既有 Storage.Append 追加该冻结计划，之后从返回的新地址和计划固定的 WorldId 重新 Load。
本批不提供 LoadedWorld.Accept(address)、Commit 或原地更新 lastRevision 的捷径。
重新 Load 自然建立新基线：若盘上已为 current Base，就不再有这次升级的 RequiresRewrite。

Schema 注册的持久副作用或未发布 State 帧允许保留；它们不表示业务 head 已推进。
Append 的 flush/发布/不确定结果不在本批新增承诺里，不声称自动幂等重试或 crash recovery。

### 4.3 两个必须明确的身份选择

**多个 Empty ID：** source 的 ID 3、9 可都指向 Empty。反向实例绑定确定选择 source 中最小 Empty ID 3，
但保留基线 DTO 原来引用 9 的槽。下次 Capture 产生真实的 9→3 引用差异，再由同版 Delta 或升级 Base
保存；9 随 current 不可达差集 Remove。禁止先静默把基线改成 3，再以 NoChange + Remove(9) 留下悬空引用。
等值非空字符串继续分别保留身份，不做值相等 intern。

**新加载会话的分配起点：** 取完整 source live ID 的最大值 + 1，包括升级后不可达的行，使用 ulong
表示 uint 域耗尽哨兵。失败烧号不回退；source 最大 ID 为 uint.MaxValue 时 Load 仍可成功，
只有实际需要分配新 ID 时才失败，已有对象无变化或升级重写仍可 Prepare。
只承诺会话内单调，可能在新会话使用旧 Revision 已退役的数字；新占用者按 New→Base，不接旧 Delta。
不扫描全历史或增加持久 high-water。此选择承接 [DB-024 的会话内边界](../0024-reference-capture-and-reusable-object-ids/README.md#11-首片初始目标本次用户确认后的收窄)，
不能称作 Repository 全历史永不复用，也不是实现了回收池。

## 5. 按依赖连续执行的 gates

本批获实施授权后，依次完成 G0–G6；通过一个 gate 后继续下一个，不为常规命名/类组织/测试分派反复停下。
每个 gate 可以形成独立 commit，但不是要求用户逐步重新授权。新功能分支只有超出 §2/§4 才需讨论。

### G0：固定跨层接缝和升级函数创作形式

- 先运行根 build/tests、检查工作区，建立本轮账本；保护无关改动。
- 在 Runtime/SG/StateStore 中收敛最小模型能力绑定、归一化结果及 loaded owner/plan 形状。
  保持 StateStore → Runtime + Storage，Runtime/SG 不认识磁盘地址；不新拆程序集。
- 升级推荐相邻 Vn→Vn+1 的单对象强类型 `in old, out next` 静态方法，区别 legacy Snapshot 路径。
  方法命名/SG 识别方式可用最小编译见证选择，编译器应检查 DTO 类型和 out 确定赋值；不做 DAG 路由。
- reader-only 消费者仍能生成/读取历史 body，不因没有升级函数就被强迫补齐所有迁移。
  current 加载缺路径必须明确失败；可以识别显式声明的升级方法或提供窄的加载能力 opt-in，
  在本 gate 选择其中一个并记录依据，不同时建设两套创作协议。
- 产物：短接口清单、需求到 gate/测试映射及已选创作形式；实际类型数量由消费者需要决定。

### G1：真实 SG 的新 DTO 升级

- 为 BinaryBody.Vn 生成/绑定升级调用，按同 SchemaId 的相邻历史路径得到 current DTO。
- 已 current 不伪升级；缺边、未来版本、错误签名/Schema 或用户函数异常明确失败；不回退 latest。
- 继承以 leaf 完整 DTO 布局升级，避免自动升级祖先后又在 leaf 转换一次而重复处理字段。
- 不提供其他对象查询、新 ID 或持久新对象创建上下文；引用仅转换已有 ID 槽并在归一化后校验。
- 验收：至少 V1→V2→V3、继承 exact base 变化/旧 CLR 祖先删除、缺路径/抛异常、current 快路；
  DTO 值与版本正确，输入和文件不变，reader-only 原测试仍通过。

### G2：完整目录归一化与重写义务

- 接 RevisionDecoder 先还原完整旧 Base/Delta，再逐对象升级；保留完整 source membership、
  stored Schema 摘要及 current DTO，升级过的对象设置 RequiresRewrite。
- string 行保持解码实例身份；所有输出 string ID 在目标视图验证。升级删掉引用不等于立即删除 source 行。
- 失败不返回半份目录、不修改输入、不注册/写盘；不要在 Upgrade 每条 Delta 的过程中切换 Schema。
- 验收：混合模型族/current/history；多 Delta 先还原后升级；删除 string 字段；升级给出缺失/错 kind
  引用时失败；值未变但 Schema 已变仍有重写义务。

### G3：生成分配与 readonly Hydrate

- 接 current DTO 生成 Allocate/Hydrate；GetUninitializedObject 不执行实例构造器或初始化表达式。
  普通字段直接赋值，readonly 用按声明类型生成的 UnsafeAccessor；基类 private 字段由声明层处理。
- 同步解除适用的新 DTO 路径 readonly 诊断，检查其 Capture、Base、Delta、Read/Prepare 全链；
  不能全局删诊断后破坏 legacy 的直接赋值反序列化。legacy 可继续明确拒绝，不做无关迁移。
- 通过同一 StringReadTable 将 current string ID 解析为对应的 string 实例；无 Transient hook，用户在交付后自行初始化。
- 验收：只有有参构造器、private 基类 readonly scalar/string、字段初始化值未执行、持久值准确、
  等值独立/共享非空字符串和 Empty/null 保真，失败不交付 World。不要用此声明循环 durable 图已实现。

### G4：受控加载基线及身份导入

- 将 G2/G3 组合为 §4 的加载 owner，只有完整成功才交付 World 和可准备保存的会话。
- current preparation 使用 SG 稳定绑定，基线直接来自升级 DTO，不通过重新 Capture 领域对象猜测磁盘状态。
- 实例映射只持有需交付/捕获的实例；source 事实与 current 可达集合的处理不遗漏 Removes。
- 实现 §4.3 的 Empty 反向映射与 fresh session 分配规则，无公共任意地址/DTO 导入入口。
- 验收：两个 Empty ID 的原 DTO 槽不被预改写；source 中已不可达最大 ID 不被新对象占用；
  uint 耗尽不阻止 Load/已有对象 Prepare，但拒绝新增分配；选错 World ID/kind/type 拒绝；输入 readonly 目录不被篡改。

### G5：加载后保存准备与跨 Schema Base

- 从 World Capture 得当前完整可达集合，与 loaded normalized baseline 比较，保留正常冻结和候选清理。
- still-live 且 RequiresRewrite 的对象直接 BaseOnlyUpdate，不制造跨版 Delta，不让 NoChange 或预算抹掉义务。
  未升级同版对象沿用融合 PrepareDelta、真实 PrepareBase 与既有策略；不可达 source 行输出 Removes。
- 持久预检核对 stored source Schema/Parent 与加载来源；仅受控迁移分支允许 current Schema 不同。
  不把一个任意 bool 当作跳过 Schema 校验的通行证，不改固定策略算法。
- 原无升级 Capture/Prepare 路径仍有效。Loaded Prepare 可注册合法 current Schema，但不 Append/发布/Accept。
- 验收：无业务修改仍强制 Base；删边 Remove；Prepare 后再改字段不影响计划；重复 Prepare、编码失败/
  Schema 冲突、Discard 后基线与重写义务保持；新字符串 ID 及其 Base 正确。

### G6：真实持久闭环、包交付与独立审查

- 使用真实 history、Schema 日志、State Segment 文件；宿主追加 G5 的计划，再关闭重开并重新 Load。
- 第一轮：旧版 Base + 多 Delta → 升级/恢复 → 无修改 → current Base；下一轮：已 current、无变化，
  策略无主动重写动机时无对象写入；再改一个字段，选允许 Delta 的参数与数据，回读正确。
- 单独验证两个 Empty ID 引用第一次续写的真实归一化 Delta/Remove，以及升级删 string 字段的清理。
- Append 确定失败后，原 loaded owner 仍保有旧 Parent/基线/重写义务；不得用就地 Accept 模拟成功。
  追加结果不确定不宣称自动重试保证。旧 Revision 仍可读取。
- 更新实际 Runtime/StateStore 包消费者以验证新 SG 和公开 Load/Prepare，不通过 friend-only 测试冒充可消费。
  独立 reviewer 审核 source/current 边界、Schema 迁移授权、实例身份、冻结内容和“非 Commit”的实际 API。

## 6. 执行方式与验证节奏

G0 先由主代理收敛接口；其后 Runtime/SG/StateStore 可按已冻结接缝分派不相交文件。
有依赖时先串行整合，不能用子任务合并顺序代替设计选择。关键 gate 由其他 reviewer 检查，
主代理看实际 diff 和输出；所有 dotnet 集中串行执行，避免共享 obj/bin 锁冲突。

代码 gate 完成后运行 `dotnet build DurableGraph.slnx --verbosity quiet` 及相关 focused tests；
组合到持久路径时运行完整 `dotnet test DurableGraph.slnx --no-build --verbosity quiet`。
最终运行：

```powershell
./experiments/PackageConsumerProbe/Run-Probe.ps1
./experiments/PackageConsumerProbe/Run-StateStoreProbe.ps1
```

focused 测试名由实际实现决定并在账本记录，不预造不存在的命令。字节格式未变，无需重写 wire golden；
新增输入失败、版本迁移和所有权场景必须有行为证据。测试通过后不反复重跑无关矩阵。

每个检查点在本文件追加简短 gate 状态/commit/验证；PROJECT-STATE 只维护当前 gate 与入口。
结束时压缩当前工作集，长期约束继续在 target，剩余事项回 roadmap，勿复制完成流水账到所有文档。

## 7. 停止条件与允许自主选择的范围

**完成：** G0–G6 全部通过，真实旧链升级续写闭环和两个包消费者成立，独立审查无阻塞，
本批改动有验证、有解释并按授权提交。此时停止，不自动进入一般引用图、struct/数组/泛型或 Commit。

**可自主处理：** 类型/方法命名、内部容器、delegate/生成 helper 组织、测试夹具、相邻升级方法的
具体创作方式、必要的现有 API 重构、局部性能 TODO、依赖明确后的 subagent 分工与提交粒度。

**需要报告并暂停依赖工作的偏离：** 只有扩大持久格式/发布语义、扩大类型支持或引入第二个基线权威
才能满足验收；所选 UnsafeAccessor 路线在目标环境出现无法局部解决的障碍；源代码出现重大新漂移。
先给出具体反例和最小候选，不因命名不确定、测试失败或工作量较大就把整批留给用户重新设计。
不能因某阶段困难就称整个批次完成；若范围确须重选，保留已验证进展和下一步入口。

## 8. 给下一轮 coding agent 的任务文本

以下保留原实施请求草稿作为计划来源；后续用户已通过 Goal 授权本批，完成证据见 §10：

```text
请按 docs/design-branches/0033-upgrade-restore-resave-batch.md 实施 G0–G6，完成现有标量/string
模型的历史 DTO 升级、无构造器/readonly Restore、受控加载基线及增量续写闭环。
先读适用 AGENTS.md 与 src/PROJECT-STATE.md，核对源码和基线；本文中的 Proposed 批次范围由本次消息批准。
可以带领 subagents 分工，关键设计分歧先交叉审查；集中串行运行 dotnet，主代理负责实际集成验证。
在批次边界内自主收敛接口、修复回归并按需 git 提交，不推送。每个 gate 通过后继续，不逐步请求确认。
保留 source 完整 membership 与 normalized DTO/RequiresRewrite；多 Empty ID 不预改写基线槽；
fresh session 从完整 source live max+1 分配，只承诺会话内单调。
Load/Prepare 不就地 Accept 新地址；由宿主用既有 Storage.Append，随后从新地址重新 Load 验证。
不实施 general durable 引用、struct/泛型/数组/BCL、持久 roots、Ref/Commit/发布恢复或 Transient hook。
通过根 build、相关/完整 tests、两个实际包消费者和独立 review；更新账本及活跃文档后停止。
遇到必须越出本文范围的真实障碍，先记录证据和最小选择，不擅自扩大，也不把局部完成当作全批完成。
```

## 9. 规划阶段核对（实施前）

主代理核对产品读取、Capture/Prepare、迁移分类与原始 Append 接缝，三位 subagent 分别从需求、
最小架构及语义反例审视 A（Upgrade/Restore）、B（再接增量续写）、C（再加一般引用）三种终点。
交叉讨论后推荐 B：A 留下重写义务无消费者，C 引入新的引用 Schema/格式范围。
撤回为“跨会话不复用”扫描祖先高水位的建议，因为现有约束仅承诺会话内单调。
明确采用 Empty 原基线槽保留、下一 Capture 自然归一化，以及 Append 后重新 Load 的边界。
该规划阶段只核对文档，没有执行新的产品 build/tests；其后的实施验证另见 §10。
草稿经独立语义复审，无阻塞项；补清耗尽游标不阻止 Load/已有对象 Prepare 的边界。
4 份 Markdown 的 137 个本地链接及引用锚点有效，UTF-8/LF 与 Git diff 检查通过。


## 10. 实施合同与验收账本

2026-09-07 用户通过 Goal 授权完整 G0–G6，主代理按 spec-driven-implementation 组织分工与集成。
范围和停止条件沿用 §2、§4、§7，不新增格式、类型或发布协议。

G0 基线：工作区干净；根 build 0 warning/error；根 tests 800/800（397 + 145 + 155 + 103）。
升级创作选择显式普通静态 `UpgradeStateVnToVnPlus1(in old, out next)`，不同于 legacy Snapshot；
缺方法仅 current 归一化失败，reader-only 不被迫实现迁移，生成调用交由编译器检查 DTO/out 类型。
Runtime 的稳定 `StateModelBinding<TDomain,TState>` 集合 exact readers、current preparation、
归一化和 Allocate/Hydrate/Capture；SG 发出静态 Model，StateStore 注册并快照能力目录。
抽象模型保留归一化能力，但不能 Allocate。Runtime 不引用 Storage。
LoadedWorld 只导入 CaptureSession 的实例身份和 ulong 游标，不伪造 Current/CapturedGraph；
完整 source membership/Schema 摘要与唯一 current DTO 比较基线由受控 owner 持有。
反向身份表可持有完整 source 的 string 实例（这些实例本来就属于基线），最小 Empty ID 优先；
实际下次 Capture 决定 current 可达集合，不为筛掉表项另行 recapture 基线。

| 需求 | 状态 | 实现归属 | 验收证据 |
|---|---|---|---|
| G0 基线、跨层契约及创作协议 | verified | 主代理 Runtime / SG / StateStore 接缝审议 | 上述基线及接口清单 |
| G1 相邻强类型升级、缺路径及历史继承 | verified | [SG StateModel](../../../src/DurableGraph.Generator/DurableGraphGenerator.StateModel.cs) | [GeneratedStateModelTests](../../../tests/DurableGraph.Tests/GeneratedStateModelTests.cs)：V1→V2→V3、current 快路、每声明边签名/缺边/异常、旧祖先删除及 leaf 独占升级 |
| G2 完整归一化、source/current 边界 | verified | [NormalizedRevision](../../../src/DurableGraph.Persistence/NormalizedRevision.cs) | [LoadedWorldTests](../../../tests/DurableGraph.Persistence.Tests/LoadedWorldTests.cs)：完整多 Delta 后升级、混合 current/history、不可达 source 失败、错误引用及零写入 |
| G3 readonly / 无构造器 Hydrate | verified | SG StateModel + [Runtime binding](../../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs) | GeneratedStateModelTests、[全部 13 标量位模式](../../../tests/DurableGraph.Tests/LoadedScalarHydrateTests.cs)：private 基类/叶类、初始值不执行、string 共享/独立/Empty/null、再次 Capture/Base/Delta/Read |
| G4 Loaded owner 与身份导入 | verified | [LoadedWorld](../../../src/DurableGraph.Persistence/LoadedWorld.cs)、[CaptureSession](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs) | LoadedWorldTests + [LoadedCaptureIdentityTests](../../../tests/DurableGraph.Tests/LoadedCaptureIdentityTests.cs)：保留原 Empty 槽、完整 source max+1、耗尽已有对象可保存、错误根/类型、无伪造 Current |
| G5 固定 Parent Prepare 与强制 Base | verified | [LoadedRevisionPlanner](../../../src/DurableGraph.Persistence/LoadedRevisionPlanner.cs) | LoadedWorldTests：强制 Base、真实 Remove、冻结/repeat、编码/重入/Schema 冲突、确定 Append 失败不推进基线；原 Capture/策略回归保持 |
| G6 冷重开、包交付、独立审查 | verified | 主代理集成与独立 reviewer | [LoadedWorldGeneratorTests](../../../tests/DurableGraph.Tests/LoadedWorldGeneratorTests.cs) 两项真实历史/RBF/Segment 冷重开；两个 [PackageConsumerProbe](../../../experiments/PackageConsumerProbe/README.md)；独立复审无未解决阻塞项 |

### 实际 API 与所有权

```csharp
var models = new StateModelRegistry();
World.__DurableBinaryBody.RegisterModel(models); // 在领域类型所在编译中使用生成 helper。
var loaded = LoadedWorld.Load<World>(store, schemas, oldAddress, worldId, models);
// 宿主初始化 Transient，再按业务需要修改 loaded.World。
var plan = loaded.Prepare(new ReadAmplificationBaseBudgetParameters(3, 5));
var nextAddress = store.Append(plan.Revision);
var next = LoadedWorld.Load<World>(store, schemas, nextAddress, plan.WorldId, models);
```

`PreparedWorldRevision` 无公共构造器/可写 Parent；`LoadedWorld<T>` 无 Accept/Commit/替换根接口。
相邻升级方法声明在领域类，参数是生成的 readonly DTO，通过其构造器产生 next。
每条已声明边都有独立 typed wrapper，即使后续缺边，也不能逃过 DTO 类型与 out 赋值检查。
Runtime current binding 将归一化 DTO 与稳定 Preparation 配对；抽象类型保留 reader/归一化及声明层
Hydrate，但不能分配为 World。内部来源元数据与比较索引引用同一 current DTO 行，不另复制旧 DTO 基线。

### 集成验证与已处理审查项

- 根 `dotnet build DurableGraph.slnx --verbosity quiet`：0 warning/error。
- focused：SG/Runtime 初轮 10/10、StateStore 20/20、生成持久闭环 2/2；随后补入两项签名缺边
  回归及一项全部标量 readonly 恢复，并纳入最终完整回归。
- 最终 `dotnet test DurableGraph.slnx --no-build --verbosity quiet --logger "trx;LogFilePrefix=db033-verified"`：
  **835/835**，0 skipped（Runtime/SG 412 + StateStore 165 + Storage 155 + Serialization 103）。
  TRX 位于各测试项目忽略的 TestResults，前缀 `db033-verified`。
- `./experiments/PackageConsumerProbe/Run-Probe.ps1` 通过：原 marker + GeneratedModel/ReadonlyRestore，
  7 history；最终产物 `experiments/PackageConsumerProbe/obj/run-20260907044456-30888`。
- `./experiments/PackageConsumerProbe/Run-StateStoreProbe.ps1` 通过：保留原 7 marker，新增
  HistoricalUpgrade/ConstructorFree/ReadonlyHydrate/ForcedBase/UnchangedResave/NormalDelta/ReopenedWorld，
  World V1/V2 来自实际 history 发布（总数 3→4）；产物
  `experiments/PackageConsumerProbe/obj/state-store-run-20260907044341-15080-70e0e720`。
- 独立审查发现“后续缺边隐藏前面错误升级签名”，已修复并复审关闭。首次全量测试的 5 个失败
  是旧 helper 全局禁止 typeof/Upgrade 的范围断言；现在用 Roslyn 逐方法检查：typeof 只允许
  Allocate 中的 GetUninitializedObject，所有 body 仍禁止类型查找、Upgrade、反射与 registry 分派。
  初轮夹具的 history 目录初始化和 readonly DTO 构造方式错误也已修复，最终无遗留失败。
- 没有新增 wire 格式、程序集、Runtime→Storage 依赖或策略算法分支；Storage/Serialization 产品代码未改。
  Prepare 已有和新增的失败清理、完整来源预检、浮点位模式及 Empty 真正落盘差异均有直接行为证据。
- 文档/工作区检查：36 份修改文件 UTF-8/LF，6 份 Markdown 中 170 个本地链接及锚点有效；
  主代理审阅整体 diff，`git diff --check` 通过。

本批在这里停止。一般引用图、复合值/数组/泛型、持久 World 根、Commit/Ref/发布恢复与 ID 回收
仍按[路线图](../../DurableGraph-research-roadmap.md)另片推进；保留的性能 TODO 不影响本批语义验收。
