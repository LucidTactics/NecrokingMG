"""Bake all display-size textures for CommanderTaskEquipmentBox + Stat Tooltip.

Per the importing-unity-ui skill: every minified/tiled/magnified Unity image is
pre-baked at exact display size (premultiplied-aware LANCZOS). Magnified ICONS
use bake_unity_icon instead (alphaIsTransparency dilation + straight bilinear,
matching Unity's GPU sampling — premul resize lightens dark silhouette rims).
"""
import numpy as np
from PIL import Image

from bake_unity_icon import bake as bake_icon


def resize_premul(img, size):
    if img.size == size:
        return img.convert('RGBA')
    a = np.asarray(img.convert('RGBA')).astype(np.float32)
    rgb, alpha = a[..., :3], a[..., 3:4]
    pm = np.concatenate([rgb * alpha / 255.0, alpha], axis=-1)
    pm_img = Image.fromarray(np.uint8(pm + 0.5), 'RGBA').resize(size, Image.LANCZOS)
    b = np.asarray(pm_img).astype(np.float32)
    oa = b[..., 3:4]
    safe = np.maximum(oa, 1e-3)
    return Image.fromarray(np.uint8(np.concatenate([np.clip(b[..., :3] * 255.0 / safe, 0, 255), oa], axis=-1) + 0.5), 'RGBA')


def bake_nine(src, dw, dh, sb, db, out):
    sl, sbm, sr, st = sb
    dl, dbm, dr, dt = db
    w, h = src.size
    sx = [0, sl, w - sr, w]
    sy = [0, st, h - sbm, h]
    dx = [0, dl, dw - dr, dw]
    dy = [0, dt, dh - dbm, dh]
    o = Image.new('RGBA', (dw, dh), (0, 0, 0, 0))
    for ry in range(3):
        for rx in range(3):
            sw, sh = sx[rx + 1] - sx[rx], sy[ry + 1] - sy[ry]
            dw2, dh2 = dx[rx + 1] - dx[rx], dy[ry + 1] - dy[ry]
            if sw <= 0 or sh <= 0 or dw2 <= 0 or dh2 <= 0:
                continue
            o.paste(resize_premul(src.crop((sx[rx], sy[ry], sx[rx + 1], sy[ry + 1])), (dw2, dh2)), (dx[rx], dy[ry]))
    o.save(out)


