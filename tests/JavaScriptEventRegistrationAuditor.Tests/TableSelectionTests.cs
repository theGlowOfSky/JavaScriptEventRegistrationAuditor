using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using JavaScriptEventRegistrationAuditor.Models;
using JavaScriptEventRegistrationAuditor.Services;

namespace JavaScriptEventRegistrationAuditor.Tests
{
    [TestClass]
    public class TableSelectionTests
    {
        private static List<TableRow> Rows()
        {
            return new List<TableRow>
            {
                new TableRow { LogicalName = "account", DisplayName = "Account" },
                new TableRow { LogicalName = "contact", DisplayName = "Contact" },
                new TableRow { LogicalName = "new_invoice", DisplayName = "Customer Invoice" }
            };
        }

        [TestMethod]
        public void Filter_MatchesLogicalOrDisplayName_CaseInsensitive()
        {
            var rows = Rows();
            CollectionAssert.AreEqual(new[] { "contact" }, TableSelection.Filter(rows, "CONTACT").Select(r => r.LogicalName).ToList());
            CollectionAssert.AreEqual(new[] { "new_invoice" }, TableSelection.Filter(rows, "customer").Select(r => r.LogicalName).ToList());
            Assert.AreEqual(3, TableSelection.Filter(rows, "  ").Count);
            Assert.AreEqual(0, TableSelection.Filter(rows, "zzz").Count);
        }

        [TestMethod]
        public void Filter_DoesNotLoseSelectionOfHiddenRows()
        {
            var rows = Rows();
            rows[0].Selected = true;                       // account selected
            TableSelection.Filter(rows, "contact");        // account is hidden by the filter
            TableSelection.Filter(rows, "");               // filter cleared again
            Assert.IsTrue(rows[0].Selected);
            Assert.AreEqual(1, rows.Count(r => r.Selected));
        }

        [TestMethod]
        public void Unscanned_ReportsSelectedTablesWithoutLoadedRegistrations()
        {
            var scanned = new HashSet<string>(new[] { "account" }, System.StringComparer.OrdinalIgnoreCase);
            var pending = TableSelection.Unscanned(new[] { "ACCOUNT", "contact" }, scanned);
            CollectionAssert.AreEqual(new[] { "contact" }, pending);
            Assert.AreEqual(0, TableSelection.Unscanned(new[] { "account" }, scanned).Count);
            Assert.AreEqual(1, TableSelection.Unscanned(new[] { "account" }, null).Count);
        }

        [TestMethod]
        public void Chunk_SplitsInOrderWithoutLoss()
        {
            var chunks = TableSelection.Chunk(new[] { "a", "b", "c", "d", "e" }, 2);
            Assert.AreEqual(3, chunks.Count);
            CollectionAssert.AreEqual(new[] { "a", "b", "c", "d", "e" }, chunks.SelectMany(c => c).ToList());
            Assert.AreEqual(1, chunks[2].Count);
            Assert.AreEqual(0, TableSelection.Chunk(new string[0], 25).Count);
        }

        [TestMethod]
        public void RowMessage_InstructsUntilLoadedThenSummarises()
        {
            Assert.AreEqual("Click View to see registrations.", TableSelection.RowMessage(null, null, false));
            Assert.AreEqual("1 registration", TableSelection.RowMessage(1, 0, false));
            Assert.AreEqual("0 registrations", TableSelection.RowMessage(0, 0, false));
            Assert.AreEqual("5 registrations, 2 mismatches", TableSelection.RowMessage(5, 2, false));
            Assert.AreEqual("3 registrations, 1 mismatch", TableSelection.RowMessage(3, 1, false));
            Assert.AreEqual("Could not load registrations.", TableSelection.RowMessage(null, null, true));
        }
    }
}
