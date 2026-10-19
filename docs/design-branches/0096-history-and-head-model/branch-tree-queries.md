# DB-096：根与子树目录查询验收

2026-10-19。问题：能否在已登记 Branch 森林上直接提供根列表与包含起点的子树，保持准确来源、空 fork 可见和无业务对象物化？本片不增加持久索引或第二份父关系，不修改持久格式或上游依赖。

## 实现与边界

[Repository](../../../src/DurableGraph.Persistence/Repository.cs) 的 `ListBranches`、`ListRootBranches`、`ListBranchSubtree` 共用私有森林读取，各入口独立持有一次操作 guard。先读取正式成员并验证当前/来源 Entry 元数据及完整来源森林，再筛选；内部管理和未登记物理 Head 不参与。父节点来自固定来源 Commit 的实际所属 Branch，从 B 的继承历史选 A/a1 创建 C，C 仍挂 A。

子树包含起点与尚无本地提交的 fork；标签允许重复，结果不承诺排序。无效起点沿用登记查询的 `KeyNotFoundException`；登记森林任何缺失父、损坏来源或环都会失败，不能变成空树或新根。空库根列表为空。返回数组可由调用者修改，不影响后续结果。

查询不需要注册业务模型，不执行业务 Capture/Allocate/Hydrate，但仍验证元数据。子树当前会读取整个登记森林；临时邻接表与迭代遍历是线性内存/工作量，不承诺整体磁盘 I/O 线性或只访问所选子树，也没有实测规模性能保证。

## 源码验收

主线程根 `dotnet build DurableGraph.slnx -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -m:1 -nr:false` 通过，0警告/0错误。该 solution 构建的 DG 输出为 Release，上游项目输出为 Debug；不将其描述为全依赖 Release 验收。

同一构建后 `--no-build --no-restore`：

- 完整 Persistence：**1058/1058，0跳过**。
- Runtime 的 RepositoryPublicApiTests、LoadedWorldGeneratorTests、PreparedRevisionGeneratorTests：**16/16，0跳过**。
- [新增树查询测试](../../../tests/DurableGraph.Persistence.Tests/BranchTreeQueryTests.cs) 9例覆盖多根/重名、父子继续、继承旧点真实归属、空 fork、冷开空模型注册、无业务回调、数组隔离、只读字节不变、非法起点、24层真实父链、寿命和重入。24层仅为结构见证，不是深链极限。
- [来源反例](../../../tests/DurableGraph.Persistence.Tests/BranchOriginQueryTests.cs) 同步覆盖新入口：未登记坏 Head 隔离，登记目标缺失/损坏、未登记父和真环拒绝；坏镜像仅在全部 owner 关闭后构造。

本机证据：`.artifacts/db096-branch-tree/build.log` 与 `tests/{persistence,runtime}.trx`。独立审阅检查产品 guard、完整森林校验及测试语义，主线程核对源码并运行验收。

## 普通包验收

产品来源 `39200fad988fd79e9d8151ba9c40acac75eb788c`，G `0.0.0-public-history-e2e.20261019095140.25524`；S 保持 `0.1.3-dev.20261019.g70b406c` / `70b406cd8d1117733eee81a33e7f978399aa98b2`。独立 package-mode restore/Release 根 Rebuild 通过，0警告/0错误。Split 控制打包的 provenance 证明生产输入匹配 HEAD、打包时工作树干净；未公开发布包。

- Split：seed/upgrade/rewrite/continue/verify 五个独立 PID 均成功，receipt Passed=true，保原公开恢复/升级/冷续写门禁。
- GroupCommit：seed/continue/verify 三个进程成功，Publish/Verify 两次构建0警告/0错误。新增见证为创建后立即列树、父继续后空 fork 来源固定，以及独立进程先用空模型注册冷读目录。产品测试另证无业务回调，消费者不独立证明内部绝无实例化。
- 主线程核实 Group assets 的4G＋6S均为 package，并逐一将十个输出 DLL 的 SHA256 与准确 nupkg 比对，全部一致。

本机 `.artifacts/db096-branch-tree/package-paths.json` 指向 `W:/TEMP/db096-tree-427167664f3d4ac5aa8ef6b0e525f1db/split/result.json` 及其 feed/provenance；`group-package-verification.json` 记录 Group 工件和逐包核对。其余日志为 `package-{restore,build,split,group}.log`；Split 的完整打包/运行日志在其外部工件目录。本片仅对新 G 资格化这两个门禁，其他历史资格保原版本意义，最终交付仍须对最终版本验收完整活动集合。

## 后续

本片源码与受影响普通包验收已闭合，后继[公开分页](history-pagination.md)和[创建故障源码资格](creation-failure-acceptance.md)亦已完成；最终版本完整门禁和正式合入仍待完成。完整目标和顺序仅维护在 [PROJECT-STATE](PROJECT-STATE.md)。
