// Compiled only where GK_QA_TESTS is defined (see QaFrameworkTests.cs).
#if GK_QA_TESTS
using GeorgianKnight.EditorTools.QA;
using NUnit.Framework;

namespace GeorgianKnight.Tests.QA
{
    /// <summary>The asset-intake rules of the Asset Quality Gate (Docs/AssetQA/QA_RULES.md): their pure logic and the
    /// defaults that used to let real defects pass (a validator that passes a defect is a bug).</summary>
    public sealed class AssetRuleTests
    {
        // a unit tetrahedron: closed, every edge shared by exactly two triangles
        static readonly float[] Tetra = { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        static readonly int[] TetraIdx = { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };

        [Test]
        public void Topology_ClosedMesh_HasNoOpenOrNonManifoldEdges()
        {
            var t = AssetMath.Topology(Tetra, TetraIdx, 1e-4f);
            Assert.AreEqual(4, t.Triangles);
            Assert.AreEqual(0, t.OpenEdges);
            Assert.AreEqual(0, t.NonManifoldEdges);
            Assert.AreEqual(0, t.LooseVertices);
        }

        [Test]
        public void Topology_SplitSeamVertices_AreWelded_NotCountedAsHoles()
        {
            // two triangles of a quad whose shared edge uses duplicated vertices (a UV seam): 4 boundary edges, not 6
            var xyz = new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 0, 0, 1, 0 };
            var t = AssetMath.Topology(xyz, new[] { 0, 1, 2, 3, 4, 5 }, 1e-4f);
            Assert.AreEqual(4, t.OpenEdges);
        }

        [Test]
        public void Topology_CountsNonManifoldAndLoose()
        {
            // three triangles on one edge (0-1), plus one vertex no triangle uses
            var xyz = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 1, 5, 5, 5 };
            var t = AssetMath.Topology(xyz, new[] { 0, 1, 2, 0, 1, 3, 0, 1, 4 }, 1e-4f);
            Assert.AreEqual(1, t.NonManifoldEdges);
            Assert.AreEqual(1, t.LooseVertices);
        }

        static readonly float[] Quad = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
        static readonly float[] QuadUv = { 0, 0, 1, 0, 1, 1, 0, 1 };
        static readonly int[] QuadIdx = { 0, 1, 2, 0, 2, 3 };

        [Test]
        public void TexelDensity_IsWidthTimesTilingOverMetres()
        {
            Assert.AreEqual(1024f, AssetMath.TexelDensity(Quad, QuadUv, QuadIdx, 1024f, 1f), 0.5f);
            var big = new float[Quad.Length];
            for (int i = 0; i < big.Length; i++) big[i] = Quad[i] * 2f;           // the same map over 2 × 2 m
            Assert.AreEqual(512f, AssetMath.TexelDensity(big, QuadUv, QuadIdx, 1024f, 1f), 0.5f);
            Assert.AreEqual(2048f, AssetMath.TexelDensity(Quad, QuadUv, QuadIdx, 1024f, 2f), 0.5f);
            Assert.AreEqual(0f, AssetMath.TexelDensity(new float[12], QuadUv, QuadIdx, 1024f, 1f));
        }

        [Test]
        public void TexelBand_AndTiledUvs()
        {
            float lo, hi;
            AssetMath.TexelBand(1024f, 1536f, 0.5f, out lo, out hi);
            Assert.AreEqual(512f, lo, 1e-3f);
            Assert.AreEqual(2304f, hi, 1e-3f);
            AssetMath.TexelBand(512f, 0f, 0.5f, out lo, out hi);
            Assert.AreEqual(float.MaxValue, hi);
            Assert.IsTrue(AssetMath.UvInsideUnit(QuadUv, QuadIdx, 0.01f));
            Assert.IsFalse(AssetMath.UvInsideUnit(new float[] { 0, 0, 4, 0, 4, 4, 0, 4 }, QuadIdx, 0.01f));
        }

        [TestCase("ArmingSword", "ArmingSword", "sword")]
        [TestCase("ArmingScabbard", "ArmingSword", "scabbard")]
        [TestCase("RoundShield", "RoundShield", "roundshield")]
        [TestCase("Shield_Round_02", "Props", "shieldround")]
        [TestCase("KiteShield", "KiteShield", "kiteshield")]
        [TestCase("Torch", "Props", "")]
        public void WeaponClass_FromFileOrFolderName(string file, string folder, string expected)
        {
            var c = AssetMath.WeaponClass(file, folder, new QaSettings().model.weaponClasses);
            Assert.AreEqual(expected, c == null ? "" : c.token);
        }

