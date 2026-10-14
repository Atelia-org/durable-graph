# 工作单：string 引用 Capture 与单调 ID 候选生命周期

> 已完成施工记录，保留当时合同与验收；不再作为续工入口。当前工作见 [产品工作集](../src/PROJECT-STATE.md)，剩余事项见 [路线图](DurableGraph-research-roadmap.md)。

> 状态：G0 已获用户裁决 / G1–G3 已实现并通过验收。仅闭合 string 引用 Capture 与内存候选分片。
> 本文写于 2026-09-06；用户已启动 Goal 并明确采纳 G0 推荐方案。
> P1 生成器到 P2 对象列表的内存子片；Goal 开工提交 `8816f0c`，最近产品提交 `0b9652c`。
> 详细设计：[DB-024](design-branches/0024-reference-capture-and-reusable-object-ids/README.md)。

## 1. 授权、事实与完成目标

当前用户明确认可引用 Capture/候选生命周期方向，并要求“先用单调递增的 id 分配策略”“延后 id 回收”，
且最初要求“在进入实施阶段前，先用文档写下初始目标设计和思路”。交接文档完成后，
用户已启动本文对应的实施 Goal，并明确先呈现 G0，收到尚未裁决分支的决定后再继续。
旧会话允许自主按需本地提交，不包含 push 或外部发布。
文档是设计证据，不因写有 Approved/Ready 就产生行动授权；遵循实际环境指令层级。

| 编号 | 来源与 authority kind | 本片要求 |
|---|---|---|
| R1 | 用户此前选定 DTO 保存输入；本轮认可 DB-024 分析 | 捕获领域引用成 ID，封闭 immutable 候选，不在写阶段重扫领域对象 |
| R2 | 用户统一引用身份决定 | string 按 ReferenceEquals 登记，内容相等不合并；root 与 string 同一 ID 域 |
| R3 | 当前用户直接决定 | 单调非零 uint 分配；数字回收/SlabBitmap/SlotPool/GC 延期 |
| R4 | 本轮认可的候选生命周期建议 | parent 隔离；显式 accept/discard；只安装实际捕获状态 |
| R5 | 既有用户静态绑定、继承/历史决定 | 已知成员直接调用；current/base Capture 共享上下文；历史 DTO 使用 exact 布局 |
| R6 | 用户批准分片、根 AGENTS.md 的最小实现/验证要求 | 保持现有层次，以真实 SG 和包消费者证明交接；不进入完整 Save/Restore |
| N1 | 用户 Goal 明确的 R3/R4 实施合同 | 失败/discard 消耗号码，高水位不回退；退役映射释放但数字不回收 |
| N2 | 用户 G0 裁决，真实下游已验证 | Runtime 放在 DurableGraph；最小公开 Capture 类型供下游生成代码调用，helper/DTO 仍 internal |

单一完成目标：下游程序集的真实 SG 将标量/string 领域 roots 捕获成显式 ID 对象列表，
保持引用身份和冻结值，并通过同一内存会话的多次 accept/discard 验证基线隔离及单调分配。
到此停止；本片不是可持久提交或可恢复领域对象的 StateStore。

## 2. 当前基线与实现位置

开工前必须读根 [AGENTS.md](../AGENTS.md)、完整 [src/PROJECT-STATE.md](../src/PROJECT-STATE.md)、DB-024，
并记录当时 git status。本文准备时工作区干净；后续不能假定仍然如此。

交接时核对的开工前事实（保留用于比较，当前实现见 §5）：

- `src/DurableGraph.Generator/DurableSchemaGenerator.BinaryBody.cs`：生成 internal readonly Vn、current Capture、
  typed Write/ReadVn；IsBinaryScalar 当前排除 String。当前/历史 DTO 依 exact 祖先闭包生成。
- `src/DurableGraph/TypeTag.cs` 与 `DurableFieldInfo.cs`：String tag=4 已属于 Schema/history；
  新 ID 槽位属于 DTO 表示，不需要把 Schema 的 String 改成 UInt32 或新增 TypeTag。
- Runtime 已单向引用 Serialization；其 UInt32 原语公开。无需 StateStore/Storage 反向依赖、无新程序集。
- `src/DurableGraph.StateStore.Serialization/Serialization/StringPayloadCodec.cs` 是内容 codec；
  当前空串解码可返回 string.Empty。首片不改其算法，也不以它声称引用恢复成立。
