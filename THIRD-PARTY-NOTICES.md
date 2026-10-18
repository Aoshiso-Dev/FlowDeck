# Third-party notices

FlowDeck の自作部分には [MIT License](LICENSE) を適用します。以下の第三者コード、そこから派生したコード、ライセンス表示のあるファイルには、それぞれの元のライセンスが適用されます。ルートの MIT ライセンスで第三者の条件を置き換えるものではありません。

## このリポジトリに含むもの

| 対象 | 出典・ライセンス | ソース・通知 |
| --- | --- | --- |
| `third-party/LibreHardwareMonitor/` | [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), MPL-2.0 および個別の通知 | 同フォルダーの `LICENSE`、`THIRD-PARTY-NOTICES.txt`、`UPSTREAM.json`、`FLOWDECK-PATCHES.md` |
| LHM の `Resources/PawnIo/*.bin` | [PawnIO.Modules 0.2.2](https://github.com/namazso/PawnIO.Modules/tree/0.2.2), LGPL-2.1-or-later | `third-party/PawnIO.Modules/` の対応ソースと通知 |
| `src/FlowDeck.Infrastructure/native/SmbusPIIX4.bin` | [PawnIO.Modules 0.2.11](https://github.com/namazso/PawnIO.Modules/tree/0.2.11), LGPL-2.1-or-later | 同フォルダーの `PawnIO.Modules-source-0.2.11.zip` と `PawnIO.Modules-LICENSE.txt` |
| `native/OpenRgb/`、`tests/native/openrgb-*/`、OpenRGB の改変・fixture 作成用スクリプト | [OpenRGB](https://github.com/CalcProgrammer1/OpenRGB), GPL-2.0-or-later | 各ファイルの表示、および `third-party/OpenRGB-Fixtures/` のライセンス・ソース・ビルド手順 |
| `tests/FlowDeck.Tests/Fixtures/Asrock*.bin` と OpenRGB から抽出した `Asrock*.json` | OpenRGB を用いた模擬テスト用データ・プログラム, GPL-2.0-or-later | `third-party/OpenRGB-Fixtures/` の対応ソース、元の `*.provenance.json`、`scripts/` の生成処理 |
| `device-definitions/openrgb-detectors.json` | 固定 OpenRGB ソースの検出登録定義から抽出, GPL-2.0-or-later | `sourceRevision` と `sourceArchiveSha256` を同ファイルに記録。抽出処理は `scripts/inspect-openrgb-detectors.py` |
| fixture 用 nlohmann/json | [JSON for Modern C++](https://github.com/nlohmann/json), MIT | `third-party/OpenRGB-Fixtures/` 内のソースとライセンス |

LibreHardwareMonitor は 0.9.6 の固定コミット `3d331e3370efb858411f19511373eff65a218701` に FlowDeck patch 8 を適用しています。改変済みのライブラリソースとビルド設定を含み、変更点は `FLOWDECK-PATCHES.md` に記録しています。

OpenRGB の固定コミットは `81bbe18a84c2e507006f19dd252e397e40a56bfe` です。テスト用プログラムは HID を模擬し、機器へ出力しません。OpenRGB アプリ本体や Qt の配布用実行環境は、このリポジトリには含めていません。

## ビルド時・実行時に取得する依存関係

NuGet の直接・推移依存は各プロジェクトの `packages.lock.json`、Python の固定版は `worker/requirements.lock`、ファームウェアの固定版は `firmware/dial/platformio.ini` を参照してください。下表は主な依存関係であり、取得したパッケージのライセンスと第三者通知も保持してください。

| コンポーネント | ライセンス・出典 |
| --- | --- |
| .NET / WPF / System.* | MIT, [dotnet](https://github.com/dotnet) |
| Python | PSF, [Python](https://www.python.org/) |
| liquidctl | GPL-3.0-or-later, [liquidctl](https://github.com/liquidctl/liquidctl) |
| HidSharp | Apache-2.0, [HidSharp](https://www.zer7.com/software/hidsharp) |
| Arduino_GFX (Dial) | BSD-2-Clause および各ファイル・フォントの通知, [Arduino_GFX](https://github.com/moononournation/Arduino_GFX), パッケージの `license.txt` とフォントの通知を保持 |
| U8g2 (Dial 日本語フォント) | BSD-2-Clause および GNU Unifont のフォントライセンス・埋込み例外, [U8g2](https://github.com/olikraus/u8g2), パッケージの `LICENSE` と `u8g2_font_unifont_t_japanese3` の通知を保持 |
| ArduinoJson | MIT, [ArduinoJson](https://github.com/bblanchon/ArduinoJson) |
| Arduino ESP32 / ESP-IDF | LGPL-2.1 / Apache-2.0 および個別の第三者ライセンス, [Arduino ESP32](https://github.com/espressif/arduino-esp32) |

Python Worker は liquidctl 1.16.0 を別プロセスで使用し、Control Hub の照明チャンネルに[上流の修正](https://github.com/liquidctl/liquidctl/commit/6227bbfdfe33db2a8e12361b9a96495c00676275)に基づくインスタンス単位の補正を適用します。元のパッケージファイルは変更しません。liquidctl と組み合わせた Worker の配布時は、その GPL 条件も適用されます。

この公開は開発用ソースとテスト素材を対象にしています。アプリ・ファームウェア・ランタイムをまとめた配布物を作る場合は、その配布物に含める全依存関係の通知・対応ソース・再リンク資料を別途同梱してください。

FlowDeck は NZXT、ASRock、GUITION、Microsoft 等の公式製品ではありません。各名称・商標はそれぞれの権利者に帰属します。

Dial の基板配線は [GUITION K5-series](https://github.com/guitionofficial/K5-series/tree/faf15c076362151d6932e95409532fb9bf0325ab) の公開回路図・ピン定義を参照しています。`firmware/dial/src/guition_st77916.h` の LCD 初期化表は、メーカーサンプルの `scr_st77916.h`（Git blob `388c074c73a915b3aba1f46a7a9837850b65ac48`）のレジスタ設定値・送信順・待機時間を Arduino_GFX の形式で記述しています。参照元と SHA-256 は同ファイルとファームウェアの README に記載しています。参照元の当該ファイルに適用されるライセンス表記は確認できておらず、これらのメーカー由来の設定に FlowDeck の MIT License を付与するものではありません。Dial の独自実装にはルートの MIT License を適用します。
