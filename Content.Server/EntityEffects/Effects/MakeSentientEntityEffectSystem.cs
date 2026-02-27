using Content.Server.Ghost.Roles.Components;
using Content.Server.Speech.Components;
using Content.Shared.EntityEffects;
using Content.Shared.EntityEffects.Effects;
using Content.Shared.Mind.Components;
using Content.Shared.NPC.Components; // ADT-tweak
using Content.Shared.NPC.Systems; // ADT-tweak
using Content.Shared.NPC.Prototypes; // ADT-tweak
using Content.Shared.ADT.Language; // ADT-tweak
using Robust.Shared.Prototypes;

namespace Content.Server.EntityEffects.Effects;

/// <summary>
/// Makes this entity sentient. Allows ghost to take it over if it's not already occupied.
/// Optionally also allows this entity to speak.
/// </summary>
/// <inheritdoc cref="EntityEffectSystem{T,TEffect}"/>
public sealed partial class MakeSentientEntityEffectSystem : EntityEffectSystem<MetaDataComponent, MakeSentient>
{
    protected override void Effect(Entity<MetaDataComponent> entity, ref EntityEffectEvent<MakeSentient> args)
    {
        // Let affected entities speak normally to make this effect different from, say, the "random sentience" event
        // This also works on entities that already have a mind
        // We call this before the mind check to allow things like player-controlled mice to be able to benefit from the effect
        if (args.Effect.AllowSpeech)
        {
            RemComp<ReplacementAccentComponent>(entity);
            // TODO: Make MonkeyAccent a replacement accent and remove MonkeyAccent code-smell.
            RemComp<MonkeyAccentComponent>(entity);
        }

        // ADT Languages start
        var lang = EnsureComp<LanguageSpeakerComponent>(entity);
        if (!lang.Languages.ContainsKey("GalacticCommon"))
            lang.Languages.Add("GalacticCommon", LanguageKnowledge.Speak);
        else
            lang.Languages["GalacticCommon"] = LanguageKnowledge.Speak;
        // ADT Languages end

        MakeFriendlyToStation(entity); // ADT-tweak

        // Stops from adding a ghost role to things like people who already have a mind
        if (TryComp<MindContainerComponent>(entity, out var mindContainer) && mindContainer.HasMind)
            return;

        // Don't add a ghost role to things that already have ghost roles
        if (TryComp(entity, out GhostRoleComponent? ghostRole))
            return;

        ghostRole = AddComp<GhostRoleComponent>(entity);
        EnsureComp<GhostTakeoverAvailableComponent>(entity);

        ghostRole.RoleName = entity.Comp.EntityName;
        ghostRole.RoleDescription = Loc.GetString(args.Effect.RoleDescription);
    }

    // ADT-tweak start
    private void MakeFriendlyToStation(Entity<MetaDataComponent> entity)
    {
        var factionSystem = EntityManager.System<NpcFactionSystem>();
        var protoMan = IoCManager.Resolve<IPrototypeManager>();

        if (!TryComp<NpcFactionMemberComponent>(entity, out var factionComp))
        {
            factionSystem.AddFaction((entity.Owner, null), "PetsNT");
            return;
        }

        bool wasHostile = false;

        foreach (var factionId in factionComp.Factions)
        {
            if (protoMan.TryIndex<NpcFactionPrototype>(factionId, out var factionProto) &&
                factionProto.Hostile.Contains("NanoTrasen"))
            {
                factionSystem.RemoveFaction((entity.Owner, factionComp), factionId, false);
                wasHostile = true;
            }
        }

        if (wasHostile)
            factionSystem.AddFaction((entity.Owner, factionComp), "PetsNT", true);
        else
            factionSystem.AddFaction((entity.Owner, factionComp), "SimpleNeutral", true);
    }
    // ADT-tweak end
}
