# DB-053：显式登记的 enum 内联状态

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0053-enum-inline-state-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：显式 enum 的单整数表示、组合/history 和 Upgrade 证据见 §8；枚举常量表未入 Schema，产品能力不据后续容器扩展自动扩大本片资格。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Implemented；G0–G3 实施及验收记录见 §8。
> 基线：DB-052 已完成；现状见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。
> 本文保留分片的问题、选择理由、实施合同与验收；长期约束已归入目标设计。

## 1. 为什么选择这一片

当前保存、同实例续存、stored-exact 解码、显式 Upgrade 与恢复主链已贯通，
字段/泛型/数组/List 已复用可组合值操作，Nullable 也已闭合。
本片补齐显式登记的用户 enum，使状态、类别和 flags 等普通领域值能直接进入这条主链。
这是面向典型模型的能力选择，不代表已有某个应用因为 enum 而被阻塞。

| 候选 | 判断 |
|---|---|
| enum | 内容为一个整数，可复用 inline Schema/history、静态 body、值升级与容器元素路径；需要先明确数值与枚举声明的历史合同 |
| Dictionary/Set | 有价值，但还需独立裁决 comparer、key 等价性、恢复索引时机及 key Upgrade 冲突；不借这一片预设答案 |
| 新应用接入 | 能提供需求证据，但目前没有指定应用和轨迹；本片用真实包消费验证公开入口，不新造业务应用 |
| 开放模板持久化、更多 List 优化 | 当前没有净删除机制或测量问题触发，沿用路线图的重访条件 |

**最小成功见证：**标记 enum 同时出现在字段、泛型、Nullable、数组和 List 中；
跨进程保存/冷读保持原整数，显式升级后强制 Base，再保存无伪变化；
删除旧 enum CLR 声明后，保留 history 仍能解码旧 DTO 并完成 owner 控制的转换。

## 2. 关键选择：有名的单整数内联布局

用户这样声明，复用现有属性：

```csharp
[DurableType("game.character-mode", 1)]
public enum CharacterMode : byte {
    Idle = 0,
    Moving = 1
}
```

- 必须显式 DurableType，具有稳定 DefinitionId 与正版本；首片支持同编译、顶层可访问 enum。
  enum 不要求也不能补 partial；file-local、CLR nested enum、未登记 enum、任意 BCL enum 不自动开放。
  支持 public 与默认 internal 可访问性；常量上误用 DurableField/Transient 明确诊断，不把常量声明当作实例字段。
- 支持 C# 的八种整数底层类型：sbyte/byte、short/ushort、int/uint、long/ulong。
  普通枚举、Flags、别名、负值、未命名数值和未定义 bits 均按底层数值原样保存恢复。
  不调用 Enum.IsDefined，不按当前成员表拒绝历史值，不规范化 flags。
- nominal 仍为 `TypeExpr.Named(DefinitionId)`；exact 定义仍是 `SchemaKind.InlineValue`、arity 0。
  定义仅含一个合成持久字段：FieldId=1、TypeTag=底层整数 tag。它不是反射读取 CLR 的 value__ 字段。
- 使用普通 Family 的版本化单字段 DTO（字段命名沿用生成器，如 Segment0Field1），而非裸整数充当 enum 的完整 DTO。
  不同 enum 保持不同 nominal 身份与生成 Family；`Box<Mode>` 不与 `Box<byte>` 合并。
  enum DTO 不持有当前领域 enum CLR Type，因此历史 body 不依赖旧 enum 声明。
- 不增加 Enum TypeTag、SchemaKind、TypeExpr 构造或特殊对象类型；无 ObjectId、无独立 live 行。
  inline Schema 仍可作为目录元数据依赖登记，不能作为对象 Base 表示使用。

### 2.1 为什么不选另两种表示

直接擦成底层整数虽然能读写，但会丢掉泛型 nominal 身份与独立版本依赖；不是本片推荐方案。
新增 enum 专用 Schema kind、常量表和独立 codec 可以保留更多声明信息，但会新建持久合同与升级分支。
目前恢复 DTO 只需要已知整数布局，推荐先复用现有 inline 机制。

