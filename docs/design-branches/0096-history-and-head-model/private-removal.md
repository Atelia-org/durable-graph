# 独立移除 Private / ToShared

> 2026-10-19；实施中。起点 `a347a2f`，合同见[统一 Catalog §4](shared-schema-only.md#4-private-实现删除合同)。本页承载本次分支会话的实施与验收，完成后供设计主线续接。

## 范围与验收

只保留统一 Shared 接纳路线，删除公开 Private/ToShared 入口及内部私有发布、推广和专用 admission。保留现有业务历史布局、Shared 命名、准确历史 Schema、冻结执行环境、State 基线及 owner/fault/source 检查。不在本片实施新 Branch API、组提交或操作内 Catalog 重构。

最小成功标准：产品无 Private/ToShared 执行路径；旧格式明确拒绝；普通升级、Event/Discard 保 State/rewrite、冲突写前拒绝、冷开 Delta 与完整帧保持有回归证据；源码根构建与相关完整 suite 通过；迁移的普通包消费者用本次新包验证。

## 已选实现边界

- 必读 Head 1 分支目录的 magic `P0R1` 升为 `P0R2`。旧布局在根解码时拒绝，不扫描全历史或导入旧 Private 内容。Shared/书签编码、图 ABI 不随此片更改。
- 保留内部每 checkout Catalog 工作区作为既有保存机制；仓库 accepted Catalog＋操作内共同候选属于后续组提交切片。
- 开发草稿使用完整关闭库的独立副本。容器消费者转普通升级；Split 保 Event 接纳新定义而不推进 State 的故事。
- 历史测试只退出已取消政策；中性夹具和准确历史、升级、失败边界断言迁入统一路线。

## 当前工作状态

运行时、Persistence 回归、Generator 回归和普通包消费者已分包交付并集成，Debug/Release 根构建均通过（零警告/错误）。源码复验与进程中止矩阵已通过，普通包验证尚未开始；本片尚未完成交付验收。

首轮回归暴露四个 Capture 回调使 Store/owner/backing 失效的用例：普通 Shared Event-only 路径先准备了 body 才拒绝。旧 promotion helper 的 Capture 后检查属于应保留保护；现已在统一 `BranchSaveSession` 的 Capture 后、Catalog/body 准备前补 `Store.EnsureUsable()`。独立审阅认可该位置；四个断言未放宽，修复后全部通过。另修复一个遗留 Generator 测试与 CrashProbe 的 P0R1 预期为 P0R2。

验证继续使用 DG 固定 Storage `731eef7cd3158572532fbeea7ba95a79213a7328` 的独立 worktree `.artifacts/db096-shared-schema/storage-731eef7`；不切换上游主工作树或升级依赖。命令基线见[前轮验证](shared-schema-only.md#5-本轮基线验证与限度)。

## 回归与证据

- 初次全量源码：Serialization 133/133、Storage 36/36；Persistence 915/919、Generator 1720/1721，失败为上述五项。
- 修复后 Release 根构建通过；Persistence 完整 919/919、Generator 重点 17/17（包括该格式断言、开发复制、Event/Discard、容器升级及 API 编译负例）通过。
- CrashProbe 构建通过；真实 controller Kill 初始化 3/3、保存 6/6 通过。只授 ProcessCrashOnly 有限窗口资格，不授断电或 OS crash 资格。
- 代码与迁移覆盖经独立审阅；全仓已删除文件入链及本次文档链接/锚点检查通过。普通包资格待追加。

源码回归、TRX、构建及 crash receipts 位于 `.artifacts/db096-private-removal/`。测试源码、消费者和控制脚本随 Git 管理；日志为本地忽略工件。
