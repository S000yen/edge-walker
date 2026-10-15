"""assets/player_right_0.png から exe のアイコン app.ico を作る（ドットがにじまない拡大）．
画像を差し替えたあとは，このスクリプトを実行してから再ビルドする：  python make_icon.py
"""
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "assets", "player_right_0.png")
OUT = os.path.join(HERE, "app.ico")
SIZES = [16, 24, 32, 48, 64, 128, 256]


def main():
    im = Image.open(SRC).convert("RGBA")
    big = im.resize((256, 256), Image.NEAREST)
    big.save(OUT, format="ICO", sizes=[(s, s) for s in SIZES])
    print(f"{OUT} を作りました（{', '.join(str(s) for s in SIZES)}px）")


if __name__ == "__main__":
    main()
