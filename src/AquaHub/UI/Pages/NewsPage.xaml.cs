using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Analysis;
using AquaHub.Core.Models;
using AquaHub.Core.Util;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public partial class NewsPage : UserControl, IPage
{
    private readonly UiThrottle _refresh;
    private readonly ObservableCollection<StoryVM> _items = new();
    private readonly StoryVMCache _rows = new();
    private List<StoryVM>? _pending;
    private ScrollViewer? _scroller;
    private string _filter = "all";
    private string? _focus;

    private static readonly (string Id, string Label)[] FilterDefs =
    {
        ("all", "All"), ("local", "Local"), ("world", "World"), ("europe", "Europe"), ("business", "Business"), ("tech", "Tech"), ("saved", "Saved"),
    };

    public NewsPage()
    {
        InitializeComponent();
        _refresh = new UiThrottle(() => Refresh(), 200);
        List.ItemsSource = _items;
        foreach (var (id, label) in FilterDefs)
        {
            var chip = new RadioButton { Content = label, Tag = id, Style = (Style)FindResource("Chip"), GroupName = "newsFilter", IsChecked = id == "all" };
            chip.Checked += (_, _) => { _filter = id; Refresh(reset: true); };
            Filters.Children.Add(chip);
        }
        List.Loaded += (_, _) =>
        {
            if (_scroller is not null) return;
            _scroller = FindScroller(List);
            if (_scroller is not null)
                _scroller.ScrollChanged += (_, _) => { if (_pending is not null && AtTop) ShowPending(); };
        };
    }

    private bool AtTop => _scroller is null || _scroller.VerticalOffset < 40;

    public void OnNavigatedTo(string? arg)
    {
        Hub.State.Changed -= OnChanged;
        Hub.State.Changed -= OnChanged; // navigating to the page already shown must not subscribe twice
        Hub.State.Changed += OnChanged;
        _focus = arg;
        Refresh(reset: arg is not null);
    }

    public void OnNavigatedFrom()
    {
        Hub.State.Changed -= OnChanged;
        _refresh.Stop();
    }

    private void OnChanged(string topic)
    {
        if (topic == Topics.News) _refresh.Request();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Refresh(reset: true);

    /// <param name="reset">The reader asked for a different list (filter, search, a specific story): rebuild and go to the top.</param>
    private void Refresh(bool reset = false)
    {
        IEnumerable<StoryCluster> stories = Hub.State.Stories;
        if (_filter == "saved")
        {
            var saved = Hub.Core.Db.GetSaved().Select(i => i.Id).ToHashSet();
            stories = stories.Where(c => c.Items.Any(i => saved.Contains(i.Id)));
        }
        else if (_filter == "local") stories = stories.Where(c => c.IsLocal);
        else if (_filter != "all") stories = stories.Where(c => c.Category == _filter);

        var q = Search.Text.Trim();
        if (q.Length > 1)
        {
            var folded = TextTools.Fold(q).ToLowerInvariant();
            stories = stories.Where(c => TextTools.Fold(c.Title + " " + c.Summary?.Tldr + " " + string.Join(" ", c.SourceNames)).ToLowerInvariant().Contains(folded));
        }

        // Rows are reused by story id and updated in place, so summaries arriving never collapse or move what you're reading.
        var desired = _rows.Map(stories.Take(120));
        if (_focus is not null && desired.FirstOrDefault(s => s.Id == _focus) is { } target)
        {
            desired.Remove(target);
            desired.Insert(0, target);
            target.Expanded = true;
            _focus = null;
            reset = true;
        }

        if (desired.Select(v => v.Id).SequenceEqual(_items.Select(v => v.Id)))
        {
            HidePending();
        }
        else if (reset || _items.Count == 0 || AtTop)
        {
            Apply(desired);
            HidePending();
            if (reset) _scroller?.ScrollToTop();
        }
        else
        {
            // The reader is further down: keep their place and offer the new order instead of jumping.
            _pending = desired;
            var fresh = desired.Count(v => !_items.Contains(v));
            NewStoriesText.Text = fresh > 0 ? $"{Plural.Of(fresh, "new story", "new stories")} · show" : "Updated order · show";
            NewStories.Visibility = Visibility.Visible;
        }

        Empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Hub.State.Stories.Count == 0 ? "Your News Scout is gathering headlines…" : "No stories match this filter.";

        var all = Hub.State.Stories;
        var sources = all.SelectMany(c => c.Items).Select(i => i.SourceName).Distinct().Count();
        var ai = all.Count(c => c.Summary?.IsAi == true);
        Subtitle.Text = all.Count == 0 ? "" :
            $"{Plural.Of(all.Count, "story", "stories")} from {Plural.Of(sources, "outlet")}  ·  {all.Count(c => c.SourceCount > 1)} cross-checked  ·  {ai} summarised on-device";
    }

    private void Apply(List<StoryVM> desired)
    {
        var keep = desired.ToHashSet();
        for (var i = _items.Count - 1; i >= 0; i--)
            if (!keep.Contains(_items[i])) _items.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _items.Count && ReferenceEquals(_items[i], desired[i])) continue;
            var at = _items.IndexOf(desired[i]);
            if (at >= 0) _items.Move(at, i);
            else _items.Insert(i, desired[i]);
        }
    }

    private void ShowPending()
    {
        if (_pending is { } pending) Apply(pending);
        HidePending();
        Empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HidePending()
    {
        _pending = null;
        NewStories.Visibility = Visibility.Collapsed;
    }

    private void OnShowNew(object sender, RoutedEventArgs e)
    {
        ShowPending();
        _scroller?.ScrollToTop();
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }
}
