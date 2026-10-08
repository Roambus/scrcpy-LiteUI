using System.Windows;
using System.Windows.Media;

namespace Kuaitou.App.Views;

/// <summary>一个图标的两个几何：<see cref="Outline"/> 只描边，<see cref="Solid"/> 只填充（可为 null）。</summary>
public sealed record MirrorGlyph(Geometry Outline, Geometry? Solid);

/// <summary>
/// 投屏窗口右侧功能栏那 9 个图标。1:1 移植 legacy/kuaitou/winbar.py 里那套 GDI 现画的数学：
/// 画布 24×24、中心 (12,12)、单位 u = 9、线宽 1.7，统一是「细线 + 圆头圆角」的观感。
///
/// 描边几何（开口折线）与实心几何（铃舌、滑杆手柄）刻意分成两个 Geometry：
/// 开口折线一旦设了 Fill 会被自动闭合填满，所以必须分开画。
/// </summary>
public static class MirrorGlyphs
{
    private const double Cx = 12.0;
    private const double Cy = 12.0;
    private const double U = 9.0;

    private static readonly Dictionary<string, MirrorGlyph> Cache = Build();

    /// <summary>按动作名取图标；未知动作返回 null。</summary>
    public static MirrorGlyph? For(string action)
        => Cache.TryGetValue(action, out MirrorGlyph? glyph) ? glyph : null;

    private static Dictionary<string, MirrorGlyph> Build() => new(StringComparer.Ordinal)
    {
        ["pin"] = Pin(),
        ["volume_up"] = Volume(up: true),
        ["volume_down"] = Volume(up: false),
        ["rotate_lock"] = Rotate(),
        ["back"] = Back(),
        ["home"] = Home(),
        ["app_switch"] = AppSwitch(),
        ["notifications"] = Notifications(),
        ["control_center"] = ControlCenter(),
    };

    // ---------- 各图标 ----------

    /// <summary>置顶：顶上一条横线当天花板，下面一个朝上的折角箭头。</summary>
    private static MirrorGlyph Pin()
    {
        double ceilingY = Cy - U * .82;
        Geometry ceiling = Line(Cx - U * .78, ceilingY, Cx + U * .78, ceilingY);
        Geometry arrow = Polyline(closed: false,
            Point(Cx - U * .52, Cy - U * .06),
            Point(Cx, Cy - U * .58),
            Point(Cx + U * .52, Cy - U * .06));
        Geometry stem = Line(Cx, Cy - U * .5, Cx, Cy + U * .72);
        return new MirrorGlyph(Group(ceiling, arrow, stem), null);
    }

    /// <summary>音量：喇叭 + 声波。加号那边多一道弧，两者一眼能分开。</summary>
    private static MirrorGlyph Volume(bool up)
    {
        double left = Cx - U * .85;
        double right = Cx + U * .04;
        Geometry body = Polyline(closed: true,
            Point(left, Cy - U * .3),
            Point(Cx - U * .38, Cy - U * .3),
            Point(right, Cy - U * .78),
            Point(right, Cy + U * .78),
            Point(Cx - U * .38, Cy + U * .3),
            Point(left, Cy + U * .3));
        double tip = Cx + U * .16;
        Geometry inner = Arc(tip, Cy, U * .42, U * .42, -48, 48);
        if (!up)
        {
            return new MirrorGlyph(Group(body, inner), null);
        }
        Geometry outer = Arc(tip, Cy, U * .8, U * .8, -48, 48);
        return new MirrorGlyph(Group(body, inner, outer), null);
    }

    /// <summary>强制横屏：横过来的手机（圆角屏 + 底部一条短横线当手势条）。</summary>
    private static MirrorGlyph Rotate()
    {
        double w = U * .95, h = U * .58;
        Geometry body = RoundRect(Cx - w, Cy - h, Cx + w, Cy + h, U * .3);
        Geometry grip = Line(Cx - U * .2, Cy + h * .52, Cx + U * .2, Cy + h * .52);
        return new MirrorGlyph(Group(body, grip), null);
    }

    /// <summary>返回：一个左折角（现代安卓的返回手势图标）。</summary>
    private static MirrorGlyph Back()
    {
        Geometry arrow = Polyline(closed: false,
            Point(Cx + U * .12, Cy - U * .72),
            Point(Cx - U * .6, Cy),
            Point(Cx + U * .12, Cy + U * .72));
        return new MirrorGlyph(arrow, null);
    }

