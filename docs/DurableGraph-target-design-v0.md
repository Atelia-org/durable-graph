# DurableGraph 目标与设计约束

> 本文维护长期目标与已选设计约束，不描述完成情况，不冻结 API 或持久格式。
> 产品现状：[src/PROJECT-STATE.md](../src/PROJECT-STATE.md)。
> 尚未完成及待裁决事项：[路线图](DurableGraph-research-roadmap.md)。
> 早期完整草稿：[2026-09-06 归档](archive/2026-09-06/DurableGraph-target-design-v0.md)。

## 1. 要解决的问题

DurableGraph 面向长期存在、持续演化的 C# 领域世界，例如角色与物品关系、共享规则、
HistoryLog，以及由这些权威事实派生的索引和认知摘要。应用直接编写普通强类型 C# 领域逻辑，
不必把模型手工翻译成通用字典树，也不依靠 setter hook、代理或 MarkDirty 报告修改。

目标闭环是：领域对象图 → 捕获版本化状态 → 比较已提交状态 → 追加对象版本 →
明确发布 → 按 exact Schema 读取 → 显式升级 → 恢复领域图与可重建状态。
Source Generator 负责可在编译期确定的类型知识与机械代码，框架负责保存、加载和身份管理。

首版接受按存活对象图进行 O(live graph) 扫描。是否增加指纹、局部扫描或其他优化，
应由真实对象数量、baseline I/O 和提交耗时决定；不把业务维护 dirty 信息作为前提。

这不是任意 CLR heap、执行栈或 continuation 的 checkpoint，也不以 ORM、查询语言、
分布式多写者或透明回滚任意 C# 副作用为目标。delegate、Task、线程、文件句柄、Socket
等执行或外部资源不因可被 CLR 引用就自动具有持久化语义。

## 2. 已选设计约束

本节汇总已由用户选择、并持续约束产品切片的方向；具体支持范围只查产品工作集。

### 程序集与命名空间边界

[DB-071](design-branches/0071-assembly-namespace-organization-review.md) 保留七项目的职责与依赖图：
Persistence 组合 Runtime、Storage 与 EventJournal；Runtime 和 Storage 共用 Serialization。
主 DurableGraph 程序集的根 namespace 保留领域/登记契约，Schema 放布局描述，Runtime 放执行绑定与状态操作。
这些 namespace 不增加程序集边界，不要求 Schema 与 Runtime 在同程序集内形成单向依赖。
组织迁移要求依赖闭包重编译，保留类型访问级别、Generated/Family/DTO、模型 ID/版本与持久格式；不引入旧包兼容壳。
只有真实消费者要求独立发布或替换时，才重新评估拆程序集。

### MVP 功能边界

以下为用户已选定的支持与裁剪范围；这是支持合同，不表示相关 codec 或加载流程已经实现：

- 数组仅支持零下界的 SZ VectorArray `T[]` 和有限 rank 的多维数组。任何维度非零下界，
  以及非 SZ 的 rank-1 数组 `T[*]`（即使下界为零），均明确拒绝，不转换成其他形状。
  多维数组 rank 上界为 4；超过上界明确拒绝。受支持槽可递归作为数组元素，包括泛型/inline struct 与数组引用；
  数组引用要求 exact nominal 类型，协变的历史 ancestry witness 独立后继，不以当前 CLR 祖先替代。
  VectorArray、Rank-2、Rank-3、Rank-4 使用独立构造码，元素类型及每维长度仍须表达；
  数组局部合同见 [DB-043](design-branches/0043-vector-array-object-slice.md)，完整表示整数寻址见 [DB-045](design-branches/0045-persisted-representation-id-slice.md)。
  可静态识别的不支持类型由 SG 拒绝，其余在 Capture/读取边界校验，不静默降级。
- 首个 BCL 内容对象选择 exact `System.Collections.Generic.List<T>`，元素复用全部受支持槽，
  并允许 List、数组及用户泛型递归组合。List 子类、接口集合字段、任意 object 槽和其他 BCL 容器不随之开放。
  List 的泛型实参不协变；`List<Base>` 内的已登记 Derived 实例继续遵循已有 class 多态约束。
- 领域建模需要易用的复合值 Key；允许当前用户 Equals/GetHashCode 或外置 comparer 决定领域查找，
  包括忽略仍需完整保存的 Timestamp 等字段。库不保存任意比较代码的历史，也不证明任意业务方法都能安全恢复。
  映射先以白名单 BCL Dictionary 适配验证主体，但该 CLR 容器选择保持实验性，后续可改为近似的自定义 IDictionary 实现。
  record struct 按下面的值合同支持；ValueTuple 等具体外观独立扩展，不把首片键白名单作为最终功能上限；后续工作见[路线图](DurableGraph-research-roadmap.md)。
  映射保存无序逻辑键值；容量、hash/bucket 与枚举顺序不持久化。比较器的实现与实例选择是两回事，
  必要的选择信息可以保存，不能仅凭函数是 Transient 就假定同类型的所有字典采用相同行为。
  字典查找相等性和持久键相等性分开：后者按同 exact key 槽的 canonical Base bytes 对应条目；不同 ID/bits 的键可以 Remove+Add。
  同键 value 使用融合 Delta，解码不依赖领域 comparer 或 entry ordinal；键和值都保留引用边。
  所有 source/current DTO 保留 canonical key 唯一和完整引用校验；当前业务 lookup 的冲突在实际领域字典构造时拒绝，
  不静默覆盖或合并。历史 DTO 可读取不代表当前业务规则必能接纳。框架已知标准策略可以继续提前校验；
  单个 snapshot 中同一闭合 Dictionary 类型的 Application 实例采用一份当前恢复规则；不承诺还原任意实例的不同自定义配置。
  Default 和框架标准选择独立保留，具体分层合同见 [DB-055](design-branches/0055-composite-dictionary-key-design.md)。
- 显式 DurableType 的同编译、顶层 partial record struct 复用普通 struct 的 InlineValue 合同，支持 readonly/mutable、
  positional/body 与泛型。位置参数、自动属性及 field-backed 属性的真实存储须以 field-target DurableField/Transient 分类，
  不调用 getter/setter/构造器/初始化器。持久 Schema 不包含 record 关键字、backing 名或合成方法；同 FieldId/完整槽的外观变化不伪升版。
  record 合成 Equals 不识别 Transient 标注，框架不替用户改写业务比较；Key 仍须满足当前恢复阶段的比较边界。
  值类型生成入口及误标诊断见 [DB-056](design-branches/0056-record-struct-state-slice.md)，不据此开放普通 class 的一般 property 序列化。
- 领域引用对象统一实现 IDurableObject，不要求继承框架基类；普通 class 与 record class 共用对象身份和 Schema 管线。
  祖先链除 object 外每层均须显式参与 Durable Schema/history，任意第三方基类适配不在当前目标内。
  record class 支持 positional/body、泛型与已标记继承，按实际声明字段归属分类/投影；不保存 record 外观或合成行为。
  引用相等决定对象身份，业务值相等不合并对象；with 新实例不继承原 ID，也不提供深复制或深不可变。
  共同接口不授予任意 object/interface 持久槽或 boxed value 身份。具体迁移与验收见 [DB-068](design-branches/0068-record-class-model-slice.md)。
