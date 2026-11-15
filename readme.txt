jkcnsl-cache

■概要
jkcnsl-cache は jkcnsl のマルチユーザー対応キャッシュサーバーです。
複数のクライアントが同じチャンネルを視聴するとき、上流サーバー（ニコニコ実況または
NX-Jikkyo などの避難所）への接続をチャンネルごとに1本に集約し、コメントを各クライアント
に配信します。

クライアントとの通信は避難所互換の WebSocket プロトコルを使用するため、jkcnsl の R コマンド
や cache_server_url を設定した L コマンドでそのまま接続できます。
NicoJK から使う場合も NicoJK.ini の refugeUri を変更するだけで対応できます。

また、Webブラウザから直接利用できる「Webクライアント」が内蔵されており、
チャンネルを選択してコメントオーバーレイを表示したり、ニコニコアカウントでログインして
コメントを投稿したりできます。

■上流ソースの種類

・ニコニコ実況 公式チャンネル（nicovideo:ch??????）
  appsettings.json のチャンネル定義を "nicovideo:{channelID}" と記述します。

・ニコニコ実況 非公式配信（nicovideo: ※channelID なし）
  チャンネルキーを "co{数字}" とし、値を "nicovideo:" と記述します。
  起動時から NoStreamCheckIntervalMinutes 間隔で「【ニコニコ実況】」をキーワードに
  ニコニコ生放送を一括検索し、該当チャンネルの非公式配信を自動検出して接続します。
  キー名は ChannelList に定義されたチャンネルの ID に対応します（例: co141 = BS日テレ）。

・NX-Jikkyo などの避難所（wss://）
  appsettings.json のチャンネル定義を避難所の WebSocket URL と記述します。

■開発サーバー（ローカル）の起動方法

バックエンドとフロントエンドをそれぞれ別のターミナルで起動します。

【ターミナル1: バックエンド】
> cd jkcnsl-cache
> dotnet run

ポート 5000（メイン）と 5001（ステータス管理）でリッスンします。

【ターミナル2: フロントエンド dev server】
> cd jkcnsl-cache/client
> npm install   ← 初回のみ
> npm run dev

Vite dev server が http://localhost:5173 で起動します。
/api・/comment・/watch へのリクエストはバックエンド（5000番）へ自動的にプロキシされます。

ブラウザで http://localhost:5173 を開くと Webクライアントが表示されます。
HMR（ホットリロード）が有効なため、フロントエンドのソース変更は即座に反映されます。

本番ビルドは以下で実行します（wwwroot/ に出力）:
> cd jkcnsl-cache/client && npm run build

■ビルド方法（Ubuntu 24.04 の例）
> curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo bash -
> sudo apt install dotnet-sdk-8.0 nodejs
> cd jkcnsl-cache/client && npm ci && npm run build && cd ..
> dotnet publish jkcnsl-cache/jkcnsl-cache.csproj -c Release -r linux-x64 \
>     --self-contained true /p:PublishSingleFile=true

フロントエンドのビルドを先に行い wwwroot/ を生成してから dotnet publish することで、
静的ファイルがまとめて publish 出力に含まれます。

■デプロイ
deploy/ フォルダのファイルを使ってセットアップできます。

  jkcnsl-cache.service  systemd サービス定義
  nginx.conf            nginx のリバースプロキシ設定
  setup.sh              Ubuntu 24.04 向け自動セットアップスクリプト
  update.sh             アップデートスクリプト
  uninstall.sh          アンインストールスクリプト

【ws モード（LAN 向け・nginx 不要）】
  > sudo bash deploy/setup.sh --mode ws [--port 5000] [--status-port 5001]

【wss モード（インターネット公開・nginx + Let's Encrypt）】
  > sudo bash deploy/setup.sh --mode wss --domain your-domain.example.com [--email admin@example.com]

手動でセットアップする場合（wss モード）:
1. フロントエンドをビルドして wwwroot/ を生成する
2. dotnet publish でビルドし /opt/jkcnsl-cache/ に配置する
3. appsettings.json の PublicBaseUrl とチャンネルを設定する
4. deploy/jkcnsl-cache.service を /etc/systemd/system/ にコピーして有効化する
5. deploy/nginx.conf を /etc/nginx/sites-available/ にコピーして有効化する
6. certbot で TLS 証明書を取得する

■appsettings.json の設定

