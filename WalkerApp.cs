using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace EdgeWalker;

/// <summary>一回きりのタイマーの管理．世代を進める代わりに CancelAll() で全部止める</summary>
internal sealed class Scheduler
{
    private readonly List<WinTimer> _timers = new();

    public void Later(double ms, Action action)
    {
        var t = new WinTimer { Interval = Math.Max(1, (int)ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _timers.Remove(t);
            t.Dispose();
            action();
        };
        _timers.Add(t);
        t.Start();
    }

    public void CancelAll()
    {
        foreach (var t in _timers.ToArray())
        {
            t.Stop();
            t.Dispose();
        }
        _timers.Clear();
    }
}

/// <summary>ショートカットキー（Ctrl+Alt+Shift+P/S/Q）を受け取る見えないウィンドウ</summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private readonly Action<int> _onHotkey;

    public MessageWindow(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // HWND_MESSAGE
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            _onHotkey((int)m.WParam);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}

/// <summary>
/// 本体．タスクトレイに常駐し，画面の外周でキャラを動かす．
/// 軽くするため，固定フレームのループは持たず，主人公が1マス進むたびにだけ動く．
/// 隠れている間は80msの監視を止め，2秒ごとの確認だけが残る．
/// </summary>
internal sealed partial class WalkerApp : ApplicationContext
{
    private enum Phase { Birth, Idle, Start, Run, End }

    private const int IdleMs = 600, StartMs = 900, PopupMs = 900, BannerMs = 3200;
    private static readonly int[] MouthCycle = { 0, 1, 2, 1 };   // 口パク: 開, 半開, 閉, 半開

    private const int HotkeyPause = 1, HotkeyQuit = 2, HotkeySettings = 3;

    private Settings _cfg;
    private readonly Scheduler _sched = new();
    private readonly WinTimer _fastTimer = new() { Interval = 80 };     // クリック監視（見えている間だけ）
    private readonly WinTimer _slowTimer = new() { Interval = 2000 };   // 全画面・メモリ・最前面の確認
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly MessageWindow _msg;
    private SettingsForm? _settingsForm;

    private Rectangle _area;
    private Layout _lay = null!;
    private Sprites _sp = null!;
    private Overlay[] _strips = Array.Empty<Overlay>();
    private readonly List<Overlay> _popups = new();
    private Font? _font, _fontBig;
    private readonly SolidBrush _dotBrush = new(Color.FromArgb(255, 233, 168));
    private readonly SolidBrush _powerBrush = new(Color.FromArgb(255, 243, 196));

    private Game _game = null!;
    private Phase _phase;
    private int _wall1 = -1, _wall2 = -1;      // 開始前の仕切り（ゴースト出現範囲の両端）
    private int _birthStage;                   // 誕生の段階 0..10
    private bool _areaStale;                   // 隠れている間に作業領域が変わった
    private readonly List<(int pos, int until)> _bombs = new();   // 食べたゴーストのミニボム
    private bool _blinking, _powerBlinkOn = true;
    private bool _scaredBlinking, _scaredWhite;   // おびえたゴーストの青⇔白の点滅
    private readonly bool[] _dirty = new bool[4];
    private readonly List<(int cell, Bitmap bmp)> _actors = new();
    private readonly List<int> _lastCells = new();

    private bool _hidden, _timerHidden, _fsHidden, _memHidden, _paused, _lbHeld;

