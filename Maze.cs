namespace EdgeWalker;

// アイドルモード用の迷路（画面に依存しない）．
// 外周は1マス幅の通路で，その内側に「壁のかたまり（島）」を並べる．島どうしは必ず1マス以上あける．
// 行き止まりはなくし，通路は全部つながるようにする．
// ゴーストの家（2x2）を1つ置く．家のまわりは壁で囲み，入口は1辺（2マス幅）だけ．主人公は入れない．
// 画面の左右・上下の端どうしをつなぐワープを1組ずつ置く．

public sealed class Maze
{
    public const int Far = 1_000_000;

    // 向きの番号: 0=右, 1=左, 2=下, 3=上（Sprites.DirIndex と同じ）
    public static readonly int[] Dx = { 1, -1, 0, 0 };
    public static readonly int[] Dy = { 0, 0, 1, -1 };

    public readonly int W, H;
    public readonly bool[] Wall;                       // 添字 = y*W + x
    public readonly List<int> Corridors = new();       // 通路のマス
    public readonly bool[] Notch;                      // 左下の凹み（残りの命を出す2マス）．壁だが絵は描かない
    public readonly bool[] House;                      // ゴーストの家の中（2x2）．Wall でもある
    public int HouseX, HouseY;                         // 家の中の左上
    public int GateDir;                                // 家から入口の外へ向かう向き（0右 1左 2下 3上）
    public readonly int[] GateOut = new int[2];        // 入口のすぐ外の通路のマス（2マス）
    public int WarpRow = -1, WarpCol = -1;             // 左右ワープの行，上下ワープの列

    private Maze(int w, int h)
    {
        W = w;
        H = h;
        Wall = new bool[w * h];
        House = new bool[w * h];
        Notch = new bool[w * h];
    }

