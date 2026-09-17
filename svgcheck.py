#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
svgcheck.py —— 矢量产出体检器（PELICAN 基准用）

为什么需要它：
    "鹈鹕骑车"这类测试，肉眼看只能得出"像/不像"，得出"哪一项坏了"很难 ——
    而**部件之间的关系**（朝向是否一致 / 有没有真的踩在地上 / 脚在不在踏板上）
    恰恰是最容易坏、又最不容易被"零件齐全"骗过去的部分。
    本脚本把关系变成数：同一个输入打两次，输出可直接对比。

用法：
    python svgcheck.py fixtures/sample-pelican.html              # 单份体检
    python svgcheck.py fixtures/sample-pelican.html out.svg      # A/B 并排对比
    python svgcheck.py --check                                   # 只跑脚本自检

设计原则：
    - 只量**客观可算**的东西；语义项（朝向 / 是不是鹈鹕）列成人工核对清单，不硬判
    - 量不到的项明确输出「跳过」，**不当作通过**（验不了 ≠ 通过）
    - 搜不到某个 class 只说明命名习惯不同 ⇒ 输出「未检测到」，**不当作"没有"**（不诬告）
    - 不依赖浏览器：纯正则解析坐标。够用于机器生成的 SVG（都是绝对坐标 M/L/Q）
