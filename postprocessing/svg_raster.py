# -*- coding: utf-8 -*-
"""Minimal rasterizer for the subset of SVG that (Iso|Plot)Writer emit, so the ACTUAL C#
SVG output can be viewed without a cairo backend. Handles rect/polygon/line/circle/text."""
import re, sys, html
from PIL import Image, ImageDraw

def col(s, default=None):
    if s is None or s == "none": return default
    s = s.strip()
    m = re.match(r"rgb\((\d+),(\d+),(\d+)\)", s)
    if m: return tuple(int(x) for x in m.groups())
    if s.startswith("#"):
        h = s[1:]
        if len(h) == 3: h = "".join(c*2 for c in h)
        return tuple(int(h[i:i+2], 16) for i in (0, 2, 4))
    named = {"white": (255,255,255), "black": (0,0,0)}
    return named.get(s, default)

def attr(tag, name):
    m = re.search(name + r'="([^"]*)"', tag)
    return m.group(1) if m else None

def render(svg_path, png_path):
    svg = open(svg_path, encoding="utf-8").read()
    W = int(attr(svg, "width") or 1000); H = int(attr(svg, "height") or 600)
    img = Image.new("RGB", (W, H), (255, 255, 255)); d = ImageDraw.Draw(img)
    for tag in re.findall(r"<(?:rect|polygon|line|circle|text)\b[^>]*?(?:/>|>.*?</text>)", svg, re.S):
        if tag.startswith("<rect"):
            x=float(attr(tag,"x") or 0); y=float(attr(tag,"y") or 0)
            w=float(attr(tag,"width") or 0); h=float(attr(tag,"height") or 0)
            f=attr(tag,"fill"); fill=None if (f or "").startswith("url") else col(f)
            st=col(attr(tag,"stroke"))
            if fill or st: d.rectangle([x,y,x+w,y+h], fill=fill, outline=st)
        elif tag.startswith("<polygon"):
            pts=[tuple(map(float,p.split(","))) for p in attr(tag,"points").split()]
            d.polygon(pts, fill=col(attr(tag,"fill")), outline=col(attr(tag,"stroke")))
        elif tag.startswith("<line"):
            d.line([float(attr(tag,"x1")),float(attr(tag,"y1")),float(attr(tag,"x2")),float(attr(tag,"y2"))],
                   fill=col(attr(tag,"stroke"),(0,0,0)), width=int(float(attr(tag,"stroke-width") or 1)))
        elif tag.startswith("<circle"):
            cx=float(attr(tag,"cx")); cy=float(attr(tag,"cy")); r=float(attr(tag,"r"))
            d.ellipse([cx-r,cy-r,cx+r,cy+r], outline=col(attr(tag,"stroke"),(0,0,0)))
        elif tag.startswith("<text"):
            x=float(attr(tag,"x") or 0); y=float(attr(tag,"y") or 0)
            m=re.search(r">([^<]*)</text>", tag); txt=html.unescape(m.group(1)) if m else ""
            anchor=attr(tag,"text-anchor"); tx=x
            if anchor=="middle": tx=x-len(txt)*2.5
            elif anchor=="end": tx=x-len(txt)*5
            d.text((tx, y-10), txt, fill=col(attr(tag,"fill"),(0,0,0)))
    img.save(png_path); print("rendered", svg_path, "->", png_path, f"({W}x{H})")

if __name__ == "__main__":
    render(sys.argv[1], sys.argv[2])
