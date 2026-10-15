"""仮のオリジナル画像（16x16ドット絵，すべて正方形）を assets/ と sheet/sheet.png に生成する．
自分で描いた画像に差し替える場合は，同じファイル名（またはsheet.pngの同じ配置）で上書きすればよい．
正方形であれば16x16以外のサイズでも使える（読み込み時にマスの大きさへ拡大される）．

  player_{right,left,down,up}_{0,1}.png   主人公（口 開／半開）．方向ごとに用意する
  player_closed.png                        主人公（口を閉じた球体）．全方向で共通
  enemy_{red,pink,cyan,orange}_{right,left,down,up}_{0,1}.png   ゴースト（足の2種類を交互に表示）
  scared_{blue,white}_{0,1}.png            おびえたゴースト（赤・水色は青，ピンク・オレンジは白）
  egg_0..egg_10.png                        誕生の11段階（egg_0 はミニボム．食べられたゴーストにも使う）
壁は画像ではなくプログラムで描く（白い角丸の枠線）．
"""
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "assets")
SHEET_OUT = os.path.join(HERE, "sheet")
S = 16
CLEAR = (0, 0, 0, 0)
DIRS = ["right", "left", "down", "up"]
COLORS = {"red": (230, 60, 60, 255), "pink": (255, 150, 200, 255),
          "cyan": (80, 210, 230, 255), "orange": (245, 160, 50, 255)}


def darker(c, k=0.6):
    return (int(c[0] * k), int(c[1] * k), int(c[2] * k), 255)


def orient(im, d):
    """右向きの絵から d 向きの絵を作る（生成用．アプリ側では回転・反転しない）"""
    if d == "left":
        return im.transpose(Image.FLIP_LEFT_RIGHT)
    if d == "down":
        return im.transpose(Image.ROTATE_270)
    if d == "up":
        return im.transpose(Image.ROTATE_90)
    return im


def player(mouth, body=(255, 200, 70, 255), mouth_col=(120, 40, 20, 255)):
    """右向きの主人公．mouth: open / half / closed"""
    im = Image.new("RGBA", (S, S), CLEAR)
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([1, 1, 14, 14], radius=7, fill=body, outline=darker(body))
    if mouth == "open":
        d.polygon([(8, 8), (15, 3), (15, 12)], fill=CLEAR)
    elif mouth == "half":
        d.polygon([(8, 8), (15, 5), (15, 10)], fill=CLEAR)
    if mouth != "closed":
        pass
    else:
        d.line([(11, 8), (14, 8)], fill=mouth_col)
    d.rectangle([7, 3, 8, 4], fill=(40, 20, 30, 255))  # 目
    return im


def enemy(color, direction, foot):
    """ゴースト．目は進行方向に寄せる．足は foot=0/1 で切れ込みの位置が変わる"""
    im = Image.new("RGBA", (S, S), CLEAR)
    d = ImageDraw.Draw(im)
    edge = darker(color)
    d.pieslice([1, 1, 14, 14], 180, 360, fill=color, outline=edge)
    d.rectangle([1, 7, 14, 14], fill=color, outline=edge)
    notches = [(3, 4), (7, 8), (11, 12)] if foot == 0 else [(1, 2), (5, 6), (9, 10), (13, 14)]
    for a, b in notches:
        d.rectangle([a, 14, b, 15], fill=CLEAR)
        d.rectangle([a, 14, b, 14], fill=CLEAR)
    dx, dy = {"right": (1, 0), "left": (-1, 0), "down": (0, 1), "up": (0, -1)}[direction]
    for ex in (4, 9):
        d.rectangle([ex, 5, ex + 2, 8], fill=(255, 255, 255, 255))
        d.rectangle([ex + 1 + dx, 6 + dy, ex + 1 + dx, 7 + dy], fill=(30, 40, 160, 255))
    return im


def scared(body, face, foot):
    im = Image.new("RGBA", (S, S), CLEAR)
    d = ImageDraw.Draw(im)
    edge = darker(body)
    d.pieslice([1, 1, 14, 14], 180, 360, fill=body, outline=edge)
    d.rectangle([1, 7, 14, 14], fill=body, outline=edge)
    notches = [(3, 4), (7, 8), (11, 12)] if foot == 0 else [(1, 2), (5, 6), (9, 10), (13, 14)]
    for a, b in notches:
        d.rectangle([a, 14, b, 15], fill=CLEAR)
    d.rectangle([5, 5, 6, 6], fill=face)
    d.rectangle([9, 5, 10, 6], fill=face)
    for i, x in enumerate(range(3, 13)):
        d.point((x, 10 + (i % 2)), fill=face)
    return im


