# youarellm

[English](README.md) | [日本語](README.ja.md)

人間のオペレーターが応答する OpenAI 互換 LLM です。

最初の対象は GitHub Copilot CLI の BYOK です。このアプリをローカルで起動し、ローカルの OpenAI 互換 `human` モデルを使う Copilot CLI セッションを開始します。

このソースコードは [MIT License](LICENSE) で提供します。

## 必要なもの

- .NET 10 SDK
- GitHub Copilot CLI

## オペレーター UI と API の起動

```powershell
dotnet run --project .\src\YouAreLlm.Web\YouAreLlm.Web.csproj --urls http://localhost:5166
```

オペレーター UI を開きます。

```text
http://localhost:5166
```

OpenAI 互換 API は次の URL で利用できます。

```text
http://localhost:5166/v1
```

## Fleet simulation

**Fleet simulation** ページでは、自己作成の worker task を 2 件並列に開始します。1 件は fixture の仕様、もう 1 件は実装を確認します。**Operator** で両方の worker request を完了すると、worker の応答だけを受け取る親 request が作成されます。親には evidence / action / verification の統合結果を返します。

これは OS の worker process や GitHub Copilot Fleet の subagent ではなく、in-process の親子タスク simulation です。自己作成の greeting fixture と mock `read_file` tool schema だけを使います。

## MCP による人間への委譲

アプリは streamable HTTP の MCP endpoint を `http://localhost:5166/mcp` で公開します。`delegate_to_human` tool を呼ぶと、Operator に保留 request が 1 件作成され、人間の最終応答を MCP client へ返します。

```powershell
copilot mcp add --transport http youarellm http://localhost:5166/mcp
```

tool が受け取れるのは一度に 1 件の、範囲を限定した作業だけです。通常の MCP task 本文は永続化しません。

## GitHub Copilot CLI から使う

新しいターミナルで、Copilot CLI を起動する前に BYOK 用の環境変数を設定します。これにより、その Copilot CLI プロセスは BYOK モードで起動し、`human` をモデルとして使います。

PowerShell:

```powershell
$env:COPILOT_PROVIDER_BASE_URL = "http://localhost:5166/v1"
$env:COPILOT_PROVIDER_TYPE = "openai"
$env:COPILOT_MODEL = "human"
copilot
```

コマンド プロンプト:

```batch
set COPILOT_PROVIDER_BASE_URL=http://localhost:5166/v1
set COPILOT_PROVIDER_TYPE=openai
set COPILOT_MODEL=human
copilot
```

プロバイダーのエンドポイントだけを設定し、起動時にモデルを指定することもできます。

```batch
set COPILOT_PROVIDER_BASE_URL=http://localhost:5166/v1
set COPILOT_PROVIDER_TYPE=openai
copilot --model human
```

その後、Copilot CLI でプロンプトを送ると、リクエストがオペレーター UI に表示されます。応答を入力して **Send final** を選択します。途中の出力をストリーミングしたい場合は **Send progress** を使います。

Copilot CLI BYOK では、ストリーミングと tool/function calling のサポートが必要です。このアプリは Chat Completions と Responses の両方でその流れを実装しています。既定は `/v1/chat/completions` です。

Responses API を使う場合は、Copilot CLI を起動する前に wire API を切り替えます。

```powershell
$env:COPILOT_PROVIDER_WIRE_API = "responses"
copilot
```

`POST /v1/responses` は `input`、`instructions`、function call と function call output を受け取り、Responses 形式の SSE event を返します。`previous_response_id` は、このプロセス内にある完了済みリクエストの会話文脈を引き継ぎます。アプリを再起動した後の ID は利用できません。

### トークン使用量の推定

完了した Chat Completions と Responses の応答には、ローカルで計算した `usage` 推定値が含まれます。`o200k_base` tokenizer で、実際に処理するメッセージ内容、宣言済みの tool、テキストまたは function call の出力を数えます。値は provider の課金記録ではありません。リクエストの framing や、このサーバーが受け取らない client 側の文脈は含みません。

オペレーターがリクエストを完了すると、UI は対応する履歴を自動で開き、同じ input、output、total の推定値を表示します。

Chat Completions のストリーミングで使用量が必要な場合は、`stream_options.include_usage: true` を指定します。`[DONE]` の前に、`usage` を含む空の choice の最終 chunk を返します。Responses のストリーミングでは、終端の `response.completed` event に usage を含めます。

Copilot CLI の組み込み `/usage` は GitHub アカウントの使用量を示します。ローカル BYOK provider が protocol 上の token usage を返しても、このアカウント単位の値は更新できません。

### `/model` について

Copilot CLI BYOK は、現時点では通常の GitHub-hosted model picker に `human` を追加しません。`COPILOT_PROVIDER_BASE_URL` を設定すると、そのプロセスは custom provider に切り替わります。モデルは `COPILOT_MODEL` または `copilot --model human` で指定する必要があります。

通常の GitHub Copilot model picker に戻すには、BYOK 用の環境変数がない新しいターミナルを開くか、先に環境変数を消します。

PowerShell:

```powershell
Remove-Item Env:COPILOT_PROVIDER_BASE_URL -ErrorAction SilentlyContinue
Remove-Item Env:COPILOT_PROVIDER_TYPE -ErrorAction SilentlyContinue
Remove-Item Env:COPILOT_MODEL -ErrorAction SilentlyContinue
Remove-Item Env:COPILOT_PROVIDER_WIRE_API -ErrorAction SilentlyContinue
```

