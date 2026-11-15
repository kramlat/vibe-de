#!/usr/bin/env python3
"""Draws the placeholder default wallpaper (a blue sky, soft clouds and a green hill) into a PNG, with numpy only.
usage: tools/make-placeholder-wallpaper.py [OUTPUT] [WIDTH HEIGHT]
It is a stand-in: real wallpapers are dropped into data/backgrounds/vde/ (any PNG/JPEG there is offered in Settings)."""
import struct, sys, zlib
import numpy as np

out = sys.argv[1] if len(sys.argv) > 1 else "data/backgrounds/vde/Vibe.png"
W, H = (int(sys.argv[2]), int(sys.argv[3])) if len(sys.argv) > 3 else (2560, 1440)
rng = np.random.default_rng(7)

def smooth_noise(w, h, cells_x, cells_y):
    """Value noise: random lattice, bicubic-ish (smoothstep) interpolation."""
    lat = rng.random((cells_y + 2, cells_x + 2))
    xs = np.linspace(0, cells_x, w, endpoint=False)
    ys = np.linspace(0, cells_y, h, endpoint=False)
    x0, y0 = xs.astype(int), ys.astype(int)
    fx, fy = xs - x0, ys - y0
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    a = lat[np.ix_(y0, x0)]; b = lat[np.ix_(y0, x0 + 1)]
    c = lat[np.ix_(y0 + 1, x0)]; d = lat[np.ix_(y0 + 1, x0 + 1)]
    top = a + (b - a) * fx[None, :]
    bot = c + (d - c) * fx[None, :]
    return top + (bot - top) * fy[:, None]

def fbm(w, h, base, octaves):
    total, amp, norm = np.zeros((h, w)), 1.0, 0.0
    for o in range(octaves):
        total += amp * smooth_noise(w, h, base * 2**o, max(2, int(base * 2**o * h / w)))
        norm += amp; amp *= 0.5
    return total / norm

yy = np.linspace(0, 1, H)[:, None]
xx = np.linspace(0, 1, W)[None, :]

# sky: deep blue at the top to a pale haze at the horizon
top, horizon = np.array([0.10, 0.36, 0.78]), np.array([0.62, 0.80, 0.96])
t = np.clip(yy / 0.66, 0, 1) ** 0.8
sky = top[None, None, :] * (1 - t[..., None]) + horizon[None, None, :] * t[..., None]
sky = np.broadcast_to(sky, (H, W, 3)).copy()

# clouds: thresholded noise, softer and thinner towards the horizon
n = fbm(W, H, 4, 6)
cover = np.clip((n - 0.50) / 0.20, 0, 1)
cover *= np.clip(1.15 - yy * 1.15, 0.15, 1)
cover = cover * cover * (3 - 2 * cover)
shade = np.clip(1.0 - 0.18 * fbm(W, H, 6, 4), 0, 1)
cloud = np.stack([shade] * 3, axis=-1) * np.array([1.0, 1.0, 1.0]) 
sky = sky * (1 - 0.92 * cover[..., None]) + cloud * (0.92 * cover[..., None])

# the hill: a long soft curve with a lit side and a little texture
edge = 0.60 + 0.07 * np.sin(xx * 2.1 + 0.6) - 0.20 * np.exp(-((xx - 0.12) ** 2) / 0.05) + 0.03 * np.sin(xx * 6.3)
edge = np.broadcast_to(edge, (H, W))
depth = np.clip((yy - edge) / 0.40, 0, 1)
lit = np.clip(0.5 + 0.9 * (0.55 - xx) + 0.5 * (0.15 - depth), 0, 1)
dark, light = np.array([0.10, 0.36, 0.08]), np.array([0.46, 0.74, 0.16])
grass = dark + (light - dark) * lit[..., None]
grass *= (1 + 0.10 * (fbm(W, H, 40, 3) - 0.5))[..., None]
a = np.clip((yy - edge) * H / 3 + 0.5, 0, 1)   # about three pixels of anti-aliasing
img = sky * (1 - a[..., None]) + grass * a[..., None]

# a little dither so the gradients do not band
img += (rng.random((H, W, 1)) - 0.5) / 255
rgb = (np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8)

def png(path, a):
    h, w, _ = a.shape
    a16 = a.astype(np.int16)
    left = np.concatenate([np.zeros((h, 1, 3), np.int16), a16[:, :-1]], axis=1)
    sub = ((a16 - left) % 256).astype(np.uint8)   # PNG filter 1 (Sub): smooth images compress far better
    raw = b"".join(b"\x01" + sub[y].tobytes() for y in range(h))
    def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))

png(out, rgb)
print(out, W, H)
