using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using JavaScriptEventRegistrationAuditor.Models;
using JavaScriptEventRegistrationAuditor.Services;

namespace JavaScriptEventRegistrationAuditor.Tests
{
    [TestClass]
    public class LibraryMismatchTests
    {
        private static string Handler(string fn, string lib, string id)
        {
            return "<Handler functionName='" + fn + "' libraryName='" + lib + "' handlerUniqueId='{" + id + "}' enabled='true' passExecutionContext='true' parameters='' />";
        }

        private static FormInfo AddForm(EnvironmentData d, string name, string handlersXml, string formLibrariesXml = "")
        {
            var form = new FormInfo { Id = Guid.NewGuid(), Name = name, TableLogicalName = "account", TypeName = "Main" };
            int order = d.Registrations.Count; string problem;
            string xml = "<form><events><event name='onload'><Handlers>" + handlersXml + "</Handlers></event></events>" + formLibrariesXml + "</form>";
            d.Forms.Add(form);
            d.Registrations.AddRange(FormXmlParser.Parse(form, xml, ref order, out problem));
            Assert.IsNull(problem);
            return form;
        }

        private static void Lib(EnvironmentData d, string name, string code)
        {
            d.Libraries.Add(new WebResourceInfo { Name = name, Retrieved = true, Content = code });
        }

        [TestMethod]
        public void FunctionInRegisteredLibrary_IsNotAMismatch()
        {
            var d = new EnvironmentData();
            AddForm(d, "Main", Handler("onLoad", "a.js", "1"));
            Lib(d, "a.js", "function onLoad(c){}");
            AuditEngine.Run(d, new QualityOptions());
            Assert.IsFalse(d.Registrations[0].IsLibraryMismatch);
            Assert.AreEqual(0, d.Tables.Single().Mismatches);
        }

        [TestMethod]
        public void FunctionNotFoundAnywhereOnTheForm_IsNotFoundAndCountedAsMismatch()
        {
            var d = new EnvironmentData();
            AddForm(d, "Main", Handler("onLoad", "a.js", "1"));
            Lib(d, "a.js", "function other(){}");
            AuditEngine.Run(d, new QualityOptions());
            var r = d.Registrations[0];
            Assert.AreEqual(VerificationResults.NotFound, r.VerificationResult);
            Assert.AreEqual("Function not found in referenced library", r.VerificationResult);
            Assert.IsTrue(r.IsLibraryMismatch);
            Assert.AreEqual(1, d.Tables.Single().Mismatches);
        }

        [TestMethod]
        public void FunctionFoundInAnotherLibraryOfTheSameForm_IsCountedAsMismatch()
        {
            var d = new EnvironmentData();
            AddForm(d, "Main", Handler("onLoad", "a.js", "1") + Handler("helper", "b.js", "2"));
            Lib(d, "a.js", "function nothing(){}");
            Lib(d, "b.js", "function onLoad(c){}\nfunction helper(){}");
            AuditEngine.Run(d, new QualityOptions());
            var r = d.Registrations.Single(x => x.FunctionName == "onLoad");
            Assert.AreEqual(VerificationResults.FoundInOther, r.VerificationResult);
            Assert.AreEqual("b.js", r.FoundInLibrary);
            Assert.IsTrue(r.IsLibraryMismatch);
            Assert.AreEqual(1, d.Tables.Single().Mismatches);
        }

        [TestMethod]
        public void LibraryAttachedToFormButUsedByNoHandler_IsSearched()
        {
            var d = new EnvironmentData();
            AddForm(d, "Main", Handler("onLoad", "a.js", "1"), "<formLibraries><Library name='extra.js' libraryUniqueId='{9}' /></formLibraries>");
            Lib(d, "a.js", "function nothing(){}");
            Lib(d, "extra.js", "function onLoad(c){}");
            AuditEngine.Run(d, new QualityOptions());
            Assert.AreEqual(VerificationResults.FoundInOther, d.Registrations[0].VerificationResult);
            CollectionAssert.AreEqual(new[] { "extra.js" }, d.Forms[0].LibraryNames);
        }

        [TestMethod]
        public void LibraryOfADifferentForm_IsNotSearched()
        {
            var d = new EnvironmentData();
            AddForm(d, "Form A", Handler("onLoad", "a.js", "1"));
            AddForm(d, "Form B", Handler("keep", "b.js", "2"));   // b.js belongs to form B only
            Lib(d, "a.js", "function nothing(){}");
            Lib(d, "b.js", "function onLoad(c){}\nfunction keep(){}");
            AuditEngine.Run(d, new QualityOptions());
            var r = d.Registrations.Single(x => x.FunctionName == "onLoad");
            Assert.AreEqual(VerificationResults.NotFound, r.VerificationResult);
            Assert.IsTrue(r.IsLibraryMismatch);
        }

        [TestMethod]
        public void MismatchCount_CombinesNotFoundAndFoundElsewhere_WithoutDoubleCounting()
        {
            var d = new EnvironmentData();
            AddForm(d, "Main", Handler("missing", "a.js", "1") + Handler("elsewhere", "a.js", "2") + Handler("ok", "a.js", "3") + Handler("helper", "b.js", "4"));
            Lib(d, "a.js", "function ok(){}");
            Lib(d, "b.js", "function elsewhere(){}\nfunction helper(){}");
            AuditEngine.Run(d, new QualityOptions());
            Assert.AreEqual(2, d.Tables.Single().Mismatches);
            Assert.AreEqual(1, d.Registrations.Count(x => x.VerificationResult == VerificationResults.NotFound));
            Assert.AreEqual(1, d.Registrations.Count(x => x.VerificationResult == VerificationResults.FoundInOther));
        }

        [TestMethod]
        public void LibraryNamesToFetch_IncludesFormLibraries()
        {
            var regs = new[] { new HandlerRegistration { LibraryName = "a.js", FunctionName = "f" } };
            var names = ScanPlanner.LibraryNamesToFetch(regs, n => false, new[] { "extra.js", "A.js" });
            CollectionAssert.AreEqual(new[] { "a.js", "extra.js" }, names);
        }
    }
}
