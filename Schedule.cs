// schedule.txt の読み込みと cron 形式の時刻判定
//   書式:  分 時 日 月 曜日 | 実行するファイル | 猫のセリフ(省略可)
//          @random 30-120  | 実行するファイル | ...   ← きまぐれ（30〜120分おきにランダム）
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

class CronEntry
{
    public string Cron, Path, Message;
    public int LineNo;
    bool[] min, hour, dom, mon, dow;
    bool domAny, dowAny;

    // きまぐれ実行（@random 最小-最大 分）
    public bool IsRandom;
    public int RandMin, RandMax;
    public DateTime NextRun;
    static readonly Random rng = new Random();

    public void ScheduleNext(DateTime from)
    {
        var t = from.AddMinutes(rng.Next(RandMin, RandMax + 1));
        NextRun = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
    }

    public bool Matches(DateTime t)
    {
        if (IsRandom)
        {
            if (t < NextRun) return false;
            ScheduleNext(t);
            return true;
        }
        if (!min[t.Minute] || !hour[t.Hour] || !mon[t.Month]) return false;
        bool d = dom[t.Day], w = dow[(int)t.DayOfWeek];
        // cron と同じく、日と曜日が両方指定されていればどちらか一致で実行
        return domAny || dowAny ? d && w : d || w;
    }

    public static CronEntry Parse(string line, int lineNo, string baseDir)
    {
        var parts = line.Split('|');
        if (parts.Length < 2) throw new FormatException("「|」でファイルを区切ってください");
        var f = parts[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string path = Environment.ExpandEnvironmentVariables(parts[1].Trim().Trim('"'));
        if (path.Length == 0) throw new FormatException("実行するファイルがありません");
        if (!System.IO.Path.IsPathRooted(path)) path = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, path));
        string message = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : null;

        if (f.Length > 0 && f[0] == "@random")
        {
            int a, b;
            var ab = f.Length == 2 ? f[1].Split('-') : new string[0];
            if (ab.Length != 2 || !int.TryParse(ab[0], out a) || !int.TryParse(ab[1], out b))
                throw new FormatException("きまぐれは「@random 30-120」のように最小-最大の分数で書いてください");
            if (a < 1 || b > 1440 || a > b) throw new FormatException("きまぐれの間隔は 1〜1440 分で、最小 ≦ 最大にしてください");
            var r = new CronEntry { Cron = "@random " + a + "-" + b, Path = path, Message = message, LineNo = lineNo, IsRandom = true, RandMin = a, RandMax = b };
            r.ScheduleNext(DateTime.Now);
            return r;
        }

        if (f.Length != 5) throw new FormatException("時刻は「分 時 日 月 曜日」の5項目で書いてください");
        var e = new CronEntry
        {
            Cron = string.Join(" ", f),
            Path = path,
            Message = message,
            LineNo = lineNo,
            min = Field(f[0], 0, 59, "分"),
            hour = Field(f[1], 0, 23, "時"),
            dom = Field(f[2], 1, 31, "日"),
            mon = Field(f[3], 1, 12, "月"),
            domAny = f[2] == "*",
            dowAny = f[4] == "*",
        };
        var w = Field(f[4], 0, 7, "曜日");
        if (w[7]) w[0] = true;   // 7 も日曜
        e.dow = w;
        return e;
    }

    // *, 5, 1-5, 1,3,5, */15, 9-18/2
    internal static bool[] Field(string s, int lo, int hi, string name)
    {
        var r = new bool[hi + 1];
        foreach (var part in s.Split(','))
        {
            string range = part;
            int step = 1;
            int slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part.Substring(0, slash);
                if (!int.TryParse(part.Substring(slash + 1), out step) || step < 1) throw Bad(name, s);
            }
            int a, b;
            if (range == "*") { a = lo; b = hi; }
            else if (range.Contains("-"))
            {
                var ab = range.Split('-');
                if (ab.Length != 2 || !int.TryParse(ab[0], out a) || !int.TryParse(ab[1], out b)) throw Bad(name, s);
            }
            else
            {
                if (!int.TryParse(range, out a)) throw Bad(name, s);
                b = slash >= 0 ? hi : a;
            }
            if (a < lo || b > hi || a > b) throw Bad(name, s);
            for (int i = a; i <= b; i += step) r[i] = true;
        }
        return r;
    }

    static FormatException Bad(string name, string s) { return new FormatException(name + "の指定「" + s + "」が読めません"); }
}

