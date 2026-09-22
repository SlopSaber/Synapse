using BeatSaberMarkupLanguage;
using TMPro;
using UnityEngine;

namespace Synapse.Extras;

internal static class UICompatibility
{
    internal static TextMeshProUGUI CreateText(RectTransform parent, string text, Vector2 position)
    {
#if MODERN_GAME_UI
        return BeatSaberUI.CreateCurvedUIText(parent, text, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), position, new Vector2(60, 10));
#else
        return BeatSaberUI.CreateText(parent, text, position);
#endif
    }

    internal static void SetWrapping(TMP_Text text, bool enabled)
    {
#if MODERN_GAME_UI
        text.textWrappingMode = enabled ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
#else
        text.enableWordWrapping = enabled;
#endif
    }
}
