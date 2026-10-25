using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>Reads the library list of a PCF control from its manifest (customcontrol.manifest).</summary>
    public static class PcfManifestParser
    {
        public const string NotRetrieved = "(control definition not retrieved)";
        public const string NoneListed = "(no libraries listed in manifest)";

        /// <summary>
        /// Code files (resources/code) and platform libraries (resources/platform-library, with version) separated by "; ".
        /// </summary>
        public static string Libraries(string manifestXml)
        {
            if (string.IsNullOrWhiteSpace(manifestXml)) return NotRetrieved;

            XDocument doc;
            try { doc = XDocument.Parse(manifestXml); }
            catch (Exception) { return NotRetrieved; }

            var items = new List<string>();
            foreach (var code in doc.Descendants("code"))
            {
                string path = (string)code.Attribute("path");
                if (!string.IsNullOrWhiteSpace(path)) items.Add(path.Trim());
            }
            foreach (var lib in doc.Descendants("platform-library"))
            {
                string name = (string)lib.Attribute("name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                string version = (string)lib.Attribute("version");
                items.Add(string.IsNullOrWhiteSpace(version) ? name.Trim() : name.Trim() + " " + version.Trim());
            }

            return items.Count == 0 ? NoneListed : string.Join("; ", items.Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }
}
