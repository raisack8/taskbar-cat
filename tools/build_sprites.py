# assets/raw の Gemini 生成シートから、猫プログラム用のスプライトを作る
#   python tools/build_sprites.py
# 出力: assets/sprites/<name>.png  （全コマ同サイズのセルを横に並べたストリップ, 2x 解像度）
#   - 緑背景を透過、緑かぶりを除去
#   - 動作のつなぎ目で大きさが揃うようシートごとに縮小率を決める
#   - 胴体の重心を全コマ共通の基準点に置き、足元を下端に揃える（ジャンプの高さは保持）
import os, sys
import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = os.path.join(os.path.dirname(__file__), "..")
RAW = os.path.join(ROOT, "assets", "raw")
OUT = os.path.join(ROOT, "assets", "sprites")
PREVIEW = len(sys.argv) > 1 and sys.argv[1] == "--preview"

STAND_H = 128  # 立ち姿（耳〜足、しっぽ込み）の高さ。実行時は 1x = 64px
SCALE_REF = "01_walk_old.jpg"  # 大きさの基準にするシート（最初に作った歩き）
LIFT = {"drag": 36}            # 地面から浮かせる高さ (2x)。つままれている間は宙に浮く

# name: (file, 行数, 除外するコマ番号)
SHEETS = {
    "walk":      ("01_walk.jpg", 2, [0, 7]),     # 1・8コマ目は顔の向きが違うので除外（6コマ）
    "idle":      ("02_idle.jpg", 2, [2, 4, 5, 6, 7]),  # 上段（真横向き）の1・2・4コマ。3コマ目は脚がずれる、下段は顔がこっち向き
    "sit":       ("03_sit.jpg", 2, []),
    "blink":     ("04_blink.jpg", 2, [4, 5, 6, 7]),
    "sleep":     ("05_sleep.jpg", 2, []),
    "sit_down":  ("06_sit_down.jpg", 1, []),
    "lie_down":  ("07_lie_down.jpg", 1, []),
    "wake":      ("08_wake_stretch.jpg", 2, [3]),   # 4コマ目だけ縮尺が小さいので除外
    "surprised": ("09_surprised.jpg", 1, []),
    "turn":      ("10_turn.jpg", 1, []),
    "talk":      ("12_talk_stand.jpg", 2, [4, 5, 6, 7]),   # 体は横向き・顔はこっち向きで口パク。脚が動かない上段4コマだけ使う
    "turn_sit":  ("14_turn_sit.jpg", 1, []),     # 座ったまま横向き→こっち向き
    "drag":      ("15_drag.jpg", 2, []),         # 首をつままれてぶら下がる（2x2）
}


def load_rgba(path):
    a = np.asarray(Image.open(path).convert("RGB")).astype(np.float32)
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    greenness = g - np.maximum(r, b)
    alpha = np.clip(1.0 - (greenness - 25.0) / 50.0, 0.0, 1.0)
    alpha[g < 60] = 1.0  # 暗い輪郭は必ず不透明
    # 緑かぶり除去
    spill = greenness > 0
    a[..., 1] = np.where(spill, np.maximum(r, b) + np.minimum(greenness, 0) , g)
    return np.dstack([a, alpha * 255.0])


def find_frames(rgba, rows):
    m = rgba[..., 3] > 128
    m = ndimage.binary_opening(m, iterations=2)
    lab, n = ndimage.label(ndimage.binary_dilation(m, iterations=12))
    boxes = []
    for i, sl in enumerate(ndimage.find_objects(lab), 1):
        area = int((m[sl] & (lab[sl] == i)).sum())
        if area >= 20000:
            boxes.append((sl[0], sl[1], area, lab[sl] == i))
    H = rgba.shape[0]
    if rows == 1:
        boxes.sort(key=lambda b: b[1].start)
        return [(b, 0) for b in boxes]
    mid = H / 2
    out = []
    for row in range(2):
        rb = [b for b in boxes if ((b[0].start + b[0].stop) / 2 >= mid) == (row == 1)]
        rb.sort(key=lambda b: b[1].start)
        out += [(b, row) for b in rb]
    return out


def resize_premul(rgba, scale):
    h, w = rgba.shape[:2]
    nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
    al = rgba[..., 3] / 255.0
    chans = []
    for c in range(3):
        im = Image.fromarray(rgba[..., c] * al, "F").resize((nw, nh), Image.BOX)
        chans.append(np.asarray(im))
    A = np.asarray(Image.fromarray(al, "F").resize((nw, nh), Image.BOX))
    out = np.zeros((nh, nw, 4), np.float32)
    nz = A > 1e-4
    for c in range(3):
        out[..., c] = np.where(nz, chans[c] / np.maximum(A, 1e-4), 0)
    out[..., 3] = A * 255.0
    return np.clip(out, 0, 255)


