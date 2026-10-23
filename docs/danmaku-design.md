# ニコニコ式（流れるコメント）レンダラ 設計メモ

目的: TwitchDownloader のチャット描画資産を流用し、Twitch VOD のチャットを
ニコニコ動画式に右から左へ流して動画に合成する。ニコニコ関連機能は作らない。

ベース: fork (JamesBond000000-0/TwitchDownloader) master = 本家 8adfd8e (2026-10-06)
ライセンス: MIT

## 方針: ChatRenderer の partial クラスとして実装する

- `TwitchDownloaderCore/ChatRenderer.cs` は `public sealed partial class`。
  同一アセンブリに `ChatRenderer.Danmaku.cs` を追加すれば、private な
  ペイント/キャッシュ/描画ヘルパーをそのまま再利用できる（本体改造が最小）。
- 新規 CLI モード `danmakurender` を追加（既存 `chatrender` と並列）。

## 流用するもの（既存実装）

- チャット JSON のモデルと取得 (ChatDownloader / Chat/ChatJson.cs)
  - `content_offset_seconds` が各コメントの表示時刻
- エモート取得・キャッシュ: Twitch / BTTV / FFZ / 7TV、静止画＋動画
- 絵文字: Twemoji / Noto Color Emoji の画像描画（カラー対応）
- バッジ描画、ユーザー名の色 (user_color / デフォルトパレット / 視認性調整)
- メッセージをテキスト片＋エモート片に分解し SKImage 化する処理
  - 動画エモートは静止画スプライトに焼かず、フレーム毎に位置を合わせて上描きする既存方式
- 時間区間ごとに描画するセクション方式（長尺 VOD のメモリ対策）
- ffmpeg への rawvideo (bgra/rgba) パイプ (FfmpegProcess)

## 新規に書くもの

### 1. コメントスプライト生成（単一行）
- 折り返しなし (noWrap) で「名前: 本文」を1行の SKImage に描く
  - 名前は user_color、本文は MessageColor、を想定
  - オプション: 名前表示 on/off、バッジ on/off（既定 off、横幅が伸びるため）
- 同一本文の繰り返し（エモートだけの連投など）が多いので、本文ハッシュでスプライトをキャッシュ

### 2. レーン割当て (DanmakuLaneAllocator)
- 画面を fontSize ベースの段（レーン）に分割
- v1 は全コメント一様速度（例: 画面幅を 6 秒で横切る px/sec）
  - 一様速度なら追い越しが起きず、割当て条件は「前のコメントの末尾が画面内に入ったか」だけになる
  - 長いコメントほど表示時間は伸びる（v1 では許容、速度可変は v2 課題）
- 空きレーンが無ければそのコメントは落とす（ニコニコと同じ挙動）。落とした件数をログに出す

### 3. フレーム合成ループ
- 時刻 t のフレーム: 各アクティブコメントの x = 画面幅 - 速度 × (t - 出現時刻)
- 背景は透明で出力し、ffmpeg の overlay で VOD に合成する（単体 mp4 出力も可）
- 既定 30fps（1080p・60fps・bgra パイプは約 500MB/s になるため）
- 時刻同期用に --offset（秒、±）オプション

## CLI イメージ

```
TwitchDownloaderCLI danmakurender -i <vodid>_chat.json \
  -w 1920 -h 1080 --framerate 30 --font-size 36 \
  --duration 6 --offset 0 --show-name true -o <vodid>_danmaku.mp4
# 合成は別途: ffmpeg -i vod.mp4 -i danmaku.mp4 -filter_complex overlay -c:a copy out.mp4
```

## マイルストーン

- M1: ✅ 完了 (2026-10-23) スプライト生成＋静止画1フレーム PNG 出力
  - 実装: `TwitchDownloaderCore/ChatRenderer.Danmaku.cs` (partial クラス)、
    CLI verb `danmakurender` (`TwitchDownloaderCLI/Modes/RenderDanmaku.cs`、
    `Modes/Arguments/DanmakuRenderArgs.cs`、Program.cs に登録)
  - 方式: ChatWidth=8192 で折り返しを無効化して単一行スプライト化し、
    描画画像の不透明ピクセル走査で実コンテンツ幅を測定 (M1 簡略化)、
    一様速度でレーン割当て (先入れ優先、空きがなければ落とす)
  - 動作確認: `samples/danmaku-sample-chat.json` (合成サンプル14件) で
    1920x1080・t=9s・duration=6s のフレームを生成。画面内12件・落下0件、2秒で完了
    - 名前色 (user_color)、日本語、カラー絵文字 (🔥 を画像描画)、
      Twitch エモート画像 (Kappa) のインライン表示を確認
  - 既知の問題:
    - この VM からは BTTV API がタイムアウトし、サードパーティ有効だと
      FetchScaledImages が TaskCanceled で落ちる (本家 chatrender でも同じはず)。
      サンプル実行では --bttv/--ffz/--stv false で回避。M2 で取得失敗を
      非致命的にできないか検討
    - レーン割当てのために先頭から全コメントのスプライト化が必要 (長尺で重い)。
      M2/M3 でテキスト幅測定のみで割当てする方式に変える
