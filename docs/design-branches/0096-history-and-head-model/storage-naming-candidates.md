# 上游命名比较的收敛结果

2026-10-17 更新：命名海选已结束。实际采用的合同来自上游[15 重命名记录](../../../../atelia-storage/docs/HeadStore/15-headstore-renaming-plan.md)与[16 HeadSetTag 方案](../../../../atelia-storage/docs/HeadStore/16-headset-tags-plan.md)。本页保留竞争方案的取舍理由，供理解当前分层；不再是待批准改名表。

## 当前统一词汇

| 原符号 | 实际符号 | 关键边界 |
| --- | --- | --- |
| VersionStore / Atelia.VersionStore | HeadStore / Atelia.HeadStore | 持久发布入口和接受历史，不负责业务历史模型 |
| RefId | HeadId | 同库的稳定入口身份，不是 current Entry |
| RefRevision | HeadEntryId | 实际 HeadId + 完整 ticket；定位值，不是健康证书 |
| RefBytesSnapshot | HeadEntry | checked-read 的 owned 结果，Id / RecordedAtUnixMs / Payload |
| RootPayload / PayloadBytes 提议 | HeadEntryPayload 概念 | HeadEntry.Payload 保持只读 bytes；旧 tag 的 RootPayload 类型删除 |
| CreateRef / PublishRef(s) | CreateHead / PublishHead(s) | 创建与既有入口更新分开；联合发布返回各成员 EntryId |
| ReadRef / ReadRevision | ReadHead / ReadEntry | 当前读取与精确读取分开，无精确读取失败回退 |
| ReadRefHistory / ForkRef | ReadHeadHistory / ForkHead | 历史沿来源接续；fork 初始克隆有子 Head 身份 |
| ForkInfo | HeadOriginInfo | SourceEntryTicket 仍是来源声明，不是已签发 SourceEntryId |
| RootMapCodec | NamedFrameMapCodec | 可选 string → FrameAddress 编码，不自带持久索引 |
| 任意 bytes tag | HeadSetTag | 功能替换，非纯改名；固定 HeadId → HeadEntryId 表 |
| CreateTag / ResolveTag | CreateTag / ReadTag（HS） | 创建输入 HeadId span；返回成员表，不再接受 bytes/Related |
| TagInfo / TagScanEnd | HeadSetTagInfo / HeadSetTagScanEnd | 元信息含 EntryCount，无 Related |

`HeadSetTag` 允许单成员，Set 强调集合语义，不引入另一种 HeadSet 实体。`CreateTag` 同步捕获所选 Head 的当前已接受位置；`ReadTag` 校验完整表，`ReadEntry` 才验证成员内容。联合发布和命名是两个操作。

`PublicationOutcome`、FrameAddress、Ticket、LocalId、RecordedAtUnixMs 等保名。时间不是单调编号；原物理路径 `refs`、`tags`、`versionstore.*` 保留。新 tag 语义采用上游统一格式 2，不读旧 bytes tag。

## 原竞争方案为何退出

| 方案 | 优点 | 本轮未选理由 |
| --- | --- | --- |
| PublicationLogStore / Log / Position / Entry | 发布序列明确 | 名称长，Position 易被理解为可自造偏移；Head 与 PublishHeads 更直接表达稳定入口和共同更新 |
| PublicationStore / RefPublication | 强调发布协议 | 一次操作与多个成员记录容易都被叫 publication |
| StreamStore | 序列和分叉紧凑 | 易带入事件流、订阅、字节流预期 |
| SlotStore | 少占用领域词汇 | 暗示固定槽数、原地覆盖和编号复用 |
| 保留 VersionStore，统一 RefVersion | 改动小 | version/revision 继续与 DG 图版本、Schema 版本争用 |
| JournalStore / LogStore | 追加语义明确 | 易误认 WAL/诊断日志，未降低说明成本 |

早期首选曾包含独立 `NamedValue`：它准确反映**当时**任意 bytes tag 的行为。用户后来将需求收窄为捕获所选 Head 位置，独立具名值职责已删除，因此该建议失去前提。后继名 `HeadSnapshot` 又被上游选为 `HeadSetTag`，使 coding agent 在局部代码中直接看到集合含义。两次调整不意味着把旧任意 bytes tag 偷换成同义词。

## 留给 DG 的词汇与问题

Version、Revision、Snapshot、Branch、Checkpoint 可以围绕领域故事重新梳理，但不因术语可用就新增实体。首先区分 head-log 与 domain-history；确定逻辑历史入口、rewind 和操作来源上下文，再选择身份、目录和 metadata。

DG 当前 `CreateTag/ResolveTag` 仍是历史图书签；不能因同名就转发至 HS.CreateTag。当前适配采用独立目录，详见[接入切片](headstore-adaptation.md)。业务 Branch 仍不等于 Head，一图也不等于一个 HeadEntry。

具体实现和可运行证据以[上游验收](../../../../atelia-storage/docs/HeadStore/16-headset-tags-acceptance.md)及 DG 接入切片为准，不把早期提名、试读或讨论当作新版本验收。
