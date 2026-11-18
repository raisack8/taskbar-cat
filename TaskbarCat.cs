// タスクバーの上を歩く猫。build.bat でビルド (.NET Framework 標準の csc.exe のみ使用)
// スプライトは assets/sprites/*.png を exe に埋め込む（tools/build_sprites.py で生成）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class TaskbarCat : Form
{
    // ---- Win32 ----
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT ptSrc, int key, ref BLEND bl, int flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, int usage, out IntPtr bits, IntPtr sec, int off);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
    struct RECT { public int L, T, R, B; }
    struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    struct BITMAPINFOHEADER { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }

    // ---- スプライト ----
    const int WalkFrames = 6;             // walk.png のコマ数（build_sprites.py と合わせる）
    static int CellW, CellH;              // build_sprites.py のセルサイズ (2x)。walk.png から読む
    const int StandH = 64;                // 中サイズ・100% DPI での立ち姿の高さ
    const int TalkMouths = 1;             // しゃべる時に口を開ける回数

    class Frame { public Bitmap Bmp; public int X, Y; }
    class Anim { public Frame[] F; public int[] Ms; public int Top, HeadX; }

    enum Mode { Walk, Stand, Sit, Sleep, Once, Talk, Hide, Hidden, Drag }   // Hide = 隅へ移動中, Hidden = 隠れ中, Drag = つかまれ中

    // 隠れ中: 右下の隅で座り、体を画面の外に出して、しっぽだけ見せる
    const int HideCut = 64;   // 座りスプライト(2x)でこの x より右（体）を画面外に出す
    bool hidden;
    EventWaitHandle showEvent;   // 2つ目の起動から「出てきて」を受け取る
    double HiddenX() { return wa.Right - HideCut * f + cellW / 2.0; }
    enum Fx { None = -1, Bang = 0, Question = 1, Heart = 2, Zzz = 3, Sweat = 4, Note = 5 }

    Anim walk, idle, sit, hideSit, blink, sleep, sitDown, standUp, lieDown, wake, surprised, turn;
    Anim talk, talkSit, turnSit, turnSitBack;   // しゃべる時: 立ち口パク / 座ったまま振り向き / 座り口パク
    Mode talkReturn;                             // しゃべり終わったら戻る状態
    Bitmap[] icons;
    float f;            // マスター(2x)→表示の倍率
    int cellW, cellH;
    float dpi;
    float sizeMul = 1f;
    bool talkEnabled = true;
    bool fxEnabled = true;
    Fx ShownFx { get { return fxEnabled ? fx : Fx.None; } }
    static bool restart;

    // アップデート（Update.cs）: 起動 1 分後と毎日 12:00 に確認し、見つかったら猫が知らせてメニューに出す
    Updater.Release update;   // 見つかった新しい版。無ければ null
    bool updateCheck = true, checkingUpdate, updating;
    DateTime nextUpdateCheck;

    // ---- 状態 ----
    Rectangle wa;
    double x;           // 猫の中心のスクリーン x
    int dir = 1;
    Mode mode;
    Anim cur;
    int fi, frameEnd, modeEnd, loops;   // loops = ループ再生が先頭に戻った回数
    bool loop;
    Action onDone;
    Fx fx = Fx.None;
    int fxEnd, nextTalk, talkEnd, nextCheck, lastTick;
    string talkText;
    SizeF talkSize;
    Font font;
    bool dirty = true;
    Rectangle lastBounds;
    int lastBx;

    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Random rnd = new Random();
    // ホームフォルダ: ini を置き、相対パスの基準にもする（exe の場所に何も書かないため）
    internal static readonly string HomeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".taskbar-cat");
    readonly string iniPath = Path.Combine(HomeDir, "TaskbarCat.ini");   // ini は保存先の設定を持つので常にホームフォルダ

    // 保存先（ini の data=）。空ならホームフォルダ
    static readonly string[] DataFiles = { "schedule.txt", "oneshot.txt", "messages.txt" };   // 保存先を変える時にコピーするもの
    string dataSetting = "";
    string dataDir;

    // スケジュール実行・単発バッチ
    string schedPath, oneShotPath, logDir;
    List<CronEntry> sched = new List<CronEntry>();
    DateTime schedStamp;
    long lastMinute;
    readonly object logLock = new object();

    // 描画先 DIB
    IntPtr memDC, dib, bits;
    Bitmap canvas;
    Graphics cg;
    int dibW, dibH;

    string[] lines = {
        "にゃーん", "にゃ？"
    };
    // 猫語のみ
    static readonly string[] SleepLines = { "zzz…", "むにゃ…", "すぴー…" };
    static readonly string[] ClickLines = { "にゃっ！", "にゃ！？", "ふにゃっ", "みゃっ！" };
    static readonly string[] PetLines = { "ごろごろ♪", "にゃ〜ん♪", "ふにゃ〜", "ごろにゃ〜ん" };
    static readonly string[] WakeLines = { "にゃ…？", "ふにゃ〜…", "んにゃ…" };

    TaskbarCat()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;

        using (var g = CreateGraphics()) dpi = g.DpiX;
        try { Directory.CreateDirectory(HomeDir); } catch { }
        LoadSettings();
        string dataErr;
        dataDir = ResolveDataDir(dataSetting, out dataErr);
        schedPath = Path.Combine(dataDir, "schedule.txt");
        oneShotPath = Path.Combine(dataDir, "oneshot.txt");
        logDir = Path.Combine(dataDir, "logs");
        if (dataErr != null) Log("[設定] 保存先 " + dataSetting + " を使えないのでホームフォルダを使います : " + dataErr);
        LoadSprites();
        font = new Font("Yu Gothic UI", 13f * dpi / 96f, GraphicsUnit.Pixel);

        string custom = Path.Combine(dataDir, "messages.txt");
        if (File.Exists(custom))
        {
            var l = new List<string>();
            foreach (var s in File.ReadAllLines(custom, Encoding.UTF8)) if (s.Trim().Length > 0) l.Add(s.Trim());
            if (l.Count > 0) lines = l.ToArray();
        }

        var menu = new ContextMenu();
        var hideItem = menu.MenuItems.Add("隠れる", (s, e) => SetHidden(!hidden));
        menu.Popup += (s, e) => hideItem.Checked = hidden;
        var tasks = menu.MenuItems.Add("タスク");
        FillTasksMenu(tasks);
        tasks.Popup += (s, e) => FillTasksMenu(tasks);   // 開くたびに oneshot.txt から作り直す
        var look = menu.MenuItems.Add("外見");
        var talkItem = look.MenuItems.Add("しゃべる", (s, e) =>
        {
            talkEnabled = !talkEnabled;
            if (!talkEnabled && talkText != null) { talkText = null; dirty = true; }
            SaveSettings();
        });
        var fxItem = look.MenuItems.Add("エフェクト", (s, e) =>
        {
            fxEnabled = !fxEnabled;
            dirty = true;
            SaveSettings();
        });
        var size = look.MenuItems.Add("大きさ");
        foreach (var p in new[] { new KeyValuePair<string, float>("小", 0.75f), new KeyValuePair<string, float>("中", 1f), new KeyValuePair<string, float>("大", 1.4f) })
        {
            float m = p.Value;
            var it = size.MenuItems.Add(p.Key, (s, e) => SetSize(m));
            it.RadioCheck = true;
            size.Popup += (s, e) => it.Checked = Math.Abs(sizeMul - m) < 0.01f;
        }
        look.Popup += (s, e) =>
        {
            talkItem.Checked = talkEnabled;
            fxItem.Checked = fxEnabled;
        };
        var settings = menu.MenuItems.Add("設定");
        var data = settings.MenuItems.Add("保存先");
        var dataNow = data.MenuItems.Add("");
        dataNow.Enabled = false;
        data.MenuItems.Add("-");
        var dataHome = data.MenuItems.Add("既定（" + HomeDir.Replace("&", "&&") + "）", (s, e) => ChangeDataDir(""));
        var dataPick = data.MenuItems.Add("フォルダを選ぶ…", (s, e) => PickDataDir());
        dataHome.RadioCheck = dataPick.RadioCheck = true;
        data.MenuItems.Add("-");
        data.MenuItems.Add("保存先を開く", (s, e) => OpenFile("explorer.exe", dataDir));
        data.Popup += (s, e) =>
        {
            dataNow.Text = dataDir.Replace("&", "&&");
            dataHome.Checked = dataSetting.Length == 0;
            dataPick.Checked = !dataHome.Checked;
        };
        settings.MenuItems.Add("-");
        var updCheckItem = settings.MenuItems.Add("アップデートを知らせる", (s, e) => { updateCheck = !updateCheck; SaveSettings(); });
        settings.MenuItems.Add("アップデートを確認", (s, e) => CheckUpdate(true));
        settings.Popup += (s, e) => updCheckItem.Checked = updateCheck;
        menu.MenuItems.Add("-");
        menu.MenuItems.Add("ポートを調べる", (s, e) => OpenPortForm());
        menu.MenuItems.Add("タスクマネージャー", (s, e) => Launch("taskmgr.exe"));
        menu.MenuItems.Add("コントロールパネル", (s, e) => Launch("control.exe"));
        menu.MenuItems.Add("-");
        var updateItem = menu.MenuItems.Add("アップデート", (s, e) => StartUpdate());
        updateItem.Visible = false;
        menu.Popup += (s, e) =>
        {
            updateItem.Visible = update != null;
            updateItem.Enabled = !updating;
            if (update != null) updateItem.Text = "アップデート（v" + update.Ver + "）" + (updating ? " 中…" : "");
        };
        menu.MenuItems.Add("再起動", (s, e) => { restart = true; Close(); });
        menu.MenuItems.Add("終了", (s, e) => Close());
        ContextMenu = menu;
        MouseDown += (s, e) => OnDown(e);
        MouseMove += (s, e) => OnDrag(e);
        MouseUp += (s, e) => OnUp(e);

        wa = Screen.PrimaryScreen.WorkingArea;
        x = wa.Left + cellW / 2 + rnd.Next(Math.Max(1, wa.Width - cellW));
        int now = Environment.TickCount;
        lastTick = now;
        nextTalk = now + 5000;
        nextCheck = now;
        if (hidden) { x = HiddenX(); dir = 1; SetMode(Mode.Hidden); }
        else SetMode(Mode.Stand);

        // TaskbarCat.exe をもう一度起動されたら、隠れていても出てくる（右クリックできなくなった時の保険）
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "TaskbarCat_Show");
        ThreadPool.RegisterWaitForSingleObject(showEvent, (st, timedOut) =>
        {
            try { BeginInvoke((Action)(() => { if (hidden) SetHidden(false); else Say("にゃ？"); })); } catch { }
        }, null, -1, false);
        lastMinute = DateTime.Now.Ticks / TimeSpan.TicksPerMinute;   // 起動した分は実行しない
        nextUpdateCheck = DateTime.Now.AddMinutes(1);   // ログオン直後はネットにつながっていないことがあるので少し待つ
        Updater.CleanupOld();
        LoadSchedule(false);
        RegisterHotkeys();
        timer.Tick += (s, e) => Tick();
        timer.Interval = 40;
        timer.Start();
        Shown += (s, e) => { Update2(true); BeginInvoke((Action)(() => EmptyWorkingSet((IntPtr)(-1)))); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8; // LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST
            return cp;
        }
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    // ================= スプライト読み込み =================

    void LoadSprites()
    {
        f = sizeMul * dpi / 96f * StandH / 128f;
        if (CellW == 0)
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("walk.png"))
            using (var img = Image.FromStream(s, false, false)) { CellW = img.Width / WalkFrames; CellH = img.Height; }
        cellW = (int)Math.Round(CellW * f);
        cellH = (int)Math.Round(CellH * f);
        walk = LoadAnim("walk", WalkFrames, 120);
        idle = LoadAnim("idle", 3, 550);
        sit = LoadAnim("sit", 8, 230);
        // 隠れ中のしっぽ振り: しっぽを上げる 6・7コマ目は使わず、1〜5コマ目を行って戻る（コマは sit と共有）
        hideSit = new Anim { F = new[] { sit.F[0], sit.F[1], sit.F[2], sit.F[3], sit.F[4], sit.F[3], sit.F[2], sit.F[1] }, Ms = sit.Ms, Top = sit.Top, HeadX = sit.HeadX };
        blink = LoadAnim("blink", 4, 70);
        sleep = LoadAnim("sleep", 8, 450);
        sitDown = LoadAnim("sit_down", 4, 130);
        standUp = Reverse(sitDown);
        lieDown = LoadAnim("lie_down", 4, 200);
        wake = LoadAnim("wake", 8, 0);
        wake.Ms = new[] { 400, 600, 220, 220, 380, 800, 320, 260 };
        surprised = LoadAnim("surprised", 5, 0);
        surprised.Ms = new[] { 60, 110, 200, 120, 450 };
        // つかまれている間は首をつままれてぶら下がるアニメ、離したらびっくりジャンプの着地2コマ
        held = LoadAnim("drag", 4, 220);
        land = new Anim { F = new[] { surprised.F[3], surprised.F[4] }, Ms = new[] { 120, 350 }, Top = surprised.Top, HeadX = surprised.HeadX };
        turn = LoadAnim("turn", 3, 120);
        talk = LoadAnim("talk", 4, 150);
        turnSit = LoadAnim("turn_sit", 3, 140);
        turnSitBack = Reverse(turnSit);
        // 方向転換の真ん中（正面）は、座って正面を向いたコマを使う
        turn.F[1].Bmp.Dispose();
        turn.F[1] = turnSit.F[2];
        turn.Top = Math.Min(turn.Top, turnSit.F[2].Y);
        talkSit = LoadAnim("talk_sit", 4, 150);   // 座って正面を向いた口パク
        talkSit.HeadX = talkSit.F[0].X + talkSit.F[0].Bmp.Width / 2;

        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("effects.png"))
        using (var src = new Bitmap(s))
        {
            int n = 6, cw = src.Width / n, sz = (int)Math.Round(src.Height * f);
            icons = new Bitmap[n];
            for (int i = 0; i < n; i++)
            {
                icons[i] = new Bitmap(sz, sz, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(icons[i]))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, sz, sz), i * cw, 0, cw, src.Height, GraphicsUnit.Pixel);
                }
            }
        }
    }

    Anim LoadAnim(string name, int count, int ms)
    {
        var a = new Anim { F = new Frame[count], Ms = new int[count] };
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name + ".png"))
        using (var src = new Bitmap(s))
        {
            int sw = src.Width, sh = src.Height, cw = sw / count;
            var bd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[bd.Stride * sh];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            src.UnlockBits(bd);
            var attr = new ImageAttributes();
            attr.SetWrapMode(WrapMode.TileFlipXY);
            for (int i = 0; i < count; i++)
            {
                // 不透明部分の範囲を求めて、その部分だけ縮小して持つ（メモリ節約）
                int x0 = cw, y0 = sh, x1 = -1, y1 = -1;
                for (int yy = 0; yy < sh; yy++)
                    for (int xx = 0; xx < cw; xx++)
                        if (px[yy * bd.Stride + (i * cw + xx) * 4 + 3] > 0)
                        {
                            if (xx < x0) x0 = xx; if (xx > x1) x1 = xx;
                            if (yy < y0) y0 = yy; if (yy > y1) y1 = yy;
                        }
                int dx = (int)Math.Floor(x0 * f), dy = (int)Math.Floor(y0 * f);
                int dw = (int)Math.Ceiling((x1 + 1) * f) - dx, dh = (int)Math.Ceiling((y1 + 1) * f) - dy;
                var bmp = new Bitmap(dw, dh, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float ox = x0 * f - dx, oy = y0 * f - dy, w = (x1 + 1 - x0) * f, h = (y1 + 1 - y0) * f;
                    g.DrawImage(src, new[] { new PointF(ox, oy), new PointF(ox + w, oy), new PointF(ox, oy + h) },
                        new RectangleF(i * cw + x0, y0, x1 + 1 - x0, y1 + 1 - y0), GraphicsUnit.Pixel, attr);
                }
                a.F[i] = new Frame { Bmp = bmp, X = dx, Y = dy };
                a.Ms[i] = ms;
            }
        }
        a.Top = int.MaxValue;
        foreach (var fr in a.F) a.Top = Math.Min(a.Top, fr.Y);
        a.HeadX = a.F[0].X + a.F[0].Bmp.Width * 3 / 4;
        return a;
    }

    static Anim Reverse(Anim a)
    {
        var r = new Anim { F = (Frame[])a.F.Clone(), Ms = (int[])a.Ms.Clone(), Top = a.Top, HeadX = a.F[a.F.Length - 1].X + a.F[a.F.Length - 1].Bmp.Width * 3 / 4 };
        Array.Reverse(r.F);
        return r;
    }

    void SetSize(float m)
    {
        sizeMul = m;
        SaveSettings();
        var old = new HashSet<Bitmap>(icons);
        foreach (var a in new[] { walk, idle, sit, blink, sleep, sitDown, lieDown, wake, surprised, turn, talk, talkSit, turnSit, held })
            foreach (var fr in a.F) old.Add(fr.Bmp);
        LoadSprites();
        onDone = null;
        SetMode(mode == Mode.Once || mode == Mode.Talk || mode == Mode.Drag ? Mode.Stand : mode);
        foreach (var b in old) b.Dispose();
        if (mode == Mode.Hidden) x = HiddenX();
        else if (mode != Mode.Hide) x = Math.Max(wa.Left + cellW / 2, Math.Min(x, wa.Right - cellW / 2));
        Update2(true);
        GC.Collect();
        EmptyWorkingSet((IntPtr)(-1));
    }

    // ================= 状態遷移 =================

    string Pick(string[] a) { return a[rnd.Next(a.Length)]; }

    void Play(Anim a, bool isLoop)
    {
        cur = a; fi = 0; loop = isLoop; loops = 0;
        frameEnd = Environment.TickCount + a.Ms[0];
        dirty = true;
    }

    void Once(Anim a, Action done)
    {
        mode = Mode.Once;
        onDone = done;
        Play(a, false);
    }

    void SetMode(Mode m)
    {
        mode = m;
        int now = Environment.TickCount;
        switch (m)
        {
            case Mode.Walk: Play(walk, true); modeEnd = now + rnd.Next(4000, 12000); break;
            case Mode.Hide: Play(walk, true); fx = Fx.None; break;
            case Mode.Hidden: Play(hideSit, true); fx = Fx.None; break;
            case Mode.Stand: Play(idle, true); modeEnd = now + rnd.Next(2000, 6000); break;
            case Mode.Sit: Play(sit, true); modeEnd = now + rnd.Next(6000, 20000); break;
            case Mode.Sleep:   // 構ってもらえていないほど長く寝る
                int[][] spans = { new[] { 60, 120 }, new[] { 120, 300 }, new[] { 300, 900 } };   // 秒
                var sp = spans[Lonely()];
                Play(sleep, true); modeEnd = now + rnd.Next(sp[0], sp[1]) * 1000; fx = Fx.Zzz; break;
        }
        // 振り向き・びっくり等の途中でしゃべり始めた場合、動作が終わった時点でまだ吹き出しが出ていればこっちを向く
        if (talkText != null && (m == Mode.Walk || m == Mode.Stand || m == Mode.Sit)) StartTalkPose();
    }

    // 隠れる: 右の隅へ早足で歩いて画面の外へ。出てくる: 隅から左へ歩いて出てくる
    void SetHidden(bool h)
    {
        hidden = h;
        SaveSettings();
        onDone = null;
        if (h)
        {
            talkText = null;
            dir = 1;
            if (x >= HiddenX()) { x = HiddenX(); SetMode(Mode.Hidden); }
            else SetMode(Mode.Hide);
        }
        else
        {
            dir = -1;
            SetMode(Mode.Walk);
        }
        dirty = true;
    }

    void TurnThen(Mode next) { Once(turn, () => { dir = -dir; SetMode(next); }); }

    // 最後に構ってもらってからの時間: 0 = 5分未満, 1 = 20分未満, 2 = それ以上
    int lastPet = Environment.TickCount;
    int Lonely()
    {
        int min = (Environment.TickCount - lastPet) / 60000;
        return min < 5 ? 0 : min < 20 ? 1 : 2;
    }

    void NextMode()
    {
        // 寝に行く確率(%)。構ってもらえていないほど眠くなる  [歩き, 立ち, 座り][さびしさ]
        int[][] sleepy = { new[] { 10, 25, 45 }, new[] { 8, 20, 40 }, new[] { 15, 35, 60 } };
        int lonely = Lonely();
        int r = rnd.Next(100);
        switch (mode)
        {
            case Mode.Walk:
                if (r < sleepy[0][lonely]) Once(lieDown, () => SetMode(Mode.Sleep));
                else if ((r = rnd.Next(100)) < 37) SetMode(Mode.Stand);
                else if (r < 62) TurnThen(Mode.Walk);
                else Once(sitDown, () => SetMode(Mode.Sit));
                break;
            case Mode.Stand:
                if (r < sleepy[1][lonely]) Once(lieDown, () => SetMode(Mode.Sleep));
                else if ((r = rnd.Next(100)) < 45) SetMode(Mode.Walk);
                else if (r < 62) TurnThen(Mode.Walk);
                else if (r < 88) Once(sitDown, () => SetMode(Mode.Sit));
                else TurnThen(Mode.Stand);
                break;
            case Mode.Sit:
                if (r < sleepy[2][lonely]) Once(standUp, () => Once(lieDown, () => SetMode(Mode.Sleep)));
                else Once(standUp, () => SetMode(rnd.Next(100) < 60 ? Mode.Walk : Mode.Stand));
                break;
            case Mode.Sleep:
                WakeUp(false);
                break;
        }
    }

    void WakeUp(bool talk)
    {
        fx = Fx.None;
        Once(wake, () => SetMode(Mode.Stand));
        if (talk) Say(Pick(WakeLines));
    }

    // ---- ドラッグで左右に動かす（クリックだけなら Poke） ----
    Anim held, land;
    bool pressed, dragged;
    int downX, lastDragX;
    double grabOffset;

    void OnDown(MouseEventArgs e)
    {
        lastPet = Environment.TickCount;   // クリック・ドラッグ・右クリックは「構ってもらった」
        if (e.Button != MouseButtons.Left) return;
        pressed = true;
        dragged = false;
        downX = lastDragX = Cursor.Position.X;
    }

    void OnDrag(MouseEventArgs e)
    {
        if (!pressed) return;
        int cx = Cursor.Position.X;
        if (!dragged)
        {
            if (Math.Abs(cx - downX) < (int)(5 * dpi / 96)) return;   // 少し動かしたらドラッグ開始
            dragged = true;
            if (hidden) { hidden = false; SaveSettings(); }          // 隠れていたら引っぱり出す
            onDone = null;
            fx = Fx.None;
            mode = Mode.Drag;
            Play(held, true);
            grabOffset = downX - x;
        }
        double nx = Math.Max(wa.Left + cellW * 0.3, Math.Min(wa.Right - cellW * 0.3, cx - grabOffset));
        // 動かしている向きを向く（細かい揺れで反転しないよう少し動いてから）
        if (Math.Abs(cx - lastDragX) >= (int)(3 * dpi / 96))
        {
            int d = cx > lastDragX ? 1 : -1;
            if (d != dir) { dir = d; dirty = true; }
            lastDragX = cx;
        }
        x = nx;
        Update2(false);
    }

    void OnUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !pressed) return;
        pressed = false;
        if (dragged) Once(land, () => SetMode(Mode.Stand));   // 着地して立つ
        else Poke();
    }

    void Poke()
    {
        switch (mode)
        {
            case Mode.Sleep: WakeUp(true); break;
            case Mode.Sit: ShowFx(Fx.Heart, 2500); Say(Pick(PetLines)); break;
            case Mode.Walk:
            case Mode.Stand:
                ShowFx(Fx.Bang, 1200);
                Once(surprised, () => SetMode(Mode.Stand));
                Say(Pick(ClickLines));
                break;
            default: Say(Pick(ClickLines)); break;
        }
    }

    void ShowFx(Fx e, int ms) { fx = e; fxEnd = Environment.TickCount + ms; dirty = true; }

    // 設定ファイル: key=value 形式（旧形式の「数値1行」も大きさとして読む）
    void LoadSettings()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                string l = raw.Trim();
                int eq = l.IndexOf('=');
                string k = eq < 0 ? "size" : l.Substring(0, eq).Trim(), v = eq < 0 ? l : l.Substring(eq + 1).Trim();
                if (k == "size") sizeMul = float.Parse(v, inv);
                else if (k == "talk") talkEnabled = v != "0";
                else if (k == "fx") fxEnabled = v != "0";
                else if (k == "hide") hidden = v == "1";
                else if (k == "data") dataSetting = v;
                else if (k == "update") updateCheck = v != "0";
            }
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            File.WriteAllText(iniPath, "size=" + sizeMul.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "\r\ntalk=" + (talkEnabled ? "1" : "0") + "\r\nfx=" + (fxEnabled ? "1" : "0") + "\r\nhide=" + (hidden ? "1" : "0") + "\r\ndata=" + dataSetting +
                "\r\nupdate=" + (updateCheck ? "1" : "0") + "\r\n");
        }
        catch { }
    }

    // 保存先: 相対パスはホームフォルダ基準、%USERPROFILE% などの環境変数も使える。作れない時はホームフォルダ
    static string ResolveDataDir(string setting, out string error)
    {
        error = null;
        if (setting.Length == 0) return HomeDir;
        try
        {
            string d = Path.GetFullPath(Path.Combine(HomeDir, Environment.ExpandEnvironmentVariables(setting)));
            Directory.CreateDirectory(d);
            return d;
        }
        catch (Exception ex) { error = ex.Message; return HomeDir; }
    }

    void PickDataDir()
    {
        using (var d = new FolderBrowserDialog { Description = "ログ・schedule.txt・oneshot.txt・messages.txt を置くフォルダ", SelectedPath = dataDir.TrimEnd('\\') })
            if (d.ShowDialog(this) == DialogResult.OK) ChangeDataDir(d.SelectedPath);
    }

    // 保存先を変えたら、今のファイルをコピーするか聞いてから再起動する（ログは移さない）
    void ChangeDataDir(string setting)
    {
        string err, dir = ResolveDataDir(setting, out err);
        if (err != null) { MessageBox.Show(this, "このフォルダは使えません。\n" + err, "TaskbarCat", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (string.Equals(dir.TrimEnd('\\'), dataDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            dataSetting = setting;
            SaveSettings();
            return;
        }
        var names = new List<string>();
        foreach (var n in DataFiles)
            if (File.Exists(Path.Combine(dataDir, n)) && !File.Exists(Path.Combine(dir, n))) names.Add(n);
        if (names.Count > 0)
        {
            var r = MessageBox.Show(this, "今の " + string.Join("、", names.ToArray()) + " を新しい保存先へコピーしますか？\n\n" +
                "いいえ: コピーせずに切り替えます（無いファイルは空から始まります）", "TaskbarCat",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes)
                try { foreach (var n in names) File.Copy(Path.Combine(dataDir, n), Path.Combine(dir, n)); }
                catch (Exception ex) { MessageBox.Show(this, "コピーできませんでした。\n" + ex.Message, "TaskbarCat", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        }
        Log("[設定] 保存先を変更 " + dataDir + " → " + dir);
        dataSetting = setting;
        SaveSettings();
        restart = true;
        Close();
    }

    // force: 「しゃべる」OFF でも表示する（スクリプトの出力用）
    void Say(string text, bool force = false)
    {
        if (!talkEnabled && !force) return;
        talkText = text;
        using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b))
            talkSize = g.MeasureString(text, font, (int)(BubbleMaxW * dpi / 96), StringFormat.GenericTypographic);
        talkEnd = Environment.TickCount + Math.Min(15000, 2500 + 120 * text.Length);
        dirty = true;
        StartTalkPose();
    }

    // しゃべっている間はこっちを向く。立ち/歩き → 口パク、座り → 振り向いて正面。寝ている時や一連の動作中はそのまま
    void StartTalkPose()
    {
        if (mode == Mode.Walk || mode == Mode.Stand)
        {
            talkReturn = mode;
            mode = Mode.Talk;
            Play(talk, true);
        }
        else if (mode == Mode.Sit)
        {
            talkReturn = Mode.Sit;
            Once(turnSit, () => { mode = Mode.Talk; Play(talkSit, true); });
        }
        else if (mode == Mode.Talk) Play(cur, true);   // しゃべっている途中で次のセリフ → 口パクをやり直す
    }

    void EndTalkPose()
    {
        if (talkReturn == Mode.Sit) Once(turnSitBack, () => SetMode(Mode.Sit));
        else SetMode(talkReturn);
    }

    const int BubbleMaxW = 320;   // 吹き出しの最大幅 (100% DPI)。超える分は折り返す

    // スクリプトの出力を吹き出し向けに整える: 空行を除き、長ければ最後の5行・1行60文字まで
    static string ClipOutput(string s)
    {
        var lines = new List<string>();
        foreach (var raw in s.Replace("\r", "").Split('\n'))
        {
            string l = raw.TrimEnd();
            if (l.Trim().Length == 0) continue;
            lines.Add(l.Length > 60 ? l.Substring(0, 59) + "…" : l);
        }
        const int max = 5;
        if (lines.Count > max) { lines.RemoveRange(0, lines.Count - max); lines.Insert(0, "…"); }
        return string.Join("\n", lines.ToArray());
    }

    // ================= スケジュール実行 =================

    void LoadSchedule(bool announce)
    {
        var errors = new List<string>();
        try
        {
            var old = sched;
            sched = Schedule.Load(schedPath, errors);
            schedStamp = File.GetLastWriteTime(schedPath);
            // きまぐれ実行の「次の時刻」は、同じ行が残っていれば読み直しても引き継ぐ
            foreach (var e in sched)
            {
                if (!e.IsRandom) continue;
                var prev = old.Find(o => o.IsRandom && o.Cron == e.Cron && o.Path == e.Path);
                if (prev != null) e.NextRun = prev.NextRun;
                else Log("[きまぐれ] " + Path.GetFileName(e.Path) + " の次の実行は " + e.NextRun.ToString("HH:mm") + " ごろ");
            }
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        foreach (var e in errors) Log("[schedule.txt] " + e);
        // 吹き出しは猫語だけ。詳しい内容はログに残す
        if (errors.Count > 0) { Say("にゃ…？"); ShowFx(Fx.Question, 3000); }
        else if (announce) Say("にゃ！");
    }

    // 分が変わった時だけ判定（スリープ等で過ぎた分は cron と同じく実行しない）
    void CheckSchedule()
    {
        var t = DateTime.Now;
        long minute = t.Ticks / TimeSpan.TicksPerMinute;
        if (minute == lastMinute) return;
        lastMinute = minute;
        if (t >= nextUpdateCheck)   // スリープ等で 12:00 を過ぎていたら、起きた時に確認する
        {
            var noon = t.Date.AddHours(12);
            nextUpdateCheck = noon > t ? noon : noon.AddDays(1);
            if (updateCheck) CheckUpdate(false);
        }
        try { if (File.GetLastWriteTime(schedPath) != schedStamp) LoadSchedule(true); } catch { }
        try { if (File.GetLastWriteTime(oneShotPath) != oneShotStamp) RegisterHotkeys(); } catch { }   // メモ帳で直接編集された時
        foreach (var e in sched)
            if (e.Matches(t))
            {
                RunEntry(e);
                if (e.IsRandom) Log("[きまぐれ] " + Path.GetFileName(e.Path) + " の次の実行は " + e.NextRun.ToString("HH:mm") + " ごろ");
            }
    }

    void RunEntry(CronEntry e) { RunFile(e.Path, e.Message); }

    // message: 実行開始時のセリフ（null なら「にゃっ！」）
    // .bat/.cmd/.ps1/.py は必ずバックグラウンド（コンソールのウィンドウを出さない）で実行し、出力は吹き出し・ログへ
    void RunFile(string path, string message)
    {
        if (!File.Exists(path))
        {
            Log("[見つからない] " + path);
            Say("にゃ…？");
            ShowFx(Fx.Question, 3000);
            return;
        }
        Say(message ?? "にゃっ！");

        // .NET Framework は UseShellExecute の既定値が true なので明示的に false にする
        var psi = new System.Diagnostics.ProcessStartInfo { WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false };
        Encoding enc = Encoding.Default;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".ps1": psi.FileName = "powershell.exe"; psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\""; break;
            case ".bat":
            case ".cmd": psi.FileName = "cmd.exe"; psi.Arguments = "/c \"\"" + path + "\"\""; break;
            case ".py":
                psi.FileName = "python"; psi.Arguments = "\"" + path + "\"";
                enc = new UTF8Encoding(false);
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                break;
            default: psi.FileName = path; psi.UseShellExecute = true; break;
        }

        try
        {
            if (psi.UseShellExecute)
            {
                System.Diagnostics.Process.Start(psi);
                Log("[起動] " + path);
                return;
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = psi.StandardErrorEncoding = enc;
            // 標準入力もつないで即閉じる。pause や set /p が見えないまま入力待ちで止まり続けないように
            psi.RedirectStandardInput = true;
            var p = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            var output = new StringBuilder();   // ログ用（標準出力 + 標準エラー）
            var stdout = new StringBuilder();   // 吹き出し用（標準出力のみ）
            p.OutputDataReceived += (s, a) =>
            {
                if (a.Data == null) return;
                lock (output) { output.AppendLine("    " + a.Data); stdout.AppendLine(a.Data); }
            };
            p.ErrorDataReceived += (s, a) => { if (a.Data != null) lock (output) output.AppendLine("    " + a.Data); };
            var started = DateTime.Now;
            p.Exited += (s, a) =>
            {
                p.WaitForExit();   // 出力の読み取りを待つ
                int code = p.ExitCode;
                p.Dispose();
                string result;
                lock (output)
                {
                    Log("[終了 exit " + code + ", " + (int)(DateTime.Now - started).TotalSeconds + "秒] " + path + "\r\n" + output);
                    result = ClipOutput(stdout.ToString());
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        // 出力があればそれを表示（「しゃべる」OFF でも出す）。なければ猫語で結果だけ
                        if (result.Length > 0) Say(result, true);
                        else Say(code == 0 ? "にゃ〜ん♪" : "にゃぅ…");
                        if (code == 0) ShowFx(Fx.Note, 2000); else ShowFx(Fx.Sweat, 3000);
                    }));
                }
                catch { }   // 終了処理中
            };
            p.Start();
            try { p.StandardInput.Close(); } catch { }   // すでに終了していた等
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Log("[開始] " + path);
        }
        catch (Exception ex)
        {
            Log("[起動失敗] " + path + " : " + ex.Message);
            Say("にゃぅ…");
            ShowFx(Fx.Sweat, 3000);
        }
    }

    void Log(string msg)
    {
        try
        {
            lock (logLock)
            {
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                    DateTime.Now.ToString("HH:mm:ss ") + msg.TrimEnd() + "\r\n", new UTF8Encoding(false));
            }
        }
        catch { }
    }

    // 新しい版があるか裏で確認する。manual: メニューから確認した時（結果を必ずしゃべる）
    void CheckUpdate(bool manual)
    {
        if (checkingUpdate || updating) return;
        checkingUpdate = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Updater.Release r = null;
            string err = null;
            try { r = Updater.FindNewer(); } catch (Exception ex) { err = ex.Message; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    checkingUpdate = false;
                    if (err != null)
                    {
                        Log("[アップデート] 確認できませんでした : " + err);
                        if (manual) { Say("にゃぅ…（確認できなかった）", true); ShowFx(Fx.Sweat, 3000); }
                        return;
                    }
                    if (r == null) { if (manual) Say("最新だにゃ (v" + Updater.Version + ")", true); return; }
                    if (update == null || update.Ver != r.Ver) Log("[アップデート] v" + r.Ver + " があります（今は v" + Updater.Version + "）");
                    update = r;
                    // 大事なお知らせなので「しゃべる」OFF でも出す（多くても 1 日 2 回）
                    Say("v" + r.Ver + " が出たにゃ！\n右クリック →「アップデート」", true);
                    ShowFx(Fx.Bang, 3000);
                }));
            }
            catch { }   // 終了処理中
        });
    }

    // メニューの「アップデート」: ダウンロード → 署名を確認 → exe を差し替え → 再起動
    void StartUpdate()
    {
        var r = update;
        if (r == null || updating) return;
        updating = true;
        Say("アップデート中にゃ…", true);
        Log("[アップデート] v" + r.Ver + " をダウンロード : " + r.ExeUrl);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string err = null, note = null;
            try { note = Updater.Apply(r); } catch (Exception ex) { err = ex.Message; }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    updating = false;
                    if (err != null)
                    {
                        Log("[アップデート] 失敗 : " + err);
                        Say("にゃぅ…", true);
                        ShowFx(Fx.Sweat, 3000);
                        if (MessageBox.Show(this, "アップデートできませんでした。\n\n" + err + "\n\nリリースのページを開きますか？", "TaskbarCat",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            try { System.Diagnostics.Process.Start(Updater.ReleasesPage); } catch { }
                        return;
                    }
                    Log("[アップデート] v" + Updater.Version + " → v" + r.Ver + " に差し替えました。再起動します" + (note != null ? "\r\n    " + note : ""));
                    restart = true;
                    Close();
                }));
            }
            catch { }
        });
    }

    ScheduleForm schedForm;

    void OpenScheduleForm()
    {
        LoadSchedule(false);   // サンプル入りの schedule.txt が無ければ作る
        if (schedForm == null || schedForm.IsDisposed)
        {
            schedForm = new ScheduleForm(schedPath, () => LoadSchedule(true), RunEntry, OpenLogDir);
            schedForm.FormClosed += (s, e) => { schedForm = null; GC.Collect(); EmptyWorkingSet((IntPtr)(-1)); };
        }
        schedForm.Show();
        schedForm.Activate();
    }

    // 「タスク」: 単発バッチに登録したもの + 設定画面・ログ
    void FillTasksMenu(MenuItem parent)
    {
        parent.MenuItems.Clear();
        List<OneShotItem> items;
        try { items = OneShot.Load(oneShotPath); } catch { items = new List<OneShotItem>(); }
        string baseDir = HomeDir;   // 相対パスは保存先ではなくホームフォルダ基準
        foreach (var it in items)
        {
            string full = OneShot.Resolve(baseDir, it.File);
            string text = it.DisplayName.Replace("&", "&&") + (it.Key.Length > 0 ? "\t" + it.Key : "");   // \t の後ろは右寄せで出る
            parent.MenuItems.Add(text, (s, e) => RunFile(full, null));
        }
        if (items.Count == 0) parent.MenuItems.Add("（未登録）").Enabled = false;
        parent.MenuItems.Add("-");
        parent.MenuItems.Add("単発バッチの設定…", (s, e) => OpenOneShotForm());
        parent.MenuItems.Add("スケジュール設定…", (s, e) => OpenScheduleForm());
        parent.MenuItems.Add("ログを開く", (s, e) => OpenLogDir());
    }

    // ================= 単発バッチのショートカットキー =================

    const int WM_HOTKEY = 0x312;
    readonly List<string> hotkeyFiles = new List<string>();   // ホットキー id - 1 → 実行するフルパス
    DateTime oneShotStamp;

    // oneshot.txt のキーを登録し直す（起動時・設定画面で保存した時・ファイルが変わった時）
    void RegisterHotkeys()
    {
        if (oneShotPath == null || !IsHandleCreated) return;
        for (int i = 0; i < hotkeyFiles.Count; i++) Hotkey.UnregisterHotKey(Handle, i + 1);
        hotkeyFiles.Clear();

        List<OneShotItem> items;
        try
        {
            oneShotStamp = File.GetLastWriteTime(oneShotPath);
            items = OneShot.Load(oneShotPath);
        }
        catch (Exception ex) { Log("[oneshot.txt] " + ex.Message); return; }

        bool failed = false;
        foreach (var it in items)
        {
            int mods; Keys key;
            if (!Hotkey.TryParse(it.Key, out mods, out key)) continue;
            if (Hotkey.RegisterHotKey(Handle, hotkeyFiles.Count + 1, mods | Hotkey.MOD_NOREPEAT, (int)key))
                hotkeyFiles.Add(OneShot.Resolve(HomeDir, it.File));   // 相対パスはホームフォルダ基準
            else
            {
                Log("[ショートカット] " + it.Key + "（" + it.DisplayName + "）はほかのアプリか Windows が使っているため使えません");
                failed = true;
            }
        }
        if (failed) { Say("にゃ…？"); ShowFx(Fx.Question, 3000); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys();   // ハンドルが作り直された時用（初回はコンストラクタで呼ぶ）
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id >= 1 && id <= hotkeyFiles.Count) RunFile(hotkeyFiles[id - 1], null);
            return;
        }
        base.WndProc(ref m);
    }

    OneShotForm oneShotForm;

    void OpenOneShotForm()
    {
        if (oneShotForm == null || oneShotForm.IsDisposed)
        {
            oneShotForm = new OneShotForm(oneShotPath, p => RunFile(p, null), RegisterHotkeys);
            oneShotForm.FormClosed += (s, e) => { oneShotForm = null; GC.Collect(); EmptyWorkingSet((IntPtr)(-1)); };
        }
        oneShotForm.Show();
        oneShotForm.Activate();
    }

    PortForm portForm;

    void OpenPortForm()
    {
        if (portForm == null || portForm.IsDisposed)
        {
            portForm = new PortForm(Log, msg => Say(msg));
            portForm.FormClosed += (s, e) => { portForm = null; GC.Collect(); EmptyWorkingSet((IntPtr)(-1)); };
        }
        portForm.Show();
        portForm.Activate();
    }

    void OpenLogDir()
    {
        Directory.CreateDirectory(logDir);
        OpenFile("explorer.exe", logDir);
    }

    // Windows の標準ツールを開く（UAC でキャンセルされた時などは何もしない）
    static void Launch(string exe)
    {
        try { System.Diagnostics.Process.Start(exe); } catch { }
    }

    static void OpenFile(string exe, string arg)
    {
        try { System.Diagnostics.Process.Start(exe, "\"" + arg + "\""); } catch { }
    }

    // ================= メインループ =================

    void Tick()
    {
        int now = Environment.TickCount;
        int dt = Math.Min(200, now - lastTick);
        lastTick = now;
        if (now - nextCheck >= 0) { nextCheck = now + 2000; Check(); }
        CheckSchedule();   // 全画面アプリで隠れている間も実行する
        if (!Visible) return;

        // アニメのコマ送り
        if (now - frameEnd > 1000) frameEnd = now;   // 非表示から戻った時など
        while (now - frameEnd >= 0)
        {
            dirty = true;
            if (fi + 1 < cur.F.Length) { fi++; frameEnd += cur.Ms[fi]; }
            else if (loop)
            {
                // 座っている間はときどきまばたき
                if (mode == Mode.Sit && cur == sit && rnd.Next(3) == 0) { Play(blink, false); onDone = () => Play(sit, true); }
                // 口パクは口を TalkMouths 回開けたら、口を閉じた正面のコマで止める
                else if ((cur == talk || cur == talkSit) && ++loops >= TalkMouths) { fi = 0; frameEnd = now + 60000; }
                else { fi = 0; frameEnd += cur.Ms[0]; }
            }
            else
            {
                var d = onDone; onDone = null;
                if (d != null) d(); else SetMode(Mode.Stand);
            }
        }

        if (mode == Mode.Talk) { if (talkText == null) EndTalkPose(); }
        else if (mode != Mode.Once && mode != Mode.Hide && mode != Mode.Hidden && mode != Mode.Drag && now - modeEnd >= 0) NextMode();

        if (mode == Mode.Walk)
        {
            x += dir * 24.0 * f * 2 * dt / 1000.0;   // 1x で約 24px/秒（足が滑って見えない程度）
            double min = wa.Left + cellW * 0.3, max = wa.Right - cellW * 0.3;
            // 進行方向の端でだけ止める（隠れ場所から出てくる時は画面外から歩いてくる）
            if (x <= min && dir < 0) { x = min; TurnThen(Mode.Walk); }
            else if (x >= max && dir > 0) { x = max; TurnThen(Mode.Walk); }
        }
        else if (mode == Mode.Hide)
        {
            x += 60.0 * f * 2 * dt / 1000.0;   // 早足で隅へ
            if (x >= HiddenX()) { x = HiddenX(); SetMode(Mode.Hidden); }
        }

        if (fx != Fx.None && !(fx == Fx.Zzz && mode == Mode.Sleep) && now - fxEnd >= 0) { fx = Fx.None; dirty = true; }
        if (fx == Fx.Zzz && (now / 700) % 2 != (lastTick - dt) / 700 % 2) dirty = true;

        if (talkText != null && now - talkEnd >= 0) { talkText = null; dirty = true; }
        else if (talkText == null && now - nextTalk >= 0 && mode != Mode.Once && mode != Mode.Hide && mode != Mode.Hidden && mode != Mode.Drag)
        {
            nextTalk = now + rnd.Next(25000, 70000);
            Say(mode == Mode.Sleep ? Pick(SleepLines) : Pick(lines));
            if (mode != Mode.Sleep && rnd.Next(3) == 0) ShowFx(Fx.Note, 2000);
        }

        Update2(false);

        // 歩いていない時はタイマーを間引く
        timer.Interval = mode == Mode.Walk || mode == Mode.Hide || mode == Mode.Drag ? 40 : Math.Max(30, Math.Min(250, frameEnd - now));
    }

    // 作業領域の更新と、全画面アプリ表示中は隠れる処理
    void Check()
    {
        var w = Screen.PrimaryScreen.WorkingArea;
        if (w != wa)
        {
            wa = w;
            if (mode == Mode.Hidden) x = HiddenX();
            else if (mode != Mode.Hide) x = Math.Max(wa.Left + cellW / 2, Math.Min(x, wa.Right - cellW / 2));
            dirty = true;
        }
        bool full = false;
        IntPtr fg = GetForegroundWindow();
        RECT r;
        if (fg != IntPtr.Zero && fg != Handle && GetWindowRect(fg, out r))
        {
            var sb = Screen.PrimaryScreen.Bounds;
            if (r.L <= sb.Left && r.T <= sb.Top && r.R >= sb.Right && r.B >= sb.Bottom)
            {
                var cn = new StringBuilder(64);
                GetClassName(fg, cn, 64);
                string c = cn.ToString();
                full = c != "Progman" && c != "WorkerW";
            }
        }
        if (full && Visible) Hide();
        else if (!full && !Visible) { Show(); dirty = true; }
    }

    // ================= 配置と描画 =================

    int Mirror(int cx) { return dir > 0 ? cx : cellW - cx; }

    // 吹き出し・アイコン込みでウィンドウの範囲を求め、変化があれば再描画、なければ移動だけ
    void Update2(bool force)
    {
        int cellX = (int)Math.Round(x) - cellW / 2;
        int cellY = wa.Bottom + 1 - cellH;
        var u = new Rectangle(0, 0, cellW, cellH);
        int bx = 0;
        Rectangle bubble = Rectangle.Empty, icon = Rectangle.Empty;
        int head = Mirror(cur.HeadX), pad = (int)(6 * dpi / 96), tail = (int)(7 * dpi / 96);
        if (ShownFx != Fx.None)
        {
            int isz = icons[0].Width;
            int ix = head + dir * isz / 2 - isz / 2;
            int bob = fx == Fx.Zzz && (Environment.TickCount / 700) % 2 == 0 ? isz / 8 : 0;
            icon = new Rectangle(ix, cur.Top - isz * 3 / 4 - bob, isz, isz);
            u = Rectangle.Union(u, icon);
        }
        if (talkText != null)
        {
            int bw = (int)Math.Ceiling(talkSize.Width) + pad * 3, bh = (int)Math.Ceiling(talkSize.Height) + pad * 2;
            int bottom = (ShownFx != Fx.None ? icon.Top + icon.Height / 3 : cur.Top) - tail - 2;
            bx = head - bw / 2;
            int sx = Math.Max(wa.Left, Math.Min(cellX + bx, wa.Right - bw));
            bx = sx - cellX;
            bubble = new Rectangle(bx, bottom - bh, bw, bh + tail);
            u = Rectangle.Union(u, bubble);
        }
        // 画面の外にはみ出す部分は切り落とす（隠れ中の体が隣のモニターに映らないように）
        int over = cellX + u.Right - wa.Right;
        if (over > 0) u.Width -= over;
        int under = wa.Left - (cellX + u.X);
        if (under > 0) { u.X += under; u.Width -= under; }
        if (u.Width < 1) u.Width = 1;
        var screen = new Rectangle(cellX + u.X, cellY + u.Y, u.Width, u.Height);
        if (force || dirty || screen.Size != lastBounds.Size || bx != lastBx)
        {
            Render(u, bubble, icon, head, tail);
            dirty = false;
        }
        else if (screen.Location != lastBounds.Location)
            SetWindowPos(Handle, IntPtr.Zero, screen.X, screen.Y, 0, 0, 0x1 | 0x4 | 0x10); // NOSIZE|NOZORDER|NOACTIVATE
        lastBounds = screen;
        lastBx = bx;
    }

    void Render(Rectangle u, Rectangle bubble, Rectangle icon, int head, int tail)
    {
        if (!IsHandleCreated) return;
        EnsureDib(u.Width, u.Height);
        var g = cg;
        g.ResetTransform();
        g.Clear(Color.Transparent);
        g.TranslateTransform(-u.X, -u.Y);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // 隠れ中: しっぽの周りにほぼ透明 (alpha=1) の当たり判定を置き、右クリックしやすくする
        // （完全に透明な所はクリックが下のウィンドウへ抜けてしまうため）
        if (mode == Mode.Hidden)
            using (var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            {
                int cut = (int)(HideCut * f);
                int pw = Math.Max((int)(28 * dpi / 96), cut - (int)(18 * f)), ph = (int)(130 * f);
                g.FillRectangle(hit, cut - pw, cellH - ph, pw, ph);
            }

        var fr = cur.F[fi];
        int w = fr.Bmp.Width, h = fr.Bmp.Height;
        if (dir > 0) g.DrawImage(fr.Bmp, new Rectangle(fr.X, fr.Y, w, h));
        else g.DrawImage(fr.Bmp, new Rectangle(cellW - fr.X, fr.Y, -w, h));

        if (ShownFx != Fx.None) g.DrawImage(icons[(int)ShownFx], icon);

        if (talkText != null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var body = new Rectangle(bubble.X, bubble.Y, bubble.Width - 1, bubble.Height - tail - 1);
            int tx = Math.Max(body.Left + tail * 2, Math.Min(head, body.Right - tail * 2));
            using (var path = new GraphicsPath())
            using (var pen = new Pen(Color.FromArgb(60, 56, 64), Math.Max(1f, dpi / 96f)))
            {
                int r = Math.Min(body.Height, (int)(16 * dpi / 96));
                path.AddArc(body.Left, body.Top, r, r, 180, 90);
                path.AddArc(body.Right - r, body.Top, r, r, 270, 90);
                path.AddArc(body.Right - r, body.Bottom - r, r, r, 0, 90);
                path.AddLine(tx + tail, body.Bottom, tx, body.Bottom + tail);
                path.AddLine(tx, body.Bottom + tail, tx - tail / 2, body.Bottom);
                path.AddArc(body.Left, body.Bottom - r, r, r, 90, 90);
                path.CloseFigure();
                g.FillPath(Brushes.White, path);
                g.DrawPath(pen, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // 複数行（スクリプトの出力など）は左寄せ、1行は中央
            bool multi = talkText.Contains("\n") || talkSize.Height > font.GetHeight() * 1.5f;
            var textRect = new RectangleF(body.X + (body.Width - talkSize.Width) / 2f, body.Y, talkSize.Width + 1, body.Height);
            using (var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = multi ? StringAlignment.Near : StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var tb = new SolidBrush(Color.FromArgb(50, 46, 54)))
                g.DrawString(talkText, font, tb, textRect, sf);
        }
        g.Flush();

        IntPtr screenDC = GetDC(IntPtr.Zero);
        var pt = new POINT((int)Math.Round(x) - cellW / 2 + u.X, wa.Bottom + 1 - cellH + u.Y);
        var sz = new SIZE(u.Width, u.Height);
        var src = new POINT(0, 0);
        var bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(Handle, screenDC, ref pt, ref sz, memDC, ref src, 0, ref bl, 2);
        ReleaseDC(IntPtr.Zero, screenDC);
    }

    void EnsureDib(int w, int h)
    {
        if (dib != IntPtr.Zero && w == dibW && h == dibH) return;
        FreeDib();
        IntPtr screenDC = GetDC(IntPtr.Zero);
        memDC = CreateCompatibleDC(screenDC);
        ReleaseDC(IntPtr.Zero, screenDC);
        var bi = new BITMAPINFOHEADER { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
        dib = CreateDIBSection(memDC, ref bi, 0, out bits, IntPtr.Zero, 0);
        SelectObject(memDC, dib);
        canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        cg = Graphics.FromImage(canvas);
        dibW = w; dibH = h;
    }

    void FreeDib()
    {
        if (cg != null) { cg.Dispose(); cg = null; }
        if (canvas != null) { canvas.Dispose(); canvas = null; }
        if (memDC != IntPtr.Zero) { DeleteDC(memDC); memDC = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { FreeDib(); base.OnFormClosed(e); }

    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, "TaskbarCat_SingleInstance", out first))
        {
            if (!first)
            {
                // すでに起動中なら「出てきて」と合図して終わる
                try { using (var ev = EventWaitHandle.OpenExisting("TaskbarCat_Show")) ev.Set(); } catch { }
                return;
            }
            SetProcessDPIAware();
            Application.Run(new TaskbarCat());
        }
        // 二重起動防止の Mutex を手放してから起動し直す
        if (restart) System.Diagnostics.Process.Start(Application.ExecutablePath);
    }
}
