# -*- coding: utf-8 -*-
"""
OneBox 图标生成器：平台 + 各插件的矢量造型 -> 多尺寸 ICO / PNG。

尺寸策略（关键）：
  大尺寸（>=32px）在 2048 画布上完整绘制，细节丰富；
  小尺寸（16/20/24/28px）用两招保证清晰，而不是把大图缩小：
    1) 笔画按"目标像素反推"加粗 —— 2048→16 是 128:1，不显式加粗的线
       在 16px 上连半个像素都不到，必然糊；这里按 512 画布上的实际像素定宽度
    2) 造型按 alpha 包围盒自动放大并居中，占满画幅（小图标的通行做法）

图标造型差异：
  platform   等轴测立方体 + 顶面插槽        —— 宿主是承载各小工具的容器
  textpin    文本卡片 + 右上角图钉           —— 置顶文本，图钉表示"钉在最上层"
  quicknote  折角便签 + 镂空闪电            —— 快速便签
  screenshot 四角取景框 + 中心准星圆环       —— 截图
  recorder   显示器轮廓 + 红色录制圆点        —— 录屏
"""
import io
import math
import os
import struct

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

BASE = 1024.0          # 设计坐标系基准边长
LARGE = 2048           # 大尺寸画布
SMALL = 512            # 小尺寸画布
AA = 2.0               # 2048 画布上的边缘软化宽度


def hex2rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4))


class Render:
    """一次绘制：把 1024 基准坐标映射到某个画布上。

    stroke() 负责"笔画加粗"：小尺寸画布上按目标像素反推，
    否则从 2048 缩到 16 时线宽不足 1px，图形会糊成一团。
    """

    def __init__(self, canvas, small):
        self.canvas = canvas
        self.small = small
        self.k = canvas / BASE
        self.aa = AA * canvas / LARGE
        self._mg = None
        self.im = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
        self.d = ImageDraw.Draw(self.im)

    # 坐标换算
    def p(self, v):
        return v * self.k

    def box(self, x0, y0, x1, y1):
        return [x0 * self.k, y0 * self.k, x1 * self.k, y1 * self.k]

    def pts(self, seq):
        return [(x * self.k, y * self.k) for x, y in seq]

    def stroke(self, base):
        """线宽：base 为 1024 基准宽度；小尺寸额外加粗。"""
        return max(1, int(self.p(base) * (4.5 if self.small else 1.0)))

    @property
    def mg(self):
        if self._mg is None:
            self._mg = np.mgrid[0:self.canvas, 0:self.canvas].astype(np.float32)
        return self._mg

    # ---- 底板 ----
    def squircle(self, size, n=4.2):
        yy, xx = self.mg
        c = self.canvas / 2.0
        half = size * self.k / 2.0
        u = np.abs(xx - c) / half
        v = np.abs(yy - c) / half
        f = u ** n + v ** n
        edge = self.aa / half
        return np.clip((1.0 - f) / edge + 0.5, 0, 1)

    def gradient(self, c0, c1, angle_deg):
        yy, xx = self.mg
        a = math.radians(angle_deg)
        t = xx * math.cos(a) + yy * math.sin(a)
        t = (t - t.min()) / max(1e-6, t.max() - t.min())
        c0, c1 = np.array(c0, np.float32), np.array(c1, np.float32)
        return c0[None, None, :] * (1 - t[..., None]) + c1[None, None, :] * t[..., None]


PLATE = 896            # 底板边长（1024 基准，四周各留 64）


def base_plate(r, deep, shallow, glow, glow_xy, glow_r):
    mask = r.squircle(PLATE)
    rgb = r.gradient(deep, shallow, 118)

    yy, xx = r.mg
    d = np.sqrt((xx - r.p(glow_xy[0])) ** 2 + (yy - r.p(glow_xy[1])) ** 2)
    strength = np.clip(1.0 - d / r.p(glow_r), 0, 1)[..., None] ** 2
    rgb = rgb + np.array(glow, np.float32)[None, None, :] * strength * 0.75

    edge = np.clip(mask - r.squircle(PLATE - 10), 0, 1)[..., None]
    rgb = (rgb * (1 - edge * 0.6)
           + np.array(hex2rgb("#AFCBFF"), np.float32)[None, None, :] * edge * 0.6)
    return np.clip(rgb, 0, 1), mask