コマンド プロンプト:

```batch
set COPILOT_PROVIDER_BASE_URL=
set COPILOT_PROVIDER_TYPE=
set COPILOT_MODEL=
set COPILOT_PROVIDER_WIRE_API=
```

## Aspire

Aspire AppHost から起動することもできます。

```powershell
dotnet run --project .\src\YouAreLlm.AppHost\YouAreLlm.AppHost.csproj
```

Dashboard のトレースでは、Copilot CLI からの `/v1/chat/completions` と `/v1/responses` のリクエストを確認できます。各リクエストには、オペレーターの応答待ちを含めた `gen_ai.chat` span も記録されます。属性に記録するのは API 種別、選択されたモデル、ストリーミングの有無、入力メッセージ数、完了種別だけです。プロンプト本文と応答本文は記録しません。

別のターミナルで span を追跡できます。

```powershell
aspire otel spans youarellm-web --apphost .\src\YouAreLlm.AppHost\YouAreLlm.AppHost.csproj --follow
```

AppHost は、対話用の Copilot CLI resource も 2 つ起動します。`copilot-completions` は Chat Completions を使い、`copilot-responses` は `COPILOT_PROVIDER_WIRE_API=responses` を設定します。どちらにもローカルの `youarellm-web` `/v1` endpoint と `human` model が自動設定されます。Dashboard で resource を選び、terminal を開いて初回のログイン、workspace の信頼確認、権限承認を行ってください。

Copilot CLI BYOK の最短経路では、`YouAreLlm.Web` を `http://localhost:5166` で直接起動するのが最も予測しやすい方法です。直接起動する場合、`OTEL_EXPORTER_OTLP_ENDPOINT` を設定しない限りテレメトリは export されません。

## API

- `GET /v1/models` は `human` モデルを返します。
- `POST /v1/chat/completions` は OpenAI 互換の Chat Completions リクエストを受け取ります。
- `POST /v1/responses` は OpenAI 互換の Responses リクエストを受け取ります。
- ストリーミング応答は Server-Sent Events で送信されます。

## セキュリティとプロンプトの可視性

Copilot CLI BYOK は、設定されたプロバイダーにモデルリクエストを送信します。`youarellm` がプロバイダーの場合、オペレーター UI には Copilot CLI がモデルへ送るメッセージが表示されます。リクエストに含まれている場合は、`system`、`developer`、tool、user メッセージも表示されます。

このデータは機密情報として扱ってください。エンドポイントはローカルで実行し、公開ネットワークに露出しないでください。また、自分の用途で許可されていることを確認しない限り、プロンプトの内容を公開したり、共有したりしないでください。

`X-YouAreLlm-Self-Authored: true` ヘッダーを明示した自己作成のリクエストだけを、研究用として `research-data\self-authored-raw-prompts\` に未加工のまま保存します。通常の BYOK リクエストは永続化しません。このフォルダは `presentation\` の外にあり、Git 管理から除外されます。保存先は `ResearchCapture:Directory` で変更できます。

このプロジェクトは、ローカルの BYOK provider 実験として作られています。GitHub Copilot の system prompt、developer prompt、internal prompt、その他の機密 prompt/context data を抽出、公開、再配布することを目的としていません。

このプロジェクトを使って、GitHub Copilot CLI やその他の第三者製品を変更、パッチ適用、再パッケージ、再配布しないでください。`COPILOT_PROVIDER_BASE_URL` など、文書化された provider configuration を通じてのみ使ってください。

ローカルのみで動作させる場合は、Copilot CLI を次のように起動することもできます。

```batch
set COPILOT_OFFLINE=true
```

## 法務と公開時の注意

このプロジェクトは GitHub または Microsoft と提携しておらず、承認やスポンサーを受けたものでもありません。デモ、スクリーンショット、ログ、記事を公開する前に、自分の環境に適用される GitHub Copilot、GitHub、Microsoft、第三者の最新の規約を確認してください。

### public repository の公開範囲

このリポジトリで公開するのは、アプリケーションのソースコード、テスト、ドキュメントだけです。取得した prompt、tool の実行結果、private repository の文脈、認証情報、個人データ、スクリーンショット、生成したデモ資料は commit しないでください。誤って公開しないよう、`research-data\`、`presentation\`、`outputs\`、PowerPoint の成果物は Git 管理から除外しています。

このアプリケーションには認証がなく、ローカル開発環境で使う設計です。公開ネットワークへ bind したり、インターネット向けサービスとして配備したりしないでください。

このプロジェクトについて発表または公開するときは、次の点に注意してください。

- ローカルの OpenAI 互換 BYOK provider、または human-in-the-loop 実験として説明する。
- 実際の system、developer、tool、internal prompt の内容を公開しない。
- 機密コード、認証情報、個人データ、private repository context を含むリクエストログやスクリーンショットを公開しない。
- prompt extraction、reverse engineering、Copilot internals disclosure のためのツールとして見せない。
- 公開することのセキュリティと法務上の影響を確認しない限り、サービスはローカル開発環境に閉じる。

## VS Code Chat

VS Code Chat 対応は、CLI の経路が動いた後の次の対象です。同じエンドポイントを model ID `human` の Custom Endpoint model として登録できます。将来的には、VS Code extension で model と `@youarellm /human <prompt>` command を提供できます。
