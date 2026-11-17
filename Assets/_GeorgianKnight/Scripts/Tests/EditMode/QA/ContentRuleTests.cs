// Compiled only where GK_QA_TESTS is defined (see QaFrameworkTests.cs).
#if GK_QA_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using GeorgianKnight.EditorTools.QA;
using NUnit.Framework;

namespace GeorgianKnight.Tests.QA
{
    public sealed class PixelMathTests
    {
        static byte[] Fill(int pixels, byte r, byte g, byte b, byte a = 255)
        {
            var d = new byte[pixels * 4];
            for (int i = 0; i < pixels; i++) { d[i * 4] = r; d[i * 4 + 1] = g; d[i * 4 + 2] = b; d[i * 4 + 3] = a; }
            return d;
        }

        [Test]
        public void Black_IsCrushed_White_IsClipped()
        {
            var black = PixelMath.Analyze(Fill(100, 0, 0, 0));
            var white = PixelMath.Analyze(Fill(100, 255, 255, 255));
            Assert.AreEqual(0f, black.MeanLuminance, 1e-6f);
            Assert.AreEqual(1f, black.CrushedFraction, 1e-6f);
            Assert.AreEqual(1f, white.ClippedFraction, 1e-6f);
            Assert.AreEqual(1f, white.MeanLuminance, 1e-4f);
        }

        [Test]
        public void Saturation_And_Contrast()
        {
            var red = PixelMath.Analyze(Fill(10, 255, 0, 0));
            Assert.AreEqual(1f, red.MeanSaturation, 1e-6f);
            var half = new byte[8];
            half[3] = 255; half[4] = 255; half[5] = 255; half[6] = 255; half[7] = 255;   // one black, one white pixel
            Assert.AreEqual(0.5f, PixelMath.Analyze(half).LuminanceStdDev, 1e-3f);
        }

        [Test]
        public void ExposureFlags_FollowTheLimits()
        {
            var lim = new QaSettings.VisualLimits();
            var dark = PixelMath.ExposureFlags(PixelMath.Analyze(Fill(100, 10, 10, 10)), lim);
            CollectionAssert.Contains(dark, QaRules.VisUnderexposed);
            CollectionAssert.Contains(dark, QaRules.VisFlat);
            var mid = new byte[400];
            for (int i = 0; i < 100; i++) { byte v = (byte)(i < 50 ? 40 : 200); mid[i * 4] = v; mid[i * 4 + 1] = v; mid[i * 4 + 2] = (byte)(v * 0.9f); mid[i * 4 + 3] = 255; }
            CollectionAssert.IsEmpty(PixelMath.ExposureFlags(PixelMath.Analyze(mid), lim));
        }

        [Test]
        public void AverageColor_IgnoresTransparentPixels()
        {
            var d = new byte[8];
            d[0] = 200; d[1] = 100; d[2] = 50; d[3] = 255;      // opaque
            d[4] = 0; d[5] = 0; d[6] = 0; d[7] = 0;              // fully transparent black
            float r, g, b;
            Assert.IsTrue(PixelMath.AverageColor(d, out r, out g, out b));
            Assert.AreEqual(200f, r, 1e-3f);
            Assert.AreEqual(100f, g, 1e-3f);
        }

        [Test]
        public void AlbedoLuminance_WhiteTint_IsIdentity_AndTintDarkens()
        {
            Assert.AreEqual(128f, PixelMath.AlbedoLuminance(128, 128, 128, 1f, 1f, 1f), 0.5f);
            Assert.Less(PixelMath.AlbedoLuminance(128, 128, 128, 0.5f, 0.5f, 0.5f), 70f);
        }

        [Test]
        public void UvOverlap_SeparateIslands_None_StackedIslands_Full()
        {
            // two unit quads side by side (no overlap), then the same quad twice (full overlap)
            float[] u = { 0, 1, 1, 0, 1, 2, 2, 1 }, v = { 0, 0, 1, 1, 0, 0, 1, 1 };
            int[] side = { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            Assert.AreEqual(0f, PixelMath.UvOverlap(u, v, side, 64, 1000), 0.01f);
            int[] stacked = { 0, 1, 2, 0, 2, 3, 0, 1, 2, 0, 2, 3 };
            float[] u2 = { 0, 1, 1, 0 }, v2 = { 0, 0, 1, 1 };
            Assert.AreEqual(1f, PixelMath.UvOverlap(u2, v2, stacked, 64, 1000), 0.01f);
        }
    }

    public sealed class AudioMetricsTests
    {
        static float[] Sine(int rate, float seconds, float amp, float leadSilence)
        {
            int lead = (int)(rate * leadSilence), n = lead + (int)(rate * seconds);
            var s = new float[n];
            for (int i = lead; i < n; i++) s[i] = amp * (float)Math.Sin(2 * Math.PI * 440 * i / rate);
            return s;
        }

