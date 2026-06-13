using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Voxa.Studio.Services;
using Voxa.Studio.Theme;

namespace Voxa.Studio.Views;

/// <summary>
/// The launch splash (VST-002 §4). The window owns only presentation: the wordmark settles
/// from wide letter-spacing, the microcopy ticks the stage names <see cref="ReportStage"/>
/// receives from the real boot, and the bottom bar's width is the actual stage index — honest
/// progress, not a timer. A click skips the intro; App.axaml.cs closes the window the moment
/// init completes, even mid-animation.
/// </summary>
public partial class SplashWindow : Window
{
    private const double WideLetterSpacing = 16;   // settles to the wordmark's 0.42em (9.7px @ 23px)
    private const double FinalLetterSpacing = 9.7;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DispatcherTimer? _intro;
    private int _stageIndex;

    /// <summary>
    /// Raised when the user clicks to skip the intro. The host (App) uses this to bypass the
    /// minimum on-screen hold — a deliberate skip should get the user into the shell at once.
    /// </summary>
    public event Action? SkipRequested;

    public SplashWindow()
    {
        InitializeComponent();

        if (MotionSettings.ReduceMotion)
        {
            ProgressBar.Transitions = null; // bar snaps to the real stage, no tween
            ShowEndState();
        }
        else
        {
            _intro = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _intro.Tick += (_, _) => ApplyIntro(_clock.Elapsed.TotalSeconds);
            _intro.Start();
        }

        PointerPressed += (_, _) =>
        {
            SkipIntro();
            SkipRequested?.Invoke();
        };
    }

    /// <summary>Wordmark fades in 1.25–1.95 s while the tracking settles; microcopy follows at 1.5 s.</summary>
    private void ApplyIntro(double t)
    {
        double wordProgress = Motion.EaseOut.Ease(Math.Clamp((t - 1.25) / 0.7, 0, 1));
        Wordmark.Opacity = wordProgress;
        double spacing = WideLetterSpacing - (WideLetterSpacing - FinalLetterSpacing) * wordProgress;
        WordV.LetterSpacing = spacing;
        WordS.LetterSpacing = spacing;

        Meta.Opacity = Motion.EaseOut.Ease(Math.Clamp((t - 1.5) / 0.6, 0, 1));

        if (t > 2.2) _intro?.Stop();
    }

    private void SkipIntro()
    {
        _intro?.Stop();
        Mark.CompleteIntro();
        ShowEndState();
    }

    private void ShowEndState()
    {
        Wordmark.Opacity = 1;
        Meta.Opacity = 1;
        WordV.LetterSpacing = FinalLetterSpacing;
        WordS.LetterSpacing = FinalLetterSpacing;
    }

    /// <summary>Called on the UI thread for each real boot stage; drives microcopy + progress.</summary>
    public void ReportStage(string stage)
    {
        _stageIndex++;
        StageText.Text = stage == "ready" ? "ready" : $"{stage}…";
        ProgressBar.Width = Width * Math.Min(_stageIndex, StartupCoordinator.Stages.Count)
                            / StartupCoordinator.Stages.Count;
    }

    /// <summary>Boot failed — show the message where the microcopy was and stop pretending.</summary>
    public void ShowError(string message)
    {
        SkipIntro();
        StageText.Text = message;
        StageText.Foreground = (Avalonia.Media.IBrush)this.FindResource("VxBadBrush")!;
        MetaDot.Fill = (Avalonia.Media.IBrush)this.FindResource("VxBadBrush")!;
    }
}
