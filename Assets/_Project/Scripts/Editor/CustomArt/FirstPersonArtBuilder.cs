using System.IO;
using System.Linq;
using System.Collections.Generic;
using LastShift;
using UnityEditor;
using UnityEngine;
namespace CustomArt.EditorTools
{
    public static class FirstPersonArtBuilder
    {
        const string Hands = "Assets/_Project/Art/Characters/Custom/FPHands_Custom.fbx";
        const string Config = "Assets/_Project/Resources/FirstPersonArt.asset";
        [MenuItem("Tools/Custom Art/Build First Person Grip Assets")]
        public static void Build()
        {
            foreach (var path in new[] { Hands, "Assets/_Project/Art/Environment/Models/Shotgun_Custom.fbx" })
                if (AssetImporter.GetAtPath(path) is ModelImporter importer && !importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
            var meshes = AssetDatabase.LoadAllAssetsAtPath(Hands).OfType<Mesh>().ToArray();
            Mesh Find(string name) => meshes.FirstOrDefault(m => m.name == name || m.name.EndsWith(name));
            if (Find("GripR") == null || Find("ForeL") == null || Find("SlingL") == null) throw new System.InvalidOperationException("Detailed hand meshes are missing");
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Resources")) AssetDatabase.CreateFolder("Assets/_Project","Resources");
            var art = AssetDatabase.LoadAssetAtPath<FirstPersonArt>(Config);
            if (art == null) { art = ScriptableObject.CreateInstance<FirstPersonArt>(); AssetDatabase.CreateAsset(art,Config); }
            foreach (var old in AssetDatabase.LoadAllAssetsAtPath(Config).OfType<Mesh>()) Object.DestroyImmediate(old,true);
            art.gripRight = Find("GripR"); art.supportLeft = Find("ForeL"); art.slingLeft = Find("SlingL");
            art.handMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Characters/Custom/Materials/M_FPHands_Custom.mat");
            art.pistol = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Environment_Custom/Pistol_Custom.prefab");
            art.shotgun = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Environment_Custom/Shotgun_Custom.prefab");
            art.weaponEuler = new Vector3(-90f,180f,0f);
            if (art.handMaterial == null || art.pistol == null || art.shotgun == null) throw new System.InvalidOperationException("Hand material or weapon prefabs are missing");
            SplitPump(art);
            EditorUtility.SetDirty(art); AssetDatabase.SaveAssets();
            Debug.Log("[Custom Art] First-person detailed hands, weapon art and contact poses ready");
        }
        static void SplitPump(FirstPersonArt art)
        {
            var filter = art.shotgun.GetComponentInChildren<MeshFilter>(); var renderer = filter.GetComponent<Renderer>();
            var source = filter.sharedMesh; var vertices = source.vertices;
            var transform = Matrix4x4.Rotate(Quaternion.Euler(art.weaponEuler)) * art.shotgun.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            var points = vertices.Select(v => transform.MultiplyPoint3x4(v)).ToArray();
            var normals = source.normals.Select(n => transform.MultiplyVector(n).normalized).ToArray();
            var uv = source.uv; var triangles = source.triangles;
            var body = new List<int>(); var pump = new List<int>();
            art.shotgunMaterial = renderer.sharedMaterial;
            var tex = new Texture2D(2,2); var map = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/_Project/Art/Environment/Models/Textures/Shotgun_Custom/Shotgun_Custom_BaseColor.png");
            if (map != null) tex.LoadImage(File.ReadAllBytes(AssetDatabase.GetAssetPath(map)));
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i+1], c = triangles[i+2];
                Vector3 p = (points[a]+points[b]+points[c])/3f;
                Vector2 sample = (uv[a]+uv[b]+uv[c])/3f; Color color = tex.GetPixelBilinear(sample.x,sample.y);
                bool wood = color.r > color.g * 1.15f && color.g > color.b * 1.08f;
                var list = wood && p.z > 0.135f && p.z < 0.305f && Mathf.Abs(p.x) < 0.045f && Mathf.Abs(p.y-0.007f) < 0.045f ? pump : body;
                list.Add(a); list.Add(b); list.Add(c);
            }
            Object.DestroyImmediate(tex);
            if (pump.Count == 0) throw new System.InvalidOperationException("Unable to identify the shotgun's wooden fore-end");
            Mesh Part(string name,List<int> indices,Vector3 offset)
            {
                var used = indices.Distinct().ToArray(); var remap = new Dictionary<int,int>();
                for(int i=0;i<used.Length;i++) remap[used[i]]=i;
                var mesh = new Mesh { name = name }; mesh.vertices = used.Select(i => points[i]-offset).ToArray(); mesh.normals = used.Select(i=>normals[i]).ToArray(); mesh.uv = used.Select(i=>uv[i]).ToArray(); mesh.triangles = indices.Select(i=>remap[i]).ToArray(); mesh.RecalculateBounds();
                AssetDatabase.AddObjectToAsset(mesh,art); return mesh;
            }
            art.shotgunReceiver = Part("FP_ShotgunReceiver",body,Vector3.zero);
            art.shotgunPump = Part("FP_ShotgunForeEnd",pump,new Vector3(0,0.007f,0.22f));
        }
    }
}
