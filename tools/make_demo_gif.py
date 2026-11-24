# Démo simulée de Tchat Overlay (rendu illustratif, pas une capture réelle)
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import math, random, sys

W, H = 800, 450
FPS = 10
F = "/usr/share/fonts/truetype/dejavu/"
font = ImageFont.truetype(F + "DejaVuSans.ttf", 13)
bold = ImageFont.truetype(F + "DejaVuSans-Bold.ttf", 13)
small = ImageFont.truetype(F + "DejaVuSans.ttf", 11)
smallb = ImageFont.truetype(F + "DejaVuSans-Bold.ttf", 11)
cap = ImageFont.truetype(F + "DejaVuSans-Bold.ttf", 15)
keyf = ImageFont.truetype(F + "DejaVuSans-Bold.ttf", 18)
ital = ImageFont.truetype(F + "DejaVuSans-Oblique.ttf", 12)

# ---------- Fond « jeu » générique (paysage néon) ----------
def make_background():
    bg = Image.new("RGB", (W, H))
    d = ImageDraw.Draw(bg)
    for y in range(H):
        t = y / H
        r = int(18 + 40 * t); g = int(10 + 8 * t); b = int(40 + 50 * (1 - abs(t - 0.5) * 2))
        d.line([(0, y), (W, y)], fill=(r, g, b))
    # soleil
    for i in range(60, 0, -1):
        c = (255, 80 + i, 150 + i // 2)
        d.ellipse([560 - i, 150 - i, 560 + i, 150 + i], fill=c if i < 58 else (120, 40, 110))
    for k in range(6):
        y = 160 + k * 9
        d.rectangle([490, y, 630, y + 3 + k // 2], fill=(40, 18, 70))
    # montagnes
    random.seed(3)
    pts = [(0, 260)]
    x = 0
    while x < W:
        x += random.randint(40, 90)
        pts.append((x, random.randint(185, 250)))
    pts += [(W, 260), (W, 270), (0, 270)]
    d.polygon(pts, fill=(28, 14, 52))
    # sol + grille
    d.rectangle([0, 262, W, H], fill=(14, 6, 30))
    for i in range(-20, 21):
        d.line([(W / 2 + i * 12, 262), (W / 2 + i * 90, H)], fill=(170, 40, 200), width=1)
    y = 262; step = 4
    while y < H:
        d.line([(0, y), (W, y)], fill=(170, 40, 200))
        y += step; step *= 1.35
    # HUD de jeu (barre de vie, mini-carte)
    d.rounded_rectangle([W - 200, 18, W - 20, 34], 4, fill=(30, 30, 40), outline=(220, 220, 230))
    d.rounded_rectangle([W - 198, 20, W - 70, 32], 3, fill=(60, 210, 120))
    d.ellipse([W - 110, 300, W - 20, 390], fill=(20, 30, 40), outline=(200, 200, 210), width=2)
    d.ellipse([W - 68, 342, W - 62, 348], fill=(255, 220, 60))
    d.text((W - 200, 38), "TOUR 2/3   1:42.38", font=smallb, fill=(235, 235, 245))
    return bg

BG = make_background()

# ---------- Messages ----------
USERS = {
    "Luna42": (255, 105, 180), "PixelFox": (30, 144, 255), "Viewer_Neko": (154, 205, 50),
    "Spammer123": (255, 127, 80), "Azury_Neko": (0, 230, 255),
}

def mlen(fnt, s):
    return fnt.getlength(s.replace("<E>", "")) + 20 * s.count("<E>")

def wrap(text, fnt, width, first_offset):
    words = text.split(" "); lines = []; cur = ""; off = first_offset
    for w in words:
        test = (cur + " " + w).strip()
        if mlen(fnt, test) + off > width and cur:
            lines.append((cur, off)); cur = w; off = 0
        else:
            cur = test
    lines.append((cur, off))
    return lines

CHAT_X, CHAT_W, CHAT_BOTTOM, CHAT_TOP = 20, 330, 392, 70

def bubble_image(msg, highlight=False):
    """Rend une bulle de message en RGBA."""
    pad = 7; av = 20
    if msg.get("system"):
        lines = wrap(msg["text"], ital, CHAT_W - 2 * pad, 0)
        h = pad * 2 + 16 * len(lines)
        im = Image.new("RGBA", (CHAT_W, h), (0, 0, 0, 0))
        d = ImageDraw.Draw(im)
        d.rounded_rectangle([0, 0, CHAT_W - 1, h - 1], 6, fill=(0, 0, 0, 150))
        for i, (ln, _) in enumerate(lines):
            d.text((pad, pad + i * 16), ln, font=ital, fill=(205, 205, 205))
        return im
    name = msg["user"]; col = USERS[name]
    name_w = bold.getlength(name) + font.getlength(" : ")
    first = av + 6 + name_w
    text = msg["text"]
    lines = wrap(text, font, CHAT_W - 2 * pad, first)
    h = max(pad * 2 + 17 * len(lines), pad * 2 + av)
    im = Image.new("RGBA", (CHAT_W, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, CHAT_W - 1, h - 1], 6, fill=(0, 0, 0, 155),
                        outline=(0, 200, 255, 255) if highlight else None, width=1)
    # avatar rond avec initiale
    ax, ay = pad, pad - 2
    d.ellipse([ax, ay, ax + av, ay + av], fill=tuple(int(c * 0.75) for c in col) + (255,))
    d.text((ax + av / 2, ay + av / 2), name[0], font=smallb, fill=(255, 255, 255), anchor="mm")
    nx = ax + av + 6
    d.text((nx + 1, pad + 1), name, font=bold, fill=(0, 0, 0))
    d.text((nx, pad), name, font=bold, fill=col)
    d.text((nx + bold.getlength(name), pad), " : ", font=font, fill=(255, 255, 255))
    for i, (ln, off) in enumerate(lines):
        x = pad + off; y = pad + i * 17
        parts = ln.split("<E>")
        cx = x
        for k, part in enumerate(parts):
            d.text((cx + 1, y + 1), part, font=font, fill=(0, 0, 0))
            d.text((cx, y), part, font=font, fill=(255, 255, 255))
            cx += font.getlength(part)
            if k == len(parts) - 1: break
            ex = cx + 1
            cx += 20
            d.ellipse([ex, y - 1, ex + 18, y + 17], fill=(255, 204, 0), outline=(150, 90, 0))
            d.ellipse([ex + 5, y + 4, ex + 8, y + 8], fill=(60, 30, 0))
            d.ellipse([ex + 11, y + 4, ex + 14, y + 8], fill=(60, 30, 0))
            d.arc([ex + 4, y + 5, ex + 14, y + 14], 20, 160, fill=(60, 30, 0), width=2)
    return im

# ---------- Chronologie ----------
events = []          # (frame, action, data)
msgs = []            # messages actifs : dict(user,text,start,removed_at)

def add(f, user, text, system=False):
    events.append((f, "add", dict(user=user, text=text, system=system)))

add(4, "Luna42", "Salut tout le monde !")
add(14, "PixelFox", "Tu joues à quoi ce soir ?")
add(24, "Viewer_Neko", "GG <E> trop fort ce virage")
add(34, "Spammer123", "ACHETEZ DES FOLLOWERS ICI !!! lien-douteux")
add(44, "Luna42", "Attention au dernier tour")

STEP2, TYPE1_START, SEND1 = 56, 66, 92
TEXT1 = "Ce soir : course néon jusqu'à minuit"
add(SEND1 + 1, "Azury_Neko", TEXT1)

STEP3 = 104
RCLICK, HOVER_TO, SUB_HOVER, CLICK_TO = 117, 122, 128, 133
events.append((CLICK_TO, "remove", "Spammer123"))
add(CLICK_TO + 1, None, "Spammer123 : timeout 10 min.", system=True)

STEP4 = 146
LCLICK = 156
TYPE2_START, SEND2 = 162, 184
TEXT2 = "Une course de chats néon"
add(SEND2 + 1, "Azury_Neko", "@PixelFox " + TEXT2)
END = 205

def caption_for(f):
    if f < STEP2: return "Tchat Twitch par-dessus le jeu (clics traversants)"
    if f < STEP3: return "Pro — Ctrl+Shift+F9 : barre de réponse"
    if f < STEP4: return "Pro — Ctrl+Shift+F8 : mode interactif, clic droit = modération"
    return "Pro — clic sur un message = répondre"

def keycap(d, f, start, label):
    if start <= f < start + 9:
        w = keyf.getlength(label) + 30
        x = (W - w) / 2; y = 175
        d.rounded_rectangle([x, y, x + w, y + 40], 8, fill=(20, 20, 28, 230), outline=(145, 70, 255, 255), width=2)
        d.text((W / 2, y + 20), label, font=keyf, fill=(255, 255, 255), anchor="mm")

def draw_cursor(d, x, y):
    pts = [(x, y), (x, y + 18), (x + 5, y + 14), (x + 9, y + 22), (x + 12, y + 20), (x + 8, y + 12), (x + 14, y + 12)]
    d.polygon(pts, fill=(255, 255, 255), outline=(0, 0, 0))

def reply_bar(d, text, header, caret):
    bw = 440; x = (W - bw) / 2; y = 398 - (22 if header else 0)
    h = 44 + (22 if header else 0)
    d.rounded_rectangle([x, y, x + bw, y + h], 6, fill=(24, 24, 24, 235))
    iy = y + 8
    if header:
        d.text((x + 10, y + 7), header, font=small, fill=(230, 230, 230))
        d.text((x + bw - 18, y + 6), "x", font=smallb, fill=(200, 200, 200))
        iy += 22
    d.rounded_rectangle([x + 8, iy, x + bw - 176, iy + 28], 3, fill=(70, 70, 70, 255), outline=(145, 70, 255, 255))
    d.text((x + 14, iy + 7), text + ("|" if caret else ""), font=font, fill=(255, 255, 255))
    d.rounded_rectangle([x + bw - 168, iy, x + bw - 98, iy + 28], 3, fill=(60, 60, 66, 255))
    d.text((x + bw - 133, iy + 14), "Envoyer", font=small, fill=(255, 255, 255), anchor="mm")
    d.rounded_rectangle([x + bw - 92, iy, x + bw - 8, iy + 28], 3, fill=(60, 60, 66, 255))
    d.text((x + bw - 50, iy + 14), "Modération", font=small, fill=(255, 255, 255), anchor="mm")

MENU_ITEMS = ["Répondre à @Spammer123", "Copier le message", "Copier le pseudo", "-",
              "Supprimer le message", "Timeout @Spammer123  >", "Bannir @Spammer123", "Débannir / lever le timeout"]

def context_menu(d, x, y, hover, sub, subhover):
    mw = 230; ih = 22
    h = sum(8 if i == "-" else ih for i in MENU_ITEMS) + 8
    d.rectangle([x + 3, y + 3, x + mw + 3, y + h + 3], fill=(0, 0, 0, 120))
    d.rectangle([x, y, x + mw, y + h], fill=(245, 245, 245, 255), outline=(150, 150, 150, 255))
    cy = y + 4; tpos = None
    for i, it in enumerate(MENU_ITEMS):
        if it == "-":
            d.line([(x + 6, cy + 4), (x + mw - 6, cy + 4)], fill=(200, 200, 200)); cy += 8; continue
        if i == hover:
            d.rectangle([x + 2, cy, x + mw - 2, cy + ih - 1], fill=(204, 232, 255, 255))
        d.text((x + 12, cy + 4), it, font=font, fill=(20, 20, 20))
        if i == 5: tpos = cy
        cy += ih
    if sub and tpos is not None:
        sx = x + mw - 2; sw = 90
        d.rectangle([sx, tpos, sx + sw, tpos + 3 * ih + 8], fill=(245, 245, 245, 255), outline=(150, 150, 150, 255))
        for j, lab in enumerate(["1 min", "10 min", "1 h"]):
            yy = tpos + 4 + j * ih
            if j == subhover:
                d.rectangle([sx + 2, yy, sx + sw - 2, yy + ih - 1], fill=(204, 232, 255, 255))
            d.text((sx + 12, yy + 4), lab, font=font, fill=(20, 20, 20))
    return None

def lerp(a, b, t): return a + (b - a) * max(0, min(1, t))

frames = []
cache = {}
cursor = None
for f in range(END):
    for (ef, act, data) in events:
        if ef == f:
            if act == "add":
                msgs.append(dict(data, start=f))
            elif act == "remove":
                msgs[:] = [m for m in msgs if m.get("user") != data]

    frame = BG.copy().convert("RGBA")
    ov = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(ov)

    interactive = STEP3 + 6 <= f < LCLICK + 1
    # pile des messages (bas -> haut)
    y = CHAT_BOTTOM
    positions = {}
    for m in reversed(msgs):
        hl = interactive and m.get("user") == "Spammer123" and f >= HOVER_TO - 8 and f < CLICK_TO
        hl = hl or (interactive and m.get("user") == "PixelFox" and STEP4 + 4 <= f <= LCLICK)
        key = (m.get("user"), m["text"], hl)
        if key not in cache: cache[key] = bubble_image(m, hl)
        b = cache[key]
        y -= b.height + 4
        if y < CHAT_TOP: break
        a = min(1.0, (f - m["start"] + 1) / 4)
        if a < 1:
            b = b.copy(); b.putalpha(b.getchannel("A").point(lambda v: int(v * a)))
        dy = int((1 - a) * 8)
        ov.alpha_composite(b, (CHAT_X, y + dy))
        positions[(m.get("user"), m["text"])] = (CHAT_X, y, b.height)

    if interactive:
        d.rectangle([CHAT_X - 6, CHAT_TOP - 26, CHAT_X + CHAT_W + 6, CHAT_BOTTOM + 6], outline=(0, 200, 255, 255), width=2)
        d.rectangle([CHAT_X - 5, CHAT_TOP - 25, CHAT_X + 318, CHAT_TOP - 8], fill=(0, 96, 128, 200))
        d.text((CHAT_X, CHAT_TOP - 23), "MODE INTERACTIF — clic = répondre, clic droit = actions", font=small, fill=(255, 255, 255))

    # étape 2 : barre de réponse via raccourci
    keycap(d, f, STEP2 + 1, "Ctrl + Shift + F9")
    if STEP2 + 6 <= f <= SEND1:
        n = max(0, int((f - TYPE1_START) * 1.5)) if f >= TYPE1_START else 0
        reply_bar(d, TEXT1[:n], None, f % 4 < 2)

    # étape 3 : mode interactif + modération
    keycap(d, f, STEP3 + 1, "Ctrl + Shift + F8")
    spam = positions.get(("Spammer123", "ACHETEZ DES FOLLOWERS ICI !!! lien-douteux"))
    if STEP3 + 6 <= f < CLICK_TO + 6 and (spam or f >= CLICK_TO):
        tx, ty = (CHAT_X + 200, (spam[1] + 12) if spam else 300)
        if f < RCLICK:
            t = (f - (STEP3 + 6)) / (RCLICK - STEP3 - 6)
            cursor = (lerp(560, tx, t), lerp(330, ty, t))
        elif RCLICK <= f < CLICK_TO and spam:
            mx, my = tx, max(60, ty - 120)
            hover = 4 if f < HOVER_TO else 5
            context_menu(d, mx, my, hover, f >= HOVER_TO + 2, 1 if f >= SUB_HOVER else 0)
            if f < HOVER_TO: cursor = (mx + 60, my + 110)
            elif f < SUB_HOVER: cursor = (mx + 120, my + 132)
            else: cursor = (mx + 270, my + 140)
    # étape 4 : clic sur un message -> réponse
    pf = positions.get(("PixelFox", "Tu joues à quoi ce soir ?"))
    if STEP4 <= f <= LCLICK and pf:
        t = (f - STEP4) / (LCLICK - STEP4)
        cursor = (lerp(cursor[0] if cursor else 500, CHAT_X + 160, t), lerp(cursor[1] if cursor else 300, pf[1] + 12, t))
    if LCLICK + 1 <= f <= SEND2:
        n = max(0, int((f - TYPE2_START) * 1.5)) if f >= TYPE2_START else 0
        reply_bar(d, TEXT2[:n], "Réponse à @PixelFox : Tu joues à quoi ce soir ?", f % 4 < 2)
        cursor = None
    if f in (RCLICK, LCLICK, CLICK_TO) and cursor:
        d.ellipse([cursor[0] - 12, cursor[1] - 12, cursor[0] + 12, cursor[1] + 12], outline=(255, 255, 0, 255), width=2)
    if cursor and STEP3 + 6 <= f <= LCLICK:
        draw_cursor(d, *cursor)

    # légende de l'étape
    c = caption_for(f)
    cw = cap.getlength(c) + 28
    d.rounded_rectangle([(W - cw) / 2, 10, (W + cw) / 2, 38], 14, fill=(10, 10, 16, 210), outline=(145, 70, 255, 255), width=2)
    d.text((W / 2, 24), c, font=cap, fill=(255, 255, 255), anchor="mm")
    d.text((W - 8, H - 6), "Démo simulée", font=small, fill=(200, 200, 210, 170), anchor="rd")

    frame.alpha_composite(ov)
    frames.append(frame.convert("RGB"))

# palette commune (sans tramage) pour un GIF léger
sample = Image.new("RGB", (W, H * 4))
for i, fi in enumerate([50, 80, 125, 175]):
    sample.paste(frames[fi], (0, H * i))
pal = sample.quantize(colors=200, method=Image.Quantize.MEDIANCUT)
out = [fr.quantize(palette=pal, dither=Image.Dither.NONE) for fr in frames]
durs = [100] * len(out); durs[-1] = 2500
out[0].save(sys.argv[1], save_all=True, append_images=out[1:], duration=durs, loop=0, optimize=True, disposal=1)
frames[125].save(sys.argv[1].replace(".gif", "_apercu.png"))
print("frames", len(out))
