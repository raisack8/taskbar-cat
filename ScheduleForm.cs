// スケジュール設定画面（開いた時だけ作る）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

class ScheduleForm : Form
{
    readonly string path, baseDir;
    readonly Action onSaved;
    readonly Action<CronEntry> runNow;
    List<ScheduleItem> items = new List<ScheduleItem>();
    readonly ListView lv;
    bool filling, shown;

    protected override void OnShown(EventArgs e) { base.OnShown(e); shown = true; }

    public ScheduleForm(string path, Action onSaved, Action<CronEntry> runNow, Action openLog)
    {
        this.path = path;
        this.baseDir = TaskbarCat.HomeDir;   // 相対パスは保存先ではなくホームフォルダ基準
        this.onSaved = onSaved;
        this.runNow = runNow;

        Text = "スケジュール - TaskbarCat";
        Font = new Font("Yu Gothic UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 340);
        MinimumSize = new Size(520, 260);

        lv = new ListView
        {
            View = View.Details, FullRowSelect = true, CheckBoxes = true, HideSelection = false,
            MultiSelect = false, Dock = DockStyle.Fill,
        };
        lv.Columns.Add("タイミング", 210);
        lv.Columns.Add("ファイル", 280);
        lv.Columns.Add("セリフ", 140);
        lv.DoubleClick += (s, e) => Edit();
        lv.ItemChecked += (s, e) =>
        {
            // ListView はハンドル作成時にも ItemChecked を出す（一瞬 false→true になる）ので、
            // 画面が出きるまでと、一覧の作り直し中は無視する
            if (filling || !shown) return;
            var it = (ScheduleItem)e.Item.Tag;
            if (it.BadLine != null || it.Enabled == e.Item.Checked) return;
            it.Enabled = e.Item.Checked;
            Save();
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, Padding = new Padding(8, 4, 8, 4),
        };
        buttons.Controls.Add(Btn("追加…", (s, e) => Add()));
        buttons.Controls.Add(Btn("編集…", (s, e) => Edit()));
        buttons.Controls.Add(Btn("削除", (s, e) => Delete()));
        buttons.Controls.Add(Btn("今すぐ実行", (s, e) => RunSelected()));
        var gap = Btn("ログを開く", (s, e) => openLog());
        gap.Margin = new Padding(3, 18, 3, 3);
        buttons.Controls.Add(gap);
        buttons.Controls.Add(Btn("閉じる", (s, e) => Close()));

        var hint = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = false, Height = 26, Padding = new Padding(6, 4, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = "チェックを外すと一時停止。変更はすぐ保存されます。猫が起動している間だけ実行されます。",
        };

        Controls.Add(lv);       // Fill は最後にレイアウトされるよう先に追加
        Controls.Add(buttons);
        Controls.Add(hint);

        Reload();
    }

    static Button Btn(string text, EventHandler h)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(110, 28) };
        b.Click += h;
        return b;
    }

    void Reload()
    {
        try { items = Schedule.LoadItems(path); }
        catch (Exception ex) { MessageBox.Show(this, "schedule.txt を読めませんでした。\n" + ex.Message, Text); }
        Fill();
    }

    void Fill(ScheduleItem select = null)
    {
        filling = true;
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var it in items)
        {
            ListViewItem li;
            if (it.BadLine != null)
            {
                li = new ListViewItem(new[] { "読めない行（編集で直せます）", it.BadLine, "" }) { ForeColor = Color.Firebrick };
                li.Checked = false;
            }
            else
            {
                li = new ListViewItem(new[] { CronUi.Describe(it.Cron), it.File, it.Message }) { Checked = it.Enabled };
                if (!it.Enabled) li.ForeColor = SystemColors.GrayText;
            }
            li.Tag = it;
            lv.Items.Add(li);
            if (it == select) li.Selected = true;
        }
        lv.EndUpdate();
        filling = false;
    }

    ScheduleItem Selected { get { return lv.SelectedItems.Count > 0 ? (ScheduleItem)lv.SelectedItems[0].Tag : null; } }

    void Save()
    {
        try { Schedule.SaveItems(path, items); }
        catch (Exception ex) { MessageBox.Show(this, "保存できませんでした。\n" + ex.Message, Text); return; }
        onSaved();
        Fill(Selected);
    }

    void Add()
    {
        var it = new ScheduleItem();
        using (var d = new EntryDialog(it, baseDir))
            if (d.ShowDialog(this) == DialogResult.OK) { items.Add(it); Save(); Fill(it); }
    }

    void Edit()
    {
        var sel = Selected;
        if (sel == null) return;
        var copy = sel.Clone();
        using (var d = new EntryDialog(copy, baseDir))
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                items[items.IndexOf(sel)] = copy;
                Save();
                Fill(copy);
            }
    }

    void Delete()
    {
        var sel = Selected;
        if (sel == null) return;
        string name = sel.BadLine ?? Path.GetFileName(sel.File);
        if (MessageBox.Show(this, "「" + name + "」を削除しますか？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        items.Remove(sel);
        Save();
    }

    void RunSelected()
    {
        var sel = Selected;
        if (sel == null) return;
        if (sel.BadLine != null) { MessageBox.Show(this, "読めない行は実行できません。編集で直してください。", Text); return; }
        try { runNow(CronEntry.Parse(sel.EntryLine(), 0, baseDir)); }
        catch (FormatException ex) { MessageBox.Show(this, ex.Message, Text); }
    }
}

class EntryDialog : Form
{
    readonly ScheduleItem item;
    readonly string baseDir;
    readonly TextBox file, msg, raw;
    readonly RadioButton rTime, rInterval, rRandom, rRaw;
    readonly DateTimePicker time;
    readonly ComboBox every;
    readonly CheckBox ranged;
    readonly NumericUpDown from, to, rmin, rmax;
    readonly CheckBox[] days = new CheckBox[7];
    readonly FlowLayoutPanel pTime, pInterval, pRandom, pRaw, pDays;
    readonly Label preview;

    public EntryDialog(ScheduleItem item, string baseDir)
    {
        this.item = item;
        this.baseDir = baseDir;

        Text = "スケジュールの編集";
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

        // ファイル
        file = new TextBox { Width = 340 };
        var browse = new Button { Text = "参照…", AutoSize = true };
        browse.Click += (s, e) => Browse();
        Row(tl, "ファイル", Flow(file, browse));

        // タイミング
        rTime = new RadioButton { Text = "時刻を指定", AutoSize = true };
        rInterval = new RadioButton { Text = "一定間隔", AutoSize = true };
        rRaw = new RadioButton { Text = "cron で指定", AutoSize = true };
        rRandom = new RadioButton { Text = "きまぐれ", AutoSize = true };
        Row(tl, "タイミング", Flow(rTime, rInterval, rRandom, rRaw));

        time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
        pTime = Flow(Lbl("時刻"), time);

        every = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
        foreach (int m in CronUi.EveryChoices) every.Items.Add(m < 60 ? m + "分" : m / 60 + "時間");
        ranged = new CheckBox { Text = "時間帯を限定", AutoSize = true, Margin = new Padding(16, 5, 3, 3) };
        from = new NumericUpDown { Minimum = 0, Maximum = 23, Width = 48 };
        to = new NumericUpDown { Minimum = 0, Maximum = 23, Width = 48 };
        pInterval = Flow(every, Lbl("ごと"), ranged, from, Lbl("時〜"), to, Lbl("時台"));

        raw = new TextBox { Width = 160 };
        pRaw = Flow(raw, Lbl("分 時 日 月 曜日（例: */15 9-18 * * 1-5）"));

        // きまぐれ: 最小〜最大分のあいだでランダムな間隔
        rmin = new NumericUpDown { Minimum = 1, Maximum = 1440, Width = 64 };
        rmax = new NumericUpDown { Minimum = 1, Maximum = 1440, Width = 64 };
        pRandom = Flow(rmin, Lbl("〜"), rmax, Lbl("分おきに、猫の気分で実行（間隔は毎回ランダム）"));

        // 3つの入力欄を1つのセルにまとめ、選んでいない欄は隠して詰める
        var modes = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        modes.Controls.AddRange(new Control[] { pTime, pInterval, pRandom, pRaw });
        Row(tl, "", modes);

        // 曜日（月〜日の順に並べる）
        pDays = Flow();
        for (int i = 1; i <= 7; i++)
        {
            int d = i % 7;
            days[d] = new CheckBox { Text = CronUi.DayNames[d], AutoSize = true, Margin = new Padding(3, 3, 6, 3) };
            pDays.Controls.Add(days[d]);
        }
        var weekdays = new LinkLabel { Text = "平日", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
        weekdays.Click += (s, e) => { for (int d = 0; d < 7; d++) days[d].Checked = d >= 1 && d <= 5; };
        var all = new LinkLabel { Text = "毎日", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        all.Click += (s, e) => { foreach (var c in days) c.Checked = true; };
        pDays.Controls.Add(weekdays);
        pDays.Controls.Add(all);
        Row(tl, "曜日", pDays);

        msg = new TextBox { Width = 200 };
        Row(tl, "セリフ（任意）", Flow(msg, Lbl("実行時に猫が言う。空なら「にゃっ！」")));

        preview = new Label { AutoSize = true, Margin = new Padding(3, 10, 3, 6), ForeColor = Color.SteelBlue };
        Row(tl, "", preview);

        var ok = new Button { Text = "OK", MinimumSize = new Size(88, 28), AutoSize = true };
        var cancel = new Button { Text = "キャンセル", MinimumSize = new Size(88, 28), AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (s, e) => Ok();
        var pButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        pButtons.Controls.Add(cancel);
        pButtons.Controls.Add(ok);
        Row(tl, "", pButtons);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(tl);

        // 値を入れる
        file.Text = item.File;
        msg.Text = item.Message;
        string cron = item.Cron;
        if (item.BadLine != null)
        {
            // 読めない行: | の前を cron 欄に、後ろをファイル欄に入れて直してもらう
            var parts = item.BadLine.Replace("#off ", "").Split('|');
            cron = parts[0].Trim();
            if (parts.Length > 1) file.Text = parts[1].Trim().Trim('"');
            if (parts.Length > 2) msg.Text = parts[2].Trim();
        }
        var spec = CronUi.Decode(cron);
        time.Value = DateTime.Today.AddHours(spec.Hour).AddMinutes(spec.Minute);
        every.SelectedIndex = Math.Max(0, Array.IndexOf(CronUi.EveryChoices, spec.Every));
        ranged.Checked = spec.Ranged;
        from.Value = spec.From; to.Value = spec.To;
        for (int d = 0; d < 7; d++) days[d].Checked = spec.Dow[d];
        raw.Text = cron;
        if (item.BadLine != null) spec.Kind = CronUi.Kind.Raw;
        rmin.Value = Math.Max(1, Math.Min(1440, spec.RandMin)); rmax.Value = Math.Max(1, Math.Min(1440, spec.RandMax));
        (spec.Kind == CronUi.Kind.Time ? rTime : spec.Kind == CronUi.Kind.Interval ? rInterval : spec.Kind == CronUi.Kind.Random ? rRandom : rRaw).Checked = true;

        EventHandler changed = (s, e) => UpdateUi();
        foreach (Control c in new Control[] { rTime, rInterval, rRandom, rRaw, time, every, ranged, from, to, rmin, rmax, raw, file })
        {
            if (c is RadioButton) ((RadioButton)c).CheckedChanged += changed;
            else if (c is CheckBox) ((CheckBox)c).CheckedChanged += changed;
            else if (c is DateTimePicker) ((DateTimePicker)c).ValueChanged += changed;
            else if (c is ComboBox) ((ComboBox)c).SelectedIndexChanged += changed;
            else if (c is NumericUpDown) ((NumericUpDown)c).ValueChanged += changed;
            else c.TextChanged += changed;
        }
        foreach (var c in days) c.CheckedChanged += changed;
        UpdateUi();
    }

    static FlowLayoutPanel Flow(params Control[] cs)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Dock = DockStyle.Fill };
        p.Controls.AddRange(cs);
        return p;
    }

    static Label Lbl(string t) { return new Label { Text = t, AutoSize = true, Margin = new Padding(3, 6, 3, 3) }; }

    int rows;

    // 行・列を明示して置く（自動配置だと非表示の部品があると位置がずれる）
    void Row(TableLayoutPanel tl, string label, Control c)
    {
        tl.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, rows);
        tl.Controls.Add(c, 1, rows);
        rows++;
    }

    CronUi.Spec ReadSpec()
    {
        var s = new CronUi.Spec
        {
            Kind = rTime.Checked ? CronUi.Kind.Time : rInterval.Checked ? CronUi.Kind.Interval : rRandom.Checked ? CronUi.Kind.Random : CronUi.Kind.Raw,
            RandMin = (int)rmin.Value, RandMax = (int)rmax.Value,
            Hour = time.Value.Hour, Minute = time.Value.Minute,
            Every = CronUi.EveryChoices[Math.Max(0, every.SelectedIndex)],
            Ranged = ranged.Checked, From = (int)from.Value, To = (int)to.Value,
            Raw = raw.Text,
        };
        for (int d = 0; d < 7; d++) s.Dow[d] = days[d].Checked;
        return s;
    }

    string Validate(out string cron)
    {
        cron = CronUi.Encode(ReadSpec());
        if (rInterval.Checked && ranged.Checked && from.Value > to.Value) return "時間帯の開始が終了より後になっています";
        if (file.Text.Trim().Length == 0) return "実行するファイルを指定してください";
        try { CronEntry.Parse(cron + " | x", 0, baseDir); }
        catch (FormatException ex) { return ex.Message; }
        return null;
    }

    void UpdateUi()
    {
        pTime.Visible = rTime.Checked;
        pInterval.Visible = rInterval.Checked;
        pRandom.Visible = rRandom.Checked;
        pRaw.Visible = rRaw.Checked;
        pDays.Enabled = !rRaw.Checked && !rRandom.Checked;
        from.Enabled = to.Enabled = ranged.Checked;

        string cron;
        string err = Validate(out cron);
        if (err != null && !(err.StartsWith("実行するファイル") && file.Text.Length == 0))
        {
            preview.ForeColor = Color.Firebrick;
            preview.Text = err;
        }
        else
        {
            preview.ForeColor = Color.SteelBlue;
            preview.Text = "→ " + CronUi.Describe(cron) + " に実行　（書式: " + cron + "）";
        }
    }

    void Browse()
    {
        using (var d = new OpenFileDialog
        {
            Filter = "スクリプト (*.ps1;*.bat;*.cmd;*.py)|*.ps1;*.bat;*.cmd;*.py|すべてのファイル (*.*)|*.*",
            Title = "実行するファイル",
        })
        {
            try { if (file.Text.Length > 0) d.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(baseDir, Environment.ExpandEnvironmentVariables(file.Text)))); } catch { }
            if (d.ShowDialog(this) == DialogResult.OK) file.Text = d.FileName;
        }
    }

    void Ok()
    {
        string cron;
        string err = Validate(out cron);
        if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        string f = file.Text.Trim().Trim('"');
        string full = Path.IsPathRooted(Environment.ExpandEnvironmentVariables(f)) ? Environment.ExpandEnvironmentVariables(f)
            : Path.Combine(baseDir, Environment.ExpandEnvironmentVariables(f));
        if (!File.Exists(full) &&
            MessageBox.Show(this, "ファイルが見つかりません。このまま保存しますか？\n" + full, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        item.Cron = cron;
        item.File = f;
        item.Message = msg.Text.Trim().Replace("|", "");
        item.BadLine = null;
        DialogResult = DialogResult.OK;
    }
}