这一选择有明确代价：**Schema/history 不保存枚举常量表，不检测常量名、常量赋值、别名或 Flags 属性变化。**
它记录可持久化的表示，不替用户验证业务含义。

| 变化 | 推荐合同 |
|---|---|
| 增加名称、别名、改名、修改 Flags 属性 | 不自动改变 exact Schema；旧整数仍原样有效 |
| 改写已有整数的业务解释、需要迁移旧值 | 用户显式递增 enum 版本及受影响 owner 版本，并提供转换；框架不能从常量表猜测意图 |
| 改变底层整数类型或宽度 | exact 布局改变，必须升版；同版本修改由已有 history/Schema 冲突机制拒绝 |
| 同 DefinitionId/version 从 enum 映射到同单字段布局 struct | 不额外禁止；历史表示相同，当前投影不同。不存在持久 enum/struct 品种标记，也不保证这种改写后生成 API 外观不变 |

可以在底层布局不变时主动升版；版本本身进入 exact 依赖，因此 owner 仍须按已有规则升版。
若需要“枚举常量表变化也必须被 history 自动检测”，应在采纳本片前改选声明合同，而非施工时偷偷加入。

## 3. 生成与绑定形状

源码调查确认：当前障碍主要在 current 领域投影，既有持久格式能够表达上述布局。

| 接缝 | 实施前事实 | 本片变更 |
|---|---|---|
| [DurableTypeAttribute](../../../src/DurableGraph/DurableTypeAttribute.cs) | 仅 class/struct | 增加 Enum 目标 |
| [SG 入口](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs)、[声明验证](../../../src/DurableGraph.Generator/DurableSchemaGenerator.Ancestry.cs) | 仅 TypeDeclarationSyntax，要求 partial，按实例字段生成模型 | 接受 EnumDeclarationSyntax；单独构建合成字段模型，不枚举常量为持久字段，不注入 enum 成员 |
| [Family factory](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericFactories.cs)、[领域投影](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs) | current factory 查领域类型内的 __DurableCreateCurrent；已知 inline 字段引用领域内 projection | enum 使用外置强类型 projection/current factory；统一 helper 名称选择的局部接缝，不能给 enum 发出 partial 类型 |
| [StateDefinitionBinding](../../../src/DurableGraph/Runtime/Binding/StateDefinitionBinding.cs) | 明确拒绝 domainTypeDefinition.IsEnum | 仅为已显式登记、arity 0、单整数当前模板的 inline enum 放行；绑定阶段核对真实底层类型与当前 exact 模板 |
| [模板/history](../../../src/DurableGraph.Generator/DurableSchemaGenerator.TemplateHistory.cs)、[构建工具](../../../src/DurableGraph.Build/SchemaHistoryTool.cs) | 已有 inline kind、底层标量字段和版本传播 | 复用现有语法；让涉及 enum 的生成使用 Family 路径，保留历史单字段 reader |

Capture 将 enum 强类型转成底层整数，再构造该版本 DTO；Hydrate 将 DTO 整数强类型转回当前 enum。
不需要 DynamicMethod、反射访问枚举内部字段、Convert 装箱或每元素 Type 查表。
已知字段直接调用外置 helper；开放泛型经现有 IValueProjection/IStateOps 静态约束闭合。
外置 helper 的可访问性须匹配领域 enum，不能用 public projection 暴露 internal enum；纯 DTO/history 仍不引用领域类型。

DTO 的 Base、StateEquals、PrepareDelta、ApplyDelta、VisitReferences 全部复用已有单字段 inline body；
不为省一个子 Delta 标记另造 enum patch。Nullable 自动包装此 exact inline child。
Family 选择采用可由现有材料证明的规则：

- 存在当前 enum 时，该编译进入 Family 路径。
- 有当前 Durable 声明，且保留了没有当前对应声明的 InlineValue history 时，也进入 Family 路径。
  这是按历史恢复需要选择路径，同样适用于删除的 struct；不从“单整数 shape”猜测它以前是不是 enum。
