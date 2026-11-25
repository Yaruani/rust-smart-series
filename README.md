# Rust Smart Series

Rustの精錬、調合、リサイクル、工業コンベア、クラフト、アイテムスタックを調整するCarbon／Oxide（uMod）向けプラグイン集です。必要なプラグインを個別に導入できます。

## プラグイン一覧

| プラグイン | バージョン | 主な機能 | 管理者用チャットコマンド |
|---|---|---|---|
| [SmartSmelt](SmartSmelt.cs) | 1.3.2 | 炉ごとの精錬・燃料消費・出力倍率を設定 | `/smeltspeed 10` |
| [SmartMixing](SmartMixing.cs) | 1.3.2 | 混合台で処理するバッチ量を変更 | `/mixspeed 200` |
| [SmartRecycle](SmartRecycle.cs) | 1.3.5 | バッチ量・効率・視覚／音響効果を変更 | `/recyclespeed 2`、`/recyclespeed visual`、`/recyclespeed sound` |
| [SmartConveyor](SmartConveyor.cs) | 1.3.4 | 工業コンベアが1回に転送できるアイテム数の上限を調整 | `/conveyorspeed 100` |
| [SmartCraft](SmartCraft.cs) | 1.3.3 | 即時・倍速クラフトと対象アイテムの制御 | `/smartcraft speed 0`、`/smartcraft toggle`、`/smartcraft reload` |
| [SmartStack](SmartStack.cs) | 1.4.8 | 全体倍率・カテゴリ倍率・アイテムごとのスタック上限を設定 | `/stack set wood 10000`、`/stack global 10`。別名は `/smartstack` |

## 導入と設定

1. 必要な `.cs` ファイルをCarbonでは `carbon/plugins`、Oxideでは `oxide/plugins` に配置します。
2. 初回ロード時に `carbon/configs` または `oxide/config` にプラグインごとのJSONが生成されます。`Plugin Enabled` の既定値は全6本で `true` です。
3. 設定を変更したら対象プラグインを再ロードします。SmartCraftの設定ファイルは `/smartcraft reload` でも再読込できます。

### 利用権限

SmartStack以外の5本は `Use Permission` が既定で有効です。上の表にある設定コマンドは、Rustのサーバー管理者がゲーム内チャットで使います。

| プラグイン | 利用権限 |
|---|---|
| SmartSmelt | `smartsmelt.use` |
| SmartMixing | `smartmixing.use` |
| SmartRecycle | `smartrecycle.use` |
| SmartConveyor | `smartconveyor.use`。コンベア設置者の権限を確認します。 |
| SmartCraft | `smartcraft.use` で即時・倍速クラフトを利用できます。`smartcraft.normal` を持つプレイヤーには通常速度を適用します。 |
| SmartStack | 利用権限の付与は不要です。変更した上限はサーバー全体に適用されます。 |

## 倍率と設定コマンド

| コマンド例 | 変更する内容 |
|---|---|
| `/smeltspeed 10` | `Speed Multipliers` の `global` を10に設定します。炉ごとの設定がある場合はその値を優先します。木炭の出力倍率は `Output Multipliers` の `charcoal` で設定します。 |
| `/mixspeed 200` | 混合台で1回に処理するバッチの設定値を200にします。実際の処理量は材料と出力欄の空きに応じます。 |
| `/recyclespeed 10` | リサイクラーで1回に処理する入力の設定値を10個にします。`visual` または `sound` を渡すと、対応する演出を切り替えます。 |
| `/conveyorspeed 100` | 1回の転送上限に使う倍率を100にします。上限は基準値60に倍率を掛けて計算されます。 |
| `/smartcraft speed 0` / `/smartcraft speed 10` | それぞれ即時モードと10倍速を設定します。`/smartcraft toggle` で機能を切り替え、`/smartcraft reload` で設定ファイルを再読込できます。 |

精錬・調合・リサイクルの独自処理では、必要な出力枠が足りない場合に処理を止めます。

## SmartStackの管理

`/smartstack` は `/stack` の別名です。

| コマンド | 動作 |
|---|---|
| `/stack search wood` | アイテムのShortnameまたは英語の表示名で検索し、チャットに両方を表示します。 |
| `/stack set 10000` | 手に持ったアイテムの上限を10,000個にします。 |
| `/stack set wood 50000` | Shortnameで指定したアイテムの上限を50,000個にします。指定できる上限は1以上の整数です。 |
| `/stack reset` / `/stack reset wood` | 手に持ったアイテム、またはShortnameで指定したアイテムの個別設定を解除します。 |
| `/stack global 10` | 全体倍率を10に更新します。カテゴリ倍率と個別上限は引き続き優先されます。 |
| `/stack status` | 有効状態、全体倍率、個別上限をサーバーコンソールに出力します。 |

適用順は、`Specific Item Overrides (Shortname: Limit)` の**個別上限**、`Category Multipliers (Overrides Global)` の**カテゴリ倍率**、`Global Stack Multiplier` の**全体倍率**です。カテゴリ倍率と全体倍率は元のスタック上限に掛けます。

## 旧プラグインからの移行

移行処理は、移行先のプラグインに設定ファイルがまだない場合に実行されます。

| プラグイン | 初回ロード時の扱い |
|---|---|
| SmartStack | `StackModifier.json` の `Stack Categories` から個別のスタック上限を取り込みます。 |
| SmartCraft | `InstantCraft.json` から通常速度・禁止アイテムと、空き確認・スタック分割の設定を取り込みます。 |
| SmartConveyor | `ConveyorStacks.json` の既定転送上限を倍率に換算します。 |
| SmartMixing / SmartRecycle | 旧 `MixingSpeed.json` / `RecycleManager.json` を検知した場合、新プラグインの既定値で初期化します。 |

SmartSmeltは、読み込んだ設定の中に `furnace.shortname` を含む旧形式を検知すると、SmartSmeltの既定値で設定を書き直します。

## ライセンス

[MIT License](LICENSE)