def flat_plate(r, deep, shallow):
    """小尺寸底板：不出渐变与发光，纯色 + 略深的边，保证剪影干净。"""
    deep, shallow = np.array(deep, np.float32), np.array(shallow, np.float32)
    color = deep * 0.62 + shallow * 0.38
    rgb = np.broadcast_to(color, (r.canvas, r.canvas, 3)).copy()
    mask = r.squircle(PLATE, n=3.6)
    edge = np.clip(mask - r.squircle(PLATE - 10, n=3.6), 0, 1)[..., None]
    rgb = (rgb * (1 - edge * 0.5)
           + np.array(hex2rgb("#9FB6DC"), np.float32)[None, None, :] * edge * 0.5)
    return rgb, mask


def add_glow(rgb, shape_alpha, color, radius, strength):
    blur = Image.fromarray((np.clip(shape_alpha, 0, 1) * 255).astype(np.uint8))
    blur = blur.filter(ImageFilter.GaussianBlur(radius))
    a = np.asarray(blur, np.float32) / 255.0
    return rgb + np.array(color, np.float32)[None, None, :] * (a * strength)[..., None]


def to_np(im):
    a = np.asarray(im, np.float32) / 255.0
    return a[..., :3], a[..., 3]


def over(dst_rgb, dst_a, src_rgb, src_a):
    out_a = src_a + dst_a * (1 - src_a)
    safe = np.maximum(out_a, 1e-6)[..., None]
    return (src_rgb * src_a[..., None] + dst_rgb * (dst_a * (1 - src_a))[..., None]) / safe, out_a


# ================================================================ 四枚造型

def draw_platform(r):
    """等轴测立方体 + 顶面插槽：宿主是承载小工具的容器。"""
    d = r.d
    apex, shoulder, front = 196, 340, 484
    left_x, right_x = 232, 792
    drop = 284

    d.polygon(r.pts([(512, apex), (right_x, shoulder), (512, front), (left_x, shoulder)]),
              fill=(168, 204, 255, 255))
    d.polygon(r.pts([(left_x, shoulder), (512, front), (512, front + drop), (left_x, shoulder + drop)]),
              fill=(74, 122, 208, 255))
    d.polygon(r.pts([(512, front), (right_x, shoulder), (right_x, shoulder + drop), (512, front + drop)]),
              fill=(32, 62, 118, 255))

    # 顶面插槽
    if r.small:
        d.polygon(r.pts([(512, 272), (656, 340), (512, 408), (368, 340)]), fill=(140, 236, 255, 255))
    else:
        d.polygon(r.pts([(512, 268), (664, 340), (512, 412), (360, 340)]), fill=(24, 46, 88, 255))
        d.polygon(r.pts([(512, 300), (626, 340), (512, 380), (398, 340)]), fill=(150, 240, 255, 255))

    w = r.stroke(11)
    d.line(r.pts([(512, apex), (right_x, shoulder), (512, front), (left_x, shoulder), (512, apex)]),
           fill=(214, 234, 255, 255), width=w, joint="curve")
    d.line(r.pts([(512, front), (512, front + drop)]), fill=(150, 200, 255, 235), width=w)
    d.line(r.pts([(left_x, shoulder), (left_x, shoulder + drop)]), fill=(122, 176, 245, 205), width=w)
    d.line(r.pts([(right_x, shoulder), (right_x, shoulder + drop)]), fill=(96, 140, 220, 205), width=w)

    rgb, alpha = to_np(r.im)
    return rgb, alpha, hex2rgb("#5FB8FF")