- 已有 `VersionedStateDtoTests.cs`、`StateDtoHistoryIntegrationTests.cs`、`ScalarStateDtoTests.cs`
  及 `GeneratedBinaryBodyTests.cs` 均在 `tests/DurableGraph.Tests/`；具备真实 SG 编译/执行及 history publisher helpers。
- `experiments/PackageConsumerProbe/Run-Probe.ps1` 验证单 runtime PackageReference；consumer 中
  `Domain.Binary.cs` 是现有 DTO 下游。无须修改旧 MultiSegment/TwoLeg Probe。

G0 等待裁决期间已在 `1c4f2e9` 重新验证基线（2026-09-06）：根 build 0 warnings/errors；
根 `dotnet test --no-build` 全部 471/471，通过数为 DurableGraph 264、Serialization 94、Storage 73、StateStore 40，零跳过；
真实 PackageConsumerProbe 通过，产物位于 `experiments/PackageConsumerProbe/obj/run-20260906022525-33812`。
这只证明改动前产品基线；本片实现与验收结果单独见 §5，不能用这些旧结果声称引用 Capture 验收已通过。

## 3. 首片合同与 G0 需冻结的形状

### 范围固定

- 输入是显式领域 roots；每个领域类型仍符合 SchemaOnly + GenerateBinaryBody 的现有 class/继承限制，
  持久字段为 13 种标量或 string。支持同一批多个 concrete root 类型、重复/null roots 和共享 string。
- 不支持 Durable 类型字段互引、一般 struct、数组、BCL 容器、boxed value 身份、跨程序集领域继承。
- root 的运行时类型必须匹配所选 capture binding 的 concrete 类型，避免派生对象被当 base root 截断。
  base helper 的段级调用仍允许派生实例；它不单独登记 base 为另一个对象。
- 相同 roots 顺序与字段顺序给出确定的发现次序；每层按 FieldId，继承 base-first。
  不承诺根顺序改变后新 ID 仍一样，不按内容排序/重编号来制造图 canonicalization。
- string 内容条目保留原不可变字符串；引用槽保存 uint，null=0，不建 null 条目。
  当前 String kind 尚不区分 nullable annotation，string/string? 均沿既有元数据语义允许 null；不另建 nullability 格式。

### 候选与会话所有权

- 从空 session 开始，单线程、一个在途候选；从 Begin 到 Seal/失败之间由调用方保证领域视图稳定。
- Session 拥有单调分配高水位和当前 accepted 状态/实例绑定。候选拥有新绑定、roots、ID→条目与 live 集合。
  可借用 parent ID，但不修改 parent 的 DTO、成员集合或实例绑定；分配高水位允许提前前进。
- Capture 先登记 ID 再处理条目；重复引用不重复捕获。标量 DTO 不持有领域对象；string 条目保留不可变内容。
  管理用 source→ID 映射单独持有，不能通过公开候选条目泄漏可变领域实例。
- Seal 后候选 roots、条目、集合均不可修改，不能暴露可写 Dictionary/数组的旁路。
  清楚区分仍在构建的内部数据与可交给下游的完整候选；失败不返回“可接受的部分图”。
- Accept 验证候选属于此 session、来自当前 parent、且未 accept/discard；安装捕获结果和对应绑定。
  Accept 之后只保留本次 live 实例绑定；退役 CLR 实例重新入图按新对象处理。
- Discard/构建失败保留 parent，只清理本候选临时绑定；号码已消耗。丢弃后重试的新对象拿到更大 ID。
  只读候选数据可以留作观察，但不能跨 session、重复或过期 Accept。
- uint.MaxValue 最后一次分配有效，下一次分配明确失败，绝不 wrap；内部 wider counter 或显式 exhausted 状态
  由实现选择。允许 internal near-limit 测试接缝，不增加用户配置型 allocator 框架。
- 该单调性仅属于本次 session，不实现 reopen/import、持久分配高水位、pin、候选并发或真正 commit publication。

### 生成代码接缝提案（G0 的评审点）

暂名 `CaptureSession` / `CaptureContext` / `CapturedGraph`；DTO 和 generated helper 继续 internal。
推荐沿现有 Runtime 暴露必要的 context/条目访问边界，接缝不拥有磁盘 Schema/StateRevision。
对象边界可以装箱 readonly DTO，字段级不能引入 Type 字典、反射或 ValueSlotCodec 调用。