def body_anchor(alpha):
    """しっぽ・脚を除いた胴体+頭の重心 x と、足元 (最下端の不透明行)"""
    m = alpha > 128
    ys, xs = np.nonzero(m)
    bottom = ys.max()
    core = ndimage.binary_opening(m, structure=np.ones((13, 13)))
    if core.sum() < 50:
        core = m
    cy, cx = ndimage.center_of_mass(core)
    return cx, bottom


def make_talk_sit(W, H):
    """座って正面を向いたコマ (turn_sit の3コマ目) に口を描いて、座り口パク 4コマを作る。
    assets/raw/13_talk_sit.* があればそちらを使うべきだが、無い間の代わり。"""
    from PIL import ImageDraw
    src = np.asarray(Image.open(os.path.join(OUT, "turn_sit.png")).convert("RGBA"))[:, 2 * W:3 * W].copy()
    a = src[..., 3] > 128
    ys, xs = np.nonzero(a)
    top, bottom, cx_body = ys.min(), ys.max(), (xs.min() + xs.max()) / 2
    r, g, b = (src[..., i].astype(int) for i in range(3))
    pink = a & (r > 180) & (r - g > 40) & (r - b > 30)
    # 鼻 = 顔の中央付近・上半分にあるピンク（耳の内側のピンクは左右端なので除く）
    py, px = np.nonzero(pink)
    sel = (py < top + (bottom - top) * 0.45) & (np.abs(px - cx_body) < 12)
    if not sel.any():
        print("talk_sit: 鼻が見つからないので作りません")
        return
    nx = (px[sel].min() + px[sel].max()) / 2
    ny = py[sel].max()
    muzzle = tuple(int(v) for v in src[ny + 6, int(nx) - 10, :3])

    def frame(open_w, open_h):
        im = Image.fromarray(src, "RGBA")
        d = ImageDraw.Draw(im)
        if open_w == 0:
            return im
        # 元の「ω」の口を毛色で消してから、開いた口を描く
        d.rectangle([nx - 9, ny + 2, nx + 9, ny + 10], fill=muzzle + (255,))
        x0, y0 = nx - open_w / 2, ny + 3
        d.ellipse([x0, y0, x0 + open_w, y0 + open_h], fill=(60, 36, 44, 255))            # 口の中
        d.ellipse([x0 + 1.5, y0 + open_h * 0.5, x0 + open_w - 1.5, y0 + open_h - 0.5],
                  fill=(222, 110, 120, 255))                                                # 舌
        if open_h >= 7:                                                                     # 小さい牙
            d.rectangle([x0 + 2, y0 + 1, x0 + 3, y0 + 2], fill=(245, 243, 240, 255))
            d.rectangle([x0 + open_w - 3, y0 + 1, x0 + open_w - 2, y0 + 2], fill=(245, 243, 240, 255))
        return im

    frames = [frame(0, 0), frame(9, 6), frame(12, 10), frame(9, 6)]
    strip = Image.new("RGBA", (W * 4, H))
    for i, f in enumerate(frames):
        strip.paste(f, (i * W, 0))
    strip.save(os.path.join(OUT, "talk_sit.png"), optimize=True)
    print("talk_sit 4 frames (nose at %d,%d)" % (nx, ny))


