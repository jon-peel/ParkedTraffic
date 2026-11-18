using System.Data;
using System.Globalization;
using NStack;
using Terminal.Gui;

namespace ParkedTraffic;

/// <summary>Terminal UI: stand table (by bay), log, and key actions. All sim work goes through <see cref="SimWorker"/>.</summary>
public static class Tui
{
    private static SimWorker _worker = null!;
    private static TableView _table = null!;
    private static ListView _logView = null!;
    private static Label _header = null!;
    private static readonly List<string> _log = new();
    private static List<StandRow> _rows = new();
    private static bool _allStands;
    private static bool _dirty = true;

    // Search ("/"): bar above the key bar. While _searching, keys go to the search field.
    private static TextField _searchField = null!;
    private static Label _searchInfo = null!;
    private static bool _searching;
    private static string _searchTerm = "";

    /// <param name="icao">Airport to fill, or null to detect it from your aircraft's position.</param>
    public static int Run(string? icao)
    {
        _worker = new SimWorker(icao);
        Log.Line += line =>
        {
            lock (_log) { _log.Add(line); if (_log.Count > 500) _log.RemoveAt(0); }
            _dirty = true;
        };

        Application.Init();
        ApplyDarkTheme();
        var top = Application.Top;

        var win = new Window(icao is null ? "ParkedTraffic - finding your airport..." : $"ParkedTraffic - {icao.ToUpperInvariant()}")
            { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
        _header = new Label("Starting...") { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        _table = new TableView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Percent(68), FullRowSelect = true, MultiSelect = false };
        _table.Style.AlwaysShowHeaders = true;
        _logView = new ListView(new List<string>()) { X = 0, Y = Pos.Bottom(_table), Width = Dim.Fill(), Height = Dim.Fill() };
        win.Add(_header, _table, _logView);

        // Single-key actions (either case). The status bar only shows them / handles mouse clicks.
        var actions = new Dictionary<char, Action>
        {
            ['a'] = AddDialog,
            ['r'] = RemoveHold,
            ['h'] = ToggleHold,
            ['e'] = () => EditRule(apron: false),
            ['p'] = () => EditRule(apron: true),
            ['x'] = ExcludeAirline,
            ['f'] = () => _worker.Post(() => _worker.Filler?.TopUp()),
            ['t'] = () => { _worker.AutoRefill = !_worker.AutoRefill; _dirty = true; },
            ['s'] = () => { _allStands = !_allStands; _dirty = true; },
            ['q'] = Quit,
            ['?'] = ShowHelp,
            ['o'] = QuotaDialog,
            ['b'] = () => { if (Selected is { } row) _worker.Post(() => _worker.Filler?.ToggleBeacon(row.Stand)); },
            ['/'] = StartSearch,
        };

        // Key bar at the bottom; wraps onto more lines when the window is narrow.
        var bar = new KeyBar(new[]
        {
            "~?~ Help", "~/~ search", "~A~dd", "~R~emove+hold", "~H~old/release", "~E~dit stand", "a~P~ron rule",
            "e~X~clude airline", "~F~ill now", "qu~O~ta", "~B~eacon", "~T~oggle refill", "all ~S~tands", "~Ctrl+A..Z~ apron", "~Q~uit",
        })
        { X = 0, ColorScheme = Colors.Menu };

        // Search bar: one line just above the key bar.
        // CanFocus must be true on the container, or the field inside can't take focus (Terminal.Gui throws).
        var searchRow = new View { X = 0, Width = Dim.Fill(), Height = 1, ColorScheme = Colors.Menu, CanFocus = true };
        var slash = new Label("/") { X = 1, Y = 0 };
        _searchField = new TextField("") { X = 3, Y = 0, Width = 24, CanFocus = false };
        _searchInfo = new Label("") { X = 29, Y = 0, Width = Dim.Fill() };
        searchRow.Add(slash, _searchField, _searchInfo);
        _searchField.TextChanged += _ =>
        {
            if (!_searching) return;
            _searchTerm = _searchField.Text?.ToString()?.Trim() ?? "";
            JumpToMatch(next: false); // live: first match as you type
        };
        top.Add(win, searchRow, bar);
        UpdateSearchInfo();

        void LayoutBar()
        {
            int lines = bar.HeightFor(Application.Driver.Cols);
            bar.Y = Pos.AnchorEnd(lines);
            bar.Width = Dim.Fill();
            bar.Height = lines;
            searchRow.Y = Pos.AnchorEnd(lines + 1);
            win.Height = Dim.Fill(lines + 1);
            top.LayoutSubviews();
            top.SetNeedsDisplay();
        }
        LayoutBar();
        Application.Resized += _ => LayoutBar();

        // Intercept keys before any view sees them, but only on the main screen (not inside dialogs).
        Application.RootKeyEvent = ke =>
        {
            if (Application.Current != top) return false;

            // In the search bar: Enter = done typing (keep the search), Esc = cancel; everything else is typed.
            if (_searching)
            {
                if (ke.Key == Key.Enter) { EndSearch(keepTerm: true); return true; }
                if (ke.Key == Key.Esc) { EndSearch(keepTerm: false); return true; }
                return false;
            }

            // With a search active, Enter cycles to the next match and Esc clears it; otherwise Enter = Add.
            if (ke.Key == Key.Enter) { if (_searchTerm.Length > 0) JumpToMatch(next: true); else AddDialog(); return true; }
            if (ke.Key == Key.Esc && _searchTerm.Length > 0) { EndSearch(keepTerm: false); return true; }
            if (ke.Key == Key.F1) { ShowHelp(); return true; }
            // Ctrl+Q keeps its usual meaning (quit, with the confirm + clean removal); other Ctrl+letters jump to aprons.
            if (CtrlLetter(ke) is { } apron) { if (apron == 'Q') Quit(); else JumpToApron(apron); return true; }
            if (ke.KeyValue is > 0 and < 0x10000 && (ke.Key & (Key.CtrlMask | Key.AltMask)) == 0)
            {
                char c = char.ToLowerInvariant((char)ke.KeyValue);
                if (actions.TryGetValue(c, out var act)) { act(); return true; }
            }
            return false;
        };

        _worker.Ready += () =>
        {
            _dirty = true;
            Application.MainLoop?.Invoke(() => win.Title = $"ParkedTraffic - {_worker.Icao}");
        };
        _worker.Stopped += () => Application.MainLoop?.Invoke(() => Application.RequestStop());
        _worker.Start();

        Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(500), _ => { Refresh(); return true; });
        Application.Run();
        Application.Shutdown();
        _worker.Stop();
        return 0;
    }

