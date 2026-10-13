using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StellarisUI;

/// <summary>Offscreen expression/pose checks: no desktop window, audio, configuration, or file writes.</summary>
internal static class CharacterExpressionInteractionValidator
{
	private static readonly (string FileName, CharacterTouchRegion Region, CharacterExpression Expression, CharacterPose Pose)[] ExpectedVoiceMapping =
	{
		("ahoge-01.mp3", CharacterTouchRegion.Ahoge, CharacterExpression.Surprised, CharacterPose.AhogeGuard),
		("ahoge-02.mp3", CharacterTouchRegion.Ahoge, CharacterExpression.Angry, CharacterPose.Standard),
		("ahoge-03.mp3", CharacterTouchRegion.Ahoge, CharacterExpression.Pout, CharacterPose.Standard),
		("body-01.mp3", CharacterTouchRegion.Body, CharacterExpression.Laugh, CharacterPose.Standard),
		("body-02.mp3", CharacterTouchRegion.Body, CharacterExpression.Smile, CharacterPose.Wave),
		("body-03.mp3", CharacterTouchRegion.Body, CharacterExpression.Wry, CharacterPose.Hips),
		("chest-01.mp3", CharacterTouchRegion.Chest, CharacterExpression.Shy, CharacterPose.Shy),
		("chest-02.mp3", CharacterTouchRegion.Chest, CharacterExpression.Angry, CharacterPose.ArmsCrossed),
		("chest-03.mp3", CharacterTouchRegion.Chest, CharacterExpression.Wry, CharacterPose.Standard)
	};

	public static void ValidateVoiceMapping()
	{
		Require(CharacterVoiceInteraction.Lines.Count == ExpectedVoiceMapping.Length,
			"voice catalog must contain exactly nine replies");
		Require(CharacterVoiceInteraction.Lines.Select(line => line.FileName).Distinct(StringComparer.Ordinal).Count()
			== ExpectedVoiceMapping.Length, "voice catalog must contain nine unique clip names");
		foreach (var expected in ExpectedVoiceMapping)
		{
			CharacterVoiceLine? line = CharacterVoiceInteraction.Lines.SingleOrDefault(item => item.FileName == expected.FileName);
			Require(line != null && line.Region == expected.Region && line.Expression == expected.Expression
				&& line.Pose == expected.Pose, "voice expression/pose mapping changed for " + expected.FileName);
		}
	}

	public static void Validate()
	{
		ValidateVoiceMapping();
		ValidateExpressionLifecycle();
		CharacterExpressionAssets assets = new CharacterExpressionAssets();
		Image figure = new Image { Width = 256, Height = 384, Stretch = Stretch.Uniform };
		Size frameSize = new Size(figure.Width, figure.Height);
		foreach (CharacterLook look in CharacterExpressionAssets.AllLooks())
		{
			BitmapSource character = assets.Load(look);
			Require(character.IsFrozen, "sprite source must be frozen: " + look);
			CharacterInteractionValidator.Validate(character, CharacterExpressionAssets.Geometry(look));
			// Switch one detached Image through every source, including ambient-only looks.
			figure.Source = character;
			figure.Measure(frameSize);
			figure.Arrange(new Rect(new Point(), frameSize));
			figure.UpdateLayout();
			RenderTargetBitmap frame = new RenderTargetBitmap(256, 384, 96, 96, PixelFormats.Pbgra32);
			frame.Render(figure);
			byte[] pixels = new byte[256 * 384 * 4];
			frame.CopyPixels(pixels, 256 * 4, 0);
			bool visible = false;
			for (int offset = 3; offset < pixels.Length && !visible; offset += 4)
				visible = pixels[offset] >= 64;
			Require(visible, "offscreen sprite rendered an empty figure: " + look);
		}
		// Every voice line must resolve to a loadable sprite.
		foreach (CharacterVoiceLine line in CharacterVoiceInteraction.Lines)
			Require(assets.Load(line.Look).IsFrozen, "voice line has no sprite: " + line.FileName);
		figure.Source = null;
	}

	public static void ValidateExpressionLifecycle()
	{
		CharacterLook neutral = CharacterLook.Of(CharacterExpression.Neutral);
		CharacterLook thinking = CharacterLook.Of(CharacterPose.Thinking);
		foreach (var expected in new (string? Mood, CharacterLook Look)[]
		{
			(null, neutral),
			("idle", neutral),
			("unknown", neutral),
			("happy", CharacterLook.Of(CharacterExpression.Smile)),
			("analyzing", thinking),
			("alert", CharacterLook.Of(CharacterPose.Stop))
		})
			Require(CharacterExpressionState.FromMood(expected.Mood) == expected.Look,
				"ambient mood mapping changed for " + (expected.Mood ?? "null"));

		CharacterExpressionState state = new CharacterExpressionState();
		Require(state.Current == neutral && state.Ambient == neutral && state.Touch == null,
			"new expression state must be neutral with no touch");
		CharacterLook shy = CharacterLook.Of(CharacterPose.Shy);
		state.BeginTouch(shy);
		state.SetAmbientMood("analyzing");
		Require(state.Current == shy && state.Touch == shy && state.Ambient == thinking,
			"ambient updates must not replace a touch reaction");
		Require(state.EndTouch() && state.Touch == null && state.Current == thinking,
			"ending a touch must restore the latest ambient look");
		Require(!state.EndTouch() && state.Current == thinking,
			"ending a completed touch must be an idempotent no-op");
		CharacterLook surprised = CharacterLook.Of(CharacterExpression.Surprised);
		state.BeginTouch(surprised);
		state.SetAmbientMood("idle");
		Require(state.Current == surprised && state.Touch == surprised && state.Ambient == neutral,
			"a later touch must survive ambient reset");
		Require(state.EndTouch() && state.Touch == null && state.Current == neutral,
			"ending the later touch must restore idle");
		Require(new CharacterLook(CharacterExpression.Wry, CharacterPose.Hips).Normalized == CharacterLook.Of(CharacterPose.Hips),
			"poses must ignore the expression when choosing a sprite");
		foreach (CharacterLook look in CharacterExpressionAssets.AllLooks())
		{
			state.BeginTouch(look);
			Require(state.Current == look && state.Touch == look, "touch state rejected a defined look: " + look);
			Require(state.EndTouch() && state.Current == state.Ambient && state.Touch == null,
				"touch state did not restore ambient after " + look);
		}
	}

	private static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException("character expression interaction regression: " + message);
	}
}
