using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Server.Audio;
using Robust.Shared.Audio;

namespace Content.Server._Ganimed.CritHeartbeat;

public sealed class CritHeartbeatSystem : EntitySystem
{
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CritHeartbeatComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<CritHeartbeatComponent, DamageChangedEvent>(OnDamage);
    }

    private void OnMobStateChanged(Entity<CritHeartbeatComponent> ent, ref MobStateChangedEvent args)
    {
        if (!ent.Comp.Enabled)
            return;

        ent.Comp.AudioStream = args.NewMobState == MobState.Critical
            ? _audio.PlayEntity(ent.Comp.HeartbeatSound, ent, ent)?.Entity
            : _audio.Stop(ent.Comp.AudioStream);
    }

    private void OnDamage(Entity<CritHeartbeatComponent> ent, ref DamageChangedEvent args)
    {
        if (!ent.Comp.Enabled)
            return;

        if (_audio.IsPlaying(ent.Comp.AudioStream))
            return;

        if (!_mobState.IsCritical(ent))
            return;

        var pitch = Math.Min(1, 100 / args.Damageable.TotalDamage.Float());

        _audio.Stop(ent.Comp.AudioStream);
        ent.Comp.AudioStream = _audio.PlayEntity(ent.Comp.HeartbeatSound, ent, ent, AudioParams.Default.WithPitchScale(pitch))?.Entity;
    }
}
