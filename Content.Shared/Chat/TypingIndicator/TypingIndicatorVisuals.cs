using Robust.Shared.Serialization;

namespace Content.Shared.Chat.TypingIndicator;

[Serializable, NetSerializable]
public enum TypingIndicatorVisuals : byte
{
    State,
    Channel // Ganimed-Add (Typing indicator color based on chat type)
}

[Serializable]
public enum TypingIndicatorLayers : byte
{
    Base
}
