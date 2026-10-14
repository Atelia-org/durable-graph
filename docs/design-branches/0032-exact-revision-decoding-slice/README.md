# DB-032：完整 exact-version 对象目录冷读

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0032-exact-revision-decoding-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：当片自动 reader 分派与完整目录冷读证据见 §8；原 typed reader/LoadedWorld 路径已退出，当前读取合同由 DB-087 承接。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

状态：**Chosen / Implemented**；2026-09-07。§1–7 保留提案时依据，当前实施合同与结果见 §8。
提案基线：`39624f6`，DB-031 已完成；当前事实见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。

## 1. 问题与最小结果

DB-031 已能从持久 Base 取得 exact Schema，但应用仍须逐对象指定 ID 和强类型 reader。
本片要回答：只给指定 Revision、SchemaStore 和应用登记的模型族，能否自动读回该 Revision
完整的 stored-exact DTO/string 目录，并在交付前验证全部 string 引用？

最小成功判据：真实关闭重开后，两个异构模型族、混合历史/current 版本及共享 string 的全部
live 对象都由文件中的类型信息分派；应用不再逐对象选择 ReadVn/ApplyDeltaVn。
任何晚期坏 body 或坏引用导致整个调用失败，不交付部分目录，也不修改文件或 CaptureSession。

这里的“完整”是指定 Revision 声明的完整 live membership；不是证明 roots 可达闭包，
也不是证明数据来自已发布 branch。调用方仍显式选择地址及对应 Repository 的两类 Store。

## 2. 为什么选这一片

| 候选 | 收益与代价 | 本轮建议 |
|---|---|---|
| 完整 exact DTO 目录与自动 reader | 组合现有历史 body、持久类型和 live map；形成升级与加载共同需要的输入，新增边界较小 | 优先 |
| 新 DTO Upgrade | 直接推进模型演化，但当前仍需手写异构 reader 分派；若并做则增加升级函数协议、失败与重写义务 | 接目录之后评估 |
| roots / Restore / WorkingTree | 更接近应用 Commit；会同时涉及根格式、构造/填充、实例 ID 接续、高水位及发布裁决 | 拆为后继分片 |
| 自定义 struct | 可独立推进值布局、Schema 依赖传播 | 保留路线图，不作为本片前置 |

不将上述排列冻结为整个 MVP 的强制流水线。完成本片后重新按证据选片。

## 3. 当前可复用接缝

- [StateRevisionStore](../../../src/DurableGraph.Storage/StateRevisionStore.cs)：
  ReadLiveObjectHeads 提供有序完整 membership；ReadObjectVersionChain 按对象逐链验证 prior，返回 owned bytes。
- [TypedObjectVersionReader](../../../src/DurableGraph.Persistence/TypedObjectVersionReader.cs)：
  Base 的完整 Schema 匹配先于该对象 body callbacks；同版逐段读取、全消费，string 禁止 Delta。
- [SG BinaryBody](../../../src/DurableGraph.Generator/DurableSchemaGenerator.BinaryBody.cs)：
  已为每个受支持模型的 V1..Vcurrent 生成 Schema、ReadVn、ApplyDeltaVn 和 ValidateStringReferences。
  历史祖先可不保留 CLR 定义；但整个模型族若已从当前源码消失，不会仅凭 Schema 日志自动生成 reader。
- [CapturedObject](../../../src/DurableGraph/CapturedObject.cs)：
  已有 exact Schema、owned boxed unmanaged DTO、复制返回 GetState 和 string 内容的行表示。
  [CapturedGraph](../../../src/DurableGraph/Runtime/Capture/CapturedGraph.cs) 另有 roots 与 Capture 含义，不能伪造空 roots 充作读回图。
- [StringReadTable](../../../src/DurableGraph/Runtime/Capture/StringReadTable.cs)：
  已定义每视图身份及零 ID/null、Empty 规则；当前仅接受 raw bytes，统一读取需要小的建表接缝。

## 4. 推荐设计

### 4.1 明确登记模型族，自动选择历史版本

SG 为每个启用 GenerateBinaryBody 的模型族生成一次登记适配器，把它的全部可用 Vn reader
登记到局部读取目录。应用在组合处明确列出模型族，读取时按 Base 中的 `(SchemaId, Version)`
查找。示意如下，名称与签名留待施工收敛：

```text
Character.__DurableBinaryBody.RegisterReaders(readers)
Item.__DurableBinaryBody.RegisterReaders(readers)
decoded = ReadRevision(stateStore, schemaStore, revisionAddress, readers)
```

目录只持有代码能力：exact Schema、typed Base/Delta reader、typed string validator。
持久 SchemaStore 仍拥有定义事实，目录不能覆盖它或令未知版本回退 latest。
string 使用预制内建路径，无需登记用户 Schema。

