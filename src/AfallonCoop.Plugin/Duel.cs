using System;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.PvP;
using HarmonyLib;

namespace AfallonCoop.Plugin;

/// <summary>
/// Duels between players with the game's own DuelService (made for dueling an adventurer): "Duel" on another player
/// asks them (the game's confirmation popup); on yes both machines start the game's duel, each with the other
/// player's shell as the opponent (its banner, countdown, boundary, the non-lethal end).
///  - A hit on the opponent's shell is sent to them and taken by their character, with the attacker's shell as the
///    caster: there the game's duel rules let it through and keep it from killing (the last point of health loses).
///  - Each machine decides only for its own player: losing (health, the boundary, forfeit) or a cancel there ends
///    the duel and is told to the other, which ends it the same way. What the game would decide about a shell here
///    (its mirrored health at 1, its mirrored position outside) is ignored.
/// </summary>
internal static class Duel
{
    public const byte Kind = 19;
    private const byte OpAsk = 1, OpAnswer = 2, OpHit = 3, OpLost = 4, OpCancel = 5;
    private const string WinsFormat = "{0} wins the duel!";

    private static readonly NetWriter W = new();
    private static byte _opponent = 255;      // the player I duel (or asked, or was asked by)
    private static bool _fromNet;             // an end told by the other side: not told back
    private static (byte from, float at)? _asked;

    /// <summary>Interaction menu options of ours (their click), by the option object.</summary>
    private static readonly System.Collections.Generic.Dictionary<IntPtr, Action> Options = new();

    private static DuelService Service { get { try { return DuelService.Instance; } catch { return null; } } }

    /// <summary>This entity is the shell of the player I am dueling.</summary>
    public static bool IsOpponentShell(CombatEntity e)
    {
        if (e == null || _opponent == 255) return false;
        var o = Service?.Opponent;
        return o != null && o.Pointer == e.Pointer && PlayerShells.TryGetPlayer(e, out var p) && p == _opponent;
    }

    private static void Send(byte op, byte to, Action<NetWriter> body = null)
    {
        W.Reset(); W.U8(Kind); W.U8(op); W.U8(to);
        body?.Invoke(W);
        CoopManager.Session?.SendWorld(W.Span);
    }

    /// <summary>"Duel" on a player: they are asked.</summary>
    public static void Ask(byte player)
    {
        var svc = Service;
        var shell = PlayerShells.Get(player);
        if (svc == null || shell == null) return;
        if (!svc.CanChallenge(shell, out var why)) { PvPCombat.Alert(why); return; }
        _opponent = player;
        Send(OpAsk, player);
        Hud.Notify($"You challenged {Groups.Name(player)} to a duel");
    }

    public static void Receive(byte from, ref NetReader r)
    {
        byte op = r.U8(), to = r.U8();
        var s = CoopManager.Session;
        if (s == null || to != s.LocalId) return;
        switch (op)
        {
            case OpAsk:
            {
                var shell = PlayerShells.Get(from);
                string why = null;
                if (Service == null || shell == null || !Service.CanChallenge(shell, out why)) { Send(OpAnswer, from, w => w.U8(0)); if (why != null) Hud.Notify($"{Groups.Name(from)} challenged you, but: {why}"); return; }
                _asked = (from, UnityEngine.Time.unscaledTime);
                string who = Groups.Name(from);
                Groups.Ask($"{who} challenges you to a duel. Accept?", () => Answer(true), () => Answer(false));
                break;
            }
            case OpAnswer:
            {
                bool yes = r.U8() == 1;
                if (from != _opponent) return;
                if (yes) Start(from);
                else { _opponent = 255; Hud.Notify($"{Groups.Name(from)} declined the duel"); }
                break;
            }
            case OpHit: TakeHit(from, ref r); break;
            case OpLost: EndFromOther(from, true, r.Str()); break;
            case OpCancel: EndFromOther(from, false, r.Str()); break;
        }
    }

    /// <summary>My answer to the challenge shown to me (the popup or the dev bridge).</summary>
    public static string Answer(bool yes)
    {
        if (_asked is not { } a) return "no challenge";
        _asked = null;
        Send(OpAnswer, a.from, w => w.U8((byte)(yes ? 1 : 0)));
        if (yes) { _opponent = a.from; Start(a.from); }
        return yes ? "accepted" : "declined";
    }

    private static void Start(byte player)
    {
        var svc = Service;
        var shell = PlayerShells.Get(player);
        if (svc == null || shell == null || !svc.Challenge(shell))
        {
            Send(OpCancel, player, w => w.Str("Duel cancelled."));
            _opponent = 255;
            return;
        }
        Plugin.L.LogInfo($"Duel with #{player} started");
    }

    /// <summary>A hit of mine on the opponent's shell: theirs to take. True = handled (not applied here).</summary>
    public static bool ForwardHit(byte victim, CombatCalculations.DamageResult dr, int stat)
    {
        if (victim != _opponent || Service == null || !Service.Fighting || !MobSync.IsLocalSide(dr.caster)) return false;
        Send(OpHit, victim, w => { w.F32(dr.DamageAmount); w.I32(stat); w.Bool(dr.IsAutoAttack); w.Bool(dr.IsDamageOverTime); });
        return true;
    }

    private static void TakeHit(byte from, ref NetReader r)
    {
        float amount = r.F32(); int stat = r.I32(); bool auto = r.Bool(), dot = r.Bool();
        var me = GameInfo.Player;
        var shell = PlayerShells.Get(from);
        if (from != _opponent || me == null || shell == null || Service == null || !Service.Fighting) return;
        // through the game's TakeDamage: its duel rules allow it (both duelists) and stop it at the last point of health
        float before = CombatUtilities.GetCurrentStatValue(me, stat);
        Plugin.L.LogInfo($"Duel: hit {amount:0.#} from #{from}, health {before:0}");
        me.TakeDamage(new CombatCalculations.DamageResult { caster = shell, target = me, DamageAmount = amount, IsAutoAttack = auto, IsDamageOverTime = dot, DamageActionType = "" }, null, stat);
    }

