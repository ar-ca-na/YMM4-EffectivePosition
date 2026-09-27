using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace EffectivePosition;

/// <summary>
/// 選択中アイテムの実質座標を表示する帯。アイテム編集パネルとツールウィンドウの両方で使う。
/// 表示中だけ一定間隔で計算し直す（キーフレームやグループ制御の値の変化を全部購読するより確実で軽い）。
/// </summary>
public sealed class EffectivePositionPanel : Border
{
    readonly Func<(Timeline? Timeline, IReadOnlyList<IItem> Items)> source;
    readonly DispatcherTimer timer;
    readonly TextBlock xText = Value(), yText = Value(), zText = Value(), zoomText = Value(), rotText = Value();
    readonly TextBlock ownText = Note(), groupsText = Note(), notesText = Note();
    readonly WrapPanel body;
    readonly TextBlock emptyText;
    readonly StackPanel stack;
    string? lastKey;

    public EffectivePositionPanel(Func<(Timeline? Timeline, IReadOnlyList<IItem> Items)> source, bool hideWhenEmpty)
    {
        this.source = source;
        HideWhenEmpty = hideWhenEmpty;
        Padding = new Thickness(8, 4, 8, 5);
        BorderThickness = new Thickness(0, 0, 0, 1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

        stack = new StackPanel();
        var title = new TextBlock { Text = "実質座標（グループ制御込み）", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) };
        stack.Children.Add(title);

        // 幅が狭いパネルでも切れないよう、項目ごとに折り返す
        body = new WrapPanel();
        AddCell("X", xText);
        AddCell("Y", yText);
        AddCell("Z", zText);
        AddCell("拡大率", zoomText);
        AddCell("回転", rotText);
        stack.Children.Add(body);
        stack.Children.Add(ownText);
        stack.Children.Add(groupsText);
        stack.Children.Add(notesText);

        emptyText = Note();
        emptyText.Text = "座標を持つアイテムを 1 つ選択すると表示します";
        stack.Children.Add(emptyText);
        Child = stack;

        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); timer.Start(); }
            else timer.Stop();
        };
        Unloaded += (_, _) => timer.Stop();
    }

    public bool HideWhenEmpty { get; }

    void AddCell(string label, TextBlock value)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
        cell.Children.Add(new TextBlock { Text = label, Opacity = 0.7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        cell.Children.Add(value);
        body.Children.Add(cell);
    }

    static TextBlock Value() => new() { FontFamily = new FontFamily("Consolas, Yu Gothic UI"), VerticalAlignment = VerticalAlignment.Center };
    static TextBlock Note() => new() { Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

    public void Refresh()
    {
        EffectivePositionResult? r = null;
        try
        {
            var (timeline, items) = source();
            if (timeline is not null && items.Count == 1)
                r = PositionCalculator.Calculate(timeline, items[0], timeline.CurrentFrame);
        }
        catch (Exception ex)
        {
            // 計算の失敗で YMM4 を落とさない
            System.Diagnostics.Debug.WriteLine(ex);
            r = null;
        }
        Show(r);
    }

    void Show(EffectivePositionResult? r)
    {
        string key = r is null ? "" : Key(r);
        if (key == lastKey) return;
        lastKey = key;

        // 自分自身を Collapsed にすると IsVisible が落ちてタイマーが止まるので、中身だけ畳む
        bool collapse = r is null && HideWhenEmpty;
        stack.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        Padding = collapse ? new Thickness(0) : new Thickness(8, 4, 8, 5);
        BorderThickness = collapse ? new Thickness(0) : new Thickness(0, 0, 0, 1);

        if (r is null)
        {
            body.Visibility = ownText.Visibility = groupsText.Visibility = notesText.Visibility = Visibility.Collapsed;
            emptyText.Visibility = Visibility.Visible;
            return;
        }
        body.Visibility = groupsText.Visibility = Visibility.Visible;
        emptyText.Visibility = Visibility.Collapsed;

        xText.Text = $"{r.X:0.0}px";
        yText.Text = $"{r.Y:0.0}px";
        zText.Text = $"{r.Z:0.0}px";
        zoomText.Text = $"{r.Zoom:0.0}%";
        rotText.Text = $"{r.Rotation:0.0}°";
        ownText.Text = $"アイテム単体: X {r.OwnX:0.0} / Y {r.OwnY:0.0} / Z {r.OwnZ:0.0} / 拡大率 {r.OwnZoom:0.0}% / 回転 {r.OwnRotation:0.0}°";
        ownText.Visibility = r.Steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        groupsText.Text = r.Steps.Count == 0
            ? "グループ制御なし（アイテムの座標のまま）"
            : "適用中: " + string.Join(" → ", r.Steps.Select(s => s.LayerLabel + (s.IsComposite ? "(画像化)" : "")));
        groupsText.ToolTip = r.Steps.Count == 0 ? null : string.Join("\n", r.Steps.Select(s =>
            $"{s.LayerLabel} {(s.IsComposite ? "画像化グループ" : "グループ制御")}: X {s.X:0.#} / Y {s.Y:0.#} / Z {s.Z:0.#} / 拡大率 {s.Zoom:0.#}% / 回転 {s.Rotation:0.#}°"));

        var notes = new List<string>();
        if (r.IsOutsideItem) notes.Add("再生位置がアイテムの外なので、アイテムの端のフレームで計算");
        if (r.HasImageCenterComposite) notes.Add("「画像中心」の画像化グループにほかの中身もあるため近似値");
        if (r.HasVideoEffects) notes.Add("映像エフェクトによる移動は含まない");
        notesText.Text = string.Join("\n", notes.Select(n => "※" + n));
        notesText.Visibility = notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static string Key(EffectivePositionResult r) =>
        FormattableString.Invariant($"{r.X:0.00}|{r.Y:0.00}|{r.Z:0.00}|{r.Zoom:0.00}|{r.Rotation:0.00}|{r.OwnX:0.00}|{r.OwnY:0.00}|{r.OwnZ:0.00}|{r.OwnZoom:0.00}|{r.OwnRotation:0.00}|{r.IsOutsideItem}|{r.HasVideoEffects}|{r.HasImageCenterComposite}|")
        + string.Join(",", r.Steps.Select(s => FormattableString.Invariant($"{s.Group.Layer}:{s.X:0.0}:{s.Y:0.0}:{s.Z:0.0}:{s.Zoom:0.0}:{s.Rotation:0.0}:{s.IsComposite}")));
}
