using System;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using GameObject = UnityEngine.GameObject;

namespace AfallonCoop.Plugin;

/// <summary>
/// Player groups in the game (the group itself lives in the session, decided by the host):
///  - "Invite to group" on another player (their shell) invites them; the invitee answers in the game's confirmation popup;
///  - this machine reports how many adventurer bot companions the local player has (they are members; pets are not);
///  - the game's own "group full" check includes the co-op group (5 members: players and bots);
///  - the leader can send a member's bot away; it leaves its owner the game's way.
/// Rewards for group members are in MobSync (participants' group, within <see cref="RewardRange"/>).
/// </summary>
internal static class Groups
{
    /// <summary>Group members this close to a kill share it (XP, loot, quest credit), as in WoW.</summary>
    public const float RewardRange = 100f;

    private static float _nextBotCount;

    public static void Wire(AfallonCoop.Session.CoopSession s)
    {
        s.GroupInvited += from =>
        {
            string who = Name(from);
            Hud.Notify($"{who} invites you to a group");
            Ask($"{who} invites you to a group. Join?", () => s.GroupAnswer(true), () => s.GroupAnswer(false));
        };
        s.GroupNotice += Hud.Notify;
        s.GroupBotDismissed += DismissBot;
        s.GroupsChanged += () => { Plugin.L.LogInfo("Groups: " + Describe()); GroupFrames.RefreshAll(); };
        s.ReturnRequested += (scene, x, y, z) => { _returnTo = (scene, new Vector3(x, y, z)); Plugin.L.LogInfo($"Group: return to {x:0},{y:0},{z:0} when in that scene"); };
    }

    public static string Name(byte player)
    {
        var s = CoopManager.Session;
        if (s == null || !s.Players.TryGetValue(player, out var p)) return "#" + player;
        return string.IsNullOrEmpty(p.CharacterName) ? p.Name : p.CharacterName;
    }

    private static Action _yes, _no;
    private static Il2CppSystem.Action _yesIl, _noIl;

