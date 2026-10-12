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
    python svgcheck.py fixtures/sample-pelican.html --json       # 机器可读（对拍用）
    python svgcheck.py --check                                   # 只跑脚本自检
    解释器不够新时用包装器：  bin\\svgcheck.cmd --check

判据从哪来（**这是本工具唯一的设计约束**）：
    全部来自仓库根的 rubric.json —— 权重、容差、判据文案、角色依赖、证据政策。
    代码里**不许**出现硬编码的权重数字或判据文案：两把尺子（本文件与 C# 的
    SvgCheck.Core）只要有一方把判据抄进代码，它们就会漂移，对拍随即失去意义。
    几何**怎么算**属于实现，写在代码里并按 rule.id 分派；算出多少**算通过**
    属于策略，只能问 rubric.json。

设计原则：
    - 只量**客观可算**的东西；语义项（朝向 / 是不是鹈鹕）列成人工核对清单，不硬判
    - 量不到的项明确输出「跳过 / unknown」，**不当作通过**（验不了 ≠ 通过）
    - 搜不到某个 class 只说明命名习惯不同 ⇒ 输出「未检测到」，**不当作"没有"**（不诬告）
    - 不依赖浏览器：纯正则解析坐标。已实现的 path 子集（M/L/H/V/Z 及其相对形式）
      语义必须正确；没实现的命令（曲线、圆弧）只推进笔位不生成几何，绝不猜
