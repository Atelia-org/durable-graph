# DB-081：热 State 提交材料进入恢复接缝

> 状态：**前置资格实验形成否定结论，热提交准入未实施**。2026-09-28；按 §6 暂停接线，证据、备选与重启条件见[实验记录](0081-hot-commit-restoration-material-experiment.md)。下文保留重启时须满足的合同，不表示已交付。
> 产品合同以 [DB-083](0083-repository-checkpoint-api-user-stories.md) 为准；Fork/Checkout 有最近 State 才恢复该图，不交付 PendingEvent。
> 顺序：第 5 片；依赖 [DB-080](0080-prepared-checkpoint-reuse-slice.md) 的材料/证书与驻留接缝，下一片 [DB-082](0082-prepared-immutable-leaf-reuse-slice.md)。
> DB-080 的实际证书链、单槽边界与测量限制见[实施记录](0080-prepared-checkpoint-reuse-implementation.md)；本片仍须单独证明新 State 的冷恢复等价，不能直接复用旧历史证书。
> 本片只尝试复用成功 State 提交的 owned DTO，不覆盖 Event 恢复材料或未提交 live graph fork。总体语义见 [DB-076](0076-efficient-graph-fork-technical-path.md)。
> 术语：采用[术语表](../DurableGraph-glossary.md#restoration-preparation)；恢复准备（Prepare）与保存侧内容准备区分。非泛型 `BranchCheckout` / `Checkout` 已由 [078-A](0078-a-repository-free-history-implementation.md) 交付；每分支占用与 named Fork 见 [078-C 记录](0078-c-branch-checkout-fork-implementation.md)。本片内部优化仍未实施。

## 1. 本片的问题与完成标准

刚提交 State 后立刻分叉，是树形探索的重要路径。DB-080 首次仍从磁盘执行恢复准备；本片利用 State 提交已经生成的 owned DTO，
**具备完整恢复证明的成功 State 提交可直接进入同一个 State 准备材料槽，随后 fork 无须读回 typed body 或 Normalize，
值、图身份与后续保存语义和冷重开一致。** 无法证明资格的候选不入槽，继续使用正常冷准备。
不复制源分支工作副本的 CLR 图，不另建热缓存，不改变 State/Schema/Journal 格式或发布顺序。

Event 提交只推进历史 Head，保留工作副本 State 保存基线，不修改该槽、不构造 Event 恢复准备材料。
Event-first 时基线仍为空；前缀 Event 均不入槽，Checkout/Fork 的无 State 路径也不访问或清除另一分支的槽。
该分支首个 State 成功提交后才首次产生 State 热材料候选：其图 Parent 为 null，Journal Parent 仍为前一 Event；
不得把最后一个 Event 的图、绑定或 DTO 当作 State 基线。
`S0→E1→E2` 后 Fork 若 S0 尚在槽中即可命中；若另一分支已将槽替换，允许冷准备 S0。
这只是命中率差别，不允许用最近缓存的 State 代替请求 Head 的最近 State。

## 2. 利用已有提交产物

现有 [WorldWorkspace](../../src/DurableGraph.Persistence/WorldWorkspace.cs) 对 State 执行
`NormalizedRevision.FromCandidate → WithAddress → Install`，已经维护 exact head/H 与完整候选来源。
本片只在这个 State 路径补齐 DB-079 单图准备材料。
[PreparedWorldSave](../../src/DurableGraph.Persistence/PreparedWorldSave.cs) 对 Event 没有 `_next`，
并禁止调用推进 State 的 PrepareInstall；这一职责分离保持，不再为 Event 缓存扩展它。
Event 不能推进 State 保存基线、清除其 rewrite 义务或 Accept Event 身份表。

使用同一 FromCandidate/WithAddress 与 DB-079 单图准备材料接缝，不为热路径伪造 DecodedRevision。
不得长期持有整个 CapturedGraph/CaptureContext 来当缓存，因为其中可能连带保活原始领域实例与候选身份映射；
只转移 owned DTO、必要 schema/provenance 和选择信息。新 State 属于新 revision，不能把旧保存基线直接改地址冒充新材料。
跨类型根替换同样取本次候选的实际 root binding、RootId 与可达闭包；旧根仍可达或已有子对象升根时保留正常身份，
不为热材料重置实例 ID、不复用旧根模型，也不要求另存一份根类型权威。新材料仅在正常保存安装成功后取得发布资格。

### 2.1 热证明必须对应新 State 的冷恢复

完整材料须证明新 revision 的 source membership/current DTO、实际 root/reachable、每行 exact head/H、Store/SchemaStore 来源与固定模型环境，
以及被省略的冷 reader/Decode/Normalize/结构验证路径的完整依赖。Capture 成功本身不是这个证明。
不能把整次 Commit 的依赖收集结果或旧基线的证书直接当作新 State 恢复证书：

- Capture 与冷 reader 可能走不同标准 Resolve/Validate 路径，只收集 Capture 会漏掉冷读的真实依赖。
- 旧历史 Upgrade 的依赖也不能无条件合并。例：S_old 经 v1→v2→v3 恢复，只有 v2 依赖 X；
  保存 S_new 后记录已是 current。之后 X 冲突时，热恢复 S_new 不应仅因旧证书而失败，
  因为冷恢复 S_new 不再执行该历史 Upgrade；旧 S_old 的再次恢复仍须检查 X。
- 通过标准 binding/reader/Normalize 接缝的真实依赖提供证明，不分析任意 delegate，
  不把历次保存或其他图的所有依赖累积到新 revision。某 binding 路径尚不能在不读回时给出完整证据，
  该候选不入热槽，之后走 DB-080 冷准备。优化缺证据不使原本合法的 Commit 失败；
  已执行的真实验证若报告错误仍传播，不能吞错冒充普通 cache miss。

跨类型替换提供同一原则的另一见证：A 根换为 B 根后，A 独有且已不在新图中的依赖不应污染 B 的恢复证书；
若 B 仍引用 A，则 A 当前布局及实际冷恢复路径的依赖仍须纳入，不能按“换根类型”一律删除。
即使两次 State 复用部分或全部 object head，新 revision 的完整来源、根选择和依赖证明仍单独建立。

没有执行的 Hydrate 所首次产生的检查仍由真正物化承担，不为收集它们在 Commit 内额外 Allocate/Hydrate；
DB-082 若跳过叶回调，再独立收集、复核相应额外依赖。

## 3. 提交与入槽时序

| 阶段 | 必须完成的工作 |
|---|---|
| Capture/内容准备（Prepare） | 冻结 canonical current DTO；判定新 State 是否具备完整恢复证明，准备 root/reachable 与结构证明，不重新捕获 live 图 |
| State append 得到实际地址 | 补齐每行 exact head/H 与 revision 来源，准备单图准备材料/槽替换所需内存 |
| Journal/ref 发布之前 | 完成所有可失败的材料构造和必要 callback；不把候选暴露给其他操作 |
| 确认 publication，完成既有安装 | State 正常 Install；仅无 callback/无扩容的引用赋值将合格 State 材料挂入单槽；Event 不参与入槽 |
| 任意失败 | 本次候选不入槽；遵守既有 fault/outcome，不以保留 DTO 挽救未确认发布 |
| 证据不足的成功 State 提交 | 不安装本次热材料，旧槽可保留；后续按 exact key 正常命中旧 State 或冷准备新 State |

本片为热来源增加入槽门：**完整已验证的恢复准备材料 + 确认发布及既有安装成功**，不要求先试物化。
DB-080 的 cold 路径仍在完整恢复成功后入槽；不能为照搬那项时序而在 Commit 内额外 Allocate/Hydrate 一遍。
未来物化失败仍传播且不交付半图。必需证书覆盖省去的恢复准备工作；首次 Hydrate 才触发的额外检查仍由该回调承担，
不能为了收集它们而在提交期间预执行 Hydrate。

拟热入槽的材料可以复用 Capture 已做的验证；可补齐的证明在发布前完成，不能补齐则不入热槽。
不能仅因为 `NormalizedRevision` 名字相同，就把缺根、可达顺序或依赖证书的热材料宣称为完整恢复准备结果。
当前归一化恒等合同是冷热等价的必要前提；不重新支持“纯但 current x→x+1”的解释差异。

槽始终只有一份 State 材料，不存请求 Head。Event 不为恢复准备增加回调、不保留旧 State 的准备证书或重建被淘汰的槽。
工作副本继续保留完整保存来源、SourceLayout/RequiresRewrite；从历史升级 State 提交若干 Event 后，
后续 cold Fork 重新恢复持久最近 State 并取得完整历史依赖。工作副本 Head 和新 ref 均保持精确请求位置。
应用稍后改动原 State/Event 字段也不会改变已冻结材料；fork 必须反映请求位置最近 State 的提交值。
缓存命中后仍执行 DB-080 的资源、来源、根类型及 live Schema 复核。

## 4. 分工与施工顺序

G0 写冷热等价、证书充分性/过度收集与故障入槽见证；G1 打通 State 提交材料资格及成功门；G2 验证连续 Event 保持槽及跨分支淘汰后的 cold miss；
G3 接入共同单槽、测量提交与 fork 合计成本并独立复核失败路径。
G1/G2 可由不同 subagent 分析/写测试，但 WorldWorkspace/PreparedWorldSave/Publish 的修改应顺序集成，
避免两套热材料协议。不能以某个 binding 命中就宣称所有模型已具备不读回的恢复证明。

## 5. 验收矩阵

| 场景 | 最小可观察结果 |
|---|---|
| 合格初始 S0、连续 State 成功后立刻 fork | 无 typed 解码/Normalize，值/alias/持久 ID 与冷重开一致；不要求冷热后继新 ID 数值永久一致 |
| E0→E1→首 State，提交前后另有分支槽 | 前缀 Event 不读写槽、不 Accept 身份；首 State 才按正常热资格入槽，Graph Parent=null；热/冷恢复与后续保存一致 |
| A→B 异型根，保留旧根为子对象或旧子对象升根 | 材料使用新实际根选择、完整来源与独立证书；共有实例 ID 不重置；后继无参 State 保存及热/冷 fork 一致 |
| 异型新 State 已删除/仍保留旧根独有依赖 | 仅旧图依赖冲突不导致新图热路径额外失败；仍是新图实际依赖的冲突，热/冷路径均拒绝 |
| 提交后修改原 live State | 从提交检查点 fork 仍见提交值，不把未提交修改带入 |
| S0→E1→E2，随后 fork E1/E2 | 槽仍为 S0，只恢复 State，无 Event 物化；Head/ref 精确为 E1/E2 |
| 历史升级 State 后提交若干 Event | Event 不清掉 State rewrite；fork 首次 State 保存仍 Base/Remove，后继再正常 Delta |
| 历史升级 State 的槽被另一分支替换后提交 Event | Event 不重建槽；fork 正常 cold miss，历史中间依赖重新检查 |
| S_old 升级经 X，再保存 current S_new，之后 X 冲突 | S_new 热/冷恢复均不因仅属于旧 Upgrade 的 X 失败；旧 S_old 恢复仍检查 X |
| 冷 reader 具有 Capture 未触达的标准依赖 | 合格热材料覆盖它；证据不足时不入槽，合法提交保持成功；真实冲突不被吞掉 |
| 新增/删除节点、循环岛、同值不同 ID | source membership 与 cursor 正确，热/冷后续保存可观察语义相同 |
| 只追加未发布、Unknown、Published 后安装失败 | 失败候选不入可用槽；repo fault 后无旧槽继续读写；重开依实际 ref 判断 |
| 新 State 的必需 Schema 迟注册冲突 | 命中拒绝，与 cold 一致，不因热来源漏掉真实恢复依赖 |
| A/B 分支交替提交、旧分支工作副本继续编辑 | 单槽仅按成功且合格的 State 替换；旧保存基线不被修改；兄弟 cursor 独立 |

已有 [WorldWorkspaceStorageBaselineTests](../../tests/DurableGraph.Persistence.Tests/WorldWorkspaceStorageBaselineTests.cs)、
[EventHistoryRepositoryTests](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs)、
[EventHistoryPublicationFailureTests](../../tests/DurableGraph.Persistence.Tests/EventHistoryPublicationFailureTests.cs)
是 head/H、Event 不安装 State、发布失败的事实基础；旧 PendingEvent 断言不作为新交付合同。
新增断言须覆盖热 admission/拒绝资格及冷热依赖等价，不能只重跑原保存测试。

## 6. 成本与停点

测量必须包含 **State Commit + 紧随其后的 N 次 fork**，另测从不 fork 的提交路径和连续 Event、跨分支换槽路径。
新建恢复准备信息的开销可能只是从 fork 搬到 Commit，不能只报告 fork 的局部下降。
比较 DB-080 冷首次恢复准备与本片热材料，覆盖普通/Family、链式低复用与同点扇出，
记录热资格覆盖、额外提交耗时、解码/归一化次数、分配与驻留量。Event 后未命中须计入总成本，不写成保证 Event 热恢复。

按 [共通验收](0076-efficient-graph-fork-technical-path.md#10-分片施工导航) 验证；若无需读回就无法取得足够恢复证据，或 eager 准备总成本不值得，
保留否定结论并暂停默认接线；正常 cold 路径继续成立，不在同片添加公共 pin/API、第二套缓存或凭空放宽 provider 合同。
本片不授予 live 实例共享资格；下一片只从已提交 DTO 的成功物化结果取得叶实例。
