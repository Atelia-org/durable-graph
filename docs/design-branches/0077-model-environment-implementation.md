# DB-077 实施批次与验收记录

> 2026-09-27；状态：已完成并通过独立验收。用户授权从 DB-077–083 规划并委派一批实施任务，以及按需 Git 提交。
> 合同：[DB-077](0077-repository-model-environment-slice.md)；目标：[DB-083](0083-repository-checkpoint-api-user-stories.md)。实施起点 `180c476bd4109f0cd7c0039258092cc8e10ba1a8`，工作树干净。

## 本批交付边界

完成 DB-077：一次 Repository 打开固定一份模型配置快照，所有创建、恢复、读取共用该环境；durable exact-current Normalize 由库保证恒等，不执行历史转换回调。迁移活动源码、测试、README 与真实包消费者，删除逐操作模型参数。

保留当前泛型根、严格 E/S 交替及全仓一个工作副本，作为独立验收停点。非泛型公共迁移、Event-first、跨类型替根、Checkpoint 查询、多分支 Fork 属于后续 DB-078。DB-079–082 的准备材料和实例复用不进入本批；不改持久格式或兄弟仓源码。

上游本地包 `0.1.2-dev.20260927.1` 单独核对来源并进行包模式兼容验证。上游 tag 已交付不等于 DG 已交付 tag；DG 接入仍依赖 DB-078-A 的地址与公开入口。开发包验证与正式默认 pin 分开记录。

## 任务与验收映射

| 任务 | 所有权与依赖 | 完成证据 |
|---|---|---|
| G0 基线与集成 | 主线程；核对设计、源码、包来源、共享接口 | 根构建、原测试基线、集成 diff、最终独立审阅 |
| G1 固定模型环境 | Repository、WorldWorkspace、Registry XML 与新增环境测试 | null 在资源创建前拒绝；builder 修改仅影响后续 Open；普通/Family 闭合复用；精简目录惰性恢复；失败资源释放 |
| G2 current 恒等 | StateModelBinding 与规范化测试 | 完整 layout/typed DTO 校验；值/ID/preparation 保持；current 不执行回调；历史转换及 live Schema 检查保持 |
| G3 仓内消费者 | 活动 src/tests 调用点及测试适配器 | 删除逐操作 registry；故障模型通过不同 Open 隔离；低层独立 snapshot 入口不变；全量回归 |
| G4 包与示例 | 活动 experiments 与根 README | 强制 Family EventHistory、恢复与 README 原文真实包见证；历史旧包 lane 保留原 API |
| G5 独立验收 | 独立只读 reviewer，主线程裁定/复跑 | 无未解决阻断项；文档与实现一致；包来源和真实加载路径可核对 |

G1/G2/G3/G4 按文件所有权并行；所有 dotnet 构建、测试与打包由主线程串行执行。测试同时保留 append-only、严格 Open、不修尾、完整源验证、发布 outcome/fault、防重入和资源寿命协议。

## 后续任务顺序

| 停点 | 主要实施任务 | 进入条件 |
|---|---|---|
| 078-A | 非泛型 Repository/BranchCheckout、owner-bound 地址；Event-first 与自由历史；跨类型根/身份安装；冷开验证与真包迁移 | 本批验收通过；以 DB-078 §1.1 为完整纵向合同 |
| 078-B | eager 独立 Checkpoint/nearest PreviousX；固定历史事件枚举；默认可变隔离及稳定 getter | A 的地址与导航稳定 |
| 078-C | 每 branch 占用；恢复后发布的 guarded named Fork；故障、Dispose 与重入见证 | A；推荐 B 先验收 |
| 079 | 合并按用途准备和物化核心，保留 ReadPair 独立共享证明 | A/B/C 正确性基线完整 |
| 080 | 单 State 准备材料槽与完整 Schema 证书 | 079；无 State 请求不触碰槽 |
| 081 / 082 | 热 State 材料证明 / 单 State immutable 叶复用，分别测量总成本 | 都依赖 080；082 不硬依赖 081；无收益允许记录结论而不启用 |
| DG tag 接入 | 对齐已发布上游 API、严格 Open/持久屏障与错误语义，增加创建/解析和冷重开真包见证 | 078-A 与精确上游包；独立于 079–082 |

各后续停点仍需按实际前置结果分派实施；这张表不声称它们已交付。

## 验证记录

- 实施前：`dotnet build DurableGraph.slnx -c Release --no-restore` 通过，0 警告、0 错误。
- 实施前全量回归 2729 通过（Serialization 163、Storage 202、Persistence 744、Runtime/Generator 1620）。
- 新 Storage 本地包模式根构建通过，0 警告、0 错误；全量回归 2743 通过（163 / 202 / 753 / 1625），无失败或跳过。
  使用专用 NuGet.Config 将五个存储包映射到上游 `artifacts/tag-feed`；restore assets 确认全部为 `0.1.2-dev.20260927.1`。
  来源 revision 为 `deb55672106c8a8966e4b04a75bedf0b1523be7f`，五包 hash 与 manifest 一致。
- 独立只读 reviewer 未发现阻断项；主线程复核核心接线与消费者差异，并修正一处已失效的 Normalize 注释，保留 ReadPair 比较算法。
- 核心新增见证：[RepositoryModelEnvironmentTests](../../tests/DurableGraph.Persistence.Tests/RepositoryModelEnvironmentTests.cs)、
  [CurrentNormalizationTests](../../tests/DurableGraph.Tests/CurrentNormalizationTests.cs)；普通/强制 Family 的真实生成升级路径见
  [GeneratedStateModelTests](../../tests/DurableGraph.Tests/GeneratedStateModelTests.cs)。TRX 已确认这些用例逐项通过。
- 本机详细日志、TRX 与独立验证脚本在忽略目录 `.artifacts/db077/`；包验证使用 DG `0.0.0-db077.20260927.1` 与上述独立 Storage 版本，未发布远程包。
- 24 条真实包 runner 通过：EventHistory、Recovery、README 原文、StateStore、Generic、RecordClass、Array、BclScalar、
  CompositeDictionary、CrossAssembly、Dictionary、Enum、HistoryCapability、ImmutableLeaf、InheritanceLibrary、InlineLibrary、
  InlineStruct、List、Nullable、Record、TemporalScalar、ValueUpgrade，以及 OrganizationMigration、DurableBaseMigration。
  EventHistory 使用强制 Family；其隔离 restore assets 指向新 Storage 版本，九个输出运行时 DLL 均逐字节匹配 feed 的包。
  两条真实旧包迁移保留已接受 history、旧帧和冷重开续写；旧输入分别沿用 DB-071 与 DB-068 记录的包。
- 默认 `0.1.1-preview.2` pin 未修改；已恢复普通 restore，根构建仍为 0 警告、0 错误，Storage 202 项、Persistence 753 项通过。
- List replay 普通冒烟通过：20 个独立测量仓库、300 个已验证修订；此处只作为 API 迁移后的正确性证据，不作性能排名。
- 最终集成差异已复核；15 份变更 Markdown 的 765 个本地链接有效，现有标题锚点未改动，`git diff --check` 通过。
- G0–G5 全部完成，无未解决阻断项；后续实施从 078-A 开始。
