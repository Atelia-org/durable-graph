# DB-094：有限支持真实 property backing field 的前期调研

> 状态：**调研储备，范围与规模尚未摸清，未选为当前实施方案**。
> 调研基线：`b209a777c3c103108f95e0277241a127fb1cc7b4`；2026-10-15。
> 当前已选后续工作是 [DB-093 显式分类有效性检查](../0093-explicit-storage-classification/README.md)。本案不改变它的施工边界。

## 1. 要回答的问题

能否让普通 Durable class 明确标注的真实 property backing field 复用现有字段持久化机制，保留普通 class 的属性 API、相等性与继承语义，同时不增加一般 getter/setter 序列化协议？

这是正向能力扩展。它可以减少显式字段包装的建模成本，但当前已经证实的 H01 只要求“不能静默丢弃分类”；DB-093 的编译拒绝足以先完成该正确性闭环。

本案最小研究成功标准是：选定一个有限声明范围和生成路线，说明所有必改入口与会改变的模型/生成外观，再用隔离实例证明同一 backing 的 Capture、静态 body、Hydrate 与历史规则成立。在此之前，不按“一处过滤修改”估计施工规模，也不把 record 的现成回归算作 ordinary class 新资格。

## 2. 已有证据与它没有证明的事

### 2.1 Roslyn 确实暴露了可用的实际 storage

[共用 ExplicitStorageClassificationProbe](../../../experiments/ExplicitStorageClassificationProbe/README.md) 从 `.artifacts/h01-options` 迁入源码管理。它显示普通自动属性、get-only 与 C# 14 `field` 属性具有实际 `IFieldSymbol`，`AssociatedSymbol` 为 property，Field target 标注已附着。

computed property 无 backing；event 的 backing 不作为普通字段 symbol 暴露；主构造参数的错误 field target 不能因存在 capture 字段而被转移。这些结果支持一个小的 storage 白名单，不能推出“所有属性都能持久化”。

Probe 目前只观察当前 Generator，不包含普通 backing 支持补丁、运行期 backing 往返、完整 Commit 或新功能性能实验。其编译/Emit、Generator 准入与产品行为证据分别保边界。

### 2.2 Family 投影已有可复用机制

[GenericProjection](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs) 的 backing 分支按 `IsImplicitlyDeclared && AssociatedSymbol is IPropertySymbol` 识别实际存储，使用 MetadataName 与 UnsafeAccessor；这段判断不依赖类型必须是 record。class 用对象 receiver，inline struct 用 ref receiver。

record 的自动、get-only/init、private、C# 14 field-backed 等已有生成与恢复见证：[Generator 正负例](../../../tests/DurableGraph.Tests/RecordClassGeneratorTests.cs)、[record 图往返](../../../tests/DurableGraph.Tests/RecordClassGraphTests.cs)。它们证明基础机制存在，不证明扩大声明范围后不需新验收。

### 2.3 传统路径是实质接线边界

[UsesFamilyTemplates](../../../src/DurableGraph.Generator/DurableGraphGenerator.TemplateHistory.cs) 让 record 自动进入 Family，但普通非泛型 class 仅标量 backing 不一定触发。路由在 [Generator](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs) 的 compilation 层选择。

传统 [Capture](../../../src/DurableGraph.Generator/DurableGraphGenerator.GeneratedState.cs) 及 [Hydrate](../../../src/DurableGraph.Generator/DurableGraphGenerator.StateModel.cs) 仍有直接拼接源码字段名的分支；`<Value>k__BackingField` 不能作为普通 C# 成员表达式。仅放宽 GetDirectFields 不是完整支持。

## 3. 可行支持路线及代价

| 路线 | 机制 | 已知收益 | 不能省略的代价 |
|---|---|---|---|
| 自动选择 Family | 含新增 Durable backing 的 ordinary 声明触发现有 Family | 最大复用，不新增用户开关 | 该编译其他模型也可能切换 DTO/登记/Upgrade 引用外观 |
| 显式 Family opt-in | 要求已有 `DurableGraphGenerateDefinitions=true`，未开启时明确拒绝 | 避免无声切换，复用现有配置 | 同源声明能力依赖配置；不能让无关模型恰好触发 Family 决定是否支持 |
| 同时补传统 emitter | legacy 与 Family 都用真实 backing accessor | 可保传统 DTO 外观 | 两套 Capture/Hydrate 及相应正负例维护；目前无必须保 legacy 外观的具体需求 |

仅 Transient backing 不进入 DTO，不应仅因它触发 Family。无论选哪条路线，都要保 DB-093 的无目标/错误标注拒绝，精确放开新的受支持 storage。

