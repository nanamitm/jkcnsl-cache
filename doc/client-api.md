# Client API

このドキュメントでは、内蔵Webクライアントや Android アプリなどの外部クライアント向けに提供する JSON / WebSocket API を説明します。

## ソースID

各表示チャンネルには代表となる video ID があります。例: `jk104`

接続先はソースキーで表します。

- `jk104`: 公式ソース枠
- `co104`: 非公式ソース枠
- `jk104r`: 避難所またはローカルソース枠

クライアントはソースキーを自前で組み立てず、`channels[].sources[].key` と、APIが返すURLを使ってください。

`/watch/jk104` または `/comment/jk104` が要求され、`jk104` が未設定の場合、サーバーは `jk104r` へフォールバックすることがあります。両方が設定されている場合は、要求されたキーをそのまま使います。

## ソース種別

`sourceType` は安定した機械判定用の値です。`label` は表示用テキストです。

- `official`: ニコニコ公式ソース
- `unofficial`: ニコニコ生放送の非公式配信ソース
- `refuge`: 外部避難所 WebSocket ソース
- `local`: ローカルコメントストリーム
- `unknown`: 設定済みだが種別不明のソース

`type` はクライアントAPIの一部ではありません。ロジック判定にローカライズされた表示文字列を使わないでください。

## `GET /api/status`

現在のチャンネル状態とソース状態を返します。

```json
{
  "uptimeSec": 123,
  "channels": [
    {
      "id": 104,
      "name": "NHK BS8K",
      "video": "jk104",
      "bs": true,
      "running": true,
      "currentTarget": "localstream:",
      "force": 0,
      "viewers": 1,
      "totalComments": 10,
      "lastResNo": 10,
      "sources": [
        {
          "key": "jk104",
          "sourceType": "official",
          "label": "公式",
          "configured": false,
          "running": false,
          "currentTarget": null,
          "force": 0,
          "viewers": 0,
          "totalComments": 0,
          "lastResNo": 0,
          "isReserved": false,
          "scheduledStartUtc": null,
          "status": "notConfigured",
          "statusText": null,
          "commentable": false,
          "requiresAuth": true,
          "watchUrl": "/watch/jk104",
          "commentUrl": "/comment/jk104"
        },
        {
          "key": "jk104r",
          "sourceType": "local",
          "label": "ローカル",
          "configured": true,
          "running": true,
          "currentTarget": "localstream:",
          "force": 0,
          "viewers": 1,
          "totalComments": 10,
          "lastResNo": 10,
          "isReserved": false,
          "scheduledStartUtc": null,
          "status": "running",
          "statusText": "ローカルコメントストリーム",
          "commentable": true,
          "requiresAuth": false,
          "watchUrl": "/watch/jk104r",
          "commentUrl": "/comment/jk104r"
        }
      ]
    }
  ]
}
```

チャンネル直下の `force`、`viewers`、`totalComments`、`lastResNo` は表示チャンネル単位の集計値です。`sources[]` 内の値はソースごとの値です。

## 番組オブジェクト

現在番組オブジェクトには、nullable な `genreCode` と `genreName` が含まれます。TVer は `0x0` のような大分類ジャンルコードを返します。NHK、AT-X、放送大学などの外部データでは、取得元に応じて `null` または固定ジャンルになることがあります。

既知のジャンルコード:

- `0x0`: ニュース／報道
- `0x1`: スポーツ
- `0x2`: 情報／ワイドショー
- `0x3`: ドラマ
- `0x4`: 音楽
- `0x5`: バラエティ
- `0x6`: 映画
- `0x7`: アニメ／特撮
- `0x8`: ドキュメンタリー／教養
- `0x9`: 劇場／公演
- `0xA`: 趣味／教育
- `0xB`: 福祉
- `0xF`: その他

## `GET /api/programs/schedule`

1放送日分のEPGデータを返します。このAPIを呼び出しても、外部の TVer、NHK、AT-X、放送大学APIへのアクセスは発生しません。

クエリパラメータ:

