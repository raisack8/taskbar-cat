// ポートを調べる: ポート番号 → 使っているプロセスを一覧にし、右クリックで終了する（netstat -ano / taskkill の GUI 版）
//   一覧は WMI（root\StandardCimv2 の MSFT_NetTCPConnection / MSFT_NetUDPEndpoint）から、検索した時だけ取る
//   EDR 対策: netstat / taskkill を子プロセスで呼ばない。ほかのプロセスを OpenProcess で開くのは終了する時だけ
//            （Process.MainModule は使わない）。OS・セキュリティ製品・ほかのセッションのプロセスは終了できなくする
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class PortRow
{
    public string Proto = "", LocalAddr = "", RemoteAddr = "", State = "", Name = "?", Path = "";
    public int LocalPort, RemotePort = -1, Pid;   // RemotePort: UDP は -1
    public string Block;   // 終了できない理由（null なら終了できる）

    public bool IsUdp { get { return RemotePort < 0; } }
    public string Local { get { return Ports.Endpoint(LocalAddr, LocalPort); } }
    public string Remote { get { return IsUdp ? "" : Ports.Endpoint(RemoteAddr, RemotePort); } }

    // 確認ダイアログ・ログ用: 「TCP 3000 (LISTEN)」
    public string PortText { get { return Proto + " " + LocalPort + (State.Length > 0 ? " (" + State + ")" : ""); } }

    public string[] Cells { get { return new[] { Proto, Local, Remote, State, Pid.ToString(), Name, Path }; } }
}

