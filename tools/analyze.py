# assets/raw の各シートからスプライト（緑背景以外の塊）を検出して一覧表示する
import sys, glob, os
import numpy as np
from PIL import Image
from scipy import ndimage

def fg_mask(a):
    r, g, b = a[..., 0].astype(int), a[..., 1].astype(int), a[..., 2].astype(int)
    green = (g > 120) & (g - r > 50) & (g - b > 50)
    return ~green

def blobs(path, min_area=2000):
    a = np.asarray(Image.open(path).convert("RGB"))
    m = fg_mask(a)
    m = ndimage.binary_opening(m, iterations=2)          # JPEG ノイズ除去
    m2 = ndimage.binary_dilation(m, iterations=12)        # 尻尾など近い塊をつなぐ
    lab, n = ndimage.label(m2)
    out = []
    for i, sl in enumerate(ndimage.find_objects(lab), 1):
        area = int((m[sl] & (lab[sl] == i)).sum())
        if area >= min_area:
            out.append((sl[1].start, sl[0].start, sl[1].stop, sl[0].stop, area))
    # 行ごと → 左から
    out.sort(key=lambda b: (b[1] // (a.shape[0] // 4), b[0]))
    return a.shape, out

if __name__ == "__main__":
    for p in sorted(glob.glob(sys.argv[1] + "/*.jpg")):
        shape, bs = blobs(p)
        print(os.path.basename(p), shape[1], "x", shape[0], len(bs), "blobs")
        for b in bs:
            print("   x=%4d y=%4d w=%4d h=%4d area=%d" % (b[0], b[1], b[2] - b[0], b[3] - b[1], b[4]))
