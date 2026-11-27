using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class UserManagementViewModel : ViewModelBase
{
    private const string MaskedPassword = "•••";
    private readonly UserManagementService _service = new();

    public override string Title => "User Management";

    [ObservableProperty] private DatabaseSession? _session;
    [ObservableProperty] private ObservableCollection<DatabaseUser> _users = new();
    [ObservableProperty] private DatabaseUser? _selectedUser;
    [ObservableProperty] private PermissionGrant? _selectedPermission;
    [ObservableProperty] private ObservableCollection<PermissionGrant> _selectedUserPermissions = new();
    [ObservableProperty] private string _statusMessage = string.Empty;

    public Func<string, string, Task<(string username, string password)?>>? ShowCreateUserDialog { get; set; }
    public Func<string, IEnumerable<string>, string?, Task<(string priv, string? obj)?>>? ShowGrantPrivilegeDialog { get; set; }
    public Func<string, string, Task<string?>>? ShowChangePasswordDialog { get; set; }
    public Func<string, Task<bool>>? ShowConfirmDialog { get; set; }

    public bool SupportsUserWrite => Session?.SupportsUserWrite == true;
    public bool HasSelectedUser => SelectedUser != null;
    public bool HasSelectedPermission => SelectedPermission != null;

    partial void OnSelectedUserChanged(DatabaseUser? value)
    {
        SelectedPermission = null;
        OnPropertyChanged(nameof(HasSelectedUser));
        OnPropertyChanged(nameof(HasSelectedPermission));
        SelectedUserPermissions.Clear();
        if (value != null && Session != null)
            _ = LoadPermissionsAsync(value.Name);
    }

    partial void OnSelectedPermissionChanged(PermissionGrant? value) =>
        OnPropertyChanged(nameof(HasSelectedPermission));

    partial void OnSessionChanged(DatabaseSession? value)
    {
        OnPropertyChanged(nameof(SupportsUserWrite));
        OnPropertyChanged(nameof(HasSelectedUser));
        OnPropertyChanged(nameof(HasSelectedPermission));
        if (value != null)
            _ = LoadUsers();
        else
        {
            Users.Clear();
            SelectedUserPermissions.Clear();
            SelectedUser = null;
            SelectedPermission = null;
        }
    }

    private async Task LoadPermissionsAsync(string userName)
    {
        if (Session == null)
            return;

        var grants = await _service.GetPermissionsAsync(Session, userName);
        SelectedPermission = null;
        SelectedUserPermissions.Clear();
        foreach (var grant in grants)
            SelectedUserPermissions.Add(grant);
    }

    private IEnumerable<string> GetPrivilegeOptions() => Session?.Capabilities.PrivilegeOptions ?? Array.Empty<string>();

    [RelayCommand]
    private async Task LoadUsers()
    {
        if (Session == null)
        {
            StatusMessage = "No session connected.";
            return;
        }

        try
        {
            var users = await _service.GetUsersAsync(Session);
            Users.Clear();
            foreach (var user in users)
                Users.Add(user);

            if (SelectedUser != null)
                SelectedUser = Users.FirstOrDefault(user => string.Equals(user.Name, SelectedUser.Name, StringComparison.OrdinalIgnoreCase));

            StatusMessage = $"Loaded {Users.Count} users.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
        if (Session == null)
            return;

        if (!SupportsUserWrite)
        {
            StatusMessage = "This database provider does not support user management.";
            return;
        }

        if (ShowCreateUserDialog == null)
            return;

        try
        {
            var dialogResult = await ShowCreateUserDialog("Create User", "Create");
            if (dialogResult is not { } result)
                return;

            var previewSql = Session.GenerateCreateUserSql(result.username, MaskedPassword);
            if (ShowConfirmDialog != null && !await ShowConfirmDialog(previewSql))
                return;

            await _service.CreateUserAsync(Session, result.username, result.password);
            await LoadUsers();
            SelectedUser = Users.FirstOrDefault(user =>
                string.Equals(user.Name, result.username, StringComparison.OrdinalIgnoreCase) ||
                user.Name.StartsWith(result.username + "@", StringComparison.OrdinalIgnoreCase));
            StatusMessage = $"User '{result.username}' created.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DropUserAsync()
    {
        if (Session == null || SelectedUser == null)
            return;

        if (!SupportsUserWrite)
        {
            StatusMessage = "This database provider does not support user management.";
            return;
        }

        try
        {
            var sql = Session.GenerateDropUserSql(SelectedUser.Name);
            if (ShowConfirmDialog != null && !await ShowConfirmDialog(sql))
                return;

            var userName = SelectedUser.Name;
            await _service.DropUserAsync(Session, userName);
            await LoadUsers();
            SelectedUser = null;
            StatusMessage = $"User '{userName}' dropped.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (Session == null || SelectedUser == null)
            return;

        if (!SupportsUserWrite)
        {
            StatusMessage = "This database provider does not support user management.";
            return;
        }

        if (ShowChangePasswordDialog == null)
            return;

        try
        {
            var newPassword = await ShowChangePasswordDialog(SelectedUser.Name, "Change Password");
            if (string.IsNullOrEmpty(newPassword))
                return;

            var previewSql = Session.GenerateChangePasswordSql(SelectedUser.Name, MaskedPassword);
            if (ShowConfirmDialog != null && !await ShowConfirmDialog(previewSql))
                return;

            await _service.ChangePasswordAsync(Session, SelectedUser.Name, newPassword);
            StatusMessage = $"Password updated for '{SelectedUser.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task GrantPrivilegeAsync()
    {
        if (Session == null || SelectedUser == null)
            return;

        if (!SupportsUserWrite)
        {
            StatusMessage = "This database provider does not support user management.";
            return;
        }

        if (ShowGrantPrivilegeDialog == null)
            return;

        try
        {
            var dialogResult = await ShowGrantPrivilegeDialog(SelectedUser.Name, GetPrivilegeOptions(), null);
            if (dialogResult is not { } result)
                return;

            var previewSql = Session.GenerateGrantSql(SelectedUser.Name, result.priv, result.obj);
            if (ShowConfirmDialog != null && !await ShowConfirmDialog(previewSql))
                return;

            await Session.GrantPrivilegeAsync(SelectedUser.Name, result.priv, result.obj);
            await LoadPermissionsAsync(SelectedUser.Name);
            StatusMessage = $"Granted {result.priv} to '{SelectedUser.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RevokePrivilegeAsync()
    {
        if (Session == null || SelectedUser == null || SelectedPermission == null)
            return;

        if (!SupportsUserWrite)
        {
            StatusMessage = "This database provider does not support user management.";
            return;
        }

        try
        {
            var objectName = string.IsNullOrWhiteSpace(SelectedPermission.ObjectName) ? null : SelectedPermission.ObjectName;
            var previewSql = Session.GenerateRevokeSql(SelectedUser.Name, SelectedPermission.Privilege, objectName);
            if (ShowConfirmDialog != null && !await ShowConfirmDialog(previewSql))
                return;

            await Session.RevokePrivilegeAsync(SelectedUser.Name, SelectedPermission.Privilege, objectName);
            await LoadPermissionsAsync(SelectedUser.Name);
            StatusMessage = $"Revoked {SelectedPermission.Privilege} from '{SelectedUser.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }
}
