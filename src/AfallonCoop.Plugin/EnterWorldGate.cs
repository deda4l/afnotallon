using System;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;

namespace AfallonCoop.Plugin;

/// <summary>The game's "enter the world" buttons of the main menu, held until a chosen co-op join is connected.</summary>
internal static class EnterWorldGate
{
    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.PlaySelectedCharacter))]
        private static bool Play(MainMenuManager __instance) => Gate(() => __instance.PlaySelectedCharacter());

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.CreateCharacter))]
        private static bool Create(MainMenuManager __instance) => Gate(() => __instance.CreateCharacter());

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.CreateCharacterAndEnterWorld))]
        private static bool CreateAndEnter(MainMenuManager __instance) => Gate(() => __instance.CreateCharacterAndEnterWorld());

        private static bool Gate(Action enter)
        {
            try { return CoopManager.BeforeEnterWorld(enter); }
            catch (Exception e) { Plugin.L.LogWarning("Enter world gate: " + e.Message); return true; }
        }
    }
}
