using Voxa.Diagnostics;
using Voxa.Frames;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VDX-008 Studio surfacing: the Talk viewbar badge tracks in-flight delegated tasks from the hub's
/// background-task events, and every event lands in the event log — including background-result
/// turns gated to silence, which are otherwise invisible. Pure view-model tests.
/// </summary>
public class TalkBackgroundTaskTests
{
    private static TalkViewModel Vm() => new(TestSupport.Services());

    [Fact]
    public void Badge_Tracks_Started_And_Completed_Tasks()
    {
        var vm = Vm();
        Assert.False(vm.ShowBackgroundTasksBadge);

        vm.EnqueueForTest(new BackgroundTaskStartedEvent("t1", "find flights to Lisbon"));
        vm.EnqueueForTest(new BackgroundTaskStartedEvent("t2", "check the weather"));
        vm.DrainPending();

        Assert.Equal(2, vm.ActiveBackgroundTasks);
        Assert.True(vm.ShowBackgroundTasksBadge);
        Assert.Equal("2 background tasks", vm.BackgroundTasksLabel);

        vm.EnqueueForTest(new BackgroundTaskCompletedEvent("t1", IsError: false, ElapsedMs: 5300));
        vm.DrainPending();

        Assert.Equal(1, vm.ActiveBackgroundTasks);
        Assert.Equal("1 background task", vm.BackgroundTasksLabel);

        vm.EnqueueForTest(new BackgroundTaskCompletedEvent("t2", IsError: true, ElapsedMs: 60000));
        vm.DrainPending();

        Assert.Equal(0, vm.ActiveBackgroundTasks);
        Assert.False(vm.ShowBackgroundTasksBadge);
    }

    [Fact]
    public void Count_Never_Goes_Negative_On_Unmatched_Completions()
    {
        // A subscriber attaching mid-session can see a completion whose start it missed.
        var vm = Vm();
        vm.EnqueueForTest(new BackgroundTaskCompletedEvent("t?", IsError: false, ElapsedMs: 10));
        vm.DrainPending();

        Assert.Equal(0, vm.ActiveBackgroundTasks);
        Assert.False(vm.ShowBackgroundTasksBadge);
    }

    [Fact]
    public void All_Background_Events_Reach_The_Event_Log()
    {
        var vm = Vm();
        vm.EnqueueForTest(new BackgroundTaskStartedEvent("t1", new string('g', 80))); // long goal truncates
        vm.EnqueueForTest(new BackgroundTaskRejectedEvent("t2"));
        vm.EnqueueForTest(new BackgroundTaskDroppedEvent("t3"));
        vm.EnqueueForTest(new BackgroundTaskCompletedEvent("t1", IsError: true, ElapsedMs: 1200));
        vm.DrainPending();

        Assert.Contains(vm.EventLog, line => line.Contains("BackgroundTaskStarted") && line.Contains("…"));
        Assert.Contains(vm.EventLog, line => line.Contains("rejected — request queue full"));
        Assert.Contains(vm.EventLog, line => line.Contains("held result dropped"));
        Assert.Contains(vm.EventLog, line => line.Contains("FAILED after 1200 ms"));
    }

    [Fact]
    public void Silent_BackgroundResult_Turns_Are_Visible_In_The_Log_Only()
    {
        var vm = Vm();
        vm.EnqueueForTest(new LlmTurnEvent("turn-1", Started: true, TurnTrigger.BackgroundResult));
        vm.EnqueueForTest(new LlmTurnEvent("turn-1", Started: false, TurnTrigger.BackgroundResult));
        // User-turn edges stay OUT of the log — they'd double every turn's noise.
        vm.EnqueueForTest(new LlmTurnEvent("turn-2", Started: true, TurnTrigger.UserUtterance));
        vm.DrainPending();

        Assert.Contains(vm.EventLog, line => line.Contains("background-result turn started"));
        Assert.Contains(vm.EventLog, line => line.Contains("background-result turn ended"));
        Assert.DoesNotContain(vm.EventLog, line => line.Contains("turn-2"));
        Assert.Empty(vm.Transcript); // no bubbles for a silent turn
    }
}
