using System;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// The game's own chat box ("Say:") doubles as co-op chat: what you send goes to everyone in the session,
/// and their lines appear in your chat panel plus as a bubble over their character.
/// </summary>
internal static class ChatBridge
{
    private static ChatPanel _panel;

    public static void Install(Harmony h)
    {
        h.PatchAll(typeof(SendPatch));
        Plugin.L.LogInfo("Chat bridge installed");
    }

    [HarmonyPatch(typeof(PlayerChatInput), nameof(PlayerChatInput.TrySendMessage))]
    private static class SendPatch
    {
        private static void Postfix(string value, bool __result)
        {
            try
            {
                if (!__result || !CoopManager.Connected) return;
                var text = PlayerChatInput.NormalizeMessage(value);
                if (!string.IsNullOrWhiteSpace(text)) CoopManager.Session.SendChat(text);
            }
            catch (Exception e) { Plugin.L.LogWarning("Chat send failed: " + e.Message); }
        }
    }

    /// <summary>A line from another player.</summary>
    public static void Incoming(byte id, string name, string text)
    {
        text = PlayerChatInput.EscapeMarkup(text); // a typed "<b>" shows as text everywhere, as in the game's own chat
        Hud.Bubble(id, text);
        try
        {
            if (_panel == null) _panel = Object.FindObjectOfType<ChatPanel>();
            if (_panel != null)
            {
                _panel.AddPlayerMessage(name, text);
                return;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Chat display failed: " + e.Message); _panel = null; }
        Hud.Notify($"{name}: {text}"); // main menu or no chat panel: show in the co-op feed
    }
}
