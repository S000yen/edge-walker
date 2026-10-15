using System.Drawing;
using System.Drawing.Drawing2D;
using WinTimer = System.Windows.Forms.Timer;

namespace EdgeWalker;

/// <summary>
/// アイドルモード：キーボード・マウスの操作がしばらくないと，青い壁の迷路でパックマンが自動で遊ぶ．
/// 操作があると壁が消えて，全員が一番近い外周へ散らばり，着いたものから外周モードの追跡・逃走に入る．
/// 迷路は見える範囲（キャラの周囲）だけ，キャラごとの小さな透明ウィンドウで表示する．
/// </summary>
internal sealed partial class WalkerApp
{
    private enum Mode { Ring, Idle }
    private enum IdlePhase { None, Birth, Run, End }

    private sealed class Transit
    {
        public int Kind;            // -1=主人公，0〜=外周のゲームでのゴースト番号
        public int Color;           // ゴーストの色の番号
        public int X, Y;            // 今いるマス
        public int Dx, Dy;          // 進む向き
        public int Left;            // 外周まで残りのマス数
        public int Steps;           // 進んだマス数（口パク用）
        public bool Done;
        public Overlay Ov = null!;
    }

    private Mode _mode = Mode.Ring;
    private IdlePhase _iphase = IdlePhase.None;
    private Maze? _maze;
    private MazeArt? _art;
    private MazeGame? _mg;
    private VisionWin? _vwPlayer;
    private readonly VisionWin?[] _vwGhost = new VisionWin?[4];
    private Overlay? _lives;
    private int _ibirth;
    private bool _rebirth;                   // 主人公が復活する演出中（最初の誕生ではない）
    private readonly List<(int x, int y, int until)> _ibombs = new();
    private uint _idleStamp;
    private WinTimer? _idleTimer;
    private bool _iBlinking, _iWhite;
    private readonly List<Transit> _transit = new();
    private double _paceCarry = -1;        // 途中でモードが切り替わったとき，ゴーストの加速の時間を引き継ぐ（-1=引き継がない）
    private long _ringEndedAt = -1_000_000_000L;   // 外周のラウンドが終わった時刻（Environment.TickCount64）

    // ================= 入る・見張る =================
    private bool IdleAllowed => _cfg.IdleMode && _mode == Mode.Ring && !_paused && !_fsHidden && !_memHidden
                                && _lay.Cols >= 12 && _lay.Rows >= 10;

    private void CheckIdleEntry()
    {
        if (!IdleAllowed) return;
        // エッジのラウンドの途中では入らない（ラウンドが終わっている，または隠れているときだけ）
        if (!_hidden && _phase != Phase.End) return;
        if (Native.IdleMs() < _cfg.IdleSeconds * 1000L) return;
        // 外周のラウンドが終わってすぐは入らない（アイドルの試合間隔と同じだけ空ける）
        if (Environment.TickCount64 - _ringEndedAt < _cfg.IdleRestartSec * 1000L) return;
        EnterIdle();
    }

    private void EnterIdle()
    {
        // 外周のラウンドの途中なら，その時点のゴーストの速さを引き継ぐ
        _paceCarry = _phase == Phase.Run && _game.State == GameState.Run ? _game.PaceSec : -1;
        _mode = Mode.Idle;
        _sched.CancelAll();
        KillPopups();
        foreach (var s in _strips) s.Hide();
        _hidden = true;
        _timerHidden = false;
        _fastTimer.Stop();
        _blinking = false;
        _scaredBlinking = _scaredWhite = false;

        _idleStamp = Native.LastInputStamp();
        if (_idleTimer is null)
        {
            _idleTimer = new WinTimer { Interval = 250 };
            _idleTimer.Tick += (_, _) => IdleWatch();
        }
        _idleTimer.Start();
        StartIdleRound();
    }

    /// <summary>操作があったら外周へ戻る</summary>
    private void IdleWatch()
    {
        if (_mode != Mode.Idle)
        {
            _idleTimer?.Stop();
            return;
        }
        if (Native.LastInputStamp() != _idleStamp) LeaveIdle();
    }

