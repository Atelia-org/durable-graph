# DB-021：实际生成的 primitive class body

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0021-generated-primitive-body-slice.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：当片曾直接读写领域对象；接口已由 DB-022 的 readonly DTO 路线取代，保留原合同与验证证据。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Superseded by DB-022 — 原片已于 2026-09-05 实现；基线 `f9cf2de`。
>
> 承接 [DB-018](../0018-generated-graph-codec-shape/0018-generated-graph-codec-shape.md) 和 [DB-020](../0020-typed-slot-array-binding-slice/README.md)。

后续校准：用户选择 Versioned DTO 作为保存输入，[DB-022](../0022-versioned-state-dto-capture/README.md)
已取代本文的直接领域 Read/Write 接口。GenerateBinaryBody 现生成 readonly Vn、current Capture 与 DTO body；
下文保留本片当时的合同和验证证据，不作为当前 API 用法。

## 问题与选择

真实 SG 能否复用已有 Schema 字段模型，直接静态读写 private 成员，并按声明层组合继承 body？
首片选现有 SchemaOnly class，避免同时引入 struct exact 内嵌类型表达及历史格式。
与自动为所有 SchemaOnly 生成 body 相比，显式开关保留已有 metadata-only 消费者的能力边界。

## 本片合同

- `DurableTypeAttribute.GenerateBinaryBody` 默认 false；true 要求 SchemaOnly=true。
  领域祖先必须都启用，仍受现有同编译、顶层、非泛型、非 record、partial class 约束。
- 本片 durable 字段只接受 bool/int/long；string 明确诊断，不 inline、不漏写。
  其他类型仍由既有字段诊断拒绝。Transient 不参与内容。
- 同一已验证的 DurableFieldModel 决定 Schema 和 body：base-first，各声明层内按 FieldId 递增。
  基类/派生重复局部 FieldId 合法，private 字段由声明类自己的 nested helper 访问。
- 生成 `internal [new] static class __DurableBinaryBody`，含
  `internal static void Write(ref BinaryPayloadWriter writer, DeclaringType value)` 和
  `internal static void Read(ref BinaryPayloadReader reader, DeclaringType value)`。
  明确调用基类 helper 后直接 WriteBoolean/WriteInt32/WriteInt64 或 Read 对应原语。
  无 Type 查表、ValueSlotCodec、逐字段委托/反射/装箱；继承 helper 名冲突使用诊断拒绝。
- body 处理其声明类型及祖先的当前布局，参数不要求 exact runtime Type；派生值可用于基类段。
  外层将来负责 actual Type/exact Schema 的记录绑定，不能把这些方法当历史 decoder。
  不生成 header、FieldId、版本号、长度或对象身份；同 primitive 的字节规则沿用现有 leaf。
- null value 在消费/写出前拒绝。读取原地更新已有目标，失败可留下先前字段/reader 游标；
  写失败也不回滚。外层负责未发布目标、稳定保存视图和 EnsureFullyConsumed。
- body 只在本身及祖先 metadata/history 验证成功时生成；缺失历史、同版本 shape 变化等不能得到可用 body。
  malformed/conflicting history 全局抑制 body，因为损坏输入不一定能可靠归属某个 Schema。
  不生成旧版本 body、Snapshot、Upgrade 或对象分配器。
- Runtime 引用现有 Serialization leaf，使下游程序集获得正常传递依赖。
  公开 Reader/Writer 类型、构造及本片所需 bool/int/long 读写、reader 边界检查/计数；
  其他方法继续 internal，随实际消费者开放。typed slot/数组模板仍 internal，不新增 friend。

## 所有权与验收

| 要求 | 实现所有者 | 验收 |
|---|---|---|
| opt-in/字段/链/history 验证及静态输出 | generator agent | 生成源码检查、诊断、有效链编译 |
| 实际生成程序集行为与错误 | tests agent | golden、private/Transient、局部 ID、排序、部分失败、null |
| 属性/字节公开边界/依赖及集成 | 主代理 | 非 friend 下游编译、根构建及产品测试 |
| 正确性和遗漏 | 独立只读 reviewer | 无未解决阻塞问题 |

开工根构建通过（0 warnings/errors）。完成需相关测试与根 solution build、独立审查、
工作集/笔记/设计索引校准及自主提交。
本片不扩充 Schema kind，不实现一般 struct、泛型、引用图、数组对象、历史 binary decoder、BCL 容器或 Storage Save。

## 完成证据

- Generator 的 BinaryBody partial 复用已校验字段模型和 GenerateSchemaOnly 返回的有效模型，
  DG0020 拒绝错误 opt-in、string、helper 保留名及不合格祖先。逐字段输出是直接 primitive 调用。
- GeneratedBinaryBodyTests 新增 17 个 case，通过真实 Roslyn 生成、编译、加载后执行；
  验证三层 private 字段加空层、局部重复 ID/排序、极值 golden、Transient、null、读写部分失败、
  trailing、祖先及损坏 history 门禁、当前 v2 body 与历史元数据区别。
- 主代理最终 `dotnet build DurableGraph.slnx --verbosity quiet`：0 warnings / 0 errors。
- 主代理 `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：448/448、无跳过；
  DurableGraph 241、Serialization 94、Storage 73、StateStore 40。
- PackageConsumerProbe 完整通过原有发布/verify/boxed 升级场景及新增静态 body 场景。
  消费者只有一个 runtime PackageReference，传递获得 Serialization，输出 `BinaryBody:012154:True`；
  没有 friend、手工 analyzer 或 AdditionalFiles 接线。
- 独立只读审查无阻塞；建议的损坏/冲突 history 回归测试已补齐并通过。
  产品工作集、包文档、DB-018/索引及实验笔记已校准。
