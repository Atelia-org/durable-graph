# 消费者接入配方：实施与验收

2026-10-20。承接[Echo Garden反馈研究](echo-garden-feedback.md)，已完成实施、根构建、定向源码测试与普通包验证。本片不重新打开DB-096已完成交付，也不改变核心API或持久格式。读者入口为[可执行配方指南](../../../experiments/PackageConsumerProbe/ApplicationRecipesConsumer/README.md)。

## 范围与设计

在 `experiments/PackageConsumerProbe/ApplicationRecipesConsumer/` 放一个普通PackageReference消费者，提供三个短小的应用代码示例：

- Navigation：版本1导航DTO、应用仓库key、LastUsedBranch、可选BookmarkCommit；公开字节codec外包装Hex，严格拒绝非法/default，Repo选择/成员校验与表示解析分开。
- BranchSelection：记录Fork返回的新ID，保留旧副本直到新副本打开成功；已有待打开分支时不重复fork；open成功后切换，再释放旧副本。不吞发布异常，不保证faulted owner可继续，不隐式保存旧编辑。
- Summary：版本1 UTF-8 JSON、4KiB应用预算、纯State投影；空/未知版本/无效/可用四种读取结果。投影开始到整个提交调用结束保持图稳定，不新增自动factory。

三个helper属于可复制的应用配方，不是新增库API；README主路径只加简短提醒与入口。一个专用runner在仓外新目录复制消费者源码，普通包restore、Publish/Verify、独立seed/verify进程，记录实际包/源码/日志身份。独立目录与两进程导航是本片特有资格，故单设入口而不向旧API编译负例runner塞入另一套故事。

## 最小验收

1. ID默认/畸形/零值/错长度/未知导航版本拒绝；两进程恢复指定Branch及固定Commit，不按标签/列表排序选择；空fork冷续写仍写子线，书签不随推进移动。
2. 实际切换helper由源码测试链接：旧活副本含未提交编辑，新分支恢复失败后旧副本可在健康owner下保存；目录只增一条；解除失败后仅重试open；faulted owner不能继续。普通包另验缺模型失败与配置冻结，重开完整registry后重用同一分支。
3. 摘要边界与缺字段校验；普通包保存后无模型重开预览，摘要与准确Commit的State投影一致；保留延迟捕获旧摘要反例，正确模式覆盖共同提交。
4. 根solution build、相关源码测试、新普通包消费者运行；验证包来源与实际输出DLL，示例没有friend或手工analyzer。运行时/生成器/Build代码未变时可复用已验收的准确G；当前README/消费者是新输入，不将其提交冒称包来源。
5. 文档、导航、续工状态收尾；只提交本片路径。不把故障注入源码测试、普通包正常路径、网页集成和掉电证据混为一层。

## 实际结果

实施基线为`f99ed4e`；新增helper、示例、runner、三项源码测试及必要导航文档。运行时、生成器、Build、发布协议和持久格式均未改。故障测试直接Compile Link真实BranchSelection，避免测试复制的另一个实现。

| 判据 | 实际结果 |
|---|---|
| 根solution | 准确Storage包模式restore/build Release，0警告/0错误 |
| 定向源码测试 | ApplicationRecipeSwitchTests三项、BranchGroupCommitTests及既有HistoricalForkRestoreFailure，共22/22通过，0跳过 |
| ID/导航 | default/非法/零/长度/版本反例；两进程恢复选定子线与固定书签；重复标签、重排列表、父线不变均通过 |
| 切换 | 源码实测临时allocator失败保旧编辑、旧线继续保存不移动pending、拒绝重复fork、同ID重试成功；owner fault与真实回调重入边界通过 |
| 缺模型 | 普通包fork成功/open准确以InvalidDataException拒绝、登记保留、owner健康；对原registry补登记仍失败，完整重开同ID后继续成功 |
| Summary | UTF-8/字段/版本/预算反例通过；无模型冷浏览与准确State投影比对通过；延迟请求旧摘要反例与正确双分支共同提交均通过 |
| 实际普通包 | Publish/Rebuild Verify均通过且history集合/字节保持；seed PID15780、verify PID32604，分别exit0，准确成功输出 |

普通包G为 `0.0.0-db096-final-readme.20261019110000.28648`，产品来源 `440bccfcb856758f53013697ca853ea6e7b4df85`；Storage S为 `0.1.3-dev.20261019.g70b406c`，来源`70b406c`。复用[既有最终资格](final-delivery-acceptance.md)的候选feed，没有重新打包或覆盖同版本。本轮的新README导航文本不属于旧G的打包来源；所验收的是新消费者使用这批准确包。

runner记录实际输入、nuspec来源、assets、缓存与运行输出；主线程另将11个包哈希与既有README候选资格逐项关联，核对10个当前消费者/runner/global输入哈希、命令日志、实际两PID及TRX。独立subagent审阅实现与真实回执；没有把nuspec声明本身当成新的构建来源证明。

本机工件：

- `.artifacts/application-recipes/`：`root-restore.log`、`root-build.log`、`tests/recipes.trx`、`tests.log`、`package.log`、`audit.json`。
- `W:/TEMP/dg-recipes-accept-128cac2e752c4d748bbfae858c89b9ce/`：复制的源码、独立cache、history、存档、导航和`result.json`。
- 普通包回执SHA256：`75F1C77178CF39653F6FD23175AEEB9874F7581A7C19E1585C0DF6EEAA3B2DD3`。运行时consumer Git HEAD为基线f99ed4e＋本片未提交输入，输入字节已单独记录；不能声称新文件已经存在于f99ed4e。

本片没有再次运行原12个普通包门禁或完整2992项历史基线，也没有网页端到端、导航文件原子替换、并发应用锁、性能或掉电资格。旧副本清理在串行合同下无I/O；实际测试覆盖回调中Dispose被拒绝，不伪造“open正常返回后Dispose失败”的注入。

收尾检查了集成diff、10份Markdown的556个本地链接与64个锚点，未发现错误；`git diff --check`通过。

本片已闭合。今后只有真实消费者在多个typed DTO中反复需要同一转换器，或不能用纯投影后立即提交维持摘要一致性时，才重新评估converter或更强API；不由本配方自动扩出框架机制。
