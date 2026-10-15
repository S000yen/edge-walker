using System.Drawing;

namespace EdgeWalker;

/// <summary>
/// 作業領域のふちを一周する1マス幅の通路．位置は輪の上の番号(0..L-1)で表す．
/// 画面全体は覆わず，上・右・下・左の細い帯4枚で表示する．
/// </summary>
internal sealed class Layout
{
    public readonly int T;          // 1マスのピクセル数
    public readonly int Cols, Rows;
    public readonly int Ox, Oy;     // 通路全体の左上
    public readonly int L;          // 外周のマス数
    public readonly Point[] Path;   // 番号 -> (列, 行)．時計回り
    private readonly int[] _strip;

    public Layout(Rectangle area, int cellsShort)
    {
        int w = area.Width, h = area.Height;
        T = Math.Max(8, Math.Min(w, h) / Math.Max(1, cellsShort));
        Cols = w / T;
        Rows = h / T;
        Ox = area.Left + (w - Cols * T) / 2;
        Oy = area.Top + (h - Rows * T) / 2;

        var p = new List<Point>();
        for (int x = 0; x < Cols; x++) p.Add(new Point(x, 0));
        for (int y = 1; y < Rows; y++) p.Add(new Point(Cols - 1, y));
        for (int x = Cols - 2; x >= 0; x--) p.Add(new Point(x, Rows - 1));
        for (int y = Rows - 2; y > 0; y--) p.Add(new Point(0, y));
        Path = p.ToArray();
        L = Path.Length;

        _strip = new int[L];
        for (int s = 0; s < L; s++)
        {
            var c = Path[s];
            _strip[s] = c.Y == 0 ? 0 : c.Y == Rows - 1 ? 2 : c.X == Cols - 1 ? 1 : 3;   // 上・右・下・左
        }
    }

    public int StripOf(int s) => _strip[s];

    public Rectangle StripRect(int i) => i switch
    {
        0 => new Rectangle(Ox, Oy, Cols * T, T),
        2 => new Rectangle(Ox, Oy + (Rows - 1) * T, Cols * T, T),
        1 => new Rectangle(Ox + (Cols - 1) * T, Oy + T, T, (Rows - 2) * T),
        _ => new Rectangle(Ox, Oy + T, T, (Rows - 2) * T),
    };

    /// <summary>帯の中での、そのマスの左上</summary>
    public Point LocalTopLeft(int s)
    {
        var c = Path[s];
        return _strip[s] is 0 or 2 ? new Point(c.X * T, 0) : new Point(0, (c.Y - 1) * T);
    }

    /// <summary>画面上での、そのマスの左上</summary>
    public Point ScreenTopLeft(int s)
    {
        var c = Path[s];
        return new Point(Ox + c.X * T, Oy + c.Y * T);
    }

    /// <summary>進行方向 dir (+1/-1) に向かうときの向き (dx, dy)</summary>
    public (int dx, int dy) Heading(int s, int dir)
    {
        var a = Path[s];
        var b = Path[((s + dir) % L + L) % L];
        return (b.X - a.X, b.Y - a.Y);
    }
}
