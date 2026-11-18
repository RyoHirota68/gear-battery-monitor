# Battery Monitor for Windows

Pulsar製VSPO! GEARマウスとSony INZONE Budsの残量を、USB HID経由で読み取るWindows 10/11用の常駐アプリです。Windows標準の.NET Framework 4.xを使用します。

[Windows用 exe をダウンロード](https://github.com/RyoHirota68/gear-battery-monitor/releases/latest)

![マウスと左右イヤホン、充電ケースのトレイアイコンの表示例](docs/tray-preview.png)

## 使い方

1. exeを置く場所を決めてから起動します。インストールや管理者権限は不要です。
2. マウスはUSBレシーバーまたは対応するUSB有線接続を使用します。INZONE Budsは付属USBドングルを接続し、スイッチを **PC** 側にします。
3. 通知領域にマウス、左イヤホン（L）、右イヤホン（R）、ケースの4アイコンが現れます。数字は残量のパーセントです。隠れている場合は **^** からタスクバーへドラッグするか、Windowsのタスクバー設定で表示を有効にします。
4. 右クリックで4つの残量と接続状態を確認できます。左クリックまたは「今すぐ更新」で即時取得します。
5. 自動起動が必要なら「Windows サインイン時に起動」をオンにします。exeを移動した場合は新しい場所から起動し、同項目をオンにします。解除するにはチェックを外します。

30秒ごとに更新します。各部位の残量が初めて20%以下になったときにWindowsの通知を出し、20%を超えると再通知の対象になります。マウスの充電中は通知を抑制します。

未接続やスリープ、ケース収納などで値を取得できないときは「取得不可」と表示します。ケースに両耳を収納すると、ケースの値も取得できなくなることがあります。INZONE BudsのBluetooth接続には対応していません。

## ビルド

ソースZIPを展開し、PowerShellから次を実行します。同じフォルダーに `BatteryMonitor.exe` が生成されます。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

## 接続診断

値が表示されない場合は、マウスを動かし、イヤホンをケースから取り出した状態で「今すぐ更新」を押します。詳細は同じフォルダーのPowerShellで次を実行すると記録できます。

```powershell
Start-Process .\BatteryMonitor.exe -ArgumentList '--diagnostics battery-diagnostics.txt' -Wait
```

## 参照した実装と仕様

- [mousebatt](https://github.com/ryanlewis/mousebatt/blob/main/src/protocol.rs) — Pulsarの17バイト出力レポートと64バイト機能レポート。
- [openinzone](https://github.com/penguinwokrs/openinzone/blob/main/docs/PROTOCOL.md) — Sony HIDの電池照会、チェックサム、左右とケースの並び。
- [inzone-buds-battery-tray](https://github.com/kinako19/inzone-buds-battery-tray/blob/main/docs/protocol-notes.md) — INZONE Buds固有の応答例と、ケース収納時の挙動。

接続確認に使用したレシーバーは Pulsar LED 8K Dongle（`3710:5502`）と INZONE BudsのPCモード（`054C:0EC2`）です。
