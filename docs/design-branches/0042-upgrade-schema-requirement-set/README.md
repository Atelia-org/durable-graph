# DB-042：Upgrade 计划的 exact Schema 依赖证书

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0042-upgrade-schema-requirement-set.md`，正文保留当片合同与历史验收含义。
> 阅读范围：plan 级完整依赖与首 callback 前拒绝的当片证据见 §5；原单调 SchemaStore 查询接缝保历史含义，DB-087 承接当前 Schema 视图/reader 环境。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Chosen / Implemented — 2026-09-08。
> 来源：[DB-038](../0038-generic-schema-state-and-binding-design/README.md) 与
> [DB-039](../0039-composable-value-upgrade-design/README.md) 已实现的整链预绑定及缓存复核。
> 当前能力与续工入口见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。

## 1. 问题与目标

`StateBindingContext` 会从冻结的代码目录推导并缓存 Upgrade 中间布局，但它仍实时查询
Repository 内单调积累的 SchemaStore。此前尚未登记的 exact key 以后可能首次取得权威定义；
若该定义与缓存推导不同，必须在任何 owner 或 value Upgrade callback 前拒绝。

现有实现把执行前复核分散在每个 owner step、value plan 及其依赖中，重复递归展开同一
base/inline Schema DAG，并且冲突只报告笼统错误。本片将它收敛为 UpgradePlan 持有的一份
不可变 exact Schema requirement set。

成功标准：缓存 UpgradePlan 再遇到晚登记冲突时，统一验证完整 owner/value 的 base/inline
依赖闭包，在首个 callback 前拒绝，并报告从 owner edge 到冲突 Schema 的稳定依赖路径。

## 2. 选择

- UpgradePlan 构造时以 source/current 及每条相邻 owner edge 的 source/target 为根。每个 value
  dependency 都只能从这些 exact endpoint 的声明字段中选择，其端点及嵌套依赖已包含在 owner
  的 base/inline 闭包中；因此不再遍历一套平行的 value-plan 图，也不以 callback 是否实际取得工具为条件。
- 每个 Schema 根只在构造 requirement set 时展开 base/inline 闭包；nominal reference 不形成
  exact 依赖。集合按 `(TypeExpr, Version)` 去重；同 key 异形在构造时拒绝，并保留两条来源路径。
- 集合保留第一个稳定发现路径用于运行期冲突诊断；外层 `InvalidDataException` 的 inner
  `SchemaConflictException` 携带 repository registered 与 plan expected 两份完整布局。共享 DAG 不按树重复保存，但构造时仍检查
  从较长路径到达所产生的 256 层深度上限。
- 缓存命中时先验证 plan requirements，再执行强类型步骤。UpgradePlan 同时保存 current DTO
  CLR 类型，因此不必为了核对 `TCurrent` 再走会重复展开闭包的 reader/binding 缓存入口。
- 零步、同版本 Normalize 仍以 source/current 为 requirement roots，不能绕过晚登记冲突。
- 普通 `BindSchema`、reader/current/value binding 的缓存命中仍须复核 SchemaStore；它们不属于
  UpgradePlan 生命周期。本片复用同一个 requirement-set 算法实现单根复核，不削弱这些入口。

## 3. 明确不做

- 不加入 SchemaStore catalog generation/epoch；有实际批量 Upgrade 性能证据后，可在 requirement
  set 的 `Validate` 内透明增加快速路径。
- 不在读取时自动登记推导 Schema，不因冲突静默失效并重绑计划，也不引入闭合泛型历史账本。
- 不改变 Schema、DTO、Base/Delta、StateRevision 或 SchemaStore 持久格式，不增加公开 API。
- 不改变 provider 选择、整链预绑定、UpgradeContext/value tool 生命周期及异常种类。

## 4. 验收

- 缓存的 hidden middle owner 布局以及其 inline child 晚登记冲突，均在 callback 前失败；诊断包含
  owner edge、source/target 或字段路径、冲突 TypeExpr 与版本。
- value dependency 的晚登记冲突经过同一 plan 证书报告，未调用的已声明依赖也被验证。
- 同版本零步计划不能绕过复核；共享 exact DAG 去重且较长路径仍执行深度限制。
- 非 Upgrade 的 BindSchema/reader/value 缓存晚登记冲突回归保持通过。
- 根 solution build、相关测试及完整 solution tests 通过；`git diff --check` 通过。

## 5. 实施结果

- `ExactSchemaRequirementSet` 成为普通 binding 单根复核与 UpgradePlan 多根证书的同一算法；按 exact key
  保存稳定顺序和首条路径，同键异形立即报告两条来源。共享 DAG 的 subtree height 继续校验较长到达路径。
- UpgradePlan 保存 requirements、steps 与 current DTO CLR Type。缓存命中顺序固定为 requirement validation
  → requested DTO type validation → callbacks；零步计划也包含 source/current。
- owner endpoints 的闭包已覆盖所有 value dependency，因此删除了 UpgradeStep、ValueUpgradePlan 与
  UpgradeDependencies 上分散的 `CheckRegistered` 状态和递归协作；value plan 也不再保存仅供该检查使用的槽。
- repository mismatch 仍抛 `InvalidDataException`；消息包含 exact key 与依赖路径，inner
  `SchemaConflictException` 提供 registered/expected 两份完整 `DurableSchema`。
- `StateModelSnapshot` 注释明确区分冻结代码目录与实时单调 SchemaStore。本片未改变任何公开 API 或持久格式。

验收结果：根 `dotnet build DurableGraph.slnx --no-restore --verbosity quiet` 零警告、零错误；
完整 solution tests 1115/1115（Runtime/SG 539、StateStore 318、Storage 155、Serialization 103），零失败、零跳过。
相关 focused tests 21/21 与 16/16 通过；独立正确性审阅没有 blocker，提出的完整布局异常载荷与同键双路径回归已纳入。
`git diff --check` 与受影响 Markdown 本地链接检查通过。