- 现有泛型/Nullable 等形状和显式 owner/value Upgrade 触发照旧。
  零当前 Durable 声明的纯 history 编译继续使用已有显式值规则入口，不新增自动生成全部历史的模式。

enum→同 ID struct 等 CLR 外观改写只保证持久解释等价，不能同时保证生成 API 永久稳定。
无 enum 标记的 history 无法分辨两个同 shape 的来源；不为掩盖此边界新增持久标记，
也不据此强制所有无关编译迁移旧普通生成路径。G0 应单独验证上述判据及现有普通路径回归。

继续写 history v6、SCB1 v2、Base v4、既有数组/List codec；**不因支持新 CLR 投影而升格式版本**。
已接受 history 文件保持原 bytes/hash，不增加旧磁盘格式兼容器。

## 4. 组合、版本传播与 Upgrade

- 支持 E、E?、E[]、rank 2–4 数组、List<E>、List<E?>、包含 E 的 struct、泛型 class/struct，
  以及受支持构造的递归组合。数组/List 的引用身份、冻结、Delta、Remove 和共享规则不变。
- `Box<E>`、声明为 `T` 的槽闭合 E、`T? where T : struct` 闭合 E、
  `where T : Enum`、`where T : struct, Enum`、`where T : unmanaged, Enum` 的合法实例需要生成/运行验证；
  不顺便开放 System.Enum/object/interface 持久字段。
- 固定 E 字段记录 E exact 版本；该版本改变沿 inline/base 传播。
  数组/List 引用边截断传播，但对应容器自己的 exact 元素布局改变，并由它作为 owner 处理升级。
  phantom/nominal 参数继续不被强制索取无关 exact 表示。
- enum 不自行自动升级。普通 owner 显式构造下一版 DTO，或声明已有值工具并调用；
  单字段 enum Family DTO 可直接作为 DurableValueUpgrade 的源/目标 DTO。
- 数组/List 使用已有显式元素规则集；Nullable 的提升仍需 AllowNullableLifting。
  enum 同底层类型但 exact 版本不同不构成 KeepExact，不默认为恒等转换。
- 空数组、空 List、absent Nullable 仍在业务回调前绑定所需能力；错误显式 provider 不回退。
  cached plan 对完整 exact 依赖的复核继续包含 enum 版本，不新增缓存旁路。
- 历史 enum CLR 改名或删除后，旧 DTO/body 只读历史材料；当前映射仍需明确登记当前 enum/等价 inline 模型。
  框架不按名称或当前常量表重解释旧整数，Upgrade 保持单 owner、无图访问/新 ID。

## 5. 施工步骤与分工

实施沿以下依赖顺序完成；没有缩减组合或历史支持，验收结果见 §8。

1. **G0：验证最小生成接缝。**用一个 byte enum + 单字段 owner、删除 enum 后仍有当前 owner、以及显式值规则的纯历史入口，
   验证合成 inline 模型、外置 projection、Family 选择与 retained history reader；
   明确当前 enum factory 的模板/底层类型校验。保持现有格式，无新持久品种标记。
2. **G1：生成与 Runtime 登记。**完善八种底层类型、诊断、泛型/Nullable/容器组合及当前/历史绑定。
   单一 agent 拥有 SG 文件，另一 agent 可独立处理 Runtime 登记校验与 focused tests；Shared/Build 变动统一协调。
3. **G2：历史与 Upgrade。**验证固定/泛型依赖传播、保留文件/hash、旧 CLR 删除、owner/值/容器升级及完整缓存复核。
   不重写已合法的原有 history，也不把未标记 enum 的负例改成正例。
4. **G3：真实包与集成验收。**独立 agent 构建多版本消费者及可执行断言；主线程串行根 build/tests/包流程，
   另一 agent 审查语义边界和集成 diff。通过后更新状态、目标与路线图，并按需提交。

无需另立程序集、通用 enum registry、生成后端或升级系统。遇到更多 CLR 种类仍另行选择。