"""
import re
import sys
import os
import argparse
from pathlib import Path

# ---------------------------------------------------------------- 解析层
ATTR = re.compile(r'([\w:-]+)\s*=\s*"([^"]*)"')
NUM = re.compile(r'-?\d+(?:\.\d+)?')


def read_svg(path):
    """从 .svg / .html / .htm 里抠出 <svg>…</svg>；也接受直接贴 SVG 文本的文件"""
    raw = Path(path).read_text(encoding='utf-8', errors='replace')
    m = re.search(r'<svg\b[\s\S]*?</svg>', raw, re.I)
    return (m.group(0) if m else raw), raw


def attrs(tag):
    return dict(ATTR.findall(tag))


def num(d, k, default=None):
    try:
        return float(d[k])
    except (KeyError, ValueError):
        return default


def points_of(d_attr):
    """从 path 的 d 里抠出所有坐标对（M/L/Q/C 一律按连续 (x,y) 取）——够用即可"""
    v = [float(x) for x in NUM.findall(d_attr or '')]
    return list(zip(v[0::2], v[1::2]))


class SvgFacts:
    """把一份 SVG 里可算的东西全算出来"""

    def __init__(self, text, raw_len, raw=None):
        self.text = text
        # ⚠️ 动画/关键帧写在 <head> 的 <style> 里，**在 <svg> 块之外** ——
        #    只看 SVG 块会把动画整个漏掉（本脚本第一版就漏了，实测：报 @keyframes 0 个）
        css = raw if raw is not None else text
        self.bytes = raw_len
        self.vb = self._viewbox()
        self.tags = self._collect()
        self.circles = self._circles()
        self.wheels = self._wheels()
        self.horizontals = self._horizontals()
        self.gradients = len(re.findall(r'<(?:linear|radial)Gradient\b', text, re.I))
        self.keyframes = re.findall(r'@keyframes\s+([\w-]+)', css)
        self.anim_count = len(re.findall(r'animation\s*:', css))
        self.spokes_named = self._class_count(r'spoke|ray')
        self.lines_total = len(re.findall(r'<line\b', text, re.I))
        # 有语义化 class 就用它；没有才退化成"所有 line"（并如实标注是退化后的数）
        self.spoke_like = self.spokes_named or self.lines_total

    # --- 基础
    def _viewbox(self):
        m = re.search(r'viewBox\s*=\s*"([^"]+)"', self.text, re.I)
        if not m:
            return None
        v = [float(x) for x in NUM.findall(m.group(1))]
        return v if len(v) == 4 else None

    def _collect(self):
        out = {}
        for name in ('path', 'circle', 'ellipse', 'line', 'rect', 'polygon', 'polyline', 'g', 'text'):
            out[name] = re.findall(r'<' + name + r'\b[^>]*>', self.text, re.I)
        return out

    def _circles(self):
        res = []
        for t in self.tags['circle']:
            a = attrs(t)
            cx, cy, r = num(a, 'cx'), num(a, 'cy'), num(a, 'r')
            if None in (cx, cy, r) or r <= 0:
                continue
            res.append({'cx': cx, 'cy': cy, 'r': r,
                        'cls': (a.get('class') or '') + ' ' + (a.get('id') or '')})
        return res

    def _wheels(self):
        """推定车轮：半径最大的两个圆（真轮子必然是大圆且成对）"""
        c = sorted(self.circles, key=lambda x: -x['r'])
        return c[:2] if len(c) >= 2 else c

    def _classed(self, cls_word):
        return [t for t in re.findall(r'<[a-z]+\b[^>]*>', self.text, re.I)
                if re.search(cls_word, attrs(t).get('class', ''), re.I)]

    def _class_count(self, cls_word):
        return len(self._classed(cls_word))

    def _horizontals(self):
        """长水平线（推定地面 / 路面 / 地平线）"""
        res = []
        vw = self.vb[2] if self.vb else None
        for t in self.tags['line']:
            a = attrs(t)
            x1, y1, x2, y2 = num(a, 'x1'), num(a, 'y1'), num(a, 'x2'), num(a, 'y2')
            if None in (x1, y1, x2, y2):
                continue
            if abs(y1 - y2) < 1 and (vw is None or abs(x2 - x1) > 0.4 * vw):
                res.append({'y': y1, 'len': abs(x2 - x1), 'how': 'line'})
        for t in self.tags['path']:
            a = attrs(t)
            m = re.match(r'\s*M\s*(-?[\d.]+)[\s,]+(-?[\d.]+)\s*[Hh]\s*(-?[\d.]+)', a.get('d', ''))
            if m:
                y, x2 = float(m.group(2)), float(m.group(3))
                if vw is None or abs(x2 - float(m.group(1))) > 0.4 * vw:
                    res.append({'y': y, 'len': 9999, 'how': 'path-H'})
        # 扁长矩形也是常见的地面画法（实测踩过：只认 line / path-H 会漏掉整条地面）
        vh = self.vb[3] if self.vb else None
        for t in self.tags['rect']:
            a = attrs(t)
            x, y, w, h = num(a, 'x', 0), num(a, 'y'), num(a, 'width'), num(a, 'height')
            if None in (y, w, h):
                continue
            wide = vw is None or w > 0.4 * vw
            flat = vh is None or h < 0.08 * vh
            if wide and flat:
                res.append({'y': y + h / 2, 'len': w, 'how': 'rect(扁长)'})
        return res

    # --- 关系（才是有价值的部分）
    def wheel_gap(self):
        if len(self.wheels) < 2:
            return None
        a, b = self.wheels
        import math
        return {'dist': math.dist((a['cx'], a['cy']), (b['cx'], b['cy'])),
                'sum_r': a['r'] + b['r'], 'r': (a['r'], b['r'])}

    def ground(self):
        """轮子最低点 vs 画布底"""
        if not self.wheels:
            return None
        bottom = max(w['cy'] + w['r'] for w in self.wheels)
        vh = (self.vb[1] + self.vb[3]) if self.vb else None
        return {'bottom': bottom, 'vb_bottom': vh,
                'margin': (vh - bottom) if vh else None}

    def ground_gap(self):
        """轮底 vs 最低那条水平线 —— 补上"穿过"之外的另一种失败：悬空 / 沉入"""
        if not self.horizontals or not self.wheels:
            return None
        g = max(self.horizontals, key=lambda h: h['y'])
        bottom = max(w['cy'] + w['r'] for w in self.wheels)
        return {'ground_y': g['y'], 'bottom': bottom,
                'diff': g['y'] - bottom, 'how': g['how']}

    def lines_through_wheels(self):
        """地面线是否横穿轮子内部（= 车没真的站在线上）"""
        hits = []
        for h in self.horizontals:
            for w in self.wheels:
                if abs(h['y'] - w['cy']) < w['r'] * 0.9:
                    hits.append((h['y'], w['cx'], w['cy'], w['r']))
        return hits


# ---------------------------------------------------------------- 报告层
def bar(ok):
    return '✅' if ok is True else ('⚠️' if ok is None else '❌')


def report(path):
    svg, raw = read_svg(path)
    F = SvgFacts(svg, len(raw.encode('utf-8')), raw=raw)
    L = []
    A = L.append

    A(f'■ {os.path.basename(path)}   {F.bytes} bytes')
    A(f'  画布 viewBox = {F.vb}')
    A(f'  元素：path {len(F.tags["path"])} · line {len(F.tags["line"])} · '
      f'circle {len(F.tags["circle"])} · ellipse {len(F.tags["ellipse"])} · rect {len(F.tags["rect"])}')
    A(f'  渐变 {F.gradients} 个 · @keyframes {len(F.keyframes)} 个 {F.keyframes} · animation 声明 {F.anim_count} 处')

    A('')
    A('  [几何一致性 —— 客观可算]')
    g = F.ground()
    bottom = g['bottom'] if g else None
    if g:
        A(f'  轮子最低点 y={g["bottom"]:.0f}，画布底 y={g["vb_bottom"]:.0f}，'
          f'留白 {g["margin"]:.0f}px')
    if F.horizontals:
        flag = 0
        for h in F.horizontals:
            inside = [w for w in F.wheels if abs(h['y'] - w['cy']) < w['r'] * 0.9]
            note = ''
            if inside and bottom is not None:
                note = (f'  ← 落在轮子内部：轮底 y={bottom:.0f}，'
                        f'线比轮底高 {bottom - h["y"]:.0f}px')
                flag += 1
            A(f'  水平线 y={h["y"]:.0f}（{h["how"]}）{note}')
        if flag:
            A(f'  ⚠️  {flag} 条水平线与轮子相交 —— 若它们是地面/路面线，说明'
              f'**车没站在线上**（轮子沉到线下面去了）')
        else:
            A('  ✅ 没有水平线穿过轮子')
        # 贴地判定：穿过之外还有两种失败 —— 悬空 / 沉入
        gg = F.ground_gap()
        if gg:
            d = gg['diff']
            if abs(d) <= 6:
                A(f'  ✅ 轮底与最低水平线基本贴合（差 {d:+.0f}px）')
            elif d > 0:
                A(f'  ⚠️  轮子**悬空**：轮底 y={gg["bottom"]:.0f}，'
                  f'最低水平线 y={gg["ground_y"]:.0f}，高出 {d:.0f}px')
            else:
                A(f'  ⚠️  轮子**沉入线下**：轮底 y={gg["bottom"]:.0f}，'
                  f'最低水平线 y={gg["ground_y"]:.0f}，低了 {-d:.0f}px')
    else:
        A('  ⏭  跳过：一条水平线都没有，找不到"地面"（验不了 ≠ 通过）')

    wg = F.wheel_gap()
    if wg:
        sep = wg['dist'] > wg['sum_r']
        A(f'  推定车轮 r={wg["r"][0]:.0f}/{wg["r"][1]:.0f}，中心距 {wg["dist"]:.0f} '
          f'vs 半径和 {wg["sum_r"]:.0f}')
        A(f'  {bar(sep)} 两轮不重叠'
          f'{"（轮距 %d px 底盘）" % (wg["dist"] - wg["sum_r"]) if sep else ""}')
    else:
        A('  ⏭  跳过：找不出成对的圆（可能不是两个轮子）')

    A('')
    A('  [部件齐备度 —— 计数取证]')
    A(f'  辐条类元素：{F.spoke_like} 个'
      f'{"（有 class 标注）" if F.spokes_named else "（无 class 标注，退化成所有 <line> 计数）"}'
      f'  {bar(F.spoke_like >= 8) if F.spoke_like else "⚠️ 一个都没有（车轮多半只有个圈）"}')
    for name, pat in [('曲柄/踏板', r'pedal|crank'), ('喉囊', r'pouch|gular|beak|bill'),
                      ('羽毛笔画', r'feather'), ('腿', r'\b(leg|limb)\b'), ('翅', r'wing')]:
        n = F._class_count(pat)
        if n:
            A(f'  {name}：{n} 处  ✅')
        else:
            # ⚠️ 检测不到 ≠ 没有 —— 换个命名习惯就搜不到，所以不当结论
            A(f'  {name}：未检测到（可能是命名习惯不同，**不能当作没有**，需人工看图）')
    A(f'  渐变着色：{F.gradients} 个  {bar(F.gradients > 0)}')
    A(f'  动画：{"有 %d 个关键帧 / %d 处声明" % (len(F.keyframes), F.anim_count) if F.anim_count else "无"}'
      f'  {"✅" if F.anim_count else "（静态图也能过关，不算硬伤）"}')

    A('')
    A('  [人工核对清单 —— 机器不敢替你判的]')
    for q in ['① 鸟的朝向与车头方向一致吗？',
              '② 脚落在踏板上吗（不是悬空）？',
              '③ 有下垂的喉囊（不是一根尖嘴）？',
              '④ 喙长 ≥ 头径吗？',
              '⑤ 身体坐在座垫上（不是浮在车把上）？',
              '⑥ 有眼睛（瞳孔/高光）吗？']:
        A(f'    □ {q}')
    return '\n'.join(L)


# ---------------------------------------------------------------- 自检
SELF_TESTS = [
    # (说明, SVG 片段, 期望的断言名 -> bool)
    ('两轮分离 + 轮底贴地',
     '<svg viewBox="0 0 100 100"><circle cx="20" cy="70" r="20"/>'
     '<circle cx="80" cy="70" r="20"/><path d="M0 90H100"/></svg>', True),
    ('轮子相交',
     '<svg viewBox="0 0 100 100"><circle cx="40" cy="70" r="20"/>'
     '<circle cx="60" cy="70" r="20"/></svg>', False),
]


def self_check():
    ok = True
    for desc, svg, want_sep in SELF_TESTS:
        F = SvgFacts(svg, len(svg), raw=svg)
        wg = F.wheel_gap()
        got = wg['dist'] > wg['sum_r'] if wg else None
        flag = '✓' if got == want_sep else '✗'
        if got != want_sep:
            ok = False
        print(f'  {flag} 自检：{desc} → 两轮分离={got}（期望 {want_sep}）')
    # 地面线穿轮检测
    svg = ('<svg viewBox="0 0 100 100"><circle cx="50" cy="50" r="40"/>'
           '<path d="M0 50H100"/></svg>')
    F = SvgFacts(svg, len(svg), raw=svg)
    hit = bool(F.lines_through_wheels())
    print(f'  {"✓" if hit else "✗"} 自检：地面线横穿轮心 → 命中={hit}（期望 True）')
    ok = ok and hit
    # CSS 在 SVG 块之外也要能读到（第一版漏过）
    F = SvgFacts('<svg viewBox="0 0 10 10"><rect width="10" height="10"/></svg>', 10,
                 raw='<style>@keyframes spin{to{transform:rotate(360deg)}}.a{animation:spin 1s}</style>')
    print(f'  {"✓" if F.keyframes == ["spin"] and F.anim_count == 1 else "✗"} '
          f'自检：<svg> 块外的 @keyframes 也要读到 → {F.keyframes}')
    ok = ok and F.keyframes == ['spin']
    # 扁长矩形当地面也要认出来（实测漏过一次）
    svg = ('<svg viewBox="0 0 100 100"><circle cx="50" cy="40" r="30"/>'
           '<rect x="0" y="70" width="100" height="6"/></svg>')
    F = SvgFacts(svg, len(svg), raw=svg)
    found = [h['how'] for h in F.horizontals]
    print(f'  {"✓" if found == ["rect(扁长)"] else "✗"} 自检：扁长矩形当地面 → {found}')
    ok = ok and found == ['rect(扁长)']
    # 贴地判定：悬空 / 沉入 都要能分出来
    cases = [('贴地', '<svg viewBox="0 0 100 100"><circle cx="20" cy="70" r="20"/>'
                      '<circle cx="80" cy="70" r="20"/><path d="M0 90H100"/></svg>', 0),
             ('悬空', '<svg viewBox="0 0 100 100"><circle cx="20" cy="60" r="20"/>'
                      '<circle cx="80" cy="60" r="20"/><path d="M0 90H100"/></svg>', 10),
             ('沉入', '<svg viewBox="0 0 100 100"><circle cx="20" cy="80" r="20"/>'
                      '<circle cx="80" cy="80" r="20"/><path d="M0 90H100"/></svg>', -10)]
    for name, svg, want in cases:
        F = SvgFacts(svg, len(svg), raw=svg)
        gg = F.ground_gap()
        got = round(gg['diff']) if gg else None
        good = got == want
        ok = ok and good
        print(f'  {"✓" if good else "✗"} 自检：{name} → 轮底与地面差 {got}px（期望 {want}）')
    # 无轮子时不炸
    try:
        F = SvgFacts('<svg viewBox="0 0 10 10"><rect width="10" height="10"/></svg>', 10)
        F.wheel_gap(); F.ground(); F.lines_through_wheels()
        print('  ✓ 自检：无圆 / 无 viewBox 时不崩')
    except Exception as e:
        ok = False
        print('  ✗ 自检：无圆时报错', e)
    print(f'  ⇒ 自检{"全部通过" if ok else "有失败"}')
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description='矢量产出体检器（鹈鹕基准）')
    ap.add_argument('files', nargs='*', help='一个或两个 .svg/.html 文件')
    ap.add_argument('--check', action='store_true', help='只跑脚本自检')
    args = ap.parse_args()

    if args.check:
        return self_check()
    if not args.files:
        ap.print_help()
        return 2

    print('=' * 78)
    for f in args.files[:2]:
        if not os.path.exists(f):
            print(f'找不到文件：{f}')
            return 2
        print(report(f))
        print('=' * 78)
    if len(args.files) >= 2:
        print('两份产出的数已经都列在上面 —— 逐项对照即可（判据优先于印象）。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
