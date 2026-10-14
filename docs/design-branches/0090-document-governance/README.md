# DB-090：设计记录治理与随任务维护

> 2026-10-14；结论：新 DB 按问题聚合，指定一个阅读入口，维护绑定任务事件。
> 状态：2026-10-14，本次治理已实施。历史材料按问题归组，总索引收窄为导航，维护规则由局部 AGENTS 承载；实际验收见[结果记录](results.md)。
> 执行入口：[局部 AGENTS](../AGENTS.md)；发现依据的入口：[设计索引](../README.md)。本案是文档治理，不产生产品施工授权。

后续 [DB-091](../0091-retired-material-cleanup/README.md)经当前源码和独立复核退出淘汰材料；本案迁移数字描述当片，不要求永远保留全部历史副本。

## 1. 问题、边界与验收

要回答的问题：怎样让 Coding Agent 找到某个问题的决定与证据，并让后续工作保持这个结构？
本案只治理设计记录的组织、导航与维护，不调整产品代码、包、Probe 生命周期或持久合同。

最小验收是：不知道编号时能按主题从索引找到问题；知道编号时能限制搜索到该问题材料；
入口能区分当前合同、当片结果与被取代依据；一次新增或结题无需同步多份能力或待办账本。
目录布局尚无检索耗时或任务成功率的对照实验，本案不声称性能收益。

沿用仓库已有知识职责：当前能力与焦点归 [PROJECT-STATE](../../../src/PROJECT-STATE.md)，
长期目标与选择归[目标设计](../../DurableGraph-target-design-v0.md)，未完成事项和重访条件归[路线图](../../DurableGraph-research-roadmap.md)，
设计记录保问题的来由、裁决和有限验收，[实验簿](../../DurableGraph-lab-notebook.md)只补独立证据索引。
源码、可执行测试和实际输出决定实现事实；这些导航文件不互相充当独立证明。

## 2. 现场证据与真正的风险

盘点基线为 Git `54cc621364b8f2a0c11e1efa8dcdb76abfe72242`，工作树起始干净。
变更前本目录有 112 个直属文件：111 Markdown、1 CSV，合计 1,837,865 字节；递归共 119 个文件。
13 个编号组有多份材料，包括 DB-071 的五份（含 Goal）、DB-078 的四份、DB-086 的三份。
已有 0044 问题目录；0083 子目录只装故事附件，主文仍平铺，不能算完成了统一问题入口。

