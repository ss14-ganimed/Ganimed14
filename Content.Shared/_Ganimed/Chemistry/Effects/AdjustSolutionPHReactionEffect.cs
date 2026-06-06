using Content.Shared._Ganimed.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.Effects;

public sealed partial class AdjustSolutionPHReactionEffectSystem : EntityEffectSystem<SolutionComponent, AdjustSolutionPHReactionEffect>
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;

    protected override void Effect(Entity<SolutionComponent> entity, ref EntityEffectEvent<AdjustSolutionPHReactionEffect> args)
    {
        AdjustPH(entity.Comp.Solution, args.Effect, args.Scale);
        Dirty(entity);
    }

    private void AdjustPH(Solution solution, AdjustSolutionPHReactionEffect effect, float scale)
    {
        if (solution.Volume <= FixedPoint2.Zero || scale <= 0f)
            return;

        var currentPH = ChemistryPH.GetSolutionPH(solution, _prototype);
        var influence = Math.Clamp(scale * effect.StrengthPerUnit / solution.Volume.Float(), 0f, 1f);
        solution.PHOverride = Math.Clamp(
            currentPH + (Math.Clamp(effect.TargetPH, ChemistryPH.MinPH, ChemistryPH.MaxPH) - currentPH) * influence,
            ChemistryPH.MinPH,
            ChemistryPH.MaxPH);
    }
}

public sealed partial class AdjustSolutionPHReactionEffect : EntityEffectBase<AdjustSolutionPHReactionEffect>
{
    [DataField("targetPH")]
    public float TargetPH = ChemistryPH.NeutralPH;

    [DataField("strengthPerUnit")]
    public float StrengthPerUnit = 1f;

    public override string? EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
    {
        return null;
    }
}
