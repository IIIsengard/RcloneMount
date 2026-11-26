using System.Drawing;
using RcloneMount.UI;

namespace RcloneMount.Tests;

public class ThemeContrastTests
{
    // The client_id banner's 8.75 pt link sits on WarnTint: WCAG AA needs 4.5:1 for normal-size text.
    [Theory]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void BannerLink_OnWarnTint_MeetsAA(ThemeMode mode)
    {
        var before = Theme.Mode;
        try
        {
            Theme.SetMode(mode);
            Assert.True(Contrast(Theme.AccentHover, Theme.WarnTint) >= 4.5, $"link {mode}");
            Assert.True(Contrast(Theme.TextPrimary, Theme.WarnTint) >= 4.5, $"pressed link {mode}");
        }
        finally { Theme.SetMode(before); }
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Lin(int v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