"""
import argparse
import json
import math
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent          # 仓库根：契约按 __file__ 定位，不依赖 CWD
RUBRIC_FILE = HERE / 'rubric.json'
TOOL_NAME = 'svgcheck.py'                        # _report.schema 里 tool 字段的约定值

ATTR = re.compile(r'([\w:-]+)\s*=\s*"([^"]*)"')
NUM = re.compile(r'-?\d+(?:\.\d+)?')
PH_RE = re.compile(r'\{([^{}]+)\}')
_PATOK = re.compile(r'([MmLlHhVvCcSsQqTtAaZz])|([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)')

# 占位符 {sign} 的取值 —— 契约把它定义为 diff 符号的派生量，故只能在这里落地
SIGN_POS = '悬于'
SIGN_NEG = '沉入'
SIGN_ZERO = '贴合'


class RubricError(Exception):
    """契约缺失/走形 —— 此时本工具拒绝给结论，而不是退回硬编码判据"""


# ---------------------------------------------------------------- 契约层
def load_rubric():
    """读 rubric.json 并做形状体检。缺文件/缺字段一律抛错：宁可不出数，不可出错数。"""
    try:
        doc = json.loads(RUBRIC_FILE.read_text(encoding='utf-8'))
    except FileNotFoundError:
        raise RubricError('找不到契约文件 %s。判据的唯一真源缺失，本工具拒绝工作 —— '
                          '把权重/文案抄进代码就等于造第二把会漂移的尺子。' % RUBRIC_FILE)
    except ValueError as e:
        raise RubricError('契约文件 %s 不是合法 JSON：%s' % (RUBRIC_FILE, e))
    for key in ('version', 'tolerances', 'groups', 'evidence_policy', '_report'):
        if key not in doc:
            raise RubricError('契约缺少必需区块 %r' % key)
    return Rubric(doc)


class Rubric(object):
    """rubric.json 的只读视图"""

    def __init__(self, doc):
        self.doc = doc
        self.version = doc['version']
        self.tolerances = doc.get('tolerances') or {}
        self.role_names = {}
        for r in doc.get('roles') or []:
            self.role_names[r['id']] = r.get('name') or r['id']
        self.rules = []                                    # [(group_id, rule_dict)]
        for g in doc.get('groups') or []:
            for r in g.get('rules') or []:
                if 'id' not in r or 'weight' not in r or 'criteria' not in r:
                    raise RubricError('规则 %r 缺 id/weight/criteria' % r.get('id'))
                self.rules.append((g.get('id'), r))
        self.by_id = dict((r['id'], r) for _, r in self.rules)
        pol = doc.get('evidence_policy') or {}
        self.hybrid = set(pol.get('hybrid', {}).get('rules') or [])
        self.geometric = set(pol.get('geometric', {}).get('rules') or [])
        self.informational = doc.get('informational') or []
        self.manual_only = doc.get('manual_only') or []
        self.report = doc.get('_report') or {}

    def tol(self, key):
        if key not in self.tolerances:
            raise RubricError('容差 %r 未在 rubric.tolerances 里声明' % key)
        return self.tolerances[key]

    def resolve(self, v):
        """把 "@tolerance_key" 解引用成数；其他值原样返回"""
        if isinstance(v, str) and v.startswith('@'):
            return self.tol(v[1:])
        return v

    def eval_criteria(self, rule_id, value):
        """自上而下第一条命中生效。max_abs 比 |value|，max 比 value，min 比 value，
        三者都不填就是兜底 else。"""
        rule = self.by_id[rule_id]
        for c in rule.get('criteria') or []:
            if 'max_abs' in c:
                if abs(value) <= self.resolve(c['max_abs']):
                    return c
            elif 'max' in c:
                if value <= self.resolve(c['max']):
                    return c
            elif 'min' in c:
                if value >= self.resolve(c['min']):
                    return c
            else:
                return c
        raise RubricError('规则 %s 的 criteria 没有一条命中，且缺兜底分支' % rule_id)


def fmt_text(tmpl, vals):
    """文案占位符填充。
    - 支持 :.0f / :+.0f / :.2f 这类格式说明符
    - **取不到的占位符原样保留**（契约：不静默吞掉）—— 宁可让 {typo} 暴露在报告里
    """
    def sub(m):
        body = m.group(1)
        name, _, spec = body.partition(':')
        if name not in vals:
            return m.group(0)
        v = vals[name]
        if not spec:
            return str(v)
        try:
            return format(v, spec)
        except (TypeError, ValueError, KeyError):
            return m.group(0)                 # 值存在但装不进这个格式说明符：同样原样保留
    return PH_RE.sub(sub, tmpl)


def with_derived(measured):
    """把派生键算给文案用：{absdiff} = |diff|，{sign} 由 diff 符号决定"""
    d = dict(measured)
    v = d.get('diff')
    if isinstance(v, (int, float)) and not isinstance(v, bool):
        d.setdefault('absdiff', abs(v))
        d.setdefault('sign', SIGN_POS if v > 0 else (SIGN_NEG if v < 0 else SIGN_ZERO))
    return d


# ---------------------------------------------------------------- 解析层
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


# path 子集：已实现的必须语义正确，未实现的绝不臆造坐标
_CMD_ARITY = {'m': 2, 'l': 2, 'h': 1, 'v': 1, 'c': 6, 's': 4, 'q': 4, 't': 2, 'a': 7, 'z': 0}
_NEEDS_PEN = set('lhv')          # 需要已知起点才能算的绝对命令


def parse_path(d_attr):
    """把 path 的 d 解析成 (直线段, 精确笔位, 凸包界)，相对命令一律先转绝对。

    segments [(x0,y0,x1,y1),...] : 只来自 M/L/H/V/Z 的直的部分。
    pts      [(x,y),...]         : 所有**精确落在路径上**的点（移动/直线/曲线端点、闭合点）。
                                    包围盒用它 ⇒ 只会算小，不会凭空算出画布外去。
    hull     [(x,y),...]         : pts 再加上贝塞尔**控制点**。曲线必在其控制点凸包内，
                                    所以 hull 是路径真实外形的**上界** —— 喉囊那种
                                    "下垂全在控制点里"的形状只有靠它才量得出。

    实现边界（刻意为之，宁缺勿误）：
      - 已实现 M/m L/l H/h V/v Z/z：相对→绝对转换正确，隐式重复参数按 SVG 规范处理
      - C/S/Q/T：取端点（相对命令用当前点 + 增量，端点是精确的），不生成直线段
      - A/a：flag 可以无分隔符粘连（"0 0 1"），数字流切分不可靠 ⇒ 直接丢弃当前笔位
      - 参数不足：整条 d 就此为止，不拿残缺的数字对凑数
    """
    segs, pts, hull = [], [], []
    toks = []
    for m in _PATOK.finditer(d_attr or ''):
        g = m.group(0)
        if len(g) == 1 and g.isalpha():
            toks.append(('c', g))
        else:
            try:
                toks.append(('n', float(g)))
            except ValueError:
                pass
    pen = None            # 当前点 (x,y)，未知时为 None
    start = None          # 子路径起点
    cmd = None
    i, N = 0, len(toks)

    def mark(*pairs):
        for p in pairs:
            hull.append(p)

    while i < N:
        kind, val = toks[i]
        if kind == 'c':
            cmd = val
            k = val.lower()
            if k == 'z':
                if pen is not None and start is not None:
                    if abs(pen[0] - start[0]) > 1e-9 or abs(pen[1] - start[1]) > 1e-9:
                        segs.append((start[0], start[1], pen[0], pen[1]))
                    pen = start
                i += 1
                cmd = None
                continue
            if k == 'a':
                pen = None            # 圆弧：不猜端点，丢弃当前笔位
            i += 1
            continue
        # 数字：必须挂在一个已知命令上
        if cmd is None:
            i += 1
            continue
        k = cmd.lower()
        n = _CMD_ARITY.get(k, 0)
        if n <= 0:
            i += 1
            continue
        nums, j = [], i
        while j < N and len(nums) < n:
            if toks[j][0] != 'n':
                break
            nums.append(toks[j][1])
            j += 1
        if len(nums) < n:
            break                                  # 参数不全：丢掉尾巴
        i = j
        rel = cmd.islower()
        if rel and pen is None:
            continue                               # 相对命令没有已知起点 ⇒ 无从换算
        if k in _NEEDS_PEN and pen is None:
            continue
        ox, oy = pen if pen else (0.0, 0.0)
        if k == 'm':
            x = ox + nums[0] if rel else nums[0]
            y = oy + nums[1] if rel else nums[1]
            pen, start = (x, y), (x, y)
            pts.append(pen)
            mark(pen)
            cmd = 'l'                              # M 之后的隐式重复参数是 lineto
        elif k in ('l', 'h', 'v'):
            if k == 'l':
                x = ox + nums[0] if rel else nums[0]
                y = oy + nums[1] if rel else nums[1]
            elif k == 'h':
                x = ox + nums[0] if rel else nums[0]
                y = oy
            else:
                x, y = ox, (oy + nums[0] if rel else nums[0])
            if pen is not None:
                segs.append((pen[0], pen[1], x, y))
            pen = (x, y)
            pts.append(pen)
            mark(pen)
        else:                                      # c / s / q / t：只推进到端点
            nctrl = {'c': 2, 'q': 1}.get(k, 0)     # s/t 的反射控制点算不出，只吃给出的
            ctrl = []
            for t in range(nctrl):
                cx = ox + nums[2 * t] if rel else nums[2 * t]
                cy = oy + nums[2 * t + 1] if rel else nums[2 * t + 1]
                ctrl.append((cx, cy))
            x = ox + nums[-2] if rel else nums[-2]
            y = oy + nums[-1] if rel else nums[-1]
            pen = (x, y)
            pts.append(pen)
            mark(pen)
            mark(*ctrl)
    return segs, pts, hull


def points_of(d_attr):
    """兼容旧接口：返回精确笔位（已改成相对→绝对的正确语义，不再把所有数字当坐标对）"""
    return parse_path(d_attr)[1]


def pt_seg_dist(px, py, x0, y0, x1, y1):
    dx, dy = x1 - x0, y1 - y0
    l2 = dx * dx + dy * dy
    if l2 <= 1e-12:
        return math.hypot(px - x0, py - y0)
    t = ((px - x0) * dx + (py - y0) * dy) / l2
    t = max(0.0, min(1.0, t))
    return math.hypot(px - (x0 + t * dx), py - (y0 + t * dy))


# ---------------------------------------------------------------- 颜色与取景

# CSS/SVG 1.0 命名色里高频的一撮。表外 ⇒ 返回 None（无定论），不当深色也不当浅色 ——
# 与 C# 侧 ColorParse.Named 同一份语法，判据与渲染不能各说各话。
NAMED_COLORS = {
    'black': (0, 0, 0), 'white': (255, 255, 255), 'red': (255, 0, 0),
    'lime': (0, 255, 0), 'blue': (0, 0, 255), 'yellow': (255, 255, 0),
    'cyan': (0, 255, 255), 'aqua': (0, 255, 255), 'magenta': (255, 0, 255),
    'fuchsia': (255, 0, 255), 'gray': (128, 128, 128), 'grey': (128, 128, 128),
    'silver': (192, 192, 192), 'maroon': (128, 0, 0), 'navy': (0, 0, 128),
    'teal': (0, 128, 128), 'olive': (128, 128, 0), 'purple': (128, 0, 128),
    'green': (0, 128, 0), 'orange': (255, 165, 0), 'brown': (165, 42, 42),
    'pink': (255, 192, 203), 'gold': (255, 215, 0), 'ivory': (255, 255, 240),
    'khaki': (240, 230, 140), 'salmon': (250, 128, 114), 'tan': (210, 180, 140),
    'beige': (245, 245, 220), 'coral': (255, 127, 80), 'wheat': (245, 222, 179),
    'snow': (255, 250, 250), 'crimson': (220, 20, 60), 'indigo': (75, 0, 130),
    'violet': (238, 130, 238), 'plum': (221, 160, 221), 'orchid': (218, 112, 214),
    'lavender': (230, 230, 250), 'thistle': (216, 191, 216), 'linen': (250, 240, 230),
    'azure': (240, 255, 255), 'seashell': (255, 245, 238), 'honeydew': (240, 255, 240),
    'mintcream': (245, 255, 250), 'oldlace': (253, 245, 230),
    'floralwhite': (255, 250, 240), 'cornsilk': (255, 248, 220),
    'lemonchiffon': (255, 250, 205), 'papayawhip': (255, 239, 213),
    'blanchedalmond': (255, 235, 205), 'bisque': (255, 228, 196),
    'peachpuff': (255, 218, 185), 'navajowhite': (255, 222, 173),
    'moccasin': (255, 228, 181), 'sandybrown': (244, 164, 96),
    'burlywood': (222, 184, 135), 'goldenrod': (218, 165, 32),
    'darkgoldenrod': (184, 134, 11), 'rosybrown': (188, 143, 143),
    'saddlebrown': (139, 69, 19), 'sienna': (160, 82, 45), 'chocolate': (210, 105, 30),
    'firebrick': (178, 34, 34), 'darkred': (139, 0, 0), 'tomato': (255, 99, 71),
    'orangered': (255, 69, 0), 'hotpink': (255, 105, 180), 'deeppink': (255, 20, 147),
    'palevioletred': (219, 112, 147), 'mediumvioletred': (199, 21, 133),
    'rebeccapurple': (102, 51, 153), 'darkmagenta': (139, 0, 139),
    'darkorchid': (153, 50, 204), 'blueviolet': (138, 43, 226),
    'mediumpurple': (147, 112, 219), 'mediumorchid': (186, 85, 211),
    'darkviolet': (148, 0, 211), 'darkslateblue': (72, 61, 139),
    'slateblue': (106, 90, 205), 'mediumslateblue': (123, 104, 238),
    'midnightblue': (25, 25, 112), 'darkblue': (0, 0, 139), 'mediumblue': (0, 0, 205),
    'deepskyblue': (0, 191, 255), 'dodgerblue': (30, 144, 255),
    'cornflowerblue': (100, 149, 237), 'steelblue': (70, 130, 180),
    'royalblue': (65, 105, 225), 'skyblue': (135, 206, 235),
    'lightskyblue': (135, 206, 250), 'powderblue': (176, 224, 230),
    'lightsteelblue': (176, 196, 222), 'aliceblue': (240, 248, 255),
    'ghostwhite': (248, 248, 255), 'gainsboro': (220, 220, 220),
    'lightgray': (211, 211, 211), 'lightgrey': (211, 211, 211),
    'darkgray': (169, 169, 169), 'darkgrey': (169, 169, 169),
    'dimgray': (105, 105, 105), 'dimgrey': (105, 105, 105),
    'slategray': (112, 128, 144), 'slategrey': (112, 128, 144),
    'lightslategray': (119, 136, 153), 'lightslategrey': (119, 136, 153),
    'darkslategray': (47, 79, 79), 'darkslategrey': (47, 79, 79),
    'cadetblue': (95, 158, 160), 'turquoise': (64, 224, 208),
    'mediumturquoise': (72, 209, 204), 'darkturquoise': (0, 206, 209),
    'paleturquoise': (175, 238, 238), 'aquamarine': (127, 255, 212),
    'mediumaquamarine': (102, 205, 170), 'lightseagreen': (32, 178, 170),
    'darkcyan': (0, 139, 139), 'forestgreen': (34, 139, 34), 'darkgreen': (0, 100, 0),
    'seagreen': (46, 139, 87), 'mediumseagreen': (60, 179, 113),
    'springgreen': (0, 255, 127), 'lawngreen': (124, 252, 0), 'chartreuse': (127, 255, 0),
    'yellowgreen': (154, 205, 50), 'darkolivegreen': (85, 107, 47),
    'olivedrab': (107, 142, 35), 'greenyellow': (173, 255, 47),
    'palegreen': (152, 251, 152), 'lightgreen': (144, 238, 144),
    'darkseagreen': (143, 188, 143), 'mediumspringgreen': (0, 250, 154),
    'lightyellow': (255, 255, 224), 'lightgoldenrodyellow': (250, 250, 210),
    'palegoldenrod': (238, 232, 170), 'lavenderblush': (255, 240, 245),
    'mistyrose': (255, 228, 225), 'peru': (205, 133, 63), 'whitesmoke': (245, 245, 245),
}


def parse_rgb(value):
    """颜色字面量 → (r,g,b) 或 None。None 的含义是【无定论】，不是【不是那个颜色】。"""
    if not value:
        return None
    v = value.strip()
    if not v or v.lower().startswith('url('):
        return None
    if v.lower() in ('none', 'transparent', 'currentcolor'):
        return None
    if v[0] == '#':
        h = v[1:]
        if len(h) in (3, 4):
            try:
                return tuple(int(c * 2, 16) for c in h[:3])
            except ValueError:
                return None
        if len(h) in (6, 8):
            try:
                return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))
            except ValueError:
                return None
        return None
    if v.lower().startswith('rgb'):
        lp = v.find('(')
        if lp < 0:
            return None
        body = v[lp + 1:].rstrip(')')
        parts = [p for p in re.split(r'[,\s/]+', body) if p]
        comps = []
        for p in parts[:3]:
            if p.endswith('%'):
                try:
                    comps.append(int(round(max(0.0, min(100.0, float(p[:-1]))) / 100.0 * 255)))
                except ValueError:
                    continue
            else:
                try:
                    comps.append(int(float(p)))
                except ValueError:
                    continue
        if len(comps) < 3:
            return None
        return tuple(max(0, min(255, c)) for c in comps[:3])
    return NAMED_COLORS.get(v.lower())


def luminance(value):
    """Rec.709 相对亮度 (0.2126R + 0.7152G + 0.0722B)/255。

    公式由 rubric 的 _两条放水教训 钉死 —— C# 侧必须同式，用 Rec.601 会给出不同 verdict。
    解析不了返回 None，调用方按无定论处理（不当深色，也不当浅色）。
    """
    rgb = parse_rgb(value)
    if rgb is None:
        return None
    r, g, b = rgb
    return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0


def subject_bbox(F, bg_cover):
    """主体包围盒，**剔除满画布背景**。

    一块在两个方向上都盖住 viewBox ≥ bg_cover 的矩形是天，不是主体。不剔的话
    within_canvas 恒过（背景顶着画布边 ⇒ 留白 0 但也不为负），真的被裁切的图也照样拿满分。
    详见 rubric 的 _两条放水教训。C# 侧 SvgDocument.SubjectBounds 同规则。
    """
    if not F.vb:
        return F.bbox
    vx, vy, vw, vh = F.vb
    if vw <= 0 or vh <= 0:
        return F.bbox
    kept_rects = []
    for r in F.rects:
        if r['w'] > 0 and r['h'] > 0 and r['w'] / vw >= bg_cover and r['h'] / vh >= bg_cover:
            continue
        kept_rects.append(r)
    if len(kept_rects) == len(F.rects):
        return F.bbox                      # 没剔掉任何东西，直接沿用全量口径
    xs, ys = [], []
    for c in F.circles:
        xs += [c['cx'] - c['r'], c['cx'] + c['r']]
        ys += [c['cy'] - c['r'], c['cy'] + c['r']]
    for e in F.ellipses:
        xs += [e['cx'] - e['rx'], e['cx'] + e['rx']]
        ys += [e['cy'] - e['ry'], e['cy'] + e['ry']]
    for r in kept_rects:
        xs += [r['x'], r['x'] + r['w']]
        ys += [r['y'], r['y'] + r['h']]
    for (x0, y0, x1, y1) in F.segments:
        xs += [x0, x1]
        ys += [y0, y1]
    for p in F.paths:
        for (x, y) in p['pts']:
            xs.append(x)
            ys.append(y)
    if not xs:
        return F.bbox
    return [min(xs), min(ys), max(xs), max(ys)]


class SvgFacts(object):
    """把一份 SVG 里可算的东西全算出来"""

    def __init__(self, text, raw_len, raw=None):
        self.text = text
        # ⚠️ 动画/关键帧/fill 样式写在 <head> 的 <style> 里，**在 <svg> 块之外** ——
        #    只看 SVG 块会把动画整个漏掉（本脚本第一版就漏了，实测：报 @keyframes 0 个）
        self.css = raw if raw is not None else text
        self.bytes = raw_len
        self.vb = self._viewbox()
        self.tags = self._collect()
        self.css_fills = self._css_fills()
        self.paths = self._paths()
        self.circles = self._circles()
        self.ellipses = self._ellipses()
        self.rects = self._rects()
        self.lines = self._lines()
        self.wheels = self._wheels()
        self.horizontals = self._horizontals()
        self.gradients = len(re.findall(r'<(?:linear|radial)Gradient\b', text, re.I))
        self.keyframes = re.findall(r'@keyframes\s+([\w-]+)', self.css)
        self.anim_count = len(re.findall(r'animation\s*:', self.css))
        self.spokes_named = self._class_count(r'spoke|ray')
        self.lines_total = len(re.findall(r'<line\b', text, re.I))
        # 有语义化 class 就用它；没有才退化成"所有 line"（并如实标注是退化后的数）
        self.spoke_like = self.spokes_named or self.lines_total
        self.segments = self._segments()
        self.bbox = self._bbox()
        self.fill_layers = self._fill_layers()
        self.degraded = not self._well_formed()

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

    def _well_formed(self):
        """XML 能不能整块解析。False ⇒ 已降级为宽松解析，几何结论置信度降低"""
        try:
            ET.fromstring(self.text)
            return True
        except Exception:
            return False

    def _css_fills(self):
        """class 名 → CSS 里声明的 fill。用来分辨「描边笔画」和「实心形体」，
        否则 class="feather"{fill:none} 的羽毛笔画会被当成喉囊那样的实心块。"""
        out = {}
        for m in re.finditer(r'([^{}]+)\{([^{}]*)\}', self.css):
            sel, body = m.group(1), m.group(2)
            fm = re.search(r'(?<![\w-])fill\s*:\s*([^;]+)', body)
            if not fm:
                continue
            val = fm.group(1).strip().lower()
            for part in sel.split(','):
                for cls in re.findall(r'\.([A-Za-z_][\w-]*)', part):
                    out.setdefault(cls, val)
        return out

    def _paths(self):
        """按文档顺序抽出每个 <path>：属性、直线段、笔位、有效 fill、是否闭合"""
        out = []
        stack = []                                    # 未闭合 <g> 的 fill 继承链
        for m in re.finditer(r'<(/?)(g|path)\b([^>]*)>', self.text, re.I):
            closing = m.group(1) == '/'
            name = m.group(2).lower()
            blob = m.group(3)
            a = dict(ATTR.findall(blob))
            selfclosed = blob.rstrip().endswith('/')
            if closing:
                if name == 'g' and stack:
                    stack.pop()
                continue
            own = (a.get('fill') or '').strip().lower() or None
            if name == 'g':
                if not selfclosed:
                    stack.append(own)
                continue
            cssf = None
            for tok in (a.get('class') or '').split():
                if tok in self.css_fills:
                    cssf = self.css_fills[tok]
                    break
            inh = next((x for x in reversed(stack) if x), None)
            segs, pts, hull = parse_path(a.get('d', ''))
            out.append({'attrs': a, 'cls': (a.get('class') or '') + ' ' + (a.get('id') or ''),
                        'segs': segs, 'pts': pts, 'hull': hull, 'fill': own or cssf or inh,
                        'has_curve': bool(re.search(r'[CcSsQqTtAa]', a.get('d', '') or '')),
                        'closed': bool(re.search(r'[Zz]', a.get('d', '') or '')),
                        'rel': bool(re.search(r'[mmlhvz]', a.get('d', '') or ''))})
        return out

    def _circles(self):
        res = []
        for t in self.tags['circle']:
            a = attrs(t)
            cx, cy, r = num(a, 'cx'), num(a, 'cy'), num(a, 'r')
            if None in (cx, cy, r) or r <= 0:
                continue
            res.append({'cx': cx, 'cy': cy, 'r': r,
                        'fill': a.get('fill'),
                        'cls': (a.get('class') or '') + ' ' + (a.get('id') or '')})
        return res

    def _ellipses(self):
        res = []
        for t in self.tags['ellipse']:
            a = attrs(t)
            cx, cy = num(a, 'cx', 0.0), num(a, 'cy', 0.0)
            rx, ry = num(a, 'rx'), num(a, 'ry')
            if None in (rx, ry) or rx <= 0 or ry <= 0:
                continue
            res.append({'cx': cx, 'cy': cy, 'rx': rx, 'ry': ry})
        return res

    def _rects(self):
        res = []
        for t in self.tags['rect']:
            a = attrs(t)
            x, y, w, h = num(a, 'x', 0.0), num(a, 'y', 0.0), num(a, 'width'), num(a, 'height')
            if None in (w, h) or w <= 0 or h <= 0:
                continue
            res.append({'x': x, 'y': y, 'w': w, 'h': h})
        return res

    def _lines(self):
        """四个坐标齐全的 <line>。缺坐标的一律丢掉 —— 它们多半活在 <g transform=...> 里，
        而本工具不认 transform（C# 侧认，这是两侧几何合法的差异点）。"""
        res = []
        for t in self.tags['line']:
            a = attrs(t)
            x1, y1, x2, y2 = num(a, 'x1'), num(a, 'y1'), num(a, 'x2'), num(a, 'y2')
            if None in (x1, y1, x2, y2):
                continue
            res.append({'x0': x1, 'y0': y1, 'x1': x2, 'y1': y2})
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
        """长水平线（推定地面 / 路面 / 地平线）。
        除 y 之外**必须记录 x 区间**：只存 y 的话，横亘在别处的短构件（踏板、车把）
        会被当成地面线落进轮子里 —— 那是本工具真实踩过的坑。"""
        res = []
        vw = self.vb[2] if self.vb else None
        vh = self.vb[3] if self.vb else None

        def keep(x0, x1, y, how):
            ln = abs(x1 - x0)
            if vw is not None and ln <= 0.4 * vw:
                return
            res.append({'y': y, 'x0': min(x0, x1), 'x1': max(x0, x1), 'len': ln, 'how': how})

        for L in self.lines:
            if abs(L['y0'] - L['y1']) < 1:
                keep(L['x0'], L['x1'], L['y0'], 'line')
        for p in self.paths:
            for (sx0, sy0, sx1, sy1) in p['segs']:
                if abs(sy0 - sy1) < 1e-6:
                    keep(sx0, sx1, sy0, 'path-H')
        # 扁长矩形也是常见的地面画法（实测踩过：只认 line / path-H 会漏掉整条地面）
        for r in self.rects:
            wide = vw is None or r['w'] > 0.4 * vw
            flat = vh is None or r['h'] < 0.08 * vh
            if wide and flat:
                keep(r['x'], r['x'] + r['w'], r['y'] + r['h'] / 2.0, 'rect(扁长)')
        return res

    def _segments(self):
        """所有可信的直线段：<line> 元素 + path 里 M/L/H/V/Z 的直的部分"""
        segs = [(L['x0'], L['y0'], L['x1'], L['y1']) for L in self.lines]
        for p in self.paths:
            segs.extend(p['segs'])
        return segs

    def _bbox(self):
        """主体包围盒（圆取 cy±r，横线/矩形/椭圆取坐标极值，path 取已解析的笔位）。
        曲线只取端点 ⇒ 是**低估**而非误估：宁可算小了，也不会凭空算出画布外去。"""
        xs, ys = [], []
        for c in self.circles:
            xs += [c['cx'] - c['r'], c['cx'] + c['r']]
            ys += [c['cy'] - c['r'], c['cy'] + c['r']]
        for e in self.ellipses:
            xs += [e['cx'] - e['rx'], e['cx'] + e['rx']]
            ys += [e['cy'] - e['ry'], e['cy'] + e['ry']]
        for r in self.rects:
            xs += [r['x'], r['x'] + r['w']]
            ys += [r['y'], r['y'] + r['h']]
        for (x0, y0, x1, y1) in self.segments:
            xs += [x0, x1]
            ys += [y0, y1]
        for p in self.paths:
            for (x, y) in p['pts']:
                xs.append(x)
                ys.append(y)
        if not xs:
            return None
        return [min(xs), min(ys), max(xs), max(ys)]

    def _fill_layers(self):
        """不同填充色的层数（none/transparent 不算一层）。渐变引用 url(#..) 各算一层。"""
        cols = set()
        for m in re.finditer(r'(?<![\w-])fill\s*=\s*"([^"]*)"', self.text, re.I):
            cols.add(m.group(1).strip().lower())
        for m in re.finditer(r'(?<![\w-])fill\s*:\s*([^;{}]+)', self.css):
            cols.add(m.group(1).strip().lower())
        for p in self.paths:
            if p['fill']:
                cols.add(p['fill'])
        junk = {'none', 'transparent', 'inherit', 'initial', 'unset', ''}
        return len([c for c in cols if c not in junk])

    # --- 关系（才是有价值的部分）
    def wheel_gap(self):
        if len(self.wheels) < 2:
            return None
        a, b = self.wheels
        return {'dist': math.hypot(a['cx'] - b['cx'], a['cy'] - b['cy']),
                'sum_r': a['r'] + b['r'], 'r': (a['r'], b['r'])}

    def ground(self):
        """轮子最低点 vs 画布底"""
        if not self.wheels:
            return None
        bottom = max(w['cy'] + w['r'] for w in self.wheels)
        vh = (self.vb[1] + self.vb[3]) if self.vb else None
        return {'bottom': bottom, 'vb_bottom': vh,
                'margin': (vh - bottom) if vh else None}

    def lowest_horizontal(self):
        if not self.horizontals:
            return None
        return max(self.horizontals, key=lambda h: h['y'])

    def ground_gap(self):
        """轮底 vs 最低那条水平线 —— 补上"穿过"之外的另一种失败：悬空 / 沉入"""
        g = self.lowest_horizontal()
        if g is None or not self.wheels:
            return None
        bottom = max(w['cy'] + w['r'] for w in self.wheels)
        return {'ground_y': g['y'], 'bottom': bottom,
                'diff': g['y'] - bottom, 'how': g['how']}

    def crossings(self):
        """横线 ↔ 轮子相交检测。

        **必须同时校验 x 区间重叠**：轮圈多为 fill:none，判定"地面线落在轮内"只看 y
        会把踏板、车把这些横亘在别处的短构件一并报成穿模（本工具真实诬告过一次的坑）。
        返回 (命中对 [(line_y, wheel_cx, wheel_cy, wheel_r)], 命中的横线列表)。"""
        pairs, lines = [], []
        for h in self.horizontals:
            hit = False
            for w in self.wheels:
                if (h['x1'] >= w['cx'] - w['r'] and h['x0'] <= w['cx'] + w['r']
                        and abs(h['y'] - w['cy']) < w['r'] * 0.9):
                    pairs.append((h['y'], w['cx'], w['cy'], w['r']))
                    hit = True
            if hit:
                lines.append(h)
        return pairs, lines

    def lines_through_wheels(self):
        """地面线是否横穿轮子内部（= 车没真的站在线上）"""
        return self.crossings()[0]

    # --- 部件取证（几何优先，名称兜底；两者都拿不出正面证据 ⇒ 上层给 unknown 而不是 fail）
    def segment_graph(self, merge_px):
        """把直线段的端点在 merge_px 容差内合并成节点"""
        verts, incident = [], {}

        def find(x, y):
            """端点落在已有节点代表位置 merge_px 内 ⇒ 同一个节点（取质心当代表）"""
            for i, v in enumerate(verts):
                if math.hypot(v[0] / v[2] - x, v[1] / v[2] - y) <= merge_px:
                    v[0] += x
                    v[1] += y
                    v[2] += 1
                    return i
            verts.append([x, y, 1])
            incident[len(verts) - 1] = []
            return len(verts) - 1

        edges = set()
        for (x0, y0, x1, y1) in self.segments:
            if abs(x1 - x0) < 1e-9 and abs(y1 - y0) < 1e-9:
                continue                              # 零长度段不是边
            a, b = find(x0, y0), find(x1, y1)
            if a == b:
                continue
            edges.add((min(a, b), max(a, b)))
            incident[a].append((b, (x0, y0, x1, y1)))
            incident[b].append((a, (x0, y0, x1, y1)))
        adj = dict((i, set()) for i in range(len(verts)))
        for a, b in edges:
            adj[a].add(b)
            adj[b].add(a)
        return verts, edges, adj, incident

    def count_triangles(self, merge_px):
        """在合并过节点的线段图上找 3-环。返回 (三角形数, 节点数, 边数)"""
        verts, edges, adj, _ = self.segment_graph(merge_px)
        n = 0
        ids = sorted(adj)
        for a in ids:
            for b in adj[a]:
                if b <= a:
                    continue
                for c in adj[b]:
                    if c <= b:
                        continue
                    if a in adj[c]:
                        n += 1
        return n, len(verts), len(edges)

    def spoke_evidence(self):
        """辐条计数：优先几何（端点落在轮环内且过轮心附近的径向线段），
        退而求其次才是 class 名。两者都是 0 ⇒ 返回 0，由上层给 unknown。"""
        geo = 0
        for w in self.wheels:
            cx, cy, r = w['cx'], w['cy'], w['r']
            for (x0, y0, x1, y1) in self.segments:
                d0, d1 = math.hypot(x0 - cx, y0 - cy), math.hypot(x1 - cx, y1 - cy)
                if max(d0, d1) > 1.12 * r:
                    continue
                if math.hypot(x1 - x0, y1 - y0) < 0.5 * r:
                    continue
                if pt_seg_dist(cx, cy, x0, y0, x1, y1) > 0.2 * r:
                    continue                          # 不经过轮心附近 ⇒ 是弦/切线，不是辐条
                geo += 1
        return geo, self.spokes_named

    def crank_evidence(self, merge_px):
        """曲柄+踏板取证：找「五通」候选节点 —— 位于两轮之间、轴心高度附近，
        且引出两条近似反向、等长、末端各带一段横向平台的短段。
        返回 (检出的曲柄臂数, 说明)。轮子都认不出来时返回 0，由上层给 unknown。"""
        if len(self.wheels) < 2:
            return 0, 'no-wheel'
        a, b = sorted(self.wheels, key=lambda c: c['cx'])
        rmean = (a['r'] + b['r']) / 2.0
        lo, hi = a['cx'], b['cx']
        span = hi - lo
        if span <= 0:
            return 0, 'no-span'
        axy = (a['cy'] + b['cy']) / 2.0
        merge = float(merge_px)                         # 节点合并容差来自 rubric
        verts, edges, adj, incident = self.segment_graph(merge)
        best = 0
        for vi, v in enumerate(verts):
            vx, vy = v[0] / v[2], v[1] / v[2]
            if not (lo + 0.15 * span <= vx <= hi - 0.15 * span):
                continue
            if abs(vy - axy) > 0.6 * rmean:
                continue
            arms = []
            for (vj, seg) in incident.get(vi, []):
                x0, y0, x1, y1 = seg
                L = math.hypot(x1 - x0, y1 - y0)
                if not (0.15 * rmean <= L <= 1.0 * rmean):
                    continue
                d0, d1 = math.hypot(x0 - vx, y0 - vy), math.hypot(x1 - vx, y1 - vy)
                if min(d0, d1) > merge:
                    continue
                e = (x1, y1) if d0 <= d1 else (x0, y0)
                arms.append((L, e, seg, ((e[0] - vx) / L, (e[1] - vy) / L)))
            qualified = [am for am in arms if self._platform_at(am[1], am[0], am[2], merge)]
            cand = 0
            for i in range(len(qualified)):
                for j in range(i + 1, len(qualified)):
                    Li, Ei, Si, ui = qualified[i]
                    Lj, Ej, Sj, uj = qualified[j]
                    if ui[0] * uj[0] + ui[1] * uj[1] > -0.3:
                        continue                      # 曲柄是反向的一对
                    if max(Li, Lj) > 1.65 * min(Li, Lj):
                        continue                      # 两边一般等长
                    cand = max(cand, 2)
            best = max(best, cand)
        return best, ('geometric' if best else 'inconclusive')

    def _platform_at(self, end, arm_len, arm_seg, merge):
        """踏板平台不一定和曲柄臂共端点（作者常常另起一条 h 短线），
        所以在 end 附近半径 max(2*merge, 0.5*arm_len) 内找一段长度相当的短段。"""
        rad = max(2.0 * merge, 0.5 * arm_len)
        for seg in self.segments:
            if seg is arm_seg:
                continue
            x0, y0, x1, y1 = seg
            L = math.hypot(x1 - x0, y1 - y0)
            if not (0.2 * arm_len <= L <= 3.0 * arm_len):
                continue
            if min(math.hypot(x0 - end[0], y0 - end[1]),
                   math.hypot(x1 - end[0], y1 - end[1])) <= rad:
                return True
        return False

    def _filled_hulls(self):
        """每个「有效 fill 不是 none 的 <path>」的凸包界 (idx, x0, y0, x1, y1)。
        用 hull（含贝塞尔控制点）而不是 pts：曲线必在其控制点凸包内，
        所以这是路径外形的上界 —— 下垂全写在控制点里的喉囊只有靠它才量得出来。"""
        out = []
        for idx, p in enumerate(self.paths):
            if (p['fill'] or 'none') in ('none', 'transparent') or not p['hull']:
                continue
            xs = [q[0] for q in p['hull']]
            ys = [q[1] for q in p['hull']]
            out.append((idx, min(xs), min(ys), max(xs), max(ys)))
        return out

    def pouch_evidence(self, subj, tol):
        """喉囊：喙下方那个下垂的实心体。四条并列条件才算正面证据（缺一即"无定论"）：
          1. 实心（有效 fill ≠ none）—— 描边笔画不是囊
          2. 凸包界 高/宽 ≥ 0.40 —— 下垂，而不是平摊的一片
          3. 挂在头部那一带（凸包中心在画面上部 40%，且底边不过画面一半）
             —— 一直垂到身子以下的，是脖子或腿，不是囊
          4. 它头顶上有别的实心形体压着它 —— 那就是喙。囊不会长在天灵盖上
        检不到就返回 0，由上层按 hybrid 政策给 **unknown**，绝不给 fail（工程笔记 2）。

        subj 是**剔除满画布背景后**的主体框：拿整块天当归一化基准，比例会全算歪。
        """
        if not subj:
            return 0, 'inconclusive'
        bx0, by0, bx1, by1 = subj
        fw, fh = bx1 - bx0, by1 - by0
        if fw <= 0 or fh <= 0:
            return 0, 'inconclusive'
        hulls = self._filled_hulls()
        geo = []
        for (idx, x0, y0, x1, y1) in hulls:
            w, h = x1 - x0, y1 - y0
            if w <= 0 or h / float(w) < tol('pouch_min_aspect'):
                continue
            if w < tol('pouch_min_width_frac') * fw:
                continue
            if (y0 + y1) / 2.0 > by0 + tol('pouch_center_top_frac') * fh:
                continue
            if y1 > by0 + tol('pouch_bottom_max_frac') * fh:
                continue
            above = [j for (j, ax0, ay0, ax1, ay1) in hulls
                     if j != idx and ay0 < y0 and ay1 <= y1
                     and min(x1, ax1) - max(x0, ax0) >= tol('pouch_above_overlap_frac') * min(w, ax1 - ax0)]
            if not above:
                continue
            geo.append(idx)
        named = [i for i, p in enumerate(self.paths)
                 if re.search(r'pouch|gular|throat|囊', p['cls'], re.I)]
        both = sorted(set(geo) | set(named))
        how = []
        if geo:
            i = geo[0]
            hh = [x for x in hulls if x[0] == i][0]
            how.append('几何：喙下方的下垂实心体 x∈[%.0f,%.0f] y∈[%.0f,%.0f]'
                       % (hh[1], hh[3], hh[2], hh[4]))
        if named:
            how.append('class 名 %d 处' % len(named))
        return len(both), ('；'.join(how) if how else 'inconclusive')

    def feather_evidence(self):
        """羽毛笔画：class 名优先（这是作者主动标注的最强证据），
        否则数「开放的、描边的、含曲线命令的短笔画」。"""
        named = self._class_count(r'feather|plume')
        if named:
            return named, 'class 名'
        geo = 0
        if self.bbox:
            diag = math.hypot(self.bbox[2] - self.bbox[0], self.bbox[3] - self.bbox[1])
            for p in self.paths:
                if p['closed'] or not p['has_curve'] or not p['pts']:
                    continue
                if (p['fill'] or '') not in ('none', 'transparent'):
                    continue                          # 实心形体不是"笔画"
                xs = [q[0] for q in p['pts']]
                ys = [q[1] for q in p['pts']]
                if math.hypot(max(xs) - min(xs), max(ys) - min(ys)) > 0.35 * diag:
                    continue                          # 太大的是轮廓，不是羽毛纹
                geo += 1
        return geo, ('几何：开放描边曲线笔画' if geo else 'inconclusive')

    def eye_evidence(self, merge_px, subj, tol):
        """眼：上半区的小而**深**的圆。

        尺寸标尺借用轮子半径（轮认不出时借主体宽度）。所有比例从 rubric 读 ——
        硬编码会让本工具与 C# 侧在同一个元素上给出不同 verdict（铁律 1）。

        深色这条是 2026-01-12 加的：只靠『小 + 在上半区』把月亮上的环形山数成了眼睛
        （实测报『3 只眼』）。亮度按 Rec.709 算，公式由 rubric 的 _两条放水教训 钉死。
        找不到深色小圆 ⇒ 无定论（unknown），不给 fail：有些画风眼睛就是白的。
        """
        if not subj:
            return 0, 'no-subject'
        w = subj[2] - subj[0]
        h = subj[3] - subj[1]
        if w <= 0 or h <= 0:
            return 0, 'no-subject'
        if len(self.wheels) >= 2:
            rmax = tol('eye_rmax_of_wheel') * ((self.wheels[0]['r'] + self.wheels[1]['r']) / 2.0)
        else:
            rmax = tol('eye_rmax_of_width') * w
        rmin = tol('eye_rmin_of_width') * w
        top = subj[1] + tol('eye_top_frac') * h
        max_lum = tol('eye_max_luminance')

        def dark(c):
            lum = luminance(c.get('fill'))
            return lum is not None and lum < max_lum

        cands = []
        for c in self.circles:
            if not (rmin <= c['r'] <= rmax):
                continue
            if c['cy'] > top:
                continue
            if not dark(c):
                continue
            cands.append(c)
        named = [c for c in self.circles if re.search(r'eye|pupil|iris|瞳', c['cls'], re.I)]
        for c in named:
            if c not in cands:
                cands.append(c)
        # 瞳孔 + 高光常画在同一处：圆心互相落在节点合并容差内的算一只眼
        clusters = []
        for c in sorted(cands, key=lambda x: -x['r']):
            placed = False
            for cl in clusters:
                if math.hypot(cl[0]['cx'] - c['cx'], cl[0]['cy'] - c['cy']) <= float(merge_px):
                    cl.append(c)
                    placed = True
                    break
            if not placed:
                clusters.append([c])
        return len(clusters), ('geometric' if cands else 'inconclusive')


