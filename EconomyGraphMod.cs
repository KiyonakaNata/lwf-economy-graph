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
//     → TagRecordedEvent(tagID, delta, newTotal, isAdd, statsCashReason, statsSourceID)
//
// タグ（現金・建材・食品…）の増減が1件ずつ、理由15種つきで流れてくる。
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
using GameState;
using Items;
using Items.Craft;
using Map.MapObjects.Mono;
using Items.Delivery;
using Items.Tag;
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
    public sealed class EconomyGraphPlugin : BaseUnityPlugin
    {
        internal const string PluginGuid = "kiyonakanata.lwfeconomygraph";
        internal const string PluginName = "LWF Economy Graph";
        internal const string PluginVersion = "1.0.1";

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
            "",              // WandSkillExpense
            "",              // InitialIncome
            "",              // PactEffectIncome
            "",              // TelephoneExpense
            "",              // CraftIncome
            "",              // CraftExpense
            "FreeLand"       // 土地購入（本体には無い。土地無料券の絵を借りる）
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
            "Pact", "StatsReasonOther", "StatsColumnProduction", "StatsColumnProduction", ""
        };

        /// <summary>本体に対応する語が無いものの控え。</summary>
        private static readonly string[] ReasonFallback = new string[ChartRenderer.ReasonCount]
        {
            "その他", "注文", "売却", "レリック", "注文", "返済", "ペナルティ", "契約",
            "リロール", "杖", "初期", "契約", "電話", "生産", "生産", "土地"
        };

        // ------------------------------------------------------------------
        // 設定
        // ------------------------------------------------------------------
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _autoShowOnResult;
        private ConfigEntry<bool> _embedInStatsWindow;
        private ConfigEntry<bool> _keepDataOnReload;
        private ConfigEntry<float> _bucketSecondsInitial;
        private ConfigEntry<int> _maxBuckets;
        private ConfigEntry<int> _maxRawEvents;
        private ConfigEntry<int> _startMode;
        private ConfigEntry<int> _maxColumns;
        private ConfigEntry<float> _iconOutline;
        private ConfigEntry<int> _fontSize;
        private ConfigEntry<string> _fontName;
        private ConfigEntry<string> _panelRect;
        private ConfigEntry<string> _resultPanelRect;
        private ConfigEntry<int> _graphAreaWidth;
        private ConfigEntry<string> _keyToggleSpec;
        private ConfigEntry<string> _keyNextTagSpec;
        private ConfigEntry<string> _keyPrevTagSpec;
        private ConfigEntry<string> _keyExportSpec;
        private ConfigEntry<string> _keyAdjustSpec;
        private ConfigEntry<string> _keyCycleModeSpec;
        private ConfigEntry<string> _keyCycleRangeSpec;
        private ConfigEntry<string> _keyToggleSideSpec;

        private Hotkey _keyToggle;
        private Hotkey _keyNextTag;
        private Hotkey _keyPrevTag;
        private Hotkey _keyExport;
        private Hotkey _keyAdjust;
        private Hotkey _keyCycleMode;
        private Hotkey _keyCycleRange;
        private Hotkey _keyToggleSide;

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
        private const int LandReason = 15;

        /// <summary>土地の購入処理の中にいるか。記録は同じ呼び出しの中で流れてくる</summary>
        private static bool InLandPurchase;

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

        private bool _visible;
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
        private bool _adjustMode;
        private float _nextAdjustRepeat;

        private string _message = string.Empty;
        private float _messageUntil;

        private GUIStyle _text;
        private GUIStyle _title;
        private Font _font;
        private int _styleFontSize;

        private readonly HashSet<string> _hiddenTags = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> _sourceSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<int, Texture2D> _silhouettes = new Dictionary<int, Texture2D>();
        private readonly string[] _reasonNames = new string[ChartRenderer.ReasonCount];
        private Sprite _gehennaFace;
        private bool _gehennaFaceResolved;
        private float _nextFaceProbe;
        private int _faceAttempts;
        private readonly List<Sprite> _silhouettePending = new List<Sprite>();
        private readonly ChartData _chart = new ChartData();
        private readonly ChartRenderer _renderer = new ChartRenderer();
        private ImguiPainter _painter;
        private readonly GUIContent _content = new GUIContent();
        private readonly Vector3[] _frameCorners = new Vector3[4];

        // ------------------------------------------------------------------
        // 起動
        // ------------------------------------------------------------------
        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "false にすると記録も表示も止める（素のゲームと同じ挙動）");
            _embedInStatsWindow = Config.Bind("General", "EmbedInStatsWindow", true,
                "本体の統計窓（収支タブ）に用意されている空き枠にグラフを描く。"
                + "false にすると自前のパネルだけになる");
            _keepDataOnReload = Config.Bind("General", "KeepDataOnReload", false,
                "ScriptEngine で読み直したとき、それまでの記録を引き継ぐ（開発用）。"
                + "ScriptEngine を入れていなければ何も起きない。"
                + "false にすると読み直しのたびに取り直す");
            _autoShowOnResult = Config.Bind("General", "AutoShowOnResult", false,
                "リザルトに入ったら自前のパネルを自動で出す。"
                + "統計窓の枠に描いているなら不要（リザルトの「統計」から開けば出る）");

            _bucketSecondsInitial = Config.Bind("Recording", "BucketSeconds", 1f,
                "グラフ1本ぶんの時間（秒）。細かいほど詳しいが、上限を超えると自動で倍になる");
            _maxBuckets = Config.Bind("Recording", "MaxBuckets", 1800,
                "バケット数の上限。超えると2つずつ束ねて幅を倍にする（ラン全体が必ず収まる）");
            _maxRawEvents = Config.Bind("Recording", "MaxRawEvents", 300000,
                "CSV 書き出し用にためる生ログの上限件数。0 でためない");

            _startMode = Config.Bind("HUD", "StartMode", 0,
                "起動時にどの表示から始めるか。0=工場の収支／1=残高の推移／2=返済区切りの収支情報");
            _maxColumns = Config.Bind("HUD", "MaxColumns", 480,
                "グラフを何本の縦棒で描くか。IMGUI は1本が描画1回なので、増やすほど重い");
            _iconOutline = Config.Bind("HUD", "IconOutlineWidth", 0.05f,
                "アイコンの白縁の太さ（アイコンの大きさに対する割合）。0 で縁無し");
            _fontSize = Config.Bind("HUD", "FontSize", 14,
                "文字サイズ");
            _fontName = Config.Bind("HUD", "FontName", "",
                "使うフォント名（例: Yu Gothic UI）。空なら Unity の既定フォント。日本語が □ になるときだけ指定する");
            _panelRect = Config.Bind("Layout", "PanelRect", "0.06,0.08,0.88,0.78",
                "通常時のパネル位置（画面比 x,y,w,h）。ゲーム中に AdjustLayout キーで動かせる");
            _resultPanelRect = Config.Bind("Layout", "ResultPanelRect", "0.05,0.50,0.62,0.46",
                "リザルト時のパネル位置（画面比 x,y,w,h）");

            // 統計窓の枠に描くようになってから、窓の操作（タグタブ・収入/支出タブ・
            // 累計/直近1分）にすべて従うようにしたので、その代わりのキーは要らなくなった。
            // 既定は空＝無効。処理そのものは残してある——
            // グラフの上にボタンを置くとき、そのまま呼べるようにするため。
            _keyToggleSpec = Config.Bind("Keys", "Toggle", "",
                "単独パネルの開閉。空で無効（統計窓の枠に描くので普段は要らない）");
            _keyNextTagSpec = Config.Bind("Keys", "NextTag", "",
                "次のタグ。空で無効（統計窓のタグタブに従うため）");
            _keyPrevTagSpec = Config.Bind("Keys", "PrevTag", "",
                "前のタグ。空で無効");
            _keyExportSpec = Config.Bind("Keys", "ExportCsv", "F8", "CSV 書き出し");
            _graphAreaWidth = Config.Bind("Layout", "GraphAreaWidth", 0,
                "統計窓のグラフ枠の幅を広げる（本体の既定は 534、左のカード欄が 1068）。"
                + "0 なら本体のまま触らない。例: 900 にするとカード欄が 702 に縮んでグラフが広くなる");

            _keyCycleModeSpec = Config.Bind("Keys", "CycleMode", "F5",
                "表示の切替：工場の収支→残高の推移→返済区切りの収支情報");
            _keyCycleRangeSpec = Config.Bind("Keys", "CycleRange", "",
                "横軸の範囲：全体／直近1分。空で無効（統計窓の「累計／直近1分」に従うため）");
            _keyToggleSideSpec = Config.Bind("Keys", "ToggleSide", "",
                "構成を収入側と支出側で切替。空で無効（統計窓の収入／支出タブに従うため）");
            _keyAdjustSpec = Config.Bind("Keys", "AdjustLayout", "",
                "レイアウト調整モードの ON/OFF。矢印で移動、Shift+矢印で大きさ、Ctrl+矢印で1px単位。抜けると設定に保存する");

            _keyToggle = Hotkey.Parse(_keyToggleSpec.Value);
            _keyNextTag = Hotkey.Parse(_keyNextTagSpec.Value);
            _keyPrevTag = Hotkey.Parse(_keyPrevTagSpec.Value);
            _keyExport = Hotkey.Parse(_keyExportSpec.Value);
            _keyAdjust = Hotkey.Parse(_keyAdjustSpec.Value);
            _keyCycleMode = Hotkey.Parse(_keyCycleModeSpec.Value);
            _keyCycleRange = Hotkey.Parse(_keyCycleRangeSpec.Value);
            _keyToggleSide = Hotkey.Parse(_keyToggleSideSpec.Value);

            _bucketSeconds = _bucketSecondsInitial.Value > 0.05f ? _bucketSecondsInitial.Value : 1f;
            _painter = new ImguiPainter(this);
            _mode = Mathf.Clamp(_startMode.Value, 0, ChartRenderer.ModeCount - 1);
            PatchRecorderConstructor();
            AttachStore();
            RestoreState();

            // ゲームの版も出す。不具合の報告にログを添えてもらえば、
            // どの版で起きたのかが一目で分かる
            string gameVersion;
            try { gameVersion = Application.version; }
            catch (Exception) { gameVersion = "?"; }

            Logger.LogInfo("[boot] " + PluginName + " " + PluginVersion
                + "  (ゲーム " + gameVersion + ")"
                + "  " + _keyToggleSpec.Value + ":開閉  " + _keyExportSpec.Value + ":CSV  "
                + _keyAdjustSpec.Value + ":レイアウト");
        }

        private void OnDestroy()
        {
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
        }

        // ------------------------------------------------------------------
        // 読み直しをまたぐ記録の持ち越し（開発用）
        // ------------------------------------------------------------------
        private const string StateKey = "lwf.economygraph.store.v2";
        private const int StoreVersion = 2;
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
            if (_keepDataOnReload.Value)
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

                if (_keepDataOnReload.Value)
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

                    // 土地購入はこちらで足した理由番号なので、旗ではなくログの値をそのまま使う
                    InLandPurchase = false;
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
                Logger.LogInfo("[state] 記録を引き継いだ（" + events + " 件 / 返済 " + _repaidTotal + " 回）");
            }
            catch (Exception e)
            {
                Logger.LogWarning("記録の引き継ぎに失敗（新しく取り直す）: " + e);
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
                    Logger.LogWarning("記録係のコンストラクタが見つからない。定期的に探す方に落ちる");
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
                Logger.LogWarning("記録係の生成を捕まえられなかった（定期的に探す方に落ちる）: " + e.Message);
                _patched = false;
            }

            PatchLandPurchase();
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
                    Logger.LogWarning("土地購入が見つからない（その他のままになる）");
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
                Logger.LogWarning("土地購入を挟めなかった（その他のままになる）: " + e.Message);
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

        // ------------------------------------------------------------------
        // 毎フレーム
        // ------------------------------------------------------------------
        private void Update()
        {
            if (_enabled == null || !_enabled.Value || _brokenDown) { return; }

            try
            {
                TrackGame();
                TrackStatsWindow();
                BuildPendingSilhouettes();
                ResolveGehennaFace();
                HandleInput();
            }
            catch (Exception e)
            {
                ReportBreakdown("記録", e);
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
                Logger.LogError("[" + where + "] 例外: " + e);
            }

            if (_failures >= FailureLimit && !_brokenDown)
            {
                _brokenDown = true;
                Logger.LogError("例外が " + FailureLimit + " 回続いたので、このMODを止めました。"
                    + "本体の更新で内部の作りが変わった可能性があります。"
                    + "設定の Enabled を false にすれば警告も出ません");
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
                Unbind();
                _boundGame = gsm;
                if (gsm != null)
                {
                    // 読み直しで戻ってきた場合は同じ走行なので消さない
                    if (gsm.GetInstanceID() != _restoredGameId) { ResetRun(); }
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
                while (_bucketCount > _maxBuckets.Value && _maxBuckets.Value >= 16) { Downsample(); }
            }

            TrackRepayment();
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
                Logger.LogInfo("[run] 返済 " + _closedColumns + " 回目（イベント無し）  " + FormatTime(_elapsed));
            }
        }

        /// <summary>返済1回ぶんの列を閉じて、次の列へ進む。</summary>
        private void CloseColumn()
        {
            _closedColumns++;
            _periodEnds.Add(_elapsed);
            _periodRepaid.Add(_closedColumns);
            _periodIndex++;
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

            ApplyGraphAreaWidth(_graphFrame);

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
                Logger.LogInfo("[stats] 収支グラフの枠に描いている（under development を退けた）");
            }
        }

        /// <summary>
        /// グラフ枠の取り分を広げる。本体は カード欄 1068 ／ グラフ枠 534 で並べているが、
        /// 収入源が少ないうちは左が大きく空く。合計幅は変えずに配分だけ変える。
        /// タブを切り替えると本体が作り直すので、枠を見つけるたびに入れ直す。
        /// </summary>
        private void ApplyGraphAreaWidth(RectTransform frame)
        {
            int want = _graphAreaWidth.Value;
            if (want <= 0 || frame == null) { return; }

            RectTransform area = frame.parent as RectTransform;
            if (area == null) { return; }
            RectTransform content = area.parent as RectTransform;
            if (content == null) { return; }

            Transform cardsTransform = content.Find("StatsBalanceCardsArea");
            RectTransform cards = cardsTransform as RectTransform;

            float total = area.rect.width + (cards != null ? cards.rect.width : 0f);
            if (total < 400f) { return; }

            float graphWidth = Mathf.Clamp(want, 200f, total - 200f);
            SetAreaWidth(area, graphWidth);
            if (cards != null) { SetAreaWidth(cards, total - graphWidth); }
        }

        private static void SetAreaWidth(RectTransform rt, float width)
        {
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            LayoutElement element = rt.GetComponent<LayoutElement>();
            if (element != null)
            {
                element.preferredWidth = width;
                if (element.minWidth > 0f) { element.minWidth = width; }
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
                Logger.LogInfo("[run] 終了。経過 " + FormatTime(_elapsed)
                    + " / 記録 " + TotalRecordCount() + " 件");
            }

            if (_isResultShown) { return; }
            if (Time.unscaledTime < _nextResultPoll) { return; }
            _nextResultPoll = Time.unscaledTime + 1f;

            if (!IsResultWindowUp()) { return; }

            _isResultShown = true;
            if (_autoShowOnResult.Value) { _visible = true; }
            Logger.LogInfo("[run] リザルト画面を検出した");
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
                    _subscriptions.Add(recorder.OnTagRecorded.Subscribe(new Action<TagRecordedEvent>(OnTagRecorded)));
                }
                catch (Exception) { }
            }
            NewRecorders.Clear();
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
                    _subscriptions.Add(main.OnTagRecorded.Subscribe(new Action<TagRecordedEvent>(OnTagRecorded)));
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

                    _subscriptions.Add(recorder.OnTagRecorded.Subscribe(new Action<TagRecordedEvent>(OnTagRecorded)));
                }

                if (first && main != null)
                {
                    SeedInitialBalances(main);
                    Logger.LogInfo("[run] 記録を開始した（記録係 " + _subscribed.Count + " 件）");
                }
            }
            catch (Exception e)
            {
                Logger.LogError("記録係への購読に失敗しました: " + e);
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
                    if (have == 0) { continue; }
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
            _bucketSeconds = _bucketSecondsInitial.Value > 0.05f ? _bucketSecondsInitial.Value : 1f;
            _bucketCount = 0;
            _elapsed = 0.0;
            _seeded = false;
            _runEndElapsed = -1.0;
            _isResultShown = false;
            _nextResultPoll = 0f;
            _selectedTag = 0;
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

            int reason = (int)ev.statsCashReason;
            if (reason < 0 || reason >= ChartRenderer.ReasonCount) { reason = 0; }

            // 土地の購入だけは本体が理由を付けないので、こちらで付け直す
            if (InLandPurchase && !ev.isAdd && reason == (int)StatsCashReason.Other) { reason = LandReason; }

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

            if (!_replaying) { RecordRaw(s, ev, t, reason); }

            while (_bucketCount > _maxBuckets.Value && _maxBuckets.Value >= 16) { Downsample(); }
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
                || r == StatsCashReason.CraftExpense;
        }

        private static void AddSource(Dictionary<string, long> dict, string sourceID, int delta)
        {
            if (string.IsNullOrEmpty(sourceID)) { return; }
            long current;
            dict.TryGetValue(sourceID, out current);
            dict[sourceID] = current + delta;
        }

        private void RecordRaw(TagSeries s, TagRecordedEvent ev, double t, int reason)
        {
            int cap = _maxRawEvents.Value;
            if (cap <= 0) { return; }
            if (_evT.Count >= cap) { _rawEventsTruncated = true; return; }

            int tagIndex = s.Index;
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

            _evT.Add((float)t);
            _evDelta.Add(ev.isAdd ? ev.delta : -ev.delta);
            _evBalance.Add(ev.newTotal);
            _evTag.Add(tagIndex);
            _evReason.Add(reason);
            _evSource.Add(sourceIdx);
            _evPeriod.Add(_periodIndex);
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
            if (idx < ChartRenderer.KnownTagNamesJa.Length) { return ChartRenderer.KnownTagNamesJa[idx]; }
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
            Logger.LogInfo("[graph] バケット幅を " + F(_bucketSeconds) + " 秒にした");
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

            if (_keyToggle != null && _keyToggle.WasPressedThisFrame(kb))
            {
                _visible = !_visible;
                if (!_visible && _adjustMode) { LeaveAdjustMode(); }
            }
            if (_keyCycleMode != null && _keyCycleMode.WasPressedThisFrame(kb))
            {
                _mode = (_mode + 1) % ChartRenderer.ModeCount;
            }
            if (_keyCycleRange != null && _keyCycleRange.WasPressedThisFrame(kb))
            {
                _lastMinute = !_lastMinute;
                Say(_lastMinute ? "直近1分" : "全体");
            }
            if (_keyToggleSide != null && _keyToggleSide.WasPressedThisFrame(kb))
            {
                _expenseSide = !_expenseSide;
            }
            if (_keyNextTag != null && _keyNextTag.WasPressedThisFrame(kb)) { CycleTag(1); }
            if (_keyPrevTag != null && _keyPrevTag.WasPressedThisFrame(kb)) { CycleTag(-1); }
            if (_keyExport != null && _keyExport.WasPressedThisFrame(kb)) { ExportCsv(); }
            if (_keyAdjust != null && _keyAdjust.WasPressedThisFrame(kb))
            {
                if (_adjustMode) { LeaveAdjustMode(); }
                else
                {
                    _adjustMode = true;
                    _visible = true;
                    Say("レイアウト調整モード：矢印=移動  Shift+矢印=大きさ  Ctrl+矢印=1px  "
                        + _keyAdjustSpec.Value + "=確定");
                }
            }

            HandleIconClick();

            if (_adjustMode) { HandleAdjust(kb); }
        }

        private void CycleTag(int direction)
        {
            if (_ordered.Count == 0) { return; }
            _selectedTag = (_selectedTag + direction + _ordered.Count) % _ordered.Count;
            _visible = true;
        }

        private void HandleAdjust(Keyboard kb)
        {
            if (Time.unscaledTime < _nextAdjustRepeat) { return; }

            float dx = 0f, dy = 0f;
            if (kb.leftArrowKey.isPressed) { dx -= 1f; }
            if (kb.rightArrowKey.isPressed) { dx += 1f; }
            if (kb.upArrowKey.isPressed) { dy -= 1f; }
            if (kb.downArrowKey.isPressed) { dy += 1f; }
            if (dx == 0f && dy == 0f) { return; }

            bool fine = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            bool resize = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            float step = fine ? 1f : 6f;
            _nextAdjustRepeat = Time.unscaledTime + (fine ? 0.05f : 0.02f);

            Rect r = CurrentRectPixels();
            if (resize)
            {
                r.width = Mathf.Max(200f, r.width + dx * step);
                r.height = Mathf.Max(120f, r.height + dy * step);
            }
            else
            {
                r.x += dx * step;
                r.y += dy * step;
            }
            SetCurrentRectPixels(r);
        }

        private void LeaveAdjustMode()
        {
            _adjustMode = false;
            try { Config.Save(); }
            catch (Exception) { }
            Say("レイアウトを保存した： " + CurrentRectEntry().Value);
        }

        private ConfigEntry<string> CurrentRectEntry()
        {
            return _isResultShown ? _resultPanelRect : _panelRect;
        }

        private Rect CurrentRectPixels()
        {
            Rect n = ParseRect(CurrentRectEntry().Value);
            return new Rect(n.x * Screen.width, n.y * Screen.height, n.width * Screen.width, n.height * Screen.height);
        }

        private void SetCurrentRectPixels(Rect px)
        {
            float w = Screen.width, h = Screen.height;
            if (w <= 0f || h <= 0f) { return; }
            CurrentRectEntry().Value = F4(px.x / w) + "," + F4(px.y / h) + "," + F4(px.width / w) + "," + F4(px.height / h);
        }

        private static Rect ParseRect(string spec)
        {
            float x = 0.06f, y = 0.08f, w = 0.88f, h = 0.78f;
            if (!string.IsNullOrEmpty(spec))
            {
                string[] parts = spec.Split(',');
                if (parts.Length == 4)
                {
                    float px, py, pw, ph;
                    if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out px) &&
                        float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out py) &&
                        float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out pw) &&
                        float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ph))
                    {
                        x = px; y = py; w = pw; h = ph;
                    }
                }
            }
            if (w < 0.1f) { w = 0.1f; }
            if (h < 0.08f) { h = 0.08f; }
            return new Rect(x, y, w, h);
        }

        // ------------------------------------------------------------------
        // CSV
        // ------------------------------------------------------------------
        private void ExportCsv()
        {
            if (_series.Count == 0) { Say("書き出すものが無い（記録が空）"); return; }

            try
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "LwfEconomy");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(dir, "economy_" + stamp + ".csv");

                StringBuilder sb = new StringBuilder(1 << 16);
                sb.Append("# ").Append(PluginName).Append(' ').Append(PluginVersion).Append('\n');
                sb.Append("# elapsed_seconds=").Append(F(_elapsed))
                  .Append(" bucket_seconds=").Append(F(_bucketSeconds))
                  .Append(" records=").Append(TotalRecordCount())
                  .Append(" raw=").Append(_evT.Count)
                  .Append(_rawEventsTruncated ? " (上限で打ち切り)" : "")
                  .Append('\n');
                sb.Append("t_sec,tag,delta,balance,reason,source\n");

                for (int i = 0; i < _evT.Count; i++)
                {
                    int tagIndex = _evTag[i];
                    string tag = (tagIndex >= 0 && tagIndex < _series.Count) ? _series[tagIndex].TagID : "?";
                    string source = (_evSource[i] >= 0 && _evSource[i] < _sourceIDs.Count) ? _sourceIDs[_evSource[i]] : "";
                    sb.Append(_evT[i].ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                      .Append(tag).Append(',')
                      .Append(_evDelta[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(_evBalance[i].ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(ReasonEnumName(_evReason[i])).Append(',')
                      .Append(Csv(source)).Append('\n');
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Logger.LogInfo("CSV を書き出した: " + path);
                Say("CSV を書き出した： " + path);
            }
            catch (Exception e)
            {
                Logger.LogError("CSV の書き出しに失敗しました: " + e);
                Say("CSV の書き出しに失敗： " + e.Message);
            }
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) { return ""; }
            if (value.IndexOf(',') < 0 && value.IndexOf('"') < 0) { return value; }
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>CSV に書く用。表示用の呼び名ではなく、列挙の名前をそのまま出す。</summary>
        private static string ReasonEnumName(int reason)
        {
            if (reason == LandReason) { return "LandPurchase"; }
            if (reason < 0 || reason >= ChartRenderer.ReasonCount) { return "Unknown"; }
            return ((StatsCashReason)reason).ToString();
        }

        // ------------------------------------------------------------------
        // 描画
        // ------------------------------------------------------------------
        private void Say(string msg)
        {
            _message = msg;
            _messageUntil = Time.unscaledTime + 6f;
        }

        private void OnGUI()
        {
            if (_enabled == null || !_enabled.Value || !_hasRun || _brokenDown) { return; }
            if (Event.current.type != EventType.Repaint) { return; }

            try { DrawFrame(); }
            catch (Exception e) { ReportBreakdown("描画", e); }
        }

        private void DrawFrame()
        {
            EnsureStyles();

            // 統計窓が開いていて、収支タブの空き枠が見つかっているならそこに描く。
            // 見つからないときだけ自前のパネルを出す。
            _drewThisFrame = false;

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
            else if (_visible)
            {
                Rect panel = CurrentRectPixels();
                DrawPanel(panel);
                _lastDrawn = panel;
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
                if (_menuObjects.Count == 0) { Logger.LogWarning("メニューの開閉を読めなかった"); }
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
            if (_text != null && _styleFontSize == _fontSize.Value) { return; }
            _styleFontSize = _fontSize.Value;

            if (_font == null && !string.IsNullOrEmpty(_fontName.Value))
            {
                try { _font = Font.CreateDynamicFontFromOSFont(_fontName.Value, _fontSize.Value); }
                catch (Exception) { _font = null; }
            }

            _text = new GUIStyle(GUI.skin.label);
            _text.fontSize = _fontSize.Value;
            _text.normal.textColor = Color.white;
            _text.alignment = TextAnchor.UpperLeft;
            _text.richText = false;
            _text.wordWrap = false;
            _text.padding = new RectOffset(0, 0, 0, 0);
            _text.margin = new RectOffset(0, 0, 0, 0);
            if (_font != null) { _text.font = _font; }

            _title = new GUIStyle(_text);
            _title.fontSize = _fontSize.Value + 3;
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
                        Color old = GUI.color;
                        GUI.color = outline;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                if (dx == 0 && dy == 0) { continue; }
                                Rect offset = new Rect(r.x + dx * step, r.y + dy * step, r.width, r.height);
                                GUI.DrawTexture(SpriteDest(offset, sp), silhouette, ScaleMode.StretchToFill, true);
                            }
                        }
                        GUI.color = old;
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

            // 納品はゲヘナの顔。本体が自分の UI で顔アイコンに使っているものをそのまま借りる
            if (reason == (int)StatsCashReason.RepaymentExpense)
            {
                Sprite face = GetGehennaFace();
                if (face != null) { return face; }
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
        /// 納品に当てるゲヘナの顔。
        ///
        /// ゲーム内の顔アイコンは InGameFaceIcons という 4x4 のシートに入っていて、
        /// **右下のコマ**がゲヘナ。立ち絵（portrait_patron_*）は縮めると潰れるので、
        /// 最初から小さく描かれているこちらを切り取って使う。
        /// 切り取りは Sprite.Create で矩形を指すだけ（画素は読まないので isReadable は不要）。
        /// シートが見つからなければ本体の顔アイコンに落ちる。
        /// </summary>
        private Sprite GetGehennaFace()
        {
            return _gehennaFace;
        }

        /// <summary>
        /// ゲヘナの顔を用意する。**描画からは呼ばない**——
        /// シート探しは Resources.FindObjectsOfTypeAll で全テクスチャを走査するので、
        /// 描画のたびに呼ぶと（アイコン1つごとに）確実に重くなる。
        /// Update から数回だけ試して、駄目なら本体の顔アイコンで打ち切る。
        /// </summary>
        private void ResolveGehennaFace()
        {
            if (_gehennaFaceResolved) { return; }
            if (Time.unscaledTime < _nextFaceProbe) { return; }
            _nextFaceProbe = Time.unscaledTime + 3f;
            _faceAttempts++;

            Texture2D sheet = FindTexture("InGameFaceIcons");
            if (sheet != null)
            {
                try
                {
                    float w = sheet.width * 0.25f;
                    float h = sheet.height * 0.25f;
                    // Rect の原点は左下。右下のコマは x=3/4, y=0
                    _gehennaFace = Sprite.Create(sheet, new Rect(sheet.width - w, 0f, w, h),
                        new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                    _gehennaFaceResolved = true;
                    return;
                }
                catch (Exception) { _gehennaFace = null; }
            }

            if (_faceAttempts >= 5)
            {
                // 見つからないものを探し続けない。立ち絵で妥協して打ち切る
                try { _gehennaFace = Patrons.GetFaceIcon(Patron.Gehenna); }
                catch (Exception) { _gehennaFace = null; }
                _gehennaFaceResolved = true;
            }
        }

        private static Texture2D FindTexture(string name)
        {
            try
            {
                Texture2D[] all = Resources.FindObjectsOfTypeAll<Texture2D>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && string.Equals(all[i].name, name, StringComparison.Ordinal))
                    {
                        return all[i];
                    }
                }
            }
            catch (Exception) { }
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
            if (!string.IsNullOrEmpty(ReasonIconTag[reason])
                || reason == (int)StatsCashReason.RepaymentExpense)
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

        /// <summary>統計窓を開いていないとき用の単独パネル。</summary>
        private void DrawPanel(Rect panel)
        {
            Fill(panel, new Color(0.04f, 0.05f, 0.07f, 0.88f));
            Fill(new Rect(panel.x, panel.y, panel.width, 2f), new Color(1f, 0.62f, 0.2f, 0.9f));

            float pad = 10f;
            DrawChart(new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2f, panel.height - pad * 2f));

            if (_adjustMode)
            {
                Fill(new Rect(panel.x, panel.y, panel.width, 2f), Color.cyan);
                Fill(new Rect(panel.x, panel.yMax - 2f, panel.width, 2f), Color.cyan);
                Fill(new Rect(panel.x, panel.y, 2f, panel.height), Color.cyan);
                Fill(new Rect(panel.xMax - 2f, panel.y, 2f, panel.height), Color.cyan);
            }

            if (Time.unscaledTime < _messageUntil && _message.Length > 0)
            {
                DrawText(panel.x + pad, panel.yMax - (_text.fontSize + 10f), ">> " + _message, _text, Color.white);
            }
        }

        private void DrawChart(Rect area)
        {
            FillChartData();
            _painter.Begin(_fontSize.Value);
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
            _chart.MaxColumns = _maxColumns.Value;
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
        private Sprite GetSourceSprite(string sourceKey, string tagID)
        {
            Sprite cached;
            if (_sourceSprites.TryGetValue(sourceKey, out cached)) { return cached; }

            Sprite sp = null;
            try { sp = ItemParamGetter.GetSprite(sourceKey); }
            catch (Exception) { sp = null; }
            if (sp == null)
            {
                try { sp = TagParamGetter.GetSprite(sourceKey); }
                catch (Exception) { sp = null; }
            }
            if (sp == null) { sp = GetRecipeResultSprite(sourceKey); }
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
                        Logger.LogWarning("統計窓の " + methodName + " が見つからない（窓の外でだけ切り替わる）");
                    }
                }

                if (method != null) { method.Invoke(_statsWindow, args); }
            }
            catch (Exception e)
            {
                Logger.LogWarning("統計窓の " + methodName + " を呼べなかった: " + e.Message);
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
                if (field == null) { Logger.LogWarning("統計窓の " + fieldName + " を読めなかった"); }
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

        private static string F4(float v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
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
