using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeorgianKnight.EditorTools
{
    /// <summary>
    /// Real elevation data for the Didgori battlefield (open AWS Terrain Tiles, "terrarium" encoding,
    /// SRTM-derived). World +X = east, +Z = north, origin = the arena centre on Mount Didgori (+ Offset).
    /// </summary>
    public static class GKDem
    {
        public const double Lat0 = 41.7606;   // Didgori battle memorial / Mount Didgori summit
        public const double Lon0 = 44.5081;
        const double MetersPerDegLat = 111320.0;

        /// <summary>Metres east (x) / north (y) of Lat0/Lon0 that become the world origin; zero for Didgori.unity.
        /// A level re-centred elsewhere on the field (Level_Didgori_Arena) sets it before Prepare.</summary>
        public static Vector2 Offset;

        static readonly Dictionary<string, Color32[]> tiles = new Dictionary<string, Color32[]>();
        static readonly System.Reflection.MethodInfo LoadImage =
            typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });

        static string CacheDir { get { return Path.Combine(Path.GetDirectoryName(Application.dataPath), "GKCache", "dem"); } }

        public static void LatLon(float x, float z, out double lat, out double lon)
        {
            lat = Lat0 + ((double)z + Offset.y) / MetersPerDegLat;
            lon = Lon0 + ((double)x + Offset.x) / (MetersPerDegLat * Math.Cos(Lat0 * Math.PI / 180.0));
        }

        static void TileXY(int zoom, double lat, double lon, out double tx, out double ty)
        {
            double n = Math.Pow(2, zoom);
            tx = (lon + 180.0) / 360.0 * n;
            double lr = lat * Math.PI / 180.0;
            ty = (1.0 - Math.Log(Math.Tan(lr) + 1.0 / Math.Cos(lr)) / Math.PI) / 2.0 * n;
        }

        class Raster { public int gx0, gy0, w, h; public float[] e; }
        static readonly Dictionary<int, Raster> rasters = new Dictionary<int, Raster>();

        /// <summary>Downloads every tile covering a square of the given half-size (metres) and builds a raster.</summary>
        public static bool Prepare(int zoom, float halfSize)
        {
            double latA, lonA, latB, lonB;
            LatLon(-halfSize, -halfSize, out latA, out lonA);
            LatLon(halfSize, halfSize, out latB, out lonB);
            double x0, y0, x1, y1;
            TileXY(zoom, latB, lonA, out x0, out y0); // north-west
            TileXY(zoom, latA, lonB, out x1, out y1); // south-east
            Directory.CreateDirectory(CacheDir);
            int tx0 = (int)Math.Floor(x0) , ty0 = (int)Math.Floor(y0), tx1 = (int)Math.Floor(x1), ty1 = (int)Math.Floor(y1);
            var r = new Raster { gx0 = tx0 * 256, gy0 = ty0 * 256, w = (tx1 - tx0 + 1) * 256, h = (ty1 - ty0 + 1) * 256 };
            r.e = new float[r.w * r.h];
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    var px = Tile(zoom, tx, ty);
                    if (px == null) return false;
                    for (int ly = 0; ly < 256; ly++)
                        for (int lx = 0; lx < 256; lx++)
                        {
                            var c = px[(255 - ly) * 256 + lx];
                            r.e[((ty - ty0) * 256 + ly) * r.w + (tx - tx0) * 256 + lx] = (c.r * 256f + c.g + c.b / 256f) - 32768f;
                        }
                }
            rasters[zoom] = r;
            return true;
        }

        static Color32[] Tile(int z, int x, int y)
        {
            string key = z + "/" + x + "/" + y;
            Color32[] px;
            if (tiles.TryGetValue(key, out px)) return px;
            string file = Path.Combine(CacheDir, "z" + z + "_" + x + "_" + y + ".png");
            if (!File.Exists(file))
            {
                string url = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/" + z + "/" + x + "/" + y + ".png";
                using (var req = UnityEngine.Networking.UnityWebRequest.Get(url))
                {
                    req.timeout = 60;
                    var op = req.SendWebRequest();
                    while (!op.isDone) { EditorUtility.DisplayProgressBar("Georgian Knight", "Downloading Didgori elevation " + key, req.downloadProgress); System.Threading.Thread.Sleep(10); }
                    if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success) { GKBridge.Log("DEM tile failed " + url + " " + req.error); tiles[key] = null; return null; }
                    File.WriteAllBytes(file, req.downloadHandler.data);
                }
            }
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            LoadImage.Invoke(null, new object[] { t, File.ReadAllBytes(file), false });
            px = t.GetPixels32();
            UnityEngine.Object.DestroyImmediate(t);
            tiles[key] = px;
            return px;
        }

        static float R(Raster r, int gx, int gy)
        {
            int x = Mathf.Clamp(gx - r.gx0, 0, r.w - 1), y = Mathf.Clamp(gy - r.gy0, 0, r.h - 1);
            return r.e[y * r.w + x];
        }

        static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        {
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t * t + (-p0 + 3f * p1 - 3f * p2 + p3) * t * t * t);
        }

        /// <summary>Elevation in metres (bicubic) at world x/z. Prepare(zoom, ...) must have been called.</summary>
        public static float Elevation(int zoom, float x, float z)
        {
            Raster r;
            if (!rasters.TryGetValue(zoom, out r)) return float.NaN;
            double lat, lon, tx, ty;
            LatLon(x, z, out lat, out lon);
            TileXY(zoom, lat, lon, out tx, out ty);
            double gx = tx * 256.0 - 0.5, gy = ty * 256.0 - 0.5;
            int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
            float fx = (float)(gx - ix), fy = (float)(gy - iy);
            float c0 = CatmullRom(R(r, ix - 1, iy - 1), R(r, ix, iy - 1), R(r, ix + 1, iy - 1), R(r, ix + 2, iy - 1), fx);
            float c1 = CatmullRom(R(r, ix - 1, iy), R(r, ix, iy), R(r, ix + 1, iy), R(r, ix + 2, iy), fx);
            float c2 = CatmullRom(R(r, ix - 1, iy + 1), R(r, ix, iy + 1), R(r, ix + 1, iy + 1), R(r, ix + 2, iy + 1), fx);
            float c3 = CatmullRom(R(r, ix - 1, iy + 2), R(r, ix, iy + 2), R(r, ix + 1, iy + 2), R(r, ix + 2, iy + 2), fx);
            return CatmullRom(c0, c1, c2, c3, fy);
        }
    }
}
