# jkcnsl-edcb-epg-uploader

EDCB の番組表を読み取り、`jkcnsl-cache` の `POST /api/admin/epg/import` へ送る Windows 向け送信ツールです。

## 使い方

1. 必要なら `appsettings.json` の `ImportApi.BaseUrl` を実サーバー URL に変更
2. `local/appsettings.json.example` を参考に `local/appsettings.json` を作成して `ImportApi.ApiKey` を設定
3. `--dry-run` で件数確認
4. 問題なければ通常実行で送信

引数なしで起動した場合は、既定で常駐モードとして起動します。

常駐起動時はコンソールを開かないようにしてあります。`--dry-run` や `--list-services` など出力が必要なコマンド時だけ、親コンソールへ表示します。

## コマンド

- `jkcnsl-edcb-epg-uploader.exe --list-services`
- `jkcnsl-edcb-epg-uploader.exe --dry-run`
- `jkcnsl-edcb-epg-uploader.exe --channel jk171`
- `jkcnsl-edcb-epg-uploader.exe --watch`
- `jkcnsl-edcb-epg-uploader.exe --install-autostart`
- `jkcnsl-edcb-epg-uploader.exe --uninstall-autostart`

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

## 常駐送信

引数なし、または `--watch` を付けて起動すると常駐モードで起動し、`Scheduler.IntervalMinutes` ごとに番組表を自動送信します。既定ではタスクトレイに常駐し、右クリックメニューから `設定` `ログ` `今すぐ送信` `終了` が使えます。自動起動の登録/削除は設定ダイアログから操作できます。起動直後に1回送るかどうかは `Scheduler.RunImmediately`、PC起動直後の待機時間は `Scheduler.StartupDelaySeconds` で調整できます。

同時起動防止のため、常駐モードでは `Scheduler.MutexName` の named mutex を使います。すでに常駐中なら2重起動は失敗します。`Scheduler.UseTrayIcon` を `false` にすると従来どおりコンソール常駐に戻せます。`Scheduler.HideConsoleWindow` を `true` にしている場合、トレイ常駐時はコンソールを隠します。

## 設定 UI とログ

トレイメニューの `設定` から、以下を `local/appsettings.json` へ保存できます。

- `ImportApi.BaseUrl`
- `ImportApi.ApiKey`
- `Scheduler.IntervalMinutes`
- `Scheduler.StartupDelaySeconds`
- `Scheduler.RunImmediately`
- `Scheduler.UseTrayIcon`
- `Scheduler.HideConsoleWindow`
- `ServiceMappings`

`ServiceMappings` は設定ダイアログ内の表で編集できます。`追加` `削除` `上へ` `下へ` `既定値を追加` に加えて、`EDCBから候補取得` で EDCB のサービス一覧から行を追加できます。

トレイメニューの `ログ` では、アプリ内で保持している直近ログを確認できます。あわせて `logs/yyyyMMdd.log` にもファイル出力されます。

## 自動起動

`--install-autostart` で、Windows のユーザー `スタートアップ` フォルダにショートカットを作成します。ログオン時はそのショートカットから本アプリが起動します。削除は `--uninstall-autostart` です。

以前のタスクスケジューラ方式と違って、通常のユーザー権限でも登録しやすく、`アクセスが拒否されました` のようなエラーを避けやすくしています。