    public WalkerApp()
    {
        _cfg = SettingsStore.Load();

        var menu = new ContextMenuStrip();
        menu.Items.Add("設定…", null, (_, _) => OpenSettings());
        _pauseItem = new ToolStripMenuItem("一時停止", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("今すぐ出す", null, (_, _) => ShowNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => Quit());
        _tray = new NotifyIcon
        {
            Text = "Edge Walker",
            Icon = Sprites.CreateTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        _msg = new MessageWindow(OnHotkey);
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
        bool ok = Native.RegisterHotKey(_msg.Handle, HotkeyPause, mods, 'P')
                  & Native.RegisterHotKey(_msg.Handle, HotkeyQuit, mods, 'Q')
                  & Native.RegisterHotKey(_msg.Handle, HotkeySettings, mods, 'S');
        if (!ok)
            _tray.ShowBalloonTip(4000, "Edge Walker",
                "ショートカット（Ctrl+Alt+Shift+P/S/Q）が他のアプリと重なっています．トレイのメニューから操作してください．",
                ToolTipIcon.Warning);

        _fastTimer.Tick += (_, _) => FastTick();
        _slowTimer.Tick += (_, _) => SlowTick();
        _slowTimer.Start();

        Build();
    }

    /// <summary>表示するモニター．設定で選んだものが見つからなければ（外されたときなど）プライマリ</summary>
    private Screen CurrentScreen()
    {
        if (_cfg.MonitorName.Length > 0)
            foreach (var s in Screen.AllScreens)
                if (s.DeviceName == _cfg.MonitorName) return s;
        return Screen.PrimaryScreen!;
    }

    // ================= 組み立て =================
    private void Build()
    {
        AbortIdle();
        _sched.CancelAll();
        KillPopups();
        foreach (var s in _strips) s.Dispose();
        _strips = Array.Empty<Overlay>();
        _sp?.Dispose();
        _font?.Dispose();
        _fontBig?.Dispose();

        _area = CurrentScreen().WorkingArea;
        _areaStale = false;
        _lay = new Layout(_area, _cfg.CellsShort);
        _sp = Sprites.Load(_lay.T);
        float scale = _cfg.FontScalePct / 100f;
        _font = new Font("Segoe UI", Math.Max(8f, _lay.T * 0.5f * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        _fontBig = new Font("Segoe UI", Math.Max(10f, _lay.T * 0.8f * scale), FontStyle.Bold, GraphicsUnit.Pixel);

        _strips = new Overlay[4];
        for (int i = 0; i < 4; i++)
        {
            _strips[i] = new Overlay(_lay.StripRect(i));
            if (_cfg.EdgeMode)
            {
                _strips[i].Show();
                _strips[i].Present();
            }
        }

        _lastCells.Clear();
        _timerHidden = false;
        if (_cfg.EdgeMode)
        {
            _hidden = false;
            _fastTimer.Start();
            NewRound();
        }
        else
        {
            // エッジモードを使わない：普段は何も出さない．操作がなくなったらメイズだけが始まる
            _hidden = true;
            _fastTimer.Stop();
            _phase = Phase.End;
        }
        CheckSuspend();
    }

    // ================= 1ラウンドの流れ =================
    private void NewRound()
    {
        KillPopups();
        _blinking = false;
        _powerBlinkOn = true;
        _scaredBlinking = _scaredWhite = false;
        _bombs.Clear();
        _game = new Game(_lay.L, _cfg);
        _wall1 = _game.Wall;
        _wall2 = _game.Wall2;
        _phase = Phase.Birth;
        _birthStage = 0;
        MarkAll();
        Redraw();
        _sched.Later(_cfg.BirthStageMs, BirthNext);
    }

    /// <summary>誕生を1段階進める（0〜10の11段階）</summary>
    private void BirthNext()
    {
        _birthStage++;
        if (_birthStage < _sp.Eggs.Length)
        {
            Redraw();
            _sched.Later(_cfg.BirthStageMs, BirthNext);
            return;
        }
        _phase = Phase.Idle;
        Redraw();
        _sched.Later(IdleMs, StartCall);
    }

    private void StartCall()
    {
        _phase = Phase.Start;
        ShowPopup(_game.Pos, "start!", Color.White, StartMs, big: true);
        _sched.Later(StartMs, Go);
    }

    private void Go()
    {
        _wall1 = _wall2 = -1;
        MarkAll();
        _phase = Phase.Run;
        Apply(_game.BeginRun());
        Redraw();
        _sched.Later(_game.IntervalMs(), Tick);
    }

    private void Tick()
    {
        if (_paused)
        {
            _sched.Later(100, Tick);
            return;
        }
        Apply(_game.Step());
        Redraw();
        if (_game.State == GameState.Run && !_scaredBlinking && _game.PowerLeft > 0 && _game.PowerLeft <= _cfg.BlinkSteps)
        {
            _scaredBlinking = true;
            _scaredWhite = false;
            _sched.Later(ScaredBlinkMs(), ScaredBlinkTick);
        }
        if (_game.State == GameState.Run)
            _sched.Later(_game.IntervalMs(), Tick);
        else
            Finish();
    }

    private void Apply(IReadOnlyList<GameEvent> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case CookieNew n:
                    _dirty[_lay.StripOf(n.Pos)] = true;
                    if (n.Kind == CookieKind.Power && !_blinking)
                    {
                        _blinking = true;
                        _sched.Later(330, BlinkTick);
                    }
                    break;
                case CookieEaten c:
                    _dirty[_lay.StripOf(c.Pos)] = true;
                    break;
                case CookiesCleared:
                    MarkAll();
                    break;
                case GhostEaten ge:
                    _bombs.Add((ge.Pos, _game.Steps + _cfg.BombSteps));
                    _dirty[_lay.StripOf(ge.Pos)] = true;
                    break;
                case Popup p:
                    ShowPopup(p.Pos, p.Text, Color.FromArgb(255, 216, 74), PopupMs, big: false);
                    break;
            }
        }
    }

    private void Finish()
    {
        _phase = Phase.End;
        _ringEndedAt = Environment.TickCount64;
        bool won = _game.State == GameState.Won;
        string text = (won ? "victory!!" : "lose…") + "\n" + _game.Points + "pt";
        ShowPopup(_game.Pos, text, won ? Color.FromArgb(255, 216, 74) : Color.FromArgb(159, 180, 255), BannerMs, big: true);
        _sched.Later(BannerMs + 300, () => Hide(_cfg.EndHideMin * 60));
    }

    /// <summary>おびえたゴーストの色の入れ替え間隔．遅くなったゴーストの移動（1マス）の半分の時間＝移動の2倍の速さ</summary>
    private double ScaredBlinkMs() => _game.IntervalMs() / (2.0 * Math.Max(0.1, _cfg.ScaredRatio));

    private void ScaredBlinkTick()
    {
        var g = _game;
        if (g.State != GameState.Run || g.PowerLeft <= 0 || g.PowerLeft > _cfg.BlinkSteps)
        {
            // 効果が終わった（または大きいクッキーでまた延びた）
            _scaredBlinking = _scaredWhite = false;
            if (g.State == GameState.Run) Redraw();
            return;
        }
        if (!_paused) _scaredWhite = !_scaredWhite;
        foreach (var gh in g.Ghosts)
            if (gh.Alive) _dirty[_lay.StripOf(gh.S)] = true;
        Flush();
        _sched.Later(ScaredBlinkMs(), ScaredBlinkTick);
    }

    /// <summary>大きいクッキーがある間だけ点滅させる（無い間はタイマーを止めて軽くする）</summary>
    private void BlinkTick()
    {
        bool any = false;
        foreach (var kv in _game.Cookies)
        {
            if (kv.Value != CookieKind.Power) continue;
            any = true;
            _dirty[_lay.StripOf(kv.Key)] = true;
        }
        if (!any)
        {
            _blinking = false;
            return;
        }
        _powerBlinkOn = !_powerBlinkOn;
        Flush();
        _sched.Later(330, BlinkTick);
    }

    // ================= 描画 =================
    private void MarkAll()
    {
        for (int i = 0; i < _dirty.Length; i++) _dirty[i] = true;
    }

    private void BuildActors()
    {
        _actors.Clear();
        var g = _game;
        bool birth = _phase == Phase.Birth;   // 誕生の演出は主人公だけ（ゴーストは最初から普通の姿）
        if (birth) _actors.Add((g.Pos, _sp.Eggs[_birthStage]));

        // 食べたゴーストは，誕生の1段階目（ミニボム）の画像を少しの間だけ残す
        _bombs.RemoveAll(b => g.Steps >= b.until);
        foreach (var b in _bombs) _actors.Add((b.pos, _sp.Eggs[0]));

        if (g.PlayerActive && !birth)
        {
            int frame = MouthCycle[g.Steps % MouthCycle.Length];
            var (dx, dy) = _lay.Heading(g.Pos, g.Dir);
            _actors.Add((g.Pos, _sp.Player[frame, Sprites.DirIndex(dx, dy)]));
        }

        bool power = g.PowerLeft > 0;
        for (int i = 0; i < g.Ghosts.Count; i++)
        {
            var gh = g.Ghosts[i];
            if (!gh.Alive || !gh.Active) continue;
            int foot = gh.S & 1;   // 1マスごとに足が入れ替わる
            Bitmap bmp;
            if (power)
            {
                // 全員青．終わり際は白と入れ替わって点滅する
                bmp = _sp.Scared[_scaredBlinking && _scaredWhite ? 1 : 0, foot];
            }
            else
            {
                var (gx, gy) = _lay.Heading(gh.S, g.Dir);
                bmp = _sp.Enemy[gh.Color % 4, Sprites.DirIndex(gx, gy), foot];
            }
            _actors.Add((gh.S, bmp));
        }
    }

    /// <summary>動いたキャラの前と今のマスがある帯だけを描き直す</summary>
    private void Redraw()
    {
        BuildActors();
        foreach (int s in _lastCells) _dirty[_lay.StripOf(s)] = true;
        _lastCells.Clear();
        foreach (var (cell, _) in _actors)
        {
            _dirty[_lay.StripOf(cell)] = true;
            _lastCells.Add(cell);
        }
        Flush();
    }

    private void Flush()
    {
        for (int i = 0; i < _strips.Length; i++)
        {
            if (!_dirty[i]) continue;
            _dirty[i] = false;
            RenderStrip(i);
        }
    }

    private void RenderStrip(int i)
    {
        var ov = _strips[i];
        int T = _lay.T;
        using (var g = ov.BeginDraw())
        {
            foreach (var kv in _game.Cookies)
            {
                int s = kv.Key;
                if (_lay.StripOf(s) != i) continue;
                bool power = kv.Value == CookieKind.Power;
                if (power && !_powerBlinkOn) continue;
                var tl = _lay.LocalTopLeft(s);
                float r = power ? T * 7f / 32f : Math.Max(2f, T / 16f);
                float cx = tl.X + T / 2f, cy = tl.Y + T / 2f;
                g.FillEllipse(power ? _powerBrush : _dotBrush, cx - r, cy - r, 2 * r, 2 * r);
            }

            DrawWall(g, i, _wall1);
            DrawWall(g, i, _wall2);

            foreach (var (cell, bmp) in _actors)
            {
                if (_lay.StripOf(cell) != i) continue;
                var tl = _lay.LocalTopLeft(cell);
                g.DrawImage(bmp, new Rectangle(tl.X, tl.Y, T, T));
            }
        }
        ov.Present();
    }

    /// <summary>仕切り：通路を横切る，角の丸い細長い矩形の白い枠線（画像は使わない）</summary>
    private void DrawWall(Graphics g, int strip, int pos)
    {
        if (pos < 0 || _lay.StripOf(pos) != strip) return;
        int T = _lay.T;
        var tl = _lay.LocalTopLeft(pos);
        var (dx, _) = _lay.Heading(pos, _game.Dir);
        float pw = Math.Max(1.5f, T / 14f);
        float thick = T * 0.28f;
        float x, y, w, h;
        if (dx != 0) { w = thick; h = T - pw; x = tl.X + (T - thick) / 2f; y = tl.Y + pw / 2f; }     // 左右に進む辺: 縦長
        else { w = T - pw; h = thick; x = tl.X + pw / 2f; y = tl.Y + (T - thick) / 2f; }              // 上下に進む辺: 横長
        float r = Math.Min(w, h) * 0.45f;
        using var path = new GraphicsPath();
        path.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        path.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        path.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.White, pw) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
        g.SmoothingMode = SmoothingMode.None;
    }

    /// <summary>キャラのそばに文字を出す（上辺では下向き，他の辺では画面の内側寄りに出る）</summary>
    private void ShowPopup(int pos, string text, Color color, int ms, bool big)
    {
        ShowPopupAround(_lay.ScreenTopLeft(pos), text, color, ms, big, _lay.StripOf(pos));
    }

    /// <summary>strip: 0=上辺 1=右辺 2=下辺 3=左辺（外周の中でのキャラの位置）．-1 なら preferAbove で決める</summary>
    private void ShowPopupAround(Point cell, string text, Color color, int ms, bool big, bool preferAbove)
        => ShowPopupAround(cell, text, color, ms, big, preferAbove ? 2 : 0);

    private void ShowPopupAround(Point cell, string text, Color color, int ms, bool big, int strip)
    {
        var font = big ? _fontBig! : _font!;
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        SizeF sz;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
            sz = pg.MeasureString(text, font, PointF.Empty, fmt);
        int w = (int)Math.Ceiling(sz.Width) + 12, h = (int)Math.Ceiling(sz.Height) + 10;

        int T = _lay.T;
        int x, y;
        switch (strip)
        {
            case 0: x = cell.X + (T - w) / 2; y = cell.Y + T; break;    // 上辺: 下に出す
            case 2: x = cell.X + (T - w) / 2; y = cell.Y - h; break;    // 下辺: 上に出す
            case 1: x = cell.X - w; y = cell.Y + (T - h) / 2; break;    // 右辺: 左に出す
            default: x = cell.X + T; y = cell.Y + (T - h) / 2; break;   // 左辺: 右に出す
        }
        x = Math.Min(Math.Max(x, _area.Left), _area.Right - w);
        y = Math.Min(Math.Max(y, _area.Top), _area.Bottom - h);

        var ov = new Overlay(new Rectangle(x, y, w, h));
        ov.Show();
        using (var g = ov.BeginDraw())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddString(text, font.FontFamily, (int)font.Style, font.Size, new RectangleF(0, 0, w, h), fmt);
            using var pen = new Pen(Color.FromArgb(235, 36, 24, 0), Math.Max(2f, font.Size / 6f)) { LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(color);
            g.DrawPath(pen, path);
            g.FillPath(brush, path);
        }
        ov.Present();
        _popups.Add(ov);
        _sched.Later(ms, () => KillPopup(ov));
    }

    private void KillPopup(Overlay ov)
    {
        if (_popups.Remove(ov)) ov.Dispose();
    }

    private void KillPopups()
    {
        foreach (var ov in _popups.ToArray()) KillPopup(ov);
    }

    // ================= 消える・戻る =================
    private void Hide(double seconds = 0)
    {
        if (!_hidden)
        {
            _sched.CancelAll();
            _hidden = true;
            KillPopups();
            foreach (var s in _strips) s.Hide();
            _fastTimer.Stop();
        }
        if (seconds > 0)
        {
            _timerHidden = true;
            _sched.Later(seconds * 1000, () =>
            {
                _timerHidden = false;
                MaybeRestart();
            });
        }
    }

    private void MaybeRestart()
    {
        if (_mode == Mode.Idle || !_cfg.EdgeMode || _paused) return;      // 一時停止中は出さない
        if (!_hidden || _timerHidden || _fsHidden || _memHidden) return;
        if (_areaStale)
        {
            Build();   // 隠れている間に作業領域（タスクバー・解像度）が変わっていたら作り直す
            return;
        }
        _hidden = false;
        foreach (var s in _strips)
        {
            s.Show();
            Native.KeepOnTop(s.Handle);
        }
        _fastTimer.Start();
        NewRound();
    }

    private void ShowNow()
    {
        _timerHidden = false;
        if (!_cfg.EdgeMode)
        {
            // エッジを使わないときは，その場でメイズを始める
            if (IdleAllowed) EnterIdle();
            return;
        }
        MaybeRestart();
    }

    /// <summary>全画面アプリ・メモリ使用率が高いとき，出現しない（隠れる）</summary>
    private void CheckSuspend()
    {
        _fsHidden = _cfg.HideOnFullscreen && Native.FullscreenAppRunning(CurrentScreen().Bounds);
        int limit = _cfg.MemoryLimitPct;
        if (limit > 0)
        {
            int load = Native.MemoryLoad();
            // 戻るときは少し余裕を持たせて，境目で出たり消えたりしないようにする
            _memHidden = load >= (_memHidden ? limit - 3 : limit);
        }
        else
        {
            _memHidden = false;
        }

        if (_fsHidden || _memHidden) Hide();
        else MaybeRestart();
    }

    // ================= 監視 =================
    private bool Hit(Point p)
    {
        int T = _lay.T, m = T / 2;
        bool Near(int s)
        {
            var tl = _lay.ScreenTopLeft(s);
            return p.X >= tl.X - m && p.X < tl.X + T + m && p.Y >= tl.Y - m && p.Y < tl.Y + T + m;
        }
        if (_game.PlayerActive && Near(_game.Pos)) return true;
        foreach (var g in _game.Ghosts)
            if (g.Alive && g.Active && Near(g.S)) return true;
        return false;
    }

    private void FastTick()
    {
        bool lb = Native.KeyDown(Native.VK_LBUTTON);
        if (lb && !_lbHeld && !_hidden && Native.GetCursorPos(out var pt) && Hit(new Point(pt.X, pt.Y)))
            Hide(_cfg.ClickHideMin * 60);   // クリック自体は下のウィンドウにそのまま通る
        _lbHeld = lb;
    }

    private void SlowTick()
    {
        // 作業領域（タスクバーの設定・解像度・モニター）が実際に変わった時だけ作り直す．
        // 自動非表示のタスクバーが一瞬出入りしても，作業領域の値は変わらないので何も起きない
        var wa = CurrentScreen().WorkingArea;
        if (wa.X != _area.X || wa.Y != _area.Y || wa.Width != _area.Width || wa.Height != _area.Height)
        {
            if (_mode == Mode.Idle)
            {
                Build();   // Build の中で迷路は片づけられる
                return;
            }
            if (_hidden) _areaStale = true;
            else
            {
                Build();
                return;
            }
        }
        CheckSuspend();
        if (_mode == Mode.Idle)
        {
            if (_fsHidden || _memHidden)
            {
                SuspendIdle();
                return;
            }
            if (_vwPlayer is not null) Native.KeepOnTop(_vwPlayer.Ov.Handle);
            foreach (var v in _vwGhost) if (v is not null) Native.KeepOnTop(v.Ov.Handle);
            foreach (var t in _transit) Native.KeepOnTop(t.Ov.Handle);
            if (_lives is not null) Native.KeepOnTop(_lives.Handle);
            foreach (var p in _popups) Native.KeepOnTop(p.Handle);
            return;
        }
        CheckIdleEntry();
        if (_mode == Mode.Idle) return;
        if (_hidden) return;
        foreach (var s in _strips) Native.KeepOnTop(s.Handle);
        foreach (var p in _popups) Native.KeepOnTop(p.Handle);
    }

    // ================= 操作 =================
    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyPause: TogglePause(); break;
            case HotkeyQuit: Quit(); break;
            case HotkeySettings: OpenSettings(); break;
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseItem.Text = _paused ? "再開" : "一時停止";
        if (!_paused) MaybeRestart();     // 止まっている間に隠れる時間が過ぎていたら，再開と同時に出す
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_cfg, _area, ApplySettings);
        _settingsForm.Show();
    }

    private void ApplySettings(Settings s)
    {
        s.Clamp();
        _cfg = s;
        SettingsStore.Save(s);
        Autostart.Set(s.Autostart);
        Build();
    }

    private void Quit()
    {
        _fastTimer.Stop();
        _slowTimer.Stop();
        _sched.CancelAll();
        AbortIdle();
        _idleTimer?.Dispose();
        KillPopups();
        foreach (var s in _strips) s.Dispose();
        _settingsForm?.Close();
        Native.UnregisterHotKey(_msg.Handle, HotkeyPause);
        Native.UnregisterHotKey(_msg.Handle, HotkeyQuit);
        Native.UnregisterHotKey(_msg.Handle, HotkeySettings);
        _msg.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
