using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EdgeWalker;

/// <summary>
/// 画像の読み込みと，マスの大きさへの拡大（ドットがにじまない拡大）．
/// 画像は正方形ならサイズは自由．exeの隣の assets フォルダにあればそちらを優先する．
/// 向きごとの画像はすべて用意されたものをそのまま使う（回転・反転はしない）．
/// </summary>
internal sealed class Sprites : IDisposable
{
    // 向きの番号: 0=右, 1=左, 2=下, 3=上
    public static readonly string[] DirNames = { "right", "left", "down", "up" };
    public static readonly string[] ColorNames = { "red", "pink", "cyan", "orange" };
    public static int DirIndex(int dx, int dy) => dx > 0 ? 0 : dx < 0 ? 1 : dy > 0 ? 2 : 3;

    public readonly Bitmap[,] Player = new Bitmap[3, 4];       // [口の形(0開,1半開,2閉), 向き]．閉じた球体は全向きで共通
    public readonly Bitmap[,,] Enemy = new Bitmap[4, 4, 2];    // [色, 向き, 足]
    public readonly Bitmap[,] Scared = new Bitmap[2, 2];       // [0=青 1=白, 足]
    public readonly Bitmap[] Eggs = new Bitmap[11];            // 誕生の11段階（0=ミニボム）
    public readonly Bitmap[] Eyes = new Bitmap[4];             // 目玉（向き）．ゴーストの絵の4色共通部分から作る

    private readonly List<Bitmap> _all = new();

    public static Sprites Load(int tile)
    {
        var sp = new Sprites();
        var closed = sp.Track(Scaled(LoadRaw("player_closed.png"), tile));
        for (int d = 0; d < 4; d++)
        {
            for (int f = 0; f < 2; f++)
                sp.Player[f, d] = sp.Track(Scaled(LoadRaw($"player_{DirNames[d]}_{f}.png"), tile));
            sp.Player[2, d] = closed;
        }
        for (int c = 0; c < 4; c++)
            for (int d = 0; d < 4; d++)
                for (int f = 0; f < 2; f++)
                    sp.Enemy[c, d, f] = sp.Track(Scaled(LoadRaw($"enemy_{ColorNames[c]}_{DirNames[d]}_{f}.png"), tile));
        for (int f = 0; f < 2; f++)
        {
            sp.Scared[0, f] = sp.Track(Scaled(LoadRaw($"scared_blue_{f}.png"), tile));
            sp.Scared[1, f] = sp.Track(Scaled(LoadRaw($"scared_white_{f}.png"), tile));
        }
        for (int i = 0; i < sp.Eggs.Length; i++)
            sp.Eggs[i] = sp.Track(Scaled(LoadRaw($"egg_{i}.png"), tile));
        // 目玉：抜き出しは起動後の最初の1回だけ（結果を覚えておき，作り直しのときは拡大するだけ）
        EyesRaw ??= BuildEyesRaw();
        for (int d = 0; d < 4; d++)
            sp.Eyes[d] = sp.Track(Scaled(new Bitmap(EyesRaw[d]), tile));
        return sp;
    }

    private static Bitmap[]? EyesRaw;     // 目玉（元の大きさ）．起動後に1回だけ作る

    /// <summary>4色x2種類の足のゴーストで，まったく同じ色になっている部分（白目と瞳）を向きごとに抜き出す</summary>
    private static Bitmap[] BuildEyesRaw()
    {
        var result = new Bitmap[4];
        for (int d = 0; d < 4; d++)
        {
            var group = new List<Bitmap>();
            for (int c = 0; c < 4; c++)
                for (int f = 0; f < 2; f++)
                    group.Add(LoadRaw($"enemy_{ColorNames[c]}_{DirNames[d]}_{f}.png"));
            result[d] = CommonPixels(group, group[0]);
            foreach (var b in group) b.Dispose();
        }
        return result;
    }