    public int Idx(int x, int y) => y * W + x;
    public bool InRange(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H;
    public bool IsOpen(int x, int y) => InRange(x, y) && !Wall[y * W + x];

    /// <summary>絵にするときの壁（家の中は壁にしない）</summary>
    public bool IsSolid(int x, int y) => InRange(x, y) && Wall[y * W + x] && !House[y * W + x] && !Notch[y * W + x];

    /// <summary>マス c から向き d へ1マス進んだ先（ワープを含む）．進めなければ -1</summary>
    public int Next(int c, int d)
    {
        int x = c % W, y = c / W, nx = x + Dx[d], ny = y + Dy[d];
        if (d == 1 && x == 0 && y == WarpRow) nx = W - 1;
        else if (d == 0 && x == W - 1 && y == WarpRow) nx = 0;
        else if (d == 3 && y == 0 && x == WarpCol) ny = H - 1;
        else if (d == 2 && y == H - 1 && x == WarpCol) ny = 0;
        if (!IsOpen(nx, ny)) return -1;
        return ny * W + nx;
    }

    public bool IsWarpCell(int x, int y)
        => (y == WarpRow && (x == 0 || x == W - 1)) || (x == WarpCol && (y == 0 || y == H - 1));

    /// <summary>家の中の4マスのうち slot 番目（0..3）</summary>
    public int HouseCell(int slot) => (HouseY + (slot >> 1)) * W + HouseX + (slot & 1);

    public static Maze Generate(int w, int h, IRng rng)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var m = TryGenerate(w, h, rng);
            if (m != null) return m;
        }
        throw new InvalidOperationException("迷路を作れませんでした");
    }

    private static Maze? TryGenerate(int w, int h, IRng rng)
    {
        var m = new Maze(w, h);
        var mark = new bool[w * h];

        // 左下の凹み：下辺の左端の2マスは通れない（ここに残りの命を出す）．外周は1つ内側の行を回り道する
        for (int nx = 0; nx < 2; nx++)
        {
            m.Wall[(h - 1) * w + nx] = true;
            m.Notch[(h - 1) * w + nx] = true;
        }

        // 家：まわりを含めて 4x4．入口の辺の2マス（ring のうち真ん中）だけ壁にしない．凹みの近くには置かない
        do
        {
            m.HouseX = 2 + rng.Next(Math.Max(1, w - 5));
            m.HouseY = 2 + rng.Next(Math.Max(1, h - 5));
        } while (m.HouseX <= 5 && m.HouseY + 3 >= h - 2);
        m.GateDir = rng.Next(4);
        int hx = m.HouseX, hy = m.HouseY;
        for (int y = hy - 1; y <= hy + 2; y++)
            for (int x = hx - 1; x <= hx + 2; x++)
            {
                bool inside = x >= hx && x <= hx + 1 && y >= hy && y <= hy + 1;
                m.Wall[y * w + x] = true;
                if (inside) m.House[y * w + x] = true;
            }
        switch (m.GateDir)
        {
            case 0: m.GateOut[0] = hy * w + hx + 2; m.GateOut[1] = (hy + 1) * w + hx + 2; break;
            case 1: m.GateOut[0] = hy * w + hx - 1; m.GateOut[1] = (hy + 1) * w + hx - 1; break;
            case 2: m.GateOut[0] = (hy + 2) * w + hx; m.GateOut[1] = (hy + 2) * w + hx + 1; break;
            default: m.GateOut[0] = (hy - 1) * w + hx; m.GateOut[1] = (hy - 1) * w + hx + 1; break;
        }
        foreach (int g in m.GateOut) m.Wall[g] = false;

        int attempts = w * h * 6;
        for (int a = 0; a < attempts; a++)
        {
            var cells = RandomShape(w, h, rng);
            if (!Fits(m, cells, mark)) continue;
            foreach (var (x, y) in cells) m.Wall[y * w + x] = true;
        }

        m.WarpRow = 1 + rng.Next(Math.Max(1, h - 2));
        m.WarpCol = 2 + rng.Next(Math.Max(1, w - 3));      // 凹みの列（0,1）は避ける

        // 行き止まり（つながっている通路が1つ以下）は壁にする．連鎖するので繰り返す
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int c = y * w + x;
                    if (m.Wall[c] || c == m.GateOut[0] || c == m.GateOut[1]) continue;
                    int open = 0;
                    for (int d = 0; d < 4; d++)
                        if (m.Next(c, d) >= 0) open++;
                    if (open <= 1)
                    {
                        m.Wall[c] = true;
                        changed = true;
                    }
                }
        }

        // 左上から行けない通路は壁にする
        var seen = m.Distances(new[] { 0 });
        for (int i = 0; i < w * h; i++)
        {
            if (!m.Wall[i] && seen[i] >= Far) m.Wall[i] = true;
            if (!m.Wall[i]) m.Corridors.Add(i);
        }

        // 入口の外が通れて，行き止まりがないことを確かめる
        foreach (int g in m.GateOut)
        {
            if (m.Wall[g]) return null;
            int open = 0;
            for (int d = 0; d < 4; d++)
                if (m.Next(g, d) >= 0) open++;
            if (open < 2) return null;
        }
        return m;
    }

    private static List<(int x, int y)> RandomShape(int w, int h, IRng rng)
    {
        int bw = 1 + rng.Next(4), bh = 1 + rng.Next(3);
        if (rng.Next(2) == 0) (bw, bh) = (bh, bw);
        int x0 = 1 + rng.Next(Math.Max(1, w - 2));
        int y0 = 1 + rng.Next(Math.Max(1, h - 2));
        var cells = new List<(int x, int y)>();

        void Rect(int x, int y, int rw, int rh)
        {
            for (int yy = y; yy < y + rh; yy++)
                for (int xx = x; xx < x + rw; xx++)
                    if (!cells.Contains((xx, yy))) cells.Add((xx, yy));
        }

        Rect(x0, y0, bw, bh);
        if (rng.Next(100) < 40)
        {
            // L字・T字になるように，もう1つの長方形を辺にくっつける
            int w2 = 1 + rng.Next(3), h2 = 1 + rng.Next(3);
            switch (rng.Next(4))
            {
                case 0: Rect(x0 + bw, y0 - (h2 - 1) + rng.Next(bh + h2 - 1), w2, h2); break;
                case 1: Rect(x0 - w2, y0 - (h2 - 1) + rng.Next(bh + h2 - 1), w2, h2); break;
                case 2: Rect(x0 - (w2 - 1) + rng.Next(bw + w2 - 1), y0 + bh, w2, h2); break;
                default: Rect(x0 - (w2 - 1) + rng.Next(bw + w2 - 1), y0 - h2, w2, h2); break;
            }
        }
        return cells;
    }

    /// <summary>外周を空け，他の島とは斜めを含めて1マス以上あける</summary>
    private static bool Fits(Maze m, List<(int x, int y)> cells, bool[] mark)
    {
        int w = m.W, h = m.H;
        foreach (var (x, y) in cells)
        {
            if (x < 1 || x > w - 2 || y < 1 || y > h - 2) return false;
            mark[y * w + x] = true;
        }
        bool ok = true;
        foreach (var (x, y) in cells)
        {
            for (int dy = -1; dy <= 1 && ok; dy++)
                for (int dx = -1; dx <= 1 && ok; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!m.InRange(nx, ny)) continue;
                    int i = ny * w + nx;
                    if (m.Wall[i] && !mark[i]) ok = false;
                    if (dx == 0 && dy == 0 && m.Wall[i]) ok = false;
                }
            if (!ok) break;
        }
        foreach (var (x, y) in cells) mark[y * w + x] = false;
        return ok;
    }

    /// <summary>複数の出発点からの最短歩数（幅優先，ワープを含む）．行けないマスは Far</summary>
    public int[] Distances(IEnumerable<int> sources)
    {
        var dist = new int[W * H];
        Array.Fill(dist, Far);
        var queue = new int[W * H];
        int head = 0, tail = 0;
        foreach (int s in sources)
        {
            if (s < 0 || s >= dist.Length || Wall[s] || dist[s] == 0) continue;
            dist[s] = 0;
            queue[tail++] = s;
        }
        while (head < tail)
        {
            int c = queue[head++];
            for (int d = 0; d < 4; d++)
            {
                int n = Next(c, d);
                if (n < 0 || dist[n] != Far) continue;
                dist[n] = dist[c] + 1;
                queue[tail++] = n;
            }
        }
        return dist;
    }

    /// <summary>
    /// 入るのに追加の手間（enterCost[c]，1以上）がかかるマスを含めた，出発点からの道のり（ダイクストラ）．
    /// 結果の dist[c] は「c から出発点まで行くのにかかる手間」で，c 自身に入る手間は含まない
    /// </summary>
    public double[] WeightedDistances(IEnumerable<int> sources, double[] enterCost)
    {
        var dist = new double[W * H];
        Array.Fill(dist, double.PositiveInfinity);
        var pq = new PriorityQueue<int, double>();
        foreach (int s in sources)
        {
            if (s < 0 || s >= dist.Length || Wall[s]) continue;
            dist[s] = 0;
            pq.Enqueue(s, 0);
        }
        while (pq.TryDequeue(out int c, out double pr))
        {
            if (pr > dist[c]) continue;
            for (int d = 0; d < 4; d++)
            {
                int n = Next(c, d);
                if (n < 0) continue;
                double nd = dist[c] + enterCost[c];
                if (nd < dist[n])
                {
                    dist[n] = nd;
                    pq.Enqueue(n, nd);
                }
            }
        }
        return dist;
    }

    /// <summary>指定の座標にいちばん近い通路のマス</summary>
    public int NearestOpen(int x, int y)
    {
        x = Math.Clamp(x, 0, W - 1);
        y = Math.Clamp(y, 0, H - 1);
        if (!Wall[y * W + x]) return y * W + x;
        for (int r = 1; r < W + H; r++)
        {
            for (int yy = y - r; yy <= y + r; yy++)
                for (int xx = x - r; xx <= x + r; xx++)
                {
                    if (Math.Max(Math.Abs(xx - x), Math.Abs(yy - y)) != r) continue;
                    if (IsOpen(xx, yy)) return yy * W + xx;
                }
        }
        return Corridors.Count > 0 ? Corridors[0] : 0;
    }
}
