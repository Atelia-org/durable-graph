# DB-096：公开历史分页实施与验收

2026-10-19。本片接通 [API 草图 §5](api-sketch.md#5-历史方向和成本边界) 已接受的准确起点分页；不新增 State 筛选、持久 cursor、历史总数或另一套历史权威，不重写已有完整历史读取。当前源码与新 G 的受影响普通包验收均已完成；不将本片资格外推到其他旧 G/S 或未覆盖场景。

## 最小调用面

```csharp
CommitHistoryPage page = repo.ReadHistoryPage(
    HistoryStart.AtOrBefore(commitId),
    new HistoryPageLimits(MaxItems: 50, MaxWorkSteps: 10_000),
    cancellationToken);
// 展示或筛选 page.Items；后续始终使用 page.Continuation，不从展示项推导。
```

四个小类型分别表示起点、两个独立限制、物化页和正常停止原因：

- `HistoryStart`：get-only `CommitId` / `IsInclusive`，通过 `AtOrBefore` / `Before` 构建；default 在执行时拒绝。
- `HistoryPageLimits(int MaxItems, long MaxWorkSteps)`：两者必须为正。不预分配 MaxItems 大小的数组。
- `CommitHistoryPage`：只读 `Items`、nullable `Continuation`、`StopReason`，描述的摘要拥有独立存储，可在仓库关闭后保留。
- `HistoryPageStopReason`：`ReachedRoot` / `PageFull`。预算、取消、损坏和清理错误抛出，不交付已收集前缀。

保留一个显式 limits 值，避免多个裸数字在调用处混淆。它只约束条数与 HeadStore 历史 work steps，不承诺总 I/O、字节、耗时或峰值内存上限；DG 图 metadata 验证和摘要复制不由底层步数完整计量。签名不带 predicate/Kind：首版返回全部 State/Event 描述，应用筛选，避免扫描进度与展示结果混为一谈。

## 接线和验收边界

一次公开操作 guard 覆盖准确起点扫描、DG Describe、HS 清理、资源/取消复核与页交付。资源层直接一次 `ReadEntryHistory`，不先 ReadDescriptor/CheckCommit 再重复定位，不取最新 current、不先读整链再 Take。DG 每项仍核正式业务成员、payload 与图 envelope 的 State/Event 角色；不恢复业务对象，也不把描述查询升级为完整图/schema/body 审计。

Before 的 anchor 仍先验证再略过；底层 Entry allowance 为 `(long)MaxItems + (IsInclusive ? 0 : 1)`，重读消耗工作预算。页满即正常停止，Continuation=`Before(lastProcessedCommitId)`；即使最后一项刚好是根也不额外预读，下次可得到空 ReachedRoot。短页抵达根则无 Continuation。相同预算重试失败不保证前进。

最小验收包括：固定旧点不随新提交漂移、同值真实提交不合并、跨 fork/空 fork 无 clone、Before 排除与根后空、满页恰根、筛选后无 State 仍可续页、空业务注册冷开、结果隔离与只读不写；非法/未登记/管理及坏角色位置（包括 Before anchor）、坏中间/继承项拒绝；预算前/中途失败、取消/owner/lifetime/重入/清理边界。源码与普通包均需实际执行，不以底层 Probe 取代 DG 产品资格。

## 源码验收

主线程根 `dotnet build DurableGraph.slnx -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -m:1 -nr:false` 最终0警告/0错误；DG输出Release，上游source项目输出Debug。同一构建后完整 Persistence **1087/1087、0跳过**，相关 Runtime 三类（RepositoryPublicApiTests、LoadedWorldGeneratorTests、PreparedRevisionGeneratorTests）**16/16、0跳过**。其中新增分页29例：

- [健康及参数14例](../../../tests/DurableGraph.Persistence.Tests/CommitHistoryPageTests.cs)：固定anchor、同值不同ID、真正多层fork及空fork、父后续隔离、满页/短页/空末页、全Event筛选、真实联合提交各自链、int.MaxValue无预分配/溢出、复制隔离及冷开无业务回调。
- [故障15例](../../../tests/DurableGraph.Persistence.Tests/CommitHistoryPageFailureTests.cs)：准确预算错误码、完整首描述后失败但无页、Before重读收费、预/中途/最终cleanup取消、重入与寿命、真实临时文件关闭后的错误、HS仍健康但DG单独fault、管理/未登记/不准确anchor、Before坏payload或Meta角色、坏中间项与未登记祖先。

确定性故障仅使用 internal `HistoryPageItemObserver`；清理反例在它内部包装实际HS临时文件，先关闭真文件再注入取消/错误/DG故障，不替代正常扫描、不修盘。未枚举所有物理I/O位置或敌对文件改写，未取得性能/跨平台/掉电资格。

初轮根build有两条xUnit写法警告，已消除；补充反例一度引用不存在的HS.IsFaulted，改为调用实际 `EnsureUsable` 验证HS健康。原28例先通过，最终完整套件包含新增清理后DG故障例。证据在 `.artifacts/db096-history-pages/`：最终 `build-3.log`、`persistence.log`、`tests/persistence.trx`、`runtime.log`、`tests/runtime.trx`；`build-1/2.log`、`pages-1.log` 保先前范围。独立审阅已核产品、健康/故障测试与消费者，主线程实际运行验收。

## 普通包与后续

产品来源 `2e4823dbbf8445f80c23413a91f1be0d54fb98d7`；G `0.0.0-public-history-e2e.20261019101005.37220`，S保持 `0.1.3-dev.20261019.g70b406c` / `70b406cd8d1117733eee81a33e7f978399aa98b2`。package-mode restore/Release根Rebuild为0警告/0错误。Split控制打包确认生产输入匹配HEAD且当时工作树干净，未公开发布包。

- Split：五个独立PID的seed/upgrade/rewrite/continue/verify通过，receipt Passed=true。
- Group：三个进程通过，history Publish/Verify构建0警告/0错误。空业务registry冷开后分页固定继承前缀与混合Event/State/Event，逐项满页最后续空页，以准确seed/transfer书签核对来源。预算和故障资格由上述源码测试承担。
- 主线程核Group assets的4G＋6S均为package，十个实际输出DLL的SHA256与准确nupkg全部一致。

本机 `.artifacts/db096-history-pages/package-paths.json` 指向 `W:/TEMP/db096-pages-435e9c33de8c4808be4a9cb2fb16d4b4/split/result.json` 和feed/provenance；`group-package-verification.json`保Group路径与哈希。根日志为 `package-{restore,build,split,group}.log`，Split完整打包/运行日志在外部工件目录。本片新G只资格化Split/Group，最终交付仍须对最终版本验收完整活动集合。

下一片为[创建登记故障专项](creation-failure-acceptance.md)，随后最终消费者/文档清理和合入；完整队列只维护在 [PROJECT-STATE](PROJECT-STATE.md)。
