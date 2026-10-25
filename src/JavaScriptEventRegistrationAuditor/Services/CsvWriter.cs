using System.Collections.Generic;
using System.IO;
using System.Text;

namespace JavaScriptEventRegistrationAuditor.Services
{
    public static class CsvWriter
    {
        public static void Write(string path, IList<string> headers, IEnumerable<IList<string>> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Line(headers));
            foreach (var r in rows) sb.AppendLine(Line(r));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Line(IList<string> cells)
        {
            var parts = new List<string>();
            foreach (var c in cells)
            {
                string v = c ?? string.Empty;
                // Neutralise spreadsheet formula injection while keeping the text readable.
                if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@')) v = "'" + v;
                parts.Add("\"" + v.Replace("\"", "\"\"") + "\"");
            }
            return string.Join(",", parts);
        }
    }
}
