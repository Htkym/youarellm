# youarellm

[English](README.md) | [日本語](README.ja.md)

OpenAI-compatible LLM powered by a human operator.

The first target is GitHub Copilot CLI BYOK. Run this app locally and start a Copilot CLI session that uses the local OpenAI-compatible `human` model.

This source code is licensed under the [MIT License](LICENSE).

## Requirements

- .NET 10 SDK
- GitHub Copilot CLI

## Run the operator UI and API

```powershell
dotnet run --project .\src\YouAreLlm.Web\YouAreLlm.Web.csproj --urls http://localhost:5166
```

Open the operator UI:

```text
http://localhost:5166
```

The OpenAI-compatible API is available at:

```text
http://localhost:5166/v1
```

## Fleet simulation

The **Fleet simulation** page starts two self-authored worker tasks in parallel: one inspects the fixture specification and the other inspects its implementation. Complete both worker requests in **Operator**. The app then creates one parent request containing only the worker responses, which you complete with an evidence/action/verification synthesis.

This is an in-process parent-child task simulation, not an operating-system worker process or a GitHub Copilot Fleet subagent. It uses only the self-authored greeting fixture and mock `read_file` tool schema.

## MCP human delegation

The app exposes a streamable HTTP MCP endpoint at `http://localhost:5166/mcp`. Its `delegate_to_human` tool creates one pending Operator request and returns the human's final response to the MCP client.

```powershell
copilot mcp add --transport http youarellm http://localhost:5166/mcp
```

The tool accepts only one bounded task at a time. It does not persist ordinary MCP task content.

## Use from GitHub Copilot CLI

In a new terminal, set BYOK environment variables before starting Copilot CLI. This starts that Copilot CLI process in BYOK mode and uses `human` as its model.

PowerShell:

```powershell
$env:COPILOT_PROVIDER_BASE_URL = "http://localhost:5166/v1"
$env:COPILOT_PROVIDER_TYPE = "openai"
$env:COPILOT_MODEL = "human"
copilot
```

Command Prompt:

```batch
set COPILOT_PROVIDER_BASE_URL=http://localhost:5166/v1
set COPILOT_PROVIDER_TYPE=openai
set COPILOT_MODEL=human
copilot
```

Alternatively, set only the provider endpoint and choose the model at launch:

```batch
set COPILOT_PROVIDER_BASE_URL=http://localhost:5166/v1
set COPILOT_PROVIDER_TYPE=openai
copilot --model human
```

Then send a prompt in Copilot CLI. The request appears in the operator UI. Type a response and select **Send final**. Use **Send progress** to stream partial output.

Copilot CLI BYOK requires streaming and tool/function calling support. This app implements that flow for both Chat Completions and Responses. `/v1/chat/completions` remains the default.

To use the Responses API, switch the Copilot CLI wire API before starting it:

```powershell
$env:COPILOT_PROVIDER_WIRE_API = "responses"
copilot
```

`POST /v1/responses` accepts `input`, `instructions`, function calls, and function call outputs, then returns Responses-format SSE events. `previous_response_id` continues context from completed requests held by this process; IDs do not survive an app restart.

### Token usage estimates

Completed Chat Completions and Responses responses include local `usage` estimates. They use the `o200k_base` tokenizer to count the effective message content, declared tools, and text or function-call output. The values are estimates, not provider billing records; request framing and any client-side context that this server does not receive are not included.

After the operator completes a request, the operator UI automatically opens its completed history entry and shows the same input, output, and total estimates.

For Chat Completions streaming, send `stream_options.include_usage: true` to receive a final empty-choice chunk with `usage` before `[DONE]`. Responses streaming includes usage in its terminal `response.completed` event.

Copilot CLI's built-in `/usage` reports GitHub account consumption. A local BYOK provider cannot update that account-level value, even when it returns protocol-level token usage.

### Reproducible token measurement

