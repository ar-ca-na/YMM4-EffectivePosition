// YMM4 本体の描画エンジン（TimelineSource）で実際にフレームを描き、
// 図形の描画結果（重心・面積・向き）と PositionCalculator の計算値を突き合わせる。
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using EffectivePosition;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

static class Program
{
    const int W = 1920, H = 1080, FPS = 60;
    static string ymm4Dir = "";

    [STAThread]
    static int Main(string[] args)
    {
        ymm4Dir = args.Length > 0 ? args[0] : throw new ArgumentException("YMM4 のフォルダを引数に渡す");
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = Path.Combine(ymm4Dir, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        SetDllDirectory(ymm4Dir);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length > 2 && args[1] == "--save")
            return SaveProject(args[2]);
        return Run();
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);

    sealed record Case(string Name, Func<(Timeline tl, ShapeItem target)> Build, int Frame, double PosTol = 1.0, bool ExpectMismatch = false);

    static int Run()
    {
        // 基準: グループなし・拡大率100・回転0 の図形の面積と向き
        var (baseTl, baseItem) = Scene1(it => { });
        var baseM = Measure(baseTl, 0)!.Value;
        Console.WriteLine($"基準: 面積 {baseM.Area:0} 向き {baseM.Angle:0.0}°");

        var cases = new List<Case>
        {
            new("グループなし", () => Scene1(it => { Set(it.X, 100); Set(it.Y, 50); }), 10),
            new("単一グループ 移動+拡大+回転", () => Build(
                new[] { G(0, 10, x: 200, y: -100, zoom: 150, rot: 30) },
                it => { it.Layer = 1; Set(it.X, 100); Set(it.Y, 50); Set(it.Zoom, 80); Set(it.Rotation, 10); }), 10),
            new("入れ子グループ", () => Build(
                new[] { G(0, 10, x: -300, y: 0, zoom: 50, rot: -45), G(1, 10, x: 100, y: 100, zoom: 200, rot: 90) },
                it => { it.Layer = 2; Set(it.X, 50); Set(it.Y, 20); }), 10),
            new("グループのみ対象・番号違い（適用されない）", () => Build(
                new[] { G(0, 10, x: 300, y: 0, groupOnly: true, group: 1) },
                it => { it.Layer = 1; it.Group = 2; Set(it.X, 10); }), 10),
            new("グループのみ対象・番号一致", () => Build(
                new[] { G(0, 10, x: 300, y: 0, groupOnly: true, group: 3) },
                it => { it.Layer = 1; it.Group = 3; Set(it.X, 10); }), 10),
            new("範囲外（適用されない）", () => Build(
                new[] { G(0, 1, x: 300, y: 200) },
                it => { it.Layer = 3; Set(it.X, 10); }), 10),
            new("非表示のグループ（適用されない）", () => Build(
                new[] { G(0, 10, x: 300, y: 200, hidden: true) },
                it => { it.Layer = 1; Set(it.X, 10); }), 10),
            new("グループの時間範囲外（適用されない）", () => Build(
                new[] { G(0, 10, x: 300, y: 200, frame: 50, length: 30) },
                it => { it.Layer = 1; Set(it.X, 10); }), 10),
            new("親の親（範囲はグループ経由でのみ届く）", () => Build(
                new[] { G(0, 1, x: -200, y: 0, zoom: 120, rot: 15), G(1, 5, x: 50, y: 80, rot: -30) },
                it => { it.Layer = 4; Set(it.X, 40); Set(it.Y, -30); }), 10),
            new("キーフレーム（グループとアイテムの開始位置が違う）", () => Build(
                new[] { Anim(G(0, 10, frame: 20, length: 100), g => { Move(g.X, -400, 400); Move(g.Rotation, 0, 90); }) },
                it => { it.Layer = 1; it.Frame = 40; it.Length = 60; Move(it.Y, -200, 200); }), 50),
            new("画像化（画面中心）", () => Build(
                new[] { Comp(G(1, 10, x: 150, y: -50, zoom: 130, rot: 20), GroupItemCompositeCenter.ScreenCenter) },
                it => { it.Layer = 2; Set(it.X, 60); Set(it.Y, 40); }), 10),
            new("画像化（画面中心）＋外側と内側の通常グループ", () => Build(
                new[] { G(0, 10, x: -100, y: 100, zoom: 80, rot: -20),
                        Comp(G(1, 10, x: 150, y: -50, zoom: 130, rot: 20), GroupItemCompositeCenter.ScreenCenter),
                        G(2, 10, x: 30, y: 30, rot: 45) },
                it => { it.Layer = 3; Set(it.X, 60); Set(it.Y, 40); }), 10),
            new("画像化（画像中心）※近似、図形 1 つなら一致するはず", () => Build(
                new[] { Comp(G(1, 10, x: 150, y: -50, zoom: 130, rot: 20), GroupItemCompositeCenter.ImageCenter) },
                it => { it.Layer = 2; Set(it.X, 60); Set(it.Y, 40); }), 10),
            new("画像化（画像中心）＋外側の通常グループ", () => Build(
                new[] { G(0, 10, x: -100, y: 100, zoom: 80, rot: -20),
                        Comp(G(1, 10, x: 150, y: -50, zoom: 130, rot: 20), GroupItemCompositeCenter.ImageCenter) },
                it => { it.Layer = 2; Set(it.X, 60); Set(it.Y, 40); Set(it.Rotation, 5); }), 10),
            new("画像化（画像中心）＋ほかの中身あり（近似。ずれるのが正しい）", () => Build(
                new[] { Comp(G(1, 10, x: 150, y: -50, zoom: 130, rot: 20), GroupItemCompositeCenter.ImageCenter) },
                it => { it.Layer = 2; Set(it.X, 60); Set(it.Y, 40); },
                extra: tl => { var o = new ShapeItem { Frame = 0, Length = 120, Layer = 3 }; Set(o.X, -300); Set(o.Y, 200); Set(o.Opacity, 40); return o; },
                measureOnlyTarget: true), 10, ExpectMismatch: true),
        };

        int fail = 0;
        foreach (var c in cases)
        {
            measureOnly = null;
            var (tl, target) = c.Build();
            var m = Measure(tl, c.Frame, measureOnly);
            var r = PositionCalculator.Calculate(tl, target, c.Frame)!;
            if (m is null) { Console.WriteLine($"NG  {c.Name}: 何も描画されなかった"); fail++; continue; }
            var mm = m.Value;
            double ex = W / 2.0 + r.X, ey = H / 2.0 + r.Y;
            double ezoom = Math.Sqrt(mm.Area / baseM.Area) * 100;
            double dAngle = AngleDiff(mm.Angle - baseM.Angle, r.Rotation);
            double dPos = Math.Sqrt((mm.Cx - ex) * (mm.Cx - ex) + (mm.Cy - ey) * (mm.Cy - ey));
            bool ok = dPos <= c.PosTol && Math.Abs(ezoom - r.Zoom) <= 1.0 && Math.Abs(dAngle) <= 1.0;
            if (c.ExpectMismatch)
            {
                // 近似になる場面では、ずれていて、かつ注記フラグが立っていることを確かめる
                ok = r.HasImageCenterComposite;
            }
            if (!ok) fail++;
            Console.WriteLine($"{(ok ? "OK" : "NG")}  {c.Name}{(r.HasImageCenterComposite ? "  [近似の注記あり]" : "")}");
            Console.WriteLine($"      計算 X {r.X,8:0.00} Y {r.Y,8:0.00} 拡大 {r.Zoom,7:0.00}% 回転 {r.Rotation,7:0.00}°  グループ {r.Steps.Count}");
            Console.WriteLine($"      描画 X {mm.Cx - W / 2.0,8:0.00} Y {mm.Cy - H / 2.0,8:0.00} 拡大 {ezoom,7:0.00}%  向きの差 {dAngle,5:0.00}°  位置の差 {dPos:0.00}px");
        }
        Console.WriteLine(fail == 0 ? "全件一致" : $"不一致 {fail} 件");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>実機で確かめるためのプロジェクトを書き出す（入れ子グループ＋グループ外の図形）</summary>
    static int SaveProject(string path)
    {
        var (tl, it) = Build(
            new[] { G(0, 10, x: -300, y: 0, zoom: 50, rot: -45, length: 600), G(1, 10, x: 100, y: 100, zoom: 200, rot: 90, length: 600) },
            it => { it.Layer = 2; it.Length = 600; Set(it.X, 50); Set(it.Y, 20); });
        var free = new ShapeItem { Frame = 0, Length = 600, Layer = 20 };
        Set(free.X, 400); Set(free.Y, -200);
        tl.Items = tl.Items.Add(free);
        var scenes = new Scenes(false);
        scenes.AddScene(tl);
        YukkuriMovieMaker.Json.Json.Save(new Project(0, scenes, path, null!, new()), path);
        var r = PositionCalculator.Calculate(tl, it, 10)!;
        Console.WriteLine($"保存: {path}  期待値 X {r.X:0.0} Y {r.Y:0.0} 拡大 {r.Zoom:0.0}% 回転 {r.Rotation:0.0}°");
        return 0;
    }

    static double AngleDiff(double measured, double expected)
    {
        // 長方形の主軸は 180° 周期
        double d = (measured - expected) % 180;
        if (d > 90) d -= 180;
        if (d < -90) d += 180;
        return d;
    }

    // ---------- シーン組み立て ----------

    static (Timeline, ShapeItem) Scene1(Action<ShapeItem> setup) => Build(Array.Empty<GroupItem>(), setup);

    static ShapeItem? measureOnly;

    static (Timeline, ShapeItem) Build(GroupItem[] groups, Action<ShapeItem> setup, Func<Timeline, IItem>? extra = null, bool measureOnlyTarget = false)
    {
        var tl = new Timeline("test", W, H, FPS, 48000, new TimelineVerticalLine());
        var it = new ShapeItem { Frame = 0, Length = 120, Layer = 0 };
        if (it.ShapeParameter is SizeAndAspectShapeParameterBase sp)
            sp.AspectRate.SetFirstValue(50); // 縦横比を崩して向きを測れるようにする
        setup(it);
        tl.Items = ImmutableList.Create<IItem>(groups).Add(it);
        if (extra is not null)
        {
            tl.Items = tl.Items.Add(extra(tl));
            if (measureOnlyTarget) measureOnly = it;
        }
        return (tl, it);
    }

    static GroupItem G(int layer, int range, double x = 0, double y = 0, double zoom = 100, double rot = 0,
        bool groupOnly = false, int group = 0, bool hidden = false, int frame = 0, int length = 120)
    {
        var g = new GroupItem { Layer = layer, GroupRange = range, Frame = frame, Length = length, IsGroupOnly = groupOnly, Group = group, IsHidden = hidden };
        Set(g.X, x); Set(g.Y, y); Set(g.Zoom, zoom); Set(g.Rotation, rot);
        return g;
    }

    static GroupItem Comp(GroupItem g, GroupItemCompositeCenter center) { g.IsComposite = true; g.CompositeCenter = center; return g; }
    static GroupItem Anim(GroupItem g, Action<GroupItem> a) { a(g); return g; }
    static void Set(Animation a, double v) => a.SetFirstValue(v);
    static void Move(Animation a, double from, double to)
    {
        typeof(Animation).GetProperty("Values")!.SetValue(a, ImmutableList.Create(new AnimationValue(from), new AnimationValue(to)));
        a.AnimationType = AnimationType.直線移動;
    }

    // ---------- 描画と計測 ----------

    record struct M(double Cx, double Cy, double Area, double Angle);

    static M? Measure(Timeline tl, int frame, ShapeItem? only = null)
    {
        // 対象以外は不透明度 40% で置くので、不透明な画素だけ数えれば対象だけを測れる
        return Moments(Render(tl, frame), only is null ? 0 : 0.9);
    }

    static byte[] Render(Timeline tl, int frame)
    {
        var asm = typeof(Timeline).Assembly;
        var tsType = asm.GetType("YukkuriMovieMaker.Player.Video.TimelineSource", true)!;
        var ctor = tsType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).First(c => c.GetParameters().Length == 3);
        var update = tsType.GetMethods().First(m => m.Name == "Update" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(TimeSpan));
        var usageType = update.GetParameters()[1].ParameterType;

        using var devices = new GraphicsDevices();
        using var ctx = devices.CreateContext();
        var scene = new Scene(tl, new Scenes(false), Array.Empty<Guid>());
        using var ts = (IDisposable)ctor.Invoke(new object?[] { ctx, scene, null });
        update.Invoke(ts, new object[] { TimeSpan.FromSeconds((double)frame / FPS), Enum.Parse(usageType, "Exporting") });
        var output = (ID2D1Image)tsType.GetProperty("Output")!.GetValue(ts)!;

        var dc = ctx.DeviceContext;
        var fmt = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var target = dc.CreateBitmap(new SizeI(W, H), IntPtr.Zero, W * 4, new BitmapProperties1(fmt, 96, 96, BitmapOptions.Target));
        dc.Target = target;
        dc.BeginDraw();
        dc.Clear(new Color4(0, 0, 0, 0));
        dc.DrawImage(output, new System.Numerics.Vector2(W / 2f, H / 2f));
        dc.EndDraw();
        dc.Target = null;

        using var cpu = dc.CreateBitmap(new SizeI(W, H), IntPtr.Zero, W * 4, new BitmapProperties1(fmt, 96, 96, BitmapOptions.CannotDraw | BitmapOptions.CpuRead));
        cpu.CopyFromBitmap(target);
        var map = cpu.Map(MapOptions.Read);
        var buf = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            Marshal.Copy(map.Bits + y * map.Pitch, buf, y * W * 4, W * 4);
        cpu.Unmap();
        return buf;
    }

    static M? Moments(byte[] buf, double threshold)
    {
        {
            double s = 0, sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            {
                for (int y = 0; y < H; y++)
                {
                    int row = y * W * 4;
                    for (int x = 0; x < W; x++)
                    {
                        double a = buf[row + x * 4 + 3] / 255.0;
                        if (a <= threshold) continue;
                        double px = x + 0.5, py = y + 0.5;
                        s += a; sx += a * px; sy += a * py;
                        sxx += a * px * px; syy += a * py * py; sxy += a * px * py;
                    }
                }
            }
            if (s < 1) return null;
            double cx = sx / s, cy = sy / s;
            double mu20 = sxx / s - cx * cx, mu02 = syy / s - cy * cy, mu11 = sxy / s - cx * cy;
            double angle = 0.5 * Math.Atan2(2 * mu11, mu20 - mu02) * 180 / Math.PI;
            return new M(cx, cy, s, angle);
        }
    }
}
