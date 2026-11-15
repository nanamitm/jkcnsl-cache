# 5ch 実況コメント取得・配信 設計案

ステータス: 初版実装済み（`feature/nichan-source` ブランチ）。実装時に変えた点は末尾の「11. 実装メモ」を参照

## 1. 目的と方針

TVTComment（noriokun4649/TVTComment, MIT）が持つ「5ch 実況スレのレスをコメントとして流す」機能を、
jkcnsl-cache の上流ソースの一種として実装する。

- **取得と配信はサーバー（jkcnsl-cache）で行う。** 5ch は HTTP ポーリング型のため、
  同じチャンネルを見ているクライアントが何人いても、5ch へのアクセスは 1 本にまとめる。
- **配信は既存の避難所互換プロトコルに乗せる。** jkcnsl / NicoJK / Webクライアント / Android から
  変更なし、または URL を変えるだけで受信できるようにする。
- **書き込みはサーバー経由では行わない。** 全員がサーバーの IP で投稿することになり、
  規制されるとその IP を使う全員が書き込めなくなるため。TVTComment と同様に
  「スレをブラウザーで開く」導線に留め、必要になったらクライアント側（jkcnsl）で実装する（7章）。

## 2. ソースキーと設定

### 2.1 ソースキー

既存の `jk{id}`（公式）/ `co{id}`（非公式）/ `jk{id}r`（避難所・ローカル）に、
`jk{id}n`（5ch）を追加する。

| キー | sourceType | label |
|---|---|---|
| `jk104` | `official` | 公式 |
| `co104` | `unofficial` | 非公式 |
| `jk104r` | `refuge` / `local` | 避難所 / ローカル |
| `jk104n` | `nichan`（新規） | 5ch |

- `GetDownstreamThreadId` は `jk104n` → `jk104` を返す（`NormalizeJkThreadId` の `r` と同じ扱いに `n` を追加）。
  NicoJK は thread ID でチャンネルを判定しているため。
- NicoJK からは `refugeUri=wss://{host}/watch/{jkID}n` とすれば 5ch ソースだけを受信できる。
  `refugeMixing=1` と組み合わせると「公式 + 5ch」の混合表示になる（jkcnsl 側の混合処理をそのまま使える）。

### 2.2 appsettings.json

```jsonc
{
  "CacheServer": {
    "Channels": {
      "jk1n":   "5ch:",                    // 自動選択（既定の板・キーワード対応表を使う）
      "jk141n": "5ch:",
      "jk9n":   "5ch:https://fate.5ch.io/test/read.cgi/livemx/1731600000/"  // スレ固定
    },
    "Nichan": {
      "Enabled": false,                    // 明示的に有効化しない限り動かさない（8章）
      "UserAgent": "",                     // 空なら既定値。連絡先を含む UA を推奨
      "SubjectIntervalSeconds": 60,        // スレ一覧（subject.txt）の取得間隔。下限 30
      "DatIntervalSeconds": 10,            // dat 差分取得の間隔。下限 5
      "MaxThreadsPerChannel": 2,           // 同時に追うスレ数（現スレ + 次スレ）
      "HostMinIntervalMilliseconds": 1000, // 同一ホストへの最小リクエスト間隔（全チャンネル共通）
      "MaxConcurrentRequests": 2,
      "InitialBacklogSeconds": 30,         // 初回取得時に流す過去レスの範囲
      "PlaybackDelaySeconds": 15,          // 書き込み時刻 + この秒数で配信する（4.3）
      "MaxCommentLength": 120,
      "MaxLines": 4,                       // これを超える行数のレス（AA 等）は捨てる
      "StoreComments": false,              // CommentStorageService へ保存するか
      "Boards": {
        // 既定の対応表（3.1）を上書き・追加する
        "jk141": [
          { "Board": "https://tanuki.5ch.io/livebs2/", "Keywords": ["BS日テレ"] }
        ]
      }
    }
  }
}
```

`"5ch:"` の後ろが空なら自動選択、`read.cgi` 形式の URL ならスレ固定とする。

## 3. スレ選択

### 3.1 板・キーワード対応表