    internal static void Ask(string text, Action yes, Action no)
    {
        try
        {
            var popup = ConfirmationPopupManager.Instance;
            if (popup != null)
            {
                // Kept alive here: the game holds only the IL2CPP side of these delegates until the click.
                _yes = () => { Plugin.L.LogInfo("Group popup: yes"); yes(); };
                _no = () => { Plugin.L.LogInfo("Group popup: no"); no(); };
                _yesIl = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(_yes);
                _noIl = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(_no);
                popup.ShowGeneric(text, _yesIl, _noIl);
                return;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Group popup: " + e.Message); }
        Hud.Notify(text + " (no popup available: declined)");
        no();
    }

    /// <summary>Back in the group after a disconnect: where the character was (applied once in the world, in that scene).</summary>
    private static (int scene, Vector3 pos)? _returnTo;
    private static float _inWorldSince = -1;

    private static void ApplyReturn()
    {
        if (!GameInfo.InWorld) { _inWorldSince = -1; return; }
        if (_inWorldSince < 0) _inWorldSince = Time.unscaledTime;
        if (_returnTo == null || Time.unscaledTime - _inWorldSince < 2f) return; // the game places the character first
        var (scene, pos) = _returnTo.Value;
        _returnTo = null;
        if (AfallonCoop.Protocol.StableHash.Of(Dungeons.Place) != scene) { Hud.Notify("Your group is elsewhere (another area)"); return; }
        GameInfo.TeleportPlayer(pos);
        Hud.Notify("Back where you were with your group");
    }

    public static void Update()
    {
        var s = CoopManager.Session;
        if (s != null && CoopManager.Connected) { ApplyReturn(); AssistGroup(); }
        if (s == null || !CoopManager.Connected || Time.unscaledTime < _nextBotCount) return;
        _nextBotCount = Time.unscaledTime + 0.5f;
        s.ReportBots(CountBots());
    }

    /// <summary>The local player's adventurer bot companions (recruited bots; summons and other pets are not members).</summary>
    public static int CountBots()
    {
        var me = GameInfo.Player;
        var pets = me != null ? me.GetCurrentPets() : null;
        if (pets == null) return 0;
        int n = 0;
        foreach (var p in pets)
        {
            try { var npc = p?.GetNPCData(); if (npc != null && (int)npc.npcType == 9 && !p.IsDead()) n++; } catch { }
        }
        return n;
    }

    private static void DismissBot(int npcId)
    {
        var me = GameInfo.Player;
        var pets = me != null ? me.GetCurrentPets() : null;
        if (pets == null) return;
        foreach (var p in pets)
        {
            var m = p?.TryCast<MobCombatEntity>();
            if (m == null || m.GetNPCData()?.ID != npcId) continue;
            Hud.Notify($"The group leader sent {m.GetNPCData().entryDisplayName} away");
            m.LeavePlayerParty();
            return;
        }
    }

    /// <summary>Same group, and close enough to share a kill.</summary>
    public static bool SharesKill(byte participant, Vector3 at)
    {
        var s = CoopManager.Session;
        var me = GameInfo.Player;
        if (s == null || me == null || participant == s.LocalId || !s.InSameGroup(participant, s.LocalId)) return false;
        return Vector3.Distance(me.transform.position, at) <= RewardRange;
    }

    public static string Describe()
    {
        var s = CoopManager.Session;
        if (s == null || s.Groups.Count == 0) return "no groups";
        var parts = new System.Collections.Generic.List<string>();
        foreach (var g in s.Groups)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var m in g.Members)
                names.Add((m.Key == g.LeaderKey ? "*" : "") + (m.Online ? Name(m.Id) : m.Name + "(offline)") + (m.Bots > 0 ? $"+{m.Bots} bots" : ""));
            parts.Add($"group {g.Id} ({g.Size}/5): {string.Join(", ", names)}");
        }
        return string.Join("; ", parts);
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    /// <summary>A remote player (their shell), or a bot companion of one (its copy here): who it is and whose bot.</summary>
    internal static bool Resolve(CombatEntity e, out byte player, out bool bot)
    {
        bot = false;
        if (PlayerShells.TryGetPlayer(e, out player)) return true;
        if (MobSync.TryGetPetOwner(e, out player))
        {
            try { bot = (int)e.GetNPCData().npcType == 9; } catch { }
            return bot;
        }
        return false;
    }

    // ---------------- bot companions look after the whole group ----------------
    //
    // The game's companion (a pet of its player) heals, defends and resurrects only its owner and the owner's other pets.
    // In a co-op group the other players (their shells here) and their bots (copies) count as the same party: a heal or
    // resurrection on them goes to their owner (MobSync), a hit on a mob that attacks them goes to the mob's owner.

    /// <summary>A pet or bot companion of the local player, while in a co-op group.</summary>
    private static bool IsMyCompanionInGroup(BLINK.RPGBuilder.AI.AIEntity ai, out AfallonCoop.Session.GroupInfo group)
    {
        group = null;
        var s = CoopManager.Session;
        var me = GameInfo.Player;
        var e = ai != null ? ai.ThisCombatEntity : null;
        if (s == null || !CoopManager.Connected || me == null || e == null || !e.IsPet()) return false;
        var owner = e.GetOwnerEntity();
        if (owner == null || owner.Pointer != me.Pointer) return false;
        group = s.MyGroup;
        return group != null;
    }

    /// <summary>The other online players of the group (their shells here) and their pets and bots (copies).</summary>
    private static System.Collections.Generic.IEnumerable<CombatEntity> GroupAllies(AfallonCoop.Session.GroupInfo g)
    {
        byte local = CoopManager.Session.LocalId;
        foreach (var id in g.Players)
        {
            if (id == local) continue;
            var shell = PlayerShells.Get(id);
            if (shell != null) yield return shell;
            foreach (var pet in MobSync.PetCopiesOf(id)) yield return pet;
        }
    }

    private static bool _findingEnemy;

    // ---------------- helping the group ----------------
    //
    // The game sends a player's pets at whoever damages that player (CombatManager.CheckIfPetsShouldAttack, from
    // TakeDamage). For a co-op group a mob attacking another member (their shell here), one of their bots, or one of this
    // player's own bots counts the same (the party, as FindEnemyTargetingParty sees it), from the moment the mob goes
    // for them: idle bots of this player join in, by that same rule.

    private const float AssistRange = 40f;
    private static float _nextAssist;

    private static void AssistGroup()
    {
        var s = CoopManager.Session;
        var me = GameInfo.Player;
        if (s?.MyGroup == null || me == null || me.Dead || Time.unscaledTime < _nextAssist) return;
        _nextAssist = Time.unscaledTime + 0.3f;
        var pets = me.GetCurrentPets();
        if (pets == null || pets.Count == 0) return;
        var at = me.transform.position;
        foreach (var mob in MobSync.AllSynced())
        {
            if (mob.Dead || (mob.transform.position - at).sqrMagnitude > AssistRange * AssistRange) continue;
            CombatEntity t = null;
            try { t = mob.GetTarget(); } catch { }
            if (t == null || t.Dead) continue;
            bool mine = false;
            try { var owner = t.GetOwnerEntity(); mine = owner != null && owner.Pointer == me.Pointer; } catch { } // one of this player's own bots
            if (!mine)
            {
                if (!Resolve(t, out var player, out _) && !MobSync.TryGetPetOwner(t, out player)) continue;
                if (player == s.LocalId || !s.InSameGroup(player, s.LocalId)) continue;
            }
            try { CombatManager.Instance.CheckIfPetsShouldAttack(me, mob); } catch { }
        }
    }

    // ---------------- the leash in a fight ----------------
    //
    // The game pulls a pet out of a fight and teleports it to its owner once the owner is further than the follow
    // template's TeleportDistanceThreshold (twice that while the owner fights): about 10/20 m, so a leader stepping
    // back makes the bots blink to them and run back to the mob. In a co-op group the leash is measured from the fight:
    // while a bot's target is alive and a group player is near it, the bot keeps fighting up to FightLeash from its owner.

    /// <summary>Owner distance at which even a fighting bot comes back (the follow template's teleport takes over).</summary>
    private const float FightLeash = 50f;
    /// <summary>A fight counts as the group's while one of its players is this close to the target.</summary>
    private const float FightNear = 30f;

    private static BLINK.RPGBuilder.AI.AIStatePetFollowOwnerTemplate _leashTemplate;
    private static float _leashSaved;

    private static bool GroupFight(BLINK.RPGBuilder.AI.AIEntity ai)
    {
        var e = ai.ThisCombatEntity;
        if (e == null || !e.InCombat) return false;
        CombatEntity t = null;
        try { t = e.GetTarget(); } catch { }
        if (t == null || t.Dead) return false;
        var at = t.transform.position;
        var me = GameInfo.Player;
        if (me != null && (me.transform.position - at).sqrMagnitude < FightNear * FightNear) return true;
        var g = CoopManager.Session.MyGroup;
        foreach (var id in g.Players)
        {
            var shell = id == CoopManager.Session.LocalId ? null : PlayerShells.Get(id);
            if (shell != null && (shell.transform.position - at).sqrMagnitude < FightNear * FightNear) return true;
        }
        return false;
    }

    private static string _kickLabel;

    /// <summary>"Inspect" has no field of its own in the menu: the button that calls OnInspectClicked, found by name.</summary>
    private static void HideInspect(BLINK.RPGBuilder.UI.TargetContextMenu menu)
    {
        foreach (var b in menu.GetComponentsInChildren<UnityEngine.UI.Button>(true))
        {
            if (b == null || b.gameObject == menu.inspectTalentsButton) continue;
            var t = b.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (b.name.IndexOf("Inspect", StringComparison.OrdinalIgnoreCase) >= 0 && b.name.IndexOf("Talent", StringComparison.OrdinalIgnoreCase) < 0
                && b.gameObject.activeSelf)
            {
                b.gameObject.SetActive(false);
                HiddenInspect.Add(b.gameObject);
            }
        }
    }

    private static readonly System.Collections.Generic.List<GameObject> HiddenInspect = new();

    private static void RestoreInspect()
    {
        foreach (var go in HiddenInspect) if (go != null) go.SetActive(true);
        HiddenInspect.Clear();
    }

    // ---------------- summon / travel to, in the target menu of a group member ----------------

    /// <summary>Set by a far member's frame (no entity to open the menu on): the menu opened next is for them.</summary>
    public static byte? FarMenu;
    private static GameObject _summonButton, _travelButton;
    private static byte _menuPlayer;

    /// <summary>"Summon" and "Travel to" (clones of the menu's own Invite button) for that member, or hidden (null).</summary>
    private static void MemberButtons(BLINK.RPGBuilder.UI.TargetContextMenu menu, byte? player)
    {
        if (_summonButton == null && menu.inviteButton != null)
        {
            var template = menu.inviteButton.GetComponent<UnityEngine.UI.Button>();
            var parent = menu.inviteButton.transform.parent;
            int at = menu.inviteButton.transform.GetSiblingIndex() + 1;
            _summonButton = UiKit.CloneButton(template, parent, "Summon", () => { Summon.SummonPlayer(_menuPlayer); menu.Hide(); }).gameObject;
            _travelButton = UiKit.CloneButton(template, parent, "Travel to", () => { Summon.TravelTo(_menuPlayer); menu.Hide(); }).gameObject;
            _summonButton.transform.SetSiblingIndex(at);
            _travelButton.transform.SetSiblingIndex(at + 1);
        }
        if (player is { } p) _menuPlayer = p;
        if (_summonButton != null) _summonButton.SetActive(player != null);
        if (_travelButton != null) _travelButton.SetActive(player != null);
    }

    /// <summary>The target menu at the mouse for a member who has no entity here (a far frame).</summary>
    public static void OpenFarMenu(byte player)
    {
        var menu = BLINK.RPGBuilder.UI.TargetContextMenu.Instance;
        var me = GameInfo.Player;
        if (menu == null || me == null) return;
        FarMenu = player;
        var mouse = UnityEngine.InputSystem.Mouse.current != null ? UnityEngine.InputSystem.Mouse.current.position.ReadValue() : Vector2.zero;
        menu.Show(me, mouse);
    }

    /// <summary>The kick button reads "Leave group" on oneself; its own label otherwise.</summary>
    private static void KickLabel(BLINK.RPGBuilder.UI.TargetContextMenu menu, string text)
    {
        var t = menu.kickButton != null ? menu.kickButton.GetComponentInChildren<TMPro.TMP_Text>(true) : null;
        if (t == null) return;
        _kickLabel ??= t.text;
        t.text = text ?? _kickLabel;
    }

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>The target's right-click menu on a player: invite / remove from the co-op group; the rest later.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.TargetContextMenu), nameof(BLINK.RPGBuilder.UI.TargetContextMenu.Show))]
        private static void MenuForPlayers(BLINK.RPGBuilder.UI.TargetContextMenu __instance, CombatEntity __0)
        {
            try
            {
                var s = CoopManager.Session;
                if (s == null) return;
                void Show(GameObject b, bool on) { if (b != null) b.SetActive(on); }
                KickLabel(__instance, null);
                RestoreInspect();
                MemberButtons(__instance, null);
                PlayerActions.MenuButtons(__instance, null);
                if (FarMenu is { } far)
                {
                    // a far member's frame (no entity here): only what reaches them
                    FarMenu = null;
                    Show(__instance.inviteButton, false); Show(__instance.tradeButton, false); Show(__instance.duelButton, false);
                    Show(__instance.friendButton, false); Show(__instance.inspectTalentsButton, false); Show(__instance.kickButton, false);
                    HideInspect(__instance);
                    MemberButtons(__instance, far);
                    return;
                }
                if (__0 != null && GameInfo.Player != null && __0.Pointer == GameInfo.Player.Pointer)
                {
                    // oneself (own portrait): leave the group
                    bool grouped = s.MyGroup != null;
                    Show(__instance.inviteButton, false); Show(__instance.tradeButton, false); Show(__instance.duelButton, false);
                    Show(__instance.friendButton, false); Show(__instance.inspectTalentsButton, false);
                    HideInspect(__instance);
                    Show(__instance.kickButton, grouped);
                    if (grouped) KickLabel(__instance, "Leave group");
                    return;
                }
                if (!Resolve(__0, out var player, out var bot)) return;
                var g = s.MyGroup;
                bool leader = g != null && g.Leader == s.LocalId;
                bool together = s.InSameGroup(s.LocalId, player);
                Show(__instance.inviteButton, !bot && !together && (g == null || leader));
                Show(__instance.kickButton, together && leader);
                Show(__instance.tradeButton, !bot); // player to player trade (Trade)
                if (!bot && together) MemberButtons(__instance, player);
                if (!bot) PlayerActions.MenuButtons(__instance, player); // Follow, Ride
                Show(__instance.duelButton, !bot); // a duel (Duel)
                Show(__instance.friendButton, !bot); // a friend (Friends)
                if (!bot) { var ft = __instance.friendButton?.GetComponentInChildren<TMPro.TMP_Text>(true); if (ft != null) ft.text = Friends.IsFriend(player) ? "Remove friend" : "Add friend"; }
                Show(__instance.inspectTalentsButton, false);
            }
            catch (Exception e) { Plugin.L.LogWarning("Group menu: " + e.Message); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.TargetContextMenu), nameof(BLINK.RPGBuilder.UI.TargetContextMenu.OnInviteClicked))]
        private static bool Invite(BLINK.RPGBuilder.UI.TargetContextMenu __instance)
        {
            var s = CoopManager.Session;
            if (s == null || !Resolve(__instance.GetTargetEntity(), out var player, out var bot) || bot) return true;
            s.GroupInvite(player);
            __instance.Hide();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.TargetContextMenu), nameof(BLINK.RPGBuilder.UI.TargetContextMenu.OnKickClicked))]
        private static bool Kick(BLINK.RPGBuilder.UI.TargetContextMenu __instance)
        {
            var s = CoopManager.Session;
            var target = __instance.GetTargetEntity();
            if (s != null && target != null && GameInfo.Player != null && target.Pointer == GameInfo.Player.Pointer && s.MyGroup != null)
            {
                s.GroupLeave();
                __instance.Hide();
                return false;
            }
            if (s == null || !Resolve(target, out var player, out var bot)) return true;
            if (bot) s.GroupDismissBot(player, target.GetNPCData().ID);
            else s.GroupKick(player);
            __instance.Hide();
            return false;
        }

        /// <summary>A bot in a group fight is leashed from the fight, not from its owner (see <see cref="FightLeash"/>).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIEntity), "FixedUpdate")]
        private static void LeashFromFight(BLINK.RPGBuilder.AI.AIEntity __instance)
        {
            _leashTemplate = null;
            try
            {
                if (!IsMyCompanionInGroup(__instance, out _) || !GroupFight(__instance)) return;
                var t = __instance.BehaviorTemplate?.DefaultPetStateTemplate?.TryCast<BLINK.RPGBuilder.AI.AIStatePetFollowOwnerTemplate>();
                if (t == null || !t.EnableTeleportation) return;
                var owner = __instance.ThisCombatEntity.GetOwnerEntity();
                _leashTemplate = t;
                _leashSaved = t.TeleportDistanceThreshold;
                t.TeleportDistanceThreshold = Mathf.Max(_leashSaved, owner != null && owner.InCombat ? FightLeash / 2f : FightLeash); // the game doubles it while the owner fights
            }
            catch (Exception e) { Plugin.L.LogWarning("Companion leash: " + e.Message); _leashTemplate = null; }
        }

        /// <summary>The template is shared by all pets: back to the game's value right after this bot's update.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIEntity), "FixedUpdate")]
        private static void LeashRestore()
        {
            if (_leashTemplate == null) return;
            try { _leashTemplate.TeleportDistanceThreshold = _leashSaved; } catch { }
            _leashTemplate = null;
        }

        /// <summary>A healing companion also looks at the group: the lowest share of health below its threshold, in range.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIEntity), nameof(BLINK.RPGBuilder.AI.AIEntity.FindLowestHealthPartyMember))]
        private static void LowestHealth(BLINK.RPGBuilder.AI.AIEntity __instance, float __0, float __1, ref CombatEntity __result)
        {
            try
            {
                if (!IsMyCompanionInGroup(__instance, out var g)) return;
                var from = __instance.transform.position;
                float best = __1;
                if (__result != null) { float max = MobSync.MaxHealth(__result); if (max > 0) best = MobSync.Health(__result) / max; }
                foreach (var a in GroupAllies(g))
                {
                    if (a == null || a.Dead || (a.transform.position - from).sqrMagnitude > __0 * __0) continue;
                    float max = MobSync.MaxHealth(a);
                    if (max <= 0) continue;
                    float share = MobSync.Health(a) / max;
                    if (share < best) { best = share; __result = a; }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Companion heal target: " + e.Message); }
        }

        /// <summary>A companion defends the group: a mob attacking another member (or their bot) when none attacks its owner.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIEntity), nameof(BLINK.RPGBuilder.AI.AIEntity.FindEnemyTargetingParty))]
        private static void EnemyOfGroup(BLINK.RPGBuilder.AI.AIEntity __instance, CombatEntity __0, ref CombatEntity __result)
        {
            if (_findingEnemy || __result != null || __0 == null || GameInfo.Player == null || __0.Pointer != GameInfo.Player.Pointer) return;
            try
            {
                if (!IsMyCompanionInGroup(__instance, out var g)) return;
                _findingEnemy = true;
                foreach (var id in g.Players)
                {
                    var shell = id == CoopManager.Session.LocalId ? null : PlayerShells.Get(id);
                    if (shell == null) continue;
                    var enemy = __instance.FindEnemyTargetingParty(shell); // the game's own rule, for that player's party
                    if (enemy != null) { __result = enemy; return; }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Companion defend: " + e.Message); }
            finally { _findingEnemy = false; }
        }

        /// <summary>No room for another bot companion when the co-op group is full (the game asks before recruiting).</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.IsGroupFull))]
        private static void GroupFull(ref bool __result)
        {
            try
            {
                var s = CoopManager.Session;
                if (!__result && s != null && CoopManager.Connected && s.MyGroup != null && s.FreeGroupSlots <= 0) __result = true;
            }
            catch { }
        }
    }
}