推荐重复登记同一稳定生成 binding 幂等；同 key 的另一 binding 明确拒绝，即使声明的 Schema
相等，也不按委托相等或最后写入者猜选 codec。同 key 不同 Schema 同样拒绝。
读取期间使用固定目录，不允许动态补登记或回调改变已选绑定；具体采用冻结副本还是构造后只读，
选择最小实现即可。不是进程级静态注册表、程序集扫描、反射造 reader 或一般 TypeCodec。

### 4.2 分层与静态 body

Runtime 放不依赖存储地址的读取 binding，泛型实现内部以局部 TState 完成 Base 和所有 Delta，
最终只将结果 DTO 装箱一次。SG 绑定已有强类型静态函数；字段与已知元素继续直接调用字节原语，
不增加成员级 Type 查表或 callvirt。动态选择只发生在对象/版本边界。

StateStore 负责 Base 包头、SchemaStore 查询、SchemaKey 索引和原始链的协调。Runtime 不引用
StateStore/Storage，SG 生成代码也不要求用户模型项目新增 StateStore 依赖，不增加程序集。
现有 typed 单对象入口与新批读共用链应用规则，避免两套 full-consumption/Delta 语义。

施工时有一个已知机械选择：现有 body 暴露 ReadOnlySpan，而不是 ReadOnlyMemory。
推荐以 Count + 按索引取得只读 body span 的小型 source 接缝适配已有 owned chain，先剥 Base 头，
再交给 Runtime 泛型循环；仅在同步读取期间借用，不泄漏 pooled lease。
不要为了传参将每条 Delta 重新 ToArray，也不要把 ApplyDelta 擦除为 object→object 而每段装箱。
若有更短且同样保持类型/所有权的实现可替代，不为该接缝建立通用 visitor 框架。

### 4.3 对象优先读取，整目录交付

1. 取得指定 Revision 的完整 live map，按 ID 遍历，保持 DB-028 的 object-first 方式。
2. 对每个对象读取完整原始链，解释 Base kind/key。durable 在调用该对象 reader 前，
   从 SchemaStore 取完整定义并与登记 binding 比较；之后重建 stored-exact DTO。
3. string 验证 Base-only 并解码一次。每个 ID 仅拥有一个解码实例；不同非空 ID 保持独立实例，
   所有 Empty 仍指向 string.Empty。建立 StringReadTable 和结果行时复用同一实例，不能再次解码。
   为现有表增加最小的受控构建接缝即可，不要求缓存整个 Revision 的 string raw payload。
4. 完成全部对象后，调用各 durable 行对应版本的生成 validator，在同一目标 Revision 的
   StringReadTable 中校验引用。零为 null；不存在或指向 durable 的 string ID 均拒绝。
5. 全部成功后，一次返回只读结果；原始链可逐对象释放引用，不必保留整个目录的全部 raw chains。

后排未知 Schema/坏数据允许在前排 DTO 已经内部解码后失败。保留的是“本对象 Schema mismatch
不执行本对象 body”，并不增加“任何错误使全批 callback 零执行”的要求。
生成 body/validator 为纯读取；框架不承诺回滚调用者自定义恶意 callback 的外部副作用。
无部分交付指不返回半份结果，也不通过流式枚举、逐对象通知或外部共享集合暴露候选。

引用经目标 Revision 而非 owner Base 的历史 Revision 校验。目录读取覆盖所有 membership 行，
不因尚无 roots 或未来升级可能删边而跳过当前坏引用；current 可达性由后继加载/保存层处理。
空 live map 返回空目录，不把不存在/非法 Revision 地址当作空图。

### 4.4 结果是历史读取视图

推荐独立只读容器 `DecodedRevision`（暂名），含所读 RevisionAddress、按 ID 排序的完整对象行、
ID 查询与相同实例的 string 解析视图。行可复用 CapturedObject 的内容表示以减少重复 DTO，
但应修正其 XML 注释说明读取用途；不附 current preparation binding、不公开任意 boxed DTO 构造器。
具体容器名称和可见性以真正调用点为准，不把文档示意当长期 API。

所有输出都独立于原始 buffers、可变输入集合及 Store 生命周期；DTO 只能复制取出。
集合防护也检查非泛型接口，避免 Array.AsReadOnly 经 ICollection.SyncRoot 暴露底层数组。

该结果保留 stored exact Vn，可能混有不同对象版本，**不是可编辑 current DTO 基线**。
长期可编辑路径仍须完整重建后 Upgrade 为 current、处理 RequiresRewrite、Restore/身份导入。
本片不能直接把结果安装为 CaptureSession.Current，也不通过伪造 roots/领域实例跳过这些步骤。
RevisionAddress 只标明查询视图，不是发布凭据或允许与任意 Repository 混配的身份证明。