    /// <summary>Dark theme: light text on black, cyan highlights, yellow hotkeys.</summary>
    private static void ApplyDarkTheme()
    {
        static Terminal.Gui.Attribute A(Color fg, Color bg) => Application.Driver.MakeAttribute(fg, bg);

        var main = new ColorScheme
        {
            Normal = A(Color.Gray, Color.Black),
            Focus = A(Color.Black, Color.Cyan),            // selected table row
            HotNormal = A(Color.BrightYellow, Color.Black),
            HotFocus = A(Color.BrightYellow, Color.Cyan),
            Disabled = A(Color.DarkGray, Color.Black),
        };
        var dialog = new ColorScheme
        {
            Normal = A(Color.White, Color.DarkGray),
            Focus = A(Color.Black, Color.Cyan),            // focused field / button
            HotNormal = A(Color.BrightYellow, Color.DarkGray),
            HotFocus = A(Color.BrightYellow, Color.Cyan),
            Disabled = A(Color.Gray, Color.DarkGray),
        };
        var bar = new ColorScheme
        {
            Normal = A(Color.Gray, Color.Black),
            Focus = A(Color.Black, Color.Cyan),
            HotNormal = A(Color.BrightCyan, Color.Black),
            HotFocus = A(Color.BrightYellow, Color.Cyan),
            Disabled = A(Color.DarkGray, Color.Black),
        };
        var error = new ColorScheme
        {
            Normal = A(Color.White, Color.Red),
            Focus = A(Color.Black, Color.BrightRed),
            HotNormal = A(Color.BrightYellow, Color.Red),
            HotFocus = A(Color.BrightYellow, Color.BrightRed),
            Disabled = A(Color.Gray, Color.Red),
        };
        Colors.TopLevel = main;
        Colors.Base = main;
        Colors.Dialog = dialog;
        Colors.Menu = bar;
        Colors.Error = error;
        Application.Top.ColorScheme = main;
    }

