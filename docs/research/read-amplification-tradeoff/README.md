# ReadAmplificationThreshold 的理想模型图

直接用浏览器打开 [index.html](index.html)。无需启动服务器、安装依赖或联网。
滑块、数值输入和预设按钮联动三张曲线；主图可下载为独立 SVG。

本页回答：选择的额外 payload 容忍度，与模型中的平均重建 payload 是什么关系？
它是函数可视化，不是性能测量；初始显示 `L=5`。
默认值选择与验收见 [DB-070](../../design-branches/0070-read-amplification-default/README.md)。

令 `L` 为 ReadAmplificationThreshold，`B` 为完整 Base payload，`d` 为小而稳定的 Delta payload。
在对象大小稳定、长轨迹、预算不推迟 Base、周期内均匀取样的近似下：

- 两次 Base 间约积累 `(L−1)B` 的 Delta。
- 额外 payload 比例 `ε ≈ 1/(L−1)`，分母是持续写 Delta 的 payload。
- 平均重建倍率 `R ≈ (L+1)/2`，平均重建 payload 为 `RB`。
- 如果按刚好用满容忍度的边界选值，消元得到 `R ≈ 1+1/(2ε)`；`25%` 代入时为 `0.25`。
- 长期写入 payload 相对持续 Delta 的倍率约为 `1+ε`。

主图横轴为容忍度百分数，纵轴为平均重建倍率，是双曲线。
下方两图共用阈值 `L` 的范围，分别显示开销的递减曲线、重建的线性增长。
`L=3/5/10/11` 分别对应 `ε=50%/25%/约11.1%/10%`、`R=2/3/5.5/6`。

忽略初始 Base 的有限历史影响、Base 替代当次小 Delta 的修正、离散选择与 D 上界的偏差，
以及 frame/map/Journal 等外围开销。不把 payload 倍率当作耗时或实际 I/O 倍率；
`ε` 不是 `BaseBudgetPercent`，也不是产品保证的空间上限。
连续图可以取小数，产品阈值为整数。展示范围为 `L∈[2,21]`；
该渐近公式在 `L=1` 发散，不能用来描述有限 `d` 下每次写 Base 的实际开销。

实现背景：[DB-069](../../design-branches/0069-incremental-save-baseline/README.md)。
