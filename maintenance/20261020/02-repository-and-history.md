# Repository、WorkingCopy 与历史质量体检

检查基线：`1beca7cf2477d369010b3577b7a795c41dbc45f1`；2026-10-20。本文面向后续 Coding Agent，是调查结果，不是实施授权或新的产品合同。

## 结论与证据等级

本包未发现可据当前证据认定的 P0/P1 公共流程正确性缺陷。发现一项有明确调用链依据、仍需成本计数的 P2 性能债，以及一项需要专门复现的 P3 重入防御候选。二者均未修复；特别不能把理论复杂度、既有测试源码或历史验收写成此次运行结果。

检查范围：Repository 的创建/Fork、BranchInfo/CurrentCommit、WorkingCopy 占用和释放、State/Event 基线、多分支请求与提交、历史查询/分页/枚举寿命、Tag 和紧凑 ID。读取了根 AGENTS、完整 src/PROJECT-STATE 和 DB-096/PROJECT-STATE；重点证据来自当前源码与相应测试。底层 codec、确认屏障、读取恢复及 generator 由其他报告深入检查。

只追加、旧点另开、继承历史、Fork 成功立即登记、ID 跨库概率诊断、统一 Catalog 和不保旧 API 均为已选边界，不列为缺陷。对 SOLID 的判断落在具体责任与重复工作上，不建议为形式完整而抽接口或拆程序集。

## R02-01：长 Event 尾的保存准入重复回扫历史

**级别：P2；性质：源码可确认的性能债，实际成本尚未量化；建议先测量。**

### 事实与触发

一次普通 `work.CommitEvent(...)` 先创建请求，再调用共同组内核。最近 State 的同一事实至少被独立查询三次：

