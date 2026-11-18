// 業務日誌 — プラグイン本体の続き（partial）。
//
// 本体（0.32.0）の執務室には「業務日誌」ボタンが置いてあるが、半透明・操作不可で、押しても音が鳴るだけ。
// 本体は Run の結果を JSON で残している（RunData.cs）ものの合計しか無く、
// 「どういう Run だったか」は追えない。そこで、
//   1. Run が終わったら、グラフの記録（置き場＝AttachStore の中身）をファイルに残す
//   2. 執務室のボタンから画面（LogView.cs）を開き、選んだ Run の記録を置き場へ読み戻して流し直す
// とする。流し直しは F6 の読み直し用にもともとある仕組み（RestoreState）をそのまま使うので、
// 描かれるのは統計画面と同じグラフで、モード切替やホバーも同じに動く。
//
// -- Run との対応 -------------------------------------------------------
// 本体の Run の ID は internal の奥にあり、取りに行くと版上げに弱い。
// グラフのファイル名に「記録を始めた時刻（UTC）」を入れ、本体の記録の開始〜終了の間に
// 入っているもので対応させる（Run は重ならないので取り違えない）。
//
// -- 大きさ -------------------------------------------------------------
// 生ログは売却1個ごとに1件で、1 Run で十数万件になる。同じ秒・同じ出所の続きは
// 1件に束ねてから書く（流し直した結果は秒の刻みまで同じ）。さらに gzip で詰める。
//
// -- ボタンの生かし方 ---------------------------------------------------
// 本体のボタン（UIOperationButton）は本体の入力の仕組みを通ったときだけ押下を流す。
// そこに乗ると版上げに弱いので、ボタンの上に透明な普通の Button を1枚かぶせて受ける。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameState;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LwfEconomyGraph
{
    public sealed partial class EconomyGraphPlugin
    {
        internal static EconomyGraphPlugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> LogEnabled;
        /// <summary>「工場を確認」の再生の速さ（ゲーム内の時間の何倍か）。100 で、30 秒おきの回が 0.3 秒ずつ。
        /// 再生の帯のボタンで切り替えた値の控え</summary>
        internal static ConfigEntry<int> PlaybackSpeed;
        internal static ConfigEntry<bool> ShowCleared;
        internal static ConfigEntry<bool> ShowBankrupt;
        internal static ConfigEntry<bool> ShowAbandoned;
        internal static ConfigEntry<bool> ShowGrid;
        internal static ConfigEntry<bool> ShowPactEffects;
        internal static ConfigEntry<bool> ShowFullNumbers;
        internal static ConfigEntry<bool> ShowCraftRate;
        internal static ConfigEntry<bool> ShowHidden;
        internal static ConfigEntry<bool> OnlyFavorites;

        private const string OfficeScene = "Office";
        private const string ArchiveButtonName = "ButtonArchives";
        private const string ArchiveHitName = "EconomyGraphLogHit";

        private const string GraphSuffix = ".graph.gz";
        private const string GraphStamp = "yyyyMMdd_HHmmss";
        /// <summary>版 1 は、列を閉じた返済の支出を1つ後ろの列の番号で書いていた（読むときに直す）。版 2 から正しい番号</summary>
        private const int GraphFileVersion = 2;
        private const string RunStartKey = "lwf.economygraph.runstart";

        /// <summary>いまの記録を始めた時刻（UTC）。グラフのファイル名になる</summary>
        private DateTime _runStartUtc;

        /// <summary>いま置き場にある記録は保存済みか（ファイルから読み戻したものも保存済み扱い）</summary>
        private bool _graphSaved = true;
        private const string GraphSavedKey = "lwf.economygraph.saved";

        /// <summary>保存済みの印を付け替える。F6 の読み直しをまたいで保つ（置き場と同じく AppDomain に置く）。
        /// これが無いと、業務日誌で読み戻した Run を、読み直しのあとに「まだ保存していない記録」と取り違えて書き直す</summary>
        private void SetGraphSaved(bool saved)
        {
            _graphSaved = saved;
            try { AppDomain.CurrentDomain.SetData(GraphSavedKey, saved); }
            catch (Exception) { }
        }

        /// <summary>置き場に読み戻してある Run のファイル。同じものを読み直さないため</summary>
        private string _loadedGraph;

        private float _nextLogPoll;
        private bool _archiveMissLogged;

        private void BootLog()
        {
            Instance = this;
            HookPactCards();
            PatchEndRequest();
            PatchCraftCompleted();
            Log = Logger;
            LogEnabled = Config.Bind("BusinessLog", "Enabled", true, "");

            // 画面のボタンやチェックで切り替えた値の控え。人が書き換える設定ではないので、設定ファイル（.cfg）には
            // 並べず、記録の隣の別ファイルに持つ（ConfigurationManager の一覧にも出ない）
            ConfigFile view = new ConfigFile(
                Path.Combine(Path.Combine(Paths.BepInExRootPath, "LwfEconomy"), "view.cfg"), true);
            // 一覧の絞り込み
            ShowCleared = view.Bind("List", "ShowCleared", true, "");
            ShowBankrupt = view.Bind("List", "ShowBankrupt", true, "");
            ShowAbandoned = view.Bind("List", "ShowAbandoned", true, "");
            ShowHidden = view.Bind("List", "ShowHidden", false, "");
            OnlyFavorites = view.Bind("List", "OnlyFavorites", false, "");
            // 配置図のマス目の線
            ShowGrid = view.Bind("Run", "ShowGrid", false, "");
            PlaybackSpeed = view.Bind("Run", "PlaybackSpeed", 100, "");
            // 契約履歴のカードに効果の文を出すか
            ShowPactEffects = view.Bind("Run", "ShowPactEffects", true, "");
            // 統計のカードの数を、K・M に丸めず全部の桁で出すか
            ShowFullNumbers = view.Bind("Run", "ShowFullNumbers", false, "");
            // クラフトの表で、1回の秒数の代わりに毎秒の数を出すか
            ShowCraftRate = view.Bind("Run", "ShowCraftRate", false, "");

            // F6 の読み直しをまたいでも、記録を始めた時刻は同じ Run のものを使う
            try
            {
                object ticks = AppDomain.CurrentDomain.GetData(RunStartKey);
                if (ticks is long) { _runStartUtc = new DateTime((long)ticks, DateTimeKind.Utc); }
            }
            catch (Exception) { }
            // 読み直しで引き継いだ記録は、まだ終わっていない Run のもの
            if (_hasRun)
            {
                // 引き継いだ記録が保存済みかどうかは、読み直す前の印に従う（印が無ければ未保存とみなす）
                object saved = null;
                try { saved = AppDomain.CurrentDomain.GetData(GraphSavedKey); }
                catch (Exception) { }
                _graphSaved = (saved is bool) && (bool)saved;
            }
        }

        private void DestroyLog()
        {
            UnhookPactCards();
            StopTaxRecorder();
            // F6 の読み直しで古い部品が残ると、消えたコードを指すボタンになる
            LogView.Close();
            GameObject hit = GameObject.Find(ArchiveHitName);
            if (hit != null) { Destroy(hit); }
            // 本体のボタンを、手を入れる前の状態に戻す
            if (_archiveGroup != null)
            {
                _archiveGroup.alpha = _archiveAlpha;
                _archiveGroup.interactable = _archiveInteractable;
                _archiveGroup.blocksRaycasts = _archiveBlocks;
                _archiveGroup = null;
            }
        }

        /// <summary>新しい Run の記録を始めたときに呼ぶ。</summary>
        private void MarkRunStart()
        {
            _runStartUtc = DateTime.UtcNow;
            SetGraphSaved(false);
            _loadedGraph = null;
            try { AppDomain.CurrentDomain.SetData(RunStartKey, _runStartUtc.Ticks); }
            catch (Exception) { }
        }

        internal const string SeedSuffix = ".seed.txt";

        /// <summary>その Run のマップシードを `<開始時刻>.seed.txt` に控える。
        /// 本体の Run の記録に残るのは、貼って始めたときのカスタムシードだけ。普通に始めた Run のシードは
        /// 残らないので、本体が持っている「いまの Run のシード」（難易度選択の画面に出るのと同じコード）を控える。</summary>
        private void SaveRunSeed()
        {
            try
            {
                string seed = Map.Initializer.MapSeedGameStart.CurrentSeedCode;
                if (string.IsNullOrEmpty(seed)) { return; }
                string dir = GraphDirectory();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, _runStartUtc.ToString(GraphStamp, CultureInfo.InvariantCulture) + SeedSuffix), seed);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[log] cannot save the map seed: " + e.Message);
            }
        }

        /// <summary>
        /// Run が終わる瞬間に記録を残せるようにする。
        ///
        /// 本体は Run の終わり方（クリア・時間切れ・タイトルへ戻る・ゲームを終了・エラー）をすべて
        /// GameStateManager.RequestEnd に通す。ここは盤面がまだ生きている最後の場所で、このあと
        /// 本体は片付けに入る。タイトルへ戻ったときは、こちらが「Run を離れた」と気づくのは場面が消えた後なので、
        /// 工場の最後の配置を撮れなかった。RequestEnd の頭で撮れば、どの終わり方でも最後の状態が残る。
        /// 当てられなければ、従来の合図（結果画面・Run を離れた・ゲームの終了）だけで動く。
        /// </summary>
        private void PatchEndRequest()
        {
            try
            {
                System.Reflection.MethodInfo target = AccessTools.Method(typeof(GameStateManager), "RequestEnd");
                System.Reflection.MethodInfo prefix = typeof(EconomyGraphPlugin).GetMethod("OnEndRequested",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (target == null || prefix == null)
                {
                    Logger.LogWarning("[log] GameStateManager.RequestEnd not found; the last state of an abandoned run is not kept");
                    return;
                }
                if (_harmony == null) { _harmony = new Harmony(PluginGuid); }
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                // 当たったかどうかをログに残す（読み直しを忘れて古い版のまま試していないかの見分けにもなる）
                Logger.LogInfo("[log] hooked the end of a run");
            }
            catch (Exception e)
            {
                Logger.LogWarning("[log] cannot hook the end of a run: " + e.Message);
            }
        }

        /// <summary>本体が Run を終えにかかった。本体の終了処理を止めないよう、ここでの失敗は握りつぶす。</summary>
        private static void OnEndRequested()
        {
            try
            {
                if (Instance == null) { return; }
                // 最後の返済がアイテムの納品だと、返済のイベントが出ず、列を閉じるのは次のフレームの見回り
                // （TrackRepayment）になる。本体は同じ呼び出しの中で Run を終えるので、先に見回りを済ませてから書く。
                // そうしないと、最後の1回が数に入らないまま保存され、保存済みの印で書き直しも効かない
                try { if (Instance._boundGame != null) { Instance.TrackRepayment(); } }
                catch (Exception) { }
                Instance.SaveRunGraphOnce("end of the run");
            }
            catch (Exception) { }
        }

        private void OnApplicationQuit()
        {
            SaveRunGraphOnce("quit");
        }

        // ------------------------------------------------------------------
        // 執務室
        // ------------------------------------------------------------------

        private void TrackLog()
        {
            if (!LogEnabled.Value) { return; }

            try
            {
                if (Time.unscaledTime >= _nextLogPoll)
                {
                    _nextLogPoll = Time.unscaledTime + 0.5f;
                    if (SceneManager.GetActiveScene().name == OfficeScene) { HookArchiveButton(); }
                    else if (LogView.IsOpen) { LogView.Close(); }
                }

                Keyboard kb = Keyboard.current;
                if (kb != null && LogView.IsOpen && kb.escapeKey.wasPressedThisFrame) { LogView.OnEscape(); }
            }
            catch (Exception e)
            {
                // グラフの記録（Update の本筋）は巻き込まない
                if (!_archiveMissLogged) { _archiveMissLogged = true; Logger.LogWarning("[log] " + e); }
            }
        }

        /// <summary>執務室の「業務日誌」ボタンを押せるようにする。本体が薄塗りに戻すことがあるので、居る間は押し戻す。</summary>
        private CanvasGroup _archiveGroup;
        private float _archiveAlpha;
        private bool _archiveInteractable, _archiveBlocks;
        private bool _archiveOfficialLogged;

        private void HookArchiveButton()
        {
            GameObject button = GameObject.Find(ArchiveButtonName);
            if (button == null)
            {
                if (!_archiveMissLogged) { _archiveMissLogged = true; Logger.LogWarning("[log] " + ArchiveButtonName + " not found"); }
                return;
            }

            if (button.transform.Find(ArchiveHitName) != null)
            {
                // もう手を入れてある。本体が「薄く・押せない」に戻していたら、押せる状態に戻し直す
                // （いまの本体にそういう動きは無いが、戻されると開けなくなるので念のため。0.5 秒ごとの見回りで足りる）
                if (_archiveGroup != null
                    && (!_archiveGroup.interactable || !_archiveGroup.blocksRaycasts || _archiveGroup.alpha < 1f))
                {
                    _archiveGroup.alpha = 1f;
                    _archiveGroup.interactable = true;
                    _archiveGroup.blocksRaycasts = true;
                }
                return;
            }

            // 本体のボタンは、未実装のあいだ CanvasGroup で薄く・押せなくしてある。
            // 最初から押せる状態なら、本体が業務日誌を実装したということなので、手を出さない
            // （かぶせたボタンが本体の画面をふさいでしまう）。
            CanvasGroup group = button.GetComponent<CanvasGroup>();
            if (group != null && group.interactable && group.blocksRaycasts && group.alpha >= 1f)
            {
                if (!_archiveOfficialLogged)
                {
                    _archiveOfficialLogged = true;
                    Logger.LogInfo("[log] the game's own " + ArchiveButtonName + " is usable; the mod leaves it alone");
                }
                return;
            }
            if (group != null)
            {
                // 元の状態を覚えておき、MOD を外す（F6 で読み直す）ときに戻す。戻さないと、読み直した後に
                // 「最初から押せる＝本体が実装した」と取り違える
                _archiveGroup = group;
                _archiveAlpha = group.alpha;
                _archiveInteractable = group.interactable;
                _archiveBlocks = group.blocksRaycasts;
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }

            GameObject go = new GameObject(ArchiveHitName, typeof(RectTransform));
            RectTransform r = go.GetComponent<RectTransform>();
            r.SetParent(button.transform, false);
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            Image image = go.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0f);
            Button hit = go.AddComponent<Button>();
            hit.transition = Selectable.Transition.None;
            hit.targetGraphic = image;
            hit.onClick.AddListener(delegate { LogView.Open(); });
            Logger.LogInfo("[log] hooked " + ArchiveButtonName);

            // 執務室に入るたびに写す。本体は Run を終えるごとに古い記録を1件消すので、
            // 画面を開いたときだけ写していると、開かずに 100 回遊んだ分が抜ける
            try { RunData.Sync(); }
            catch (Exception e) { Logger.LogWarning("[log] cannot keep copies: " + e.Message); }
        }

        /// <summary>業務日誌の「収入／支出」の切り替えに、グラフの向きを合わせる。</summary>
        internal void SetLogSide(bool expense)
        {
            _expenseSide = expense;
        }

        /// <summary>画面のグラフの枠に、読み戻してある Run のグラフを描く。描いたら true。</summary>
        private bool DrawLogGraph()
        {
            if (_loadedGraph == null || !LogView.IsOpen) { return false; }
            Rect area;
            if (!TryGetScreenRect(LogView.GraphArea, out area)) { return false; }
            DrawPanel(area);
            _lastDrawn = area;
            return true;
        }

        // ------------------------------------------------------------------
        // グラフの記録を残す
        // ------------------------------------------------------------------

        private static string GraphDirectory()
        {
            return RunData.CopyDirectory();
        }

        /// <summary>いまの Run の記録を1回だけ残す。結果画面が出たとき・Run を離れたとき・ゲームを終えるときに呼ぶ。</summary>
        private void SaveRunGraphOnce(string why)
        {
            if (_graphSaved || !_hasRun) { return; }
            if (LogEnabled == null || !LogEnabled.Value) { return; }
            SetGraphSaved(true);
            if (_runStartUtc.Ticks == 0L) { _runStartUtc = DateTime.UtcNow.AddSeconds(-_elapsed); }

            // 資源の増減が1件も無い Run（何も売らずに離れた等）は、グラフの控えだけ書かない。
            // 配置・契約・レシピ・シード・課税の控えは、グラフと関係なく残す。前はここで全部やめていたので、
            // そういう Run では課税が発動していても控えが書かれず、業務日誌に内訳が出なかった
            bool hasGraph = _evT != null && _evT.Count > 0;
            if (hasGraph) try
            {
                SyncScalars();
                string dir = GraphDirectory();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, _runStartUtc.ToString(GraphStamp, CultureInfo.InvariantCulture) + GraphSuffix);
                string temp = path + ".tmp";
                int written;
                using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write))
                using (GZipStream zip = new GZipStream(file, CompressionMode.Compress))
                using (BinaryWriter w = new BinaryWriter(zip))
                {
                    written = WriteGraph(w);
                }
                if (File.Exists(path)) { File.Delete(path); }
                File.Move(temp, path);
                Logger.LogInfo("[log] saved the graph (" + why + "): " + Path.GetFileName(path) + "  "
                    + _evT.Count + " records as " + written + ", " + new FileInfo(path).Length + " bytes"
                    + (_rawEventsTruncated ? "  (records truncated at MaxRawEvents)" : ""));
            }
            catch (Exception e)
            {
                Logger.LogWarning("[log] cannot save the graph: " + e);
            }

            SaveMapShots();
            SavePactCards();
            SaveRecipes();
            SaveRunSeed();
            SaveRunTaxes(why);
            // 書いた直後に探しても見つかるよう、覚えておいたフォルダの中身を捨てる
            _runFilesAt = -100f;
        }

        /// <summary>置き場の中身を書く。生ログは束ねてから書き、書いた件数を返す。</summary>
        private int WriteGraph(BinaryWriter w)
        {
            w.Write(GraphFileVersion);
            w.Write(_runStartUtc.Ticks);
            w.Write(_targetProgress);

            w.Write(_scalars.Length);
            for (int i = 0; i < _scalars.Length; i++) { w.Write(_scalars[i]); }

            w.Write(_periodEnds.Count);
            for (int i = 0; i < _periodEnds.Count; i++) { w.Write(_periodEnds[i]); }
            w.Write(_periodRepaid.Count);
            for (int i = 0; i < _periodRepaid.Count; i++) { w.Write(_periodRepaid[i]); }

            w.Write(_storeTagIDs.Count);
            for (int i = 0; i < _storeTagIDs.Count; i++)
            {
                w.Write(_storeTagIDs[i]);
                w.Write(_storeInitial[i]);
                List<long> balances = _storeBalances[i];
                w.Write(balances.Count);
                for (int b = 0; b < balances.Count; b++) { w.Write(balances[b]); }
            }

            w.Write(_sourceIDs.Count);
            for (int i = 0; i < _sourceIDs.Count; i++) { w.Write(_sourceIDs[i]); }

            // 続いている「同じ秒・同じタグ・同じ向き・同じ理由・同じ出所・同じ列」を1件に束ねる。
            // 並びは変えない（台帳は収入と支出の順で結果が変わる）ので、束ねるのは隣どうしだけ
            List<int> starts = new List<int>();
            for (int i = 0; i < _evT.Count; i++)
            {
                if (i > 0 && SameGraphGroup(i - 1, i)) { continue; }
                starts.Add(i);
            }
            w.Write(starts.Count);
            for (int g = 0; g < starts.Count; g++)
            {
                int first = starts[g];
                int end = g + 1 < starts.Count ? starts[g + 1] : _evT.Count;
                int delta = 0;
                for (int i = first; i < end; i++) { delta += _evDelta[i]; }
                w.Write(_evT[first]);
                w.Write(delta);
                w.Write(_evBalance[end - 1]);
                w.Write(_evTag[first]);
                w.Write(_evReason[first]);
                w.Write(_evSource[first]);
                w.Write(_evPeriod[first]);
            }
            return starts.Count;
        }

        private bool SameGraphGroup(int a, int b)
        {
            return _evTag[a] == _evTag[b]
                && _evReason[a] == _evReason[b]
                && _evSource[a] == _evSource[b]
                && _evPeriod[a] == _evPeriod[b]
                && (_evDelta[a] > 0) == (_evDelta[b] > 0)
                && (int)(_evT[a] / _bucketSeconds) == (int)(_evT[b] / _bucketSeconds);
        }

        // ------------------------------------------------------------------
        // グラフの記録を読み戻す
        // ------------------------------------------------------------------

        /// <summary>その Run の記録のファイル（グラフ・工場の配置）。無ければ null。</summary>
        internal static string FindRunFile(RunSummary run, string suffix)
        {
            try { return FindRunFileCore(run, suffix); }
            catch (Exception) { return null; }
        }

        // 記録のフォルダの中身（種類ごと）。Run を選ぶ・タブを替えるたびにフォルダを見直さないよう、数秒だけ覚えておく。
        // 新しい記録は Run の終わりに書かれ、見るのは執務室へ戻ってからなので、数秒の遅れは表に出ない
        private static readonly Dictionary<string, string[]> _runFiles = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static float _runFilesAt = -100f;

        private static string[] RunFiles(string dir, string suffix)
        {
            if (Time.unscaledTime - _runFilesAt > 3f)
            {
                _runFiles.Clear();
                _runFilesAt = Time.unscaledTime;
            }
            string[] files;
            if (!_runFiles.TryGetValue(suffix, out files))
            {
                files = Directory.GetFiles(dir, "*" + suffix);
                _runFiles[suffix] = files;
            }
            return files;
        }

        private static string FindRunFileCore(RunSummary run, string suffix)
        {
            string dir = GraphDirectory();
            if (!Directory.Exists(dir)) { return null; }

            // 記録係はシーンの組み立て中に作られ、本体の「開始」より少し前になりうる
            DateTime from = run.StartedUtc.AddSeconds(-120.0);
            // ただし前の Run が終わるより前には遡らない。すぐ終えた Run の直後だと、この2分の幅に前の Run の
            // 控えが入り、この Run に無い種類の控え（契約・課税など）として拾ってしまう
            DateTime previousEnd = RunData.PreviousEnd(run);
            if (previousEnd > from) { from = previousEnd; }
            string best = null;
            DateTime bestAt = DateTime.MinValue;
            foreach (string file in RunFiles(dir, suffix))
            {
                string name = Path.GetFileName(file);
                DateTime at;
                if (!DateTime.TryParseExact(name.Substring(0, name.Length - suffix.Length), GraphStamp,
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at))
                {
                    continue;
                }
                if (at < from || at > run.EndedUtc) { continue; }
                if (best == null || at > bestAt) { best = file; bestAt = at; }
            }
            return best;
        }

        /// <summary>選んだ Run のグラフを置き場へ読み戻す。グラフがあれば true。
        /// 置き場を書き換えるので、Run の最中（記録中）には読まない。</summary>
        internal bool LoadRunGraph(RunSummary run)
        {
            if (_boundGame != null || GameStateManager.Instance != null) { return false; }

            string path = FindRunFile(run, GraphSuffix);
            if (path == null) { return false; }
            if (path == _loadedGraph) { return true; }

            // 直前の Run の記録がまだ置き場にあるなら、上書きする前に残す
            SaveRunGraphOnce("before viewing");

            try
            {
                ResetRun();
                int target;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (GZipStream zip = new GZipStream(file, CompressionMode.Decompress))
                using (BinaryReader r = new BinaryReader(zip))
                {
                    target = ReadGraph(r);
                }
                // 置き場のゲーム番号は、次に始まる Run と取り違えないよう消しておく
                _scalars[SGameID] = 0.0;
                _scalars[SHasRun] = 1.0;
                RestoreState();
                _targetProgress = target;
                _selectedTag = 0;
                SetGraphSaved(true);
                // 流し直しに失敗すると置き場は空に戻っている
                bool ok = _hasRun && _evT.Count > 0;
                _loadedGraph = ok ? path : null;
                return ok;
            }
            catch (Exception e)
            {
                Logger.LogWarning("[log] cannot read " + Path.GetFileName(path) + ": " + e.Message);
                ResetRun();
                _hasRun = false;
                SetGraphSaved(true);
                _loadedGraph = null;
                return false;
            }
        }

        /// <summary>ファイルの中身を置き場へ入れる（置き場のリストは作り直さず、中身だけ入れ替える）。</summary>
        private int ReadGraph(BinaryReader r)
        {
            int version = r.ReadInt32();
            if (version != 1 && version != GraphFileVersion) { throw new InvalidDataException("version " + version); }
            _runStartUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            int target = r.ReadInt32();

            int scalars = r.ReadInt32();
            for (int i = 0; i < scalars; i++)
            {
                double v = r.ReadDouble();
                if (i < _scalars.Length) { _scalars[i] = v; }
            }
            _scalars[SVersion] = StoreVersion;

            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) { _periodEnds.Add(r.ReadDouble()); }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) { _periodRepaid.Add(r.ReadInt32()); }

            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                _storeTagIDs.Add(r.ReadString());
                _storeInitial.Add(r.ReadInt64());
                int count = r.ReadInt32();
                List<long> balances = new List<long>(count);
                for (int b = 0; b < count; b++) { balances.Add(r.ReadInt64()); }
                _storeBalances.Add(balances);
            }

            n = r.ReadInt32();
            for (int i = 0; i < n; i++) { _sourceIDs.Add(r.ReadString()); }

            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                _evT.Add(r.ReadSingle());
                _evDelta.Add(r.ReadInt32());
                _evBalance.Add(r.ReadInt32());
                _evTag.Add(r.ReadInt32());
                _evReason.Add(r.ReadInt32());
                _evSource.Add(r.ReadInt32());
                _evPeriod.Add(r.ReadInt32());
            }

            // 版 1 の直し：列を閉じた返済の支出は、閉じた後の番号で書かれている。
            // 見分け方は時刻（列を閉じた時刻＝その返済の時刻）。当てはまるものを1つ前の列へ戻す
            if (version == 1)
            {
                for (int i = 0; i < _evPeriod.Count; i++)
                {
                    int p = _evPeriod[i];
                    if (_evDelta[i] >= 0 || _evReason[i] != (int)Items.Delivery.StatsCashReason.RepaymentExpense) { continue; }
                    if (p <= 0 || p - 1 >= _periodEnds.Count) { continue; }
                    if (Math.Abs(_periodEnds[p - 1] - _evT[i]) < 0.01) { _evPeriod[i] = p - 1; }
                }
            }
            return target;
        }
    }
}
