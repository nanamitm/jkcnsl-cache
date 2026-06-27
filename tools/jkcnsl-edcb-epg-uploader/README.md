# jkcnsl-edcb-epg-uploader

EDCB の番組表を読み取り、`jkcnsl-cache` の `POST /api/admin/epg/import` へ送る Windows 向け送信ツールです。

## 概要

- 引数なしで起動すると常駐モードで起動します
- 常駐時はタスクトレイに入り、定期的に番組表を送信します
- `--dry-run` や `--list-services` のような確認用コマンドも使えます
- 常駐起動時はコンソールを開かないようにしてあります

## 初回セットアップ

1. 必要なら `appsettings.json` の `ImportApi.BaseUrl` を実サーバー URL に変更
2. `local/appsettings.json.example` を参考に `local/appsettings.json` を作成して `ImportApi.ApiKey` を設定
3. 必要なら設定ダイアログの `送信チャンネル設定` を確認
4. `--dry-run` で件数確認
5. 問題なければ通常起動または `--watch` で常駐開始

## 普段の使い方

- 通常は引数なしで起動すれば常駐モードになります
- タスクトレイの右クリックメニューから `設定` `ログ` `今すぐ送信` `終了` が使えます
- 定期送信の間隔は `Scheduler.IntervalMinutes` で調整できます
- 起動直後に1回送るかどうかは `Scheduler.RunImmediately` で変更できます
- PC起動直後の待機時間は `Scheduler.StartupDelaySeconds` で調整できます

## 設定ファイル

`appsettings.json` を読み込んだ後、`local/appsettings.json` があればその内容で上書きします。`local/appsettings.json` は `.gitignore` で除外しているため、ローカルの API キーをそのまま置いてもリモートへプッシュされません。

## 単一 exe 配布

単一ファイルの自己完結 exe は、publish profile を使って作成できます。

```powershell
dotnet publish .\jkcnsl-edcb-epg-uploader.csproj /p:PublishProfile=Properties\PublishProfiles\win-x64-single-file.pubxml
```

出力先は `bin/publish/win-x64/` です。`local/appsettings.json` は引き続き外部ファイルとして扱う想定なので、配布時は必要に応じて `local/appsettings.json.example` を元に別途配置してください。

## 設定ダイアログ

`設定` から以下を編集できます。

- `ImportApi.BaseUrl`
- `ImportApi.ApiKey`
- `Scheduler.IntervalMinutes`
- `Scheduler.StartupDelaySeconds`
- `Scheduler.RunImmediately`
- `Scheduler.UseTrayIcon`
- `Scheduler.HideConsoleWindow`
- `ServiceMappings`

`ServiceMappings` は表形式で編集できます。

- `追加`
- `削除`
- `上へ`
- `下へ`
- `既定値を追加`
- `EDCBから候補取得`

保存や自動起動登録の成功メッセージは、設定ウィンドウ右下のステータスに表示されます。

## 送信チャンネル設定

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

## ログ

- `ログ` 画面で直近ログを確認できます
- `logs/yyyyMMdd.log` にもファイル出力されます

## 常駐送信

引数なし、または `--watch` を付けて起動すると常駐モードで起動し、`Scheduler.IntervalMinutes` ごとに番組表を自動送信します。既定ではタスクトレイに常駐し、右クリックメニューから `設定` `ログ` `今すぐ送信` `終了` が使えます。自動起動の登録/削除は設定ダイアログから操作できます。起動直後に1回送るかどうかは `Scheduler.RunImmediately`、PC起動直後の待機時間は `Scheduler.StartupDelaySeconds` で調整できます。

同時起動防止のため、常駐モードでは `Scheduler.MutexName` の named mutex を使います。すでに常駐中なら2重起動は失敗します。`Scheduler.UseTrayIcon` を `false` にすると従来どおりコンソール常駐に戻せます。`Scheduler.HideConsoleWindow` を `true` にしている場合、トレイ常駐時はコンソールを隠します。

## 自動起動

`--install-autostart` で、Windows のユーザー `スタートアップ` フォルダにショートカットを作成します。ログオン時はそのショートカットから本アプリが起動します。削除は `--uninstall-autostart` です。

以前のタスクスケジューラ方式と違って、通常のユーザー権限でも登録しやすく、`アクセスが拒否されました` のようなエラーを避けやすくしています。

## コマンド一覧

- `jkcnsl-edcb-epg-uploader.exe`
- `jkcnsl-edcb-epg-uploader.exe --watch`
- `jkcnsl-edcb-epg-uploader.exe --dry-run`
- `jkcnsl-edcb-epg-uploader.exe --list-services`
- `jkcnsl-edcb-epg-uploader.exe --channel jk171`
- `jkcnsl-edcb-epg-uploader.exe --install-autostart`
- `jkcnsl-edcb-epg-uploader.exe --uninstall-autostart`
