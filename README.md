[English](README.en.md)

# LWF Economy Graph

**Lazy Witch's Factory** の統計画面に、収支のグラフを描き足す MOD

![収入の内訳](img/composition.png)

**[最新版をダウンロード](https://github.com/KiyonakaNata/lwf-economy-graph/releases/latest)** ／ **[Thunderstore](https://thunderstore.io/c/lazy-witchs-factory/p/KiyonakaNata/LwfEconomyGraph/)**（MOD管理ソフトでお手軽導入）

---

## できること

グラフの下の帯で4つの表示を切り替える

### 返済区切りの収支情報

- 返済1回ぶんが1本の棒
- その返済を何で払ったかを積み上げる
- 棒にカーソルを合わせると、その回の内訳表示

![内訳のホバー](img/composition-hover.png)

現金だけでなく、資源でも同様の表示が可能（左:収入　右:支出）

<p>
  <img src="img/composition-fuel-income.png" width="49%" alt="燃料の収入">
  <img src="img/composition-fuel-expense.png" width="49%" alt="燃料の支出">
</p>

### 工場の収支

- 各資源の毎分の増減
- 数えるのは**生産と売却（ポータル納品）のみ**
- レリック、杖、土地購入、ペナルティ等、突発的な増減は集計しない

<p>
  <img src="img/net.png" width="49%" alt="工場が回っている">
  <img src="img/net-red.png" width="49%" alt="赤に転んだところ">
</p>

（左:異常なし　右:今後の燃料切れを示唆）

### 残高の推移

- 各種資源の残高
- 返済のあった時刻には縦線が入る
- **線の右端の絵を押すと、その線を消せる**
  - 下の帯の資源アイコンからも同じことができ、消した線はそこから戻せる

<p>
  <img src="img/balance-all.png" width="49%" alt="残高の重ね表示">
  <img src="img/balance-hidden.png" width="49%" alt="いくつか消した状態">
</p>

（左:全表示　右:除去後）

### 収支の速度

範囲を「直近1分間」にすると速度の折れ線になる

![直近1分の折れ線](img/rate.png)

---

## 入れかた（手で入れる場合）

1. **BepInEx 5** を入れる — [配布元](https://github.com/BepInEx/BepInEx/releases)
   - `BepInEx_win_x64_5.4.x.zip` を落とす
   - 中身をゲームのフォルダ（`LazyWitchsFactory.exe` と同じ場所）へ展開する

     > **ゲームのフォルダの場所**（Steam）
     > ライブラリでゲームを右クリック → 管理 → ローカルファイルを閲覧

2. 一度ゲームを起動して終了すると、`BepInEx/plugins` などが作られる
3. このMODの zip の中の `LwfEconomyGraph.dll` を **`BepInEx/plugins/` に入れる**
4. ゲームを起動し、統計画面を開いて、グラフが表示されていることを確認する

## 消しかた

**このMODだけ消す**

- `BepInEx/plugins/LwfEconomyGraph.dll`
- 設定と CSV も消すなら `BepInEx/config/kiyonakanata.lwfeconomygraph.cfg` と `BepInEx/LwfEconomy/`

**BepInEx ごと消す**（他のMODも全部止まる）

- `BepInEx` フォルダ
- ゲームのフォルダにある `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`

---

## 使いかた

![操作の帯](img/toolbar.png)

| 並び | 何が起きるか |
|---|---|
| 重ねた線 | 8種の残高グラフの切り替え |
| 資源のアイコン | その資源を見る（重ねているときは、見る／隠す） |
| 金槌 | 選んだ資源の工場の収支 |
| 三角 | 収入 ↔ 支出 |
| 帯 | ゲーム全体 ↔ 直近1分間 |
| 積み上げ | 返済ごとの内訳 |

帯のボタンと本体の統計タブは連動する

| キー | 動作 |
|---|---|
| **F5** | 表示の切替 |
| **F8** | 記録を CSV に書き出す（`BepInEx/LwfEconomy/`） |

記録はゲームを終了するか、次のランを始めると消える

---

## 設定

`BepInEx/config/kiyonakanata.lwfeconomygraph.cfg`（ゲームを一度起動すると作られる）

| 項目 | 既定 | 値 |
|---|---|---|
| `Enabled` | `true` | `false` で記録も表示も止まる |
| `EmbedInStatsWindow` | `true` | `false` で統計窓ではなく別パネルに描く |
| `StartMode` | `0` | 0=工場の収支／1=残高の推移／2=返済区切りの収支情報 |
| `FontName` | 空 | 文字が □ になるときだけ指定する（例 `Yu Gothic UI`） |
| `IconOutlineWidth` | `0.05` | アイコンの白縁の太さ（`0` で縁なし） |

---

## 動作の条件

| | |
|---|---|
| Lazy Witch's Factory | **ver 0.24.1** で作成・確認 |
| BepInEx | **5.4.23.5** で確認（5.4.x なら動くはず） |

本体の更新で動かなくなったら、このMODの寿命なので消すこと

## うまく動かないとき

まず `BepInEx/LogOutput.log` を見る

| ログ | いまどうなっているか |
|---|---|
| `[boot] LWF Economy Graph ...` が無い | **読まれていない** — DLL の置き場所を確認 |
| `統計窓の ... が見つからない` | **グラフは表示** — 下の帯のボタンが本体の窓と連動しない |
| `土地購入を挟めなかった` | **グラフは表示** — 土地の代金が「その他」に混ざる |
| `例外が 20 回続いたので、このMODを止めました` | **MODが自分で止まった** — ゲームを再起動する |

**不具合の報告**には次の3つを添えること

- 画面の写真
- `BepInEx/LogOutput.log`
- F8 で出力した CSV

---

## 免責

- **非公式のMOD** — 公式のサポート対象外
- MODを入れた状態で起きた不具合・クラッシュ・セーブデータの破損などは自己責任
- [公式のMODに関する方針](https://store.steampowered.com/news/app/3971650/view/699897618302503133)に従って作成

ソースコードは MIT ライセンス（`img/` のスクリーンショットはゲーム画面の写しで、権利は開発元に帰属）
