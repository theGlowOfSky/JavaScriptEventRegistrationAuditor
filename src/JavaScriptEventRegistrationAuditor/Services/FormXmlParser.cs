using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using JavaScriptEventRegistrationAuditor.Models;

namespace JavaScriptEventRegistrationAuditor.Services
{
    /// <summary>
    /// Extracts event handler registrations from Dataverse systemform XML
    /// (&lt;event name=".." attribute=".."&gt;&lt;Handlers&gt;&lt;Handler functionName libraryName handlerUniqueId enabled passExecutionContext parameters/&gt;).
    /// </summary>
    public static class FormXmlParser
    {
        public static List<HandlerRegistration> Parse(FormInfo form, string xml, ref int orderCounter, out string problem)
        {
            problem = null;
            var list = new List<HandlerRegistration>();
            if (string.IsNullOrWhiteSpace(xml))
            {
                problem = "Form XML is empty.";
                return list;
            }

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception ex)
            {
                problem = "Form XML could not be parsed: " + ex.Message;
                return list;
            }

            // Libraries attached to the form (used to limit the "other library" search to this form).
            foreach (var lib in doc.Descendants("formLibraries").Descendants("Library"))
            {
                string libName = ((string)lib.Attribute("name") ?? string.Empty).Trim();
                if (libName.Length > 0 && !form.LibraryNames.Contains(libName, StringComparer.OrdinalIgnoreCase)) form.LibraryNames.Add(libName);
            }

            // Custom (PCF) controls: <controlDescriptions><controlDescription forControl=".."><customControl name="..">.
            // Built-in platform controls ("MscrmControls.*") are not listed.
            foreach (var cd in doc.Descendants("controlDescription"))
            {
                string forControl = ((string)cd.Attribute("forControl") ?? string.Empty).Trim();
                foreach (var cc in cd.Descendants("customControl"))
                {
                    string ccName = ((string)cc.Attribute("name") ?? string.Empty).Trim();
                    if (ccName.Length == 0 || ccName.StartsWith("MscrmControls.", StringComparison.OrdinalIgnoreCase)) continue;
                    string ctlName = ResolveControlName(doc, forControl);
                    if (!form.PcfControls.Any(p => string.Equals(p.ControlName, ctlName, StringComparison.OrdinalIgnoreCase) && string.Equals(p.CustomControlName, ccName, StringComparison.OrdinalIgnoreCase)))
                        form.PcfControls.Add(new PcfControlRef { ControlName = ctlName, CustomControlName = ccName });
                }
            }

            foreach (var ev in doc.Descendants("event"))
            {
                string eventName = (string)ev.Attribute("name") ?? string.Empty;
                string control = (string)ev.Attribute("attribute");
                bool isControlEvent = false;
                // Control events (e.g. subgrid onrecordselect) name their control in the "control" attribute.
                if (string.IsNullOrEmpty(control))
                {
                    control = (string)ev.Attribute("control");
                    isControlEvent = !string.IsNullOrEmpty(control);
                }
                if (string.IsNullOrEmpty(control))
                {
                    // Control/tab level events: use the closest enclosing identifier, if any.
                    var ctl = ev.Ancestors("control").FirstOrDefault();
                    control = ctl != null ? (string)ctl.Attribute("id") : null;
                    isControlEvent = !string.IsNullOrEmpty(control);
                    if (string.IsNullOrEmpty(control))
                    {
                        var tab = ev.Ancestors("tab").FirstOrDefault();
                        control = tab != null ? (string)tab.Attribute("name") : null;
                    }
                }

                foreach (var h in ev.Descendants("Handler"))
                {
                    list.Add(new HandlerRegistration
                    {
                        TableLogicalName = form.TableLogicalName,
                        FormName = form.Name,
                        FormId = form.Id,
                        FormType = form.TypeName,
                        ControlName = control ?? string.Empty,
                        IsControlEvent = isControlEvent,
                        EventName = eventName,
                        HandlerId = (string)h.Attribute("handlerUniqueId") ?? string.Empty,
                        FunctionName = ((string)h.Attribute("functionName") ?? string.Empty).Trim(),
                        LibraryName = ((string)h.Attribute("libraryName") ?? string.Empty).Trim(),
                        Parameters = (string)h.Attribute("parameters") ?? string.Empty,
                        Enabled = ParseBool((string)h.Attribute("enabled"), true),
                        PassExecutionContext = ParseBool((string)h.Attribute("passExecutionContext"), false),
                        OriginalOrder = orderCounter++
                    });
                }
            }
            return list;
        }

        /// <summary>Maps a controlDescription's forControl to the bound control: its data field name, else its id, else the raw value.</summary>
        private static string ResolveControlName(XDocument doc, string forControl)
        {
            string wanted = forControl.Trim('{', '}');
            if (wanted.Length == 0) return string.Empty;

            var ctl = doc.Descendants("control").FirstOrDefault(c =>
                string.Equals(((string)c.Attribute("id") ?? string.Empty).Trim('{', '}'), wanted, StringComparison.OrdinalIgnoreCase));

            if (ctl == null) return forControl;

            string field = (string)ctl.Attribute("datafieldname");
            return !string.IsNullOrEmpty(field) ? field : ((string)ctl.Attribute("id") ?? forControl);
        }

        private static bool ParseBool(string value, bool whenMissing)
        {
            if (string.IsNullOrEmpty(value)) return whenMissing;
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }
    }
}
