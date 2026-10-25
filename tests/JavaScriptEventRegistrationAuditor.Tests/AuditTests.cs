using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using JavaScriptEventRegistrationAuditor.Models;
using JavaScriptEventRegistrationAuditor.Services;

namespace JavaScriptEventRegistrationAuditor.Tests
{
    [TestClass]
    public class AuditTests
    {
        private const string Xml = @"<form><events>
          <event name='onsave' application='false' active='false'><Handlers>
            <Handler functionName='Contoso.Acc.onSave' libraryName='new_acc.js' handlerUniqueId='{11111111-1111-1111-1111-111111111111}' enabled='true' passExecutionContext='true' parameters='' /></Handlers></event>
          <event name='onchange' attribute='name'><Handlers>
            <Handler functionName='onName' libraryName='new_acc.js' handlerUniqueId='{22222222-2222-2222-2222-222222222222}' enabled='false' passExecutionContext='false' parameters='' /></Handlers></event>
          <event name='onload'><Handlers>
            <Handler functionName='onLoad' libraryName='new_other.js' handlerUniqueId='{33333333-3333-3333-3333-333333333333}' enabled='true' passExecutionContext='true' parameters='' /></Handlers></event>
        </events></form>";

        private static EnvironmentData Build()
        {
            var d = new EnvironmentData { EnvironmentName = "Test" };
            var form = new FormInfo { Id = Guid.NewGuid(), Name = "Main", TableLogicalName = "account", TypeName = "Main" };
            d.Forms.Add(form);
            int order = 0; string problem;
            d.Registrations.AddRange(FormXmlParser.Parse(form, Xml, ref order, out problem));
            Assert.IsNull(problem);
            d.Libraries.Add(new WebResourceInfo { Name = "new_acc.js", Retrieved = true, Content =
                "var Contoso = Contoso || {};\nContoso.Acc = {\n  onSave: function (ctx) {\n    debugger;\n    var fc = ctx.getFormContext();\n  }\n};\nContoso.Acc.onSave = Contoso.Acc.onSave;\n" });
            d.Libraries.Add(new WebResourceInfo { Name = "new_other.js", Retrieved = true, Content = "function onLoad(executionContext) { /* } */ var s = '}'; }\nfunction onName(){ if (a == b) {} }" });
            return d;
        }

        [TestMethod]
        public void Parser_ReadsAllHandlersWithControlAndFlags()
        {
            var d = Build();
            Assert.AreEqual(3, d.Registrations.Count);
            var change = d.Registrations.Single(r => r.EventName == "onchange");
            Assert.AreEqual("name", change.ControlName);
            Assert.IsFalse(change.Enabled);
            Assert.AreEqual(string.Empty, d.Registrations.Single(r => r.EventName == "onsave").ControlName);
        }

        [TestMethod]
        public void Report_IsSortedByEventName()
        {
            var sorted = AuditEngine.SortForReport(Build().Registrations).Select(r => r.EventName).ToList();
            CollectionAssert.AreEqual(new[] { "onchange", "onload", "onsave" }, sorted);
        }

        [TestMethod]
        public void Verification_DistinguishesRegisteredAndOtherLibrary()
        {
            var d = Build();
            AuditEngine.Run(d, new QualityOptions());
            Assert.AreEqual(VerificationResults.FoundInRegistered, d.Registrations.Single(r => r.EventName == "onsave").VerificationResult);
            var load = d.Registrations.Single(r => r.EventName == "onload");
            Assert.AreEqual(VerificationResults.FoundInRegistered, load.VerificationResult);
            var change = d.Registrations.Single(r => r.EventName == "onchange");
            Assert.AreEqual(VerificationResults.FoundInOther, change.VerificationResult);
            Assert.AreEqual(1, d.Tables.Single().Mismatches);
            Assert.AreEqual(1, d.Tables.Single().Disabled);
        }

        [TestMethod]
        public void Scanner_IgnoresBracesInStringsAndComments()
        {
            var defs = FunctionScanner.Index("x.js", "function onLoad(c) { /* } */ var s = '}'; }\nfunction b(){}");
            Assert.IsTrue(defs.All(x => x.Complete));
            Assert.AreEqual(1, defs.Single(x => x.FullName == "onLoad").StartLine);
        }

        [TestMethod]
        public void SheetNames_AreSafeAndUnique()
        {
            var map = ExcelExporter.BuildSheetNames(new[] { "a:b", "a_b", new string('x', 40), "Table Index" });
            Assert.AreEqual(4, map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.IsTrue(map.Values.All(v => v.Length <= 31));
        }

        [TestMethod]
        public void FileName_UsesEnvironmentPrefix()
        {
            Assert.AreEqual("Prod_JavaScriptEventsRegistrations.xlsx", ExcelExporter.DefaultFileName("Prod"));
            Assert.IsFalse(ExcelExporter.DefaultFileName("a/b:c").Contains("/"));
        }
    }
}
