using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using JavaScriptEventRegistrationAuditor.Resources;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace JavaScriptEventRegistrationAuditor
{
    /// <summary>MEF entry point discovered by XrmToolBox (assembly must be in the Plugins folder).</summary>
    [Export(typeof(IXrmToolBoxPlugin)),
     ExportMetadata("Name", "JavaScript Event Registration Auditor"),
     ExportMetadata("Description", "Audits JavaScript form event registrations across Dataverse tables and exports an Excel report."),
     ExportMetadata("SmallImageBase64", IconData.Small32),
     ExportMetadata("BigImageBase64", IconData.Big80),
     ExportMetadata("BackgroundColor", "White"),
     ExportMetadata("PrimaryFontColor", "#0F6CBD"),
     ExportMetadata("SecondaryFontColor", "DimGray")]
    public class Plugin : PluginBase
    {
        public Plugin()
        {
            // Dependencies (ClosedXML etc.) live in Plugins\<AssemblyName>\ beside the plugin DLL.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPluginFolder;
        }

        public override IXrmToolBoxPluginControl GetControl() { return new MainControl(); }

        private static Assembly ResolveFromPluginFolder(object sender, ResolveEventArgs args)
        {
            var self = Assembly.GetExecutingAssembly();
            string file = new AssemblyName(args.Name).Name + ".dll";
            string folder = Path.Combine(Path.GetDirectoryName(self.Location) ?? string.Empty, self.GetName().Name);
            string path = Path.Combine(folder, file);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }
}
