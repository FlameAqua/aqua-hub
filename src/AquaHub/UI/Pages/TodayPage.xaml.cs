using System.Windows.Controls;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public partial class TodayPage : UserControl, IPage
{
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
}
