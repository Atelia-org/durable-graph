# DB-029：已准备内容的分类与对象写入规划

本问题保留 owned body→精确 payload 计量→Base／Delta 选择的现行接缝。旧 raw Revision 构造、FileScope 上界与提前预测写入文件的讨论已退出；规划器不拥有 Store、Schema view、目录或发布。

## 当前合同

- `CaptureSession.Prepare` 消费已 Seal 的捕获候选，生成 owned `PreparedBaseBody`／`PreparedDeltaBody`，不重新 Capture 或安装候选。Delta 的 `HasChanges` 决定是否变化；非空的零位图不等于 Update。
- 所有 live 对象的 Base 预先准备，策略决策后复用 bytes。裸 body 不含 typed 类型头或 ObjectVersion envelope；producer 以准确 catalog 编码当前 typed Base，承担 DTO／完整布局对应关系。
- `ObjectWritePlanner.ClassifyCaptured` 要求 `Previous` 与 exact prior 的存在性一致。新对象为 Insert；已有对象要求重写时为 BaseOnlyUpdate；其余由 Delta 的存在性／HasChanges 分类为 Update 或 NoChange。跨布局等必须重写的更新不能借旧 Delta 延续解释。
- `PlanObjectWrites` 消费完整 post-live 行，检查 null／重复 ID，按 ID 排序；Update／NoChange 必须有可信 prior H。BaseOnlyUpdate／Insert 不要求 D/H，也不花可选 Base 预算。
- B/D 通过共用 `ObjectVersionPayloadSize` 计量实际 typed 内容：kind、固定 12B prior、length 与 body；不再采用向后文件距离的保守上界。H 取已验证来源的实际对象链消费字节。表示规则见 [DB-015](../0015-statestore-object-representation-policy/README.md)。
- 输出是 owned `ObjectVersionRecord[]`、估算行及表示计划。稀疏 Writes 不代表完整 live 目录；producer 另外核真实 source、heads/H、完整成员集合与 Removes，共同 saver 决定目录并追加真实帧。
- 准备成功不等于追加或发布成功。追加后依据实际图／记录准备安装材料，确认发布后才接受 State 基线；Event／Discard 纪律见 [DB-069](../0069-incremental-save-baseline/README.md)。

## 当前实现与验证入口

| 接缝 | 当前源码 | 回归入口 |
|---|---|---|
| 冻结候选与内容准备 | [CaptureSession](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs)、[PreparedBaseBody](../../../src/DurableGraph.Serialization/PreparedBaseBody.cs)、[PreparedDeltaBody](../../../src/DurableGraph.Serialization/PreparedDeltaBody.cs) | [捕获准备](../../../tests/DurableGraph.Tests/CapturedGraphPreparationTests.cs)、[真实生成准备](../../../tests/DurableGraph.Tests/GeneratedCapturePreparationTests.cs) |
| 分类、payload 与纯规划 | [PreparedObject](../../../src/DurableGraph.Persistence/PreparedObject.cs)、[ObjectWritePlanner](../../../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs)、[payload 计量](../../../src/DurableGraph.Storage/ObjectVersionPayloadSize.cs) | [规划器回归](../../../tests/DurableGraph.Persistence.Tests/ObjectRevisionPlannerTests.cs)、[payload 与真实编码](../../../tests/DurableGraph.Storage.Tests/ObjectVersionPayloadSizeTests.cs) |
| 真实 source 与 typed producer | [BusinessPersistenceAdapter](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0BusinessWorkspace.cs)、[GraphRecordStore](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphStore.cs) | [实际 B/D/H 与 Event 基线](../../../tests/DurableGraph.Persistence.Tests/BusinessPlanningAccountingTests.cs)、[候选安装归属](../../../tests/DurableGraph.Persistence.Tests/BusinessSaveCandidateInstallationTests.cs) |

这些入口区分纯规划检查与实际来源／发布验证，不能用一份人工 PreparedObject 列表证明持久图来源正确。全量 Base 准备与完整成员校验仍有成本，不承诺总 O(k) 或固定峰值内存。

历史合同可用 `git show 6dbaf0e:docs/design-branches/0029-prepared-object-revision-planning-slice/README.md` 查阅；旧 raw Store／wire／scope 的验收保当时含义，不授当前 FS/VS 路线或性能资格。
