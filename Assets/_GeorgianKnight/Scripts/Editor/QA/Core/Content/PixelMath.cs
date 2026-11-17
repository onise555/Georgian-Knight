using System;

namespace GeorgianKnight.EditorTools.QA
{
    /// <summary>Exposure statistics of one rendered frame (all values 0..1, luminance on display-referred sRGB values).</summary>
    public struct ImageStats
    {
        public float MeanLuminance;
        public float ClippedFraction;     // any channel >= 250/255
        public float CrushedFraction;     // luma <= 5/255
        public float MeanSaturation;      // HSV saturation
        public float LuminanceStdDev;     // global contrast
        public int Pixels;
    }

    /// <summary>
    /// Pixel math shared by the material validator (average albedo) and qa.shots (exposure metrics). Operates on raw
    /// RGBA32 bytes so it is engine-free and unit-testable.
    /// </summary>
    public static class PixelMath
    {
        /// <summary>Rec. 709 luma of sRGB-encoded channel values (any scale).</summary>
        public static float Luma(float r, float g, float b) { return 0.2126f * r + 0.7152f * g + 0.0722f * b; }

        /// <summary>
        /// Alpha-weighted average colour (0..255 per channel) of an RGBA32 buffer; cut-out pixels (alpha 0) do not drag
        /// foliage/cloth averages toward black. Returns false when the buffer is empty or fully transparent.
        /// </summary>
        public static bool AverageColor(byte[] rgba, out float r, out float g, out float b)
        {
            r = g = b = 0f;
            if (rgba == null || rgba.Length < 4) return false;
            double sr = 0, sg = 0, sb = 0, sw = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                double w = rgba[i + 3] / 255.0;
                if (w <= 0) continue;
                sr += rgba[i] * w; sg += rgba[i + 1] * w; sb += rgba[i + 2] * w; sw += w;
            }
            if (sw <= 0) return false;
            r = (float)(sr / sw); g = (float)(sg / sw); b = (float)(sb / sw);
            return true;
        }

        public static float SrgbToLinear(float c) { return c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f); }

        public static float LinearToSrgb(float c) { return c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f; }

        /// <summary>
        /// Albedo luminance (0..255, sRGB-encoded) of a texture average (0..255 sRGB) tinted by the base colour (0..1 sRGB);
        /// the tint is applied in linear space as the shader does, then re-encoded for comparison with the PBR range.
        /// </summary>
        public static float AlbedoLuminance(float r, float g, float b, float tintR, float tintG, float tintB)
        {
            float lr = LinearToSrgb(SrgbToLinear(r / 255f) * SrgbToLinear(tintR));
            float lg = LinearToSrgb(SrgbToLinear(g / 255f) * SrgbToLinear(tintG));
            float lb = LinearToSrgb(SrgbToLinear(b / 255f) * SrgbToLinear(tintB));
            return Luma(lr, lg, lb) * 255f;
        }

        public static ImageStats Analyze(byte[] rgba)
        {
            var s = new ImageStats();
            if (rgba == null || rgba.Length < 4) return s;
            int n = rgba.Length / 4;
            double sum = 0, sumSq = 0, sat = 0;
            int clipped = 0, crushed = 0;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                float r = rgba[o] / 255f, g = rgba[o + 1] / 255f, b = rgba[o + 2] / 255f;
                float l = Luma(r, g, b);
                sum += l;
                sumSq += l * l;
                if (rgba[o] >= 250 || rgba[o + 1] >= 250 || rgba[o + 2] >= 250) clipped++;
                if (l <= 5f / 255f) crushed++;
                float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                sat += max <= 0f ? 0f : (max - min) / max;
            }
            double mean = sum / n;
            s.Pixels = n;
            s.MeanLuminance = (float)mean;
            s.LuminanceStdDev = (float)Math.Sqrt(Math.Max(0.0, sumSq / n - mean * mean));
            s.ClippedFraction = clipped / (float)n;
            s.CrushedFraction = crushed / (float)n;
            s.MeanSaturation = (float)(sat / n);
            return s;
        }

        /// <summary>The VIS.* rules a frame violates under the given limits (empty when the exposure is acceptable).</summary>
        public static System.Collections.Generic.List<QaRule> ExposureFlags(ImageStats s, QaSettings.VisualLimits lim)
        {
            var flags = new System.Collections.Generic.List<QaRule>();
            if (s.Pixels == 0) return flags;
            if (s.MeanLuminance < lim.meanLumMin) flags.Add(QaRules.VisUnderexposed);
            if (s.MeanLuminance > lim.meanLumMax) flags.Add(QaRules.VisOverexposed);
            if (s.ClippedFraction > lim.clippedMax) flags.Add(QaRules.VisClipped);
            if (s.CrushedFraction > lim.crushedMax) flags.Add(QaRules.VisCrushed);
            if (s.MeanSaturation > lim.saturationMax) flags.Add(QaRules.VisOversaturated);
            if (s.LuminanceStdDev < lim.contrastMin) flags.Add(QaRules.VisFlat);
            return flags;
        }

        /// <summary>
        /// Fraction (0..1) of the UV area covered by more than one triangle, rasterised on a grid over the UV bounding box
        /// (tiling UVs outside 0..1 are fine; stacked/mirrored islands are not). Large meshes are sampled up to
        /// <paramref name="maxTriangles"/>.
        /// </summary>
        public static float UvOverlap(float[] u, float[] v, int[] triangles, int grid, int maxTriangles)
        {
            if (u == null || v == null || triangles == null || triangles.Length < 3 || u.Length != v.Length || grid < 2) return 0f;
            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            for (int i = 0; i < u.Length; i++)
            {
                minU = Math.Min(minU, u[i]); maxU = Math.Max(maxU, u[i]);
                minV = Math.Min(minV, v[i]); maxV = Math.Max(maxV, v[i]);
            }
            float su = Math.Max(1e-6f, maxU - minU) / grid, sv = Math.Max(1e-6f, maxV - minV) / grid;
            var cover = new byte[grid * grid];
            int triCount = triangles.Length / 3;
            int step = Math.Max(1, triCount / Math.Max(1, maxTriangles));
            for (int t = 0; t < triCount; t += step)
            {
                int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                if (a >= u.Length || b >= u.Length || c >= u.Length) continue;
                Rasterize(cover, grid, (u[a] - minU) / su, (v[a] - minV) / sv, (u[b] - minU) / su, (v[b] - minV) / sv, (u[c] - minU) / su, (v[c] - minV) / sv);
            }
            int used = 0, multi = 0;
            foreach (var k in cover)
            {
                if (k > 0) used++;
                if (k > 1) multi++;
            }
            return used == 0 ? 0f : multi / (float)used;
        }

        /// <summary>Adds one coverage count to every grid cell whose centre lies strictly inside the triangle.</summary>
        static void Rasterize(byte[] cover, int grid, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (Math.Abs(area) < 1e-9f) return;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int x1 = Math.Min(grid - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
            int y1 = Math.Min(grid - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            float sign = area > 0 ? 1f : -1f;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    // strict test: samples exactly on a shared edge belong to neither triangle (no false overlap)
                    if (sign * Edge(ax, ay, bx, by, px, py) > 0 && sign * Edge(bx, by, cx, cy, px, py) > 0 && sign * Edge(cx, cy, ax, ay, px, py) > 0 && cover[y * grid + x] < 255)
                        cover[y * grid + x]++;
                }
        }

        static float Edge(float x0, float y0, float x1, float y1, float px, float py) { return (x1 - x0) * (py - y0) - (y1 - y0) * (px - x0); }
    }
}