def draw_textpin(r):
    """置顶文本：文本卡片 + 右上角图钉，图钉表示"钉在所有窗口最上层"。

    小尺寸下卡片上的文字行会糊成一片，因此只保留两行粗行；
    图钉则放大并加粗，靠它和"折角便签""剪贴板"区分开。
    """
    d = r.d

    # 文本卡片
    d.rounded_rectangle(r.box(264, 296, 760, 828), radius=r.p(84),
                        fill=(32, 38, 79, 255), outline=(126, 150, 255, 255), width=r.stroke(11))

    rows = [(408, 300), (556, 244)] if r.small else \
           [(392, 320), (492, 264), (592, 300), (692, 180)]
    for i, (y, w) in enumerate(rows):
        h = 84 if r.small else 48
        color = (108, 224, 255, 255) if i == 0 else (86, 108, 176, 255)
        d.rounded_rectangle([r.p(344), r.p(y), r.p(344 + w), r.p(y + h)],
                            radius=r.p(h / 2), fill=color)

    # 图钉：针 + 钉帽，压在卡片右上角
    needle = r.stroke(16 if r.small else 13)
    d.line(r.pts([(668, 372), (760, 258)]), fill=(198, 210, 236, 255), width=needle)

    head_cx, head_cy = 792, 216
    head_r = 132 if r.small else 112
    d.ellipse(r.box(head_cx - head_r, head_cy - head_r, head_cx + head_r, head_cy + head_r),
              fill=(255, 190, 64, 255), outline=(255, 236, 176, 255), width=r.stroke(9))

    # 钉帽内的高光小圆，暗示这是个立体的图钉而不是色块
    hl = head_r * 0.42
    d.ellipse(r.box(head_cx - hl - head_r * 0.18, head_cy - hl - head_r * 0.18,
                    head_cx + hl - head_r * 0.18, head_cy + hl - head_r * 0.18),
              fill=(255, 240, 196, 255))

    rgb, alpha = to_np(r.im)
    return rgb, alpha, hex2rgb("#FFB43C")


def draw_quicknote(r):
    """快速便签：折角纸片 + 镂空闪电（"快"）。"""
    d = r.d
    fold = 224
    x0, y0, x1, y1 = 232, 232, 792, 792

    # 后面一张纸错位，暗示"随手记了很多条"
    if not r.small:
        d.polygon(r.pts([(x0 + 40, y0 - 32), (x1 + 40, y0 - 32), (x1 + 40, y1 - fold - 32),
                         (x1 - fold + 40, y1 - 32), (x0 + 40, y1 - 32)]),
                  fill=(176, 118, 34, 255))

    d.polygon(r.pts([(x0, y0), (x1, y0), (x1, y1 - fold), (x1 - fold, y1), (x0, y1)]),
              fill=(255, 194, 78, 255))
    d.polygon(r.pts([(x1 - fold, y1), (x1, y1 - fold), (x1, y1)]), fill=(255, 238, 182, 255))
    d.line(r.pts([(x1 - fold, y1), (x1, y1 - fold)]), fill=(255, 252, 232, 255), width=r.stroke(7))

    # 镂空闪电：填底板深色，像在纸片上挖空
    if r.small:
        bolt = [(556, 316), (382, 562), (488, 562), (430, 716), (628, 458), (516, 458)]
    else:
        bolt = [(556, 336), (398, 556), (490, 556), (446, 692), (610, 468), (516, 468)]
    d.polygon(r.pts(bolt), fill=(22, 33, 54, 255))

    rgb, alpha = to_np(r.im)
    return rgb, alpha, hex2rgb("#FFB43C")


