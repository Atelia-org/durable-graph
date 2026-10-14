# DB-016：策略之后的下一块对象内容纵切

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0016-next-product-object-content-slice.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：保留当时 codec-first 排序理由，后续形状转入 DB-018；不是今日的下一工作单或新增能力授权。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen — 用户已接受 codec-first；后续统一引用身份及生成器形状见 DB-018，尚未实现。
>
> 创建日期：2026-09-05
>
> 审查基线：`c8a98dc`，估算 DTO → 保存计划已提交。

## 1. 本轮问题与判断

用户先要求比较早期 core 的版本化 Schema、Source Generator history probe、单对象展平序列化，
以及其他适合叠加到产品代码中的层级，随后接受 codec-first。本分支保留当时排序分析；
具体类型范围、accessor、格式和后续引用/数组/容器要点转入
[DB-017](../0017-object-codec-design-points/README.md)，随后由
[DB-018](../0018-generated-graph-codec-shape/0018-generated-graph-codec-shape.md)推进到统一引用身份/TypeCodec/base-first 形状；
string 成员不再 inline 写值。尚未授权实现这些新能力。

**建议先做：当前 exact Schema、单对象直接标量字段的 Source Generator Base 二进制编解码及大小计算。**
它将已有 Schema/生成器与已有字节原语接通，提供真实 payload；随后优先接入 ObjectVersion 的持久内容。
这是一项范围与收益判断，不是说 Storage 必须先有 SG codec 才能开发。

最初建议的“直接字段”仅限现有 bool/int/long/string；用户随后要求先从支持的 CLR 基础类型做起，
具体扩展名单见 DB-017，不再把旧四类型范围当成最终施工约束。
用户已把 Durable 引用/循环、SG ref 元素遍历的数组、按内容重建的 BCL 容器列为后续目标，
首片仍不能宣称完成图展平。

## 2. 源码事实：哪些东西已经有了

