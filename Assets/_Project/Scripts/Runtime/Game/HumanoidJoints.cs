using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
namespace LastShift
{
    /// <summary>Two-bone limbs with blended skin at knees/elbows and a separate foot bone.</summary>
    public sealed class HumanoidJoints : IDisposable
    {
        sealed class Chain
        {
            public Transform upper, lower, foot; public Quaternion rest;
            public float a, b, side; public bool leg; public Bounds upperBounds, lowerBounds, footBounds;
        }
        sealed class Skin { public Mesh mesh; public SkinnedMeshRenderer skin; public MeshRenderer original; }
        readonly List<Chain> chains = new List<Chain>();
        readonly List<Skin> skins = new List<Skin>();
        readonly List<(Transform t, Transform parent)> attachments = new List<(Transform, Transform)>();
        public int JointCount => chains.Count * 2;
        public float CrouchDrop { get; private set; }
        public HumanoidJoints(Transform model)
        {
            foreach(var name in new[] { "Arm_L", "Arm_R", "Leg_L", "Leg_R" })
            {
                Transform upper = null;
                foreach(var t in model.GetComponentsInChildren<Transform>(true)) if(t.name == name) {upper=t; break;}
                if(upper == null) continue;
                bool leg = name.StartsWith("Leg_");
                var filters = new List<MeshFilter>(); var bounds = new Bounds(); bool found=false;
                foreach(var mf in upper.GetComponentsInChildren<MeshFilter>(true))
                {
                    bool prop=false;
                    for(var p=mf.transform;p!=null && p!=upper;p=p.parent)
                        if(p.name=="Gun" || p.name=="Lantern" || p.name=="Flashlight") {prop=true;break;}
                    var r=mf.GetComponent<MeshRenderer>();
                    if(prop || r==null || !r.enabled || mf.sharedMesh==null || !mf.sharedMesh.isReadable) continue;
                    filters.Add(mf);
                    foreach(var v in mf.sharedMesh.vertices)
                    { var point=upper.InverseTransformPoint(mf.transform.TransformPoint(v)); if(!found) {bounds=new Bounds(point,Vector3.zero);found=true;} else bounds.Encapsulate(point); }
                }
                if(!found || -bounds.min.y < 0.15f) continue;
                float length=-bounds.min.y;
                var c=new Chain {upper=upper,rest=upper.localRotation,leg=leg,side=name.EndsWith("L")?0:Mathf.PI,
                    a=length*(leg?0.48f:0.47f), b=length*(leg?0.40f:0.43f)};
                c.lower=new GameObject((leg?"Shin_":"Forearm_")+name.Substring(4)).transform;
                c.lower.SetParent(upper,false); c.lower.localPosition=Vector3.down*c.a;
                c.foot=new GameObject((leg?"Foot_":"Wrist_")+name.Substring(4)).transform;
                c.foot.SetParent(c.lower,false); c.foot.localPosition=Vector3.down*c.b;
                Bounds Section(float top,float bottom,Vector3 origin)
                { var min=bounds.min; var max=bounds.max; min.y=bottom;max.y=top;return new Bounds((min+max)*0.5f-origin,max-min); }
                c.upperBounds=Section(Mathf.Min(bounds.max.y,0),-c.a,Vector3.zero);
                c.lowerBounds=Section(-c.a,-c.a-c.b,Vector3.down*c.a);
                c.footBounds=Section(-c.a-c.b,bounds.min.y,Vector3.down*(c.a+c.b));
                foreach(var mf in filters)
                {
                    var original=mf.GetComponent<MeshRenderer>(); var mesh=Object.Instantiate(mf.sharedMesh);mesh.name += " articulated";
                    Refine(mesh, upper.worldToLocalMatrix * mf.transform.localToWorldMatrix, c.a, c.a+c.b, length);
                    var bones=new[]{upper,c.lower,c.foot};var weights=new BoneWeight[mesh.vertexCount];var vertices=mesh.vertices;
                    for(int i=0;i<weights.Length;i++)
                    {
                        float depth=-upper.InverseTransformPoint(mf.transform.TransformPoint(vertices[i])).y;
                        float joint=Mathf.SmoothStep(0,1,Mathf.InverseLerp(c.a-length*0.07f,c.a+length*0.07f,depth));
                        float distal=Mathf.SmoothStep(0,1,Mathf.InverseLerp(c.a+c.b-length*0.035f,c.a+c.b+length*0.035f,depth));
                        weights[i]=new BoneWeight {boneIndex0=0,boneIndex1=1,boneIndex2=2,weight0=1-joint,weight1=joint*(1-distal),weight2=joint*distal};
                    }
                    mesh.boneWeights=weights;var poses=new Matrix4x4[3];
                    for(int i=0;i<3;i++) poses[i]=bones[i].worldToLocalMatrix*mf.transform.localToWorldMatrix;
                    mesh.bindposes=poses;
                    // A separate renderer object avoids two Renderer components on the imported part.
                    var visual=new GameObject("Articulated skin");visual.layer=mf.gameObject.layer;visual.transform.SetParent(mf.transform,false);
                    var skin=visual.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=mesh;skin.bones=bones;skin.rootBone=upper;
                    skin.sharedMaterials=original.sharedMaterials;skin.shadowCastingMode=original.shadowCastingMode;skin.receiveShadows=original.receiveShadows;
                    skin.localBounds=new Bounds(mesh.bounds.center,mesh.bounds.size+Vector3.one*length*2f);
                    original.enabled=false;skins.Add(new Skin {mesh=mesh,skin=skin,original=original});
                }
                // Held props follow the wrist, including muzzle and lamp children.
                foreach(var t in upper.GetComponentsInChildren<Transform>(true))
                    if(t!=upper && (t.name=="Gun" || t.name=="Lantern" || t.name=="Flashlight"))
                    {attachments.Add((t,t.parent));t.SetParent(c.foot,true);}
                chains.Add(c);
                if(leg) CrouchDrop=Mathf.Max(CrouchDrop,(c.a+c.b)*(1-Mathf.Cos(52f*Mathf.Deg2Rad)));
            }
        }
        struct Vertex
        {
            public Vector3 p, n; public Vector2 uv; public Vector4 tangent;
            public static Vertex Lerp(Vertex a,Vertex b,float t) => new Vertex {p=Vector3.Lerp(a.p,b.p,t),n=Vector3.Lerp(a.n,b.n,t).normalized,uv=Vector2.Lerp(a.uv,b.uv,t),tangent=Vector4.Lerp(a.tangent,b.tangent,t)};
        }
        // Imported cylinders often have only end rings. Insert rings around the joints before
        // weighting; otherwise even a skinned cylinder would stretch straight across the bend.
        static void Refine(Mesh mesh,Matrix4x4 toLimb,float joint,float end,float length)
        {
            var source=mesh.vertices;var normals=mesh.normals;var uv=mesh.uv;var tangents=mesh.tangents;
            var vertices=new List<Vector3>();var ns=new List<Vector3>();var uvs=new List<Vector2>();var ts=new List<Vector4>();var submeshes=new List<int[]>();
            var planes=new[]{joint-length*0.07f,joint,joint+length*0.07f,end-length*0.035f,end,end+length*0.035f};
            float Depth(Vertex v) => -toLimb.MultiplyPoint3x4(v.p).y;
            List<Vertex> Clip(List<Vertex> polygon,float plane,bool below)
            {
                var result=new List<Vertex>();
                for(int i=0;i<polygon.Count;i++)
                {
                    var a=polygon[i];var b=polygon[(i+1)%polygon.Count];float da=Depth(a)-plane,db=Depth(b)-plane;
                    bool inside=below?da<=0:da>=0, other=below?db<=0:db>=0;
                    if(inside)result.Add(a);
                    if(inside!=other)result.Add(Vertex.Lerp(a,b,da/(da-db)));
                }
                return result;
            }
            for(int sub=0;sub<mesh.subMeshCount;sub++)
            {
                var indices=new List<int>();var tris=mesh.GetTriangles(sub);
                for(int i=0;i<tris.Length;i+=3)
                {
                    var polygon=new List<Vertex>();
                    for(int k=0;k<3;k++){int n=tris[i+k];polygon.Add(new Vertex{p=source[n],n=normals.Length>n?normals[n]:Vector3.up,uv=uv.Length>n?uv[n]:Vector2.zero,tangent=tangents.Length>n?tangents[n]:new Vector4(1,0,0,1)});}
                    var pieces=new List<List<Vertex>>{polygon};
                    foreach(float plane in planes)
                    {
                        var split=new List<List<Vertex>>();
                        foreach(var part in pieces)
                        {
                            float min=float.PositiveInfinity,max=float.NegativeInfinity;
                            foreach(var v in part){float d=Depth(v);min=Mathf.Min(min,d);max=Mathf.Max(max,d);}
                            if(min<plane-0.00001f && max>plane+0.00001f){split.Add(Clip(part,plane,true));split.Add(Clip(part,plane,false));}else split.Add(part);
                        }
                        pieces=split;
                    }
                    foreach(var part in pieces)
                    {
                        int start=vertices.Count;
                        foreach(var v in part){vertices.Add(v.p);ns.Add(v.n);uvs.Add(v.uv);ts.Add(v.tangent);}
                        for(int k=1;k<part.Count-1;k++){indices.Add(start);indices.Add(start+k);indices.Add(start+k+1);}
                    }
                }
                submeshes.Add(indices.ToArray());
            }
            mesh.Clear();mesh.indexFormat=vertices.Count>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(vertices);mesh.SetNormals(ns);mesh.SetUVs(0,uvs);mesh.SetTangents(ts);mesh.subMeshCount=submeshes.Count;
            for(int i=0;i<submeshes.Count;i++)mesh.SetTriangles(submeshes[i],i);mesh.RecalculateBounds();
        }
        public bool TryBounds(Transform t,out Bounds bounds)
        {
            foreach(var c in chains) {if(t==c.upper){bounds=c.upperBounds;return true;}if(t==c.lower){bounds=c.lowerBounds;return true;}if(t==c.foot){bounds=c.footBounds;return true;}}
            bounds=default;return false;
        }
        public Transform ParentBone(Transform t)
        {foreach(var c in chains) {if(t==c.lower)return c.upper;if(t==c.foot)return c.lower;}return null;}
        public void Pose(float phase,float gait,float crouch,bool airborne,float airPhase,float aim,bool twoHanded,float attack)
        {
            float tuck=airborne?Mathf.Sin(Mathf.Clamp01(airPhase)*Mathf.PI):0;
            foreach(var c in chains)
            {
                if(c.leg)
                {
                    float cycle=phase+c.side, swing=Mathf.Sin(cycle);
                    float drop=CrouchDrop*crouch;
                    float z=swing*gait*0.22f*(1-0.55f*crouch)-0.12f*tuck;
                    float lift=Mathf.Max(0,swing)*gait*0.12f+0.22f*tuck;
                    float down=c.a+c.b-drop-lift;
                    float minimum=Mathf.Sqrt(c.a*c.a+c.b*c.b+2*c.a*c.b*Mathf.Cos(140f*Mathf.Deg2Rad));
                    float distance=Mathf.Clamp(Mathf.Sqrt(down*down+z*z),Mathf.Max(minimum,Mathf.Abs(c.a-c.b)+0.01f),c.a+c.b-0.0001f);
                    float hip=Mathf.Atan2(-z,down)-Mathf.Acos(Mathf.Clamp((c.a*c.a+distance*distance-c.b*c.b)/(2*c.a*distance),-1,1));
                    float knee=Mathf.PI-Mathf.Acos(Mathf.Clamp((c.a*c.a+c.b*c.b-distance*distance)/(2*c.a*c.b),-1,1));
                    c.upper.localRotation=c.rest*Quaternion.Euler(hip*Mathf.Rad2Deg,0,0);
                    c.lower.localRotation=Quaternion.Euler(knee*Mathf.Rad2Deg,0,0);
                    c.foot.localRotation=Quaternion.Euler(-(hip+knee)*Mathf.Rad2Deg,0,0);
                }
                else
                {
                    bool right=c.upper.name=="Arm_R";
                    float aiming=right || twoHanded?aim:0;
                    float bend=Mathf.Lerp(12f+gait*(10f+8f*Mathf.Sin(phase+c.side))+crouch*12f+tuck*22f,right?32f:55f,aiming);
                    bend=Mathf.Lerp(bend,right?85f:40f,attack);
                    c.upper.localRotation*=Quaternion.Euler(bend*aiming,0,0);
                    c.lower.localRotation=Quaternion.Euler(-bend,0,0);c.foot.localRotation=Quaternion.identity;
                }
            }
        }
        public void Reset() {foreach(var c in chains){c.upper.localRotation=c.rest;c.lower.localRotation=c.foot.localRotation=Quaternion.identity;}}
        public void Dispose()
        {
            foreach(var a in attachments) if(a.t!=null && a.parent!=null)a.t.SetParent(a.parent,true);
            foreach(var s in skins){if(s.original!=null)s.original.enabled=true;if(s.skin!=null)Destroy(s.skin.gameObject);Destroy(s.mesh);}
            foreach(var c in chains)if(c.lower!=null)Destroy(c.lower.gameObject);
            chains.Clear();skins.Clear();attachments.Clear();
        }
        static void Destroy(Object o){if(o==null)return;if(Application.isPlaying)Object.Destroy(o);else Object.DestroyImmediate(o);}
    }
}