static class Ports
{
    // パスが取れない時の保険。大文字・小文字は区別しない。セキュリティ製品・OS の部品は絶対に終了させない
    static readonly string[] Protected =
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "explorer", "sihost", "ctfmon",
        "MsMpEng", "NisSrv", "MpDefenderCoreService", "SecurityHealthService", "SecurityHealthSystray", "SgrmBroker", "smartscreen",
    };

    // MSFT_NetTCPConnection.State（Get-NetTCPConnection で確認済み: Listen=2, Established=5, CloseWait=8, TimeWait=11, Bound=100）
    static string StateName(int s)
    {
        switch (s)
        {
            case 1: return "CLOSED";
            case 2: return "LISTEN";
            case 3: return "SYN_SENT";
            case 4: return "SYN_RECEIVED";
            case 5: return "ESTABLISHED";
            case 6: return "FIN_WAIT_1";
            case 7: return "FIN_WAIT_2";
            case 8: return "CLOSE_WAIT";
            case 9: return "CLOSING";
            case 10: return "LAST_ACK";
            case 11: return "TIME_WAIT";
            case 12: return "DELETE_TCB";
            case 100: return "BOUND";
            default: return s.ToString();
        }
    }

    public static string Endpoint(string addr, int port)
    {
        return (addr.IndexOf(':') >= 0 ? "[" + addr + "]" : addr) + ":" + port;
    }

    // port: 0 なら TCP は LISTEN だけ、UDP は全部
    public static List<PortRow> Find(int port, bool udp)
    {
        var rows = new List<PortRow>();
        Query(@"root\StandardCimv2",
            "SELECT LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess FROM MSFT_NetTCPConnection" +
            (port > 0 ? " WHERE LocalPort = " + port + " OR RemotePort = " + port : " WHERE State = 2"),
            o =>
            {
                string la = Str(o["LocalAddress"]);
                rows.Add(new PortRow
                {
                    Proto = la.IndexOf(':') >= 0 ? "TCPv6" : "TCP",
                    LocalAddr = la, LocalPort = Int(o["LocalPort"]),
                    RemoteAddr = Str(o["RemoteAddress"]), RemotePort = Int(o["RemotePort"]),
                    State = StateName(Int(o["State"])), Pid = Int(o["OwningProcess"]),
                });
            });
        if (udp)
            Query(@"root\StandardCimv2",
                "SELECT LocalAddress, LocalPort, OwningProcess FROM MSFT_NetUDPEndpoint" + (port > 0 ? " WHERE LocalPort = " + port : ""),
                o =>
                {
                    string la = Str(o["LocalAddress"]);
                    rows.Add(new PortRow
                    {
                        Proto = la.IndexOf(':') >= 0 ? "UDPv6" : "UDP",
                        LocalAddr = la, LocalPort = Int(o["LocalPort"]), Pid = Int(o["OwningProcess"]),
                    });
                });

        FillProcesses(rows);

        // ポート指定あり: ローカルポートが一致する行を先に。あとはプロトコル・ポート・PID の順
        var order = new Dictionary<PortRow, int>();
        for (int i = 0; i < rows.Count; i++) order[rows[i]] = i;
        rows.Sort((a, b) =>
        {
            int c = 0;
            if (port > 0) c = (a.LocalPort == port ? 0 : 1).CompareTo(b.LocalPort == port ? 0 : 1);
            if (c == 0) c = string.CompareOrdinal(a.Proto, b.Proto);
            if (c == 0) c = a.LocalPort.CompareTo(b.LocalPort);
            if (c == 0) c = a.Pid.CompareTo(b.Pid);
            return c != 0 ? c : order[a].CompareTo(order[b]);
        });
        return rows;
    }

    // プロセス名（Process.GetProcessById）・パスとセッション（Win32_Process。一覧に出た PID の分だけ）・終了できない理由
    static void FillProcesses(List<PortRow> rows)
    {
        var pids = new List<int>();
        foreach (var r in rows) if (!pids.Contains(r.Pid)) pids.Add(r.Pid);

        var paths = new Dictionary<int, string>();
        var sessions = new Dictionary<int, int>();
        for (int i = 0; i < pids.Count; i += 50)
        {
            var where = new StringBuilder();
            for (int j = i; j < Math.Min(i + 50, pids.Count); j++)
                where.Append(where.Length == 0 ? " WHERE " : " OR ").Append("ProcessId = ").Append(pids[j]);
            Query(@"root\cimv2", "SELECT ProcessId, ExecutablePath, SessionId FROM Win32_Process" + where, o =>
            {
                int pid = Int(o["ProcessId"]);
                paths[pid] = Str(o["ExecutablePath"]);
                if (o["SessionId"] != null) sessions[pid] = Int(o["SessionId"]);
            });
        }

        var names = new Dictionary<int, string>();
        foreach (int pid in pids)
        {
            string n = "?";
            try { using (var p = Process.GetProcessById(pid)) n = p.ProcessName; } catch { }   // 終了済みなど
            names[pid] = n;
        }

        int self, mySession;
        using (var me = Process.GetCurrentProcess()) { self = me.Id; mySession = me.SessionId; }
        foreach (var r in rows)
        {
            r.Name = names[r.Pid];
            string path;
            if (paths.TryGetValue(r.Pid, out path)) r.Path = path;
            int session;
            r.Block = BlockReason(r, self, mySession, sessions.TryGetValue(r.Pid, out session) ? session : -1);
        }
    }

    // 終了できない理由。条件を緩めないこと（セキュリティ製品を止めようとすると EDR に検出される）
    static string BlockReason(PortRow r, int self, int mySession, int session)
    {
        if (r.Pid == 0 || r.Pid == 4) return "System のため終了できません";
        if (r.Pid == self) return "猫自身のため終了できません";
        if (session < 0 || r.Name == "?") return "プロセスの情報を取れないため終了できません";
        if (session != mySession) return "サービスやほかのユーザーのプロセスのため終了できません";
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (r.Path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return "Windows の部品のため終了できません";
        foreach (var n in Protected)
            if (string.Equals(n, r.Name, StringComparison.OrdinalIgnoreCase)) return "Windows・セキュリティ製品の部品のため終了できません";
        return null;
    }

    static void Query(string scope, string wql, Action<ManagementBaseObject> each)
    {
        using (var s = new ManagementObjectSearcher(scope, wql))
        using (var results = s.Get())
            foreach (ManagementBaseObject o in results)
                using (o) each(o);
    }

    static string Str(object o) { return o == null ? "" : o.ToString(); }
    static int Int(object o) { return o == null ? 0 : Convert.ToInt32(o); }
}

// ポートの画面（開いた時だけ作る）
class PortForm : Form
{
    readonly Action<string> log, say;   // 本体の Log / Say
    readonly TextBox portBox;
    readonly CheckBox udpBox;
    readonly Button searchBtn, reloadBtn;
    readonly ListView lv;
    readonly Label status;
    readonly MenuItem killItem, openItem;
    List<PortRow> rows = new List<PortRow>();
    int lastPort = -1;   // 前回の検索条件（-1: まだ検索していない、0: ポート欄が空）
    bool lastUdp, busy;
    int sortCol = -1;
    bool sortDesc;

    public PortForm(Action<string> log, Action<string> say)
    {
        this.log = log;
        this.say = say;

        Text = "ポート - TaskbarCat";
        Font = new Font("Yu Gothic UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 400);
        MinimumSize = new Size(560, 300);
        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) { e.Handled = true; Reload(); } };

