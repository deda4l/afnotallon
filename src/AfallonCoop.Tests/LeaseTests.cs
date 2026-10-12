using AfallonCoop.World;
using Xunit;

namespace AfallonCoop.Tests;

/// <summary>Scenarios from MODDING_PLAN §4.5 at the level of one spawner.</summary>
public class LeaseTests
{
    private const byte Host = 0, C1 = 1, C2 = 2, C3 = 3;
    private const string S = "cave|Spider|1044,29,-671";

    private readonly LeaseRegistry _r = new();
    private readonly List<string> _log = new();

    public LeaseTests() { _r.OwnerChanged += (l, why) => _log.Add($"{l.Owner}:{why}"); }

    [Fact]
    public void First_player_near_gets_it_others_observe_and_there_is_never_a_second_owner()
    {
        _r.Subscribe(C2, S, 0);
        _r.Subscribe(C3, S, 1);
        _r.Subscribe(Host, S, 2);
        Assert.Equal(C2, _r.OwnerOf(S));
        Assert.Single(_log);
    }

    [Fact]
    public void Owner_walks_away_observer_takes_over_with_new_epoch()
    {
        _r.Subscribe(C2, S, 0);
        _r.Subscribe(C3, S, 0);
        uint e = _r.Get(S).Epoch;
        _r.Unsubscribe(C2, S, 5);
        Assert.Equal(C3, _r.OwnerOf(S));
        Assert.True(_r.Get(S).Epoch > e);
    }

    [Fact]
    public void Everyone_leaves_spawner_goes_dormant_then_next_visitor_owns_it()
    {
        _r.Subscribe(C1, S, 0);
        _r.Unsubscribe(C1, S, 1);
        Assert.Equal(LeaseRegistry.NoOwner, _r.OwnerOf(S));
        _r.Subscribe(C3, S, 2);
        Assert.Equal(C3, _r.OwnerOf(S));
    }

    [Fact]
    public void Fight_is_never_handed_over_while_it_lasts()
    {
        _r.Subscribe(C2, S, 0);
        _r.Subscribe(C3, S, 0);
        _r.SetCombat(C2, S, true, 1);
        _r.Unsubscribe(C2, S, 2);   // kites the mobs away
        _r.Tick(3);
        Assert.Equal(C2, _r.OwnerOf(S));
        _r.SetCombat(C2, S, false, 10);
        _r.Tick(10.1);
        Assert.Equal(C3, _r.OwnerOf(S));
    }

    [Fact]
    public void Fight_moves_at_once_when_the_owner_leaves_the_area()
    {
        _r.Subscribe(C2, S, 0);
        _r.Subscribe(C3, S, 0);
        _r.SetCombat(C2, S, true, 1);
        _r.Unsubscribe(C2, S, 2, leftPlace: true);   // ran out of the dungeon: the mobs are gone on C2's machine
        Assert.Equal(C3, _r.OwnerOf(S));
        Assert.False(_r.Get(S).InCombat);
    }

    [Fact]
    public void Calm_spawner_migrates_to_host_only_after_the_delay_and_never_during_combat()
    {
        _r.Subscribe(C2, S, 0);
        _r.Subscribe(Host, S, 0);
        _r.SetCombat(C2, S, true, 0.5);
        _r.Tick(10);
        Assert.Equal(C2, _r.OwnerOf(S));            // fighting: stays
        _r.SetCombat(C2, S, false, 11);
        _r.Tick(12);
        Assert.Equal(C2, _r.OwnerOf(S));            // calm for 1 s only
        _r.Tick(14.1);
        Assert.Equal(Host, _r.OwnerOf(S));          // calm 3 s with host near
    }

    [Fact]
    public void Owner_disconnecting_mid_fight_hands_over_immediately_preferring_host()
    {
        _r.Subscribe(C1, S, 0);
        _r.Subscribe(C3, S, 0);
        _r.Subscribe(Host, S, 0);
        _r.SetCombat(C1, S, true, 1);
        _r.PlayerLeft(C1, 2);
        Assert.Equal(Host, _r.OwnerOf(S));
        Assert.False(_r.Get(S).InCombat);
    }

    [Fact]
    public void Only_the_owner_can_report_combat()
    {
        _r.Subscribe(C1, S, 0);
        _r.Subscribe(C2, S, 0);
        _r.SetCombat(C2, S, true, 1);
        Assert.False(_r.Get(S).InCombat);
    }

    [Fact]
    public void Scattered_players_each_own_their_own_spawners()
    {
        var keys = new[] { "a", "b", "c", "d", "e" };
        for (byte p = 0; p < 5; p++) _r.Subscribe(p, keys[p], 0);
        for (byte p = 0; p < 5; p++) Assert.Equal(p, _r.OwnerOf(keys[p]));
        _r.Tick(100); // nobody shares a spawner: nothing migrates
        for (byte p = 0; p < 5; p++) Assert.Equal(p, _r.OwnerOf(keys[p]));
    }
}
