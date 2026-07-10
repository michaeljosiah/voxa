namespace Voxa.Frames;

/// <summary>
/// What triggered an agent turn (VDX-008): the user's transcribed utterance, or a completed
/// background task re-entering the conversation. Lives in the frame namespace (not Processors)
/// because turn-lifecycle frames carry it — frames must not reference processor types.
/// </summary>
public enum TurnTrigger
{
    UserUtterance,
    BackgroundResult,
}
