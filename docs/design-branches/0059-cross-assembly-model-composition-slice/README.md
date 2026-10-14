# DB-059：跨程序集模型目录与类型组合

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0059-cross-assembly-model-composition-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：外部 nominal/动态参数、独立库升级与 App/Host 不变的证据见 §9；固定外部 inline 与 base 分别由 DB-060/061 承接，不把当时拒绝写为今日限制。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：已实施，G0–G3 验收通过；用户于 2026-09-10 授权，结果见 §9 验收账本。
> 日期：2026-09-10；调查基线：`737fbb4`（DB-058 已实施）。
> 当前能力：[PROJECT-STATE](../../../src/PROJECT-STATE.md)；总体目标：[目标设计](../../DurableGraph-target-design-v0.md)。

## 1. 问题与选择

**让一个应用可以把领域模型放进独立库，再用普通 C# 引用将它们组成同一份可保存、升级和恢复的 World。**
第一份消费者的依赖方向为 `Host → AppModel → DomainLibrary`；Host 显式登记各库提供的能力。
对象图可以共享和循环，程序集依赖不需要循环。

DB-058 后，常用标量、record、泛型、Nullable、数组/List/Dictionary 已提供较完整的单编译建模能力。
本片选择一个实际项目组织缺口，不继续按 BCL 名单排期：

