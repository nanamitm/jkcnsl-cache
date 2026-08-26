# Channels Stream API

`WS /api/channels/ws` は、勢いリストを WebSocket で配信するAPIです。

既存の `GET /api/channels` XML API はそのまま残し、ポーリングではなくサーバーからのプッシュ更新を受け取りたいクライアント向けに提供します。

## メッセージ

### `snapshot`

WebSocket接続が受理された直後に1回送信されます。

```json
{
  "type": "snapshot",
  "updatedAt": "2026-05-04T12:00:00.000+09:00",
  "statsIntervalSec": 2,
  "channels": [
    {
      "id": 141,
      "name": "BS日テレ",
      "video": "jk141",
      "bs": true,
      "force": 12,
      "viewers": 3,
      "comments": 4567,
      "lastResNo": 4567,
      "program": null,
      "sources": [
        {
          "key": "jk141r",
          "sourceType": "refuge",
          "label": "避難所",
          "configured": true,
          "running": true,
          "currentTarget": "wss://example.invalid/api/v1/channels/jk141/ws/watch",
          "force": 12,
          "viewers": 3,
          "totalComments": 4567,
          "lastResNo": 4567,
          "isReserved": false,
          "scheduledStartUtc": null,
          "status": "running",
          "statusText": null,
          "commentable": true,
          "requiresAuth": false,
          "watchUrl": "/watch/jk141r",
          "commentUrl": "/comment/jk141r"
        }
      ]
    }
  ]
}
```

`program` には、サーバー内でキャッシュしているEPGから現在番組が入ります。多くのチャンネルは TVer EPG を使います。`jk103` と `jk104` は `CacheServer:NhkProgramApi:API_Key` が設定されている場合に NHK 番組APIを使います。`jk333` は AT-X公式番組表、`jk531` は放送大学公式の番組情報取得APIを使います。対応表がないチャンネルや、現在番組が見つからない場合は `null` になります。

`sources` はクライアントAPIのソース情報スキーマに従います。詳細は `doc/client-api.md` を参照してください。

### `stats`

サーバー設定の間隔で繰り返し送信されます。変更されたチャンネルだけではなく、全チャンネルの集計値を含みます。

```json
{
  "type": "stats",
  "updatedAt": "2026-05-04T12:00:02.000+09:00",
  "intervalSec": 2,
  "channels": [
    {
      "id": 141,
      "video": "jk141",
      "force": 15,
      "viewers": 3,
      "comments": 4570,
      "lastResNo": 4570
    }
  ]
}
```

### `programs`

現在番組が変化したときに送信されます。クライアントが手元の番組状態を一括で置き換えられるよう、全チャンネル分を含みます。

```json
{
  "type": "programs",
  "updatedAt": "2026-05-04T12:30:00.000+09:00",
  "channels": [
    {
      "id": 141,
      "video": "jk141",
      "program": {
        "title": "[新][終]番組タイトル",
        "startAt": "2026-05-04T12:00:00+09:00",
        "endAt": "2026-05-04T13:00:00+09:00",
        "source": "tver",
        "genreCode": "0x3",
        "genreName": "ドラマ",
        "updatedAt": "2026-05-04T12:29:59.000+09:00",
        "stale": false
      }
    }
  ]
}
```

## 設定

`CacheServer:ChannelsStreamIntervalSeconds` は `stats` のプッシュ間隔です。既定値は `2` で、既存の勢いリストAPIのキャッシュ間隔に合わせています。

`CacheServer:BroadcastSendTimeoutSeconds` は `snapshot`/`programs` を各クライアントへ送信する際のタイムアウトです。既定値は `5` 秒で、コメント配信（`UpstreamChannelBase`）と共通の設定を使います。応答のないクライアントが1件でもいると、この値を超えて送信が詰まり続けることはなく、そのクライアントだけを切断して他クライアントへの配信を継続します。

`CacheServer:ProgramInfoUpdateIntervalSeconds` は、TVer EPGキャッシュを更新する間隔です。既定値は `1200` 秒です。開発用設定では短い間隔に上書きできます。

`CacheServer:ProgramInfoEvaluationIntervalSeconds` は、キャッシュ済みEPGを現在時刻と照合して現在番組を評価する間隔です。既定値は `60` 秒です。

`CacheServer:NhkProgramApi:API_Key` を設定すると、`jk103`（NHK BSプレミアム4K、service `s5`）と `jk104`（NHK BS8K、service `s6`）で NHK 番組APIを使います。APIキーは環境変数 `CacheServer__NhkProgramApi__API_Key` でも指定できます。ローカル開発では `jkcnsl-cache/local/appsettings.json` に保存することもできます。このディレクトリはGit管理外で、通常の appsettings 読み込み後に上書きされます。

`CacheServer:NhkProgramApi:Area` はNHKの地域コードです。標準例では東京の `130` を使います。

`CacheServer:NhkProgramApi:UpdateIntervalSeconds` は NHK 番組APIの更新間隔です。既定値は `43200` 秒です。過剰アクセスを避けるため、`3600` 秒未満を指定しても `3600` 秒に丸められます。`jk103` と `jk104` は別々に取得するため、NHK更新1回につきAPIリクエストは2回発生します。

NHK 番組APIの情報を他のデータと併用して表示または配信する場合は、NHK 番組APIの利用規約に従い、次のクレジットを表示してください。

`ＮＨＫ番組の情報提供:ＮＨＫ`

`CacheServer:AtxProgram` は AT-X（`jk333`）の番組表取得を制御します。有効な場合、設定された公式週間番組表URLをスクレイピングします。

`CacheServer:OujProgram` は 放送大学 BS531（`jk531`）の番組表取得を制御します。既定URLは公式XML番組情報取得APIの `https://bangumi.ouj.ac.jp/v4/bslife/oujapi.php` です。BS531の番組は `source: "ouj"` になり、ジャンルは固定で `0xF`（`その他`）になります。