def egg(stage):
    """誕生の11段階．0=ミニボム，1〜4=卵が育つ，5〜7=ひびが増える，8〜9=割れる，10=出てくる直前"""
    im = Image.new("RGBA", (S, S), CLEAR)
    d = ImageDraw.Draw(im)
    shell, edge, spot = (250, 240, 215, 255), (170, 140, 100, 255), (205, 175, 125, 255)
    inner = (255, 140, 160, 255)
    if stage == 0:
        d.ellipse([4, 6, 11, 13], fill=(40, 40, 55, 255), outline=(15, 15, 25, 255))
        d.point((6, 8), fill=(120, 120, 140, 255))
        d.line([(8, 6), (9, 4), (11, 3)], fill=(150, 110, 70, 255))
        d.point((12, 2), fill=(255, 200, 60, 255))
        d.point((11, 1), fill=(255, 120, 40, 255))
        return im
    if stage <= 4:
        h = [0, 5, 7, 9, 12][stage]
        w = max(4, int(h * 0.75))
        cx, bottom = 8, 14
        d.ellipse([cx - w // 2, bottom - h, cx + w // 2, bottom], fill=shell, outline=edge)
        return im
    d.ellipse([3, 1, 12, 15], fill=shell, outline=edge)
    for p in [(6, 11), (9, 12), (10, 10), (5, 9)]:
        d.point(p, fill=spot)
    crack = (110, 80, 60, 255)
    zig = [(4, 7), (5, 6), (6, 7), (7, 6), (8, 7), (9, 6), (10, 7), (11, 6)]
    if stage == 5:
        for p in zig[:3]:
            d.point(p, fill=crack)
    elif stage == 6:
        for p in zig[:6]:
            d.point(p, fill=crack)
    elif stage == 7:
        for p in zig:
            d.point(p, fill=crack)
        d.rectangle([6, 2, 9, 4], fill=inner)
    elif stage == 8:
        im = Image.new("RGBA", (S, S), CLEAR)
        d = ImageDraw.Draw(im)
        d.ellipse([3, 6, 12, 15], fill=shell, outline=edge)
        d.rectangle([3, 6, 12, 8], fill=inner)
        d.polygon([(3, 5), (5, 1), (7, 5), (9, 1), (12, 5)], fill=shell, outline=edge)
    elif stage == 9:
        im = Image.new("RGBA", (S, S), CLEAR)
        d = ImageDraw.Draw(im)
        d.rounded_rectangle([4, 4, 11, 13], radius=3, fill=inner, outline=darker(inner))
        d.polygon([(0, 14), (3, 9), (5, 14)], fill=shell, outline=edge)
        d.polygon([(10, 14), (13, 9), (15, 14)], fill=shell, outline=edge)
    else:
        im = Image.new("RGBA", (S, S), CLEAR)
        d = ImageDraw.Draw(im)
        d.rounded_rectangle([2, 2, 13, 14], radius=5, fill=inner, outline=darker(inner))
        d.rectangle([5, 6, 6, 7], fill=(40, 20, 30, 255))
        d.rectangle([9, 6, 10, 7], fill=(40, 20, 30, 255))
        d.polygon([(0, 15), (2, 12), (4, 15)], fill=shell, outline=edge)
        d.polygon([(11, 15), (13, 12), (15, 15)], fill=shell, outline=edge)
    return im


# スプライトシートの配置（列 x 行）．Sprites.cs の定義と同じにすること
SHEET_COLS, SHEET_ROWS = 11, 7


def build():
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(SHEET_OUT, exist_ok=True)
    for f in os.listdir(OUT):
        if f.endswith(".png"):
            os.remove(os.path.join(OUT, f))
    cells = {}
    base = {"open": player("open"), "half": player("half")}
    for di, d in enumerate(DIRS):
        for f, key in enumerate(["open", "half"]):
            cells[f"player_{d}_{f}.png"] = (di * 2 + f, 0), orient(base[key], d)
    cells["player_closed.png"] = (8, 0), player("closed")
    for ci, (cn, col) in enumerate(COLORS.items()):
        for di, d in enumerate(DIRS):
            for f in range(2):
                cells[f"enemy_{cn}_{d}_{f}.png"] = (di * 2 + f, 1 + ci), enemy(col, d, f)
    blue, white = (60, 80, 220, 255), (240, 240, 255, 255)
    for f in range(2):
        cells[f"scared_blue_{f}.png"] = (f, 5), scared(blue, (255, 255, 255, 255), f)
        cells[f"scared_white_{f}.png"] = (2 + f, 5), scared(white, (220, 50, 60, 255), f)
    for i in range(11):
        cells[f"egg_{i}.png"] = (i, 6), egg(i)
    sheet = Image.new("RGBA", (S * SHEET_COLS, S * SHEET_ROWS), CLEAR)
    for name, ((c, r), im) in cells.items():
        im.save(os.path.join(OUT, name))
        sheet.paste(im, (c * S, r * S))
    sheet.save(os.path.join(SHEET_OUT, "sheet.png"))
    return cells


if __name__ == "__main__":
    n = len(build())
    print(f"assets/ に {n} 枚，sheet/sheet.png を生成しました")
