# DB-096：给 HeadStore 的增强请求与 fork 候选

> 状态：2026-10-17，供上游后续设计与实施使用。本轮只做源码核对和方案分析，未改上游、未实施新格式。
> R1 是用户认可的增强需求；R2 已由上游实施于 `474dbe4ddfc12898a894c157f72ac5a44a7bcc80`，DG 接入尚未验收。本文保留对比依据与 DG 需求，不替代上游正式合同。不承担兼容旧格式/API 的要求。
> 背景：[用户故事](user-stories.md)、[概念入口](README.md)。原比较基线为 `731eef7cd3158572532fbeea7ba95a79213a7328`；§3 的旧 API/编码描述保留这个含义，已实施结果见正式合同。

## 1. 需求与当前设计自由度

DramaBoard 要求父子世界线分别可继续，从历史存档分叉，并按选中存档的实际归属建树。世界线标签相对固定；存档摘要随每次提交发布。候选将一条世界线映射到一个 Head，把图入口、摘要和基础存档的 HeadEntryId 放在 Entry payload 中。

后续用户确认首版原线只追加、旧点另开、fork 成功即完整可见；[应用合同设计稿](application-design.md)据此优先验证继承 head-history，暂不额外编码逻辑 Parent。R1 请求仍保留，但不再因为父链候选就自动成为首片全部工作的前置。

用户已明确：DG 当前 publish 相关设计与实现属于草稿、预计会重写；`GraphEnvelope.Parent` 不是需要兼容或保留的架构约束。新设计应自行选定唯一的领域历史关系，不必为旧 Parent 增设同步层。已有源码/测试仍说明当前行为，不能据此推导新设计必须维持当前目录布局或发布 API。必要的持久化正确性仍要由新方案重新证明。

已认可的语义是：**fork 本身不增加一次领域存档。** 旧实现有克隆 c0 时，它代表源 a1；新上游选择根本不产生 c0。这里保留的比较考察整个系统复杂性，而非只计算某个方法的分支数。

## 2. R1：持久化 HeadEntryId 并按准确位置读回

### 2.1 请求与已知缺口

显式父链候选需要把前序 HeadEntry 的准确身份存入自定义 payload，关闭所有 owner 后再读回，并直接取得该 Entry。准确存档定位也可独立使用这项能力。不能要求应用扫描全部 head-log 去重新取得一个已签发 ID。

当前 `HeadEntryId` 的构造器私有、工厂 internal；`ReadEntry` 接受已取得的 ID，保持值跨 owner 可用，但没有任意 payload 中 ID 的公开编码/解码入口。HeadSetTag 已有内部成员表编码，这不等于对应用开放了单个 ID 的持久化合同。

### 2.2 最小能力合同

- 导出一个有效 HeadEntryId 的明确持久表示，保留准确 Head 身份和完整 ticket（包括长度）。编码不能依赖 CLR struct 内存布局或运行时 hash。
- 读回时明确库身份。优选带库身份的自描述定位值；如选择库内紧凑坐标，必须显式提供并检查所属库上下文，不能把来自另一个库的裸编号静默绑定成本库位置。
- 从该表示直接读准确 Entry，取得内容及其 ID；验证至少覆盖现有 `ReadEntry` 的格式、完整位置、记录及发布资格检查。不能在失败时改读 Head 当前值或其他旧值。
- 区分“字节结构可解析”和“目标 Entry 可接受”。解析成功不是目标存在、已发布或未来永久健康的证明；必要的 CU 依赖检查仍执行，但不扩为整个仓库预检或业务对象恢复。
- 根无前序由上层字段表达，不将 default HeadEntryId 解释为有效存档。

**建议先按“编码 + owner 上按编码定位并 checked read”设计最小入口。** 这样首次导入即可返回 HeadEntry，避免先完整检查出 ID 再立即重复读取。纯解码返回定位值、之后调用 `ReadEntry` 也是可行竞争方案；是否需要新类型以及最终方法名，由上游确定。此处不预设签名或字节布局。

### 2.3 最小验收

1. 源 Entry ID 编入 DG 风格 payload，全部关闭后恢复准确旧 Entry；源 Head 后续推进不改变结果。
2. 同值连续发布得到的两个 ID，导出/读回后仍可区分。
3. 拒绝错误库上下文、截断/畸形编码、非法 ticket、缺失或损坏目标及未成立的 CU；不以“解码成功”跳过精确读检查。
4. 查询范围不依赖历史长度；必要 CU 检查成本单列，不声称固定一次磁盘 I/O。

## 3. R2：保留初始克隆，还是仅保存来源引用？

### 3.1 两个候选

