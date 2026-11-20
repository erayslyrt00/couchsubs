// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>Eldeki telefonun eğik ekranını düz okuyup sözcükleri özgün görüntüdeki yerlerine döndürür.</summary>
internal static class GamePhoneReader
{
    public static Reading Read(Frame frame, CropRegion region, GamePhoneGeometry.Pose pose, ITextReader reader)
    {
        double topX = pose.TopX * frame.Width, topY = pose.TopY * frame.Height;
        double dx = (pose.BottomX - pose.TopX) * frame.Width, dy = (pose.BottomY - pose.TopY) * frame.Height;
        // Ekran iki hoparlör arasındadır. Perspektifte alt yatay kenarın eğimi üsttekinden farklıdır.
        (double X, double Y) Point(double u, double v)
        {
            double t = 0.04 + 0.915 * v;
            double side = (u - 0.5) * 0.52;
            return (topX + dx * t + side * dy, topY + dy * t - side * dx * (0.52 + v * 0.30));
        }

        // Küçük yazıyı tek örneklemede büyüt; yüksek çözünürlükte ara görüntü 960 satırı aşmasın.
        double scale = dy * 0.5 < 300 ? 1.5 : 1;
        scale = Math.Min(scale, 960 / (dy * 0.9));
        int width = Math.Max(1, (int)Math.Round(dy * 0.5 * scale));
        int height = Math.Max(1, (int)Math.Round(dy * 0.9 * scale));
        var flat = Frame.Blank(width, height);
        for (int y = 0; y < height; y++)
        {
            double v = (y + 0.5) / height;
            var left = Point(0, v);
            var right = Point(1, v);
            double stepX = (right.X - left.X) / width, stepY = (right.Y - left.Y) / width;
            double sx = left.X + stepX * 0.5 - 0.5, sy = left.Y + stepY * 0.5 - 0.5;
            for (int x = 0; x < width; x++, sx += stepX, sy += stepY)
                Sample(frame, sx, sy, flat.Pixels, y * flat.Stride + x * 4);
        }

        var reading = reader.ReadScreen(flat);
        if (reading.Lines.Count == 0) return Reading.Empty;
        TextBox Map(TextBox b)
        {
            var a = Point(b.Left, b.Top); var c = Point(b.Right, b.Top);
            var d = Point(b.Left, b.Bottom); var e = Point(b.Right, b.Bottom);
            double X(double x) => Math.Clamp((x / frame.Width - region.Left) / (region.Right - region.Left), 0, 1);
            double Y(double y) => Math.Clamp((y / frame.Height - region.Top) / (region.Bottom - region.Top), 0, 1);
            return new TextBox(X(Math.Min(Math.Min(a.X, c.X), Math.Min(d.X, e.X))),
                               Y(Math.Min(Math.Min(a.Y, c.Y), Math.Min(d.Y, e.Y))),
                               X(Math.Max(Math.Max(a.X, c.X), Math.Max(d.X, e.X))),
                               Y(Math.Max(Math.Max(a.Y, c.Y), Math.Max(d.Y, e.Y))));
        }
        return new Reading(reading.Lines.Select(line => line with
        {
            PhoneBox = line.Box,
            Box = line.Box is { } box ? Map(box) : null,
            Words = line.Words?.Select(w => w with { Box = Map(w.Box) }).ToArray(),
            Icons = line.Icons?.Select(i => i with { Box = Map(i.Box) }).ToArray(),
        }).ToArray());
    }

    private static void Sample(Frame frame, double x, double y, byte[] output, int offset)
    {
        x = Math.Clamp(x, 0, frame.Width - 1);
        y = Math.Clamp(y, 0, frame.Height - 1);
        int x0 = (int)x, y0 = (int)y;
        int wx = (int)((x - x0) * 256), wy = (int)((y - y0) * 256);
        int a = y0 * frame.Stride + x0 * 4, b = x0 + 1 < frame.Width ? a + 4 : a;
        int nextRow = y0 + 1 < frame.Height ? frame.Stride : 0;
        var pixels = frame.Pixels;
        for (int channel = 0; channel < 3; channel++)
        {
            int top = pixels[a + channel] * (256 - wx) + pixels[b + channel] * wx;
            int bottom = pixels[a + nextRow + channel] * (256 - wx) + pixels[b + nextRow + channel] * wx;
            output[offset + channel] = (byte)((top * (256 - wy) + bottom * wy + 32768) >> 16);
        }
        output[offset + 3] = 255;
    }
}