# ---------------------------------------------------------------- 角色推定
# Python 侧没有交互式角色指派，所以只推定「认错代价最低」的三个；
# 剩下那些（beak_tip / eye / foot / pedal / seat / seat_tube_top / pouch / handlebar）
# **一律不猜**：自动认错元素就是诬告，缺角色的规则直接输出 unknown。
AUTO_ROLES = ('wheel_rear', 'wheel_front', 'ground')
NEVER_GUESS = ('beak_tip', 'eye', 'foot', 'pedal', 'seat', 'seat_tube_top', 'pouch', 'handlebar')


def infer_roles(F):
    roles = {}
    if len(F.wheels) >= 2:
        # 半径最大的两个圆；按 x 排序后 x 小者为 rear、大者为 front。
        # 这个前后指派是**任意但一致**的 —— heading_consistent 在 Python 侧永远是
        # unknown（它要 beak_tip/eye），所以不影响任何被判决的规则。
        pair = sorted(F.wheels, key=lambda c: c['cx'])
        for key, w in (('wheel_rear', pair[0]), ('wheel_front', pair[1])):
            roles[key] = {'kind': 'circle', 'cx': w['cx'], 'cy': w['cy'], 'r': w['r'],
                          'source': 'auto：半径最大的两个圆'}
    g = F.lowest_horizontal()
    if g is not None:
        roles['ground'] = {'kind': 'line', 'x0': g['x0'], 'x1': g['x1'], 'y': g['y'],
                           'how': g['how'], 'source': 'auto：y 最大的那条长横线'}
    return roles