## 5. 范围与后继

本片包含 Runtime 读取 binding/必要内容建表接缝、SG 模型族登记适配器、StateStore 完整目录读取、
生成集成 tests 和实际包消费者。支持范围继续是现有标量/string 及同编译 class history。
Storage wire v3、Base 类型头和 Schema 日志格式保持现状；不引入新的持久字段。

本片不包含 Upgrade、RequiresRewrite 的保存接续、roots 持久化、领域实例构造/填充、
CaptureSession 导入、ID 高水位恢复/回收、WorkingTree/Commit/Ref、一般引用、struct、数组/BCL，
也不新增恢复承诺或 Frame/map cache。

完成后优先评估“exact 目录 → current DTO 升级与重写义务”，再与 Restore/根绑定及工作会话需求
一起选下一可观察闭环；这些尚未授权实施。未实现项的长期维护位置仍为[路线图](../../DurableGraph-research-roadmap.md)。

## 6. 施工顺序与验收

推荐先由主代理收敛跨层 binding/结果形状，然后分派 Runtime、SG、StateStore 三项。
接口落定后再接生成集成和实际包消费；独立 reviewer 检查类型匹配、引用身份、失败交付与依赖方向。
dotnet 验证集中串行运行，不让子任务同时修改或构建共享产物。

必须观察到：

- 真实持久 Base→多 Delta→新 Base，两个异构模型族与历史/current 混合；冷重开只给地址和模型族
  登记即可恢复全部行，不维护 per-object reader/Schema 字典。旧祖先 CLR 删除的历史 Vn 仍可读。
- Schema 日志有定义却未登记 reader、整个模型族已不再生成、未来未知版本、同 key 不同完整
  字段/祖先定义均拒绝，不回退 current；重复登记规则可观察。
- 坏 Delta、尾随 bytes、错误 prior、string Delta 仍拒绝；同对象 Schema mismatch 时该 body 零调用。
- 共享非空 string 为同一实例；等值不同 ID 为不同实例；多个 Empty ID 为同一 Empty，null 独立。
  行查询与 string 解析表共享实例；继承和历史 DTO 的引用槽都检查。
- 无变化 owner 沿用旧记录，而目标 Revision 已 Remove 其 string 或改为错误 kind 时，整次读取
  失败；历史 Revision 仍可独立读取。失败不改变先前成功的结果或任何文件。
- 晚期坏引用/未知 reader 不交付前半目录；空目录成功；已返回结果在关闭 Store 后仍可使用，
  外部不能篡改 rows、DTO 或 reader 目录来改变既成结果。
- 生成成员 body 保持静态绑定，Delta 循环保持 typed state；实际 Runtime 包仍可独立生成该登记
  适配器，Runtime + StateStore 包消费者验证完整目录读取，不借 ProjectReference 代替。

实施后运行根 solution build、相关 Runtime/SG/StateStore tests、完整 suite，及原/StateStore 两个
PackageConsumer probes。没有新 wire，所以不为本片另造 wire golden；沿用原格式回归即可。
达成上述结果即停止，不把待办加载层一起实现。

## 7. 本轮规划证据

主代理核对当前工作集、目标约束、路线图、DB-031 与上述源码；两位 subagent 分别评估选片
及生成/Runtime 读取接缝。交叉讨论确认不必把 Upgrade/roots 并入，也不必全目录 header 预检。
本轮只改设计与导航；未运行 build/tests，不把 DB-031 的既有测试数字记为新验证。
提案及三份导航经独立只读复审，无阻塞项；4 份 Markdown 为 UTF-8/LF，115 个本地链接及
引用锚点检查通过，Git diff 检查通过。

## 8. 施工合同与账本

