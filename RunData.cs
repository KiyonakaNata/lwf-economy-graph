// 本体が書いた Run の記録（RunHistory/Completed/*.run.json）を読む。
//
// 記録するのは本体。こちらは読むだけで、本体のファイルには一切書かない。
// 本体の読み込みクラス（RunHistoryJsonCodec など）は internal なうえ版ごとに形が変わるので使わず、
// JSON を直に読む。古い版の記録には無い項目があるので、どの項目も「無ければ空」で受ける
// （手元の 100 件は schemaVersion 4 が 70 件・5 が 30 件。2026-11-17）。
//
// 本体は直近 100 件だけ残して古い順に消す。それより前も見られるよう、写しを自前のフォルダに取る。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BaseSystem;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Utility.SaveData;

namespace LwfEconomyGraph
{
    /// <summary>一覧の1行に要る分だけ。本文（JObject）は重いので持たず、詳細を開くときに読み直す</summary>
    internal sealed class RunSummary
    {
        public string Path;
        // 記録のファイルの大きさ。要約の索引が古くなっていないかの見分けに使う
        public long Size;
        public long Sequence;
        public DateTime StartedLocal;
        // グラフの記録（RunGraph）と突き合わせるための時刻
        public DateTime StartedUtc;
        public DateTime EndedUtc;
        public string Result;
        public string EndReason;
        public double ElapsedSeconds;
        public string GameMode;
        public string Difficulty;
        public string Mission;
        // 任務パックの MOD（mods/mission_packs）の任務の名前。本体の記録には借り物の任務名で載るので、控えから拾う
        public string CustomMissionName;
        public string[] Patrons;
        public int RepaymentsDone;
        public int RepaymentsTarget;
    }

    internal static class RunData
    {
        private const string Suffix = ".run.json";

        // 完了した記録は本体が書き換えない（同じ名前なら同じ中身）ので、パスで覚えておけば読み直さずに済む
        private static readonly Dictionary<string, RunSummary> Cache = new Dictionary<string, RunSummary>();

        /// <summary>本体が完了した Run を置くフォルダ。</summary>
        private static string GameDirectory()
        {
            return Path.Combine(Path.Combine(ApplicationStorageContext.PersistentDataRoot,
                BuildDefinition.GetRunHistoryDirectoryPath()), "Completed");
        }

        internal static string CopyDirectory()
        {
            return Path.Combine(Path.Combine(Paths.BepInExRootPath, "LwfEconomy"), "runs");
        }

        /// <summary>本体のフォルダにあって写しに無いものを写す。写した数を返す。</summary>
        internal static int Sync()
        {
            string from = GameDirectory();
            if (!Directory.Exists(from)) { return 0; }
            string to = CopyDirectory();
            Directory.CreateDirectory(to);
            int copied = 0;
            foreach (string file in Directory.GetFiles(from, "*" + Suffix))
            {
                string target = Path.Combine(to, Path.GetFileName(file));
                if (File.Exists(target)) { continue; }
                try
                {
                    File.Copy(file, target, false);
                    copied++;
                }
                catch (Exception e)
                {
                    EconomyGraphPlugin.Log.LogWarning("[data] cannot copy " + Path.GetFileName(file) + ": " + e.Message);
                }
            }
            return copied;
        }

        // 要約の索引（ファイル名 → 要約）。記録は1件 数十 KB あり、件数が増えると全部読むのに時間が掛かるので、
        // 一覧に要る分だけを1つのファイルに持つ。索引に無い・大きさが違う記録だけ、本文を読んで足す。
        // 形を変えたらファイル名の版を上げる（古い索引は読まれず、作り直される）。
        private const string IndexName = "summaries.v1.json";
        private static Dictionary<string, RunSummary> _index;

