# DB-084：EventJournal 不可变 tag 与 DurableGraph 接入分片

> 状态：**目标合同已选定，未实施；上游实施前仍须核对格式与接口设计**。2026-09-27。
> 用户已选择不可变 tag 支持跨重开定位，并明确持久化实现归属兄弟仓库 `atelia-storage/src/EventJournal`。
> 本文记录 DurableGraph 的消费要求、上游交付前置与接入验收，不授权修改上游代码，不将候选方法名当作现有 API。

## 1. 需求与边界

调用者需要给已保存的检查点一个固定名字。分支继续提交或移动后，这个名字仍定位原检查点；关闭重开后也可重新解析。
这是用户已确认的目标，不再等待是否需要 tag 的产品选择。持久化与重开解析由 EventJournal 拥有，DurableGraph 不另建 tag 文件或第二套权威索引。

[DB-083](0083-repository-checkpoint-api-user-stories.md) 的 `CheckpointAddress` 仍只在签发它的同一次 Repository 打开中有效。
首片不提供可序列化的外部检查点地址；tag 名可以由调用者保留，但必须在指定 Repository 内解析，不是全局仓库身份或通用地址 token。

最小消费形状如下；它们只在本片接入后提供，不是 DB-078 首片的前置 API：

```csharp
repo.CreateTag("before-experiment", selectedAddress);
// 关闭并重新打开同一 Repository。
CheckpointAddress selected = reopened.ResolveTag("before-experiment");
reopened.CreateBranch("experiment", selected); // 仅创建 ref，保持选中位置。
using var work = reopened.Checkout("experiment");
```

CreateTag 接受当前打开实例签发的有效检查点地址。ResolveTag 返回由当前打开实例签发的地址；不同调用不要求分配不同对象，
但不得返回旧打开实例的句柄。tag 操作不物化领域图、不新增业务 Event/State、不修改分支 Head 或工作副本基线。
tag 可定位 State，也可定位首个 State 之前的 Event；解析成功不等于取得业务初始化结果。
这里使用 078-A 已交付的 ref-only 创建与 Checkout；078-C 完成后可用 Fork 合并两步，
其恢复失败不留 ref 的合同仍按 078-C，不能把两步示例误当成同一失败语义。

## 2. 已核实的上游与消费事实

2026-09-27 核对时，[StorageDependency.props](../../eng/StorageDependency.props) 固定包版本 `0.1.1-preview.2`、
源码 revision `976aa345f923da09e2a5cf1dc25ba592b3818b63`；兄弟仓当时 HEAD 恰为同一 revision，工作树干净。
这次一致不意味着以后可以用兄弟 HEAD 解释 DG 固定包行为。上游根有 AGENTS.md，未发现 PROJECT-STATE.md；
实施时应重新核对其指南、README 和当前源码。

- [EventJournal 指南](../../../atelia-storage/src/EventJournal/README.md) 与
  [Refs 实现](../../../atelia-storage/src/EventJournal/EventJournal.Refs.cs) 提供 branch/ref、move 和 archive，当前没有不可变 tag API。
- CreateBranch 使用 Create/Fork、ref object Init、BindName 多步发布；普通 branch 还能 move/archive。
  “创建一个约定不移动的 branch”无法在存储 API 层保证不可变，也引入无需求的 RefId/reflog 生命周期。
- [EventAddress](../../../atelia-storage/src/EventJournal/EventAddresses.cs) 是物理坐标加 Hint，不含仓库身份。
  checked-read 能验证此 journal 中的目标帧，不能证明外部传入的同坐标原本来自哪个仓库。
- [RefOpFrame](../../../atelia-storage/src/EventJournal/RefOpFrame.cs) 和 replay 只识别现有操作；新记录不能未经格式核对直接混入旧协议。
- 上游某些可写打开默认允许尾部恢复；DG 的 [HistoryJournal](../../src/DurableGraph.Persistence/HistoryJournal.cs)
  使用严格 options 关闭修尾，并额外校验所有物理文件及确认耐久性。新增 tag 必须保持这一消费约束。

这些是源码核对结果，不是 tag 功能或故障注入测试已通过的证明。

## 3. 最小可观察合同

| 项目 | 本片合同 |
|---|---|
| 操作 | 创建命名绑定、按名字解析。上游候选为 `CreateTag(name, EventAddress)` / `ResolveTag(name)`；实际结果类型与命名遵循其独立接口设计。 |
| 名称 | tag 使用独立的 ordinal 名称空间；允许 branch 与 tag 同名，靠显式 API 区分。复用现有 branch 名称的字符/长度约束，不引入层级目录或通用 ref-kind 框架。 |
| 重复名 | 同名创建一律拒绝，包括目标相同的情况；不把 Create 隐式改成 upsert 或幂等 ensure。 |
| 目标 | 非空、当前 journal 中 checked-readable 的已存在事件帧。DG 还验证该帧是本 Repository 的合法检查点及地址属于当前打开实例。 |
| 不存在 | Resolve 明确返回不存在；损坏记录、坏目标与未知格式必须报错，不得当作 tag 不存在。 |
| 不可变 | 不提供 move、覆盖、delete、archive 或 tag checkout；不允许同名重新绑定。 |
| 可读性 | 只读打开可以解析；创建在只读、已关闭或故障资源上拒绝。无领域模型物化依赖。 |
| 并发 | 延续单 driver、串行操作；不引入跨进程活刷新或跨实例 CAS。 |

