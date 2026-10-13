"""拼豆（bead）皮肤工具链。

模块
----
- :mod:`bead_model` —— ``neko.beads/1`` 数据模型（加载 / 校验 / 序列化），纯标准库。
- :mod:`bead_bake`  —— 烘焙器：模型 → ``neko_<state>.png`` + 眼球层 + 影子 + ``skin.json`` + 缩略图。
- :mod:`bead_verify`—— 资产级断言器（烘焙确定性 / 调色板合规 / 硬边 / 基线 / 帧数一致）。

设计原则：**全整数、无随机、无重采样** —— 同一模型两次烘焙逐字节一致。
"""

__all__ = ["bead_model", "bead_bake", "bead_verify"]