根入口有两种局部实现候选：调用方显式提供强类型 capture binding，或 SG 生成每类的 root 登记适配器。
两者都必须检验 exact concrete root、保存其 Schema 与 typed DTO，不生成全局 registry、运行时反射扫描或持久类型号。
这是实施前必须冻结的跨程序集接缝；用户随后采纳了具体推荐方案。
2026-09-06 的具体签名、调用示例和独立只读评估已写入 DB-024 §3.1：推荐 SG root 适配器，
两方案共用一个 Runtime 泛型入口，DTO 保持 internal/unmanaged，封闭条目按值返回 DTO。
用户随后明确采纳该推荐方案；G0 已冻结，尚须实施和验证。

String 槽位生成目标为 `uint SegmentNFieldM`；Schema/history tag 仍为 String。
current Capture 通过共享 context 取得 ID，Write/ReadVn 直接 UInt32，不接图解析器。
有 string 的 current 布局必须传 context；不提供暗中创建孤立 ID 表的无参便利重载。
纯标量现有 `Capture(value)` 保留，供既有消费者使用；它参与图捕获时使用同一会话的 root 接缝。
历史版本即使包含已删除的 string 字段，也生成 ID DTO 与 typed ID body；不生成历史领域 Capture/Restore。
ReadVn 只读引用数字，不能独立判断目标是否存在；完整图加载验证另片完成。

此处 byte body 仅延续已有 field 顺序和 UInt32 原语：没有 string 内容、对象头、TypeCodec 或完整 graph wire。
旧 binary body 从未支持 string，因此不引入旧 inline string body 兼容路径；legacy boxed string 路径保持。

## 4. 依赖关口与证据

### G0 — 冻结最小下游接缝（R1/R2/R5/R6，N2）

先对上节两种根入口做签名/生成形状核验，比较下游可访问性、exact root 拒绝、私有字段/继承可访问性，
以及需要公开多少 Runtime 类型。不写完整 registry 来跳过问题。
输出一个短示例和选定签名，记录到 DB-024；根 AGENTS 要求把 material public API 选择先呈现给用户。
若两种方案有实质不同公共合同且用户尚未裁决，停在 Draft 并报告差异，不标签为“实施就绪”。
若用户启动时已经批准具体 G0 方案，则先复核其能在真实下游成立，再继续。

### G1 — 单调会话与封闭候选（R2/R3/R4，N1）

消费 G0 已冻结接缝，在 `src/DurableGraph/` 实现最小会话/构建器/条目与生命周期。
不得在 Serialization 增加 Schema/对象图依赖，或把这些职责放进 Storage。
聚焦验收：共享与不同实例不混、0 只表达 null、重复/null roots、parent 隔离、accept/discard/异常，
退役映射清理但不复用号码、跨 session/重复/过期 candidate 拒绝、near-limit 耗尽。
异常测试在至少登记一个新对象后失败，验证 parent 不变且后续分配不退回旧高水位。

### G2 — 真实 SG 的引用 DTO 与历史（R1/R2/R5）

将当前/历史 string DTO 槽位转换为 ID；Capture 的基类 helper 共享上下文，Write/ReadVn 静态使用 UInt32。
验证私有字段、Transient 忽略、跨两个 root/继承段共享 string、相等内容不同实例、null/空串/孤立代理项。
Seal 后改变领域字段或引用，候选 DTO/条目保持原值，Accept 不重新 Capture。
独立 ID golden + typed ReadVn 往返只证明数字布局，不宣称已恢复字符串或领域对象。
真实 publisher→accepted history→recompile：旧 string 字段/旧 CLR 祖先消失后仍可生成旧 ID DTO。
未知/损坏 history、未升版本仍拒绝；旧 string DG0020 测试须改成正向见证，其他拒绝边界不能删掉。
Schema-only metadata、legacy boxed、13 标量及其原字节回归保持。

### G3 — 包消费者、独立审查与收尾（R6）

扩充真实包消费者，验证无需 friend 或手工 analyzer wiring 即可 Capture/Seal/Accept/Discard；
多种 concrete roots 共用 string、重复保存身份稳定，必要接口均可访问。
对子代理分派有明确文件所有权的任务（沿用用户此前允许按需委派），先冻结接缝再并行写，主代理检查实际 diff；
独立只读审查关注身份、所有权、history 与 API 范围。审查结论必须由代码和测试证实。
完成全部验收后更新工作集、DB-024、包说明及实验笔记；整理本 Goal 修改，按授权本地提交，停止进入下一片。

## 5. 验证命令与完成合同

当前证据进度（2026-09-06，随实施更新）：

