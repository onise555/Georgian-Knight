using GeorgianKnight.Core;
using UnityEngine;

namespace GeorgianKnight
{
    /// <summary>Put on the player prefab root: binds it to <see cref="IPlayerContext"/> for its lifetime.</summary>
    [DefaultExecutionOrder(-500)]
    public sealed class PlayerBinding : MonoBehaviour
    {
        void OnEnable()
        {
            IPlayerContext ctx;
            if (Services.TryGet(out ctx)) ctx.Bind(gameObject);
        }

        void OnDisable()
        {
            IPlayerContext ctx;
            if (Services.TryGet(out ctx)) ctx.Unbind(gameObject);
        }
    }
}
