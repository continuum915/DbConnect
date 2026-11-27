using System;
using Avalonia.Controls;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class DataCompareView : UserControl
{
    public DataCompareView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is DataCompareViewModel vm) ApplySyncDialogs.Wire(this, vm.Apply);
    }
}
