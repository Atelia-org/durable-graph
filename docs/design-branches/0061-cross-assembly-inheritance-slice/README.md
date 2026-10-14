# DB-061：跨程序集继承与基类状态投影

> 整理说明：2026-10-14 从 `docs/design-branches/0061-cross-assembly-inheritance-slice.md` 搬入问题入口；正文来源 Git `8ef9fca`。
> 适用性：已实施分片记录；typed base projection、Family 与两代真实包证据限本文基线，展开 DTO/body 与 State 格式当片保持。
> 本文原过程与测试数字保留当时基线；当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。
> 本文 Generator 内部名称保当片拼写；当前 Analysis／Family 名称映射见 [DB-092 A](../0092-type-namespace-refactoring/implementation-a.md)。

> 状态：已实施，G0–G3 验收通过；用户于 2026-09-10 授权完整实施，施工账本见 §10。
> 日期：2026-09-10；调查基线：`6123faa`（DB-060 已完成）。
> 续工入口：[PROJECT-STATE](../../../src/PROJECT-STATE.md)；总体目标：[目标设计](../../DurableGraph-target-design-v0.md)。

## 1. 选择与最小成功标准

**让 `Game.Character : ModelLibrary.Entity` 和对应的泛型继承能够保存、恢复和演化，
不要求应用知道基类的私有字段类型，也不把拆库变成一次持久布局迁移。**

领域继承在同一编译中已有产品路径；DB-059/060 已补齐跨库引用、泛型实参和固定 inline。
外部基类是这组组合中仍被整体拒绝的部分。推荐先补齐它，再选择新的值构造或存储后端。

