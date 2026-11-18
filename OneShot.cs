// 単発バッチ: oneshot.txt に登録したファイルを、右クリックメニューから好きな時に1回実行する
//   書式:  メニューに出す名前 | 実行するファイル | ショートカットキー（省略可）
//   実行は常にバックグラウンド（コンソールのウィンドウは出さない）。旧書式の「| ウィンドウ表示」はキーとして読めないので無視される
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

class OneShotItem
{
    public string Name = "", File = "", Key = "";   // Key: "Ctrl+Alt+B" の形（Hotkey.Format で正規化済み）。空ならなし

    public string DisplayName { get { return Name.Length > 0 ? Name : Path.GetFileNameWithoutExtension(File); } }

    public string ToLine() { return Name.Replace("|", "") + " | " + File + (Key.Length > 0 ? " | " + Key : ""); }

    public OneShotItem Clone() { return (OneShotItem)MemberwiseClone(); }
}

static class OneShot
{
    public const string Header =
@"# TaskbarCat 単発バッチ
# 書式:  メニューに出す名前 | 実行するファイル | ショートカットキー
#
#   名前    : 省略するとファイル名がメニューに出ます（「| ファイル」だけ書く）
#   ファイル: フルパス、または %USERPROFILE%.taskbar-cat からの相対パス。%USERPROFILE% などの環境変数も使えます
#   実行方法: .ps1 → PowerShell / .bat .cmd → cmd / .py → python（いずれもウィンドウなしのバックグラウンド）
#             その他 → ダブルクリックと同じ
#   キー    : 省略可。Ctrl+Alt+B のように書くと、どのアプリを使っていてもそのキーで即実行します
#             Ctrl / Alt / Win のどれかを含めてください（Shift は追加で使えます）
#
# 右クリック →「タスク」の一覧から実行できます。上から順にメニューに並びます。
#
# バックアップ | C:\scripts\backup.bat | Ctrl+Alt+B
";

    public static List<OneShotItem> Load(string path)
    {
        var items = new List<OneShotItem>();
        if (!System.IO.File.Exists(path)) return items;
        foreach (var raw in System.IO.File.ReadAllLines(path, Encoding.UTF8))
        {
            string l = raw.Trim();
            if (l.Length == 0 || l.StartsWith("#")) continue;
            string[] parts = l.Split('|');   // パスに | は使えないので素直に分けてよい
            var it = parts.Length == 1 ? new OneShotItem { File = l.Trim('"') }
                : new OneShotItem { Name = parts[0].Trim(), File = parts[1].Trim().Trim('"') };
            if (parts.Length >= 3) it.Key = Hotkey.Normalize(parts[2]);   // 読めない（旧書式の「ウィンドウ表示」等）なら空
            if (it.File.Length > 0) items.Add(it);
        }
        return items;
    }

    public static void Save(string path, List<OneShotItem> items)
    {
        var sb = new StringBuilder(Header);
        sb.Append("\r\n");
        foreach (var it in items) sb.Append(it.ToLine()).Append("\r\n");
        System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    // 環境変数を展開し、相対パスは exe のフォルダから
    public static string Resolve(string baseDir, string file)
    {
        string f = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
        try { return Path.IsPathRooted(f) ? f : Path.GetFullPath(Path.Combine(baseDir, f)); }
        catch (ArgumentException) { return f; }   // パスに使えない文字 → 実行時に「見つからない」になる
    }
}

// グローバルホットキー（RegisterHotKey）の文字列 ⇔ 修飾キー・仮想キー
static class Hotkey
{
    public const int MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    const int Required = MOD_CONTROL | MOD_ALT | MOD_WIN;   // Shift だけ・修飾なしは普段の入力を奪うので不可

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // "Ctrl+Alt+B" → mods, key。使えない書き方なら false
    public static bool TryParse(string s, out int mods, out Keys key)
    {
        mods = 0;
        key = Keys.None;
        if (string.IsNullOrEmpty(s)) return false;
        string[] parts = s.Split('+');
        for (int i = 0; i < parts.Length - 1; i++)
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default: return false;
            }
        key = ParseKey(parts[parts.Length - 1].Trim());
        return key != Keys.None && (mods & Required) != 0;
    }

