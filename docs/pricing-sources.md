# 内置模型参考价格

核对日期：2026-10-31。版本：`OpenAI-Standard-2026-10-31`。数据文件：`model-prices.builtin.json`，嵌入程序资源，运行时无需联网下载或手动配置。

此表用于将有明细的 Token 记录按公开 Standard API 单价折算为参考金额，不是 ChatGPT 订阅价格或账单。未公布价格的内部模型不会猜价；旧模型保留价格仅为历史记录估算，不表示仍可调用。记录中精确模型名与价格表匹配，不通过删除后缀推测模型。

官方来源：

- [统一 API 定价](https://developers.openai.com/api/docs/pricing)：Standard 定价、ChatGPT `chat-latest`、`gpt-5.3-codex` 与 GPT-5.6 Sol 参考价格。
- [Prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching)：缓存读取与写入属于不同输入分类；本表采用默认 30 分钟缓存写入费率。
- [GPT-6 Astra](https://developers.openai.com/api/docs/models/gpt-6-astra)、[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)、[GPT-6.1 Terra](https://developers.openai.com/api/docs/models/gpt-6.1-terra)、[GPT-6 Sol](https://developers.openai.com/api/docs/models/gpt-6-sol)、[GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna)：基础单价、缓存写入和长上下文规则。
- [GPT-5.6 Sol](https://developers.openai.com/api/docs/models/gpt-5.6-sol)、[GPT-5.6 Luna](https://developers.openai.com/api/docs/models/gpt-5.6-luna)、[GPT-5.4](https://developers.openai.com/api/docs/models/gpt-5.4)。GPT-5.6 Sol 为核对日有效的促销价格，后续调整需更新内置快照。
- [GPT-5.2-Codex](https://developers.openai.com/api/docs/models/gpt-5.2-codex)、[GPT-5.1-Codex](https://developers.openai.com/api/docs/models/gpt-5.1-codex)、[GPT-5.1-Codex-Max](https://developers.openai.com/api/docs/models/gpt-5.1-codex-max)、[GPT-5.1-Codex-Mini](https://developers.openai.com/api/docs/models/gpt-5.1-codex-mini)、[GPT-5-Codex](https://developers.openai.com/api/docs/models/gpt-5-codex)。
- [GPT-5.2](https://developers.openai.com/api/docs/models/gpt-5.2)、[GPT-5.1](https://developers.openai.com/api/docs/models/gpt-5.1)、[GPT-5](https://developers.openai.com/api/docs/models/gpt-5)、[GPT-5 Mini](https://developers.openai.com/api/docs/models/gpt-5-mini)、[GPT-5 Nano](https://developers.openai.com/api/docs/models/gpt-5-nano)。
- [GPT-4.1](https://developers.openai.com/api/docs/models/gpt-4.1)、[GPT-4.1 Mini](https://developers.openai.com/api/docs/models/gpt-4.1-mini)、[GPT-4.1 Nano](https://developers.openai.com/api/docs/models/gpt-4.1-nano)、[GPT-4o](https://developers.openai.com/api/docs/models/gpt-4o)、[GPT-4o Mini](https://developers.openai.com/api/docs/models/gpt-4o-mini)。
- [o3](https://developers.openai.com/api/docs/models/o3)、[o4-mini](https://developers.openai.com/api/docs/models/o4-mini)。

费用按未缓存输入、缓存读取、缓存写入、输出分别计算。推理输出包含于输出中，不另加一次。长上下文阈值按每次请求的输入 Token 判断；费用为当前价格快照对既有记录的参考重算，不代表当时付款。高级缓存保留时长、加速模式、地区、工具等附加费用不包含在此基准内。
