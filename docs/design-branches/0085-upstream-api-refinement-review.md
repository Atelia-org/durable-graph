# DB-085：上游四项 API 增量的下游复核

> 日期：2026-10-11。结论：**四项可接收，没有需要再次修改实现的阻断项；一条非阻断 XML 文档建议。**
> 本轮只读检查上游并运行验证；不包含原反馈第 3 项控制数据确认边界，不实施 DG 迁移。
> 原始调研与其历史源码身份见[可行性报告](0085-rbf3-framestore-versionstore-migration-feasibility.md)。
> 后续：XML建议已于上游`974fd47`实施并提交；第3项与strict/旧存档的当前裁决由[DB-086](0086-new-storage-refactoring-plan.md)取代，本记录保留当轮只读复核范围。

## 1. 本轮来源与验收问题

当前上游改用已经 pull 的 WSL checkout `/repos/Atelia-org/atelia-storage`，开始与结束检查均为
`daf7e1c043554b47b7e4ec7564359234192c4aa1`，工作树干净。
对照起点是前轮指定的 `e989ba60f8866c60bd86f99388701dfbe9c6125f`。
两个实现 commit 为 `ac97ad3`（精确读取、用户长度/切片、组合 guard）和 `daf7e1c`（恢复报告）。
不再使用早先停在旧 revision 的 Linux checkout 判断；也不把旧六包候选当本轮来源。

问题：已接纳的四项改进是否满足 DG 的实际使用需要，是否引入新的生命周期、读取资格或诊断缺口？
最小标准：源码/API 与承诺一致；旧 revision 冷重读、必要 CRC/Fence/CU 检查、恢复后失败报告、
borrowed owner guard、Length/切片寿命的可执行向量通过；未实现的额外需求不混入本批反馈。

上游[实施与验收](../../../atelia-storage/docs/FrameStore-VersionStore/14-downstream-api-refinement-acceptance.md)
用于定位合同和向量，本轮结论另据实际源码与本轮重跑，不直接复用其测试数字。

## 2. 四项检查结果

| 原反馈 | 实现与复核重点 | 本轮判断 |
|---|---|---|
| 精确 revision 读取 | [ReadRevision](../../../atelia-storage/src/VersionStore/VersionStore.Revision.cs) 返回原 revision/时间及 owned root bytes，支持同库冷重开；复用 fork 的 exact 内核，保留完成范围、CRC、尾 Fence、CU peer 资格；不扫描本地后缀定位、不回退旧点；default/foreign 在目标 I/O 前拒绝 | 可接收；没有要求补新的 revision 构造或跨进程导入接口 |
| 恢复可观察性 | [共同 ref/tag 打开](../../../atelia-storage/src/VersionStore/Internal/Storage/DirectoryVersionStoreFiles.cs) 与 [台账](../../../atelia-storage/src/VersionStore/Internal/Runtime/VersionStoreRecoveryLog.cs) 接通 current/exact/history/ListForks/CU peer/tag/ScanTags；工厂前准备容量，成功后立即记录非 None 动作，早于后续验证；上层失败、fault/Dispose 不抹动作；RO 不修尾 | 可接收；物理动作与发布 Outcome 仍独立，没有遗漏需要另加 API 的已要求通道 |
| 可用性检查 | 公共 [EnsureUsable](../../../atelia-storage/src/VersionStore/VersionStore.cs) 按 VS Dispose→VS fault→实际 data guard 检查；无 I/O/确认，允许活跃 Builder/visitor；纯 guard 失败不额外 fault VS，Dispose 独立清理 | 可接收；下游缓存命中前可以使用组合 guard |
| 用户长度和切片 | [Length](../../../atelia-storage/src/FrameStore/FramePayloadWriter.cs) 从 0 计用户 Reserved+Advanced bytes（含 meta），共享首次 writer 交付前的原点；[Payload/TailMeta](../../../atelia-storage/src/FrameStore/FrameRead.cs) 零复制，空切片也有释放检查；旧租约、副本、fault/Dispose 不绕过检查 | 可接收；原绝对 HeadLen 扣减用法须在下游移除 |

关键测试已覆盖原时间/owned bytes、无关坏后缀、必要坏目标/peer、未成立 CU 不回退、
history/tag visitor 内精确读取、可纠正 End 失败、跨副本长度、空/纯 meta/混合切片和租约复用。
恢复报告还覆盖修尾后 header/CU/tag 初始化保护拒绝、visitor/取消/清理失败，及报告视图跨两层关闭的寿命。
公开 source consumer 的新进程图写读包含在 VS 本轮完整项目回归中。

