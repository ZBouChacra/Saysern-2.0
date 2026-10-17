"""How a front-end phrase becomes a text key. Must match uiKey() in web/src/texts.ts and admin/src/texts.ts (a unit test pins one long example)."""
import struct

def ui_key(english: str) -> str:
    if len(english) <= 140:
        return "ui:" + english
    h = 0x811C9DC5                                   # FNV-1a over the UTF-16 code units, like the browser sees them
    units = struct.unpack("<%dH" % (len(english.encode("utf-16-le")) // 2), english.encode("utf-16-le"))
    for u in units:
        h = ((h ^ u) * 0x01000193) & 0xFFFFFFFF
    return "ui:" + english[:110] + "…#" + format(h, "08x")
