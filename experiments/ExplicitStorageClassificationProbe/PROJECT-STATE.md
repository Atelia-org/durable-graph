# 显式 storage 分类 Probe 工作集

- 目标：并列观察原始 C# symbol、实际 PE storage、Generator diagnostics/候选与生成后 Emit，定位显式分类是否被忽略。
- 当前状态：14-case 调查已迁入源码管理并在 DB-093 修复后复跑，形状与 record 正控通过；当前观察及冻结原始表见 README。产品修复资格由 DB-093 的源码回归与普通包验收提供。
- 合同：无 SG error、Emit 成功、完整模型能力分别判断；默认观察不固定缺陷诊断，形状与 record 正控失败才使机制 Probe 失败。
- 运行及冻结原始表：[README](README.md)；动态 JSON 进入 obj 或调用者指定位置，不提交本机日志。
- 维护入口：[DB-093 显式分类合同及验收](../../docs/design-branches/0093-explicit-storage-classification/README.md)已完成；机制工具无需因诊断变化保留旧缺陷。[DB-094 backing 支持调研](../../docs/design-branches/0094-backing-field-support/README.md)仍独立裁决，当前不实施支持。
- 延期：实际保存/冷恢复资格、history/包模式交付与 backing 支持矩阵不由本 Probe 代替。产品状态继续以 [src/PROJECT-STATE](../../src/PROJECT-STATE.md)为入口。
