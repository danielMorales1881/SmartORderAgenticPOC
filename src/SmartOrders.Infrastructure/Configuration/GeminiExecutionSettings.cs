// GeminiExecutionSettings removed — per-agent settings are now passed via ChatOptions:
//   ResponseFormat = ChatResponseFormat.Json          → JSON mode
//   MaxOutputTokens = N                               → max tokens
//   AdditionalProperties["thinking_budget"] = 0       → disable thinking
// See Program.cs IChatClientBuilder.ConfigureOptions() setup.
