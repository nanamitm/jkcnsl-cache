# jkcnsl-edcb-epg-uploader

EDCB の番組表を読み取り、`jkcnsl-cache` の `POST /api/admin/epg/import` へ送る Windows 向けツールです。

## まずやること

1. `appsettings.json` の `ImportApi.BaseUrl` を必要に応じて変更
2. `local/appsettings.json.example` を参考に `local/appsettings.json` を作成
3. `ImportApi.ApiKey` を設定
4. `--dry-run` で送信件数を確認
5. 問題なければ引数なしで起動

`local/appsettings.json` は `.gitignore` で除外されているため、ローカルの API キーはそのままでもリポジトリへ入りません。

## 使い方

- 引数なしで起動すると常駐モードになります
- 常駐中はタスクトレイから `設定` `ログ` `今すぐ送信` `終了` を使えます
- 定期送信の間隔は `Scheduler.IntervalMinutes` で変更できます
- PC 起動直後の待機時間は `Scheduler.StartupDelaySeconds` で変更できます

## 設定画面

設定画面では主に以下を編集できます。

- `ImportApi.BaseUrl`
- `ImportApi.ApiKey`
- `Scheduler.IntervalMinutes`
- `Scheduler.StartupDelaySeconds`
- `Scheduler.RunImmediately`
- `ServiceMappings`

`送信チャンネル設定` では、`追加` から EDCB のサービス一覧を開いて対象チャンネルを選べます。`ONID / TSID / SID` は EDCB の識別子なので読み取り専用です。
この識別子は送信時にも `jkcnsl-cache` へそのまま渡されるため、サーバー側では `video` 名だけでなく実 `ONID / TSID / SID` ベースでもチャンネルを正規化できます。

## よく使うコマンド

- `jkcnsl-edcb-epg-uploader.exe`
  常駐モードで起動します。
- `jkcnsl-edcb-epg-uploader.exe --dry-run`
  実際には送信せず、何件送る予定かだけ確認します。
- `jkcnsl-edcb-epg-uploader.exe --list-services`
  EDCB から取得できるサービス一覧を表示します。送信チャンネル設定の確認用です。
- `jkcnsl-edcb-epg-uploader.exe --channel jk171`
  指定した 1 チャンネルだけ送信します。個別確認したいときに使います。

## 自動起動

`--install-autostart` で Windows のスタートアップに登録できます。削除は `--uninstall-autostart` です。

## ログ

- アプリ内の `ログ` 画面で直近ログを確認できます
- `logs/yyyyMMdd.log` にも出力されます

## 単一 exe の作成

```powershell
dotnet publish .\jkcnsl-edcb-epg-uploader.csproj /p:PublishProfile=Properties\PublishProfiles\win-x64-single-file.pubxml
```

出力先は `bin/publish/win-x64/` です。
