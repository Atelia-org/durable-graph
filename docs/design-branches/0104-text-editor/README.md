# DB-104 · 通用 TextEditor 与 DurableGraph 的集成边界

主线从 `f829f849` 起由注册叶取代内建 `DurableText`。领域模型采用 `Atelia.Text.TextEditor`，由文本包登记自包含叶对象的 codec。DurableGraph 掌握图中身份、准确前驱和发布，类型作者掌握冻结内容与编码。这条路线也服务于未来在不同来源上提供一致的 LLM 编辑工具。[主线迁移与验收](mainline-results.md) · [注册叶原型实验](registered-leaves.md) · [早期边界与算法对照](evidence.md)

继续研究登记方式、封装分配或第三方节点出边时，可从[两个后续研究方向](codec-followups.md)进入。文中给出了 C3 宽 Rope 的冻结源码、共存性能对照和共享节点带环的冷开样例。

## 总体方向

- `Atelia.Text` 拥有内存正文、冻结快照、串行编辑和同步全文访问。它可以依赖实际使用的兄弟项目，各项目间的依赖保持单向。内存草稿和文件正文可在图外使用同一编辑器。
- 一个叶对象可以有多条入边，并拥有独立的可变 CLR 身份。其持久内容自包含，内部没有需要 DG 管理的引用。两个字段或列表元素指向同一编辑器时，共享一个 ObjectId；分别打开两份图，各自得到独立可变实例。
- 后续的字面及正则 Search、ReplaceUnique、ReplaceAll 属于可复用的单篇文本操作。工具宿主解析地址、限制可见集合、控制编辑顺序，并区分工作副本已修改、图已提交和文件已写回。可用 Completion.Tools 的结构化与原始文本包装器比较多行输入；批量替换预览另做工具实验。[用户故事](../0103-durable-text-search/README.md#memo-编辑工具与批量替换预览)

## 第一片的设计问题

| 方案 | 得到的能力 | 要付出的成本 |
|---|---|---|
| 共享 Piece Table，图内和图外各用一个编辑器，实现相同工具接口 | 工具体验可以统一，现行 DG 领域类型及热保存路径可以保留。共存提交中的 `DurableText` 提供了该方案的图内基准。 | 同一个普通内存编辑器进入图字段需要转换。两套包装共用的底层操作需要跨程序集提供。 |
| 整个编辑器搬入文本库，授予 DG 访问内部实现的友元权限 | 同一个 CLR 实例可进图，持久化仍能利用每片来源；早期隔离原型给出了热编码和冷重放的对照。 | 图框架理解文本类型及其持久化机制；接入另一个用户类型仍需专门接线。具体协议与数据保存在[该轮证据](evidence.md)。 |
| **显式登记自包含的叶对象 codec** | 类型作者可在自己的包里使用私有表示。DG 用统一协议处理当前及将来的叶类型；用户可以直接把编辑器放进字段或受支持的外层容器。 | 生成器、当前 CLR 模型及历史 reader 都要能识别登记的类型；需要量出真实保存和恢复的成本。 |

## 第一片的协作合同

类型作者冻结稳定内容、准备 Base、对图提供的同对象准确前驱判断变化，并从有版本的 Base/Delta 链恢复独立实例。廉价的 Base 估算让 DG 把实际编码推迟到选中 Base 之后。没有可用差分时可要求写 Base，内容未变可以复用已有对象版本。DG 按图选择表示、编码记录头和前驱地址、安装实际字节计量。持久身份和版本随 Schema；冷读需要对应 reader，格式作者用历史字节样本维持语义。

文本的三个身份各有用途。公开条件编辑检查编辑器归属和逻辑内容版本，同版保存重绑后仍可用。保存基线的安装与捕获时的物理视图相关，捕获后的编辑要保留。编码 Copy 还需核对当前来源和**本图的准确前驱**，另一份相同长度的前驱可能通过全部范围校验却产生不同正文。[现行文本集成回归](../../../tests/DurableGraph.Persistence.Tests/TextEditorIntegrationTests.cs)

State 安装成功后，框架更新本次捕获共用的只读收据。文本在下次编辑或捕获时核对收据和物理视图，再整理片表。Event 捕获没有收据；丢弃候选、发布前失败和捕获后发生的编辑都能保持正确的基线。完整保存周期把延迟整理的开销计入下一次编辑。发布后、安装前故障可通过关闭故障仓、按应用持有的持久引用重开来恢复已发布的图；新图具有新的 CLR 身份。正式协议的发布阶段只操作框架状态。[发布结果](../../../src/DurableGraph.Persistence/RepositoryPublicationException.cs) · [故障回归](../../../tests/DurableGraph.Persistence.Tests/RegisteredLeafLifecycleTests.cs)

## 接入自己的叶类型

1. 给公开、顶层、非泛型的 sealed class 声明 `[DurableLeaf("应用拥有的名义 ID", 1)]`。在领域模型中直接以这个 CLR 类型声明字段。叶可以包含字符串、字节数组等私有表示；若其成员需要独立的图身份或恢复图中的其他对象，应作为含出边的模型建模。图根沿用现有的 `IDurableObject` 合同。
2. 派生 `DurableLeafCodec<TDomain, TFrozen>`。最小实现提供 `Capture`、`PrepareBase`、`ReadBase` 和 `Restore`。冻结值可以是私有类型，必须在后续编辑期间持续可读；每次恢复应产生独立的可变实例。只支持 Base 的类型可以从这四项开始。若实现 Delta，读取逻辑须消费整个 body，并保留输入的冻结前驱。
3. 为频繁更新的大对象实现 `PrepareDelta` 和廉价的 `EstimateBaseSize`。返回 `null` 要求本次写 Base，返回 `HasChanges == false` 表示能够复用前驱，返回有变化的 body 供 DG 在 Delta 和 Base 之间选择。估算仅用于规划，落盘按实际字节计量。
4. 由类型包暴露一个登记方法，将稳定的 codec 实例交给 `IDurableLeafRegistration.RegisterLeaf`。应用在打开仓库前对 `StateModelRegistry` 登记领域定义及所有用到的叶。可以直接参考文本包和独立位图的完整写法。[文本登记](../../../src/Text/TextEditorPersistence.cs) · [独立位图实现](../../../experiments/RegisteredLeafProbe/Bitmap/VariableBitmap.cs)

名义 ID 属于应用的整个持久化历史，版本标识该 ID 下的具体格式。每个 registry 可以检查本配置的重名。不同配置若用同一个 ID 和版本分别表示一个零字段对象和一个接受空载荷的叶，读取可能成功并恢复成另一种 CLR 类型。私有编码改变含义时，作者需要提升版本并保留旧版本 reader 和固定历史样本。框架按声明的身份、版本和 Schema 选择 reader，并检查字节是否完整消费。[叶协议](../../../src/DurableGraph.Serialization/DurableLeafCodec.cs)

## 长期无保存的正文

单基线给每个文本片段保留来源偏移，适合成功保存后物理整理、再做稀疏编辑的工作负载。多图交错编辑共享实例时，也可以从各图的冻结快照重新寻找相同区间。两条路线对片段压平和跨图来源的表现不同。[单基线与两快照差分](evidence.md#单基线与两快照差分)

纯内存草稿可能长期没有保存边界。在编辑批次之间整理能降低碎片和底串驻留。已有基线时重排需保留 Copy/New 来源，旧快照仍可保持旧底串存活。默认策略留待真实草稿负载测量。[碎片、来源和驻留](evidence.md#无保存编辑)

<a id="native-text-sources"></a>

## 旧实现与重跑入口

主线提交 `06c57dd4a7f3baf75fd89a7a7d592861fc61ce4f` 同时保有内建文本、注册文本和相同负载的对照程序。旧文档中的 Git 路径均可用 `git show 06c57dd4:<仓内路径>` 查阅。完整重跑可用 `git worktree add --detach ../db104-coexist 06c57dd4`，从该目录构建。原生编码器位于 `src/DurableGraph/Runtime/Text/TextBodyCodec.cs`；对应的图集成、分段 Base 和旧 reader 试验随这一提交保留。[双路对照与逐提交数据](coexist-results/README.md)

使用注册文本的新应用应重新生成领域 Schema 历史并创建新仓库。旧内建类型的专用标签及其 v10 文本历史语法已退出。现行字段使用 v9 的名义引用历史，落盘 Base 使用 ABI 5 的名义 descriptor；文本仍以分段 Base 和 Copy/New Delta 编码。日常接入见[文本包说明](../../text/README.md)，结果与适用范围见[主线验收](mainline-results.md)。
