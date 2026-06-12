using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-002 D2 §6: the accuracy harness must count edits the way the literature does — WER =
/// (S+I+D)/N over normalized word tokens — and the alignment it returns must color the right
/// words, or the diff teaches users the wrong lesson about their model.
/// </summary>
public class WordErrorRateTests
{
    [Fact]
    public void Perfect_Match_Is_Zero_Wer()
    {
        var r = WordErrorRate.Compute("the quick brown fox", "the quick brown fox");
        Assert.Equal(0, r.Wer);
        Assert.All(r.Tokens, t => Assert.True(t.IsMatch));
    }

    [Fact]
    public void Punctuation_And_Case_Do_Not_Count_As_Errors()
    {
        // WER measures recognition, not orthography.
        var r = WordErrorRate.Compute("Hello, world.", "hello world");
        Assert.Equal(0, r.Wer);
    }

    [Fact]
    public void Substitution_Insertion_And_Deletion_Are_Each_Counted_And_Located()
    {
        // ref: the quick brown fox jumps    over the lazy dog
        // hyp: the quick brown fox jumped over a    lazy dog today
        var r = WordErrorRate.Compute(
            "the quick brown fox jumps over the lazy dog",
            "the quick brown fox jumped over a lazy dog today");

        Assert.Equal(2, r.Substitutions); // jumps→jumped, the→a
        Assert.Equal(1, r.Insertions);    // today
        Assert.Equal(0, r.Deletions);
        Assert.Equal(9, r.ReferenceWords);
        Assert.Equal(3.0 / 9, r.Wer, 5);

        var subs = r.Tokens.Where(t => t.IsSubstitution).Select(t => t.Word).ToArray();
        Assert.Equal(["jumped", "a"], subs);
        Assert.Equal("was \"jumps\"", r.Tokens.First(t => t.IsSubstitution).Detail);
        Assert.Equal("today", Assert.Single(r.Tokens, t => t.IsInsertion).Word);
    }

    [Fact]
    public void Deletions_Surface_The_Missing_Reference_Word()
    {
        var r = WordErrorRate.Compute("ask not what your country", "ask what your country");
        Assert.Equal(1, r.Deletions);
        Assert.Equal("not", Assert.Single(r.Tokens, t => t.IsDeletion).Word);
        Assert.Equal(1.0 / 5, r.Wer, 5);
    }

    [Fact]
    public void Empty_Hypothesis_Is_Total_Deletion()
    {
        var r = WordErrorRate.Compute("one two three", "");
        Assert.Equal(1.0, r.Wer);
        Assert.Equal(3, r.Deletions);
    }

    [Fact]
    public void Wer_Can_Exceed_One_Hundred_Percent()
    {
        // More insertions than reference words — the metric is honest about it.
        var r = WordErrorRate.Compute("hi", "hello there general kenobi");
        Assert.True(r.Wer > 1);
    }
}