static class Schedule
{
    public const string Sample =
@"# TaskbarCat スケジュール
# 書式:  分 時 日 月 曜日 | 実行するファイル | 猫のセリフ(省略可)
#
#   曜日   : 0=日 1=月 2=火 3=水 4=木 5=金 6=土 (7 も日曜)
#   書ける値: *  5  1-5  1,3,5  */15  9-18/2
#   きまぐれ: 「@random 30-120」と書くと 30〜120 分おきにランダムな間隔で実行
#   ファイル: フルパス、または %USERPROFILE%.taskbar-cat からの相対パス。%USERPROFILE% などの環境変数も使えます
#   実行方法: .ps1 → PowerShell / .bat .cmd → cmd / .py → python / その他 → ダブルクリックと同じ
#
# 保存すると1分以内に自動で読み込まれます。実行結果は logs フォルダに残ります。
# 先頭の # を外すと有効になります。「#off 」で始まる行は一時停止中（設定画面でチェックを外したもの）。
# 右クリック →「タスク」→「スケジュール設定…」の画面からも編集できます。
#
# 0 9 * * 1-5   | C:\scripts\morning.ps1 | 朝のやつ動かすにゃ
# 30 12 * * *   | C:\scripts\lunch.bat   | おひるだにゃ
# */15 * * * *  | C:\scripts\check.py
# @random 30-120 | C:\scripts\kimagure.ps1
";

    public static List<CronEntry> Load(string path, List<string> errors)
    {
        var list = new List<CronEntry>();
        if (!File.Exists(path)) File.WriteAllText(path, Sample, new UTF8Encoding(true));
        string baseDir = TaskbarCat.HomeDir;   // 相対パスは保存先ではなくホームフォルダ基準
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i].Trim();
            if (l.Length == 0 || l.StartsWith("#")) continue;
            try { list.Add(CronEntry.Parse(l, i + 1, baseDir)); }
            catch (Exception ex) { errors.Add((i + 1) + "行目: " + ex.Message); }
        }
        return list;
    }

    // ---- 設定画面用: 一時停止中の行や読めない行も含めて読み書きする ----

    public static List<ScheduleItem> LoadItems(string path)
    {
        var items = new List<ScheduleItem>();
        if (!File.Exists(path)) return items;
        string baseDir = TaskbarCat.HomeDir;   // 相対パスは保存先ではなくホームフォルダ基準
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string l = raw.Trim();
            bool enabled = true;
            if (l.StartsWith("#off ")) { enabled = false; l = l.Substring(5).Trim(); }
            else if (l.Length == 0 || l.StartsWith("#")) continue;
            try
            {
                var e = CronEntry.Parse(l, 0, baseDir);
                var parts = l.Split('|');
                items.Add(new ScheduleItem { Enabled = enabled, Cron = e.Cron, File = parts[1].Trim().Trim('"'), Message = e.Message ?? "" });
            }
            catch (FormatException)
            {
                items.Add(new ScheduleItem { BadLine = raw.Trim() });
            }
        }
        return items;
    }

    public static void SaveItems(string path, List<ScheduleItem> items)
    {
        var sb = new StringBuilder(Sample);
        sb.Append("\r\n");
        foreach (var it in items) sb.Append(it.ToLine()).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}