1. [CommitSave](../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs#L173) 调用 [CreateCommitRequest](../../src/DurableGraph.Persistence/Repository.GroupCommit.cs#L8)，后者经 `RequireCheckoutWriter` 进入 [RequireCheckoutSource](../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs#L161)，调用 `FindNearestState`。
2. [CommitBranches 输入检查](../../src/DurableGraph.Persistence/Repository.GroupCommit.cs#L61) 再次调用 `RequireCheckoutSource`，再次查询。
3. [PrepareSaveGroup](../../src/DurableGraph.Persistence/RepositoryResources.GroupSave.cs#L57) 调用 [ValidateSaveTarget](../../src/DurableGraph.Persistence/RepositoryResources.GroupSave.cs#L27)，再从准确 expected Commit 查最近 State。

[FindKind](../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs#L205) 经 `ScanHistory` 从准确 tip 逐条扫描到匹配 State 或历史根。未保存 State 的纯 Event 分支，以及一个 State 后连续追加很多 Event 的分支，都会触发长尾回扫。长度为 k 的尾部每次准入包含 O(k) 描述访问；连续追加 N 个 Event 的这些路径包含 O(N²) 的累计描述访问。State 密集、尾部很短的常用轨迹不必承受同样成本。

这不是把物理缓存当作不存在：[Describe](../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs#L158) 每条调用 `BusinessEntryPayloadCodec.Decode`，其 [Decode](../../src/DurableGraph.Persistence/BusinessEntryPayloadCodec.cs#L32) 会复制 summary；随后 `ReadMetadata` 的 [缓存命中路径](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L221) 可以避免 frame 解码，却不会取消 HS 遍历、Describe 或 summary 复制。因此这里首先是遍历/CPU/分配问题，不能据此声称每次都产生相同规模的磁盘 I/O。上游 HS 读缓存的实际收益未在本包测量。

### 影响与边界

功能正确性由重复来源检查保护，目前没有证据应直接删除其中任一层。风险是公开支持的纯 Event 长轨迹吞吐逐渐下降；Summary 越大，导航时的临时复制可能越明显。现有 [ConsecutiveEventsAndStatesUseExactHeadHistoryAndNearestStateSource](../../tests/DurableGraph.Persistence.Tests/DB078HistoryTests.cs#L40) 验证语义，不构成长历史复杂度门禁。

[路线图](../../docs/DurableGraph-research-roadmap.md#L194) 已把读取/保存成本优化放在真实测量之后；本项将一个具体可测的热点补到这个边界内，不据此自动启动索引或缓存工程。

### 最小建议与可执行验收

先在现有 Persistence.Tests 的 friend 测试中加入计数见证：

1. 分别创建纯 Event 分支与 `S0,E1,...,EN` 分支；使用单 int 根，Summary 分别为空和固定非空；令 N 为 32、64、128。关闭 observer，避免用业务图规模掩盖导航成本。
2. 在 `RepositoryResources.ScanHistory` 的 visitor 接缝增加仅供内部诊断的描述访问计数；比较每次 Commit 前后增量和累计量。同时记录现成的 [GraphStore.ReadCacheStatistics](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L32) 的 GraphHits/GraphMisses/GraphDecodes、分配字节和耗时。缓存预算取 0 与足够容纳该轨迹两组。
3. 正控用每次都 CommitState 的相同规模轨迹，区别 Event 尾长度和对象捕获成本；同时覆盖 fork 后没有本地 State 的继承尾。

只有成本确有影响时，先消除同一操作中重复的已证明来源选择：将准确 base/current/State 来源作为操作局部选择结果传给后续准备，并保留资源 owner 的 publication、lifetime 和准确 expected Commit 核验。若要跨操作复用 `StateSourceCommitId`，必须明确谁证明它及谁在成功 Event/State、Fork/Open、失败/fault 时维护，不新增第二个持久父关系。

验收除计数下降外，必须保持无 State、跨 fork、State/Event 混组、过期请求、错误来源拒绝、失败后原基线及冷开恢复回归；不能用性能目标替代完整来源验证。

## R02-02：请求创建复制自定义 Summary 时没有 Repository 操作 guard

**级别：P3；性质：边缘防御候选；未做运行复现，不计作已确认缺陷。**

### 事实与待证路径

[CreateCommitRequest](../../src/DurableGraph.Persistence/Repository.GroupCommit.cs#L8) 只调用 `RequireCheckoutWriter`，随后直接构造请求，没有设置 `_busy`；[CommitRequest 构造函数](../../src/DurableGraph.Persistence/CommitRequest.cs#L14) 在读取 `ExpectedBaseCommitId` 前执行 `summary.ToArray()`。

普通 `byte[]` 支持的 ReadOnlyMemory 不执行应用回调。但 `ReadOnlyMemory<byte>` 也可来自自定义 `MemoryManager<byte>`，其取 Span 过程可运行实现方代码。因此需要验证下列边缘情况：summary 复制期间，用户的 `GetSpan` 回调能否重入同一 WorkingCopy 的 Commit/Dispose，改变基线或占用后，外层请求仍正常返回。即时 CommitState/Event 也先走此请求构造路径。作为对照，[创建新根](../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs#L61) 在进入准备前已置 `_busy`，并且其 summary 复制位于资源 `_preparing` guard 内。

这不是并发线程访问；风险点是在同步公开调用内部触发提供者代码。不会据此推断发生存档损坏：后续执行还会检查原副本活性和准确基线。可能暴露的是调用内基线改变或“忙时 Dispose 被拒绝”合同的覆盖空洞。

### 最小建议与可执行验收

先增加一个极小的 `MemoryManager<byte>` 测试夹具：先取得 `.Memory`，再安装一次性 GetSpan 回调（避免 Memory 属性构造阶段提前回调）；回调分别尝试 `work.CommitEvent`、`work.Dispose`、`repository.Dispose`。对 `CreateStateCommitRequest`、`CreateEventCommitRequest`、即时 Commit 分别观察是否被拒绝以及外层结果、BaseCommitId、占用和 owner 健康状态。

若可复现，最小修复候选是在请求准入、summary 复制和 ExpectedBase 绑定期间保持 Repository 的局部操作 guard，并在退出时清理；不要引入新请求状态机、锁或异步设施。需注意即时 Commit 随后进入 `CommitBranches`，应在独立请求构造完成后释放 guard，不能让普通调用被误判为重入。验收还应保留 summary 自有复制、创建请求不 Capture、不 append、成功后旧请求过期等既有测试。

若团队将自定义 MemoryManager 的重入排除在输入合同之外，应明确记录该排除并关闭此候选；当前 XML 未见这个限制。本包不因这一小众路径擅自改变公开流程。

## 已核对且应保留的机制

| 机制 | 当前证据与意义 |
|---|---|
| 组输入先验证，再 Capture；请求绑定原活副本和准确基线 | [BranchGroupCommitTests](../../tests/DurableGraph.Persistence.Tests/BranchGroupCommitTests.cs#L140) 覆盖延迟捕获/summary 自有复制/过期；非法全集输入在首次 Capture 前拒绝。不要加入额外 Consumed 权威。 |
| 全组成功后才推进公开 BaseCommitId，Event 不安装 State | [组交付](../../src/DurableGraph.Persistence/Repository.GroupCommit.cs#L100) 先构造全组描述，再推进副本历史；[混组回归](../../tests/DurableGraph.Persistence.Tests/BranchGroupCommitTests.cs#L20) 检查 State 基线、原实例和环境固定。 |
| 占用按 BranchId、打开失败释放、旧 Dispose 不释放后继 | [CheckoutCore](../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs#L77) 的 acquired/delivered finally 和 [占用测试](../../tests/DurableGraph.Persistence.Tests/DB078BranchOccupancyTests.cs#L31)。未建议把它改成标签键或弱引用自动释放。 |
| Fork 依据准确来源发布支线，树按来源真实发布分支归属 | [BranchOriginQueryTests](../../tests/DurableGraph.Persistence.Tests/BranchOriginQueryTests.cs#L13) 覆盖空 fork、父线前进和冷开；单目标读取与全森林校验有刻意范围区分。 |
| 分页不交付损坏/取消/清理失败的前缀，Before 校验被排除 anchor | [CommitHistoryPageFailureTests](../../tests/DurableGraph.Persistence.Tests/CommitHistoryPageFailureTests.cs#L31)、[终项清理取消](../../tests/DurableGraph.Persistence.Tests/CommitHistoryPageFailureTests.cs#L112)；页满可有空末页是明确合同。 |
| 创建分支登记与打开 WorkingCopy 分开 | [BranchRegistrationFailureTests](../../tests/DurableGraph.Persistence.Tests/BranchRegistrationFailureTests.cs#L19)、[物理创建失败](../../tests/DurableGraph.Persistence.Tests/BranchPhysicalCreationFailureTests.cs#L15)。创建成功而打开失败不意味着撤销支线。 |
| 组的输入枚举/模型回调 guard 与故障后停用 | [BranchGroupFailureTests](../../tests/DurableGraph.Persistence.Tests/BranchGroupFailureTests.cs#L170) 已有 GetEnumerator/MoveNext/Current/Dispose 和用户模型回调的边界见证；R02-02 只指出未覆盖的另一种提供者。 |

## 修改与验证交接

- 本包仅新建本文；未修改产品、测试、共享状态文档或其他审阅者文件，没有执行 dotnet、提交或发布。
- 上述测试名称及行为来自源码阅读；此次运行结果由主线程统一填写，不复用历史测试通过数。
- 建议主线程相关过滤器：`FullyQualifiedName~BranchGroup|FullyQualifiedName~DB078|FullyQualifiedName~BranchOriginQueryTests|FullyQualifiedName~BranchTreeQueryTests|FullyQualifiedName~CommitHistoryPage|FullyQualifiedName~RepositoryTag|FullyQualifiedName~BranchRegistrationFailureTests|FullyQualifiedName~BranchPhysicalCreationFailureTests`。注意 `DB078` 是文件名历史前缀，实际部分测试类为 `EventHistoryRepositoryTests`，故需要同时覆盖该类或运行完整 Persistence.Tests。
- 未覆盖：真实长历史耗时/HS I/O、内存提供者运行复现、进程 kill/掉电、跨平台、此轮普通 DG 包资格、外部非法修改打开中的文件。本报告没有把这些边界推断成已通过。
