using System.Text;
using Concierge.Web.Voice;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class VoiceInterruptionTimingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompletesEachInterruptionAgainstItsOwnStartAndOnlyOnce()
    {
        var time = new FakeTimeProvider(Now);
        var timings = new VoiceInterruptionTimings(time);
        timings.Begin(1);

        time.Advance(TimeSpan.FromMilliseconds(250));
        timings.Begin(2);
        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(timings.TryComplete(2, out var secondDuration));
        Assert.Equal(100, secondDuration);
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(timings.TryComplete(1, out var firstDuration));
        Assert.Equal(550, firstDuration);
        Assert.False(timings.TryComplete(1, out _));
        Assert.False(timings.TryComplete(3, out _));
    }

    [Fact]
    public void ReadsOnlyPositiveInterruptionSilenceAcknowledgements()
    {
        Assert.True(VoiceRelay.TryReadInterruptionSilenced(
            Encoding.UTF8.GetBytes("""{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":true}"""),
            out var number, out var bargeIn));
        Assert.Equal(7, number);
        Assert.True(bargeIn);
        Assert.True(VoiceRelay.TryReadInterruptionSilenced(
            Encoding.UTF8.GetBytes("""{"type":"InterruptionSilenced","interruptionNumber":8,"bargeIn":false}"""),
            out _, out bargeIn));
        Assert.False(bargeIn);

        foreach (var message in new[]
        {
            """{"type":"PlaybackAck","interruptionNumber":7}""",
            """{"type":"InterruptionSilenced","interruptionNumber":0}""",
            """{"type":"InterruptionSilenced","interruptionNumber":"7"}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":"true"}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":1}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":null}""",
            """{"type":"InterruptionSilenced","interruptionNumber":0,"bargeIn":true}""",
            "[]",
            "{not json",
        })
        {
            Assert.False(VoiceRelay.TryReadInterruptionSilenced(Encoding.UTF8.GetBytes(message), out _, out _));
        }
    }

    [Fact]
    public void RepeatedStartForOneInterruptionDoesNotResetItsTimer()
    {
        var time = new FakeTimeProvider(Now);
        var timings = new VoiceInterruptionTimings(time);
        timings.Begin(1);
        time.Advance(TimeSpan.FromMilliseconds(250));

        timings.Begin(1);
        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(timings.TryComplete(1, out var duration));
        Assert.Equal(350, duration);
    }

    [Fact]
    public async Task FlushAudioReportsDiscardedAudioAndKeepsEvents()
    {
        var queue = new PlaybackQueue(10);
        queue.TryEnqueue(new PlaybackItem(false, new byte[960]));
        queue.TryEnqueue(new PlaybackItem(true, "{}"u8.ToArray()));
        queue.TryEnqueue(new PlaybackItem(false, new byte[960]));

        Assert.Equal(2, queue.FlushAudio());
        Assert.Equal(0, queue.FlushAudio());
        var remaining = await queue.DequeueAsync(new PlaybackPacer(9120), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(remaining.IsText);
        Assert.Equal("{}", Encoding.UTF8.GetString(remaining.Data));
    }

    [Fact]
    public void PlaybackLeadIsBoundedAndOnlyValidCumulativeAcksFreeCapacity()
    {
        var pacer = new PlaybackPacer(24_000 * 380 / 1000);
        for (var frame = 0; frame < 19; frame++)
        {
            Assert.True(pacer.TryReserve(480));
        }
        Assert.False(pacer.TryReserve(480));
        Assert.False(pacer.Acknowledge(9121));
        Assert.False(pacer.TryReserve(480));
        Assert.True(pacer.Acknowledge(480));
        Assert.True(pacer.TryReserve(480));
        Assert.False(pacer.Acknowledge(479));
        Assert.True(pacer.Acknowledge(9600)); // The flush can retire played and discarded samples.
        Assert.True(pacer.TryReserve(480));
    }
}