DG 的公开接缝为 `void CreateTag(string name, CheckpointAddress address)` 与
`CheckpointAddress ResolveTag(string name)`。底层 EventAddress 不直接作为公开可持久化的 DG 地址；
拒绝旧打开实例或其他 Repository 的 CheckpointAddress 必须发生在 tag 追加之前。
上游只验证本地坐标时的来源局限仍明确保留，不能将 checked-read 描述成通用的跨仓库身份认证。

tag 名以当前打开的仓库为范围。仓库副本各自解析本地 tag；此片不定义复制后的全局身份、跨仓去重、合并或外部书签序列化协议。
未来只有真实消费者要求离开 Repository 上下文交换地址时，才重新设计这类身份合同。

## 4. 持久权威与失败边界

上游最小机制是一条不可变 `name → EventAddress` 持久记录与可由记录重建的内存索引。
采用扩展现有 ref-op-log 还是独立 tag log、具体 frame codec 和错误类型，由上游实施前的独立设计确定；
必须证明一个创建具有明确的发布点，不能因布局选择引入第二套 tag 权威或给 tag 配置可移动 ref object。
这些是工程方案核对，已定的不可变、名字冲突和失败行为不因此重新待决。

创建至少经过名称/重复/目标校验、记录准备、追加与耐久确认、内存可见结果安装。发布前完成可预见的校验与分配；
新索引安装不得调用领域代码。成功返回意味着绑定已按上游的耐久合同完成，重开仍能解析同一目标。
若 replay 遇到重复名字，即使目标相同也应拒绝损坏记录，不能采用 last-wins 把不可变 tag 变成可覆盖引用。

普通校验失败不追加 tag。进入写入后发生异常，绑定可能已经持久化：调用者停止使用受影响 driver，关闭重开后按名字解析实际结果，
不能自动覆盖、删除或盲目重新 Create。上游须明确如何表达“不曾尝试发布 / 可能发布 / 已确认发布”及故障后的资源可用性；
不假装当前 EventJournal 已提供 DG 的 `GraphCommitOutcome`。DG 接入时将实际证据映射到自己的 publication/fault 合同，未知结果保持 Unknown。
若重开因坏尾而失败，正常打开报告损坏；只有显式离线救援可以修尾，不能为查询创建结果而自动改盘。

现有 DG `HistoryJournal.ConfirmDurable()` 对 events、ref objects、ref-op-log 有明确的确认顺序。
tag 接入必须核对目标 Graph/Schema/Journal 数据的耐久确认先于发布引用它们的 tag：
若采用 ref-op-log，应验证既有末尾 publication barrier 覆盖新记录；若采用独立文件，须显式加入校验与确认顺序，
不得依赖目录枚举顺序。正常读写、确认、只读打开和失败处理均保持现有完整帧 append-only，不启用上游默认修尾。

tag 的耐久性依赖底层实际文件/目录发布合同。新布局若新建文件或目录，必须核对其承诺与验证证据；
不能单凭调用一次 DurableFlush 就额外宣称此前未证明的断电保证。

## 5. 交付顺序与验收

1. **上游设计和实施。** 在 `atelia-storage/src/EventJournal` 所属仓库固定布局、格式演进、接口与故障结果表达；
   同步它的指南和测试，验证严格打开与只读行为。本片不扩大为通用 ref 分类或迁移框架。
2. **上游独立交付。** 按该仓当前规则完成源码与独立 PackageReference 验收，使用新的存储包版本与明确 revision。
   具体提交、打包及发布按当时授权执行；本设计不将它们视为已完成。
3. **DG 接入。** 在 [DB-078-A](0078-editable-checkpoint-fork-slice.md#11-078-a非泛型公共基础与自由历史)
   建立 CheckpointAddress 和公共基础合同后，更新固定依赖并接入上述两个方法、严格打开与确认路径。
   上游工作可独立推进；DG 接入不依赖 DB-080 的准备材料缓存，也不阻塞 DB-078 的 E/S 核心能力。
4. **包模式消费验收。** 使用更新后的固定包验证 create → close → reopen → resolve → read，以及 ref-only CreateBranch + Checkout。
   078-C 具备后再加 named Fork 集成见证，它不是本片依赖 078-A 的交付门。
   兄弟源代码联调不替代固定包验证，也不能静默跟随上游 HEAD。

最小验收矩阵：

| 场景 | 必须观察到的结果 |
|---|---|
| State 与 Event-only 前缀 | 均可命名并重开解析；不要求已经存在 State。 |
| 源分支后续提交/移动 | tag 地址仍固定，读取及 ref-only CreateBranch + Checkout 保留所选位置；078-C 完成后追加 named Fork 见证。 |
| 同名 tag 创建两次 | 同目标、不同目标均在写入前拒绝；同名 branch 不冲突。 |
| 跨打开或跨仓误用 | DG 拒绝旧地址且不追加；重开 Resolve 签发当前打开的有效地址。 |
| 只读及模型无关 | Resolve 不写盘、不物化领域图；Create 拒绝，旧数据字节不变。 |
| 格式与目标错误 | 重复 tag 持久记录、损坏记录、无效目标、未知格式明确失败，不报告普通 NotFound。 |
| 追加前与发布后故障 | 区分确定未发布与可能/已发布；重开得到实际绑定或报告损坏，不自动回滚或修尾。 |
| 耐久确认 | 所有新增元数据纳入严格验证和引用依赖顺序；失败不使旧 driver 继续服务。 |
| 独立包消费 | 使用明确版本/revision 验证跨重开定位，当前打开地址寿命仍受检查。 |

文档成熟度的完成条件是这些可观察合同与接入边界固定；代码实施的完成条件是上游和 DG 各自提供相应实测证据。
