// LWF Economy Graph
//
// Lazy Witch's Factory（ver 0.24.1）に、収支の時系列グラフとラン終了時の
// 集計パネルを足す BepInEx Mod。
//
// -- なぜ必要か ---------------------------------------------------------
// 本体の統計窓（Stats/StatsWindowController）が持っているのは
//   ・累計
//   ・直近1分
// の2つの数字だけで、時間軸を一切保存していない。
// 「どの時点で稼ぎが伸びたのか」「返済のたびにどれだけ削られたのか」は
// ゲーム内のどこにも残らない。リザルト画面も勝敗とメタ通貨の演出だけで、
// そのランの収支は出てこない。
//
// -- どうやって取っているか ---------------------------------------------
// ほぼ本体が公開している口だけで足りる。Harmony を当てているのは
// 記録係のコンストラクタ1点だけで、ゲームの挙動は変えない
// （納品口ごとの記録係を、探索せずに生成の瞬間に掴むため）。
//
//   GameStateManager.Instance.GetTagsRecorder().OnTagRecorded
//     → TagRecordedEvent(tagID, delta, newTotal, isAdd, statsCashReason, statsSourceID, saleItemCountDelta)
//       （saleItemCountDelta は 0.29.1 で増えた。統計窓の売却個数用でここでは使わない。
//        引数が増えると古い DLL は MissingMethodException で起動を止めるので、版ごとにビルドし直す）
//
// タグ（現金・建材・食品…）の増減が1件ずつ、理由17種つきで流れてくる。
// 記録係は納品口（ポータル）ごとに別にあり、従の記録係で受けた分は
// **自分のイベントしか発火しない**（主へは無音で数だけ足す）ので、全部に繋ぐ。
// 残高のほうはイベントから取らず、主の記録係を毎フレーム見る
// （従の newTotal はその記録係の手持ちで、全体の残高ではないため）。
// 時刻は GameStateManager.GetElapsedGameplaySeconds()（ポーズを除いた経過秒）。
//
// -- 制約 ---------------------------------------------------------------
// 本体が時系列を持っていない以上、この Mod は「入れてから先」しか描けない。
// ランの途中で入れたらその途中から、入れる前のランは何も出ない。
// 開始時の所持分（InitialIncome）は購読より前に記録が済んでいるので、
// 購読した瞬間の残高を t=0 の初期値として置く。収支の合計には含めない。
//
// -- 保存の仕方 ---------------------------------------------------------
// バケット（既定1秒）ごとに「残高の最後の値」と「理由別の増減」を持つ。
// バケット数が上限（既定1800）を超えたら2つずつ束ねて幅を倍にする。
// 30分までは1秒、1時間までは2秒…と、ラン全体が必ず1画面に収まる。
// 生ログ（1件ずつ）は CSV 書き出し用に別途ためるが、上限で打ち切る。
//
// C# 5 コンパイラ（csc.exe / .NET Framework 4.0）でビルドするため、
// 文字列補間・?. 演算子・式形式メンバ・out var は使えない。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Map.Ownership;
using Pact.Pacts.LandPacts;
using Pact.Pacts.WandPacts;
using GameState;
using Items;
using Items.Craft;
using Map.MapObjects.Mono;
using Items.Delivery;
using Items.Tag;
using Items.ValueCountSettings;
using R3;
using BaseSystem;
using Stats;
using TMPro;
using UI;
using UI.Cursor;
using UI.SelectableWindow.Managers;
using Utility.Localization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LwfEconomyGraph
{
    /// <summary>
    /// "Ctrl+Alt+Enter" のような文字列で指定するホットキー。
    /// 修飾キーは指定したものが押されていて、指定していないものが押されていない場合のみ成立する。
    /// </summary>
    internal sealed class Hotkey
    {
        private readonly bool _ctrl;
        private readonly bool _alt;
        private readonly bool _shift;
        private readonly Key _key;
        private readonly string _text;

        private Hotkey(bool ctrl, bool alt, bool shift, Key key, string text)
        {
            _ctrl = ctrl; _alt = alt; _shift = shift; _key = key; _text = text;
        }

        public override string ToString() { return _text; }

        internal static Hotkey Parse(string spec)
        {
            if (string.IsNullOrEmpty(spec)) { return null; }

            bool ctrl = false, alt = false, shift = false;
            string keyName = null;

            string[] parts = spec.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) { continue; }
                string lower = p.ToLowerInvariant();
                if (lower == "ctrl" || lower == "control") { ctrl = true; }
                else if (lower == "alt") { alt = true; }
                else if (lower == "shift") { shift = true; }
                else { keyName = p; }
            }
            if (keyName == null) { return null; }

            try
            {
                Key k = (Key)Enum.Parse(typeof(Key), keyName, true);
                return new Hotkey(ctrl, alt, shift, k, spec.Trim());
            }
            catch (Exception) { return null; }
        }

        internal bool WasPressedThisFrame(Keyboard kb)
        {
            if (kb == null) { return false; }

            bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            bool alt = kb.leftAltKey.isPressed || kb.rightAltKey.isPressed;
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            if (ctrl != _ctrl || alt != _alt || shift != _shift) { return false; }

            if (kb[_key].wasPressedThisFrame) { return true; }
            if (_key == Key.Enter && kb[Key.NumpadEnter].wasPressedThisFrame) { return true; }
            return false;
        }
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class EconomyGraphPlugin : BaseUnityPlugin
    {
        internal const string PluginGuid = "kiyonakanata.lwfeconomygraph";
        internal const string PluginName = "LWF Economy Graph";
        internal const string PluginVersion = "2.0.1";

        private static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.9f);

        /// <summary>
        /// 出所IDを持たない理由に当てる絵（バフのタグID）。
        /// タグのアイコンに落とすと注文と返済が同じ絵になって見分けられないため。
        /// StatsCashReason の並び。空はタグのアイコンか、下の個別の当て方に落ちる。
        ///
        /// 注文に当てているのは「注文が早くなる」レリックの絵。**アイコンとして描かれた電話**で、
        /// 挿絵の telephone は1枚絵なので小さくすると潰れる。
        /// </summary>
        private static readonly string[] ReasonIconTag = new string[ChartRenderer.ReasonCount]
        {
            "",              // Other
            "FasterOrder",   // OrderIncome   注文
            "",              // SaleIncome    （売却はアイテムの絵が出る）
            "",              // BuffRelicIncome
            "FasterOrder",   // OrderExpense  注文
            "",              // RepaymentExpense  納品＝ゲヘナの顔（下で当てる）
            "",              // DebuffPenaltyExpense
            "",              // PactCostExpense
            "",              // RerollExpense
            "",              // WandSkillExpense  杖スキルのコスト＝魔法のワンド（下で当てる）
            "",              // InitialIncome
            "",              // PactEffectIncome
            "",              // TelephoneExpense
            "",              // CraftIncome
            "",              // CraftExpense
            "",              // TaxExpense
            "",              // CostRefund    （返金はレリックの絵が出る）
            "FreeLand",      // 土地購入（本体には無い。土地無料券の絵を借りる）
            "",              // 地面師（本体には無い。マモンの顔を下で当てる）
            ""               // 杖の収入（本体には無い。魔法のワンドの絵が出る）
        };

        /// <summary>
        /// 理由の呼び名を本体のメッセージ表から引くための鍵（StatsCashReason の並び）。
        ///
        /// **本体が実際に引いている鍵だけを使う。** 独自の鍵を書くと、その言語の表に
        /// 無いので日本語のまま出てしまう（あるいは鍵がそのまま出る）。
        ///
        /// 杖・電話・リロール・初期は本体に対応する語が無い——`ResolveDisplayKind` が
        /// どれも Other に丸めているため。なのでこちらも「その他」を引く。
        /// 分類が本体と揃うし、全言語で正しく出る。
        /// </summary>
        private static readonly string[] ReasonMessageKeys = new string[ChartRenderer.ReasonCount]
        {
            "StatsReasonOther", "Order", "StatsColumnSale", "Relic", "Order", "Repayment",
            "DebuffRelic", "Pact", "StatsReasonOther", "StatsReasonOther", "StatsReasonOther",
            "Pact", "StatsReasonOther", "StatsColumnProduction", "StatsColumnProduction", "Tax",
            "StatsReasonOther", "", "Pact", "StatsReasonOther"
        };

        /// <summary>本体に対応する語が無いものの控え。</summary>
        private static readonly string[] ReasonFallback = new string[ChartRenderer.ReasonCount]
        {
            "Other", "Order", "Sell Off", "Relic", "Order", "Repayment", "Penalty", "Pact",
            "Reroll", "Wand", "Initial", "Pact", "Phone", "Production", "Production", "Tax",
            "Refund", "Land Purchases", "Land Swindler", "Wand"
        };

        // ------------------------------------------------------------------
        // 設定
        // ------------------------------------------------------------------
        // 設定は、人が変える理由のあるものだけを出す（2.0.0 で 33 → 8 項目に減らした）。消したのは、グラフを
        // 統計窓の枠ではなく別のパネルに描いていたころの名残（パネルの位置・出す消すのキー・タブをキーで送る）と、
        // 内部の上限・寸法。下の定数はその既定の値
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _embedInStatsWindow;
        private ConfigEntry<int> _startMode;
        private ConfigEntry<float> _iconOutline;
        private ConfigEntry<string> _fontName;
#if DEV_TOOLS
        private ConfigEntry<string> _keyInjectSpec;
        private ConfigEntry<bool> _keepDataOnReload;
#endif

        /// <summary>棒1本の秒数（始まりの値。列が MaxBuckets を超えるたびに倍になる）</summary>
        private const float BucketSecondsInitial = 1f;
        private const int MaxBuckets = 1800;
        /// <summary>生の記録の上限（業務日誌のグラフの保存と、直近1分の速度に使う）。同じ秒の同じ出所はまとめて持つので、
        /// 30 分のプレイで 2〜3 万件。届くのは、よほど大きな工場だけ</summary>
        private const int MaxRawEvents = 1000000;
        private const int MaxColumns = 480;
        private const int TextSize = 14;

#if DEV_TOOLS
        private Hotkey _keyInject;
