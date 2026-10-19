# 多分支联合提交普通包消费者

仅使用公开 API 与普通 PackageReference。三个独立进程验证：两个 Event-first 分支首次共同保存两种新 State、执行时捕获与摘要所有权；冷开 State/Event 混组；再次冷开准确历史和 Event 不保存 State 编辑；无业务模型登记时列根/子树，空 fork 在父线前进后仍继承原点。请求不是快照，旧 Base 的请求拒绝；代码不模拟外部事务或自动事件重放。

```powershell
./experiments/PackageConsumerProbe/Run-GroupCommitProbe.ps1 -PackageSource <完整十包feed> -Version <DG版本>
```

省略配对参数时使用既有包准备入口制作候选。runner 验证生成 history 的 Publish/Verify 和三个运行标记；当前实现与本次运行资格查 [0096 工作集](../../../docs/design-branches/0096-history-and-head-model/PROJECT-STATE.md)。尚未运行的新候选不能借用旧版本资格；原子发布故障与生命周期反例由产品测试承担。