    static Keys ParseKey(string p)
    {
        if (p.Length == 1)
        {
            char c = char.ToUpperInvariant(p[0]);
            if (c >= '0' && c <= '9') return Keys.D0 + (c - '0');
            if (c >= 'A' && c <= 'Z') return (Keys)c;
            return Keys.None;
        }
        if (p.Length == 0 || char.IsDigit(p[0]) || p.Contains(",")) return Keys.None;   // Enum.TryParse が数値やカンマ区切りも受け付けるため
        Keys k;
        if (!Enum.TryParse(p, true, out k) || (k & Keys.Modifiers) != 0 || IsModifierKey(k)) return Keys.None;
        return k;
    }

    public static bool IsModifierKey(Keys k)
    {
        switch (k)
        {
            case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey:
            case Keys.ControlKey: case Keys.LControlKey: case Keys.RControlKey:
            case Keys.Menu: case Keys.LMenu: case Keys.RMenu:
            case Keys.LWin: case Keys.RWin:
                return true;
        }
        return false;
    }

    public static string Prefix(int mods)
    {
        return ((mods & MOD_CONTROL) != 0 ? "Ctrl+" : "") + ((mods & MOD_ALT) != 0 ? "Alt+" : "")
             + ((mods & MOD_SHIFT) != 0 ? "Shift+" : "") + ((mods & MOD_WIN) != 0 ? "Win+" : "");
    }

    // mods に Ctrl / Alt / Win のどれも無ければ ""（使えない組み合わせ）
    public static string Format(int mods, Keys key)
    {
        if ((mods & Required) == 0 || key == Keys.None || IsModifierKey(key)) return "";
        string name;
        if (key >= Keys.D0 && key <= Keys.D9) name = ((char)('0' + (key - Keys.D0))).ToString();
        else if (key == Keys.Prior) name = "PageUp";
        else if (key == Keys.Next) name = "PageDown";
        else if (key == Keys.Return) name = "Enter";
        else name = key.ToString();
        return Prefix(mods) + name;
    }

    public static string Normalize(string s)
    {
        int mods; Keys key;
        return TryParse(s, out mods, out key) ? Format(mods, key) : "";
    }

    // 今このキーを登録できるか（ほかのアプリが使っていないか）を、一時的に登録して確かめる
    public static bool IsFree(IntPtr hWnd, string s)
    {
        int mods; Keys key;
        if (!TryParse(s, out mods, out key)) return false;
        const int TestId = 0xBFFE;
        if (!RegisterHotKey(hWnd, TestId, mods | MOD_NOREPEAT, (int)key)) return false;
        UnregisterHotKey(hWnd, TestId);
        return true;
    }
}

// 押したキーの組み合わせをそのまま入れる欄。Backspace / Delete で解除。Tab・Enter・Esc は普段どおり
class HotkeyBox : TextBox
{
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    string value = "";

    public HotkeyBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        ShortcutsEnabled = false;
        ShowValue();
    }

    public string Value
    {
        get { return value; }
        set { this.value = value ?? ""; ShowValue(); }
    }

    void ShowValue() { Text = value.Length > 0 ? value : "（なし）"; }

    static bool WinDown { get { return GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0; } }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys code = keyData & Keys.KeyCode, m = keyData & Keys.Modifiers;
        bool win = WinDown;
        bool plain = !win && (m == Keys.None || (m == Keys.Shift && code == Keys.Tab));
        if (plain && (code == Keys.Tab || code == Keys.Enter || code == Keys.Escape)) return base.ProcessCmdKey(ref msg, keyData);
        if (plain && (code == Keys.Back || code == Keys.Delete)) { Value = ""; return true; }

        int mods = ((m & Keys.Control) != 0 ? Hotkey.MOD_CONTROL : 0) | ((m & Keys.Alt) != 0 ? Hotkey.MOD_ALT : 0)
                 | ((m & Keys.Shift) != 0 ? Hotkey.MOD_SHIFT : 0) | (win ? Hotkey.MOD_WIN : 0);
        if (Hotkey.IsModifierKey(code)) { Text = Hotkey.Prefix(mods); return true; }   // 押している途中
        string s = Hotkey.Format(mods, code);
        if (s.Length > 0) Value = s;
        else ShowValue();   // Ctrl / Alt / Win なし → 受け付けない
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if ((ModifierKeys & (Keys.Control | Keys.Alt | Keys.Shift)) == 0 && !WinDown) ShowValue();
        base.OnKeyUp(e);
    }

    protected override void OnLeave(EventArgs e) { ShowValue(); base.OnLeave(e); }
}

