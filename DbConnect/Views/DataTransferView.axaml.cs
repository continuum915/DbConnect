using System;
using Avalonia.Controls;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class DataTransferView : UserControl
{
    public DataTransferView()
    {
        InitializeComponent();
    }

    /// <summary>Dropping or emptying target tables is confirmed in the same dialog as the explorer's destructive actions.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not DataTransferViewModel vm) return;
        vm.ConfirmDestructive = async preview =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return false;
            var confirm = new ProductionConfirmViewModel(preview);
            await new ProductionConfirmDialog { DataContext = confirm }.ShowDialog(owner);
            return confirm.Confirmed;
        };
    }
}
