using System.Drawing;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace EdgeWalker;

/// <summary>設定画面．「適用」で保存して，外周を作り直す</summary>
internal sealed class SettingsForm : Form
{
    private const string TOuter = "エッジ", TIdle = "メイズ", TSpeed = "速さ・難しさ", TLook = "見た目";

    // Tab が null の項目はメインの画面に，それ以外は「詳細設定」のそのタブに出る
    private sealed record Field(string? Tab, string Label, decimal Min, decimal Max, decimal Inc, int Decimals,
        Func<Settings, decimal> Get, Action<Settings, decimal> Set);

    private static readonly Field[] Fields =
    {
        new(TLook, "画面の短辺に並べるマス数", 12, 80, 1, 0, s => s.CellsShort, (s, v) => s.CellsShort = (int)v),
        new(TSpeed, "主人公の1ブロック時間（ms，固定）", 50, 600, 5, 0, s => s.PlayerMs, (s, v) => s.PlayerMs = (int)v),
        new(TSpeed, "ゴーストの初速（主人公=1）", 0.3m, 1.9m, 0.01m, 2, s => (decimal)s.GhostStart, (s, v) => s.GhostStart = (double)v),
        new(TSpeed, "ゴーストが1分ごとに速くなる量", 0, 1, 0.01m, 2, s => (decimal)s.GhostRampPerMin, (s, v) => s.GhostRampPerMin = (double)v),
        new(TSpeed, "ゴーストの最高速（主人公=1）", 0.3m, 1.99m, 0.01m, 2, s => (decimal)s.GhostMax, (s, v) => s.GhostMax = (double)v),
        new(TSpeed, "反撃の長さ（ブロック）", 5, 200, 1, 0, s => s.PowerSteps, (s, v) => s.PowerSteps = (int)v),
        new(TOuter, "クッキー出現の先読み（マス）", 2, 15, 1, 0, s => s.Ahead, (s, v) => s.Ahead = (int)v),
        new(TOuter, "通常クッキーの出現確率", 0, 1, 0.05m, 2, s => (decimal)s.PDot, (s, v) => s.PDot = (double)v),
        new(TOuter, "大きいクッキーの出現確率", 0, 0.2m, 0.002m, 3, s => (decimal)s.PPower, (s, v) => s.PPower = (double)v),
        new(TOuter, "再生成しない範囲（ブロック）", 0, 200, 1, 0, s => s.NoCookieHistory, (s, v) => s.NoCookieHistory = (int)v),
        new(TOuter, "ゴースト同士の最小間隔（マス）", 1, 8, 1, 0, s => s.SpawnGap, (s, v) => s.SpawnGap = (int)v),
        new(TLook, "文字の大きさ（%）", 30, 200, 5, 0, s => s.FontScalePct, (s, v) => s.FontScalePct = (int)v),
        new(TLook, "誕生1段階の長さ（ms，全11段階）", 30, 600, 10, 0, s => s.BirthStageMs, (s, v) => s.BirthStageMs = (int)v),
        new(TLook, "食べたゴーストのミニボム表示（ブロック）", 1, 40, 1, 0, s => s.BombSteps, (s, v) => s.BombSteps = (int)v),
        new(TOuter, "ゴースト初期距離（最小）", 4, 30, 1, 0, s => s.SpawnMin, (s, v) => s.SpawnMin = (int)v),
        new(TOuter, "ゴースト初期距離（最大）", 6, 60, 1, 0, s => s.SpawnMax, (s, v) => s.SpawnMax = (int)v),
        new(null, "クリック後に消える時間（分）", 0.1m, 60, 0.5m, 1, s => (decimal)s.ClickHideMin, (s, v) => s.ClickHideMin = (double)v),
        new(null, "ラウンドが終わってから次まで消える時間（分）", 0.1m, 120, 0.5m, 1, s => (decimal)s.EndHideMin, (s, v) => s.EndHideMin = (double)v),
        new(null, "メモリ使用率がこれ以上なら隠す（%，0で無効）", 0, 100, 5, 0, s => s.MemoryLimitPct, (s, v) => s.MemoryLimitPct = (int)v),
        new(null, "操作がなくなってからメイズが始まるまで（秒）", 5, 3600, 5, 0, s => s.IdleSeconds, (s, v) => s.IdleSeconds = (int)v),
        new(TIdle, "1試合のあと次を始めるまで（秒）", 1, 600, 1, 0, s => s.IdleRestartSec, (s, v) => s.IdleRestartSec = (int)v),
        new(TIdle, "見える範囲（キャラの周囲のマス数）", 3, 12, 1, 0, s => s.IdleVision, (s, v) => s.IdleVision = (int)v),
        new(TIdle, "クッキーを置く割合（%，通路のうち）", 5, 100, 5, 0, s => s.IdleCookiePct, (s, v) => s.IdleCookiePct = (int)v),
        new(TIdle, "うち大きいクッキーの数", 0, 40, 1, 0, s => s.IdlePower, (s, v) => s.IdlePower = (int)v),
        new(TIdle, "主人公のうっかり率（%）", 0, 50, 1, 0, s => s.IdleMistakePct, (s, v) => s.IdleMistakePct = (int)v),
        new(TIdle, "「近い」とみなす距離（マス）", 3, 30, 1, 0, s => s.IdleNear, (s, v) => s.IdleNear = (int)v),
        new(TIdle, "主人公が家の周りを先に食べる範囲（マス，0でオフ）", 0, 30, 1, 0, s => s.IdleHouseRadius, (s, v) => s.IdleHouseRadius = (int)v),
        new(TIdle, "家の周りを先に食べるのは，外のゴーストがこの数以下の間（4=ずっと）", 0, 4, 1, 0, s => s.IdleHouseMaxOut, (s, v) => s.IdleHouseMaxOut = (int)v),
        new(TIdle, "ゴーストを捕まえたあと家から離れる長さ（ブロック，0でオフ）", 0, 200, 1, 0, s => s.IdleAwayBlocks, (s, v) => s.IdleAwayBlocks = (int)v),
        new(TIdle, "家からこの距離以内のゴーストは追わない（マス，0でオフ）", 0, 30, 1, 0, s => s.IdleChaseHouseDist, (s, v) => s.IdleChaseHouseDist = (int)v),
        new(TIdle, "主人公が復活できる回数（0〜2）", 0, 2, 1, 0, s => s.IdleRevives, (s, v) => s.IdleRevives = (int)v),
        new(TIdle, "最初のゴーストが家を出るまで（ブロック）", 1, 100, 1, 0, s => s.IdleHouseFirst, (s, v) => s.IdleHouseFirst = (int)v),
        new(TIdle, "次のゴーストが出るまでの間隔（ブロック）", 1, 100, 1, 0, s => s.IdleHouseGap, (s, v) => s.IdleHouseGap = (int)v),
        new(TSpeed, "目玉の速さ（ゴーストの初速の何倍）", 1, 3, 0.1m, 1, s => (decimal)s.EyeMul, (s, v) => s.EyeMul = (double)v),
        new(TIdle, "復活したゴーストが家を出るまで（ブロック）", 1, 100, 1, 0, s => s.IdleReviveWait, (s, v) => s.IdleReviveWait = (int)v),
        new(TIdle, "操作後にエッジへ散る速さ（ms/マス）", 15, 300, 5, 0, s => s.IdleScatterMs, (s, v) => s.IdleScatterMs = (int)v),
        new(TSpeed, "ゴーストの数（最大4）", 1, 4, 1, 0, s => s.GhostCount, (s, v) => s.GhostCount = (int)v),
        new(TSpeed, "クッキーを食べた1ブロックだけ主人公が速くなる割合（0.05=5%）", 0, 1, 0.01m, 2, s => (decimal)s.DotBoost, (s, v) => s.DotBoost = (double)v),
        new(TSpeed, "反撃中に主人公が速くなる割合（0.1=10%）", 0, 1, 0.01m, 2, s => (decimal)s.PowerBoost, (s, v) => s.PowerBoost = (double)v),
        new(TLook, "反撃が終わる前に点滅する長さ（ブロック）", 0, 40, 1, 0, s => s.BlinkSteps, (s, v) => s.BlinkSteps = (int)v),
    };