## 6. 验收矩阵

| 类别 | 最小可观察断言 |
|---|---|
| 数值与格式 | 八种底层类型极值、负值、别名、未知值/flags bits 原样恢复；独立 Base/Delta golden，截断/非法已有 body 结构拒绝；数值相同无伪 Delta |
| 强类型与身份 | 同底层的两个 enum 不合并 nominal/Family；E 与底层整数不混同；泛型、Nullable、数组/List 递归闭合；不逐槽装箱查表 |
| 声明与历史 | public/internal enum 无 partial 正常，public owner 的 private readonly internal-enum 字段可用；未标记/nested/file-local/常量误标持久属性明确诊断；同版改底层类型拒绝；只改常量表的 history 不变是显式正例 |
| 版本传播 | E V1→V2 导致固定 inline owner/祖先依赖升版；容器引用 owner 不因元素升版自动传播；同 layout 新版本也需显式 Upgrade |
| 严格绑定 | Runtime 手工登记的 enum/模板底层类型不符拒绝；错误 kind/arity、不同 exact 版本及 late dependency 冲突在 callback 前拒绝 |
| 图与持久性 | 正式 GraphSession 同实例 Commit、无变化提交、实际 Delta、冷重开；prepared bytes 不随领域值/集合后续修改变化；升级强制 Base 后再次提交无变化 |
| 跨版本真实包 | V1 保存 E 字段/E?/数组/List 和泛型；V2 删除旧 CLR enum、同 ID 新 CLR enum 改底层类型并显式转换；旧 exact 解码不执行业务，当前 Load 才升级；共享容器只升级一次；空/absent 仍验证缺失工具 |

真实包只用 PackageReference、分开进程与构建、Publish/Verify history；检查旧 history 原文件/hash保留。
至少重跑受影响的 Generic/Nullable 包回归；是否追加其他包以实际生成路径变更决定，避免机械重跑全部实验。

## 7. 规划轮评估与采纳记录

规划轮仅依据源码定位，没有运行 enum 实验；实施事实与验证记录见 §8。
独立设计审视同意“单整数 inline 表示”能复用历史 reader；最大接缝是 enum 无法容纳现有领域内生成方法。
独立审查发现不能从单整数 history 反推原 CLR 声明是不是 enum；§3 已据此改为明确的 Family 触发规则，
不承诺任意 CLR 外观变更后的生成 API 恒定。实现前需用 G0 证明该规则与外置 projection。

用户已采纳 §2.1 的语义选择：**按整数布局版本化，不把枚举常量表纳入 Schema 不变性。**
这项选择让重命名/增加别名无需迁移；其代价是业务数值含义变化由作者显式负责。
将来若需要框架自动保护常量表，应独立重访该合同，不能把这项差异当作编码细节。

## 8. 实施账本

2026-09-09 用户采纳完整分片及常量表边界。仅实施本文 enum 能力；不新增格式、容器、通用生成后端或升级机制。
进入实施前工作树仅含上一轮本设计及三个导航文档的改动；纳入本片保留。

| 合同 | 实施位置/所有权 | 验收 |
|---|---|---|
| G0/G1 enum 合成模型、外置投影、Family 触发及诊断 | Generator agent / DurableSchemaGenerator 各 partial | EnumGeneratedTests，历史孤儿与普通路径回归 |
| 显式 current enum 底层模板校验 | Runtime agent / DurableTypeAttribute、StateDefinitionBinding | EnumDefinitionBindingTests |
| G2 history 不变性、依赖传播、显式升级及缓存复核 | History agent / 新历史与升级测试 | EnumHistoryTests、EnumUpgradeTests |
| 八种底层数值与 canonical Base/Delta | 主线程 / 新 body 测试 | EnumStateBodyTests |
| G3 跨版本保存/升级/续存 | Package agent / EnumConsumer、runner | 真实 PackageReference 两进程见证 |
| 集成审查、根检查、相关包与文档 | 主线程与独立 reviewer | 完成后记录结果 |