// 単発バッチの設定画面（開いた時だけ作る）
class OneShotForm : Form
{
    readonly string path, baseDir;
    readonly Action<string> runNow;   // フルパス
    readonly Action saved;            // 保存した後（ホットキーを登録し直す）
    List<OneShotItem> items = new List<OneShotItem>();
    readonly ListView lv;

    public OneShotForm(string path, Action<string> runNow, Action saved)
    {
        this.path = path;
        this.baseDir = TaskbarCat.HomeDir;   // 相対パスは保存先ではなくホームフォルダ基準
        this.runNow = runNow;
        this.saved = saved;

        Text = "単発バッチ - TaskbarCat";
        Font = new Font("Yu Gothic UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 340);
        MinimumSize = new Size(460, 300);

        lv = new ListView
        {
            View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, Dock = DockStyle.Fill,
        };
        lv.Columns.Add("名前", 150);
        lv.Columns.Add("ファイル", 350);
        lv.Columns.Add("ショートカット", 120);
        lv.DoubleClick += (s, e) => Edit();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, Padding = new Padding(8, 4, 8, 4),
        };
        buttons.Controls.Add(Btn("追加…", (s, e) => Add()));
        buttons.Controls.Add(Btn("編集…", (s, e) => Edit()));
        buttons.Controls.Add(Btn("削除", (s, e) => Delete()));
        buttons.Controls.Add(Btn("上へ", (s, e) => MoveSelected(-1)));
        buttons.Controls.Add(Btn("下へ", (s, e) => MoveSelected(1)));
        var run = Btn("今すぐ実行", (s, e) => RunSelected());
        run.Margin = new Padding(3, 18, 3, 3);
        buttons.Controls.Add(run);
        buttons.Controls.Add(Btn("閉じる", (s, e) => Close()));

        var hint = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = false, Height = 26, Padding = new Padding(6, 4, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "登録したものは 右クリック →「タスク」に並びます。ショートカットキーはどのアプリを使っていても効きます。変更はすぐ保存されます。",
        };

        Controls.Add(lv);       // Fill は最後にレイアウトされるよう先に追加
        Controls.Add(buttons);
        Controls.Add(hint);

        try { items = OneShot.Load(path); }
        catch (Exception ex) { MessageBox.Show(this, "oneshot.txt を読めませんでした。\n" + ex.Message, Text); }
        Fill();
    }

