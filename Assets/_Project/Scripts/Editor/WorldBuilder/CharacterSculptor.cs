using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Sculpts smooth character parts from simple shapes, the way you'd model in clay:
    /// ellipsoids and tapered capsules ("round cones") are blended together with a smooth
    /// union (signed distance fields), then turned into a mesh with Surface Nets.
    /// Normals come straight from the distance field, so the result is smooth without a
    /// modelling package. UVs wrap around a chosen axis (metres along it, angle around it),
    /// which is what the generated fur textures (tiger stripes etc.) expect.
    /// </summary>
    public class Sculpt
    {
        struct Shape { public bool cone; public Vector3 a, b, rad; public float r1, r2, k; }

        readonly List<Shape> shapes = new List<Shape>();
        Vector3 axisFrom = Vector3.zero, axisTo = Vector3.forward, up = Vector3.up;

        /// <summary>Ellipsoid centred at c with radii r; k = how softly it blends into what's there already.</summary>
        public Sculpt Ell(Vector3 c, Vector3 r, float k = 0.05f) { shapes.Add(new Shape { a = c, rad = r, k = k }); return this; }

        /// <summary>Tapered capsule from a (radius r1) to b (radius r2).</summary>
        public Sculpt Cone(Vector3 a, Vector3 b, float r1, float r2, float k = 0.05f)
        { shapes.Add(new Shape { cone = true, a = a, b = b, r1 = r1, r2 = r2, k = k }); return this; }

        /// <summary>A bendy limb/tail/trunk: tapered capsules through the points.</summary>
        public Sculpt Chain(float k, params (Vector3 p, float r)[] pts)
        {
            for (int i = 0; i < pts.Length - 1; i++) Cone(pts[i].p, pts[i + 1].p, pts[i].r, pts[i + 1].r, k);
            return this;
        }

        /// <summary>Axis the texture wraps around (u = metres along it, v = angle; seam underneath).</summary>
        public Sculpt Axis(Vector3 from, Vector3 to, Vector3? upDir = null)
        { axisFrom = from; axisTo = to; up = upDir ?? Vector3.up; return this; }

        // ------------------------------------------------------------------ distance field

        public float Eval(Vector3 p)
        {
            float d = float.MaxValue;
            for (int i = 0; i < shapes.Count; i++)
            {
                var s = shapes[i];
                float ds = s.cone ? RoundCone(p, s.a, s.b, s.r1, s.r2) : Ellipsoid(p - s.a, s.rad);
                d = i == 0 ? ds : SMin(d, ds, s.k);
            }
            return d;
        }

        static float SMin(float a, float b, float k)
        {
            if (k <= 0f) return Mathf.Min(a, b);
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / k;
            return Mathf.Min(a, b) - h * h * k * 0.25f;
        }

        static float Ellipsoid(Vector3 p, Vector3 r)
        {
            float k0 = new Vector3(p.x / r.x, p.y / r.y, p.z / r.z).magnitude;
            float k1 = new Vector3(p.x / (r.x * r.x), p.y / (r.y * r.y), p.z / (r.z * r.z)).magnitude;
            return k1 > 1e-6f ? k0 * (k0 - 1f) / k1 : -Mathf.Min(r.x, Mathf.Min(r.y, r.z));
        }

        // Inigo Quilez's exact round cone distance
        static float RoundCone(Vector3 p, Vector3 a, Vector3 b, float r1, float r2)
        {
            Vector3 ba = b - a;
            float l2 = Vector3.Dot(ba, ba);
            if (l2 < 1e-8f) return (p - a).magnitude - Mathf.Max(r1, r2);
            float rr = r1 - r2, a2 = l2 - rr * rr, il2 = 1f / l2;
            Vector3 pa = p - a;
            float y = Vector3.Dot(pa, ba), z = y - l2;
            Vector3 xv = pa * l2 - ba * y;
            float x2 = Vector3.Dot(xv, xv), y2 = y * y * l2, z2 = z * z * l2;
            float k = Sign(rr) * rr * rr * x2;
            if (Sign(z) * a2 * z2 > k) return Mathf.Sqrt(x2 + z2) * il2 - r2;
            if (Sign(y) * a2 * y2 < k) return Mathf.Sqrt(x2 + y2) * il2 - r1;
            return (Mathf.Sqrt(x2 * a2 * il2) + y * rr) * il2 - r1;
        }

        static float Sign(float v) => v > 0f ? 1f : v < 0f ? -1f : 0f;

        Vector3 Gradient(Vector3 p, float e)
        {
            var g = new Vector3(Eval(p + new Vector3(e, 0, 0)) - Eval(p - new Vector3(e, 0, 0)),
                                Eval(p + new Vector3(0, e, 0)) - Eval(p - new Vector3(0, e, 0)),
                                Eval(p + new Vector3(0, 0, e)) - Eval(p - new Vector3(0, 0, e)));
            return g.sqrMagnitude > 1e-12f ? g.normalized : Vector3.up;
        }

        Bounds ShapeBounds()
        {
            var b = new Bounds();
            bool first = true;
            void Enc(Vector3 c, Vector3 ext)
            {
                var bb = new Bounds(c, ext * 2f);
                if (first) { b = bb; first = false; } else b.Encapsulate(bb);
            }
            foreach (var s in shapes)
            {
                if (s.cone) { Enc(s.a, Vector3.one * s.r1); Enc(s.b, Vector3.one * s.r2); }
                else Enc(s.a, s.rad);
            }
            return b;
        }

        // ------------------------------------------------------------------ meshing (Surface Nets)

        public Mesh Build(string name, float cell)
        {
            var bounds = ShapeBounds();
            Vector3 min = bounds.min - Vector3.one * (cell * 2f);
            Vector3 size = bounds.size + Vector3.one * (cell * 4f);
            int nx = Mathf.CeilToInt(size.x / cell) + 1, ny = Mathf.CeilToInt(size.y / cell) + 1, nz = Mathf.CeilToInt(size.z / cell) + 1;

            var f = new float[nx * ny * nz];
            int I(int x, int y, int z) => x + nx * (y + ny * z);
            Vector3 P(int x, int y, int z) => min + new Vector3(x, y, z) * cell;
            for (int z = 0; z < nz; z++) for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++) f[I(x, y, z)] = Eval(P(x, y, z));

            int cx = nx - 1, cy = ny - 1, cz = nz - 1;
            var cellVert = new int[cx * cy * cz];
            int C(int x, int y, int z) => x + cx * (y + cy * z);
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            int[,] corner = { { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 }, { 0, 0, 1 }, { 1, 0, 1 }, { 0, 1, 1 }, { 1, 1, 1 } };
            int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
            var val = new float[8];

            for (int z = 0; z < cz; z++) for (int y = 0; y < cy; y++) for (int x = 0; x < cx; x++)
            {
                int inside = 0;
                for (int c = 0; c < 8; c++)
                {
                    val[c] = f[I(x + corner[c, 0], y + corner[c, 1], z + corner[c, 2])];
                    if (val[c] < 0f) inside++;
                }
                if (inside == 0 || inside == 8) { cellVert[C(x, y, z)] = -1; continue; }
                Vector3 sum = Vector3.zero; int count = 0;
                for (int e = 0; e < 12; e++)
                {
                    int a = edges[e, 0], b = edges[e, 1];
                    if ((val[a] < 0f) == (val[b] < 0f)) continue;
                    float t = val[a] / (val[a] - val[b]);
                    var pa = new Vector3(corner[a, 0], corner[a, 1], corner[a, 2]);
                    var pb = new Vector3(corner[b, 0], corner[b, 1], corner[b, 2]);
                    sum += Vector3.Lerp(pa, pb, t); count++;
                }
                Vector3 v = min + (new Vector3(x, y, z) + sum / count) * cell;
                // Pull the vertex onto the true surface (two Newton steps): smoother silhouettes
                for (int it = 0; it < 2; it++)
                {
                    var g = Gradient(v, cell * 0.25f);
                    float d = Eval(v);
                    v -= g * d;
                }
                cellVert[C(x, y, z)] = verts.Count;
                verts.Add(v);
                normals.Add(Gradient(v, cell * 0.5f));
            }

            var tris = new List<int>();
            void Quad(int c0, int c1, int c2, int c3)
            {
                if (c0 < 0 || c1 < 0 || c2 < 0 || c3 < 0) return;
                // Wind so the face points along the field gradient (outwards)
                Vector3 n = Vector3.Cross(verts[c1] - verts[c0], verts[c2] - verts[c0]);
                Vector3 avg = normals[c0] + normals[c1] + normals[c2] + normals[c3];
                if (Vector3.Dot(n, avg) < 0f) tris.AddRange(new[] { c0, c2, c1, c0, c3, c2 });
                else tris.AddRange(new[] { c0, c1, c2, c0, c2, c3 });
            }
            for (int z = 1; z < cz; z++) for (int y = 1; y < cy; y++) for (int x = 1; x < cx; x++)
            {
                bool s0 = f[I(x, y, z)] < 0f;
                if (s0 != (f[I(x + 1, y, z)] < 0f))   // x edge: cells around it in y/z
                    Quad(cellVert[C(x, y - 1, z - 1)], cellVert[C(x, y, z - 1)], cellVert[C(x, y, z)], cellVert[C(x, y - 1, z)]);
                if (s0 != (f[I(x, y + 1, z)] < 0f))   // y edge
                    Quad(cellVert[C(x - 1, y, z - 1)], cellVert[C(x, y, z - 1)], cellVert[C(x, y, z)], cellVert[C(x - 1, y, z)]);
                if (s0 != (f[I(x, y, z + 1)] < 0f))   // z edge
                    Quad(cellVert[C(x - 1, y - 1, z)], cellVert[C(x, y - 1, z)], cellVert[C(x, y, z)], cellVert[C(x - 1, y, z)]);
            }

            // Cylindrical UVs around the part's axis: u = metres along, v = 0 on the back .. 1 underneath (both sides mirrored)
            Vector3 axis = axisTo - axisFrom;
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.forward;
            axis.Normalize();
            Vector3 upP = Vector3.ProjectOnPlane(up, axis);
            if (upP.sqrMagnitude < 1e-4f) upP = Vector3.ProjectOnPlane(Vector3.forward, axis);
            upP.Normalize();
            Vector3 side = Vector3.Cross(axis, upP);
            var uvs = new List<Vector2>(verts.Count);
            foreach (var v in verts)
            {
                Vector3 rel = v - axisFrom;
                float u = Vector3.Dot(rel, axis);
                Vector3 r = rel - axis * u;
                float ang = Mathf.Atan2(Vector3.Dot(r, side), Vector3.Dot(r, upP));
                uvs.Add(new Vector2(u, Mathf.Abs(ang) / Mathf.PI));   // 0 on the back, 1 underneath: mirrored, so no seam
            }

            var mesh = new Mesh { name = name, indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(verts); mesh.SetNormals(normals); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>Generated fur / skin textures for the sculpted animals (u = metres along the part, v = angle).</summary>
    public static class CreatureTextures
    {
        const string Folder = "Assets/_Project/Art/Characters/Textures";

        public static Texture2D Get(string name)
        {
            string path = $"{Folder}/{name}.png";
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex != null) return tex;
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/_Project/Art/Characters", "Textures");
            var t = name switch
            {
                "Tiger_Fur2" => Make(512, 256, Tiger),
                "Deer_Coat2" => Make(256, 256, Deer),
                "Elephant_Skin2" => Make(512, 256, Elephant),
                _ => Make(256, 256, Fabric),
            };
            File.WriteAllBytes(path, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.maxTextureSize = 512;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Texture2D Make(int w, int h, System.Func<float, float, Color> f)
        {
            var t = new Texture2D(w, h, TextureFormat.RGB24, true);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) t.SetPixel(x, y, f(x / (float)w, y / (float)h));
            t.Apply();
            return t;
        }

        static float N(float x, float y) => Mathf.PerlinNoise(x, y);

        // One texture repeat = 1 m along the body. v = 0 is the back, 1 the belly.
        static Color Tiger(float u, float v)
        {
            float fromBack = v;                                                 // 0 back .. 1 belly
            var orange = Color.Lerp(new Color(0.78f, 0.36f, 0.08f), new Color(0.93f, 0.55f, 0.18f), fromBack);
            var c = Color.Lerp(orange, new Color(0.93f, 0.9f, 0.84f), Mathf.SmoothStep(0f, 1f, (fromBack - 0.62f) / 0.18f));
            // Stripes: about 7 per metre, wavy, thinning towards the belly (u wraps, so 7 whole stripes)
            float wobble = (N(u * 6f, v * 5f) - 0.5f) * 0.9f + Mathf.Sin(v * Mathf.PI * 6f) * 0.12f;
            float s = Mathf.Sin((u * 7f + wobble) * Mathf.PI * 2f);
            float width = Mathf.Lerp(0.55f, 0.92f, fromBack);
            if (s > width && fromBack < 0.85f) c = Color.Lerp(c, new Color(0.05f, 0.04f, 0.04f), Mathf.Clamp01((s - width) * 8f));
            return c * (0.92f + 0.12f * N(u * 40f, v * 40f));
        }

        static Color Deer(float u, float v)
        {
            float fromBack = v;
            var c = Color.Lerp(new Color(0.42f, 0.27f, 0.15f), new Color(0.6f, 0.42f, 0.25f), fromBack);
            c = Color.Lerp(c, new Color(0.9f, 0.84f, 0.72f), Mathf.SmoothStep(0f, 1f, (fromBack - 0.7f) / 0.15f));
            // Spotted deer (chital, common in Sri Lanka): white spots along the flanks
            float spots = N(u * 9f, v * 9f);
            if (fromBack > 0.2f && fromBack < 0.65f && spots > 0.72f) c = Color.Lerp(c, new Color(0.92f, 0.88f, 0.8f), 0.8f);
            return c * (0.94f + 0.1f * N(u * 50f, v * 50f));
        }

        static Color Elephant(float u, float v)
        {
            var c = new Color(0.44f, 0.42f, 0.41f) * (0.85f + 0.25f * N(u * 3f, v * 3f));
            // Wrinkles: fine wavy creases around the body
            float crease = Mathf.Abs(Mathf.Sin((u * 26f + N(u * 4f, v * 8f) * 2f) * Mathf.PI));
            if (crease < 0.12f) c *= 0.72f;
            return c * (0.94f + 0.1f * N(u * 70f, v * 70f));
        }

        static Color Fabric(float u, float v)
        {
            float weave = (Mathf.Sin(u * 300f) * Mathf.Sin(v * 300f)) * 0.03f;
            return Color.white * (0.9f + 0.08f * N(u * 20f, v * 20f) + weave);
        }
    }

    /// <summary>Keeps every sculpted mesh in one asset file so the prefabs can reference them.</summary>
    public class MeshBank
    {
        readonly string path;
        Object container;
        readonly Dictionary<string, Mesh> existing;

        public MeshBank(string path)
        {
            this.path = path;
            container = AssetDatabase.LoadMainAssetAtPath(path);
            existing = container == null ? new Dictionary<string, Mesh>()
                : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Where(m => m.name != container.name).ToDictionary(m => m.name, m => m);
        }

        public Mesh Add(Mesh m)
        {
            if (container == null)
            {
                var holder = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(holder, path);
                container = holder;
            }
            if (existing.TryGetValue(m.name, out var old))
            {
                EditorUtility.CopySerialized(m, old);
                Object.DestroyImmediate(m);
                return old;
            }
            AssetDatabase.AddObjectToAsset(m, container);
            existing[m.name] = m;
            return m;
        }

        public void Save() { if (container != null) { EditorUtility.SetDirty(container); AssetDatabase.SaveAssets(); } }
    }
}
