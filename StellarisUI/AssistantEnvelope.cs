using System.Collections.Generic;

namespace StellarisUI;

public sealed record AssistantEnvelope(string Reply, string Mood, string Status, List<AssistantAction>? Actions = null);

public sealed record AssistantAction(string Type, string Path, string? Content = null);