    private static readonly string[] Tabs = { TSpeed, TOuter, TIdle, TLook };

    private Settings _initial;
    private readonly Rectangle _area;
    private readonly Action<Settings> _apply;
    private readonly NumericUpDown[] _inputs = new NumericUpDown[Fields.Length];
    private readonly int _cellsIdx = Array.FindIndex(Fields, f => f.Label.StartsWith("画面の短辺"));
    private readonly CheckBox _chkFullscreen = new() { Text = "全画面のアプリ中は隠す", AutoSize = true };
    private readonly CheckBox _chkEdge = new() { Text = "画面の縁を歩き回る（エッジモード）．オフなら，普段は何も出さない", AutoSize = true };
    private readonly CheckBox _chkIdle = new() { Text = "操作がないとき，画面いっぱいの迷路で遊ぶ（メイズモード）", AutoSize = true };
    private static readonly string[] MazeStyleNames = { "ランダム（毎回どちらか）", "島型（壁のかたまり）", "格子型（通路も壁も1マス幅）" };
    private readonly ComboBox _cmbMaze = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly CheckBox _chkPersonality = new() { Text = "迷路のゴーストに個性をつける（オフなら全員が最短で追う）", AutoSize = true };
    private readonly CheckBox _chkAutostart = new() { Text = "Windowsの起動時に自動で常駐する", AutoSize = true };
    private readonly Label _info = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly ComboBox _cmbMonitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly List<string> _monitorNames = new();          // _cmbMonitor の並び．先頭の "" はプライマリ（自動）
    private readonly List<Rectangle> _monitorAreas = new();       // 同じ並びの作業領域
    private readonly List<Label> _applied = new();                // 「適用しました」の表示（メインと詳細設定に1つずつ）
    private readonly WinTimer _appliedTimer = new() { Interval = 3000 };
    private readonly Form _details;       // 「詳細設定」．メインの画面とは別に，閉じても値は残る