| 要求/关口 | 当前状态 | 责任与下一证据 |
|---|---|---|
| G0 / N2 | 用户已采纳推荐方案，真实下游验证通过 | DB-024 §3.1；真实 SG emit/执行及单 PackageReference 消费者，无 friend 或手工 analyzer 接线 |
| R2/R3/R4/N1 / G1 | 已实现，聚焦测试通过 | `src/DurableGraph/Capture*.cs`；`ReferenceCaptureSessionTests.cs` 的 23 cases：身份/隔离/烧号/退役/耗尽/WeakReference 释放 |
| R1/R5 / G2 | 已实现，聚焦测试通过 | `DurableSchemaGenerator.BinaryBody.cs`；`ReferenceCaptureGeneratorTests.cs` 及旧 String 拒绝转正，真实生成/ID golden/publisher/history |
| R6 / G3 | 根构建、全套测试、真实包及独立审查通过 | 根 build 0 warnings/errors，tests 503/503，ReferenceCapture 聚焦 37/37；PackageConsumerProbe passed；工作集/DB-024/package/实验簿已同步 |

最终执行证据（2026-09-06）：

- `dotnet build DurableGraph.slnx --verbosity quiet`：0 warnings/errors。
- 聚焦项目测试 `--no-build --filter FullyQualifiedName~ReferenceCapture`：37/37，零跳过。
- `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：503/503，零跳过；
  DurableGraph 296、Serialization 94、Storage 73、StateStore 40。
- `./experiments/PackageConsumerProbe/Run-Probe.ps1`：通过，产物目录
  `experiments/PackageConsumerProbe/obj/run-20260906024101-5228`；新增三个 Schema 与原标量三个一起发布，
  消费者精确输出 `BinaryBody:012154:True:ReferenceCapture:True`，先执行断言再输出成功标记。
- 主代理检查实际 diff；独立只读审查覆盖 Runtime/SG/测试/包入口，未发现剩余阻塞问题。
  Runtime 自审发现并复现的 `Array.AsReadOnly` → `ICollection.SyncRoot` 数组写入旁路已改为私有读取接口包装，
  有两种集合均不可取得该旁路的回归测试。`git diff --check` 通过。

错误路径证据：注册/Seal 失败、非法重入、错误 phase、跨 session/重复/过期候选、uint 最后值与耗尽、
保留旧候选时退役领域实例/回调闭包释放，均在 Runtime 测试执行；历史拒绝精确检查坏 Leaf 不生成 body，
合法 Base 可保留 body，并由 publisher Publish/Verify 双端拒绝同版 String→UInt32 Schema 变动。
Schema/history 原 String tag、legacy boxed 和 13 标量原字节的回归包含在上述全套测试与包验证中。

下列路径已在当前树核对；当前基线执行结果见 §2，最终实施验收仍须重新运行：

```powershell
dotnet build DurableGraph.slnx --verbosity quiet
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj --verbosity quiet
dotnet test DurableGraph.slnx --no-build --verbosity quiet
./experiments/PackageConsumerProbe/Run-Probe.ps1
git diff --check
```

实施时给新增测试取统一的 ReferenceCapture 前缀，可用同测试项目的
`--filter FullyQualifiedName~ReferenceCapture` 聚焦；未匹配任何测试不能当作通过。
build/会写同一 bin 的测试/pack 串行，避免已观察过的 Windows DLL 锁冲突。
最终要求根 build 零 warning/error，所有相关测试与包消费者通过，独立审查无未解决阻塞问题；
R1–R6/N1–N2 都有 G0–G3 结果或明确裁决，不以“文件写完/测试数量增加”代替行为证据。

工作树闭合仅处理本 Goal 引入的改动；保留启动时的脏文件，不为得到 clean status 而 stash/reset/clean/覆盖。
只可修改上述产品接缝、对应测试、包消费者和关联文档；不修改上游 atelia 或旧 StateStore Probes。
不 push、发布 NuGet、迁移数据或创建外部资源；需要这些动作意味着偏离本片。

停止边界：引用身份被精确捕获成封闭 ID DTO 列表，内存候选生命周期可验证。
不实现字符串对象内容解码/独立空串分配、领域 Restore、循环/多态字段、struct/数组/BCL、
ID 回收/bitmap/generation/compaction、DTO 升级/比较/估算策略接入、StateRevision/ObjectVersion 写入或持久 Save。
以上延期与“ObjectId 将来允许回收复用”不冲突。

遇到需扩大依赖方向、public/持久格式、恢复语义或并发范围的选择，记录具体反例并交用户裁决。
正常实现困难不算完成或真正 blocker；后续 Goal 状态遵循当时环境的规则。
