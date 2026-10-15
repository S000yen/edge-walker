using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace EdgeWalker;

/// <summary>
/// 迷路の壁の絵．壁のかたまり（島）の輪郭を，二重の青い線（外側と内側）で描く．
/// 角は丸く，行き止まりの端も丸く閉じる．迷路ができるたびに作り直す
/// </summary>
internal sealed class MazeArt : IDisposable
{
    // 壁のかたまり（島）ごとに，外側と内側の線，それが入る範囲（迷路全体のピクセル座標）を持つ．
    // 見えている範囲に入るものだけ描くので，迷路が大きくても1回の描画は軽い
    private readonly List<(GraphicsPath outer, GraphicsPath inner, Rectangle box)> _islands = new();
    public readonly Pen Pen;
    public readonly Pen GatePen, WarpPen;
    public readonly List<(int x, int y)> WarpCells = new();
    private readonly Maze _m;
    private readonly int _tile;

    public MazeArt(Maze m, int tile)
    {
        _m = m;
        _tile = tile;
        Pen = new Pen(Color.FromArgb(255, 72, 104, 255), Math.Max(1.5f, tile / 16f)) { LineJoin = LineJoin.Round };
        GatePen = new Pen(Color.White, Math.Max(1.5f, tile / 14f)) { LineJoin = LineJoin.Round };
        WarpPen = new Pen(Color.FromArgb(255, 120, 220, 255), Math.Max(1.2f, tile / 20f));
        for (int x = 0; x < m.W; x++)
            for (int y = 0; y < m.H; y++)
                if (m.IsWarpCell(x, y)) WarpCells.Add((x, y));
        foreach (var loop in TraceLoops(m))
        {
            var outer = new GraphicsPath();
            var inner = new GraphicsPath();
            AddRounded(outer, loop, 0.10f * tile, 0.38f * tile, tile);
            AddRounded(inner, loop, 0.30f * tile, 0.16f * tile, tile);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var (x, y) in loop)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            var box = Rectangle.FromLTRB(minX * tile - tile, minY * tile - tile, maxX * tile + tile, maxY * tile + tile);
            _islands.Add((outer, inner, box));
        }
    }

    /// <summary>view（迷路全体のピクセル座標）にかかる壁だけを描く</summary>
    public void DrawWalls(Graphics g, Rectangle view)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (outer, inner, box) in _islands)
        {
            if (!box.IntersectsWith(view)) continue;
            g.DrawPath(Pen, outer);
            g.DrawPath(Pen, inner);
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    /// <summary>家の入口（白い壁）が view にかかるか</summary>
    public bool GateInView(Rectangle view)
    {
        int T = _tile;
        var box = new Rectangle((_m.HouseX - 1) * T, (_m.HouseY - 1) * T, 4 * T, 4 * T);
        return box.IntersectsWith(view);
    }

    /// <summary>壁のかたまりの輪郭（時計回り）．角の座標（マス単位）だけを返す</summary>
    private static List<List<(int x, int y)>> TraceLoops(Maze m)
    {
        var edges = new Dictionary<(int, int), List<(int, int)>>();
        void Add(int sx, int sy, int ex, int ey)
        {
            if (!edges.TryGetValue((sx, sy), out var l)) edges[(sx, sy)] = l = new List<(int, int)>();
            l.Add((ex, ey));
        }
        bool IsWall(int x, int y) => m.IsSolid(x, y);

        for (int y = 0; y < m.H; y++)
            for (int x = 0; x < m.W; x++)
            {
                if (!m.IsSolid(x, y)) continue;
                if (!IsWall(x, y - 1)) Add(x, y, x + 1, y);
                if (!IsWall(x + 1, y)) Add(x + 1, y, x + 1, y + 1);
                if (!IsWall(x, y + 1)) Add(x + 1, y + 1, x, y + 1);
                if (!IsWall(x - 1, y)) Add(x, y + 1, x, y);
            }

        var result = new List<List<(int x, int y)>>();
        while (true)
        {
            (int, int)? startKey = null;
            foreach (var kv in edges)
                if (kv.Value.Count > 0)
                {
                    startKey = kv.Key;
                    break;
                }
            if (startKey is null) break;

            var start = startKey.Value;
            var firstList = edges[start];
            var cur = firstList[^1];
            firstList.RemoveAt(firstList.Count - 1);
            var verts = new List<(int x, int y)> { start };
            var din = (dx: Math.Sign(cur.Item1 - start.Item1), dy: Math.Sign(cur.Item2 - start.Item2));
            int guard = 0;
            while (cur != start && guard++ < 1_000_000)
            {
                verts.Add(cur);
                if (!edges.TryGetValue(cur, out var list) || list.Count == 0) break;
                int pick = 0;
                int bestCross = int.MinValue;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    int dx = Math.Sign(e.Item1 - cur.Item1), dy = Math.Sign(e.Item2 - cur.Item2);
                    int cross = din.dx * dy - din.dy * dx;      // 大きいほど右に曲がる
                    if (cross > bestCross)
                    {
                        bestCross = cross;
                        pick = i;
                    }
                }
                var next = list[pick];
                list.RemoveAt(pick);
                din = (Math.Sign(next.Item1 - cur.Item1), Math.Sign(next.Item2 - cur.Item2));
                cur = next;
            }

            // まっすぐな途中の点は捨てて，角だけにする
            var corners = new List<(int x, int y)>();
            int n = verts.Count;
            for (int i = 0; i < n; i++)
            {
                var p = verts[(i + n - 1) % n];
                var c = verts[i];
                var q = verts[(i + 1) % n];
                int d1x = Math.Sign(c.x - p.x), d1y = Math.Sign(c.y - p.y);
                int d2x = Math.Sign(q.x - c.x), d2y = Math.Sign(q.y - c.y);
                if (d1x != d2x || d1y != d2y) corners.Add(c);
            }
            if (corners.Count >= 4) result.Add(corners);
        }
        return result;
    }

    /// <summary>輪郭を内側に inset だけ縮めて，角を半径 radius で丸めた線を path に足す</summary>
    private static void AddRounded(GraphicsPath path, List<(int x, int y)> loop, float inset, float radius, int tile)
    {
        int n = loop.Count;
        var q = new (float x, float y)[n];
        for (int i = 0; i < n; i++)
        {
            var p = loop[(i + n - 1) % n];
            var c = loop[i];
            var nx = loop[(i + 1) % n];
            int d1x = Math.Sign(c.x - p.x), d1y = Math.Sign(c.y - p.y);
            int d2x = Math.Sign(nx.x - c.x), d2y = Math.Sign(nx.y - c.y);
            // 内側（進行方向の右手）への法線の和
            float ox = (-d1y + -d2y) * inset;
            float oy = (d1x + d2x) * inset;
            q[i] = (c.x * tile + ox, c.y * tile + oy);
        }

        bool first = true;
        (float x, float y) lastB = (0, 0);
        for (int i = 0; i < n; i++)
        {
            var p = q[(i + n - 1) % n];
            var c = q[i];
            var nx = q[(i + 1) % n];
            float lp = Dist(p, c), ln = Dist(c, nx);
            float r = Math.Min(radius, Math.Min(lp, ln) / 2f);
            if (r < 0.5f) r = 0.5f;
            var a = Toward(c, p, r);
            var b = Toward(c, nx, r);
            if (first)
            {
                path.StartFigure();
                first = false;
            }
            else
            {
                path.AddLine(lastB.x, lastB.y, a.x, a.y);
            }
            path.AddBezier(a.x, a.y, c.x, c.y, c.x, c.y, b.x, b.y);
            lastB = b;
        }
        path.CloseFigure();
    }

    private static float Dist((float x, float y) a, (float x, float y) b)
        => (float)Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

    private static (float x, float y) Toward((float x, float y) from, (float x, float y) to, float len)
    {
        float d = Dist(from, to);
        if (d < 0.0001f) return from;
        return (from.x + (to.x - from.x) * len / d, from.y + (to.y - from.y) * len / d);
    }

    /// <summary>家の入口の白い壁．閉じているときは入口の全体，開いているときは両端だけ</summary>
    public void DrawGate(Graphics g, bool open)
    {
        int T = _tile;
        float pw = GatePen.Width;
        float thick = T * 0.28f;
        float bx = 0, by = 0;         // 入口の境目の線の中心
        bool horizontal;              // 壁が横長（上下の辺が入口）
        int hx = _m.HouseX, hy = _m.HouseY;
        switch (_m.GateDir)
        {
            case 0: bx = (hx + 2) * T; by = (hy + 1) * T; horizontal = false; break;
            case 1: bx = hx * T; by = (hy + 1) * T; horizontal = false; break;
            case 2: bx = (hx + 1) * T; by = (hy + 2) * T; horizontal = true; break;
            default: bx = (hx + 1) * T; by = hy * T; horizontal = true; break;
        }
        float len = 2 * T - pw;
        float x = horizontal ? bx - len / 2 : bx - thick / 2;
        float y = horizontal ? by - thick / 2 : by - len / 2;
        float w = horizontal ? len : thick, h = horizontal ? thick : len;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!open)
        {
            using var path = RoundedRect(x, y, w, h);
            g.DrawPath(GatePen, path);
        }
        else
        {
            float stub = T * 0.4f;
            for (int k = 0; k < 2; k++)
            {
                float sx = horizontal ? (k == 0 ? x : x + w - stub) : x;
                float sy = horizontal ? y : (k == 0 ? y : y + h - stub);
                using var path = RoundedRect(sx, sy, horizontal ? stub : w, horizontal ? h : stub);
                g.DrawPath(GatePen, path);
            }
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h)
    {
        float r = Math.Min(w, h) * 0.45f;
        var path = new GraphicsPath();
        path.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        path.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        path.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>ワープの出入口の印（二重の輪）</summary>
    public void DrawWarps(Graphics g, Func<int, int, bool> inView)
    {
        int T = _tile;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (x, y) in WarpCells)
        {
            if (!inView(x, y)) continue;
            float cx = x * T + T / 2f, cy = y * T + T / 2f;
            float r1 = T * 0.42f, r2 = T * 0.24f;
            g.DrawEllipse(WarpPen, cx - r1, cy - r1, 2 * r1, 2 * r1);
            g.DrawEllipse(WarpPen, cx - r2, cy - r2, 2 * r2, 2 * r2);
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    public void Dispose()
    {
        foreach (var (outer, inner, _) in _islands)
        {
            outer.Dispose();
            inner.Dispose();
        }
        _islands.Clear();
        Pen.Dispose();
        GatePen.Dispose();
        WarpPen.Dispose();
    }
}

/// <summary>
/// キャラの周囲だけが見える小さな透明ウィンドウ（端に向かって薄くなる）．キャラと一緒に動く．
/// 画面全体を覆うウィンドウより軽い
/// </summary>
internal sealed class VisionWin : IDisposable
{
    public readonly Overlay Ov;
    private readonly int _r, _tile, _size;
    private readonly byte[] _mask;
    private readonly int[] _edge;      // マスクが255でない場所（薄くする場所）の番号

    public VisionWin(int radius, int tile, int screenX, int screenY)
    {
        _r = radius;
        _tile = tile;
        _size = (2 * radius + 1) * tile;
        Ov = new Overlay(new Rectangle(screenX, screenY, _size, _size));
        Ov.Show();

        _mask = new byte[_size * _size];
        for (int py = 0; py < _size; py++)
            for (int px = 0; px < _size; px++)
            {
                double dx = (px + 0.5) / tile - (radius + 0.5);
                double dy = (py + 0.5) / tile - (radius + 0.5);
                double d = Math.Sqrt(dx * dx + dy * dy);
                double f = Math.Clamp((radius + 0.3 - d) / 1.8, 0.0, 1.0);
                _mask[py * _size + px] = (byte)Math.Round(f * 255);
            }
        var edge = new List<int>();
        for (int i = 0; i < _mask.Length; i++)
            if (_mask[i] != 255) edge.Add(i);
        _edge = edge.ToArray();
    }

    /// <summary>中心のマスが (cx, cy) のとき，この窓の左上（迷路全体を T 倍した座標）</summary>
    public (int x, int y) Origin(int cx, int cy) => ((cx - _r) * _tile, (cy - _r) * _tile);

    /// <summary>描いて，端を薄くして，画面の (screenX, screenY) に出す．draw には迷路全体の座標系で描く</summary>
    public void Render(int cx, int cy, int screenX, int screenY, Action<Graphics> draw)
    {
        var (ox, oy) = Origin(cx, cy);
        using (var g = Ov.BeginDraw())
        {
            g.TranslateTransform(-ox, -oy);
            draw(g);
        }
        ApplyMask();
        Ov.PresentAt(screenX, screenY);
    }

    /// <summary>端に向かって薄くする．ビットマップのメモリを直接書き換える（コピーしない）．
    /// マスクが255（そのまま）の場所は飛ばし，薄くする場所の番号だけを最初に1回作って使い回す</summary>
    private unsafe void ApplyMask()
    {
        var bmp = Ov.Surface;
        if (bmp.Width != _size || bmp.Height != _size) return;
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            byte* basePtr = (byte*)bd.Scan0;
            int stride = bd.Stride;
            foreach (int idx in _edge)
            {
                int y = idx / _size, x = idx - y * _size;
                byte* p = basePtr + y * stride + x * 4;
                if (p[3] == 0) continue;
                int f = _mask[idx];
                if (f == 0)
                {
                    p[0] = p[1] = p[2] = p[3] = 0;
                    continue;
                }
                p[0] = (byte)(p[0] * f / 255);
                p[1] = (byte)(p[1] * f / 255);
                p[2] = (byte)(p[2] * f / 255);
                p[3] = (byte)(p[3] * f / 255);
            }
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
    }

    public void Dispose() => Ov.Dispose();
}
