using System.ComponentModel;
using System.Windows.Controls;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace EffectivePosition;

/// <summary>
/// 入口。YMM4 はプラグインを起動時に 1 度生成するので、ここでアイテム編集への差し込みを始める。
/// あわせて「ツール」メニューに実質座標のウィンドウを出す（アイテム編集に差し込めなかった場合の代わり）。
/// </summary>
[PluginDetails(AuthorName = "arcana")]
public sealed class EffectivePositionPlugin : IToolPlugin
{
    public EffectivePositionPlugin()
    {
        ItemEditorInjector.Start();
    }

    public string Name => "実質座標表示";
    public Type ViewModelType => typeof(EffectivePositionToolViewModel);
    public Type ViewType => typeof(EffectivePositionToolView);
}

public sealed class EffectivePositionToolViewModel : IToolViewModel, ITimelineToolViewModel
{
    Timeline? timeline;

    public string? Title => "実質座標";

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested { add { } remove { } }

    public void SetTimelineToolInfo(TimelineToolInfo info) => timeline = info.Timeline;

    internal (Timeline?, IReadOnlyList<IItem>) GetSource()
    {
        var t = timeline;
        return (t, t?.SelectedItems ?? (IReadOnlyList<IItem>)Array.Empty<IItem>());
    }

    public ToolState SaveState() => new();
    public void LoadState(ToolState stateData) { }
}

public sealed class EffectivePositionToolView : UserControl
{
    public EffectivePositionToolView()
    {
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is EffectivePositionToolViewModel vm)
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new EffectivePositionPanel(vm.GetSource, hideWhenEmpty: false),
                };
        };
    }
}
