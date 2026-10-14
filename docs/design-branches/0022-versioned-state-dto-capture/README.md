# DB-022：Versioned DTO、Capture 与 DTO binary body

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0022-versioned-state-dto-capture.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：readonly Vn、current Capture 与 DTO body 的首片；bool/int/long 清单随后由 DB-023 扩充，正文记录原范围。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen / Implemented — 2026-09-05；基线 `8fb4d05`。
>
> 用户已选择捕获后的版本化状态作为保存管线输入，并授权本片设计实施。
> 取代 [DB-021（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/design-branches/0021-generated-primitive-body-slice/README.md")直接读写领域实例的 body 接口。

后续范围扩充见 [DB-023](../0023-scalar-schema-dto-slice/README.md)：13 种标量贯通 Schema/history/DTO，
本文的 bool/int/long 清单记录首片范围；DTO 所有权及接口合同保持。

## 选择与范围

领域对象负责运行中的行为；捕获的状态负责后续比较、估算和编码。
本片先闭合真实 SG 的 current Capture → immutable DTO → binary Write/Read，及历史 DTO 积累。
仍要求 SchemaOnly + GenerateBinaryBody 整条当前领域链启用，仅支持 bool/int/long。
默认 legacy boxed serializer 和未启用 body 的 SchemaOnly 行为保留。
领域字段仍沿用现有非 readonly 限制（DG0011）；DTO Capture 不再直接反序列化领域字段，
后续可按路径重新评估该限制及诊断措辞，本片不扩大领域类型 shape。

现有 .dgsnapshot 保存 FieldId/TypeTag/exact base，已足以重建这些历史 DTO；
继续由已有 publisher 积累 metadata，SG 每次从 accepted history + 当前定义再生成 V1..Vcurrent。
不保存另一套可编辑 DTO 历史或新增 history 格式。

## 生成合同

同一 `internal [new] static class __DurableBinaryBody` 内生成：

```csharp
internal readonly struct V1 {
    internal readonly int Segment0Field1;
    internal V1(int segment0Field1) { Segment0Field1 = segment0Field1; }
    internal static DurableSchema Schema => DeclaringType.GetSchema(1);
}
internal static VCurrent Capture(DeclaringType value);
internal static void Write(ref BinaryPayloadWriter writer, in V1 value);
internal static V1 ReadV1(ref BinaryPayloadReader reader);
// 每个版本各有自己的 Vn、Write 重载、ReadVn。
```

- DTO 内物理展平：从最上层领域 base 起，按声明层编号 Segment0..N；每层 FieldId 递增。
  同层内以 FieldId 命名，不保留源字段名；不同版本分别拥有自己的形状。
  DurableSchema 仍按声明层分段且绑定 exact 祖先，DTO 不另建 Schema authority。
- 每个 DTO 的静态 Schema 对应外层 GetSchema(n)，含完整 exact 祖先链；版本在 CLR DTO 类型中表达。
- 历史字段来自该版本的 accepted closure，不能引用今天的 base DTO 或旧 CLR 名称。
  当前版本只在当前模型与所有祖先 metadata/history 校验通过后生成。
  任一待生成版本的祖先闭包含 string 或其他不支持 kind 时明确 DG0020，整个该类型 DTO/body 拒绝生成。
  损坏/冲突历史沿用全局抑制，缺失/冲突/祖先版本未递增继续由已有校验拒绝。
- Capture 仅当前版本，null 前置拒绝；基类 private 字段由其自己的 Capture 读取，返回当前 base DTO，
  派生 Capture 复制相同 Segment 编号的基类值并读取本层 private 字段。Transient 完全忽略。
  scalar-only DTO 不持有领域引用；捕获后领域变化不影响 DTO。
  Capture 期间仍要求调用方提供稳定视图，不声明全图原子快照。
- Write 只收 `in Vn`，直接调用 byte primitives；移除领域对象 Read/Write 重载，不保留双路径。
  ReadVn 顺序读取 primitive，成功后构造并返回完整只读 DTO；异常可留下 Reader 已消费位置，
  但不会返回部分 DTO 或修改领域对象。赋值接收端在方法抛出时保留原值。
  writer 下游失败不回滚已写字节；最终 payload 边界仍由外层 EnsureFullyConsumed 检查。
- 字节顺序与 DB-021 的当前 body 相同：base-first/层内 FieldId；不增加 header、字段号、类型号。
  仅有显式 typed 历史 body，不实现运行时 exact schema decoder registry、自动升级或历史 DTO→领域对象。
- 只读 struct 是本片 scalar 状态的简单实现，不承诺未来引用/数组 DTO 的容器形状；
  不增加 IEquatable、通用 state interface、对象池或按 Type 查表。

## 所有权与完成标准

| 要求 | 所有者 | 验收 |
|---|---|---|
| DTO/current Capture/历史 typed ReadWrite、闭包校验 | generator agent | 静态输出、旧布局独立于旧 CLR 名称、所有拒绝路径 |
| 改造 DB-021 tests + DTO 行为测试 | tests agent | Capture 隔离、immutable、golden、失败、历史/继承/删除字段 |
| 真实 publisher→历史积累→再生成→执行 | 主代理 | 旧祖先 CLR 消失、版本差异、single PackageReference 消费 |
| 独立审查与最终集成 | reviewer + 主代理 | 根 build、全部产品 tests、package probe、diff、文档、提交 |

不在本片实现图 Capture、ObjectId 上下文、字符串引用、StateStore 比较/Save/基线安装、
DTO 升级/Restore、一般 struct/泛型/数组或新的 Schema kind。

## 后续管线约束

捕获完成后候选状态只读；保存比较、估算及编码应消费同一候选。
发布成功才可把实际提交的候选安装为 Parent 基线，失败继续使用旧基线；不能重新捕获领域对象充当提交结果。
加载升级的 DTO 与实际存储的 Parent 版本应区分。跨 Schema Delta 首片禁用/写 Base 仍是候选，尚未实施。
StateStore 的 DTO 容器与比较策略另片收敛，不把本片值类型列表当完整保存框架。

## 完成证据

- Generator 的 BinaryBody partial 基于既有 metadata 生成各版只读 DTO、current Capture 及 typed body。
  helper 接缝保持原开关，去掉领域 Read/Write；没有增加 history 格式、程序集、接口或 registry。
- DB-021 的 17 个行为测试迁移到 DTO；新增 6 个 DTO 行为 case 和 2 个 publisher 集成 case。
  验证 Capture 隔离、readonly/in API、Schema 同一实例、各版本字段删除/改型、空层、
  历史/当前间接 unsupported 依赖、read 失败无部分 DTO、write 部分失败及最终消费边界。
- 真实 SG → publisher → history → SG → 执行，在旧 CLR 祖先改名、祖先链替换后仍读写 V1 原始字节；
  历史文件保持原内容，history 输入顺序扰动和再发布后生成的 body 一致。
- 主代理 `dotnet build DurableGraph.slnx --verbosity quiet`：0 warnings / 0 errors。
  `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：456/456、无跳过，
  DurableGraph 249、Serialization 94、Storage 73、StateStore 40。
- PackageConsumerProbe 完整通过：仅一个 runtime PackageReference，Capture 后改变领域值，
  DTO Write/Read 保持 golden `012154`，DTO Schema 配对正确；既有 boxed/history 流程也通过。
- 独立只读审查无阻塞，建议的间接基类历史拒绝测试已补齐；产品工作集、包说明、相关设计与笔记已校准。