class ScheduleItem
{
    public bool Enabled = true;
    public string Cron = "@random 30-120", File = "", Message = "";   // 新規はきまぐれ
    public string BadLine;   // 読めなかった行はそのまま保持する

    public string EntryLine()
    {
        return Cron + " | " + File + (Message.Length > 0 ? " | " + Message.Replace("|", "") : "");
    }

    public string ToLine()
    {
        if (BadLine != null) return BadLine;
        return (Enabled ? "" : "#off ") + EntryLine();
    }

    public ScheduleItem Clone() { return (ScheduleItem)MemberwiseClone(); }
}

// cron 文字列 ⇔ 画面の入力（時刻指定 / 一定間隔 / cron 直接）
static class CronUi
{
    public static readonly string[] DayNames = { "日", "月", "火", "水", "木", "金", "土" };
    public static readonly int[] EveryChoices = { 1, 5, 10, 15, 20, 30, 60, 120, 180 };

    public enum Kind { Time, Interval, Random, Raw }

    public class Spec
    {
        public Kind Kind = Kind.Time;
        public int Hour = 9, Minute = 0, Every = 15, From = 9, To = 18;
        public int RandMin = 30, RandMax = 120;
        public bool Ranged;
        public bool[] Dow = { true, true, true, true, true, true, true };
        public string Raw = "";
    }

    public static Spec Decode(string cron)
    {
        var s = new Spec { Raw = cron };
        var f = cron.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length == 2 && f[0] == "@random")
        {
            var ab = f[1].Split('-');
            int a, b;
            if (ab.Length == 2 && int.TryParse(ab[0], out a) && int.TryParse(ab[1], out b))
            { s.Kind = Kind.Random; s.RandMin = a; s.RandMax = b; return s; }
            s.Kind = Kind.Raw; return s;
        }
        if (f.Length != 5 || f[2] != "*" || f[3] != "*") { s.Kind = Kind.Raw; return s; }
        try
        {
            var w = CronEntry.Field(f[4], 0, 7, "曜日");
            for (int i = 0; i < 7; i++) s.Dow[i] = w[i] || (i == 0 && w[7]);
        }
        catch (FormatException) { s.Kind = Kind.Raw; return s; }

        int m, h;
        if (int.TryParse(f[0], out m) && int.TryParse(f[1], out h))
        {
            if (m < 0 || m > 59 || h < 0 || h > 23) { s.Kind = Kind.Raw; return s; }
            s.Kind = Kind.Time; s.Minute = m; s.Hour = h; return s;
        }

