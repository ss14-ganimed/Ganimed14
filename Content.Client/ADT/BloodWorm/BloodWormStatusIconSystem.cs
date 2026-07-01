using Content.Shared._Ganimed.BloodWorm.Components;
using Content.Shared.Antag;
using Content.Shared.StatusIcon;
using Content.Shared.StatusIcon.Components;
using Robust.Client.Player;
using Robust.Shared.Prototypes;

namespace Content.Client._Ganimed.BloodWorm;

public sealed class BloodWormStatusIconSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BloodWormInfectedComponent, GetStatusIconsEvent>(OnGetStatusIcons);
    }

    private void OnGetStatusIcons(Entity<BloodWormInfectedComponent> ent, ref GetStatusIconsEvent args)
    {
        var viewer = _player.LocalEntity;
        if (viewer == null)
            return;

        if (!HasComp<ShowAntagIconsComponent>(viewer.Value) && !HasComp<BloodWormResourceComponent>(viewer.Value))
            return;

        args.StatusIcons.Add(_prototype.Index(ent.Comp.StatusIcon));
    }
}