    private static void EndFromOther(byte from, bool theyLost, string message)
    {
        var svc = Service;
        if (from != _opponent || svc == null) return;
        _fromNet = true;
        try
        {
            var shell = PlayerShells.Get(from);
            if (theyLost && shell != null) svc.Finish(shell, WinsFormat);
            else svc.Cancel(string.IsNullOrEmpty(message) ? "Duel cancelled." : message);
        }
        catch (Exception e) { Plugin.L.LogWarning("Duel end: " + e.Message); }
        finally { _fromNet = false; _opponent = 255; }
    }

    /// <summary>The opponent left: the duel is over (their id goes to the next player who joins).</summary>
    public static void OnPlayerLeft(byte player)
    {
        if (_asked?.from == player) _asked = null;
        if (player == _opponent) EndFromOther(player, false, "Duel cancelled: the other player left.");
    }

    public static string DevAnswer(bool yes) => Answer(yes);

    public static string DevHit(float amount)
    {
        var o = Service?.Opponent; var me = GameInfo.Player;
        if (o == null || me == null) return "no opponent";
        o.TakeDamage(new CombatCalculations.DamageResult { caster = me, target = o, DamageAmount = amount, DamageActionType = "" }, null, MobSync.HealthStat);
        return "hit";
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.TargetContextMenu), nameof(BLINK.RPGBuilder.UI.TargetContextMenu.OnDuelClicked))]
        private static bool DuelClicked(BLINK.RPGBuilder.UI.TargetContextMenu __instance)
        {
            try
            {
                if (CoopManager.Session == null || !PlayerShells.TryGetPlayer(__instance.GetTargetEntity(), out var player)) return true;
                Ask(player);
                __instance.Hide();
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("Duel menu: " + e.Message); return true; }
        }

        /// <summary>
        /// "Duel" in the interaction menu (F) on a player. The menu has no duel option of its own: one of its text options
        /// (the game's prefab, the character icon of a Trade option) whose click is taken here.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.Initialize))]
        private static void DuelOption(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            try
            {
                Options.Clear(); // the menu's options are made anew each time
                if (CoopManager.Session == null || !PlayerShells.TryGetPlayer(__instance.entity, out var player)) return;
                var go = UnityEngine.Object.Instantiate(__instance.TextOptionPrefab, __instance.TextOptionsParent);
                var option = go.GetComponent<NPCInteractionTextOption>();
                option.Initialize("Duel", __instance);
                option.Type = BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType.Trade; // its icon
                __instance.optionSlots?.Add(option);
                var panel = __instance;
                Options[option.Pointer] = () => { Ask(player); try { panel.Hide(); } catch { } };
            }
            catch (Exception e) { Plugin.L.LogWarning("Duel option: " + e.Message); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NPCInteractionTextOption), nameof(NPCInteractionTextOption.Click))]
        private static bool OptionClick(NPCInteractionTextOption __instance)
        {
            if (!Options.TryGetValue(__instance.Pointer, out var act)) return true;
            try { act(); } catch (Exception e) { Plugin.L.LogWarning("Duel option: " + e.Message); }
            return false;
        }

        /// <summary>A shell's defeat is decided where its player is (their health), not by its mirror here.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DuelService), nameof(DuelService.HandleDefeat))]
        private static bool NoShellDefeat(CombatEntity __0, ref bool __result)
        {
            if (!PlayerShells.TryGetPlayer(__0, out _)) return true;
            __result = Service != null && Service.Contains(__0); // a duelist: no death, but no defeat either
            return false;
        }

        /// <summary>Nor its loss by the boundary: only the other side's word ends the duel for a shell.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DuelService), nameof(DuelService.Finish))]
        private static bool NoShellFinish(CombatEntity __0) => _fromNet || !PlayerShells.TryGetPlayer(__0, out _);

        /// <summary>My side ended it (I lost, forfeited, or it was cancelled here): the other side is told.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DuelService), nameof(DuelService.End))]
        private static void Ended(CombatEntity __0, string __1)
        {
            try
            {
                if (_fromNet || _opponent == 255) return;
                byte other = _opponent;
                var me = GameInfo.Player;
                if (__0 != null && me != null && __0.Pointer == me.Pointer) Send(OpLost, other, w => w.Str(__1 ?? ""));
                else if (__0 == null) Send(OpCancel, other, w => w.Str(__1 ?? ""));
                _opponent = 255;
            }
            catch (Exception e) { Plugin.L.LogWarning("Duel end: " + e.Message); }
        }

        /// <summary>The duel's texts name a shell by its template: the player's name.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PvPCombat), nameof(PvPCombat.Name))]
        private static void PlayerName(CombatEntity __0, ref string __result)
        {
            if (PlayerShells.TryGetPlayer(__0, out var p)) __result = Groups.Name(p);
        }

        /// <summary>A shell has no AI to stop.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DuelService), nameof(DuelService.StopNpc))]
        private static bool NoShellAi(CombatEntity __0) => !PlayerShells.TryGetPlayer(__0, out _);

        /// <summary>The duel aims the opponent at the player: for a shell (AI off) just its target field.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.SetTarget))]
        private static bool ShellTarget(MobCombatEntity __instance, CombatEntity __0)
        {
            if (!PlayerShells.TryGetPlayer(__instance, out _)) return true;
            try { __instance.CurrentTarget = __0; } catch { }
            return false;
        }
    }
}
