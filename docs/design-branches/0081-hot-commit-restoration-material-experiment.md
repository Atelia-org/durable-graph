# DB-081：热提交恢复资格的前置实验

> 状态：**资格实验形成否定结论，热提交准入未实施**；2026-09-28。基线 `f5ec476`。按 [DB-081 §6](0081-hot-commit-restoration-material-slice.md#6-成本与停点) 停在证明门，不修改产品接线；DB-080 继续默认启用。
> 本记录保留可执行反例、取舍及重启条件；公共语义仍见 [DB-083](0083-repository-checkpoint-api-user-stories.md)。

## 问题、范围与最小验收

问题是：现有 binding 合同能否在不读回 typed body、不预执行 Allocate/Hydrate 的条件下，证明提交侧 owned DTO 可以替代新 State 的完整冷恢复准备？

最小否定证据是一份合法 provider：Capture、保存、factory 闭合均成功，但实际冷 reader 另有标准 Schema 检查；迟注册冲突时冷恢复拒绝。若已有证据不能覆盖该检查，就不能将提交材料标为合格。
另验证旧 State 经历史中间版本产生的证书不能直接带入已保存为 current 的新 State。测试不引入故意破坏值往返的 codec，也不按 callback 次数改变业务值。

本轮只增加回归见证并更新设计状态。没有公开 API、生成器、格式、依赖包或缓存容量改变，没有始终返回 false 的热准入框架，也没有禁用后无人使用的产品接线。

## 源码裁决

现成的保存安装机制已经解决了来源和发布问题：

- [NormalizedRevision.FromCandidate / WithAddress](../../src/DurableGraph.Persistence/NormalizedRevision.cs) 保留本次完整候选、实际 binding、独立的新 revision、每行 exact head/H 与 Store 来源。
- [PreparedWorldSave](../../src/DurableGraph.Persistence/PreparedWorldSave.cs) 在发布前准备安装，只有 State 推进工作副本；Event 没有下一 State 基线。
- [Repository.Publish](../../src/DurableGraph.Persistence/Repository.cs) 保留明确的 append、Journal、ref 和 Install 顺序。未来有证明时可在发布前构造局部候选，正常 Install 后仅赋值槽引用。
- [StateModelBinding.Normalize](../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs) 对 exact current DTO 已有恒等合同；无需为新 current State 重放旧历史 Upgrade。

缺口在被省略的 reader 路径：

1. [StateReaderBinding](../../src/DurableGraph/Runtime/Binding/StateReaderBinding.cs) 接受任意 `ReadBase` / `ApplyDelta` 委托。它们可以通过同一 binding context 执行标准 Schema 检查，甚至按正在读取的值选择依赖。
2. [StateModelSnapshot](../../src/DurableGraph.Persistence/StateModelSnapshot.cs) 的闭合证书记录 factory **实际执行**的检查；解析 reader 或再次验证 factory 证书不会执行 reader body。
3. [普通生成 reader](../../src/DurableGraph.Generator/DurableSchemaGenerator.GeneratedState.cs) 与 [Family reader](../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericFactories.cs) 都使用同一公开 reader 类型，没有传递完整的读写对应与依赖闭合证明。知道生成源码做什么，不等于 Runtime 已有识别并验证该能力的协议。
4. [StateValueBinding](../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs) 的动态值操作仍可来自自定义 `IStateOps`；仅信任容器外壳不能证明元素操作。每份产品 State 必须有 durable 根，因此内建 string 的已知实现也不足以形成有用的完整图资格。

最小反例是 `ReadBase` 正常读出 DTO 后，经同 context 执行 `BindSchema(D)`。D 不在根布局、Capture 或 factory 的闭合路径中。
保存成功不要求执行这个检查；D 迟冲突后冷恢复必须拒绝，而仅凭 owned DTO 与上述已有证据无法证明省略它。
这里缺的是充分证明，不是新发现的产品读取错误。DB-080 实际执行冷恢复再收集证书，保持正确。

## 可执行见证

| 入口 | 区分的事实 |
|---|---|
| [DB081HotAdmissionProofTests](../../tests/DurableGraph.Persistence.Tests/DB081HotAdmissionProofTests.cs) | 普通与 Family factory 的 body-only 依赖；Base 与真实 Delta 路径；合法 Commit 不预执行恢复回调；迟冲突阻止 cold Fork，保留旧槽且不留 ref；成功对照沿 cold→DB-080 hit 保持提交值和可变隔离 |
| 同文件的按值依赖见证 | 同一个 reader 曾成功冷读另一个值，也不证明新 State 的 body 依赖；不能用一次采样建立 reader 永久资格 |
| [DB081HistoricalCertificateTests](../../tests/DurableGraph.Persistence.Tests/DB081HistoricalCertificateTests.cs) | v1→v2→v3 仅中间版本依赖 X；Event 不重建被淘汰的槽且保留 rewrite；保存 current 新 State 后 X 冲突不影响其 cold/hit，旧 State 仍拒绝 |

普通与 Family 测试使用手写 binding/definition，验证真实运行时接缝；不将它们称为新增 Source Generator 或 PackageReference 消费证据。
普通 binding 夹具通过反射取得同仓库 context，不代表新增公开访问 API；Family 反例直接捕获标准 factory 参数中的 context，已可独立证明缺口。
测试不向产品槽注入未证明材料，因而是“现有证据不足”的反例，不是宣称已测过某个完整热实现的性能或故障矩阵。

## 备选方案及停点

| 方案 | 收益 | 尚欠的证明或代价 | 本轮结论 |
|---|---|---|---|
| Capture / factory 证书直接入槽 | 最容易接线，少做冷准备 | 漏 body-only 和按值选择的依赖 | 拒绝 |
| 复制旧 State 证书 | 可借已有冷恢复证据 | 新 body 可能多依赖；已消失的历史依赖又可能造成误拒绝 | 拒绝无条件复制 |
| 临时编码为 Base 后试读 | 可以观察一次 Base reader | 实际持久 Delta 链的 ApplyDelta 依赖未被执行；增加重复编码/解码，不能代替精确链证明 | 不作为充分证据 |
| 在 Commit 读回完整实际链 | 可沿真实冷路径取得证据 | 把冷准备搬到每次 Commit；不能实现本片“不读回”的资格目标，未 fork 的提交也付费 | 不以此冒充热 DTO 复用 |
| 只迁移已有证据且完整来源、head、layout、DTO 均不变的新 revision | 原则上可能覆盖预热后的无变化提交 | 仍需新 revision 的根/结构证明；不能覆盖初始 State 和通常会改值的探索提交 | 保留窄路径假设，未实现、未测量 |
| 受约束 codec/provider 证明 | 有望支持普通、Family 和递归值操作 | 需明确读写对应、全部省略回调的依赖闭合及保守拒绝规则，涉及生成器到 Runtime 的支撑协议 | 独立设计后才重启 |

独立审查特意挑战了“所有热候选都不可能”的说法：已有冷证据、完整相同内容来源的窄路径原则上可能成立。当前结论只限于**现有合同无法为一般新/变更 State 提供所需的无读回证明**，不宣称理论上无法优化。
不按委托是否 static、`Target == null`、生成名称或标记猜测资格，也不加一个没有证明规则的信任 bool。

按原分片 §6，在成本测量之前停止默认接线。没有合格的通用热候选，就不制造 Commit+Fork A/B 数据；本轮不声称额外提交成本、热资格覆盖率或端到端加速。
原分片 G1–G3 的热发布故障矩阵及总成本测量仍未执行；以后重启时必须完成，不能以当前 cold 回归替代。

## 验证与后续

主线程完成集成验证：Persistence 基线 **897/897**；新增 **10** 个用例后完整 Persistence **907/907**，0 failed、0 skipped。
根 Release build **0 warning / 0 error**。产品源码、生成器与包接线未改，本轮未重跑完整 solution tests 或 PackageReference probes，不引用旧结果充作本轮证据。
独立审查未发现遗留阻断；主线程核对实际源码、diff 与 TRX，成功对照及真实 Delta 断言均通过。
7 份修改/新增 Markdown 的 463 个本地文件链接有效，原有标题与显式锚点保持，`git diff --check` 通过。

首次根构建进程以 `-1073740791` 异常退出，日志无编译诊断；未归因为代码错误或已查明的环境原因。单节点禁用节点复用重试成功。
构建与测试串行，成功命令为：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nodeReuse:false
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=final.trx" --results-directory .artifacts/db081/test-results
```

原始输出保留在 `.artifacts/db081/`：`baseline.log`、首次 `build.log`、成功 `build-retry.log`、`tests.log` 与 `test-results/{baseline,final}.trx`，不作为产品文件提交。

后续建议先选 [DB-082](0082-prepared-immutable-leaf-reuse-slice.md)：它仅硬依赖 DB-080，可从真实成功物化取得叶实例及其额外检查证据，不要求先解决热提交资格。
若初始 State 后立即 fork 的成本确实成为业务瓶颈，再独立设计上述 codec/provider 证明；不要只给 Repository 增加接线。无变化提交的窄路径也须先有工作负载和完整来源证明。

续工顺序：[PROJECT-STATE](../../src/PROJECT-STATE.md) → 本记录 → 所选分片完整合同 → [DB-080 实施记录](0080-prepared-checkpoint-reuse-implementation.md) 与 [DB-083](0083-repository-checkpoint-api-user-stories.md)。
DB-084 的 DG tag 接入仍是独立能力，不随本轮开始。