## 3. 唯一建议反馈：补充精确读取的 XML 说明

优先级：低，非阻断；不需要修改实现或新增存储 API。

[ReadRevision 的 XML remarks](../../../atelia-storage/src/VersionStore/VersionStore.Revision.cs) 说明它不发布、
不改选节点，但没有直接提醒使用者可写 owner 的精确读取仍可能触发 lazy 修尾。
完整 README 与恢复报告合同已经说明，因此不是行为缺陷；建议在这个新 API 的 IDE 提示中也加一句：

> 可写 owner 的惰性打开仍可能执行物理尾恢复；成功恢复动作记入 RecoveryReports，与业务发布结果独立。

本轮 `RecoveryReportsPublic.RefRecoveryReportsTests` 的 exact 通道实际验证了这一行为。
无需因这个说明项暂停后续 DG 机制实验，也不将其扩大为本轮排除的控制数据确认议题。

## 4. DG 接入时应遵守的用法

- `EnsureUsable()` 是显式组合检查。内部 `ReadRef`/`ReadRevision` 等纯发布目录查询仍检查 VS 自身，
  没有自动改为探测 data；DG 在依赖 backing data 的流程和缓存命中前主动检查，保持借用寿命。
- `RecoveryReports` 是只追加 live view。串行操作前后保存 Count，复制 `[before, after)` 供日志使用；
  导出时同时保存 VersionStoreId。需要冻结集合/异步使用时由调用方复制。
- 台账只记录成功 RBF open 返回的非 None 动作。工厂失败可能已改 bytes 却没有成功报告，
  缺报告不能判定无变化；NotAttempted 也可以伴随物理修尾。这是已明确的范围，不再追加完整失败取证要求。
- 新 `Length` 含用户 payload 与 meta，不含内部 framing；原扣 HeadLen 的绝对值用法须移除。
  已取得的读取 span 不得越过 FrameRead.Dispose；owned bytes 可以越过 backing owner 的关闭。

## 5. 本轮实际验证

环境：WSL/Linux 本地 checkout，.NET SDK `10.0.201`，构建/测试串行。

```bash
dotnet build Atelia.Storage.slnx -c Release -m:1 -nr:false --nologo
dotnet test tests/FrameStore.Tests/FrameStore.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false --nologo --logger trx --results-directory /tmp/dg-storage-api-review-daf7e1c/FrameStore
dotnet test tests/VersionStore.Tests/VersionStore.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false --nologo --logger trx --results-directory /tmp/dg-storage-api-review-daf7e1c/VersionStore
```

| 检查 | 实际结果 |
|---|---|
| Release solution build | 0 error；41 项未改 Data/Rbf 中既有 XML warning |
| FrameStore | 440 passed，0 failed，1 skipped |
| VersionStore | 1420 passed，0 failed，1 skipped |
| 两项目合计 | **1860 passed，0 failed，2 skipped**；逐项 TRX 汇总 |
| 新增命名向量 | **93 项全部通过**，包含在上面的总数，不重复加计 |
| 当前 restore assets | FS/VS 的 Atelia 依赖均为 main project；K4os 1.3.8；无旧 Journal/SegmentStore 闭包 |

两项 skip 均为 WindowsCallerRootAndParentSpellingIsNotPersistentCanonicalName；没有宣称本轮 Windows 执行。
93 项由 PayloadFacadeTests、PublicRevisionReadTests、ConditionalRevisionReadTests、VersionStoreUsabilityTests、
恢复报告公开/故障测试及 OwnedReadSlices_OutliveFaultedOwnerUntilTheirOwnDispose 的 TRX outcome 汇总。
原始 build/test 日志、TRX 和 summary.json 保留在 `/tmp/dg-storage-api-review-daf7e1c/`，可能后续清理。

未重跑其他七个测试项目、PackageReference smoke、包发布或完整 DG 新栈候选。
这批新增 API 尚未进入旧六包候选，未来包消费必须选含本批内容的新来源；当前项目引用实验可以使用本次源码。
第 3 项控制数据确认、strict writable、ref 缓存、命名/CAS/业务父链及旧存档策略都没有在本轮重新裁决。
