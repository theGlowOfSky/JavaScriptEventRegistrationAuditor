using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using JavaScriptEventRegistrationAuditor.Models;
using JavaScriptEventRegistrationAuditor.Services;

namespace JavaScriptEventRegistrationAuditor.Tests
{
    [TestClass]
    public class PcfAndSubgridTests
    {
        private static FormInfo NewForm()
        {
            return new FormInfo { Id = Guid.NewGuid(), Name = "Main", TableLogicalName = "account", TypeName = "Main" };
        }

        [TestMethod]
        public void SubgridEvent_UsesTheControlAttributeAsControlName()
        {
            var form = NewForm(); int order = 0; string problem;
            var regs = FormXmlParser.Parse(form,
                "<form><events><event name='onrecordselect' application='false' active='false' control='Contacts'><Handlers>" +
                "<Handler functionName='onSelect' libraryName='a.js' handlerUniqueId='{1}' enabled='true' passExecutionContext='true' /></Handlers></event></events></form>",
                ref order, out problem);
            Assert.AreEqual(1, regs.Count);
            Assert.AreEqual("onrecordselect", regs[0].EventName);
            Assert.AreEqual("Contacts", regs[0].ControlName);
        }

        [TestMethod]
        public void FieldEvent_StillUsesTheAttributeAttribute()
        {
            var form = NewForm(); int order = 0; string problem;
            var regs = FormXmlParser.Parse(form,
                "<form><events><event name='onchange' attribute='name' control='ignored'><Handlers>" +
                "<Handler functionName='f' libraryName='a.js' handlerUniqueId='{1}' enabled='true' /></Handlers></event></events></form>", ref order, out problem);
            Assert.AreEqual("name", regs[0].ControlName);
        }

        [TestMethod]
        public void PcfControls_AreFoundAndBuiltInControlsSkipped()
        {
            var form = NewForm(); int order = 0; string problem;
            FormXmlParser.Parse(form,
                "<form><tabs><tab><columns><column><sections><section><rows><row>" +
                "<cell><control id='new_rating' classid='{x}' datafieldname='new_rating' /></cell></row></rows></section></sections></column></columns></tab></tabs>" +
                "<controlDescriptions>" +
                "<controlDescription forControl='new_rating'><customControl formFactor='0' name='contoso_Contoso.StarRating' /><customControl formFactor='1' name='contoso_Contoso.StarRating' /></controlDescription>" +
                "<controlDescription forControl='{ABC}'><customControl formFactor='0' name='MscrmControls.Grid.GridControl' /></controlDescription>" +
                "<controlDescription forControl='SubgridX'><customControl formFactor='0' name='fab_Fabrikam.Timeline' /></controlDescription>" +
                "</controlDescriptions></form>", ref order, out problem);
            Assert.IsNull(problem);
            Assert.AreEqual(2, form.PcfControls.Count);
            Assert.AreEqual("new_rating", form.PcfControls[0].ControlName);
            Assert.AreEqual("contoso_Contoso.StarRating", form.PcfControls[0].CustomControlName);
            Assert.AreEqual("SubgridX", form.PcfControls[1].ControlName);
        }

        [TestMethod]
        public void Manifest_ListsCodeFilesAndPlatformLibraries()
        {
            string xml = "<manifest><control namespace='N' constructor='C'><resources><code path='index.js' order='1'/>" +
                         "<platform-library name='React' version='16.8.6'/><platform-library name='Fluent' /></resources></control></manifest>";
            Assert.AreEqual("index.js; React 16.8.6; Fluent", PcfManifestParser.Libraries(xml));
            Assert.AreEqual(PcfManifestParser.NoneListed, PcfManifestParser.Libraries("<manifest><control/></manifest>"));
            Assert.AreEqual(PcfManifestParser.NotRetrieved, PcfManifestParser.Libraries(null));
            Assert.AreEqual(PcfManifestParser.NotRetrieved, PcfManifestParser.Libraries("<not xml"));
        }

        [TestMethod]
        public void PcfRow_IsListedButNotVerifiedOrCountedAsMismatch()
        {
            var d = new EnvironmentData();
            var form = NewForm(); d.Forms.Add(form);
            d.Registrations.Add(new HandlerRegistration
            {
                TableLogicalName = "account", FormName = "Main", FormId = form.Id, FormType = "Main", ControlName = "new_rating",
                EventName = "PCF Control", FunctionName = "contoso_Contoso.StarRating", LibraryName = "index.js; React 16.8.6", Enabled = true, IsPcf = true
            });
            AuditEngine.Run(d, new QualityOptions());
            var r = d.Registrations.Single();
            Assert.AreEqual(VerificationResults.PcfControl, r.VerificationResult);
            Assert.IsFalse(r.IsLibraryMismatch);
            Assert.AreEqual(0, r.ActionParts.Count);
            var t = d.Tables.Single();
            Assert.AreEqual(1, t.Events);
            Assert.AreEqual(1, t.Enabled);
            Assert.AreEqual(0, t.Mismatches);
        }

        [TestMethod]
        public void PcfRowWithoutManifest_ExplainsLibrariesAreUnknown()
        {
            var d = new EnvironmentData();
            d.Registrations.Add(new HandlerRegistration { TableLogicalName = "account", FormId = Guid.NewGuid(), EventName = "PCF Control", FunctionName = "x_Y.Z", LibraryName = PcfManifestParser.NotRetrieved, Enabled = true, IsPcf = true });
            AuditEngine.Run(d, new QualityOptions());
            StringAssert.Contains(d.Registrations[0].RecommendedAction, "could not be retrieved");
        }

        [TestMethod]
        public void LibraryNamesToFetch_IgnoresPcfRows()
        {
            var regs = new[]
            {
                new HandlerRegistration { LibraryName = "a.js", FunctionName = "f" },
                new HandlerRegistration { LibraryName = "index.js; React 16.8.6", FunctionName = "x_Y.Z", IsPcf = true }
            };
            CollectionAssert.AreEqual(new[] { "a.js" }, ScanPlanner.LibraryNamesToFetch(regs, n => false));
        }

        [TestMethod]
        public void ControlEvents_AreFlaggedSoTheyCanBeSkippedByDefault()
        {
            var form = NewForm(); int order = 0; string problem;
            var regs = FormXmlParser.Parse(form,
                "<form><events>" +
                "<event name='onload'><Handlers><Handler functionName='a' libraryName='x.js' handlerUniqueId='{1}' enabled='true' /></Handlers></event>" +
                "<event name='onchange' attribute='name'><Handlers><Handler functionName='b' libraryName='x.js' handlerUniqueId='{2}' enabled='true' /></Handlers></event>" +
                "<event name='onrecordselect' control='Contacts'><Handlers><Handler functionName='c' libraryName='x.js' handlerUniqueId='{3}' enabled='true' /></Handlers></event>" +
                "</events></form>", ref order, out problem);
            Assert.AreEqual(3, regs.Count);
            Assert.IsFalse(regs.Single(r => r.EventName == "onload").IsControlEvent);
            Assert.IsFalse(regs.Single(r => r.EventName == "onchange").IsControlEvent);
            Assert.IsTrue(regs.Single(r => r.EventName == "onrecordselect").IsControlEvent);
        }

        [TestMethod]
        public void ScanOptions_AreOffByDefault()
        {
            var o = new ScanOptions();
            Assert.IsFalse(o.IncludeSubgridEvents);
            Assert.IsFalse(o.IncludePcfControls);
        }
    }
}
