// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class GamePhoneReaderTests
{
    [Theory]
    [InlineData(-0.06)]
    [InlineData(0.06)]
    public void CorrectsBothTiltDirectionsAndRestoresWordAndIconPositions(double tilt)
    {
        var pose = new GamePhoneGeometry.Pose(0.50, 0.16, 0.50 + tilt, 0.82);
        var frame = Frame.Blank(800, 450);
        // Pixel coordinates are encoded in the image, so the fake OCR can verify which part of the source it received.
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
        {
            int i = y * frame.Stride + x * 4;
            frame.Pixels[i] = (byte)(x / 4);
            frame.Pixels[i + 1] = (byte)(y / 2);
        }
        var reader = new ProbeReader();
        var region = pose.Bounds;
        var reading = GamePhoneReader.Read(frame, region, pose, reader);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(4, reader.Samples.Count);
        var line = Assert.Single(reading.Lines);
        var box = line.Box!.Value.PlacedIn(new TextBox(region.Left, region.Top, region.Right, region.Bottom));
        Assert.InRange(Math.Abs(box.Left * frame.Width - reader.Samples.Min(p => p.X)), 0, 5);
        Assert.InRange(Math.Abs(box.Right * frame.Width - reader.Samples.Max(p => p.X)), 0, 5);
        Assert.InRange(Math.Abs(box.Top * frame.Height - reader.Samples.Min(p => p.Y)), 0, 3);
        Assert.InRange(Math.Abs(box.Bottom * frame.Height - reader.Samples.Max(p => p.Y)), 0, 3);
        Assert.Equal(line.Box, Assert.Single(line.Words!).Box);
        Assert.Equal(line.Box, Assert.Single(line.Icons!).Box);
        Assert.Equal(new TextBox(0.2, 0.25, 0.6, 0.35), line.PhoneBox);
    }

    [Fact]
    public void WrappedMessagesAndReplyButtonsStayWithTheirOwnBubbleOnAnAngledScreen()
    {
        // Tilt makes the display boxes overlap even between different bubbles.
        static TextLine Line(string text, double x, double y, double right) =>
            new(text, new TextBox(x, y, right, y + 0.068)) { PhoneBox = new TextBox(x, y, right, y + 0.026) };
        var reading = new Reading([
            Line("That was quite a", 0.10, 0.20, 0.75),
            Line("surprise!", 0.10, 0.237, 0.35),
            Line("Did you see it?", 0.10, 0.325, 0.70),
            Line("Ask later", 0.52, 0.78, 0.88),
            Line("No problem", 0.50, 0.862, 0.88),
        ]);
        var groups = GamePhoneText.Groups(reading);
        Assert.Equal(["That was quite a surprise!", "Did you see it?", "Ask later", "No problem"],
            groups.Select(group => Assert.Single(group.Paragraphs)));
        Assert.Equal(reading.Lines[0].Box!.Value.Union(reading.Lines[1].Box!.Value), groups[0].Box);
    }

    private sealed class ProbeReader : ITextReader
    {
        public int Calls { get; private set; }
        public List<(double X, double Y)> Samples { get; } = [];
        public string Read(Frame image) => throw new NotSupportedException();
        public Reading ReadScreen(Frame image)
        {
            Calls++;
            var box = new TextBox(0.2, 0.25, 0.6, 0.35);
            foreach (double v in new[] { box.Top, box.Bottom })
            foreach (double u in new[] { box.Left, box.Right })
            {
                int i = (int)(image.Height * v) * image.Stride + (int)(image.Width * u) * 4;
                Samples.Add((image.Pixels[i] * 4, image.Pixels[i + 1] * 2));
            }
            return new Reading([new TextLine("Notice", box, Words: [new TextWord("Notice", box)],
                                            Icons: [new IconSlot(box, "X")])]);
        }
    }
}
