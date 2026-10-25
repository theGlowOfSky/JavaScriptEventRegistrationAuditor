using System.Text;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>
    /// Replaces comments and string/template literal contents with spaces while keeping every character offset
    /// and line break intact, so regex scans do not match text inside comments or strings.
    /// Known limitation: regular-expression literals containing quote characters are not recognised.
    /// </summary>
    public static class JsMasker
    {
        public static string Mask(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            int n = s.Length, i = 0;
            var sb = new StringBuilder(n);
            while (i < n)
            {
                char c = s[i];
                char d = i + 1 < n ? s[i + 1] : '\0';
                if (c == '/' && d == '/')
                {
                    while (i < n && s[i] != '\n' && s[i] != '\r') { sb.Append(' '); i++; }
                }
                else if (c == '/' && d == '*')
                {
                    sb.Append("  "); i += 2;
                    while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/')) { sb.Append(Keep(s[i])); i++; }
                    if (i < n) { sb.Append("  "); i += 2; }
                }
                else if (c == '\'' || c == '"' || c == '`')
                {
                    char q = c;
                    sb.Append(q); i++;
                    while (i < n && s[i] != q)
                    {
                        if (s[i] == '\\' && i + 1 < n) { sb.Append(' '); i++; sb.Append(Keep(s[i])); i++; continue; }
                        if (q != '`' && (s[i] == '\n' || s[i] == '\r')) break; // unterminated literal: stop at line end
                        sb.Append(Keep(s[i])); i++;
                    }
                    if (i < n && s[i] == q) { sb.Append(q); i++; }
                }
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }

        private static char Keep(char c) { return c == '\n' || c == '\r' ? c : ' '; }
    }
}
