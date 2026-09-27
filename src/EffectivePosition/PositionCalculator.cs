using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace EffectivePosition;

/// <summary>適用されたグループ制御 1 つ分。値はそのフレームでのグループ制御自身の値。</summary>
public sealed record GroupStep(GroupItem Group, string LayerLabel, double X, double Y, double Z, double Zoom, double Rotation, bool IsComposite);

public sealed record EffectivePositionResult(
    double X, double Y, double Z, double Zoom, double Rotation,
    double OwnX, double OwnY, double OwnZ, double OwnZoom, double OwnRotation,
    IReadOnlyList<GroupStep> Steps,
    int Frame,
    bool IsOutsideItem,
    bool HasVideoEffects,
    bool HasImageCenterComposite);

/// <summary>
/// グループ制御を含めた座標を、YMM4 の描画と同じ規則で計算する。
/// 規則は YMM4 本体の TimelineGroupResolver と GroupedDrawingEffect に合わせてある:
///   ・親になるのは、非表示でなく、表示中のレイヤーにあり、そのフレームを含むグループ制御のうち
///     「上のレイヤーにあり、対象がその範囲内」かつ「グループのみ対象なら同じグループ番号」のもの。
///     親の親も辿る。
///   ・レイヤーの大きい（内側の）グループから順に  位置 = G位置 + 回転(G回転)・(G拡大率 × 位置)。
///   ・画像化（IsComposite）のグループで一旦止まり、そのグループ自身の変形を掛けてから、
///     そのグループの親を同じ規則で辿る。
/// 映像エフェクトによる移動は含まない（エフェクトを実行しないと値が決まらないため）。
/// </summary>
public static class PositionCalculator
{
    public static EffectivePositionResult? Calculate(Timeline timeline, IItem item, int frame)
    {
        if (item is not VisualItem visual)
            return null;

        int fps = timeline.VideoInfo.FPS;
        // 再生位置がアイテムの外なら、アイテムの端のフレームで計算する
        bool outside = frame < item.Frame || frame >= item.Frame + Math.Max(1, item.Length);
        int f = Math.Clamp(frame, item.Frame, item.Frame + Math.Max(1, item.Length) - 1);

        var (ox, oy, oz, ozoom, orot) = Evaluate(visual, f, fps);
        double x = ox, y = oy, z = oz, zoom = ozoom / 100.0, rot = orot;

        var visibles = timeline.LayerSettings.IsVisibles;
        var candidates = timeline.Items
            .OfType<GroupItem>()
            .Where(g => !g.IsHidden && visibles[g.Layer] && ContainsFrame(g, f))
            .ToList();

        var steps = new List<GroupStep>();
        bool hasEffects = visual.VideoEffects.Any(e => e.IsEnabled);
        bool imageCenter = false;

        IItem current = item;
        var applied = new HashSet<GroupItem>();
        while (true)
        {
            var ordered = ParentsAndGrandparents(current, candidates)
                .Distinct()
                .OrderByDescending(g => g.Layer)
                .ToList();

            GroupItem? composite = null;
            foreach (var g in ordered)
            {
                if (g.IsComposite)
                {
                    composite = g;
                    break;
                }
                if (!applied.Add(g))
                    continue;
                Apply(timeline, g, f, fps, ref x, ref y, ref z, ref zoom, ref rot, steps, isComposite: false);
                hasEffects |= g.VideoEffects.Any(e => e.IsEnabled);
            }

            if (composite is null || !applied.Add(composite))
                break;

            // 画像化したグループは、中身を 1 枚の画像にしてからグループ自身の座標・拡大率・回転で描く。
            // 「画面中心」なら上と同じ式。「画像中心」は中身の描画範囲の中心 c を軸に拡大・回転し、c だけずらして置く
            // （YMM4 の CompositeSource）。c は描画しないと分からないので、中身がこのアイテムだけのときに正確になる
            // 「c = このアイテムの位置」で計算し、ほかにも中身があれば近似として注記する。
            if (composite.CompositeCenter == GroupItemCompositeCenter.ImageCenter)
            {
                Apply(timeline, composite, f, fps, ref x, ref y, ref z, ref zoom, ref rot, steps, isComposite: true, pivotX: x, pivotY: y);
                if (HasOtherContent(timeline, composite, item, candidates, f))
                    imageCenter = true;
            }
            else
            {
                Apply(timeline, composite, f, fps, ref x, ref y, ref z, ref zoom, ref rot, steps, isComposite: true);
            }
            hasEffects |= composite.VideoEffects.Any(e => e.IsEnabled);
            current = composite;
        }

        return new EffectivePositionResult(
            x, y, z, zoom * 100.0, rot,
            ox, oy, oz, ozoom, orot,
            steps, f, outside, hasEffects, imageCenter);
    }

    static void Apply(Timeline timeline, GroupItem g, int frame, int fps,
        ref double x, ref double y, ref double z, ref double zoom, ref double rot,
        List<GroupStep> steps, bool isComposite, double pivotX = 0, double pivotY = 0)
    {
        var (gx, gy, gz, gzoomPercent, grot) = Evaluate(g, frame, fps);
        double s = gzoomPercent / 100.0;
        double rad = grot / 180.0 * Math.PI;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double sx = (x - pivotX) * s, sy = (y - pivotY) * s, sz = z * s;
        x = gx + pivotX + (sx * cos - sy * sin);
        y = gy + pivotY + (sx * sin + sy * cos);
        z = gz + sz;
        zoom *= s;
        rot += grot;
        steps.Add(new GroupStep(g, timeline.LayerSettings.Labels[g.Layer], gx, gy, gz, gzoomPercent, grot, isComposite));
    }

    static (double X, double Y, double Z, double Zoom, double Rotation) Evaluate(VisualItem item, int timelineFrame, int fps)
    {
        long local = timelineFrame - item.Frame;
        long length = item.Length;
        return (
            item.X.GetValue(local, length, fps),
            item.Y.GetValue(local, length, fps),
            item.Z.GetValue(local, length, fps),
            item.Zoom.GetValue(local, length, fps),
            item.Rotation.GetValue(local, length, fps));
    }

    /// <summary>画像化グループの中に、対象アイテム以外に描画されるものがあるか</summary>
    static bool HasOtherContent(Timeline timeline, GroupItem composite, IItem target, IReadOnlyList<GroupItem> candidates, int frame)
    {
        var visibles = timeline.LayerSettings.IsVisibles;
        return timeline.Items.Any(i =>
            i != target && i is IVideoItem && i is not GroupItem
            && !i.IsHidden && visibles[i.Layer] && ContainsFrame(i, frame)
            && ParentsAndGrandparents(i, candidates).Contains(composite));
    }

    static bool ContainsFrame(IItem item, int frame) => item.Frame <= frame && frame < item.Frame + item.Length;

    static IEnumerable<GroupItem> Parents(IItem item, IReadOnlyList<GroupItem> groups) =>
        groups.Where(g => (!g.IsGroupOnly || (g.Group != 0 && g.Group == item.Group))
                          && g.Layer < item.Layer && item.Layer <= g.Layer + g.GroupRange);

    static IEnumerable<GroupItem> ParentsAndGrandparents(IItem item, IReadOnlyList<GroupItem> groups)
    {
        var parents = Parents(item, groups).ToList();
        foreach (var p in parents)
            yield return p;
        var rest = groups.Except(parents).ToList();
        foreach (var gp in parents.SelectMany(p => ParentsAndGrandparents(p, rest)))
            yield return gp;
    }
}