def draw_screenshot(r):
    """截图：四角取景框 + 中心准星。

    小尺寸下不用圆环：16px 时圆环、刻度与框臂会连成一片，看上去像朵花。
    改成方硬的粗取景框 + 实心准星点，剪影才立得住。
    """
    d = r.d

    if r.small:
        lo, hi, L, arm = 176, 848, 372, 132
        for ox, oy, sx, sy in ((lo, lo, 1, 1), (hi, lo, -1, 1), (lo, hi, 1, -1), (hi, hi, -1, -1)):
            x0 = r.p(ox) if sx > 0 else r.p(ox) - r.p(L)
            y0 = r.p(oy) if sy > 0 else r.p(oy) - r.p(L)
            a = r.p(arm)
            d.rectangle([x0, y0, x0 + r.p(L), y0 + a], fill=(206, 186, 255, 255))
            d.rectangle([x0, y0, x0 + a, y0 + r.p(L)], fill=(206, 186, 255, 255))
        d.ellipse(r.box(408, 408, 616, 616), fill=(236, 246, 255, 255))
    else:
        lo, hi, L, arm = 210, 814, 292, r.stroke(74)
        rad = max(1, arm // 2)
        for ox, oy, sx, sy in ((lo, lo, 1, 1), (hi, lo, -1, 1), (lo, hi, 1, -1), (hi, hi, -1, -1)):
            x0 = r.p(ox) if sx > 0 else r.p(ox) - r.p(L)
            y0 = r.p(oy) if sy > 0 else r.p(oy) - r.p(L)
            d.rounded_rectangle([x0, y0, x0 + r.p(L), y0 + arm], radius=rad, fill=(198, 176, 255, 255))
            d.rounded_rectangle([x0, y0, x0 + arm, y0 + r.p(L)], radius=rad, fill=(198, 176, 255, 255))

        d.ellipse(r.box(354, 354, 670, 670), outline=(150, 116, 255, 255), width=r.stroke(54))
        for x0, y0, x1, y1 in ((470, 372, 554, 470), (470, 554, 554, 652),
                               (372, 470, 470, 554), (554, 470, 652, 554)):
            d.rounded_rectangle(r.box(x0, y0, x1, y1), radius=r.p(22), fill=(216, 202, 255, 255))
        d.ellipse(r.box(452, 452, 572, 572), fill=(232, 244, 255, 255))

    rgb, alpha = to_np(r.im)
    return rgb, alpha, hex2rgb("#9E7BFF")


def draw_recorder(r):
    """录屏：显示器轮廓 + 中心红色录制圆点 + 底座。

    小尺寸下去掉底座、收细描边并缩小红点：细线框和横线在 16px 会断成一截一截，
    只留「屏幕 + 红点」的剪影最清楚。
    """
    d = r.d
    frame = (255, 214, 214, 255)
    dot = (255, 82, 82, 255)

    if r.small:
        # 16px 下不再画底座：横线会与屏幕轮廓糊成一团；只留「屏幕 + 红点」，
        # 并收细描边、缩小红点，保证两者之间仍有空隙。
        d.rounded_rectangle(r.box(190, 300, 834, 690), radius=r.p(64),
                            outline=frame, width=r.stroke(36))
        d.ellipse(r.box(414, 378, 610, 574), fill=dot)
    else:
        d.rounded_rectangle(r.box(236, 286, 788, 654), radius=r.p(58),
                            outline=frame, width=r.stroke(34))
        d.ellipse(r.box(392, 350, 632, 590), fill=dot)
        d.line(r.pts([(512, 654), (512, 748)]), fill=frame, width=r.stroke(30))
        d.line(r.pts([(370, 790), (654, 790)]), fill=frame, width=r.stroke(30))

    rgb, alpha = to_np(r.im)
    return rgb, alpha, hex2rgb("#FF6B6B")


def draw_generic(r):
    """通用插件占位图标：芯片造型（第三方插件未声明图标时使用）。"""
    d = r.d
    body, rad = 296, 100
    w = r.stroke(20 if r.small else 11)

    d.rounded_rectangle(r.box(body, body, 1024 - body, 1024 - body), radius=r.p(rad),
                        fill=(38, 44, 60, 255), outline=(168, 180, 202, 255), width=w)

    # 两侧引脚
    pin_t = 62 if r.small else 44
    pin_l = 84 if r.small else 62
    for cy in ((430, 594) if r.small else (410, 512, 614)):
        d.rounded_rectangle([r.p(body - pin_l), r.p(cy - pin_t / 2),
                             r.p(body), r.p(cy + pin_t / 2)], radius=r.p(pin_t / 2),
                            fill=(168, 180, 202, 255))
        d.rounded_rectangle([r.p(1024 - body), r.p(cy - pin_t / 2),
                             r.p(1024 - body + pin_l), r.p(cy + pin_t / 2)], radius=r.p(pin_t / 2),
                            fill=(168, 180, 202, 255))

    # 中心核心
    c, half = 512, 118 if r.small else 96
    d.rounded_rectangle(r.box(c - half, c - half, c + half, c + half), radius=r.p(34),
                        fill=(206, 218, 240, 255))

    rgb, alpha = to_np(r.im)
    return rgb, alpha, hex2rgb("#8E9CB8")


SPECS = {
    "platform": dict(fn=draw_platform,
                     deep=hex2rgb("#1B2B50"), shallow=hex2rgb("#0B1220"),
                     glow=hex2rgb("#2F66B4")),
    "textpin": dict(fn=draw_textpin,
                    deep=hex2rgb("#1B2050"), shallow=hex2rgb("#0A0D24"),
                    glow=hex2rgb("#3B49C8")),
    "quicknote": dict(fn=draw_quicknote,
                      deep=hex2rgb("#3A2A12"), shallow=hex2rgb("#1A1207"),
                      glow=hex2rgb("#8E5710")),
    "screenshot": dict(fn=draw_screenshot,
                       deep=hex2rgb("#261D44"), shallow=hex2rgb("#100C1E"),
                       glow=hex2rgb("#5B3FB0")),
    "recorder": dict(fn=draw_recorder,
                     deep=hex2rgb("#3A1A22"), shallow=hex2rgb("#180A0E"),
                     glow=hex2rgb("#B0303C")),
    "generic": dict(fn=draw_generic,
                    deep=hex2rgb("#2B313F"), shallow=hex2rgb("#12151C"),
                    glow=hex2rgb("#3E4658")),
}

# 图标名 -> 项目里的落地路径（相对 OneBox/）
DESTINATIONS = {
    "platform": r"src\OneBox.Host\Assets\app.ico",
    "textpin": r"src\OneBox.Plugins.TextPin\Assets\app.ico",
    "quicknote": r"src\OneBox.Plugins.QuickNote\Assets\app.ico",
    "screenshot": r"src\OneBox.Plugins.Screenshot\Assets\app.ico",
    "recorder": r"src\OneBox.Plugins.Recorder\Assets\app.ico",
    "generic": r"src\OneBox.Host\Assets\plugin-generic.ico",
}

# 额外输出 256px PNG：宿主的界面列表用 pack 资源加载它（BitmapImage 解不了 .ico）
PNG_DESTINATIONS = {
    "platform": r"src\OneBox.Host\Assets\app.png",
    "generic": r"src\OneBox.Host\Assets\plugin-generic.png",
}

SMALL_FILL = 0.66      # 小尺寸造型占画幅比例（相对 1024 基准）


def fit_large(r, shape_im):
    """小尺寸：按 alpha 包围盒把造型放大居中，并留出固定边距。"""
    a = np.asarray(shape_im)[..., 3]
    ys, xs = np.nonzero(a > 8)
    if len(xs) == 0:
        return shape_im

    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    span = max(x1 - x0 + 1, y1 - y0 + 1)

    target = r.canvas * (SMALL_FILL / (PLATE / BASE))
    scale = target / span

    # 以包围盒中心裁一块正方形再缩放，保证放大后仍居中
    half = span / 2.0
    box = (round(cx - half), round(cy - half), round(cx + half), round(cy + half))
    cropped = shape_im.crop(box)
    resized = cropped.resize((int(round(target)), int(round(target))), Image.LANCZOS)

    out = Image.new("RGBA", (r.canvas, r.canvas), (0, 0, 0, 0))
    off = (r.canvas - resized.width) // 2
    out.paste(resized, (off, off), resized)
    return out


def compose(name, small):
    spec = SPECS[name]
    r = Render(SMALL if small else LARGE, small)

    if small:
        rgb, mask = flat_plate(r, spec["deep"], spec["shallow"])
    else:
        rgb, mask = base_plate(r, spec["deep"], spec["shallow"], spec["glow"], (806, 236), 600)

    s_rgb, s_a, glow_color = spec["fn"](r)

    if small:
        shape = fit_large(r, r.im)
        s_rgb, s_a = to_np(shape)
    else:
        rgb = np.clip(add_glow(rgb, s_a, glow_color, r.p(30), 0.62), 0, 1)

    s_a = s_a * mask
    rgb, alpha = over(rgb, mask, s_rgb, s_a)

    return rgb, alpha


def to_image(rgb, alpha):
    out = np.concatenate([np.clip(rgb, 0, 1) * 255, np.clip(alpha, 0, 1)[..., None] * 255], axis=2)
    return Image.fromarray(out.astype(np.uint8), "RGBA")


# ================================================================ ICO 写出

def _dib(im):
    """32bpp BMP 条目（含全零 AND 掩码），兼容 System.Drawing.Icon 与资源管理器。"""
    arr = np.asarray(im.convert("RGBA"), np.uint8)
    h, w = arr.shape[:2]
    xor = arr[..., [2, 1, 0, 3]][::-1].tobytes()
    and_mask = bytes((((w + 31) // 32) * 4) * h)
    return struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor), 0, 0, 0, 0) + xor + and_mask


def _png(im):
    """大尺寸条目存 PNG：BMP 那套是未压缩的，256px 一张就 256 KB。

    Vista 起的 ICO 都允许 PNG 条目，能把这部分压到几十 KB；
    小尺寸仍用 BMP，兼容性最好。
    """
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path, frames):
    entries, blobs, offset = [], [], 6 + 16 * len(frames)
    for size, im in frames:
        data = _png(im) if size >= 128 else _dib(im)
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        blobs.append(data)
        offset += len(data)

    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(frames)))
        for e in entries:
            f.write(e)
        for b in blobs:
            f.write(b)


