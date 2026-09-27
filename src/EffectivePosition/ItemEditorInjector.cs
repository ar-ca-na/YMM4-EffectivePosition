using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.ViewModels;
using YukkuriMovieMaker.Views;

namespace EffectivePosition;

/// <summary>
/// YMM4 の「アイテム編集」パネル（ItemEditorView）の上端に実質座標の帯を差し込む。
/// ItemEditorView の中身は Grid 1 枚（「未選択」表示とプロパティ一覧が重なっている）なので、
/// 行を 1 つ足して既存の子を 1 行下げ、空いた先頭行に帯を置く。
/// YMM4 の非公開の構造に依存するため、想定と違えば何もせず諦める（ツールウィンドウは使える）。
/// </summary>
internal static class ItemEditorInjector
{
    static bool started;
    static FieldInfo? timelineField;

    public static void Start()
    {
        if (started) return;
        started = true;
        try
        {
            timelineField = typeof(ItemEditorViewModel)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(f => f.FieldType == typeof(Timeline));

            // 以後に読み込まれる（ドッキングの付け替えで作り直される場合も含む）ItemEditorView を拾う
            EventManager.RegisterClassHandler(typeof(ItemEditorView), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => { if (s is ItemEditorView v) TryInject(v); }));

            // 既に表示済みのものを拾う
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ScanAllWindows);
        }
        catch (Exception ex)
        {
            Log("起動に失敗", ex);
        }
    }

    static void ScanAllWindows()
    {
        try
        {
            if (Application.Current is null) return;
            foreach (Window w in Application.Current.Windows)
                foreach (var v in Descendants<ItemEditorView>(w))
                    TryInject(v);
        }
        catch (Exception ex)
        {
            Log("ウィンドウの走査に失敗", ex);
        }
    }

    internal static bool TryInject(ItemEditorView view)
    {
        try
        {
            if (view.Content is not Grid root) return false;
            if (root.Children.OfType<EffectivePositionPanel>().Any()) return true;

            if (root.RowDefinitions.Count == 0)
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            foreach (UIElement child in root.Children)
                Grid.SetRow(child, Grid.GetRow(child) + 1);
            root.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });

            var panel = new EffectivePositionPanel(() => GetSource(view), hideWhenEmpty: true);
            Grid.SetRow(panel, 0);
            if (root.ColumnDefinitions.Count > 1)
                Grid.SetColumnSpan(panel, root.ColumnDefinitions.Count);
            root.Children.Add(panel);
            return true;
        }
        catch (Exception ex)
        {
            Log("アイテム編集への差し込みに失敗", ex);
            return false;
        }
    }

    static (Timeline?, IReadOnlyList<IItem>) GetSource(ItemEditorView view)
    {
        if (view.DataContext is not ItemEditorViewModel vm)
            return (null, Array.Empty<IItem>());
        var timeline = timelineField?.GetValue(vm) as Timeline;
        return (timeline, vm.Items.Value);
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is T t) { yield return t; continue; }
            int n = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++)
                stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    static void Log(string message, Exception ex)
    {
        try
        {
            YukkuriMovieMaker.Commons.Log.Default.Write("[実質座標表示] " + message, ex);
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
