using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>Base gameplay services shared by every gameplay module.</summary>
    [CreateAssetMenu(menuName = "Georgian Knight/Modules/Gameplay Base", fileName = "GameplayBaseModule")]
    public sealed class GameplayBaseModule : GameModule
    {
        public override void Install(GameRoot root)
        {
            Services.Register<IPlayerContext>(new PlayerContext());
            if (root.GetComponent<SettingsApplier>() == null) root.gameObject.AddComponent<SettingsApplier>();
        }
    }
}