    /// <summary>全部の画像で同じ色（ほぼ同じ）になっている不透明なマスだけを残す．共通部分がなければ fallback の複製</summary>
    private static Bitmap CommonPixels(List<Bitmap> srcs, Bitmap fallback)
    {
        var first = srcs[0];
        int w = first.Width, h = first.Height;
        foreach (var b in srcs)
            if (b.Width != w || b.Height != h) return new Bitmap(fallback);   // 大きさが違うときは共通部分を取れない
        var dst = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        int kept = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c0 = first.GetPixel(x, y);
                if (c0.A < 200) continue;
                bool same = true;
                for (int i = 1; i < srcs.Count && same; i++)
                {
                    var c = srcs[i].GetPixel(x, y);
                    same = c.A >= 200 && Math.Abs(c.R - c0.R) + Math.Abs(c.G - c0.G) + Math.Abs(c.B - c0.B) <= 30;
                }
                if (!same) continue;
                dst.SetPixel(x, y, c0);
                kept++;
            }
        if (kept >= 4) return dst;
        dst.Dispose();
        return new Bitmap(fallback);
    }

    private Bitmap Track(Bitmap b)
    {
        _all.Add(b);
        return b;
    }

    // スプライトシートの配置（列 x 行）．シートの幅 = 11マス，高さ = 7マス．
    //   行0: player_right_0/1 player_left_0/1 player_down_0/1 player_up_0/1 player_closed
    //   行1〜4: 赤・ピンク・水色・オレンジ．各行は enemy_{色}_{right,left,down,up}_{0,1}（右0,1 左0,1 下0,1 上0,1）
    //   行5: scared_blue_0/1 scared_white_0/1
    //   行6: egg_0 〜 egg_10
    public const int SheetCols = 11, SheetRows = 7;
    private static readonly Dictionary<string, (int Col, int Row)> SheetCells = BuildSheetCells();

    private static Dictionary<string, (int Col, int Row)> BuildSheetCells()
    {
        var m = new Dictionary<string, (int, int)>();
        for (int d = 0; d < 4; d++)
            for (int f = 0; f < 2; f++)
                m[$"player_{DirNames[d]}_{f}.png"] = (d * 2 + f, 0);
        m["player_closed.png"] = (8, 0);
        for (int c = 0; c < 4; c++)
            for (int d = 0; d < 4; d++)
                for (int f = 0; f < 2; f++)
                    m[$"enemy_{ColorNames[c]}_{DirNames[d]}_{f}.png"] = (d * 2 + f, 1 + c);
        for (int f = 0; f < 2; f++)
        {
            m[$"scared_blue_{f}.png"] = (f, 5);
            m[$"scared_white_{f}.png"] = (2 + f, 5);
        }
        for (int i = 0; i < 11; i++) m[$"egg_{i}.png"] = (i, 6);
        return m;
    }

    private static Bitmap? _sheet;
    private static bool _sheetChecked;

    /// <summary>exeの隣の assets\sheet.png があれば読み込む（なければ null）</summary>
    private static Bitmap? ExternalSheet()
    {
        if (_sheetChecked) return _sheet;
        _sheetChecked = true;
        string file = Path.Combine(AppContext.BaseDirectory, "assets", "sheet.png");
        if (!File.Exists(file)) return null;
        using var fs = File.OpenRead(file);
        using var tmp = new Bitmap(fs);
        var sheet = new Bitmap(tmp);
        int cell = sheet.Width / SheetCols;
        if (cell < 1 || sheet.Width != cell * SheetCols || sheet.Height != cell * SheetRows)
        {
            int w = sheet.Width, h = sheet.Height;
            sheet.Dispose();
            throw new InvalidDataException(
                $"sheet.png の大きさが合いません（{w}x{h}）．{SheetCols}列x{SheetRows}行で，1マスが正方形になる大きさにしてください（例：{16 * SheetCols}x{16 * SheetRows}）．");
        }
        _sheet = sheet;
        return _sheet;
    }

    private static Bitmap LoadRaw(string name)
    {
        var sheet = ExternalSheet();
        if (sheet is not null && SheetCells.TryGetValue(name, out var cellPos))
        {
            int cell = sheet.Width / SheetCols;
            var part = new Bitmap(cell, cell, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(part);
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(sheet, new Rectangle(0, 0, cell, cell), cellPos.Col * cell, cellPos.Row * cell, cell, cell, GraphicsUnit.Pixel);
            return part;
        }

        string file = Path.Combine(AppContext.BaseDirectory, "assets", name);
        Bitmap raw;
        if (File.Exists(file))
        {
            using var fs = File.OpenRead(file);
            using var tmp = new Bitmap(fs);
            raw = new Bitmap(tmp);   // ファイルを掴みっぱなしにしないよう複製する
        }
        else
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("assets/" + name)
                ?? throw new FileNotFoundException("画像が見つかりません: " + name);
            using var tmp = new Bitmap(stream);
            raw = new Bitmap(tmp);
        }
        if (raw.Width != raw.Height)
        {
            int w = raw.Width, h = raw.Height;
            raw.Dispose();
            throw new InvalidDataException($"{name} が正方形ではありません（{w}x{h}）．正方形の画像にしてください．");
        }
        return raw;
    }

    /// <summary>ドットがにじまないように（最近傍で）tile x tile に拡大する</summary>
    private static Bitmap Scaled(Bitmap src, int tile)
    {
        using (src)
        {
            var dst = new Bitmap(tile, tile, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(dst);
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(src, new Rectangle(0, 0, tile, tile), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            return dst;
        }
    }

    /// <summary>トレイに出すアイコン（主人公の画像から作る）</summary>
    public static Icon CreateTrayIcon()
    {
        using var big = Scaled(LoadRaw("player_right_0.png"), 32);
        IntPtr h = big.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            Native.DestroyIcon(h);
        }
    }

    public void Dispose()
    {
        foreach (var b in _all) b.Dispose();
        _all.Clear();
    }
}
