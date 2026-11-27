using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class UserManagementView : UserControl
{
    public UserManagementView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not UserManagementViewModel vm)
            return;

        vm.ShowCreateUserDialog = async (title, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window == null)
                return null;

            var dialog = new CreateUserDialog((username, password) => vm.Session?.GenerateCreateUserSql(username, password) ?? string.Empty)
            {
                Title = title
            };
            var result = await dialog.ShowDialog<CreateUserDialog?>(window);
            return result is { Username: not null, Password: not null }
                ? (result.Username, result.Password)
                : null;
        };

        vm.ShowGrantPrivilegeDialog = async (username, privileges, _) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window == null)
                return null;

            var availablePrivileges = privileges?.ToList() ?? new List<string>();
            if (availablePrivileges.Count == 0)
                return null;

            var dialog = new GrantPrivilegeDialog(
                username,
                availablePrivileges,
                (privilege, objectName) => vm.Session?.GenerateGrantSql(username, privilege, objectName) ?? string.Empty);
            var result = await dialog.ShowDialog<GrantPrivilegeDialog?>(window);
            return result is { Privilege: not null }
                ? (result.Privilege, result.ObjectName)
                : null;
        };

        vm.ShowChangePasswordDialog = async (username, title) =>
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window == null)
                return null;

            var dialog = new ChangePasswordDialog(
                username,
                (user, password) => vm.Session?.GenerateChangePasswordSql(user, password) ?? string.Empty)
            {
                Title = title
            };
            var result = await dialog.ShowDialog<ChangePasswordDialog?>(window);
            return result?.Password;
        };

        vm.ShowConfirmDialog = async sql => await ShowSqlPreviewAsync(sql);
    }

    private async Task<bool> ShowSqlPreviewAsync(string sql)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null)
            return false;

        var dialog = new CommitPreviewDialog(new CommitPreviewRequest(sql, "Review generated SQL before execution."));
        return await dialog.ShowDialog<bool?>(window) == true;
    }
}