    static Button Btn(string text, EventHandler h)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 28) };
        b.Click += h;
        return b;
    }

    void Fill(OneShotItem select = null)
    {
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var it in items)
        {
            var li = new ListViewItem(new[] { it.DisplayName, it.File, it.Key }) { Tag = it };
            if (it.Name.Length == 0) li.ForeColor = SystemColors.GrayText;   // ファイル名をそのまま使っている
            lv.Items.Add(li);
            if (it == select) { li.Selected = true; li.EnsureVisible(); }
        }
        lv.EndUpdate();
    }

    OneShotItem Selected { get { return lv.SelectedItems.Count > 0 ? (OneShotItem)lv.SelectedItems[0].Tag : null; } }

    void Save(OneShotItem select)
    {
        try { OneShot.Save(path, items); }
        catch (Exception ex) { MessageBox.Show(this, "保存できませんでした。\n" + ex.Message, Text); }
        Fill(select);
        saved();
    }

    void Add()
    {
        var it = new OneShotItem();
        using (var d = new OneShotDialog(it, baseDir, KeyOwner(null)))
            if (d.ShowDialog(this) == DialogResult.OK) { items.Add(it); Save(it); }
    }

    void Edit()
    {
        var sel = Selected;
        if (sel == null) return;
        var copy = sel.Clone();
        using (var d = new OneShotDialog(copy, baseDir, KeyOwner(sel)))
            if (d.ShowDialog(this) == DialogResult.OK) { items[items.IndexOf(sel)] = copy; Save(copy); }
    }

    // キー → そのキーを使っているほかの項目（self 以外）
    Func<string, OneShotItem> KeyOwner(OneShotItem self)
    {
        return k => items.Find(o => o != self && o.Key.Length > 0 && o.Key == k);
    }

    void Delete()
    {
        var sel = Selected;
        if (sel == null) return;
        if (MessageBox.Show(this, "「" + sel.DisplayName + "」を削除しますか？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        items.Remove(sel);
        Save(null);
    }

    void MoveSelected(int d)
    {
        var sel = Selected;
        if (sel == null) return;
        int i = items.IndexOf(sel), j = i + d;
        if (j < 0 || j >= items.Count) return;
        items[i] = items[j];
        items[j] = sel;
        Save(sel);
    }

    void RunSelected()
    {
        var sel = Selected;
        if (sel != null) runNow(OneShot.Resolve(baseDir, sel.File));
    }
}

class OneShotDialog : Form
{
    readonly OneShotItem item;
    readonly string baseDir;
    readonly Func<string, OneShotItem> keyOwner;
    readonly TextBox name, file;
    readonly HotkeyBox key;

    public OneShotDialog(OneShotItem item, string baseDir, Func<string, OneShotItem> keyOwner)
    {
        this.item = item;
        this.baseDir = baseDir;
        this.keyOwner = keyOwner;

        Text = "単発バッチの登録";
        Font = new Font("Yu Gothic UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var tl = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        file = new TextBox { Width = 340 };
        var browse = new Button { Text = "参照…", AutoSize = true };
        browse.Click += (s, e) => Browse();
        Row(tl, "ファイル", Flow(file, browse));

        name = new TextBox { Width = 200 };
        Row(tl, "名前（任意）", Flow(name, new Label { Text = "メニューに出す名前。空ならファイル名", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }));

        key = new HotkeyBox { Width = 160 };
        var clear = new Button { Text = "解除", AutoSize = true };
        clear.Click += (s, e) => { key.Value = ""; key.Focus(); };
        Row(tl, "ショートカット", Flow(key, clear, new Label
        {
            Text = "欄を選んでキーを押す（Ctrl / Alt / Win と組み合わせる）", AutoSize = true,
            ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3),
        }));

        Row(tl, "実行", new Label
        {
            Text = "バックグラウンドで実行（ウィンドウは出ません。出力は吹き出しとログに出ます）", AutoSize = true,
            ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 3),
        });

        var ok = new Button { Text = "OK", MinimumSize = new Size(88, 28), AutoSize = true };
        var cancel = new Button { Text = "キャンセル", MinimumSize = new Size(88, 28), AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (s, e) => Ok();
        var pButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        pButtons.Controls.Add(cancel);
        pButtons.Controls.Add(ok);
        Row(tl, "", pButtons);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(tl);

        file.Text = item.File;
        name.Text = item.Name;
        key.Value = item.Key;
    }

    static FlowLayoutPanel Flow(params Control[] cs)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
        p.Controls.AddRange(cs);
        return p;
    }

    int rows;

    void Row(TableLayoutPanel tl, string label, Control c)
    {
        tl.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, rows);
        tl.Controls.Add(c, 1, rows);
        rows++;
    }

    void Browse()
    {
        using (var d = new OpenFileDialog
        {
            Filter = "バッチ・スクリプト (*.bat;*.cmd;*.ps1;*.py)|*.bat;*.cmd;*.ps1;*.py|すべてのファイル (*.*)|*.*",
            Title = "実行するファイル",
        })
        {
            try { if (file.Text.Length > 0) d.InitialDirectory = Path.GetDirectoryName(OneShot.Resolve(baseDir, file.Text)); } catch { }
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                file.Text = d.FileName;
                if (name.Text.Trim().Length == 0) name.Text = Path.GetFileNameWithoutExtension(d.FileName);
            }
        }
    }

    void Ok()
    {
        string f = file.Text.Trim().Trim('"');
        if (f.Length == 0) { MessageBox.Show(this, "実行するファイルを指定してください", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        string full = OneShot.Resolve(baseDir, f);
        if (!File.Exists(full) &&
            MessageBox.Show(this, "ファイルが見つかりません。このまま保存しますか？\n" + full, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        string k = key.Value;
        if (k.Length > 0)
        {
            var owner = keyOwner(k);
            if (owner != null)
            {
                MessageBox.Show(this, k + " は「" + owner.DisplayName + "」で使っています。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                key.Focus();
                return;
            }
            // 元から自分のキーなら猫が登録中なので確かめない
            if (k != item.Key && !Hotkey.IsFree(Handle, k) &&
                MessageBox.Show(this, k + " はほかのアプリか Windows が使っているため、今は効きません。\nこのまま保存しますか？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                key.Focus();
                return;
            }
        }

        item.File = f;
        item.Name = name.Text.Trim().Replace("|", "");
        item.Key = k;
        DialogResult = DialogResult.OK;
    }
}
