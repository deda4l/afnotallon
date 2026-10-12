using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>Builds UI by cloning the game's own widgets, so it looks native and needs no assets.</summary>
internal static class UiKit
{
    public static TMP_FontAsset Font;

    public static UnityAction Action(Action a) => DelegateSupport.ConvertDelegate<UnityAction>(a);

    public static void StripLocalization(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (c == null) continue;
            var n = c.GetIl2CppType().Name;
            if (n.Contains("Locali")) Object.DestroyImmediate(c);
        }
    }

    public static void SetLabel(GameObject go, string text)
    {
        var t = go.GetComponentInChildren<TMP_Text>(true);
        if (t != null) t.text = text;
    }

    public static Button CloneButton(Button template, Transform parent, string text, Action onClick)
    {
        var go = Object.Instantiate(template.gameObject, parent);
        go.name = "AfCoop_Btn_" + text;
        go.SetActive(true);
        StripLocalization(go);
        SetLabel(go, text);
        var b = go.GetComponent<Button>();
        b.onClick = new Button.ButtonClickedEvent(); // drops the persistent (inspector) listeners too
        if (onClick != null) b.onClick.AddListener(Action(onClick));
        b.interactable = onClick != null;
        return b;
    }

    /// <summary>A menu row (frame and button, cloned from the game's own) with this label and click.</summary>
    public static GameObject Row(GameObject template, Transform parent, string text, Action onClick)
    {
        var go = Object.Instantiate(template, parent);
        go.name = "AfCoop_Row_" + text;
        go.SetActive(true);
        StripLocalization(go);
        var b = go.GetComponentInChildren<Button>(true);
        SetLabel(b.gameObject, text);
        b.onClick = new Button.ButtonClickedEvent(); // drops the persistent (inspector) listeners too
        if (onClick != null) b.onClick.AddListener(Action(onClick));
        b.interactable = onClick != null;
        return go;
    }

    /// <summary>
    /// A list that scrolls (mouse wheel, drag) at the top-down spot (y, w, h) of a panel built with <see cref="Place"/>;
    /// rows go into the returned content, one under the other.
    /// </summary>
    public static Transform ScrollList(Transform parent, float y, float w, float h)
    {
        var view = new GameObject("AfCoop_Scroll");
        view.transform.SetParent(parent, false);
        var bg = view.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.25f); // also what the wheel and drags hit
        view.AddComponent<RectMask2D>();
        Place(bg, 0, y, w, h);
        var content = new GameObject("AfCoop_ScrollContent");
        content.transform.SetParent(view.transform, false);
        var cr = content.AddComponent<RectTransform>();
        cr.anchorMin = new Vector2(0f, 1f); cr.anchorMax = new Vector2(1f, 1f); cr.pivot = new Vector2(0.5f, 1f);
        cr.anchoredPosition = Vector2.zero; cr.sizeDelta = Vector2.zero;
        var layout = content.AddComponent<VerticalLayoutGroup>();
        layout.spacing = 6f; layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = layout.childControlHeight = layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var sr = view.AddComponent<ScrollRect>();
        sr.content = cr; sr.viewport = Rect(bg); sr.horizontal = false;
        sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 30f;
        return content.transform;
    }

    public static TextMeshProUGUI Text(Transform parent, string text, float size, Color color, TextAlignmentOptions align)
    {
        var go = new GameObject("AfCoop_Text");
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        if (Font != null) t.font = Font;
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.enableWordWrapping = true;
        t.raycastTarget = false;
        return t;
    }

    public static RectTransform Rect(Component c) => c.GetComponent<RectTransform>();

    public static void Place(Component c, float x, float y, float w, float h)
    {
        var r = Rect(c);
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 1f);
        r.pivot = new Vector2(0.5f, 1f);
        r.anchoredPosition = new Vector2(x, y);
        if (w > 0 && h > 0) r.sizeDelta = new Vector2(w, h);
    }
}
