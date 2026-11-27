using Avalonia.Controls;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class MaskingRulesView : UserControl
{
    public MaskingRulesView()
    {
        InitializeComponent();
    }

    private void TemplateCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is string templateName
            && DataContext is MaskingRulesViewModel vm)
        {
            vm.LoadTemplateCommand.Execute(templateName);
            combo.SelectedIndex = -1;
        }
    }
}