#endif

        /// <summary>F6 の読み直しで記録を持ち越すか。開発用で、配布版では持ち越さない</summary>
        private bool KeepDataOnReload
        {
            get
            {
#if DEV_TOOLS
                return _keepDataOnReload.Value;
#else
                return false;
#endif
            }
        }

        // ------------------------------------------------------------------
        // 状態
        // ------------------------------------------------------------------
        private GameStateManager _boundGame;

        // 記録係は1つではない。納品口（DeliveryDepositor）ごとに従の記録係があり、
        // 従で受けた分は**自分のイベントしか発火しない**（主へは無音で数だけ足す）。
        // 主だけに繋ぐと、その納品口を通った売却が丸ごと見えなくなる。
        // 本体の統計窓も同じように全部に繋いでいる（SubscribeDeliveryDepositors）。
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        private readonly HashSet<ResourceTagsRecorder> _subscribed = new HashSet<ResourceTagsRecorder>();

        /// <summary>
        /// 生まれたばかりの記録係。コンストラクタのポストフィックスから積まれる。
        /// 定期的に探しに行くのをやめるための仕掛け——
        /// FindObjectsByType は読み込み済みの全オブジェクトを walk するので、
        /// 巡回にすると周期的にフレームが落ちる。
        /// </summary>
        private static readonly List<ResourceTagsRecorder> NewRecorders = new List<ResourceTagsRecorder>();

        /// <summary>
        /// 本体に無い理由。土地の購入は理由を付けずに引かれるので「その他」に混ざる
        /// （LandPurchaseManager.TryPurchase が素の TryConsume を呼ぶ）。
        /// 購入している間だけ旗を立てて、こちらで区別する。
        /// </summary>
        private const int LandReason = 17;

        /// <summary>
        /// 本体に無い理由。契約「地面師」の現金は契約の効果（PactEffectIncome）として出所なしで入るので、
        /// 他の契約の収入と混ざる。効果を実行している間だけ旗を立てて、こちらで区別する。
        /// </summary>
        private const int LandCrimeReason = 18;

        /// <summary>土地の購入処理の中にいるか。記録は同じ呼び出しの中で流れてくる</summary>
        private static bool InLandPurchase;

        /// <summary>地面師の効果の中にいるか。ExecuteEffect は待たずに同じ呼び出しの中で現金を足す</summary>
        private static bool InLandCrime;

        /// <summary>
        /// 本体に無い理由。杖の契約（溶解液・ファイアーボール）の報酬は、壊す演出を待ってから
        /// 理由も出所も付けずに足される（HarvestableConverter → AddRecord）ので「その他」に混ざる。
        /// 待った後なので、効果の入口を挟む作りでは旗が間に合わない。
        /// 報酬に渡す TagCount は HarvestableConverter が1つずつ持ち回している実体なので、
        /// AddRecordCore に渡ってきた実体で見分け、そのとき持っている杖の契約 ID を出所にする。
        /// </summary>
        private const int WandIncomeReason = 19;

        /// <summary>いま記録している報酬が杖の契約のものなら、その契約 ID</summary>
        private static string InWandReward;

        /// <summary>杖の報酬に使われる TagCount の実体と、杖を持っている実行係（CommitSkillState のたびに掴み直す）</summary>
        private static TagCount WandRewardA, WandRewardB;
        private static WandSkillExecutor WandExecutor;
        private static FieldInfo WandConverterField, WandSourceField, ConverterRewardField, ConverterResourceField;

        private Harmony _harmony;
        private bool _patched;
        private float _nextRecorderScan;

        private readonly List<TagSeries> _series = new List<TagSeries>();   // 作った順（生ログの添字）
        private readonly List<TagSeries> _ordered = new List<TagSeries>();  // 表示順
        private readonly Dictionary<string, TagSeries> _seriesByTag = new Dictionary<string, TagSeries>(StringComparer.Ordinal);

        // 生ログ。読み直しをまたぐので、こちらの型ではなく素の型の並びで持つ（AttachStore を見よ）
        private List<float> _evT;
        private List<int> _evDelta;      // 符号つき
        private List<int> _evBalance;
        private List<int> _evTag;
        private List<int> _evReason;
        private List<int> _evSource;     // -1 = なし
        private List<int> _evPeriod;
        private List<string> _sourceIDs;
        private readonly Dictionary<string, int> _sourceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool _rawEventsTruncated;

        private float _bucketSeconds = 1f;
        private int _bucketCount;
        private double _elapsed;
        private double _runEndElapsed = -1.0;
        private bool _isResultShown;
        private float _nextResultPoll;
        private bool _hasRun;

        // ScriptEngine で読み直すとプラグインの実体ごと作り直されるので、
        // 記録は AppDomain に素の配列で退避して、次の実体が流し直す。
        // これをやらないと、見た目を1つ直すたびに走行の記録が消えて再現性が無くなる。
        private bool _replaying;
        private int _restoredGameId;
        private bool _seeded;

        private int _chartDrawnFrame = -100;
        private int _selectedTag;
        private int _mode;
        private bool _lastMinute;    // 統計窓が開いていないときの横軸の範囲
        private bool _expenseSide;   // 統計窓が開いていないときの収入／支出の別

        // 返済の区切り。WinCondition.CurrentProgress が1回ずつ増えるのをそのまま使う
        // 列は「返済で区切られた区間」。同時に複数返済したら1列にまとめる
        // （別々の列にすると中身の無い列が並ぶ）。
        private int _periodIndex;
        private int _repaidTotal;
        private int _closedColumns;
        private List<int> _periodRepaid;
        private int _targetProgress;

        // 次の返済のノルマ。要求がアイテムのときは GetTargetTag() が "None" を返すので数量を0にする
        private string _requiredTag;
        private int _requiredCount;
        private long _requiredCurrent;
        private List<double> _periodEnds;

        private readonly Dictionary<string, Sprite> _tagSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<string, FieldInfo> _statsFields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

        // 本体の統計窓に用意されている空き枠（"under development" と書かれている場所）
        // ゲームは OS のカーソルを消して自前の uGUI 画像で描いている（CursorViewSwitcher）。
        // IMGUI は Canvas より後に描かれるので、こちらのパネルがカーソルを覆ってしまう。
        // 覆った場合だけ、同じ絵を最前面に描き直す。
        private CursorViewSwitcher _cursorView;
        private GameObject _cursorChaser;
        private float _nextCursorProbe;
        private readonly List<Graphic> _cursorGraphics = new List<Graphic>();
        private Rect _lastDrawn;
        private bool _drewThisFrame;

        private CommonUIController _commonUI;
        private List<GameObject> _menuObjects;
        private float _nextMenuProbe;

        private StatsWindowController _statsWindow;
        private RectTransform _graphFrame;
        private float _nextWindowProbe;
        private float _nextFrameProbe;
        private bool _loggedFrameFound;


        private GUIStyle _text;
        private GUIStyle _title;
        private Font _font;
        private int _styleFontSize;

        private readonly HashSet<string> _hiddenTags = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _sourceSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<int, Texture2D> _silhouettes = new Dictionary<int, Texture2D>();
        private readonly string[] _reasonNames = new string[ChartRenderer.ReasonCount];
        private float _nextTaxFaceCache;
        private readonly Sprite[] _faces = new Sprite[FacePatrons.Length];
        private float _nextFaceProbe;
        private readonly List<Sprite> _silhouettePending = new List<Sprite>();
        private readonly ChartData _chart = new ChartData();
        private readonly ChartRenderer _renderer = new ChartRenderer();
        private ImguiPainter _painter;
        private readonly GUIContent _content = new GUIContent();
        private readonly Vector3[] _frameCorners = new Vector3[4];

        // ------------------------------------------------------------------
        // 起動
        // ------------------------------------------------------------------
        // 本体は 0.29.1 から、起動中に例外が出るとゲームごと起動を止める（エラー画面）。
        // 本体の更新で参照先が変わっても MOD だけ止まって起動は通るよう、全部を受け止める
        private void Awake()
        {
            try
            {
                Boot();
            }
            catch (Exception e)
            {
                Logger.LogWarning("[boot] " + PluginName + " " + PluginVersion
                    + " disabled (the game version may have changed): " + e);
                try { if (_harmony != null) { _harmony.UnpatchSelf(); } }
                catch (Exception e2) { Logger.LogWarning("[boot] unpatch failed: " + e2.Message); }
                enabled = false;
            }
        }

        private void Boot()
        {
            _enabled = Config.Bind("General", "Enabled", true, "");
            _embedInStatsWindow = Config.Bind("General", "EmbedInStatsWindow", true, "false = no graph in the Statistics screen");
            _startMode = Config.Bind("HUD", "StartMode", 0,
                "0 = balance, 1 = holdings, 2 = per repayment");
            _iconOutline = Config.Bind("HUD", "IconOutlineWidth", 0.05f,
                "share of the icon size, 0 = none");
            _fontName = Config.Bind("HUD", "FontName", "",
                "empty = Unity default. Yu Gothic UI");
#if DEV_TOOLS
            _keyInjectSpec = Config.Bind("Keys", "InjectSamples", "", "dev only. empty = off");
            _keepDataOnReload = Config.Bind("General", "KeepDataOnReload", false, "dev only");
#endif

#if DEV_TOOLS
            _keyInject = Hotkey.Parse(_keyInjectSpec.Value);
#endif

            _bucketSeconds = BucketSecondsInitial;
            _painter = new ImguiPainter(this);
            _mode = Mathf.Clamp(_startMode.Value, 0, ChartRenderer.ModeCount - 1);
            PatchRecorderConstructor();
            AttachStore();
            // 持ち越しは開発用（F6 の読み直し）。本体の型が変わるとメソッドごと読めなくなる
            // （0.29.1 で TagRecordedEvent の引数が増え、ここで起動が止まった）ので、
            // 失敗しても持ち越しを諦めるだけにする
            try { RestoreState(); }
            catch (Exception e) { Logger.LogWarning("[boot] could not restore the previous data: " + e.Message); }

            // 業務日誌（RunLog.cs）
            BootLog();

            // ゲームの版も出す。不具合の報告にログを添えてもらえば、
            // どの版で起きたのかが一目で分かる
            string gameVersion;
            try { gameVersion = Application.version; }
            catch (Exception) { gameVersion = "?"; }

            Logger.LogInfo("[boot] " + PluginName + " " + PluginVersion
                + "  (game " + gameVersion + ")");
        }

        private void OnDestroy()
        {
            DestroyLog();
            SyncScalars();
            Unbind();

            if (_harmony != null)
            {
                try { _harmony.UnpatchSelf(); }
                catch (Exception) { }
                _harmony = null;
            }
            NewRecorders.Clear();
            InLandPurchase = false;
            InLandCrime = false;
            InWandReward = null;
            WandRewardA = null;
            WandRewardB = null;
            WandExecutor = null;
        }

        // ------------------------------------------------------------------
        // 読み直しをまたぐ記録の持ち越し（開発用）
        // ------------------------------------------------------------------
        private const string StateKey = "lwf.economygraph.store.v4";
        private const int StoreVersion = 4;
        private const int StoreSlots = 14;

        // 目盛りの置き場所（_scalars の添字）
        private const int SVersion = 0, SGameID = 1, SBucketSeconds = 2, SBucketCount = 3,
                          SElapsed = 4, SPeriodIndex = 5, SRepaidTotal = 6, SClosedColumns = 7,
                          SHasRun = 8, STruncated = 9;

        private double[] _scalars;
        private List<string> _storeTagIDs;
        private List<long> _storeInitial;
        private List<List<long>> _storeBalances;

        /// <summary>
        /// 記録の置き場を AppDomain から受け取る（無ければ作る）。
        ///
        /// ScriptEngine の読み直しではプラグインの実体ごと作り直される。
        /// 壊れる直前に写す（OnDestroy で退避）作りは使えない——
        /// Unity の Destroy はフレーム末尾まで遅れるので、写されるのは
        /// 新しい実体が読み終わったあとになり、必ず空を読むことになる。
        ///
        /// なので写さない。List&lt;int&gt; のような素の型は読み直しをまたいでも同じ型なので、
        /// 置き場そのものを AppDomain に置いて、記録は最初からそこへ書く。
        /// </summary>
        private void AttachStore()
        {
            object[] store = null;
            if (KeepDataOnReload)
            {
                try { store = AppDomain.CurrentDomain.GetData(StateKey) as object[]; }
                catch (Exception) { store = null; }
            }

            double[] scalars = (store != null && store.Length == StoreSlots) ? store[0] as double[] : null;
            if (scalars == null || scalars.Length < 10 || (int)scalars[SVersion] != StoreVersion)
            {
                scalars = new double[10];
                scalars[SVersion] = StoreVersion;

                store = new object[StoreSlots];
                store[0] = scalars;
                store[1] = new List<double>();      // 列の終わり（秒）
                store[2] = new List<int>();         // 列ごとの返済回数
                store[3] = new List<string>();      // タグ
                store[4] = new List<long>();        // 開始時の所持
                store[5] = new List<List<long>>();  // 残高の列
                store[6] = new List<string>();      // 出所
                store[7] = new List<float>();       // 生ログ：時刻
                for (int i = 8; i < StoreSlots; i++) { store[i] = new List<int>(); }

                if (KeepDataOnReload)
                {
                    try { AppDomain.CurrentDomain.SetData(StateKey, store); }
                    catch (Exception) { }
                }
            }

            _scalars = scalars;
            _periodEnds = (List<double>)store[1];
            _periodRepaid = (List<int>)store[2];
            _storeTagIDs = (List<string>)store[3];
            _storeInitial = (List<long>)store[4];
            _storeBalances = (List<List<long>>)store[5];
            _sourceIDs = (List<string>)store[6];
            _evT = (List<float>)store[7];
            _evDelta = (List<int>)store[8];
            _evBalance = (List<int>)store[9];
            _evTag = (List<int>)store[10];
            _evReason = (List<int>)store[11];
            _evSource = (List<int>)store[12];
            _evPeriod = (List<int>)store[13];
        }

        /// <summary>目盛りを置き場へ書き戻す。毎フレーム呼んでよい（確保は起きない）。</summary>
        private void SyncScalars()
        {
            if (_scalars == null) { return; }

            _scalars[SGameID] = (_boundGame != null) ? _boundGame.GetInstanceID() : 0;
            _scalars[SBucketSeconds] = _bucketSeconds;
            _scalars[SBucketCount] = _bucketCount;
            _scalars[SElapsed] = _elapsed;
            _scalars[SPeriodIndex] = _periodIndex;
            _scalars[SRepaidTotal] = _repaidTotal;
            _scalars[SClosedColumns] = _closedColumns;
            _scalars[SHasRun] = _hasRun ? 1.0 : 0.0;
            _scalars[STruncated] = _rawEventsTruncated ? 1.0 : 0.0;
        }

        /// <summary>置き場のタグ列と対応づける。残高の列は置き場のものをそのまま使う。</summary>
        private void RegisterStoreTag(TagSeries s)
        {
            if (_storeTagIDs == null) { return; }

            if (s.Index < _storeTagIDs.Count)
            {
                if (string.Equals(_storeTagIDs[s.Index], s.TagID, StringComparison.Ordinal))
                {
                    s.Balances = _storeBalances[s.Index];
                    return;
                }

                // 食い違ったら、そこから先を捨てて作り直す（起きないはずだが黙って進めない）
                _storeTagIDs.RemoveRange(s.Index, _storeTagIDs.Count - s.Index);
                _storeInitial.RemoveRange(s.Index, _storeInitial.Count - s.Index);
                _storeBalances.RemoveRange(s.Index, _storeBalances.Count - s.Index);
            }

            _storeTagIDs.Add(s.TagID);
            _storeInitial.Add(s.InitialBalance);
            _storeBalances.Add(s.Balances);
        }

        /// <summary>
        /// 置き場に残っている記録を組み直す。読み直しの直後だけ効く。
        ///
        /// 残高は置き場の列をそのまま使うので写さない。
        /// 内訳・台帳・列ごとの集計は生ログを流し直して組み上げる。
        /// </summary>
        private void RestoreState()
        {
            if (_scalars == null || _scalars[SHasRun] < 0.5) { return; }

            try
            {
                _restoredGameId = (int)_scalars[SGameID];
                _bucketSeconds = (float)_scalars[SBucketSeconds];
                _bucketCount = (int)_scalars[SBucketCount];
                _repaidTotal = (int)_scalars[SRepaidTotal];
                _closedColumns = (int)_scalars[SClosedColumns];
                // 2026-11-18 より前の記録は、最後の返済が回数に入っていない（CloseColumn の注記）
                if (_repaidTotal < _closedColumns) { _repaidTotal = _closedColumns; }
                _rawEventsTruncated = _scalars[STruncated] >= 0.5;

                int periodIndex = (int)_scalars[SPeriodIndex];
                double elapsed = _scalars[SElapsed];

                _series.Clear();
                _ordered.Clear();
                _seriesByTag.Clear();
                for (int i = 0; i < _storeTagIDs.Count; i++)
                {
                    TagSeries s = GetOrCreateSeries(_storeTagIDs[i]);
                    s.InitialBalance = _storeInitial[i];
                    s.HeldUnattributed = _storeInitial[i];   // 開始時の所持は出所が辿れない
                    if (s.Balances.Count > 0) { s.Balance = s.Balances[s.Balances.Count - 1]; }
                }

                _sourceIndex.Clear();
                for (int i = 0; i < _sourceIDs.Count; i++) { _sourceIndex[_sourceIDs[i]] = i; }

                _replaying = true;
                int events = _evT.Count;
                for (int i = 0; i < events; i++)
                {
                    int tag = _evTag[i];
                    if (tag < 0 || tag >= _storeTagIDs.Count) { continue; }

                    _elapsed = _evT[i];
                    _periodIndex = _evPeriod[i];

                    // 返済かどうかの判定はノルマのタグと突き合わせている。
                    // 生ログにノルマは残っていないので、そのイベントのタグを当てておく
                    // （列を閉じるのは流し直しでは止めてあるので、内訳の記録だけが効く）
                    _requiredTag = _storeTagIDs[tag];

                    string sourceID = (_evSource[i] >= 0 && _evSource[i] < _sourceIDs.Count)
                        ? _sourceIDs[_evSource[i]] : null;

                    // 土地購入・地面師はこちらで足した理由番号なので、旗ではなくログの値をそのまま使う
                    InLandPurchase = false;
                    InLandCrime = false;
                    InWandReward = null;
                    OnTagRecorded(new TagRecordedEvent(_storeTagIDs[tag], Math.Abs(_evDelta[i]), _evBalance[i],
                        _evDelta[i] > 0, (StatsCashReason)_evReason[i], sourceID));
                }
                _replaying = false;
                _requiredTag = null;

                _periodIndex = periodIndex;
                _elapsed = elapsed;

                // 山は残高の列から取り直す
                for (int i = 0; i < _series.Count; i++)
                {
                    TagSeries s = _series[i];
                    s.PeakBalance = s.InitialBalance;
                    s.PeakAt = 0.0;
                    for (int b = 0; b < s.Balances.Count; b++)
                    {
                        if (s.Balances[b] > s.PeakBalance)
                        {
                            s.PeakBalance = s.Balances[b];
                            s.PeakAt = b * (double)_bucketSeconds;
                        }
                    }
                }

                _hasRun = true;
                _seeded = true;   // 引き継いだのだから、開始時の所持は取り直さない
                Logger.LogInfo("[state] carried over " + events + " records, " + _repaidTotal + " repayments");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[state] cannot carry over the records; recording from scratch: " + e);
                _replaying = false;
                _requiredTag = null;
                ResetRun();
                _restoredGameId = 0;
            }
        }

        /// <summary>
        /// 記録係が作られた瞬間に掴めるようにする。
        /// 納品口（ポータル）ごとに記録係があり、走行中にも増えるが、
        /// 探しに行く（FindObjectsByType）と周期的に重くなる。
        /// 当てられなければ従来どおり定期的に探す方へ落ちる。
        /// </summary>
        private void PatchRecorderConstructor()
        {
            try
            {
                ConstructorInfo ctor = typeof(ResourceTagsRecorder).GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(DeliveryEffectSpawner), typeof(bool) },
                    null);
                if (ctor == null)
                {
                    Logger.LogWarning("[hook] ResourceTagsRecorder constructor not found; falling back to polling");
                    return;
                }

                MethodInfo postfix = typeof(EconomyGraphPlugin).GetMethod("OnRecorderCreated",
                    BindingFlags.Static | BindingFlags.NonPublic);

                _harmony = new Harmony(PluginGuid);
                _harmony.Patch(ctor, null, new HarmonyMethod(postfix));
                _patched = true;
            }
            catch (Exception e)
            {
                Logger.LogWarning("[hook] cannot patch the recorder; falling back to polling: " + e.Message);
                _patched = false;
            }

            PatchLandPurchase();
            PatchLandCrime();
            PatchWandReward();
        }

        /// <summary>
        /// 杖の報酬を見分ける。杖を持ち替えるたび（CommitSkillState）に報酬用の TagCount を掴み、
        /// AddRecordCore にその実体が渡ってきた間だけ契約 ID を立てる。
        /// AddRecord は1行で AddRecordCore を呼ぶだけなので、インライン化されないよう中身の側を挟む。
        /// 外れても杖の報酬が「その他」に混ざるだけなので、警告だけ出して続ける。
        /// </summary>
        private void PatchWandReward()
        {
            try
            {
                BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                WandConverterField = typeof(WandSkillExecutor).GetField("_harvestableConverter", inst);
                WandSourceField = typeof(WandSkillExecutor).GetField("_activePresentationSource", inst);
                ConverterRewardField = typeof(HarvestableConverter).GetField("_rewardTag", inst);
                ConverterResourceField = typeof(HarvestableConverter).GetField("_resourceTag", inst);
                MethodInfo commit = typeof(WandSkillExecutor).GetMethod("CommitSkillState", inst);
                MethodInfo core = typeof(ResourceTagsRecorder).GetMethod("AddRecordCore", inst);
                if (WandConverterField == null || WandSourceField == null || ConverterRewardField == null
                    || ConverterResourceField == null || commit == null || core == null)
                {
                    Logger.LogWarning("[hook] wand rewards not found; wand income stays under Other");
                    return;
                }

                if (_harmony == null) { _harmony = new Harmony(PluginGuid); }
                _harmony.Patch(commit, postfix: new HarmonyMethod(typeof(EconomyGraphPlugin).GetMethod("WandCommitted",
                    BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony.Patch(core,
                    prefix: new HarmonyMethod(typeof(EconomyGraphPlugin).GetMethod("RecordCoreBegin", BindingFlags.Static | BindingFlags.NonPublic)),
                    finalizer: new HarmonyMethod(typeof(EconomyGraphPlugin).GetMethod("RecordCoreEnd", BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                Logger.LogWarning("[hook] cannot patch wand rewards; wand income stays under Other: " + e.Message);
            }
        }

        /// <summary>
        /// 地面師の効果を挟む。旗の立て方は土地購入と同じ。
        /// 外れても他の契約の収入に混ざるだけなので、警告だけ出して続ける。
        /// </summary>
        private void PatchLandCrime()
        {
            try
            {
                MethodInfo target = typeof(CashByLandCrimePact).GetMethod("ExecuteEffect",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null)
                {
                    Logger.LogWarning("[hook] CashByLandCrimePact.ExecuteEffect not found; Land Swindler stays under Pact");
                    return;
                }

                MethodInfo begin = typeof(EconomyGraphPlugin).GetMethod("LandCrimeBegin",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo end = typeof(EconomyGraphPlugin).GetMethod("LandCrimeEnd",
                    BindingFlags.Static | BindingFlags.NonPublic);

                if (_harmony == null) { _harmony = new Harmony(PluginGuid); }
                _harmony.Patch(target, prefix: new HarmonyMethod(begin), finalizer: new HarmonyMethod(end));
            }
            catch (Exception e)
            {
                Logger.LogWarning("[hook] cannot patch Land Swindler; it stays under Pact: " + e.Message);
            }
        }

        /// <summary>
        /// 土地の購入を挟む。本体は理由を付けずに資源を引くので、
        /// そのままだと「その他」に混ざって何に使ったか分からなくなる。
        /// 記録は TryConsume から同じ呼び出しの中で流れてくるので、旗を立てるだけで足りる。
        /// </summary>
        private void PatchLandPurchase()
        {
            try
            {
                MethodInfo target = typeof(LandPurchaseManager).GetMethod("TryPurchase",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null)
                {
                    Logger.LogWarning("[hook] LandPurchaseManager.TryPurchase not found; land purchases stay under Other");
                    return;
                }

                MethodInfo begin = typeof(EconomyGraphPlugin).GetMethod("LandPurchaseBegin",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo end = typeof(EconomyGraphPlugin).GetMethod("LandPurchaseEnd",
                    BindingFlags.Static | BindingFlags.NonPublic);

                if (_harmony == null) { _harmony = new Harmony(PluginGuid); }

                // 抜けは finalizer で戻す（例外で抜けても旗が立ちっぱなしにならない）
                _harmony.Patch(target, prefix: new HarmonyMethod(begin), finalizer: new HarmonyMethod(end));
            }
            catch (Exception e)
            {
                Logger.LogWarning("[hook] cannot patch land purchases; they stay under Other: " + e.Message);
            }
        }

        private static void OnRecorderCreated(ResourceTagsRecorder __instance)
        {
            if (__instance != null) { NewRecorders.Add(__instance); }
        }

        private static void LandPurchaseBegin()
        {
            InLandPurchase = true;
        }

        private static void LandPurchaseEnd()
        {
            InLandPurchase = false;
        }

        private static void LandCrimeBegin()
        {
            InLandCrime = true;
        }

        private static void LandCrimeEnd()
        {
            InLandCrime = false;
        }

        private static void WandCommitted(WandSkillExecutor __instance)
        {
            try
            {
                object converter = WandConverterField.GetValue(__instance);
                WandExecutor = __instance;
                WandRewardA = converter != null ? ConverterRewardField.GetValue(converter) as TagCount : null;
                WandRewardB = converter != null ? ConverterResourceField.GetValue(converter) as TagCount : null;
            }
            catch (Exception) { WandRewardA = null; WandRewardB = null; }
        }

        /// <summary>記録のたびに呼ばれるので、実体の比較だけで抜ける</summary>
        private static void RecordCoreBegin(TagCount tagCount)
        {
            if (tagCount == null || WandExecutor == null) { return; }
            if (!ReferenceEquals(tagCount, WandRewardA) && !ReferenceEquals(tagCount, WandRewardB)) { return; }
            try
            {
                WandSkillPresentationSource source = WandSourceField.GetValue(WandExecutor) as WandSkillPresentationSource;
                InWandReward = source != null ? source.PactID : null;
            }
            catch (Exception) { InWandReward = null; }
        }

        private static void RecordCoreEnd()
        {
            InWandReward = null;
        }

        // ------------------------------------------------------------------
        // 毎フレーム
        // ------------------------------------------------------------------
        private void Update()
        {
            if (_enabled == null || !_enabled.Value || _brokenDown) { return; }

            TrackLog();
            TrackTaxRecorder();

            try
            {
                TrackGame();
                TrackStatsWindow();
                BuildPendingSilhouettes();
                ResolveFaces();
                HandleInput();
            }
            catch (Exception e)
            {
                ReportBreakdown("record", e);
            }
        }

        /// <summary>
        /// 例外が続いたら自分を止める。
        ///
        /// 毎フレーム走る場所なので、放っておくとログが溢れるし、
        /// OnGUI が途中で落ちると IMGUI の状態が壊れて他のMODの表示まで巻き込む。
        /// 本体の更新で内部の名前が変わったときに、素のゲームへ静かに戻れるようにしておく。
        /// </summary>
        private void ReportBreakdown(string where, Exception e)
        {
            _failures++;
            if (_failures <= 3)
            {
                Logger.LogError("[" + where + "] exception: " + e);
            }

            if (_failures >= FailureLimit && !_brokenDown)
            {
                _brokenDown = true;
                Logger.LogError("[stop] " + FailureLimit + " exceptions in a row; the mod is now off."
                    + " Set Enabled = false in the config to silence this.");
            }
        }

        private const int FailureLimit = 20;
        private int _failures;
        private bool _brokenDown;

        /// <summary>
        /// ランの開始・終了を見張る。GameStateManager はランごとに作り直されるので、
        /// インスタンスが変わったら新しいランとみなして記録を捨てる。
        /// </summary>
        private void TrackGame()
        {
            GameStateManager gsm = GameStateManager.Instance;

            if (!ReferenceEquals(gsm, _boundGame))
            {
                // Run を離れた（タイトルへ戻る・次の Run）。記録を消す前に業務日誌へ残す
                SaveRunGraphOnce("left the run");
                Unbind();
                _boundGame = gsm;
                if (gsm != null)
                {
                    // 読み直しで戻ってきた場合は同じ走行なので消さない
                    if (gsm.GetInstanceID() != _restoredGameId) { ResetRun(); MarkRunStart(); }
                    _restoredGameId = 0;
                    _hasRun = true;
                }
            }

            if (_boundGame == null) { return; }

            if (_boundGame.IsSetRecorder)
            {
                // 生成を捕まえられているなら、積まれたものを拾うだけでよい。
                // 捕まえられていないときだけ、従来どおり定期的に探しに行く
                DrainNewRecorders();

                bool needScan = !_patched || _subscribed.Count == 0;
                if (needScan && Time.unscaledTime >= _nextRecorderScan)
                {
                    _nextRecorderScan = Time.unscaledTime + 10f;
                    SubscribeRecorders();
                }
            }

            SampleBalances();
            SyncScalars();

            try
            {
                _elapsed = _boundGame.GetElapsedGameplaySeconds();
            }
            catch (Exception) { }

            // 増減が無い間もグラフを伸ばす。
            int wanted = (int)(_elapsed / _bucketSeconds) + 1;
            if (wanted > _bucketCount)
            {
                _bucketCount = wanted;
                while (_bucketCount > MaxBuckets && MaxBuckets >= 16) { Downsample(); }
            }

            TrackRepayment();
            TickMapTimed();
            // 業務日誌の課税通知書に出す顔（逮捕・損害賠償の発動時の表情）は、Run 中にしか読み込まれていない。
            // ときどき拾って覚えておく（LogTaxView.cs）
            if (Time.unscaledTime >= _nextTaxFaceCache)
            {
                _nextTaxFaceCache = Time.unscaledTime + 20f;
                LogView.CacheTaxFaces();
            }
            TrackRequirement();
            TrackRunEnd();
        }

        /// <summary>
        /// 次の返済のノルマと、その達成度。
        /// 進行中の期の列はこの割合で高さを決める（100%に伸ばさない）。
        /// 固定費用のデバフで現金が負になることがあるので、負のまま渡す。
        /// </summary>
        private void TrackRequirement()
        {
            _requiredTag = null;
            _requiredCount = 0;
            _requiredCurrent = 0;

            try
            {
                WinCondition condition = _boundGame.GetWinCondition();
                if (condition == null) { return; }

                string tagID = condition.GetTargetTag();
                if (string.IsNullOrEmpty(tagID) || tagID == "None") { return; }

                _requiredTag = tagID;
                _requiredCount = _boundGame.GetTargetCount();

                ResourceTagsRecorder recorder = _boundGame.GetTagsRecorder();
                if (recorder != null) { _requiredCurrent = recorder.GetCount(tagID); }
            }
            catch (Exception)
            {
                _requiredTag = null;
                _requiredCount = 0;
            }
        }

        /// <summary>
        /// 返済の区切り。WinCondition.CurrentProgress は返済1回ごとに1つ増える
        /// （AddProgress）。増えた瞬間の経過秒を区切りとして覚えるだけ。
        /// </summary>
        private void TrackRepayment()
        {
            int progress = 0;
            try
            {
                progress = _boundGame.GetCurrentProgress();
                _targetProgress = _boundGame.GetTargetProgress();
            }
            catch (Exception) { return; }

            if (progress <= _repaidTotal) { return; }
            _repaidTotal = progress;

            // 返済のイベントで閉じられなかったぶんを埋める。
            // 要求がアイテムの回はタグの増減が出ないので、イベントでは気づけない
            while (_closedColumns < progress)
            {
                CloseColumn();
                Logger.LogInfo("[run] repayment " + _closedColumns + " (no event)  " + FormatTime(_elapsed));
            }
        }

        /// <summary>返済1回ぶんの列を閉じて、次の列へ進む。</summary>
        private void CloseColumn()
        {
            _closedColumns++;
            // 返済した回数は本体の値を毎フレーム見て追っている（TrackRepayment）が、最後の返済ではその場で Run が
            // 終わり、次のフレームが来ない。見出しが「29/30」のまま残るので、列を閉じた数にも合わせる
            if (_repaidTotal < _closedColumns) { _repaidTotal = _closedColumns; }
            _periodEnds.Add(_elapsed);
            _periodRepaid.Add(_closedColumns);
            _periodIndex++;
            // 業務日誌：返済のたびに工場の配置を控える（MapLog.cs）
            CaptureMap();
        }

        /// <summary>
        /// 統計窓と、その収支タブにある空き枠を追いかける。
        /// タブ・タグの切替で作り直されるので、消えていたら探し直す。
        /// 探すのは窓が開いている間だけ、しかも間隔を空けている。
        /// </summary>
        private void TrackStatsWindow()
        {
            if (!_embedInStatsWindow.Value) { _graphFrame = null; return; }

            if (_statsWindow == null)
            {
                if (Time.unscaledTime < _nextWindowProbe) { return; }
                _nextWindowProbe = Time.unscaledTime + 1f;
                try
                {
                    StatsWindowController[] found = UnityEngine.Object.FindObjectsByType<StatsWindowController>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None);
                    if (found.Length > 0) { _statsWindow = found[0]; }
                }
                catch (Exception) { }
                if (_statsWindow == null) { return; }
            }

            if (!_statsWindow.IsVisible)
            {
                _graphFrame = null;
                _loggedFrameFound = false;
                return;
            }

            // 掴んでいる枠が隠れたら掴み直す。
            // 収入ページと支出ページは同じ名前の枠を持ったまま同時に存在していて、
            // タブを切り替えると表示側が入れ替わる。掴みっぱなしだと
            // 隠れたほうを持ち続けて、見えているページには何も描かれない。
            if (_graphFrame != null && !_graphFrame.gameObject.activeInHierarchy) { _graphFrame = null; }
            if (_graphFrame != null) { return; }

            // 収支タブ以外を開いていると枠は存在しない。毎フレーム部分木を歩くと無駄なので少し待つ
            if (Time.unscaledTime < _nextFrameProbe) { return; }
            _nextFrameProbe = Time.unscaledTime + 0.2f;

            Transform frame = FindActiveChildRecursive(_statsWindow.transform, "StatsBalanceGraphFrame");
            if (frame == null) { return; }

            _graphFrame = frame as RectTransform;
            if (_graphFrame == null) { return; }

            // 枠の中の "under development" は退けておく（作り直されるたびに出てくる）
            Transform placeholder = FindChildRecursive(frame, "StatsBalanceGraphUnderDevelopment");
            if (placeholder != null && placeholder.gameObject.activeSelf)
            {
                placeholder.gameObject.SetActive(false);
            }

            // 枠はタブ・タグの切替のたびに作り直されるので、記録は窓を開くたび1回だけにする
            if (!_loggedFrameFound)
            {
                _loggedFrameFound = true;
                Logger.LogInfo("[stats] drawing in the balance graph frame");
                _faceRetry = true;
            }
        }

        private static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null) { return null; }
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (string.Equals(child.name, name, StringComparison.Ordinal)) { return child; }
                Transform found = FindChildRecursive(child, name);
                if (found != null) { return found; }
            }
            return null;
        }

        /// <summary>
        /// 同じ名前の子が複数あるとき、**表示されているほう**を返す。
        /// 収入ページと支出ページが同じ名前の枠を同時に持っているため、
        /// 名前だけで探すと隠れているほうを掴んでしまう。
        /// </summary>
        private static Transform FindActiveChildRecursive(Transform root, string name)
        {
            if (root == null) { return null; }
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (string.Equals(child.name, name, StringComparison.Ordinal)
                    && child.gameObject.activeInHierarchy)
                {
                    return child;
                }
                Transform found = FindActiveChildRecursive(child, name);
                if (found != null) { return found; }
            }
            return null;
        }

        /// <summary>
        /// ランの終わりとリザルト画面の検出。
        ///
        /// GameStateManager.IsGameOver は GameSequence.End、つまり
        /// リザルトの「オフィスへ戻る」で ExitGame() が呼ばれた後にしか立たない。
        /// 一方 IsEndingGame() は Finalizing（清算の開始）から true になるので、
        /// これを「ランの終わり」に使い、パネルを出すのは
        /// リザルト画面（ResultUIManager）が実際に出てからにする。
        /// 探索は終盤に入ってからの毎秒1回だけなので負荷にならない。
        /// </summary>
        private void TrackRunEnd()
        {
            bool ending = false;
            try { ending = _boundGame.IsEndingGame(); }
            catch (Exception) { }
            if (!ending) { return; }

            if (_runEndElapsed < 0.0)
            {
                _runEndElapsed = _elapsed;
                Logger.LogInfo("[run] end at " + FormatTime(_elapsed)
                    + ", " + TotalRecordCount() + " records");
            }

            if (_isResultShown) { return; }
            if (Time.unscaledTime < _nextResultPoll) { return; }
            _nextResultPoll = Time.unscaledTime + 1f;

            if (!IsResultWindowUp()) { return; }

            _isResultShown = true;
            SaveRunGraphOnce("result");
            Logger.LogInfo("[run] result window is up");
        }

        private static bool IsResultWindowUp()
        {
            try
            {
                ResultUIManager[] found = UnityEngine.Object.FindObjectsByType<ResultUIManager>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] != null && found[i].gameObject.activeInHierarchy) { return true; }
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>コンストラクタで積まれた記録係を拾って繋ぐ。探索は要らない。</summary>
        private void DrainNewRecorders()
        {
            if (NewRecorders.Count == 0) { return; }

            for (int i = 0; i < NewRecorders.Count; i++)
            {
                ResourceTagsRecorder recorder = NewRecorders[i];
                if (recorder == null || !_subscribed.Add(recorder)) { continue; }

                try
                {
                    Subscribe(recorder);
                }
                catch (Exception) { }
            }
            NewRecorders.Clear();
        }

        /// <summary>
        /// 記録係に繋ぐ。従（納品口ごと）の記録係から来た**支出は捨てる**。
        ///
        /// 従の記録係は、受けた分を主へ黙って足す（AddRecordWithoutNotice）が、
        /// 引くときは自分の分しか減らさない（TryConsumeInternal）。
        /// 納品口の依頼が払いを受け取ると（TributeDemand → OrderExpense）従だけが減り、
        /// 主の残高はそのまま残る。それを数えると、払っていない額が支出に積まれる
        /// （激流コンベアの納品口に現金10を入れて返済すると、返済10と注文10の両方が出る。
        /// 本体の統計窓も同じ数え方をしている）。
        /// </summary>
        private void Subscribe(ResourceTagsRecorder recorder)
        {
            _subscriptions.Add(recorder.OnTagRecorded.Subscribe(new Action<TagRecordedEvent>(delegate (TagRecordedEvent ev)
            {
                if (!ev.isAdd && !IsMainRecorder(recorder)) { return; }
                OnTagRecorded(ev);
            })));
        }

        private bool IsMainRecorder(ResourceTagsRecorder recorder)
        {
            ResourceTagsRecorder main = null;
            try { main = _boundGame != null ? _boundGame.GetTagsRecorder() : null; }
            catch (Exception) { main = null; }
            // 主が分からないうちは捨てない（従と決めつけて本物の支出を落とさない）
            return main == null || ReferenceEquals(main, recorder);
        }

        /// <summary>
        /// 主の記録係と、納品口ごとの従の記録係すべてに繋ぐ。
        /// 生成を捕まえられないときの保険。繋ぎ済みのものは飛ばす。
        /// </summary>
        private void SubscribeRecorders()
        {
            try
            {
                bool first = _subscribed.Count == 0;

                ResourceTagsRecorder main = _boundGame.GetTagsRecorder();
                if (main != null && _subscribed.Add(main))
                {
                    Subscribe(main);
                }

                // Include は読み込み済みの全オブジェクトを walk するので重い。
                // 定期的に回す用途では Exclude（有効なものだけ）で足りる——
                // 無効な納品口は納品しないし、有効になれば次の巡回で拾う
                DeliveryDepositor[] depositors = UnityEngine.Object.FindObjectsByType<DeliveryDepositor>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                for (int i = 0; i < depositors.Length; i++)
                {
                    if (depositors[i] == null) { continue; }

                    ResourceTagsRecorder recorder = depositors[i].GetRecorder();
                    if (recorder == null || !_subscribed.Add(recorder)) { continue; }

                    Subscribe(recorder);
                }

                if (first && main != null)
                {
                    SeedInitialBalances(main);
                    Logger.LogInfo("[run] recording, " + _subscribed.Count + " recorders");
                }
            }
            catch (Exception e)
            {
                Logger.LogError("[run] cannot subscribe to the recorders: " + e);
            }
        }

        /// <summary>
        /// 開始時の所持分は購読より前に記録が済んでいる。
        /// 残高だけ初期値として置き、収支の合計には数えない。
        ///
        /// **走行の頭でしかやってはいけない。** 読み直し（F6）でも記録係に繋ぎ直すので、
        /// 素直に呼ぶと走行の途中の残高をバケット0に書き込んでしまい、
        /// グラフの左端に現在値の縦線が立つ。開始時の所持も同じ額で上書きされる。
        /// </summary>
        private void SeedInitialBalances(ResourceTagsRecorder main)
        {
            if (_seeded) { return; }
            _seeded = true;

            for (int i = 0; i < ChartRenderer.KnownTagIDs.Length; i++)
            {
                int have = 0;
                try { have = main.GetCount(ChartRenderer.KnownTagIDs[i]); }
                catch (Exception) { continue; }
                if (have == 0) { continue; }

                TagSeries s = GetOrCreateSeries(ChartRenderer.KnownTagIDs[i]);
                s.InitialBalance = have;
                if (_storeInitial != null && s.Index < _storeInitial.Count) { _storeInitial[s.Index] = have; }
                s.Balance = have;
                s.HeldUnattributed = have;   // 開始時の所持は出所が辿れない
                s.PeakBalance = have;
                s.PeakAt = 0.0;
                EnsureBucket(s, 0);
                s.Balances[0] = have;
            }
        }

        /// <summary>
        /// 残高は毎フレーム主の記録係から取る。
        ///
        /// イベントの newTotal は「そのイベントを出した記録係の手持ち」で、
        /// 従の記録係では全体の残高にならない。さらに従が主へ足すときは無音なので、
        /// イベントだけ見ていると主の残高が変わったことに気づけない。
        /// </summary>
        private void SampleBalances()
        {
            ResourceTagsRecorder main = null;
            try { main = _boundGame.GetTagsRecorder(); }
            catch (Exception) { return; }
            if (main == null) { return; }

            int bucket = (int)(_elapsed / _bucketSeconds);
            if (bucket < 0) { bucket = 0; }

            for (int i = 0; i < ChartRenderer.KnownTagIDs.Length; i++)
            {
                string tagID = ChartRenderer.KnownTagIDs[i];

                int have;
                try { have = main.GetCount(tagID); }
                catch (Exception) { continue; }

                TagSeries s;
                if (!_seriesByTag.TryGetValue(tagID, out s))
                {
                    // 持っていない資源は、最初に持ったときに列を作る（帯にアイコンが並ぶのもそのとき）。
                    // 現金だけは 0 でも作る。始まりは現金 0 なので、最初の納品まで帯に現金のアイコンが無く、
                    // 壊れているように見えた
                    if (have == 0 && tagID != "Cash") { continue; }
                    s = GetOrCreateSeries(tagID);
                }

                EnsureBucket(s, bucket);
                s.Balance = have;
                s.Balances[bucket] = have;

                if (have > s.PeakBalance)
                {
                    s.PeakBalance = have;
                    s.PeakAt = _elapsed;
                }
            }
        }

        private void Unbind()
        {
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                try { _subscriptions[i].Dispose(); }
                catch (Exception) { }
            }
            _subscriptions.Clear();
            _subscribed.Clear();
            _nextRecorderScan = 0f;
            _boundGame = null;
        }

        private void ResetRun()
        {
            _series.Clear();
            _ordered.Clear();
            _seriesByTag.Clear();
            _evT.Clear();
            _evDelta.Clear();
            _evBalance.Clear();
            _evTag.Clear();
            _evReason.Clear();
            _evSource.Clear();
            _evPeriod.Clear();
            _sourceIDs.Clear();
            _sourceIndex.Clear();
            _mergeSecond = -1;
            _mergeIndex.Clear();
            _storeTagIDs.Clear();
            _storeInitial.Clear();
            _storeBalances.Clear();
            if (_scalars != null)
            {
                for (int i = 1; i < _scalars.Length; i++) { _scalars[i] = 0.0; }
            }
            _rawEventsTruncated = false;
            _periodEnds.Clear();
            _periodRepaid.Clear();
            _repaidTotal = 0;
            _closedColumns = 0;
            _hiddenTags.Clear();
            _requiredTag = null;
            _requiredCount = 0;
            _requiredCurrent = 0;
            _periodIndex = 0;
            _targetProgress = 0;
            _bucketSeconds = BucketSecondsInitial;
            _bucketCount = 0;
            _elapsed = 0.0;
            _seeded = false;
            _runEndElapsed = -1.0;
            _isResultShown = false;
            _nextResultPoll = 0f;
            _selectedTag = 0;
            ResetMapShots();
            _pactCards.Clear();
            ResetCraftCounts();
        }

        // ------------------------------------------------------------------
        // 記録
        // ------------------------------------------------------------------
        private void OnTagRecorded(TagRecordedEvent ev)
        {
            if (!_enabled.Value) { return; }
            if (string.IsNullOrEmpty(ev.tagID) || ev.tagID == "None") { return; }
            if (ev.delta <= 0) { return; }

            // 収支タブに出る8種だけを見る。スタンプやローラーは本体の統計窓にも出ないので拾わない
            if (ChartRenderer.KnownIndex(ev.tagID) >= ChartRenderer.KnownTagIDs.Length) { return; }

            // 杖の報酬は本体が理由も出所も付けないので、ここで付け直す（生ログにもこの形で残る）
            if (InWandReward != null && ev.isAdd && ev.statsCashReason == StatsCashReason.Other)
            {
                ev = new TagRecordedEvent(ev.tagID, ev.delta, ev.newTotal, ev.isAdd,
                    (StatsCashReason)WandIncomeReason, InWandReward, ev.saleItemCountDelta);
            }

            int reason = (int)ev.statsCashReason;
            if (reason < 0 || reason >= ChartRenderer.ReasonCount) { reason = 0; }

            // 土地の購入だけは本体が理由を付けないので、こちらで付け直す
            if (InLandPurchase && !ev.isAdd && reason == (int)StatsCashReason.Other) { reason = LandReason; }
            if (InLandCrime && ev.isAdd && reason == (int)StatsCashReason.PactEffectIncome) { reason = LandCrimeReason; }

            double t = _elapsed;
            int bucket = (int)(t / _bucketSeconds);
            if (bucket < 0) { bucket = 0; }

            TagSeries s = GetOrCreateSeries(ev.tagID);
            EnsureBucket(s, bucket);
            if (bucket >= _bucketCount) { _bucketCount = bucket + 1; }

            // 残高はここでは触らない。newTotal はイベントを出した記録係の手持ちで、
            // 納品口ごとの従の記録係では全体の残高にならないため（SampleBalances で取る）
            s.RecordCount++;

            int slot = (ev.isAdd ? 0 : ChartRenderer.ReasonCount) + reason;
            s.Flows[bucket * ChartRenderer.SlotsPerBucket + slot] += ev.delta;

            EnsurePeriod(s, _periodIndex);
            s.PeriodFlows[_periodIndex * ChartRenderer.SlotsPerBucket + slot] += ev.delta;

            RecordSource(s, ev, reason);

            // 返済で消えた分は、消える前の台帳の構成比で割って「その列の返済内容」に残す。
            // 列は返済1回ぶんなので、収入ではなくこれで埋めたい
            // （持ち越しだけで返した回でも中身が出る）。
            bool isRepayment = !ev.isAdd
                && ev.statsCashReason == StatsCashReason.RepaymentExpense
                && string.Equals(ev.tagID, _requiredTag, StringComparison.Ordinal);

            // 台帳：収入で積み、支出で新しい山から取り崩す。
            // 返済は取り崩した中身をそのまま列の内訳に残すので、こちらで二重に引かない。
            // リロールのように内訳に出さない支出も、残高は確かに減るので通す
            if (isRepayment) { RecordRepaidComposition(s, ev.delta); }
            else if (!ev.isAdd) { s.SpendFromHolding(ev.delta); }

            // 列は返済1回ぶん。**返済のイベントそのもので閉じる**。
            // 返済回数の増加を毎フレーム見て閉じる作りだと、
            // 同じフレームに2回返した場合に列が1本しか増えず、内訳も1本にまとまってしまう。
            // 生ログに残す列は、閉じる前の番号（この返済が属する列）。閉じた後の番号で残すと、読み直したときに
            // 返済の支出と返済の内訳が1つ後ろの列へずれ、最初の列から返済が抜ける（2026-11-18 まで、そうなっていた）
            int recordedPeriod = _periodIndex;
            if (isRepayment && !_replaying) { CloseColumn(); }

            if (ev.isAdd)
            {
                s.IncomeTotal += ev.delta;
                s.IncomeByReason[reason] += ev.delta;
                AddSource(s.IncomeBySource, ev.statsSourceID, ev.delta);
            }
            else
            {
                s.ExpenseTotal += ev.delta;
                s.ExpenseByReason[reason] += ev.delta;
                AddSource(s.ExpenseBySource, ev.statsSourceID, ev.delta);
            }

            if (!_replaying) { RecordRaw(s, ev, t, reason, recordedPeriod); }

            while (_bucketCount > MaxBuckets && MaxBuckets >= 16) { Downsample(); }
        }

        /// <summary>
        /// 出所ごとの記録。statsSourceID が入るのは
        /// 売却（アイテムID）・レリック／ペナルティ（バフのタグID）・クラフト（レシピID）の5種で、
        /// それ以外（受注・返済・契約など）は理由そのものを1つの出所として扱う。
        /// </summary>
        private void RecordSource(TagSeries s, TagRecordedEvent ev, int reason)
        {
            // リロールは内訳として見たいものではないので出さない。
            // 残高は主の記録係から別に取っているので、抜いてもグラフはずれない
            if (ev.statsCashReason == StatsCashReason.RerollExpense) { return; }

            bool hasSource = !string.IsNullOrEmpty(ev.statsSourceID) && UsesSourceID(reason);
            string key = hasSource ? ev.statsSourceID : "#" + reason.ToString(CultureInfo.InvariantCulture);
            SourceSeries series = s.GetOrCreateSource(ev.isAdd, key, reason, hasSource);
            series.Add(_periodIndex, ev.delta);
            if (ev.isAdd) { s.AddHolding(series, ev.delta); }
        }

        /// <summary>
        /// 返済で消えた額を、台帳の新しい山から取り崩して出所へ割り振る。
        /// 取り崩しそのものがここで起きるので、別に SpendFromHolding は呼ばない。
        /// 出所が辿れないぶん（開始時の所持）は「初期」として1本にまとめる。
        /// </summary>
        private void RecordRepaidComposition(TagSeries s, long amount)
        {
            if (amount <= 0) { return; }

            _takeSources.Clear();
            _takeAmounts.Clear();
            s.TakeFromHolding(amount, _takeSources, _takeAmounts);

            for (int i = 0; i < _takeSources.Count && i < _takeAmounts.Count; i++)
            {
                SourceSeries src = _takeSources[i];
                if (src == null) { src = s.GetOrCreateSource(true, "#10", 10, false); }
                src.AddRepaid(_periodIndex, _takeAmounts[i]);
            }
        }

        private readonly List<SourceSeries> _takeSources = new List<SourceSeries>();
        private readonly List<long> _takeAmounts = new List<long>();

        private static bool UsesSourceID(int reason)
        {
            StatsCashReason r = (StatsCashReason)reason;
            return r == StatsCashReason.SaleIncome
                || r == StatsCashReason.BuffRelicIncome
                || r == StatsCashReason.DebuffPenaltyExpense
                || r == StatsCashReason.CraftIncome
                || r == StatsCashReason.CraftExpense
                || r == StatsCashReason.CostRefund
                || r == StatsCashReason.PactEffectIncome
                || reason == WandIncomeReason;
        }

        private static void AddSource(Dictionary<string, long> dict, string sourceID, int delta)
        {
            if (string.IsNullOrEmpty(sourceID)) { return; }
            long current;
            dict.TryGetValue(sourceID, out current);
            dict[sourceID] = current + delta;
        }

        // 生の記録をまとめるための控え（いまの秒のぶんだけ）。鍵は「タグ・世代・理由・出所・列・向き」、値は生の記録の位置
        private int _mergeSecond = -1;
        private readonly Dictionary<long, int> _mergeIndex = new Dictionary<long, int>();
        private readonly List<int> _mergeGeneration = new List<int>();   // タグごと。向きが変わるたびに 1 進む
        private readonly List<int> _mergeSign = new List<int>();         // タグごと。この秒で最後に来た向き（0 はまだ無い）

        /// <summary>
        /// 生の記録に1件足す。**同じ秒の「同じタグ・同じ向き・同じ理由・同じ出所・同じ列」は、1件にまとめる**
        /// （2.0.1。それまでは増減1回につき1件で、大きめの工場は 20 分ほどで上限の 30 万件に届き、そこから先が
        /// 記録されなかった＝業務日誌のグラフが途中で切れ、プレイ中の直近1分の速度も 0 になった。
        /// 2026-11-19〜20 の 12 プレイのうち 4 つで起きていた。まとめると同じプレイが 2 万件前後になる）。
        /// 台帳は同じタグの収入と支出の順で結果が変わるので、あいだに逆向きの増減が挟まったら、そこから先は別の件にする
        /// （タグごとの「世代」を進める）。別のタグとの前後は入れ替わるが、タグどうしは台帳が別なので結果は同じ。
        /// </summary>
        private void RecordRaw(TagSeries s, TagRecordedEvent ev, double t, int reason, int period)
        {
            int tagIndex = s.Index;
            int second = (int)t;
            if (second != _mergeSecond)
            {
                _mergeSecond = second;
                _mergeIndex.Clear();
                for (int i = 0; i < _mergeSign.Count; i++) { _mergeSign[i] = 0; _mergeGeneration[i] = 0; }
            }
            while (_mergeSign.Count <= tagIndex) { _mergeSign.Add(0); _mergeGeneration.Add(0); }
            int sign = ev.isAdd ? 1 : -1;
            if (_mergeSign[tagIndex] != 0 && _mergeSign[tagIndex] != sign) { _mergeGeneration[tagIndex]++; }
            _mergeSign[tagIndex] = sign;
            int sourceIdx = -1;
            if (!string.IsNullOrEmpty(ev.statsSourceID))
            {
                if (!_sourceIndex.TryGetValue(ev.statsSourceID, out sourceIdx))
                {
                    sourceIdx = _sourceIDs.Count;
                    _sourceIDs.Add(ev.statsSourceID);
                    _sourceIndex[ev.statsSourceID] = sourceIdx;
                }
            }

            long key = ((long)(tagIndex & 0xFF) << 54) | ((long)(_mergeGeneration[tagIndex] & 0x3FFF) << 40)
                | ((long)(reason & 0xFF) << 32) | ((long)((sourceIdx + 1) & 0xFFFFF) << 12)
                | ((long)(period & 0x7FF) << 1) | (ev.isAdd ? 1L : 0L);
            int at;
            if (_mergeIndex.TryGetValue(key, out at) && at < _evT.Count)
            {
                _evDelta[at] += ev.isAdd ? ev.delta : -ev.delta;
                _evBalance[at] = ev.newTotal;
                return;
            }
            if (_evT.Count >= MaxRawEvents) { _rawEventsTruncated = true; return; }
            _mergeIndex[key] = _evT.Count;

            _evT.Add((float)t);
            _evDelta.Add(ev.isAdd ? ev.delta : -ev.delta);
            _evBalance.Add(ev.newTotal);
            _evTag.Add(tagIndex);
            _evReason.Add(reason);
            _evSource.Add(sourceIdx);
            _evPeriod.Add(period);
        }

        private TagSeries GetOrCreateSeries(string tagID)
        {
            TagSeries s;
            if (_seriesByTag.TryGetValue(tagID, out s)) { return s; }

            s = new TagSeries(tagID, _series.Count, ChartRenderer.ReasonCount);
            s.DisplayName = ResolveTagName(tagID);
            _seriesByTag.Add(tagID, s);
            _series.Add(s);        // 作った順のまま。生ログの TagIndex が指すので並べ替えない
            RegisterStoreTag(s);
            RebuildOrder();
            return s;
        }

        /// <summary>
        /// 表示用の並び。本体の統計窓と同じ順（現金→建材→…）にして、それ以外は後ろに置く。
        /// _series 自体は生ログの添字が指しているので動かせない。
        /// </summary>
        private void RebuildOrder()
        {
            _ordered.Clear();
            for (int i = 0; i < _series.Count; i++) { _ordered.Add(_series[i]); }
            _ordered.Sort(delegate (TagSeries a, TagSeries b)
            {
                int ia = ChartRenderer.KnownIndex(a.TagID);
                int ib = ChartRenderer.KnownIndex(b.TagID);
                if (ia != ib) { return ia.CompareTo(ib); }
                return string.CompareOrdinal(a.TagID, b.TagID);
            });
        }

        private static string ResolveTagName(string tagID)
        {
            // 本体の名前（プレイ中の言語に合う）を優先し、駄目なら組み込みの表に落とす。
            try
            {
                string name = TagParamGetter.GetName(tagID);
                name = StripRichText(name);
                if (!string.IsNullOrEmpty(name)) { return name; }
            }
            catch (Exception) { }

            int idx = ChartRenderer.KnownIndex(tagID);
            if (idx < ChartRenderer.KnownTagNames.Length) { return ChartRenderer.KnownTagNames[idx]; }
            return tagID;
        }

        /// <summary>&lt;sprite=...&gt; や &lt;color=...&gt; を落とす。IMGUI は TMP のリッチテキストを解さない。</summary>
        private static string StripRichText(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('<') < 0) { return value; }
            StringBuilder sb = new StringBuilder(value.Length);
            bool inTag = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) { sb.Append(c); }
            }
            return sb.ToString().Trim();
        }

        private static void EnsurePeriod(TagSeries s, int period)
        {
            while (s.PeriodFlows.Count <= (period + 1) * ChartRenderer.SlotsPerBucket - 1)
            {
                s.PeriodFlows.Add(0L);
            }
        }

        /// <summary>
        /// 残高の列と流れの表をその区間まで伸ばす。
        ///
        /// 2つを同じループで伸ばしてはいけない。残高は読み直しをまたいで引き継ぐが、
        /// 流れの表は生ログから組み直すので、引き継いだ直後は長さが揃っていない
        /// （残高だけ埋まっていて流れが空）。それぞれの長さを見て伸ばす。
        /// </summary>
        private void EnsureBucket(TagSeries s, int bucket)
        {
            while (s.Balances.Count <= bucket)
            {
                long carry = s.Balances.Count > 0 ? s.Balances[s.Balances.Count - 1] : s.InitialBalance;
                s.Balances.Add(carry);
            }

            int need = (bucket + 1) * ChartRenderer.SlotsPerBucket;
            while (s.Flows.Count < need) { s.Flows.Add(0L); }
        }

        /// <summary>
        /// バケットを2つずつ束ねて幅を倍にする。残高は後ろの値、増減は足し合わせる。
        /// 返済マーカーは絶対時刻で持っているので触らない。
        /// </summary>
        private void Downsample()
        {
            for (int i = 0; i < _series.Count; i++)
            {
                TagSeries s = _series[i];

                // 残高と流れの表の長さを揃えてから畳む
                int have = _bucketCount > s.Balances.Count ? _bucketCount : s.Balances.Count;
                if (have > 0) { EnsureBucket(s, have - 1); }

                int oldCount = s.Balances.Count;
                int newCount = (oldCount + 1) / 2;

                for (int b = 0; b < newCount; b++)
                {
                    int a = b * 2;
                    int c = a + 1 < oldCount ? a + 1 : a;
                    s.Balances[b] = s.Balances[c];

                    for (int slot = 0; slot < ChartRenderer.SlotsPerBucket; slot++)
                    {
                        long sum = s.Flows[a * ChartRenderer.SlotsPerBucket + slot];
                        if (c != a) { sum += s.Flows[c * ChartRenderer.SlotsPerBucket + slot]; }
                        s.Flows[b * ChartRenderer.SlotsPerBucket + slot] = sum;
                    }
                }

                s.Balances.RemoveRange(newCount, oldCount - newCount);
                s.Flows.RemoveRange(newCount * ChartRenderer.SlotsPerBucket, (oldCount - newCount) * ChartRenderer.SlotsPerBucket);
            }

            _bucketSeconds *= 2f;
            _bucketCount = (_bucketCount + 1) / 2;
            Logger.LogInfo("[graph] bar width is now " + F(_bucketSeconds) + " s");
        }

        private long TotalRecordCount()
        {
            long n = 0;
            for (int i = 0; i < _series.Count; i++) { n += _series[i].RecordCount; }
            return n;
        }

        // ------------------------------------------------------------------
        // 入力
        // ------------------------------------------------------------------
        private void HandleInput()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) { return; }

