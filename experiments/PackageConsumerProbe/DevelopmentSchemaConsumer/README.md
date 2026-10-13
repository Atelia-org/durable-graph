# Development Schema：V2 草稿与最终 accepted history

这是一个有限普通 PackageReference 实验。问题是：保留固定 shared V1 与 accepted V1 history 时，两个不同 V2 草稿能否各自从同一旧 State 恢复、正常 Upgrade 和私有保存，最后只接受定稿 V2B？
2026-10-13 已由主线以 DB-088 删除特例后的新 G 实际运行并独立复核通过；旧包资格保留在历史段。

模型只有一个 Widget 族：V1 的 Value 为 int，V2A 为 long，V2B 为 double；两份 V2 的 key/version 相同但真实布局不同。两个 Widget 成环并共享 128 项 List<int>。草稿 A/B 都使用 V1 的 baseline tag：ResolveTag → ref-only CreateBranch → CheckoutPrivate → 修改已恢复的 State → CommitState。没有 CreatePrivateBranch 或新 initialState，也不读取另一草稿作为业务来源。

最终分支也从 baseline 恢复，用 B 代码执行 CommitStateToShared，然后关闭全部资源，以新的 Shared owner 冷续写 List。公开 State/root ID、chronological ReadFrames 的准确前驱、领域值、环和 alias 共同观察继承；精确对象头、完整对象 IDs、record kind/prior/H 不属于本包观察范围，由对应源码测试另证。

| 编译输出 | 模型 | history 输入 | 实际模式 |
|---|---|---|---|
| stage-v1 | V1 | development-history | Publish、Verify |
| stage-a | V2A | 只含 V1 的 development-history | Off |
| stage-b | V2B | 同一份只含 V1 的 development-history | Off |
| stage-final-b | V2B | release-history，先复制原 V1 完整文件 | Publish、Verify |
| stage-rejected-a | V2A | 已含 V1＋最终 V2B 的 release-history | Off，必须 DG0015 拒绝 |

共有六次成功 Rebuild 和一次预期失败 Rebuild；不是更改 history 文件来接受异形。Off 只跳过包内 Build CLI 的 Publish/Verify，仍由包内 targets 向 Source Generator 提供 accepted history。两个 Off 编译及其实际 V1→V2 Upgrade 必须保持唯一 V1 文件原 SHA。release 最终恰好为 V1＋V2B，V1 原 SHA 不变；此后 A 即使 Off 也不能绕开 accepted V2 的异形检查。

runtime 有五个全新 PID：seed、draft-a、draft-b、final、verify。草稿保存后的 cold 读取和 final 推广后的 cold 续写均是该 PID 内全部关闭资源后新开 owner；verify 另为独立 readonly PID。A/B 保存期间以全部 Repository 资源关闭后的公开 FS/VS 读取，核 ref2 revision/payload 精确保持 V1；每阶段同时记录真实 ref1/ref2 的 revision/payload。最终才推进 shared，冷续写不再改 shared。各阶段保全部已完成数据帧的原地址、tag、payload 与 TailMeta SHA。最后 readonly PID 及外部 controller 都比较数据库完整文件集合和 SHA。

## 运行

runner 仅消费现存准确完整十包 feed，不 pack、不构建存储源码。下例使用已经控制打包的候选 G；G 与存储 S 独立。必须指定全新仓外 WorkDirectory，旧 fixture/history/receipt 永不覆盖。

~~~powershell
pwsh -NoProfile -File experiments/PackageConsumerProbe/Run-DevelopmentSchemaProbe.ps1 -PackageSource W:/TEMP/dg-db088-round2-20261013-141026-16448/feed -Version 0.0.0-db088-e2e.20261013141051.30576 -WorkDirectory W:/TEMP/dg-development-schema-<runId>
~~~

默认 provenance 为 feed 下对应 G 的 dg-source-provenance sidecar，也可传 PackProvenancePath。runner 使用隔离 NuGet.Config/cache、Directory.Build 边界和独立 stage，核实际 assets 无 ProjectReference、十个实际加载 DLL、包内 SG/Build/props/targets SHA 与真实 Rebuild 日志。result.json、witness.json、逐阶段 evidence/stdout/stderr 与 build logs 在成功或失败时保留。

本故事将两个 V2 草稿限定为各自从固定 V1 baseline 开始。若后一个模型继承前一个 V2 草稿的数据，而非重新从 V1 开始，应用应递增版本；本实验不实现任意草稿间迁移、不承诺一套 Open 同时容纳同键异形模型，也不授进程 Kill、断电、H 或性能资格。新 G 已删除私有新根 API，wire 不改；历史验收资产保持原含义。

