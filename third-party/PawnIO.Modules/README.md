# PawnIO.Modules 対応ソースと照合記録

FlowDeckのビルドに含まれるPawnIOモジュールに対応する、公式上流ソースとライセンスを記録しています。既存の`.bin`は変更していません。

| 使用箇所 | 公式リリース | 照合結果 |
| --- | --- | --- |
| `third-party/LibreHardwareMonitor/Resources/PawnIo/*.bin`（12本） | [0.2.2](https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.2) | 全12本のSHA-256が公式リリースZIP内の同名ファイルと一致 |
| `src/FlowDeck.Infrastructure/native/SmbusPIIX4.bin`（1本） | [0.2.11](https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.11) | SHA-256が公式リリースZIP内の同名ファイルと一致 |

LibreHardwareMonitor側の上流`Resources/PawnIo/README`には「0.1.6」と記載されていますが、実ファイル12本の組合せは0.2.2と一致します。上流ファイルの保存とハッシュ照合を維持するため、そのREADMEは変更せず、ここに実際の対応を明記しています。0.1.6をこれら12本の対応ソースとして扱いません。

## 同伴するソース

- 0.2.2: [source-0.2.2.zip](source-0.2.2.zip)。公式タグが指すコミット`e12a858d952461ee2e919897cacbff7f905fe370`の未改変ソースです。
- 0.2.11: [既存のソースZIP](../../src/FlowDeck.Infrastructure/native/PawnIO.Modules-source-0.2.11.zip)。公式タグが指すコミットは`52a7e536dff3e53c96917a28caac5e0fa6510696`です。公式codeloadから取得したタグのZIPと、既存ZIP全体のSHA-256一致を確認しています。重複するZIPは追加していません。

いずれのソースZIPも、モジュールの`.p`、`include`、ライセンス、上流のビルド用GitHub Actions設定を含みます。上流に含まれるPawnコンパイラーのRPMとソースRPMもそのまま保持しています。

版・固定コミット・取得URL・ソースZIPのSHA-256・13本のバイナリとソースファイルの対応は[manifest.json](manifest.json)、公開ファイルのハッシュ一覧は[SHA256SUMS.txt](SHA256SUMS.txt)を参照してください。

## モジュール対応

| リポジトリ内のモジュール | 対応する公式版 |
| --- | --- |
| LHM: `AMDFamily0F.bin` | 0.2.2 |
| LHM: `AMDFamily10.bin` | 0.2.2 |
| LHM: `AMDFamily17.bin` | 0.2.2 |
| LHM: `IntelMSR.bin` | 0.2.2 |
| LHM: `IsaBridgeEC.bin` | 0.2.2 |
| LHM: `LpcACPIEC.bin` | 0.2.2 |
| LHM: `LpcCrOSEC.bin` | 0.2.2 |
| LHM: `LpcIO.bin` | 0.2.2 |
| LHM: `RyzenSMU.bin` | 0.2.2 |
| LHM: `SmbusI801.bin` | 0.2.2 |
| LHM: `SmbusNCT6793.bin` | 0.2.2 |
| LHM: `SmbusPIIX4.bin` | 0.2.2 |
| FlowDeck.Infrastructure: `SmbusPIIX4.bin` | 0.2.11 |

## 照合方法

Python 3の標準ライブラリだけで、公開ツリー内のソースZIP・ライセンス・13本のバイナリを照合できます。リポジトリのルートで実行します。

```powershell
python ./third-party/PawnIO.Modules/verify.py
```

公式リリースZIPも独立に照合する場合は、`manifest.json`の`releaseArchiveUrl`からそれぞれのZIPを同じフォルダーへ取得し、そのフォルダーを`--release-archive-dir`へ渡します。ZIPのSHA-256と各収録バイナリを比較します。この検証スクリプトはモジュールを実行せず、ハードウェア・ドライバー・サービスを操作しません。

## 上流のビルド方法と検証の範囲

ソースZIP内の`.github/workflows/ci.yml`が上流のビルド定義です。Oracle Linux 9の環境で同梱`_pawn/pawn-*.x86_64.rpm`を導入し、各`.p`へ次のコンパイル引数を渡して`.amx`を生成します。

```sh
pawncc ModuleName.p '-iinclude' '-C64' '-;+' '-(+' '-p'
```

公開している照合結果は、既存の署名済み`.bin`と公式リリースの一致を確認したものです。ローカルでコンパイル・署名済みバイナリを再生成した結果ではありません。上流の公開ビルド定義は`.amx`までを生成するため、その成功だけでは公式署名済み`.bin`と同一のファイルを生成したことにはなりません。

## ライセンス

モジュールは**LGPL-2.1-or-later**です。全文は[COPYING](COPYING)、著作権表示は各ソースファイルを参照してください。上流ソースZIP内と同じライセンス全文を置いています。公式バイナリZIP内のライセンスとは改行形式だけが異なります。

`include`のPawnIOヘッダーは各ファイルの**0BSD**表示に従います。同伴されたPawnコンパイラー・ソースRPM内のファイルは、それぞれのライセンス表示に従います。FlowDeckのMIT Licenseでこれらのライセンスを置き換えるものではありません。
