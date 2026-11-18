using Content.Shared.ADT.SpeechBarks;
using Content.Shared.Speech.Components;

namespace Content.Server.ADT.Speech.EntitySystems;

public sealed class ADTVoiceOverrideSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<VoiceOverrideComponent, TransformSpeakerBarkEvent>(OnTransformSpeakerBark);
    }

    private void OnTransformSpeakerBark(Entity<VoiceOverrideComponent> entity, ref TransformSpeakerBarkEvent args)
    {
        if (!entity.Comp.Enabled)
            return;

        args.Data = entity.Comp.Bark ?? args.Data;
    }
}
