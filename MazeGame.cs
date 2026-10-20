namespace EdgeWalker;

// アイドルモード用のゲーム（画面に依存しない）．主人公は自動で動く．
// 時間の単位は外周モードと同じ「ブロック時間」（主人公が1マス進む時間）．
//
// ゴーストは最初，家（2x2）の中にいて，順に出てくる．道を戻らず，分かれ道ではランダムに選ぶ．
// 主人公が「近い」距離にいる間だけ，主人公を狙う．食べられると目玉になって家へ戻り，すぐ復活して，また出てくる．
// 主人公は捕まっても2回まで復活できる（ゴーストから遠い場所に生まれる）．

public enum MKind { CookieEaten, PowerOn, PowerOff, GhostEaten, GhostBack, GhostOut, Died, Popup, Win, Lose }

public readonly record struct MEvent(MKind Kind, int X, int Y, int Index = 0, string? Text = null);

public enum GSt { House, Out, Eye }

public sealed class MGhost
{
    public int X, Y;
    public int Dir = -1;       // 進んでいる向き（0右 1左 2下 3上．まだ動いていなければ -1）
    public GSt St = GSt.House;
    public int ReleaseStep;    // 家にいる間：このブロック時間になったら出る
    public double Acc;
    public bool Immune;        // 食べられて復活した直後．次の大きいクッキーを食べるまでは，反撃中でもおびえない
    public bool IsOut => St == GSt.Out;
}

public sealed class MazeGame
{
    public const int PtsDot = 1, PtsPower = 5, PtsGhost = 20;

    public readonly Maze M;
    public GameState State = GameState.Ready;
    public int Steps, Points, PowerLeft, CookiesLeft;
    public int PX, PY, PDir;
    public int Lives, MaxLives;             // 残りの復活できる回数
    public int GateOpenUntil = -1;           // 家の入口が開いている（このブロック時間まで）
    public readonly List<MGhost> Ghosts = new();
    public readonly byte[] Cookie;        // 0=なし 1=通常 2=大きい．添字 = y*W + x

    private readonly Settings _c;
    private readonly IRng _rng;
    private readonly List<MEvent> _ev = new();
    private readonly int[] _home;          // 家の入口（外）までの道のり
    private int _awayUntil = -1;           // ゴーストを捕まえた直後，このブロック時間まで家から離れる
    private bool _houseSeen;              // 家の近くまで来たことがある（家の場所を覚えた）
    private readonly int[] _eyeHome;       // 同じ．ただしワープは通らない（目玉用）
    private int _powerCookies;
    private readonly double _pace0;

    public bool GateOpen => Steps <= GateOpenUntil;

    public MazeGame(Maze m, Settings cfg, IRng? rng = null, double paceStartSec = 0)
    {
        _pace0 = paceStartSec;
        M = m;
        _c = cfg;
        _rng = rng ?? new SystemRng();
        Cookie = new byte[m.W * m.H];
        _home = m.Distances(m.GateOut);
        _eyeHome = m.Distances(m.GateOut, warp: false);
        MaxLives = Lives = Math.Clamp(cfg.IdleRevives, 0, 2);

        var cor = m.Corridors;

        // 主人公：家の入口から遠いところ
        int maxHome = 0;
        foreach (int c in cor) maxHome = Math.Max(maxHome, _home[c] >= Maze.Far ? 0 : _home[c]);
        int need = Math.Min(14, maxHome);
        var starts = new List<int>();
        foreach (int c in cor)
            if (_home[c] < Maze.Far && _home[c] >= need) starts.Add(c);
        if (starts.Count == 0) starts.AddRange(cor);
        int p = starts[_rng.Next(starts.Count)];
        PX = p % m.W;
        PY = p / m.W;
        PDir = 0;

        // ゴースト：最初は全員家の中．順に出る
        int n = Math.Clamp(cfg.GhostCount, 1, 4);
        for (int i = 0; i < n; i++)
        {
            int hc = m.HouseCell(i);
            Ghosts.Add(new MGhost
            {
                X = hc % m.W,
                Y = hc / m.W,
                St = GSt.House,
                Dir = m.GateDir,
                ReleaseStep = cfg.IdleHouseFirst + i * cfg.IdleHouseGap,
            });
        }

        // クッキー：通路のほぼ全部（割合は設定）．大きいクッキーは互いに離す
        var cells = new List<int>();
        foreach (int c in cor)
            if (c != p) cells.Add(c);
        Shuffle(cells);
        int total = Math.Max(1, cells.Count * Math.Clamp(cfg.IdleCookiePct, 5, 100) / 100);
        int power = Math.Min(cfg.IdlePower, total);
        var powers = new List<int>();
        for (int spacing = 9; spacing >= 0 && powers.Count < power; spacing -= 3)
        {
            foreach (int c in cells)
            {
                if (powers.Count >= power) break;
                if (powers.Contains(c)) continue;
                bool ok = true;
                foreach (int o in powers)
                    if (Math.Abs(o % m.W - c % m.W) + Math.Abs(o / m.W - c / m.W) < spacing) ok = false;
                if (ok) powers.Add(c);
            }
        }
        int placed = 0;
        foreach (int c in powers)
        {
            Cookie[c] = 2;
            placed++;
        }
        foreach (int c in cells)
        {
            if (placed >= total) break;
            if (Cookie[c] != 0) continue;
            Cookie[c] = 1;
            placed++;
        }
        CookiesLeft = placed;
        _powerCookies = powers.Count;
    }

