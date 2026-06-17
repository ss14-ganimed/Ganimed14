using Content.Client._Ganimed.Research.UI;
using Content.Shared._Ganimed.Research.Components;
using Content.Shared._Ganimed.Research.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Content.Client._Ganimed.Research.Systems;

public sealed partial class ExperimentScannerSystem : SharedExperimentScannerSystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ExperimentScannerComponent, AfterAutoHandleStateEvent>(OnAfterAutoHandleState);
    }

    private void OnAfterAutoHandleState(Entity<ExperimentScannerComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (!Net.IsClient)
            return;

        if (!TryComp<UserInterfaceComponent>(ent, out var ui))
            return;

        foreach (var bui in ui.ClientOpenInterfaces.Values)
        {
            if (bui is ExperimentScannerBoundUserInterface scannerUi)
                scannerUi.RefreshFromComponent(ent.Comp);
        }
    }
}
