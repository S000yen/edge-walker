using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace EdgeWalker;

/// <summary>
/// 枠なし・背景透明（ピクセルごとの透明度）・最前面・クリック素通りのウィンドウ．
/// 中身は Surface に描いて Present() で画面に反映する．タスクバーにもAlt+Tabにも出ない．
/// </summary>
internal sealed class Overlay : Form
{
    public Bitmap Surface { get; }

    // 画面に出すためのメモリ（DIBセクション）は最初に1回だけ作り，Surface はその上に直接描く．
    // 毎回ビットマップを作ってコピーしないので軽い．作れなかったときは従来の方法に切り替える
    private IntPtr _memDc, _dib, _oldBmp;

    public Overlay(Rectangle bounds)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        int w = Math.Max(1, bounds.Width), h = Math.Max(1, bounds.Height);
        Surface = TryCreateDib(w, h) ?? new Bitmap(w, h, PixelFormat.Format32bppPArgb);
    }

    private Bitmap? TryCreateDib(int w, int h)
    {
        try
        {
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,          // 上から下の並び（Bitmap と同じ向き）
                biPlanes = 1,
                biBitCount = 32,
            };
            _dib = Native.CreateDIBSection(IntPtr.Zero, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || bits == IntPtr.Zero) { ReleaseDib(); return null; }
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            try { _memDc = Native.CreateCompatibleDC(screenDc); }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
            if (_memDc == IntPtr.Zero) { ReleaseDib(); return null; }
            _oldBmp = Native.SelectObject(_memDc, _dib);
            return new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        }
        catch
        {
            ReleaseDib();
            return null;
        }
    }

    private void ReleaseDib()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBmp != IntPtr.Zero) Native.SelectObject(_memDc, _oldBmp);
            Native.DeleteDC(_memDc);
        }
        if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
        _memDc = _dib = _oldBmp = IntPtr.Zero;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                          | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>描画用のGraphics（全面を透明で初期化済み）．using で閉じること</summary>
    public Graphics BeginDraw()
    {
        var g = Graphics.FromImage(Surface);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        return g;
    }

    /// <summary>Surface の内容を画面に反映する</summary>
    public void Present() => PresentAt(Left, Top);

    /// <summary>Surface の内容を，指定の位置に動かしながら画面に反映する</summary>
    public void PresentAt(int left, int top)
    {
        if (!IsHandleCreated || !Visible) return;

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = _memDc, hBmp = IntPtr.Zero, old = IntPtr.Zero;
        bool temp = memDc == IntPtr.Zero;      // DIBが使えないときだけ，毎回作る（従来の方法）
        try
        {
            if (temp)
            {
                memDc = Native.CreateCompatibleDC(screenDc);
                hBmp = Surface.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBmp);
            }

            var size = new Native.SIZE { cx = Surface.Width, cy = Surface.Height };
            var src = new Native.POINT { X = 0, Y = 0 };
            var dst = new Native.POINT { X = left, Y = top };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };
            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            if (temp)
            {
                if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
                if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
            }
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Surface.Dispose();     // 先に Surface を閉じてから，その下のメモリを解放する
        ReleaseDib();
        base.Dispose(disposing);
    }
}
