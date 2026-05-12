jkcnsl-ws-load

■概要
jkcnsl-cache の WebSocket コメント配信を負荷試験するための簡易ツールです。
Ubuntu / WSL2 / Linux VPS での実行を想定しています。

主な機能:
・/comment/{channel} に指定数の WebSocket クライアントを接続する
・任意で各クライアントが /watch/{channel} も接続し、一定レートでコメント投稿する
・/watch 接続時はサーバーから通知された keepIntervalSec に従って keepSeat を自動送信する
・受信コメント数、接続失敗数、切断数、投稿成功数、簡易遅延を集計する

■Go のインストール

Ubuntu / WSL2:
> sudo apt update
> sudo apt install golang-go
> go version

VPS で Go を入れたくない場合は、別環境でビルドした Linux 用バイナリをコピーして実行できます。

■ビルド

> cd tools/jkcnsl-ws-load
> go mod tidy
> go build -o jkcnsl-ws-load

開発中はビルドせずに以下でも実行できます。

> go run . --url ws://localhost:5000/comment/jk104 --clients 10 --duration 30s

■基本的な使い方

受信クライアントだけを100接続する:

> ./jkcnsl-ws-load \
>   --url ws://example.com:5000/comment/jk104 \
>   --clients 100 \
>   --duration 5m

100クライアントで受信しつつ、各クライアントが平均10秒に1コメント投稿する:

> ./jkcnsl-ws-load \
>   --url ws://example.com:5000/comment/jk104 \
>   --watch-url ws://example.com:5000/watch/jk104 \
>   --clients 100 \
>   --duration 5m \
>   --post-rate 0.1

100クライアント接続完了後に5秒待ってから測定を開始する:

> ./jkcnsl-ws-load \
>   --url ws://example.com:5000/comment/jk104 \
>   --watch-url ws://example.com:5000/watch/jk104 \
>   --clients 100 \
>   --duration 5m \
>   --post-rate 0.1 \
>   --start-after-connected \
>   --warmup 5s

HTTPS / WSS 構成:

> ./jkcnsl-ws-load \
>   --url wss://example.com/comment/jk104 \
>   --watch-url wss://example.com/watch/jk104 \
>   --clients 100 \
>   --duration 5m \
>   --post-rate 0.1

自己署名証明書などの検証を一時的に無視する場合:

> ./jkcnsl-ws-load --url wss://example.com/comment/jk104 --insecure

■オプション

--url
  /comment/{channel} の WebSocket URL です。必須です。

--watch-url
  投稿に使う /watch/{channel} の WebSocket URL です。
  --post-rate が 0 より大きい場合は必須です。

--clients
  /comment に接続するクライアント数です。省略時は 100 です。
  --post-rate が 0 より大きい場合は、同じ数の /watch 接続も作成します。

--duration
  試験時間です。例: 60s, 5m, 1h。省略時は 60s です。

--post-rate
  1クライアントあたりの平均投稿コメント数/秒です。省略時は 0 で、投稿しません。
  例: --clients 100 --post-rate 0.1 なら全体では平均10コメント/秒です。
  例: --clients 100 --post-rate 1 なら全体では平均100コメント/秒です。
  localstream の PostIntervalMilliseconds が 1000 の場合、1接続からは毎秒1コメントまでです。
  --post-rate 1 より大きい値を指定するとサーバー側で POST_TOO_FAST が返る可能性があります。

--post-text
  投稿コメントの接頭辞です。連番が末尾に付きます。
  ニコニコ公式互換の75文字制限に収めるため、60文字以下にしてください。

--ramp
  クライアント接続を分散する時間です。省略時は 10s です。
  100クライアントなら10秒かけて順番に接続します。

--start-after-connected
  全 /comment クライアントの接続成功または失敗が確定してから投稿と測定を開始します。
  --post-rate が 0 より大きい場合は、各クライアントの /watch 接続準備も待ちます。
  接続中の履歴受信や接続処理の影響を latency に混ぜたくない場合に使います。
  このオプションを使う場合、--duration は測定開始後の時間になります。

--warmup
  --start-after-connected 使用時、接続完了後に測定開始まで待つ時間です。
  ウォームアップ中の received / latency / posted などは測定開始時にリセットされます。

--report-interval
  進捗表示間隔です。省略時は 5s です。

--insecure
  WSS の TLS 証明書検証を無効化します。試験用途以外では使わないでください。

■表示される集計

progress / final の行に以下が表示されます。

connected
  接続に成功した /comment クライアント数です。

connectFailed
  接続に失敗した数です。localstream の MaxClients / MaxTotalClients 到達時にも増えることがあります。

disconnected
  切断済みクライアント数です。試験終了時は connected と近い値になります。

received
  全クライアント合計の受信メッセージ数です。
  100クライアントで全体10コメント投稿された場合、おおむね1000になります。

recvErr
  受信中にエラーで切断された数です。

posted
  /watch へ投稿を試みた数です。

postOK
  postCommentResult を受け取った数です。

postFailed
  投稿エラーまたは error 応答の数です。

lastNo
  受信した最大コメント番号です。

latencyAvgMs / latencyMaxMs
  chat.date と chat.date_usec から見た受信遅延です。
  値は「クライアントがコメントを受信した時刻 - コメント生成時刻」の平均 / 最大です。
  複数クライアント接続時は、同じコメントでも各クライアントの受信ごとに 1 サンプルとして集計します。

■段階的な試験例

まず小さく:

> ./jkcnsl-ws-load --url ws://example.com:5000/comment/jk104 --clients 10 --duration 1m --post-rate 0.1 --watch-url ws://example.com:5000/watch/jk104

次に100接続:

> ./jkcnsl-ws-load --url ws://example.com:5000/comment/jk104 --clients 100 --duration 5m --post-rate 0.1 --watch-url ws://example.com:5000/watch/jk104

問題なければ300、500へ増やします。

■注意

・負荷試験は自分が管理するサーバーに対してのみ行ってください。
・VPSで実行する場合は、試験対象サーバーの MaxClients / MaxTotalClients と OS の接続上限を確認してください。
・localstream の PostIntervalMilliseconds が 1000 の場合、--post-rate は 1 以下にしてください。
・100クライアント程度なら家庭用ルーター越しでも動く可能性は高いですが、切り分けしやすさでは負荷用VPSから実行する方が安全です。
