using System.Windows;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

public partial class RunSetupWindow : Window
{
    public RunSetupWindow(RunSetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += (_, _) =>
        {
            DialogResult = vm.DialogResult;
            Close();
        };
    }
}