{
  "CacheServer": {
    "BindAddress": "0.0.0.0",
    "MainPort": 5000,        ← Webクライアント・WebSocket・/api/status など
    "StatusPort": 5001,      ← 管理ページ（/）・ログSSE（/api/logs）専用ポート
    "PublicBaseUrl": "ws://{サーバーIP}:5000",
    "NoStreamCheckIntervalMinutes": 30,
    "WatchKeepIntervalSeconds": 30,
    "WatchIdleTimeoutSeconds": 90,
    "BroadcastTimeZone": "Asia/Tokyo",
    "ProgramInfoUpdateIntervalSeconds": 3600,
    "ProgramInfoFailureRetrySeconds": 1800,
    "ProgramInfoEvaluationIntervalSeconds": 60,
    "LocalStream": {
      "HistoryLimit": 20,
      "HistorySeconds": 3,
      "MaxClients": 100,
      "MaxTotalClients": 500,
      "QueueLimit": 100,
      "DropDisconnectStreak": 5,
      "DropStreakResetSeconds": 10,
      "PostIntervalMilliseconds": 1000,
      "MaxCommentLength": 75
    },
    "NhkProgramApi": {
      "API_Key": "",
      "Area": "130",
      "UpdateIntervalSeconds": 43200,
      "FailureRetrySeconds": 1800
    },
    "AtxProgram": {
      "Enabled": true,
      "Url": "https://www.at-x.com/program",
      "ApiUrl": "https://api.atx.01core.app/api/schedules?id",
      "UpdateIntervalSeconds": 86400,
      "FailureRetrySeconds": 1800
    },
    "OujProgram": {
      "Enabled": true,
      "Url": "https://bangumi.ouj.ac.jp/v4/bslife/oujapi.php",
      "UpdateIntervalSeconds": 43200,
      "FailureRetrySeconds": 1800
    },
    "ExtraChannels": [],
    "ChannelOverrides": {},
    "TVerProgramMap": {},
    "Channels": {

      "co141": "nicovideo:",   ← 非公式配信（起動時から定期検索・監視）
      "co161": "nicovideo:",   ← キーは co{チャンネルID} の形式

      "jk1":   "nicovideo:ch2646436",   ← 公式ニコニコ実況チャンネル
      "jk101": "nicovideo:ch2647992",
      "jk104": "localstream:",           ← ログインなしのローカルコメントストリーム
      "jk141": "wss://nx-jikkyo.tsukumijima.net/api/v1/channels/jk141/ws/watch",
      "jk333": "wss://nx-jikkyo.tsukumijima.net/api/v1/channels/jk333/ws/watch",

      "ch2646436": "nicovideo:ch2646436"  ← refugeMixing 用エイリアス
    }
  }
}

MainPort (5000): Webクライアント・WebSocket・/api/status・/api/channels を公開。
StatusPort (5001): 管理用ステータスページ（/）とログSSE（/api/logs）を公開。
  ※ 同じポートにする場合は MainPort = StatusPort と設定する。

BroadcastTimeZone: 非公式配信の予定時刻表示に使うタイムゾーン（省略時はシステム設定）。

WatchKeepIntervalSeconds / WatchIdleTimeoutSeconds: /watch 接続の生存確認に使います。
  サーバーは seat.keepIntervalSec として WatchKeepIntervalSeconds を返し、クライアントは
  keepSeat を定期送信します。WatchIdleTimeoutSeconds を超えて無通信の場合は切断します。

ProgramInfoUpdateIntervalSeconds: TVer を含む番組表全体の更新間隔です。
  省略時は 1200 秒です。設定ミスによる過剰アクセスを防ぐため、60 秒未満を指定しても最低 60 秒として扱います。
  通常運用では 3600 秒以上を推奨します。

ProgramInfoFailureRetrySeconds: TVer 取得失敗などで番組表全体の更新に失敗した場合の再試行間隔です。
  省略時は 1800 秒です。設定ミスによる過剰アクセスを防ぐため、300 秒未満を指定しても最低 300 秒として扱います。

ProgramInfoEvaluationIntervalSeconds: 取得済み番組表から現在番組を再評価する間隔です。
  外部サイトへのアクセスは行いません。

