namespace AfallonCoop.Protocol;

public enum MsgType : byte
{
    Hello = 1,          // client -> host
    Welcome = 2,        // host -> client
    Reject = 3,         // host -> client, then disconnect
    PlayerJoined = 4,   // host -> all
    PlayerLeft = 5,     // host -> all
    PlayerInfo = 6,     // client -> host -> others (character chosen, level, scene...)
    PlayerState = 7,    // unreliable; client -> host -> others
    Chat = 8,
    Ping = 9,
    Pong = 10,
    MenuState = 11,     // client -> host: "I have a menu open" (for the all-in-menu pause rule)
    PauseState = 12,    // host -> all: authoritative pause on/off + reason
    Fx = 13,            // client -> host -> others: an animation / visual effect / sound the owner's character played
    FxStream = 14,      // same, unreliable: continuous updates where only the latest matters (projectile positions)
    Interest = 15,      // client -> host: "I am near spawner K" / "no longer near" (lease subscription)
    LeaseCombat = 16,   // client -> host: the owner reports whether spawner K is fighting (ownership stays put meanwhile)
    LeaseOwner = 17,    // host -> all: spawner K is now simulated by player P (epoch E)
    World = 18,         // client -> host -> others: world object events from their owner (mob spawned, died...)
    WorldStream = 19,   // same, unreliable: world object state stream (positions, animation)
    Group = 20,         // player groups: requests client -> host, state/invites/notices host -> clients (GroupOp)
    WorldStreamTo = 21, // client -> host -> one player: a world stream meant for that player only; arrives as WorldStream
}

/// <summary>Sub-kinds of <see cref="MsgType.Group"/>.</summary>
public enum GroupOp : byte
{
    // client -> host
    Invite = 1, Answer = 2, Leave = 3, Kick = 4, Promote = 5, DismissBot = 6, Bots = 7,
    /// <summary>A message for the sender's group (the plugin's own content); the host relays it as <see cref="FromMember"/>.</summary>
    Cast = 8,
    // host -> client
    State = 10, InvitedBy = 11, Notice = 12, BotDismissed = 13, ReturnTo = 14, FromMember = 15,
}

public enum PauseReason : byte { None = 0, Host = 1, AllInMenu = 2 }

public static class ProtocolInfo
{
    /// <summary>Bump on any wire-format change. Peers with a different value are rejected.</summary>
    public const ushort Version = 28;
    public const int MaxPlayers = 5;
    public const byte HostId = 0;
}

public sealed class HelloMsg
{
    public ushort Protocol;
    public string ModVersion = "";
    public string GameBuild = "";
    public string PlayerName = "";
    public ulong SteamId;
    /// <summary>Random id this mod install made for itself once: who a LAN/IP player is across reconnects.</summary>
    public string ClientId = "";
    /// <summary>The host's password, if it has one (a co-op guard against strangers, not a secret store).</summary>
    public string Password = "";

    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.Hello);
        w.U16(Protocol); w.Str(ModVersion); w.Str(GameBuild); w.Str(PlayerName); w.U64(SteamId); w.Str(ClientId); w.Str(Password);
    }

    public static HelloMsg Read(ref NetReader r)
    {
        var h = new HelloMsg { Protocol = r.U16(), ModVersion = r.Str(), GameBuild = r.Str(), PlayerName = r.Str(), SteamId = r.U64() };
        if (r.Remaining > 0) h.ClientId = r.Str(); // older versions end here: still read, then rejected by version
        if (r.Remaining > 0) h.Password = r.Str();
        return h;
    }
}

