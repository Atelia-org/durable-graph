# DB-004：Snapshot History 的创作与发布工作流

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0004-snapshot-history-authoring-and-publishing.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：单项目 local Publish / CI Verify 的当片结果；不等于持久 SchemaStore、多 writer 事务或长期工作流冻结。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 历史材料（2026-09-06 治理）：原 Open 标签保留；单项目 local Publish / CI Verify 已落地，
> 不能把长期工作流未冻结读成“publisher 尚未实现”。当前事实见[PROJECT](../../../src/PROJECT-STATE.md)，后续问题见[路线图](../../DurableGraph-research-roadmap.md)。

> 状态：Open
>
> 创建日期：2026-08-27
>
> 当前快速原型选择：成功的本地非 design-time compilation 自动发布 current Snapshot；history 作为 checked-in AdditionalFiles；单 project、单 TargetFramework、单 writer、串行发布。

> 实现状态：EXP-008 已通过 `Atelia.DurableGraph` 包内 Generator、`build/*.props/targets` 与 `DurableGraph.Build` tool 落地并由真实 PackageReference consumer 验证；该状态不表示格式或工作流已经冻结。

## 问题

Source Generator 只能从显式输入重建历史，不能把自己的 `AddSource` 输出隐式累积为下一轮输入。需要由 Generator 外部的 owner 把 current durable shape 冻结进受版本控制的 Snapshot History，同时避免失败编译、缓存残留或同 key 异形污染历史。

## 已观察到的边界

EXP-006 已证明：

- compiler-generated candidate 不是后续 AdditionalFile；
- post-compile publisher 可以形成下一 build 才可见的反馈；
- staging 残留会造成假阳性或延迟污染，发布前必须只处理本次成功 compilation 的 candidates；
- clean build 可以从外部 history 重建 strong Snapshot；
- 重复发布可以做到内容幂等。

EXP-008 进一步观察到：

- 单一 nupkg 可把 runtime、C# analyzer、MSBuild build assets 和私有 tool 一并交付给直接 PackageReference consumer；
- local Publish、CI Verify、缺前驱 Generator error、缺 current history verifier error 与幂等发布均可在 package boundary 上执行；
- candidate manifest 与 accepted history 必须使用不同 header，不能靠改扩展名把候选冒充 authority input；
- 没有 durable type 时生成空 candidate manifest，可以覆盖旧 generated manifest 而不创建空 history 目录。

## 当前快速原型选择

### Authority 与格式

- 每个 durable project 默认在源码树的 `DurableGraphSnapshots/` 维护 checked-in Snapshot History 目录；当前只读取该目录根部的 `*.dgsnapshot`。
- 一份文件表达一个 exact `(SchemaId, Version)` shape；当前文件名包含 SchemaId UTF-8 SHA-256、Version 和 canonical content SHA-256，原始 SchemaId 不进入路径。
- 文件至少保存 format version、SchemaId、Version、按 FieldId 排序的 FieldId/TypeTag；机械 generated member name 由 FieldId 派生。
- 当前采用简单、确定、line-oriented 的 build-time probe format；它不是 wire format、persistent SchemaStore encoding 或最终 canonical Schema authority。
- 同 key 同形幂等；同 key 异形硬失败，永不覆盖。

### 自动发布

```text
local publish mode
    -> run or reuse an up-to-date successful CoreCompile
    -> require exactly one fixed-name current candidate manifest
    -> validate all candidates and existing history
    -> final publish target appends missing exact versions
    -> command succeeds only if publication succeeds
    -> next build consumes them as AdditionalFiles
```

- design-time build 不发布。
- local publish mode 允许 current Vn 尚未入账；它必须由本次真实 compilation 产生 candidate，并在 build command 成功前发布。
- CI/`ContinuousIntegrationBuild` 使用 read-only mode：要求 current Vn 已有 exact history entry；漏提交本地生成文件时立即失败，绝不发布。
- 编译失败不发布；publisher 不得扫描并追认旧 staging。
- 发布边界是成功 `CoreCompile`，不是整个可扩展 MSBuild 图的最终事务点；后续 packing、文档或第三方 `AfterBuild` target 仍可能失败，而已冻结的可运行 assembly shape 不回滚。
- publisher 只接受固定 manifest；真实 compilation 会覆盖它，失败 CoreCompile 不进入 publisher。up-to-date CoreCompile 可复用上次成功 compilation 的同一 manifest；若 manifest 丢失则 build 明确失败并要求 clean rebuild，不能静默跳过 gate。
- 定义 durable type 的 project 是该 SchemaId 的唯一 writer。
- 当前只支持单 TargetFramework、单进程串行 build；检测到同 key 冲突即 fail。
- History 文件进入 source control，diff 是发布结果的人工审计面。
- 当前由直接 `PackageReference` 自动导入 `build/Atelia.DurableGraph.props/targets`；暂不启用 `buildTransitive`。

### 连续性闸门

- 新 Schema 必须从 V1 开始。
- 当前 Vn 大于 1 时，history 必须已有无缺口的 V1...V(n-1)。
- local publish mode 中 current Vn 缺失可以 append；已存在时必须 exact shape match。
- CI/read-only mode 中 current Vn 必须已经存在且 exact-match。
- 缺少前驱时拒绝构建可运行 binding；Generator 不猜测或恢复已被编辑掉的旧 shape。

这使任何整体成功且启用 publisher 的 local real build 同时冻结 current version，降低“忘记 accept 后直接改 V2”的窗口。编译器可能已产生 intermediate assembly，但 publisher 失败时整个 build command 仍失败，该版本不算已接受。

## 竞争方案

### A：显式 Accept target

例如：

```text
dotnet msbuild /t:AcceptDurableHistory
```

普通 build 只读且要求 current version 已接受；accept build 只允许 bootstrap V1 或 append `latest + 1`。

优点：build purity 更强，CI/IDE 不会意外修改工作树，版本接受是明确动作。

代价：开发者可能在接受 V1 前直接编辑到 V2。要 fail closed，普通 build 必须在 current 尚未入账时保持红色，accept target 还需要专门的 compilation mode。

### B：CLI/code fix

由 CLI 或 IDE code fix 执行 create-or-compare，并顺便 scaffold upgrade implementation。

优点：用户体验和诊断可控，适合未来原子写入与 rescue tooling。

代价：新增工具部署、版本匹配与调用流程；当前仅有一个 publisher consumer。

### C：引用旧程序集/包

从上一发布 artifact 携带历史。

优点：发布包可自描述。

代价：artifact provenance、多版本引用与 clean clone availability 更复杂；当前没有跨包消费者。

## 当前明确不保证

- 多 writer、并行 build 或多 TargetFramework 的原子发布。
- 多个 Schema candidate 的跨文件事务。
- history format 与未来 wire/canonical schema format 相同。
- 用户从未成功构建/提交过的旧源码 shape 可以恢复。
- 间接 PackageReference、非 C# project 或跨 TFM history ownership。

## 重访触发条件

- ordinary build 修改工作树造成真实 IDE、CI 或团队协作问题。
- 出现并行/multi-TFM/multi-project Schema writer。
- 需要跨文件原子接受一批 Schema versions。
- CLI/code fix 已有其他真实职责，可自然承接 publisher。
- 首个 persistent SchemaStore/canonical format 需要统一 authority。

## 相关材料

- `experiments/SourceGeneratorHistoryProbe/README.md`
- `docs/design-branches/0001-schema-authority-and-runtime-representation.md`
- `docs/design-branches/0002-read-time-version-upgrade-pipeline.md`
