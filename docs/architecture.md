# 構成と検証

```
ブラウザ専用UI
  ├─ メモ編集 → .NET Notes API → atomic notes.json + .bak
  ├─ ルール検索 → immutable 日本語bigram/Latin BM25 index（RAM）
  └─ AIアシスト → .NET Harness
                       ├─ モードに応じた公式条文／個人メモの参照
                       ├─ system固定／history role検証
                       ├─ apply-template → tokenize → context budget調整
                       ├─ 明示されたTODOチェックリストの抽出ツール
                       └─ 子プロセス llama-server → SSE回答 → 出典番号検査
```

- `TimsahOffice.Core`: 純粋なルールインポート・検索、atomic JSON、階層ノート、モデル取得、推論プロセス、プロンプトとストリーム。
- `TimsahOffice.App`: loopback ASP.NET API、キャンセル可能な単一バックグラウンドジョブ、モデルの待機アンロード、静的UI。
- `tests/TimsahOffice.Tests`: 日本語検索、原文の表と版、無効な上流構造、ノート永続化・競合・階層循環・バックアップ、ダウンロードのSHA失敗、履歴のrole注入、設定境界。
- `scripts/smoke.py`: 実アプリAPIのHost/Origin/トークン、保存・競合、実モデルの自己名・ルール回答・メモ処理。`--inference`を付けた場合だけ実LLMを使う。
- `scripts/package.py`: 不変URLからエンジン／モデルを検証し、.NET 10 self-contained一式へ取り込む。OSライブラリの依存を除き、別のRuntimeインストールは不要。

## 保存と競合

Notes APIは同じプロセス内で書き込みを直列化し、ファイルの排他で同じ保存先の多重起動を防ぐ。メモ更新はrevision一致が必要。循環する親子関係、子のあるページ削除を拒否する。直前のnotes.jsonを.bakへコピーし、一時ファイルからatomic renameで新しいノート群を有効化する。ブラウザは未保存ドラフトを復元できる。複数PC間の同期や共同編集は実装しない。

## ハーネスとモデル

モデル重みを変更せず、systemに`Timsah-Assitant`を設定する。検索には日本語bigram・英数字語のBM25と少数の同義語を使う。SQLite・埋め込み・ベクトルDB・別の検索モデルは常駐させない。公式原文ブロックとMarkdown本文を索引化し、ルールナビの要約を規定と誤認しない。共通と競技システムは同一コミットから取得。

モデルごとのテンプレートをllama.cppが適用。`/apply-template`と`/tokenize`による実トークン数に最大出力と余裕128tokenを合わせてコンテキストに収める。必要なら古い履歴を外し、長い抜粋を短くする。短くした結果を出典カードに表示。プライベートメモは別のN番号を付ける。ソース番号の検査は意味の検証ではない。小さなモデルの誤回答・読み落としを防ぎ切ることはできない。

生成／起動／停止／待機アンロードは同じゲートで排他し、回答中にアンロードしない。起動と回答終了時に最終活動時刻を更新。5秒間隔でタイムアウトを確認し、子プロセス終了により重み・KV・計算バッファを解放。待機アンロード状態からのみ依頼時に再ロードする。手動停止は別の状態。取得と更新は別の単一ジョブゲートで直列化し、処理中は設定変更を拒否する。中断はCancellationTokenで処理し、.partを削除する。

## UI

ゼロビルドのローカルUI。生成Markdown・ユーザーMarkdownはDOMPurifyでサニタイズし、リンクのprotocolを制限する。外部画像は読み込まない。CSP、no-referrer、Origin/Host、Sec-Fetch-Site、ランダムAPIトークンにより外部サイトからローカルAPIへ到達しにくくする。会話はタブ内一時データ。回答をメモへ残せる。

## 検証範囲

Linuxで実モデルの起動・回答・モデル切替・待機アンロード／再ロード・終了後のプロセス解放を検証する。Windows/macOSはネイティブバイナリの起動と.NET/API/パッケージをGitHub Actionsで検証する。GUIのOS差異、macOS署名・公証は対象外。Gemma 4はテキストのみ使用し、mmprojや音声入力は対象外。カスタムモデルのテンプレート互換性はそのモデルに依存する。
