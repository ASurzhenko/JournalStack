using System;
using System.Globalization;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JournalStack.Client.Tests
{
    public class EntryRulesTests
    {
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("\n\t ")]
        public void EmptyAfterTrimIsRefused(string raw)
        {
            Assert.IsFalse(EntryRules.TryClean(raw, out _, out string error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void NullIsRefused()
        {
            Assert.IsFalse(EntryRules.TryClean(null, out _, out _));
        }

        [Test]
        public void TextIsTrimmed()
        {
            Assert.IsTrue(EntryRules.TryClean("  hello \n", out string text, out _));
            Assert.AreEqual("hello", text);
        }

        [Test]
        public void Exactly2000IsAccepted()
        {
            Assert.IsTrue(EntryRules.TryClean(new string('a', 2000), out string text, out _));
            Assert.AreEqual(2000, text.Length);
        }

        [Test]
        public void Length2001IsRefused()
        {
            Assert.IsFalse(EntryRules.TryClean(new string('a', 2001), out _, out _));
        }

        [Test]
        public void LimitIsMeasuredAfterTrim()
        {
            Assert.IsTrue(EntryRules.TryClean(" " + new string('a', 2000) + " ", out _, out _));
        }

        [TestCase("a\u0000b")]
        [TestCase("a\u0007b")]
        [TestCase("a\u001bb")]
        [TestCase("a\u007fb")]
        public void ControlCharactersAreRefused(string raw)
        {
            Assert.IsFalse(EntryRules.TryClean(raw, out _, out _));
        }

        [Test]
        public void NewlineReturnAndTabAreAllowed()
        {
            Assert.IsTrue(EntryRules.TryClean("a\nb\rc\td", out string text, out _));
            Assert.AreEqual("a\nb\rc\td", text);
        }

        [Test]
        public void EmojiIsCountedInUtf16Units()
        {
            // One emoji is two units: 999 of them are 1998 units (accepted), 1001 are 2002 (refused).
            string emoji = "\U0001F600";
            Assert.AreEqual(2, emoji.Length);
            Assert.IsTrue(EntryRules.TryClean(string.Concat(System.Linq.Enumerable.Repeat(emoji, 999)), out _, out _));
            Assert.IsTrue(EntryRules.TryClean(string.Concat(System.Linq.Enumerable.Repeat(emoji, 1000)), out _, out _));
            Assert.IsFalse(EntryRules.TryClean(string.Concat(System.Linq.Enumerable.Repeat(emoji, 1001)), out _, out _));
        }

        [Test]
        public void EmojiPairStraddlingTheLimitIsRefusedAsWhole()
        {
            // 1999 letters + one emoji = 2001 units: refused, same as the server (it counts trimmed.Length).
            string raw = new string('a', 1999) + "\U0001F600";
            Assert.AreEqual(2001, raw.Length);
            Assert.IsFalse(EntryRules.TryClean(raw, out _, out _));
        }

        [Test]
        public void WireTimeHasTheFixedShapeAndParsesBackAsUtc()
        {
            string time = EntryRules.NewWireTime();
            Assert.IsTrue(Regex.IsMatch(time, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"), time);
            Assert.IsTrue(DateTime.TryParseExact(time, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed));
            Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);
            Assert.Less(Math.Abs((DateTime.UtcNow - parsed).TotalSeconds), 5);
        }
    }
}
