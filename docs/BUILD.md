# 開発手順

この手順はWindows向けソースのビルドと模擬動作の確認を対象とします。リポジトリのルートからPowerShellで実行してください。

## 必要な環境

- Windows 11 x64
- PowerShell
- .NET SDK: `global.json`の`10.0.202`を基準とした`latestPatch`指定に適合する安定版
- `pip`を利用できるPython。Worker用Pythonは次のスクリプトで別途構築します
- 初回のNuGet・Pythonパッケージ取得用のインターネット接続

アプリは`net10.0-windows`、`win-x64`を対象とします。M5GO・PlatformIO・OpenRGBのビルド環境は、通常のC#ビルドとDEMOには不要です。

## Pythonの準備

```powershell
./scripts/bootstrap-python.ps1 -BuildPython (Get-Command python.exe).Source
```

`python.exe`がPATHにない場合は、`-BuildPython`へ実行ファイルのパスを指定します。スクリプトはPython 3.13.12の埋め込み版をダウンロードしてSHA-256を確認し、`.tools/python313`へ展開します。構築用の依存関係と`worker/requirements.lock`のパッケージをこの環境に導入します。既存Pythonは`pip`の呼び出し元として使用します。

## ビルドと自動テスト

```powershell
./scripts/build.ps1 -Test
```

このスクリプトは順に以下を実行します。

1. `FlowDeck.sln`を`--locked-mode`で復元
2. ソリューションをReleaseでビルド
3. `tests/FlowDeck.Tests`のC#テストを実行し、`artifacts/tests/core.trx`へ結果を保存
4. `.tools/python313/python.exe`で`tests/worker`のPythonテストを実行

`-Test`を省くと復元とビルドのみです。ロックファイルを更新せずに復元が失敗する場合は、SDKとパッケージ取得先を確認してください。

この手順はサービスを登録せず、ファームウェアを書き込みません。モック・fixtureを使ったテストと、実機の発光・回転・終了時復帰の検証は別です。

## DEMOと画面試験

```powershell
dotnet run --project src/FlowDeck.App/FlowDeck.App.csproj -c Release --no-build -- --devices-preview --demo --data-dir ./artifacts/demo
```

現在の汎用画面を開くには、`--devices-preview --demo --data-dir`の3指定が必要です。指定した保存先に模擬設定・ログを作成し、実機用プロバイダーや通常サービスへ接続しません。`--demo`だけでは旧画面のDEMO経路になるため、上のコマンドをそのまま使用してください。

画面の自動試験には、別の保存先を指定して`--smoke`を追加します。Windowsの対話デスクトップ上で画面が開き、試験後に終了します。

```powershell
dotnet run --project src/FlowDeck.App/FlowDeck.App.csproj -c Release --no-build -- --devices-preview --demo --smoke --data-dir ./artifacts/demo-smoke
```

`build.ps1 -Test`はこの画面試験を自動では実行しません。

## M5GOファームウェア（任意）

M5GOを使わない場合、この手順は不要です。対象機種と操作は[ファームウェアのREADME](../firmware/m5go/README.md)を参照してください。

```powershell
python -m venv .tools/platformio
./.tools/platformio/Scripts/python.exe -m pip install platformio==6.1.18
./scripts/build-firmware.ps1
```

既定では空いている`N:`を一時的に割り当て、日本語パスを扱えないESP32ツールチェーンへ短いパスを渡します。使用中なら`-DriveLetter`に別の未使用ドライブ文字（D〜Z）を指定してください。終了時に割り当てを解除します。`-UploadPort`を指定しない限り、端末への書き込みは行いません。

## 配布物の構築範囲

公開リポジトリには完成したGUI／サービスの配布版、埋め込みPythonの構築済み環境、M5GOの生成済みファームウェア、OpenRGBの完成ランタイム、ローカルの配布素材一式を含めていません。

`scripts/package.ps1`と`scripts/package-service.ps1`は、これらを準備した開発環境で使用するスクリプトです。特に`copy-distribution-materials.ps1`は`third-party/distribution-materials.bundle.json`が指す素材一式と、そのハッシュ・ライセンス・対応ソースの整合性を確認します。必要な素材がないclone直後の状態では、パッケージ作成は完了しません。

OpenRGB連携のネイティブビルドには、別途固定された上流ソースとC++／Qtなどのビルド環境が必要です。検証fixtureの対応ソースと手順は`third-party/OpenRGB-Fixtures`を参照してください。既存の`package`スクリプトを実行することは、バイナリ配布に必要な依存物・通知の確認が完了したことを意味しません。