# ---------------------------------------------------------------- 量（几何怎么算 = 实现）
def measure(rule_id, F, R, roles):
    """按 rule.id 分派。返回 (value, measured)；value=None 表示**拿不出正面证据**，
    由上层按 evidence_policy 决定是 unknown 还是 fail。"""
    rid = rule_id
    if rid == 'wheel_ground_contact':
        wb = max(roles[k]['cy'] + roles[k]['r'] for k in ('wheel_rear', 'wheel_front'))
        gy = roles['ground']['y']
        return gy - wb, {'wheel_bottom': wb, 'ground': gy, 'diff': gy - wb,
                         'ground_x': [roles['ground']['x0'], roles['ground']['x1']]}
    if rid == 'ground_clear_of_wheels':
        cross = F.crossings()[1]
        return len(cross), {'count': len(cross), 'horizontals': len(F.horizontals),
                            'crossing_y': [h['y'] for h in cross]}
    if rid == 'wheels_separated':
        a, b = roles['wheel_rear'], roles['wheel_front']
        dist = math.hypot(a['cx'] - b['cx'], a['cy'] - b['cy'])
        sumr = a['r'] + b['r']
        return dist - sumr, {'dist': dist, 'sumr': sumr, 'gap': dist - sumr,
                             'diff': dist - sumr, 'r': [a['r'], b['r']]}
    if rid == 'spokes_present':
        # 契约把 spokes_present 划在 geometric（缺席可证），并且 requires 两只轮子 ——
        # 所以走到这里说明轮环已经认出来了，数出来的 0 就是真的 0，可以给 fail。
        # 但几何优先、class 名次之：辐条多半画在 <g transform=...> 里，本工具不认
        # transform；几何数出 0 根就照着 criteria 的兜底分支去诬告，是不行的。
        geo, named = F.spoke_evidence()
        count = geo if geo > 0 else named
        how = ('几何：端点落在轮环内且经过轮心的径向线段' if geo > 0 else
               ('class 名（几何法无从下手：辐条多半画在 <g transform=...> 里，'
                '而本工具不认 transform）' if named > 0 else '几何与 class 名都为 0'))
        return count, {'count': count, 'geo': geo, 'named': named, 'how': how}
    if rid == 'frame_triangles':
        n, nv, ne = F.count_triangles(R.tol('vertex_merge_px'))
        return n, {'count': n, 'vertices': nv, 'segments': ne}
    if rid == 'crank_pedals':
        geo, how = F.crank_evidence(R.tol('vertex_merge_px'))
        named = F._class_count(r'pedal|crank')
        if geo > 0:
            return geo, {'count': geo, 'geo': geo, 'named': named,
                         'how': '几何：五通处两条反向等长短段，末端各带横向平台'}
        if named > 0:
            return named, {'count': named, 'geo': geo, 'named': named, 'how': 'class 名'}
        return None, {'geo': geo, 'named': named, 'how': how}
    if rid == 'pouch_present':
        n, how = F.pouch_evidence(subject_bbox(F, R.tol('bg_cover_frac')), R.tol)
        if n > 0:
            return n, {'count': n, 'how': how}
        return None, {'count': 0, 'how': how}          # hybrid：检不到 ⇒ unknown，不是 fail
    if rid == 'feather_strokes':
        n, how = F.feather_evidence()
        if n > 0:
            return n, {'count': n, 'how': how}
        return None, {'count': 0, 'how': how}
    if rid == 'eye_present':
        n, how = F.eye_evidence(R.tol('vertex_merge_px'),
                                subject_bbox(F, R.tol('bg_cover_frac')), R.tol)
        if n > 0:
            return n, {'count': n, 'how': how}
        return None, {'count': 0, 'how': how}
    if rid == 'within_canvas':
        # 用剔掉满画布背景后的主体框：拿那天当归一化基准，within_canvas 就恒过了
        subj = subject_bbox(F, R.tol('bg_cover_frac'))
        if not F.vb or not subj:
            return None, {'bbox': subj, 'viewbox': F.vb}
        x, y, w, h = F.vb
        bx0, by0, bx1, by1 = subj
        edges = [bx0 - x, (x + w) - bx1, by0 - y, (y + h) - by1]
        margin = min(edges)
        return margin, {'margin': margin, 'edges': edges, 'bbox': subj,
                        'diff': margin - R.tol('canvas_margin_px')}
    if rid == 'gradient_depth':
        g, layers = F.gradients, F.fill_layers
        # 契约规定的合成值：有渐变 ⇒ 100 + 渐变数（借此跨过 min:100 那道门槛），
        # 没渐变 ⇒ 就是纯色层数（最高也就落到 warn 档）。
        # 两条 criteria 比的是同一个数，靠 100 这个量级分开 —— 不是我选的阈值。
        value = (100 + g) if g > 0 else layers
        return value, {'gradients': g, 'layers': layers, 'value': value}
    # 其余规则（heading_consistent / feet_on_pedals / seat_on_seat_tube / beak_length）
    # 全靠角色指派，Python 侧不猜 ⇒ 走 requires 门禁，到不了这里
    return None, {}