| 能力 | 当前证据 | 对下一步的影响 |
|---|---|---|
| 版本化 Schema 描述 | [DurableSchema](../../../src/DurableGraph/Schema/DurableSchema.cs)已有 SchemaId、Version、有序 FieldId/TypeTag 和结构相等 | 源码没有名为 VersionedSchema 的类型；不需要为了名字重建一层 |
| 累积历史 shape 与生成类型 | [生成器](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs)消费 .dgsnapshot；[build targets](../../../src/DurableGraph/build/Atelia.DurableGraph.targets)与[发布工具](../../../src/DurableGraph.Build/SchemaHistoryTool.cs)已集成 Publish/Verify | SourceGeneratorHistoryProbe 的关键机制已进入主项目和 package；不应重新搬运 |
| 相邻版本升级 | 生成器产生历史 Snapshot、必需的 partial Upgrade 声明及运行时相邻调用链 | 业务转换函数体仍需手写，不是自动推导领域升级逻辑 |
| 历史读取 | [生成器测试](../../../tests/DurableGraph.Tests/DurableSchemaGeneratorTests.cs)与[当时的内存 Store 测试](https://github.com/Robird/durable-graph/blob/3f83dab70156927d854d82753a08fe2a419b8274/tests/DurableGraph.Tests/InMemoryStateStoreTests.cs)已验证历史读取升级、失败和无隐式回写 | 不再把“运行时升级尚未实现”的早期记录作为当前施工清单 |
| 现有对象序列化 | [当时的 IDurableSerializer](https://github.com/Robird/durable-graph/blob/3f83dab70156927d854d82753a08fe2a419b8274/src/DurableGraph/IDurableSerializer.cs)使用 boxed 字段字典；[当时的 InMemoryStateStore](https://github.com/Robird/durable-graph/blob/3f83dab70156927d854d82753a08fe2a419b8274/src/DurableGraph/InMemoryStateStore.cs)只保存 demo slot | 还没有对象二进制 Base/Delta |
| 图操作生成探索 | [DurableGraphOperationsProbeGenerator](../../../src/DurableGraph.Generator/DurableGraphOperationsProbeGenerator.cs)是未注册的 internal probe，生成 capture/equality/reference visit | 位于 src 不等于默认生成器已经提供图序列化；没有 bytes codec |
| 字节原语 | [Serialization](../../../src/DurableGraph.Serialization/BinaryPayloadWriter.cs)已有整数、Boolean、字符串等读写 | 可复用编码语义，但当前均 internal |
| 对象存储 | [StateRevisionWireWriter](../../../src/DurableGraph.Storage/StateRevisionWireWriter.cs)只写 membership；[StateRevisionStore](../../../src/DurableGraph.Storage/StateRevisionStore.cs)只重放 shallow heads | 真实 ObjectVersion 内容和 raw Base/Delta 链仍缺失 |

现有能力进入 src，并不意味着它们的 API、Schema authority、二进制格式或可靠性已经冻结。
`DurableSchema.GetHashCode()` 也不是持久 SchemaHash。

## 3. 候选排序及独立审查分歧

| 候选 | 新增的最小可观察能力 | 本轮建议 |
|---|---|---|
| 重做/扩展 VersionedSchema 或 history 工具 | 新 Schema authority 或更多历史能力 | 暂缓；已有消费者能用 typed Schema，history 机制已有源码和测试 |
| 当前 Schema 的 SG Base codec | 一个对象变成确定的真实 bytes，能计数和解码 | 用户已选择；具体 primitive 范围见 DB-017 |
| raw Base-only ObjectVersion 存储 | 两个对象内容跨 Revision/文件轮转后，重开并从指定 head 取回 | 最强备选，也建议作为 codec 之后紧接的存储切片 |
| 完整 raw Base/Delta chain | 从 exact object head 找到 Base 和有序 Delta 字节链 | 价值明确，但新增父版本定位、链验证、记录 framing 等契约，不当成第一步的最小版本 |
| fixed policy + 完整 Save | 真实 B/D/H 驱动持续 Delta 与 Base 重写 | 需要对象 codec、内容记录和 baseline/提交更新语义，稍后闭合 |

三路独立分析分别核查 Schema/history、codec、最小产品收益，再交叉质询。
意见并非完全一致：codec 审查者认为，若重点是沿现有 StateStore 自底向上叠加，
先做 raw ObjectVersion/chain 更顺，因为 SG codec 还涉及可见性和 package 接线；
Schema 审查者保留 codec-first，需求审查者在比较完整 rawchain 与 Base-only 范围后支持 codec-first。

主审选择 codec-first 的依据是：用户没有指定“尽快证明文件内容重开”为首要目标，
而现有四种类型的对象字节语义可以独立闭合；完整 rawchain 会同时引入更多存储语义。
raw Base-only 与 SG Base 是两条可交换次序的机制，不能靠投票或“下层永远先做”声称唯一正确排序。

## 4. 最初首切片边界及后续修订

下述是排序阶段的小切片建议；用户后续要求统一引用身份（含 string）、TypeCodec 与 base-first，
具体首片需按 DB-018 重定，不能将以下 sealed/scalar 范围当作否定新方向的约束。

要回答的唯一问题：

> 对一个当前 exact Schema，能否把对象的直接 durable 字段写成确定的完整 Base payload，
> 得到其字节大小，再从这些 bytes 恢复相同 durable 字段？

- 复用现有 sealed、top-level、partial、直接继承 DurableBase 的类型约束。
- 从支持的 CLR primitive 类型开始，名单与新增 Schema kind 见 DB-017；
  按 FieldId 决定字段次序，Transient 不进入 payload。
- 字符串的 null 编码必须显式决定：现有 TypeTag.String 不区分 C# nullable annotation，
  不能让相同 exact Schema 因注解不同产生不兼容的字节解释。
- 生成代码直接消费 typed 字段或冻结 Snapshot，不以 boxed 字典作为新二进制路径的长期中转。
- 大小计算与写出使用相同编码语义和同一份字段值；不要求首版无扫描、零分配或高速估算。
  对本切片，精确长度是容易检验的见证；策略接口仍允许近似估算。
- 首版只写当前 Schema，只读与该 codec exact 匹配的 Schema。可显式由调用方提供完整 DurableSchema，
  先比较身份、版本及全部字段 shape，再解码；不把裸 bytes 说成自描述格式。
- 接口形式、payload 中是否保留局部 format marker、计数实现等在获准后的具体设计中裁决；
  本分析不创建新 ISerializer 家族、registry、SchemaHash 或持久 SchemaStore。
- 首版不修改现有 history 发布协议，也不新增 binary 历史升级；现有 boxed 升级路径保持可验证。
- Base-only 不能完整提供 Update 所需 D/H；不得伪造 D、把 Update 改报 Insert 或修改已选策略来演示接入。

### 必须面对的真实接线成本

共享 Serialization 的 writer/reader/string codec 当前全部 internal，friend 只覆盖 Storage 和既有测试。
Source Generator 输出编译到消费者程序集，因此给 Generator 自己加 friend 无法解决消费者访问。

推荐在既有 Serialization leaf 中开放首个真实消费者所需的最小低层读写边界，
并验证 runtime/consumer 的引用与 package 依赖传递；最终开放哪些类型和成员留给实施设计。
不为任意下游维护 friend 名单，不在生成模板复制 VarInt/string 算法，也不为整洁另建程序集。
只验证仓库内 privileged test assembly 编译，不足以证明这条生成式产品路径已接通。

### 最小验收见证

1. 当前版本类型含非连续、乱序声明的 FieldId，以及 Transient 字段；声明顺序变化不改变 payload。
2. 独立手算 golden bytes 与读写 roundtrip 同时验证，不能只有同一 writer/reader 自证。
3. 大小计算等于实际输出；覆盖负整数及编码边界、null/空字符串、Unicode 与现有字符串编码语义。
4. 错误身份、版本、同 key 不同 shape 在解码前拒绝；截断、非法 Boolean/编码、尾随数据失败。
5. 字段恢复正确，完整验证后才构造最终领域对象；不扩大 Transient 重建或领域 invariant 保证。
6. 真正独立 consumer assembly 使用默认注册的生成器，能编译调用并执行新路径；
   若触及 NuGet dependency/build 接线，补本地 package consumer 验证。

## 5. 后续依赖与交换次序的条件

推荐 Base codec 之后立即验证真实 ObjectVersion 内容存取，避免继续在 generator 周边扩展历史工具。
可先用两个对象的 Base bytes 验证：同一 Revision 写入、后续 Revision 更新其中一个并继承另一个、
跨文件轮转、关闭重开、从调用方给定 exact head 取回各自内容。此处不需要自动猜 head 或引入持久发布 authority。

再根据真实消费者收敛单对象 Delta 编码与 raw chain 获取，随后接 fixed policy 和 Save 的 B/D/H：
Base 重置重建成本，Delta 累加，同一保存视图保持稳定，失败不能推进已提交 baseline。
这些是后续问题清单，本轮不冻结具体 record/Save API。

引用字段转 ObjectId、完整图 identity/reachability、binary historical decoder/upgrade、
Schema authority/SchemaStore 以及外层 publication，分别等对应消费者出现，不能默认为首片前置。

若近期最急需的是文件内容及恢复读取，交换前两片，选择 raw Base-only 即可；
测试里的手写小 codec 可长期作为独立 oracle，不必变成第二套产品序列化实现。
若 SG 首片开始扩大引用类型系统、history 或 package 框架，也应重新比较 raw Base-only 的更小范围。

## 6. 本轮证据

- 策略提交 `c8a98dc` 已完成，提交时工作树干净；此前根 build 与 40/73/65 个 StateStore 相关测试通过。
- 本轮重新运行 `DurableGraph.Tests`：147/147 通过，无失败或跳过，包含生成器、历史工具和早期图探针测试。
- 重新读取主项目、Probe README、共享原语及 Storage wire，未运行会发布历史的 Probe 脚本。
- 本轮只有分析文档更新，没有实现新的 codec、ObjectVersion payload 或 Save 接入。

相关边界：[DB-001](../0001-schema-authority-and-runtime-representation/README.md)、
[DB-005](../0005-durable-inheritance-flattening/README.md)、
[DB-015](../0015-statestore-object-representation-policy/README.md)、
[阶段 B 总览（归档）](../../../experiments/ARCHIVE.md#multi-segment "原路径：experiments/MultiSegmentStateStoreProbe/STATESTORE-SUBSYSTEM-DESIGN.md")。