        [Test]
        public void Measures_LeadingSilence_Peak_AndRms()
        {
            var st = AudioMetrics.Analyze(Sine(48000, 0.5f, 0.5f, 0.1f), 1, 48000, -50f);
            Assert.AreEqual(100f, st.LeadingSilenceMs, 1f);
            Assert.AreEqual(-6.02f, st.PeakDb, 0.1f);
            Assert.Less(st.RmsDb, st.PeakDb);
            Assert.AreEqual(0, st.ClippedRuns);
        }

        [Test]
        public void Detects_Clipping_And_DcOffset()
        {
            var s = new float[1000];
            for (int i = 0; i < s.Length; i++) s[i] = i % 100 < 10 ? 1f : 0.2f;
            var st = AudioMetrics.Analyze(s, 1, 48000, -50f);
            Assert.AreEqual(10, st.ClippedRuns);
            Assert.Greater(st.DcOffset, 0.2f);
        }

        [Test]
        public void Reads_16bit_Pcm_Wav()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            short[] data = { 0, 16384, -16384, 32767 };
            w.Write("RIFF".ToCharArray()); w.Write(36 + data.Length * 2); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(data.Length * 2);
            foreach (var d in data) w.Write(d);
            float[] samples;
            int ch, rate;
            Assert.IsTrue(AudioMetrics.TryReadWav(ms.ToArray(), out samples, out ch, out rate));
            Assert.AreEqual(1, ch);
            Assert.AreEqual(44100, rate);
            Assert.AreEqual(4, samples.Length);
            Assert.AreEqual(0.5f, samples[1], 1e-4f);
            Assert.AreEqual(-0.5f, samples[2], 1e-4f);
        }
    }

    public sealed class AssetNamingTests
    {
        [TestCase("rock_wall_14_diff_2k.jpg", TextureRole.Albedo)]
        [TestCase("rock_wall_14_nor_gl.jpg", TextureRole.Normal)]
        [TestCase("rock_wall_14_arm.jpg", TextureRole.Data)]
        [TestCase("Knight_Body_BaseColor.png", TextureRole.Albedo)]
        [TestCase("Knight_Body_Normal.png", TextureRole.Normal)]
        [TestCase("Knight_Body_MetalSmooth.png", TextureRole.Data)]
        [TestCase("Sword_Roughness.png", TextureRole.Data)]
        [TestCase("Torch_Emission.png", TextureRole.Emission)]
        [TestCase("KnightBodyNormal.png", TextureRole.Normal)]
        [TestCase("georgian_banner.png", TextureRole.Unknown)]
        public void TextureRoles(string file, TextureRole expected)
        {
            Assert.AreEqual(expected, TextureNaming.Classify(file));
        }

        [Test]
        public void Stem_MatchesMaterialAndTextures()
        {
            Assert.AreEqual("rock_wall_14", TextureNaming.Stem("M_Rock_Wall_14.mat"));
            Assert.AreEqual("rock_wall_14", TextureNaming.Stem("rock_wall_14_diff_2k.jpg"));
        }

        [TestCase("AX_standing_idle", ClipLoopKind.Loop)]
        [TestCase("SS_Sword_And_Shield_Walk", ClipLoopKind.Loop)]
        [TestCase("GK_Slash_Outward", ClipLoopKind.OneShot)]
        [TestCase("AX_standing_block_react_large", ClipLoopKind.OneShot)]
        [TestCase("SS_Block_Idle", ClipLoopKind.Loop)]
        [TestCase("GK_Idle_To_Run", ClipLoopKind.Unknown)]
        public void ClipLoopExpectations(string name, ClipLoopKind expected)
        {
            Assert.AreEqual(expected, ClipRules.Classify(name));
        }