    // ================= 1試合の流れ =================
    private void StartIdleRound()
    {
        CloseVision();
        _sched.CancelAll();
        KillPopups();
        _ibombs.Clear();
        _iBlinking = _iWhite = false;
        _rebirth = false;
        _art?.Dispose();
        _maze = Maze.Generate(_lay.Cols, _lay.Rows, new SystemRng());
        _art = new MazeArt(_maze, _lay.T);
        _mg = new MazeGame(_maze, _cfg, null, Math.Max(0, _paceCarry));
        _paceCarry = -1;
        CreateVision();
        DrawLives();
        _iphase = IdlePhase.Birth;
        _ibirth = 0;
        RenderIdle();
        _sched.Later(_cfg.BirthStageMs, IdleBirthNext);
    }

    /// <summary>主人公の誕生（最初と，復活のとき）を1段階進める</summary>
    private void IdleBirthNext()
    {
        if (_mode != Mode.Idle || _mg is null) return;
        _ibirth++;
        if (_ibirth < _sp.Eggs.Length)
        {
            RenderIdle();
            _sched.Later(_cfg.BirthStageMs, IdleBirthNext);
            return;
        }
        _iphase = IdlePhase.Run;
        RenderIdle();
        bool again = _rebirth;
        _rebirth = false;
        _sched.Later(again ? 250 : IdleMs, () =>
        {
            if (_mg is null) return;
            if (!again) _mg.BeginRun();
            _sched.Later(_mg.IntervalMs(), IdleTick);
        });
    }

    private void IdleTick()
    {
        if (_mode != Mode.Idle || _mg is null) return;
        if (_paused)
        {
            _sched.Later(100, IdleTick);
            return;
        }
        var ev = _mg.Step();
        bool died = ApplyIdle(ev);
        _ibombs.RemoveAll(b => _mg.Steps >= b.until);
        if (_mg.State == GameState.Run && !_iBlinking && _mg.PowerLeft > 0 && _mg.PowerLeft <= _cfg.BlinkSteps)
        {
            _iBlinking = true;
            _iWhite = false;
            _sched.Later(IdleBlinkMs(), IdleBlinkTick);
        }
        if (died && _mg.State == GameState.Run)
        {
            StartRebirth();
            return;
        }
        RenderIdle();
        if (_mg.State == GameState.Run) _sched.Later(_mg.IntervalMs(), IdleTick);
        else IdleFinish();
    }

    /// <summary>主人公が捕まって復活する：遠い場所で，誕生と同じ11段階の演出（その間ゴーストは止まる）</summary>
    private void StartRebirth()
    {
        _rebirth = true;
        _iphase = IdlePhase.Birth;
        _ibirth = 0;
        DrawLives();
        RenderIdle();
        _sched.Later(_cfg.BirthStageMs, IdleBirthNext);
    }

    private double IdleBlinkMs() => _mg!.IntervalMs() / (2.0 * Math.Max(0.1, _cfg.ScaredRatio));

    private void IdleBlinkTick()
    {
        var mg = _mg;
        if (_mode != Mode.Idle || mg is null || mg.State != GameState.Run || mg.PowerLeft <= 0 || mg.PowerLeft > _cfg.BlinkSteps)
        {
            _iBlinking = _iWhite = false;
            return;
        }
        if (_iphase == IdlePhase.Run)
        {
            if (!_paused) _iWhite = !_iWhite;
            RenderIdle();
        }
        _sched.Later(IdleBlinkMs(), IdleBlinkTick);
    }

