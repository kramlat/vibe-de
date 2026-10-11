using Vde.Shell.Notifications;
using Vibe.Registry;

namespace Vde.Shell.Notifications.Tests;

// Time that only moves when the test says so.
internal sealed class ManualClock : IScheduler
{
    private readonly List<(DateTime due, Action action, bool[] cancelled)> _timers = new();
    public DateTime Now { get; private set; } = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    public IDisposable After(TimeSpan delay, Action action)
    {
        var cancelled = new bool[1];
        _timers.Add((Now + delay, action, cancelled));
        return new Cancel(cancelled);
    }

    public void Advance(TimeSpan span)
    {
        Now += span;
        foreach (var timer in _timers.Where(t => t.due <= Now && !t.cancelled[0]).OrderBy(t => t.due).ToList())
        {
            timer.cancelled[0] = true;
            timer.action();
        }
    }

    public int Pending => _timers.Count(t => !t.cancelled[0]);

    private sealed class Cancel(bool[] flag) : IDisposable
    {
        public void Dispose() => flag[0] = true;
    }
}

public class CenterTests
{
    private static (NotificationCenter center, ManualClock clock, List<string> log) Make(NotificationSettings? settings = null)
    {
        var clock = new ManualClock();
        var center = new NotificationCenter(clock, () => clock.Now, settings);
        var log = new List<string>();
        center.Added += (_, n) => log.Add($"added {n.Id}");
        center.Updated += (_, n) => log.Add($"updated {n.Id}");
        center.Closed += (_, e) => log.Add($"closed {e.Id} {e.Reason}");
        center.ActionInvoked += (_, e) => log.Add($"action {e.Id} {e.Key}");
        return (center, clock, log);
    }

    private static NotificationRequest Req(string app = "App", string summary = "Hello", uint replaces = 0, int timeout = -1,
        Urgency urgency = Urgency.Normal, IReadOnlyList<NotificationAction>? actions = null, bool resident = false, bool transient = false)
        => new(app, summary, replaces, Actions: actions, ExpireTimeoutMs: timeout, Urgency: urgency, Resident: resident, Transient: transient);

    [Fact]
    public void Ids_start_at_one_and_never_repeat_among_the_active()
    {
        var (center, _, log) = Make();

        Assert.Equal(1u, center.Notify(Req()));
        Assert.Equal(2u, center.Notify(Req()));
        Assert.Equal(new[] { "added 1", "added 2" }, log);
        Assert.Equal(new uint[] { 1, 2 }, center.Active.Select(n => n.Id));
    }

    [Fact]
    public void Replacing_keeps_the_id_and_the_creation_time_and_restarts_the_expiry()
    {
        var (center, clock, log) = Make();
        var id = center.Notify(Req(summary: "first"));
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(id, center.Notify(Req(summary: "second", replaces: id)));

        var shown = Assert.Single(center.Active);
        Assert.Equal("second", shown.Summary);
        Assert.True(shown.Updated > shown.Created);
        Assert.Equal(new[] { "added 1", "updated 1" }, log);

        clock.Advance(TimeSpan.FromSeconds(3));   // 6 s after the first, 3 s after the replacement
        Assert.Single(center.Active);              // the 5 s default has not run out again yet
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(center.Active);
        Assert.Equal(0, clock.Pending);            // the first timer was cancelled by the replacement, the second has now fired
    }

    [Fact]
    public void Replacing_an_unknown_or_closed_id_creates_a_new_notification()
    {
        var (center, _, _) = Make();
        var id = center.Notify(Req());
        center.Close(id, CloseReason.ClosedByCall);

        var again = center.Notify(Req(replaces: id));

        Assert.NotEqual(id, again);
        Assert.NotEqual(0u, center.Notify(Req(replaces: 999)));
    }