        [Test]
        public void WeaponClass_TheGatesMeasuredAssets()
        {
            var classes = new QaSettings().model.weaponClasses;
            var sword = AssetMath.WeaponClass("ArmingSword", "ArmingSword", classes);
            Assert.IsFalse(1.981f >= sword.min && 1.981f <= sword.max, "the 1.98 m arming sword must fail");
            Assert.IsTrue(0.961f >= sword.min && 0.961f <= sword.max, "Sword.fbx at 0.961 m passes");
            var shield = AssetMath.WeaponClass("RoundShield", "RoundShield", classes);
            Assert.IsTrue(0.722f >= shield.min && 0.722f <= shield.max);
            var kite = AssetMath.WeaponClass("KiteShield", "KiteShield", classes);
            Assert.IsFalse(4.199f <= kite.max);
        }

        [TestCase("House_Test01", true)]
        [TestCase("TEMP_Rock", true)]
        [TestCase("TreePlaceholder", true)]
        [TestCase("tmp_cube", true)]
        [TestCase("Test", true)]
        [TestCase("Contest", false)]
        [TestCase("Temple", false)]
        [TestCase("Attempt", false)]
        [TestCase("Testament", false)]
        [TestCase("Latest_Banner", false)]
        public void TestAssetNames(string name, bool expected)
        {
            Assert.AreEqual(expected, AssetMath.IsTestAssetName(name));
        }

        static byte[] Pixels(int n, byte r, byte g, byte b)
        {
            var d = new byte[n * 4];
            for (int i = 0; i < n; i++) { d[i * 4] = r; d[i * 4 + 1] = g; d[i * 4 + 2] = b; d[i * 4 + 3] = 255; }
            return d;
        }

        [Test]
        public void NormalBias_FlatMapPasses_TheShieldBackFails()
        {
            float r, g, b;
            AssetMath.ChannelMeans(Pixels(16, 128, 128, 255), out r, out g, out b);
            Assert.IsFalse(AssetMath.NormalBiased(r, g, b, 0.04f, 0.85f));
            AssetMath.ChannelMeans(Pixels(16, 107, 100, 250), out r, out g, out b);   // medieval_wood_Normal: 0.418 / 0.391
            Assert.IsTrue(AssetMath.NormalBiased(r, g, b, 0.04f, 0.85f));
            Assert.IsTrue(AssetMath.NormalBiased(0.5f, 0.5f, 0.6f, 0.04f, 0.85f), "low blue = not a tangent-space map");
        }

        [Test]
        public void BandShare_CountsGreysOnly()
        {
            var px = new byte[8 * 4];
            for (int i = 0; i < 4; i++) px[i * 4] = 128;          // 4 grey metal texels, 4 at 0
            Assert.AreEqual(0.5f, AssetMath.BandShare(px, 0, 0.15f, 0.85f), 1e-6f);
            Assert.AreEqual(0f, AssetMath.BandShare(Pixels(4, 255, 0, 0), 0, 0.15f, 0.85f), 1e-6f);
        }

        [Test]
        public void LodRatios()
        {
            string problem;
            Assert.IsTrue(AssetMath.LodRatiosOk(new long[] { 10000, 5000, 2500, 1000 }, new[] { 0.5f, 0.25f, 0.1f }, 0.1f, out problem));
            Assert.IsFalse(AssetMath.LodRatiosOk(new long[] { 10000, 9000 }, new[] { 0.5f, 0.25f, 0.1f }, 0.1f, out problem));
            StringAssert.Contains("LOD1", problem);
        }

        [Test]
        public void Lookups_SkySunAndSmoothnessClass()
        {
            var s = new QaSettings();
            float elevation;
            Assert.IsTrue(AssetMath.Lookup("kloofendal_48d_partly_cloudy_puresky_4k", s.scene.skySuns, out elevation));
            Assert.AreEqual(48f, elevation);
            Assert.IsFalse(AssetMath.Lookup("unknown_sky", s.scene.skySuns, out elevation));
            var wool = AssetMath.TokenClass("M_Wool_Coat", s.material.smoothnessClasses);
            Assert.IsNotNull(wool);
            Assert.AreEqual(0.25f, wool.max, 1e-6f);
            Assert.IsNull(AssetMath.TokenClass("M_Glass", s.material.smoothnessClasses));
        }

        [Test]
        public void Defaults_FollowQuality_NotTheOldBudgets()
        {
            var s = new QaSettings();
            CollectionAssert.DoesNotContain(s.model.heroNames, "Knight", "Knight.fbx is the enemy mesh");
            CollectionAssert.Contains(s.model.heroNames, "Bakur");
            Assert.AreEqual(120000, s.model.heroTriangles);
            Assert.AreEqual(60000, s.model.npcTriangles);
            Assert.IsTrue(0.9f > s.material.metallicLow && 0.9f < s.material.metallicHigh, "BannerIron's 0.9 metallic must be flagged");
            Assert.AreEqual(5000, s.prefab.lodTriangles);
        }

        [Test]
        public void TextureNaming_StrandsAndBaseMapAreAlbedo()
        {
            Assert.AreEqual(TextureRole.Albedo, TextureNaming.Classify("beard_strands.png"));
            Assert.AreEqual(TextureRole.Albedo, TextureNaming.Classify("Coat_BaseMap.png"));
        }
    }
}
#endif