    public SettingsForm(Settings current, Rectangle area, Action<Settings> apply)
    {
        _initial = current.Clone();
        _area = area;
        _apply = apply;

        Text = "Edge Walker 設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        for (int i = 0; i < Fields.Length; i++)
        {
            var f = Fields[i];
            _inputs[i] = new NumericUpDown
            {
                Minimum = f.Min,
                Maximum = f.Max,
                Increment = f.Inc,
                DecimalPlaces = f.Decimals,
                Width = 90,
            };
        }
        _inputs[_cellsIdx].ValueChanged += (_, _) => UpdateInfo();
        foreach (var n in MazeStyleNames) _cmbMaze.Items.Add(n);
        FillMonitors();
        _cmbMonitor.SelectedIndexChanged += (_, _) => UpdateInfo();
        _appliedTimer.Tick += (_, _) =>
        {
            _appliedTimer.Stop();
            foreach (var l in _applied) l.Visible = false;
        };

        _details = BuildDetails();

        // ---- メイン：モードのオン/オフと，PCとしての設定 ----
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(12),
        };
        int row = 0;
        void Span(Control c)
        {
            table.Controls.Add(c, 0, row++);
            table.SetColumnSpan(c, 2);
        }
        void Labeled(int fieldIndex)
        {
            table.Controls.Add(MakeLabel(Fields[fieldIndex].Label), 0, row);
            table.Controls.Add(_inputs[fieldIndex], 1, row++);
        }

