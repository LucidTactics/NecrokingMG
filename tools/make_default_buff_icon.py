from PIL import Image, ImageDraw
import os, math
os.makedirs("assets/UI/Icons/Buffs", exist_ok=True)
S = 96
im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
# Dark rounded token with a parchment-gold rim.
d.ellipse([6, 6, S-6, S-6], fill=(38, 32, 46, 235), outline=(150, 130, 80, 255), width=4)
# Four-point sparkle in warm gold.
cx, cy = S/2, S/2
gold = (224, 196, 120, 255)
for a in range(4):
    ang = math.radians(a*90)
    lx, ly = math.cos(ang), math.sin(ang)
    px, py = -ly, lx  # perpendicular
    tip = (cx + lx*30, cy + ly*30)
    b1 = (cx + px*7, cy + py*7)
    b2 = (cx - px*7, cy - py*7)
    d.polygon([tip, b1, (cx, cy), b2], fill=gold)
d.ellipse([cx-6, cy-6, cx+6, cy+6], fill=(255, 238, 190, 255))
im.save("assets/UI/Icons/Buffs/_default.png")
print("wrote assets/UI/Icons/Buffs/_default.png")