当前产品合同见 [DB-087](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#private-plan)，已有独立包资产见 [SplitRepositoryConsumer](../SplitRepositoryConsumer/README.md) 和 [PrivateContainerConsumer](../PrivateContainerConsumer/README.md)。

## DB-088 第二轮：删除特例后的新 G 验收

G `0.0.0-db088-e2e.20261013141051.30576`，生产提交 `ca5598b5b37b0b6c4f2bb50ac6968f1b12988043`；Storage S `0.1.3-dev.20261011.g974fd47`，来源 `974fd47abb73792f9d350af87ab91a53848952eb`。受控 package-mode 新 pack，189 项生产输入与 HEAD 相同、包完整字节及实际加载 DLL／SG／Build 输入均核验；无远程发布。

六次成功 Rebuild、一次预期 DG0015，五 PID 全部 exit0；development history 始终只有 V1，release 只有 V1＋V2B且原 V1 字节不变。实际 refs、cold fresh owner、旧完成帧与独立 readonly 的全部文件集合／SHA 检查通过。完整高位 ID／source／prior／H 仍由[源码故事](../../../tests/DurableGraph.Tests/DevelopmentSchemaWorkflowGeneratorTests.cs)另证，本轮相关生成纵向四项也重新运行通过。

新工件 `W:/TEMP/dg-db088-round2-20261013-141026-16448/development`；`result.json` SHA256 `6C2AA43375D4AEB9AAD6C9D93AEB1FEFABB9F195A2101E5711AB8771797F49BC`。工件根另有不可变 feed／source sidecar与 `independent-acceptance.json`。新 G 三故事及构建／回归共同停点见 [DB-088 §7](../../../docs/design-branches/0088-development-schema-workflow.md#7-实施结果与限定验收)。

## 历史资格：2026-10-13 删除前实验

| 层次 | 实际结果 |
|---|---|
| 包来源 | 复用 G `0.0.0-private-container-e2e.20261013055132.27864`、Storage S `0.1.3-dev.20261011.g974fd47`；没有新 pack／远程发布。G 的生产来源仍为 `d92a3b15e18d0d7309584c0933ef6f6ca8bd8c2c`，主线核当前生产源码／build inputs 未变，不称本轮 HEAD 的新包 |
| 编译与字节 | 十包 feed→fresh cache ZIP／DLL→四个成功 stage→五 PID 的 50 个实际 loaded DLL；四份包内 SG／Build／props／targets 与六个消费输入的 SHA 核对，无 ProjectReference |
| 真 MSBuild | 六次成功 Rebuild，均零 warning／error；A 对已含 B2 的 release history Off 编译另获预期 DG0015。两个草稿 Off 均实际读取 V1 AdditionalFiles 且不执行 Publish／Verify CLI |
| history | development 始终只有 V1；release 恰好 V1＋定稿 V2B，原 V1 SHA 保持；不是修改已接受 history 来绕异形 |
| 五 PID | seed 35344／draft-a 7884／draft-b 24496／final 3748／verify 12548；各 exit0。草稿 cold 读与最终 cold 写为各 phase 内 fresh owner，不称另一 writer PID |
| 实际 refs | 草稿期间 shared ticket `1811939348` 与 payload 保持；final 为 `3221225492`，同形冷续写及 readonly 保持该 revision／payload。main／tag 和两草稿 heads 保持 |
| 只读与帧 | 消费者／controller 执行 readonly 全文件集合／SHA 比较；每阶段检验既有完成帧地址／tag／payload／TailMeta。原 before/after 文件向量未另存，不称独立 raw-frame 全审计 |

成功工件在 `W:/TEMP/dg-development-schema-20261013T113533135Z-460cdbdf`。忽略目录 `.artifacts/db088-development-schema-20261013/` 保存主线 `accept-package.py`／`independent-acceptance.json`、源码 build／TRX、成功与失败工件指针。
result SHA256 为 `54A56799501C82FE77855BFC93C8437666E95A5D93A9477F95EDE8D32AACF3EB`，witness 为 `9848D5EB355B4AF5F41B9726CA168F604FBFC57D8102C25634C70B80FC41D399`。

施工中的 helper 名称冲突、State 被误传 Event-only 查询和 pin XML 取值错误均已修正并换全新目录；失败工件原地保留，不作产品失败／恢复资格。完整 private live ID、真实 records／prior／H、Event／Discard rewrite 与 winner catalog 两版本计数，另由 [源码故事](../../../tests/DurableGraph.Tests/DevelopmentSchemaWorkflowGeneratorTests.cs)授资格。
