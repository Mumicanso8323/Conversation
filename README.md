# Conversation

贅沢な会話シミュレーション（C# / .NET, ChatCompletions ベース）。

## 会話フロー（現状）

1. ユーザー入力を受け取る
2. `/` 始まりならコマンド層で処理して終了（API送信しない）
3. 通常入力ならセッション状態をロード
4. `MaybeSummarizeAsync` で一定ターン超過時に古い履歴を要約して `SummaryMemory` に圧縮
5. `BuildMessagesForModel` で以下順にモデル入力を構築
   - System: Persona/固定ルール
   - System: `[MEMORY]` 要約メモ
   - 直近 `KeepLastTurns` の履歴
   - 今回のユーザー入力
6. ChatClient (`CompleteChatAsync` / `CompleteChatStreamingAsync`) を実行
7. user/assistant ターンを状態に追加し、JSON保存

## セッション永続化

- 保存先: `sessions/{sessionId}.json`
- ストア実装: `JsonFileChatStateStore`

## コマンド

- `/save`
- `/load <id>`
- `/reset`
- `/aff`
- `/psy`
- `/persona {id}`
- `/export`（`exports/{sessionId}-{timestamp}.json` に出力 + コンソール表示）
- `/import <path>`（省略時は `import.json`）
- `/help`
- `/exit`
