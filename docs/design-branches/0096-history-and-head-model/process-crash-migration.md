# DB-096：Branch/Commit 进程中止见证迁移

2026-10-19。基线为 `d4734a1`，产品组提交来源 `9aa72ff`，Storage pin 为 `70b406c`。本片只迁移既有 MetaBootstrapCrashProbe 的 host/controller，不改产品发布协议或持久格式。

## 问题、范围与判据

旧 host 仍以 BranchCheckout、Head 1 的字符串到图地址 map 作为业务入口。新模型中 D 只登记正式 Branch；普通保存更新真实业务 B 与必要 S，D/T不变。必须恢复已有6个保存窗口和3个初始化窗口的可执行性，不能只让它编译或删除原反例。

最小验收：每窗真 external Kill 后由独立 PID verifier 冷读，准确选择旧或新 B/S，身份绑定不漂移，D/T不推进，完整帧保持，准确source/Schema/prior/IDs/H及冷单条Delta续写保持；初始化缺入口拒绝且不补建、锁释放、第三窗空库续写通过。只读打开不改文件，正常backing恢复保原合同。

协议v2新增保存目标与原Base、D/T准确Entry定位值；READY只作诊断，不能代替发布资格。已发布业务图按真实Commit历史选择来源；完整孤立候选仅用已知旧State验物理关系，不赋予不存在的Commit身份。具体运行命令、窗口表和输出纪律只维护在[Probe README](../../../experiments/MetaBootstrapCrashProbe/README.md)。

## 实施与实际验收

主线程统一运行根 Release solution build、独立 host Release build，均0警告/0错误；两组 controller 首跑均 exit 0，未重试或削弱原断言。产品源码及 Storage pin 不变，本片无需新 G；运行来源是产品基线 `d4734a1` 加本轮 host/controller 输入，不能把当时尚未提交的 host 说成已存在于该基线。

| 模式 | 窗口 | 实际结果 |
|---|---|---|
| 保存 | AfterMetaAppend | 旧 B/S，完整 Meta 候选保留 |
| 保存 | AfterBusinessAppend | 旧 B/S，完整业务/Meta 候选保留 |
| 保存 | AfterFirstCuEnd | 旧 B/S，1个CU End、0次flush hook |
| 保存 | AfterAllCuEnd | 新 B/S，2个CU End、0次flush hook |
| 保存 | AfterPublicationConfirmed | 新 B/S，2个CU End、2次flush hook |
| 保存 | BeforeInstall | 新 B/S，2个CU End、2次flush hook |
| 初始化 | AfterFirstCreateHead | 缺 Head 2，拒绝且不补建，释放锁 |
| 初始化 | AfterSecondCreateHead | 缺 Head 3，拒绝且不补建，释放锁 |
| 初始化 | AfterThirdCreateHead | 完整空 D/S/T，零用户帧，冷保存及真实Delta续写 |

两个实现包分别负责 host 和 controller；独立审阅发现旧 Kill helper 在 writer 已自行退出时可能仍写入 ExternalProcessKill 标记，现正常验收路径必须确认仍活着并实际请求 Kill；清理路径保持宽容。controller另按窗口核对准确 PublicationStage，避免相同可见性/计数的后两窗串接。

主线程与独立复核者分别读取实际回执及各窗 JSON：六条 seed/READY/verify 身份链一致；九次 writer 均 READY 后以非零退出，Kill请求/退出/新 verifier启动顺序成立，相关进程PID不同；数据和D/T检查、完整帧保留、RO字节不变、准确ID/prior/H、冷续写及恢复标记均通过。所有实际输入/DLL/结果/READY摘要、assets和六个Storage ProjectReference匹配；实际上游为干净 `70b406c`。本次没有运行其他未受修改的完整产品测试套件，既有组提交源码/包证据保持其原范围。

工件位于本机 `.artifacts/db096-crash-migration/`：

- `root-build.log`、`host-build-1.log`；对应本次统一构建。
- `save-v2/receipt.json`、`init-v2/receipt.json`；各窗包含seed（保存模式）、READY、verify和stdout/stderr。`save-v2.log`、`init-v2.log`为controller输出。
- `verified-matrix.json`为主线程核验摘要；`frozen-inputs/`保存运行时输入字节，包含维护前README，以便后续文档更新不使原输入SHA失去可追溯对象。

这些本机证据不随Git分发；可用Probe README中的命令在准确源码上重跑。实际 host/controller 源码随本片提交，文档维护不改变已验证可执行字节。

## 限度与后续

本片保一个业务B加S的两成员CU和工厂三控制Head窗口，不宣称穷尽多业务Branch进程中止组合、OS crash/power-loss、Linux或性能。既有组提交55项source反例与普通包验收见[组提交记录](group-commit-integration.md)。此门槛闭合后，继续根/子树、分页和最终合入，完整队列只查[工作集](PROJECT-STATE.md)。