        int Idx(string startsWith) => Array.FindIndex(Fields, f => f.Tab is null && f.Label.StartsWith(startsWith));
        Label Header(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 10, 3, 3) };

        Span(Header("■ エッジモード"));
        Span(_chkEdge);
        Labeled(Idx("ラウンドが終わって"));
        Labeled(Idx("クリック後"));

        Span(Header("■ メイズモード"));
        Span(_chkIdle);
        Labeled(Idx("操作がなくなって"));

        Span(Header("■ PC"));
        table.Controls.Add(MakeLabel("表示するモニター"), 0, row);
        table.Controls.Add(_cmbMonitor, 1, row++);
        Span(_chkAutostart);
        Span(_chkFullscreen);
        Labeled(Idx("メモリ使用率"));

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var apply_ = new Button { Text = "適用", Width = 80 };
        var reset = new Button { Text = "標準に戻す", Width = 90 };
        var more = new Button { Text = "詳細設定…", Width = 90 };
        var close = new Button { Text = "閉じる", Width = 80 };
        apply_.Click += (_, _) => ApplyClicked();
        reset.Click += (_, _) => LoadValues(new Settings());
        more.Click += (_, _) => _details.ShowDialog(this);
        close.Click += (_, _) => Close();
        buttons.Controls.Add(apply_);
        buttons.Controls.Add(reset);
        buttons.Controls.Add(more);
        buttons.Controls.Add(close);
        buttons.Controls.Add(MakeAppliedLabel());
        Span(buttons);

        Controls.Add(table);
        LoadValues(_initial);
    }

    private Label MakeAppliedLabel()
    {
        var l = new Label
        {
            Text = "✓ 適用しました",
            AutoSize = true,
            ForeColor = Color.FromArgb(255, 30, 130, 60),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 6, 3, 3),
            Visible = false,
        };
        _applied.Add(l);
        return l;
    }

    /// <summary>モニターの一覧を作る．先頭は「プライマリ（自動）」</summary>
    private void FillMonitors()
    {
        _monitorNames.Add("");
        _monitorAreas.Add(_area);
        _cmbMonitor.Items.Add("プライマリ（自動）");
        foreach (var scr in Screen.AllScreens)
        {
            _monitorNames.Add(scr.DeviceName);
            _monitorAreas.Add(scr.WorkingArea);
            string name = scr.DeviceName.TrimStart('\\', '.');
            _cmbMonitor.Items.Add($"{name}  {scr.Bounds.Width}x{scr.Bounds.Height}" + (scr.Primary ? "（プライマリ）" : ""));
        }
    }

    private static Label MakeLabel(string text)
        => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) };

    /// <summary>細かい数値をタブごとに並べた「詳細設定」を作る（メインと同じ入力欄を使う）</summary>
    private Form BuildDetails()
    {
        var form = new Form
        {
            Text = "Edge Walker 詳細設定",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true,
            StartPosition = FormStartPosition.CenterScreen,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        // TabControl は中身に合わせて大きさが変わらないので，大きさを決めておく．はみ出す分は各タブの中でスクロール
        var tabs = new TabControl { Size = new Size(660, 420) };
        foreach (var name in Tabs)
        {
            var page = new TabPage(name) { AutoScroll = true };
            var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
            int row = 0;
            for (int i = 0; i < Fields.Length; i++)
            {
                if (Fields[i].Tab != name) continue;
                table.Controls.Add(MakeLabel(Fields[i].Label), 0, row);
                table.Controls.Add(_inputs[i], 1, row++);
            }
            if (name == TIdle)
            {
                table.Controls.Add(MakeLabel("迷路の種類"), 0, row);
                table.Controls.Add(_cmbMaze, 1, row++);
                table.Controls.Add(_chkPersonality, 0, row);
                table.SetColumnSpan(_chkPersonality, 2);
            }
            else if (name == TLook)
            {
                table.Controls.Add(_info, 0, row);
                table.SetColumnSpan(_info, 2);
            }
            page.Controls.Add(table);
            tabs.TabPages.Add(page);
        }

        var apply_ = new Button { Text = "適用", Width = 80 };
        var close = new Button { Text = "閉じる", Width = 80 };
        apply_.Click += (_, _) => ApplyClicked();
        close.Click += (_, _) => form.Close();   // ShowDialog で開いた画面は，閉じても作り直さず隠れるだけ
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(apply_);
        buttons.Controls.Add(close);
        buttons.Controls.Add(MakeAppliedLabel());

        var outer = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(8) };
        outer.Controls.Add(tabs, 0, 0);
        outer.Controls.Add(buttons, 0, 1);
        form.Controls.Add(outer);
        return form;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _appliedTimer.Dispose();
            _details.Dispose();
        }
        base.Dispose(disposing);
    }

    private void LoadValues(Settings s)
    {
        for (int i = 0; i < Fields.Length; i++)
        {
            var f = Fields[i];
            _inputs[i].Value = Math.Min(Math.Max(f.Get(s), f.Min), f.Max);
        }
        _chkFullscreen.Checked = s.HideOnFullscreen;
        _chkAutostart.Checked = s.Autostart;
        int mi = _monitorNames.IndexOf(s.MonitorName);
        _cmbMonitor.SelectedIndex = mi < 0 ? 0 : mi;      // 見つからない（外されている）ときはプライマリ
        _chkEdge.Checked = s.EdgeMode;
        _chkIdle.Checked = s.IdleMode;
        _chkPersonality.Checked = s.IdlePersonality;
        _cmbMaze.SelectedIndex = Math.Clamp(s.MazeStyle, 0, MazeStyleNames.Length - 1);
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        int cells = (int)_inputs[_cellsIdx].Value;
        var area = _cmbMonitor.SelectedIndex >= 0 && _cmbMonitor.SelectedIndex < _monitorAreas.Count ? _monitorAreas[_cmbMonitor.SelectedIndex] : _area;
        int t = Math.Max(8, Math.Min(area.Width, area.Height) / Math.Max(1, cells));
        int ring = 2 * (area.Width / t + area.Height / t) - 4;
        _info.Text = $"1マス {t}px ／ エッジ {ring}マス";
    }

    private void ApplyClicked()
    {
        var s = _initial.Clone();   // 画面に出していない項目はそのまま引き継ぐ
        for (int i = 0; i < Fields.Length; i++)
            Fields[i].Set(s, _inputs[i].Value);
        s.HideOnFullscreen = _chkFullscreen.Checked;
        s.Autostart = _chkAutostart.Checked;
        s.MonitorName = _cmbMonitor.SelectedIndex > 0 ? _monitorNames[_cmbMonitor.SelectedIndex] : "";
        s.EdgeMode = _chkEdge.Checked;
        s.IdleMode = _chkIdle.Checked;
        s.IdlePersonality = _chkPersonality.Checked;
        s.MazeStyle = Math.Max(0, _cmbMaze.SelectedIndex);
        _apply(s);
        _initial = s.Clone();
        foreach (var l in _applied) l.Visible = true;
        _appliedTimer.Stop();
        _appliedTimer.Start();
    }
}
