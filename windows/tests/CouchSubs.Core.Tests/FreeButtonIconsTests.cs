// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

/// <summary>Ek alanlarda fiilsiz yerlerdeki simgeler (tuş çubuğu, satır başı) ve tuş çubuğu sözlüğü.</summary>
public class FreeButtonIconsTests
{
    private const char M = ButtonIcons.Marker;

    /// <summary>Siyah kare; verilen piksel kutularına beyaz halka çizilir (düğme dairesi gibi).</summary>
    private static Frame Draw(int width, int height, params (int X, int Y, int Size)[] rings)
    {
        var frame = Frame.Blank(width, height);
        foreach (var (x0, y0, size) in rings)
            for (var y = y0; y < y0 + size; y++)
                for (var x = x0; x < x0 + size; x++)
                {
                    var edge = x - x0 < 3 || y - y0 < 3 || x0 + size - x <= 3 || y0 + size - y <= 3;
                    if (!edge) continue;
                    var i = (y * frame.Stride) + (x * 4);
                    frame.Pixels[i] = frame.Pixels[i + 1] = frame.Pixels[i + 2] = 255;
                    frame.Pixels[i + 3] = 255;
                }
        return frame;
    }

    private static TextWord Word(string text, double left, double right) => new(text, new TextBox(left, 0.25, right, 0.75));

    private static TextLine Line(params TextWord[] words) =>
        new(string.Join(' ', words.Select(word => word.Text)), new TextBox(words[0].Box.Left, 0.25, words[^1].Box.Right, 0.75), null, words);

    [Fact]
    public void ButtonBarGapWithAnIconIsMarked()
    {
        // 400×40 kare, satır 20 piksel; DESTINATION ile EXIT arasında 40 piksellik boşlukta simge.
        var frame = Draw(400, 40, (191, 11, 18));
        var line = Line(Word("SET", 0.05, 0.15), Word("DESTINATION", 0.17, 0.45), Word("EXIT", 0.55, 0.65));
        var marked = ButtonIcons.MarkFree(line, frame);
        Assert.Equal($"SET DESTINATION {M} EXIT", marked.Text);
        Assert.Single(marked.Icons!);
    }

    [Fact]
    public void EmptyGapIsNotAnIcon()
    {
        var line = Line(Word("SET", 0.05, 0.15), Word("DESTINATION", 0.17, 0.45), Word("EXIT", 0.55, 0.65));
        Assert.Same(line, ButtonIcons.MarkFree(line, Draw(400, 40)));
    }

    /// <summary>Sıradan cümlenin ortasındaki şekil harita işaretidir (GTA 6 ipucu kutusu), simge sayılmaz.</summary>
    [Fact]
    public void GapInASentenceIsNotSearched()
    {
        var frame = Draw(400, 40, (191, 11, 18));
        var line = Line(Word("your", 0.05, 0.15), Word("vehicle", 0.17, 0.45), Word("for", 0.55, 0.65));
        Assert.Same(line, ButtonIcons.MarkFree(line, frame));
    }

    [Fact]
    public void IconLeftOfTheLineIsMarked()
    {
        // Satır x=120'de başlar; simge solunda (yükseklik 20, arama genişliği 36 piksel).
        var frame = Draw(400, 40, (92, 11, 18));
        var line = Line(Word("Pick", 0.30, 0.40), Word("up", 0.42, 0.47));
        var marked = ButtonIcons.MarkFree(line, frame);
        Assert.Equal($"{M} Pick up", marked.Text);
    }

    [Fact]
    public void MisreadLetterInAButtonBarIsMarkedWhenItLooksLikeAnIcon()
    {
        // "OFFER RIDE e LUCIA": ✕ "e" okunuyor.
        var frame = Draw(400, 40, (178, 11, 18));
        var line = Line(Word("OFFER", 0.05, 0.20), Word("RIDE", 0.22, 0.40), Word("e", 0.45, 0.50), Word("LUCIA", 0.56, 0.75));
        Assert.Equal($"OFFER RIDE {M} LUCIA", ButtonIcons.MarkFree(line, frame).Text);
    }

    [Fact]
    public void ButtonBarCommandsComeFromTheGlossary()
    {
        Assert.Equal($"Hedef belirle {M} Çıkış {M}", UiCommands.Translate($"SET DESTINATION {M} EXIT {M}"));
        Assert.Equal($"Aç {M}", UiCommands.Translate($"OPEN {M}"));
        Assert.Equal("Yakıt al", UiCommands.Translate("REFUEL"));
        Assert.Null(UiCommands.Translate("Open the door"));
        Assert.Null(UiCommands.Translate($"LAUNCH ROCKETS {M} EXIT"));
    }
}
