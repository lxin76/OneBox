# -*- coding: utf-8 -*-
"""图标检查图：放大看设计细节，像素级放大看小尺寸剪影。"""
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
MASTER = os.path.join(HERE, "master")
NAMES = ["platform", "textpin", "quicknote", "screenshot"]


def sheet_big():
    """256 原尺寸并排，检查造型与配色。"""
    pad, cell = 28, 256
    w = pad + len(NAMES) * (cell + pad)
    h = pad * 2 + cell
    canvas = Image.new("RGB", (w, h), (16, 16, 26))
    for i, n in enumerate(NAMES):
        im = Image.open(os.path.join(MASTER, f"{n}.png")).convert("RGBA").resize((cell, cell), Image.LANCZOS)
        canvas.paste(im, (pad + i * (cell + pad), pad), im)
    canvas.save(os.path.join(HERE, "check-big.png"))

    # 浅色背景版本，确认描边与发光在亮底上不刺眼
    canvas2 = Image.new("RGB", (w, h), (238, 240, 246))
    for i, n in enumerate(NAMES):
        im = Image.open(os.path.join(MASTER, f"{n}.png")).convert("RGBA").resize((cell, cell), Image.LANCZOS)
        canvas2.paste(im, (pad + i * (cell + pad), pad), im)
    canvas2.save(os.path.join(HERE, "check-big-light.png"))


def sheet_small():
    """真实小尺寸（32/24/16）最近邻放大，检查剪影可辨性。"""
    src = Image.open(os.path.join(MASTER, f"{NAMES[0]}.ico"))
    zoom = 8
    sizes = [48, 32, 24, 16]
    pad = 20
    cellw = max(sizes) * zoom
    w = pad + len(NAMES) * (cellw + pad)
    h = pad + len(sizes) * (cellw + pad)
    canvas = Image.new("RGB", (w, h), (16, 16, 26))
    for i, n in enumerate(NAMES):
        ico = Image.open(os.path.join(MASTER, f"{n}.ico"))
        for j, s in enumerate(sizes):
            ico.size = (s, s)
            frame = ico.convert("RGBA").resize((s * zoom, s * zoom), Image.NEAREST)
            x = pad + i * (cellw + pad) + (cellw - s * zoom) // 2
            y = pad + j * (cellw + pad) + (cellw - s * zoom) // 2
            canvas.paste(frame, (x, y), frame)
    canvas.save(os.path.join(HERE, "check-small.png"))


def sheet_small_real():
    """按真实像素大小排列（不放大），模拟实际观感。"""
    pad = 26
    w = pad + len(NAMES) * (56 + pad)
    h = 200
    canvas = Image.new("RGB", (w, h), (28, 30, 42))
    for i, n in enumerate(NAMES):
        ico = Image.open(os.path.join(MASTER, f"{n}.ico"))
        x = pad + i * (56 + pad)
        canvas.paste(ico.convert("RGBA").resize((48, 48), Image.LANCZOS), (x, 40))
        canvas.paste(ico.convert("RGBA").resize((32, 32), Image.LANCZOS), (x, 100))
        # 16 用最近邻还原真实像素观感
        ico.size = (16, 16)
        canvas.paste(ico.convert("RGBA").resize((24, 24), Image.NEAREST), (x, 140))
    canvas.save(os.path.join(HERE, "check-real.png"))


sheet_big()
sheet_small()
sheet_small_real()
print("ok")