    private static void Refresh()
    {
        if (_worker.Filler is { } f) { _rows = f.Snapshot(_allStands); _dirty = true; }
        if (!_dirty) return;
        _dirty = false;

        var f2 = _worker.Filler;
        _header.Text = f2 is null
            ? $"Status: {_worker.Status}"
            : $"Parked {f2.PlacedCount}/{f2.Target} (quota {_worker.Config?.QuotaText})   auto-refill {(_worker.AutoRefill ? "ON" : "OFF")}   mix: {f2.MixSource}   {(_allStands ? "all stands" : "gates only")}";

        int sel = Math.Max(0, _table.SelectedRow);
        var dt = new DataTable();
        // Apron first; "Ap" when every apron name is short (A, B, ...).
        bool shortAprons = _rows.Where(r => r.Apron.Length > 0).All(r => r.Apron.Length <= 2);
        foreach (var c in new[] { shortAprons ? "Ap" : "Apron", "Stand", "Type", "Rad", "Rule", "Callsign", "Light", "Aircraft" }) dt.Columns.Add(c);
        foreach (var r in _rows)
            dt.Rows.Add(r.Apron, ShortStandName(r), r.Stand.TypeName + (r.Stand.HasJetway ? " ⊓" : ""), $"{r.Stand.Radius:F0}m", r.Rule,
                        r.Callsign, r.Lights, r.Aircraft);
        int offset = _table.RowOffset; // assigning a new Table can reset the scroll position
        _table.Table = dt;
        if (_rows.Count > 0)
        {
            _table.RowOffset = Math.Min(offset, _rows.Count - 1);
            _table.SelectedRow = Math.Min(sel, _rows.Count - 1);
        }

        List<string> copy;
        lock (_log) copy = _log.ToList();
        _logView.SetSource(copy);
        if (copy.Count > 0) _logView.SelectedItem = copy.Count - 1;
        _logView.TopItem = Math.Max(0, copy.Count - _logView.Bounds.Height);
    }

    /// <summary>Ctrl+A..Ctrl+Z -> 'A'..'Z' (drivers report either CtrlMask|letter or the control code 1..26).</summary>
    private static char? CtrlLetter(KeyEvent ke)
    {
        if ((ke.Key & Key.AltMask) != 0) return null;
        if ((ke.Key & Key.CtrlMask) != 0)
        {
            int k = (int)(ke.Key & ~Key.CtrlMask & ~Key.ShiftMask);
            if (k is >= 'A' and <= 'Z') return (char)k;
            if (k is >= 'a' and <= 'z') return char.ToUpperInvariant((char)k);
            if (k is >= 1 and <= 26) return (char)('A' + k - 1);
        }
        else if (ke.KeyValue is >= 1 and <= 26 && ke.Key is not (Key.Tab or Key.Enter or Key.Backspace))
            return (char)('A' + ke.KeyValue - 1);
        return null;
    }

    /// <summary>
    /// Ctrl+letter: jump to the apron with that letter (Gate A, B, C...), or else to aprons whose name starts with it
    /// ("Parking", "N Parking", "Gate"). Pressing it again cycles through the matching aprons.
    /// Selects the apron's first stand and scrolls it to the top of the table.
    /// </summary>
    private static void JumpToApron(char letter)
    {
        string l = letter.ToString();
        var aprons = _rows.Select(r => r.Apron).Distinct().ToList(); // in table (bay) order
        var matches = aprons.Where(a => a.Equals(l, StringComparison.OrdinalIgnoreCase)).ToList();
        matches.AddRange(aprons.Where(a => a.Length > 1 && a.StartsWith(l, StringComparison.OrdinalIgnoreCase)));
        if (matches.Count == 0) { Log.Write($"No apron starting with {letter} at this airport."); return; }

        // Already on one of the matches? Go to the next one.
        string? current = Selected?.Apron;
        int at = current is null ? -1 : matches.FindIndex(a => a.Equals(current, StringComparison.OrdinalIgnoreCase));
        string target = matches[(at + 1) % matches.Count];

        int idx = _rows.FindIndex(r => r.Apron.Equals(target, StringComparison.OrdinalIgnoreCase));
        _table.SelectedRow = idx;
        _table.RowOffset = idx;
        _table.SetNeedsDisplay();
    }