#if DEV_TOOLS
            if (_keyInject != null && _keyInject.WasPressedThisFrame(kb)) { InjectSamples(); }
#endif

            HandleIconClick();
        }

#if DEV_TOOLS
        /// <summary>
        /// 開発用。滅多に出ない出所の見た目（色・絵・顔）を確かめるために、
        /// その出所から現金が入ったことにしたイベントをこちらの記録にだけ流す。
        /// 本体の資源や統計窓の数字には触らない（残高の線も動かない）。
        /// 押すたびに同じ額を足すので、何度か押すと構成グラフの上位に上がってくる。
        /// </summary>
        private void InjectSamples()
        {
            const int amount = 10000;
            OnTagRecorded(new TagRecordedEvent("Cash", amount, 0, true, StatsCashReason.BuffRelicIncome, "OnOpenSafeCash"));   // トロフィー
            OnTagRecorded(new TagRecordedEvent("Cash", amount, 0, true, StatsCashReason.CraftIncome, "ChainExplosion"));       // 大満足
            OnTagRecorded(new TagRecordedEvent("Cash", amount, 0, true, StatsCashReason.CraftIncome, "HarvestableSale"));      // 採掘ポイントの売却
            // 交配（アルラウネに印）。支出は台帳を新しい収入から減らすので、苗の返金より先に流す
            OnTagRecorded(new TagRecordedEvent("Fertilizer", amount, 0, false, StatsCashReason.CraftExpense, "ClonePlant"));
            OnTagRecorded(new TagRecordedEvent("Fertilizer", amount, 0, false, StatsCashReason.CraftExpense, "CloneRock"));
            OnTagRecorded(new TagRecordedEvent("Fertilizer", amount, 0, false, StatsCashReason.CraftExpense, "CloneSafe"));

            // 苗の返金は交配で払った肥料が戻るもの。現金には出ない
            OnTagRecorded(new TagRecordedEvent("Fertilizer", amount, 0, true, StatsCashReason.CostRefund, "OnCloneRefundRock"));   // 鉱物の苗
            OnTagRecorded(new TagRecordedEvent("Fertilizer", amount, 0, true, StatsCashReason.CostRefund, "OnCloneRefundSafe"));   // 金庫の苗

            OnTagRecorded(new TagRecordedEvent("Cash", amount, 0, true, StatsCashReason.PactEffectIncome, "SellAllHarvestablePact"));   // 絶頂
            OnTagRecorded(new TagRecordedEvent("Luxury", amount, 0, true, StatsCashReason.PactEffectIncome, "SetAllPriceTag4"));       // 独占

            // 杖も本物と同じく旗を立てて流す
            InWandReward = "WandDestroyGetChemical";
            try { OnTagRecorded(new TagRecordedEvent("Chemical", amount, 0, true)); }
            finally { InWandReward = null; }
            InWandReward = "WandDestroyGetFuel";
            try { OnTagRecorded(new TagRecordedEvent("Fuel", amount, 0, true)); }
            finally { InWandReward = null; }
            OnTagRecorded(new TagRecordedEvent("Magic", amount, 0, false, StatsCashReason.WandSkillExpense));   // 杖スキルのコスト

            // 地面師は本物と同じく旗を立てて流す（付け直しの経路ごと確かめる）
            InLandCrime = true;
            try { OnTagRecorded(new TagRecordedEvent("Cash", amount, 0, true, StatsCashReason.PactEffectIncome)); }
            finally { InLandCrime = false; }

            // 返済（ゲヘナの顔）。ノルマのタグで流すと列が閉じてしまうので、ノルマでないタグの支出にする。
            // 支出は手元の台帳を新しい収入から減らすので、上で収入を入れたタグ（現金・肥料）は避ける。
            // 課税を流さないのも同じ理由（入れたばかりの収入が消えてアイコンが出なくなる）
            string repayTag = string.Equals(_requiredTag, "Food", StringComparison.Ordinal) ? "Luxury" : "Food";
            OnTagRecorded(new TagRecordedEvent(repayTag, amount, 0, false, StatsCashReason.RepaymentExpense));

            string note = "Injected samples (MOD records only): +" + amount.ToString("N0", CultureInfo.InvariantCulture)
                + " each, repayment on " + repayTag;
            Logger.LogInfo("[dev] " + note);
        }