The generic token measurement tool and procedure are available in
[`token\`](token\README.md). They exclude captured requests and other
confidential context; local captures remain under Git-ignored `research-data\`.

### About `/model`

Copilot CLI BYOK does not currently add `human` to the normal GitHub-hosted model picker. Setting `COPILOT_PROVIDER_BASE_URL` switches that process to a custom provider, and the model must be supplied with `COPILOT_MODEL` or `copilot --model human`.

To return to the normal GitHub Copilot model picker, start a new terminal without the BYOK environment variables, or clear them first:

PowerShell:

```powershell
Remove-Item Env:COPILOT_PROVIDER_BASE_URL -ErrorAction SilentlyContinue
Remove-Item Env:COPILOT_PROVIDER_TYPE -ErrorAction SilentlyContinue
Remove-Item Env:COPILOT_MODEL -ErrorAction SilentlyContinue
Remove-Item Env:COPILOT_PROVIDER_WIRE_API -ErrorAction SilentlyContinue
```

Command Prompt:

```batch
set COPILOT_PROVIDER_BASE_URL=
set COPILOT_PROVIDER_TYPE=
set COPILOT_MODEL=
set COPILOT_PROVIDER_WIRE_API=
```

## Aspire

You can also run the Aspire AppHost:

```powershell
dotnet run --project .\src\YouAreLlm.AppHost\YouAreLlm.AppHost.csproj
```

The Dashboard shows the incoming Copilot CLI `/v1/chat/completions` and `/v1/responses` requests as distributed traces. Each request also contains a `gen_ai.chat` span, which remains open while the operator prepares the response. It records only the protocol, selected model, streaming mode, input-message count, and completion kind; prompt and response content are intentionally excluded from trace attributes.

You can stream the captured spans from another terminal:

```powershell
aspire otel spans youarellm-web --apphost .\src\YouAreLlm.AppHost\YouAreLlm.AppHost.csproj --follow
```

The AppHost also starts two interactive Copilot CLI resources. `copilot-completions` uses Chat Completions, while `copilot-responses` sets `COPILOT_PROVIDER_WIRE_API=responses`. Both resources receive the local `youarellm-web` `/v1` endpoint and the `human` model configuration automatically. Open the selected resource's terminal in the Dashboard to complete the initial login, workspace-trust, and permission prompts.

For the CLI BYOK quick path, running `YouAreLlm.Web` directly on `http://localhost:5166` is the most predictable option. Direct runs do not export telemetry unless `OTEL_EXPORTER_OTLP_ENDPOINT` is configured.

## API

- `GET /v1/models` returns the `human` model.
- `POST /v1/chat/completions` accepts OpenAI-compatible Chat Completions requests.
- `POST /v1/responses` accepts OpenAI-compatible Responses requests.
- Streaming responses are sent as Server-Sent Events.

## Security and prompt visibility

Copilot CLI BYOK sends the model request to the configured provider. When `youarellm` is the provider, the operator UI can show the messages that Copilot CLI sends to the model, including `system`, `developer`, tool, and user messages when they are present in the request.

Treat this data as confidential. Run the endpoint locally, do not expose it on a public network, and do not publish or share prompt contents unless you have confirmed that doing so is allowed for your use case.

Only requests explicitly marked with the `X-YouAreLlm-Self-Authored: true` header are saved verbatim for local research in `research-data\self-authored-raw-prompts\`, outside `presentation\` and excluded from Git. Ordinary BYOK requests are not persisted. The path is configured by `ResearchCapture:Directory`.

This project is intended as a local BYOK provider experiment. It is not intended to extract, publish, or redistribute GitHub Copilot system prompts, developer prompts, internal prompts, or other confidential prompt/context data.

Do not use this project to modify, patch, repackage, or redistribute GitHub Copilot CLI or any other third-party product. Use it only through documented provider configuration such as `COPILOT_PROVIDER_BASE_URL`.

For local-only operation, you can also start Copilot CLI with:

```batch
set COPILOT_OFFLINE=true
```

## Legal and publication notes

This project is not affiliated with, endorsed by, or sponsored by GitHub or Microsoft. Review the current GitHub Copilot, GitHub, Microsoft, and third-party terms that apply to your environment before publishing demos, screenshots, logs, or articles.

### Public source repositories

This repository publishes the application's source code, tests, documentation,
and the public-safe token measurement materials in `token\`. Do not commit
captured prompts, tool results, private repository context, credentials,
personal data, screenshots, or generated demo materials. `research-data\`,
`presentation\`, `outputs\`, and PowerPoint artifacts are excluded from Git to
reduce accidental publication.

The application has no authentication and is designed only for local development. Do not bind it to a public network or deploy it as an internet-facing service.

When presenting or publishing about this project:

- Describe it as a local OpenAI-compatible BYOK provider or human-in-the-loop experiment.
- Do not publish real system, developer, tool, or internal prompt contents.
- Do not publish request logs or screenshots that include confidential code, credentials, personal data, or private repository context.
- Do not frame the project as a prompt extraction, reverse engineering, or Copilot internals disclosure tool.
- Keep the service bound to local development environments unless you have reviewed the security and legal implications of exposing it.

## VS Code Chat

VS Code Chat support is the next target after the CLI path. The same endpoint can be registered as a Custom Endpoint model with model ID `human`; a VS Code extension can later provide the model and an `@youarellm /human <prompt>` command.