SMALL_SIZES = (16, 20, 24, 28)
LARGE_SIZES = (32, 40, 48, 64, 96, 128, 256)


def main():
    here = os.path.dirname(os.path.abspath(__file__))   # OneBox/tools/icons
    repo = os.path.dirname(os.path.dirname(here))       # OneBox
    master = os.path.join(here, "master")               # 高分辨率母版，供预览与追溯
    os.makedirs(master, exist_ok=True)

    for name in SPECS:
        big = to_image(*compose(name, small=False))
        tiny = to_image(*compose(name, small=True))
        big.save(os.path.join(master, f"{name}.png"))

        frames = [(s, (tiny if s in SMALL_SIZES else big).resize((s, s), Image.LANCZOS))
                  for s in SMALL_SIZES + LARGE_SIZES]
        write_ico(os.path.join(master, f"{name}.ico"), frames)

        # 落地到项目：ICO 既作 exe 图标，也随插件目录分发（宿主读它做列表图标）
        ico_dest = os.path.join(repo, DESTINATIONS[name])
        os.makedirs(os.path.dirname(ico_dest), exist_ok=True)
        with open(os.path.join(master, f"{name}.ico"), "rb") as src, open(ico_dest, "wb") as dst:
            dst.write(src.read())

        if name in PNG_DESTINATIONS:
            png_dest = os.path.join(repo, PNG_DESTINATIONS[name])
            big.resize((256, 256), Image.LANCZOS).save(png_dest, optimize=True)

        print(f"{name}: ok -> {DESTINATIONS[name]}")


if __name__ == "__main__":
    main()