- 支持 CLR `Nullable<T>`，T 为受支持标量、Durable inline struct 或显式登记 enum，包括泛型 struct；可作为字段、
  泛型实参和数组/List 元素。DTO 使用 unmanaged `NullableState<TState>`，absent 不访问内部状态或产生引用边。
  Nullable 无独立对象身份或业务版本，内部 exact 布局变化沿原 inline 规则传播到 owner；
  不随之开放其他 CLR 值类型或 boxed value。包装合同见 [DB-052](design-branches/0052-nullable-value-slot-slice.md)。
- Guid、decimal、TimeSpan 作为内建标量值组合到既有字段、泛型、Nullable 和容器，不要求用户 Schema 或独立对象身份。
  Guid 保存全部 128 bit；decimal 保存公开的系数、scale 与符号，包括正负零；TimeSpan 保存完整有符号 Ticks。
  decimal 数值相等不意味着持久状态相等，scale-only 修改也保存；领域 Dictionary 比较仍按其原规则，持久键按完整表示配对。
  采用公开稳定 API，不转文本、不依赖 CLR 私有内存布局，不自动把 long/TimeSpan 或 double/decimal 相互转换。
  编码与验收见 [DB-057](design-branches/0057-bcl-scalar-value-slice.md)；其他 BCL 值另行选择。
- DateOnly 保存 DayNumber，TimeOnly 保存日内 Ticks；DateTimeOffset 完整保存 clock Ticks 与整分钟 UTC offset，
  不保存时区规则，不随本地时区转换，也不统一改为 UTC。三者作为内建标量直接组合到既有值/容器管线。
  DateTimeOffset 同瞬间不同 offset 是持久变化；领域 Dictionary 默认按同瞬间查找，持久 key 仍按完整 bytes 配对。
  日期、时刻、ticks 与带偏移时间戳之间的业务转换继续由显式 Upgrade 决定；DateTime 的 Local/DST 合同独立后继。
  具体编码与验收见 [DB-058](design-branches/0058-temporal-scalar-value-slice.md)。
- 用户 enum 显式标记 DurableType；支持同编译、顶层 public/internal 声明及八种 C# 整数底层类型，
  无需 partial。Schema 复用 InlineValue：独立 nominal 身份/版本，单一 FieldId=1 的底层整数槽；
  生成版本化 DTO 与外置静态投影，不把 enum 擦成普通整数身份，不新增持久格式或对象行。
  常量名称、别名、赋值表和 Flags 属性不进入 Schema/history；未命名数值与未定义 bits 原样保存恢复。
  底层整数类型变化必须升版；业务数值含义变化由作者显式升版和 Upgrade，框架不从常量表猜测迁移。
  版本传播、Nullable/泛型/容器组合及 owner 控制的值升级复用 inline 规则。
  具体合同见 [DB-053](design-branches/0053-enum-inline-state-slice.md)。
- Upgrade 仅转换单个对象的字段，从旧 DTO 产生下一版 DTO；不读取其他对象，不拆分/合并对象，
  不创建带持久身份的新对象。创建下一版 DTO 值本身不属于这一禁令。已有引用槽可以保留、调整或
  清空，但必须满足输出类型与引用合法性；不提供遍历其他对象内容或分配新 ObjectId 的升级上下文。
  单对象升级仍可能删边，因此 source 目录与升级后可达集合的区别不能省略。
- 外层保存/加载 API 只需一个应用领域根，称为 World；不要求 CLR 类型必须命名为 World。
  不建设多根列表、命名根或局部根加载的产品 API。内部已有多根 Capture/测试机制不因此变为
  MVP 对外承诺；根的空值/清空语义由后续 API 分片明确，不改变 Revision Parent 的含义。
- MVP 不提供、发现或自动调用 Transient 重建 hook，不调度 hook 依赖。库交付完整持久对象图后，
  用户代码自行重建索引、缓存并决定何时向业务代码开放。详见下文宿主边界。
- MVP 不支持领域对象图中 boxed value 的持久对象身份，遇到该类内容明确拒绝。
  此限制不排除受支持值字段/inline struct，也不禁止框架内部为异构版本化状态 DTO 目录装箱。
- 支持没有无参构造器的领域类，也支持受支持字段类型的 readonly 实例持久字段，包括 private
  及基类声明的字段。恢复采用 `RuntimeHelpers.GetUninitializedObject` 分配，再由 SG 生成
  Hydrate 填充；不要求用户补无参构造器或专用反序列化构造器。readonly 写入仅用于未交付实例的
  恢复阶段，不提供运行期间修改已交付对象 readonly 字段的能力；不据此扩展到 static readonly。