def fit_canvas(src_path, cw, ch, out_path, alpha_boost=1.0):
    im = Image.open(src_path).convert('RGBA')
    w, h = im.size
    s = min(cw / w, ch / h)
    nw, nh = max(1, round(w * s)), max(1, round(h * s))
    fitted = resize_premul(im, (nw, nh))
    if alpha_boost != 1.0:
        a = np.asarray(fitted).astype(np.float32)
        a[..., 3] = np.clip(a[..., 3] * alpha_boost, 0, 255)
        fitted = Image.fromarray(np.uint8(a + 0.5), 'RGBA')
    canvas = Image.new('RGBA', (cw, ch), (0, 0, 0, 0))
    canvas.paste(fitted, ((cw - nw) // 2, (ch - nh) // 2))
    canvas.save(out_path)


# --- Commander box ---
cf2i = Image.open('assets/UI/Frames/CircleFrame2Inner.png').convert('RGBA')
CF2I_SRC = (126, 133, 129, 129)  # L,B,R,T from sprite meta
for w, h in [(75, 75), (108, 60), (101, 60), (114, 60)]:
    bake_nine(cf2i, w, h, CF2I_SRC, (4, 4, 4, 4), f'assets/UI/Frames/CF2I_{w}x{h}.png')
cloth = Image.open('assets/UI/Frames/ClothUpgradeThinFrame.png').convert('RGBA')
CLOTH_SRC = (40, 40, 40, 40)
for w, h in [(75, 75), (108, 60), (101, 60), (114, 60)]:
    bake_nine(cloth, w, h, CLOTH_SRC, (27, 27, 27, 27) if h > 60 else (27, 22, 27, 22),
              f'assets/UI/Frames/Cloth_{w}x{h}.png')
# Slot ButtonHighlight: cloth frame inset (-9,-11), PPUMult 4.49
# dst border = 40 * 100 / (64 * 4.49) = 13.9 ~ 14
bake_nine(cloth, 66, 64, CLOTH_SRC, (14, 14, 14, 14), 'assets/UI/Frames/Cloth_66x64.png')

# Slot TabStencil: SimpleThatchPattern 570x867 TILED at PPUMult 19.27
# tile size units = px * 100 / (64 * 19.27) = px * 0.0811 -> 46.2 x 70.3
thatch = Image.open('assets/UI/Patterns/SimpleThatchPattern.png').convert('RGBA')
tile = resize_premul(thatch, (46, 70))
o = Image.new('RGBA', (75, 75), (0, 0, 0, 0))
for ty in (75 - 70, 75 - 140):  # UV origin bottom-left
    for tx in (0, 46):
        o.paste(tile, (tx, ty))
o.save('assets/UI/Patterns/Thatch_75x75.png')

# Title underbar: RenaiThinBar 17x171 rotated -90deg, drawn 304x5. GPU bilinear
# MINIFICATION samples only 2 adjacent texels per output row (not an area
# average) — emulate it: dilate RGB, then lerp at the 5 GPU sample positions.
from bake_unity_icon import dilate_rgb
bar = Image.open('assets/UI/Bars/RenaiThinBar.png').convert('RGBA').rotate(-90, expand=True)
ba = dilate_rgb(np.asarray(bar)).astype(np.float32)  # 17 rows x 171 cols
rows = []
for i in range(5):
    sp = (i + 0.5) * 17 / 5 - 0.5
    f, t = int(np.floor(sp)), sp - np.floor(sp)
    rows.append(ba[f] * (1 - t) + ba[min(f + 1, 16)] * t)
bar5 = Image.fromarray(np.uint8(np.stack(rows) + 0.5), 'RGBA')
bar5.resize((304, 5), Image.BILINEAR).save('assets/UI/Bars/RenaiThinBar_304x5.png')

fit_canvas('assets/UI/Icons/Actions/Stats.png', 39, 36, 'assets/UI/Icons/Actions/Stats_39x36.png')
fit_canvas('assets/UI/Icons/Actions/Attack2.png', 39, 36, 'assets/UI/Icons/Actions/Attack2_39x36.png')
fit_canvas('assets/UI/Icons/Actions/Attack.png', 39, 36, 'assets/UI/Icons/Actions/Attack_39x36.png')
# Portrait outline is baked in TEXTURE space (155x200, th 3 texels) and then
# downscaled with the sprite — Unity's shader outline scales with the draw
# (3 texels * 0.455 = ~1.4 display px); baking at display size doubled it.
from bake_unity_icon import bake_outline
su = np.asarray(Image.open('assets/UI/Portraits/SampleUnit.png').convert('RGBA'))
# 0.416 is Unity's value; our LANCZOS downscale dilutes the ring more than
# GPU 2-texel sampling, so compensate to match the measured halo profile.
su = bake_outline(su, 3, (255, 247, 242), 0.62)
Image.fromarray(su, 'RGBA').save('assets/UI/Portraits/SampleUnit_outlined.png')
fit_canvas('assets/UI/Portraits/SampleUnit_outlined.png', 88, 91, 'assets/UI/Portraits/SampleUnit_88x91.png')
# DiffuseParticleSprite is an RGB luminance ramp; its .meta has alphaUsage: 2
# (FromGrayScale) — Unity builds the alpha from luminance. Plain resize gives
# an opaque rectangle.
dif = np.asarray(Image.open('assets/UI/Misc/DiffuseParticleSprite.png').convert('RGB')).astype(np.float32)
lum = (dif[..., 0] * 0.299 + dif[..., 1] * 0.587 + dif[..., 2] * 0.114)
dif_rgba = np.empty(dif.shape[:2] + (4,), np.uint8)
dif_rgba[..., :3] = 255
dif_rgba[..., 3] = np.uint8(lum + 0.5)
resize_premul(Image.fromarray(dif_rgba, 'RGBA'), (149, 34)).save('assets/UI/Misc/Diffuse_149x34.png')

# Blacksmith tiled overlay: tile = rect width (308 units); true rect sits at
# x 25 (center-x 179, pivot 0.5) so only cols 0..287 are inside the window —
# bake just the visible portion (895 * 287/308 = 834 src cols).
bs = Image.open('assets/UI/Background/Blacksmith.jpg').convert('RGBA')
crop = bs.crop((0, 1024 - 715, 834, 1024))
resize_premul(crop, (287, 246)).save('assets/UI/Background/blacksmith_cb_287x246.png')

# Swatch1.3's bottom 3 source rows are a transparent tail — crop it so the
# baked bottom border is the opaque dark edge (1px, lands under the title bar).
s13 = Image.open('assets/UI/Ribbons/Swatch1.3.png').convert('RGBA').crop((0, 0, 337, 89))
bake_nine(s13, 304, 56, (10, 5, 7, 9), (3, 1, 2, 3), 'assets/UI/Ribbons/Swatch1.3_304x56.png')

# --- Stat tooltip ---
dp = Image.open('assets/UI/Background/dragonpattern-transparent.png').convert('RGBA')
crop = dp.crop((0, 1536 - 244, 526, 1536))
resize_premul(crop, (222, 103)).save('assets/UI/Background/dragonpattern_stattip_222x103.png')
gb = Image.open('assets/UI/Bars/goldbar.png').convert('RGBA')
resize_premul(gb, (173, 3)).save('assets/UI/Bars/goldbar_h_173x3.png')
bake_icon('assets/UI/Icons/SturmIcons/SturmStrength24.png', 36, 36, 'assets/UI/Icons/SturmIcons/SturmStrength_36.png')
Image.new('RGBA', (4, 4), (255, 255, 255, 255)).save('assets/UI/Misc/white4.png')

# --- Stat tooltip icons: 36px versions of the 24px stat icons, with the
# Unity-style texture-space outline pre-baked (matches the Strength icon) ---
from bake_unity_icon import bake_outline, dilate_rgb as _dil
STAT_ICONS = {
    'hp': 'assets/UI/Icons/NewIcons/Health24.png',
    'morale': 'assets/UI/Icons/SturmIcons/morale2_24.png',
    'size': 'assets/UI/Icons/NewIcons/Size324.png',
    'toughness': 'assets/UI/Icons/NewIcons/Tough24.png',
    'magicpower': 'assets/UI/Icons/NewIcons/MagicWand48.png',
    'magicres': 'assets/UI/Icons/SturmIcons/Spirit2_24.png',
    'strength': 'assets/UI/Icons/SturmIcons/SturmStrength24.png',
    'protection': 'assets/UI/Icons/NewIcons/Prot24.png',
    'shield': 'assets/UI/Icons/NewIcons/Coverage24.png',
    'attack': 'assets/UI/Icons/NewIcons/Attack24.png',
    'defense': 'assets/UI/Icons/NewIcons/Defense24.png',
    'parry': 'assets/UI/Icons/NewIcons/Parry24.png',
    'speed': 'assets/UI/Icons/NewIcons/Speed24.png',
    'encumbrance': 'assets/UI/Icons/NewIcons/Enc24.png',
    'upkeep': 'assets/UI/Icons/NewIcons/Gold24.png',
}
for key, src_p in STAT_ICONS.items():
    arr = np.asarray(Image.open(src_p).convert('RGBA'))
    arr = bake_outline(arr, 0.5, (51, 40, 40), 0.88)
    arr = dilate_rgb(arr)
    Image.fromarray(arr, 'RGBA').resize((36, 36), Image.BILINEAR).save(
        f'assets/UI/Icons/StatTips/{key}_36.png')

# --- Resource HUD tooltip (222x231) ---
bake_icon('assets/UI/Icons/Population/Humans24.png', 36, 36, 'assets/UI/Icons/Population/Humans_36.png')
# Dragon stencil: tiled PPUMult 3.7 -> tile 222.1 x 648.7 units (width = window
# width by design); 231 units tall = bottom 547 src rows.
crop = dp.crop((0, 1536 - 547, 526, 1536))
resize_premul(crop, (222, 231)).save('assets/UI/Background/dragonpattern_rt_222x231.png')
resize_premul(gb, (140, 3)).save('assets/UI/Bars/goldbar_h_140x3.png')
# Tabulation box: parchment (Simple, stretched), thatch stencil (tiled PPUMult
# 12.4 -> tile 71.9 x 109.3), RenaiThinFrame nine (PPUMult 6.31 -> border 4).
par = Image.open('assets/UI/Patterns/Parchment-2-Tile.psd').convert('RGBA')
resize_premul(par, (205, 123)).save('assets/UI/Patterns/Parchment_205x123.png')
t2 = resize_premul(thatch, (72, 109))
o = Image.new('RGBA', (206, 123), (0, 0, 0, 0))
for ty in (123 - 109, 123 - 218):
    for tx in (0, 72, 144):
        o.paste(t2, (tx, ty))
o.save('assets/UI/Patterns/Thatch_206x123.png')
renai = Image.open('assets/UI/Frames/RenaiThinFrame.png').convert('RGBA')
bake_nine(renai, 207, 125, (16, 16, 16, 16), (4, 4, 4, 4), 'assets/UI/Frames/Renai_207x125.png')

# --- Dynamic resource tooltip (RTD_, auto-size layout) ---
# Image layers are baked at the widget's MAX height; the renderer crops from
# the top when the measured height is shorter (1:1 pixels, no resample).
# Box max: 12 rows x 24 = 288 (layers inset 2 -> 284). Root max: 396.
resize_premul(par, (203, 284)).save('assets/UI/Patterns/Parchment_203x284.png')
t3 = resize_premul(thatch, (72, 109))
o = Image.new('RGBA', (203, 284), (0, 0, 0, 0))
ty = 284
while ty > -109:
    ty -= 109
    for tx in (0, 72, 144):
        o.paste(t3, (tx, ty))
o.save('assets/UI/Patterns/Thatch_203x284.png')
crop = dp.crop((0, 1536 - 936, 526, 1536))  # bottom 396/648.7 of the tile
resize_premul(crop, (222, 396)).save('assets/UI/Background/dragonpattern_rtd_222x396.png')

print('all extra-panel textures baked')
