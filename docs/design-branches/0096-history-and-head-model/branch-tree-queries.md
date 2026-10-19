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

待本片新 G 的 Split 和 GroupCommit 普通包验收。GroupCommit 消费者已增加创建后立即列树、父继续后空 fork 来源固定，以及独立进程空模型注册的冷目录读取；当前源码通过不代替该包资格。S 保持 `0.1.3-dev.20261019.g70b406c` / `70b406cd8d1117733eee81a33e7f978399aa98b2`，不重新打上游包。

## 后续

本片闭合后推进公开历史分页；登记故障剩余窗口、最终消费者/文档清理和正式合入仍待完成。完整目标和顺序仅维护在 [PROJECT-STATE](PROJECT-STATE.md)。