#endif


        private void OnGUI()
        {
            if (_enabled == null || !_enabled.Value || !_hasRun || _brokenDown) { return; }
            if (Event.current.type != EventType.Repaint) { return; }

            try { DrawFrame(); }
            catch (Exception e) { ReportBreakdown("draw", e); }
        }

        private void DrawFrame()
        {
            EnsureStyles();

            // 統計窓が開いていて、収支タブの空き枠が見つかっているならそこに描く。
            // 見つからないときだけ自前のパネルを出す。
            _drewThisFrame = false;

            // 業務日誌が開いていれば、その枠に選んだ Run のグラフを描く（執務室。統計窓もメニューも無い）
            if (DrawLogGraph())
            {
                _drewThisFrame = true;
                DrawGameCursorOnTop();
                return;
            }

            // Esc のメニューが開いているあいだは何も描かない。
            // 統計窓はメニューの下に残ったままなので、こちらは「窓が見えている」と判断してしまい、
            // メニューの上にグラフが乗ってしまう。
            if (IsMenuOpen()) { return; }

            Rect frame;
            if (TryGetFrameRect(out frame))
            {
                DrawEmbedded(frame);
                _lastDrawn = frame;
                _drewThisFrame = true;
            }

            if (_drewThisFrame) { DrawGameCursorOnTop(); }
        }

        /// <summary>
        /// ゲームが描いているカーソルを最前面に描き直す。
        ///
        /// ゲームは OS のカーソルを隠して（Cursor.visible = false）、
        /// uGUI の Image でカーソルを描いている。IMGUI は Canvas より後に描かれるので、
        /// パネルを出すとカーソルが下に潜って見えなくなる。
        /// パネルがカーソルを覆っているときだけ、同じ絵を同じ位置に描き足す。
        /// </summary>
        private void DrawGameCursorOnTop()
        {
            if (!_chart.MouseValid || !_lastDrawn.Contains(_chart.Mouse)) { return; }

            if (_cursorChaser == null)
            {
                if (Time.unscaledTime < _nextCursorProbe) { return; }
                _nextCursorProbe = Time.unscaledTime + 1f;
                if (!TryFindCursor()) { return; }
            }
            if (!_cursorChaser.activeInHierarchy) { return; }

            try
            {
                _cursorGraphics.Clear();
                _cursorChaser.GetComponentsInChildren<Graphic>(false, _cursorGraphics);

                for (int i = 0; i < _cursorGraphics.Count; i++)
                {
                    Graphic graphic = _cursorGraphics[i];
                    if (graphic == null || !graphic.enabled) { continue; }

                    Rect rect;
                    if (!TryGetScreenRect(graphic.rectTransform, out rect)) { continue; }

                    Image image = graphic as Image;
                    if (image != null)
                    {
                        if (image.sprite == null) { continue; }
                        DrawSprite(image.preserveAspect ? FitAspect(rect, image.sprite) : rect,
                            image.sprite, image.color);
                        continue;
                    }

                    TextMeshProUGUI text = graphic as TextMeshProUGUI;
                    if (text != null && !string.IsNullOrEmpty(text.text))
                    {
                        DrawText(rect.x, rect.y, text.text, _text, text.color);
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Esc のメニュー（MENU / ゲームに戻る…）が開いているか。
        ///
        /// 見るのは _gameMenu だけ。本体の IsGameMenuOpen() もそれだけを見ている。
        /// _rootMenu はメニューを閉じていても有効なままなので、条件に入れると
        /// グラフが常に消える。
        /// private な [SerializeField] なのでリフレクションで覗き、
        /// 取れなければ「開いていない」とみなす（描画を止めない）。
        /// </summary>
        private bool IsMenuOpen()
        {
            if (_commonUI == null)
            {
                if (Time.unscaledTime < _nextMenuProbe) { return false; }
                _nextMenuProbe = Time.unscaledTime + 1f;
                try
                {
                    CommonUIController[] found = UnityEngine.Object.FindObjectsByType<CommonUIController>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None);
                    if (found.Length == 0) { return false; }
                    _commonUI = found[0];
                    _menuObjects = null;
                }
                catch (Exception) { return false; }
            }

            if (_menuObjects == null)
            {
                _menuObjects = new List<GameObject>();
                string[] names = new string[] { "_gameMenu" };
                for (int i = 0; i < names.Length; i++)
                {
                    try
                    {
                        FieldInfo field = typeof(CommonUIController).GetField(names[i],
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        GameObject value = (field != null) ? field.GetValue(_commonUI) as GameObject : null;
                        if (value != null) { _menuObjects.Add(value); }
                    }
                    catch (Exception) { }
                }
                if (_menuObjects.Count == 0) { Logger.LogWarning("[menu] cannot read the open state"); }
            }

            for (int i = 0; i < _menuObjects.Count; i++)
            {
                if (_menuObjects[i] != null && _menuObjects[i].activeInHierarchy) { return true; }
            }
            return false;
        }

        private bool TryFindCursor()
        {
            try
            {
                if (_cursorView == null)
                {
                    CursorViewSwitcher[] found = UnityEngine.Object.FindObjectsByType<CursorViewSwitcher>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None);
                    if (found.Length == 0) { return false; }
                    _cursorView = found[0];
                }

                // _cursorChaser は private な [SerializeField] なのでリフレクションで取る。
                // 取れなければ CursorViewSwitcher 自身の下を見る
                FieldInfo field = typeof(CursorViewSwitcher).GetField("_cursorChaser",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                _cursorChaser = (field != null ? field.GetValue(_cursorView) as GameObject : null)
                    ?? _cursorView.gameObject;
                return _cursorChaser != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 本体の統計窓（収支タブ）にある "under development" の枠を画面座標で返す。
        ///
        /// 枠は StatsBalanceContent の右半分（534x480、内側 494x440）で、
        /// 左のカード一覧（ScrollRect）とは兄弟なのでスクロールしない。
        /// タブやタグを切り替えると作り直されるので、消えていたら探し直す。
        /// </summary>
        private bool TryGetFrameRect(out Rect guiRect)
        {
            guiRect = new Rect();
            if (_graphFrame == null) { return false; }
            if (_statsWindow == null || !_statsWindow.IsVisible) { return false; }
            if (!_graphFrame.gameObject.activeInHierarchy) { return false; }
            if (!TryGetScreenRect(_graphFrame, out guiRect)) { return false; }

            return guiRect.width > 40f && guiRect.height > 40f;
        }

        /// <summary>uGUI の矩形を IMGUI の座標（左上が原点）に写す。</summary>
        private bool TryGetScreenRect(RectTransform rectTransform, out Rect guiRect)
        {
            guiRect = new Rect();
            if (rectTransform == null) { return false; }

            rectTransform.GetWorldCorners(_frameCorners);

            Canvas canvas = rectTransform.GetComponentInParent<Canvas>();
            Camera cam = null;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) { cam = canvas.worldCamera; }

            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, _frameCorners[0]);
            Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, _frameCorners[2]);

            float x0 = Mathf.Min(bl.x, tr.x);
            float x1 = Mathf.Max(bl.x, tr.x);
            float y0 = Mathf.Min(bl.y, tr.y);
            float y1 = Mathf.Max(bl.y, tr.y);

            // IMGUI の y は上が 0、Canvas の y は下が 0
            guiRect = new Rect(x0, Screen.height - y1, x1 - x0, y1 - y0);
            return guiRect.width > 0.5f && guiRect.height > 0.5f;
        }

        private void EnsureStyles()
        {
            if (_text != null && _styleFontSize == TextSize) { return; }
            _styleFontSize = TextSize;

            if (_font == null && !string.IsNullOrEmpty(_fontName.Value))
            {
                try { _font = Font.CreateDynamicFontFromOSFont(_fontName.Value, TextSize); }
                catch (Exception) { _font = null; }
            }

            _text = new GUIStyle(GUI.skin.label);
            _text.fontSize = TextSize;
            _text.normal.textColor = Color.white;
            _text.alignment = TextAnchor.UpperLeft;
            _text.richText = false;
            _text.wordWrap = false;
            _text.padding = new RectOffset(0, 0, 0, 0);
            _text.margin = new RectOffset(0, 0, 0, 0);
            if (_font != null) { _text.font = _font; }

            _title = new GUIStyle(_text);
            _title.fontSize = TextSize + 3;
            _title.fontStyle = FontStyle.Bold;
        }

        private static void Fill(Rect r, Color c)
        {
            if (r.width <= 0f || r.height <= 0f) { return; }
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>文字列の実寸。日本語と英数が混ざるので文字数からは出せない。</summary>
        private float W(string s, GUIStyle style)
        {
            if (string.IsNullOrEmpty(s)) { return 0f; }
            _content.text = s;
            return style.CalcSize(_content).x;
        }

        /// <summary>影を1px落として描く。影は同じ style の色だけ差し替える（別 style だと字の大きさがずれる）。</summary>
        private void DrawText(float x, float y, string s, GUIStyle style, Color color)
        {
            if (string.IsNullOrEmpty(s)) { return; }
            Rect r = new Rect(x, y, 4000f, style.fontSize + 6);

            Color keep = style.normal.textColor;

            style.normal.textColor = ShadowColor;
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), s, style);

            style.normal.textColor = color;
            GUI.Label(r, s, style);

            style.normal.textColor = keep;
        }

        // ------------------------------------------------------------------
        // 描画
        //
        // 中身（何をどう描くか）は EconomyChart.cs の ChartRenderer にある。
        // こちらは「IMGUI で描く筆」を用意して渡すだけ。
        // 同じ ChartRenderer に、確認用の preview が System.Drawing の筆を渡す。
        // ------------------------------------------------------------------

        /// <summary>IMGUI で描く筆。</summary>
        private sealed class ImguiPainter : IChartPainter
        {
            private readonly EconomyGraphPlugin _owner;
            private int _baseSize;

            internal ImguiPainter(EconomyGraphPlugin owner)
            {
                _owner = owner;
            }

            internal void Begin(int baseSize)
            {
                _baseSize = baseSize;
                SetScale(1f);
            }

            public void SetScale(float scale)
            {
                _owner._text.fontSize = Mathf.RoundToInt(_baseSize * scale);
                _owner._title.fontSize = _owner._text.fontSize + 3;
            }

            public float FontSize { get { return _owner._text.fontSize; } }

            public float LineHeight { get { return _owner._text.fontSize + 6; } }

            public float Measure(string text) { return _owner.W(text, _owner._text); }

            public float MeasureTitle(string text) { return _owner.W(text, _owner._title); }

            public void Fill(Rect r, Color color) { EconomyGraphPlugin.Fill(r, color); }

            public void Text(float x, float y, string text, Color color)
            {
                _owner.DrawText(x, y, text, _owner._text, color);
            }

            public void Title(float x, float y, string text, Color color)
            {
                _owner.DrawText(x, y, text, _owner._title, color);
            }

            public bool Icon(Rect r, string sourceKey, string tagID, Color tint, Color outline)
            {
                Sprite sp;
                if (sourceKey == ChartRenderer.CraftIconKey) { sp = _owner.GetCraftIcon(); }
                else if (sourceKey == null) { sp = _owner.GetTagSprite(tagID); }
                else if (sourceKey.Length > 1 && sourceKey[0] == '#') { sp = _owner.GetReasonSprite(sourceKey, tagID); }
                else { sp = _owner.GetSourceSprite(sourceKey, tagID); }
                if (sp == null) { return false; }

                // 縁取り。
                //
                // GUI.color は**乗算**なので、白で染めても絵は元の色のまま出る
                // （黒にはできるが白にはできない）。ずらした同じ絵を敷いても、
                // 下地が同系色だと縁として見えない——これが「白縁が一切見えない」の正体。
                // なので絵から白いシルエットを作っておいて、それを8方向にずらして敷く。
                if (outline.a > 0.01f)
                {
                    Texture2D silhouette = _owner.GetSilhouette(sp);
                    if (silhouette != null)
                    {
                        float step = Mathf.Max(1f, r.width * _owner._iconOutline.Value);

                        // 顔は上半身の絵から頭を切り出したもので、首や髪の切り口が枠に接している。
                        // そこにも縁を付けると頭が切り離されて見える（生首）ので、
                        // 縁は枠の内側にだけ描く。枠に接した切り口には縁が付かない
                        bool clip = _owner.IsFace(sp);
                        Rect area = r;
                        if (clip) { GUI.BeginGroup(r); area = new Rect(0f, 0f, r.width, r.height); }

                        Color old = GUI.color;
                        GUI.color = outline;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                if (dx == 0 && dy == 0) { continue; }
                                Rect offset = new Rect(area.x + dx * step, area.y + dy * step, area.width, area.height);
                                GUI.DrawTexture(SpriteDest(offset, sp), silhouette, ScaleMode.StretchToFill, true);
                            }
                        }
                        GUI.color = old;

                        if (clip) { GUI.EndGroup(); }
                    }
                }

                DrawSprite(r, sp, tint);
                return true;
            }

            public string ReasonLabel(int reason)
            {
                return _owner.ReasonName(reason);
            }
        }

        /// <summary>
        /// 出所IDを持たない項目（鍵が "#理由番号"）の絵。
        /// 当てる絵が決まっていればアドレスから読み、無ければタグのアイコンに落ちる。
        /// </summary>
        private Sprite GetReasonSprite(string key, string tagID)
        {
            int reason;
            if (!int.TryParse(key.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out reason)
                || reason < 0 || reason >= ChartRenderer.ReasonCount)
            {
                return GetTagSprite(tagID);
            }

            // 納品はゲヘナ、地面師はマモンの顔。本体が自分の UI で顔アイコンに使っているものをそのまま借りる
            int faceIndex = FaceIndex(reason);
            if (faceIndex >= 0 && _faces[faceIndex] != null) { return _faces[faceIndex]; }

            // 杖スキルのコストは魔法のワンド（杖の収入と同じ絵）。ワンドはタグではなくアイテムなので出所の引き方で引く
            if (reason == (int)StatsCashReason.WandSkillExpense)
            {
                Sprite wand = GetSourceSprite("Wand", tagID);
                if (wand != null) { return wand; }
            }

            string tag = ReasonIconTag[reason];
            if (string.IsNullOrEmpty(tag)) { return GetTagSprite(tagID); }

            Sprite mapped = GetTagSprite(tag);
            return mapped ?? GetTagSprite(tagID);
        }

        /// <summary>
        /// クラフトタブの絵（MenuCraft）を本体のUIから借りる。
        /// 工場の収支のボタンに使う。四角を並べて描いた工場より、
        /// 本体で見慣れた絵のほうが「生産の話だ」と伝わる。
        ///
        /// アドレスから読むのではなく、統計窓の中に既に出ている Image を探す。
        /// 見つからなければ null を返して、描いた絵のほうへ落ちる。
        /// </summary>
        private Sprite GetCraftIcon()
        {
            if (_craftIcon != null) { return _craftIcon; }
            if (_statsWindow == null || _craftIconTries > 8) { return null; }
            _craftIconTries++;

            try
            {
                UnityEngine.UI.Image[] images =
                    _statsWindow.GetComponentsInChildren<UnityEngine.UI.Image>(true);

                for (int i = 0; i < images.Length; i++)
                {
                    Sprite sp = images[i].sprite;
                    if (sp == null) { continue; }
                    if (sp.name.StartsWith("MenuCraft", StringComparison.Ordinal))
                    {
                        _craftIcon = sp;
                        return sp;
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        private Sprite _craftIcon;
        private int _craftIconTries;

        /// <summary>
        /// 顔を当てる項目と、その後援者（並びを揃える）。
        /// 理由で引くもの：返済＝ゲヘナ、地面師＝マモン。
        /// 出所の ID で引くもの（契約の効果）：独占＝マモン、絶頂＝アスモデウス。
        /// </summary>
        private static readonly Patron[] FacePatrons = new Patron[] { Patron.Gehenna, Patron.Mammon, Patron.Mammon, Patron.Asmodeus };
        private static readonly int[] FaceReasons = new int[] { (int)StatsCashReason.RepaymentExpense, LandCrimeReason, -1, -1 };
        private static readonly string[] FaceSources = new string[] { null, null, "SetAllPriceTag4", "SellAllHarvestablePact" };

        /// <summary>
        /// 顔の表情。ゲヘナは笑顔、地面師のマモンはキラキラ、独占のマモンは普通の顔（Discord の絵文字のように顔だけを切って出す）。
        ///
        /// どれもアドレスが無い（Addressables のキーに無い）ので、読み込み済みの絵を名前で探す。
        /// マモンのキラキラは顔シートのコマで、事務所用（150px）を先に、ゲーム内用（100px）を次に見る。
        /// ゲヘナの笑顔の立ち絵は InGame の場面が直接持っている。
        /// 見つからなければ本体の顔アイコン（通常の表情）に落ちる。構図はどれもほぼ同じ。
        /// </summary>
        private static readonly string[][] FaceSpriteNames = new string[][]
        {
            new string[] { "portrait_patron_gehenna_smile" },
            new string[] { "pic_patron_Mammon_shiny", "pic_patron_Mammon_shiny_0" },
            new string[] { "pic_patron_Mammon_normal" },
            new string[0]
        };

        /// <summary>
        /// 会話の立ち絵（Utage の分割テクスチャ）から組み立てる表情。顔シートにも立ち絵にも無い表情はここにしか無い。
        /// アスモデウスのオホ顔（pic_patron_Asmodeus_oho）。素材は Resources にあるのでいつでも読める。
        /// </summary>
        private static readonly string[] FaceDicing = new string[] { null, null, null, "pic_patron_Asmodeus_oho" };

        /// <summary>分割テクスチャから切る枠（立ち絵 3401x4096 に対する割合・左上原点）。顔が枠いっぱいになる寄り</summary>
        private static readonly Rect[] FaceDicingCrops = new Rect[]
        {
            new Rect(), new Rect(), new Rect(),
            new Rect(0.4366f, 0.2490f, 0.1411f, 0.1172f)
        };

        /// <summary>
        /// 顔の絵から顔だけを切る枠（絵に対する割合・左上原点で x, y, 幅, 高さ）。
        /// 元は胸元や小物まで入った上半身で、アイコンの大きさに縮めると表情が潰れる。
        /// 枠は Discord の絵文字（:lwf_mammon_kirakira: / :lwf_gehenna_smile:）に重ねて合わせた。
        /// アスモデウスは分割テクスチャが読めなかったときの立ち絵（portrait_patron_asmodeus）用
        /// </summary>
        private static readonly Rect[] FaceCrops = new Rect[]
        {
            new Rect(0.39f, 0.27f, 0.49f, 0.49f),   // ゲヘナ
            new Rect(0.47f, 0.25f, 0.46f, 0.46f),   // マモン（地面師）
            new Rect(0.47f, 0.25f, 0.46f, 0.46f),   // マモン（独占）
            new Rect(0.22f, 0.20f, 0.50f, 0.50f)    // アスモデウス
        };

        /// <summary>
        /// 杖の契約の収入は魔法のワンド（アイテム Wand）の絵で出す。
        /// 契約カードの絵は背景ごと描かれていて、切り出すと他のアイコンから浮いた。
        /// 溶解液は薬品だけ、ファイアーボールは燃料だけを出すので、同じ絵でも同じグラフに並ばない
        /// </summary>
        private static readonly Dictionary<string, string> SourceIconAlias = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "WandDestroyGetChemical", "Wand" },
            { "WandDestroyGetFuel", "Wand" }
        };

        /// <summary>
        /// アルラウネへの命令（交配・売却）は、アルラウネ（召喚アイテム Summon-Farmer の絵）の左下に白縁付きの対象の印を重ねて出す。
        /// 本体の命令の絵（五角形が増える・×が付く）は小さいと何の命令か分からない。
        /// 交配の3つは同じ肥料の支出に並ぶので、印（木・岩・金庫）で見分ける。
        /// 印の絵は HarvestableDB が起動時に読み込む採掘ポイントのアイコン（読み込み済みの絵から名前で探す）。
        /// 売却の印は現金のタグ
        /// </summary>
        private static readonly string[] BadgeSources = new string[] { "ClonePlant", "CloneRock", "CloneSafe", "HarvestableSale" };
        private static readonly string[] BadgeSprites = new string[] { "icon_harvestables_Tree", "icon_harvestables_IronRock", "icon_harvestables_Safe", null };
        private static readonly string[] BadgeTags = new string[] { null, null, null, "Cash" };
        private const string BadgeBaseItem = "Summon-Farmer";
        private const float BadgeScale = 0.6f;
        private const int BadgeIconSize = 96;
        private readonly Sprite[] _badged = new Sprite[BadgeSources.Length];
        private float _nextBadgeProbe;

        private static int BadgeIndexBySource(string sourceKey)
        {
            if (string.IsNullOrEmpty(sourceKey)) { return -1; }
            for (int i = 0; i < BadgeSources.Length; i++)
            {
                if (string.Equals(BadgeSources[i], sourceKey, StringComparison.Ordinal)) { return i; }
            }
            return -1;
        }

        /// <summary>印付きの絵を作る。顔と同じく Update から間を空けて呼ぶ（読み込み済みの絵の全走査があるため）</summary>
        private void ResolveBadges(Sprite[] loaded)
        {
            Sprite baseSp = null;
            for (int i = 0; i < _badged.Length; i++)
            {
                if (_badged[i] != null) { continue; }
                if (baseSp == null)
                {
                    try { baseSp = ItemParamGetter.GetSprite(BadgeBaseItem); }
                    catch (Exception) { baseSp = null; }
                    if (baseSp == null) { return; }
                }

                Sprite badge = null;
                if (BadgeTags[i] != null) { badge = GetTagSprite(BadgeTags[i]); }
                else
                {
                    if (loaded == null)
                    {
                        try { loaded = Resources.FindObjectsOfTypeAll<Sprite>(); }
                        catch (Exception) { loaded = new Sprite[0]; }
                    }
                    badge = FindSprite(loaded, new string[] { BadgeSprites[i] });
                }
                if (badge == null) { continue; }

                _badged[i] = ComposeBadge(baseSp, badge, BadgeScale, BadgeIconSize);
                if (_badged[i] != null) { Logger.LogInfo("[badge] " + BadgeSources[i]); }
            }
        }

        /// <summary>
        /// 絵を RenderTexture に重ねて描き、読み戻して1枚の絵にする。失敗したら null。
        /// 印は左下（右下だと顔に被る）。印には白縁を付ける：印だけを描いて読み戻し、
        /// その形を BadgeOutline px 太らせた範囲を白で塗ってから印を載せる
        /// </summary>
        private Sprite ComposeBadge(Sprite baseSp, Sprite badge, float scale, int size)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = null;
            try
            {
                rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                float b = size * scale;
                Rect badgeRect = new Rect(0f, size - b, b, b);

                Color32[] baseLayer = RenderLayer(rt, tex, new Rect(0f, 0f, size, size), baseSp);
                Color32[] badgeLayer = RenderLayer(rt, tex, badgeRect, badge);

                // 印の絵は描かれている範囲が絵ごとに違う（金庫は下に余白があって小さい）。
                // 描かれた範囲を測り、その範囲が左下の b 四方に収まるよう置き直して描き直す
                Rect fitted = FitDrawnArea(badgeLayer, size, badgeRect, b);
                if (fitted.width > 0f) { badgeLayer = RenderLayer(rt, tex, fitted, badge); }

                // 印の形を太らせた範囲（白縁）。ReadPixels の並びは左下から行ごと
                int r = BadgeOutline;
                Color32[] px = baseLayer;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        byte edge = 0;
                        for (int dy = -r; dy <= r && edge < 255; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= size) { continue; }
                            for (int dx = -r; dx <= r; dx++)
                            {
                                int xx = x + dx;
                                if (xx < 0 || xx >= size || dx * dx + dy * dy > r * r) { continue; }
                                byte a = badgeLayer[yy * size + xx].a;
                                if (a > edge) { edge = a; }
                            }
                        }
                        int i = y * size + x;
                        if (edge > 0) { px[i] = Over(new Color32(255, 255, 255, edge), px[i]); }
                        px[i] = Over(badgeLayer[i], px[i]);
                    }
                }
                tex.SetPixels32(px);
                tex.Apply();
                return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[badge] cannot compose: " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) { RenderTexture.ReleaseTemporary(rt); }
            }
        }

        private const int BadgeOutline = 3;

        /// <summary>
        /// 描いた層の不透明な範囲（上が 0 の座標）を、左下の side 四方に下端をそろえて収める矩形を返す。
        /// 横は真ん中に寄せる。何も描かれていなければ幅 0
        /// </summary>
        private static Rect FitDrawnArea(Color32[] layer, int size, Rect drawn, float side)
        {
            int minX = size, maxX = -1, minY = size, maxY = -1;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (layer[y * size + x].a <= 40) { continue; }
                    int yd = size - 1 - y;   // ReadPixels は下から並ぶ
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (yd < minY) { minY = yd; }
                    if (yd > maxY) { maxY = yd; }
                }
            }
            if (maxX < 0) { return new Rect(); }

            float cw = maxX - minX + 1, ch = maxY - minY + 1;
            float k = side / Mathf.Max(cw, ch);
            float x0 = -(minX - drawn.x) * k + (side - cw * k) * 0.5f;
            float y0 = size - (maxY + 1 - drawn.y) * k;
            return new Rect(x0, y0, drawn.width * k, drawn.height * k);
        }

        /// <summary>1枚だけ描いて読み戻す</summary>
        private static Color32[] RenderLayer(RenderTexture rt, Texture2D read, Rect r, Sprite sp)
        {
            int size = rt.width;
            RenderTexture.active = rt;
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, size, size, 0f);
            DrawSpriteInto(r, sp);
            GL.PopMatrix();
            read.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
            return read.GetPixels32();
        }

        /// <summary>上の色を下の色に重ねる（ふつうのアルファ合成）</summary>
        private static Color32 Over(Color32 top, Color32 under)
        {
            float ta = top.a / 255f, ua = under.a / 255f;
            float oa = ta + ua * (1f - ta);
            if (oa <= 0f) { return new Color32(0, 0, 0, 0); }
            return new Color32(
                (byte)((top.r * ta + under.r * ua * (1f - ta)) / oa),
                (byte)((top.g * ta + under.g * ua * (1f - ta)) / oa),
                (byte)((top.b * ta + under.b * ua * (1f - ta)) / oa),
                (byte)(oa * 255f));
        }

        /// <summary>RenderTexture に描く用。絵のある範囲（textureRect）だけを、余白込みの論理矩形どおりに置く</summary>
        private static void DrawSpriteInto(Rect r, Sprite sp)
        {
            Texture tex = sp.texture;
            Rect tr = sp.textureRect;
            Rect uv = new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height);
            Graphics.DrawTexture(SpriteDest(r, sp), tex, uv, 0, 0, 0, 0);
        }

        /// <summary>分割テクスチャから組み立てる絵の大きさ（px）。アイコンは大きくても 50px 程度</summary>
        private const int DicingFaceSize = 128;

        private int _faceAttempts;

        /// <summary>代わり（本体の顔アイコン）で決めた顔。本来の絵が読み込まれたら差し替える</summary>
        private readonly bool[] _faceIsFallback = new bool[FacePatrons.Length];

        /// <summary>
        /// 統計窓を開いたら立てる。顔シートは場面に入ってから読み込まれるので、
        /// タイトル画面で決めた代わりの顔を、窓を開いたときに1回だけ探し直す
        /// </summary>
        private bool _faceRetry;

        /// <summary>
        /// コマの一部を指す絵を作る。Sprite.Create は矩形を指すだけなので画素は読まない
        /// （isReadable は要らない）。作れなければ元の絵のまま返す。
        /// </summary>
        private static Sprite CropFace(Sprite sp, Rect crop)
        {
            try
            {
                Rect tr = sp.textureRect;   // 左下原点・画素
                float w = tr.width * crop.width;
                float h = tr.height * crop.height;
                float x = tr.x + tr.width * crop.x;
                float y = tr.y + tr.height * (1f - crop.y - crop.height);
                return Sprite.Create(sp.texture, new Rect(x, y, w, h), new Vector2(0.5f, 0.5f),
                    sp.pixelsPerUnit, 0, SpriteMeshType.FullRect);
            }
            catch (Exception) { return sp; }
        }

        private static int FaceIndexBySource(string sourceKey)
        {
            if (string.IsNullOrEmpty(sourceKey)) { return -1; }
            for (int i = 0; i < FaceSources.Length; i++)
            {
                if (string.Equals(FaceSources[i], sourceKey, StringComparison.Ordinal)) { return i; }
            }
            return -1;
        }

        private static int FaceIndex(int reason)
        {
            for (int i = 0; i < FaceReasons.Length; i++)
            {
                if (FaceReasons[i] == reason) { return i; }
            }
            return -1;
        }

        /// <summary>
        /// 顔を用意する。**描画からは呼ばない**——読み込み済みの絵を名前で探すのは
        /// Resources.FindObjectsOfTypeAll の全走査なので、描画のたびに呼ぶと重くなる。
        ///
        /// 以前は InGameFaceIcons のシートを位置で切っていたが、0.30 でシートが 4x4 から 5x4 になり
        /// 並びも入れ替わって別人が出た。名前とアドレスで引けばシートの並びに左右されない。
        /// 間を空けて数回だけ試し、決まらなければ本体の顔アイコン（通常の表情）で打ち切る。
        /// </summary>
        private void ResolveFaces()
        {
            bool missing = false, fallback = false;
            for (int i = 0; i < _faces.Length; i++)
            {
                if (_faces[i] == null) { missing = true; }
                else if (_faceIsFallback[i]) { fallback = true; }
            }
            bool retry = _faceRetry && fallback;
            bool badges = false;
            for (int i = 0; i < _badged.Length; i++)
            {
                if (_badged[i] == null) { badges = true; break; }
            }
            if (badges && (_faceRetry || Time.unscaledTime >= _nextBadgeProbe))
            {
                _nextBadgeProbe = Time.unscaledTime + 3f;
                ResolveBadges(null);
            }
            _faceRetry = false;
            if (!missing && !retry) { return; }
            if (!retry)
            {
                if (Time.unscaledTime < _nextFaceProbe) { return; }
                _nextFaceProbe = Time.unscaledTime + 3f;
                _faceAttempts++;
            }

            Sprite[] loaded = null;
            for (int i = 0; i < _faces.Length; i++)
            {
                if (_faces[i] != null && !_faceIsFallback[i]) { continue; }
                // 探し直しは本来の絵だけ。代わりの顔はもう持っているので取り直さない
                bool again = _faces[i] != null;

                string from = null;
                Sprite sp = null;

                if (FaceDicing[i] != null)
                {
                    sp = ComposeDicingFace(FacePatrons[i], FaceDicing[i], FaceDicingCrops[i], DicingFaceSize);
                    if (sp != null)
                    {
                        _faces[i] = sp;
                        _faceIsFallback[i] = false;
                        Logger.LogInfo("[face] " + FacePatrons[i] + " from " + FaceDicing[i]);
                        continue;
                    }
                }

                if (FaceSpriteNames[i].Length > 0)
                {
                    if (loaded == null)
                    {
                        try { loaded = Resources.FindObjectsOfTypeAll<Sprite>(); }
                        catch (Exception) { loaded = new Sprite[0]; }
                    }
                    sp = FindSprite(loaded, FaceSpriteNames[i]);
                    if (sp != null) { from = sp.name; }
                }

                bool isFallback = false;
                if (sp == null && !again && _faceAttempts >= 5)
                {
                    try { sp = Patrons.GetFaceIcon(FacePatrons[i]); }
                    catch (Exception) { sp = null; }
                    if (sp != null) { from = "Patrons.GetFaceIcon"; isFallback = true; }
                }

                if (sp == null) { continue; }
                _faces[i] = CropFace(sp, FaceCrops[i]);
                _faceIsFallback[i] = isFallback;
                Logger.LogInfo("[face] " + FacePatrons[i] + " from " + from);
            }
        }

        /// <summary>切り出した顔か。切り口が枠に接しているので、縁取りを枠の内側に留める</summary>
        private bool IsFace(Sprite sp)
        {
            for (int i = 0; i < _faces.Length; i++)
            {
                if (_faces[i] != null && ReferenceEquals(_faces[i], sp)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// Utage の分割テクスチャから、枠の範囲だけを組み立てた絵を作る。
        ///
        /// 立ち絵は 58px 四方（cellSize 64 − 余白 3×2）の小片に割られ、同じ小片はアトラスの1か所を共有している。
        /// cellIndexList が「立ち絵の小片（左下から行ごと）→アトラスの小片の番号」で、アトラスも左下から並ぶ。
        /// 枠に掛かる小片だけを RenderTexture に描いて読み戻す（読み戻す先は自前の Texture2D なので isReadable は関係ない）。
        /// 失敗したら null を返して、呼び出し側が立ち絵に落とす。
        /// </summary>
        private Sprite ComposeDicingFace(Patron patron, string pattern, Rect crop, int size)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture rt = null;
            try
            {
                Utage.DicingTextures dicing = Resources.Load<Utage.DicingTextures>("ingameadventure/texture/character/pic_patron_" + patron);
                if (dicing == null) { Logger.LogWarning("[face] no dicing texture for " + patron); return null; }
                Utage.DicingTextureData data = null;
                List<Utage.DicingTextureData> list = dicing.TextureDataList;
                for (int i = 0; list != null && i < list.Count; i++)
                {
                    if (list[i] != null && string.Equals(list[i].Name, pattern, StringComparison.Ordinal)) { data = list[i]; break; }
                }
                Texture2D atlas = null;
                List<Texture2D> atlases = dicing.AtlasTextures;
                for (int i = 0; data != null && atlases != null && i < atlases.Count; i++)
                {
                    if (atlases[i] != null && string.Equals(atlases[i].name, data.AtlasName, StringComparison.Ordinal)) { atlas = atlases[i]; break; }
                }
                if (atlas == null && atlases != null && atlases.Count == 1) { atlas = atlases[0]; }
                if (data == null || atlas == null)
                {
                    Logger.LogWarning("[face] no pattern " + pattern + " (patterns=" + (list != null ? list.Count : -1)
                        + ", first=" + (list != null && list.Count > 0 && list[0] != null ? list[0].Name : "-")
                        + ", atlases=" + (atlases != null ? atlases.Count : -1)
                        + ", atlas0=" + (atlases != null && atlases.Count > 0 && atlases[0] != null ? atlases[0].name : "-")
                        + ", found=" + (data != null) + ")");
                    return null;
                }

                BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                List<int> cells = typeof(Utage.DicingTextureData).GetField("cellIndexList", any).GetValue(data) as List<int>;
                int transparent = (int)typeof(Utage.DicingTextureData).GetField("transparentIndex", any).GetValue(data);
                if (cells == null) { return null; }

                int cs = dicing.CellSize, pad = dicing.Padding, inner = cs - pad * 2;
                int width = data.Width, height = data.Height;
                int cols = (width + inner - 1) / inner, rows = (height + inner - 1) / inner;
                int perRow = atlas.width / cs;
                if (inner <= 0 || perRow <= 0 || cells.Count < cols * rows) { return null; }

                // 枠（左下原点・立ち絵の画素）
                float fx = crop.x * width, fw = crop.width * width;
                float fh = crop.height * height, fy = (1f - crop.y - crop.height) * height;
                float kx = size / fw, ky = size / fh;

                rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
                RenderTexture.active = rt;
                GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, size, size, 0f);   // 上が 0（GUI と同じ向き）
                for (int r = 0; r < rows; r++)
                {
                    float iy = r * inner;
                    if (iy + inner <= fy || iy >= fy + fh) { continue; }
                    for (int c = 0; c < cols; c++)
                    {
                        float ix = c * inner;
                        if (ix + inner <= fx || ix >= fx + fw) { continue; }
                        int cell = cells[r * cols + c];
                        if (cell == transparent) { continue; }

                        float x0 = Mathf.Max(ix, fx), x1 = Mathf.Min(ix + inner, fx + fw);
                        float y0 = Mathf.Max(iy, fy), y1 = Mathf.Min(iy + inner, fy + fh);
                        float ax = (cell % perRow) * cs + pad + (x0 - ix);
                        float ay = (cell / perRow) * cs + pad + (y0 - iy);
                        Rect uv = new Rect(ax / atlas.width, ay / atlas.height, (x1 - x0) / atlas.width, (y1 - y0) / atlas.height);
                        Rect dest = new Rect((x0 - fx) * kx, (fy + fh - y1) * ky, (x1 - x0) * kx, (y1 - y0) * ky);
                        Graphics.DrawTexture(dest, atlas, uv, 0, 0, 0, 0);
                    }
                }
                GL.PopMatrix();

                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
                tex.Apply();
                return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[face] cannot compose " + pattern + ": " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) { RenderTexture.ReleaseTemporary(rt); }
            }
        }

        private static Sprite FindSprite(Sprite[] loaded, string[] names)
        {
            // 並びは優先順。先の名前が見つかればそれを使う
            for (int n = 0; n < names.Length; n++)
            {
                for (int i = 0; i < loaded.Length; i++)
                {
                    if (loaded[i] != null && string.Equals(loaded[i].name, names[n], StringComparison.Ordinal))
                    {
                        return loaded[i];
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 理由の呼び名。本体のメッセージ表から引いて、無いものだけ組み込みの言葉に落とす
        /// （プレイ中の言語に合わせるため）。出所IDを持たない項目のホバー表示にだけ使う。
        /// </summary>
        private string ReasonName(int reason)
        {
            if (reason < 0 || reason >= ChartRenderer.ReasonCount) { return string.Empty; }

            // 専用の絵があるものは絵だけで分かるので、名前は出さない
            if (!string.IsNullOrEmpty(ReasonIconTag[reason]) || FaceIndex(reason) >= 0
                || reason == (int)StatsCashReason.WandSkillExpense)
            {
                return string.Empty;
            }

            string cached = _reasonNames[reason];
            if (cached != null) { return cached; }

            string key = ReasonMessageKeys[reason];
            string name = null;
            if (!string.IsNullOrEmpty(key))
            {
                try
                {
                    name = LocalizedTextGetter.GetLocalizedText(LocalizationInitializer.StringTableType.Message, key);
                    name = StripRichText(name);
                }
                catch (Exception) { name = null; }
            }
            if (string.IsNullOrEmpty(name)) { name = ReasonFallback[reason]; }

            _reasonNames[reason] = name;
            return name;
        }

        /// <summary>本体の統計窓の空き枠に描く。</summary>
        private void DrawEmbedded(Rect frame)
        {
            float scale = Mathf.Clamp(frame.width / ChartRenderer.DesignWidth, 0.8f, 2.2f);
            float pad = 8f * scale;
            Rect area = new Rect(frame.x + pad, frame.y + pad,
                frame.width - pad * 2f, frame.height - pad * 2f);
            DrawChart(area);
        }

        /// <summary>地を敷いてグラフを描く。業務日誌のグラフの枠用（統計窓の枠は本体の地の上に描くので DrawEmbedded）。</summary>
        private void DrawPanel(Rect panel)
        {
            Fill(panel, new Color(0.04f, 0.05f, 0.07f, 0.88f));
            Fill(new Rect(panel.x, panel.y, panel.width, 2f), new Color(1f, 0.62f, 0.2f, 0.9f));

            float pad = 10f;
            DrawChart(new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2f, panel.height - pad * 2f));
        }

        private void DrawChart(Rect area)
        {
            FillChartData();
            _painter.Begin(TextSize);
            _renderer.Draw(area, _chart, _painter);
            _chartDrawnFrame = Time.frameCount;
        }

        /// <summary>描くのに要るものを渡す。中身は参照を差し替えるだけで、確保はしない。</summary>
        private void FillChartData()
        {
            _chart.Ordered = _ordered;
            _chart.Selected = ResolveSeries();
            _chart.BucketSeconds = _bucketSeconds;
            _chart.BucketCount = _bucketCount;
            _chart.MaxColumns = MaxColumns;
            _chart.PeriodIndex = _periodIndex;
            _chart.PeriodRepaid = _periodRepaid;
            _chart.RepaidTotal = _repaidTotal;
            _chart.TargetProgress = _targetProgress;
            _chart.RequiredTagID = _requiredTag;
            _chart.RequiredCount = _requiredCount;
            _chart.RequiredCurrent = _requiredCurrent;
            _chart.PeriodEnds = _periodEnds;
            _chart.HiddenTags = _hiddenTags;
            _chart.Mode = _mode;
            FillMouse();
            _chart.LastMinute = UseLastMinute();
            _chart.ExpenseSide = IsExpenseSide();
            _chart.Loose = LogView.IsOpen;   // 業務日誌で過去の Run を見ているときは、内訳を細かく出す

            BuildRates();
        }

        // 直近1分。本体の統計窓と同じ数え方に合わせる——
        // 「いまの秒から数えて60個ぶんの秒バケットの合計」（StatsWindowController.StatsCounter）。
        // 毎秒の速さに直すと本体の表示と数字が食い違うので、そのまま同じ量を出す。
        private const int RateWindow = 60;    // 何秒ぶんを1点にまとめるか
        private const int RateSlots = 60;     // 横に何点並べるか（＝直近60秒）
        private const int RateHistory = RateWindow + RateSlots;   // 遡って要る秒数
        private const int RateMaxLines = 8;

        private readonly Dictionary<string, long[]> _rateBuckets =
            new Dictionary<string, long[]>(StringComparer.Ordinal);
        private readonly List<string> _rateKeys = new List<string>();
        private float _nextRateBuild;

        /// <summary>
        /// 構成グラフを1分表示にしたときの、出所ごとの「直近1分の量」。
        ///
        /// 左端の点も直近1分の合計なので、そのぶん余分に（合計120秒）遡る。
        /// 右端の点は本体のカードに出ている数字と一致する。
        ///
        /// 出所ごと×秒の表を常に持つと走行が伸びたときに嵩むし、要るのは直近だけなので、
        /// 生ログの末尾を遡って必要なときに組む。毎フレームだと確保が続くので間隔を空ける。
        /// </summary>
        private void BuildRates()
        {
            bool wanted = _mode == ChartRenderer.ModeComposition && _chart.LastMinute;
            if (!wanted)
            {
                if (_chart.RateSources.Count > 0)
                {
                    _chart.RateSources.Clear();
                    _chart.RateValues.Clear();
                    _chart.RateTotals.Clear();
                }
                return;
            }

            if (Time.unscaledTime < _nextRateBuild) { return; }
            _nextRateBuild = Time.unscaledTime + 0.25f;

            _chart.RateSources.Clear();
            _chart.RateValues.Clear();
            _chart.RateTotals.Clear();
            _chart.RateSlotSeconds = 1f;
            _chart.RateTotal = 0;

            TagSeries s = _chart.Selected;
            if (s == null || _evT == null) { return; }

            // 本体と同じく秒で刻む。いまの秒を右端にして、そこから遡る
            int nowSecond = (int)_elapsed;
            int baseSecond = nowSecond - (RateHistory - 1);
            double from = baseSecond;
            bool expense = _chart.ExpenseSide;

            _rateBuckets.Clear();
            _rateKeys.Clear();

            for (int i = _evT.Count - 1; i >= 0; i--)
            {
                if (_evT[i] < from) { break; }
                if (_evTag[i] != s.Index) { continue; }

                int delta = _evDelta[i];
                if ((delta > 0) == expense) { continue; }

                int reason = _evReason[i];
                // リロールは内訳に出さない（構成グラフと揃える）
                if (reason == (int)StatsCashReason.RerollExpense) { continue; }

                int slot = (int)_evT[i] - baseSecond;
                if (slot < 0 || slot >= RateHistory) { continue; }

                string sourceID = (_evSource[i] >= 0 && _evSource[i] < _sourceIDs.Count)
                    ? _sourceIDs[_evSource[i]] : null;
                bool hasSource = !string.IsNullOrEmpty(sourceID) && UsesSourceID(reason);
                string key = hasSource ? sourceID : "#" + reason.ToString(CultureInfo.InvariantCulture);

                long[] arr;
                if (!_rateBuckets.TryGetValue(key, out arr))
                {
                    arr = new long[RateHistory];
                    _rateBuckets[key] = arr;
                    _rateKeys.Add(key);
                }
                arr[slot] += Math.Abs(delta);
            }

            if (_rateKeys.Count == 0) { return; }

            // 見出しに出す総量は、線に出さないぶんも含めた全部
            for (int i = 0; i < _rateKeys.Count; i++)
            {
                _chart.RateTotal += Latest(_rateBuckets[_rateKeys[i]]);
            }

            // いま多い順（＝右端の値）。線は重ねるので、多すぎると読めない
            _rateKeys.Sort(delegate (string a, string b)
            {
                return Latest(_rateBuckets[b]).CompareTo(Latest(_rateBuckets[a]));
            });

            int lines = Mathf.Min(RateMaxLines, _rateKeys.Count);
            for (int i = 0; i < lines; i++)
            {
                string key = _rateKeys[i];
                long[] arr = _rateBuckets[key];

                SourceSeries src = FindSource(s, !expense, key);
                if (src == null) { continue; }

                _chart.RateSources.Add(src);
                _chart.RateTotals.Add(Latest(arr));
                _chart.RateValues.Add(Rolling(arr));
            }
        }

        /// <summary>右端の点＝いまの「直近1分」。本体のカードの数字と同じもの。</summary>
        private static long Latest(long[] seconds)
        {
            long total = 0;
            for (int i = RateSlots; i < seconds.Length; i++) { total += seconds[i]; }
            return total;
        }

        /// <summary>
        /// 各点を「そこまでの60秒の合計」に直す。
        /// 点 i は秒 (baseSecond + i) から 60 秒ぶんを見る。
        /// </summary>
        private static float[] Rolling(long[] seconds)
        {
            float[] line = new float[RateSlots];

            long run = 0;
            for (int i = 0; i < RateWindow; i++) { run += seconds[i]; }

            for (int i = 0; i < RateSlots; i++)
            {
                run += seconds[RateWindow + i];
                run -= seconds[i];
                line[i] = run;
            }
            return line;
        }

        private static SourceSeries FindSource(TagSeries s, bool income, string key)
        {
            List<SourceSeries> all = income ? s.IncomeSources : s.ExpenseSources;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Key, key, StringComparison.Ordinal)) { return all[i]; }
            }
            return null;
        }

        /// <summary>
        /// マウスの位置を IMGUI と同じ向き（左上が原点）にして渡す。
        /// Event.current は Repaint 以外でも呼ばれるので、入力系から直に取る。
        /// </summary>
        private void FillMouse()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                _chart.MouseValid = false;
                return;
            }

            Vector2 position = mouse.position.ReadValue();
            _chart.Mouse = new Vector2(position.x, Screen.height - position.y);
            _chart.MouseValid = true;
        }

        /// <summary>いま描くタグ。統計窓が開いていれば窓のタグタブに従う。</summary>
        private TagSeries ResolveSeries()
        {
            string tagID = StatsSelectedTagID();
            if (!string.IsNullOrEmpty(tagID))
            {
                TagSeries found;
                if (_seriesByTag.TryGetValue(tagID, out found)) { return found; }
                return null;
            }
            if (_ordered.Count == 0) { return null; }
            if (_selectedTag >= _ordered.Count) { _selectedTag = 0; }
            return _ordered[_selectedTag];
        }

        // ------------------------------------------------------------------
        // アイコンと言葉
        // ------------------------------------------------------------------

        /// <summary>
        /// アトラスの一部を切り出して描く。
        ///
        /// スプライトは余白を切り詰めてアトラスに詰められていることがあり、
        /// そのとき textureRect（実際に絵のある範囲）は論理サイズ rect より小さい。
        /// 切り詰め後の絵を枠いっぱいに引き伸ばすと**縦横比が崩れて位置もずれる**ので、
        /// 論理サイズを基準に縮尺を決めて、その中の正しい場所へ置く。
        /// </summary>
        private static void DrawSprite(Rect r, Sprite sp, Color tint)
        {
            if (sp == null || sp.texture == null) { return; }

            Rect tr = sp.textureRect;
            float tw = sp.texture.width;
            float th = sp.texture.height;
            if (tw <= 0f || th <= 0f || tr.width <= 0f || tr.height <= 0f) { return; }

            Rect dest = SpriteDest(r, sp);

            Rect uv = new Rect(tr.x / tw, tr.y / th, tr.width / tw, tr.height / th);
            Color old = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(dest, sp.texture, uv, true);
            GUI.color = old;
        }

        /// <summary>
        /// 論理矩形 r の中で、実際に絵のある範囲がどこに来るか。
        /// 切り詰められたスプライトでも縦横比と位置が崩れないようにするための計算。
        /// </summary>
        private static Rect SpriteDest(Rect r, Sprite sp)
        {
            Rect tr = sp.textureRect;
            Rect logical = sp.rect;
            if (logical.width <= 0.5f || logical.height <= 0.5f) { return r; }

            float sx = r.width / logical.width;
            float sy = r.height / logical.height;
            Vector2 offset = sp.textureRectOffset;   // 論理矩形の左下から見た、絵のある範囲の位置
            return new Rect(
                r.x + offset.x * sx,
                r.yMax - (offset.y + tr.height) * sy,   // IMGUI は上が 0 なので下端から測り直す
                tr.width * sx,
                tr.height * sy);
        }

        /// <summary>
        /// 絵から白いシルエットを作る（縁取り用）。
        ///
        /// ゲームのテクスチャは読み取り不可（isReadable = false）なので画素を直接は読めない。
        /// いったん RenderTexture へ写して読み戻し、色だけ白に置き換える。
        /// OnGUI の最中に描画先を切り替えたくないので、作るのは Update 側で行い、
        /// 出来るまでの数フレームは縁無しで描く。
        /// </summary>
        private Texture2D GetSilhouette(Sprite sp)
        {
            if (sp == null || sp.texture == null) { return null; }

            int key = sp.GetInstanceID();
            Texture2D found;
            if (_silhouettes.TryGetValue(key, out found)) { return found; }

            if (!_silhouettePending.Contains(sp)) { _silhouettePending.Add(sp); }
            return null;
        }

        private void BuildPendingSilhouettes()
        {
            if (_silhouettePending.Count == 0) { return; }

            // 1フレームに作るのは少しだけ（描画先の切り替えが要るので）
            int budget = 4;
            for (int i = _silhouettePending.Count - 1; i >= 0 && budget > 0; i--)
            {
                Sprite sp = _silhouettePending[i];
                _silhouettePending.RemoveAt(i);
                budget--;

                if (sp == null) { continue; }
                int key = sp.GetInstanceID();
                if (_silhouettes.ContainsKey(key)) { continue; }

                _silhouettes[key] = BuildSilhouette(sp);
            }
        }

        private static Texture2D BuildSilhouette(Sprite sp)
        {
            RenderTexture temp = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                Rect tr = sp.textureRect;
                int w = Mathf.Max(1, Mathf.RoundToInt(tr.width));
                int h = Mathf.Max(1, Mathf.RoundToInt(tr.height));

                float tw = sp.texture.width;
                float th = sp.texture.height;

                temp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(sp.texture, temp,
                    new Vector2(tr.width / tw, tr.height / th),
                    new Vector2(tr.x / tw, tr.y / th));

                RenderTexture.active = temp;
                Texture2D result = new Texture2D(w, h, TextureFormat.ARGB32, false);
                result.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);

                Color32[] pixels = result.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i].r = 255;
                    pixels[i].g = 255;
                    pixels[i].b = 255;
                }
                result.SetPixels32(pixels);
                result.filterMode = FilterMode.Bilinear;
                result.wrapMode = TextureWrapMode.Clamp;
                result.Apply(false, false);
                return result;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temp != null) { RenderTexture.ReleaseTemporary(temp); }
            }
        }

        /// <summary>Image が preserveAspect のときは、枠の中で縦横比を保った位置に収める。</summary>
        private static Rect FitAspect(Rect r, Sprite sp)
        {
            if (sp == null) { return r; }

            Rect logical = sp.rect;
            if (logical.width <= 0.5f || logical.height <= 0.5f || r.width <= 0f || r.height <= 0f) { return r; }

            float spriteAspect = logical.width / logical.height;
            float rectAspect = r.width / r.height;

            if (spriteAspect > rectAspect)
            {
                float h = r.width / spriteAspect;
                return new Rect(r.x, r.y + (r.height - h) * 0.5f, r.width, h);
            }

            float w = r.height * spriteAspect;
            return new Rect(r.x + (r.width - w) * 0.5f, r.y, w, r.height);
        }

        /// <summary>
        /// クラフトの出所はレシピIDで、アイテムでもタグでもないのでそのままでは絵が引けない。
        /// レシピ定義を引いて「そのレシピが作る物」のアイコンに読み替える。
        /// </summary>
        private static Sprite GetRecipeResultSprite(string recipeID)
        {
            try
            {
                RecipeDefinition definition;
                if (!RecipeDatabase.TryGetDefinition(recipeID, out definition)) { return null; }
                if (definition == null || definition.Result == null) { return null; }

                string key = definition.Result.Key;
                if (string.IsNullOrEmpty(key)) { return null; }

                return definition.Result.Type == RecipeValueType.Tag
                    ? TagParamGetter.GetSprite(key)
                    : ItemParamGetter.GetSprite(key);
            }
            catch (Exception) { return null; }
        }

        private Sprite GetTagSprite(string tagID)
        {
            Sprite sp;
            if (_tagSprites.TryGetValue(tagID, out sp)) { return sp; }
            try { sp = TagParamGetter.GetSprite(tagID); }
            catch (Exception) { sp = null; }
            _tagSprites[tagID] = sp;
            return sp;
        }

        /// <summary>
        /// 出所のアイコン。引き方は本体の GetBalanceEntrySprite と同じで、
        /// 売却はアイテム、レリック／ペナルティはタグ（＝バフのID）。
        /// レシピなど絵の無いものはタグのアイコンに落ちる。
        /// </summary>
        /// <summary>課税の印に使う顔（ゲヘナの笑顔。グラフで納品の出所に使っているのと同じ絵）。まだ読めていなければ null。</summary>
        internal Sprite TaxFace()
        {
            return _faces[0];
        }

        private Sprite GetSourceSprite(string sourceKey, string tagID)
        {
            int face = FaceIndexBySource(sourceKey);
            if (face >= 0 && _faces[face] != null) { return _faces[face]; }
            int badged = BadgeIndexBySource(sourceKey);
            if (badged >= 0 && _badged[badged] != null) { return _badged[badged]; }

            Sprite cached;
            if (_sourceSprites.TryGetValue(sourceKey, out cached)) { return cached; }

            string alias;
            if (SourceIconAlias.TryGetValue(sourceKey, out alias))
            {
                Sprite aliased = GetSourceSprite(alias, tagID);
                _sourceSprites[sourceKey] = aliased;
                return aliased;
            }

            // レシピはタグより先に引く。本体の統計窓もクラフトは作る物の絵を出すし、
            // レシピと同名のタグ（ChainExplosion＝コマンド「連鎖爆発」）があると、
            // 作る物（大満足）とは別の絵を拾って色とも食い違う
            Sprite sp = null;
            try { sp = ItemParamGetter.GetSprite(sourceKey); }
            catch (Exception) { sp = null; }
            if (sp == null) { sp = GetRecipeResultSprite(sourceKey); }
            if (sp == null)
            {
                try { sp = TagParamGetter.GetSprite(sourceKey); }
                catch (Exception) { sp = null; }
            }
            if (sp == null) { sp = GetTagSprite(tagID); }

            _sourceSprites[sourceKey] = sp;
            return sp;
        }

        /// <summary>
        /// グラフ下の帯を押したときの処理。
        ///
        /// IMGUI のボタンにすると押した瞬間にゲーム側へも入力が渡ってしまうので、
        /// 直前に描いた矩形を覚えておいて、こちらで当たりを見る。
        /// </summary>
        private void HandleIconClick()
        {
            // 描いていないときの押し込みを拾わない。
            // 矩形は最後に描いたときのものが残るので、隠れている間も当たってしまう
            if (Time.frameCount - _chartDrawnFrame > 2) { return; }

            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame) { return; }

            Vector2 pos = mouse.position.ReadValue();
            Vector2 gui = new Vector2(pos.x, Screen.height - pos.y);

            for (int i = 0; i < _chart.ModeRects.Count && i < _chart.ModeIDs.Count; i++)
            {
                if (!_chart.ModeRects[i].Contains(gui)) { continue; }
                _mode = _chart.ModeIDs[i];
                return;
            }

            for (int i = 0; i < _chart.ToggleRects.Count && i < _chart.ToggleIDs.Count; i++)
            {
                if (!_chart.ToggleRects[i].Contains(gui)) { continue; }

                if (_chart.ToggleIDs[i] == ChartRenderer.ToggleSide)
                {
                    _expenseSide = !IsExpenseSide();
                    InvokeStats("SwitchTab", new object[] { _expenseSide ? 1 : 0 });
                }
                else
                {
                    _lastMinute = !UseLastMinute();
                    InvokeStats("ToggleStatsRange", null);
                }
                return;
            }

            for (int i = 0; i < _chart.IconRects.Count && i < _chart.IconTagIDs.Count; i++)
            {
                if (!_chart.IconRects[i].Contains(gui)) { continue; }

                // 重ねているときは「1つを選ぶ」が無いので、見せる／隠すにする
                if (_mode == ChartRenderer.ModeBalanceAll)
                {
                    string tagID = _chart.IconTagIDs[i];
                    if (!_hiddenTags.Remove(tagID)) { _hiddenTags.Add(tagID); }
                }
                else
                {
                    _selectedTag = i;
                    SetStatsSelectedTag(_chart.IconTagIDs[i]);
                }
                return;
            }
        }

        // ------------------------------------------------------------------
        // 範囲・側の判定（統計窓の状態に合わせる）
        // ------------------------------------------------------------------

        /// <summary>横軸を直近1分に絞るか。統計窓が開いていれば窓のトグルに従う。</summary>
        private bool UseLastMinute()
        {
            if (_graphFrame != null)
            {
                bool? fromWindow = ReadStatsBool("_isLastMinuteRangeSelected");
                if (fromWindow.HasValue) { return fromWindow.Value; }
            }
            return _lastMinute;
        }

        /// <summary>統計窓が支出タブを開いているか。開いていなければ収入側。</summary>
        private bool IsExpenseSide()
        {
            if (_graphFrame != null)
            {
                int? tab = ReadStatsInt("_selectedTabIndex");
                if (tab.HasValue) { return tab.Value == 1; }
            }
            return _expenseSide;
        }

        private bool? ReadStatsBool(string fieldName)
        {
            object v = ReadStatsField(fieldName);
            if (v is bool) { return (bool)v; }
            return null;
        }

        private int? ReadStatsInt(string fieldName)
        {
            object v = ReadStatsField(fieldName);
            if (v is int) { return (int)v; }
            return null;
        }

        /// <summary>
        /// 本体の統計窓が選んでいるタグを動かす。
        ///
        /// 窓に埋め込んでいるときは、こちらの選択より窓の選択が優先される
        /// （ResolveSeries・UseLastMinute・IsExpenseSide を見よ）。押しても変わらないのはそのため。
        /// 窓の見た目も一緒に動かしたいので、フィールドを直接書き換えず本体の入口を呼ぶ。
        /// </summary>
        private void SetStatsSelectedTag(string tagID)
        {
            if (string.IsNullOrEmpty(tagID)) { return; }
            InvokeStats("OnBalanceTagSelected", new object[] { tagID });
        }

        /// <summary>統計窓の内側の処理を呼ぶ。窓が無ければ何もしない。</summary>
        private void InvokeStats(string methodName, object[] args)
        {
            if (_statsWindow == null) { return; }

            try
            {
                MethodInfo method;
                if (!_statsMethods.TryGetValue(methodName, out method))
                {
                    method = typeof(StatsWindowController).GetMethod(methodName,
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    _statsMethods[methodName] = method;

                    if (method == null)
                    {
                        Logger.LogWarning("[stats] " + methodName + " not found; the window will not follow the mod");
                    }
                }

                if (method != null) { method.Invoke(_statsWindow, args); }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[stats] cannot call " + methodName + ": " + e.Message);
            }
        }

        private readonly Dictionary<string, MethodInfo> _statsMethods =
            new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        private object ReadStatsField(string fieldName)
        {
            if (_statsWindow == null) { return null; }
            FieldInfo field;
            if (!_statsFields.TryGetValue(fieldName, out field))
            {
                try
                {
                    field = typeof(StatsWindowController).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch (Exception) { field = null; }
                _statsFields[fieldName] = field;
                if (field == null) { Logger.LogWarning("[stats] cannot read " + fieldName); }
            }
            if (field == null) { return null; }
            try { return field.GetValue(_statsWindow); }
            catch (Exception) { return null; }
        }

        private string StatsSelectedTagID()
        {
            if (_graphFrame == null) { return null; }
            return ReadStatsField("_selectedBalanceTagID") as string;
        }

        // ------------------------------------------------------------------
        // 書式
        // ------------------------------------------------------------------
        private static string N(long v)
        {
            return v.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string F(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string FormatTime(double seconds)
        {
            if (seconds < 0.0) { seconds = 0.0; }
            int total = (int)seconds;
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int sec = total % 60;
            if (h > 0)
            {
                return h.ToString(CultureInfo.InvariantCulture) + ":"
                    + m.ToString("00", CultureInfo.InvariantCulture) + ":"
                    + sec.ToString("00", CultureInfo.InvariantCulture);
            }
            return m.ToString(CultureInfo.InvariantCulture) + ":" + sec.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
