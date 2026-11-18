using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    // SS13 uses #8589fa but it can come off as more harsh so we muted it a bit more.
    public const string DefaultSpaceLightColor = "#8487db";

    /// <summary>
    /// Default space light color, in sRGB hex.
    /// </summary>
    public static readonly CVarDef<string> SpaceLightColor =
        CVarDef.Create("light.space_light_color", DefaultSpaceLightColor, CVar.SERVERONLY);

    public static readonly CVarDef<bool> AmbientOcclusion =
        CVarDef.Create("light.ambient_occlusion", false, CVar.CLIENTONLY | CVar.ARCHIVE); // Ganimed-Tweak (true -> false)

    public static readonly Color DefaultAmbientOcclusionColor = Color.FromHex("#04080FAA");

    /// <summary>
    /// Color of ambient occlusion.
    /// </summary>
    public static readonly CVarDef<string> AmbientOcclusionColor =
        CVarDef.Create("light.ambient_occlusion_color", "#04080F80", CVar.CLIENTONLY); // Ganimed-Tweak (#04080FAA)

    /// <summary>
    /// Render resolution scale for ambient occlusion.
    /// </summary>
    public static readonly CVarDef<float> AmbientOcclusionResolutionScale =
        CVarDef.Create("light.ambient_occlusion_resolution_scale", 0.5f, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Distance in world-pixels of ambient occlusion.
    /// </summary>
    public static readonly CVarDef<float> AmbientOcclusionDistance =
        CVarDef.Create("light.ambient_occlusion_distance", 4f, CVar.CLIENTONLY);
}
