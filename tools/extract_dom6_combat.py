"""Extract combat-relevant pages from the Dominions 6 manual to a text file.
Avoids loading the 18MB PDF into the agent context; writes a small searchable txt.
"""
import sys, re

PDF = r"C:\Users\Lucid\Desktop\dom6manual.pdf"
OUT = r"e:\Nightfall\NecrokingMG\tools\dom6_combat.txt"

KEYWORDS = [
    "open-ended", "open ended", "DRN", "2d6", "two dice",
    "attack value", "defence", "defense skill", "protection",
    "to hit", "fatigue", "encumbrance", "morale", "rout",
    "armour piercing", "armor piercing", "armour negating",
    "repel", "length of the weapon", "shield", "parry", "precision",
    "strength is added", "damage is", "magic resistance",
]

def load_pages():
    try:
        from pypdf import PdfReader
    except ImportError:
        from PyPDF2 import PdfReader
    r = PdfReader(PDF)
    return [(i, (p.extract_text() or "")) for i, p in enumerate(r.pages)]

def main():
    pages = load_pages()
    print(f"total pages: {len(pages)}")
    hits = []
    for i, txt in pages:
        low = txt.lower()
        score = sum(low.count(k) for k in KEYWORDS)
        if score >= 3:
            hits.append((score, i))
    hits.sort(reverse=True)
    # pick the densest cluster of combat pages, then expand to neighbors
    chosen = sorted({i for _, i in hits[:18]})
    # also include immediate neighbors for continuity
    expanded = sorted({j for i in chosen for j in (i-1, i, i+1) if 0 <= j < len(pages)})
    with open(OUT, "w", encoding="utf-8") as f:
        for i in expanded:
            f.write(f"\n\n===== PAGE {i} =====\n")
            f.write(pages[i][1])
    print(f"combat-dense pages: {chosen}")
    print(f"wrote {len(expanded)} pages to {OUT}")

if __name__ == "__main__":
    main()