TVTComment の `Data/2chthreads.txt`（`[boards]` と `[threadmapping]`）を元に、
`ChannelList.cs` の jk ID をキーとした既定表をコードに持つ（`NichanBoardCatalog.cs`）。

- TVTComment の対応表は NID/SID/TSID フラグがキーだが、jkcnsl-cache は jk ID がキーなので変換して持つ。
- 1 チャンネルに複数の板を割り当て可能（例: TOKYO MX → `livemx` / `weekly`「MX」/ `liveanime`「TOKYO MX」）。
- ホストは `5ch.io` に正規化する（TVTComment の `ThreadAddress.Normalize` と同じ）。
- `Nichan:Boards` で上書きできる。`ExtraChannels` で追加したチャンネルにもここで割り当てられる。

### 3.2 選択アルゴリズム

1. 板ごとに `subject.txt` を取得する（Shift_JIS）。**板単位で共有キャッシュ**し、
   `livebs2` のように多数の BS チャンネルが同じ板を使う場合でも 1 回の取得で済ませる。
2. タイトルを NFKC 正規化・小文字化し、キーワードのいずれかを含むスレに絞る（キーワードが空なら全スレ）。
3. レス数 1000 以上・告知スレ（スレ番号 `924` 始まりの 10 桁）を除外する。
4. 「スレ立て時刻（= スレ番号の UNIX 時刻）が新しい順」に `MaxThreadsPerChannel` 本選ぶ。
   TVTComment はレス数順だが、実況では「今伸びている最新スレ + 次スレ」を取るほうが取りこぼしが少ない。
5. 404 なら `bbsmenu.html` で板移転を確認して 1 回だけ再試行する（TVTComment の `ResolveMovedBoardAsync`）。

将来の拡張として、`ProgramInfoService` の現在番組タイトルで
番組名入りのスレ（アニメ実況板など）を優先する選択を追加できる。初版では行わない。

## 4. 取得と変換

### 4.1 dat 差分取得

TVTComment の `DatThreadLoader` と同じ方式で取得する。

- `GET https://{server}.5ch.io/{board}/dat/{key}.dat`
- 2 回目以降は `Range: bytes={既取得長-1}-` と `If-Modified-Since`。
  先頭 1 バイトが `\n` であることで差分の整合性を確認し、合わなければ全体を取り直す。
- `304` は更新なし、`416` は全体を取り直す、`404` / `410` はスレ落ちとして次の選択を待つ。
- `429` / `5xx` は `Retry-After` に従う。`401` / `403` はホスト単位で長めに止める（既存の 403 → 1 時間待機の考え方に合わせる）。

> **実装前の確認事項:** 5ch の dat 直接取得は時期によって制限されてきた。TVTComment の現行版が
> `5ch.io` への直接取得で動いていることを前提にするが、実装開始時に実際のレスポンスを確認する。
> 使えない場合は `read.cgi` の HTML を解析する方式（TVTComment の `ThreadParser`）を代わりに使う。

### 4.2 レス → chat JSON

既存の避難所・ローカルストリームと同じ形の `chat` を生成する。

| chat のフィールド | 値 |
|---|---|
| `thread` | `GetDownstreamThreadId`（`jk104` など） |
| `no` | チャンネル内の通し番号（サーバー採番）。スレをまたいでも単調増加させる |
| `vpos` | `(書き込み時刻 - VposBaseTime)` を 10ms 単位で |
| `date` / `date_usec` | 書き込み時刻（JST として解析）。`date_usec` は 0 |
| `user_id` | `5ch:` + ID 欄（ID がない板では `5ch:{スレ番号}-{レス番号}`） |
| `anonymity` | 1 |
| `mail` | 空（5ch のメール欄の `sage` 等は渡さない） |
| `content` | 本文を整形したもの（下記） |

本文の整形:

- `<br>` は半角スペースにする（ニコニコのコメントは 1 行のため）。
- HTML タグを除去し、文字参照をデコードする。アンカー（`>>123`）は文字列として残す。
- `MaxLines` を超える行数のレス（AA など）は捨てる。
- `MaxCommentLength` を超えたら切り詰めて `…` を付ける。
- 空になったレス、1 レス目（スレ立てテンプレ）は流さない。

