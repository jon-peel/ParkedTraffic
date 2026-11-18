using Terminal.Gui;

namespace ParkedTraffic;

/// <summary>
/// Bottom key bar that wraps onto extra lines when the window is narrow (the built-in StatusBar is one line only).
/// Items are written like "~A~dd": the letter between tildes is drawn as the hotkey.
/// </summary>
public sealed class KeyBar : View
{
    private const string Separator = " │ ";
    private readonly List<string> _items;

    public KeyBar(IEnumerable<string> items)
    {
        _items = items.ToList();
        CanFocus = false;
    }

    private static int VisibleLength(string item) => item.Replace("~", "").Length;

    /// <summary>Splits the items into lines that fit the given width.</summary>
    public List<List<string>> Lines(int width)
    {
        var lines = new List<List<string>> { new() };
        int used = 1; // leading space
        foreach (var item in _items)
        {
            int len = VisibleLength(item) + (lines[^1].Count > 0 ? Separator.Length : 0);
            if (lines[^1].Count > 0 && used + len > width)
            {
                lines.Add(new());
                used = 1;
                len = VisibleLength(item);
            }
            lines[^1].Add(item);
            used += len;
        }
        return lines;
    }

    /// <summary>Number of lines needed at this width.</summary>
    public int HeightFor(int width) => Math.Max(1, Lines(Math.Max(10, width)).Count);

    public override void Redraw(Rect bounds)
    {
        var scheme = ColorScheme ?? Colors.Menu;
        Driver.SetAttribute(scheme.Normal);
        Clear();

        var lines = Lines(Math.Max(10, Bounds.Width));
        for (int row = 0; row < lines.Count && row < Bounds.Height; row++)
        {
            Move(0, row);
            Driver.SetAttribute(scheme.Normal);
            Driver.AddStr(" ");
            for (int i = 0; i < lines[row].Count; i++)
            {
                if (i > 0) { Driver.SetAttribute(scheme.Disabled); Driver.AddStr(Separator); }
                var parts = lines[row][i].Split('~'); // even index = normal text, odd index = hotkey
                for (int p = 0; p < parts.Length; p++)
                {
                    Driver.SetAttribute(p % 2 == 1 ? scheme.HotNormal : scheme.Normal);
                    Driver.AddStr(parts[p]);
                }
            }
        }
    }
}
