// agy-statusline.exe
// Hooked into the Antigravity CLI "statusLine" setting (official, documented feed).
// The CLI pipes its session-state JSON to stdin; we save it for the overlay app
// and print a short credits line back to the CLI status bar.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class StatusLine
{
    static int Main()
    {
        try
        {
            string raw;
            using (var r = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                raw = r.ReadToEnd();

            if (string.IsNullOrWhiteSpace(raw)) { Console.Out.Write(" "); return 0; }

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgyCreditOverlay");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, "state.json");
            string tmp = Path.Combine(dir, "state." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp");
            File.WriteAllText(tmp, raw, new UTF8Encoding(false));

            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (File.Exists(dest)) File.Replace(tmp, dest, null);
                    else File.Move(tmp, dest);
                    break;
                }
                catch (IOException) { Thread.Sleep(30); }
                catch (UnauthorizedAccessException) { Thread.Sleep(30); }
            }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            Console.Out.Write(BuildLine(raw));
        }
        catch
        {
            Console.Out.Write(" ");
        }
        return 0;
    }

    static string BuildLine(string raw)
    {
        var ser = new JavaScriptSerializer();
        var root = ser.DeserializeObject(raw) as IDictionary<string, object>;
        if (root == null) return " ";
        object q;
        if (!root.TryGetValue("quota", out q) || !(q is IDictionary<string, object>))
            return "Credits: waiting for quota data";

        var parts = new List<string>();
        foreach (var kv in (IDictionary<string, object>)q)
        {
            var b = kv.Value as IDictionary<string, object>;
            if (b == null) continue;
            object v;
            if (b.TryGetValue("remaining_fraction", out v) && v != null)
                parts.Add(kv.Key + ": " + Math.Round(Convert.ToDouble(v) * 100) + "%");
            else if (b.TryGetValue("remaining_amount", out v) && v != null)
                parts.Add(kv.Key + ": " + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
        }
        return parts.Count == 0 ? "Credits: n/a" : "Credits  " + string.Join("  |  ", parts.ToArray());
    }
}