- `date`: 任意。`yyyy-MM-dd` 形式の放送日です。この日付は当日 `05:00` から翌日 `05:00` までを意味します。省略時は現在の放送日を使います。

データの取得順序:
1. メモリ内キャッシュ（現在の放送日と翌放送日のデータを保持）
2. SQLite ストレージ（過去60日分を保持）— メモリにない日付はここから補完

例:

```json
{
  "date": "2026-05-06",
  "broadcastStartHour": 5,
  "startAt": "2026-05-06T05:00:00+09:00",
  "endAt": "2026-05-07T05:00:00+09:00",
  "loaded": true,
  "updatedAt": "2026-05-06T12:00:00.000+09:00",
  "channels": [
    {
      "id": 1,
      "video": "jk1",
      "name": "NHK総合",
      "bs": false,
      "programs": [
        {
          "title": "ニュース",
          "startAt": "2026-05-06T12:00:00+09:00",
          "endAt": "2026-05-06T12:30:00+09:00",
          "source": "tver",
          "genreCode": "0x0",
          "genreName": "ニュース／報道"
        }
      ]
    }
  ]
}
```

要求された放送日のデータがメモリにもDBにも存在しない場合、`loaded` は `false` になります。キャッシュ済み番組がないチャンネルは空の `programs` 配列を返すため、クライアント側では「未取得」と表示してください。

## `GET /api/programs/schedule/range`

DBに保存されているEPGデータの放送日範囲を返します。クライアントの日付ナビゲーションUI向けに使用します。

データが存在しない場合は `earliestDate`・`latestDate` ともに `null` になります。

```json
{
  "earliestDate": "2026-04-01",
  "latestDate": "2026-05-06"
}
```

## `WS /api/channels/ws`

チャンネル情報のプッシュ更新を提供します。

メッセージ:

- `snapshot`: 接続直後に送信されます。チャンネル統計、現在番組、`sources[]` を含みます。
- `stats`: 設定された間隔で送信されます。チャンネル集計値を含みます。
- `programs`: 現在番組が変化したときに送信されます。

ソース選択には `snapshot.channels[].sources[]` を使い、変化する値の更新には `stats` と `programs` を使ってください。

## コメント接続

接続には、ソース情報で提供されるURLを使います。

- 視聴・制御 WebSocket: `sources[].watchUrl`
- コメント受信 WebSocket: `sources[].commentUrl`

クライアントはまず `watchUrl` に接続し、次のメッセージを送信します。

```json
{
  "data": {
    "room": { "commentable": true }
  }
}
```

ニコニコアカウントのCookieがある場合は、`data.cookie` に含めます。

その後、`commentUrl` に WebSocket サブプロトコル `msg.nicovideo.jp#json` を指定して接続します。

## `/watch` の生存確認

サーバーは `/watch` 接続に対して、接続直後に `seat` メッセージを送信します。

```json
{
  "type": "seat",
  "data": {
    "keepIntervalSec": 30
  }
}
```

クライアントは `keepIntervalSec` 秒ごとに次のメッセージを送信してください。

```json
{
  "type": "keepSeat"
}
```

一定時間 `keepSeat` や `postComment` が届かない場合、サーバーは `/watch` 接続を切断します。

## 投稿

コメントは watch WebSocket 経由で投稿します。

```json
{
  "type": "postComment",
  "data": {
    "text": "hello",
    "vpos": 1234,
    "isAnonymous": true,
    "color": "red",
    "position": "ue",
    "size": "big"
  }
}
```

デフォルト値のコマンドは省略できます。

- color: `white`
- position: `naka`
- size: `medium`
- font: 既定フォント

ニコニコログインが必要かどうかは `requiresAuth` で判断してください。`local` と `refuge` ソースは、`commentable` が `true` の場合、ニコニコアカウントなしで投稿できます。

## スレッドID

サーバーは下流向けの thread ID を代表チャンネルIDへ正規化します。たとえば内部ソースキーが `jk104r` の場合でも、そのソース経由で接続したクライアントは `thread_id="jk104"` を受け取り、コメントは `thread="jk104"` または `thread="jk104_..."` になります。
