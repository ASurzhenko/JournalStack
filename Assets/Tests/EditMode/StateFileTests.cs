using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace JournalStack.Client.Tests
{
    public class StateFileTests
    {
        private string _dir;
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "js-state-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "state.json");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_dir, true);
        }

        private string[] BadCopies()
        {
            return Directory.GetFiles(_dir, "state.json.bad-*");
        }

        [Test]
        public void MissingFileIsAFreshInstall()
        {
            LocalState state = StateFile.Load(_path, out string warning);
            Assert.IsNull(warning);
            Assert.AreEqual(0, state.entries.Count);
        }

        [Test]
        public void RoundTripKeepsEveryField()
        {
            var state = new LocalState { deviceId = "d", token = "t" };
            state.entries.Add(new LocalEntry { id = "i", text = "line1\nline2 é", clientCreatedAt = "c", state = SyncState.Failed, reason = "r" });
            Assert.IsTrue(StateFile.Save(_path, state, out _));
            LocalState back = StateFile.Load(_path, out string warning);
            Assert.IsNull(warning);
            Assert.AreEqual("d", back.deviceId);
            Assert.AreEqual("t", back.token);
            Assert.AreEqual("line1\nline2 é", back.entries[0].text);
            Assert.AreEqual(SyncState.Failed, back.entries[0].state);
            Assert.AreEqual("r", back.entries[0].reason);
        }

        [Test]
        public void SaveOverAnExistingFileReplacesIt()
        {
            var state = new LocalState();
            Assert.IsTrue(StateFile.Save(_path, state, out _));
            state.deviceId = "second";
            Assert.IsTrue(StateFile.Save(_path, state, out _));
            Assert.AreEqual("second", StateFile.Load(_path, out _).deviceId);
            Assert.IsFalse(File.Exists(_path + ".tmp"));
        }

        [Test]
        public void ARealCapturedStateFileParsesWithValues()
        {
            // Bytes written by the client itself during a run against the local stack (see Fixtures.cs).
            File.WriteAllText(_path, Fixtures.RealState);
            LocalState state = StateFile.Load(_path, out string warning);
            Assert.IsNull(warning);
            Assert.Greater(state.entries.Count, 0);
            Assert.AreEqual(43, state.token.Length);
            Assert.IsNotEmpty(state.deviceId);
            Assert.IsNotEmpty(state.entries[0].text);
            Assert.IsNotEmpty(state.entries[0].clientCreatedAt);
            Assert.AreEqual(0, BadCopies().Length);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("{\"deviceId\":\"d\",\"token\":\"t\",\"entri")]
        [TestCase("{}")]
        [TestCase("not json at all")]
        public void AnUnreadableFileIsKeptAndTheAppStartsEmpty(string content)
        {
            File.WriteAllText(_path, content);
            LogAssert.ignoreFailingMessages = true;
            LocalState state = StateFile.Load(_path, out string warning);
            LogAssert.ignoreFailingMessages = false;
            Assert.IsNotNull(warning);
            Assert.AreEqual(0, state.entries.Count);
            Assert.IsTrue(File.Exists(_path), "the original must stay");
            Assert.AreEqual(1, BadCopies().Length);
            Assert.AreEqual(content, File.ReadAllText(BadCopies()[0]));
        }

        [Test]
        public void ASecondCorruptionMakesASecondCopy()
        {
            LogAssert.ignoreFailingMessages = true;
            File.WriteAllText(_path, "{}");
            StateFile.Load(_path, out _);
            File.WriteAllText(_path, "{\"x\":1}");
            StateFile.Load(_path, out _);
            LogAssert.ignoreFailingMessages = false;
            Assert.AreEqual(2, BadCopies().Distinct().Count());
        }

        [Test]
        public void SaveIntoAMissingFolderFailsWithAnError()
        {
            Assert.IsFalse(StateFile.Save(Path.Combine(_dir, "nope", "state.json"), new LocalState(), out string error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void JsonUtilityLeavesAnAbsentListNonNullSoTheKeyCheckIsWhatCatchesAWrongShape()
        {
            LocalState parsed = JsonUtility.FromJson<LocalState>("{\"deviceId\":\"d\"}");
            Assert.IsNotNull(parsed.entries);
            Assert.AreEqual(0, parsed.entries.Count);
        }
    }
}
