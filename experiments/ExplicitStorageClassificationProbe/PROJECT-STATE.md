# 显式 storage 分类 Probe 工作集

- 目标：并列观察原始 C# symbol、实际 PE storage、Generator diagnostics/候选与生成后 Emit，定位显式分类是否被忽略。
- 当前状态：2026-10-15 的 14-case 临时调查已迁入源码管理并经主线程构建、运行及原始观察比对；限定结果见 README。本轮产品实现未修改，H01 仍存在。
- 合同：无 SG error、Emit 成功、完整模型能力分别判断；默认观察不固定缺陷诊断，形状与 record 正控失败才使机制 Probe 失败。
- 运行及冻结原始表：[README](README.md)；动态 JSON 进入 obj 或调用者指定位置，不提交本机日志。
- 近期顺序：[DB-093 显式分类有效性计划](../../docs/design-branches/0093-explicit-storage-classification/README.md)先实施与产品回归；[DB-094 backing 支持调研](../../docs/design-branches/0094-backing-field-support/README.md)独立裁决，当前不实施支持。
- 延期：实际保存/冷恢复资格、history/包模式交付与 backing 支持矩阵不由本 Probe 代替。产品状态继续以 [src/PROJECT-STATE](../../src/PROJECT-STATE.md)为入口。