LocalStream: "localstream:" と設定したチャンネルで使う内蔵コメントストリームです。
  NicoJK / jkcnsl からは通常の避難所と同じように /watch/{jkID} / /comment/{jkID} へ接続します。
  ログイン機能はなく、投稿は匿名コメントとして扱います。
  公式・非公式・避難所の上流接続が失敗している間は、一時的に同じ /watch / /comment 上で
  ローカル待避ストリームとして動作します。この間はログインなしで投稿できます。
  上流接続が回復すると自動的に通常の中継へ戻ります。待避中の投稿は上流へ後送しません。
  MaxCommentLength は本文最大文字数です。ニコニコ公式互換として省略時は 75 文字です。
  HistoryLimit は新規接続時に送る直近コメント数です。履歴はメモリ上のみで、再起動時に消えます。
  HistorySeconds は履歴を保持する秒数です。0 を指定すると過去コメントを保持しません。
  MaxClients はコメント受信クライアント数の上限です。0 以下を指定すると無制限です。
  上限に達した場合、新規 /comment 接続は PolicyViolation で切断されます。
  MaxTotalClients は全 localstream チャンネル合計のコメント受信クライアント数上限です。
  0 以下を指定すると無制限です。上限に達した場合、新規 /comment 接続は PolicyViolation で切断されます。
  QueueLimit はクライアントごとの送信キュー上限です。上限を超えた場合は古いキューを破棄し、
  最新コメントへ追いつくことを優先します。破棄が DropDisconnectStreak 回連続した遅いクライアントは
  切断します。DropStreakResetSeconds 秒間正常に送信できれば破棄連続数をリセットします。
  PostIntervalMilliseconds は1接続あたりの最小投稿間隔です。

NhkProgramApi: NHK BSプレミアム4K（jk103）と NHK BS8K（jk104）の番組情報取得に使います。
  API_Key には NHK 番組APIで発行されたキーを設定します。未設定の場合、NHK 番組APIへの
  アクセスは行わず、jk103 / jk104 の番組タイトルは表示されません。
  Area は地域コードです。東京の場合は "130" を指定します。
  UpdateIntervalSeconds は NHK 番組APIへの取得間隔です。省略時は 43200 秒（12時間）です。
  設定ミスによる過剰アクセスを防ぐため、3600 秒未満を指定しても最低 3600 秒として扱います。
  FailureRetrySeconds は取得失敗時の再試行間隔です。省略時は 1800 秒（30分）です。
  一部サービスだけ取得に失敗した場合でも、同じ放送日の既存キャッシュがあれば保持します。
  jk103 と jk104 を取得するため、1回の更新で NHK 番組APIを2回呼び出します。
  APIキーを appsettings.json に保存したくない場合は、環境変数
  CacheServer__NhkProgramApi__API_Key でも設定できます。
  ローカル開発では Git 管理外の jkcnsl-cache/local/appsettings.json に保存することもできます。
  このファイルは appsettings.json / appsettings.Development.json の後に読み込まれるため、
  API_Key だけを以下のように上書きできます。

  {
    "CacheServer": {
      "NhkProgramApi": {
        "API_Key": "your-nhk-program-api-key"
      }
    }
  }

  サンプルは doc/local.appsettings.example.json を参照してください。

  NHK 番組APIの情報を他のデータと併用して表示・配信する場合、NHK 番組APIの利用規約に従い
  「ＮＨＫ番組の情報提供:ＮＨＫ」のクレジットを表示してください。

AtxProgram: AT-X（jk333）の番組情報取得に使います。
  Enabled が true の場合、ApiUrl の AT-X 公式番組表APIを取得し、jk333 の番組タイトルとして使います。
  ApiUrl で0件または取得失敗になった場合は、互換用に Url の公式週間番組表HTML解析へフォールバックします。
  UpdateIntervalSeconds は取得間隔です。省略時は 86400 秒（24時間）です。
  設定ミスによる過剰アクセスを防ぐため、21600 秒未満を指定しても最低 21600 秒として扱います。
  FailureRetrySeconds は取得失敗時の再試行間隔です。省略時は 1800 秒（30分）です。
  取得に失敗した場合でも、同じ放送日の既存キャッシュがあれば保持します。
  取得済み番組表は内部でキャッシュし、現在番組の切り替えは ProgramInfoEvaluationIntervalSeconds 間隔で評価します。

OujProgram: 放送大学ラジオ（jk531）の番組情報取得に使います。
  Enabled が true の場合、Url の放送大学公式 番組情報取得APIから BS531 の番組表を取得します。
  UpdateIntervalSeconds は取得間隔です。省略時は 43200 秒（12時間）です。
  FailureRetrySeconds は取得失敗時の再試行間隔です。省略時は 1800 秒（30分）です。
  放送大学APIはXMLを返すため、内部でXMLを解析します。ジャンルは BS531 固定で「その他」(0xF) として扱います。

