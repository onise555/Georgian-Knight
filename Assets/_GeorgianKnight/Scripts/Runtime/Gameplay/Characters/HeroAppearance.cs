using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>
    /// Bakur's two head looks (CHARACTERS.md §3): the helmet set (padded coif, spangenhelm with its brow band, mail
    /// aventail) for travel and battle, and bare head with the hair for every dialogue scene except battle beats.
    /// </summary>
    public sealed class HeroAppearance : MonoBehaviour
    {
        public GameObject[] helmetSet;
        public GameObject[] hairSet;
        [SerializeField] bool helmet = true;

        public bool Helmet { get { return helmet; } set { helmet = value; Apply(); } }

        void Awake() { Apply(); }

        void Apply()
        {
            if (helmetSet != null) foreach (var g in helmetSet) if (g != null) g.SetActive(helmet);
            if (hairSet != null) foreach (var g in hairSet) if (g != null) g.SetActive(!helmet);
        }
    }
}