| 候选 | 本轮取舍 |
|---|---|
| 跨程序集 Durable 基类 | 推荐；补齐领域库拆分，复用已有 exact base、展开 DTO 和只读 history 通道 |
| ValueTuple | 可以实现，但 record struct 已提供复合值建模；仍需多 child exact 槽、参数来源及历史 Upgrade 的扩展，见 [DB-057 §8](../0057-bcl-scalar-value-slice/README.md#8-valuetuple-后继保留的问题) |
| SchemaStore 复用 StateStore | 容器前置条件已较齐全，但员工通道、联合视图及冲突作用域尚需裁决；替换后端本身不提供分叉/回滚，见[路线图 §4.1](../../DurableGraph-research-roadmap.md#41-schemastore-复用-statestore-与联合版本视图) |
| DateTime、其他容器、性能优化 | 分别按真实建模和测量触发，不为延续开发而扩大同一片 |

最小成功标准：两个独立模型库及 Host，模型库各自维护自有 history；派生实例包含基类 private/readonly
字段和隐藏的泛型 inline 实现，能够 Capture → Commit → 冷重开，保留共享/循环。
基类布局升级后，派生族显式升版并转换完整 DTO；旧 exact 读取、升级首存 Base、随后 NoChange/Delta 贯通。

## 2. 当前事实与需要改变的接缝

这些是基线源码事实，不是新方案的运行结果：

- [ValidateCurrentDependency](../../../src/DurableGraph.Generator/DurableGraphGenerator.Ancestry.cs)
  要求 base 存在于本编译的 `DurableTypeModel` 列表；[现有负例](../../../tests/DurableGraph.Tests/CrossAssemblyGeneratorTests.cs)
  对外部普通/泛型 base 断言 DG0019。
- [GenericDomainFields / current projection](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs)
  递归全部祖先 CLR 字段，以本地 `types.Single` 查声明，调用声明层 internal 读写器；
  factory 还会为继承的动态字段发出 `typeof(field.DomainType)`。仅放宽 DG0019 不能使这条路径跨库。
- [FlattenGenericFields / MakeGenericLayout](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericState.cs)
  已依据 exact history 进行 base-first 展开、类型参数替换和状态参数去重。
  每段独立 FieldId、完整 leaf DTO、静态 Base/Delta body 可以继续使用。
- [StateModelBinding<TDomain,TState>](../../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs)
  已保存强类型 Capture/Hydrate 委托，但对象入口要求 exact CLR 类型。
  [StateBindingContext](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.cs) 已能解析基类 current model，
  `BindSchema(...).GetValue(...)` 能从完整槽取得 state/ops，无需领域字段 CLR Type。
- [DB-060 导入导出](../../../src/DurableGraph.Generator/DurableGraphGenerator.SchemaExports.cs)
  当前仅接受 InlineValue 模板；导入根及递归边未覆盖 BaseSchema。
  [Build reference 校验](../../../src/DurableGraph.Build/SchemaHistoryTool.References.cs) 的归属与闭包框架可复用，
  [ParseReference](../../../src/DurableGraph.Build/SchemaHistoryTool.cs) 的 inline 限制需扩展。

关键反例是 `public Base<T>` 内的 `private InternalPair<T>` 或 `private InternalPoint?`。
公开原字段 accessor 或公开带 `IValueProjection<InternalPair<T>, ...>` 约束的方法都会泄露不可访问类型。
问题应在基类库的投影边界解决，不要求用户把这些实现类型改成 public。

## 3. 建议范围

支持本地既有合法 Durable class 继承外部 public 顶层 Durable class，包括 abstract、泛型基类、
泛型参数重排/重复/闭合，以及跨多库的继承链。定义库使用现有 Family 生成路径；普通库可设置
`DurableGraphGenerateDefinitions=true`。消费方遇到外部 exact base 自动选择 Family。

基类 private/readonly 字段、internal inline/Nullable 实现及已有引用/容器槽均保留原合同。
只要求跨库可命名的基类入口可访问，不要求它的私有实现类型可访问。
基类与派生类继续是同一实例的声明段，不新增 ObjectId、对象行或独立提交单位。

本片不增加：外部未标记祖先、record class、嵌套 CLR 类型、接口/object 通配槽、数组协变、
自动跨库业务规则发现、基类自动逐段 Upgrade、全局 DTO 嵌套化或新的 State wire 格式。
普通非 Family 生成路径保留；不为缺少导出的旧普通库增加第二套跨库 fallback。

## 4. 当前投影：复用已绑定的强类型能力

### 4.1 方案比较与推荐

| 方案 | 结论 |
|---|---|
| 把声明层字段 accessor 改 public | 拒绝；私有字段可以用 internal CLR 类型，且消费者仍须闭合隐藏字段的 projection |
| 新增公开 SG Capture/Hydrate 泛型 helper | 可以继续设计，但若保留原约束会泄露隐藏类型；若内部重新闭合，则重复已有 model binding 工作 |
| 从 current model 取得窄的 typed base projection | 推荐；直接复用已有委托，基类库负责 CLR 字段和投影闭合，消费者只组合公开 DTO |
| 改成嵌套 base DTO 或整段运行时装箱 | 不选；前者迁移现有 DTO/Upgrade API，后者增加装箱与类型擦除，均不是解决访问边界所必需 |

拟新增的最小形状如下，名称可在施工中局部调整：

```csharp
// 生成基础设施；不是对象登记、读取或升级入口。
public sealed class StateBaseProjection<TBase, TState>
    where TBase : DurableBase where TState : unmanaged {
    public TState Capture(TBase value, CaptureContext context);
    public void Hydrate(TBase target, in TState state, ObjectReadTable objects);
}

// StateBindingContext 上的冷绑定方法。
public StateBaseProjection<TBase, TState> BindBaseProjection<TBase, TState>(
    DurableSchema expectedBaseSchema)
    where TBase : DurableBase where TState : unmanaged;
```

绑定取得本 snapshot 的 current model，要求 exact CLR base、完整 expected BaseSchema、实际 DTO 类型相同，
并复用 `BindSchema` 的完整依赖校验。返回对象只持有两份已闭合委托；在 leaf model 闭合时创建、随该 binding 复用，
不增加全局静态注册、按对象缓存或每字段 Type 查找。
`StateBaseProjection` 构造器为 internal/private，只能由上述已校验绑定路径构造；
不暴露无参构造或任意 Schema/委托拼装入口。示意代码省略构造器，不表示采用隐式 public 构造器。

能力须显式授予：`StateModelBinding<TDomain,TState>` 可加末尾可选参数 `supportsBaseProjection = false`，
Family SG 为其生成的 class binding 传 true。任意手工 binding 的旧委托可能只接受 exact 实例，
不能因为它有相同泛型签名就自动承诺可用于派生实例。未授予、错 DTO 或错完整 Schema 在交付 projection 前拒绝。

projection 接受非空的 TBase/派生实例，只 Capture/Hydrate 该基类的完整当前状态；
Hydrate 仍只服务未交付实例的恢复。它不 Allocate、不登记当前基类对象或分配独立 ID、不 Normalize，
也不调用基类 Upgrade。Capture 其中的引用字段仍会正常登记引用目标，包括指回实际 leaf 的引用。
现有 ObjectBinding 对整对象 `GetType() == typeof(TDomain)` 的检查原样保留，不能改成 assignable。

### 4.2 统一 Family 的当前生成路径

推荐所有 Family class 都采用“本声明字段 + immediate base projection”，包括同库继承。
这样可以移除 Family current projection 对全部祖先 CLR 字段的递归，不维护同库/跨库两套投影路径。
普通生成路径和 inline struct 投影不因此改成委托式操作。

生成的 Capture 顺序：调用一次 immediate base projection 得到 base DTO，捕获本声明字段，
按既有段顺序构造完整 leaf DTO。Hydrate 从 leaf DTO 构造 base DTO，调用一次 base projection，
再填充本声明字段。多层基类由各自 binding 递归处理；不在 leaf 中再次遍历祖先领域字段。

完整 leaf DTO/body 继续 base-first 展开；FieldId/段号、DTO arity、历史模板、Base/Delta bytes 均不因拆库改变。
不能简单把两段 Delta 拼起来：现有 leaf Delta 位图仍由现有 body 统一生成和解释。

代价是每个继承边多一次已绑定委托调用及 base DTO 临时值复制；深链可能重复复制祖先前缀。
先接受这个局部成本来只维护一套 Family 当前投影，代码留一条实测后再优化的 TODO。
不把它宣传为性能改进，不因此添加池、DynamicMethod 或第二条“快速”投影路径。

### 4.3 泛型参数与隐藏类型的处理

把当前混合的参数用途分开，但不新增持久类型体系：

1. **state/ops 参数**：按既有完整 `GenericLayout.DynamicFields` 顺序，从 validated
   `StateSchemaBinding.GetValue(pattern, inlineVersion)` 获取，覆盖继承和本声明字段。
2. **current projection 参数**：只为本声明实际读取/写入的动态字段获取；同 dynamic index 选择一个本地代表。
   同一状态参数可能同时被基类和本地字段使用，不能因原代表字段属于基类就漏掉本地投影。
3. **base DTO 类型**：保留基类自身开放 layout 的参数顺序，再把每个 base 动态参数代表字段
   映射到 leaf 中对应的祖先字段，取该位置的状态类型；不是按 DTO CLR Type 反推或重新去重 base 参数。

例如 Base 的 T/U 是两项状态参数，`Derived<X> : Base<X,X>` 仍组合
`BaseV1<TState0,TState0>`，即使 leaf 只需要一个状态参数；闭合 `Base<int,int>` 得到
`BaseV1<int,int>`。两个不同 nominal 引用即使都变成 ObjectId，也不合并其语义或 base 参数来源。
参数映射以同版声明段/FieldId 的对应位置为依据，不能只以字段 CLR 类型匹配。
具体可取 base 动态参数代表字段的 base-first 展平序号 j，对应 leaf 的祖先前缀中同一序号 j；
即使经多层泛型实参替换，字段顺序不变，而 state 参数数量可以合流或变为具体类型。

消费方由此只命名公开的 base CLR 类型、Family DTO 及其状态参数；不会发出隐藏字段的
`typeof(InternalPair<T>)` 或 `InternalProjection`。fixed inline DTO 仍用已导入的公开 Family，
历史和当前 state/ops 类型仍须一致，不用当前值 factory 替代 stored-exact reader。

## 5. 只读模板与执行合同

复用 DB-060 的一个导出 attribute、一个 canonical reference manifest 及其候选摘要，
不引入另一种 metadata 文件或复制 `.dgschema`：

- Family 输出自有 ReferenceObject 与 InlineValue 的当前/保留模板；不重新导出 imported 模板。
- 导入根包括当前外部 base、owned history 中的外部 exact base，以及已有 inline/局部值规则端点。
  沿 BaseSchema 和固定 inline 边递归；nominal 引用仍不展开目标版本。
- base 边要求 ReferenceObject，inline 边要求 InlineValue，校验完整 kind/arity/version/owner。
  先让 referenced 自闭合，再核对 accepted + referenced，最后核对 current；保留 owned missing history 不可由 import 补齐的规则。
- 查 current base 时保留真实 metadata owner；独立库同 ID、错 owner、缺版本、缺祖先、循环/深度超界均拒绝。
  无需求的编译保留 early return，不为所有引用程序集建立全量历史目录。
- Build 只 Publish/count 自有 current candidate；零候选 Verify、history-only 外部祖先仍检查完整输入。
  retained 派生 reader 不要求历史基类的旧 CLR 声明还存在；但保留模板与执行能力的责任不消失。

建议执行合同按能力区分：既有 InlineValue 保持 contract 1，新增 ReferenceObject 使用 contract 2；
两者分别校验 kind，未知合同拒绝。reference manifest 容器仍为 v1，entry 内现有 contract 整数承载此区别。
这是新增 class 构建执行合同，不改 `.dgschema` v9、SCB1 v2、对象 Base v4、Storage v3 或容器 grammar。

ReferenceObject helper 核验不能照搬 inline 的 IStateOps 要求：class Body 当前并不实现该接口。
按 class 合同核对公开 Family、静态 `StateDefinitionBinding Definition`，以及每版以下材料的真实类型、
泛型参数对应和 ref/in 修饰，不能只按成员名或参数数量接受：

| 材料 | 最小要求 |
|---|---|
| `Vn<TState...>` | 公开 readonly 状态字段与展开 layout 的名称/顺序/状态类型对应；公开构造器按同一字段顺序接收这些状态类型，零字段用 default，不要求额外空构造器 |
| `BodyVn<TState,TOps...>` | 公开 struct 及已有 state/ops 约束；DTO 类型必须与该版 Vn 对应 |
| body 静态方法 | `Write(ref BinaryPayloadWriter, in DTO, DurableSchema)`、`Read(ref BinaryPayloadReader, DurableSchema) -> DTO`、`Apply(ref BinaryPayloadReader, in DTO, DurableSchema, bool requireChanges = false) -> DTO`、`Visit(in DTO, IStateReferenceVisitor, DurableSchema)`；并核对既有 `PrepareBase`、`PrepareDelta`、`StateEquals` 的对应 DTO/Schema 签名 |

DTO 构造器是跨库 Hydrate 拼回 base DTO 的必要入口，只有可读字段并不足够。
不要求 ref assembly 包含私有 `CreateHistorical` 等生成器内部 factory；公开 Definition 在运行时承载这份能力。
当前 base projection 的真实能力在 Runtime 冷绑定时核对。历史-only class 不要求已删除 CLR 的当前 projection。
没有新的自动程序集扫描；宿主仍显式登记全部需要的 Definition/reader/业务 Upgrade。

## 6. Schema 历史与升级责任

基类布局变化仍沿 exact base 传播：Base v1 → v2 后，受影响的 Derived 也显式升版。
即使 Derived 自己的字段没变，也不能继续使用旧版本绑定新 Base。普通 nominal 引用方不跟随升版。
隐藏 inline 的版本变化同样经 Base 再传播到 Derived；泛型动态实参仍沿现有仓库内一致性边界，
不新增闭合历史账本，也不宣称两个空仓库之间具有全局 key 等价保证。

历史读取直接从 imported exact base 模板生成当前库自有 leaf reader，读回当时完整 leaf DTO。
Upgrade 继续只执行 leaf owner 的显式相邻版本链，不自动执行每层基类的 owner Upgrade。
业务可以显式调用库提供的普通转换函数或已有值工具；这属于作者选择，不增加规则发现机制。
若对象实际就是非抽象 Base 实例，它仍按自身独立 owner 的升级链处理。

两代包可以重编译受影响消费者。不得把本片验收误写成“更换基类 DLL 后任何旧消费者无需重编译”。
缺历史 reader/模板/升级边仍拒绝；失败不交付部分 World，不推进发布 head。
callback 前预检沿既有**逐对象完整升级链**保证，不承诺整个图出现晚期错误时此前所有 callback 均未运行。

## 7. 施工顺序与可观察验收

批准实施后按 G0 → G1/G2 → G3 推进；G0 是拟议方案的首次编译执行验证。
G0 可以先接通最小 class export/import 原型来取得真实编译材料；完整错误输入矩阵与 Build 合同在 G1 验收，
不用先完成整个 G1 才能验证投影接缝。

| 阶段 | 工作与过关条件 |
|---|---|
| G0 接缝验证 | 最小 typed projection 与真实 metadata/ref assembly 的 abstract `Base<T>`；含 private readonly、`InternalPair<T>`、`InternalPoint?`。跨库 Derived 成功 Capture/Hydrate，consumer 生成代码不命名 hidden CLR；再验证参数合并/闭合。做不到先报告反例，不能删掉隐藏字段见证后称通过 |
| G1 模板/Build | 扩展自有 class 导出、base 导入根/闭包、按 kind 的执行合同核验；独立 Publish/Clean/Verify，不写 foreign history；错误材料和仅历史/零候选也明确拒绝 |
| G2 Family 投影与图 | 统一 own + immediate base，完成参数映射和显式 projection 能力；local/split 同 Schema、history、DTO arity 及 body bytes；完整引用图/readonly/无构造恢复、NoChange/Delta 和严格 exact 对象入口 |
| G3 历史与交付 | 两代多库真实 PackageReference、旧 CLR 删除、base/derived 升版、显式 leaf Upgrade、强制 Base 后 NoChange/Delta；根 build/test、相关旧包回归、独立审查及文档维护 |

必须包含的边界见证：

- abstract base、同库与跨库混合的至少三层继承，含基类/派生重复 FieldId；只分配实际 leaf，构造器/初始化器未执行。
- `Base<T,U>` 的交换、重复和固定实参；T 仅存在祖先、phantom T、两个不同引用槽同为 ObjectId、
  一个 dynamic index 同时用于基类和本地字段，hidden Nullable/generic inline，以及零持久字段的 base/derived。
- base 声明的引用指回派生实例，或 `List<Base>` 含多个登记派生对象，保留共享/循环；child-only 修改不制造 owner ID 槽变化。
- 只改祖先字段和只改 leaf 字段均产生正确 Delta；独立 golden 覆盖跨字节的 leaf 位图，禁止分别编码 base/leaf Delta。
- 直接整对象基类 binding 处理派生实例仍拒绝；未显式授予的手工 binding、错 TState、错完整 exact base 均拒绝。
- base private inline 的旧 CLR 删除后，旧 exact reader 与显式 leaf Upgrade 成功；每个 leaf 只调用自己选定的升级边，
  不能偷偷多调用 base Upgrade。缺边、漏升派生版本、错祖先材料、不同库同 ID 和晚登记依赖冲突仍拒绝。
- history-only 已保留的派生版本依赖旧 base，不能由 current 新 base 或复制到 consumer 的假 owned 记录补齐。
- 伪导出缺少 DTO 构造器、错字段状态类型、body 方法 ref/in 或返回类型不匹配时，生成器诊断拒绝；
  不以 ref assembly 缺少私有 factory 为错误，也不因公开成员同名就接受错误执行合同。

真实包采用 `BaseLibrary → MiddleLibrary → App/Host` 的传递链，可由独立 AppModel + Host 分开托管；
至少跨越两次继承边。各模型库维护自己的 history，删除旧的隐藏 inline CLR 和可替换的旧基类 CLR 名称，
由 retained DTO/template 解释旧布局，新版派生源码改继承当前基类。
使用真实 ref/ 编译资产、lib/ 运行资产，不用 ProjectReference、InternalsVisibleTo 或手工复制 history 代替。

主线程串行执行 Windows .NET 验证，建议复用既有 helper 和包 runner 组织方式：

- 根 `dotnet build DurableGraph.slnx`；相关生成器/Runtime/Store 测试和最终根 test。
- 新继承包两代验收；既有 CrossAssembly、InlineLibrary、Generic、ValueUpgrade 包线。
  其他包线按实际改动接缝补充，不无条件重复无关算法性能实验。
- 集成 diff、`git diff --check`、受影响 Markdown 本地链接/锚点；结果只在本片账本记录。

## 8. 分派与停止条件

建议一个 agent 负责 Runtime typed projection，一个负责 SG ancestry/export/投影，另一路负责 Build/reference 合同；
先冻结跨模块 API 与 entry 合同，再并行写互不重叠的文件。测试与真实包按生产接口稳定程度启动，
独立 reviewer 不修改产品文件。主线程保留候选裁决、G0 验收、共享文件协调和最终集成/提交责任。

若发现必须暴露基类 hidden CLR、按每字段反射/装箱、改变 leaf DTO/State wire、放宽同 key 一致性，
或执行额外基类 Upgrade 才能恢复，则停在具体反例处重新裁决；不要临时另建第二套执行后端。
规划轮没有运行新功能的 build/test；G0–G3 的实施结果见 §10。

## 9. 规划审阅记录

基于 `6123faa`，分别进行候选方向评估、受限接缝事实调查和独立投影方案审阅，再交叉检查泛型映射。
结论收敛到显式 typed base projection、统一 Family 当前投影、保留完整 leaf DTO/body。
最后审阅补齐 projection 内部构造权限、DTO 公开构造器、class body 真实签名和 G0 最小导入前置。
这些结论来自规划时的源码与设计检查，后续编译执行验证单独记入 §10。

规划轮只修改本分片、PROJECT-STATE、路线图与设计索引，并检查本地链接/锚点与集成 diff。
实际 build/test/包验证与偏差集中在 §10，不复制到其他活跃文档。

## 10. 实施合同与验收账本

施工基线 `5308fbc`，工作区干净。用户授权 §3–§8 的完整分片；不展开其他候选或后续性能工作。
冻结 `StateBaseProjection<TBase,TState>`、`StateBindingContext.BindBaseProjection`，
model 构造器末尾 `supportsBaseProjection=false`、Family 显式启用；整对象 exact 检查不变。
导出合同 InlineValue=1 / ReferenceObject=2，reference 容器 v1 / history v9 与 State wire 保持。
主线程负责所有 Windows dotnet build/test/pack 串行验证，子任务只在各自文件所有权内编辑。

| 要求 | 实施所有者/位置 | 验收 | 状态 |
|---|---|---|---|
| typed projection 与显式能力 | [StateBaseProjection](../../../src/DurableGraph/Runtime/Binding/StateBaseProjection.cs)、StateModelBinding | [8 项 Runtime 测试](../../../tests/DurableGraph.Tests/StateBaseProjectionTests.cs)：未授予、错类型/Schema、null、晚登记冲突、整对象 exact | 已验证 |
| Family own + immediate base、泛型映射 | [GenericProjection](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs) | [Generator 专项](../../../tests/DurableGraph.Tests/CrossAssemblyInheritanceGeneratorTests.cs)：metadata/ref hidden inline/Nullable、参数合流/重排/具体化/双 ObjectId；local/split golden | 已验证 |
| class 导出与 base 只读依赖 | [SchemaExports](../../../src/DurableGraph.Generator/DurableGraphGenerator.SchemaExports.cs)、[helper 核验](../../../src/DurableGraph.Generator/DurableGraphGenerator.SchemaExportHelpers.cs) | 同上专项：真实类型/构造器/ref 签名、额外约束、ref-like 拒绝、historical-only | 已验证 |
| reference 合同与自有 history 发布 | Shared protocol、SchemaHistoryTool | [31 项 Build 测试](../../../tests/DurableGraph.Tests/CrossAssemblyInheritanceBuildTests.cs)：contract2、accepted 先验闭包、零候选、归属/版本/kind/arity/深度拒绝 | 已验证 |
| 图/历史升级 | [图测试](../../../tests/DurableGraph.Tests/CrossAssemblyInheritanceGraphTests.cs)、[历史测试](../../../tests/DurableGraph.Tests/CrossAssemblyInheritanceHistoryTests.cs) | 4 项：引用循环、readonly、跨字节 Delta、旧 CLR 删除、leaf-only Upgrade、Base/NoChange/Delta、缺能力不发布 | 已验证 |
| 真实多库包与回归 | [InheritanceLibraryConsumer](../../../experiments/PackageConsumerProbe/InheritanceLibraryConsumer/README.md) | 两代独立 history、ref/lib；既有 CrossAssembly/InlineLibrary/Generic/ValueUpgrade | 已验证 |
| 独立审查与最终交付 | 主线程 / reviewer | 根 build/test、包线、diff/链接、文档 | 已验证 |

实施补充：

- 私有字段实现留在定义库，实际投影直接复用已有委托；没有新增反射逐字段访问或 DTO 装箱路径。
- 独立审查补齐零字段 base DTO 使用 default、helper 额外约束/ref-like 拒绝。
  struct/unmanaged 自带的无参构造能力不视为额外约束，避免误拒 metadata 符号。
- 两处测试集成问题已修正：PreparedDeltaBody 是引用类型，不能取 Nullable.Value；旧外部 base 负例改正例后，
  字段更名 OwnValue 避免 C# 遮蔽 warning，保留 FieldId/声明段，未抑制 warning 或放宽断言。
- 完全没有当前 Durable 声明或局部值规则的空库，不因剩余 history 自动生成全部恢复能力；
  本片历史-only 依赖沿原 Family 入口触发，不扩大 DB-060 的空库能力边界。

验证记录：

- 基线：根 build 零警告/错误，既有 CrossAssembly 116 项通过；全量旧基线 2406 项来自 DB-060 记录。
- 最终根 `dotnet build DurableGraph.slnx --no-restore` 零警告/错误；
  `dotnet test DurableGraph.slnx --no-build --no-restore` **2467 项通过、零失败/跳过**，新增 61 项。
  分布为 Runtime/SG 1519、StateStore 630、Serialization 163、Storage 155。
- 新继承真实包首轮及审查修补后的最终轮都通过，无 fixture 绕行；最终自包含产物为
  `experiments/PackageConsumerProbe/obj/inheritance-library-20260910125007-39028-1d6b0378`，
  Runtime 包版本 `0.0.0-inheritance-library-e2e.20260910125007-39028-1d6b0378`。
  BaseLibrary / MiddleLibrary / AppModel 的 history 为 3→6 / 1→2 / 1→2，旧文件名/hash/bytes 保持。
  两代删除旧基类与 hidden inline CLR、两个 leaf 各升级一次、base/middle 零次、两 Base → NoChange → Delta → 冷重开。
  主线程另核对最终 10 份 history bytes/hash 和 6 处消费者代际 ref/lib 选择。
- 新包保留 SDK Pack 的已知 NU5131 提示，未抑制；本片验证 PackageReference，不增加 packages.config 合同，说明在包 README。
- 复用最终 Runtime feed 顺序执行四条旧包线，全部通过：CrossAssembly（App/Host DLL 不变）、
  InlineLibrary（三库固定值历史）、Generic（三代泛型）、ValueUpgrade（保留值规则/旧 CLR 删除）。
  原型的普通生成路径、动态组合和已有 inline 合同均保留。
- 独立 Runtime 与 SG/Build 审查已关闭，无未解决阻塞；最终代码未改 State wire、history 格式或既有展开 DTO arity。
- 文档检查：10 份变更 Markdown、527 个本地链接、50 个锚点均通过；集成 diff 已检查。