ExtraChannels: ChannelList.cs にないチャンネルを appsettings.json だけで追加します。
  追加したチャンネルは Webクライアント、/api/status、/api/channels、/api/channels/ws の対象になります。

  "ExtraChannels": [
    { "Id": 999, "Name": "追加チャンネル", "Video": "jk999", "Bs": true }
  ]

ChannelOverrides: ChannelList.cs にある標準チャンネルの表示情報を上書きします。

  "ChannelOverrides": {
    "jk234": { "Name": "グリーンチャンネル" }
  }

TVerProgramMap: 番組タイトル取得で使う TVer EPG の対応表を追加・上書きします。
  標準で主要な地上波・BSチャンネルは登録済みですが、TVer 側の broadcasterId が変わった場合や、
  ExtraChannels で追加したチャンネルに番組タイトルを出したい場合に設定します。

  "TVerProgramMap": {
    "jk999": { "Type": "bs", "Area": 23, "BroadcasterId": 999 }
  }

  現在 TVer の BS 番組表にある標準登録済みの主な追加BSチャンネル:
  放送大学テレビ(jk231/jk232), グリーンチャンネル(jk234), J SPORTS 1-4(jk242-jk245),
  BS釣りビジョン(jk251), 日本映画専門ch(jk255), ディズニーch(jk256)。
  放送大学ラジオ(jk531) は TVer ではなく OujProgram で取得します。

co{id} チャンネル: 起動時から定期的にニコニコ生放送を検索し、チャンネル名（"BS日テレ" 等）
  に一致する非公式配信を自動検出します。対応 ID は ChannelList.cs を参照。
  スクレイピングに失敗した lv は次の検索バッチまで待機し、連続リトライを防止します。

jk{id}n チャンネル（5ch）: 5ch 実況スレのレスをコメントとして配信します。
  同梱の appsettings.json では有効になっており、地上波（jk1n〜jk9n）、NHK BS（jk101n / jk103n）、
  無料 BS（jk141n など 10 局）、AT-X（jk333n）を設定済みです。
  使わない場合は CacheServer:Nichan:Enabled を false にします（Nichan セクションがない場合も無効です）。
  チャンネルを追加・変更するには、Enabled が true の状態で、Channels に
  "5ch:"（板とキーワードからスレを自動選択）または "5ch:{スレのURL}"（スレ固定）を設定します。

  "Channels": {
    "jk1n":   "5ch:",
    "jk141n": "5ch:",
    "jk9n":   "5ch:https://fate.5ch.io/test/read.cgi/livemx/1794700000/"
  },
  "Nichan": {
    "Enabled": true
  }

  ・コメントクライアントが接続している間だけ取得します（起動時からの常時取得はしません）。
    /watch だけが接続されている状態が2分続いた場合も取得を休止します。
  ・板とキーワードの既定の対応表は Nichan/NichanBoardCatalog.cs にあります。
    Nichan:Boards で上書き・追加できます（Excludes は一致しても除外する語）。
      "Boards": { "jk141": [ { "Board": "https://tanuki.5ch.io/livebs2/", "Keywords": ["BS日テレ"] } ] }
  ・スレは「勢い（1時間あたりのレス数）」の高い順に MaxThreadsPerChannel 本（既定3）を追跡します。
    番組表（TVer など）で現在の番組がわかるチャンネルでは、番組名に一致するスレのうち最も勢いのある
    1 本を必ず含めます（「[字]」「＃6」「（49-54）」などを除いた主タイトルで照合。『』で囲まれた作品名があれば
    その中身でも照合します。番組表で番組名と副題が
    つながっている場合に備え、スレタイの先頭の語が番組名の先頭にあるときも一致とします）。番組が切り替わると
    次の一覧更新を待たずに選び直し、前の番組のスレも 5 分間は一致扱いにします。
  ・アニメ・特撮実況板（liveanime）は作品ごとにスレが立つため、番組がアニメ（またはジャンル不明）の
    ときだけ見て、番組名に一致するスレだけを対象にします。Boards で独自の板にこの扱いをさせる場合は
    "ProgramOnly": true を指定します。
    レス数が1000に達したスレや dat 落ちしたスレは外れ、次の一覧更新で次スレに移ります。
  ・レスは「書き込み時刻 + PlaybackDelaySeconds」に配信します（既定は DatIntervalSeconds + 5 秒）。
    ポーリングでまとめて届いたレスを書き込み時の間隔で流すためです。0 にすると届いた時点ですぐ流します。
  ・勢いは、コメントを受信しているクライアントがいる間は配信したレス数（直近 1 分）です。
    誰も受信していない 5ch ソースは dat を取得しないため、subject.txt のレス数の増え方（直近 10 分までの
    平均。1 分あたりに換算）を勢いとして表示します。このため 5ch ソースを設定した板の subject.txt は、
    受信者がいなくても SubjectIntervalSeconds ごとに取得します（板ごとに 1 回。dat は取得しません）。
    起動後 2 分ほどは観測期間が足りず 0 になります。BackgroundMomentum を false にすると取得をやめ、
    受信者がいない 5ch ソースの勢いは 0 になります。
  ・5ch への書き込みはできません（サーバー経由で投稿すると全員がサーバーの IP で書き込むことになるため）。
    Webクライアントではスレタイがスレへのリンクになります。
  ・Webクライアントでは、追跡中のスレが 2 本以上あるとコメント欄の上にスレの一覧が出て、
    チェックを外したスレのコメントを表示しないようにできます（取得はサーバー側で続きます）。
  ・5ch のレスは既定ではコメント保存・エクスポートの対象にしません（StoreComments）。
  ・インターネットに公開しているサーバーで有効にすると、5ch の書き込みを第三者に再配信することになります。
    同梱の設定では有効になっているので、有効のまま公開するかどうかは運用者の判断で決め、
    再配信しない場合は Enabled を false にしてください。
  ・NicoJK からは refugeUri=wss://{サーバー}/watch/{jkID}n で 5ch だけを受信できます。
    refugeMixing=1 と組み合わせると「公式 + 5ch」の混合表示になります。
    避難所と 5ch の両方を受けたい場合は、下の統合ソース（{jkID}m）を使います。