    /// <summary>桌面：一个圆（安卓导航栏上的主页就是它）。</summary>
    private static MirrorGlyph Home()
        => new(Oval(Cx, Cy, U * .7), null);

    /// <summary>多任务：一个圆角方框（安卓导航栏上的最近任务）。</summary>
    private static MirrorGlyph AppSwitch()
    {
        double r = U * .66;
        return new MirrorGlyph(RoundRect(Cx - r, Cy - r, Cx + r, Cy + r, U * .26), null);
    }

    /// <summary>通知栏：铃铛（圆顶 + 两侧直壁 + 底沿 + 一个铃舌）。</summary>
    private static MirrorGlyph Notifications()
    {
        double r = U * .56;
        double bellyTop = Cy - U * .1;
        double bellyBottom = Cy + U * .4;
        Geometry dome = Arc(Cx, bellyTop, r, r, 0, 180);
        Geometry left = Line(Cx - r, bellyTop, Cx - r, bellyBottom);
        Geometry right = Line(Cx + r, bellyTop, Cx + r, bellyBottom);
        Geometry brim = Line(Cx - U * .78, bellyBottom, Cx + U * .78, bellyBottom);
        Geometry clapper = Oval(Cx, Cy + U * .76, Math.Max(1.0, U * .17));
        return new MirrorGlyph(Group(dome, left, right, brim), Group(clapper));
    }

    /// <summary>控制中心：两条滑杆各带一个滑块（快捷设置面板）。</summary>
    private static MirrorGlyph ControlCenter()
    {
        double topY = Cy - U * .42;
        double bottomY = Cy + U * .42;
        Geometry topRail = Line(Cx - U * .85, topY, Cx + U * .85, topY);
        Geometry bottomRail = Line(Cx - U * .85, bottomY, Cx + U * .85, bottomY);
        Geometry topKnob = Oval(Cx - U * .28, topY, Math.Max(1.5, U * .26));
        Geometry bottomKnob = Oval(Cx + U * .36, bottomY, Math.Max(1.5, U * .26));
        return new MirrorGlyph(Group(topRail, bottomRail), Group(topKnob, bottomKnob));
    }

    // ---------- 图元 ----------

    private static Point Point(double x, double y) => new(x, y);

    private static Geometry Line(double x1, double y1, double x2, double y2)
        => new LineGeometry(Point(x1, y1), Point(x2, y2));

    private static Geometry Oval(double cx, double cy, double r)
        => new EllipseGeometry(Point(cx, cy), r, r);

    private static Geometry RoundRect(double x1, double y1, double x2, double y2, double r)
        => new RectangleGeometry(new Rect(Point(x1, y1), Point(x2, y2)), r, r);

    /// <summary>折线。拐角由 StrokeLineJoin=Round 抹圆，箭头那种折角才不会显得尖。</summary>
    private static Geometry Polyline(bool closed, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: closed);
            for (int i = 1; i < points.Length; i++)
            {
                ctx.LineTo(points[i], isStroked: true, isSmoothJoin: true);
            }
        }
        return geometry;
    }

    /// <summary>圆弧（角度制，0° = 正右、逆时针为正，与 GDI 的 Arc 一致）。</summary>
    private static Geometry Arc(double cx, double cy, double rx, double ry, double a0, double a1)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(At(cx, cy, rx, ry, a0), isFilled: false, isClosed: false);
            ctx.ArcTo(
                At(cx, cy, rx, ry, a1), new Size(rx, ry), 0, isLargeArc: false,
                SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: false);
        }
        return geometry;
    }

    private static Point At(double cx, double cy, double rx, double ry, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return Point(cx + rx * Math.Cos(rad), cy - ry * Math.Sin(rad));
    }

    /// <summary>把若干图元合成一个几何并冻结（冻结后可在多个窗口间共享，渲染也更快）。</summary>
    private static Geometry Group(params Geometry[] parts)
    {
        if (parts.Length == 1)
        {
            parts[0].Freeze();
            return parts[0];
        }

        var group = new GeometryGroup();
        foreach (Geometry part in parts)
        {
            part.Freeze();
            group.Children.Add(part);
        }
        group.Freeze();
        return group;
    }
}