        portBox = new TextBox { Width = 90, Margin = new Padding(3, 5, 3, 3) };
        searchBtn = new Button { Text = "検索", AutoSize = true, MinimumSize = new Size(80, 28) };
        searchBtn.Click += (s, e) => Search();
        reloadBtn = new Button { Text = "再読み込み", AutoSize = true, MinimumSize = new Size(80, 28) };
        reloadBtn.Click += (s, e) => Reload();
        udpBox = new CheckBox { Text = "UDP も含める", AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
        AcceptButton = searchBtn;   // ポート欄で Enter → 検索

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(6, 6, 6, 2) };
        top.Controls.Add(new Label { Text = "ポート", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        top.Controls.AddRange(new Control[] { portBox, searchBtn, reloadBtn, udpBox });

        lv = new ListView
        {
            View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, Dock = DockStyle.Fill, ShowItemToolTips = true,
        };
        lv.Columns.Add("プロトコル", 70);
        lv.Columns.Add("ローカル", 140);
        lv.Columns.Add("リモート", 140);
        lv.Columns.Add("状態", 95);
        lv.Columns.Add("PID", 55, HorizontalAlignment.Right);
        lv.Columns.Add("プロセス", 100);
        lv.Columns.Add("パス", 300);
        lv.ColumnClick += (s, e) => SortBy(e.Column);
        lv.SelectedIndexChanged += (s, e) => ShowCount();   // 選んだ行が終了できない時は理由を出す
        // ダブルクリックでは何もしない（誤って終了しないように）

        var menu = new ContextMenu();
        killItem = menu.MenuItems.Add("プロセスを終了…", (s, e) => KillSelected());
        menu.MenuItems.Add("-");
        menu.MenuItems.Add("PID をコピー", (s, e) => { var r = Selected; if (r != null) Copy(r.Pid.ToString()); });
        menu.MenuItems.Add("行をコピー", (s, e) => { var r = Selected; if (r != null) Copy(string.Join("\t", r.Cells)); });
        openItem = menu.MenuItems.Add("ファイルの場所を開く", (s, e) => OpenLocation());
        menu.Popup += (s, e) =>
        {
            var r = Selected;
            foreach (MenuItem m in menu.MenuItems) m.Enabled = r != null;
            killItem.Enabled = r != null && r.Block == null && !busy;
            openItem.Enabled = r != null && r.Path.Length > 0;
        };
        lv.ContextMenu = menu;

        status = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = false, Height = 26, Padding = new Padding(6, 4, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "ポート番号を入れて検索してください（空のまま検索すると、待ち受け中の一覧）",
        };

        Controls.Add(lv);       // Fill は最後にレイアウトされるよう先に追加
        Controls.Add(top);
        Controls.Add(status);
    }

    PortRow Selected { get { return lv.SelectedItems.Count > 0 ? (PortRow)lv.SelectedItems[0].Tag : null; } }

    void Search()
    {
        // 全角数字も受け付ける
        var sb = new StringBuilder();
        foreach (char ch in portBox.Text.Trim()) sb.Append(ch >= '０' && ch <= '９' ? (char)('0' + (ch - '０')) : ch);
        string t = sb.ToString();
        int port = 0;
        if (t.Length > 0 && (!int.TryParse(t, System.Globalization.NumberStyles.None, null, out port) || port < 1 || port > 65535))
        {
            rows = new List<PortRow>();
            Fill();
            status.Text = "1〜65535 の数字を入れてください";
            return;
        }
        Query(port, udpBox.Checked);
    }

    // F5 / 再読み込み: 前回と同じ条件で取り直す（まだ検索していなければ、今の入力で検索）
    void Reload()
    {
        if (lastPort < 0) Search();
        else Query(lastPort, lastUdp);
    }

    void Query(int port, bool udp)
    {
        if (busy) return;
        lastPort = port;
        lastUdp = udp;
        busy = true;
        searchBtn.Enabled = reloadBtn.Enabled = false;
        status.Text = "調べています…";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<PortRow> result = null;
            string err = null;
            try { result = Ports.Find(port, udp); }
            catch (Exception ex) { err = ex.Message; }
            try { BeginInvoke((Action)(() => Done(result, err))); }
            catch { }   // 画面がもう閉じている
        });
    }

    void Done(List<PortRow> result, string err)
    {
        if (IsDisposed) return;
        busy = false;
        searchBtn.Enabled = reloadBtn.Enabled = true;
        rows = result ?? new List<PortRow>();
        Fill();
        if (err != null) status.Text = "調べられませんでした: " + err;
    }

    void Fill()
    {
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var r in rows)
        {
            var li = new ListViewItem(r.Cells) { Tag = r };
            if (r.Block != null) { li.ForeColor = SystemColors.GrayText; li.ToolTipText = r.Block; }
            lv.Items.Add(li);
        }
        if (sortCol >= 0) lv.ListViewItemSorter = new RowSorter(sortCol, sortDesc);
        lv.EndUpdate();
        ShowCount();
    }

    void ShowCount()
    {
        if (busy) return;
        var r = Selected;
        if (r != null && r.Block != null) status.Text = r.Name + " (PID " + r.Pid + ") … " + r.Block;
        else status.Text = rows.Count + " 件  右クリックでプロセスを終了できます";
    }

    void SortBy(int col)
    {
        sortDesc = col == sortCol && !sortDesc;
        sortCol = col;
        lv.ListViewItemSorter = new RowSorter(col, sortDesc);
    }

    class RowSorter : System.Collections.IComparer
    {
        readonly int col;
        readonly bool desc;
        public RowSorter(int col, bool desc) { this.col = col; this.desc = desc; }

        public int Compare(object x, object y)
        {
            PortRow a = (PortRow)((ListViewItem)x).Tag, b = (PortRow)((ListViewItem)y).Tag;
            int c;
            if (col == 1) c = Pair(a.LocalPort, b.LocalPort, a.LocalAddr, b.LocalAddr);        // ローカル: ポート順
            else if (col == 2) c = Pair(a.RemotePort, b.RemotePort, a.RemoteAddr, b.RemoteAddr);
            else if (col == 4) c = a.Pid.CompareTo(b.Pid);
            else c = string.Compare(a.Cells[col], b.Cells[col], StringComparison.OrdinalIgnoreCase);
            return desc ? -c : c;
        }

        static int Pair(int p1, int p2, string a1, string a2)
        {
            int c = p1.CompareTo(p2);
            return c != 0 ? c : string.CompareOrdinal(a1, a2);
        }
    }

    void KillSelected()
    {
        var r = Selected;
        if (r == null || r.Block != null || busy) return;

        var ports = new List<string>();
        foreach (var o in rows) if (o.Pid == r.Pid && !ports.Contains(o.PortText)) ports.Add(o.PortText);
        string who = r.Name + " (PID " + r.Pid + ")";
        string msg = "このプロセスを終了しますか？\n\n  " + who + "\n" +
            (r.Path.Length > 0 ? "  " + r.Path + "\n" : "") +
            "  使っているポート: " + string.Join(", ", ports) + "\n\n保存していない作業は失われます。";
        if (MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        // ログ用: 「TCP 3000」（状態なし・重複なし）
        var portLog = new List<string>();
        foreach (var o in rows)
        {
            string p = o.Proto + " " + o.LocalPort;
            if (o.Pid == r.Pid && !portLog.Contains(p)) portLog.Add(p);
        }

        try
        {
            using (var p = Process.GetProcessById(r.Pid))
            {
                // PID が別のプロセスに使い回されていたら止めない
                if (!string.Equals(p.ProcessName, r.Name, StringComparison.OrdinalIgnoreCase))
                {
                    log("[ポート] 終了中止 " + who + " : プロセスが入れ替わっていた (" + p.ProcessName + ")");
                    MessageBox.Show(this, "プロセスが入れ替わっています。再読み込みしてください", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                p.Kill();
                p.WaitForExit(3000);
            }
            log("[ポート] 終了 " + who + " " + string.Join(", ", portLog));
            say("止めたにゃ");
        }
        catch (Win32Exception ex)
        {
            log("[ポート] 終了失敗 " + who + " : " + ex.Message);
            MessageBox.Show(this, "このプロセスは止められませんでした（管理者権限で動いている可能性があります）", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        catch (ArgumentException) { AlreadyExited(who); }          // GetProcessById の時点でもういない
        catch (InvalidOperationException) { AlreadyExited(who); }  // Kill の時点でもういない
        Reload();
    }

    void AlreadyExited(string who)
    {
        log("[ポート] 終了失敗 " + who + " : もう終了していた");
        MessageBox.Show(this, "もう終了していました", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void OpenLocation()
    {
        var r = Selected;
        if (r == null || r.Path.Length == 0) return;
        try { Process.Start("explorer.exe", "/select,\"" + r.Path + "\""); } catch { }
    }

    void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch { }   // ほかのアプリがクリップボードを掴んでいる
    }
}