jk{id}m チャンネル（統合ソース: 避難所 + 5ch）: 避難所（jk{id}r）と 5ch（jk{id}n）のコメントを 1 本にまとめて配信します。
  NicoJK のように避難所を 1 つしか設定できないクライアントでも、両方のコメントを受け取れます。
  ・Channels への設定は不要です。jk{id}r が WebSocket の避難所（ws:// / wss://）で、jk{id}n が 5ch ソースのとき
    自動で使えます（CacheServer:MergedSource:Enabled を false にすると無効になります）。
  ・公式（jk{id}）は含みません。クライアントが公式を別に受けている前提で、混ぜると二重に流れるためです。
  ・上流には新たに接続せず、サーバー内の jk{id}r / jk{id}n を購読します（5ch の取得回数は増えません）。
  ・2 つのソースの no が重ならないよう no を振り直し、vpos は統合ソースの vposBaseTime 基準に直します。
    thread はそのまま（避難所が転送してきたコメントの thread も変えないので、jkcnsl の転送コメント判定はそのまま効きます）。
  ・各コメントに "source":"refuge" / "nichan" を付けます（避難所互換クライアントは無視します）。
  ・投稿は避難所へ転送します（5ch へは書き込めません）。
  ・勢いリストのソース一覧には出しません。視聴者数は避難所側に数えます。
  ・NicoJK: refugeUri=wss://{サーバー}/watch/{jkID}m と refugeMixing=1 で「公式 + 避難所 + 5ch」になります。

  Nichan の設定項目（括弧内は既定値）:
    Enabled (false。同梱の appsettings.json では true), UserAgent ("Monazilla/1.00 (jkcnsl-cache)"),
    SubjectIntervalSeconds (60, 下限30), DatIntervalSeconds (10, 下限5),
    MaxThreadsPerChannel (3, 1〜5), HostMinIntervalMilliseconds (1000, 下限200),
    MaxConcurrentRequests (2), InitialBacklogSeconds (30), PlaybackDelaySeconds,
    MaxCommentLength (120), MaxLines (4, これを超える行数のレスは AA とみなして捨てる),
    StoreComments (false), HistoryLimit (20), HistorySeconds (30), BlockedWaitMinutes (60),
    BackgroundMomentum (true)
  5ch から 401/403 が返ったホストへは BlockedWaitMinutes の間アクセスしません。
  429/503 の場合は Retry-After に従います。
  取得状況はステータスページ（StatusPort）の「5ch」タブで確認できます。リクエスト数・304率・エラー・
  429/503 の推移、ホストごとの集計と停止状態、ソースごとに追跡中のスレ、取得した板を表示します
  （JSON は /api/admin/nichan。集計はメモリ上のみで、再起動すると消えます）。