| 真实材料 | 暴露的问题与不能丢的语义 |
|---|---|
| [DB-071 organization-review](../0071-assembly-namespace/0071-assembly-namespace-organization-review.md) | 文件名是 review，实际承载已采纳方向并被目标设计引用；不能按名字降为纯过程材料 |
| [DB-081 实验](../0081-hot-commit-restoration-material-slice/0081-hot-commit-restoration-material-experiment.md) | 实验完成形成否定结论，热准入未实施；反例和重启条件仍有价值 |
| [DB-082 结果](../0082-prepared-immutable-leaf-reuse-slice/0082-prepared-immutable-leaf-reuse-implementation.md)及 [CSV](../0082-prepared-immutable-leaf-reuse-slice/0082-prepared-immutable-leaf-reuse-measurements.csv) | 候选已实现，但总成本收益未成立，默认关闭；验收通过不等于启用 |
| [DB-086 实施（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/design-branches/0086-new-storage-refactoring-plan/0086-new-storage-implementation.md")与 [DB-087](../0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#current-status) | 086 当片验收有效，单根／SCB1／合帧已由 087 替代；底座正常恢复等选择仍保留，不能整篇判作失效 |
| [DB-087 历史资格](../0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#private-plan) | P0/P1 归档不含后来 P2；完整原 P2 另在 Git `d8c0ecb` 的 §9。泛称“完整归档”可能丢掉一个时期的证据 |
| [077/078 共同评审](../0077-repository-model-environment-slice/0077-0078-api-dialectical-review.md) | 一份材料服务两个问题，应多处链接、只存一份 |

外链负担是已存在的：排除 design-branches 与 archive 后，仓内 31 个 Markdown 文件、102 行引用 DB-087 文件名。
它保留大量旧章节 anchor，迁移不能只验证目标文件存在。这个计数不包含仓外书签，也不是全库断链清单。
当前源码的 `Repository.ImmutableLeafReuseEnabled` 默认 false，
[对应回归](../../../tests/DurableGraph.Persistence.Tests/SplitPreparedStateReuseTests.cs)仍区分 body-only 依赖与候选开关；
上述语义不是为了整理文档而发明的状态区分。本轮只核源码与现有记录，没有重跑产品测试。

## 3. 选择最少的组织方式

**目录按问题，文件按实际材料需要，适用性由内容说明。**
不把变化的状态编入路径；不为设计、评审、实施建立三个全局目录。

```text
docs/design-branches/
  README.md                         # 找问题，不接管产品状态或排期
  AGENTS.md                         # 写入与收尾约定
  0090-document-governance/
    README.md                       # 本案较小，正文就在这里
  NNNN-topic/                       # 未来问题，下面附件均为按需示意
    README.md                       # 限定结论与阅读入口
    design.md                       # 需要独立引用的详细合同
    review.md                       # 有保留价值的质疑、裁决和理由
    results.md                      # 最终结果、来源、范围与未覆盖项
    measurements.csv                # 可复算的数据
  0086-new-storage-refactoring-plan/
    README.md                       # 同一问题的阅读入口
    0086-new-storage-*.md            # 保留原合同、裁决与验收
```

不要先创建四件套。小问题一份 README 就够；长合同或独立结果需要单独引用时再分文件。
一份混合旧稿可以保持整体，由入口说明阅读顺序。文件名不决定它是不是已选合同。
共同评审放在一个明确位置，由各问题链接，不增设共享材料登记系统。

问题入口首屏只需回答：**问什么；目前得出什么有限结论；适用于哪里；合同与证据去哪读。**
示意开头如下，字段不需要解析器：

```markdown
# DB-NNN：问题名称

> 结论：实验否定，候选未接线。适用范围：本片指定 provider 与基线。
> 当前合同／后继：链接；实际反例与最终结果：链接。

问题、范围与最小验收写一次。正文较长时，这里给主要材料的阅读顺序。
```

入口不是完整合同的第二份摘要。它不重抄测试账本，不维护新的施工队列；
完成时用一节紧凑最终结果替换过程，长期约束与未完事项链接既有维护 home。
首屏读完应能选择下一份材料；不按固定字数自动拆文档。

| 裁决 | 最小机制及原因 |
|---|---|
| simplify | 新问题用稳定目录；现有编号前缀搜索仍有效，目录的收益是一个物理搜索范围和局部链接空间 |
| simplify | 顶层索引只列 DB／主题／主入口和必要后继提示；已选与已实施的详细区别在入口和实际证据查 |
| merge | 共同材料只保一份；当前能力、长期约束、未完事项各归既有 home |
| keep | 反对意见、否定实验、默认开关、适用基线及后继；单一 done 标签会丢掉真实边界 |
| defer | 机器 frontmatter、自动生成索引、定期巡检或全库 CI；等真实消费和重复验证成本出现再选 |

## 4. Coding Agent 随任务维护

执行规则只在[局部 AGENTS](../AGENTS.md)维护；根 AGENTS 提供入口。
以下是工作方式的说明，不新增另一个强制检查体系。

| 任务事件 | 最少动作 |
|---|---|
| 开题或重开 | 先查已有问题；记录一次问题／范围／最小验收。新 DB 登记一个入口；产品工作从 PROJECT 链接合同。新范围可写后继，旧工单不自动复活 |
| 结论变化 | 替换问题入口的限定结论，保理由与证据。检查本工作包引用或取代的旧结论是否改变适用性；必要时只补后继通知 |
| 完成或停在否定结果 | 写一节最终结果及未覆盖范围，退出过程计划；产品能力、长期选择、剩余项分别回既有 home，DB 留链接 |
| 移动或压缩 | 核准确来源与覆盖时期；检查入链、出链、anchor 和实际证据去向，查看集成 diff |

维护跟随导致知识变化的工作包，不要求每次改代码都更新全部文档。
证据附件保日期、来源 revision、实际结果与限制，不因后继实施而回写成新版本资格。
若旧结论仍被引用，入口补一句“哪部分被谁替代”；不重写冻结论证。

收尾时 Agent 应能简短交代：结论是否变化、哪个入口已同步、证据存在哪里、哪些知识 home 受影响。
这是任务结果的一部分，不要求保存一份新的维护报告。
本轮是文档治理，不把它添加为产品能力或产品施工队列。

## 5. 实际检索路线

查当前行为从 PROJECT／源码／测试进入；查未完成事项从 roadmap 进入；查理由或历史验收从设计索引进入。
未知编号先用主题找索引，确定问题后缩小到目录：

```powershell
rg -n 'FrameStore|Schema|Fork' docs/design-branches/README.md
rg --files docs/design-branches/0086-new-storage-refactoring-plan
rg -n '恢复|验收' docs/design-branches/0086-new-storage-refactoring-plan
rg -n '维护|迁移' docs/design-branches/0090-document-governance

# 历史问题明确选择冻结范围
rg -n 'P1d' docs/archive/2026-10-13
```

不设置 `.rgignore` 隐藏 review、实施或历史正文：否定证据和被取代原因同样是合法搜索目标。
原命令中的平铺路径属于原版本；恢复原上下文时使用对应 Git 修订，实际文档导航使用上面的目录。

## 6. 已实施范围与历史读取

按批准范围先独立验收 DB-071，再迁其余材料。111 个原平铺文件全部归组，顶层只保 README 与 AGENTS；
70 个单文直接作为问题 README，18 个多材料／详细合同问题新增短入口，DB-044 保持既有结构，DB-083 聚合已有故事附件。
总索引只列 90 个编号／主题入口及少量必要的后继提示。路径、anchor、CSV、原论证及历史命令的验收集中见[结果记录](results.md)。

正文只重定位实际 Markdown／HTML 链接；单文补来源与适用性 notice，不机械拆分混合稿，不改测试数字或原授权。
未来若压缩旧稿，仍须先提取有效决定和未完成问题，再冻结准确全文并说明覆盖时期。
默认更新仓内消费者，不建立一整套转发壳；已知无法同步的消费者才按需保转发，fragment 必须指向正确版本的实际证据。
日期 archive 只保存冻结稿，问题入口保后继与阅读范围，不按一个状态把整组迁出。

部分历史源码／工具在整理前已退出当前树；其链接按原记录基线解读，不无差别指向新实现。
文档的原平铺全文可在 Git `8ef9fca` 查询；更早源码或包证据按各记录中的实际修订恢复。
DB-087 原 P2 仍取 `d8c0ecb` 的原路径 §9，不能用只覆盖 P0/P1 的日期归档替代。

## 7. 当前停点与后续维护

本次目录治理与索引收窄已完成，没有剩余必经迁移包。三个独立视角的设计审查、交叉质询、
DB-071 试点及最终独立验收结果均归入[结果记录](results.md)。目录不是检索加速的必要条件，一个阅读入口不等于集中全部合同。

后续在真实任务收尾执行局部规则，不开定期全库治理工程：

- 新增材料仍需不断改总索引：检查入口是否选错，或是否让索引承担细节。
- README 开始追加每轮步骤与测试数字：替换为最终结果，详情只保一处并链接。
- 结论变化使旧记录的适用性改变：补限定后继通知，冻结论证保原含义。
- 大量受影响链接重复核验且出现误漏：按实际需要保一个机械检查工具；不让它推断批准、实现或证据强度。

文档维护不自动成为产品任务的额外施工阶段；只有本次知识变化影响对应 home 时才同步。
