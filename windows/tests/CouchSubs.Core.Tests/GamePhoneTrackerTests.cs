// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class GamePhoneTrackerTests
{
    [Fact]
    public void NeedsTwoAlignedSamplesAndClosesOnFirstMissingPhone()
    {
        var frame = Frame.Blank(2, 2);
        CropRegion? candidate = new(0.15, 0.83, 0.32, 0.58);
        int calls = 0;
        var tracker = new GamePhoneTracker(_ => { calls++; return candidate; });
        Assert.True(tracker.TrySample(frame, 100, out var initial));
        Assert.Null(initial);
        Assert.False(tracker.TrySample(frame, 260, out _));
        Assert.Equal(1, calls);
        Assert.True(tracker.TrySample(frame, 430, out var opened));
        Assert.Equal(candidate, opened);
        candidate = null;
        Assert.True(tracker.TrySample(frame, 760, out var closed));
        Assert.Null(closed);
        candidate = new(0.14, 0.8, 0.31, 0.56);
        Assert.True(tracker.TrySample(frame, 1090, out var reopening));
        Assert.Null(reopening);
        tracker.Reset();
        Assert.True(tracker.TrySample(frame, 1091, out var reset));
        Assert.Null(reset);
    }

    [Fact]
    public void DoesNotJoinUnrelatedOpenings()
    {
        var frame = Frame.Blank(2, 2);
        var area = new CropRegion(0.14, 0.8, 0.30, 0.52);
        var tracker = new GamePhoneTracker(_ => area);
        tracker.TrySample(frame, 1, out _);
        area = area with { Left = 0.42, Right = 0.64 };
        tracker.TrySample(frame, 401, out var moved);
        Assert.Null(moved);
        tracker.TrySample(frame, 801, out var stable);
        Assert.Equal(area, stable);
    }

    [Fact]
    public void EmptyPictureAndSubtitleStripeDoNotOpenPhone()
    {
        var frame = Frame.Blank(640, 360);
        Array.Fill(frame.Pixels, (byte)210, 280 * frame.Stride, 14 * frame.Stride);
        var tracker = new GamePhoneTracker();
        tracker.TrySample(frame, 0, out _);
        tracker.TrySample(frame, 500, out var result);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(110, 25)]
    [InlineData(55, 23)]
    public void UsesTheLowerBezelInsteadOfAHighContrastTextStroke(byte casing, byte slot)
    {
        var frame = PhoneFrame(casing, slot);
        Assert.True(GamePhoneGeometry.TryLocate(frame, out var pose));
        Assert.InRange(pose.TopY * frame.Height, 32, 35);
        Assert.InRange(pose.BottomY * frame.Height, 188, 191);
        for (int y = 32; y < 37; y++)
            Array.Fill(frame.Pixels, casing, y * frame.Stride + 211 * 4, 5 * 4);
        Assert.False(GamePhoneGeometry.TryLocate(frame, out _));
    }

    internal static Frame PhoneFrame(byte casing = 110, byte slot = 25)
    {
        var frame = Frame.Blank(400, 225);
        Fill(0, 0, 400, 225, 85);
        Fill(160, 22, 84, 179, casing);
        Fill(164, 40, 76, 141, 195);
        Fill(196, 33, 13, 2, slot);  // kulaklık
        Fill(212, 33, 3, 3, slot);   // ön kamera
        Fill(198, 189, 9, 2, slot);   // alt yarık
        Fill(198, 170, 9, 2, 24);     // yazının parçası

        return frame;

        void Fill(int left, int top, int width, int height, byte gray)
        {
            for (int y = top; y < top + height; y++)
                Array.Fill(frame.Pixels, gray, y * frame.Stride + left * 4, width * 4);
        }
    }
}
