using System.IO;
using GeorgianKnight.Core;
using NUnit.Framework;
using UnityEngine;

namespace GeorgianKnight.Tests.Core
{
    /// <summary>
    /// GK-25: the player's vSync and frame cap must survive the quality level, which carries its own vSync. Apply changes
    /// the editor's quality settings (in the editor they are the project's QualitySettings.asset), so every level's vSync,
    /// the active level, the frame cap and the listener volume are put back after each test. In Play the same is done by
    /// EditorQualityGuard, which the second test checks (its Play hooks themselves need a Play round trip).
    /// </summary>
    public sealed class SettingsServiceTests
    {
        int level, frameRate;
        int[] levelVSync;
        float volume;
        string file;

        [SetUp]
        public void Save()
        {
            level = QualitySettings.GetQualityLevel();
            levelVSync = new int[QualitySettings.names.Length];
            for (int i = 0; i < levelVSync.Length; i++) { QualitySettings.SetQualityLevel(i, false); levelVSync[i] = QualitySettings.vSyncCount; }
            QualitySettings.SetQualityLevel(level, false);
            frameRate = Application.targetFrameRate;
            volume = AudioListener.volume;
            file = Path.Combine(Path.GetTempPath(), "gk_settings_test_" + System.Guid.NewGuid().ToString("N") + ".json");
        }

        [TearDown]
        public void Restore()
        {
            for (int i = 0; i < levelVSync.Length; i++) { QualitySettings.SetQualityLevel(i, false); QualitySettings.vSyncCount = levelVSync[i]; }
            QualitySettings.SetQualityLevel(level, false);
            Application.targetFrameRate = frameRate;
            AudioListener.volume = volume;
            if (File.Exists(file)) File.Delete(file);
        }

        [Test]
        public void Apply_KeepsThePlayersVSyncAndFrameCap_OnEveryQualityLevel([Values(false, true)] bool vSync)
        {
            Assert.Greater(QualitySettings.names.Length, 0, "no quality levels");
            var service = new SettingsService(null, file);
            foreach (QualityPreset preset in System.Enum.GetValues(typeof(QualityPreset)))
            {
                var s = service.CreateDefaults();
                s.quality = preset;
                s.vSync = vSync;
                s.frameRateCap = 90;
                s.language = Loc.Language ?? s.language;
                service.Commit(s);

                int expectedLevel = Mathf.Min((int)preset, QualitySettings.names.Length - 1);
                Assert.AreEqual(expectedLevel, QualitySettings.GetQualityLevel(), preset + ": quality level");
                Assert.AreEqual(vSync ? 1 : 0, QualitySettings.vSyncCount, preset + ": vSyncCount");
                Assert.AreEqual(vSync ? -1 : 90, Application.targetFrameRate, preset + ": targetFrameRate");
            }
        }

        [Test]
        public void EditorQualityGuard_PutsBackWhatApplyWroteIntoTheProject()
        {
            string before = EditorQualityGuard.Snapshot();
            bool opposite = QualitySettings.vSyncCount == 0;   // the opposite of what the project has
            var service = new SettingsService(null, file);
            foreach (QualityPreset preset in System.Enum.GetValues(typeof(QualityPreset)))
            {
                var s = service.CreateDefaults();
                s.quality = preset;
                s.vSync = opposite;
                s.language = Loc.Language ?? s.language;
                service.Commit(s);
            }
            Assert.AreNotEqual(before, EditorQualityGuard.Snapshot(), "Apply changed nothing, so this test proves nothing");
            EditorQualityGuard.Restore(before);
            Assert.AreEqual(before, EditorQualityGuard.Snapshot());
        }
    }
}
