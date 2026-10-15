namespace EdgeWalker;

// ゲームのルール部分（画面に依存しない）．walker_game.py の移植．
//
// 外周は一本の輪（長さ L）として扱い，位置はすべて 0..L-1 の整数で表す．
// 時間の単位は「ブロック時間」＝主人公が1マス進む時間．Step() を1回呼ぶ＝主人公が1マス進む，で，
// ゴーストや各種タイマーもその回数を基準にする．実時間の間隔は IntervalMs() が返す．

public interface IRng
{
    double NextDouble();
    int Next(int maxExclusive);
}

public sealed class SystemRng : IRng
{
    private readonly Random _r = new();
    public double NextDouble() => _r.NextDouble();
    public int Next(int maxExclusive) => _r.Next(maxExclusive);
}

public enum GameState { Ready, Run, Won, Lost }

public enum CookieKind { Dot, Power }

public abstract record GameEvent;
public sealed record CookieNew(int Pos, CookieKind Kind) : GameEvent;
public sealed record CookieEaten(int Pos, CookieKind Kind) : GameEvent;
public sealed record CookiesCleared : GameEvent;
public sealed record Popup(int Pos, string Text) : GameEvent;
public sealed record GhostEaten(int Index, int Pos) : GameEvent;
public sealed record PowerOn : GameEvent;
public sealed record PowerOff : GameEvent;
public sealed record Win : GameEvent;
public sealed record Lose : GameEvent;

public sealed class Ghost
{
    public int S;
    public bool Alive = true;
    public double Acc;
    public bool Active = true;     // まだ外周に着いていない間は false
    public int Color;              // 色の番号（0赤 1ピンク 2水色 3オレンジ）
}

public sealed class Game
{
    public const int PtsDot = 1, PtsPower = 5, PtsGhost = 20;

    public readonly int L;
    public GameState State = GameState.Ready;
    public int Steps;
    public int Points;
    public int PowerLeft;
    public int Pos;
    public bool PlayerActive = true;   // まだ外周に着いていない間は false
    public int Dir;          // 主人公（と全員）の進行方向．+1 / -1
    public int Wall;         // 開始前の仕切り（主人公側，ゴースト範囲の手前）の位置
    public int Wall2;        // 開始前の仕切り（ゴースト範囲の奥）の位置
    public readonly List<Ghost> Ghosts = new();
    public readonly Dictionary<int, CookieKind> Cookies = new();

    private readonly Settings _c;
    private readonly IRng _rng;
    private double _pace0;
    private readonly Dictionary<int, int> _visited = new();
    private readonly List<GameEvent> _ev = new();   // Step() が返す一覧（使い回す）