# ---------------------------------------------------------------- 判（criteria + evidence_policy）
VERDICTS = ('pass', 'warn', 'fail', 'unknown')
VERDICT_BAR = {'pass': '✅', 'warn': '⚠️', 'fail': '❌', 'unknown': '⏭'}


def mk_rule(rule, gid, verdict, score, measured, text):
    """measured 必须已经 jclean 过 —— 文案填充与 JSON 用同一份数值，
    免得报告里出现 y=366.0 而 measured 里写 366 这种自相矛盾"""
    return {'id': rule['id'], 'group': gid, 'weight': rule['weight'],
            'verdict': verdict, 'score': score,
            'measured': measured, 'text': text}


def evaluate(F, R, roles):
    out = []
    for gid, rule in R.rules:
        rid = rule['id']
        unknown_tmpl = rule.get('unknown', '')
        requires = rule.get('requires') or []
        missing = [x for x in requires if x not in roles]
        if missing:
            vals = dict(R.tolerances)
            vals['missing'] = '、'.join(R.role_names.get(x, x) for x in missing)
            out.append(mk_rule(rule, gid, 'unknown', None, jclean({'missing': missing}),
                               fmt_text(unknown_tmpl, vals)))
            continue
        value, measured = measure(rid, F, R, roles)
        measured = jclean(measured)
        if value is None:
            # hybrid 组：拿不出正面证据 ⇒ unknown / score=null。
            # **禁止用 fail 表达「我不知道」**（工程笔记 2：诬告一次，整把尺子就不可信）。
            # geometric 组本该缺席可证；走到这里说明连数都量不出来（例如没有 viewBox），
            # 那同样是「无资格判」而不是 fail —— rubric 为这些规则也备了 unknown 文案。
            vals = dict(R.tolerances)
            vals.update(with_derived(measured))
            out.append(mk_rule(rule, gid, 'unknown', None, measured,
                               fmt_text(unknown_tmpl, vals)))
            continue
        crit = R.eval_criteria(rid, value)
        verdict = crit.get('verdict')
        if verdict not in VERDICTS:
            raise RubricError('规则 %s 给出 verdict=%r，越界 %s' % (rid, verdict, VERDICTS))
        if verdict == 'unknown':
            raise RubricError('规则 %s 的 criteria 不该产出 unknown —— unknown 只能来自'
                              '缺角色或缺正面证据那条路' % rid)
        vals = dict(R.tolerances)
        vals.update(with_derived(measured))
        out.append(mk_rule(rule, gid, verdict, crit.get('score'), measured,
                           fmt_text(crit.get('text', ''), vals)))
    return out