### 4.3 配信タイミング

ポーリング間隔ごとに数十件がまとめて届くため、そのまま流すと画面上でコメントが固まって流れる。
各レスを「書き込み時刻 + `PlaybackDelaySeconds`」の時刻に配信するキューを持ち、
書き込み時の間隔を保って流す（TVTComment がクライアント側で行っている遅延補正をサーバーで行う）。

- `PlaybackDelaySeconds` は `DatIntervalSeconds` + 余裕分を既定とする。0 なら届いた時点ですぐ流す。
- 配信時刻をすでに過ぎたレスは即時配信する。`InitialBacklogSeconds` より古いレスは流さない。

### 4.4 履歴

`LocalStreamChannel` と同様、新規接続時に直近のコメントを送る（`HistoryLimit` / `HistorySeconds` を流用）。
ポーリング型なので、接続直後に何も流れない時間を減らせる。

## 5. コンポーネント構成

新規ファイル（`jkcnsl-cache/Nichan/` 配下を想定）:

| ファイル | 役割 |
|---|---|
| `NichanHttpClient.cs` | 5ch 用の `HttpClient`、ホスト単位の最小間隔・同時数制限、`Retry-After` 処理 |
| `NichanBoardService.cs` | `subject.txt` の板単位共有キャッシュ、`bbsmenu.html` による板移転解決（DI シングルトン） |
| `NichanBoardCatalog.cs` | jk ID → 板・キーワードの既定表と `Nichan:Boards` の読み込み |
| `NichanDatLoader.cs` | dat の差分取得 |
| `NichanDatParser.cs` | dat 行（`名前<>メール<>日付 ID<>本文<>タイトル`）の解析 |
| `NichanUpstreamChannel.cs` | `UpstreamChannelBase` の派生。スレ選択・ポーリング・変換・遅延配信 |

`NichanUpstreamChannel` の要点:

- `ConnectAndReceiveAsync` の中でポーリングループを回す（既存の再接続・バックオフの仕組みに乗る）。
- `Status` / `StatusText`: `running` / 「{スレタイ} ({レス数})」。対象スレがないときは `noThread` /「対応スレがありません」。
- `CurrentTarget`: 追っているスレの `read.cgi` URL。
- `VposBaseTime`: ループ開始時刻を返す。
- `PostCommentAsync`: `{"type":"error","data":{"code":"NOT_SUPPORTED"}}` を返す。
- クライアントが 0 人になったら停止する（既存の通常チャンネルと同じ）。監視対象（`IsMonitored`）にはしない。

既存ファイルの変更:

| ファイル | 変更内容 |
|---|---|
| `ChannelManager.cs` | `GetOrCreateChannel` に `5ch:` の分岐、`GetChannelFullStats` の種別に「5ch」 |
| `UpstreamChannelBase.cs` | `NormalizeJkThreadId` で末尾 `n` も外す |
| `ChannelsStreamPayloads.cs` | `nichan` の sourceType・ラベル。`commentable=false`、`requiresAuth=false` |
| `Program.cs` | `/api/status` などの `sources` に `jk{id}n` を追加、DI 登録 |
| `CommentStorageService` 呼び出し | `StoreComments=false` のとき 5ch のコメントを保存しない |
| `StatusPage.cs` | 5ch ソースの状態表示（追跡中のスレ、取得エラー） |
| `client/`（Webクライアント） | ソース選択に「5ch」を追加。投稿欄の代わりに「スレを開く」リンク |
| `readme.txt` / `doc/client-api.md` | 設定と sourceType の追記 |

## 6. 負荷とアクセス制御

- 同一ホストへのリクエストは全チャンネル合計で `HostMinIntervalMilliseconds` 以上空ける。
  地上波・BS の全チャンネルを有効にしても、板は十数個に集約されるため subject.txt の取得は少ない。
  dat は「視聴者がいるチャンネル × `MaxThreadsPerChannel`」本だけ取得する。
- `MetricsService` に 5ch のリクエスト数・エラー数・429 の件数を追加し、ステータスページに出す。
- 起動時に一斉に始めないよう、各チャンネルの初回取得に揺らぎ（ジッター）を入れる。

