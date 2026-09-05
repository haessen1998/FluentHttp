# SDK 审计与重构记录

本次将仓库收敛为六个可分发库、两个 SDK 示例及一个测试项目，目标框架保持 .NET 10，文件格式版本保持 1.0。

## 结构与发布

- 移除 MAUI、CLI、应用专用 AI/MAF 项目、MAUI 发布工作流及已跟踪的安装包。
- Storage/Try 迁至 `extensions`，保留命名空间及功能，更新解决方案和测试项目引用。
- 修复 CI 引用旧 `FluentHttp.sln` 的问题，增加测试和打包步骤。
- `scripts/pack.ps1` 只打包 `src` 和 `extensions`；两个示例显式关闭打包。
- 根 README、两个核心包 README、Storage/Try 包 README 和两个示例同步为 SDK 使用方式。

## 运行时发现与处理

| 发现 | 修复或增强 | 回归用例（SdkRegressionTests） |
| --- | --- | --- |
| 路径双花括号替换顺序错误，带 fragment 的 URL 查询拼接错误，数值受当前文化影响 | 先替换双花括号，查询插入 fragment 前，Fluent 参数用 invariant culture | `Fluent_request_encodes_path_query_and_uses_invariant_values` |
| 相对 URL 重定向无法依赖客户端 BaseAddress，301/302 将所有方法改成 GET | 基于实际请求 URI 解析 Location，保留应保留的方法与请求体 | `Redirect_preserves_method_semantics` |
| 跨源跳转重新应用认证，流式调用未使用统一重定向逻辑 | 统一发送流程，跨源移除已知敏感头并禁用认证继承 | `Cross_origin_redirect_removes_credentials_for_buffered_and_streamed_calls` |
| 外部客户端默认认证头会重新附加到跨源请求 | 拒绝此类跨源跳转，避免修改共享客户端 | `Cross_origin_redirect_rejects_default_client_credentials` |
| 查询认证重复构建风险，Inherit 没有采用 context 认证 | 每个请求首次只构建一次查询，显式处理 Inherit/None | `Query_auth_is_not_duplicated_and_inherited_auth_is_respected` |
| 请求选项写回调用方 context；缺少直接借用 HttpClient 的入口 | 对配置做浅快照，新增借用客户端构造函数 | `Execution_does_not_mutate_context_or_dispose_borrowed_client` |
| 泛型返回只处理字符串/JSON，异常缺少状态码且拼接整个响应正文 | byte[]、非泛型响应、ReadableHttpException 携带 Exchange/StatusCode | `Fluent_response_supports_binary_empty_and_structured_errors` |
| JSON 序列化选项写死 | WithJsonOptions 同时支持请求和响应 | `Fluent_json_options_apply_to_request_and_response` |
| 手写 JSON 数组解析接受截断和错误数据 | 标准流式 JSON 解析，Auto 保留非数组 Raw 回退 | `Json_array_stream_rejects_malformed_content`；原有 Auto 回退测试 |
| Raw 模式按字节块单独解码，跨块 UTF-8 损坏 | 使用有解码状态的 StreamReader | `Raw_stream_preserves_utf8_across_single_byte_reads` |
| DI 注册无法直接链式配置 SDK 命名客户端 | AddReadableHttpClient 返回 IHttpClientBuilder；factory.Request 提供 Fluent 入口 | `Named_factory_exposes_fluent_requests_and_handler_configuration` |
| 超时参数缺少早期校验 | 接受正值和 InfiniteTimeSpan，校验 HttpClient 支持范围 | `Timeout_rejects_invalid_values`、`Timeout_accepts_supported_boundaries` |
| 取消与不跟随重定向行为需要保护 | 保持取消异常传播和原始 3xx 响应 | `Cancellation_propagates_without_becoming_an_exchange_error`、`Disabled_redirects_return_original_response` |

## 使用与兼容性

- 现有 Fluent、执行器和默认 DI 注册入口保留。新异常继承 HttpRequestException，但消息不再包含响应体；需要正文时读取 Exchange。
- 泛型 JSON 调用仍要求非空且非 null JSON；204/HEAD 使用新的非泛型 SendAsync。
- builder 和模型仍可变；执行器不会回写 context 的顶层配置，但调用期间不要修改嵌套集合或认证对象。
- 外部 handler 的代理、Cookie、TLS 和自动重定向由宿主配置。SDK DI 注册关闭自动重定向及池化 Cookie 缓存。
- 函数工厂返回的 HttpClient 由执行器释放；直接传入的 HttpClient 由调用方释放。
- Raw 是 UTF-8 文本流。HTTP 非成功状态由流的 Headers 消息报告，解析和传输异常继续传播。
- 包版本更新为 2.1.0，本地打包不会发布到 NuGet。

## 验证

本机 SDK：10.0.400-preview.0.26322.102。CI 配置使用 .NET 10.0.x。

```shell
dotnet test tests/ReadableHttp.Tests/ReadableHttp.Tests.csproj --configuration Release --no-restore --nologo --disable-build-servers
dotnet build ReadableHttp.sln --configuration Release --no-restore --nologo --disable-build-servers
pwsh ./scripts/pack.ps1
```

- 测试：Passed: 76, Failed: 0, Skipped: 0；原有 49 个用例加本次 27 个回归用例。
- 完整 Release 构建：9 个项目，0 警告、0 错误。
- 打包：6 个库成功生成；全部包含 README，核心包包含三个 JSON Schema，示例不打包。
- 本地构建曾被 MSBuild 复用节点锁住 DLL；关闭构建服务器后，最终构建与测试成功。
