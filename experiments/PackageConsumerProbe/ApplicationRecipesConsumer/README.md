# 应用接入配方：导航、分叉切换、预览

三个可复制的应用代码片段，共用一个普通包消费者验证。它们不是新框架API，也不依赖Echo Garden仓库或临时实验文件。先看对应helper；[Program.cs](Program.cs)是完整的持久化验收故事，[Model.cs](Model.cs)是只有三个字段的示例模型。

| 要解决的问题 | 可复用代码 | 可执行边界检查 |
|---|---|---|
| JSON配置/网页DTO中的ID | [Navigation.cs](Navigation.cs) | [NavigationChecks](NavigationChecks.cs) |
| 已创建分支，但打开失败 | [BranchSelection.cs](BranchSelection.cs) | [链接真实helper的故障测试](../../../tests/DurableGraph.Persistence.Tests/ApplicationRecipeSwitchTests.cs) |
| 不加载State显示预览 | [PreviewSummary.cs](PreviewSummary.cs) | [PreviewSummaryChecks](PreviewSummaryChecks.cs) |

## 1. 保存“上次使用的分支”

`BranchId`和`CommitId`默认用System.Text.Json序列化会得到 `{}`。本例把其公开字节codec包装为Hex，再放入版本化应用DTO：

```csharp
string json = NavigationCodec.Encode("garden", work.BranchId, bookmarkCommit);
// 保存json；下次启动读取。导航配置写入/替换策略由应用负责。
Navigation saved = NavigationCodec.Decode(json);
if (saved.RepositoryKey != "garden") { throw new InvalidDataException("Unknown repository."); }
IdText.TryReadBranch(saved.LastUsedBranch, out BranchId branch);
using var resumed = repo.OpenWorkingCopy(branch);
```

这里的`repo`必须是应用按`RepositoryKey`选定并打开的仓库。Decode检查协议版本、必需字段和ID表示；Repository仍负责成员和位置校验。Hex大小写都能读取，写出统一大写；无书签用null，不编码default。使用Commit书签前同样调用`IdText.TryReadCommit`。

LastUsedBranch是用户选择，不是“列表最后一条”。标签可重复，结果无排序保证；空fork的current还可能是祖先旧Commit。ID也不是跨库全局身份，不能依赖概率误用检查选择仓库。本例不提供默认JsonConverter、字典键协议或可直接标注的Durable ID字段。

## 2. 分叉后再切换，分别处理结果

`BranchSelection`拥有其工作副本；调用方拥有Repository并保证所有操作串行。Fork只记录已创建的待打开分支，OpenPending才尝试切换：

```csharp
using var selection = new BranchSelection(repo, currentBranch);
BranchInfo created = selection.Fork(savedCommit, "另一条路线");
// Fork自身的发布异常在这一行处理：按Outcome/TargetBranchId核查，不自动重建。
try {
    selection.OpenPending();
} catch (Exception error) when (selection.PendingBranchId == created.Id) {
    Console.Error.WriteLine($"分支 {IdText.Encode(created.Id)} 已创建，尚未打开：{error.Message}");
    // UI保留selection与created.Id。健康owner可在解除临时失败后仅重试OpenPending。
    if (repo.IsFaulted) { throw; } // 调用方结束使用所有副本，关闭后重开created.Id。
}
```

这是一个错误呈现边界，不是“所有异常都可以重试”的策略。缺模型时，向旧registry补注册不会更新已打开Repository的冻结配置；应关闭后用完整配置重开，再打开同一ID。Fork若自身抛发布异常，不进入上面的open处理；Unknown/Published都不能盲目再次fork。

旧副本在新open失败时保留，但仅健康owner下可继续保存。新open成功后才换Current、清Pending，再Dispose旧副本；清理错误不会误入上述“尚未打开”的catch。旧副本的未提交编辑不属于所选存档，Dispose也不会保存它们，应用必须先决定保存还是丢弃。

待打开的ID目前保存在内存；若操作中途退出也需要续接，将它作为应用导航状态持久化。该偏好与DG提交没有跨文件原子性保证。

## 3. 从State投影版本化摘要

```csharp
// 先完成全部业务编辑。
state.Day = 2;
byte[] summary = SummaryCodec.Encode(SummaryCodec.FromState(state));
CommitInfo saved = work.CommitState(summary);
// 从投影开始到整个提交调用返回/抛出，业务图保持稳定。
```

多线程应用用同一应用锁保护这整段；在同一锁内“先算摘要、再改State”仍会产生不匹配。多分支共同提交也遵循先完成所有修改、再投影所有摘要、构造请求并立即提交的顺序。`CreateStateCommitRequest`只复制摘要、保留根引用，State稍后才捕获；[可执行对照](Program.cs)保留了这个差异，不依赖框架自动修正。

读端先取得准确Commit，再解析其已返回的字节：

```csharp
CommitInfo commit = repo.ReadCurrentCommit(branch).Commit; // 存储错误正常传播。
SummaryReadResult result = SummaryCodec.Read(commit.Summary);
// Available: 使用result.Preview；None: 无摘要；
// Unsupported: 此预览版本暂不支持；Invalid: 摘要不可用。
```

V1是`schemaVersion/day/flowers/won`的UTF-8 JSON，版本独立于DurableSchema。示例预算4KiB，校验必需字段、类型、范围和UTF-8；未知版本不按V1猜测，空/坏摘要不默认成day0，也不强迫加载State。降级只发生在应用解码阶段，不能吞底层读取/校验错误。继续游戏仍恢复State，预览不是权威状态。

## 运行与证据范围

先按[依赖指南](../../../docs/storage-dependency.md)准备完整普通包feed和准确G，再从仓库根目录运行：

```powershell
./experiments/PackageConsumerProbe/Run-ApplicationRecipesProbe.ps1 `
  -PackageSource <feed> -Version <G>
```

可用`-WorkDirectory <fresh-outside-directory>`指定全新仓外目录。runner不打包，接受十个Atelia原包或附K4os的完整候选feed；独立cache、正常PackageReference/Generator/Build，执行Publish与Rebuild Verify，最后启动两个进程：

- seed：保存纯State、旧点fork、打开空分支，写出导航JSON；执行文本/摘要边界检查。
- verify：无模型浏览预览，按导航冷重开并保存子线，确认书签/父线不变；另验缺模型打开失败和冻结配置、重开同ID，以及摘要共同提交正反例。

`result.json`记录实际输入哈希、包nuspec来源、包/输出DLL核对、命令日志/PID/退出码及最终Passed。实际包来源要与已有合格feed证据关联，不能把当前示例Git HEAD冒称包构建来源。

故障注入另由源码测试覆盖：真实allocator临时失败、旧编辑继续保存、同ID重试、owner fault禁止继续、回调重入不破坏占用。这些不是普通包故障注入或网页端到端资格。完整结果见[实施验收](../../../docs/design-branches/0096-history-and-head-model/application-recipes.md)。
