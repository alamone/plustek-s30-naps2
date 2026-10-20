using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ScanKiosk;

public sealed record ScanOutcome(bool Ok, int PagesScanned, string? Error);

/// <summary>Runs NAPS2.Console for one scan. Output goes to a local temp file; Publish() moves it into place.</summary>
public sealed class ScanRunner
{
    public static readonly string WorkDir = Path.Combine(Path.GetTempPath(), "ScanKiosk");
    Process? _proc;
    volatile bool _cancelled;

    public event Action<int>? PageScanned;

    /// <param name="output">local temp PDF to write</param>
    /// <param name="prepend">existing local PDF whose pages go first ("Add more pages"), or null</param>
    public async Task<ScanOutcome> RunAsync(KioskConfig cfg, Naps2Profile profile, string output, string? prepend)
    {
        _cancelled = false;
        var psi = new ProcessStartInfo(cfg.Naps2ConsolePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-p", profile.Name, "-o", output, "-f", "-v" }) psi.ArgumentList.Add(a);
        if (prepend != null) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(prepend); }

        Log.Write($"Scan: \"{profile.Name}\" -> {output}{(prepend != null ? " (adding to existing pages)" : "")}");
        var lines = new List<string>();
        int pages = 0;
        try
        {
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("NAPS2.Console did not start");
        }
        catch (Exception ex)
        {
            Log.Write($"Cannot start NAPS2.Console: {ex.Message}");
            return new ScanOutcome(false, 0, $"Could not start NAPS2 ({ex.Message}).\nCheck that NAPS2 is installed at {cfg.Naps2ConsolePath}.");
        }

        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (lines) lines.Add(line.Trim());
            Log.Write("  naps2: " + line.Trim());
            var m = Regex.Match(line, @"Scanned page (\d+)");
            if (m.Success)
            {
                pages = int.Parse(m.Groups[1].Value);
                PageScanned?.Invoke(pages);
            }
        }
        _proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        _proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        await _proc.WaitForExitAsync();
        int code = _proc.ExitCode;
        _proc.Dispose();
        _proc = null;

        if (_cancelled) return new ScanOutcome(false, pages, null);
        bool saved = File.Exists(output) && new FileInfo(output).Length > 0;
        Log.Write($"Scan finished: exit {code}, {pages} page(s), output {(saved ? "written" : "missing")}");
        if (code == 0 && saved && pages > 0) return new ScanOutcome(true, pages, null);
        string[] all;
        lock (lines) all = lines.ToArray();
        return new ScanOutcome(false, pages, FriendlyError(all, pages, saved));
    }

    public void Cancel()
    {
        _cancelled = true;
        try { _proc?.Kill(entireProcessTree: true); } catch (Exception ex) { Log.Write($"Cancel: {ex.Message}"); }
    }

    static string FriendlyError(string[] lines, int pages, bool saved)
    {
        string all = string.Join("\n", lines);
        if (pages == 0 && (all.Contains("No scanned pages", StringComparison.OrdinalIgnoreCase) || saved || lines.Length <= 2))
            return "No pages were scanned.\nCheck that the paper is loaded in the feeder, then try again.";
        if (all.Contains("profile is unavailable", StringComparison.OrdinalIgnoreCase))
            return "This preset was not found in NAPS2.\nOpen NAPS2 on this computer and check the profile names.";
        if (Regex.IsMatch(all, "jam", RegexOptions.IgnoreCase))
            return "The paper jammed.\nOpen the scanner, remove the paper, and try again.";
        if (Regex.IsMatch(all, "busy|in use|offline|not (be )?found|disconnected|not available|no device", RegexOptions.IgnoreCase))
            return "The scanner is not available.\nCheck that it is switched on and connected, then try again.";
        var last = lines.LastOrDefault(l => !l.StartsWith("Beginning", StringComparison.Ordinal)) ?? "Unknown error";
        return "The scan did not complete.\n" + last;
    }

    // ------------------------------------------------------------------ output naming / publishing

    /// <summary>Expands NAPS2 placeholders: $(YYYY) $(YY) $(MM) $(DD) $(hh) $(mm) $(ss) and $(n…) = next free number.</summary>
    public static string ResolveOutputPath(string pattern, DateTime now)
    {
        string s = Environment.ExpandEnvironmentVariables(pattern)
            .Replace("$(YYYY)", now.ToString("yyyy")).Replace("$(YY)", now.ToString("yy"))
            .Replace("$(MM)", now.ToString("MM")).Replace("$(DD)", now.ToString("dd"))
            .Replace("$(hh)", now.ToString("HH")).Replace("$(mm)", now.ToString("mm")).Replace("$(ss)", now.ToString("ss"));
        var m = Regex.Match(s, @"\$\((n+)\)");
        if (m.Success)
        {
            for (int i = 1; i < 100000; i++)
            {
                var candidate = s.Replace(m.Value, i.ToString("D" + m.Groups[1].Value.Length));
                if (!File.Exists(candidate)) return candidate;
            }
        }
        if (!File.Exists(s)) return s;
        string dir = Path.GetDirectoryName(s)!, stem = Path.GetFileNameWithoutExtension(s), ext = Path.GetExtension(s);
        for (int i = 1; ; i++)
        {
            var c = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(c)) return c;
        }
    }

    /// <summary>
    /// Copies the finished local PDF to its destination under a temporary name (not *.pdf, so folder watchers
    /// ignore it) and renames it into place, so a partially written PDF never appears in the incoming folder.
    /// </summary>
    public static void Publish(string localPdf, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string tmp = destination + ".partial";
        File.Copy(localPdf, tmp, overwrite: true);
        File.Move(tmp, destination, overwrite: true);
    }
}
