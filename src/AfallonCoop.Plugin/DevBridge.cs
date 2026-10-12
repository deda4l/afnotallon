using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using BLINK.RPGBuilder.Managers;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Test automation channel. Each game copy polls BepInEx/AfCoop_cmd_&lt;pid&gt;.txt (one command per line), runs the
/// commands on the main thread, deletes the file, and appends results to AfCoop_out_&lt;pid&gt;.txt.
/// Lets an agent or a script drive two copies on one PC without fighting over keyboard focus.
/// </summary>
internal static class DevBridge
{
    private static readonly int Pid = Process.GetCurrentProcess().Id;
    private static readonly string CmdPath = Path.Combine(Paths.BepInExRootPath, $"AfCoop_cmd_{Pid}.txt");
    private static readonly string OutPath = Path.Combine(Paths.BepInExRootPath, $"AfCoop_out_{Pid}.txt");
    private static float _next;

    public static void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.5f;
        if (!File.Exists(CmdPath)) return;
        string[] lines;
        try { lines = File.ReadAllLines(CmdPath); File.Delete(CmdPath); }
        catch { return; }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string result;
            try { result = Run(line); }
            catch (Exception e) { result = "ERROR " + e.GetType().Name + ": " + e.Message; }
            File.AppendAllText(OutPath, $"{DateTime.Now:HH:mm:ss} > {line}{Environment.NewLine}{result}{Environment.NewLine}");
        }
    }

    /// <summary>The commands as this source has them: each `case "x":` with the comment on its line.</summary>
    private static string Help(string part)
    {
        using var st = typeof(DevBridge).Assembly.GetManifestResourceStream("AfallonCoop.DevCommands");
        if (st == null) return "no command list in this build";
        var src = new StreamReader(st).ReadToEnd();
        var sb = new System.Text.StringBuilder();
        foreach (var raw in src.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("case \"")) continue;
            var names = System.Text.RegularExpressions.Regex.Matches(line, "case \"([a-z0-9_]+)\":").Select(m => m.Groups[1].Value).ToList();
            if (names.Count == 0) continue;
            int c = line.IndexOf("// ", StringComparison.Ordinal);
            var note = c >= 0 ? line.Substring(c + 3) : "";
            var entry = string.Join(", ", names) + (note.Length > 0 ? " - " + note : "");
            if (part.Length > 0 && entry.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
            sb.AppendLine(entry);
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : "nothing matches";
    }

    private static string Run(string line)
    {
        var sp = line.IndexOf(' ');
        var cmd = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
        var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
        var mm = MainMenuManager.Instance;

        switch (cmd)
        {
            case "help": return Help(arg); // help [part]: the commands (with their notes) whose name or note has that part
            case "scenes": return Dungeons.DevScenes(); // all game scenes: id, name, which are challenge scenes
            case "challenge": return Challenges.Dev(arg); // challenge [clock | skip <s> | types | logic | tree <path part> [depth]]: the challenge stone HUD here and what it reads, its clock, the scene's behaviour types, any object's components
            case "status":
            {
                var s = CoopManager.Session;
                var p = GameInfo.Player;
                return $"mode={CoopManager.Mode} state={s?.State} players={s?.PlayerCount} scene='{GameInfo.SceneName}' " +
                       $"inWorld={p != null} pos={(p != null ? p.transform.position.ToString() : "-")} err='{CoopManager.LastError}' " +
                       $"players=[{(s == null ? "" : string.Join("; ", s.Players.Values.Select(x => $"#{x.Id} {x.Name}/{x.CharacterName}/{x.Scene}")))}]";
            }
            case "host": if (arg == "steam") CoopManager.HostSteam(); else CoopManager.HostLan(); return "ok";
            case "join": CoopManager.JoinAddress(arg); return "ok";
            // The menu's way: choose how to host / where to join, then "play" opens or joins the session.
            case "phost": CoopManager.PrepareHost(arg == "steam" ? CoopMode.HostSteam : CoopMode.HostLan); return "ok";
            case "pjoin": { var t = arg; CoopManager.PrepareJoin(t, () => CoopManager.JoinAddress(t)); return "ok"; }
            case "leave": CoopManager.Leave(); return "ok";
            case "mainmenu": UnityEngine.Object.FindObjectOfType<GameState>().BackToMainMenu(); return "ok"; // the game's way back to the main menu (as from the pause menu)
            case "newchar": mm.ClickNewChar(); return "ok";
            case "name": mm.characterNameIF.text = arg; return "ok";
            case "create": mm.CreateCharacter(); return "ok";
            case "continue": mm.ClickContinue(); return "ok";
            case "play":
                mm.ClickContinue();
                mm.SelectCharacter(arg);
                mm.PlaySelectedCharacter();
                return "ok";
            case "dump": DevTools.Dump(); return "ok";
            case "anim":
                return string.Join(" | ", Puppets.Visible().Select(v => $"#{v.id} pos={v.tr.position}{Puppets.Describe(v.id)}"));
            case "tp":
            {
                var v = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                GameInfo.TeleportPlayer(new Vector3(v[0], v[1], v[2]));
                return "ok";
            }
            case "tpally": DevTools.TeleportToNextAlly(); return "ok";
            case "chat": CoopManager.Session?.SendChat(arg); return "ok";
            case "say":
            {
                var input = UnityEngine.Object.FindObjectOfType<PlayerChatInput>();
                return input == null ? "no chat input" : "sent=" + input.TrySendMessage(arg);
            }
            case "races":
            {
                var sb = new System.Text.StringBuilder();
                var races = GameDatabase.Instance.GetRaces();
                foreach (var kv in races)
                {
                    sb.Append($"race {kv.Key} '{kv.Value.entryName}':");
                    foreach (var g in kv.Value.Genders) sb.Append($" [{g.Gender?.entryName} key={g.PrefabKey}]");
                    sb.AppendLine();
                }
                return sb.ToString();
            }
            case "race": mm.SelectRace(int.Parse(arg)); return "ok";
            case "gender": mm.SelectGender(int.Parse(arg)); return "ok";
            case "class": mm.SelectClass(int.Parse(arg)); return "ok";
            case "options":
            {
                var o = UnityEngine.Object.FindObjectOfType<OptionsPanel>();
                if (o == null) return "no options panel";
                if (arg == "show") o.Show(); else if (arg == "hide") o.Hide();
                return $"open={o.IsOpen()} timeScale={Time.timeScale}";
            }
            case "prof":
            {
                var rep = Prof.Report(reset: arg == "reset");
                foreach (var v in Puppets.Visible())
                    rep += $"  puppet #{v.id}: children={v.tr.GetComponentsInChildren<Transform>(true).Length} particles={v.tr.GetComponentsInChildren<ParticleSystem>(true).Length} audio={v.tr.GetComponentsInChildren<AudioSource>(true).Length}" + Environment.NewLine;
                return rep;
            }
            case "mobs":
            {
                var sb = new System.Text.StringBuilder();
                var player = GameInfo.Player;
                var me = player != null ? player.transform.position : Vector3.zero;
                float radius = arg.Length > 0 ? float.Parse(arg) : 60f;
                foreach (var spw in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.AI.NPCSpawner>())
                {
                    if (spw == null || Vector3.Distance(spw.transform.position, me) > radius) continue;
                    var sid = spw.GetComponent<BLINK.RPGBuilder.WorldPersistence.SaverIdentifier>();
                    sb.Append($"spawner '{spw.name}' id={(sid != null ? sid.GetIdentifier() : "-")} pos={spw.transform.position} active={spw.IsActive} npcs={spw.CurrentNPCs?.Count}:");
                    if (spw.CurrentNPCs != null)
                        foreach (var n in spw.CurrentNPCs)
                            if (n != null) sb.Append($" [{n.name} lvl{n.GetLevel()} pos={n.transform.position}]");
                    sb.AppendLine();
                }
                return sb.ToString();
            }
            case "ctrl":
            {
                var sb = new System.Text.StringBuilder("local: " + Puppets.AnimatorReport(GameInfo.PlayerAnimator));
                foreach (var v in Puppets.Visible())
                    if (Puppets.TryGetVisual(v.id, out _, out var pa)) sb.Append(Environment.NewLine + $"puppet #{v.id}: " + Puppets.AnimatorReport(pa));
                sb.Append(Environment.NewLine + $"fx sent={FxSync.Sent} played={FxSync.Played} missing={FxSync.Missing}");
                return sb.ToString();
            }
            case "abilities":
            {
                var cd = GameInfo.Character;
                var db = GameDatabase.Instance.GetAbilities();
                var sb = new System.Text.StringBuilder();
                if (cd?.Abilities != null)
                    foreach (var a in cd.Abilities)
                        if (a != null && a.known) sb.Append($"{a.ID} '{(db.ContainsKey(a.ID) ? db[a.ID].entryName : "?")}' rank={a.rank}; ");
                if (cd?.ActionAbilities != null)
                    foreach (var a in cd.ActionAbilities)
                        if (a?.ability != null) sb.Append(Environment.NewLine + $"action key={a.key} name={a.actionKeyName} ability={a.ability.ID} '{a.ability.entryName}'");
                return sb.ToString();
            }
            case "cast": GameActionsManager.Instance.UseAbility(GameInfo.Player, int.Parse(arg)); return "ok";
            case "states":
            {
                var sb = new System.Text.StringBuilder();
                var states = GameInfo.Player?.GetStates();
                if (states != null)
                    foreach (var s in states)
                    {
                        if (s == null) continue;
                        sb.Append($"effect {s.stateEffectID} '{s.stateName}' type={s.stateEffect?.effectType} rank={s.effectRankIndex} " +
                                  $"t={s.stateCurDuration:0.0}/{s.stateMaxDuration:0.0} go={(s.stateEffectGO != null ? DevTools.PathOf(s.stateEffectGO.transform) : "-")}");
                        var vfx = s.effectRank?.VisualEffectEntries;
                        if (vfx != null)
                            foreach (var v in vfx)
                                sb.Append($" [vfx '{v?.Template?.entryName}' act={v?.ActivationType} endless={v?.Endless} dur={v?.Duration}]");
                        sb.AppendLine();
                    }
                return sb.ToString();
            }
            case "dbfx": // effects whose visuals outlive a one-shot (auras); arg filters by name
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in GameDatabase.Instance.GetEffects())
                {
                    var e = kv.Value;
                    if (e?.ranks == null || (arg.Length > 0 && e.entryName.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    var r0 = e.ranks.Count > 0 ? e.ranks[0] : null;
                    if (r0?.VisualEffectEntries == null || r0.VisualEffectEntries.Count == 0) continue;
                    bool lasting = false;
                    foreach (var v in r0.VisualEffectEntries) if (v != null && (v.Endless || v.Duration > 3f)) lasting = true;
                    if (!lasting && arg.Length == 0) continue;
                    sb.Append($"effect {kv.Key} '{e.entryName}' type={e.effectType} dur={e.duration}:");
                    foreach (var v in r0.VisualEffectEntries)
                        sb.Append($" [{v?.Template?.entryName} act={v?.ActivationType} endless={v?.Endless} dur={v?.Duration}]");
                    sb.AppendLine();
                }
                return sb.ToString();
            }
            case "weapons": // where each weapon model is attached: owner vs puppets
            {
                var sb = new System.Text.StringBuilder();
                void One(string who, BLINK.RPGBuilder.Characters.PlayerAppearance a)
                {
                    if (a == null) { sb.AppendLine(who + ": no appearance"); return; }
                    sb.Append($"{who}: lastArmed={a.LastArmed} npc={a.IsNPCModel} race={a.VisualRaceID} gender={a.VisualGender}");
                    foreach (var w in new[] { a.weapon1GO, a.weapon2GO, a.weapon3GO })
                        if (w != null) sb.Append($" | {w.name} @ {w.transform.parent?.name} lp={w.transform.localPosition} lr={w.transform.localEulerAngles}");
                    sb.AppendLine();
                }
                One("local", GameInfo.Player?.appearance);
                foreach (var (id, tr) in Puppets.Visible()) One($"puppet #{id}", tr.GetComponentInChildren<BLINK.RPGBuilder.Characters.PlayerAppearance>(true));
                return sb.ToString();
            }
            case "spawn": return DevTools.SpawnNpc(arg);
            case "mobsync": return MobSync.Describe();
            case "hit": { var a = arg.Split(' ', 2); return MobSync.DevHit(a[0].Length > 0 ? float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture) : 25f, a.Length > 1 ? a[1] : ""); } // hit <amount> [mob name]
            case "sicbot": { var a2 = arg.Split(' ', 2); return MobSync.DevSicBot(a2[0], a2.Length > 1 ? a2[1] : ""); } // sicbot <bot name part> <mob name part>
            case "sic": { var a = arg.Split(' ', 2); return MobSync.DevSic(byte.Parse(a[0]), a.Length > 1 ? a[1] : ""); } // sic <player> [mob name]
            case "me": return MobSync.DevMe();
            case "die": return MobSync.DevDie();
            case "hurt": return MobSync.DevHurt(string.IsNullOrEmpty(arg) ? 50f : float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture)); // hurt [percent of max hp]
            case "healp": { var a = arg.Split(' '); return MobSync.DevHealPlayer(byte.Parse(a[0]), a.Length > 1 ? float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) : 50f); } // healp <player> [amount]
            case "respawn": return MobSync.DevRespawn();
            case "frames": return GroupFrames.Describe();
            case "uifind": return DevTools.UiFind(arg);
            case "uikids": // uikids <full path>: children of a UI object, with size and components
            {
                var go = GameObject.Find(arg);
                if (go == null) return "not found (inactive objects need their parent path)";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    var c = go.transform.GetChild(i);
                    var rt = c.GetComponent<RectTransform>();
                    var img = c.GetComponent<UnityEngine.UI.Image>();
                    sb.AppendLine($"{c.name} active={c.gameObject.activeSelf} size={(rt != null ? rt.rect.size.ToString() : "-")} pos={(rt != null ? rt.anchoredPosition.ToString() : "-")}{(img != null && img.sprite != null ? $" sprite='{img.sprite.name}'" : "")} [{string.Join(",", c.GetComponents<Component>().Select(x => x.GetIl2CppType().Name))}]");
                }
                return sb.ToString();
            }
            case "uiat": { var v = arg.Split(' '); return DevTools.UiAt(float.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture)); } // uiat <x 0..1> <y 0..1 from bottom>
            case "groups": return Groups.Describe() + $" | my bots {Groups.CountBots()}, free slots {CoopManager.Session?.FreeGroupSlots}";
            case "ginvite": CoopManager.Session?.GroupInvite(byte.Parse(arg)); return "ok";
            case "ganswer": CoopManager.Session?.GroupAnswer(arg != "no"); return "ok";
            case "gleave": CoopManager.Session?.GroupLeave(); return "ok";
            case "gkick": CoopManager.Session?.GroupKick(byte.Parse(arg)); return "ok";
            case "gbot": { var a = arg.Split(' '); CoopManager.Session?.GroupDismissBot(byte.Parse(a[0]), int.Parse(a[1])); return "ok"; } // gbot <owner> <npc id>
            case "targets": return MobSync.DevTargets();
            case "debuff": return MobSync.DevDebuff(arg);
            case "summon": Summon.SummonPlayer(byte.Parse(arg)); return "asked"; // summon <player id>
            case "setrole": // setrole <0 damage|1 tank|2 healer>: as the finder window's role choice
                UnityEngine.Object.FindObjectOfType<BLINK.RPGBuilder.Dungeons.DungeonFinderPanel>(true).SavePreferredRole((BLINK.RPGBuilder.AI.AdventurerRole)int.Parse(arg));
                return "preferred " + BLINK.RPGBuilder.Dungeons.DungeonFinderService.Instance.Data.PreferredRole;
            case "roles": return string.Join(" | ", CoopManager.Session.Players.Values.Select(p => $"#{p.Id} {p.CharacterName} role={p.Role}"));
            case "summonanswer": return Summon.DevAnswer(); // accept the last summon shown
            case "duel": Duel.Ask(byte.Parse(arg)); return "asked"; // duel <player id>
            case "grouploot": return GroupLoot.Describe();
            case "auction": return AuctionSync.Describe();
            case "ahpost": case "ahbuy": case "ahlist": case "ahmail": case "ahclaim": return AuctionSync.Dev(cmd, arg);
            case "friend": Friends.Toggle(byte.Parse(arg)); return "friend=" + Friends.IsFriend(byte.Parse(arg)); // friend <player id>: add / forget
            case "friendlist": // friendlist [find]: the rows the Friends window shows (player rows first)
            {
                var l = BLINK.RPGBuilder.Social.AdventurerFriends.Listings(arg == "find", "");
                var rows = new System.Collections.Generic.List<string>();
                foreach (var x in l) rows.Add($"{x.Key}:{x.Name} lvl{x.Level} {x.ClassName} [{x.Region}] {x.Status} friend={x.IsFriend} party={x.InParty}");
                return string.Join(" | ", rows.Take(8));
            }
            case "wq":
                return WorldQuests.Dev();
            case "goto": { var a = arg.Split(' '); var ci = System.Globalization.CultureInfo.InvariantCulture; var g = a[0] + (a.Length > 4 ? "#" + a[4] : ""); return Dungeons.GoTo(a[0].Replace('_', ' ') + (a.Length > 4 ? "#" + a[4] : ""), new Vector3(float.Parse(a[1], ci), float.Parse(a[2], ci), float.Parse(a[3], ci))) ? "going" : "refused"; } // goto <scene_name> x y z [instance]
            case "npcabilities": // npcabilities <name part>: a live NPC's AI abilities and how they execute
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var e in GameState.combatEntities)
                {
                    string nm; try { nm = e?.GetNPCData()?.entryName; } catch { nm = null; }
                    var ai = e?.GetComponent<BLINK.RPGBuilder.AI.AIEntity>();
                    if (nm == null || nm.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0 || ai == null) continue;
                    parts.Add($"{nm} copy={MobSync.IsCopy(e)}:");
                    foreach (var kv in ai.GetAbilities())
                    {
                        var ab = kv.Value?.ability;
                        if (ab == null) continue;
                        var rk = ab.ranks != null && ab.ranks.Count > 0 ? ab.ranks[Math.Clamp(kv.Value.abilityRank, 0, ab.ranks.Count - 1)] : null;
                        parts.Add($"  {ab.ID}:{ab.entryName} type {rk?.targetType} act {rk?.activationType} cast {rk?.castTime} tele {rk?.GroundTelegraphEnabled} onHit {rk?.TelegraphSpawnOnHit} channel {rk?.channelTime}");
                    }
                    break;
                }
                return string.Join(" | ", parts);
            }
            case "abilitydump": // abilitydump <id>: how an ability executes, and what it chains into
            {
                var db = GameDatabase.Instance.GetAbilities();
                var effects = GameDatabase.Instance.GetEffects();
                var parts = new System.Collections.Generic.List<string>();
                int id = int.Parse(arg);
                for (int depth = 0; depth < 4 && db.ContainsKey(id); depth++)
                {
                    var ab = db[id];
                    var rk = ab.ranks?.Count > 0 ? ab.ranks[0] : null;
                    if (rk == null) break;
                    var eff = new System.Collections.Generic.List<string>();
                    if (rk.effectsApplied != null) foreach (var e in rk.effectsApplied) if (e != null) eff.Add($"{e.effectID}:{(effects.ContainsKey(e.effectID) ? effects[e.effectID].entryName + "/" + effects[e.effectID].effectType : "?")} on {e.target} delay {e.delay}");
                    parts.Add($"{ab.ID}:{ab.entryName} type {rk.targetType} act {rk.activationType} cast {rk.castTime} channel {rk.channelTime} aoe r{rk.AOERadius} x{rk.AOEHitCount}/{rk.AOEHitInterval} ground r{rk.groundRadius} range {rk.groundRange} hits {rk.groundHitCount}/{rk.groundHitInterval} t{rk.groundHitTime} groundVfx {rk.groundVisualEffect?.name}/{rk.groundVisualEffectDuration} hitVfx {rk.hitEffect?.name} projVfx {rk.projectileEffect?.name} vfxEntries {rk.VisualEffectEntries?.Count} actions {rk.Actions?.Count} effects [{string.Join("; ", eff)}] extra {rk.extraAbilityExecutedID}/{rk.extraAbilityExecutedActivationType}");
                    if (rk.extraAbilityExecutedID <= 0 || rk.extraAbilityExecutedID == id) break;
                    id = rk.extraAbilityExecutedID;
                }
                return string.Join(" || ", parts);
            }
            case "hiteffect": return BossSync.DevHitEffect(int.Parse(arg)); // hiteffect <ability id>
            case "takequest": // takequest <quest id>: as Accept in the quest window
            {
                var q = GameDatabase.Instance.GetQuests()[int.Parse(arg)];
                UnityEngine.Object.FindObjectOfType<QuestInteractionPanel>(true).AcceptQuest(q);
                return "took " + q.entryName;
            }
            case "myquests":
            {
                var db = GameDatabase.Instance.GetQuests();
                var rows = new System.Collections.Generic.List<string>();
                foreach (var e in GameInfo.Character.Quests) if (e != null) rows.Add($"{e.questID}:{(db.ContainsKey(e.questID) ? db[e.questID].entryName : "?")}={e.state}");
                return rows.Count + ": " + string.Join(" | ", rows.Take(30));
            }
            case "sharequest": return QuestShare.Dev(int.Parse(arg)); // sharequest <quest id>
            case "pa": return PlayerActions.Dev(arg); // pa follow <id> | pa ride <id> | pa off | pa
            case "flights": // flight masters here and their stops
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var fm in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.Travel.FlightMaster>(true))
                {
                    var stops = new System.Collections.Generic.List<string>();
                    if (fm.Network?.Stops != null) foreach (var st in fm.Network.Stops) if (st != null) stops.Add(st.ID);
                    parts.Add($"master '{fm.MasterName}' stop {fm.StopID} at {fm.transform.position} net {fm.Network?.NetworkID} -> [{string.Join(",", stops)}]");
                }
                return parts.Count == 0 ? "no flight master here" : string.Join(" | ", parts);
            }
            case "fly": // fly <destination stop id>: from the nearest flight master, as its window would
            {
                var me = GameInfo.Player;
                foreach (var e in GameState.combatEntities) // a flight master NPC becomes one when spoken to
                {
                    string nm; try { nm = e?.GetNPCData()?.entryName; } catch { nm = null; }
                    if (nm != null && nm.Contains("Skywarden") && Vector3.Distance(e.transform.position, me.transform.position) < 15f)
                        try { BLINK.RPGBuilder.Travel.FlightMaster.OpenForNPC(e); } catch { }
                }
                BLINK.RPGBuilder.Travel.FlightMaster best = null; float bd = float.MaxValue;
                foreach (var fm in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.Travel.FlightMaster>())
                {
                    float d = Vector3.Distance(fm.transform.position, me.transform.position);
                    if (d < bd) { bd = d; best = fm; }
                }
                if (best == null) return "no flight master";
                return $"from {best.StopID} ({bd:0} m): " + BLINK.RPGBuilder.Travel.FlightPathRide.TryDepart(best, arg);
            }
            case "bossmechanics": // bosses and elites, by what their fights do (phases, adds, forced movement, channels, ground, hit effects)
            {
                var db = GameDatabase.Instance;
                var abilities = db.GetAbilities();
                var effects = db.GetEffects();
                var npcs = db.GetNPCs();
                var lines = new System.Collections.Generic.List<string>();
                foreach (var kv in npcs)
                {
                    var npc = kv.Value;
                    if (npc == null || (int)npc.npcType is not (1 or 3)) continue;
                    if (arg.Length > 0 && npc.entryName.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var tags = new System.Collections.Generic.SortedSet<string>();
                    int phases = npc.Phases?.Count ?? 0;
                    if (phases > 1) tags.Add($"phases {phases}");
                    if (npc.Phases != null)
                        foreach (var ph in npc.Phases)
                        {
                            var pt = ph?.PhaseTemplate;
                            if (pt == null) continue;
                            if (ph.Preset != null && phases > 1) tags.Add("preset swap");
                            if (pt.ActionsTemplate?.PhaseActions != null)
                                foreach (var pa in pt.ActionsTemplate.PhaseActions)
                                    if (pa?.GameActionsTemplate?.GameActions != null)
                                        foreach (var ga in pa.GameActionsTemplate.GameActions)
                                            if (ga != null) tags.Add(ga.type == GameActionsData.GameActionType.NPC ? $"phase action NPC {ga.NPCAction} #{ga.NPCID}" : $"phase action {ga.type}");
                            if (pt.PotentialBehaviors == null) continue;
                            foreach (var pb in pt.PotentialBehaviors)
                            {
                                var pas = pb?.BehaviorTemplate?.PotentialAbilities;
                                if (pas == null) continue;
                                foreach (var pa in pas)
                                {
                                    var list = pa?.AbilitiesTemplate?.Abilities;
                                    if (list == null) continue;
                                    foreach (var a in list)
                                    {
                                        if (a == null || !abilities.ContainsKey(a.abilityID)) continue;
                                        var ab = abilities[a.abilityID];
                                        var rk = ab.ranks?.Count > 0 ? ab.ranks[Math.Clamp(a.abilityRank, 0, ab.ranks.Count - 1)] : null;
                                        if (rk == null) continue;
                                        int tt = (int)rk.targetType;
                                        if (tt == 6) tags.Add($"ground: {ab.entryName}");
                                        if (rk.channelTime > 0.05f && (int)rk.activationType == 2) tags.Add($"channel: {ab.entryName}");
                                        if (rk.hitEffect != null) tags.Add($"hit effect: {ab.entryName} ({rk.hitEffect.name})");
                                        if (rk.TelegraphSpawnOnHit) tags.Add($"pool on hit: {ab.entryName}");
                                        if (rk.Actions != null && rk.Actions.Count > 0) tags.Add($"ability actions: {ab.entryName}");
                                        if (rk.effectsApplied != null)
                                            foreach (var e in rk.effectsApplied)
                                                if (e != null && effects.ContainsKey(e.effectID))
                                                {
                                                    int et = (int)effects[e.effectID].effectType;
                                                    if (et is 10 or 16 or 17) tags.Add($"{(et == 10 ? "teleport" : et == 16 ? "knockback" : "motion")}: {ab.entryName}");
                                                    if (et == 14) tags.Add($"summons pet: {ab.entryName}");
                                                    if (et == 22) tags.Add($"game action effect: {ab.entryName}");
                                                }
                                    }
                                }
                            }
                        }
                    if (tags.Count > 0) lines.Add($"{npc.ID}:{npc.entryName} [{npc.npcType}] — {string.Join("; ", tags)}");
                }
                return string.Join(" || ", lines);
            }
            case "bosstrace": return BossTrace.Set(arg);
            case "telegraphs": return BossTrace.Abilities();
            case "bosses": { var t = BLINK.RPGBuilder.World.DungeonTimerManager.Instance; if (t?.BossNPCs == null) return "no boss list here"; var l = new System.Collections.Generic.List<string>(); foreach (var b in t.BossNPCs) if (b != null) l.Add($"{b.ID}:{b.entryName}"); return string.Join(" | ", l); }
            case "house": return Housing.Dev();
            case "props": // the game's properties
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var kv in GameDatabase.Instance.GetProperties()) parts.Add($"{kv.Key}:{kv.Value.entryName} {kv.Value.propertyType} {kv.Value.purchasePrice}");
                return string.Join(" | ", parts);
            }
            case "ownhouse": // ownhouse <property id>: as bought (test)
            {
                var cd = BLINK.RPGBuilder.Characters.Character.Instance.CharacterData;
                cd.OwnedProperties.Add(new CharacterEntries.PropertyEntry { propertyID = int.Parse(arg) });
                RPGBuilderJsonSaver.SaveCharacterData();
                BLINK.RPGBuilder.Managers.PropertyManager.PropertyOwnershipChanged?.Invoke();
                return "owned " + arg;
            }
            case "furnish": // furnish <property id> <item id>: a piece where I stand (test)
            {
                var a = arg.Split(' ');
                var cd = BLINK.RPGBuilder.Characters.Character.Instance.CharacterData;
                var me = GameInfo.Player;
                cd.PlacedFurniture.Add(new CharacterEntries.FurniturePlacementEntry { propertyID = int.Parse(a[0]), itemID = int.Parse(a[1]), position = me.transform.position + me.transform.forward * 2f, rotation = Vector3.zero });
                RPGBuilderJsonSaver.SaveCharacterData();
                foreach (var ar in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.World.PropertyRemodelArea>(true)) if (ar.PropertyID == int.Parse(a[0])) ar.RespawnFurniture();
                return "placed";
            }
            case "coowner": return Housing.DevCoOwner(byte.Parse(arg)); // coowner <player id>: toggle
            case "build": // build <property id> | build off: the game's build mode there, as its prompt enters it
            {
                var bm = BuildingModeManager.Instance;
                if (arg == "off") { bm.ExitBuildMode(); return "active=" + bm.IsActive; }
                foreach (var a in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.World.PropertyRemodelArea>(true))
                    if (a.PropertyID == int.Parse(arg)) { bm.EnterBuildMode(a); return "active=" + bm.IsActive; }
                return "no such property here";
            }
            case "houses": // properties here, who owns them, furniture items and what their prefabs carry
            {
                var parts = new System.Collections.Generic.List<string>();
                foreach (var a in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.World.PropertyRemodelArea>(true))
                    parts.Add($"area '{a.name}' prop {a.PropertyID} pieces {a.spawnedFurniture?.Count} owned {BLINK.RPGBuilder.Managers.PropertyManager.Instance?.IsOwned(a.PropertyID)} at {a.transform.position} initial {a.initialFurniture?.Count}");
                foreach (var sg in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.World.PropertyForSaleSign>(true))
                    parts.Add($"sign '{sg.name}' prop {sg.property?.ID}:{sg.property?.entryName} at {sg.transform.position} active {sg.gameObject.activeInHierarchy} visual {sg.SignVisualObject?.activeInHierarchy} owned {BLINK.RPGBuilder.Managers.PropertyManager.Instance?.IsOwned(sg.property.ID)}");
                var cd = BLINK.RPGBuilder.Characters.Character.Instance?.CharacterData;
                if (cd != null) parts.Add($"mine: props {cd.OwnedProperties?.Count} furniture {cd.PlacedFurniture?.Count} claimed {cd.ClaimedInitialFurniture?.Count}");
                if (arg == "items")
                    foreach (var kv in GameDatabase.Instance.GetItems())
                    {
                        var it = kv.Value;
                        if (it == null || string.IsNullOrEmpty(it.buildingPrefabKey)) continue;
                        var comps = new System.Collections.Generic.List<string>();
                        try { var go = it.GetBuildingPrefab(); if (go != null) foreach (var c in go.GetComponentsInChildren<Component>(true)) { var t = c.GetIl2CppType().Name; if (!comps.Contains(t) && t is not ("Transform" or "MeshFilter" or "MeshRenderer" or "BoxCollider" or "MeshCollider" or "LODGroup")) comps.Add(t); } } catch { }
                        parts.Add($"{it.ID}:{it.entryName} [{string.Join(",", comps)}]");
                    }
                return string.Join(" | ", parts);
            }
            case "mail": return Mail.Dev(arg); // mail: recipients and bag items | mail open: the letter window | mail <to> <item or -1> [count] [gold] [text]: send
            case "worldrandom": return WorldRandom.Dev(); // the seed and what each random activator (camps, meteors, roadblocks, invasions) picked here
            case "rules":
                return WorldRules.Dev();
            case "corruption":
                return Corruption.Dev();
            case "corrupt": // corrupt <level> [affix,affix...]: as a token at the altar
            {
                var a = arg.Split(' ');
                var affixes = new System.Collections.Generic.List<int>();
                if (a.Length > 1) foreach (var x in a[1].Split(',')) affixes.Add(int.Parse(x));
                return Corruption.DevStart(int.Parse(a[0]), affixes);
            }
            case "heroic": // heroic on|off: the game's own switch (the host's is the world's)
                if (arg.Length > 0) BLINK.RPGBuilder.Managers.HeroicTierManager.SetActive(arg == "on");
                return $"heroic {BLINK.RPGBuilder.Managers.HeroicTierManager.IsActive} live {BLINK.RPGBuilder.Managers.HeroicTierManager.IsLive} hp x{BLINK.RPGBuilder.Managers.HeroicTierManager.GetCurrentHealthMultiplier():0.##}";
            case "meter": // the damage meter's rows: name, damage, healing
            {
                var m = UnityEngine.Object.FindObjectOfType<DPSMeterManager>(true);
                if (m?.activeEntries == null) return "no meter";
                var rows = new System.Collections.Generic.List<string>();
                foreach (var e in m.activeEntries) rows.Add($"{SafeName(e.entity)}: dmg {e.totalDamage:0} heal {e.totalHealing:0}");
                return $"tracking={m.isTracking} elapsed={(m.isTracking ? UnityEngine.Time.time - m.combatStartTime : m.encounterElapsed):0.0} | " + string.Join(" | ", rows);
            }
            case "loottoggle": GroupLoot.Toggle(int.Parse(arg)); return "toggled"; // loottoggle <rarity index>: the leader switches it
            case "rolltest": return GroupLoot.DevFound(arg); // rolltest [item id]: a valuable drop for the group
            case "rollchoose": return GroupLoot.DevChoose(arg); // rollchoose need|greed|pass
            case "rarities": // the rarity order (economy settings) and how many items each rarity has; roll settings
            {
                var eco = GameDatabase.Instance.GetEconomySettings();
                var counts = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var kv in GameDatabase.Instance.GetItems()) { var n = kv.Value?.ItemRarity != null ? kv.Value.ItemRarity.name : "(" + kv.Value?.rarity + ")"; counts[n] = counts.TryGetValue(n, out var c0) ? c0 + 1 : 1; }
                var order = new System.Collections.Generic.List<string>(); if (eco.itemRarityList != null) foreach (var r in eco.itemRarityList) order.Add(r.ToString());
                var types = new System.Collections.Generic.List<string>(); if (eco.RollableItemTypes != null) foreach (var t in eco.RollableItemTypes) types.Add(t?.name);
                return $"order=[{string.Join(", ", order)}] items=[{string.Join(", ", counts.Select(kv => kv.Key + ":" + kv.Value))}] rollTimer={eco.RollTimerDuration} rollable=[{string.Join(", ", types)}]";
            }
            case "bg": return DevBg(arg); // bg [list | queue <i> | accept | decline | leave | score | hit <amount> | enemy | base <i> | capture <i> | stats <player>]
            case "duelanswer": return Duel.DevAnswer(arg != "no"); // duelanswer [no]
            case "shellhp": { var sh = PlayerShells.Get(byte.Parse(arg)); var v = Puppets.Vitals(byte.Parse(arg)); string got = ""; if (v != null) foreach (var x in v.Values) { got = $"{x.Current:0}/{x.Max:0}"; break; } return sh == null ? "no shell" : $"shell {BLINK.RPGBuilder.Combat.CombatUtilities.GetCurrentStatValue(sh, MobSync.HealthStat):0}/{BLINK.RPGBuilder.Combat.CombatUtilities.GetMaxStatValue(sh, MobSync.HealthStat):0} received {got}"; } // shellhp <player id>
            case "duelhit": return Duel.DevHit(float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture)); // duelhit <amount>: hit the opponent's shell as an attack of mine would
            case "duelstate": { var d = BLINK.RPGBuilder.PvP.DuelService.Instance; return $"active={d?.Active} fighting={d?.Fighting} status='{d?.Status}' opponent='{d?.Opponent?.name}' hp={(GameInfo.Player != null ? BLINK.RPGBuilder.Combat.CombatUtilities.GetCurrentStatValue(GameInfo.Player, MobSync.HealthStat) : -1):0}"; }
            case "inspect": Inspect.Ask(byte.Parse(arg)); return "asked"; // inspect <player id>: the inspect window on their shell
            case "inspected": // what the inspect window shows now
            {
                var panel = UnityEngine.Object.FindObjectOfType<NpcInspectPanel>(true);
                var e = panel?.InspectedEntity;
                if (e == null) return "no inspect window";
                var arm = new System.Collections.Generic.List<string>();
                if (e.equippedArmors != null) foreach (var x in e.equippedArmors) if (x?.item != null) arm.Add(x.item.entryName + (x.temporaryItemDataID >= 0 ? " " + ItemData.Describe(x.temporaryItemDataID) : ""));
                var wpn = new System.Collections.Generic.List<string>();
                if (e.equippedWeapons != null) foreach (var x in e.equippedWeapons) if (x?.item != null) wpn.Add(x.item.entryName + (x.temporaryItemDataID >= 0 ? " " + ItemData.Describe(x.temporaryItemDataID) : ""));
                return $"open={panel.IsOpen()} '{panel.npcNameText?.text}' / '{panel.npcLevelText?.text}' armor=[{string.Join(", ", arm)}] weapons=[{string.Join(", ", wpn)}]";
            }
            case "travelto": Summon.TravelTo(byte.Parse(arg)); return "asked"; // travelto <player id>
            case "trade": case "tradeanswer": case "tradeoffer": case "tradegold": case "tradeready": case "tradecancel": case "tradeui": case "tradestate": case "bag": case "tradestall": case "fillbag":
                return Trade.Dev(cmd, arg); // player trade: trade <player id>, tradeanswer [no], tradeoffer <bag slot>, tradegold <n>, tradeready, tradecancel, tradestate, bag
            case "mirrortrace": VisualMirror.Trace = arg != "off"; return "mirror trace " + (VisualMirror.Trace ? "on" : "off"); // projectiles: spawn/end positions
            case "statetrace": StateSync.Trace = arg != "off"; return "state trace " + (StateSync.Trace ? "on" : "off"); // log state messages with ms
            case "mobstates": return MobSync.DevMobStates();
            case "mobhp": return MobSync.DevMobHp();
            case "bots": return MobSync.DevBots();
            case "botshere": return MobSync.DevBotsHere();
            case "recruitnpc": return MobSync.DevRecruitNpc(int.Parse(arg)); // recruitnpc <adventurer npc id> (npcs 9 lists them)
            case "recruit": return int.TryParse(string.IsNullOrEmpty(arg) ? "2" : arg, out var recruitRole) ? MobSync.DevRecruit(recruitRole) : MobSync.DevRecruit(-1, arg); // recruit [role: 0 dps, 1 tank, 2 healer | class or name part]
            case "dismiss": // dismiss <bot name part>: one of my bots leaves the group (the game's own way)
            {
                var pets = GameInfo.Player?.GetCurrentPets();
                if (pets != null) foreach (var p in pets)
                {
                    var m = p?.TryCast<BLINK.RPGBuilder.Combat.MobCombatEntity>();
                    if (m == null || (m.GetNPCData()?.entryName ?? "").IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    m.LeavePlayerParty();
                    return $"'{m.GetNPCData()?.entryName}' left";
                }
                return "no such bot";
            }
            case "dungeons": return Dungeons.DevList();
            case "finder": return GroupFinder.Describe();
            case "settime": { var a = arg.Split(' '); CozyTimeBridge.SetWorldTime(int.Parse(a[0]), int.Parse(a[1])); return "ok"; } // settime <h> <m>
            case "weather": // weather <profile name part>: this machine's sky changes to it
            {
                if (string.IsNullOrWhiteSpace(arg)) // no name: what the sky is blended from now
                {
                    var cz = DistantLands.Cozy.CozyWeather.instance;
                    var parts = new System.Collections.Generic.List<string>();
                    if (cz?.currentWeatherProfiles != null)
                        foreach (var w in cz.currentWeatherProfiles) if (w?.profile != null) parts.Add($"{w.profile.name}={w.weight:0.00}{(w.transitioning ? "~" : "")}");
                    var ecos = new System.Collections.Generic.List<string>();
                    foreach (var e in UnityEngine.Object.FindObjectsOfType<DistantLands.Cozy.CozyEcosystem>()) ecos.Add($"{e.gameObject.name}:'{e.currentWeather?.name}' t={e.weatherTimer:0}");
                    return $"current='{cz?.currentWeather?.name}' blend=[{string.Join(", ", parts)}] ecos=[{string.Join(", ", ecos)}]";
                }
                foreach (var p in Resources.FindObjectsOfTypeAll<DistantLands.Cozy.Data.WeatherProfile>())
                    if (p != null && p.name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0) { DistantLands.Cozy.CozyWeather.instance.SetWeather(p, 5f); return "weather " + p.name; }
                return "profiles: " + string.Join(", ", Resources.FindObjectsOfTypeAll<DistantLands.Cozy.Data.WeatherProfile>().Select(p => p.name));
            }
            case "armor": // equipped armor: item, slot, cape?, visible now; helm/cloak settings
            {
                var items = GameDatabase.Instance.GetItems();
                var ap = GameInfo.Player?.appearance;
                var rows = new System.Collections.Generic.List<string>();
                foreach (var a in GameInfo.Character.ArmorPiecesEquipped)
                {
                    if (a == null || a.itemID <= 0 || !items.ContainsKey(a.itemID)) continue;
                    var it = items[a.itemID];
                    rows.Add($"'{it.entryName}' slot='{it.ArmorSlot?.entryName}' cape={BLINK.RPGBuilder.Characters.PlayerAppearance.IsCape(it)} visible={ap?.IsArmorVisualVisible(it)}");
                }
                return $"showHelm={BLINK.RPGBuilder.Managers.GameplaySettings.ShowHelm} showCloak={BLINK.RPGBuilder.Managers.GameplaySettings.ShowCloak} | " + string.Join(" | ", rows);
            }
            case "armorof": // armorof <player id>: the armor items that player sends (what their puppet wears here)
            {
                var items = GameDatabase.Instance.GetItems();
                if (!CoopManager.Session.Players.TryGetValue(byte.Parse(arg), out var pi)) return "no player";
                return string.Join(" | ", pi.Armor.Select(id => items.ContainsKey(id) ? $"{items[id].ArmorSlot?.entryName}" : id.ToString()));
            }
            case "clock": // time of day and weather here
            {
                CozyTimeBridge.TryGetCurrentTime(out int h, out int m);
                string w = ""; try { w = DistantLands.Cozy.CozyWeather.instance?.currentWeather?.name ?? ""; } catch { }
                return $"{h:00}:{m:00} weather='{w}'";
            }
            case "forms": // effects that are animal forms (shapeshifting)
            {
                var rows = new System.Collections.Generic.List<string>();
                foreach (var kv in GameDatabase.Instance.GetEffects())
                    if (kv.Value != null && (int)kv.Value.effectType == 8) rows.Add($"#{kv.Key} '{kv.Value.entryName}' ranks={kv.Value.ranks?.Count} ctrl='{kv.Value.ranks?[0]?.shapeshiftingAnimatorController?.name}' combat='{kv.Value.ranks?[0]?.shapeshiftingAnimatorControllerCombat?.name}' model='{kv.Value.ranks?[0]?.shapeshiftingModel?.name}'");
                return rows.Count + ": " + string.Join(" | ", rows.Take(40));
            }
            case "botform": // botform <effect id | -1> [bot name part]: one of my bots (or an own world bot by name) takes that form, the game's own way
            {
                var a = arg.Split(' ', 2);
                int id = int.Parse(a[0]);
                BLINK.RPGBuilder.Combat.MobCombatEntity bot = null;
                if (a.Length > 1)
                {
                    foreach (var m in MobSync.AllSynced())
                        if (!MobSync.IsCopy(m) && (m.GetNPCData()?.entryName ?? "").IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0) { bot = m; break; }
                }
                else
                {
                    var pets = GameInfo.Player?.GetCurrentPets();
                    if (pets != null) foreach (var p in pets) { bot = p?.TryCast<BLINK.RPGBuilder.Combat.MobCombatEntity>(); if (bot != null) break; }
                }
                if (bot == null) return "no bot";
                if (bot.IsShapeshifted()) bot.ResetShapeshifting();
                if (id >= 0) bot.InitShapeshifting(GameDatabase.Instance.GetEffects()[id], 0);
                return $"'{bot.GetNPCData()?.entryName}' shapeshifted={bot.IsShapeshifted()} classProgression={(bot.ClassProgression != null)}";
            }
            case "myform": // myform <effect id | -1>: the local player takes that form (the game's own shapeshifting)
            {
                int id = int.Parse(arg);
                var me = GameInfo.Player;
                if (me.IsShapeshifted()) me.ResetShapeshifting();
                if (id >= 0) me.InitShapeshifting(GameDatabase.Instance.GetEffects()[id], 0);
                return $"shapeshifted={me.IsShapeshifted()}";
            }
            case "puppetform": // puppetform <player id>: is that player's puppet shown in a form here
            {
                if (!Puppets.TryGetVisual(byte.Parse(arg), out var go, out var an)) return "no puppet";
                return $"form={(FormVisual.IsIn(go, out var fe) ? fe : -1)} ctrl='{an?.runtimeAnimatorController?.name}' model={(go.GetComponentsInChildren<Transform>(true).Any(t => t.name == "AfCoop_Form"))}";
            }
            case "tpzone": return MobSync.DevTpZone(string.IsNullOrEmpty(arg) ? 0 : int.Parse(arg)); // tpzone [n]: n-th nearest bot zone
            case "inspectbot": // inspectbot <name part>: "Inspect" on a bot copy, as the target menu does (asks its owner)
            {
                foreach (var m in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.Combat.MobCombatEntity>())
                    if (m != null && MobSync.IsCopy(m) && m.name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0) { Inspect.AskBot(m); return "asked its owner"; }
                return "no such bot copy here";
            }
            case "botgear": // botgear <name part>: a bot's equipped items (slot, id, item data) and main stats, to compare a copy with its owner's
            {
                foreach (var m in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.Combat.MobCombatEntity>())
                {
                    if (m == null || m.name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var sb = new System.Text.StringBuilder($"'{m.name}' copy={MobSync.IsCopy(m)} armor:");
                    if (m.equippedArmors != null) foreach (var a in m.equippedArmors) if (a?.item != null) sb.Append($" [{a.ArmorSlot?.name ?? "-"} #{a.item.ID} d{a.temporaryItemDataID}]");
                    sb.Append(" weapons:");
                    if (m.equippedWeapons != null) foreach (var w in m.equippedWeapons) sb.Append(w?.item != null ? $" [#{w.item.ID} d{w.temporaryItemDataID}]" : " [-]");
                    sb.Append(" stats:");
                    foreach (var kv in GameDatabase.Instance.GetStats())
                        if (kv.Value != null && kv.Value.entryName is "Strength" or "Agility" or "Intellect" or "Stamina" or "Spirit" or "Health")
                            sb.Append($" {kv.Value.entryName}={BLINK.RPGBuilder.Combat.CombatUtilities.GetCurrentStatValue(m, kv.Key):0.##}");
                    return sb.ToString();
                }
                return "no such bot here";
            }
            case "findbot": // findbot <class or name part>: world bots in this scene (they have a class), nearest first
            case "tpbot":   // tpbot <class or name part>: go to the nearest one
            {
                var me = GameInfo.Player;
                var found = new System.Collections.Generic.List<(float d, BLINK.RPGBuilder.Combat.MobCombatEntity m, string cls)>();
                foreach (var ce in GameState.combatEntities)
                {
                    var m = ce?.TryCast<BLINK.RPGBuilder.Combat.MobCombatEntity>();
                    if (m == null || m.Dead) continue;
                    string cls = "";
                    try { cls = m.ClassProgression?.Build?.Class?.entryName ?? ""; } catch { }
                    string nm = m.GetNPCData()?.entryName ?? m.name;
                    if (cls.Length == 0 && !MobSync.IsCopy(m)) continue; // not a world bot (copies have no class here)
                    if (arg.Length > 0 && cls.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0 && nm.IndexOf(arg, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    found.Add((Vector3.Distance(me.transform.position, m.transform.position), m, cls));
                }
                found.Sort((a, b) => a.d.CompareTo(b.d));
                if (cmd == "tpbot")
                {
                    if (found.Count == 0) return "none";
                    GameInfo.TeleportPlayer(found[0].m.transform.position + Vector3.right * 3f);
                    return $"to '{found[0].m.GetNPCData()?.entryName}' ({found[0].cls}) {found[0].d:0} m";
                }
                return found.Count + ": " + string.Join(" | ", found.Take(25).Select(f => $"'{f.m.GetNPCData()?.entryName}' {f.cls} {f.d:0} m{(MobSync.IsCopy(f.m) ? " copy" : "")}{(f.m.IsShapeshifted() ? " FORM" : "")}"));
            }
            case "mounts": // effects that are mounts
            {
                var rows = new System.Collections.Generic.List<string>();
                foreach (var kv in GameDatabase.Instance.GetEffects())
                    if (kv.Value != null && (int)kv.Value.effectType == 21)
                    {
                        var r0 = kv.Value.ranks?[0];
                        var by = new System.Collections.Generic.List<string>(); // abilities that give it
                        foreach (var ab in GameDatabase.Instance.GetAbilities())
                            if (ab.Value?.ranks != null)
                                foreach (var ar in ab.Value.ranks)
                                    if (ar?.effectsApplied != null)
                                        foreach (var ea in ar.effectsApplied) if (ea != null && ea.effectID == kv.Key) { by.Add($"{ab.Key}:'{ab.Value.entryName}'"); goto next; }
                        next:
                        rows.Add($"#{kv.Key} '{kv.Value.entryName}' prefab='{r0?.MountPrefab?.name}' seat='{r0?.RiderReParentName}' reparent={r0?.ReParentCharacterArmature} rider={r0?.RiderPosition} mountPos={r0?.MountPosition} by=[{string.Join(",", by)}]");
                    }
                return rows.Count + ": " + string.Join(" | ", rows.Take(30));
            }
            case "mymount": // mymount <effect id | -1>: the local character mounts (the game's own InitMount) or gets off
            {
                int id = int.Parse(arg);
                var me = GameInfo.Player;
                if (me.isMounted) me.ResetMount();
                if (id >= 0) me.InitMount(GameDatabase.Instance.GetEffects()[id], 0);
                return $"mounted={me.isMounted}";
            }
            case "botmount": // botmount <effect id | -1> [name]: one of my bots / an own world bot mounts
            {
                var a = arg.Split(' ', 2);
                int id = int.Parse(a[0]);
                BLINK.RPGBuilder.Combat.MobCombatEntity bot = null;
                foreach (var m in MobSync.AllSynced())
                    if (!MobSync.IsCopy(m) && (a.Length < 2 ? m.IsPet() : (m.GetNPCData()?.entryName ?? "").IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0)) { bot = m; break; }
                if (bot == null) return "no bot";
                if (bot.isMounted) bot.ResetMount();
                if (id >= 0) bot.InitMount(GameDatabase.Instance.GetEffects()[id], 0);
                return $"'{bot.GetNPCData()?.entryName}' mounted={bot.isMounted}";
            }
            case "mountcheck": // mounts shown here: on copies and puppets
            {
                var rows = new System.Collections.Generic.List<string>();
                foreach (var m in MobSync.AllSynced()) if (MountVisual.IsOn(m.gameObject, out var me2)) rows.Add($"copy '{m.name}' on #{me2} {MountVisual.Describe(m.gameObject)}");
                foreach (var pid in CoopManager.Session.Players.Keys)
                    if (Puppets.TryGetVisual(pid, out var go, out _) && MountVisual.IsOn(go, out var pe)) rows.Add($"puppet #{pid} on #{pe} rider='{Puppets.RiderOf(pid)?.name}' riderParent='{Puppets.RiderOf(pid)?.parent?.name}' {MountVisual.Describe(go)}");
                return rows.Count + ": " + string.Join(" | ", rows);
            }
            case "copyforms": // forms of the copies here
            {
                var rows = new System.Collections.Generic.List<string>();
                foreach (var m in MobSync.AllSynced())
                {
                    bool visual = FormVisual.IsIn(m.gameObject, out var ve);
                    if (m.IsShapeshifted() || visual || arg == "all")
                        rows.Add($"'{m.name}' form #{(m.IsShapeshifted() ? m.ShapeshiftedEffect?.ID : visual ? ve : -1)}{(visual ? " (shown)" : "")} copy={MobSync.IsCopy(m)} this='{m.ThisAnimator?.name}' ctrl='{m.ThisAnimator?.runtimeAnimatorController?.name}' visual[{FormVisual.Describe(m.gameObject)}] animators={m.GetComponentsInChildren<Animator>(true).Length}");
                }
                return rows.Count + ": " + string.Join(" | ", rows);
            }
            case "shellstates": // shellstates <player id>: states mirrored on that player's shell here
            {
                var sh = PlayerShells.Get(byte.Parse(arg));
                if (sh == null) return "no shell";
                var st = sh.GetStates();
                var rows = new System.Collections.Generic.List<string>();
                if (st != null) foreach (var x in st) if (x != null) rows.Add($"{x.stateEffectID} '{x.stateName}' rank={x.effectRankIndex}");
                return rows.Count + ": " + string.Join(" | ", rows);
            }
            case "sprites": // sprites <name part>: loaded sprites (name, size, texture)
            {
                var rows = new System.Collections.Generic.List<string>();
                foreach (var spr in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (spr != null && spr.name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0) rows.Add($"'{spr.name}' {spr.rect.width}x{spr.rect.height} tex='{spr.texture?.name}'");
                return rows.Count + ": " + string.Join(" | ", rows.Take(60));
            }
            case "loading": { var l = BLINK.RPGBuilder.Managers.LoadingScreenManager.Instance; return $"isSceneLoading={l?.isSceneLoading} holds={BLINK.RPGBuilder.Managers.LoadingScreenManager.HasSceneReadyHolds}"; }
            case "pets": // the local player's pets: name, NPC type, summon effect
            {
                var pets = GameInfo.Player?.GetCurrentPets();
                if (pets == null) return "none";
                var rows = new System.Collections.Generic.List<string>();
                foreach (var p in pets) { var n = p?.GetNPCData(); rows.Add($"'{n?.entryName}' #{n?.ID} type={(n != null ? (int)n.npcType : -1)} effect={p?.summonEffectID} dead={p?.Dead}"); }
                return rows.Count + ": " + string.Join(" | ", rows);
            }
            case "levelto": // levelto <level>: the game's own XP gain until that level (dev characters for dungeons)
            {
                int want = int.Parse(arg);
                for (int i = 0; i < 200 && GameInfo.Character.Level < want; i++) BLINK.RPGBuilder.Managers.LevelingManager.Instance.AddCharacterEXP(5000);
                return "level " + GameInfo.Character.Level;
            }
            case "dfqueue": { var a = arg.Split(' '); var f = BLINK.RPGBuilder.Dungeons.DungeonFinderService.Instance; var ids = new Il2CppSystem.Collections.Generic.List<int>(); ids.Add(int.Parse(a[0])); var role = a.Length > 1 ? (BLINK.RPGBuilder.AI.AdventurerRole)int.Parse(a[1]) : f.SelectedRole; var allowed = string.Join(",", new[] { 0, 1, 2 }.Where(r => BLINK.RPGBuilder.Dungeons.DungeonFinderRules.RoleAllowed(f.Settings, GameInfo.Character.ClassID, (BLINK.RPGBuilder.AI.AdventurerRole)r))); return "queued=" + f.Queue(new Il2CppSystem.Nullable<BLINK.RPGBuilder.AI.AdventurerRole>(role), false, ids.Cast<Il2CppSystem.Collections.Generic.IEnumerable<int>>()) + $" role={role} allowed=[{allowed}] " + f.Status; } // dfqueue <dungeon id>
            case "dfaccept": return "accepted=" + BLINK.RPGBuilder.Dungeons.DungeonFinderService.Instance.Accept() + " " + BLINK.RPGBuilder.Dungeons.DungeonFinderService.Instance.Status;
            case "dfcancel": BLINK.RPGBuilder.Dungeons.DungeonFinderService.Instance.Cancel(null); return "ok";
            case "dfleave": BLINK.RPGBuilder.Dungeons.DungeonFinderService.Instance.LeaveDungeon(); return "ok";
            case "travel": return Dungeons.DevTravel(arg); // travel <game scene id or name part>
            case "casttrace": return DevCastTrace.Set(arg); // casttrace <npc name part> | casttrace (off)
            case "grun": return DevGameActions.Run(arg); // grun <game actions template name part>
            case "gactions": return DevGameActions.Find(arg); // gactions <npc or object name>: game actions that spawn/remove it
            case "debuffp": { var a = arg.Split(' ', 2); return MobSync.DevDebuffPlayer(byte.Parse(a[0]), a.Length > 1 ? a[1] : "root"); }
            case "net": // net | net <pingMs> [jitterMs] [loss%] | net off — traffic report, imitated network conditions
            {
                var t = CoopManager.Net;
                if (t == null) return "no session";
                if (arg == "off") { t.Conditions.PingMs = 0; t.Conditions.JitterMs = 0; t.Conditions.LossPercent = 0; }
                else if (arg.Length > 0)
                {
                    var v = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    t.Conditions.PingMs = int.Parse(v[0]);
                    t.Conditions.JitterMs = v.Length > 1 ? int.Parse(v[1]) : 0;
                    t.Conditions.LossPercent = v.Length > 2 ? double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
                }
                return $"sim: {t.Conditions}; ping {CoopManager.Session?.PingToHost} ms{Environment.NewLine}" +
                       t.Stats.Report(b => ((AfallonCoop.Protocol.MsgType)b).ToString()) +
                       "world and fx by kind:" + Environment.NewLine + TrafficView.Report();
            }
            case "npcdiff": // npcdiff <id> <id>: properties that differ between two NPC templates
            {
                var ids = arg.Split(' ');
                var all = GameDatabase.Instance.GetNPCs();
                var a = all[int.Parse(ids[0])]; var b = all[int.Parse(ids[1])];
                var sb = new System.Text.StringBuilder();
                foreach (var prop in typeof(RPGNpc).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    string va, vb;
                    try { va = prop.GetValue(a)?.ToString() ?? "null"; vb = prop.GetValue(b)?.ToString() ?? "null"; } catch { continue; }
                    if (va != vb) sb.AppendLine($"{prop.Name}: {va} | {vb}");
                }
                return sb.ToString();
            }
            case "look": // look <npc id>: a bot's appearance string next to the local character's
            {
                var n = GameDatabase.Instance.GetNPCs()[int.Parse(arg)];
                var mine = new AfallonCoop.Protocol.PlayerInfo();
                GameInfo.FillVisuals(mine);
                return $"npc:  race={n.AdventurerRaceID} '{n.EquipmentAppearanceSelections}'{Environment.NewLine}" +
                       $"mine: race={GameInfo.Character?.RaceID} gender={GameInfo.Character?.Gender} '{mine.Appearance}'";
            }
            case "npcs": // npcs <type number>: NPC templates of that type (8 companion, 9 adventurer, 10 quest companion)
            {
                int type = int.Parse(arg);
                var sb = new System.Text.StringBuilder();
                foreach (var kv in GameDatabase.Instance.GetNPCs())
                {
                    var n = kv.Value;
                    if (n == null || (int)n.npcType != type) continue;
                    sb.AppendLine($"#{kv.Key} '{n.entryName}' key={n.AILogicTemplateKey} race={n.AdventurerRaceID} class={n.AdventurerClassID} faction={n.factionID} " +
                                  $"equipKey={n.GetEquipmentKeyID()} look='{(n.EquipmentAppearanceSelections ?? "").Length}ch' dummy={n.isDummyTarget}");
                }
                return sb.ToString();
            }
            case "tree": // tree <object name (substring)> [depth]: components of a live object, e.g. a bot
            {
                var a = arg.Split(' ');
                int depth = a.Length > 1 && int.TryParse(a[^1], out var d) ? d : 2;
                string name = depth.ToString() == a[^1] && a.Length > 1 ? string.Join(" ", a[..^1]) : arg;
                var sb = new System.Text.StringBuilder();
                foreach (var ce in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.Combat.CombatEntity>())
                {
                    if (ce == null || ce.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.AppendLine($"=== {DevTools.PathOf(ce.transform)} type={ce.GetIl2CppType().FullName} pos={ce.transform.position}");
                    var look = ce.GetComponentInChildren<BLINK.RPGBuilder.Characters.PlayerAppearance>(true);
                    if (look != null) sb.AppendLine($"appearance on '{look.name}' npc={look.IsNPCModel} race={look.ModelRaceID} gender={look.ModelGender} armed={look.LastArmed}");
                    DevTools.Tree(ce.transform, sb, 0, depth);
                    break;
                }
                return sb.Length > 0 ? sb.ToString() : "no combat entity named like that";
            }
            case "entities": // all combat entities nearby with their type
            {
                var me = GameInfo.Player != null ? GameInfo.Player.transform.position : Vector3.zero;
                var sb = new System.Text.StringBuilder();
                foreach (var ce in UnityEngine.Object.FindObjectsOfType<BLINK.RPGBuilder.Combat.CombatEntity>())
                    if (ce != null && Vector3.Distance(ce.transform.position, me) < (arg.Length > 0 ? float.Parse(arg) : 40f))
                        sb.AppendLine($"{ce.name} type={ce.GetIl2CppType().Name} dist={Vector3.Distance(ce.transform.position, me):0}");
                return sb.ToString();
            }
            case "combat": if (arg == "off") GameInfo.Player.ResetCombat(); else GameInfo.Player.EnterCombat(); return "ok";
            case "snap": DevTools.Snapshot(); return "ok";
            case "diff": return DevTools.SnapshotDiff();
            case "ts": return $"timeScale={Time.timeScale} paused={CoopManager.Session?.Paused} reason={CoopManager.Session?.PauseReason} menu={PauseController.LocalMenuOpen}";
            case "pause": PauseController.ToggleHostPause(); return "ok";
            default: return "unknown command";
        }
    }

    private static string SafeName(UnityEngine.Object o) { try { return o == null ? "(gone)" : o.name; } catch { return "(gone)"; } }

    private static string DevBg(string arg)
    {
#if !BATTLEGROUNDS
        return Battlegrounds.Describe();
#else
        var parts = arg.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 0 ? parts[0] : "";
        string rest = parts.Length > 1 ? parts[1] : "";
        var svc = BLINK.RPGBuilder.PvP.BattlegroundService.Instance;
        switch (sub)
        {
            case "": return Battlegrounds.Describe();
            case "list": return Battlegrounds.DevList();
            case "queue": return Battlegrounds.DevQueue(int.Parse(rest));
            case "accept": return svc != null && svc.Accept() ? "accepted" : "answered: " + svc?.Status;
            case "decline": svc?.Cancel("Battleground invitation declined."); return "declined";
            case "cancel": svc?.Cancel(null); return "cancelled";
            case "leave": svc?.Leave(); return "leaving";
            case "score": return Battlegrounds.DevScore();
            case "hit": return Battlegrounds.DevHit(float.Parse(rest, System.Globalization.CultureInfo.InvariantCulture));
            case "enemy": return Battlegrounds.DevGotoEnemy();
            case "base": return Battlegrounds.DevCapture(int.Parse(rest), true);
            case "capture": return Battlegrounds.DevCapture(int.Parse(rest), false);
            case "stats": return ShellStats.Describe(byte.Parse(rest));
            case "testarena": return BattlegroundTestArena.Configure(rest);
            default: return "bg [list | queue <i> | accept | decline | cancel | leave | score | hit <amount> | enemy | base <i> | capture <i> | stats <player>]";
        }
#endif
    }
}