当前较小的候选范围是“只接纳显式标注的真实 ordinary class property backing，未标 storage 保持现状”。它与“普通类所有隐式存储都必须分类”是两种选择，不能混在同一口号中。后者会改变当前 unmarked auto-property/event/capture 的准入。

## 4. 尚需摸清的范围

| 问题 | 已有依据 | 继续研究须决定/验证 |
|---|---|---|
| class 与 struct 是否同片 | Family 同时有 object/ref 投影 | 最窄可只支持 class；struct 的 inline/容器资格另计，不能因共用过滤而意外放开 |
| auto、get-only/init、C# 14 field-backed 是否统一 | 都可能有真实 property backing | 优先按实际 storage 判断；若按 accessor syntax 再限制，要有具体收益 |
| private/generic/inherited/hidden backing | declaring-layer/base projection 已存在 | 同库与跨库分别验证，不以 public 标量单例代替 |
| 未分类 backing 的政策 | ordinary 与 record 目前不同 | 显式 opt-in 或完整分类须独立裁决；保留合法旧声明的影响清单 |
| static、未知 capture、event | 0093 将明确拒绝或排除 | backing 支持不自动授予其他 implicit storage 持久语义 |
| helper 冲突 | 目前 record 有专门检查 | 普通支持必须接入对应保留名检查，不复制一套命名权威 |
| 生成外观迁移 | Family 是编译级选择 | 选真实消费者列受影响 DTO alias、登记、Upgrade 引用，评估编译级切换范围 |
| 属性公开值与存储值不同 | getter 可以返回加工值 | 坚持保存 backing 实际值；不执行 getter/setter/constructor/initializer |
| ImmutableLeaf | 当前所有 implicit backing 都保守 false | 不随本案扩大优化资格，独立问题独立举证 |

不新增 Property-target Attribute；继续显式 field-target。相同 FieldId/完整槽不因 backing 名或属性名变化而改变持久身份，未支持的字段类型也不因属性外观而获支持。

## 5. Schema、history 与现有 H01 数据

本案未必需要新的 wire/history 格式，但会改变被遗漏属性开始入 Schema 的模型布局。这两种影响必须分开。

H01 的 ordinary V1 history 只有 Control/Field2。新增 Value/Field1 后，同 ID/V1 应由当前 history 门禁拒绝；须正常升版并显式 Upgrade 定义旧缺值，不能改写 accepted V1 或把原意当作已保存数据。此前未保存的 42 无法通过新 Generator 找回。

若原来已有受支持显式字段，按相同 FieldId/完整槽改为 backing，则需用实验确认布局相同、history 不变；不能因为是本案就一律要求升版。属性重命名与 CLR backing 名不应成为持久 key。

## 6. 后续关键 Probe 预案

以下是有触发条件的研究问题，**不是已批准工单或已完成实验**。全部依赖先选定有限范围和生成路线。

| Probe | 最小问题与成功/失败判据 |
|---|---|
| 输入准入与路由 | 同一个普通 class backing，在默认/显式 Family 下得到确定结果；无关模型增删不使 Schema 静默少槽；若自动切换，完整列出同编译其他模型外观影响 |
| raw backing 往返 | 值 42→Capture/body→Hydrate 为 42；getter 返回加工值也不改变存储输入；getter/setter/constructor/initializer 计数证明不被调用 |
| 最小组合 | 标量加一个引用/inline/Nullable、泛型及声明层继承的必要见证；共享/循环不因 accessor 变化而破坏 |
| history 双故事 | 旧 H01 V1 同版新增槽拒绝，V2+Upgrade 成功；已有显式字段同布局换 backing/改名不伪升版，旧 history bytes 保持 |
| 普通包 | 新 G 的正常 PackageReference、独立 cache 下 write/cold-read/Delta续写；没有 ProjectReference/friend/手工 analyzer 绕行 |

当前的[共用机制 Probe](../../../experiments/ExplicitStorageClassificationProbe/README.md)只完成前置符号观察。后续若制作 candidate patch，应保隔离并记录其源码差异；不能将 patch 的单例通过写成整个产品能力已实施。

## 7. 重访条件与停点

DB-093 完成后，真实消费者若持续需要为保留 class 的公开属性、Equals 或继承语义，把大量自动属性改写为显式字段包装，可用该模型作为本案输入。届时先统计具体声明、生成引用和组合依赖，再选择上述路线和验收范围。

目前不估算工时/改动行数，不承诺只改过滤，不推进全属性、全隐式字段、runtime 反射审计或一般 getter/setter 协议。没有具体消费者或能闭合上述问题的小实验时，保持调研储备；本轮不因文档编号已建立而自动进入施工。
