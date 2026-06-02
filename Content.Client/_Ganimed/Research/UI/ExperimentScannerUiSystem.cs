using Content.Shared._Ganimed.Research.Components;

namespace Content.Client._Ganimed.Research.UI;

public sealed class ExperimentScannerUiSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ExperimentScannerComponent, AfterAutoHandleStateEvent>(OnAfterHandleState);
    }

    private void OnAfterHandleState(Entity<ExperimentScannerComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (!TryComp<UserInterfaceComponent>(ent, out var ui))
            return;

        foreach (var bui in ui.ClientOpenInterfaces.Values)
        {
            if (bui is ExperimentScannerBoundUserInterface scannerUi)
                scannerUi.RefreshFromComponent(ent.Comp);
        }
    }
}
