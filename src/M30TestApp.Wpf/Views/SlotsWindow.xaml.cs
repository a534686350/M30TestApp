using System;
using System.Windows;
using M30TestApp.Wpf.ViewModels;

namespace M30TestApp.Wpf.Views;

public partial class SlotsWindow : Window
{
    public SlotsWindow(ConfigViewModel viewModel)
    {
        InitializeComponent();
        SlotsEditor.DataContext = viewModel;
        viewModel.SelectedSection = "工位";
        SlotsEditor.EnableSlotsOnlyMode();
    }
}
