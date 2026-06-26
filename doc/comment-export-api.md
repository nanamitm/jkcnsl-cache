# Comment Export API

コメントデータを外部の分析サーバーへ回収するための API です。
**ステータスポート**（デフォルト 5001）専用で、メインポートからはアクセスできません。

---

## エンドポイント

```
GET /api/comments/export
```

---

## クエリパラメータ

| パラメータ | 必須 | 説明 |
|---|---|---|
| `date` | △ | 取得する放送日 (`yyyy-MM-dd`)。UTC の 0:00〜翌 0:00 を返す。`from`/`to` と排他 |
| `from` | △ | 取得開始日時（ISO 8601）。`to` と対で指定 |
| `to` | △ | 取得終了日時（ISO 8601）。`from` と対で指定 |
| `channel` | ✗ | チャンネルキーで絞り込み（例: `jk1`）。省略時は全チャンネル |

`date` または `from`/`to` のいずれかは必須です。`from`/`to` の範囲は最大 **31日** です。

### タイムゾーンの扱い

- `date` は **UTC** 基準です
- `from`/`to` はオフセット付き ISO 8601 を推奨します

JST（UTC+9）の1日分を取得する場合:
```
from=2026-06-26T00:00:00+09:00&to=2026-06-27T00:00:00+09:00
```

UTC で指定する場合:
```
from=2026-06-25T15:00:00Z&to=2026-06-26T15:00:00Z
```

---

## レスポンス

- Content-Type: `application/x-ndjson; charset=utf-8`
- 形式: **JSONL**（1行1レコード、改行区切り）
- `Accept-Encoding: gzip` ヘッダーを付けると gzip 圧縮で返します

レコードは `receivedAt` 昇順、同時刻の場合は `id` 昇順で返します。

### フィールド

| フィールド | 型 | 説明 |
|---|---|---|
| `id` | number | DB 内の連番 ID |
| `receivedAt` | string (ISO 8601) | キャッシュサーバーがコメントを受信した時刻（UTC） |
| `channel` | string | チャンネルキー（例: `jk1`, `jk101`, `jk141r`） |
| `no` | number \| null | コメント番号 |
| `date` | number \| null | コメント投稿時刻（Unix 秒）。ニコニコ側のタイムスタンプ |
| `userId` | string \| null | ユーザー ID。匿名投稿ではハッシュ値または空 |
| `anonymity` | number | 匿名フラグ（`1` = 匿名、`0` = 非匿名） |
| `mail` | string \| null | コマンド文字列（色・位置・サイズ）例: `"red ue big"` |
| `content` | string | コメント本文 |

### レスポンス例（1行）

```json
{"id":1234567,"receivedAt":"2026-06-26T10:30:00+00:00","channel":"jk1","no":54321,"date":1750936200,"userId":"a1b2c3d4e5","anonymity":1,"mail":"red","content":"テスト"}
```

### エラーレスポンス

HTTP 400 Bad Request で JSON を返します。

```json
{"error":"from/to は ISO 8601 形式で指定してください (例: 2026-06-26T10:00:00+09:00)"}
{"error":"from は to より前の時刻を指定してください"}
{"error":"指定できる範囲は最大31日です"}
{"error":"date は yyyy-MM-dd 形式で指定してください"}
{"error":"date または from/to を指定してください"}
```

---

## 使用例

```bash
# JST の1日分を全チャンネルで取得（gzip）
curl -H "Accept-Encoding: gzip" \
     "http://cache-server:5001/api/comments/export?from=2026-06-26T00:00:00%2B09:00&to=2026-06-27T00:00:00%2B09:00" \
     --compressed \
     -o comments-2026-06-26.jsonl

# UTC 日付指定（全チャンネル）
curl "http://cache-server:5001/api/comments/export?date=2026-06-26" \
     -o comments-2026-06-26.jsonl

# チャンネル絞り込み・時間帯指定
curl "http://cache-server:5001/api/comments/export?from=2026-06-26T19:00:00%2B09:00&to=2026-06-26T23:00:00%2B09:00&channel=jk1"

# Python での読み込み例
import json, urllib.request, gzip

url = "http://cache-server:5001/api/comments/export?date=2026-06-26"
req = urllib.request.Request(url, headers={"Accept-Encoding": "gzip"})
with urllib.request.urlopen(req) as resp:
    f = gzip.open(resp) if resp.info().get("Content-Encoding") == "gzip" else resp
    for line in f:
        row = json.loads(line)
        print(row["channel"], row["content"])
```

---

## データ保持期間と保存タイミング

- コメントは受信から最大 5 秒以内にバッチで SQLite へ書き込まれます
- デフォルトの保持期間は **2日**（設定変更可）
- シャットダウン時には未書き込みのキューもフラッシュされます

分析サーバーは**1日1回**以上の頻度で回収することを推奨します。

---

## サーバー側の設定

`local/appsettings.json` で上書きできます。

```json
{
  "CommentStorage": {
    "DbPath": "local/comments.db",
    "RetentionDays": 2
  }
}
```

| キー | デフォルト | 説明 |
|---|---|---|
| `DbPath` | `local/comments.db` | SQLite ファイルのパス（相対パスは実行ディレクトリ基準） |
| `RetentionDays` | `2` | 保持日数。この日数より古いレコードは1時間ごとに自動削除 |

---

## `mail` フィールドの解釈

`mail` はニコニコのコマンド文字列で、スペース区切りの複数コマンドが含まれます。

| カテゴリ | 値 |
|---|---|
| 色 | `red` `pink` `orange` `yellow` `green` `cyan` `blue` `purple` `black` `white` `niconicowhite` など、または `#rrggbb` |
| 位置 | `ue`（上固定）`shita`（下固定）省略時は流れるコメント |
| サイズ | `big` `small` 省略時は通常サイズ |

例: `"red ue big"` → 赤・上固定・大きいサイズ