def main():
    os.makedirs(OUT, exist_ok=True)
    src = {}
    for name, (fn, rows, drop) in SHEETS.items():
        rgba = load_rgba(os.path.join(RAW, fn))
        frames = find_frames(rgba, rows)
        frames = [f for i, f in enumerate(frames) if i not in drop]
        src[name] = (rgba, frames)
        print("%-10s %d frames" % (name, len(frames)))

    def areas(name):
        return [f[0][2] for f in src[name][1]]

    def heights(name):
        return [f[0][0].stop - f[0][0].start for f in src[name][1]]

    # 大きさの基準は最初の歩きシート (01_walk_old) に固定する。
    # 歩きを作り直しても他のアニメの大きさが変わらないように
    ref = find_frames(load_rgba(os.path.join(RAW, SCALE_REF)), 2)
    walk_area = np.mean([b[2] for b, row in ref])
    base = STAND_H / np.median([b[0].stop - b[0].start for b, row in ref])
    s = {}

    def match(name, area_x, ref_scale, area_ref):
        s[name] = ref_scale * np.sqrt(area_ref / area_x)

    match("walk", np.mean(areas("walk")), base, walk_area)
    match("idle", np.mean(areas("idle")), base, walk_area)
    for n in ("sit_down", "lie_down", "surprised", "turn"):
        match(n, areas(n)[0], base, walk_area)
    match("wake", areas("wake")[-1], base, walk_area)
    match("sit", np.mean(areas("sit")), s["sit_down"], areas("sit_down")[-1])
    match("blink", np.mean(areas("blink")), s["sit"], np.mean(areas("sit")))
    match("sleep", np.mean(areas("sleep")), s["lie_down"], areas("lie_down")[-1])
    match("talk", np.mean(areas("talk")), base, walk_area)
    match("turn_sit", areas("turn_sit")[0], s["sit"], np.mean(areas("sit")))
    match("drag", np.mean(areas("drag")), base, walk_area)
    for n in SHEETS:
        print("scale %-10s %.4f" % (n, s[n]))

    # 各コマを縮小し、基準点・足元・ジャンプ高さを求める
    done = {}
    for name, (rgba, frames) in src.items():
        sc = s[name]
        row_ground = {}
        for (ys, xs, area, lm), row in frames:
            row_ground[row] = max(row_ground.get(row, 0), ys.stop)
        items = []
        for (ys, xs, area, lm), row in frames:
            pad = 4
            y0, y1 = max(0, ys.start - pad), min(rgba.shape[0], ys.stop + pad)
            x0, x1 = max(0, xs.start - pad), min(rgba.shape[1], xs.stop + pad)
            crop = rgba[y0:y1, x0:x1].copy()
            # 隣のスプライトの切れ端を消す
            keep = np.zeros(crop.shape[:2], bool)
            keep[ys.start - y0:ys.stop - y0, xs.start - x0:xs.stop - x0] = lm
            keep = ndimage.binary_dilation(keep, iterations=2)
            crop[..., 3] *= keep
            small = resize_premul(crop, sc)
            cx, bottom = body_anchor(small[..., 3])
            lift = (row_ground[row] - ys.stop) * sc
            if lift < 10:
                lift = 0
            lift += LIFT.get(name, 0)
            items.append((small, cx, bottom, lift))
        done[name] = items

    # 全コマ共通のキャンバス（基準点を中心に左右対称）
    left = right = top = 0
    for items in done.values():
        for small, cx, bottom, lift in items:
            ys, xs = np.nonzero(small[..., 3] > 8)
            left = max(left, cx - xs.min())
            right = max(right, xs.max() + 1 - cx)
            top = max(top, bottom + lift - ys.min() + 1)
    half = int(np.ceil(max(left, right))) + 2
    W, H = half * 2, int(np.ceil(top)) + 2
    print("cell %dx%d" % (W, H))

    for name, items in done.items():
        strip = np.zeros((H, W * len(items), 4), np.float32)
        for i, (small, cx, bottom, lift) in enumerate(items):
            ox = int(round(half - cx)) + i * W
            oy = int(round(H - 1 - lift - bottom))
            h, w = small.shape[:2]
            # はみ出し対策でクリップ
            sx0, sy0 = max(0, -ox + i * W), max(0, -oy)
            dst = strip[max(0, oy):oy + h, max(i * W, ox):min((i + 1) * W, ox + w)]
            dst[...] = small[sy0:sy0 + dst.shape[0], sx0:sx0 + dst.shape[1]]
        img = Image.fromarray(strip.round().astype(np.uint8), "RGBA")
        img.save(os.path.join(OUT, name + ".png"), optimize=True)

    make_talk_sit(W, H)

    # 演出アイコン：6等分した列ごとに切り出し
    rgba = load_rgba(os.path.join(RAW, "11_effects.jpg"))
    cw = rgba.shape[1] // 6
    icons = []
    for i in range(6):
        col = rgba[:, i * cw:(i + 1) * cw].copy()
        m = col[..., 3] > 128
        ys, xs = np.nonzero(m)
        crop = col[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
        icons.append(resize_premul(crop, 48 / crop.shape[0]))
    IW = max(i.shape[1] for i in icons)
    strip = np.zeros((48, IW * 6, 4), np.float32)
    for i, ic in enumerate(icons):
        ox = i * IW + (IW - ic.shape[1]) // 2
        strip[:ic.shape[0], ox:ox + ic.shape[1]] = ic
    Image.fromarray(strip.round().astype(np.uint8), "RGBA").save(os.path.join(OUT, "effects.png"), optimize=True)
    print("effects cell %dx48" % IW)

    if PREVIEW:
        names = list(done)
        rows = []
        for bg in ((40, 40, 48), (235, 235, 235)):
            for n in names:
                im = Image.open(os.path.join(OUT, n + ".png"))
                canvas = Image.new("RGBA", (W * 9, H), bg + (255,))
                canvas.alpha_composite(im)
                rows.append(canvas)
        sheet = Image.new("RGBA", (W * 9, H * len(rows)))
        for i, r in enumerate(rows):
            sheet.paste(r, (0, i * H))
        sheet = sheet.resize((sheet.width * 2, sheet.height * 2), Image.NEAREST)
        sheet.save(os.path.join(os.environ.get("PREVIEW_DIR", OUT), "preview.png"))


if __name__ == "__main__":
    main()