当前 Runtime 签名与历史 schema 结构沿用既有类型；enum 当前模板核对只约束最新版本，不能以当前宽度拒绝历史 inline 布局。
所有 dotnet 构建、测试和包命令由主线程串行调度，避免共享输出目录锁冲突。

### 实施结果与验证证据

- Generator 新增局部 `DurableSchemaGenerator.Enums.cs`，共用既有 Family DTO/body；
  已知 inline 字段集中选择领域内或 enum 外置 projection。public/internal、readonly、三种 Enum 泛型约束、
  rank 1–4/jagged/List/Nullable 组合均实际生成、绑定、保存和恢复。
- Runtime 仅在 DurableTypeAttribute/StateDefinitionBinding 增加 enum 目标及当前单整数模板校验；
  Shared、Build、SchemaStore、Storage 和 Serialization 无需修改。history v6、SCB1 v2、Base v4、容器 codec 不变。
- 枚举常量的持久属性误用沿用 DG0009；非法形状沿用 DG0001，未登记 enum 槽沿用 DG0007。
  同版改变底层类型拒绝；常量名称/赋值/别名/Flags 改动后 history 文件与 hash 不变已有正例。
- 基线根构建 0 警告/0 错误、1756 tests；最终根构建 0 警告/0 错误，**1828 tests 全部通过**：
  Runtime 1039、StateStore 531、Serialization 103、Storage 155；新增 72 项。
  最终日志位于 `tests/DurableGraph.Tests/obj/db053-full-final.log`，ignored，仅供本地复核。
- 新 body 测试独立给出八种底层整数 Base/Delta golden，覆盖极值、未知 bits、截断、padding/冗余拒绝及 nested 消费边界。
  无变化 inline PrepareDelta 可保留零位图，但不能作为声称有变化的 nested patch Apply；测试遵循既有严格合同。
- 根回归只需更新两处旧外观断言：DurableType 属性目标增加 Enum；孤立历史 inline 使旧 struct 测试进入 Family，
  改 DTO 别名，保留旧私有两参数 adapter 和 `10→11→12` 断言，未删除旧历史能力测试。
- 独立只读审查两轮未留下阻塞项，检查了 projection、完整布局/版本边界、失败预检及真实包实际断言。

真实包共用本轮八包 feed：
`experiments/PackageConsumerProbe/obj/enum-20260909144857-36328-7e13a004/feed`，
版本 `0.0.0-enum-e2e.20260909144857.36328`。每条 lane 使用独立消费者目录/cache/history。

| 真实包 | 结果与证据 |
|---|---|
| Enum | `enum-20260909145236-47452-fb0d441b`；V1/V2 均通过，history 4→8 且旧文件 SHA256 保留；39 个 present enum 转换按五个 owner 计数，五 Base→NoChange→实际 List Delta；旧 CLR enum 删除、exact 读取零业务回调与冷重开验证 |
| Generic | `generic-20260909145304-36952-345adbf7`；原三代见证、MissingClosedUpgrade、删除旧 inline CLR、相邻 UpgradeContext 与续存均通过 |
| Nullable | `nullable-20260909145512-18176-1428061a`；原两代提升、共享/循环、历史读取、升级 Base→Delta、清空后循环岛退出均通过 |
| InlineStruct | `inline-struct-20260909145707-39452-27fdfd17`；V1/V2 保留原普通路径，V3 删除 inline CLR 后按本片规则改用 Family 别名/登记；三代业务结果、旧 exact DTO、引用与成员移除断言保留并全部通过 |

本轮集成还纠正了 EnumConsumer 的初始升级签名：非泛型 World 的 provider 使用具体历史 DTO 端点；
Box/Cell 继续使用通用泛型方法。既有 closed-provider 合同不因 enum 扩大，没有添加回退或反射业务转换。
活动 InlineStructConsumer 的 V3 外观迁移单独经过 diff 审查；没有为保留旧别名而增加第二套历史读取机制。
文档检查覆盖 9 个 Markdown 文件、465 个本地链接与 47 个锚点，全部通过；暂存 diff 空白检查通过。