def summarize(rules, R):
    tw = sum(r['weight'] for r in rules)
    judged = [r for r in rules if r['score'] is not None]
    jw = sum(r['weight'] for r in judged)
    num = sum(r['weight'] * r['score'] for r in judged)
    return {
        'score': int(round(100.0 * num / jw)) if jw else None,
        'coverage': round(jw / float(tw), 4) if tw else None,
        'judged': len(judged),
        'unknown': len(rules) - len(judged),
        'low_coverage': bool(tw and jw / float(tw) < R.tol('low_coverage')),
    }


def jclean(v):
    """JSON 化：浮点收到 3 位（measured 只作展示，不参与对拍），其他原样"""
    if isinstance(v, bool) or v is None:
        return v
    if isinstance(v, float):
        return int(v) if abs(v - round(v)) < 1e-9 else round(v, 3)
    if isinstance(v, int):
        return v
    if isinstance(v, (list, tuple)):
        return [jclean(x) for x in v]
    if isinstance(v, dict):
        return dict((str(k), jclean(x)) for k, x in v.items())
    return str(v)


def build_info(F, R):
    out = {}
    for item in R.informational:
        iid = item.get('id')
        rec = {'name': item.get('name', iid), 'scored': bool(item.get('scored', False))}
        if iid == 'animation':
            rec['keyframes'] = list(F.keyframes)
            rec['decls'] = F.anim_count
            if item.get('text'):
                rec['text'] = fmt_text(item['text'], {'keyframes': len(F.keyframes),
                                                      'decls': F.anim_count})
        elif iid == 'bytes':
            rec['bytes'] = F.bytes
        elif iid == 'viewbox':
            rec['viewbox'] = F.vb
        elif iid == 'counts':
            for k in sorted(F.tags):
                rec[k] = len(F.tags[k])
            rec['straight_segments'] = len(F.segments)
        out[iid] = rec
    return out


def build_report(path, R):
    svg, raw = read_svg(path)
    F = SvgFacts(svg, len(raw.encode('utf-8')), raw=raw)
    roles = infer_roles(F)
    rules = evaluate(F, R, roles)
    rep = {
        'tool': TOOL_NAME,
        'rubric_version': R.version,
        'file': os.path.basename(str(path)),
        'bytes': F.bytes,
        'viewbox': F.vb,
        'degraded': F.degraded,
        'roles': jclean(roles),
        'info': jclean(build_info(F, R)),
        'rules': rules,
        'summary': summarize(rules, R),
    }
    return rep, F


# ---------------------------------------------------------------- 报告层
def bar(ok):
    return '✅' if ok is True else ('⚠️' if ok is None else '❌')


SECTION_TAG = {'relation': '客观可算', 'parts': '计数取证', 'presentation': '观感项'}


