using System;
using System.Threading.Tasks;

namespace DbConnect.ViewModels;

internal static class LoadedSetting
{
    /// <summary>Applies a stored value, unless the setting changed while it loaded: that change is newer.</summary>
    public static async Task ApplyAsync(Func<int> current, Func<Task<int>> load, Action<int> apply)
    {
        var beforeLoad = current();
        var loaded = await load();
        if (current() == beforeLoad)
            apply(loaded);
    }
}
