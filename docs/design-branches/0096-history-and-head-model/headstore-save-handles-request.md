# HeadStore 请求：持久定位值与准确 Entry 起点的历史读取

> 状态：2026-10-17，已收到交付并完成[下游复核](headstore-delivery-review.md)：同库编解码、准确历史起点和位置校验可用；裸 Handle 不含完整库身份，与本请求 P1 有差异，建议由 DG scope 包装补齐并确认职责。下文保留发出时的要求，不将交付差异改写为已满足。
> 来源：[五个调用故事](save-handle-stories.md)。本请求取代[旧 R1](headstore-requests.md#2-r1持久化-headentryid-并按准确位置读回)的具体范围和“导入优先立即读取 Payload”建议，也收敛先前单列的准确历史起点问题。已实施的引用初始化 R2 保持。
> 请求形成时核对上游 `06ff3d9a80c938d0d1af7d4895cd26b9ec04b488`；新交付基线与实际包验证见复核记录。DG 未修改依赖 pin 或运行时。无旧 API/存档兼容要求。

## 1. 最小请求与动机

希望 HeadId 与 HeadEntryId 成为可跨进程保存的定位值，并让一个准确 Entry 能自然作为历史查询起点。下游需要的不是另一组“save”类型，而是补齐现有身份和操作的组合能力。

| 请求 | 下游动机 | 优先级 |
|---|---|---|
| P1：HeadId / HeadEntryId 公开持久化 round-trip | 继续上次世界线、固定书签、业务对象保存定位值、恢复分页边界 | 必需 |
| P2：从准确 Entry 开始的有界继承历史 | 固定历史窗口、翻下一页、旧 Event 点的最近 State 恢复 | 必需 |
| P3：使用导入定位值时的资格合同 | 解码不等于检查目标；读取、fork、历史起点不错误回退或误认位置 | P1/P2 的正确性组成部分，不是独立大框架 |

现有 `ReadHead`、`ReadEntry`、`ForkHead`、`PublishHead(s)`、`ListHeads` 和 `ListHeadOrigins` 尽量复用。世界线 Head 与其 current Entry 的实际 owner 可能不同：空 C 指向 A/a1；恢复 C 身份需要 HeadId，不能从 a1 的 HeadId 猜回。

## 2. P1：两类定位值的持久化

### 2.1 必须表达什么

- HeadId 保存持久 HeadStore 身份和准确 Head 身份；HeadEntryId 额外保存完整位置和长度。不能只保存 LocalId、offset、运行时 hash 或 CLR struct 内存布局。
- 受支持编码格式内，值经导出、关闭全部 owner、解码后仍按原等值规则比较。不同库同编号不混同；同 payload 的不同 Entry 不合并。
- 解码可在未打开仓库时完成，不访问文件、不读取 payload、不生成新 Head/Tag、不全库扫描。default、错误长度、错误种类、无效身份/位置表示按明确规则拒绝；无值由上层 nullable/字段状态表示。
- 提供可用于应用设置/数据库以及 DG 序列化适配的公开编码路径。优先一个明确的二进制 codec，文本可由应用 Base64 包装；不要求同时提供 JSON converter、URI 或多套格式。
- 表示的版本/种类边界必须可解释，具体如何标识由上游选择。原型未来可以显式不兼容；本请求不要求永久兼容所有版本。

### 2.2 优选的能力形状

```text
Encode(HeadId)      → bytes
DecodeHeadId(bytes) → HeadId 或结构错误
Encode(HeadEntryId) → bytes
DecodeEntryId(bytes)→ HeadEntryId 或结构错误

打开正确的 HeadStore 后：
ReadHead(decodedHeadId)
ReadEntry(decodedEntryId)
ForkHead(decodedEntryId)
ReadHistoryFrom(decodedEntryId, ...)
```

以上是职责示意，不要求重载名称、数组分配方式或新类。ID 本身继续是 owner 无关的普通值，不携带文件 lease 或“永久已验证”状态。纯值解码不要求先读取 Payload；需要“导入并读”的调用方可以组合，不宜成为全部操作的必经入口。

DG 会在自己的层做业务包装和标量序列化接线，不要求 HeadStore 引用 DG、反射其模型或加入通用 serializer 插件框架。

## 3. P3：扩大定位值入口后的正确性边界

这是对现有“只接受库签发 EntryId”合同的实质调整，不能仅公开一个 decoder 而不复核定位操作。

当前 S4 `[A-VS-READ-REVISION]` 依赖库签发/完整 tag 表恢复的位置、完整记录不复用与正常 writer 模型；完整帧 CRC 和尾 Fence 本身不是对任意镜像的主链成员证明。现有 `ReadExactPublication` 不能未经分析就被宣布能接受所有任意构造坐标。

请上游明确并验证：在支持的正常存储模型内，解码位置如何用于准确读、fork 和历史起点；非记录位置、header 位置、伪造长度、未成立 CU 等如何拒绝。继续检查 owner、库身份、必要 header、准确 frame/ticket、CRC/envelope/Fence 和 CU 资格；错误不能改读当前值或较早的健康记录。

本请求不引入防伪 token、密码学认证或对外部重写镜像的强证明。ID 不是权限、发布成功回执或永久健康证书；编码字节能够保存，不意味着使用时跳过检查。也不要为了保留“签发”措辞而默默增加每次从 EOF 或根扫描整条历史的成本。

优选同一值类型承担解码后的定位，操作时验证；若上游发现必须保留额外来源证据，请返回具体反例、所需合同及最小替代，再比较独立 locator 或窄的导入解析入口。该点是实施前复核项，不是假定现有验证已经足够，也不预先要求两套永久 ID 类型。

## 4. P2：准确 anchor 的继承历史

### 4.1 基础语义

建议增加等价于以下能力的入口，沿用现有同步 visitor 和双预算即可：

```text
ReadHistoryFrom(HeadEntryId startInclusive, HeadHistoryBudget budget, visitor, cancellation)
```

1. 首项为指定且已接受的准确 Entry，随后枚举其所在 Head 更早的已接受记录，再沿固定 ForkOrigin 接续准确来源前缀；不产生克隆项，不按 payload 去重。
2. anchor 必须成立。直接指定合法但未成立 CU 应报错，不回退；正常向旧遍历遇到合法未成立 CU 则按现有规则跳过。必要坏记录、来源错误与 I/O 错误仍传播。
3. 不先从当前 Head 尾扫描到 anchor，也不为只读查询创建临时 fork。源/子线后来追加不改变选定前缀，也不使该 anchor 之后无关后缀成为本次历史扫描工作。
4. 查询在语义上应与“从该点 fork 后读取继承历史”一致，但不进行创建/发布。可写打开的正常底座尾恢复仍按原合同；这里不承诺所有 I/O 都只发生在 anchor 之前。
5. 保留 MaxEntries 与 MaxWorkSteps 的不同含义、取消和 owner/清理边界。CU peer 等现有计费动作仍按 MaxWorkSteps 计入；该预算未完整覆盖的底座工厂/恢复等实际 I/O 成本单独说明，不承诺严格 O(K) 物理 I/O，也不让新增准确起点绕过工作计费。
6. callback 返回 false 表示当前项已接受；不额外探测来偷偷判断 HasMore。保留当前历史回调禁止递归历史与 mutation 的边界，退出后可用保留的 ID 调用 fork。

入口只沿 anchor 自己的历史读取，不要求另传“原始世界线”并证明 anchor 属于它。DG 的选择上下文、业务资格和界面状态由 DG 负责；不借此增加通用祖先判断 API。

### 4.2 最小分页不需要持久 cursor

首轮从 e100 读至 e81 后主动停止；下一页从 e81 inclusive 开始，调用方只跳过首个相同 ID，再读 e80 及以前。重复 anchor 的交付与资格工作应计入预算。提供 exclusive-start 是可选便利，不是必需能力。

完成、主动停止、预算耗尽、取消、错误必须保持可区分；不要求统一成新的大结果类型。沿用 `HeadHistoryEnd.Complete / VisitorStopped` 与失败 Result/异常通道也可以，只要范围和已交付项语义明确。停止不证明还有下一项；允许下一次得到空的完成页。

最后已交付 Entry 只记录输出进度，不保存扫描未成立 CU 的中间工作。工作预算耗尽且无新 Entry 时，固定同预算重试可能无进展；本请求允许提高预算或明确中断，不要求任意小预算都可恢复前进。若未来需要这种保证，再单独研究物理/CU 工作进度 cursor 或查询会话。

DG 若筛选 State/Event，继续位置取最后处理的 Entry，而不是最后展示的记录。下游可以在一页调用结束后释放所有读取资源，再等待 UI，不要求跨页保留枚举器或 lease。

## 5. 可发现性与不要求新增的接口

发现链是：应用选仓库 → DG 正式世界线 → HS current → 准确起点历史 → 所选 Entry 的读/fork。已有 `ListHeads` 和来源声明图作为底座，DG 处理管理 Head 排除、标签、成员资格与小摘要。

因此本请求不要求全库 Entry catalog、唯一世界线名称索引、自动路径发现、无上下文 `Next(entry)`、按任意摘要字段查询或全库初始化审计。一个位置可能通往多个分支，不存在天然唯一的“下一存档”。

`ListHeadOrigins` 当前给出未经 source 内容检查的 HeadId/ticket 声明。五个故事可从 current/history/bookmark 获得准确位置，不要求本轮额外提供 raw ticket 构造器；以后若需要直接点击来源边跳点，可讨论窄的来源解析便利入口，不要求应用手拼 codec 字节。

HeadSetTag 仍用于捕获所选 Heads 的当前位置集合，不改为接受任意历史 Entry 集合。单个固定书签由应用/DG 保存定位值即可，名称与替换规则留在相应层。

## 6. 最小验收矩阵

| 见证 | 必须观察的结果 |
|---|---|
| 保存空 C 的 HeadId 与其 current=a1 的 EntryId 后冷重开 | 两个值分别恢复；向 C 发布不误写 A；a1 仍可准确读取 |
| codec 无 owner 执行 | 编解码不打开文件、不读 Payload；格式合法但目标不可用在使用时失败 |
| 两库同 LocalId、同位置不同长度、同值两次真实提交 | 身份不混同；错库/default/畸形拒绝；实际读取不跳过长度和资格检查 |
| 导入非记录/header/错误 ticket、坏目标、未成立 CU | 按上游明确的正常存储模型拒绝，不回退、不制造 fork 输出；覆盖 P3 新输入来源 |
| 旧 a1 书签后 A 继续追加 | 读/fork/history 仍从 a1；不扫描新增的无关后缀来寻找 a1 |
| C 从 a1 建立，分页跨 C→A，A 又有 a2/a3 | 无克隆、无重复/漏项，不带入 a2/a3；同值真实保存仍分别交付 |
| 页界在根、第一条本地 Entry、成立 CU | 重读 anchor 的预算正确；停止与完成不混淆；允许空的末页 |
| 连续未成立 CU、预算耗尽零项/部分项、取消 | 不宣称完整或没有更早记录；固定预算无进展可观察；已交付结果边界明确 |
| visitor 内请求 fork，与退出后 fork | 前者遵守现有重入拒绝；后者使用保留 ID 正常执行 |

DG 另行验收：SaveId 业务包装拒绝管理 Entry、State/Event 最近 State 区分、准确 Schema 恢复、正式创建可见性、业务模型内定位值序列化、工具副作用边界；不要求 HeadStore 测试这些领域行为。

## 7. 建议反馈与实施顺序

先复核 P3 输入来源与位置资格，收敛 P1 两类编码；P2 可先以现有已取得 ID 开发准确起点遍历，再组合导入 ID 验收。两个能力独立有用，不将所有 DG 重写作为上游前置。

请上游反馈能接受的能力形状、P3 的具体限制、最小交付范围及必要替代。没有调用收益的便利方法、通用 cursor、全局索引可以不做。具体类型/签名/布局由上游按其现行规范设计，DG 随后据已实施合同接线。

## 8. 当前证据

- [HeadId](../../../../atelia-storage/src/HeadStore/HeadId.cs)、[HeadEntryId](../../../../atelia-storage/src/HeadStore/HeadEntryId.cs)：库身份/完整 ticket、纯值等值、私有构造与 internal 签发。
- [ReadEntry](../../../../atelia-storage/src/HeadStore/HeadStore.Entry.cs)、[ForkHead](../../../../atelia-storage/src/HeadStore/HeadStore.Fork.cs)：现有准确读取、来源资格与新 Head 返回值。
- [历史 API](../../../../atelia-storage/src/HeadStore/HeadStore.History.cs)、[预算](../../../../atelia-storage/src/HeadStore/HeadHistoryBudget.cs)、[终止原因](../../../../atelia-storage/src/HeadStore/HeadHistoryEnd.cs)：当前只有 HeadId 起点，内部已有来源 anchor 逆扫，预算失败与主动停止分开。
- [S4](../../../../atelia-storage/docs/HeadStore/04-versionstore-publication.md)、[S5](../../../../atelia-storage/docs/HeadStore/05-versionstore-names-and-indexes.md)、[bytes 公面](../../../../atelia-storage/docs/HeadStore/11-versionstore-root-payload-bytes-api.md)：已签发位置前提、正常存储模型、声明来源与 exact Entry 的区别。

本稿经只读并行分析、主线程源码复核与独立反向审阅形成；上述验收是待实施要求，不是已执行结果。
