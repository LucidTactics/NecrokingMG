"""Stamp `primaryPath: death, primaryLevel: 1` on every spell in data/spells.json.

Single-shot placeholder while the path system rolls out — every spell currently
gates on Death 1 so the gameplay loop is testable. Targeted regex injection:
inserts the two new keys right after the existing `"manaCost"` line per spell,
matching the surrounding indent style. Idempotent — re-runs are no-ops because
we skip blocks where the keys are already present.
"""
import re
from pathlib import Path


def main():
    repo = Path(__file__).resolve().parent.parent
    path = repo / "data" / "spells.json"
    text = path.read_text(encoding="utf-8")

    # Match the manaCost line (capturing its indent + trailing comma/newline)
    # and insert primaryPath/primaryLevel just below with the same indent.
    pattern = re.compile(r"^(\s*)\"manaCost\":\s*[-\d.]+,\s*\n", re.MULTILINE)

    inserts = 0

    def inject(m: re.Match) -> str:
        nonlocal inserts
        indent = m.group(1)
        # Look at the next ~1KB after this match to see if primaryPath is already there.
        end = m.end()
        peek = text[end:end + 500]
        if "primaryPath" in peek and peek.find("primaryPath") < peek.find("\"id\"") if "\"id\"" in peek else "primaryPath" in peek:
            return m.group(0)  # already tagged
        inserts += 1
        return (
            m.group(0)
            + f"{indent}\"primaryPath\": \"death\",\n"
            + f"{indent}\"primaryLevel\": 1,\n"
        )

    new_text = pattern.sub(inject, text)

    if new_text == text:
        print("No changes (already tagged?)")
        return

    path.write_text(new_text, encoding="utf-8")
    print(f"Tagged {inserts} spells with primaryPath=death, primaryLevel=1")


if __name__ == "__main__":
    main()