**K：保留旧实现的克隆。** `ForkHead(a1)` 创建 C 的 header 与本地 Entry c0，c0 payload 与 a1 相同。DG 将 c0 解释为对 a1 的承接，首次新保存 c1 的逻辑 Parent 指向 a1。

**R：引用初始化。** `ForkHead(a1)` 创建 C 的来源 header，不写 c0。C 在首次本地发布之前，从固定来源 a1 解析当前值；首次保存产生真正的本地 c1。没有本地发布不等于没有当前状态。

R 的来源必须是**准确且实际存在的 Entry**，绝不能是“父 Head 当前值”。A 后续发布 a2 不会让尚未提交的 C 漂移。

| 操作 / 属性 | K：克隆 | R：来源引用 |
|---|---|---|
| 创建输出 | header + 一份 payload + 新 Entry 身份 | 来源 header；不生成重复 Entry |
| 未续写时读取当前值 | 可从 C 的本地克隆取得 payload | 从 C header 定位并 checked read a1；源为 CU 时仍需资格检查 |
| 第一次新保存 | DG 必须将 c0 归一为 a1，不能机械取 c0.Id 当领域前序 | 当前逻辑 Entry 本来就是 a1，直接作为前序 |
| 继承历史枚举 | 底层交付 c0 与 a1；DG 过滤克隆角色 | 到 header 后直接从 a1 继续；无需克隆过滤 |
| Head / Entry 身份 | 当前 Entry 始终实际位于被查询 Head | 查询 C 可以返回所属 A 的 Entry，必须明确区分 |
| 当前小 payload 的依赖 | 本地副本可减少对源 payload 的直接读取；不代表整个 State 或继承历史独立 | 首次本地保存前，当前值直接依赖准确源 Entry 可读 |
| 现有上游实现改动 | 较少；复杂性主要在 DG 归一 | 涉及查询、首次发布、历史、恢复、tag 编码与 API；不能只删掉 append c0 |

### 3.2 R 可以避免无限引用链

`ForkHead` 若仍以实际 HeadEntryId 为输入，则来源总是某个真正存在的 Entry。C 尚未提交时，`ReadHead(C)` 解析出 a1；再次从该状态分叉 D，D 可以直接引用 a1，无须引用 C 再转向 A。这也符合用户已选择的“按实际存档归属建树”，D 与 C 都在 A 下。

因此推荐保持“Head 引用准确 Entry”的模型，不引入“Head 引用另一个 Head 当前值”。访问一个引用初始化 Head 时，多一次跨 Head 定位即可找到实际 Entry；这不是对磁盘访问总次数的承诺，CU 校验、文件打开及正常尾恢复仍可能有额外工作。

### 3.3 重构需要同时改清楚的合同（旧基线比较）

1. **创建返回身份。** 当前 `ForkHead` 返回 c0 的 HeadEntryId，调用方能从中取得 C。R 没有 c0，不能只返回 a1.Id，否则新 C 的身份丢失。候选改为返回新 HeadId；若需要复合返回值应有实际调用收益，不因形式完整而增加类型。
2. **查询目标与结果归属。** `ReadHead(C)` 可返回 a1，返回 Entry 的 HeadId 必须仍是 A，不能伪造成 C。写入必须使用选中的 C，而非 `entry.Id.HeadId`。需要返回查询上下文时，应与实际 EntryId 分开表达。
3. **HeadSetTag 真的成为映射。** 选择 C 时应捕获 `C → a1`，不能丢失 C。多个被选 Head 可以指向同一个 a1。当前格式只存一个 LocalHeadId + ticket，并用该 ID 同时恢复 key 和 Entry.HeadId，无法表达这种关系；需改成员编码和校验。后来 C 保存 c1，旧 tag 仍是 `C → a1`。
4. **合法初态与恢复。** 具有完整、有效来源的 header-only fork 可以是已完成的 Head；无来源 root 的 header-only 文件不因此变成有效空库入口。私有创建文件何时正式发布仍需证明。首次写入的不完整尾正常恢复后，已创建 C 应仍能回到来源 a1；不能把完整损坏记录一律当作“尚无本地 Entry”。
5. **首次联合发布。** `PublishHeads` 含一个尚无本地 Entry 的 C 时，要能使用其来源状态作为既有当前值；联合发布未成立时仍正确选择先前的 a1。现有“初始 Entry 必为 SingleEntry”假设需要重审。
6. **依赖和错误。** 建立引用前确认准确来源的可接受性及必要持久化顺序；冷读失败不能改读源 Head 新末端。引用语义不自动提供 GC/删除策略，也不承诺所有业务对象依赖健康。

R 不要求 HeadStore 理解游戏、ParentAddress、摘要或“业务存档”。它只改变“新 Head 如何取得初值”，所以仍可保持通用的不透明 payload 存储职责。