    private void Shuffle(List<int> a)
    {
        for (int i = a.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    // 主人公が速くなっている間（クッキー加速・反撃中の加速）は，1ブロックの時間が縮む．
    // ゴーストの実時間の速さは変わらないので，その分だけ1ブロックあたりに動く量を減らす
    private double _boost;     // 直前のブロックでの主人公の加速（0.05=5%）
    private double _ms;        // ここまでに経った実時間（ミリ秒）

    /// <summary>次のブロックまでの時間（ミリ秒）．外周モードと同じく，加速中は短くなる</summary>
    public double IntervalMs() => _c.PlayerMs / (1.0 + _boost);

    /// <summary>ゴーストの加速の基準になる時間（秒）．モードが切り替わっても引き継ぐ</summary>
    public double PaceSec => _pace0 + _ms / 1000.0;

    public IReadOnlyList<MEvent> BeginRun()
    {
        _ev.Clear();
        State = GameState.Run;
        return _ev;
    }

    public IReadOnlyList<MEvent> Step()
    {
        _ev.Clear();
        if (State != GameState.Run) return _ev;

        Steps++;
        _ms += IntervalMs();
        _boost = 0.0;
        MovePlayer();
        double gain = 0.0;
        int pi = PY * M.W + PX;
        if (Cookie[pi] != 0) gain = Eat(pi);
        if (CookiesLeft == 0)
        {
            State = GameState.Won;
            _ev.Add(new MEvent(MKind.Win, PX, PY));
            return _ev;
        }
        if (Collide()) return _ev;

        if (ReleaseGhosts() && Collide()) return _ev;
        if (MoveGhosts(gain)) return _ev;
        if (MoveEyes()) return _ev;

        if (PowerLeft > 0)
        {
            PowerLeft--;
            if (PowerLeft == 0) _ev.Add(new MEvent(MKind.PowerOff, PX, PY));
        }
        return _ev;
    }

    private double Eat(int pi)
    {
        byte kind = Cookie[pi];
        Cookie[pi] = 0;
        CookiesLeft--;
        _ev.Add(new MEvent(MKind.CookieEaten, PX, PY, kind));
        if (kind == 1)
        {
            Points += PtsDot;
            return _c.DotBoost;
        }
        _powerCookies--;
        foreach (var gg in Ghosts) gg.Immune = false;       // 次の大きいクッキーで，復活したゴーストもおびえる
        Points += PtsPower;
        _ev.Add(new MEvent(MKind.Popup, PX, PY, 0, "+" + PtsPower));
        if (PowerLeft == 0) _ev.Add(new MEvent(MKind.PowerOn, PX, PY));
        PowerLeft = _c.PowerSteps;
        return 0.0;
    }

    /// <summary>主人公とゴースト（目玉）が重なったときの処理．この1ブロックを打ち切るべきなら true</summary>
    private bool Collide()
    {
        for (int i = 0; i < Ghosts.Count; i++)
        {
            var g = Ghosts[i];
            if (g.X != PX || g.Y != PY) continue;
            if (g.St == GSt.Out)
            {
                if (IsScared(g))
                {
                    g.St = GSt.Eye;
                    g.Acc = 0;
                    Points += PtsGhost;
                    if (_c.IdleAwayBlocks > 0) _awayUntil = Steps + _c.IdleAwayBlocks;
                    _ev.Add(new MEvent(MKind.GhostEaten, PX, PY, i));
                    _ev.Add(new MEvent(MKind.Popup, PX, PY, 0, "+" + PtsGhost));
                }
                else
                {
                    return Caught();
                }
            }
            // 目玉には当たっても何も起きない（倒せない）
        }
        return false;
    }

    /// <summary>反撃中で，しかもおびえているゴースト（復活した直後のゴーストはおびえない）</summary>
    public bool IsScared(MGhost g) => g.St == GSt.Out && PowerLeft > 0 && !g.Immune;

    private bool Caught()
    {
        if (Lives > 0)
        {
            Lives--;
            PowerLeft = 0;
            Relocate();
            _ev.Add(new MEvent(MKind.Died, PX, PY, Lives));
            return true;
        }
        State = GameState.Lost;
        _ev.Add(new MEvent(MKind.Lose, PX, PY));
        return true;
    }

    /// <summary>ゴーストからいちばん遠いあたりに主人公を復活させる</summary>
    private void Relocate()
    {
        var src = new List<int>();
        bool anyHouse = false;
        foreach (var g in Ghosts)
        {
            if (g.St == GSt.Out) src.Add(g.Y * M.W + g.X);
            else if (g.St == GSt.House) anyHouse = true;
        }
        if (anyHouse) src.AddRange(M.GateOut);
        var cand = new List<int>();
        if (src.Count == 0)
        {
            cand.AddRange(M.Corridors);
        }
        else
        {
            var d = M.Distances(src);
            int max = 0;
            foreach (int c in M.Corridors)
                if (d[c] < Maze.Far) max = Math.Max(max, d[c]);
            int th = (int)(max * 0.8);
            foreach (int c in M.Corridors)
                if (d[c] < Maze.Far && d[c] >= th) cand.Add(c);
            if (cand.Count == 0) cand.AddRange(M.Corridors);
        }
        int p = cand[_rng.Next(cand.Count)];
        PX = p % M.W;
        PY = p / M.W;
        PDir = 0;
        if (Cookie[p] != 0) Eat(p);
    }

    // ================= 主人公の自動操作 =================
    private void MovePlayer()
    {
        int W = M.W;
        int here = PY * W + PX;
        bool powered = PowerLeft > 0;
        // 反撃が切れる直前は，おびえているゴーストも危ない相手として扱う
        bool scaredSafe = powered && PowerLeft > 2;
        int[] md = M.Distances(new[] { here });

        var threats = new List<int>();
        var prey = new List<int>();
        foreach (var g in Ghosts)
        {
            int gi = g.Y * W + g.X;
            if (g.St == GSt.Out)
            {
                bool harmless = scaredSafe && !g.Immune;
                if (!harmless) threats.Add(gi);
                else if (md[gi] <= 14) prey.Add(gi);      // 近くのゴーストだけ追う（遠くまで追うと時間がかかる）
            }
        }
        // 家の近くにいるおびえたゴーストは，できるだけ追わない（遠くで捕まえると，目玉が戻るまで時間がかかる）
        if (_c.IdleChaseHouseDist > 0 && prey.Count > 0)
        {
            var farPrey = prey.FindAll(p => _home[p] > _c.IdleChaseHouseDist);
            if (farPrey.Count > 0) prey = farPrey;
        }
        int[]? gd = threats.Count > 0 ? M.Distances(threats) : null;
        bool danger = gd != null && gd[here] <= _c.IdleNear + 1;

        var cookies = new List<int>();
        var powers = new List<int>();
        for (int i = 0; i < Cookie.Length; i++)
        {
            if (Cookie[i] == 0) continue;
            if (Cookie[i] == 2) powers.Add(i);
            else cookies.Add(i);
        }
        bool normalLeft = cookies.Count > 0;
        if (!normalLeft) cookies = powers;

        // ゴーストを捕まえた直後は，家から遠いクッキーを食べに行く（目玉が家に戻って復活するので，家の近くは危ない）
        bool away = Steps < _awayUntil;
        if (away && normalLeft)
        {
            int th = Math.Max(12, _c.IdleHouseRadius + 2);
            var far = cookies.FindAll(c => _home[c] >= th);
            if (far.Count > 0) cookies = far;
        }

        // 家の近くに来たら家の場所を覚えておき，ゴーストが近づくまでは，家の周りのクッキーを先に食べる
        if (!away && _c.IdleHouseRadius > 0)
        {
            if (_home[here] <= _c.IdleHouseRadius + 4) _houseSeen = true;
            bool ghostNear = gd != null && gd[here] <= _c.IdleNear * 2;
            int outCount = 0;
            foreach (var og in Ghosts) if (og.St == GSt.Out) outCount++;
            if (_houseSeen && !ghostNear && normalLeft && outCount <= _c.IdleHouseMaxOut)
            {
                var local = cookies.FindAll(c => _home[c] <= _c.IdleHouseRadius);
                if (local.Count > 0) cookies = local;
            }
        }

        // 入るときの手間：大きいクッキーはできるだけ避ける（反撃中は特に）
        var ec = new double[M.W * M.H];
        Array.Fill(ec, 1.0);
        if (powered || normalLeft)
        {
            double pen = powered ? 12.0 : 6.0;
            foreach (int c in powers) ec[c] += pen;
        }
        double[] cd = M.WeightedDistances(cookies, ec);
        double[]? sd = prey.Count > 0 ? M.WeightedDistances(prey, ec) : null;

        var moves = new List<(int d, int cell)>();
        for (int d = 0; d < 4; d++)
        {
            int n = M.Next(here, d);
            if (n >= 0) moves.Add((d, n));
        }
        if (moves.Count == 0) return;

        double[]? flee = danger ? FleeMap(gd!, powers, md) : null;
        (int d, int cell) best = moves[0];
        if (_rng.NextDouble() * 100 < _c.IdleMistakePct)
        {
            best = moves[_rng.Next(moves.Count)];      // うっかり
        }
        else
        {
            double bestScore = double.NegativeInfinity;
            foreach (var mv in moves)
            {
                int ni = mv.cell;
                double score = _rng.NextDouble() * 0.01;
                int dg = gd != null ? gd[ni] : Maze.Far;
                if (dg <= 1) score -= 1000;           // 次に捕まる
                else if (dg == 2) score -= 20;

                if (danger)
                {
                    score -= flee![ni] * 10.0;
                    score -= Math.Min(ec[ni] + cd[ni], 40) * 0.5;
                }
                else if (sd != null && !double.IsPositiveInfinity(sd[ni]))
                {
                    score -= Math.Min(ec[ni] + sd[ni], 1e4) * 20.0;      // おびえているゴースト（目玉）を追う
                }
                else
                {
                    score -= Math.Min(ec[ni] + cd[ni], 1e4) * 10.0;
                    if (gd != null) score += Math.Min(dg, 20) * 0.3;
                }
                if (mv.d == (PDir ^ 1) && !danger) score -= 3.0;   // 行ったり来たりしない
                if (score > bestScore)
                {
                    bestScore = score;
                    best = mv;
                }
            }
        }
        PDir = best.d;
        PX = best.cell % W;
        PY = best.cell / W;
    }

    /// <summary>
    /// 逃げ道の地図．ゴーストから遠いほど低く（良く），そこへ向かう道の途中も低くなる．
    /// 行き止まりに追い込まれず，輪になった道を回って逃げられる．
    /// 主人公のほうが先に着ける大きいクッキーは強い目標になる
    /// </summary>
    private double[] FleeMap(int[] gd, List<int> powers, int[] md)
    {
        int n = M.W * M.H;
        var s = new double[n];
        Array.Fill(s, double.PositiveInfinity);
        var pq = new PriorityQueue<int, double>();
        foreach (int c in M.Corridors)
        {
            s[c] = -1.2 * Math.Min(gd[c], 40);
            pq.Enqueue(c, s[c]);
        }
        foreach (int c in powers)
        {
            if (md[c] >= Maze.Far || gd[c] <= md[c] + 1) continue;    // ゴーストが先に着く
            s[c] -= 26.0 + Math.Max(0, 10 - md[c]) * 4.0;             // すぐそばの大きいクッキーは，遠くの安全な場所より強い目標にする
            pq.Enqueue(c, s[c]);
        }
        while (pq.TryDequeue(out int c, out double pr))
        {
            if (pr > s[c]) continue;
            for (int d = 0; d < 4; d++)
            {
                int ni = M.Next(c, d);
                if (ni < 0) continue;
                if (s[c] + 1 < s[ni])
                {
                    s[ni] = s[c] + 1;
                    pq.Enqueue(ni, s[ni]);
                }
            }
        }
        return s;
    }

    // ================= ゴーストの動き =================
    /// <summary>順番が来た家の中のゴーストを入口の外に出す．出したら true</summary>
    private bool ReleaseGhosts()
    {
        bool any = false;
        for (int i = 0; i < Ghosts.Count; i++)
        {
            var g = Ghosts[i];
            if (g.St != GSt.House || Steps < g.ReleaseStep) continue;
            int c = M.GateOut[_rng.Next(2)];
            g.X = c % M.W;
            g.Y = c / M.W;
            g.Dir = M.GateDir;
            g.St = GSt.Out;
            g.Acc = 0;
            GateOpenUntil = Steps + 2;
            _ev.Add(new MEvent(MKind.GhostOut, g.X, g.Y, i));
            any = true;
        }
        return any;
    }

    /// <summary>ゴーストを動かす．この1ブロックを打ち切るべきなら true</summary>
    private bool MoveGhosts(double gain)
    {
        bool powered = PowerLeft > 0;
        _boost = gain + (powered ? _c.PowerBoost : 0.0);
        double speedScared = Math.Min(_c.ScaredRatio / (1.0 + _boost), 1.99);
        double speedNormal = Math.Min(_c.GhostRatioAt(PaceSec) / (1.0 + _boost), 1.99);
        double near2 = (double)_c.IdleNear * _c.IdleNear;

        int[]?[] maps = new int[]?[Ghosts.Count];
        int[]? pd = null;
        for (int i = 0; i < Ghosts.Count; i++)
        {
            var g = Ghosts[i];
            if (g.St != GSt.Out || IsScared(g)) continue;
            double dx = g.X - PX, dy = g.Y - PY;
            if (dx * dx + dy * dy > near2) continue;       // 近くにいなければ狙わない
            pd ??= M.Distances(new[] { PY * M.W + PX });
            maps[i] = M.Distances(new[] { TargetFor(i, pd) });
        }

        for (int i = 0; i < Ghosts.Count; i++)
        {
            var g = Ghosts[i];
            if (g.St != GSt.Out) continue;
            g.Acc += IsScared(g) ? speedScared : speedNormal;
            int n = (int)g.Acc;
            g.Acc -= n;
            for (int k = 0; k < n; k++)
            {
                MoveGhostOne(g, maps[i]);
                if (Collide()) return true;
                if (g.St != GSt.Out) break;
            }
        }
        return false;
    }

    /// <summary>目玉は迷路をたどって家へ戻り，着いたらすぐ復活する</summary>
    private bool MoveEyes()
    {
        for (int i = 0; i < Ghosts.Count; i++)
        {
            var g = Ghosts[i];
            if (g.St != GSt.Eye) continue;
            g.Acc += _c.EyeRatio / (1.0 + _boost);
            int n = (int)g.Acc;
            g.Acc -= n;
            for (int k = 0; k < n; k++)
            {
                int c = g.Y * M.W + g.X;
                if (_home[c] == 0)
                {
                    int hc = M.HouseCell(i);
                    g.X = hc % M.W;
                    g.Y = hc / M.W;
                    g.St = GSt.House;
                    g.Dir = M.GateDir;
                    g.ReleaseStep = Steps + _c.IdleReviveWait;
                    g.Immune = true;
                    GateOpenUntil = Steps + 2;
                    _ev.Add(new MEvent(MKind.GhostBack, g.X, g.Y, i));
                    break;
                }
                // 目玉はワープを通らない．ワープなしでは家に戻れない場所にいるときだけ，例外的に通る
                bool viaWarp = _eyeHome[c] >= Maze.Far;
                var eh = viaWarp ? _home : _eyeHome;
                var opts = new List<(int d, int cell)>();
                for (int d = 0; d < 4; d++)
                {
                    int nc = M.Next(c, d, viaWarp);
                    if (nc >= 0 && eh[nc] < eh[c]) opts.Add((d, nc));
                }
                if (opts.Count == 0) break;
                var pick = opts[_rng.Next(opts.Count)];
                g.X = pick.cell % M.W;
                g.Y = pick.cell / M.W;
                g.Dir = pick.d;
                if (Collide()) return true;
                if (g.St != GSt.Eye) break;
            }
        }
        return false;
    }

    /// <summary>ゴースト i が狙う場所（近くにいる間だけ使う）</summary>
    private int TargetFor(int i, int[] pd)
    {
        int W = M.W;
        int player = PY * W + PX;
        if (!_c.IdlePersonality) return player;
        var g = Ghosts[i];
        switch (i % 4)
        {
            case 1:   // ピンク：主人公の4マス先に先回り
                return Ahead(4);
            case 2:   // 水色：赤と主人公の2マス先を結んだ，反対側へ挟み撃ち
            {
                int a = Ahead(2);
                var red = Ghosts[0];
                int rx = red.St == GSt.Out ? red.X : PX, ry = red.St == GSt.Out ? red.Y : PY;
                return M.NearestOpen(2 * (a % W) - rx, 2 * (a / W) - ry);
            }
            case 3:   // オレンジ：近づきすぎると隅に逃げる
                return pd[g.Y * W + g.X] <= 4 ? M.NearestOpen(0, M.H - 1) : player;
            default:
                return player;
        }
    }

    /// <summary>主人公の進行方向に k マス先（壁の手前まで）</summary>
    private int Ahead(int k)
    {
        int c = PY * M.W + PX;
        for (int i = 0; i < k; i++)
        {
            int n = M.Next(c, PDir);
            if (n < 0) break;
            c = n;
        }
        return c;
    }

    /// <summary>道を戻らない．分かれ道では，進める方向からランダムに選ぶ（狙っている間は目標に近い方向）</summary>
    private void MoveGhostOne(MGhost g, int[]? map)
    {
        int c = g.Y * M.W + g.X;
        int rev = g.Dir >= 0 ? g.Dir ^ 1 : -1;
        Span<int> dirs = stackalloc int[4];
        Span<int> cells = stackalloc int[4];
        int cnt = 0;
        for (int d = 0; d < 4; d++)
        {
            if (d == rev) continue;
            int n = M.Next(c, d);
            if (n < 0) continue;
            dirs[cnt] = d;
            cells[cnt] = n;
            cnt++;
        }
        if (cnt == 0)
        {
            if (rev < 0) return;
            int n = M.Next(c, rev);
            if (n < 0) return;
            dirs[0] = rev;
            cells[0] = n;
            cnt = 1;
        }

        int pick = 0;
        if (map != null)
        {
            int bestV = int.MaxValue, ties = 0;
            for (int k = 0; k < cnt; k++)
            {
                int v = map[cells[k]];
                if (v < bestV)
                {
                    bestV = v;
                    pick = k;
                    ties = 1;
                }
                else if (v == bestV && _rng.Next(++ties) == 0)
                {
                    pick = k;
                }
            }
        }
        else
        {
            pick = _rng.Next(cnt);
        }
        g.X = cells[pick] % M.W;
        g.Y = cells[pick] / M.W;
        g.Dir = dirs[pick];
    }
}