    /// <summary>ゲームの出来事を画面に反映する．主人公が復活するなら true</summary>
    private bool ApplyIdle(IReadOnlyList<MEvent> events)
    {
        bool died = false;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case MKind.GhostEaten:
                    _ibombs.Add((e.X, e.Y, _mg!.Steps + Math.Min(_cfg.BombSteps, 2)));
                    break;
                case MKind.Died:
                    died = true;
                    break;
                case MKind.Popup:
                    ShowPopupAtCell(e.X, e.Y, e.Text ?? "", Color.FromArgb(255, 216, 74), PopupMs, false);
                    break;
            }
        }
        return died;
    }

    private void IdleFinish()
    {
        _iphase = IdlePhase.End;
        var mg = _mg!;
        bool won = mg.State == GameState.Won;
        string text = (won ? "victory!!" : "lose…") + "\n" + mg.Points + "pt";
        ShowPopupAtCell(mg.PX, mg.PY, text, won ? Color.FromArgb(255, 216, 74) : Color.FromArgb(159, 180, 255), BannerMs, true);
        _sched.Later(BannerMs + 300, CloseVision);
        _sched.Later(Math.Max(_cfg.IdleRestartSec * 1000.0, BannerMs + 500), StartIdleRound);
    }

    // ================= 描画 =================
    private (int x, int y) VisionScreenPos(int cx, int cy)
    {
        int R = _cfg.IdleVision, T = _lay.T;
        return (_lay.Ox + (cx - R) * T, _lay.Oy + (cy - R) * T);
    }

    private void CreateVision()
    {
        var mg = _mg!;
        int R = _cfg.IdleVision, T = _lay.T;
        var (sx, sy) = VisionScreenPos(mg.PX, mg.PY);
        _vwPlayer = new VisionWin(R, T, sx, sy);
        SyncVision();
    }

    /// <summary>外に出ているゴースト（目玉を含む）にだけ，見える範囲の窓を持たせる</summary>
    private void SyncVision()
    {
        var mg = _mg;
        if (mg is null) return;
        int R = _cfg.IdleVision, T = _lay.T;
        for (int i = 0; i < _vwGhost.Length; i++)
        {
            bool want = i < mg.Ghosts.Count && (mg.Ghosts[i].St == GSt.Out || mg.Ghosts[i].St == GSt.Eye);
            if (want && _vwGhost[i] is null)
            {
                var (gx, gy) = VisionScreenPos(mg.Ghosts[i].X, mg.Ghosts[i].Y);
                _vwGhost[i] = new VisionWin(R, T, gx, gy);
            }
            else if (!want && _vwGhost[i] is not null)
            {
                _vwGhost[i]!.Dispose();
                _vwGhost[i] = null;
            }
        }
    }

    private void CloseVision()
    {
        _vwPlayer?.Dispose();
        _vwPlayer = null;
        for (int i = 0; i < _vwGhost.Length; i++)
        {
            _vwGhost[i]?.Dispose();
            _vwGhost[i] = null;
        }
        _lives?.Dispose();
        _lives = null;
    }

    /// <summary>画面左下の2マス（迷路の凹み）に，残りの命を主人公の絵（左向き・半開き）で出す．使った分は消える</summary>
    private void DrawLives()
    {
        var mg = _mg;
        if (mg is null || mg.MaxLives <= 0) return;
        int T = _lay.T;
        if (_lives is null)
        {
            _lives = new Overlay(new Rectangle(_lay.Ox, _lay.Oy + (_lay.Rows - 1) * T, 2 * T, T));
            _lives.Show();
        }
        using (var g = _lives.BeginDraw())
        {
            for (int i = 0; i < mg.Lives && i < 2; i++)
                g.DrawImage(_sp.Player[1, 1], new Rectangle(i * T, 0, T, T));
        }
        _lives.Present();
    }

    private void RenderIdle()
    {
        var mg = _mg;
        if (mg is null || _art is null) return;
        SyncVision();
        int R = _cfg.IdleVision;

        void One(VisionWin? w, int cx, int cy)
        {
            if (w is null) return;
            var (sx, sy) = VisionScreenPos(cx, cy);
            w.Render(cx, cy, sx, sy, g => DrawIdleWorld(g, cx, cy, R));
        }

        One(_vwPlayer, mg.PX, mg.PY);
        for (int i = 0; i < mg.Ghosts.Count && i < _vwGhost.Length; i++)
            One(_vwGhost[i], mg.Ghosts[i].X, mg.Ghosts[i].Y);
    }

    /// <summary>迷路全体の座標（マス x T）で，(cx, cy) の周囲 R マスを描く</summary>
    private void DrawIdleWorld(Graphics g, int cx, int cy, int R)
    {
        var mg = _mg!;
        var maze = _maze!;
        int T = _lay.T;

        var view = new Rectangle((cx - R) * T, (cy - R) * T, (2 * R + 1) * T, (2 * R + 1) * T);
        _art!.DrawWalls(g, view);

        int x0 = Math.Max(0, cx - R), x1 = Math.Min(maze.W - 1, cx + R);
        int y0 = Math.Max(0, cy - R), y1 = Math.Min(maze.H - 1, cy + R);
        bool InView(int x, int y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;

        if (_art.GateInView(view)) _art.DrawGate(g, mg.GateOpen);
        _art.DrawWarps(g, InView);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                byte kind = mg.Cookie[y * maze.W + x];
                if (kind == 0) continue;
                bool power = kind == 2;
                float r = power ? T * 7f / 32f : Math.Max(2f, T / 16f);
                float ccx = x * T + T / 2f, ccy = y * T + T / 2f;
                g.FillEllipse(power ? _powerBrush : _dotBrush, ccx - r, ccy - r, 2 * r, 2 * r);
            }
        g.SmoothingMode = SmoothingMode.None;

        foreach (var b in _ibombs)
            if (InView(b.x, b.y)) g.DrawImage(_sp.Eggs[0], new Rectangle(b.x * T, b.y * T, T, T));

        bool powered = mg.PowerLeft > 0;
        for (int i = 0; i < mg.Ghosts.Count; i++)
        {
            var gh = mg.Ghosts[i];
            if (!InView(gh.X, gh.Y)) continue;
            int dir = gh.Dir < 0 ? 0 : gh.Dir;
            Bitmap bmp;
            if (gh.St == GSt.Eye)
                bmp = _sp.Eyes[dir];
            else if (gh.St == GSt.Out && powered)
                bmp = _sp.Scared[_iBlinking && _iWhite ? 1 : 0, (gh.X + gh.Y) & 1];   // 1マスごとに足が入れ替わる
            else
                bmp = _sp.Enemy[i % 4, dir, (gh.X + gh.Y) & 1];
            g.DrawImage(bmp, new Rectangle(gh.X * T, gh.Y * T, T, T));
        }

        if (InView(mg.PX, mg.PY))
        {
            Bitmap pb = _iphase == IdlePhase.Birth
                ? _sp.Eggs[Math.Min(_ibirth, _sp.Eggs.Length - 1)]
                : _sp.Player[MouthCycle[mg.Steps % MouthCycle.Length], mg.PDir];
            g.DrawImage(pb, new Rectangle(mg.PX * T, mg.PY * T, T, T));
        }
    }

    /// <summary>迷路のマス (cx, cy) のそばに文字を出す（上に出す．入らなければ下）</summary>
    private void ShowPopupAtCell(int cx, int cy, string text, Color color, int ms, bool big)
    {
        int T = _lay.T;
        var cell = new Point(_lay.Ox + cx * T, _lay.Oy + cy * T);
        ShowPopupAround(cell, text, color, ms, big, preferAbove: true);
    }

    // ================= 操作されたとき：外周へ散らばる =================
    private void LeaveIdle()
    {
        _idleTimer?.Stop();
        _sched.CancelAll();
        KillPopups();

        var mg = _mg;
        bool playing = mg is not null && _vwPlayer is not null && (_iphase == IdlePhase.Birth || _iphase == IdlePhase.Run);
        CloseVision();
        _art?.Dispose();
        _art = null;
        _maze = null;
        _mg = null;
        _iphase = IdlePhase.None;
        _mode = Mode.Ring;

        if (!_cfg.EdgeMode)
        {
            // エッジを使わない：迷路もキャラも消して，何も出ない状態に戻る
            _hidden = true;
            _timerHidden = false;
            return;
        }

        _hidden = false;
        _timerHidden = false;
        foreach (var s in _strips)
        {
            s.Show();
            Native.KeepOnTop(s.Handle);
        }
        _fastTimer.Start();

        if (!playing)
        {
            NewRound();
            return;
        }
        BeginScatter(mg!);
    }

    private static int NearestBorder(int x, int y, int cols, int rows, out int dx, out int dy)
    {
        int dl = x, dr = cols - 1 - x, dt = y, db = rows - 1 - y;
        int m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        if (m == dl) { dx = -1; dy = 0; }
        else if (m == dr) { dx = 1; dy = 0; }
        else if (m == dt) { dx = 0; dy = -1; }
        else { dx = 0; dy = 1; }
        return m;
    }

    private void BeginScatter(MazeGame mg)
    {
        _bombs.Clear();
        _wall1 = _wall2 = -1;
        _blinking = false;
        _scaredBlinking = _scaredWhite = false;
        _powerBlinkOn = true;

        var ringIndex = new Dictionary<int, int>();
        for (int s = 0; s < _lay.L; s++)
            ringIndex[_lay.Path[s].Y * _lay.Cols + _lay.Path[s].X] = s;

        int T = _lay.T;
        _transit.Clear();
        int playerRing = 0;
        var ghostRing = new List<int>();
        var ghostColor = new List<int>();

        // 主人公と，ゴースト全員（家の中・目玉も含む）
        for (int k = -1; k < mg.Ghosts.Count; k++)
        {
            int x = k < 0 ? mg.PX : mg.Ghosts[k].X;
            int y = k < 0 ? mg.PY : mg.Ghosts[k].Y;
            int left = NearestBorder(x, y, _lay.Cols, _lay.Rows, out int dx, out int dy);
            int tx = x + dx * left, ty = y + dy * left;
            int ring = ringIndex[ty * _lay.Cols + tx];
            int kind = -1;
            if (k < 0)
            {
                playerRing = ring;
            }
            else
            {
                kind = ghostRing.Count;
                ghostRing.Add(ring);
                ghostColor.Add(k % 4);
            }

            var t = new Transit
            {
                Kind = kind, Color = k < 0 ? 0 : k % 4, X = x, Y = y, Dx = dx, Dy = dy, Left = left,
                Ov = new Overlay(new Rectangle(_lay.Ox + x * T, _lay.Oy + y * T, T, T)),
            };
            t.Ov.Show();
            _transit.Add(t);
            DrawTransit(t);
        }

        _game = new Game(_lay.L, _cfg, new SystemRng(), playerRing, ghostRing, ghostColor, mg.PaceSec);
        _phase = Phase.Run;
        MarkAll();
        Redraw();
        _sched.Later(_game.IntervalMs(), Tick);
        _sched.Later(_cfg.IdleScatterMs, ScatterTick);
    }

    private void DrawTransit(Transit t)
    {
        int T = _lay.T;
        int dir = Sprites.DirIndex(t.Dx, t.Dy);
        Bitmap bmp = t.Kind < 0
            ? _sp.Player[MouthCycle[t.Steps % MouthCycle.Length], dir]
            : _sp.Enemy[t.Color % 4, dir, (t.X + t.Y) & 1];
        using (var g = t.Ov.BeginDraw())
            g.DrawImage(bmp, new Rectangle(0, 0, T, T));
        t.Ov.PresentAt(_lay.Ox + t.X * T, _lay.Oy + t.Y * T);
    }

    private void ScatterTick()
    {
        bool any = false;
        foreach (var t in _transit)
        {
            if (t.Done) continue;
            if (t.Left > 0)
            {
                t.X += t.Dx;
                t.Y += t.Dy;
                t.Left--;
                t.Steps++;
                DrawTransit(t);
            }
            if (t.Left == 0)
            {
                // 外周に着いた：ここから外周モードの追跡・逃走に入る
                t.Done = true;
                t.Ov.Dispose();
                if (t.Kind < 0) Apply(_game.ActivatePlayer());
                else _game.ActivateGhost(t.Kind);
                Redraw();
            }
            else any = true;
        }
        if (any) _sched.Later(_cfg.IdleScatterMs, ScatterTick);
        else _transit.Clear();
    }

    // ================= 中断・後片付け =================
    /// <summary>アイドルモードの表示を全部片づける（外周モードの状態には触らない）</summary>
    private void AbortIdle()
    {
        _idleTimer?.Stop();
        CloseVision();
        foreach (var t in _transit) t.Ov.Dispose();
        _transit.Clear();
        _art?.Dispose();
        _art = null;
        _maze = null;
        _mg = null;
        _iphase = IdlePhase.None;
        _mode = Mode.Ring;
    }

    /// <summary>全画面アプリやメモリ不足で，アイドルモードをやめる（外周は隠れたまま）</summary>
    private void SuspendIdle()
    {
        _sched.CancelAll();
        KillPopups();
        AbortIdle();
        _hidden = true;
    }
}
