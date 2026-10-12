using System;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// "Share" a quest from the quest journal with the group (WoW-like): each group player in the world who can take it
/// (neither on it nor ever done with it, its requirements met) gets the game's own quest window offering it,
/// Accept as from its giver; whoever shared hears who took it and who could not.
/// </summary>
internal static class QuestShare
{
    public const byte Kind = 35;
    private const byte OpOffer = 1, OpReply = 2;
    private const byte Accepted = 1, Already = 2, NotEligible = 3, Busy = 4;
    private static readonly NetWriter W = new();

    private static GameObject _button;
    private static RPGQuest _viewed;
    private static int _offered = -1;   // the quest offered to me (its window open)
    private static byte _offeredBy;

    private static CharacterEntries.QuestEntry Entry(int questId)
    {
        var quests = GameInfo.Character?.Quests;
        if (quests != null) foreach (var q in quests) if (q != null && q.questID == questId) return q;
        return null;
    }

    private static bool OnIt(RPGQuest q)
    {
        var e = Entry(q.ID);
        return e != null && (int)e.state is 0 or 5; // on going (tracked or not)
    }

    // ---------------- sharing ----------------

    private static void Share()
    {
        var s = CoopManager.Session;
        if (_viewed == null || s?.MyGroup == null) return;
        W.Reset(); W.U8(Kind); W.U8(OpOffer); W.I32(_viewed.ID);
        s.SendWorld(W.Span);
        Hud.Notify($"You share {_viewed.entryDisplayName}");
    }

    private static void Reply(byte to, int questId, byte answer)
    {
        W.Reset(); W.U8(Kind); W.U8(OpReply); W.U8(to); W.I32(questId); W.U8(answer);
        CoopManager.Session.SendWorld(W.Span);
    }

    private static void OnOffer(byte from, int questId)
    {
        var s = CoopManager.Session;
        if (s == null || !s.InSameGroup(s.LocalId, from) || !GameInfo.InWorld) return;
        var quests = GameDatabase.Instance.GetQuests();
        if (!quests.ContainsKey(questId)) return;
        var q = quests[questId];
        var e = Entry(questId);
        if (e != null && (int)e.state is 0 or 1 or 4 or 5) { Reply(from, questId, Already); return; } // on it, or done with it once (even a repeating one)
        try
        {
            var r = RequirementsManager.Instance.RequirementsMet(GameInfo.Player, q.Requirements);
            if (r != null && !r.Result) { Reply(from, questId, NotEligible); return; }
        }
        catch { }
        var panel = UnityEngine.Object.FindObjectOfType<QuestInteractionPanel>(true);
        if (panel == null || panel.IsOpen()) { Reply(from, questId, Busy); return; }
        try
        {
            panel.Show();
            panel.InitializeQuestContent(q, true); // as its giver offers it: Accept
            _offered = questId; _offeredBy = from;
            Hud.Notify($"{Groups.Name(from)} shares {q.entryDisplayName} with you");
        }
        catch (Exception ex) { Plugin.L.LogWarning("Quest share: " + ex.Message); }
    }

    private static void OnReply(byte from, byte to, int questId, byte answer)
    {
        if (to != CoopManager.Session?.LocalId) return;
        var quests = GameDatabase.Instance.GetQuests();
        string name = quests.ContainsKey(questId) ? quests[questId].entryDisplayName : "the quest";
        string who = Groups.Name(from);
        Hud.Notify(answer switch
        {
            Accepted => $"{who} accepts {name}",
            Already => $"{who} already has {name} or is done with it",
            NotEligible => $"{who} cannot take {name} yet",
            _ => $"{who} is busy",
        });
    }

    public static void Receive(byte from, ref NetReader r)
    {
        switch (r.U8())
        {
            case OpOffer: OnOffer(from, r.I32()); break;
            case OpReply: { byte to = r.U8(); int id = r.I32(); OnReply(from, to, id, r.U8()); break; }
        }
    }

    public static string Dev(int questId)
    {
        var quests = GameDatabase.Instance.GetQuests();
        if (questId == 0) // the first one I am on
            foreach (var e in GameInfo.Character.Quests) if (e != null && (int)e.state is 0 or 5 && quests.ContainsKey(e.questID)) { questId = e.questID; break; }
        if (!quests.ContainsKey(questId)) return "no such quest";
        _viewed = quests[questId];
        Share();
        return "shared " + _viewed.entryName;
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>The journal shows a quest: "Share" next to its tracking button, for a quest I am on, in a group.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(QuestJournalPanel), nameof(QuestJournalPanel.DisplayQuestContent))]
        private static void Shown(QuestJournalPanel __instance, RPGQuest __0)
        {
            try
            {
                _viewed = __0;
                if (_button == null && __instance.trackQuestButton != null)
                {
                    var template = __instance.trackQuestButton.GetComponent<UnityEngine.UI.Button>();
                    if (template != null)
                    {
                        _button = UiKit.CloneButton(template, __instance.trackQuestButton.transform.parent, "Share", Share).gameObject;
                        _button.transform.SetSiblingIndex(__instance.trackQuestButton.transform.GetSiblingIndex() + 1);
                    }
                }
                if (_button != null) _button.SetActive(CoopManager.Session?.MyGroup != null && __0 != null && OnIt(__0));
            }
            catch (Exception e) { Plugin.L.LogWarning("Quest share button: " + e.Message); }
        }

        /// <summary>The shared quest taken: its sharer hears it.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(QuestInteractionPanel), nameof(QuestInteractionPanel.AcceptQuest))]
        private static void Taken(RPGQuest __0)
        {
            try
            {
                if (__0 == null || __0.ID != _offered) return;
                _offered = -1;
                Reply(_offeredBy, __0.ID, Accepted);
            }
            catch { }
        }
    }
}
