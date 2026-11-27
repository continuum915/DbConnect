using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Ticket 04: the sidebar's split height and collapsed sections survive a restart.</summary>
public sealed class SidebarStateTests
{
    [Fact]
    public void Changes_AreWrittenToSettingsAndSaved()
    {
        var settings = new AppSettings();
        var saves = 0;
        var state = new SidebarState();
        state.Attach(settings, () => saves++);

        state.LowerHeight = 320;
        state.SnippetsExpanded = false;

        Assert.Equal(320, settings.SidebarLowerHeight);
        Assert.False(settings.SidebarSnippetsExpanded);
        Assert.Equal(2, saves);
    }

    [Fact]
    public void Attach_RestoresTheSavedLayoutWithoutSavingIt()
    {
        var settings = new AppSettings { SidebarLowerHeight = 333, InformationPaneVisible = false, InformationPaneWidth = 300 };
        var saves = 0;
        var state = new SidebarState();

        state.Attach(settings, () => saves++);

        Assert.Equal(333, state.LowerHeight);
        Assert.False(state.InformationPaneVisible);
        Assert.Equal(300, state.InformationPaneWidth);
        Assert.True(state.SnippetsExpanded);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void Attach_IgnoresAHeightTooSmallToUse()
    {
        var state = new SidebarState();
        state.Attach(new AppSettings { SidebarLowerHeight = 5 }, () => { });

        Assert.Equal(SidebarState.DefaultLowerHeight, state.LowerHeight);
    }
}