    public Game(int ringLength, Settings cfg, IRng? rng = null)
    {
        L = ringLength;
        _c = cfg;
        _rng = rng ?? new SystemRng();

        int n = cfg.GhostCount;
        int lo = Math.Max(4, cfg.SpawnMin);
        int hi = Math.Min(Math.Max(cfg.SpawnMax, lo + n - 1), L / 3);
        lo = Math.Min(lo, hi - n + 1);

        Pos = _rng.Next(L);
        int side = _rng.Next(2) == 0 ? 1 : -1;      // ゴーストがいる側
        Dir = -side;                                 // 主人公はゴーストから逃げる向き

        // 初期距離 lo..hi から，互いに gap マス以上あけて n 個（入り切らない時は間隔を詰める）
        int gap = Math.Max(1, _c.SpawnGap);
        if (n > 1) gap = Math.Min(gap, (hi - lo) / (n - 1));
        gap = Math.Max(1, gap);
        int span = hi - lo + 1 - (n - 1) * (gap - 1);
        var pool = new int[span];
        for (int i = 0; i < pool.Length; i++) pool[i] = lo + i;
        for (int i = 0; i < n; i++)
        {
            int j = i + _rng.Next(pool.Length - i);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        var dist = new int[n];
        Array.Copy(pool, dist, n);
        Array.Sort(dist);
        for (int i = 0; i < n; i++) dist[i] += i * (gap - 1);
        for (int i = n - 1; i > 0; i--)     // どの色がどの位置かはランダム
        {
            int j = _rng.Next(i + 1);
            (dist[i], dist[j]) = (dist[j], dist[i]);
        }
        for (int i = 0; i < n; i++)
            Ghosts.Add(new Ghost { S = Mod(Pos + side * dist[i]), Color = i % 4 });

        // ゴーストの出現範囲（lo..hi）を両端から囲む
        Wall = Mod(Pos + side * (lo - 1));
        Wall2 = Mod(Pos + side * (hi + 1));
        _visited[Pos] = 0;
    }

    /// <summary>
    /// アイドルモードから戻ってきた状態で始める（ゲームは動いていて，各キャラは外周に着いた順に参加する）．
    /// 主人公の進む向きは，いちばん近いゴーストから逃げる向き．
    /// </summary>
    public Game(int ringLength, Settings cfg, IRng rng, int playerPos, IReadOnlyList<int> ghostPos, IReadOnlyList<int>? colors = null, double paceStartSec = 0)
    {
        _pace0 = paceStartSec;
        L = ringLength;
        _c = cfg;
        _rng = rng;
        Pos = Mod(playerPos);
        PlayerActive = false;
        int best = int.MaxValue, sign = 0;
        for (int gi = 0; gi < ghostPos.Count; gi++)
        {
            int p = ghostPos[gi];
            Ghosts.Add(new Ghost { S = Mod(p), Active = false, Color = colors != null ? colors[gi] % 4 : gi % 4 });
            int off = Mod(p - Pos);
            if (off > L / 2) off -= L;
            if (Math.Abs(off) < best)
            {
                best = Math.Abs(off);
                sign = Math.Sign(off);
            }
        }
        if (sign == 0) sign = _rng.Next(2) == 0 ? 1 : -1;
        Dir = -sign;
        Wall = Wall2 = -1;
        _visited[Pos] = 0;
        State = GameState.Run;
    }

    /// <summary>主人公が外周に着いた．先のクッキーを決める</summary>
    public IReadOnlyList<GameEvent> ActivatePlayer()
    {
        _ev.Clear();
        PlayerActive = true;
        _visited[Pos] = Steps;
        for (int k = 1; k <= _c.Ahead; k++)
            RollCookie(Mod(Pos + Dir * k));
        return _ev;
    }

    /// <summary>ゴーストが外周に着いた</summary>
    public void ActivateGhost(int index)
    {
        Ghosts[index].Active = true;
        Ghosts[index].Acc = 0.0;
    }

    private int Mod(int v) => ((v % L) + L) % L;

    // 主人公が速くなっている間（クッキー加速・反撃中の加速）は，1ブロックの時間が縮む．
    // ゴーストの実時間の速さは変わらないので，その分だけ1ブロックあたりに動く量を減らす
    private double _boost;     // 直前のブロックでの主人公の加速（0.05=5%）
    private double _ms;        // ここまでに経った実時間（ミリ秒）

    /// <summary>次のブロックまでの時間（ミリ秒）．加速中は短くなる</summary>
    public double IntervalMs() => _c.PlayerMs / (1.0 + _boost);

    /// <summary>ゴーストの加速の基準になる時間（秒）．モードが切り替わっても引き継ぐ</summary>
    public double PaceSec => _pace0 + _ms / 1000.0;

    /// <summary>「start!」のあとに呼ぶ．先のクッキーを最初に決める</summary>
    public IReadOnlyList<GameEvent> BeginRun()
    {
        _ev.Clear();
        State = GameState.Run;
        for (int k = 1; k <= _c.Ahead; k++)
            RollCookie(Mod(Pos + Dir * k));
        return _ev;
    }

    /// <summary>主人公が1マス進む</summary>
    public IReadOnlyList<GameEvent> Step()
    {
        _ev.Clear();
        if (State != GameState.Run) return _ev;

        Steps++;
        _ms += IntervalMs();
        _boost = 0.0;
        double gain = 0.0;
        if (PlayerActive)
        {
            Pos = Mod(Pos + Dir);
            _visited[Pos] = Steps;
            if (Cookies.Remove(Pos, out var kind))
                gain = EatCookie(kind);
        }
        Collide();
        if (State != GameState.Run) return _ev;

        MoveGhosts(gain);
        if (State != GameState.Run) return _ev;

        TickPower();
        if (PlayerActive) RollCookie(Mod(Pos + Dir * _c.Ahead));
        return _ev;
    }

    // ---------- 内部 ----------
    private void RollCookie(int s)
    {
        if (Cookies.ContainsKey(s)) return;
        int last = _visited.TryGetValue(s, out var v) ? v : -1_000_000_000;
        if (Steps - last <= _c.NoCookieHistory) return;      // 最近通った場所には出さない
        foreach (var g in Ghosts)
            if (g.Alive && g.S == s) return;

        double x = _rng.NextDouble();
        CookieKind kind;
        if (x < _c.PPower) kind = PowerLeft > 0 ? CookieKind.Dot : CookieKind.Power;
        else if (x < _c.PPower + _c.PDot) kind = CookieKind.Dot;
        else return;
        Cookies[s] = kind;
        _ev.Add(new CookieNew(s, kind));
    }

    /// <summary>全員が逆走する．出ていたクッキーは消える</summary>
    private void Flip()
    {
        Dir = -Dir;
        if (Cookies.Count > 0)
        {
            Cookies.Clear();
            _ev.Add(new CookiesCleared());
        }
    }

    private double EatCookie(CookieKind kind)
    {
        _ev.Add(new CookieEaten(Pos, kind));
        if (kind == CookieKind.Dot)
        {
            Points += PtsDot;
            return _c.DotBoost;
        }
        Points += PtsPower;
        _ev.Add(new Popup(Pos, "+" + PtsPower));
        if (PowerLeft == 0)
        {
            Flip();
            _ev.Add(new PowerOn());
        }
        PowerLeft = _c.PowerSteps;
        return 0.0;
    }

    private void Collide()
    {
        for (int i = 0; i < Ghosts.Count; i++)
        {
            var g = Ghosts[i];
            if (!g.Alive || !g.Active || !PlayerActive || g.S != Pos) continue;
            if (PowerLeft > 0)
            {
                g.Alive = false;
                Points += PtsGhost;
                _ev.Add(new GhostEaten(i, Pos));
                _ev.Add(new Popup(Pos, "+" + PtsGhost));
            }
            else
            {
                State = GameState.Lost;
                _ev.Add(new Lose());
                return;
            }
        }
        bool anyAlive = false;
        foreach (var g in Ghosts) anyAlive |= g.Alive;
        if (!anyAlive)
        {
            State = GameState.Won;
            _ev.Add(new Win());
        }
    }

    private void MoveGhosts(double gain)
    {
        double ratio = PowerLeft > 0 ? _c.ScaredRatio : _c.GhostRatioAt(PaceSec);
        // ゴーストの速さは主人公の2倍未満（1ブロック時間に進むのは最大でも2マス弱）
        _boost = gain + (PowerLeft > 0 ? _c.PowerBoost : 0.0);
        double speed = Math.Min(ratio / (1.0 + _boost), 1.99);
        foreach (var g in Ghosts)
        {
            if (!g.Alive || !g.Active) continue;
            g.Acc += speed;
            int n = (int)g.Acc;
            g.Acc -= n;
            for (int k = 0; k < n; k++)
            {
                g.S = Mod(g.S + Dir);
                Collide();
                if (State != GameState.Run) return;
            }
        }
    }

    private void TickPower()
    {
        if (PowerLeft <= 0) return;
        PowerLeft--;
        if (PowerLeft == 0)
        {
            Flip();
            _ev.Add(new PowerOff());
        }
    }
}
