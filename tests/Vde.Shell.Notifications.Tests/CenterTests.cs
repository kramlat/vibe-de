using Vde.Shell.Notifications;

namespace Vde.Shell.Notifications.Tests;

public class CenterTests
{
    private static (NotificationService service, ManualClock clock, NotificationCenterModel model) Make()
    {
        var clock = new ManualClock();
        var service = new NotificationService(clock, () => clock.Now);
        return (service, clock, new NotificationCenterModel(service));
    }

    private static uint Send(NotificationService s, ManualClock c, string app, string summary, int timeoutMs = 0)
    {
        c.Advance(TimeSpan.FromMinutes(1));
        return s.Notify(new NotificationRequest(app, summary, ExpireTimeoutMs: timeoutMs));
    }

    [Fact]
    public void Groups_are_per_app_with_the_newest_group_first_and_each_group_newest_first()
    {
        var (s, c, m) = Make();
        Send(s, c, "WhatsApp", "one");
        Send(s, c, "Teams", "two");
        Send(s, c, "WhatsApp", "three");
        var groups = m.Groups();
        Assert.Equal(["WhatsApp", "Teams"], groups.Select(g => g.AppName));
        Assert.Equal(["three", "one"], groups[0].Items.Select(n => n.Summary));
        Assert.Equal(1, groups[0].Hidden);            // "+1 notification"
        Assert.Single(groups[0].Shown);
    }

    [Fact]
    public void Expanding_a_group_shows_all_of_it_and_a_closed_banner_stays_in_the_centre_once()
    {
        var (s, c, m) = Make();
        var first = Send(s, c, "App", "a", timeoutMs: 1000);
        Send(s, c, "App", "b");
        c.Advance(TimeSpan.FromSeconds(5));            // a's banner expires: it moves to the kept list but is still shown, once
        Assert.Equal(2, m.Count);
        m.ToggleExpanded("App");
        var g = Assert.Single(m.Groups());
        Assert.Equal(2, g.Shown.Count);
        Assert.Equal(0, g.Hidden);
        Assert.Contains(g.Items, n => n.Id == first);
    }

    [Fact]
    public void Dismiss_removes_from_screen_and_history_and_ClearAll_empties_everything()
    {
        var (s, c, m) = Make();
        var a = Send(s, c, "A", "x");
        Send(s, c, "B", "y");
        int changes = 0;
        m.Changed += (_, _) => changes++;
        m.Dismiss(a);
        Assert.Equal(["B"], m.Groups().Select(g => g.AppName));
        Assert.True(changes > 0);
        m.ClearAll();
        Assert.Equal(0, m.Count);
        Assert.Empty(s.Active);
        Assert.Empty(s.History);
    }

    [Fact]
    public void Do_not_disturb_is_the_services_setting_and_silenced_notifications_still_show_in_the_centre()
    {
        var (s, c, m) = Make();
        m.DoNotDisturb = true;
        Assert.True(s.Settings.DoNotDisturb);
        Send(s, c, "Quiet", "shh");
        Assert.Equal(1, m.Count);
        Assert.True(m.Groups()[0].Newest.Silenced);
    }

    [Fact]
    public void Dismiss_group_removes_every_notification_of_the_app()
    {
        var (s, c, m) = Make();
        Send(s, c, "A", "1"); Send(s, c, "A", "2"); Send(s, c, "B", "3");
        m.DismissGroup("A");
        Assert.Equal(["B"], m.Groups().Select(g => g.AppName));
    }
}
