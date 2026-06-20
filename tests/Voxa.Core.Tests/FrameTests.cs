using Voxa.Frames;

namespace Voxa.Core.Tests;

public class FrameTests
{
    [Fact]
    public void Default_Direction_Is_Downstream()
    {
        var f = new TextFrame("hi");
        Assert.Equal(FrameDirection.Downstream, f.Direction);
    }

    [Fact]
    public void Generated_Ids_Are_Unique()
    {
        var ids = Enumerable.Range(0, 1000)
            .Select(_ => new TextFrame("x").Id)
            .ToHashSet();
        Assert.Equal(1000, ids.Count);
    }

    [Fact] // CQ-004: Id is materialized on read from a stored ULID struct — it must be stable across reads.
    public void Id_Is_Stable_Across_Reads_And_Well_Formed()
    {
        var f = new TextFrame("x");
        Assert.Equal(f.Id, f.Id);       // same string each read (computed from the one stored struct, not regenerated)
        Assert.Equal(26, f.Id.Length);  // canonical ULID length
    }

    [Fact]
    public void With_Expression_Produces_Modified_Clone_Without_Mutating_Original()
    {
        var original = new TextFrame("hello");
        var modified = original with { Direction = FrameDirection.Upstream };

        Assert.Equal(FrameDirection.Downstream, original.Direction);
        Assert.Equal(FrameDirection.Upstream, modified.Direction);
        Assert.Equal(original.Id, modified.Id);
        Assert.Equal("hello", modified.Text);
    }

    [Fact]
    public void Records_With_Same_Content_Are_Value_Equal()
    {
        const string id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        var a = new TextFrame("hi") { Id = id };
        var b = new TextFrame("hi") { Id = id };
        Assert.Equal(a, b);
    }

    [Fact]
    public void Frame_Subclasses_Are_Categorized_Correctly()
    {
        Assert.IsAssignableFrom<DataFrame>(new TextFrame("x"));
        Assert.IsAssignableFrom<DataFrame>(new AudioRawFrame(ReadOnlyMemory<byte>.Empty, 16000, 1));
        Assert.IsAssignableFrom<ControlFrame>(new StartFrame());
        Assert.IsAssignableFrom<ControlFrame>(new EndFrame());
        Assert.IsAssignableFrom<SystemFrame>(new InterruptionFrame());
        Assert.IsAssignableFrom<SystemFrame>(new ErrorFrame("boom"));
    }

    [Fact]
    public void EndFrame_Is_Marked_Uninterruptible()
    {
        Assert.IsAssignableFrom<IUninterruptible>(new EndFrame());
    }

    [Fact]
    public void AudioRawFrame_Carries_Pcm_And_Format()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var frame = new AudioRawFrame(bytes, 24000, 1);
        Assert.Equal(24000, frame.SampleRate);
        Assert.Equal(1, frame.Channels);
        Assert.Equal(4, frame.Pcm.Length);
    }
}
