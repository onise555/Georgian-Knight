using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>Remembers a volume's authored exposure so brightness is applied relative to it, not cumulatively.</summary>
    public sealed class VolumeBrightnessBase : MonoBehaviour
    {
        public float baseExposure;
    }
}