def render(rep, F, R):
    L = []
    A = L.append
    by_id = dict((r['id'], r) for r in rep['rules'])
    groups = []
    for gid, _ in R.rules:
        if gid not in groups:
            groups.append(gid)

    A('■ %s   %d bytes' % (rep['file'], rep['bytes']))
    A('  画布 viewBox = %s' % (rep['viewbox'],))
    c = rep['info'].get('counts', {})
    A('  元素：path %s · line %s · circle %s · ellipse %s · rect %s'
      % (c.get('path'), c.get('line'), c.get('circle'), c.get('ellipse'), c.get('rect')))
    an = rep['info'].get('animation', {})
    A('  渐变 %d 个 · @keyframes %d 个 %s · animation 声明 %d 处'
      % (F.gradients, len(F.keyframes), F.keyframes, F.anim_count))
    if rep['degraded']:
        A('  ⚠️  XML 整块解析失败，已降级为宽松正则解析 —— 下面的几何结论置信度降低')
    if an.get('text'):
        A('  动画：%s  （静态图也能过关，不算硬伤，不计分）'
          % (an['text'] if F.anim_count else '无'))

    for gid in groups:
        gname = next((g.get('name') or g.get('id') for g in (R.doc.get('groups') or [])
                      if g.get('id') == gid), gid)
        A('')
        A('  [%s —— %s]' % (gname, SECTION_TAG.get(gid, '逐条判据')))
        if gid == 'relation':
            g = F.ground()
            bottom = g['bottom'] if g else None
            if g:
                A('  轮子最低点 y=%.0f，画布底 y=%.0f，留白 %.0fpx'
                  % (g['bottom'], g['vb_bottom'], g['margin']))
            if F.horizontals:
                pairs, cross = F.crossings()
                flag = 0
                for h in F.horizontals:
                    inside = [w for w in F.wheels
                              if (h['x1'] >= w['cx'] - w['r'] and h['x0'] <= w['cx'] + w['r']
                                  and abs(h['y'] - w['cy']) < w['r'] * 0.9)]
                    note = ''
                    if inside and bottom is not None:
                        note = ('  ← 落在轮子内部：轮底 y=%.0f，线比轮底高 %.0fpx'
                                % (bottom, bottom - h['y']))
                        flag += 1
                    A('  水平线 y=%.0f（%s，x∈[%.0f,%.0f]）%s'
                      % (h['y'], h['how'], h['x0'], h['x1'], note))
                if flag:
                    A('  ⚠️  %d 条水平线与轮子相交（x 区间已校验重叠）—— 若它们是地面/路面线，'
                      '说明**车没站在线上**' % flag)
                else:
                    A('  ✅ 没有水平线穿过轮子')
                if not pairs:
                    A('  （判定同时校验横线的 x 区间与轮子相交 —— 只比 y 会把踏板、车把误报成地面）')
            else:
                A('  ⏭  一条水平线都没有，找不到"地面"（验不了 ≠ 通过）')
            wg = F.wheel_gap()
            if wg:
                A('  推定车轮 r=%.0f/%.0f，中心距 %.0f vs 半径和 %.0f'
                  % (wg['r'][0], wg['r'][1], wg['dist'], wg['sum_r']))
        if gid == 'parts':
            A('  取证：辐条 几何 %d / class %d · 曲柄踏板 几何 %d / class %d · 直线段 %d 条'
              % (F.spoke_evidence()[0], F.spokes_named,
                 F.crank_evidence(R.tol('vertex_merge_px'))[0],
                 F._class_count(r'pedal|crank'), len(F.segments)))
            for name, pat in [('腿', r'\b(leg|limb)\b'), ('翅', r'wing')]:
                n = F._class_count(pat)
                if n:
                    A('  %s：%d 处' % (name, n))
                else:
                    A('  %s：未检测到（可能是命名习惯不同，**不能当作没有**，需人工看图）' % name)
        if gid == 'presentation':
            A('  渐变着色：%d 个 · 不同填充色 %d 层 · 主体包围盒 %s'
              % (F.gradients, F.fill_layers, F.bbox))
        for r in rep['rules']:
            if r['group'] != gid:
                continue
            A('  %s %-12s w%-3s %s' % (VERDICT_BAR[r['verdict']], r['id'], r['weight'], r['text']))

    s = rep['summary']
    A('')
    A('  ── 总分 %s · 覆盖率 %s · 已判 %d / 无判据 %d（rubric v%s）'
      % (s['score'], ('%.4f' % s['coverage']) if s['coverage'] is not None else 'n/a',
         s['judged'], s['unknown'], R.version))
    if s['low_coverage']:
        A('  ⚠️  低置信：覆盖率 %.4f 低于 rubric.tolerances.low_coverage=%s —— '
          '总分必须与这条警示同屏阅读，别单独引用分数' % (s['coverage'], R.tol('low_coverage')))

    A('')
    A('  [人工核对清单 —— 机器不敢替你判的]')
    for r in rep['rules']:
        if r['verdict'] == 'unknown':
            nm = R.by_id[r['id']].get('name') or r['id']
            A('    ⏭ %s：%s' % (nm, r['text']))
    for m in R.manual_only:
        A('    □ %s —— %s' % (m.get('name') or m.get('id'), m.get('why', '')))
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


def _chk(ok, flag, desc):
    print('  %s %s' % (flag, desc))
    return bool(ok)


