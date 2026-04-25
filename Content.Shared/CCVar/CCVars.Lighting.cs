using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<bool> AmbientOcclusion =
        CVarDef.Create("light.ambient_occlusion", false, CVar.CLIENTONLY | CVar.ARCHIVE); // Ganimed-Tweak (true -> false)

    /// <summary>
    /// Distance in world-pixels of ambient occlusion.
    /// </summary>
    public static readonly CVarDef<string> AmbientOcclusionColor =
        CVarDef.Create("light.ambient_occlusion_color", "#04080F80", CVar.CLIENTONLY); // Ganimed-Tweak (#04080FAA)

    /// <summary>
    /// Distance in world-pixels of ambient occlusion.
    /// </summary>
    public static readonly CVarDef<float> AmbientOcclusionDistance =
        CVarDef.Create("light.ambient_occlusion_distance", 4f, CVar.CLIENTONLY);
}