实施基线 `8fc1357`，工作区干净；根 build 0 警告/错误，基线完整 tests 768/768，无跳过。
用户确认的六阶段加载顺序已记录到[目标设计](../../DurableGraph-target-design-v0.md#恢复transient-与宿主边界)，
本片只实现 stored-exact DTO 目录及现有 string 引用验证。

本轮冻结接缝：

- Runtime public `StateReaderBinding<TState>` 绑定 Schema、Base/Delta reader 与 string validator；
  public `IStateReaderRegistration.Register(StateReaderBinding)` 为 SG 提供不依赖 StateStore 的登记口。
  同步 `IStateBodySource` 与共用 typed `StateBodyDecoder` 保持 internal；通过 Runtime 对 StateStore 的
  friend 可见性接入，无反向项目依赖。原 typed reader 的两个 delegate 移至 Runtime namespace。
- SG `__DurableBinaryBody.RegisterReaders(registration)` 登记各 Vn 的稳定 reader 实例，成员 body 不改。
- StateStore `StateReaderRegistry` 按 SchemaKey 登记，同一实例幂等、同 key 另一实例拒绝；
  每次读取复制固定索引，之后对原目录的登记不影响正在执行的读取或已返回结果。
- `RevisionDecoder.Read(store,schemas,revisionAddress,readers)` 返回 `DecodedRevision`，提供
  RevisionAddress、Objects、GetRequired(id) 和 Strings。对象优先解码，引用校验完成后才交付。
  行复用 CapturedObject，历史 DTO 没有 preparation；StringReadTable 复制已解码实例映射，不重新解码。

| 要求 | 实施负责 | 验证入口 | 状态 |
|---|---|---|---|
| typed 整链与一次装箱、内部 string 建表 | Runtime 子任务 | StateReaderBindingTests | 已验证 |
| 稳定模型族历史登记与静态 body | SG 子任务 | GeneratedReaderBindingTests | 已验证 |
| exact 分派、完整目录、晚期失败、只读结果 | StateStore 子任务 | RevisionDecoder / StateReaderRegistry tests | 已验证 |
| 异构历史 DTO、旧祖先及真实文件重开 | 集成子任务 | DecodedRevisionGeneratorTests | 已验证 |
| 原 Runtime 与 StateStore 实际包交付 | 包消费子任务 | 两个 PackageConsumer scripts | 已验证 |
| 总体流程文档、集中验证、独立审查 | 主代理 + reviewer | 根 build/tests、diff/链接 | 已验证 |

### 8.1 实施结果与验证

实现范围与上表一致。Runtime 提供 typed binding 和共用整链解码；SG 新增各版稳定 ReaderVn 及
RegisterReaders，已有成员 body 不变。StateStore 的局部 registry、RevisionDecoder 和 DecodedRevision
完成 exact 分派、目标视图 string 引用验证与整体交付。读取不调用 Append、RegisterBatch 或 Accept。

真实生成集成在混合 Leaf V1/V2 与另一模型族的 Revision 上，跨 Segment 还原 Base→两次 Delta→
新 Base；旧祖先 CLR 定义已删除。反例使只有历史祖先槽引用的 string ID 被 Remove 或改为 durable，
证明不能由其他 current 对象的校验代替历史槽校验。末尾对象缺 reader/坏 body 不交付半份目录；
此前成功结果及关闭 Store 后的 DTO/string 仍有效，逐文件内容对比无写入。

2026-09-07 主代理集中执行：

- `dotnet build DurableGraph.slnx --verbosity quiet`：最终 0 警告、0 错误。
- Runtime/SG 定向测试 16/16，包含新 binding/登记与既有 preparation 定位；完整冷读生成集成 1/1。
- StateStore 定向测试 25/25：新 registry/目录与既有 TypedObjectVersionReader 共用路径。
- 两项旧 SG API 清单断言适配后定向测试 2/2；仍严格检查生成方法集合。
- `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：最终 800/800，无跳过；
  DurableGraph 397、StateStore 145、Storage 155、Serialization 103，较基线增加 32 项。
- `./experiments/PackageConsumerProbe/Run-Probe.ps1`：通过；Runtime-only consumer 新增
  `GeneratedReaders:True` 强制标记，history count 7。
  产物 `experiments/PackageConsumerProbe/obj/run-20260907022946-8240`。
- `./experiments/PackageConsumerProbe/Run-StateStoreProbe.ps1`：通过；保留 DB-031 六项标记并新增
  `DecodedRevision:True`，history count 2。实际包消费者改为登记模型族并读取完整目录，
  验证两个 Revision 的值、完整 membership、行与 string 表实例一致。
  产物 `experiments/PackageConsumerProbe/obj/state-store-run-20260907023420-25348-7a8cf059`。

集成中修正测试夹具的 ReadUInt32 名称与截断异常断言；第一次全套回归发现两个旧测试的方法清单
缺 RegisterReaders，补入该方法后重跑通过。另一个旧 reflection 断言改为按 Preparation 名取字段，
避免把新增 ReaderVn 字段当作 preparation；均未放宽原 DTO/layout/字节断言。

独立 reviewer 完成产品、测试及包消费审查，无未解决阻塞项；主代理核对实际 diff 和执行结果。
27 个修改文件 UTF-8/LF，6 份 Markdown 的 140 个本地链接及引用锚点有效，Git diff 检查通过。
没有更改持久格式、Storage、策略算法或上游源码；无 Upgrade、roots、Restore、Current 导入或发布承诺。
