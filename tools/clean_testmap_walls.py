import os, shutil, sys
path = "assets/maps/testmap.json"
bak  = path + ".bak"
buf = open(path, "rb").read()
prot = buf.count(b'"protection"')
print("original: %.1f MB, protection entries: %d" % (len(buf)/1024/1024, prot))

kpos = buf.find(b'"walls"')
if kpos < 0:
    print("no walls key; nothing to do"); sys.exit(0)
open_br = buf.find(b'[', kpos)
i = open_br + 1
in_str = False; esc = False
close_br = -1
while i < len(buf):
    c = buf[i]
    if in_str:
        if esc: esc = False
        elif c == 0x5C: esc = True
        elif c == 0x22: in_str = False
    else:
        if c == 0x22: in_str = True
        elif c == 0x5D:
            close_br = i; break
    i += 1
assert close_br > open_br, "could not find walls array end"

new = buf[:open_br+1] + buf[close_br:]
print("cleaned:  %.1f MB" % (len(new)/1024/1024))

if not os.path.exists(bak):
    shutil.copyfile(path, bak)
    print("backup written:", bak)
else:
    print("backup already exists:", bak)
open(path, "wb").write(new)
print("wrote cleaned testmap.json")