        private static Dictionary<string, RunSummary> Index()
        {
            if (_index != null) { return _index; }
            try
            {
                string path = System.IO.Path.Combine(CopyDirectory(), IndexName);
                if (File.Exists(path))
                {
                    _index = JsonConvert.DeserializeObject<Dictionary<string, RunSummary>>(File.ReadAllText(path));
                }
            }
            catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[data] cannot read the index, rebuilding: " + e.Message); }
            if (_index == null) { _index = new Dictionary<string, RunSummary>(); }
            return _index;
        }

        private static void SaveIndex()
        {
            try
            {
                Directory.CreateDirectory(CopyDirectory());
                File.WriteAllText(System.IO.Path.Combine(CopyDirectory(), IndexName), JsonConvert.SerializeObject(_index));
            }
            catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[data] cannot save the index: " + e.Message); }
        }

        /// <summary>その Run より前に終わった Run のうち、いちばん遅い終了時刻。無ければ MinValue。
        /// 控えのファイルを Run に当てるとき、前の Run の控えを拾わないための下限に使う。</summary>
        internal static DateTime PreviousEnd(RunSummary run)
        {
            DateTime best = DateTime.MinValue;
            foreach (RunSummary other in Cache.Values)
            {
                if (other == run || other.Sequence >= run.Sequence) { continue; }
                if (other.EndedUtc <= run.StartedUtc && other.EndedUtc > best) { best = other.EndedUtc; }
            }
            return best;
        }

        /// <summary>新しい順の一覧。</summary>
        internal static List<RunSummary> LoadAll()
        {
            string dir = GameDirectory();
            // 本体は直近 100 件しか持たないので、いつも写しを取ってそちらを読む（写せなければ本体のフォルダを読む）
            try
            {
                int copied = Sync();
                if (copied > 0) { EconomyGraphPlugin.Log.LogInfo("[data] copied " + copied + " runs"); }
                dir = CopyDirectory();
            }
            catch (Exception e)
            {
                EconomyGraphPlugin.Log.LogWarning("[data] cannot keep copies, reading the game folder: " + e.Message);
            }

            List<RunSummary> runs = new List<RunSummary>();
            if (!Directory.Exists(dir)) { return runs; }
            Dictionary<string, RunSummary> index = Index();
            bool dirty = false;
            foreach (string file in Directory.GetFiles(dir, "*" + Suffix))
            {
                RunSummary run;
                if (!Cache.TryGetValue(file, out run))
                {
                    string name = Path.GetFileName(file);
                    long size = new FileInfo(file).Length;
                    if (index.TryGetValue(name, out run) && run != null && run.Size == size)
                    {
                        run.Path = file;
                    }
                    else
                    {
                        try { run = Summarize(file, Read(file)); }
                        catch (Exception e)
                        {
                            EconomyGraphPlugin.Log.LogWarning("[data] cannot read " + name + ": " + e.Message);
                            continue;
                        }
                        run.Size = size;
                        index[name] = run;
                        dirty = true;
                    }
                    Cache[file] = run;
                }
                runs.Add(run);
            }
            if (dirty) { SaveIndex(); }
            runs.Sort(delegate(RunSummary a, RunSummary b) { return b.Sequence.CompareTo(a.Sequence); });
            return runs;
        }

        /// <summary>自作の特別任務なら、任務を控えの番号と名前に差し替える。
        /// 控えは任務パックの MOD（mods/mission_packs）が開始時に書く BepInEx/LwfMissions/runs.tsv（開始時刻 UTC・番号・名前）。
        /// 本体の記録の開始時刻は控えの少し後になるので、近いもの（5分以内）を当てる。</summary>
        private static void ApplyCustomMission(RunSummary run)
        {
            if (string.IsNullOrEmpty(run.Mission)) { return; }
            try
            {
                string path = Path.Combine(Path.Combine(Paths.BepInExRootPath, "LwfMissions"), "runs.tsv");
                if (!File.Exists(path)) { return; }
                double best = 300;
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] cols = line.Split((char)9);
                    DateTime at;
                    if (cols.Length < 3 || !DateTime.TryParse(cols[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)) { continue; }
                    double gap = Math.Abs((run.StartedUtc - at.ToUniversalTime()).TotalSeconds);
                    if (gap < best)
                    {
                        best = gap;
                        run.Mission = cols[1];
                        run.CustomMissionName = cols[2];
                    }
                }
            }
            catch (Exception e) { EconomyGraphPlugin.Log.LogWarning("[data] cannot read the custom mission markers: " + e.Message); }
        }

