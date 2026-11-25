using UnityEngine;
namespace LastShift
{
    [CreateAssetMenu(menuName = "Last Shift/First Person Art")]
    public sealed class FirstPersonArt : ScriptableObject
    {
        public Mesh gripRight, supportLeft, slingLeft;
        public Material handMaterial;
        public GameObject pistol, shotgun;
        public Vector3 weaponEuler = new Vector3(-90f, 180f, 0f);
        public Mesh shotgunReceiver, shotgunPump;
        public Material shotgunMaterial;
    }
}