/// <summary>Who a player is. Character fields are empty until they enter the world.</summary>
public sealed class PlayerInfo
{
    public byte Id;
    public string Name = "";
    public ulong SteamId;
    /// <summary>Who this player is across sessions (set by the host): their Steam account, else their install.</summary>
    public string Key = "";
    public string CharacterName = "";
    public int ClassId = -1;
    public int RaceId = -1;
    public string Gender = "";
    /// <summary>Group role the player plays (game's AdventurerRole: 0 damage, 1 tank, 2 healer).</summary>
    public byte Role;
    public int Level;
    public string Scene = "";
    /// <summary>CharacterAppearanceModifier.Serialize(): hair, face, colours...</summary>
    public string Appearance = "";
    /// <summary>Item ids of visible armor pieces.</summary>
    public List<int> Armor = new();
    /// <summary>Item ids of equipped weapons by weapon slot (0 = empty).</summary>
    public List<int> Weapons = new();
    /// <summary>Owner's renderer materials ("path=matA+matB;..."): makes the copy look exactly like the original.</summary>
    public string Materials = "";
    /// <summary>Database IDs of the abilities this character knows: others preload their visual effects.</summary>
    public List<int> FxPrewarm = new();

    public void Write(NetWriter w)
    {
        w.U8(Id); w.Str(Name); w.U64(SteamId); w.Str(CharacterName);
        w.I32(ClassId); w.I32(RaceId); w.Str(Gender); w.I32(Level); w.Str(Scene);
        w.Str(Appearance);
        WriteInts(w, Armor);
        WriteInts(w, Weapons);
        w.Str(Materials);
        int n = Math.Min(FxPrewarm?.Count ?? 0, 1000);
        w.U16((ushort)n);
        for (int i = 0; i < n; i++) w.I32(FxPrewarm[i]);
        w.U8(Role);
        w.Str(Key);
    }

    public static PlayerInfo Read(ref NetReader r) => new()
    {
        Id = r.U8(), Name = r.Str(), SteamId = r.U64(), CharacterName = r.Str(),
        ClassId = r.I32(), RaceId = r.I32(), Gender = r.Str(), Level = r.I32(), Scene = r.Str(),
        Appearance = r.Str(), Armor = ReadInts(ref r), Weapons = ReadInts(ref r), Materials = r.Str(),
        FxPrewarm = ReadIntsU16(ref r),
        Role = r.U8(),
        Key = r.Str(),
    };

    private static List<int> ReadIntsU16(ref NetReader r)
    {
        int n = r.U16();
        var list = new List<int>(n);
        for (int i = 0; i < n; i++) list.Add(r.I32());
        return list;
    }

    private static void WriteInts(NetWriter w, List<int> list)
    {
        int n = Math.Min(list?.Count ?? 0, 64);
        w.U8((byte)n);
        for (int i = 0; i < n; i++) w.I32(list[i]);
    }

    private static List<int> ReadInts(ref NetReader r)
    {
        int n = r.U8();
        var list = new List<int>(n);
        for (int i = 0; i < n; i++) list.Add(r.I32());
        return list;
    }

    /// <summary>Everything that changes how the character looks; a change means the puppet must be rebuilt.</summary>
    public string VisualSignature =>
        $"{RaceId}|{Gender}|{Appearance}|{string.Join(",", Armor)}|{string.Join(",", Weapons)}";

    public PlayerInfo Clone()
    {
        var c = (PlayerInfo)MemberwiseClone();
        c.Armor = new List<int>(Armor);
        c.Weapons = new List<int>(Weapons);
        c.FxPrewarm = new List<int>(FxPrewarm);
        return c;
    }
}

[Flags]
public enum StateFlags : byte
{
    None = 0,
    Armed = 1,      // weapons in hands, not stowed
    InMenu = 2,     // has the game menu open (shown to others as an icon later)
    Dead = 4,
    Stealth = 8,    // in stealth: allies still see them (translucent), enemies do not
}

public enum AnimParamType : byte { Float = 1, Int = 3, Bool = 4 }

public struct AnimParam
{
    public int NameHash;
    public AnimParamType Type;
    public float Value; // ints and bools are carried as float

    public AnimParam(int hash, AnimParamType type, float value) { NameHash = hash; Type = type; Value = value; }
}

public struct AnimLayer
{
    public int StateHash;
    public float NormalizedTime;
    /// <summary>Layer weight: the game drives combat layers (AllBody/UpperBody) from scripts, not from the controller.</summary>
    public float Weight;
    public AnimLayer(int stateHash, float normalizedTime, float weight = 1f) { StateHash = stateHash; NormalizedTime = normalizedTime; Weight = weight; }
}

