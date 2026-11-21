using System.Windows.Controls;
using System.Windows;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public partial class TodayPage : UserControl, IPage
{
    private void OnReadAloudSettings(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("settings", "ask:readaloud");
    private readonly TodayVM _vm = new();

    public TodayPage()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    public void OnNavigatedTo(string? arg)
    {
        _vm.Attach();
        _vm.UpdateLaunchpad();
    }

    public void OnNavigatedFrom() => _vm.Detach();
    internal void FocusDeckButton() => OpenDeckButton.Focus();
    private void OnShowDeck(object sender, RoutedEventArgs e) => Hub.Windows.Main?.ReopenDeck();
    private void OnAddReminder(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("upcoming", "new-reminder");
}