    [Theory]
    [InlineData(-1, Urgency.Normal, 5000)]        // server default
    [InlineData(-1, Urgency.Low, 5000)]
    [InlineData(2000, Urgency.Normal, 2000)]
    [InlineData(10, Urgency.Normal, 100)]          // clamped up
    [InlineData(99_999_999, Urgency.Normal, 3_600_000)]   // clamped down to an hour
    public void Timeouts_follow_the_request_and_the_server_default(int requested, Urgency urgency, int expectedMs)
    {
        var (center, _, _) = Make();
        center.Notify(Req(timeout: requested, urgency: urgency));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), center.Active[0].Timeout);
    }

    [Fact]
    public void Zero_and_critical_notifications_never_expire_by_themselves()
    {
        var (center, clock, _) = Make();
        center.Notify(Req(timeout: 0));
        center.Notify(Req(urgency: Urgency.Critical));   // -1 with critical: until dealt with
        center.Notify(Req(urgency: Urgency.Critical, timeout: 1000));   // an explicit timeout is honoured

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(2, center.Active.Count);
        Assert.All(center.Active, n => Assert.Null(n.Timeout));
    }

    [Fact]
    public void Expiry_closes_with_the_expired_reason_and_records_history()
    {
        var (center, clock, log) = Make();
        var id = center.Notify(Req());

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Empty(center.Active);
        Assert.Equal($"closed {id} Expired", log[^1]);
        Assert.Equal(id, Assert.Single(center.History).Id);
    }

    [Fact]
    public void Closing_reports_the_reason_and_closing_twice_is_harmless()
    {
        var (center, _, log) = Make();
        var id = center.Notify(Req());

        Assert.True(center.Close(id, CloseReason.Dismissed));
        Assert.False(center.Close(id, CloseReason.Dismissed));
        Assert.False(center.Close(424242, CloseReason.ClosedByCall));

        Assert.Equal(new[] { "added 1", "closed 1 Dismissed" }, log);
    }

    [Fact]
    public void Transient_notifications_are_not_kept_in_the_history()
    {
        var (center, _, _) = Make();
        center.Close(center.Notify(Req(transient: true)), CloseReason.Dismissed);
        var kept = center.Notify(Req());
        center.Close(kept, CloseReason.Dismissed);

        Assert.Equal(kept, Assert.Single(center.History).Id);
    }

    [Fact]
    public void History_is_newest_first_and_capped_and_can_be_cleared()
    {
        var (center, _, _) = Make(new NotificationSettings { HistoryLimit = 3 });
        for (int i = 1; i <= 5; i++)
            center.Close(center.Notify(Req(summary: $"n{i}")), CloseReason.Dismissed);

        Assert.Equal(new[] { "n5", "n4", "n3" }, center.History.Select(n => n.Summary));

        center.ClearHistory();
        Assert.Empty(center.History);
    }

    [Fact]
    public void Actions_close_the_notification_unless_it_is_resident_and_unknown_actions_are_ignored()
    {
        var (center, _, log) = Make();
        var actions = new[] { new NotificationAction("default", "Open"), new NotificationAction("reply", "Reply") };
        var normal = center.Notify(Req(actions: actions));
        var resident = center.Notify(Req(actions: actions, resident: true));

        Assert.False(center.InvokeAction(normal, "nope"));
        Assert.False(center.InvokeAction(999, "default"));
        Assert.True(center.InvokeAction(normal, "reply"));
        Assert.True(center.InvokeAction(resident, "default"));

        Assert.Equal(new[] { "added 1", "added 2", "action 1 reply", "closed 1 Dismissed", "action 2 default" }, log);
        Assert.Equal(resident, Assert.Single(center.Active).Id);   // resident ones stay
    }

    [Fact]
    public void Actions_are_cleaned_up_duplicates_empty_keys_and_too_many()
    {
        var (center, _, _) = Make();
        var many = Enumerable.Range(0, 20).Select(i => new NotificationAction($"k{i}", $"label {i}")).ToList();
        many.Insert(0, new NotificationAction("", "no key"));
        many.Insert(1, new NotificationAction("k0", "duplicate of the next"));

        center.Notify(Req(actions: many));

        var actions = center.Active[0].Actions;
        Assert.Equal(8, actions.Count);
        Assert.DoesNotContain(actions, a => a.Key.Length == 0);
        Assert.Equal(actions.Count, actions.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void Do_not_disturb_silences_everything_but_critical_and_keeps_them_in_the_list()
    {
        var (center, clock, _) = Make(new NotificationSettings { DoNotDisturb = true });

        center.Notify(Req(summary: "chatty"));
        center.Notify(Req(summary: "alarm", urgency: Urgency.Critical));
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(center.Active.Single(n => n.Summary == "chatty").Silenced);
        Assert.False(center.Active.Single(n => n.Summary == "alarm").Silenced);
        Assert.Equal(2, center.Active.Count);   // silenced ones wait in the list instead of expiring unseen

        center.Settings = center.Settings with { DoNotDisturb = false };
        Assert.True(center.Active.Single(n => n.Summary == "chatty").Silenced);   // ones silenced earlier stay silenced
        center.Notify(Req(summary: "after"));
        Assert.False(center.Active.Single(n => n.Summary == "after").Silenced);
    }

    [Fact]
    public void One_noisy_application_cannot_push_out_the_others()
    {
        var (center, _, log) = Make(new NotificationSettings { MaxPerApp = 3, MaxActive = 100 });
        center.Notify(Req(app: "Quiet", summary: "mine"));
        for (int i = 0; i < 10; i++)
            center.Notify(Req(app: "Noisy", summary: $"spam {i}"));

        Assert.Equal(3, center.Active.Count(n => n.AppName == "Noisy"));
        Assert.Contains(center.Active, n => n.AppName == "Quiet");
        Assert.Equal(new[] { "spam 7", "spam 8", "spam 9" }, center.Active.Where(n => n.AppName == "Noisy").Select(n => n.Summary));   // the newest survive
        Assert.Contains(log, l => l.EndsWith("Undefined"));
    }

    [Fact]
    public void The_total_is_capped_and_critical_ones_are_the_last_to_go()
    {
        var (center, _, _) = Make(new NotificationSettings { MaxActive = 3, MaxPerApp = 100 });
        center.Notify(Req(app: "A", summary: "critical", urgency: Urgency.Critical));
        center.Notify(Req(app: "B", summary: "b1"));
        center.Notify(Req(app: "C", summary: "c1"));
        center.Notify(Req(app: "D", summary: "d1"));

        Assert.Equal(3, center.Active.Count);
        Assert.Contains(center.Active, n => n.Summary == "critical");
        Assert.DoesNotContain(center.Active, n => n.Summary == "b1");   // the oldest ordinary one made room
    }

    [Fact]
    public void Oversized_text_is_cut_not_trusted()
    {
        var (center, _, _) = Make();
        center.Notify(new NotificationRequest(new string('a', 500), new string('s', 1000), Body: new string('b', 10_000)));

        var n = center.Active[0];
        Assert.True(n.AppName.Length <= 129);
        Assert.True(n.Summary.Length <= NotificationMarkup.MaxSummary + 1);
        Assert.True(n.Body.Length <= NotificationMarkup.MaxBody + 1);
        Assert.EndsWith("…", n.Summary);
    }

    [Fact]
    public void Events_can_call_back_into_the_center_without_deadlocking()
    {
        var (center, _, _) = Make();
        center.Added += (_, n) => center.Close(n.Id, CloseReason.Dismissed);   // a UI that dismisses at once

        center.Notify(Req());

        Assert.Empty(center.Active);
        Assert.Single(center.History);
    }

    [Fact]
    public void Many_threads_notifying_and_closing_stay_consistent()
    {
        var center = new NotificationCenter(settings: new NotificationSettings { MaxActive = 1000, MaxPerApp = 1000 });
        var ids = new System.Collections.Concurrent.ConcurrentBag<uint>();

        Parallel.For(0, 8, worker =>
        {
            for (int i = 0; i < 200; i++)
            {
                var id = center.Notify(Req(app: $"app{worker}", timeout: 0));
                ids.Add(id);
                if (i % 2 == 0)
                    center.Close(id, CloseReason.Dismissed);
            }
        });

        Assert.Equal(1600, ids.Distinct().Count());   // no id handed out twice
        Assert.Equal(800, center.Active.Count);
    }
}

public class MarkupTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("<b>bold</b> and <i>italic</i>", "bold and italic")]
    [InlineData("line one<br/>line two<br>three", "line one\nline two\nthree")]
    [InlineData("Tom &amp; Jerry &lt;3", "Tom & Jerry <3")]
    [InlineData("<a href=\"https://example.org\">link</a>", "link")]
    [InlineData("<script>alert(1)</script>text", "alert(1)text")]
    public void Plain_text_has_no_markup(string body, string expected)
        => Assert.Equal(expected, NotificationMarkup.ToPlainText(body));

    [Fact]
    public void Only_safe_link_schemes_are_offered()
    {
        var links = NotificationMarkup.Links(
            "<a href=\"https://a.example\">1</a> <a href='http://b.example/x?y=1&amp;z=2'>2</a> <a href=\"mailto:me@example.org\">3</a> " +
            "<a href=\"file:///etc/passwd\">4</a> <a href=\"javascript:alert(1)\">5</a> <a href=\"vde-internal:thing\">6</a> <a href=\"/relative\">7</a>");

        Assert.Equal(new[] { "https://a.example", "http://b.example/x?y=1&z=2", "mailto:me@example.org" }, links);
    }

    [Fact]
    public void Cutting_text_never_splits_a_surrogate_pair()
    {
        var text = new string('a', 9) + "😀" + "tail";   // the emoji is two chars, at index 9-10

        var cut = NotificationMarkup.Limit(text, 10);

        Assert.EndsWith("…", cut);
        Assert.Equal(new string('a', 9) + "…", cut);   // the half emoji was dropped entirely
        Assert.Equal("short", NotificationMarkup.Limit("short", 10));
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_when_the_registry_has_nothing_and_bad_values_are_ignored()
    {
        var hive = new Hive("HKCU");
        Assert.Equal(new NotificationSettings(), NotificationSettingsStore.Load(hive));

        hive.SetValue(NotificationSettingsStore.KeyPath, "DoNotDisturb", RegistryValueKind.String, "yes please");
        hive.SetValue(NotificationSettingsStore.KeyPath, "DefaultTimeoutMs", RegistryValueKind.String, "soon");
        Assert.Equal(new NotificationSettings(), NotificationSettingsStore.Load(hive));
    }

    [Fact]
    public void Settings_round_trip_and_are_clamped_to_sane_ranges()
    {
        var hive = new Hive("HKCU");
        NotificationSettingsStore.Save(hive, new NotificationSettings { DoNotDisturb = true, DefaultTimeout = TimeSpan.FromSeconds(8), MaxActive = 50, MaxPerApp = 5, HistoryLimit = 10 });

        var loaded = NotificationSettingsStore.Load(hive);
        Assert.True(loaded.DoNotDisturb);
        Assert.Equal(TimeSpan.FromSeconds(8), loaded.DefaultTimeout);
        Assert.Equal((50, 5, 10), (loaded.MaxActive, loaded.MaxPerApp, loaded.HistoryLimit));

        hive.SetValue(NotificationSettingsStore.KeyPath, "MaxActive", RegistryValueKind.DWord, 0u);          // would disable notifications
        hive.SetValue(NotificationSettingsStore.KeyPath, "DefaultTimeoutMs", RegistryValueKind.DWord, 1u);   // would be unreadably short
        loaded = NotificationSettingsStore.Load(hive);
        Assert.Equal(1, loaded.MaxActive);
        Assert.Equal(TimeSpan.FromMilliseconds(500), loaded.DefaultTimeout);
    }

    [Fact]
    public void The_center_follows_the_registry_live_and_stops_when_unbound()
    {
        var hive = new Hive("HKCU");
        var center = new NotificationCenter();

        using (center.BindTo(hive))
        {
            Assert.False(center.Settings.DoNotDisturb);
            hive.SetValue(NotificationSettingsStore.KeyPath, "DoNotDisturb", RegistryValueKind.DWord, 1u);   // Settings, a script, anything
            Assert.True(center.Settings.DoNotDisturb);
            center.Notify(new NotificationRequest("App", "quiet please"));
            Assert.True(center.Active[0].Silenced);
        }

        hive.SetValue(NotificationSettingsStore.KeyPath, "DoNotDisturb", RegistryValueKind.DWord, 0u);
        Assert.True(center.Settings.DoNotDisturb);   // unbound: no longer follows
    }
}