        // 一定間隔
        string hb = f[1];
        int hstep = 1;
        int slash = hb.IndexOf('/');
        if (slash >= 0 && !int.TryParse(hb.Substring(slash + 1), out hstep)) { s.Kind = Kind.Raw; return s; }
        if (slash >= 0) hb = hb.Substring(0, slash);
        if (hb != "*")
        {
            var ab = hb.Split('-');
            int a, b;
            if (ab.Length != 2 || !int.TryParse(ab[0], out a) || !int.TryParse(ab[1], out b)) { s.Kind = Kind.Raw; return s; }
            s.Ranged = true; s.From = a; s.To = b;
        }
        int every = -1, n;
        if (f[0] == "0" && (hstep > 1 || hb == "*" || s.Ranged)) every = 60 * hstep;
        if (hstep == 1 && f[0] == "*") every = 1;
        if (hstep == 1 && f[0].StartsWith("*/") && int.TryParse(f[0].Substring(2), out n)) every = n;
        if (Array.IndexOf(EveryChoices, every) < 0) { s.Kind = Kind.Raw; return s; }
        s.Kind = Kind.Interval; s.Every = every;
        return s;
    }

    public static string Encode(Spec s)
    {
        if (s.Kind == Kind.Raw) return s.Raw.Trim();
        if (s.Kind == Kind.Random) return "@random " + s.RandMin + "-" + s.RandMax;
        string dow = DowField(s.Dow);
        if (s.Kind == Kind.Time) return s.Minute + " " + s.Hour + " * * " + dow;
        string hours = s.Ranged ? s.From + "-" + s.To : "*";
        if (s.Every < 60) return (s.Every == 1 ? "*" : "*/" + s.Every) + " " + hours + " * * " + dow;
        int hs = s.Every / 60;
        return "0 " + hours + (hs > 1 ? "/" + hs : "") + " * * " + dow;
    }

    static string DowField(bool[] d)
    {
        int count = 0;
        foreach (var b in d) if (b) count++;
        if (count == 0 || count == 7) return "*";
        if (count == 5 && !d[0] && !d[6]) return "1-5";
        var list = new List<string>();
        for (int i = 1; i <= 7; i++) if (d[i % 7]) list.Add((i % 7).ToString());
        return string.Join(",", list.ToArray());
    }

    public static string DescribeDays(bool[] d)
    {
        int count = 0;
        foreach (var b in d) if (b) count++;
        if (count == 0 || count == 7) return "毎日";
        if (count == 5 && !d[0] && !d[6]) return "平日";
        if (count == 2 && d[0] && d[6]) return "土日";
        var list = new List<string>();
        for (int i = 1; i <= 7; i++) if (d[i % 7]) list.Add(DayNames[i % 7]);
        return string.Join("・", list.ToArray());
    }

    public static string Describe(string cron)
    {
        var s = Decode(cron);
        string days = DescribeDays(s.Dow);
        switch (s.Kind)
        {
            case Kind.Time: return days + " " + s.Hour.ToString("00") + ":" + s.Minute.ToString("00");
            case Kind.Interval:
                string every = s.Every < 60 ? s.Every + "分ごと" : s.Every / 60 + "時間ごと";
                return every + (s.Ranged ? " (" + s.From + "〜" + s.To + "時台)" : "") + (days != "毎日" ? " " + days : "");
            case Kind.Random: return "きまぐれ";
            default: return DescribeRaw(cron);
        }
    }

    // 画面で表せない cron もなるべく日本語で説明する（例: 0 0 1 * * → 毎月1日 00:00）
    static string DescribeRaw(string cron)
    {
        var f = cron.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) return "cron: " + cron;
        try
        {
            var parts = new List<string>();
            if (f[3] != "*") parts.Add(List(f[3]) + "月");
            if (f[2] != "*") parts.Add((f[3] == "*" ? "毎月" : "") + List(f[2]) + "日");
            if (f[4] != "*")
            {
                var w = CronEntry.Field(f[4], 0, 7, "曜日");
                var d = new bool[7];
                for (int i = 0; i < 7; i++) d[i] = w[i] || (i == 0 && w[7]);
                parts.Add((f[2] != "*" ? "または " : "") + DescribeDays(d));
            }
            if (parts.Count == 0) parts.Add("毎日");
            else if (f[2] == "*" && f[4] == "*") parts[0] += "の毎日";   // 月だけ指定

            int m, h;
            if (int.TryParse(f[0], out m) && int.TryParse(f[1], out h))
                parts.Add(h.ToString("00") + ":" + m.ToString("00"));
            else
            {
                string hour = f[1] == "*" ? "" :
                    f[1].StartsWith("*/") ? f[1].Substring(2) + "時間ごとの" :
                    List(f[1].Replace("-", "〜").Split('/')[0]) + "時台" + (f[1].Contains("/") ? "（" + f[1].Split('/')[1] + "時間ごと）" : "") + "の";
                string minute = f[0] == "*" ? "毎分" :
                    f[0].StartsWith("*/") ? f[0].Substring(2) + "分ごと" :
                    List(f[0].Replace("-", "〜")) + "分";
                parts.Add(hour + minute);
            }
            return string.Join(" ", parts.ToArray());
        }
        catch (FormatException) { return "cron: " + cron; }
    }

    static string List(string s) { return s.Replace(",", "・"); }
}
