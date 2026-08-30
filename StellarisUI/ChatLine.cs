using System.Windows.Media;

namespace StellarisUI;

public sealed record ChatLine(string Label, string Text, Brush LabelBrush, Brush TextBrush);
