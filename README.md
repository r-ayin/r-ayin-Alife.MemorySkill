# r-ayin.MemorySkill — Alife 长期记忆检索插件

为 Alife 角色自动安装并维护 memory 技能（Storage/Skills/memory）：

- 跨会话长期记忆检索：只读索引角色分层存档（Memory/L1..L8），SQLite FTS5 关键词 + BGE 向量语义混合召回，时间衰减排序。
- 零手工安装：插件激活后自动释放内嵌技能文件（memory.py / SKILL.md / DATA-BOUNDARY.md），启动自动增量索引。
- 向量语义可选：检测到本机 OpenAI 兼容嵌入服务（默认 http://127.0.0.1:18436/v1/embeddings，如 bge-small-zh-v1.5）在线时自动建向量；离线自动降级纯关键词，不报错。
- AI 函数：MemoryReindex(rebuild, vec)、MemorySearch(query, k)。角色也可按 SKILL.md 手册经 Execution 插件直接跑 CLI。

依赖：Alife.Function.FunctionCaller（官方底座）；系统 PATH 中的 Python 3.8+（memory.py 纯标准库）。
数据边界：技能数据只写 Storage/Skills/memory/data/，绝不修改角色存档原文（详见 DATA-BOUNDARY.md）。
