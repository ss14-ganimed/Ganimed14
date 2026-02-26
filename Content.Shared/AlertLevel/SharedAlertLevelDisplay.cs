using Robust.Shared.Serialization;

namespace Content.Shared.AlertLevel;

[Serializable, NetSerializable]
public enum AlertLevelDisplay : byte
{
    CurrentLevel,
    Layer,
    Powered
}