/// <summary>One movement + animation sample of a player character.</summary>
public sealed class PlayerStateMsg
{
    public byte Id;
    public uint Seq;
    public double SenderTime;   // seconds, sender's local clock
    public int SceneHash;
    public float PosX, PosY, PosZ;
    public float RotY;          // yaw in degrees
    public float VelX, VelY, VelZ;
    public bool InWorld;
    public StateFlags Flags;
    /// <summary>StableHash of the animator controller name (changes with weapon type, armed/rest, forms).</summary>
    public int ControllerHash;
    /// <summary>Params holds every parameter (otherwise only recent changes, see <see cref="AnimDeltaEncoder"/>).</summary>
    public bool AnimKeyframe;
    public List<AnimParam> Params = new();
    public List<AnimLayer> Layers = new();
    /// <summary>Model parts the owner's game switched on by its own rules (stealth aura etc.): StableHash of their path.</summary>
    public List<int> ActiveParts = new();
    /// <summary>Health, mana and other vitality stats (sent when they change and once a second; empty = unchanged).</summary>
    public List<Vital> Vitals = new();

    public void Write(NetWriter w)
    {
        w.U8((byte)MsgType.PlayerState);
        w.U8(Id); w.U32(Seq); w.F64(SenderTime); w.I32(SceneHash);
        w.F32(PosX); w.F32(PosY); w.F32(PosZ); w.F32(RotY);
        w.F32(VelX); w.F32(VelY); w.F32(VelZ);
        w.Bool(InWorld);
        w.U8((byte)Flags);
        w.I32(ControllerHash);
        w.Bool(AnimKeyframe);
        w.U8((byte)Math.Min(Params.Count, 255));
        for (int i = 0; i < Params.Count && i < 255; i++)
        {
            var p = Params[i];
            w.I32(p.NameHash); w.U8((byte)p.Type); w.F32(p.Value);
        }
        w.U8((byte)Math.Min(Layers.Count, 255));
        for (int i = 0; i < Layers.Count && i < 255; i++)
        {
            w.I32(Layers[i].StateHash); w.F32(Layers[i].NormalizedTime); w.F32(Layers[i].Weight);
        }
        w.U8((byte)Math.Min(ActiveParts.Count, 255));
        for (int i = 0; i < ActiveParts.Count && i < 255; i++) w.I32(ActiveParts[i]);
        w.U8((byte)Math.Min(Vitals.Count, 255));
        for (int i = 0; i < Vitals.Count && i < 255; i++) { w.I32(Vitals[i].Stat); w.F32(Vitals[i].Current); w.F32(Vitals[i].Max); }
    }

    public static PlayerStateMsg Read(ref NetReader r)
    {
        var m = new PlayerStateMsg
        {
            Id = r.U8(), Seq = r.U32(), SenderTime = r.F64(), SceneHash = r.I32(),
            PosX = r.F32(), PosY = r.F32(), PosZ = r.F32(), RotY = r.F32(),
            VelX = r.F32(), VelY = r.F32(), VelZ = r.F32(),
            InWorld = r.Bool(),
            Flags = (StateFlags)r.U8(),
            ControllerHash = r.I32(),
            AnimKeyframe = r.Bool(),
        };
        int pc = r.U8();
        for (int i = 0; i < pc; i++)
            m.Params.Add(new AnimParam(r.I32(), (AnimParamType)r.U8(), r.F32()));
        int lc = r.U8();
        for (int i = 0; i < lc; i++)
            m.Layers.Add(new AnimLayer(r.I32(), r.F32(), r.F32()));
        int ac = r.U8();
        for (int i = 0; i < ac; i++) m.ActiveParts.Add(r.I32());
        int vc = r.U8();
        for (int i = 0; i < vc; i++) m.Vitals.Add(new Vital(r.I32(), r.F32(), r.F32()));
        return m;
    }
}

/// <summary>A vitality stat of a player's character (game stat id, current, max).</summary>
public readonly record struct Vital(int Stat, float Current, float Max);

public static class StableHash
{
    /// <summary>FNV-1a; identical on every machine (string.GetHashCode is randomized per process).</summary>
    public static int Of(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }
}
