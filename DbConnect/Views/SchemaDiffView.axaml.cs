using System;
using Avalonia.Controls;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class SchemaDiffView : UserControl
{
    public SchemaDiffView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SchemaDiffViewModel vm) ApplySyncDialogs.Wire(this, vm.Apply);
    }
}