    // ------------------------------------------------------------------ search

    /// <summary>"/": start a new, empty search and put the cursor in the search bar.</summary>
    private static void StartSearch()
    {
        _searching = true;
        _searchTerm = "";
        _searchField.Text = "";
        _searchField.CanFocus = true;
        _searchField.SetFocus();
        UpdateSearchInfo();
    }

    /// <summary>Leave the search bar; keys go back to the table. keepTerm = Enter (keep cycling), false = Esc (clear).</summary>
    private static void EndSearch(bool keepTerm)
    {
        _searching = false;
        if (!keepTerm) { _searchTerm = ""; _searchField.Text = ""; }
        _searchField.CanFocus = false;
        _table.SetFocus();
        UpdateSearchInfo();
    }

    /// <summary>What a row can be found by: shown stand (A21), full stand name (Gate A 21), callsign (ours or other's).</summary>
    private static bool Matches(StandRow r, string term)
    {
        string callsign = r.Callsign.TrimStart('-', ' ').Replace(" *", "");
        foreach (var s in new[] { r.Apron + ShortStandName(r), ShortStandName(r), r.Stand.Name, callsign })
            if (s.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Select the first match (next: false) or the next match after the current row, wrapping round.</summary>
    private static void JumpToMatch(bool next)
    {
        var hits = _searchTerm.Length == 0 ? [] : Enumerable.Range(0, _rows.Count).Where(i => Matches(_rows[i], _searchTerm)).ToList();
        if (hits.Count > 0)
        {
            int cur = _table.SelectedRow;
            int target = next ? hits.FirstOrDefault(i => i > cur, hits[0]) : hits[0];
            _table.SelectedRow = target;
            _table.EnsureSelectedCellIsVisible();
            _table.SetNeedsDisplay();
        }
        UpdateSearchInfo(hits);
    }

    private static void UpdateSearchInfo(List<int>? hits = null)
    {
        if (_searchInfo is null) return;
        if (_searchTerm.Length == 0)
            _searchInfo.Text = _searching ? "type a stand or flight, Enter = done, Esc = cancel" : "press / to search";
        else
        {
            hits ??= Enumerable.Range(0, _rows.Count).Where(i => Matches(_rows[i], _searchTerm)).ToList();
            int pos = hits.IndexOf(_table.SelectedRow) + 1;
            _searchInfo.Text = hits.Count == 0 ? "no match"
                : $"{(pos > 0 ? pos : 1)} of {hits.Count}   " + (_searching ? "Enter = done, Esc = cancel" : "Enter = next, Esc = clear, / = new search");
        }
    }

    /// <summary>Stand name without the apron already shown in its own column: "Gate A 12" (A) -> "12", "Parking 555" (Parking) -> "555".</summary>
    private static string ShortStandName(StandRow r)
    {
        string name = r.Stand.Name, apron = r.Apron;
        if (apron.Length == 0) return name;
        foreach (var prefix in new[] { $"Gate {apron} ", $"{apron} " })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length)
                return name[prefix.Length..];
        return name;
    }

    private static StandRow? Selected =>
        _table.SelectedRow >= 0 && _table.SelectedRow < _rows.Count ? _rows[_table.SelectedRow] : null;

    // ------------------------------------------------------------------ actions

    private static void AddDialog()
    {
        if (Selected is not { } row || _worker.Filler is null || _worker.Config is null) return;
        var stand = row.Stand;
        if (row.Callsign.StartsWith('-')) { MessageBox.Query("Add", $"{stand.Name} has another aircraft on it.", "OK"); return; }

        var ok = new Button("OK", is_default: true);
        var cancel = new Button("Cancel");
        var d = new Dialog($"Add / replace on {stand.Name} ({stand.TypeName}, {stand.Radius:F0} m)", 64, 11, ok, cancel);
        d.Add(new Label("Airline-Type, e.g. SAA-A320 (blank = automatic pick):") { X = 1, Y = 1 });
        var text = new TextField("") { X = 1, Y = 2, Width = Dim.Fill(1) };
        var remember = new CheckBox("Remember as an option for this stand") { X = 1, Y = 4 };
        d.Add(text, remember);

        bool accepted = false;
        ok.Clicked += () => { accepted = true; Application.RequestStop(); };
        cancel.Clicked += () => Application.RequestStop();
        text.SetFocus();
        Application.Run(d);
        if (!accepted) return;

        string input = text.Text?.ToString()?.Trim() ?? "";
        (string airline, string type)? opt = input.Length == 0 ? null : StandRule.ParseOption(input);
        if (input.Length > 0 && opt is null) { MessageBox.ErrorQuery("Add", "Use the form AAA-TYPE, e.g. SAA-A320.", "OK"); return; }
        bool keep = remember.Checked;

        _worker.Post(() =>
        {
            var cfg = _worker.Config!;
            if (keep && opt is { } o)
            {
                lock (cfg)
                {
                    if (!cfg.Stands.TryGetValue(stand.Name, out var rule)) cfg.Stands[stand.Name] = rule = new StandRule();
                    string key = $"{o.airline}-{o.type}";
                    if (!rule.Options.Contains(key, StringComparer.OrdinalIgnoreCase)) rule.Options.Add(key);
                    cfg.Save(_worker.Airport!.Icao);
                }
                Log.Write($"{stand.Name}: remembered option {o.airline}-{o.type}.");
            }
            _worker.Filler!.PlaceManual(stand, opt?.airline, opt?.type);
        });
    }

    /// <summary>Set the fill quota: a total ("40") or a percentage of free stands ("60%"). Saved per airport.</summary>
    private static void QuotaDialog()
    {
        if (_worker.Config is not { } cfg || _worker.Filler is null) return;
        var ok = new Button("OK", is_default: true);
        var cancel = new Button("Cancel");
        var d = new Dialog("Fill quota", 56, 9, ok, cancel);
        d.Add(new Label("Total (e.g. 40) or % of free stands (e.g. 60%):") { X = 1, Y = 1 });
        var text = new TextField(cfg.QuotaText) { X = 1, Y = 2, Width = 12 };
        d.Add(text);
        bool accepted = false;
        ok.Clicked += () => { accepted = true; Application.RequestStop(); };
        cancel.Clicked += () => Application.RequestStop();
        text.SetFocus();
        Application.Run(d);
        if (!accepted) return;

        string input = text.Text?.ToString()?.Trim() ?? "";
        bool percent = input.EndsWith('%');
        if (!int.TryParse(input.TrimEnd('%').Trim(), out var value) || value < 0 || (percent && value > 100))
        {
            MessageBox.ErrorQuery("Quota", "Enter a number (e.g. 40) or a percentage (e.g. 60%).", "OK");
            return;
        }
        _worker.Post(() =>
        {
            lock (cfg)
            {
                if (percent) { cfg.FillPercent = value; cfg.FillCount = null; }
                else cfg.FillCount = value;
                cfg.Save(_worker.Airport!.Icao);
            }
            Log.Write($"Quota set to {cfg.QuotaText}.");
            _worker.Filler!.ApplyQuota();
        });
    }

    private static void RemoveHold()
    {
        if (Selected is not { } row) return;
        _worker.Post(() => _worker.Filler?.RemoveAndHold(row.Stand));
    }

    private static void ToggleHold()
    {
        if (Selected is not { } row) return;
        _worker.Post(() =>
        {
            var f = _worker.Filler!;
            if (f.IsHeld(row.Stand)) f.ReleaseHold(row.Stand); else f.RemoveAndHold(row.Stand);
        });
    }

    private static void ExcludeAirline()
    {
        if (Selected is not { } row || _worker.Config is null) return;
        var airline = row.Aircraft.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (row.State != "parked" || airline is null || row.Aircraft.StartsWith('(')) { MessageBox.Query("Exclude", "Select a stand with one of our parked aircraft.", "OK"); return; }
        string name = _worker.Routes?.AirlineName(airline) ?? airline;
        if (MessageBox.Query("Exclude airline", $"Never park {airline} ({name}) at {_worker.Airport!.Icao} again?\nAll {airline} aircraft here will be removed and replaced.", "Exclude", "Cancel") != 0) return;

        _worker.Post(() =>
        {
            var cfg = _worker.Config!;
            lock (cfg)
            {
                if (!cfg.ExcludeAirlines.Contains(airline, StringComparer.OrdinalIgnoreCase)) cfg.ExcludeAirlines.Add(airline);
                cfg.Save(_worker.Airport!.Icao);
            }
            Log.Write($"Excluded {airline} ({name}) at {_worker.Airport!.Icao}.");
            _worker.Filler!.RemoveAirline(airline);
            _worker.Filler.TopUp();
        });
    }

    private static void EditRule(bool apron)
    {
        if (Selected is not { } row || _worker.Config is null) return;
        var cfg = _worker.Config;
        if (apron && row.Apron.Length == 0) { MessageBox.Query("Apron rule", "This stand has no apron name. Use E to set a stand rule instead.", "OK"); return; }
        string key = apron ? row.Apron : row.Stand.Name;
        StandRule current;
        lock (cfg)
        {
            var dict = apron ? cfg.Aprons : cfg.Stands;
            current = dict.TryGetValue(key, out var r) ? r : new StandRule();
        }

        var ok = new Button("Save", is_default: true);
        var cancel = new Button("Cancel");
        var d = new Dialog(apron ? $"Apron rule: {key} (all its stands)" : $"Stand rule: {key} (overrides apron {row.Apron})", 72, 20, ok, cancel);

        d.Add(new Label("Traffic:") { X = 1, Y = 1 });
        var traffic = new RadioGroup(new ustring[] { "In_herit", "_Any passenger", "_Domestic only", "_International only", "_Cargo (freighters)" })
        {
            X = 14, Y = 1,
            SelectedItem = current.Traffic switch { null => 0, Traffic.Any => 1, Traffic.Domestic => 2, Traffic.International => 3, _ => 4 },
        };
        d.Add(traffic);

        d.Add(new Label("Airlines:") { X = 1, Y = 6 });
        var airlines = new TextField(string.Join(", ", current.Airlines)) { X = 14, Y = 6, Width = Dim.Fill(1) };
        d.Add(new Label("only these ICAO codes; blank = any") { X = 14, Y = 7 });

        d.Add(new Label("Options:") { X = 1, Y = 9 });
        var options = new TextField(string.Join(", ", current.Options)) { X = 14, Y = 9, Width = Dim.Fill(1) };
        d.Add(new Label("airline-type picks, e.g. SAA-A320, LNK-E190") { X = 14, Y = 10 });

        d.Add(new Label("Nudge (m):") { X = 1, Y = 12 });
        var nudge = new TextField(current.Nudge?.ToString("0.#", CultureInfo.InvariantCulture) ?? "") { X = 14, Y = 12, Width = 8 };
        d.Add(new Label("+ forward / - back") { X = 24, Y = 12 });

        var disabled = new CheckBox("Never auto-fill") { X = 14, Y = 14, Checked = current.Disabled == true };
        d.Add(airlines, options, nudge, disabled);

        bool accepted = false;
        ok.Clicked += () => { accepted = true; Application.RequestStop(); };
        cancel.Clicked += () => Application.RequestStop();
        Application.Run(d);
        if (!accepted) return;

        static List<string> Codes(ustring? s) =>
            (s?.ToString() ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToUpperInvariant()).Distinct().ToList();

        var opts = Codes(options.Text);
        var bad = opts.Where(o => StandRule.ParseOption(o) is null).ToList();
        if (bad.Count > 0) { MessageBox.ErrorQuery("Rule", $"Options must look like SAA-A320: {string.Join(", ", bad)}", "OK"); return; }
        double? nudgeValue = double.TryParse(nudge.Text?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var nv) ? nv : null;

        var updated = new StandRule
        {
            Traffic = traffic.SelectedItem switch { 0 => null, 1 => Traffic.Any, 2 => Traffic.Domestic, 3 => Traffic.International, _ => Traffic.Cargo },
            Airlines = Codes(airlines.Text),
            Options = opts.Select(o => { var p = StandRule.ParseOption(o)!.Value; return $"{p.airline}-{p.type}"; }).ToList(),
            Nudge = nudgeValue,
            Disabled = disabled.Checked ? true : null,
        };

        _worker.Post(() =>
        {
            lock (cfg)
            {
                (apron ? cfg.Aprons : cfg.Stands)[key] = updated;
                cfg.Save(_worker.Airport!.Icao);
            }
            Log.Write($"{(apron ? "Apron" : "Stand")} {key}: rule saved ({(updated.IsEmpty ? "cleared" : updated.Describe())}).");
            _worker.Filler!.EnforceRules();
            _worker.Filler.TopUp();
        });
    }

    private const string HelpText = """
        KEYS  (select a stand with Up/Down first; letters work in either case)

          ? / F1   This help.
          /        Search: type part of a stand (21, A21) or callsign (SAA, 121).
                     Jumps to the first match as you type. Enter = done typing,
                     then Enter on the table = next match. Esc = clear search.
                     / again always starts a new, empty search.
          A / Enter  Add or replace an aircraft on the selected stand
                     (Enter only when no search is active).
                     Type AIRLINE-TYPE (e.g. SAA-A320) or leave blank for an
                     automatic pick. Tick "Remember" to save it as an option
                     for this stand.
          R        Remove our aircraft and HOLD the stand empty
                     (use when VATSIM assigns this stand to a pilot).
          H        Hold / release. A released stand is filled again by
                     auto-refill.
          E        Edit the rule for this STAND (overrides its apron).
          P        Edit the rule for this stand's whole APRON (e.g. all of C).
          X        Exclude the selected aircraft's airline at this airport.
                     All its aircraft are removed and replaced. Saved.
          F        Fill now: top up to the target with a fresh live lookup.
          O        Quota: a total (40) or % of free stands (60%). Saved per
                     airport; extra aircraft are removed (never manual adds).
          B        Toggle the beacon on the aircraft at this stand - ours or
                     another (VATSIM via vPilot, FSLTL). Shows in the Light column.
          T        Toggle auto-refill (tops up once a minute when below target).
          S        Show all stands / gates and ramps only.
          Ctrl+A..Z  Jump to that apron (Ctrl+C -> Gate C stands). Where aprons
                     aren't lettered, jumps to aprons starting with that letter
                     (Ctrl+P -> "Parking"). Press again to cycle through them.
          Q        Remove all parked aircraft and quit (Ctrl+Q too).

        CALLSIGN COLUMN
          BAW55     One of our aircraft: a real flight number for that airline
                    here, also set in the sim (ATC ID). * = seen flying now.
          - SAA321  Another aircraft is on the stand (yours, FSLTL, VATSIM),
                    with its callsign/ATC ID if the sim has one ("-" if not).
          held      Kept empty until you release it (H).
          (blank)   Free.

        LIGHTS COLUMN  (other aircraft on a stand: VATSIM, FSLTL, yours)
          B T S L   Beacon, Taxi, Strobe, Landing; "-" = off.
                    e.g. B--- beacon on (about to start/push), BT-- taxiing out.
                    Updated every 5 s.

        TYPE COLUMN
          Gate S/M/H/XL, Cargo, Ramp GA ...   from the scenery.
          ⊓         Stand has a jet bridge (no turboprops auto-placed here).

        RULE OPTIONS  (E / P)
          Inherit         Use the apron rule (stands) / no rule (aprons).
          Any passenger   Domestic or international passenger aircraft.
          Domestic only   Airlines based in this airport's country.
          International   Airlines based elsewhere.
          Cargo           Freighters only. Scenery cargo stands default to this.
          Airlines        Only these ICAO codes (e.g. SAA, SFR).
          Options         Fixed picks, e.g. SAA-A320, LNK-E190.
          Nudge (m)       Move the aircraft forward (+) or back (-).
          Never auto-fill Leave this stand/apron empty.

        Rules and exclusions are saved in config/<ICAO>.json.
        Defunct airlines (all airports) are in config/global.json.
        """;

    private static void ShowHelp()
    {
        var close = new Button("Close", is_default: true);
        var d = new Dialog("ParkedTraffic help", 80, 30, close);
        var text = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), ReadOnly = true, WordWrap = false, Text = HelpText };
        d.Add(text);
        close.Clicked += () => Application.RequestStop();
        Application.Run(d);
    }

    private static void Quit()
    {
        if (MessageBox.Query("Quit", "Remove all parked aircraft and quit?", "Quit", "Cancel") != 0) return;
        _header.Text = "Removing parked aircraft...";
        _worker.Stop(); // Stopped event ends the UI loop
    }
}
