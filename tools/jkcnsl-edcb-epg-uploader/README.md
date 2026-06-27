# jkcnsl-edcb-epg-uploader

EDCB の番組表を読み取り、`jkcnsl-cache` の `POST /api/admin/epg/import` へ送る Windows 向け送信ツールです。

## 使い方

1. 必要なら `appsettings.json` の `ImportApi.BaseUrl` を実サーバー URL に変更
2. `local/appsettings.json.example` を参考に `local/appsettings.json` を作成して `ImportApi.ApiKey` を設定
3. `--dry-run` で件数確認
4. 問題なければ通常実行で送信

## コマンド

- `jkcnsl-edcb-epg-uploader.exe --list-services`
- `jkcnsl-edcb-epg-uploader.exe --dry-run`
- `jkcnsl-edcb-epg-uploader.exe --channel jk171`

## 初期設定済み ServiceMappings

```json
[
  {
    "Video": "jk171",
    "Onid": 4,
    "Tsid": 16402,
    "Sid": 171
  },
  {
    "Video": "jk172",
    "Onid": 4,
    "Tsid": 16402,
    "Sid": 172
  },
  {
    "Video": "jk173",
    "Onid": 4,
    "Tsid": 16402,
    "Sid": 173
  }
]
```

2026-06-27 にこの開発環境の EDCB `--list-services` で確認した BSテレ東系の値を初期投入しています。別環境で使う場合は `--list-services` で再確認してください。

## ローカル設定

`appsettings.json` を読み込んだ後、`local/appsettings.json` があればその内容で上書きします。`local/appsettings.json` は `.gitignore` で除外しているため、ローカルの API キーをそのまま置いてもリモートへプッシュされません。
