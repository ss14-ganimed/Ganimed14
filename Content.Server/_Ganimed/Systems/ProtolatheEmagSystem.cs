using Content.Shared._Ganimed.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Lathe;
using Robust.Server.GameObjects;

namespace Content.Server._Ganimed.Systems;

public sealed class ProtolatheEmagSystem : EntitySystem
{
    [Dependency] private readonly EmagSystem _emag = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LatheAlertLevelRestrictionComponent, GotEmaggedEvent>(OnEmagged);
    }

    private void OnEmagged(EntityUid uid, LatheAlertLevelRestrictionComponent component, ref GotEmaggedEvent args)
    {
        if (!_emag.CompareFlag(args.Type, EmagType.Interaction))
            return;

        if (_emag.CheckFlag(uid, EmagType.Interaction))
            return;

        if (!TryComp<ProtolatheEmagComponent>(uid, out var emagComp))
            return;

        args.Handled = true;
        emagComp.IsEmagged = true;
        Dirty(uid, emagComp);
    }
}