        internal static JObject Read(string path)
        {
            return JObject.Parse(File.ReadAllText(path));
        }

        private static RunSummary Summarize(string path, JObject json)
        {
            RunSummary run = new RunSummary();
            run.Path = path;
            run.Sequence = Long(json, "localSequenceNumber");
            run.StartedLocal = Time(json, "startedAtUtc");
            run.StartedUtc = run.StartedLocal.ToUniversalTime();
            string ended = Has(json, "endedAtUtc") ? "endedAtUtc" : "lastCheckpointAtUtc";
            run.EndedUtc = Has(json, ended) ? Time(json, ended).ToUniversalTime() : run.StartedUtc;
            run.Result = Str(json, "result");
            run.EndReason = Str(json, "endReason");
            run.ElapsedSeconds = Num(json, "elapsedGameplaySeconds");
            run.GameMode = Str(json, "gameModeId");
            run.Difficulty = Str(json, "difficultyId");
            run.Mission = Str(json, "specialMissionId");
            ApplyCustomMission(run);

            List<string> patrons = new List<string>();
            foreach (JToken t in List(json, "patronIds")) { patrons.Add((string)t); }
            run.Patrons = patrons.ToArray();

            foreach (JToken t in List(json, "repaymentMilestones"))
            {
                JObject m = t as JObject;
                if (m == null) { continue; }
                if (Has(m, "completedAtElapsedGameplaySeconds")) { run.RepaymentsDone++; }
                run.RepaymentsTarget = (int)Long(m, "targetRepaymentCountAfterCompletion");
            }
            return run;
        }

        // ---------------------------------------------------------------
        // JSON の読み口。項目が無い・null・型が違う、のどれでも落ちずに空を返す
        // ---------------------------------------------------------------

        internal static bool Has(JObject o, string key)
        {
            if (o == null) { return false; }
            JToken t = o[key];
            return t != null && t.Type != JTokenType.Null;
        }

        internal static string Str(JObject o, string key)
        {
            if (!Has(o, key)) { return null; }
            try { return (string)o[key]; }
            catch (Exception) { return null; }
        }

        internal static long Long(JObject o, string key)
        {
            if (!Has(o, key)) { return 0L; }
            try { return (long)o[key]; }
            catch (Exception) { return 0L; }
        }

        internal static double Num(JObject o, string key)
        {
            if (!Has(o, key)) { return 0.0; }
            try { return (double)o[key]; }
            catch (Exception) { return 0.0; }
        }

        internal static IEnumerable<JToken> List(JObject o, string key)
        {
            JArray a = o != null ? o[key] as JArray : null;
            if (a == null) { return new JToken[0]; }
            return a;
        }

        internal static JObject Obj(JObject o, string key)
        {
            return o != null ? o[key] as JObject : null;
        }

        /// <summary>記録の時刻（UTC の文字列）をこの PC の時刻にする。</summary>
        internal static DateTime Time(JObject o, string key)
        {
            if (!Has(o, key)) { return DateTime.MinValue; }
            try
            {
                JToken t = o[key];
                // Newtonsoft は日時らしい文字列を先に DateTime にしてしまうことがある
                if (t.Type == JTokenType.Date) { return ((DateTime)t).ToLocalTime(); }
                return DateTimeOffset.Parse((string)t, CultureInfo.InvariantCulture).LocalDateTime;
            }
            catch (Exception) { return DateTime.MinValue; }
        }
    }
}