### 3.4 建议与最小验证

**上游已选择并实施 R，合同归入[历史/fork](../../../../atelia-storage/docs/HeadStore/05-versionstore-names-and-indexes.md)与[tag](../../../../atelia-storage/docs/HeadStore/09-versionstore-immutable-tags.md)。** 它让“创建入口”与“发布新值”分开，减少重复 Entry 身份及消费者的过滤规则。上表保留 K 的本地当前 payload 与统一非空本地 log 收益作为取舍依据；DG 不再并行设计长期 clone/reference 双模式。

上游实际资格见[本轮新增验收](../../../../atelia-storage/docs/HeadStore/12-versionstore-final-acceptance.md)；以下机制见证继续供 DG 接入核对，不表示本仓已运行：

| 场景 | 要证明的最小结果 |
|---|---|
| fork 后不保存即重开 | C 已存在；当前解析 a1；没有 c0；历史只列 a1 一次 |
| 父线继续、子线首次保存 | A 推进不改变 C；c1 的领域前序可直接取 a1；之后当前返回 c1 |
| 从未续写 C 的当前状态再次 fork | 新 D 直接引用 a1；无 Head 到 Head 的解析链 |
| tag 同时捕获 A、C、D | 保留所选三个 Head 身份；允许 Entry 值重复；C/D 后续推进不改变 tag |
| 首次 append 中断、首次 CU 未成立 | 正常恢复和发布资格判定后仍选择 a1，不出现虚构本地 Entry |
| 来源是 CU / 来源损坏 | 成功路径保准确来源；必要依赖失败可见，不静默回退到别的值 |

若实施暴露新的实质困难，再由上下游重新评估；不提前引入长期多态引用框架、多重来源权威或 clone/reference 双模式。

## 4. 建议交接顺序

1. R1 可独立收敛，不以 R2 获胜为前提；两种 fork 方案都能使用持久定位能力。
2. R2 已在上游实施；DG 按引用初始化推演，后续再独立更新 pin 和接入验收，不把历史“每 Head 必有本地 Entry”作为约束。
3. Head 的标签承载仍是独立待选事项。R 去掉 c0 不自动解决标签随创建发布；不要通过制造一个“标签用 Entry”把重复节点重新引入。
4. DG publish 重写在这些上游边界和应用故事收敛后进行。不要为过渡兼容新增双 Parent 协议，也不要将新的领域 Parent 解释能力下沉到 HeadStore。

### 后续设计发现的两个待协商能力

它们尚未获得上游承诺，不能悄悄并入 R2：

| 能力 | DG 的具体动机 | 当前边界 |
|---|---|---|
| 从准确 Entry 开始的有界继承历史读取 | 固定旧存档的历史选择、Event 点的最近 State 恢复；避免每次从今天的 Head 尾扫描 | 不等于 R1 的单点读回；先定准确起点及停止语义，再考虑分页 token |
| 创建 root/fork 时共同发布小型不可变属性 | 新世界线无本地业务 Entry 时仍有自己的标签，并可识别业务 Head | 是标签候选 A；候选 B 在 DG 登记层完成，不要求上游必做；还需单独解释新根 shared Schema 发布 |

需求和替代方案统一见[应用合同稿](application-design.md)，此处只登记接口交接边界。

## 5. 源码依据

- [ForkHead](../../../../atelia-storage/src/HeadStore/HeadStore.Fork.cs)、[创建/发布](../../../../atelia-storage/src/HeadStore/HeadStore.cs)：现已实现引用初始化；旧 clone 比较对应上文记录的原基线。
- [HeadEntryId](../../../../atelia-storage/src/HeadStore/HeadEntryId.cs)、[ReadEntry](../../../../atelia-storage/src/HeadStore/HeadStore.Entry.cs)：构造与准确读检查边界。
- [继承历史](../../../../atelia-storage/src/HeadStore/HeadStore.History.cs)、[记录读取](../../../../atelia-storage/src/HeadStore/Internal/Records/HeadRecordReader.cs)：初始 Entry、来源接续和本地读取假设。
- [Tags](../../../../atelia-storage/src/HeadStore/HeadStore.Tags.cs)、[TagEntriesCodec](../../../../atelia-storage/src/HeadStore/Internal/Tags/TagEntriesCodec.cs)：现有选择 Head、Entry 身份及成员表编码的耦合。
- [联合发布](../../../../atelia-storage/src/HeadStore/HeadStore.Batch.cs)：R 需复核首次本地 CU 的路径。

DG 本轮仅做分析与源码复核，未执行新引用初始化的 DG 接入或产品验收。
