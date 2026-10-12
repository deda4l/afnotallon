#if !BATTLEGROUNDS
using BLINK.RPGBuilder.Combat;
using HarmonyLib;

namespace AfallonCoop.Plugin;

/// <summary>
/// Battlegrounds are left out of this build: the game ships their code but no battleground to play (Stonefield Basin's
/// scene is not in the build, the finder's tab is off). The co-op version is in Battlegrounds.cs and
/// BattlegroundTestArena.cs, built with -p:Battlegrounds=true; here only what the rest of the mod asks of it.
/// </summary>
internal static class Battlegrounds
{
    public const byte Kind = 38;
    public static bool InMatch => false;
    public static bool Contains(CombatEntity e) => false;
    public static string InstanceFor(string scene) => null;
    public static bool ForwardHit(byte victim, MobCombatEntity shell, CombatCalculations.DamageResult dr, int stat) => false;
    public static bool Defeated(CombatEntity e) => false;
    public static bool Revive(CombatEntity e) => false;
    public static void Update() { }
    public static void OnPlayerLeft(byte player) { }
    public static void Receive(byte from, ref AfallonCoop.Protocol.NetReader r) { }
    public static void Install(Harmony h) { }
    public static string Describe() => "battlegrounds are not in this build (the game has none yet)";
}

internal static class BattlegroundTestArena
{
    public static void Install(Harmony h) { }
    public static string Configure(string arg) => Battlegrounds.Describe();
}
#endif
