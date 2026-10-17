using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Cp932DictGen
{
    class Program
    {
        static Encoding cp932;
        static Encoding cp950;
        static bool verbose = false;

        // Phase 1: Manual / Preset rules & sub-component aliases
        static Dictionary<string, RuleEntry> presetRules = new Dictionary<string, RuleEntry>();
        static Dictionary<string, string> componentAliases = new Dictionary<string, string>();

        // Phase 2: Strict 1-to-1 Orthographic Variant dictionaries (JPShinjitai, Joyo, Jinmei, Hyogai, JPOldStyle, TW/HK)
        static Dictionary<string, RuleEntry> strictVariantMap = new Dictionary<string, RuleEntry>();

        // Phase 5: Fallback Classical / Loose Variant dictionaries (CJKVI, JISX0213, TWEDU, HYDZD, TS/ST)
        static Dictionary<string, RuleEntry> fallbackVariantMap = new Dictionary<string, RuleEntry>();

        // Phase 3 & 4: IDS dictionaries
        static Dictionary<string, List<string>> idsMap = new Dictionary<string, List<string>>();
        static Dictionary<string, string> idsToCp932Kanji = new Dictionary<string, string>();

        class RuleEntry
        {
            public string Target;
            public string SourcePhase;
            public string Memo;
            public RuleEntry(string target, string phase, string memo)
            {
                Target = target;
                SourcePhase = phase;
                Memo = memo;
            }
        }

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            cp932 = Encoding.GetEncoding(932, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
            cp950 = Encoding.GetEncoding(950);

            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            string outCsvLevel1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cp932_traditional_dict_level1.csv");
            string outCsvAll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cp932_traditional_dict_all.csv");
            bool includeLevel2 = false;
            string testInput = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "-v" || a == "--verbose") verbose = true;
                else if (a == "--all" || a == "-a") includeLevel2 = true;
                else if ((a == "-o" || a == "--out") && i + 1 < args.Length) outCsvLevel1 = args[++i];
                else if ((a == "-t" || a == "--test") && i + 1 < args.Length) testInput = args[++i];
                else if (a == "-h" || a == "--help")
                {
                    PrintHelp();
                    return 0;
                }
            }

            LogPhase("INIT", "CP932疑似漢字・変換辞書ジェネレーター (Prototype v0.3.0) を起動します...");
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            // ---------------------------------------------------------
            // Phase 1: プリセット・部首・構成パーツ代替ルールの初期化
            // ---------------------------------------------------------
            LogPhase("PHASE 1", "プリセット変換ルールおよび部首・構成要素（半角カナ『ｲ』『ｼ』『ｷ』『ﾈ』『ﾛ』・『宀』等）定義をロード中...");
            InitPresets();
            LogDetail(string.Format("プリセット直接定義: {0} 件, 構成パーツ代替定義: {1} 件", presetRules.Count, componentAliases.Count));

            // ---------------------------------------------------------
            // Phase 2: 異体字・日本新字体辞書 & IDSデータベースのロード
            // ---------------------------------------------------------
            LogPhase("PHASE 2", "OpenCC / CJKVI 異体字辞書および CHISE IDS 漢字構造データベースをロード中...");
            LoadVariantDictionaries(dataDir);
            LoadIdsDatabase(dataDir);
            BuildNormalizedIdsIndex();
            LogDetail(string.Format("厳密1対1異体字(新字体等): {0} 件, 広義異体字フォールバック: {1} 件, IDS登録文字数: {2} 件, CP932漢字IDS逆引き: {3} 件",
                strictVariantMap.Count, fallbackVariantMap.Count, idsMap.Count, idsToCp932Kanji.Count));

            if (!string.IsNullOrEmpty(testInput))
            {
                RunTestSimulation(testInput);
                return 0;
            }

            // ---------------------------------------------------------
            // Phase 3: Big5（繁体字）CP932非対応文字の抽出
            // ---------------------------------------------------------
            LogPhase("PHASE 3", "Big5 (CP950) から CP932 非対応の繁体字固有文字を抽出中...");
            List<Big5Char> targets = ExtractNonCp932Big5(includeLevel2);
            int lv1Count = 0, lv2Count = 0;
            foreach (var t in targets)
            {
                if (t.IsLevel1) lv1Count++; else lv2Count++;
            }
            LogDetail(string.Format("抽出完了: 第1水準(常用) = {0} 字, 第2水準(次常用) = {1} 字 (合計 {2} 字)", lv1Count, lv2Count, targets.Count));

            // ---------------------------------------------------------
            // Phase 4: 多段階変換パイプラインによる変換候補の自動生成
            // ---------------------------------------------------------
            LogPhase("PHASE 4", "各文字に対して5段階パイプライン（1:プリセット -> 2:日本新字体/正字 -> 3:IDS構造同定 -> 4:IDS偏旁分解＋半角カナ合成 -> 5:古字/広義異体字）を実行中...");
            int statPreset = 0, statStrictVar = 0, statIdsNorm = 0, statIdsDecomp = 0, statFallbackVar = 0, statUnresolved = 0;

            List<string> csvLines = new List<string>();
            csvLines.Add("Big5水準,変換対象,Unicode,Big5コード,変換先文字列,生成フェーズ,IDS構造,メモ");

            List<string> unresolvedSamples = new List<string>();

            foreach (var item in targets)
            {
                string rawIds = idsMap.ContainsKey(item.CharStr) ? string.Join(" / ", idsMap[item.CharStr].ToArray()) : "";
                RuleEntry result = ResolveCharacter(item.CharStr);

                if (result != null && IsValidCp932Output(result.Target))
                {
                    if (result.SourcePhase == "1:プリセット") statPreset++;
                    else if (result.SourcePhase == "2:日本新字体/正字") statStrictVar++;
                    else if (result.SourcePhase == "3:IDS異体字同定") statIdsNorm++;
                    else if (result.SourcePhase == "4:IDS偏旁分解") statIdsDecomp++;
                    else if (result.SourcePhase == "5:広義異体字/同義字") statFallbackVar++;

                    if (verbose && item.IsLevel1)
                    {
                        Console.WriteLine("  [OK] {0} ({1}) -> {2,-6} [{3}] {4}",
                            item.CharStr, item.UnicodeHex, result.Target, result.SourcePhase, result.Memo);
                    }

                    csvLines.Add(string.Format("{0},{1},{2},{3},{4},{5},{6},{7}",
                        item.LevelName, item.CharStr, item.UnicodeHex, item.Big5Hex,
                        EscapeCsv(result.Target), EscapeCsv(result.SourcePhase), EscapeCsv(rawIds), EscapeCsv(result.Memo)));
                }
                else
                {
                    statUnresolved++;
                    if (unresolvedSamples.Count < 60)
                    {
                        unresolvedSamples.Add(string.Format("{0}({1}:{2})", item.CharStr, item.UnicodeHex, rawIds));
                    }
                    if (verbose && item.IsLevel1)
                    {
                        Console.WriteLine("  [未解決] {0} ({1}) IDS={2}", item.CharStr, item.UnicodeHex, rawIds);
                    }

                    csvLines.Add(string.Format("{0},{1},{2},{3},,{4},{5},{6}",
                        item.LevelName, item.CharStr, item.UnicodeHex, item.Big5Hex,
                        "9:未解決(AI/手動候補)", EscapeCsv(rawIds), "自動分解不可または構成パーツがCP932非対応"));
                }
            }

            // ---------------------------------------------------------
            // Phase 5: CP932整合性検証およびCSV出力
            // ---------------------------------------------------------
            LogPhase("PHASE 5", "生成結果のCP932検証およびCSV書き出し中...");
            string targetOut = includeLevel2 ? outCsvAll : outCsvLevel1;
            File.WriteAllLines(targetOut, csvLines.ToArray(), new UTF8Encoding(true));

            // Also update name2win CSV files
            string name2winCsv = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                includeLevel2 ? @"..\name2win\non_cp932_traditional_chinese.csv" : @"..\name2win\non_cp932_traditional_chinese_level1.csv");
            if (Directory.Exists(Path.GetDirectoryName(name2winCsv)))
            {
                File.WriteAllLines(name2winCsv, csvLines.ToArray(), new UTF8Encoding(true));
                LogDetail("name2win フォルダ内のCSVファイルも最新結果で同期しました: " + Path.GetFileName(name2winCsv));
            }

            swTotal.Stop();
            int resolvedTotal = statPreset + statStrictVar + statIdsNorm + statIdsDecomp + statFallbackVar;
            double rate = targets.Count > 0 ? (resolvedTotal * 100.0 / targets.Count) : 0.0;

            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine("  CP932疑似漢字・変換辞書ジェネレーター 実行サマリー (所要時間: {0} ms)", swTotal.ElapsedMilliseconds);
            Console.WriteLine("================================================================================");
            Console.WriteLine("  対象文字総数               : {0,5} 字", targets.Count);
            Console.WriteLine("  ------------------------------------------------------------------------------");
            Console.WriteLine("  [Phase 1] プリセット定義   : {0,5} 字", statPreset);
            Console.WriteLine("  [Phase 2] 日本新字体/正字  : {0,5} 字 (常用・人名・表外・旧字体への厳密な1文字置換)", statStrictVar);
            Console.WriteLine("  [Phase 3] IDS異体字同定    : {0,5} 字 (構成要素の正規化で既存CP932漢字に一致)", statIdsNorm);
            Console.WriteLine("  [Phase 4] IDS偏旁分解合成  : {0,5} 字 (半角カナ『ｲ』『ｼ』『ｷ』『ﾈ』『ﾛ』＋旁など)", statIdsDecomp);
            Console.WriteLine("  [Phase 5] 広義異体字/同義字: {0,5} 字 (古典異体字・簡繁対応による補完)", statFallbackVar);
            Console.WriteLine("  ------------------------------------------------------------------------------");
            Console.WriteLine("  ★ 自動解決合計             : {0,5} 字 ({1:F1}%)", resolvedTotal, rate);
            Console.WriteLine("  ▲ 未解決（要AI/手動補完）  : {0,5} 字 ({1:F1}%)", statUnresolved, 100.0 - rate);
            Console.WriteLine("  出力CSVファイル            : {0}", targetOut);
            Console.WriteLine("================================================================================");

            if (unresolvedSamples.Count > 0)
            {
                Console.WriteLine("  [未解決文字サンプル (先頭 {0} 件)]:", unresolvedSamples.Count);
                Console.WriteLine("  " + string.Join(", ", unresolvedSamples.ToArray()));
            }

            return 0;
        }

        static void RunTestSimulation(string input)
        {
            LogPhase("TEST", "入力文字列のCP932疑似漢字・異体字変換シミュレーションを実行中...");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("入力文字列: {0}", input);
            Console.WriteLine("--------------------------------------------------------------------------------");
            StringBuilder sbOut = new StringBuilder();
            for (int i = 0; i < input.Length; i++)
            {
                string ch = input[i].ToString();
                if (IsValidCp932String(ch))
                {
                    sbOut.Append(ch);
                    Console.WriteLine("  [{0,2}] {1} (U+{2:X4}) -> {3,-6} [CP932標準そのまま]", i + 1, ch, (int)ch[0], ch);
                }
                else
                {
                    RuleEntry res = ResolveCharacter(ch);
                    if (res != null && IsValidCp932Output(res.Target))
                    {
                        sbOut.Append(res.Target);
                        Console.WriteLine("  [{0,2}] {1} (U+{2:X4}) -> {3,-6} [{4}] {5}", i + 1, ch, (int)ch[0], res.Target, res.SourcePhase, res.Memo);
                    }
                    else
                    {
                        sbOut.Append("■");
                        Console.WriteLine("  [{0,2}] {1} (U+{2:X4}) -> ■      [未解決フォールバック]", i + 1, ch, (int)ch[0]);
                    }
                }
            }
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("変換結果  : {0}", sbOut.ToString());
            Console.WriteLine("================================================================================");
        }

        static void PrintHelp()
        {
            Console.WriteLine("使用法: cp932-dict-gen.exe [-v|--verbose] [-a|--all] [-t <文字列>] [-o <出力CSVパス>]");
            Console.WriteLine("  -v, --verbose : 冗長モード（各フェーズの進行状況と1文字ずつの変換根拠ログを出力）");
            Console.WriteLine("  -a, --all     : Big5第2水準（次常用6,594字）も含めた全7,526字を対象にする（既定は第1水準932字）");
            Console.WriteLine("  -t, --test    : 指定した文字列を本ジェネレーターの辞書で変換シミュレーションする");
            Console.WriteLine("  -o, --out     : 出力先CSVファイルパスを指定");
        }

        static void LogPhase(string phase, string message)
        {
            Console.WriteLine("[{0}] {1}", phase, message);
        }

        static void LogDetail(string message)
        {
            if (verbose)
            {
                Console.WriteLine("       -> {0}", message);
            }
        }

        static void InitPresets()
        {
            // User-approved & high-frequency presets
            AddPreset("妳", "女尓", "女偏＋尓(CP932第2水準 9B-95)");
            AddPreset("們", "ｲ門", "半角ｶﾅ『ｲ』(にんべん)＋門");
            AddPreset("這", "之言", "しんにょう代替『之』＋言");
            AddPreset("說", "説", "日本語JIS漢字(第1水準)");
            AddPreset("你", "ｲ尓", "半角ｶﾅ『ｲ』(にんべん)＋尓");
            AddPreset("她", "女也", "女偏＋也");
            AddPreset("嗎", "ﾛ馬", "半角ｶﾅ『ﾛ』(くちへん)＋馬");
            AddPreset("呢", "ﾛ尼", "半角ｶﾅ『ﾛ』(くちへん)＋尼");
            AddPreset("吧", "ﾛ巴", "半角ｶﾅ『ﾛ』(くちへん)＋巴");
            AddPreset("牠", "牛它", "うしへん＋它");
            AddPreset("祂", "ﾈ也", "半角ｶﾅ『ﾈ』(しめすへん)＋也");
            AddPreset("咱", "ﾛ自", "半角ｶﾅ『ﾛ』(くちへん)＋自");
            AddPreset("啥", "ﾛ舎", "半角ｶﾅ『ﾛ』(くちへん)＋舎");
            AddPreset("哪", "ﾛ那", "半角ｶﾅ『ﾛ』(くちへん)＋那");
            AddPreset("裡", "ﾈ里", "半角ｶﾅ『ﾈ』(ころもへん)＋里");
            AddPreset("夠", "句多", "句＋多");
            AddPreset("么", "ﾑ", "半角ｶﾅ『ﾑ』");
            AddPreset("囊", "嚢", "日本語JIS第2水準『嚢』(99-9B)");
            AddPreset("囪", "窓", "『窓/窗』の原字");
            AddPreset("甩", "用", "『用』の派生字");
            AddPreset("孓", "孑", "『孑孓』(ボウフラ)の対字");
            AddPreset("犛", "毛牛", "『氂牛(ヤク)』の異体字");
            AddPreset("夔", "憂", "『夔』の近似構造代替");
            AddPreset("羋", "羊", "『羊』の古字・同源字");
            AddPreset("嬴", "亡口月女凡", "『嬴』の構成要素展開");
            AddPreset("裊", "鳥衣", "『裊』(鳥＋衣)の構成要素展開");
            AddPreset("乜", "也", "『也』の俗字・斜め字");
            AddPreset("冇", "無", "広東語『冇(無)』または『有』の空抜き");
            AddPreset("卌", "四十", "『四十(40)』の合字");
            AddPreset("丮", "丸", "『丸/執』の原字");

            // Component aliases (used when a right-hand/inner component is not in CP932)
            componentAliases["爾"] = "尓"; // U+723E -> U+5C13 (CP932 9B-95)
            componentAliases["尔"] = "尓"; // U+5C14 -> U+5C13
            componentAliases["兌"] = "兌"; // U+514C is in CP932 (99-68)!
            componentAliases["兑"] = "兌"; // U+5151 -> U+514C
            componentAliases["溫"] = "温";
            componentAliases["昷"] = "温";
            componentAliases["𥁕"] = "温";
            componentAliases["黃"] = "黄";
            componentAliases["步"] = "歩";
            componentAliases["每"] = "毎";
            componentAliases["戶"] = "戸";
            componentAliases["户"] = "戸";
            componentAliases["內"] = "内";
            componentAliases["虛"] = "虚";
            componentAliases["錄"] = "録";
            componentAliases["彔"] = "録";
            componentAliases["顏"] = "顔";
            componentAliases["彥"] = "彦";
            componentAliases["真"] = "真";
            componentAliases["眞"] = "真";
            componentAliases["直"] = "直";
            componentAliases["具"] = "具";
            componentAliases["青"] = "青";
            componentAliases["靑"] = "青";
            componentAliases["者"] = "者";
            componentAliases["都"] = "都";
            componentAliases["鄉"] = "郷";
            componentAliases["既"] = "既";
            componentAliases["旣"] = "既";
            componentAliases["即"] = "即";
            componentAliases["卽"] = "即";
            componentAliases["舎"] = "舎";
            componentAliases["舍"] = "舎";
            componentAliases["告"] = "告";
            componentAliases["𠮷"] = "吉";
            componentAliases["𠮟"] = "叱";
            componentAliases["呑"] = "呑";
            componentAliases["吞"] = "呑";
            componentAliases["吳"] = "呉";
            componentAliases["呉"] = "呉";
            componentAliases["吕"] = "呂";
            componentAliases["匀"] = "均";
            componentAliases["勺"] = "勺";
            componentAliases["卂"] = "迅";
            componentAliases["𠂉"] = "ﾉ";
            componentAliases["幺"] = "糸";
            componentAliases["丏"] = "丐";
            componentAliases["罙"] = "深";
            componentAliases["𡕩"] = "俊";
            componentAliases["𣬉"] = "毘";
            componentAliases["堇"] = "菫";
            componentAliases["菐"] = "撲";
            componentAliases["牚"] = "掌";
            componentAliases["睘"] = "環";
            componentAliases["翏"] = "戮";
            componentAliases["敫"] = "激";
            componentAliases["𦥯"] = "学";
            componentAliases["𧾷"] = "足"; // あしへん (U+27FB7) -> 足
            componentAliases["㐌"] = "也"; // 拖/迤 の旁 -> 也
            componentAliases["䜌"] = "亦"; // 戀/變/孿 の冠 -> 亦 (日本の新字体規則)
            componentAliases["匋"] = "陶"; // 啕 の旁 -> 陶
            componentAliases["𦐇"] = "羽"; // 塌/遢/蹋 の旁 -> 羽
            componentAliases["粦"] = "燐"; // 璘/遴/磷 の旁 -> 燐
            componentAliases["巤"] = "猟"; // 邋/蠟/獵 の旁 -> 猟
            componentAliases["眔"] = "沓"; // 遝 の旁 -> 沓
            componentAliases["裊"] = "鳥衣"; // 嬝 の旁 -> 鳥衣
            componentAliases["蒦"] = "獲"; // 擭/濩 の旁 -> 獲
            componentAliases["豦"] = "拠"; // 噱/璩 の旁 -> 拠
            componentAliases["𤇾"] = "栄"; // 熒/縈 の冠 -> 栄
            componentAliases["䏌"] = "八月";
            componentAliases["穵"] = "究";
            componentAliases["㲺"] = "ｼ七";
            componentAliases["昝"] = "昝";
            componentAliases["咅"] = "倍";
            componentAliases["甾"] = "巛田";
            componentAliases["豖"] = "豕";
            componentAliases["𬙙"] = "四方";
            componentAliases["𡯄"] = "九力";
            componentAliases["⑦"] = "鳥";
            componentAliases["𠬶"] = "浸";
            componentAliases["𠂢"] = "派";
            componentAliases["𠂔"] = "市";
            componentAliases["虒"] = "虎";
            componentAliases["𠩺"] = "毛";
            componentAliases["㒼"] = "満";
            componentAliases["𣶒"] = "淵";
            componentAliases["龹"] = "巻";
            componentAliases["𠔉"] = "巻";
            componentAliases["𧘇"] = "衣";
            componentAliases["耂"] = "老";
            componentAliases["𫩠"] = "尚";
        }

        static void AddPreset(string from, string to, string memo)
        {
            if (!IsValidCp932String(from) && IsValidCp932Output(to))
            {
                presetRules[from] = new RuleEntry(to, "1:プリセット", memo);
            }
        }

        static bool IsValidCp932String(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c == '?') return false;
                byte[] b = cp932.GetBytes(c.ToString());
                if (b.Length == 1 && b[0] == 0x3F) return false;
            }
            return true;
        }

        // Rejects IDS placeholder circled numbers ①-⑳ (U+2460-U+2473) and single stroke symbols when checking generated output
        static bool IsValidCp932Output(string s)
        {
            if (!IsValidCp932String(s)) return false;
            foreach (char c in s)
            {
                int code = (int)c;
                if (code >= 0x2460 && code <= 0x24FF) return false; // ①-⑳ placeholders in cjkvi-ids
            }
            return true;
        }

        static string StripIvs(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    int cp = char.ConvertToUtf32(s[i], s[i + 1]);
                    if (cp >= 0xE0100 && cp <= 0xE01EF)
                    {
                        i++;
                        continue;
                    }
                    sb.Append(s[i]);
                    sb.Append(s[i + 1]);
                    i++;
                }
                else if (s[i] >= 0xFE00 && s[i] <= 0xFE0F)
                {
                    continue;
                }
                else
                {
                    sb.Append(s[i]);
                }
            }
            return sb.ToString();
        }

        static void LoadVariantDictionaries(string dataDir)
        {
            // 1. Strict Orthographic Variants (Japanese Shinjitai, Joyo, Jinmei, Hyogai, JP-Old-Style, TW/HK orthographic variants)
            string[] strictOpenCc = new string[] {
                "JPShinjitaiCharacters.txt",
                "TWVariants.txt",
                "HKVariants.txt"
            };
            foreach (string fn in strictOpenCc)
            {
                string p = Path.Combine(dataDir, fn);
                if (!File.Exists(p)) continue;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 2)
                    {
                        string k = StripIvs(parts[0].Trim());
                        string[] vals = parts[1].Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string rawV in vals)
                        {
                            string v = StripIvs(rawV);
                            TryAddStrictVariant(k, v, fn);
                            TryAddStrictVariant(v, k, fn);
                        }
                    }
                }
            }

            // jp-old-style.txt (tab-separated with IVS)
            string jpOldPath = Path.Combine(dataDir, "jp-old-style.txt");
            if (File.Exists(jpOldPath))
            {
                foreach (string line in File.ReadAllLines(jpOldPath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 2)
                    {
                        string c1 = StripIvs(parts[0].Trim());
                        string c2 = StripIvs(parts[1].Trim());
                        TryAddStrictVariant(c1, c2, "jp-old-style.txt");
                        TryAddStrictVariant(c2, c1, "jp-old-style.txt");
                        if (parts.Length >= 3)
                        {
                            string c3 = StripIvs(parts[2].Trim());
                            if (!string.IsNullOrEmpty(c3))
                            {
                                TryAddStrictVariant(c1, c3, "jp-old-style.txt");
                                TryAddStrictVariant(c3, c1, "jp-old-style.txt");
                            }
                        }
                    }
                }
            }

            string[] strictCjkvi = new string[] {
                "joyo-kanji-code.txt",
                "jinmei-variants.txt",
                "hyogai-variants.txt",
                "duplicate-chars.txt",
                "cjkvi-variants.txt",
                "jisx0213-variants.txt"
            };
            foreach (string fn in strictCjkvi)
            {
                string p = Path.Combine(dataDir, fn);
                if (!File.Exists(p)) continue;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    string[] parts = line.Split(',');
                    if (parts.Length >= 3)
                    {
                        string c1 = StripIvs(parts[0].Trim());
                        string rel = parts[1].Trim();
                        if (rel.StartsWith("<")) continue;
                        string c2 = StripIvs(parts[2].Trim());
                        TryAddStrictVariant(c1, c2, fn);
                        TryAddStrictVariant(c2, c1, fn);
                    }
                }
            }

            // 2. Fallback Variants (TWEDU, HYDZD classical dictionary & TS/ST simplified-traditional)
            string[] fallbackCjkvi = new string[] {
                "twedu-variants.txt",
                "hydzd-variants.txt"
            };
            foreach (string fn in fallbackCjkvi)
            {
                string p = Path.Combine(dataDir, fn);
                if (!File.Exists(p)) continue;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    string[] parts = line.Split(',');
                    if (parts.Length >= 3)
                    {
                        string c1 = StripIvs(parts[0].Trim());
                        string rel = parts[1].Trim();
                        if (rel.StartsWith("<")) continue;
                        string c2 = StripIvs(parts[2].Trim());
                        TryAddFallbackVariant(c1, c2, fn);
                        TryAddFallbackVariant(c2, c1, fn);
                    }
                }
            }

            string[] tsFiles = new string[] { "TSCharacters.txt", "STCharacters.txt" };
            foreach (string fn in tsFiles)
            {
                string p = Path.Combine(dataDir, fn);
                if (!File.Exists(p)) continue;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 2)
                    {
                        string k = StripIvs(parts[0].Trim());
                        string[] vals = parts[1].Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string rawV in vals)
                        {
                            string v = StripIvs(rawV);
                            TryAddFallbackVariant(k, v, fn);
                            TryAddFallbackVariant(v, k, fn);
                        }
                    }
                }
            }
        }

        static void TryAddStrictVariant(string from, string to, string source)
        {
            if (from.Length == 1 && to.Length == 1 && from != to)
            {
                if (!IsValidCp932String(from) && IsValidCp932Output(to))
                {
                    if (!strictVariantMap.ContainsKey(from))
                    {
                        strictVariantMap[from] = new RuleEntry(to, "2:日本新字体/正字", string.Format("CP932漢字『{0}』へ1対1置換 ({1})", to, source));
                    }
                    if (!componentAliases.ContainsKey(from))
                    {
                        componentAliases[from] = to;
                    }
                }
            }
        }

        static void TryAddFallbackVariant(string from, string to, string source)
        {
            if (from.Length == 1 && to.Length == 1 && from != to)
            {
                if (!IsValidCp932String(from) && IsValidCp932Output(to))
                {
                    if (!fallbackVariantMap.ContainsKey(from))
                    {
                        fallbackVariantMap[from] = new RuleEntry(to, "5:広義異体字/同義字", string.Format("CP932漢字『{0}』へ広義異体字置換 ({1})", to, source));
                    }
                    if (!componentAliases.ContainsKey(from))
                    {
                        componentAliases[from] = to;
                    }
                }
            }
        }

        static Regex regBracket = new Regex(@"\[[^\]]+\]", RegexOptions.Compiled);

        static void LoadIdsDatabase(string dataDir)
        {
            string p = Path.Combine(dataDir, "ids.txt");
            if (!File.Exists(p)) return;

            foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                string[] parts = line.Split('\t');
                if (parts.Length >= 3)
                {
                    string ch = parts[1].Trim();
                    List<string> seqs = new List<string>();
                    for (int i = 2; i < parts.Length; i++)
                    {
                        string cleaned = regBracket.Replace(parts[i].Trim(), "").Trim();
                        if (!string.IsNullOrEmpty(cleaned) && !seqs.Contains(cleaned))
                        {
                            seqs.Add(cleaned);
                        }
                    }
                    if (seqs.Count > 0)
                    {
                        idsMap[ch] = seqs;
                        if (ch.Length == 1 && IsValidCp932Output(ch))
                        {
                            foreach (string s in seqs)
                            {
                                if (s.Length > 1 && !idsToCp932Kanji.ContainsKey(s))
                                {
                                    idsToCp932Kanji[s] = ch;
                                }
                            }
                        }
                    }
                }
            }
        }

        static string NormalizeIdsComponents(string ids)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in ids)
            {
                string cs = c.ToString();
                if (cs == "訁" || cs == "讠") sb.Append("言");
                else if (cs == "飠" || cs == "饣") sb.Append("食");
                else if (cs == "糹" || cs == "纟") sb.Append("糸");
                else if (cs == "釒" || cs == "钅") sb.Append("金");
                else if (cs == "⻌" || cs == "⻍" || cs == "⻎") sb.Append("辶");
                else if (cs == "⺾" || cs == "⺿") sb.Append("艹");
                else if (cs == "⺘") sb.Append("扌");
                else if (cs == "⺡") sb.Append("氵");
                else if (cs == "⺅") sb.Append("亻");
                else if (cs == "⺖") sb.Append("忄");
                else if (cs == "⺨") sb.Append("犭");
                else if (cs == "⺭" || cs == "礻") sb.Append("示");
                else if (cs == "⻂") sb.Append("衤");
                else if (cs == "⺼") sb.Append("月");
                else if (strictVariantMap.ContainsKey(cs)) sb.Append(strictVariantMap[cs].Target);
                else sb.Append(cs);
            }
            return sb.ToString();
        }

        static void BuildNormalizedIdsIndex()
        {
            List<KeyValuePair<string, string>> extra = new List<KeyValuePair<string, string>>();
            foreach (var kv in idsToCp932Kanji)
            {
                string norm = NormalizeIdsComponents(kv.Key);
                if (norm != kv.Key && !idsToCp932Kanji.ContainsKey(norm))
                {
                    extra.Add(new KeyValuePair<string, string>(norm, kv.Value));
                }
            }
            foreach (var kv in extra)
            {
                if (!idsToCp932Kanji.ContainsKey(kv.Key))
                {
                    idsToCp932Kanji[kv.Key] = kv.Value;
                }
            }
        }

        class Big5Char
        {
            public bool IsLevel1;
            public string LevelName;
            public string CharStr;
            public string UnicodeHex;
            public string Big5Hex;
        }

        static List<Big5Char> ExtractNonCp932Big5(bool includeLevel2)
        {
            List<Big5Char> list = new List<Big5Char>();

            // Level 1: 0xA440 - 0xC67E
            for (int lead = 0xA4; lead <= 0xC6; lead++)
            {
                for (int trail = 0x40; trail <= 0xFE; trail++)
                {
                    if (trail == 0x7F || (trail >= 0x80 && trail <= 0xA0)) continue;
                    if (lead == 0xC6 && trail > 0x7E) continue;
                    CheckAndAddBig5(lead, trail, true, list);
                }
            }

            if (includeLevel2)
            {
                // Level 2: 0xC940 - 0xF9D5
                for (int lead = 0xC9; lead <= 0xF9; lead++)
                {
                    for (int trail = 0x40; trail <= 0xFE; trail++)
                    {
                        if (trail == 0x7F || (trail >= 0x80 && trail <= 0xA0)) continue;
                        if (lead == 0xF9 && trail > 0xD5) continue;
                        CheckAndAddBig5(lead, trail, false, list);
                    }
                }
            }

            return list;
        }

        static void CheckAndAddBig5(int lead, int trail, bool isLevel1, List<Big5Char> list)
        {
            byte[] b = new byte[] { (byte)lead, (byte)trail };
            string s = cp950.GetString(b);
            if (s.Length == 1)
            {
                int code = (int)s[0];
                if (code >= 0x4E00 && code <= 0x9FFF)
                {
                    if (!IsValidCp932String(s))
                    {
                        Big5Char item = new Big5Char();
                        item.IsLevel1 = isLevel1;
                        item.LevelName = isLevel1 ? "第1水準(常用)" : "第2水準(次常用)";
                        item.CharStr = s;
                        item.UnicodeHex = string.Format("U+{0:X4}", code);
                        item.Big5Hex = string.Format("{0:X2}{1:X2}", lead, trail);
                        list.Add(item);
                    }
                }
            }
        }

        static RuleEntry ResolveCharacter(string ch)
        {
            // 1. Preset
            if (presetRules.ContainsKey(ch)) return presetRules[ch];

            // 2. Strict 1-to-1 Orthographic Variant (JPShinjitai, Joyo, Jinmei, Hyogai, JP-Old-Style, CJKVI-Variants, TW/HK)
            if (strictVariantMap.ContainsKey(ch)) return strictVariantMap[ch];

            // 3. IDS Normalization -> 1-to-1 CP932 Kanji match
            if (idsMap.ContainsKey(ch))
            {
                foreach (string rawIds in idsMap[ch])
                {
                    string normIds = NormalizeIdsComponents(rawIds);
                    if (idsToCp932Kanji.ContainsKey(rawIds))
                    {
                        string k = idsToCp932Kanji[rawIds];
                        return new RuleEntry(k, "3:IDS異体字同定", string.Format("IDS『{0}』が一致するCP932漢字『{1}』", rawIds, k));
                    }
                    if (idsToCp932Kanji.ContainsKey(normIds))
                    {
                        string k = idsToCp932Kanji[normIds];
                        return new RuleEntry(k, "3:IDS異体字同定", string.Format("正規化IDS『{0}』が一致するCP932漢字『{1}』", normIds, k));
                    }
                }

                // 4. IDS Radical + Component Decomposition (偏旁分解＋半角カナ等)
                foreach (string rawIds in idsMap[ch])
                {
                    string memo;
                    string decomp = TryDecomposeIds(rawIds, 0, out memo);
                    if (!string.IsNullOrEmpty(decomp) && IsValidCp932Output(decomp))
                    {
                        return new RuleEntry(decomp, "4:IDS偏旁分解", memo);
                    }
                }
            }

            // 5. Fallback Classical / Loose Variant
            if (fallbackVariantMap.ContainsKey(ch)) return fallbackVariantMap[ch];

            return null;
        }

        static List<string> TokenizeIds(string ids)
        {
            List<string> tokens = new List<string>();
            for (int i = 0; i < ids.Length; i++)
            {
                if (char.IsHighSurrogate(ids[i]) && i + 1 < ids.Length && char.IsLowSurrogate(ids[i + 1]))
                {
                    tokens.Add(ids.Substring(i, 2));
                    i++;
                }
                else
                {
                    tokens.Add(ids[i].ToString());
                }
            }
            return tokens;
        }

        static bool IsIdsBinaryOp(string t)
        {
            return t == "⿰" || t == "⿱" || t == "⿴" || t == "⿵" || t == "⿶" ||
                   t == "⿷" || t == "⿸" || t == "⿹" || t == "⿺" || t == "⿻";
        }

        static bool IsIdsTernaryOp(string t)
        {
            return t == "⿲" || t == "⿳";
        }

        static string TryDecomposeIds(string rawIds, int depth, out string memo)
        {
            memo = "";
            List<string> tokens = TokenizeIds(rawIds);
            if (tokens.Count < 3) return null;

            string op = tokens[0];
            if (IsIdsBinaryOp(op))
            {
                int idx = 1;
                string part1 = ExtractSubIds(tokens, ref idx);
                string part2 = ExtractSubIds(tokens, ref idx);
                if (part1 == null || part2 == null) return null;

                string m1, m2;
                string r1 = ResolveComponent(part1, op, 1, depth + 1, out m1);
                string r2 = ResolveComponent(part2, op, 2, depth + 1, out m2);

                if (!string.IsNullOrEmpty(r1) && !string.IsNullOrEmpty(r2))
                {
                    string combined = r1 + r2;
                    string testIds = op + r1 + r2;
                    if (idsToCp932Kanji.ContainsKey(testIds))
                    {
                        string single = idsToCp932Kanji[testIds];
                        memo = string.Format("構成要素解決({0}+{1})によりCP932漢字『{2}』に一致", m1, m2, single);
                        return single;
                    }
                    memo = string.Format("IDS分解({0}): {1} + {2}", op, m1, m2);
                    return combined;
                }
            }
            else if (IsIdsTernaryOp(op))
            {
                int idx = 1;
                string part1 = ExtractSubIds(tokens, ref idx);
                string part2 = ExtractSubIds(tokens, ref idx);
                string part3 = ExtractSubIds(tokens, ref idx);
                if (part1 == null || part2 == null || part3 == null) return null;

                string m1, m2, m3;
                string r1 = ResolveComponent(part1, op, 1, depth + 1, out m1);
                string r2 = ResolveComponent(part2, op, 2, depth + 1, out m2);
                string r3 = ResolveComponent(part3, op, 3, depth + 1, out m3);

                if (!string.IsNullOrEmpty(r1) && !string.IsNullOrEmpty(r2) && !string.IsNullOrEmpty(r3))
                {
                    memo = string.Format("IDS三分割({0}): {1} + {2} + {3}", op, m1, m2, m3);
                    return r1 + r2 + r3;
                }
            }

            return null;
        }

        static string ExtractSubIds(List<string> tokens, ref int idx)
        {
            if (idx >= tokens.Count) return null;
            int start = idx;
            int needed = 1;
            while (needed > 0 && idx < tokens.Count)
            {
                string t = tokens[idx];
                idx++;
                needed--;
                if (IsIdsBinaryOp(t)) needed += 2;
                else if (IsIdsTernaryOp(t)) needed += 3;
            }
            if (needed != 0) return null;
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < idx; i++) sb.Append(tokens[i]);
            return sb.ToString();
        }

        static string ResolveComponent(string comp, string parentOp, int position, int depth, out string memo)
        {
            memo = comp;
            if (depth > 3) return null;

            // 1. Check positional radical substitutions (偏・冠・繞・旁の専用ルール)
            if (position == 1 && (parentOp == "⿰" || parentOp == "⿲"))
            {
                // Left-side radical (偏)
                if (comp == "亻" || comp == "⺅") { memo = "ｲ(にんべん)"; return "ｲ"; }
                if (comp == "氵" || comp == "⺡" || comp == "水") { memo = "ｼ(さんずい)"; return "ｼ"; }
                if (comp == "扌" || comp == "⺘" || comp == "手") { memo = "ｷ(てへん)"; return "ｷ"; }
                if (comp == "礻" || comp == "⺭" || comp == "示") { memo = "ﾈ(しめすへん)"; return "ﾈ"; }
                if (comp == "衤" || comp == "⻂" || comp == "衣") { memo = "ﾈ(ころもへん)"; return "ﾈ"; }
                if (comp == "口") { memo = "ﾛ(くちへん)"; return "ﾛ"; }
                if (comp == "忄" || comp == "⺖") { memo = "小(りっしんべん)"; return "小"; }
                if (comp == "犭" || comp == "⺨") { memo = "ｹ(けものへん)"; return "ｹ"; }
                if (comp == "阝" || comp == "⻖") { memo = "卩(こざとへん代替)"; return "卩"; }
                if (comp == "釒" || comp == "钅") { memo = "金(かねへん)"; return "金"; }
                if (comp == "糹" || comp == "纟") { memo = "糸(いとへん)"; return "糸"; }
                if (comp == "訁" || comp == "讠") { memo = "言(ごんべん)"; return "言"; }
                if (comp == "飠" || comp == "饣") { memo = "食(しょくへん)"; return "食"; }
                if (comp == "⺼" || comp == "肉") { memo = "月(にくづき)"; return "月"; }
                if (comp == "牜" || comp == "牛") { memo = "牛(うしへん)"; return "牛"; }
                if (comp == "𤣩" || comp == "王") { memo = "王(おうへん)"; return "王"; }
                if (comp == "𧾷") { memo = "足(あしへん)"; return "足"; }
                if (comp == "爫") { memo = "爪"; return "爪"; }
            }
            if (position == 2 && parentOp == "⿰")
            {
                // Right-side radical (旁)
                if (comp == "刂" || comp == "⺉") { memo = "ﾘ(りっとう)"; return "ﾘ"; }
                if (comp == "阝" || comp == "⻏") { memo = "卩(おおざと代替)"; return "卩"; }
            }
            if (position == 1 && (parentOp == "⿱" || parentOp == "⿳" || parentOp == "⿵" || parentOp == "⿸"))
            {
                // Top / Crown radical (冠・垂)
                if (comp == "宀") { memo = "宀(うかんむり)"; return "宀"; }
                if (comp == "艹" || comp == "⺾" || comp == "⺿") { memo = "艸(くさかんむり)"; return "艸"; }
                if (comp == "⺮") { memo = "竹(たけかんむり)"; return "竹"; }
                if (comp == "⻗") { memo = "雨(あめかんむり)"; return "雨"; }
                if (comp == "穴") { memo = "穴(あなかんむり)"; return "穴"; }
                if (comp == "爫" || comp == "⺤") { memo = "爪(つめかんむり)"; return "爪"; }
                if (comp == "罒" || comp == "⺲") { memo = "四(あみがしら代替)"; return "四"; }
                if (comp == "𠂉") { memo = "ﾉ"; return "ﾉ"; }
            }
            if (parentOp == "⿺")
            {
                // Lower-left wrap (しんにょう等)
                if (position == 1 && (comp == "辶" || comp == "⻌" || comp == "⻍" || comp == "⻎"))
                {
                    memo = "之(しんにょう代替)";
                    return "之";
                }
            }

            // 2. General radical / component fallbacks regardless of position
            if (comp == "亻" || comp == "⺅") { memo = "ｲ"; return "ｲ"; }
            if (comp == "氵" || comp == "⺡") { memo = "ｼ"; return "ｼ"; }
            if (comp == "扌" || comp == "⺘") { memo = "ｷ"; return "ｷ"; }
            if (comp == "礻" || comp == "⺭" || comp == "衤" || comp == "⻂") { memo = "ﾈ"; return "ﾈ"; }
            if (comp == "忄" || comp == "⺖") { memo = "小"; return "小"; }
            if (comp == "刂" || comp == "⺉") { memo = "ﾘ"; return "ﾘ"; }
            if (comp == "犭" || comp == "⺨") { memo = "ｹ"; return "ｹ"; }
            if (comp == "辶" || comp == "⻌" || comp == "⻍" || comp == "⻎") { memo = "之"; return "之"; }
            if (comp == "艹" || comp == "⺾" || comp == "⺿") { memo = "艸"; return "艸"; }
            if (comp == "阝" || comp == "⻖" || comp == "⻏") { memo = "卩"; return "卩"; }
            if (comp == "釒" || comp == "钅") { memo = "金"; return "金"; }
            if (comp == "糹" || comp == "纟") { memo = "糸"; return "糸"; }
            if (comp == "訁" || comp == "讠") { memo = "言"; return "言"; }
            if (comp == "飠" || comp == "饣") { memo = "食"; return "食"; }
            if (comp == "⺼") { memo = "月"; return "月"; }
            if (comp == "牜") { memo = "牛"; return "牛"; }
            if (comp == "𤣩") { memo = "王"; return "王"; }
            if (comp == "𧾷") { memo = "足"; return "足"; }
            if (comp == "罒" || comp == "⺲") { memo = "四"; return "四"; }

            // 3. Component alias (prioritized for special stroke groups)
            if (componentAliases.ContainsKey(comp) && IsValidCp932Output(componentAliases[comp]))
            {
                string target = componentAliases[comp];
                memo = string.Format("{0}({1}のCP932代替)", target, comp);
                return target;
            }

            // 4. Already valid 1-char CP932?
            if (comp.Length == 1 && IsValidCp932Output(comp))
            {
                memo = comp;
                return comp;
            }

            // 5. 1-to-1 variant?
            if (strictVariantMap.ContainsKey(comp))
            {
                string target = strictVariantMap[comp].Target;
                memo = string.Format("{0}({1}の新字体/正字)", target, comp);
                return target;
            }
            if (fallbackVariantMap.ContainsKey(comp))
            {
                string target = fallbackVariantMap[comp].Target;
                memo = string.Format("{0}({1}の異体字)", target, comp);
                return target;
            }

            // 6. If comp is itself an inline IDS expression (e.g. ⿱日皿)
            if (comp.Length > 1)
            {
                string normComp = NormalizeIdsComponents(comp);
                if (idsToCp932Kanji.ContainsKey(comp))
                {
                    string k = idsToCp932Kanji[comp];
                    memo = k;
                    return k;
                }
                if (idsToCp932Kanji.ContainsKey(normComp))
                {
                    string k = idsToCp932Kanji[normComp];
                    memo = k;
                    return k;
                }
                string subMemo;
                string subDecomp = TryDecomposeIds(comp, depth + 1, out subMemo);
                if (!string.IsNullOrEmpty(subDecomp) && IsValidCp932Output(subDecomp))
                {
                    memo = subMemo;
                    return subDecomp;
                }
            }

            // 7. If comp is a single non-CP932 char that has an entry in idsMap
            if (idsMap.ContainsKey(comp))
            {
                foreach (string subIds in idsMap[comp])
                {
                    string normSub = NormalizeIdsComponents(subIds);
                    if (idsToCp932Kanji.ContainsKey(normSub))
                    {
                        string k = idsToCp932Kanji[normSub];
                        memo = string.Format("{0}({1}のIDS一致)", k, comp);
                        return k;
                    }
                }
                foreach (string subIds in idsMap[comp])
                {
                    string subMemo;
                    string subDecomp = TryDecomposeIds(subIds, depth + 1, out subMemo);
                    if (!string.IsNullOrEmpty(subDecomp) && IsValidCp932Output(subDecomp))
                    {
                        memo = string.Format("{0}({1}を再分解)", subDecomp, comp);
                        return subDecomp;
                    }
                }
            }

            return null;
        }

        static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
            {
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            }
            return s;
        }
    }
}
