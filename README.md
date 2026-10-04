# FlowDeck

Windows向けの温度・ファン回転数の監視、ファン制御、RGB照明をまとめるデスクトップアプリです。C# / WPFの画面とWindowsサービス、Python / liquidctlのWorkerで構成し、任意のM5GO端末から状態の表示と操作もできます。

**開発中の2.0系ソースを公開しています。** インストーラー、実行ファイル、配布用ZIPはこのリポジトリでは提供していません。最初は、実機やサービスへ接続しないDEMOで画面を確認できます。

## 主な機能

- CPU・GPU・マザーボードの温度、ファン／ポンプの回転数の表示
- ファンの固定出力、温度カーブ、端子ごとの設定、異常時・終了時の動作管理
- RGBの固定色・機器内蔵効果・同期、明るさ、照明グループとシーン
- 通知、設定の書き出しと復元、タスクトレイ常駐、ライト／ダークテーマ
- M5GOのUSB・Wi-Fi接続、状態表示、ファン・照明の操作、本体LED同期

NZXT Control Hub、LibreHardwareMonitor経由のセンサー／マザーボードファン、ASRock USB RGB、ENE RGBメモリ、NVIDIA GPU監視、任意のOpenRGB連携を扱う実装があります。機能は機器と接続経路によって異なります。**検出可能な機種や定義ファイルへの掲載は、実機での動作保証を意味しません。** 実機検証は限られた構成で行っており、他のPC・機器・ファームウェアを含めた受入検証は完了していません。

## ビルドしてDEMOを開く

Windows 11 x64、PowerShell、[global.json](global.json)に合う.NET SDK、`pip`のあるPythonが必要です。リポジトリのルートで実行します。

```powershell
# BuildPythonには、pipを利用できる既存Pythonの実行ファイルを指定
./scripts/bootstrap-python.ps1 -BuildPython (Get-Command python.exe).Source

# ロック済み依存関係の復元、Releaseビルド、C#・Pythonテスト
./scripts/build.ps1 -Test

# 実機へ接続しない汎用画面のDEMO
# DEMOの設定・ログはartifacts/demoに保存
dotnet run --project src/FlowDeck.App/FlowDeck.App.csproj -c Release --no-build -- --devices-preview --demo --data-dir ./artifacts/demo
```

.NET SDKの指定は`10.0.202`、`rollForward: latestPatch`です。Pythonの準備スクリプトはPython 3.13.12 x64の埋め込み環境を`.tools/python313`へ作成し、Workerの依存関係を固定バージョンで導入します。初回はインターネット接続が必要です。

DEMOの温度・回転数・接続機器は模擬データです。上のDEMOコマンドは実ハードウェア用プロバイダーを生成せず、Windowsサービスの導入も行いません。ビルドの詳細、画面試験、任意のファームウェアは[開発手順](docs/BUILD.md)を参照してください。

## 実機での利用について

通常の起動はWindowsサービスへ接続します。DEMOでのビルド・テスト成功だけでは、サービス導入、機器制御、異常時の復帰まで確認したことにはなりません。ファン制御を使う場合は、端子と実配線、接続ファンの最低出力、異常時・終了時の設定を対象機器で確認してください。同じ機器を操作する他の制御ソフトとの同時使用も避けてください。

サービス・ネイティブ依存物を含めた配布物の構築には、公開ソースとは別のローカル配布素材が必要です。`scripts/package.ps1`は開発用のパッケージ作成スクリプトであり、clone直後に完成した配布版を生成する手順ではありません。詳しくは[配布物の構築範囲](docs/BUILD.md#配布物の構築範囲)を参照してください。

## 構成

| パス | 内容 |
| --- | --- |
| `src/FlowDeck.App` | WPF画面、トレイ、DEMO |
| `src/FlowDeck.Core` | 制御方針、状態管理、通信契約 |
| `src/FlowDeck.Infrastructure` | 機器・センサー・保存・通信の実装 |
| `src/FlowDeck.Service` | Windowsサービス |
| `worker` | Python / liquidctl Worker |
| `device-definitions` | 機器と連携ランタイムの定義 |
| `firmware/m5go` | 任意のM5GOファームウェア |
| `tests` | C#・Python・ネイティブの試験とfixture |
| `native/OpenRgb` | OpenRGB連携用の追加コード |
| `third-party` | ビルドに必要な第三者ソース、ライセンス、対応ソース |
| `scripts` | ビルド、検証、開発用パッケージ作成 |

## ライセンス

自作部分は[MIT License](LICENSE)で公開します。個別のライセンス表示があるコード、第三者ソース、リソースにはそれぞれのライセンスが適用されます。

- LibreHardwareMonitorとその変更部分: MPL-2.0、および同プロジェクトの第三者ライセンス
- `native/OpenRgb`の追加コード、OpenRGB由来のfixture・検出定義など: GPL-2.0-or-later
- Python Workerが依存するliquidctl: GPL-3.0-or-later
- PawnIO関連リソース、M5GOの依存ライブラリなど: 各プロジェクトのライセンス

詳細は[第三者ソフトウェアの通知](THIRD-PARTY-NOTICES.md)と各ディレクトリのライセンスを参照してください。FlowDeckはNZXT、ASRock、M5Stackなど各メーカーの公式製品ではありません。