## 7. 書き込み（将来）

サーバーからは書き込まない。対応する場合の候補:

1. **Webクライアント / Android:** スレの URL を開くリンクを出すだけ（初版）。
2. **jkcnsl:** 5ch 用の投稿コマンドを追加し、利用者自身の回線と Cookie で `bbs.cgi` に投稿する。
   `/watch` の `postComment` を 5ch ソースのときだけ jkcnsl 側で横取りする形になる。

## 8. 公開時の注意

- 5ch の書き込みを第三者に再配信することになるため、`Nichan:Enabled` は既定で `false` とする。
- wss モードでインターネットに公開しているサーバーでは、LAN 内や認証済みのクライアントだけに配信する
  オプションを検討する（初版では `Enabled` のみ）。
- コメントの保存・エクスポート（`comment-export-api`）には既定で含めない（`StoreComments=false`）。
- TVTComment のコードを移植する箇所には、出典と MIT ライセンス表記をファイル先頭に残す。

## 9. 実装ステップ

1. dat 直接取得が現在も使えるかを確認する（4.1 の確認事項）。
2. `NichanHttpClient` / `NichanDatLoader` / `NichanDatParser` と単体テスト（dat のサンプルを使う）。
3. スレ固定（`5ch:{URL}`）の `NichanUpstreamChannel` を作り、jkcnsl の `R` コマンドと NicoJK で受信を確認する。
4. `NichanBoardService` / `NichanBoardCatalog` による自動選択。
5. 遅延配信（4.3）と履歴（4.4）。
6. ステータス API・ステータスページ・Webクライアントの対応、ドキュメント更新。

## 10. 決めておきたいこと

- ソースキーの接尾辞は `n` でよいか。
- 公式や避難所と**サーバー側で混ぜた**ソース（例: `jk104m` = 避難所 + 5ch）も必要か。
  NicoJK の `refugeMixing` は 2 系統までなので、「公式 + 避難所 + 5ch」を見たい場合はサーバー側での混合が必要になる。
- `PlaybackDelaySeconds` の既定値（遅延を入れて滑らかに流すか、すぐ流すか）。

## 11. 実装メモ（初版で設計から変えた点）

- 5ch の dat 直接取得（`Range` 差分、206 応答）は 2026-11-15 時点で使えることを確認した。
- スレ選択は「新しい順」ではなく「勢い（レス数 / 経過時間）順」にした。番組ch板（livenhk など）は
  番組ごとのスレと「常に実況し続けるスレ」が並ぶため、新しい順だと過疎スレを拾いやすい。
  `MaxThreadsPerChannel` の既定は 3。
- 選択から外れたスレも、取りこぼしを防ぐため dat を 1 回取得してから外す。
- 5ch ソースは `ChannelMonitorService` の常時監視の対象外。コメントクライアントが 0 人になると
  既存チャンネルと同じく停止し、/watch だけが残っている場合も 2 分で取得を休止する。
- 新規接続時の履歴送信のため、`UpstreamChannelBase` に `GetInitialMessages()` を追加した。
- Webクライアントは、未設定の 5ch ソースの空き行を表示しない（既存のソース枠は従来どおり）。
- ステータスページに「5ch」タブ（`/api/admin/nichan`）を追加した。集計は `NichanStats`（`NichanHttpClient` が記録）で、
  `MetricsService` とは独立に 5 秒枠 × 180 個を保持する。
- 番組名を使ったスレ選択を追加した（`NichanProgramMatcher`）。`ProgramInfoService.GetProgram` の現在番組から
  照合キーを作り、一致するスレのうち最も勢いのある 1 本を必ず選ぶ（残りは従来どおり勢い順）。番組の切り替わりで
  即座に選び直し、前の番組のキーも 5 分間は有効。アニメ実況板（liveanime）は `ProgramOnly` の板として
  地上波・主な BS チャンネルに割り当て、番組がアニメ（ジャンル不明を含む）のときだけ取得する。
  ステータスページの「5ch」タブに現在の番組・照合キー・[番組一致] を表示する。
- 未対応: 
  サーバー側での混合ソース、jkcnsl からの書き込み。
