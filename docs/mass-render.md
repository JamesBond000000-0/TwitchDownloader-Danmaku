# CLI massrender — ユーザーの動画を一括で取得して danmaku レンダリング

`massrender` は CLI verb。指定した Twitch ユーザーの動画一覧を取得し、各動画について
「チャット JSON のダウンロード → danmaku レンダリング」を自動で繰り返す。
**重複回避**: 処理が成功した動画 ID を state ファイル（テキスト）に追記し、
次回以降の実行ではその ID をスキップする。失敗した動画は記録されないので次回リトライされる。

このドキュメントは `mass-cli` ブランチ用（`danmaku` ブランチから分岐）。

## 使い方

```bash
# 新しいものを最大5本まで処理
TwitchDownloaderCLI massrender -u mrtenzouez -o ./out

# 何が処理されるかだけ確認（ダウンロード・レンダリングしない）
TwitchDownloaderCLI massrender -u mrtenzouez -o ./out --dry-run --limit 10

# 過去配信（ARCHIVE）だけ、速さ固定モードで
TwitchDownloaderCLI massrender -u mrtenzouez -o ./out --type ARCHIVE --speed-mode fixed --limit 3
```

`-u` にはログイン名のほかチャンネル URL（https://www.twitch.tv/xxx）も渡せる。
存在しないユーザーはその場でエラー終了する（黙って何もしない、にはならない）。

## 出力レイアウト

```
<output>/
  mass-state.txt          ← 成功した ID の記録（既定の state ファイル）
  <videoId>/
    <videoId>_chat.json   ← ダウンロードしたチャット
    <videoId>_danmaku.mp4 ← レンダリング結果
```

## state ファイル形式

1行に1件、タブ区切り: `動画ID <TAB> 完了日時(UTC) <TAB> タイトル <TAB> 種類`。
種類は完了した処理のカンマ区切り（`video` / `chat` / `render`）で、記録は
「ID × 種類」単位。同じ ID の行が複数あれば種類は合算される。
種類フィールドの無い旧形式の行は `chat,render` 済みとして扱う（massrender が
行うのはその2つだけなので実態と一致する）。手で ID だけを書いた行や
`#` で始まるコメント行も有効（ID のみの行は旧形式として扱われる）。
`--state` で場所を変更できる。

massrender は `chat` と `render` の両方が記録済みの動画だけをスキップする。
片方だけ記録されている場合は通常通り処理する（チャットは自分の出力先に
取り直す）。

## 主なオプション

| オプション | 既定値 | 説明 |
| --- | --- | --- |
| `-u, --user` | （必須） | ログイン名またはチャンネル URL |
| `-o, --output` | （必須） | 出力ベースディレクトリ |
| `--limit` | 5 | 1回で処理する「新規」動画の最大数（スキップ分は数えない） |
| `--type` | 全て | ARCHIVE / HIGHLIGHT / UPLOAD |
| `--state` | `<output>/mass-state.txt` | state ファイルのパス |
| `--dry-run` | off | 対象の列挙だけ行う |
| `--render-start` / `--render-length` | 0 / 0（全編） | レンダリング区間の指定（動作確認用など） |
| `--duration` / `--speed-mode` / `--max-lanes` | 6 / uniform / 0 | danmakurender と同じ意味 |
| `-w` / `-h` / `--font-size` / `--fps` | 1920 / 1080 / 36 / 30 | 描画パラメータ |
| `--shadow` / `--band` / `--show-name` | off / 0 / on | danmakurender と同じ意味 |

## GUI との共有 (danmaku-mass ブランチ)

重複回避リストの形式は GUI の Mass Downloader 経路と共通化されている
（実装は `TwitchDownloaderCore/Tools/DedupList.cs`）。
GUI 側の設定で同じファイルを指定すれば、CLI で処理した動画を GUI がスキップし、
その逆も効く。記録は種類ごとで、GUI 側は各タスク（動画 DL / チャット DL /
レンダリング）が成功した時点で自分の種類を追記する。そのため「GUI でチャットだけ
取得した動画」を CLI や GUI で後から動画 DL してもブロックされない。

## 挙動メモ

- 動画は新しい順に処理する。一覧は最大20ページ（50件/ページ）まで走査する
- チャットはエモート埋め込みあり（EmbedData）で JSON ダウンロードする
- レンダリングの既定は全編。末尾のコメントが流れ切るよう、動画の長さ＋横断時間＋1秒ぶん描く
- Ctrl+C で中断できる。中断時点で未完了の動画は state に書かれない
- レンダリング結果ファイルが空/存在しない場合は失敗として扱い、記録しない

## 実測 (2026-10-23, mrtenzouez)

- `--dry-run --limit 3`: 一覧取得と対象列挙を確認
- state に最新 ID を種入れ → その ID だけスキップされ、limit は新規分で数えられた
- 実 run（54分 VOD、--render-length 20）: チャットDL＋描画＋state 追記まで約33秒で完了、
  再 run では同 ID がスキップされた
