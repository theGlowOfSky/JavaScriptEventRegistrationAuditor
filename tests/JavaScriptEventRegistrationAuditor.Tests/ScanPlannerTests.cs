using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using JavaScriptEventRegistrationAuditor.Models;
using JavaScriptEventRegistrationAuditor.Services;

namespace JavaScriptEventRegistrationAuditor.Tests
{
    [TestClass]
    public class ScanPlannerTests
    {
        // ---- eligible vs excluded tables ----
        [TestMethod]
        public void Eligibility_KeepsStandardAndCustomTables()
        {
            Assert.IsTrue(TableSelection.IsEligible("account", false, false, false, true, true));
            Assert.IsTrue(TableSelection.IsEligible("contact", false, false, false, true, true));
            Assert.IsTrue(TableSelection.IsEligible("new_widget", false, false, false, true, true));
        }

        [TestMethod]
        public void Eligibility_ExcludesBackendTables()
        {
            Assert.IsFalse(TableSelection.IsEligible("accountleads", true, false, false, true, true), "intersect");
            Assert.IsFalse(TableSelection.IsEligible("internal", false, true, false, true, true), "private");
            Assert.IsFalse(TableSelection.IsEligible("logical", false, false, true, true, true), "logical entity");
            Assert.IsFalse(TableSelection.IsEligible("asyncoperation", false, false, false, false, false), "forms cannot be created");
            Assert.IsFalse(TableSelection.IsEligible("", false, false, false, true, true), "no name");
        }

        [TestMethod]
        public void Eligibility_MissingMetadataNeverHidesATableUnlessNotCustomizable()
        {
            Assert.IsTrue(TableSelection.IsEligible("t", null, null, null, null, null));
            Assert.IsFalse(TableSelection.IsEligible("t", null, null, null, null, false));
        }

        // ---- single / multiple selected tables ----
        [TestMethod]
        public void SingleSelectedTable_IsTheOnlyTableQueried()
        {
            var missing = ScanPlanner.MissingTables(new[] { "account" }, t => false);
            var chunks = TableSelection.Chunk(missing, 25);
            Assert.AreEqual(1, chunks.Count);
            CollectionAssert.AreEqual(new[] { "account" }, chunks[0]);
        }

        [TestMethod]
        public void MultipleSelectedTables_AreChunkedAndCachedTablesSkipped()
        {
            var selected = Enumerable.Range(1, 30).Select(i => "t" + i).Concat(new[] { "T1" }).ToList(); // duplicate differing in case
            var cached = new HashSet<string>(new[] { "t2" }, StringComparer.OrdinalIgnoreCase);
            var missing = ScanPlanner.MissingTables(selected, cached.Contains);
            Assert.AreEqual(29, missing.Count);
            Assert.IsFalse(missing.Contains("t2"));
            var chunks = TableSelection.Chunk(missing, 25);
            Assert.AreEqual(2, chunks.Count);
            Assert.AreEqual(29, chunks.SelectMany(c => c).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // ---- library scoping ----
        private static HandlerRegistration Reg(string lib, string fn, string result)
        {
            return new HandlerRegistration { LibraryName = lib, FunctionName = fn, VerificationResult = result };
        }

        [TestMethod]
        public void LibraryNames_AreDistinctCaseInsensitiveAndSkipKnownOrBlank()
        {
            var regs = new[] { Reg("A.js", "f", null), Reg("a.js", "g", null), Reg("b.js", "h", null), Reg("", "i", null), Reg(null, "j", null) };
            var names = ScanPlanner.LibraryNamesToFetch(regs, n => string.Equals(n, "b.js", StringComparison.OrdinalIgnoreCase));
            CollectionAssert.AreEqual(new[] { "A.js" }, names);
        }

        // ---- scanner regression guards for the look-behind removal ----
        [TestMethod]
        public void Scanner_FunctionDeclarationIsNotAlsoCountedAsMethod()
        {
            var defs = FunctionScanner.Index("x.js", "function foo(a) { return a; }\nfunction* gen() { }\nasync function  bar(){}");
            CollectionAssert.AreEqual(new[] { "foo", "gen", "bar" }, defs.Select(d => d.FullName).ToList());
        }

        [TestMethod]
        public void Scanner_StillFindsMethodShorthandAndSkipsControlFlow()
        {
            var defs = FunctionScanner.Index("x.js", "class A { static async onLoad(ctx) { if (ctx) { } } }\nvar o = { onSave(ctx) { for (;;) { } } };");
            CollectionAssert.AreEqual(new[] { "onLoad", "onSave" }, defs.Select(d => d.FullName).ToList());
            Assert.IsTrue(defs.All(d => d.Complete));
        }
    }
}
