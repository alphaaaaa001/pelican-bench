# 题面与读图须知

## v1 · 原始题面（Simon Willison，2024-10 起）

```
Generate an SVG of a pelican riding a bicycle.
```

**它的价值在于"什么都没说"** —— 不给提示、不给风格、不给部件清单，所以能照出模型**自己**对"一只鹈鹕骑车"的空间理解。

## v2 · 升级题面（要求逐条可核）

```
Generate an SVG of a California brown pelican riding a bicycle.
The bicycle must have spokes and a correctly shaped bicycle frame.
The pelican must have its characteristic large pouch, and there should be
a clear indication of feathers. The pelican must be clearly pedaling the
bicycle. The image should show the full breeding plumage of the
California brown pelican.
```

逐条可核：**辐条 / 车架形状 / 喉囊 / 羽毛 / 明显在蹬 / 繁殖羽色**，比 v1 更能拉开差距。

> 冷知识：当时**没有任何一家模型**意识到"加州棕鹈鹕（*Pelecanus occidentalis californicus*）的繁殖期羽毛其实不是棕色的"（繁殖期是深红褐颈 + 亮红喉囊，非繁殖期才是白头白颈）。所以这一句本身就是个很好的**"真懂还是假懂"探针**。

---

## 怎么读图（三条铁律）

**1. 先看"关系"，再看"零件"。**
零件全是可枚举的，眼睛容易满足；关系才最容易坏：

- **朝向一致吗** —— 喙指哪边？车头（前叉接着的那只轮）指哪边？两边反了 = **背对骑行方向**
- **真的接触吗** —— 轮底 y 是否落在路面线上？脚是否落在踏板端点上？（不是"离得近"，是"落在上面"）
- **挂点对吗** —— 座垫在座管顶端，还是在车把上？

**2. 控制变量，否则你测的是 harness 不是模型。**
同题面、同客户端、**thinking / reasoning 开着**。异步开着思考和关着，是两次不同的实验。

**3. "差"不等于"换芯"。**
连真旗舰都会画出歪的鹈鹕 ⇒ 单看一张**不能**下结论；**同题对照、差距明显**才是信号。
并且：**模型自报的身份（"我是 XXX"）是客户端人设，不是后端证据。**

---

## 配套的自动化部分

上面"真的接触吗 / 朝向对不对"里的**可算部分**，`svgcheck.py` 已经替你量了：

```bash
python svgcheck.py A.html B.svg      # 两份产出并排出数
```

**语义部分（朝向、脚在不在踏板上、有没有喉囊）脚本不判，只打印人工核对清单** —— 因为猜错了就是诬告。