        [Test]
        public void RootMotionOutliers_UseStrictMajority()
        {
            string a = ClipRules.Signature(true, true, false, true, true, false), b = ClipRules.Signature(false, true, false, true, true, false);
            var items = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("1", a), new KeyValuePair<string, string>("2", a), new KeyValuePair<string, string>("3", b)
            };
            string majority;
            var outliers = ClipRules.Outliers(items, out majority);
            Assert.AreEqual(1, outliers.Count);
            Assert.AreEqual("3", outliers[0].Key);
            StringAssert.Contains("bakeRotation=False (family: bakeRotation=True)", ClipRules.Describe(b, majority));
            items.Add(new KeyValuePair<string, string>("4", b));
            Assert.AreEqual(0, ClipRules.Outliers(items, out majority).Count, "a 2/2 split has no majority");
        }
    }

    public sealed class CombatRulesTests
    {
        static AttackNumbers Light()
        {
            return new AttackNumbers
            {
                Kind = "Light", HitStart = 0.35f, HitEnd = 0.5f, ComboWindow = 0.55f, DodgeCancel = 0.6f, End = 0.9f, SteerUntil = 0.3f,
                Damage = 18f, PoiseDamage = 18f, StaminaCost = 10f, ClipSeconds = 1.2f
            };
        }

        static List<string> Rules(List<CombatFinding> f)
        {
            var l = new List<string>();
            foreach (var x in f) l.Add(x.Rule.Id);
            return l;
        }

        [Test]
        public void DefaultLightAttack_IsClean()
        {
            CollectionAssert.IsEmpty(CombatRules.CheckAttack(Light(), new QaSettings.CombatLimits()));
        }

        [Test]
        public void TimingOutOfOrder_IsCritical()
        {
            var a = Light();
            a.HitEnd = 0.6f;   // after comboWindow
            var f = CombatRules.CheckAttack(a, new QaSettings.CombatLimits());
            CollectionAssert.Contains(Rules(f), "DATA.ATTACK_TIMING");
            Assert.AreEqual(QaSeverity.Critical, f[0].Severity);
        }

        [Test]
        public void FastUnblockable_LacksTelegraph()
        {
            var a = Light();
            a.Kind = "Heavy"; a.Damage = 30f; a.Unblockable = true; a.HitStart = 0.2f; a.SteerUntil = 0.2f; a.ClipSeconds = 1.0f;   // 200 ms wind-up
            CollectionAssert.Contains(Rules(CombatRules.CheckAttack(a, new QaSettings.CombatLimits())), "DATA.UNBLOCKABLE_TELEGRAPH");
        }

        [Test]
        public void Damage_OutsideEnvelope_IsFlagged_ExecutionsAreExempt()
        {
            var a = Light();
            a.Damage = 120f;
            CollectionAssert.Contains(Rules(CombatRules.CheckAttack(a, new QaSettings.CombatLimits())), "DATA.ATTACK_VALUES");
            a.Kind = "Execution"; a.StaminaCost = 0f; a.PoiseDamage = 999f; a.Damage = 999f;
            CollectionAssert.DoesNotContain(Rules(CombatRules.CheckAttack(a, new QaSettings.CombatLimits())), "DATA.ATTACK_VALUES");
        }

        [Test]
        public void Dodge_WithoutIFrames_AndInvertedCancel()
        {
            CollectionAssert.Contains(Rules(CombatRules.CheckAction("dodge.front", 0.8f, 0.6f, 0f, 0f, true)), "DATA.DODGE_IFRAMES");
            CollectionAssert.Contains(Rules(CombatRules.CheckAction("stagger", 0.7f, 0.9f, 0f, 0f, false)), "DATA.ACTION_CLIP");
            CollectionAssert.IsEmpty(CombatRules.CheckAction("dodge.back", 0.82f, 0.6f, 0.05f, 0.5f, true));
        }
    }

    public sealed class LocTextTests
    {
        [TestCase("რაინდის ხმალი", true)]
        [TestCase("Knight's sword", false)]
        [TestCase("ᲠᲐᲘᲜᲓᲘ", true)]
        public void Georgian_Detection(string s, bool expected) { Assert.AreEqual(expected, LocText.HasGeorgian(s)); }

        [TestCase("áƒ áƒáƒ˜áƒœáƒ“áƒ˜", true)]
        [TestCase("???? ????", true)]
        [TestCase("რაინდი�", true)]
        [TestCase("რაინდი?", false)]
        public void Mojibake_Detection(string s, bool expected) { Assert.AreEqual(expected, LocText.IsMojibake(s)); }

        [TestCase("Lorem ipsum dolor", true)]
        [TestCase("TODO", true)]
        [TestCase("item1", true)]
        [TestCase("ტესტი", true)]
        [TestCase("A test of courage", false)]
        [TestCase("Name", false)]
        public void Placeholder_Detection(string s, bool expected) { Assert.AreEqual(expected, LocText.IsPlaceholder(s)); }

        [Test]
        public void FormatPlaceholders_MustMatchAcrossLanguages()
        {
            Assert.IsTrue(LocText.SameFormat("{0} ვერცხლი {1}", "{1} silver for {0:N0}"));
            Assert.IsFalse(LocText.SameFormat("{0} ვერცხლი", "{0} silver for {1}"));
        }

        [Test]
        public void MixedScript_IgnoresGlyphsAndPlaceholders()
        {
            CollectionAssert.IsEmpty(LocText.ForeignLatinWords("დააჭირე [E] ან LMB {0}-ისთვის <b>ახლა</b>"));
            CollectionAssert.AreEqual(new[] { "sword" }, LocText.ForeignLatinWords("რაინდის sword"));
        }

        [Test]
        public void Typography_FlagsStraightQuotesInGeorgian()
        {
            CollectionAssert.Contains(LocText.Typography("თქვა \"კარგი\"", true), "straight quotes (use „…“)");
            CollectionAssert.Contains(LocText.Typography("Wait...", false), "'...' instead of '…'");
            CollectionAssert.IsEmpty(LocText.Typography("„კარგი“ — თქვა მან.", true));
        }
    }
}
#endif