def self_check():
    ok = True
    for desc, svg, want_sep in SELF_TESTS:
        F = SvgFacts(svg, len(svg), raw=svg)
        wg = F.wheel_gap()
        got = wg['dist'] > wg['sum_r'] if wg else None
        good = got == want_sep
        ok = ok and good
        print('  %s 自检：%s → 两轮分离=%s（期望 %s）'
              % ('✓' if good else '✗', desc, got, want_sep))
    # 地面线穿轮检测
    svg = ('<svg viewBox="0 0 100 100"><circle cx="50" cy="50" r="40"/>'
           '<path d="M0 50H100"/></svg>')
    F = SvgFacts(svg, len(svg), raw=svg)
    hit = bool(F.lines_through_wheels())
    ok = _chk(hit, '✓' if hit else '✗',
              '自检：地面线横穿轮心 → 命中=%s（期望 True）' % hit) and ok
    # CSS 在 SVG 块之外也要能读到（第一版漏过）
    F = SvgFacts('<svg viewBox="0 0 10 10"><rect width="10" height="10"/></svg>', 10,
                 raw='<style>@keyframes spin{to{transform:rotate(360deg)}}.a{animation:spin 1s}</style>')
    good = F.keyframes == ['spin'] and F.anim_count == 1
    ok = _chk(good, '✓' if good else '✗',
              '自检：<svg> 块外的 @keyframes 也要读到 → %s' % F.keyframes) and ok
    # 扁长矩形当地面也要认出来（实测漏过一次）
    svg = ('<svg viewBox="0 0 100 100"><circle cx="50" cy="40" r="30"/>'
           '<rect x="0" y="70" width="100" height="6"/></svg>')
    F = SvgFacts(svg, len(svg), raw=svg)
    found = [h['how'] for h in F.horizontals]
    good = found == ['rect(扁长)']
    ok = _chk(good, '✓' if good else '✗', '自检：扁长矩形当地面 → %s' % found) and ok
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
        ok = _chk(good, '✓' if good else '✗',
                  '自检：%s → 轮底与地面差 %spx（期望 %s）' % (name, got, want)) and ok
    # 无轮子时不炸
    try:
        F = SvgFacts('<svg viewBox="0 0 10 10"><rect width="10" height="10"/></svg>', 10)
        F.wheel_gap(); F.ground(); F.lines_through_wheels()
        print('  ✓ 自检：无圆 / 无 viewBox 时不崩')
    except Exception as e:
        ok = False
        print('  ✗ 自检：无圆时报错 %s' % e)

    # ===================== 以下为两处真 bug 的回归断言 + 契约引擎断言 =====================
    # 【真 bug ①】相对坐标被当绝对坐标：M336 322 h22 是一条 22px 的短横线，
    # 旧正则接受小写 h 却把 22 当成绝对 x ⇒ 解析出 x2=22，长度算成 314，
    # 于是"踏板"被误报成"地面线穿轮"。
    segs, pts, hull = parse_path('M336 322 h22')
    good = segs == [(336.0, 322.0, 358.0, 322.0)] and pts == [(336.0, 322.0), (358.0, 322.0)]
    ok = _chk(good, '✓' if good else '✗',
              '自检：相对 h 转绝对 → M336 322 h22 解析为 x∈[336,358] 的 22px 线段 → %s' % (segs,)) and ok
    # 22px 的短线不得混进"长横线（地面候选）"池
    F = SvgFacts('<svg viewBox="0 0 640 420"><path d="M336 322 h22"/></svg>', 30)
    good = F.horizontals == []
    ok = _chk(good, '✓' if good else '✗',
              '自检：22px 短横线不得当地面候选 → %s' % F.horizontals) and ok
    # 已实现的子集必须语义正确（混合相对/绝对 + 负增量）
    segs, _, _ = parse_path('M10 10 l20 20 v30 h-25 L100 60')
    want = [(10.0, 10.0, 30.0, 30.0), (30.0, 30.0, 30.0, 60.0), (30.0, 60.0, 5.0, 60.0),
            (5.0, 60.0, 100.0, 60.0)]
    good = segs == want
    ok = _chk(good, '✓' if good else '✗',
              '自检：m/l/h/v 相对→绝对混合解析 → %s（期望 %s）' % (segs, want)) and ok
    # 曲线命令：直线段仍为空；pts 只收精确端点；hull 才收控制点 ——
    # 下垂量得出来，但包围盒不用它 ⇒ 裁切判定永远不会被控制点"撑爆"
    segs, pts, hull = parse_path('M100 100 q35 22 62 20')
    good = segs == [] and pts == [(100.0, 100.0), (162.0, 120.0)] \
        and (135.0, 122.0) in hull and (135.0, 122.0) not in pts
    ok = _chk(good, '✓' if good else '✗',
              '自检：曲线命令不臆造直线段，控制点只进 hull → segs=%s pts=%s hull=%s'
              % (segs, pts, hull)) and ok
    # 【真 bug ②】穿轮判定只比 y ⇒ x 轴盲区
    svg = ('<svg viewBox="0 0 1000 400"><circle cx="200" cy="200" r="100"/>'
           '<line x1="500" y1="200" x2="1000" y2="200"/></svg>')
    F = SvgFacts(svg, len(svg), raw=svg)
    good = bool(F.horizontals) and F.lines_through_wheels() == []
    ok = _chk(good, '✓' if good else '✗',
              '自检：x 区间不重叠的横线不得判穿轮 → 横线 %d 条，命中 %s'
              % (len(F.horizontals), F.lines_through_wheels())) and ok
    # 反方向：x 区间真重叠的必须仍然命中（别把修复做成新的盲区）
    svg = ('<svg viewBox="0 0 1000 400"><circle cx="200" cy="200" r="100"/>'
           '<line x1="0" y1="200" x2="1000" y2="200"/></svg>')
    F = SvgFacts(svg, len(svg), raw=svg)
    good = len(F.lines_through_wheels()) == 1
    ok = _chk(good, '✓' if good else '✗',
              '自检：x 区间重叠的横线仍须判穿轮 → 命中 %d（期望 1）'
              % len(F.lines_through_wheels())) and ok

    # ---------------- 契约引擎 ----------------
    try:
        R = load_rubric()
    except RubricError as e:
        print('  ✗ 自检：契约加载失败 —— %s' % e)
        print('  ⇒ 自检有失败')
        return 1
    good = len(R.rules) == 15 and R.version == (R.doc.get('version'))
    ok = _chk(good, '✓' if good else '✗',
              '自检：契约加载 → %d 条规则 / version=%s（期望 15 条）'
              % (len(R.rules), R.version)) and ok
    # criteria 语义：自上而下第一条命中；max_abs 比 |value|；@key 解引用容差
    t, tw = R.tol('ground_touch_px'), R.tol('ground_touch_warn_px')
    seq = [(t, 'pass'), (tw, 'warn'), (tw + 1, 'fail'), (-tw - 1, 'fail')]
    bad = [x for x, w in seq if R.eval_criteria('wheel_ground_contact', x)['verdict'] != w]
    good = not bad
    ok = _chk(good, '✓' if good else '✗',
              '自检：criteria 求值（max_abs / 兜底 / @容差解引用）→ 失配 %s' % bad) and ok
    # 占位符：取不到的原样保留，不静默吞掉
    s = fmt_text('轮子{sign}地面 {absdiff:.0f}px（{nope}）', with_derived({'diff': -47.0}))
    good = s == '轮子沉入地面 47px（{nope}）'
    ok = _chk(good, '✓' if good else '✗',
              '自检：{sign}/{absdiff} 派生 + 未知占位符原样保留 → %r' % s) and ok
    good = fmt_text('差 {d:+.0f}px', {'d': 6.0}) == '差 +6px'
    ok = _chk(good, '✓' if good else '✗',
              '自检：格式说明符 :+.0f → %r' % fmt_text('差 {d:+.0f}px', {'d': 6.0})) and ok
    # 角色推定的边界：只许自动推定 wheel_rear / wheel_front / ground，
    # 其余（喙尖、眼、脚、踏板、座垫、座管顶、喉囊、车把）**一个都不许猜** ——
    # 自动认错元素就是诬告，代价比少判一条规则大得多。
    svg = ('<svg viewBox="0 0 640 420"><circle cx="196" cy="300" r="66"/>'
           '<circle cx="452" cy="300" r="66"/><path d="M0 366 H640"/>'
           '<circle cx="352" cy="82" r="4.5"/><path d="M376 82 L470 96 L376 104 Z"/></svg>')
    got_roles = infer_roles(SvgFacts(svg, len(svg), raw=svg))
    leaked = sorted(set(got_roles) & set(NEVER_GUESS))
    good = not leaked and sorted(got_roles) == sorted(AUTO_ROLES)
    ok = _chk(good, '✓' if good else '✗',
              '自检：只推定 %s，绝不猜 %s → 实际推出 %s，越界 %s'
              % ('/'.join(AUTO_ROLES), '/'.join(NEVER_GUESS), sorted(got_roles), leaked)) and ok
    # 铁律 3：每条规则都必须同时有 unknown 文案与 requires；证据政策必须覆盖全部规则
    miss_u = [r['id'] for _, r in R.rules if not r.get('unknown')]
    miss_req = [r['id'] for _, r in R.rules if 'requires' not in r]
    uncovered = sorted(set(r['id'] for _, r in R.rules) - R.hybrid - R.geometric)
    overlap = sorted(R.hybrid & R.geometric)
    good = not (miss_u or miss_req or uncovered or overlap)
    ok = _chk(good, '✓' if good else '✗',
              '自检：铁律 3（每条规则都有 unknown + requires）& 证据政策全覆盖且不重叠 → '
              '缺文案 %s / 缺 requires %s / 无政策 %s / 双属 %s'
              % (miss_u, miss_req, uncovered, overlap)) and ok
    # 端到端：一份"只有两只轮子和地面"的产出 —— hybrid 一律 unknown，geometric 照判
    svg = ('<svg viewBox="0 0 200 100"><circle cx="50" cy="70" r="20"/>'
           '<circle cx="150" cy="70" r="20"/><path d="M0 90H200"/></svg>')
    F = SvgFacts(svg, len(svg), raw=svg)
    reps = evaluate(F, R, infer_roles(F))
    by = dict((r['id'], r) for r in reps)
    hy = dict((i, by[i]['verdict']) for i in sorted(R.hybrid))
    good = all(v == 'unknown' for v in hy.values()) and \
        all(by[i]['score'] is None for i in R.hybrid)
    ok = _chk(good, '✓' if good else '✗',
              '自检：hybrid 拿不出正面证据时给 unknown 而非 fail（score 必为 null）→ %s' % hy) and ok
    # geometric 组：缺席可证 ⇒ 轮子认得出来时辐条必须真判（这里是 0 根 → fail）
    good = by['spokes_present']['verdict'] == 'fail' and by['spokes_present']['score'] == 0.0
    ok = _chk(good, '✓' if good else '✗',
              '自检：geometric（缺席可证）给 fail 而不是 unknown → spokes_present=%s'
              % by['spokes_present']['verdict']) and ok
    geo_judged = [i for i in ('frame_triangles', 'within_canvas', 'gradient_depth')
                  if by[i]['verdict'] == 'unknown']
    ok = _chk(not geo_judged, '✓' if not geo_judged else '✗',
              '自检：geometric 三兄弟必须真判 → 逃成 unknown 的 %s（实际 %s）'
              % (geo_judged, dict((i, by[i]['verdict']) for i in
                                  ('frame_triangles', 'within_canvas', 'gradient_depth')))) and ok
    # unknown 规则必须同时 score=null 且不进分母（对拍口径第 4 条）
    bad = [r['id'] for r in reps if r['verdict'] == 'unknown' and r['score'] is not None]
    s = summarize(reps, R)
    good = not bad and s['judged'] + s['unknown'] == len(reps)
    ok = _chk(good, '✓' if good else '✗',
              '自检：unknown ⇒ score 必为 null 且从分子分母同时剔除 → %s（summary %s）'
              % (bad, (s['judged'], s['unknown']))) and ok

    print('  ⇒ 自检%s' % ('全部通过' if ok else '有失败'))
    return 0 if ok else 1


# ---------------------------------------------------------------- CLI
def _utf8_stdout():
    """Windows 控制台默认 GBK，报告里的 ✅/⚠️ 会直接 UnicodeEncodeError —— 统一切到 UTF-8"""
    for s in (sys.stdout, sys.stderr):
        try:
            if hasattr(s, 'reconfigure'):
                s.reconfigure(encoding='utf-8', errors='replace')
        except Exception:
            pass


def main():
    _utf8_stdout()
    ap = argparse.ArgumentParser(description='矢量产出体检器（鹈鹕基准）—— 判据一律来自 rubric.json')
    ap.add_argument('files', nargs='*', help='一个或两个 .svg/.html 文件')
    ap.add_argument('--check', action='store_true', help='只跑脚本自检')
    ap.add_argument('--json', dest='as_json', action='store_true',
                    help='按 rubric._report.schema 产出 JSON 到 stdout（两份输入时产出数组）')
    args = ap.parse_args()

    if args.check:
        return self_check()
    if not args.files:
        ap.print_help()
        return 2

    try:
        R = load_rubric()
    except RubricError as e:
        sys.stderr.write('契约错误：%s\n' % e)
        return 3

    reps = []
    for f in args.files[:2]:
        if not os.path.exists(f):
            sys.stderr.write('找不到文件：%s\n' % f)
            return 2
        try:
            reps.append(build_report(f, R))
        except RubricError as e:
            sys.stderr.write('契约错误：%s\n' % e)
            return 3

    if args.as_json:
        obj = reps[0][0] if len(reps) == 1 else [r[0] for r in reps]
        sys.stdout.write(json.dumps(obj, ensure_ascii=False, indent=2) + '\n')
        return 0

    print('=' * 78)
    for rep, F in reps:
        print(render(rep, F, R))
        print('=' * 78)
    if len(reps) >= 2:
        print('两份产出的数已经都列在上面 —— 逐项对照即可（判据优先于印象）。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