■Webクライアントの使い方

ブラウザで http://{サーバーIP}:{MainPort} を開くと内蔵 Webクライアントが表示されます。

【コメント表示】
・左のチャンネルリストからチャンネルを選択すると接続し、コメントがオーバーレイ表示されます。
・コメントログ（下部パネル）にも時刻付きで一覧表示されます。
・設定（⚙ボタン）から透過率・文字サイズ・流れる速さを調整できます。設定は保存されます。
・NG ボタンでユーザーを非表示にできます。NGリストは設定パネルから解除できます。

【コメント投稿】
・設定パネルの「ニコニコアカウント」欄からメールアドレス・パスワードでログインします。
・2段階認証（OTP）に対応しています。「この端末を信頼する」をONにすると次回はスキップ。
・ログイン後、チャンネル選択時にコメント投稿エリアが表示されます。
・コマンド欄に "red ue big" などのメールコマンドを入力できます。
・184ボタンで匿名（184）の ON/OFF を切り替えられます。
・自分の投稿コメントはオーバーレイに黄色い枠で表示されます。

【チャンネル接続状態】
・チャンネルが未接続・切断中のとき、ステージ中央に接続状態を表示します。
・非公式配信が予定されている場合、開始時刻とカウントダウンを表示します。

■クライアント側の設定（jkcnsl / NicoJK）

【jkcnsl から使う場合（L コマンド経由）】
> Scache_server_url ws[s]://{サーバーIP}:{ポート}
> Ljk1     ← キャッシュサーバー経由でコメントを取得

【jkcnsl から使う場合（R コマンド経由）】
> R1 ws[s]://{サーバーIP}:{ポート}/watch/jk1

【NicoJK から使う場合（避難所として）】
NicoJK.ini:
  refugeUri=ws[s]://{サーバーIP}:{ポート}/watch/{jkID}
  （避難所と 5ch をまとめて受けるなら /watch/{jkID}m。上の「jk{id}m チャンネル」を参照）

refugeMixing=1 の場合は ch{ID} 形式のエイリアスも Channels に登録してください。

■勢いリスト API
GET /api/channels を呼び出すと NX-Jikkyo の getchannels 互換 XML を返します（2秒キャッシュ）。
NicoJK の以下の URL をキャッシュサーバーのアドレスに変更することで自前データを提供できます。
  変更前: channelsUri=
  変更後:channelsUri=http[s]://{サーバーIP}:{MainPort}/api/channels

■勢いリスト WebSocket API
WS /api/channels/ws に接続すると、接続直後に全チャンネルの snapshot を返し、その後
CacheServer:ChannelsStreamIntervalSeconds 間隔（省略時 2 秒）で全チャンネルの勢い情報を
stats としてまとめてプッシュします。番組タイトルは TVer EPG から取得したキャッシュを
program フィールドとして返し、NHK BSプレミアム4K（jk103）と NHK BS8K（jk104）は
NhkProgramApi:API_Key 設定時に NHK 番組APIから取得します。AT-X（jk333）は AT-X公式番組表、
放送大学ラジオ（jk531）は放送大学公式 番組情報取得APIから取得します。現在番組が変わったときは
programs をプッシュします。
詳細は doc/channels-stream-api.md を参照。

■エンドポイント一覧

  GET  /                   Webクライアント（MainPort）/ 管理ステータスページ（StatusPort）
  WS   /watch/{channel}    視聴・投稿セッション（MainPort のみ）
  WS   /comment/{channel}  コメント受信セッション（MainPort のみ）
  GET  /api/status         チャンネル状態 JSON（両ポート、2秒キャッシュ）
  GET  /api/channels       勢いリスト XML・getchannels 互換（両ポート、2秒キャッシュ）
  WS   /api/channels/ws    勢いリスト WebSocket（両ポート、設定間隔で全チャンネル push）
  GET  /api/logs           ログ SSE ストリーム（StatusPort のみ）
  POST /api/login          ニコニコログイン（メール+パスワード → user_session）
  POST /api/login/mfa      2段階認証 OTP 送信

■ライセンス
MIT とします。
