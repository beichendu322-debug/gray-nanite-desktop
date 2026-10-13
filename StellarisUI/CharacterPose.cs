using System;
using System.Collections.Generic;
using System.Windows;

namespace StellarisUI;

/// <summary>
/// Whole-body action sprites. <see cref="Standard"/> is the approved salute master, on which the
/// eight expression sprites are face-only variants; every other pose is a separately drawn body.
/// </summary>
internal enum CharacterPose { Standard, AhogeGuard, Wave, Hips, ArmsCrossed, Stop, Thinking, Shy }

/// <summary>What the character currently shows: an expression on the master body, or a whole pose.</summary>
internal readonly record struct CharacterLook(CharacterExpression Expression, CharacterPose Pose = CharacterPose.Standard)
{
	public static CharacterLook Of(CharacterExpression expression) => new(expression);
	public static CharacterLook Of(CharacterPose pose) => new(CharacterExpression.Neutral, pose);
	public bool IsPose => Pose != CharacterPose.Standard;
	/// <summary>Poses carry their own drawn face, so the expression is ignored for sprite selection.</summary>
	public CharacterLook Normalized => IsPose ? new(CharacterExpression.Neutral, Pose) : this;
	public override string ToString() => IsPose ? "Pose." + Pose : Expression.ToString();
}

/// <summary>
/// Touch regions for one body silhouette, in 1024 x 1536 reference-sprite coordinates.
/// Measured on the 2026-10-13 cel-shaded set (master + seven poses); the expressions share the
/// master geometry because only their face ellipse differs from the master pixels.
/// </summary>
internal sealed class CharacterHitGeometry
{
	public string Name { get; }
	public Rect Ahoge { get; }
	public IReadOnlyList<Point> Chest { get; }
	/// <summary>Verified opaque reference points used by the offscreen validators.</summary>
	public Point AhogeSample { get; }
	public Point ChestSample { get; }
	public Point BodySample { get; }
	public Point BackgroundSample { get; } = new Point(60, 60);

	private CharacterHitGeometry(string name, Rect ahoge, Point[] chest, Point ahogeSample, Point chestSample, Point bodySample)
	{
		Name = name;
		Ahoge = ahoge;
		Chest = Array.AsReadOnly(chest);
		AhogeSample = ahogeSample;
		ChestSample = chestSample;
		BodySample = bodySample;
	}

	private static Point[] P(params int[] xy)
	{
		Point[] points = new Point[xy.Length / 2];
		for (int i = 0; i < points.Length; i++) points[i] = new Point(xy[i * 2], xy[i * 2 + 1]);
		return points;
	}

	public static CharacterHitGeometry Master { get; } = new("Standard", new Rect(305, 58, 107, 64),
		P(330, 395, 380, 372, 430, 362, 480, 370, 530, 392, 560, 440, 545, 500, 480, 525, 420, 520, 360, 525, 320, 500, 310, 450),
		new Point(358, 73), new Point(428, 445), new Point(428, 585));

	private static readonly Dictionary<CharacterPose, CharacterHitGeometry> Poses = new()
	{
		[CharacterPose.Standard] = Master,
		[CharacterPose.AhogeGuard] = new("AhogeGuard", new Rect(300, 100, 120, 58),
			P(320, 410, 400, 390, 470, 390, 540, 410, 575, 460, 560, 530, 480, 560, 400, 560, 330, 540, 312, 480),
			new Point(356, 112), new Point(438, 473), new Point(438, 620)),
		[CharacterPose.Wave] = new("Wave", new Rect(298, 80, 107, 70),
			P(320, 405, 390, 385, 460, 385, 520, 400, 545, 450, 530, 520, 460, 550, 390, 550, 325, 530, 310, 470),
			new Point(346, 94), new Point(425, 464), new Point(425, 610)),
		[CharacterPose.Hips] = new("Hips", new Rect(298, 115, 122, 63),
			P(350, 430, 420, 410, 500, 410, 570, 430, 600, 480, 585, 550, 510, 580, 430, 580, 360, 560, 340, 500),
			new Point(359, 137), new Point(466, 493), new Point(466, 640)),
		[CharacterPose.ArmsCrossed] = new("ArmsCrossed", new Rect(298, 112, 107, 58),
			P(325, 430, 390, 410, 460, 410, 525, 430, 545, 480, 530, 540, 460, 555, 390, 555, 330, 540, 315, 485),
			new Point(351, 134), new Point(427, 483), new Point(427, 615)),
		[CharacterPose.Stop] = new("Stop", new Rect(318, 105, 117, 63),
			P(415, 410, 480, 385, 550, 385, 615, 405, 640, 450, 625, 510, 560, 535, 490, 535, 430, 520, 408, 465),
			new Point(376, 121), new Point(521, 460), new Point(521, 595)),
		[CharacterPose.Thinking] = new("Thinking", new Rect(338, 95, 117, 65),
			P(405, 430, 460, 415, 520, 415, 575, 430, 595, 470, 580, 525, 520, 545, 460, 545, 410, 530, 395, 480),
			new Point(396, 113), new Point(492, 478), new Point(492, 605)),
		[CharacterPose.Shy] = new("Shy", new Rect(298, 88, 122, 62),
			P(378, 435, 440, 415, 510, 415, 575, 435, 595, 480, 580, 545, 510, 570, 440, 570, 385, 555, 370, 500),
			new Point(355, 99), new Point(478, 492), new Point(478, 630)),
	};

	public static CharacterHitGeometry For(CharacterPose pose)
		=> Poses.TryGetValue(pose, out CharacterHitGeometry? geometry) ? geometry
			: throw new ArgumentOutOfRangeException(nameof(pose), pose, "Unknown character pose.");

	public static CharacterHitGeometry For(CharacterLook look) => For(look.Pose);
}
