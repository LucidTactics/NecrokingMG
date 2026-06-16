"""Faithful BC3 (DXT5) encode/decode to visualize the compression quality tradeoff.
DXT5 = 4:1 of uncompressed RGBA. This is the *worst realistic* case at that ratio;
BC7 (same 4:1) uses better block modes and looks noticeably cleaner. So if DXT5 is
acceptable here, BC7 certainly is.
"""
import numpy as np
from PIL import Image
import sys, os

def quant565(c):
    # c: (...,3) float 0..255 -> rounded to RGB565 and back to 8-bit (what the GPU stores)
    r = np.round(c[...,0]/255*31); g = np.round(c[...,1]/255*63); b = np.round(c[...,2]/255*31)
    r=np.clip(r,0,31); g=np.clip(g,0,63); b=np.clip(b,0,31)
    return np.stack([r/31*255, g/63*255, b/31*255], -1), (r.astype(int)<<11)|(g.astype(int)<<5)|b.astype(int)

def encode_color_block(px):
    # px: (16,3) RGB floats. Range-fit endpoints along principal axis (like real encoders).
    mean = px.mean(0)
    d = px - mean
    cov = d.T @ d
    # power iteration for principal axis
    v = np.array([1.0,1.0,1.0])
    for _ in range(8):
        v = cov @ v
        n = np.linalg.norm(v)
        if n < 1e-9: break
        v = v/n
    proj = d @ v
    c0 = px[np.argmax(proj)].copy()
    c1 = px[np.argmin(proj)].copy()
    # quantize endpoints to 565 (the actual stored precision)
    c0q = quant565(c0[None])[0][0]; c1q = quant565(c1[None])[0][0]
    # 4-color palette: c0, c1, 2/3..1/3, 1/3..2/3
    pal = np.stack([c0q, c1q, (2*c0q+c1q)/3, (c0q+2*c1q)/3], 0)  # (4,3)
    # assign nearest
    dist = ((px[:,None,:]-pal[None,:,:])**2).sum(-1)  # (16,4)
    idx = dist.argmin(1)
    return pal[idx]

def encode_alpha_block(a):
    # a: (16,) alpha floats. BC4 8-value mode: a0=max,a1=min,6 interpolated.
    a0 = a.max(); a1 = a.min()
    if a0==a1:
        return np.full(16, a0)
    pal = np.array([a0, a1] + [((7-i)*a0 + i*a1)/7 for i in range(1,7)])  # 8 levels
    idx = (np.abs(a[:,None]-pal[None,:])).argmin(1)
    return pal[idx]

def dxt5(img):
    h,w = img.shape[:2]
    ph = (h+3)//4*4; pw=(w+3)//4*4
    pad = np.zeros((ph,pw,4), np.float32); pad[:h,:w]=img
    out = pad.copy()
    for by in range(0,ph,4):
        for bx in range(0,pw,4):
            blk = pad[by:by+4, bx:bx+4, :].reshape(16,4)
            rgb = encode_color_block(blk[:,:3])
            al  = encode_alpha_block(blk[:,3])
            out[by:by+4, bx:bx+4, :3] = rgb.reshape(4,4,3)
            out[by:by+4, bx:bx+4, 3]  = al.reshape(4,4)
    return out[:h,:w]

def composite_on(img, bg):
    a = img[...,3:4]/255
    return img[...,:3]*a + bg*(1-a)

def upscale(arr, f):
    return np.array(Image.fromarray(arr.astype(np.uint8)).resize(
        (arr.shape[1]*f, arr.shape[0]*f), Image.NEAREST))

def process(src_img, name, scale, outdir):
    orig = src_img.astype(np.float32)
    comp = dxt5(orig)
    h,w = orig.shape[:2]
    bg = np.full((h,w,3), 120, np.float32)  # mid gray so light+dark sprites both show
    o_rgb = composite_on(orig, bg)
    c_rgb = composite_on(comp, bg)
    # diff x8 (RGB+alpha error)
    diff = np.abs(orig-comp)
    diff_vis = np.clip(diff[...,:3].max(-1,keepdims=True)*8, 0,255).repeat(3,-1)
    # stats
    mse = ((orig-comp)**2).mean()
    psnr = 10*np.log10(255**2/mse) if mse>0 else 99
    rawbytes = h*w*4
    dxtbytes = ((h+3)//4)*((w+3)//4)*16
    # build side-by-side (upscaled nearest)
    panels = [upscale(o_rgb,scale), upscale(c_rgb,scale), upscale(diff_vis,scale)]
    gap = np.full((panels[0].shape[0], 8, 3), 30, np.uint8)
    row = np.concatenate([panels[0],gap,panels[1],gap,panels[2]],1)
    Image.fromarray(row).save(os.path.join(outdir,f'{name}_compare.png'))
    print(f'{name}: {w}x{h}  PSNR={psnr:.1f}dB  raw(VRAM)={rawbytes/1024:.0f}KB  DXT5={dxtbytes/1024:.0f}KB ({rawbytes/dxtbytes:.1f}:1)')
    return psnr

outdir='log/bc_compare'; os.makedirs(outdir,exist_ok=True)
# Tree
tree=np.array(Image.open('assets/Environment/Trees/TwistedTree.png').convert('RGBA'))
process(tree,'tree_twisted',6,outdir)
# Character frame: crop Pikemen Panic from Navarre_Units atlas
atlas=Image.open('assets/Sprites/Navarre_Units.png').convert('RGBA')
x,y,w,h=7987,3081,90,117
char=np.array(atlas.crop((x,y,x+w,y+h)))
process(char,'char_pikemen',5,outdir)
print('saved to',outdir)
