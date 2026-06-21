using Avalonia.Controls;
using Avalonia.Threading;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Views;

public partial class TalkView : UserControl
{
    // ≤30 fps render-side drain (VST-001 §3.3): the hub reader buffers events on background
    // threads; this timer applies them to bindable state on the UI thread in coalesced batches.
    private readonly DispatcherTimer _drainTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(33),
    };

    public TalkView()
    {
        InitializeComponent();
        _drainTimer.Tick += (_, _) =>
        {
            if (DataContext is TalkViewModel vm)
            {
                int before = vm.Transcript.Count;
                vm.DrainPending();
                if (vm.Transcript.Count != before)
                    TranscriptScroll.ScrollToEnd();
            }
        };
        AttachedToVisualTree += (_, _) => _drainTimer.Start();
        DetachedFromVisualTree += (_, _) => _drainTimer.Stop();
    }
}
