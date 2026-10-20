# 目标模型裁决

2026-10-20，基线 `4d7c4b3`。主线程先编写 [目标与需求账本](README.md)，三个 reviewer 分别承担需求质疑、最小架构和语义守卫，先独立审查，再交换最强反例交叉质询。这里只记录有证据的最终裁决，不以票数代替源码。

## 收敛的最小模型

同一捕获/候选/发布内核：State 有长期身份及增量基线；Event 每次新身份、完整内容、无内容来源。业务历史独立于内容来源。局部存储检查证明 Event 自包含，既有独立恢复保持图内身份和共同工厂安全域。

| 裁决 | 对象 | 依据及删错时的反例 |
|---|---|---|
| simplify | Event 的 State 差分参与 | 用户已选择独立完整图；小事实消费者支持。相对 State 的 head/H/rewrite/Remove 是原实现选择，不是业务法律 |
| merge | Pending.StateSource 与 Write.SourceGraph | 两处都是 producer 填写的同一地址，并非两份独立证据。保准入从 ExpectedEntry 推导历史源；否则可把其他 State 的内容发布到当前分支 |
| simplify | AppendBusiness source 参数 | 从 write 读取；存储层本来没有 Head 历史。历史匹配由唯一发布 owner 执行。读取的 expected source 则保留，不能仅相信磁盘自述 |
| keep | Event owned batch/完整目录检查 | null-source Base 目录仍可能借用旧 State 的对象 head；必须证明所有 live head 属于 owned 全 Base batch |
| simplify | Event Pair | 一律复用 ReadIndependent，保同调用 mutable instance guard；两次 ReadRoot 可被 singleton allocator 破坏，空 shared set 仍保跨图字符串身份表 |
| keep | State、meta 增量及 State–State Pair | 有当前消费者，删除会使 State 失去增量保存/安全共享；它们不是本片净删成果 |
| delete | 三个 Event 公共入口的策略参数 | 全 Base 后无作用，保留会给用户虚假控制面；State 参数及共用请求所需字段保持 |
| simplify | 格式边界 | 一个 P0R4 根 epoch 即可清晰拒绝旧库；不滚动没有改布局的所有 payload，不增加旧 Event reader |
| defer | Event 大载荷优化、scope 类型、去重、同 Event Pair 例外 | 无当前必须承担的产品法律；巨大共享载荷用真实写入放大测量披露，不为其保留另一套语义 |
| keep | 完整帧、正常恢复、reentry/fault、共同 Catalog/原子提交 | 用户/AGENTS 与生产故障测试的真实约束；独立 Event 不授权越过发布 owner |

## 交叉质询的修订

- 初稿仅强调 Event 退出旧基线；最终明确删去 pending 重复来源字段，并保独立历史认证，减少目标中的权威数量。
- 初稿允许同 Event 重复读取的优化未定；最终统一所有含 Event 的 Pair 为独立物化，不新增例外。
- “source=null 即独立”被否定：完整读取必须拒绝外部 head，且 metadata-only 入口不扩大为完整扫描。
- “分开读取即隔离”被否定：两个图仍必须在 hydration 前通过共同可变工厂实例检查。
- 未发现需要兼容旧 Event 差分或保留无效参数的当前消费者。仓内旧物理断言需迁移；值、图内 alias/cycle、State 不推进、准确 Schema 与故障断言保留。

不存在必须由用户另行决定的产品分歧。代码行数可能持平；主要收益是 Event 退出 State 状态组合，而不是删除 State 所需的增量内核。性能结论由同一夹具的前后测量另行确定。