| 候选 | 本轮判断 |
|---|---|
| 跨程序集模型组合 | Runtime 已按 CLR Type / DefinitionId 登记和闭合，存在不用导入外部 exact history 的边界；推荐 |
| ValueTuple | 能实现，但要扩展多个 child 的 exact 布局、历史参数来源及 Upgrade 闭包；record 已能表达复合值，保留 [DB-057 §8](../0057-bcl-scalar-value-slice/README.md#8-valuetuple-后继保留的问题) |
| SchemaStore 复用 StateStore | Dictionary 已使重访条件部分成立；仍要确定框架元数据布局、引导及联合视图。单独替换日志后端不能直接得到回滚/分叉，另片研究 |
| DateTime、更多 BCL 或性能优化 | 各有语义选择或测量触发；不作为本次拆包工作前提 |

最小成功标准：**两个独立编译的模型程序集能组成一份图，冷读后保持引用身份并继续增量保存；
只升级被引用库的对象 Schema 时，引用方的 nominal-only Schema/history 不变。**
同时要证明固定外部 inline/base 的未开放边界仍明确拒绝。

## 2. 实施前事实与切入点

- [字段识别](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs)的 `TryGetNominalReference` / `TryGetInlineValue`
  都要求目标与 owner 同程序集；[HasDurableTypeShape](../../../src/DurableGraph.Generator/DurableGraphGenerator.Ancestry.cs)
  还要求 source syntax / partial，不能直接拿来验证 metadata 类型。
- [TryGetTypePattern](../../../src/DurableGraph.Generator/DurableGraphGenerator.TemplateHistory.cs)在最后的 named 分支统一拒绝外部类型；
  但这个函数也用于仅需 nominal 的泛型参数，限制比实际执行能力更宽。
- [Family 当前投影](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs)通过
  `ResolveCurrentValue(typeof(...))` 取得状态、操作和投影类型，再闭合静态泛型代码；
  [历史工厂](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericFactories.cs)从完整槽绑定取得同类操作数。
  这些动态表示参数不要求生成代码点名外部 DTO。
- [StateModelRegistry](../../../src/DurableGraph.Persistence/StateModelRegistry.cs)及
  [StateModelSnapshot](../../../src/DurableGraph.Persistence/StateModelSnapshot.cs)已有显式登记与跨 Type 闭合；
  不按程序集扫描或选择 reader。引用的 exact 版本仍从被引用对象自己的 Base 取得。
- [BindSchema / MatchValue](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.cs)目前对 named 槽一律查询 Definition，
  而普通非泛型 SG 入口只登记 concrete Model。跨两种生成路径的 nominal 检查需要局部接通。
  [Upgrade 反推](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.Upgrade.cs)的 `InferSlotFromState` / `BuildSlot` 也有相同的 reference 分类需求。
- SG 自动选择 Family 的条件包含泛型、record、enum、Nullable、显式规则等；纯普通 struct 库不会因此获得完整 Definition 工厂。
  不能要求用户添加 dummy 泛型类型来改变生成路径。
- history 名义引用不要求外部定义文件一并在本项目出现；固定 base/inline 的 exact 依赖则要求完整历史。
  本片不改变这个区别，也不新增 wire grammar。

以上为实施前源码调查结论；后续独立程序集的产品回归与交付证据见 §9。

## 3. 支持边界：按所需知识划分

本片规则是：**允许外部 Durable 类型进入 nominal 类型表达和动态表示参数；
仍拒绝需要 SG 跨程序集静态展开固定 inline/base 模板的声明。**
这里的“动态”指冷路径闭合工厂，字段/元素 body 仍保持原有强类型静态调用。

以下 `Remote...` 定义在依赖库，`Local...` 定义在当前编译；相关运行时能力均须显式登记：

| 模型形状 | 本片支持 |
|---|---|
| `RemoteNode` 字段；已登记的外部派生实例 | 支持；字段保存 nominal + ObjectId，目标由自己的模型处理 |
| `RemotePoint[]`、`List<RemotePoint?>`、`Dictionary<RemoteKey, RemoteNode>` | 支持；容器本身独立拥有元素/键值的 exact 布局和升级选择 |
| `LocalBox<RemotePoint>`、`RemoteBox<LocalPoint>`，Box 为 durable class | 支持；Box 的参数值由统一目录闭合 |
| 本地 `InlineBox<T> { T Value; }` 字段闭合到 `InlineBox<RemotePoint>` | 支持；本地模板已知，T 的表示由动态参数提供；不能因它是 inline 而一律拒绝 |
| `InlineBox<T> { T[] Values; }` 或 phantom T，闭合到外部类型 | 支持；保留原 nominal 截断，不能平白增加 external exact 依赖 |
| `RemotePoint` / `RemotePoint?` 直接值字段；本地 struct 内固定声明 `RemotePoint` | 暂不支持；需要固定外部 inline 版本及其编译期历史材料 |
| 直接值字段是外部定义的 `RemoteInlineBox<LocalPoint>` | 暂不支持；外部根 inline 模板本身需要静态导入，不同于已知本地模板的动态 T |
| 本地 class 继承外部 durable class，包括泛型基类 | 暂不支持；本地生成器需要展开完整基类字段/历史段 |

允许上述形状继续嵌套既有受支持构造，保留既有 depth/node/arity、数组 rank、泛型约束与 comparer 边界。
外部类型是已有支持的顶层 public class/struct/record struct/enum；不开放 nested、record class、ref struct、
object/interface 通配字段、boxed identity、数组协变或任意未标记 CLR 类型。库内部自己的支持边界保持原合同。

程序集依赖单向不限制对象环：例如库的 `RemoteBox<T>` 可在应用闭合到 `LocalNode`，LocalNode 又引用该 Box。
外部基类与派生类若都由同一个依赖库生成，应用把派生对象放进外部基类引用槽，仍属已支持多态；
这不等于允许应用在外部基类上生成新的派生模型。

## 4. 生成与登记方案

### 4.1 分开识别本地定义和外部名义声明

本地定义继续执行 source partial、字段分类、exact DAG 等完整检查。
外部 metadata 声明只检查当前用途所需事实：正确的 DurableType 标记、有效 ID/version、kind/arity、
顶层可访问性、受支持 CLR 形状；class 须属于 DurableBase 链。不能用“没有 source syntax”证明外部类型非法，
也不能靠同名伪属性/同名 CLR 类型冒充框架合同。

`TryGetTypePattern` 的 named 操作允许使用这些 nominal 事实；直接 fixed inline/base 识别仍保持本地限制。
不导入外部字段、不据 metadata token/程序集版本构造持久 ID，不由标记推断运行时工厂已登记。
本片生成的 manifest 仍只包含本项目拥有的定义；不在消费方复制外部 Family/history。

### 4.2 显式导出已有 Definition 能力

新增布尔项目属性 `DurableGraphGenerateDefinitions`：

```xml
<PropertyGroup>
  <DurableGraphGenerateDefinitions>true</DurableGraphGenerateDefinitions>
</PropertyGroup>
```

设为 true 强制该编译使用现有 Family 生成路径，含纯非泛型 class/struct；未设置或 false 保持原有自动选择，
不强制退回普通路径。非法值给出明确诊断。通过包的 CompilerVisibleProperty 和 SG options 贯通，
不新建另一套生成器、DTO、运行时注册协议或格式版本。

已经自动使用 Family 的模型库不需此属性；仅普通 class Model 的库也仍可用自己的原登记入口。
纯普通 struct 库可通过此属性获得 current/historical value factories，方便参与数组和泛型闭合。

属性改变的是生成代码外观：旧 `__DurableState` / `GetSchema` 等内部调用可能需要迁到 Family；
不保证这些 helper 的 ABI。Schema ID、版本、完整布局和 history 内容不因切换生成外观变化；
必须以相同源模型的两种生成方式验证这个承诺。

### 4.3 每个库提供稳定的公开登记 facade

模型库用普通手写 C# 包住自己的生成入口，例如：

```csharp
public static class DomainCatalog {
    public static void Register(IStateModelRegistration models) {
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    }
    public static void RegisterReaders(IStateReaderRegistration readers) {
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(readers);
    }
}
```

Host 先调用 `DomainCatalog.Register(models)`、`AppCatalog.Register(models)`，再建立 Repository 会话。
选 comparer/数组/List/Dictionary 升级规则仍由当前宿主显式配置。
同一完整集合在首次 snapshot 前登记完毕；无依赖的登记顺序不应改变结果。

各程序集生成的 `DurableDefinitions` 同名。这个**编译单元聚合器**统一改为 `internal static`，
由库内的公开 facade 调用，避免 App 自身也生成同名类型时发生 CS0436/CS0433；不全局屏蔽这些诊断。
其 Register 方法可维持 public，Family/DTO/Definition 的现有可见性不变；不把此修改扩大为生成命名空间重排。
这是有实际重名消费者支撑的 helper 可见性调整，不保证原聚合器可被另一个程序集直接调用。
公开 facade 保留稳定入口，并继续自动登记本库生成的全部历史族和本地值规则，避免让用户逐个手写 Family 清单。
公开 facade 默认统一接受已有 `IStateModelRegistration`；它继承 `IStateDefinitionRegistration`，
既可包装普通 Model，也可包装 Family 入口，库以后切换生成路径时无须改变 facade 签名。
仅普通 class 库在 facade 内调用其可访问的原 Model 登记入口即可。
独立 exact DTO 读取使用 `RegisterReaders(IStateReaderRegistration)`；Family 同样登记完整 Definition，
普通生成路径登记现有 Readers。无需让只读目录接受 current Model 或引入新接口。
不新增程序集扫描、自动发现、类型加载插件、跨程序集 Upgrade 属性扫描或新导出清单。

### 4.4 普通 Model 与 Family 的 nominal 检查桥接

为已有两种登记来源补齐局部 nominal 检查，具体内部方法名称由施工决定：

1. 有该 Definition 时用它校验 kind/arity；错误直接拒绝。
2. **仅在 Definition 缺失、待校验槽确实是 named reference 时**，允许从完整 nominal TypeExpr 精确匹配的已登记
   concrete ReferenceObject Model 或独立 exact ReferenceObject reader 取得 nominal 证据。不得只按 DefinitionId 任取一个闭合实例。
3. 不调用目标工厂或 `ResolveCurrentModel` 递归闭合，不读取其当前 fields/base 来补历史模板，
   不从 CLR reflection 猜历史 kind；inline 及需要 template 的绑定仍要求完整 Definition。

它只证明声明的名义类别，不授予缺失 reader/Upgrade 能力。stored/current 引用合法性仍走各阶段的完整校验；
目标历史版本能否读取，由其自己的 exact reader 决定。存在但错误的 Definition 不允许被另一个 Model 掩盖。
普通匹配与 Upgrade 的 `InferSlotFromState` / `BuildSlot` 应共用这项 nominal 查询；
不能只修普通保存，让 generic owner 的历史 DTO 参数为外部普通 class 引用时仍要求不存在的 Definition。
reader 证据来自 snapshot 中已经显式登记的 reader，不执行工厂。只取其与版本无关的完整名义身份，
不承诺它能读取另一个版本；例如仅登记目标 v2 reader，仍不能读取目标 v1 对象。

## 5. 历史、版本和失败边界

- 每个模型库独立保留/Publish/Verify 自己的 `.dgschema`。Host 不合并外部历史成自己的声明，
  不从包内最新源码恢复旧历史。仓库内 DefinitionId 仍全局唯一，程序集名称不成为隔离命名空间。
- 只改变被引用 class 自身版本，不改变引用方 nominal-only Schema；目标 Upgrade 后仅目标及其实际升级的内容对象承担 Base 重写。
  引用方对象 body 沿用原 ObjectId，并不因此生成 Delta。
- **跨程序集不等于跨版本二进制兼容。** 不重新编译 Host 的见证须保持其使用的公共 CLR API/类型身份兼容；
  删除 Host 仍在签名中使用的 CLR 类型，不能指望历史 DTO 补回 CLR 加载能力。
- 动态参数闭合成外部 inline 值时，exact 布局仍属于所在 owner。依赖升版造成 owner 完整布局改变时，
  作者仍须按原政策递增受影响 owner 版本并提供 Upgrade；本片不承诺 SG 能跨独立编译自动发现所有闭合依赖变化。
  仓库内同 key 异形严格拒绝；两个空仓库的闭合历史保证仍沿 DB-038，不增加全局闭合历史账本。
- `InlineBox<RemotePoint>` 等合法动态闭合可能需要外部值规则；各库在自己的登记 facade 中登记已有规则，
  宿主通过现有 Runtime 选择，不让应用 SG 扫描外部方法或解释外部规则 marker。
- 缺少模型、值工厂、规则或历史 reader，仍在原有绑定/读取/升级预检边界失败，不返回半成品 World、不发布新 head。
  本片不把每种错误都升级为注册时全局扫描，也不新增“所有 source callbacks 均零调用”的承诺。
- 不改 TypeTag/TypeExpr、history v9、SCB1 v2、Base v4、Storage v3 或容器 grammar；
  外部声明的持久表示与同编译时一致，绝不写 assembly-qualified CLR 名称。

## 6. 分步施工与验收

### G0：真实 metadata 边界与生成选项

- 建立先生成并 emit 库 DLL、再只把 MetadataReference 交给应用 SG 的测试工具；禁止把库源拼入同一次 Compilation 冒充跨程序集。
- 见证当前直接外部引用被拒绝；实现显式 Family 开关，验证普通 class/struct、自动 Family、未设置/false 及非法值。
- 同模型 Auto 与强制 Family 在相同字段值/ObjectId 下的 Schema/history、Base/Delta body 字节一致；
  DTO CLR 类型可以不同，不能混用两种生成模式的 DTO 实例或提交基线。旧已接受 history 文件名/hash/bytes 不被重写。
- 测试开启选项的真实 NuGet consumer 能拿到 compiler option，不能只用测试 harness 注入。

### G1：nominal 识别与混合登记

- 外部 metadata 分类和两种 nominal 证据来源分别实现、分别审阅。
- 两向泛型组合、本地 generic inline 的动态外部参数、Nullable/enum/record 参数、数组 rank 2–4 与 List/Dictionary 组合均有代表性见证。
- Family consumer 引用普通非泛型库 Model；全量登记后交换登记顺序；共享/循环、readonly 字段及实际外部派生实例恢复。
- 两个 Family 模型程序集及 Host 经各自 facade 编译调用，无 CS0436/CS0433，不借 NoWarn、extern alias 或程序集扫描规避重名。
- 拒绝直接外部 fixed inline/base、嵌套 fixed inline、未标记/错误标记、wrong kind/arity、缺失工厂。
  错 Definition 不回退，完整 nominal 不同的 concrete Model 不冒充目标，现有 unsupported 构造负例继续生效。

### G2：持久图与各库独立历史

- 同实例多次 Commit，child-only 修改不改 owner 的 ObjectId 槽；冷重开、NoChange、普通 Delta 与断链 Remove。
- 库内对象 v1→v2，引用方 World 仍 v1，World history 完整 bytes/hash 不变；读取旧 exact DTO、执行显式 Upgrade、
  升级对象强制 Base，随后恢复 NoChange/Delta。缺目标历史 reader/升级能力明确失败，不由当前模型兜底。
- 普通库 Model 作为 Family owner 的泛型引用参数，覆盖显式历史 DTO 反推和中间版本 Upgrade；
  验证新增 nominal 查询并非只对普通 BindSchema 有效。
- 本地 generic inline / 容器的外部值布局变化：遵守 owner 升版/显式值规则，包含空容器预检；
  故意漏升 owner 在已有仓库拒绝同 key 异形，不声称两个空仓库也能提前发现。
- 既有 complete exact requirement set 的缓存晚登记冲突不因程序集边界减弱。

### G3：真实包交付与使用示例

- 在 [PackageConsumerProbe](../../../experiments/PackageConsumerProbe/README.md) 新增模型库 nupkg + AppModel + Host，
  模型库与 AppModel 各有独立项目、history 目录及公开 facade；纯 Host 无 Durable 声明时不需要 history 目录。
  应用通过 PackageReference 消费模型库，不靠 linked source、friend assembly 或内部库文件。
- 至少两代包；保留一条 **AppModel/Host DLL hash 不变、替换兼容的模型库 DLL** 的运行见证，
  证明 nominal-only 消费者不必为了目标 Schema 升版而重新生成。其他涉及外部值的泛型组合可正常重新编译，勿混称二进制兼容。
  这条见证固定库的 assembly identity、facade 签名及消费者使用的公共 API；模型库 DLL hash 必须实际变化。
- 在模型库内保留一次删除旧 inline CLR 的历史恢复见证；不删除未重编译消费者仍引用的公共 CLR 类型。
- README 给最短登记、Create/Load/Commit、history 发布和失败边界示例，说明固定外部 inline/base 的暂不支持及可用组合。
- 主线程串行运行根 solution build、完整 tests、新包见证及相关 Generic/ValueUpgrade/Nullable/CompositeDictionary/Record/TemporalScalar 包回归；
  不要求重复无关算法跑分。最后独立审查和本地链接检查。

## 7. 委派与范围控制

建议依赖顺序为 G0 → G1 → G2 → G3；可并行的文件所有权：

- SG 小组：metadata nominal 分类、Family 选项、生成测试；包 props 的具体归属先与集成者固定。
- Runtime 小组：仅 nominal 证据桥接和针对性测试，不动 exact 绑定/Upgrade 的业务规则。
- 集成见证小组：独立编译测试与两代模型库/应用包。等待 SG 接口固定后接入，不并发运行 Windows .NET 构建。
- 主线程：验收标准、版本/历史边界、包构建整合、文档和最终提交；独立 reviewer 核对调用方没有偷偷依赖外部 DTO/history。

本片不顺带统一删除普通 SG 路径、不复制两套完整工厂、不引入跨程序集固定 exact 模板 ABI。
如动态组合实际需要静态导入外部 exact history 才能完成，先停在该反例，重新比较支持边界或专门外部模板协议；
不能扩大扫描范围或使用 latest 来掩盖缺失。

## 8. 规划阶段设计审查与证据

- 默认模型独立比较 Tuple、跨程序集、SchemaStore 自举，推荐跨程序集组合；随后交叉检查普通 Model/Family 接缝和动态 inline 反例。
- 一路独立只读源码调查确认 Tuple 的多个 child exact/version、参数来源及 Upgrade 遍历仍需结构扩展；
  未把这个成本误记为 SG 无法支持 Tuple。
- 设计收敛新增了普通 Model 的 nominal 证据桥接、纯普通 struct 库显式 Family 导出、
  本地 generic inline 动态闭合的正例，以及固定外部 inline/base 的明确反例。
- 交叉审查将同一 nominal 查询延伸到 Upgrade DTO 反推，并将同名聚合登记器限定为程序集内 helper；
  对外经库的 facade 聚合，不让新拆包用户承担生成器重名警告或手工列举历史能力的维护负担。
- facade 默认用已有 `IStateModelRegistration` 同时包装两种登记路径，保持公开签名稳定；
  生成内部仍保留较窄的 `IStateDefinitionRegistration`，不新增接口。
- 独立终审未发现阻塞；上述 nominal 推导、聚合器可见性及 facade 签名建议已纳入。
  文档检查：4 份 Markdown、238 个本地链接、14 个锚点，0 错误；文档专用变更未运行 .NET 构建/测试。
- 本节只记录规划阶段源码支持的可施工推断；实施结果见 §9。

实施后的代码、测试、包与最终评审结果集中在下一节；活动文档只更新能力摘要与链接。

## 9. 实施合同与验收账本

实施基线 `af20b9c`，工作区干净。主线程负责集成和 Windows 串行 .NET 验证；不修改上游工程。
本次实施仅推进 §3–§6：external nominal / 动态参数、显式 Family 生成、统一 nominal 证据和真实分包历史。
固定外部 inline/base、跨程序集规则扫描、Tuple、自举、wire/history 版本变化均不在范围内。

| 要求 | 所有者 / 实施位置 | 验收 | 状态 |
|---|---|---|---|
| G0/G1：metadata 识别、显式选项、内部聚合器、固定 exact 拒绝 | SG 小组；Generator 与包 build assets | 独立 emit/MetadataReference、选项/字节对照/负例 | 已验证 |
| G1/G2：普通 Model/独立 reader 的 nominal 证据及 Upgrade 共用查询 | Runtime 小组；BindingContext/Snapshot | Definition 优先、不运行目标工厂、完整 nominal、历史推导/公开只读入口 | 已验证 |
| G1/G2：独立程序集组合、持久图、版本变化及失败 | 集成测试小组；新增跨程序集 tests | 共享/循环、双向泛型/inline、空集合、升级续写 | 已验证 |
| G3：模型包、AppModel/Host、独立 history、稳定消费者 DLL | 包见证小组；PackageConsumerProbe | 两代包、旧 CLR 删除、hash/历史/冷重开 | 已验证 |
| 整体验收、独立审查、文档与提交 | 主线程 / readonly reviewer | root build/tests、相关包回归、diff/link 检查 | 已验证 |

持久格式、引用目标版本独立、失败不发布、动态 inline 原有同 key 一致性，以及所有历史能力的保留是跨小组共同不变量。

实施澄清：公开 `RevisionDecoder.Read` 使用只有 reader/Definition 的目录，原稿只允许 concrete Model 提供 nominal 证据会漏掉该入口。
源码确认 StateReaderBinding 构造已要求 ReferenceObject；独立审查认可使用完整相同 nominal 的已登记 reader，
并保留 Definition 优先与实际 exact reader 校验。此局部补齐已反映到 §4.3/§4.4，不扩大 Upgrade 或 current 模型权限。

### 实施与验证证据

- SG：新增 `DurableSchemaGenerator.CrossAssembly.cs` 只解释外部 metadata nominal；显式选项和 DG0022 诊断复用现有分派，
  包 props 提供 CompilerVisibleProperty；聚合器 internal，不复制外部定义。
- Runtime：`GetNamedDeclarationKind` 为 protected virtual 冷路径查询，Snapshot 使用已有 Model/reader 目录；
  未增重复索引，未修改 exact requirement set、对象 codec、SchemaStore、Storage 或历史格式。
- 新增测试分为 [SG 与字节对照](../../../tests/DurableGraph.Tests/CrossAssemblyGeneratorTests.cs)、
  [独立编译工具](../../../tests/DurableGraph.Tests/CrossAssemblyGeneratorTestSupport.cs)、
  [图保存恢复](../../../tests/DurableGraph.Tests/CrossAssemblyGraphTests.cs)、
  [外部值历史](../../../tests/DurableGraph.Tests/CrossAssemblyHistoryTests.cs)、
  [名义查询与公开只读入口](../../../tests/DurableGraph.Persistence.Tests/CrossAssemblyNominalBindingTests.cs)。
- 基线 root build：0 警告/错误；基线完整 2290 项通过（Runtime/SG 1354、StateStore 618、Serialization 163、Storage 155）。
  实施后最终 root build：0 警告/错误（10.43 秒）；完整 **2337 项通过、0 失败/跳过**：
  Runtime/SG 1389、StateStore 630、Serialization 163、Storage 155，Runtime/SG 3 分 25 秒。
  新增 47 项展开测试：SG 30、跨库图 2、外部值历史 3、nominal/独立只读入口 12。
  日志在 ignored `obj/db059-baseline-*` / `obj/db059-final-build.log` / `obj/db059-final-tests.log`。
- 新 [CrossAssembly 包见证](../../../experiments/PackageConsumerProbe/CrossAssemblyConsumer/README.md)首次运行通过。
  独立 Domain/App 模型包、纯 Host；Domain history 2→4，App history 始终 1，旧文件名/hash/完整 bytes 保留；
  V2 删除旧 inline CLR，目标单独升级 Base 后恢复 NoChange/Delta。Domain DLL 改变而 AppModel/Host DLL 完全不变。
  工件位于 `experiments/PackageConsumerProbe/obj/cross-assembly-20260910091234-31348-17312ea4`，
  `binary-compatibility.json` 保留 DLL/完整 history 证据；运行时包版本 `0.0.0-cross-assembly-e2e.20260910091234-31348-17312ea4`。
  Domain assembly identity 固定为 `Atelia.DomainLibrary, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`。
- 独立审查无阻塞，另独立重算了两个执行目录的 DLL hashes。审查不替代主线程测试执行。
- 同一份新运行时包下的 Generic、ValueUpgrade、Nullable、CompositeDictionary、Record、TemporalScalar 六条既有包回归全部通过，
  日志为 `obj/db059-package-<名称>.log`。新包见证与六条回归均由主线程串行执行。
- 最终文档检查：9 份 Markdown、500 个本地链接、49 个锚点，0 错误；staged diff whitespace 检查通过。
- 集成期间修正了新增测试的构造入口、原始字符串插值、Publish 返回值及缺 history 负例；
  保留普通/Family NoChange 的零 bitmap body，以 HasChanges 判断变化，没有为测试改写持久行为。

本片没有未完成的范围内 TODO；固定外部 inline/base、跨程序集规则发现和独立闭合历史账本继续由路线图维护。