- M2: ✅ 完了 (2026-10-23) レーン割当て＋フレームループ＋ffmpeg パイプで mp4 生成
  - 実チャットで検証: VOD 2907540342 の 29-32分チャット (40件) を使い、
    danmakurender --video で 120秒・30fps・コメント30件配置・落下0件を 67秒で生成
    (テンプレートキャッシュ追加: 同一ユーザー・同文の繰り返しはスプライト再利用)
  - オーバーレイプレビュー: 透明背景→ProRes 4444 (.mov) で書き出し、
    実 VOD 映像 (1080p60) に ffmpeg overlay で合成し、実映像上で流れることを確認
  - ⚠️ VP9/WebM のαはこの ffmpeg 8.1.2 ビルドでは失われる (yuva420p 指定でも
    yuv420p になることを切り分け確認済み)。α中間形式は ProRes 4444 一択だが
    重い (60秒で 289MB、エンコード約7分)。M3 では中間形式をやめ、
    「VOD 入力＋danmaku 生フレームパイプを1つの ffmpeg で overlay 合成」
    の単一パス方式にする
  - ⚠️ この区間はチャット密度が低め (約13件/分) で、画面内は常時1-3件程度。
    密度の高い配信/区間では見え方が変わる点に注意
- M3: ✅ 完了 (2026-10-23) 単一パス直接合成＋offset＋高速化
  - `--base-video <file>` で単一パス合成: ffmpeg に VOD と danmaku 生フレームを
    2入力で渡し overlay を1回で実行。中間ファイルなし。
    実測 (60秒・実チャット): 旧方式 (ProRes 7分＋合成 4分＝約11分、289MB 中間)
    → **3分54秒・中間ゼロ**。残りの時間はほぼ映像の x264 エンコードそのもの
  - `--base-start` でベース映像側の対応位置を指定 (既定は --start と同じ＝
    ベースがフル VOD 前提。切り出し済みセグメントなら 0)
  - `--offset` でコメント時刻を前後シフト (チャットと映像のズレ補正)
  - フレームループをスライディングウィンドウ化 (表示中スプライトだけ走査)。
    単体動画モードは回帰なし (30件・落下0)、67秒→61秒に微改善
  - 備考: ユーザー判断で「VOD まるごと生成」は当面しない (時間がかかるため)。
    対象は区間指定の利用が中心
- M4: ✅ 完了 (2026-10-23) 速度可変＋密度調整
  - `--speed-mode fixed`: 表示時間固定方式 (ニコニコ式)。全コメントの表示時間が
    --duration 秒で統一され、長いコメントほど速く流れる。レーン割当てに
    追い越し判定 (CanEnterAfter: 前コメントの末尾が入り切っていること＋
    前コメントが抜け切るまでに追いつかないこと) を実装。既定は従来の
    `--speed-mode uniform` (一様速度)
  - `--max-lanes N`: 使用レーン数を上から N 本に制限 (0=全画面)。溢れた
    コメントは落として件数をログに出す (ニコニコと同じ挙動)
  - 検証: 高密度サンプル (635件/90秒 ≒ 7件/秒) で実施
    - fixed・全レーン: t=45 で画面内56件・重なりなし (目視)
    - fixed・--max-lanes 6: 画面内30件・落下26件 (意図通りの間引き)
    - fixed で 20秒動画: 配置202件・落下2件、生成18秒。静止画と動画で
      同時刻のレイアウト一致も確認

## GUI 配線 (WPF、2026-10-23)

- Chat Render ページに **Danmaku タブ** を追加 (`PageChatRender.xaml`):
  有効化チェック、スクロール時間、速度モード (Uniform/Fixed)、最大レーン数、
  帯の不透明度、影、名前非表示。ラベルは英語のみ (ローカライズ未対応)
- 設定は `ChatRenderOptions` の Danmaku* プロパティ経由で Core に渡る
  (`PageChatRender.GetOptions`)。キャンバスサイズ・フォント・背景色・出力
  フォーマット/コーデックは既存タブの設定をそのまま使う
- 実行経路は2つとも対応: ページの Render ボタン (`SplitBtnRender_Click`、
  Partial render の範囲指定も StartOverride/EndOverride 経由で効く) と
  キューの `ChatRenderTask`。入口は Core の `RenderDanmakuFromOptionsAsync`
- danmaku 動画は InputArgs/OutputArgs が設定されていれば GUI のコーデック
  選択を尊重する (GetFfmpegProcess 流用)。未設定 (CLI) 時は従来通り
  拡張子で決まる内蔵引数を使う
- 未対応: danmaku 設定の永続化 (Settings への保存)。現状は起動ごとに既定値
- 検証: WPF プロジェクトが `-p:EnableWindowsTargeting=true` でビルド成功
  (警告/エラー 0、XAML コンパイル含む)。Core の GUI 経路はハーネスで実走し
  60秒 MP4 の生成を確認。GUI 自体の起動確認は Windows 実機が必要

## 未決事項

- 名前表示: ✅ 決定 (2026-10-23 ユーザー) — 既定は表示 ON、`--show-name false` で
  非表示に切替可能にした (ChatRenderOptions.HideUsername を追加し DrawUsername で
  ガード)。名前なしは本文のみで幅が短くなり、よりニコニコらしくなる
- 速度: `--duration` (画面横断秒数、小さいほど速い) で調整可能。既定 6 秒は現状維持で確定
- 視認性オプション (2026-10-23 追加、明るい背景でコメントが埋もれる問題への対策):
  - `--shadow`: 文字のシルエットを黒でずらして描くドロップシャドウ。自然で効果大
  - `--band 0.45`: コメント背後に半透明の黒帯 (角丸)。字幕式で最も確実に読める
  - `--outline --outline-size 5`: 既存レンダラの縁取りを流用。ニコニコ式の太い黒縁で
    コントラスト最強だが、見た目の変化も最大
  - 比較画像 `out/vis-compare.png` (明るい屋外シーンで 上から なし/影/帯/縁取り)。
    なしだと白文字が空に完全に溶ける
  - ✅ 決定 (2026-10-23 ユーザー): 3つとも既定オフのオプションとして残す
    (必要な場面で付ける運用)
- 流れる対象: 全コメントか、量が多い場合は間引きするか
- 固定コメント（ue/shita）対応は当面なし（Twitch 側に対応概念が薄い）
