# DB-023：标量 Schema、历史与 DTO 编码贯通

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0023-scalar-schema-dto-slice.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：13 种标量的已验收扩充分片；其 TypeTag 不是未来 TypeCodec 编号合同，当片没有 string 引用 Capture。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen / Implemented — 2026-09-05；基线 `4a27294`。
>
> 本片由用户授权的下一产品分片规划选定；延续 DB-022 的 DTO 保存输入方向。

## 问题与选择

字节层已支持 13 种标量，但 Schema/历史/DTO 只接通 bool/int/long。
选择先让现有能力贯通真实生成器与包消费者，下一片再处理 string 引用 Capture。
后者同时需要身份生命周期、引用上下文和对象列表等接缝，不适合混入标量扩充。

## 合同

- 保留 TypeTag 0 Invalid、1 Boolean、2 Int32、3 Int64、4 String；
  追加 5 Byte、6 SByte、7 Int16、8 UInt16、9 UInt32、10 UInt64、11 Char、
  12 Half、13 Single、14 Double。它们属于 Schema 字段 kind，不能当成未来 TypeCodec 的编号合同。
- Schema、生成器 history parser 与 build publisher/verify 接受相同集合；未知 tag 仍拒绝。
  history 文本语法不变，旧编号和既有文件不重写；旧工具会拒绝新 tag，消费新类型需要同步更新包。
- SchemaOnly、legacy Snapshot 共享类型识别与历史模型，均支持新增标量。
  DTO current Capture、各版本 Read/Write 与 exact 祖先闭包覆盖全部 13 种标量。
- 已知字段直接静态绑定 Reader/Writer。公开新增标量原语方法，char 提供 ReadChar/WriteChar，
  复用现有 UInt16 canonical varint，允许孤立 UTF-16 代理项。
- byte/sbyte 为单字节；其余整数沿用现有 canonical varint/ZigZag；
  Half/Single/Double 沿用固定宽 little-endian，保持负零及 NaN payload 位，不数值归一化。
- Half 按编译中实际 BCL 符号识别，不按短名或显示名接纳用户自制同名 struct。
- string 仍仅支持 metadata/legacy 路径；DTO body 对当前和历史 string 都明确拒绝。
  不扩大到 enum、nullable value、decimal、native int、Int128、自定义 struct、引用/数组/容器。
- DB-022 的 readonly DTO、Capture 稳定视图、读失败不返回半成品及继承规则保持。

## 实施与验收

| 要求 | 所有者 | 证据 |
|---|---|---|
| 类型识别、Schema/历史 tag 与 DTO body | generator agent | 映射一致，旧布局保留，未知种类拒绝 |
| 生成器行为及历史回归测试 | tests agent | 13 种字段、数值边界、浮点位、历史祖先、发布再生成 |
| 字节公开边界、真实包消费者及文档 | 主代理 | 无 friend 下游编译执行，旧包流程继续通过 |
| 独立审查与集成 | reviewer + 主代理 | 根 build、全部产品 tests、package probe、diff 检查 |

不增加程序集或全局 registry，不实现 DTO 比较/估算/升级、领域恢复、图 Capture、StateStore Save。
完成后更新工作集、索引与实验笔记，并提交可复验结果。

## 完成证据

- 生成器、运行时 TypeTag、publisher/verify 已贯通新增种类；DTO 接口和历史语法保持。
  无新程序集、注册表或额外 DTO 历史文件。
- 新增 15 个行为测试：13 标量独立 golden、整数边界、char 代理项、浮点位与 Capture 隔离；
  真实发布→接受历史→移除旧 CLR 祖先→历史 DTO 解码；同版本改型、未知 tag、伪 Half 和范围外值类型拒绝；
  legacy boxed 往返。现有 string 当前/历史/间接祖先拒绝测试继续通过。
- 主代理最终 `dotnet build DurableGraph.slnx --verbosity quiet`：0 warnings / 0 errors。
  `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：471/471，无跳过；
  DurableGraph 264、Serialization 94、Storage 73、StateStore 40。
- `./experiments/PackageConsumerProbe/Run-Probe.ps1` 全流程通过：一个 runtime PackageReference，
  实际 net10 参考程序集下识别 Half，全部新增标量保持固定 golden；既有 bool/int/long、boxed 升级及历史流程通过。
- 独立只读审查无阻塞项；diff 检查、65 个本地文档链接检查通过。
  产品工作集、包说明、设计索引和实验笔记已校准；下一候选为 string 引用 Capture 与最小对象列表。