跨对象升级、多根 API、Transient hook 和 boxed value 的重访条件由[路线图](DurableGraph-research-roadmap.md#4-明确延后及重访条件)
维护。非零下界和非 SZ rank-1 数组按不支持处理，不作为默认后续待办。

### 跨程序集模型组合

领域库各自拥有声明、Schema history 和生成执行能力；应用通过库的公开登记 facade 将它们放进同一冻结目录。
名义引用和动态表示参数可以跨程序集组合，不要求消费方复制依赖库的 history 或为其再生成一份 DTO。
每个引用对象自己的 Base 决定其 exact 版本；目标升版不改变 nominal-only 引用方的 Schema。
动态 inline 参数仍沿原规则传播完整布局变化，作者负责受影响 owner 的版本与显式 Upgrade。

固定外部 inline 值通过定义库的只读模板导出组合，消费方复用其公开历史 DTO/body 与当前投影。
导出只含定义库自有模板；消费方按需解析传递 exact base/inline 依赖，分别维护自己的 history、生成代码和登记入口。
外部值的 internal 实现依赖不必作为 public 领域类型暴露。构建材料的执行合同版本独立于 history 格式，
模板导出不代替运行时 Definition/reader/Upgrade 登记，也不把 CLR 名称或程序集来源加入持久身份。
引用材料须自行闭合，再与本地 accepted history 校验；本地 current candidate 不能修补旧缺口，
本地或另一库的同 ID 定义不能冒充原提供者。具体合同见 [DB-060](design-branches/0060-cross-assembly-inline-history-slice.md)。

外部 Durable 基类由定义库的已绑定 Capture/Hydrate 投影完整基类状态；派生库只组合公开 DTO，
不访问基类私有字段的 CLR 类型。Family class 统一按本声明字段和 immediate base 协作，
完整 leaf DTO/body 仍按声明段展开，不为基类新增对象行或自动调用其 Upgrade。
基类投影是单独显式能力；普通整对象入口仍要求 exact CLR 类型。具体合同见
[DB-061](design-branches/0061-cross-assembly-inheritance-slice.md)。

固定外部 base/inline 的 exact 布局变化仍要求受影响 owner 显式升版和 Upgrade，并重编译受影响消费者。
程序集拆包也不授予任意 CLR 二进制兼容：
保留消费者 DLL 仍需保持其使用的公共 CLR 类型和成员。缺少定义、reader 或升级能力时明确拒绝，
不用当前源码代替历史代码，不用程序集名为相同 DefinitionId 提供额外身份隔离。
具体边界及独立包见证见 [DB-059](design-branches/0059-cross-assembly-model-composition-slice.md)。

### 捕获状态与领域行为分离

- 保存先从领域图捕获与完整精确 Schema 配对的版本化状态 DTO，形成捕获候选图（候选 DTO 视图）；后续比较、
  估算、Base/Delta 选择和编码都消费该候选状态。领域对象随后变化不能改变候选内容。
- 自定义领域类型的 DTO 与 Capture 由 SG 生成；受支持 CLR/BCL 引用类型使用预制或可组合适配。
  string 不可变，非空内容可以直接保留原实例；一般容器不能据此假定浅拷贝已隔离可变内容。
- 同一个候选状态在实际发布成功后才可成为提交基线；不能重新捕获领域对象来冒充已提交结果。
  基线是可丢弃、可从权威状态重建的比较投影，不是第二个持久权威来源。
- DTO 的 CLR 字段命名或物理展开方式不决定 Schema。历史 DTO 从已接受的 Schema/history
  再生成，不要求永久保留所有旧领域 CLR 类，也不另存一套 DTO 源码历史。
- 同版 DTO 的 Delta 准备融合变化判断与编码，结果持有变化判定和可复用 bytes，Delta body 大小从实际长度取得；
  选择 Delta 后复用该结果，避免再次比较和编码。临时缓冲所有权独立于可变领域对象。
  List 匹配需要反复试探元素相等性，另用同 exact 槽下无分配的静态 StateEquals；逐持久字段比较，
  不比较 struct padding、不调用领域 Equals，也不为试探生成临时 payload。匹配确定后，
  仅对实际需要 patch 的元素对调用融合 PrepareDelta，并复用其 bytes；两者必须具有相同的持久状态相等语义。
  这不要求普通对象在 PrepareDelta 前额外扫描一遍完整状态。具体分工见 [DB-049](design-branches/0049-list-range-delta-and-matcher-trial-slice.md)。
- MVP 同样提前 PrepareBase：复用强类型 Write 生成独立拥有的 Base body，以实际 body 长度计量，
  决策后直接复用选定 bytes。先接受全部 live Base 准备的 CPU/内存成本，优化留待 MVP 后测量；
  Frame 大小上限不代表候选集合的内存上限。Storage envelope 开销另按其格式计入。

设计来源：[DB-022](design-branches/0022-versioned-state-dto-capture.md)、
[DB-024](design-branches/0024-reference-capture-and-reusable-object-ids.md)、
[DB-027](design-branches/0027-generated-same-schema-delta-body-slice.md)、
[DB-029](design-branches/0029-prepared-object-revision-planning-slice.md)。

### 统一引用身份，值类型嵌套

- 所有受支持引用类型统一进入对象状态记录的目录，包括自定义 class、string、数组与 BCL 容器；
  具体目录属于 candidate、stored 或 current 视图时另行说明。
  成员中的引用只保存 ObjectId，对象本体独立保存；共享和循环是整体恢复目标。
  引用对象操作以 C# object 接受实例，按实际 runtime 类型分派至受支持的内建或用户模型；
  字段/元素的强类型操作继续静态绑定。内部 object 参数不等于开放任意 object/interface 持久字段。
- 当前 CLR 图按引用相等语义登记。内容相等的不同非空 string 实例不能合并；唯一明确例外是
  所有零长度 string 在 Capture 和读取两端都规范化为 string.Empty。null 仍与空串区分。
- DTO 引用槽及 Runtime/StateStore 上层对象身份使用非泛型 `ObjectId(uint Value)`；普通 UInt32 数值仍为 `uint`。
  零表示 null，显式构造/取 Value，不提供隐式数值转换。Storage/Serialization 边界保持原有 UInt32 编码。
  包装不携带目标族或版本，Schema 与引用目录继续决定目标约束；泛型目标品牌暂缓。
- ObjectId 是指定 StateRevision 内的查找编号。相邻保存中持续存活的对象保留编号，
  回收后的数字可以复用；跨 Revision 的裸编号相同不代表同一实体。
  新占用者从 Base 开始，不能继承旧占用者的 Delta 链。
- 引用槽经正在加载的目标 Revision 解析，即使 owner body 沿用更早的记录；旧 Revision
  使用自己的视图。已解析的 CLR 对象图缓存不能直接跨视图复用。
- stored DTO 的引用按该 Revision 中目标的 stored Schema 祖先校验；全部单对象 Upgrade 完成后，
  再按当前版本 DTO 目录校验。历史合法不保证升级后仍合法，当前 CLR 祖先也不能替代旧 Schema。
  完整 source 目录均须解码、升级和验证；仅当前版本 DTO 图中从 World 可达的实例参与分配，全部分配完成后才填充引用。
  不可达对象的坏数据仍拒绝，但其当前模型类型不可实例化本身不应阻止其他 World 的加载。
- 首轮采用会话内单调分配，失败或放弃候选可以烧号。允许未来复用不要求立即实现回收器；
  CLR 实例映射清理、可达集合变化、编号回收与历史文件物理 GC 是不同动作。
  publication 不确定也不能当作确定失败释放候选身份。
- struct 等复合值没有独立身份，采用嵌套布局。ref accessor 的作用是让值 codec 共用字段、
  数组元素等真实槽位，配合 Writer/Reader 读写；不要求统一读写模式的 visitor。
- BCL 容器按内容保存和重建，不以其 bucket、capacity 等内部实现代替持久内容合同。
  List 只保存 Count、有序逻辑元素；Capture 递归投影为 owned 状态 buffer，后续领域修改不能污染候选。
  同一列表 resize 保持 ObjectId，只改 Capacity 无状态变化；同内容的新列表仍具有新的引用身份。
  List 的差异算法不要求用户维护 change-tracking 容器。Delta 从 immutable prior 的 source 区间复制内容，
  按输出顺序组合新值与稀疏元素 patch；不修改 prior，也不依赖 target-copy 或业务编辑日志。
  匹配算法只决定复用哪些区间，不进入持久格式、ListLayout、Schema 或 RepresentationId；不同 writer 共用 reader。
  算法选择随操作快照冻结，重新配置不改变已有会话。搜索预算耗尽可退回位置匹配，不能牺牲精确恢复或 NoChange。
  默认 Adaptive 保留完整 Local Delta 基准，停滞时用独立有界 Myers 竞争，仅采用严格更短的完整 body；
  竞争者可在实际已写长度达到基准时停止。接受额外编码及短期分配，最终选择后复用 bytes。
  该保证只针对相同 frozen pair 的 raw List Delta body；不是全局最优、整个文件尺寸或耗时/峰值内存保证，见 [DB-051](design-branches/0051-bounded-list-delta-competition.md)。
  选择时优先保存耗时、分配与实际写入尺寸，冷读性能优化优先级最低；不为匹配实验扩张读链或缓存设计。
  语法与算法分工见 [DB-049](design-branches/0049-list-range-delta-and-matcher-trial-slice.md)，选型证据与后继条件见
  [路线图](DurableGraph-research-roadmap.md#32-list-差分算法选型与设计)。

设计来源：[DB-018](design-branches/0018-generated-graph-codec-shape.md)、
[DB-024](design-branches/0024-reference-capture-and-reusable-object-ids.md)、
[DB-025](design-branches/0025-string-object-decoding-slice.md)、
[DB-034](design-branches/0034-durable-reference-graph-batch.md)。

### Schema 依赖与强类型代码

- Schema 具有稳定类型标识、版本和声明层字段编号；同版本不能静默改写已有布局。
- class 按 base-first 组合，FieldId 在声明 Schema 段内编号。派生 Schema 绑定 exact base；
  祖先 exact 依赖改变时，受影响的派生版本也必须递增。
- inline struct 也是 exact 布局依赖，其版本变化沿 inline/base 依赖传播到 owner。
  引用成员使用稳定 nominal 类型约束，不因引用目标升版而递归升版整个引用图。
- 自定义 struct 与 class 一样显式标注 DurableType，拥有自己的 SchemaId/版本/history；
  inline 值没有独立 ObjectId 或对象 Model。SchemaKind 区分 ReferenceObject/InlineValue，
  同 SchemaId 不跨 kind；exact base/inline 依赖形成有界 DAG，nominal 边不进入布局闭包。
- owner 的单对象 Upgrade 显式转换嵌套 DTO；框架不另行先升级 struct。历史 inline DTO/body
  从保留的 exact history 生成，不依赖当前领域 struct 声明存在，也不要求值迁移壳。
  领域/DTO 表示保持分离，不能为泛型复用而把可变领域引用保留在 DTO 中。
  数组与 List 是各自独立的内建 owner：分别显式选定元素转换规则后，由框架逐元素执行；不能由不同入边
  分别决定同一共享内容对象的转换。元素升级保持 ObjectId 与位置，数组保持 shape，List 保持 Count；
  仍 live 的升级对象下次保存强制 Base。两者直接绑定 source→current 的显式值规则，
  不自动串接相邻规则搜索路径；空数组、空 List 也须验证转换能力。
- Upgrade 用户入口统一接收非泛型 UpgradeContext，优先考虑工具扩展的灵活性；Context 提供本次转换的只读信息，
  值转换能力由 owner 显式取得并调用，不逐个追加到历史方法的参数列表。
  当前采用预声明/预绑定能力，只在执行时查询已选工具；不由 Context 动态选择业务规则，也不扩张单对象操作边界。
  最小调用信息与后续工具组合分别由 [DB-038](design-branches/0038-generic-schema-state-and-binding-design.md)
  和 [DB-039](design-branches/0039-composable-value-upgrade-design.md) 承接；当前实现范围从 PROJECT-STATE 查证。
- 值工具按 provider 局部 key、显式规则集和两端声明段/FieldId 绑定；完整槽语义包括引用类别与 nominal 约束，
  不仅是 DTO CLR 类型。显式候选的布局、签名或子依赖失败必须拒绝，不能回退为透传；
  KeepExact 仅在作者明确启用、没有显式候选且完整槽等价时成立。
- Nullable 值工具可在显式规则集中开启 `AllowNullableLifting`，保持有值/无值状态，仅对 present 执行已绑定的 child 转换。
  显式 wrapper provider 优先，错误不可回退；空值及空集合仍须在业务 callback 前验证 child 能力和 exact 依赖。
  wrapper 的 inline 版本适用性取自 child；Nullable 不因此成为新的独立 Upgrade owner。
  整条对象升级链及其声明依赖在首个业务调用前绑定。snapshot 缓存不含调用状态，
  子工具保持自己的依赖表并继承当前 owner 的 ID/exact 对象布局端点；class owner 采用相邻版本，
  array owner 另带不可变 shape，List owner 使用独立的 ListCount，不以 ArrayShape 冒充长度。
  Context 与工具不跨同步调用保留。
- 自定义泛型的持久身份是定义 ID、有序 nominal 实参与定义版本；领域参数、冻结状态表示和静态操作参数分开。
  历史 DTO/body 位于纯状态宿主，按 stored 完整布局闭合，不要求旧领域值 CLR 类型继续存在。
  单对象整条相邻 Upgrade 链先绑定再执行；中间 exact 布局不足以唯一确定时明确拒绝，不能用 current/latest 补齐。
  元数据缓存只是派生结果，后续登记的 exact base/inline 定义仍必须与缓存的完整依赖闭包一致。
- 2026-09-08 用户选择泛型闭合 Schema 的 MVP 保证范围为目标 Repository 内严格一致，暂不增加闭合历史账本。
  开放定义 history 保证模板不变，不承诺穷尽检测所有实参导致的漏升版；不同空库可能首次接受同 key、不同完整布局。
  因此完整 Schema 校验不能省略，也不能仅凭 key 跨库复用绑定。具体反例、方案比较与后续触发见
  [DB-038 §3.3](design-branches/0038-generic-schema-state-and-binding-design.md#33-必须明确的保证作用域两个空仓库)。
- 引用对象的 Base 头表达实际 exact 类型/Schema，后续 Delta 沿同一 Schema 解释；版本变化从新 Base 开始。
  读取先在 stored Schema 下完整重建，再升级；仍存活的升级对象下次显式保存必须 Base，即使业务值未变。
  TypeCodec 表达受支持类型经数组、内建 List 或用户泛型构造的组合；
  类型表达能力与是否存在相应 codec 是两个条件，可表达不等于可读写任意 CLR 类型。
- SG 已知字段/元素类型时直接绑定字节原语或静态值 body，不为每个已知槽位增加 Type 查表、
  委托或虚调用。运行时开放组合的绑定接缝不能反过来支配静态成员的生成形状。
- DynamicMethod 是可评估的工具，不是已经选择全面翻新旧 IL 后端的承诺；若有多个生成后端，
  它们必须共享 Schema 解释，不能各自发明持久语义。

设计来源：[DB-018](design-branches/0018-generated-graph-codec-shape.md)、
[DB-019](design-branches/0019-schema-ancestry-implementation-slice.md)、
[DB-020](design-branches/0020-typed-slot-array-binding-slice.md)。

复合值的细化合同及证据见 [DB-037](design-branches/0037-inline-struct-state-slice.md)。

### 对象表示与存储职责

- MVP 固定 ReadAmplificationBaseBudgetPolicy。整数 X 倍通过对象级冷读放大产生 Base 动机，
  整数 Y% 控制可选 Base 的软预算；它不是所有写入的硬峰值保证。
  精确比较、预算和强制/可选分类以 [DB-015](design-branches/0015-statestore-object-representation-policy.md)
  与对应代码为准，后续执行层不能自行改变策略语义。没有合法 Delta 的更新显式强制 Base，
  不伪造 Delta 估算；它与 Insert 一样属于必需写入，不消耗可选 Base 预算。
  默认选择采用可解释的稳态摊销容忍度，不假定存在无工作负载依赖的天然最优阈值；
  当前参数与调优指导见[根 README](../README.md#调整保存策略)，分析依据见 [DB-070](design-branches/0070-read-amplification-default.md)。
- 策略消费完整 post-live 对象集合的估算，产生稀疏表示计划。真实 Revision Parent、对象变化分类、
  reachability 和 Removes 由保存调用方提供，策略不能证明这些输入完整。
- Storage 使用多历史 Segment 地址与 BackwardFileDistance；rollover 是 soft threshold，
  不因此强制冷对象 Base。对象内容 Delta 与 ObjectHeadMap 的 membership Delta 是不同层次。
- 普通 State 在对象内容决策之后选择目录表示：仅当目录 Base 的字节上界严格小于 Delta 的 Remove 列表时切换，
  否则保留 Delta。保留精确 Parent、本地对象记录与旧 head；不增加公开参数或新的 wire 格式，见[实现与验收](research/object-head-map-base-feasibility.md)。
- Storage 不解释 CLR 字段、Schema 升级或可达性。Append 产生 candidate address，
  外层拥有最终发布 head；raw body 读取不等价于类型或完整图验证。
- 对象 Delta 显式引用同 ObjectId 的 prior record，并与其 containing Revision 的
  exact Parent Revision 中该 ObjectId 的对象 head 对照。
  新 Base 截断对象内容重建链；这不等于截断 membership 读取、历史查询或允许删除旧文件。
- B/D/H 采用统一对象 payload 口径，包含对象独有 kind/prior/length/body 及 Base 的类型引用；
  排除 ObjectId key、共享 membership/Frame/对齐。H 与原 Frame 实编码累计一致，是成本代理而非总物理 I/O。
  冷恢复完整验证后，可随同 Store 生命周期的已提交 DTO 基线保留对象 head/H；热保存按本次实际文件范围与 payload 增量推进。
  元数据仅在确认发布后随 State 基线安装，Event 快照不推进 State；独立 raw 输入仍须验证历史链。
  具体边界与证据见 [DB-069](design-branches/0069-incremental-save-baseline.md)。

地址方向来源：[DB-014](design-branches/0014-multi-segment-backward-file-distance.md)；
内容边界来源：[DB-026](design-branches/0026-raw-base-object-content-slice.md)、
[DB-028](design-branches/0028-persisted-object-delta-chain-slice.md)。

## 3. 保留的长期产品目标

本节保留整体产品定位。它们不是本轮实现清单；机制、格式和故障承诺仍需各自切片验证。

### 长期 Schema 可读与显式演化

盘上对象版本应能找到 exact Schema 事实，不能依赖当前程序集恰好还理解旧字节。
持久 Schema 需要规范表示与一致性校验；当前运行时 GetHashCode 不能充当持久 SchemaHash。
规范表示不能依赖反射顺序、metadata token、MVID、AssemblyVersion 或进程随机 hash。

完整闭合对象表示在仓库内获得持久整数 ID，新写 Base 通过该 ID
取得领域身份与 exact 持久表示布局，Delta 沿用 Base。SchemaStore 封装描述及其解析；当前程序用保留的
历史代码绑定 DTO/reader，CLR Type/委托本身不落盘。同 ID 不重绑定，引用目标版本仍由目标自己的 Base 决定。
ID 由所属目录统一分配，等价完整表示复用；登记先于使用该 ID 的 State 发布，放弃一次 State 不撤销已登记表示。
对象寻址合同见 [DB-045](design-branches/0045-persisted-representation-id-slice.md)。目录内部采用
[DB-046](design-branches/0046-unified-schema-catalog-slice.md) 选择的统一闭合记录：用户 class 的 Schema 记录
就是其对象表示记录；inline Schema 与数组进入同一编号空间，base/inline exact 依赖按目录 ID 引用。
inline 记录只描述嵌套值，不能被 Base 当作独立对象表示；普通引用槽只约束无版本 nominal 类型，
目标版本不成为引用方的 exact 依赖。`SchemaKey` 是由完整名义类型与定义版本派生的查询、冲突索引，
同 key 异形仍须拒绝，不能靠新编号绕过。一次登记的完整缺失闭包经一个批次持久化后才交付 ID。
[DB-047](design-branches/0047-list-content-object-slice.md) 的 List 扩展复用同一编号空间，
以明确内建构造和内容 codec 解释，无需用户 Schema；其元素若为用户 inline 值，仍须登记完整 exact 依赖。
Count 与 Capacity 均不属于 ListLayout；前者属于对象内容，后者不持久化。引用 List 的 owner 只约束 nominal 类型，
不锁定该列表的历史元素布局。内建资格由框架显式定义，不能用同名用户类或程序集归属冒充。

领域 CLR 类型与版本化 DTO/reader 都是结合目录描述和保留代码得到的绑定结果，不持久化第二套 DTO 类型名称。
不同领域名义身份即使共享同一个 DTO CLR 类型也不能合并。是否持久化开放模板与必要 exact 实参，
应以能否简化模板展开和反向绑定为依据另行研究，不由整数寻址自动改变现有版本传播政策。

未知版本、相同身份/版本却不一致的 Schema、缺失升级器、损坏引用或来源不匹配时，
应明确拒绝，不猜测并不回退到 latest。升级由显式类型知识和函数承担，不自动推断业务迁移。
应用须明确仍支持的历史版本范围并保留对应可执行能力：exact DTO 解码需要每个 source-live 族的
reader/Delta applier/引用遍历，当前 World 恢复还需要全部 source 行的 Normalize/Upgrade，以及
可达对象的 Allocate/Hydrate。历史 Schema 文件不能自动补回被删除族的这些能力。
可以保留仅承载生成代码和 Upgrade 的迁移壳；退出可达闭包不免除 source 验证。
新 Revision 已 Remove 某族，只能免除读取该新 Revision 所需的该族能力，不能解除仍支持旧 Revision 的责任。
技术见证见 [DB-036](design-branches/0036-working-session-and-history-capabilities.md#4-并行小线明确历史恢复能力合同)。
读取升级本身不隐式写回 Store；升级失败不修改权威状态。历史数据成为可编辑领域图、
当前版本 DTO 比较基线及后续重写的具体衔接，由专门切片收敛。

### 可达对象图与独立对象版本

每次保存有显式根，MVP 为单个 World；从根可达的对象构成本次 live 集合。对象内容变化只产生其自身的新版本；
父对象的引用身份没有变化时，不应仅因子对象变化而制造业务差异。
不再可达的对象退出新视图，不能因此改写历史 Revision；编号复用仍按各自 Revision 解释。

因此同一 ObjectVersion 的 DTO 可以在不同 Revision 中复用，但其引用目标仍由各自 Revision 选择。
“自身版本相同”不单独证明两份已连接引用的领域实例可以合并；子对象的不同版本可能要求 owner 也分别实例化。
合并读取只承诺各图快照语义，不承诺跨图一定复用或一定不复用实例；跨图 ReferenceEquals
不能用于判断业务身份、版本或决定程序控制流。调用方须将两份输出及其可达对象都作为只读快照使用；库不冻结普通 CLR 对象。
可写恢复仍保证可变对象隔离；内部可复用同版本的冻结 DTO/string，但不能把只读共享图导入工作区。

解码复用必须限于同一资源所有者、同一冻结模型目录，并同时核对 ObjectId 与实际 head；
每份 Revision 的完整 source/current 引用及查找合法性仍各自验证，业务 Upgrade 仍各自执行。
共享普通 CLR 实例还须证明 current 状态相同且所有引用目标可共享；仅有相同布局、未触发重写或 readonly 字段不足以证明。
不同 ObjectVersion 不因内容相等而合并，空串继续沿全库 string.Empty 例外；非空 string 不做内容 intern。
本轮算法与验收边界见 [DB-064](design-branches/0064-shared-revision-decoding-design.md)，缓存和比较策略不进入持久类型身份或 wire。

比较忽略 transient，引用按身份比较，Artifact 引用按 exact address 比较；值和集合的 durable
equality 必须明确，不能仅凭非密码学 hash 判相等。同版 DTO 的 Half/float/double 持久状态比较采用按位相等：
相同 NaN 位无变化，不同 NaN payload 和正负零保留为变化；这不替领域对象定义业务 Equals。

### 四类 Store 的逻辑职责

| Store | 长期职责 | 关键边界 |
|---|---|---|
| SchemaStore | 保存版本化 Schema 事实和完整对象表示的持久寻址 | 对象能精确绑定其解释；元数据与执行代码分开保留，CLR 名称不代替持久身份 |
| StateStore | 保存对象版本及每个 Revision 的 live 绑定 | 追加新事实表达逻辑变化；历史视图不被静默覆写 |
| ArtifactStore | 保存不可变、可寻址的历史内容或大对象 | State 持 exact 引用，按需加载，不使完整历史常驻领域图 |
| DerivedStore | 缓存可以从权威输入和 recipe 重建的结果 | 可整库删除；不能反向成为 Schema/State/Artifact authority |

Artifact 面向 HistoryLog、LLM 消息、附件与历史输入输出等内容，允许只读 View/Query。
Artifact 引用不把完整内容并入 State 对象可达闭包，恢复时不允许模糊 latest fallback。
地址采用内容 hash、append 地址或其他组合，属于待裁决机制。

真实事件/快照消费者已选择由 EventJournal 引用独立 StateRevision 的
[EventHistory 路线](design-branches/0063-event-history-journal-slice.md)。
它可以覆盖上述部分历史内容职责，先屏蔽独立 ArtifactStore 的建设；四类逻辑职责不要求四个独立存储实现。
这不是对附件分块/外部大对象能力的完成声明，也不改变尚待实现的联合 Schema 视图边界。

Derived 面向 Recap、DynamicMemory、embedding、搜索索引等昂贵派生内容。命中必须绑定精确
State/Artifact/Schema 输入及 recipe/builder 版本；围栏不匹配应为 miss/stale，不能通过全库
扫描挑选“最新结果”冒充本次派生状态。上述职责不要求立即拆出四个程序集。

### 单一发布权威与明确故障结果

正常持久帧遵循 [append-only 与离线救援纪律](../AGENTS.md#persistent-data-discipline)：正常打开和失败处理不截断修尾；
专门离线救援先关闭相关资源并丢弃缓存，结束后新开 Store。该约束不改变下述逻辑 ref 发布和分支协议。
raw Revision 与完整 head map 可按完整帧地址复用，但仍为派生读缓存，不成为发布或 typed 图有效性的权威；
缓存由借用底层的 Store 自己释放，底层寿命更长，已交付的 owned 值保持可读。具体实现与验收见 [DB-067](design-branches/0067-owned-revision-read-cache-design.md)。

公共使用模型见 [DB-083 用户故事](design-branches/0083-repository-checkpoint-api-user-stories.md)。
当前公共基础为根命名空间 `Atelia.DurableGraph` 下的 `Repository`、非泛型 `BranchCheckout` 与 `CheckpointAddress`，
具体交付范围见 [DB-078-A 实施记录](design-branches/0078-a-repository-free-history-implementation.md)。
模型环境由每次 Open 固定，durable exact-current Normalize 保持值与身份恒等；实际根按每次 CLR 类型和持久 Schema 选择。
工作副本同时拥有精确 Journal Head、可选 State 根及其冻结当前 DTO 比较基线、领域实例到 ObjectId 的绑定与分配状态；
普通调用方不分别设置这些材料。仅受控加载和成功 State 提交推进其对应关系，Capture/Prepare/Accept 自身不等于持久 Commit。

历史允许 Event-first 与任意连续 E/S。同一 CLR 类型可承担两种角色，E/S 不要求共同领域基类；每个持久图仍必须选择非空 durable 根。
`CreateBranch(name, initialState)` 和 `CreateBranchFromEvent(name, initialEvent)` 各自完成首个 S 或 E 的发布后才交付工作副本，
不暴露空 head。Event-first 的 State 为 null；无参 CommitState 在捕获前拒绝，显式非空根建立首 State。
`Checkout(name)` 保持所选精确 Head，只恢复该位置自身或祖先中最近的 State；有效纯 Event 历史可用空模型目录签出为空 State，
既不物化 Event，也不导入其对象身份。已有 State 的损坏、缺模型或 Upgrade 失败必须传播，不回退旧 State 或伪装成无 State。
库没有 PendingEvent，不自动 replay，也不判定业务处理进度或外部效果是否完成。

设 P 为提交前精确 Head，B 为 P 自身或其祖先中最近已提交 State。新记录的 Journal Parent=P，图 Revision Parent=B.Revision；
首建没有 P，首 State 之前没有 B。于是 S0→E1→E2→S1→S2 中 E1/E2/S1 的图 Parent 均为 S0，S2 的图 Parent 为 S1；
E0→E1→S0 中三个图均无 Parent。Event 只保存自身可达闭包，成功后释放候选，始终不 Accept Event 的 DTO 或 live 身份，
也不推进 State 基线。无 State 的 Event 每次准备完整 Base；热捕获可消耗 ID 游标，纯 Event 历史冷签出则使用全新空 CaptureSession，
不同 Revision 的数字 ID 重合不构成跨图身份承诺。冷开仍验证全部物理记录、orphan、引用和图 Parent，不能仅验证 ref 可达链。

CommitState(nextState) 允许跨实际类型替根，领域限制由应用承担；它继续使用原工作区及身份映射。
已有 child 升根保留 ID，旧根若仍可达则成为普通子对象，仅不再可达的成员 Remove；新实例才取得新 ID。
根、root ID、完整保存基线和 live 身份绑定均以同一冻结候选准备，规定的追加、持久化屏障和 head 发布完成后才安装，
不在发布后重新 Capture 或选择模型。预先可知的 null/未注册直接根在捕获和追加前拒绝；
深层模型或回调失败依实际阶段裁决，不承诺所有失败都无物理写入。完整来源、同 ID 的 exact 布局和 Upgrade rewrite 义务继续保持。

Journal ref 是唯一发布前沿，图追加与 head 发布分开。提交仍检查工作副本所有权、精确 head、最近 State 的 exact 基线并执行最终 CAS，
防重入、fault 和 publication outcome 不因自由历史放宽。每 branch 至多一个活动工作副本，不同 branch 可同时编辑，
仓库操作仍串行。同 branch 重复 Checkout 在恢复前拒绝；Move 只要求目标 branch 没有活动工作副本。
ref-only CreateBranch 不恢复领域图，也不占用工作副本。占用仅由 ordinal 分支名集合表达，显式 Dispose 释放；
丢失工作副本不会由 GC 自动解锁，旧副本重复 Dispose 也不能释放后继占用。

`Fork(name, source)` 从本次打开签发的已提交位置创建新命名分支并交付独立工作副本，源工作副本可保持存活。
它与 Checkout 共用最近 State 恢复规则，保持精确 source Head，不读取源副本的未提交修改，不物化 Event 或追加 E/S 图。
完整 guard 内先占位、恢复并构造工作副本，再发布 ref；普通恢复失败不留新 ref，未交付占位只由本次持有者清理。
发布后失败不删除 ref、不透明重试，仍依据 outcome/fault 重开检查；正常返回后子分支可独立续写。
图内 alias/cycle、ObjectId 与完整保存来源保持，不同恢复不共享 mutable 实例；调用方主动传入的外部别名仍自行负责。
实际交付和失败验证见 [DB-078-C 记录](design-branches/0078-c-branch-checkout-fork-implementation.md)。

历史读写统一使用本次 Repository 签发的 CheckpointAddress，按打开 owner 与逻辑 Journal 位置判等；
null、外仓库或重开旧地址拒绝，诊断用 RevisionAddress/RootId 不能认证来源。重开从持久 branch ref 取得新地址，不提供外部可序列化地址。
[不可变 tag](design-branches/0084-eventjournal-immutable-tags-slice.md) 的持久权威已由上游 EventJournal 本地包提供，DG 接入仍属独立后续分片，
不另立持久权威，也不把 tag 名当作跨仓库外部地址。旧 EventHistoryRepository/EventHistorySession/Resume/GraphFrame 不保留兼容壳，
旧 publication.rbf 也不引入迁移或双发布机制。

ReadState/ReadEvent 按角色校验地址并独立恢复实际领域根；实验性 ReadPair 保持输入顺序、两边完整成功才交付，
按只读快照使用且不承诺跨图实例共享。共享候选以完整当前持久状态比较作证明，不依赖对象 Base/Delta preparation；
缺少比较能力时保守不共享，比较与验证错误仍传播，Dictionary 的 canonical key 校验不省略。
Checkout 使用独立可变 State 恢复路径；热调用中用户创建的别名仍由用户管理。
ReadCheckpoint 在指定地址独立恢复当前图及最近严格祖先的相反角色图；PreviousX 是领域根与地址，缺失时两者同时为 null。
首版 eager 至多两图，全部成功才交付，各根 getter 稳定；两图间、不同读取间及与工作副本间不共享可变实例。
操作内解码复用不改变独立分配和 singleton 拒绝。ReadPair 的显式共享不能用来实现默认 Checkpoint。
PreviousEvent 不表示处理完成或唯一原因，ReadEvent 不因便利上下文额外物化 PreviousState。

EnumerateEvents 固定 endInclusive，仅返回事件地址；可选 afterExclusive 在首项前验证为该位置的祖先，同点为空范围。
无下界的默认 NewestFirst 按需回溯，OldestFirst 可先缓冲所需事件地址；不物化沿途 State。
每次 MoveNext 检查可用性，交付前释放操作 guard 与底层 lease，循环体可 ReadEvent 或进行其他串行操作。
后续提交/Move 不改变所选历史。ReadFrames 保持全逻辑链元数据读取，切片结果不节省其成本；旧 ReadEvents 已移除。
实现与验证见 [DB-078-B 记录](design-branches/0078-b-checkpoint-history-query-implementation.md)，Open 的全物理校验成本另计。

长期目标是让 Schema、State、Artifact 的共同引用有一个可裁决的发布点，而不是各自发布
无法协调的 head；Derived 不充当权威提交的参与者。CommitManifest 是候选表达形状，
不预先冻结字段表或原子发布实现。

统一 Commit/Ref 应共同选择 StateStore、SchemaStore、ArtifactStore 的 exact 视图，支持整体推进、
回滚与分叉；不能让旧 State 视图意外配上最新 Schema/Artifact 视图。整体回滚不要求删除已追加的
物理记录。MVP 的 Repository 内单调 Schema 注册表是阶段性简化，不将其全局可见性冻结为长期合同。
SchemaStore 未来复用 StateStore 的候选路径和自举问题见[后续路线](DurableGraph-research-roadmap.md#41-schemastore-复用-statestore-与联合版本视图)。

只有已发布的 exact head/manifest 引用的 candidate 才取得权威。文件存在、时间戳或最新编号
都不够。被引用数据应先完成规定的 durability barrier，再发布；具体 process/OS crash、
power loss、torn write 和目录元数据保证必须用所选底层与故障注入说明。

publication 结果不明确时，不能假装确定失败并透明重试，应能够 reopen/reconcile 精确裁决。
发布成功后若内存 cache 安装失败，应从权威状态重建。物理 GC、保留策略和 orphan 回收不能
从“append-only”一句话推导出来，也不由当前内容 append 能力证明。

### 恢复、Transient 与宿主边界

MVP 库内加载采用以下阶段顺序；这是目标流程，不表示各阶段都已实现：

1. 按每个对象自己的 stored exact Schema 完整重建 Base/Delta，得到 stored DTO 全目录并验证引用。
2. 对需要升级的对象执行单对象字段转换的显式合法路径，形成当前版本 DTO 全目录；缺失路径或升级失败则停止加载，
   不悄悄交付旧版。记录升级对象下一次保存必须 Base 的义务，读取本身不回写。
3. 对受支持的自定义领域类用 `RuntimeHelpers.GetUninitializedObject` 分配全部实例，
   建立 ObjectId 到实例的映射；string、数组、List 等内建类型使用各自适配器。
4. SG 生成 Hydrate，填充持久字段并连接对象引用，利用先分配的完整映射保留共享和循环。
5. 完成框架的持久数据/引用校验后交付 World，不能暴露解码、升级或引用连接的半成品。

单图读取、Checkpoint、Checkout/Fork 与 ReadPair 共用上述准备和两阶段物化机制；用途决定选图与共享资格，
不另建 Fork 专属恢复算法。Checkpoint 的独立两图与 ReadPair 均先完成全部准备、实例分配和引用表验证，再开始 Hydrate；
只有 ReadPair 的可信闭包规划允许 mutable 复用，普通 allocator 返回同一实例不能获得豁免。
只读交付不导入保存身份；可编辑恢复保留完整 source 基线及独立 CaptureSession。
内部准备材料不自行授予跨操作复用资格；边界与证据见 [DB-079](design-branches/0079-shared-graph-restoration-core-implementation.md)。

Checkout/Fork 的最近 State 材料复用以精确 State/revision/root、同 Store 寿命、固定模型环境与完整 live Schema 证书为条件；
工厂与 Upgrade 计划的预闭合依赖也须传递，不能只核对 source/current 两端。每次请求独立确定 Head，无 State 时完全绕过材料槽。
完整冷恢复与工作副本构造成功后才具备入槽资格，Fork 所有候选构造先于 ref 发布，成功后仅安装引用；失败不替换，fault/Dispose 释放。
领域 mutable 实例和保存身份每次独立，工作副本保留完整保存来源；单槽不是字节或总堆上限。见 [DB-080 记录](design-branches/0080-prepared-checkpoint-reuse-implementation.md)。

提交侧 owned DTO 与 factory 闭合证书不自动证明冷 reader body 的完整依赖；已读过同一 reader 的其他值也不是充分证明。
未来热准入必须对应新 State 的实际持久链与完整来源，不能继承已不相关的历史 Upgrade 依赖。
现有合同不足时保留正常提交与冷恢复，不为优化放宽验证；反例与停点见 [DB-081 实验](design-branches/0081-hot-commit-restoration-material-experiment.md)。

分配领域实例时不执行实例构造器、基类实例构造器或实例字段初始化表达式；因此 `_cache = new()`
等 Transient 初始化也不会执行。交付后的 Transient 重建由用户代码负责，属于宿主阶段。MVP 不提供自动 Transient hook，
也不承诺撤销用户重建期间的副作用或把其失败变成库的加载失败；用户负责在业务使用前完成初始化。
这取代此前“库内调用 Transient hook 成功后才交付”的 MVP 设想，未来框架 hook 另按真实需求评估。

ReadPair 的只读约束包含会影响观察结果的 Transient 写入；视图专属 owner、查询上下文和缓存须置于
各自的图外 view/index，不写入可能共享的节点。需要原位重建 Transient 的历史浏览可分别调用
ReadState/ReadEvent，取得独立领域图并初始化；无需 writer，但持久字段仍是历史快照，续写须使用 Checkout。
不承诺对 string 或应用自有全局对象进行通用深拷贝。使用示例与验收见 [DB-066](design-branches/0066-readpair-comparison-and-transient-contract-slice.md)。

字典由框架分配并填入原实例，应用只提供需要的当前 comparer，不接管共享引用或事后替换字典。
比较依赖必须在插入时就绪并保持稳定：key 自身已填好的值、完整 string 与引用身份可用，
不能仅因目标字段持久就假定其引用对象已完成 Hydrate。引用内容比较的额外阶段另按具体需求设计。

内建引用类型使用预制适配；这些阶段不要求 string 重新复制。升级可能删除引用，须区分完整
source 目录与升级后 World 可达集合，不能假定两者始终一一对应。领域分配、引用连接和后继保存
应保留 source membership 与 stored Schema 来源，只维护一份归一化 DTO 比较基线；下一次 Capture
决定当前版本 DTO 图中从 World 得到的可达闭包，并由差集产生 Removes，不通过重新 Capture 已恢复对象猜测基线。
Empty 多 ID 的反向绑定确定选择最小 source ID，但基线引用槽保留原 ID，让下一 Capture 产生
实际引用差异。新加载工作副本从完整 source live max+1 开始分配，只承诺工作副本内单调；uint 耗尽仅阻止
新增 ID，不阻止加载或已有对象保存。固定 Parent 的低层 Prepare 不就地接受新地址，需 Append 后重新 Load；
普通连续 State 保存使用受控 BranchCheckout.CommitState，发布成功后直接安装原候选并保留领域实例；Event 提交不推进 State 基线。
List 适配器先分配空列表，待全部实例登记后逐元素 Hydrate 并按序 Add，保留共享和循环；
这类内建构造不改变用户领域类不执行构造器的恢复合同。

Hydrate 普通字段由 SG 直接赋值；readonly 实例字段优先由 SG 生成返回字段可写 ref 的
`UnsafeAccessor`，再进行强类型赋值。访问器按字段的声明类型绑定，基类 private 字段不通过
派生类型猜查；它只服务恢复填充，不改变 DTO body、Schema 或对象身份的解释规则。
DynamicMethod 保留为有具体需要时的备选，不为该能力引入第二套通用序列化后端。

不选择“生成 DTO 初始化构造器，再逐个普通 new”作为通用恢复路径：普通 new 会创建另一个实例，
不能填充已登记的占位对象，无法直接闭合一般循环引用；特殊构造器调用还会带入基类构造链及
字段初始化表达式，增加恢复语义。现有 Probe 与 .NET 10 机制验证见
[无构造器分配与 readonly 写入证据](DurableGraph-lab-notebook.md#2026-09-07无构造器分配与-readonly-实例字段写入)。
标量/string 的 SG Hydrate 与升级续写实现范围见 [DB-033](design-branches/0033-upgrade-restore-resave-batch.md)；
一般对象互引与循环恢复的证据见 [DB-034](design-branches/0034-durable-reference-graph-batch.md)。

Transient 指索引、缓存、反向查找等非持久内存状态；排除这些字段的持久化仍是产品职责。
建议宿主重建保持便宜、确定、幂等；昂贵的 LLM 摘要或 embedding 属于 Derived builder 的目标场景，
不作为 MVP 同步加载的前置依赖。

初期按单 Repository 单 writer、捕获期间领域图静止的模型探索，由宿主协调 mutation 与 Capture。
DTO 冻结边界不自动保证并发 Capture 的一致性。commit 决定哪个候选成为持久权威，
不承诺撤销已经执行的任意 C# 领域操作。

## 4. 成功标准与依据

可用产品应能用普通 C# 修改领域图，在无 ChangeTracker 的条件下保存真实差异，
跨进程保持共享/循环及规定的身份语义，按 exact Schema 显式升级历史数据；
Artifact 可精确引用并 lazy load，Derived 可删除重建，故障后只有一个可裁决权威状态。
这些是整体成功目标，不能因为某个局部 codec 或 Probe 已通过就宣称全部达成。

旧 StateJournal、SessionJournal 和 DramaBoard 分别提供图持久化、权威/派生分层和实际领域压力。
复用其可验证机制，不继承专用字典 DSL、ChangeTracker、旧 API 或格式兼容负担。
活跃证据与历史实验的入口见 [设计索引](design-branches/README.md)；
本文只在目标或长期设计决定改变时更新，实施进度不追加到这里。
