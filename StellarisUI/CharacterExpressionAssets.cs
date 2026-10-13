using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StellarisUI;

internal enum CharacterExpression { Neutral, Smile, Laugh, Surprised, Angry, Pout, Shy, Focused, Wry }

/// <summary>
/// Owns one window's frozen, embedded character sprites: the salute master (neutral), eight
/// face-only expression variants on that master body, and seven whole-body action poses.
/// Expressions must keep the master silhouette; poses are separate bodies with their own hit geometry.
/// </summary>
internal sealed class CharacterExpressionAssets
{
	private const int SourceWidth = 1024;
	private const int SourceHeight = 1536;
	private const byte VisibleAlphaThreshold = 12;
	private const int MaximumBoundsDrift = 3;
	private static readonly CharacterExpression[] Variants =
	{
		CharacterExpression.Smile, CharacterExpression.Laugh, CharacterExpression.Surprised,
		CharacterExpression.Angry, CharacterExpression.Pout, CharacterExpression.Shy,
		CharacterExpression.Focused, CharacterExpression.Wry
	};
	// Face plus both upper shoulders: a presentation-only viewport, never a sprite rewrite.
	private static readonly Rect FacePreviewSourceRect = new Rect(300, 110, 330, 260);
	private static readonly CharacterPose[] PoseVariants =
	{
		CharacterPose.AhogeGuard, CharacterPose.Wave, CharacterPose.Hips, CharacterPose.ArmsCrossed,
		CharacterPose.Stop, CharacterPose.Thinking, CharacterPose.Shy
	};
	public static IReadOnlyList<CharacterPose> Poses => PoseVariants;
	public static string PoseFileName(CharacterPose pose) => "character-pose-" + pose switch
	{
		CharacterPose.AhogeGuard => "ahoge-guard",
		CharacterPose.ArmsCrossed => "arms-crossed",
		_ => pose.ToString().ToLowerInvariant()
	} + ".png";
	private sealed record CachedAsset(string FileName, BitmapSource Source, string EncodedSha256);
	private sealed record PixelBounds(int Left, int Top, int Right, int Bottom);
	private sealed record AssetResult(string Expression, string FileName, string EncodedSha256,
		int Width, int Height, double DpiX, double DpiY, string PixelFormat,
		long TransparentPixels, long OpaquePixels, byte MinimumAlpha, byte MaximumAlpha,
		PixelBounds VisibleBounds, int BoundsDriftPixels, bool InteractionPassed);
	private readonly Dictionary<CharacterLook, CachedAsset> _assets = new();

	public CharacterExpressionAssets()
	{
		ResourceManager resources = new ResourceManager("StellarisUI.g", typeof(CharacterExpressionAssets).Assembly);
		try
		{
			_assets.Add(CharacterLook.Of(CharacterExpression.Neutral), Decode(resources, "character-neutral.png"));
			foreach (CharacterExpression expression in Variants)
			{
				string filename = "character-expression-" + expression.ToString().ToLowerInvariant() + ".png";
				_assets.Add(CharacterLook.Of(expression), Decode(resources, filename));
			}
			foreach (CharacterPose pose in PoseVariants)
				_assets.Add(CharacterLook.Of(pose), Decode(resources, PoseFileName(pose)));
		}
		finally
		{
			// OnLoad + Freeze detaches every cached bitmap from its resource stream/manager.
			resources.ReleaseAllResources();
		}
	}

	public BitmapSource Load(CharacterExpression expression) => Load(CharacterLook.Of(expression));

	public BitmapSource Load(CharacterLook look)
	{
		return _assets.TryGetValue(look.Normalized, out CachedAsset? asset)
			? asset.Source : throw new ArgumentOutOfRangeException(nameof(look), look, "Unknown character look.");
	}

	public static CharacterHitGeometry Geometry(CharacterLook look) => CharacterHitGeometry.For(look.Normalized);

	/// <summary>Every displayable look: neutral, eight expressions, seven poses.</summary>
	public static IEnumerable<CharacterLook> AllLooks()
	{
		yield return CharacterLook.Of(CharacterExpression.Neutral);
		foreach (CharacterExpression expression in Variants) yield return CharacterLook.Of(expression);
		foreach (CharacterPose pose in PoseVariants) yield return CharacterLook.Of(pose);
	}

	/// <summary>Offscreen-only validation: no MainWindow, configuration, timers, or audio.</summary>
	public static void Validate()
	{
		new CharacterExpressionAssets().Inspect();
	}

	/// <summary>Renders only a diagnostic contact sheet and report; never changes embedded sprites.</summary>
	public static void RenderPreview(string outputDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
		CharacterExpressionAssets assets = new CharacterExpressionAssets();
		IReadOnlyList<AssetResult> results = assets.Inspect();
		Directory.CreateDirectory(outputDirectory);
		const int columns = 4;
		const int rows = 2;
		const int cellWidth = 440;
		const int cellHeight = 390;
		const int padding = 20;
		const int headingHeight = 64;
		const int width = padding * 2 + columns * cellWidth;
		const int height = padding * 2 + headingHeight + rows * cellHeight;
		DrawingVisual scene = new DrawingVisual();
		using (DrawingContext drawing = scene.RenderOpen())
		{
			drawing.DrawRectangle(Brush(Color.FromRgb(7, 12, 20)), null, new Rect(0, 0, width, height));
			DrawText(drawing, "GRAY WIND // APPROVED EXPRESSION SPRITES", new Point(padding + 8, padding), 24,
				Color.FromRgb(204, 234, 249));
			DrawText(drawing, "Face + upper shoulders / unchanged embedded PNGs / 4 x 2", new Point(padding + 8, padding + 32), 14,
				Color.FromRgb(129, 155, 178));
			for (int index = 0; index < Variants.Length; index++)
			{
				CharacterExpression expression = Variants[index];
				double left = padding + index % columns * cellWidth;
				double top = padding + headingHeight + index / columns * cellHeight;
				Rect card = new Rect(left + 8, top + 8, cellWidth - 16, cellHeight - 16);
				drawing.DrawRoundedRectangle(Brush(Color.FromRgb(14, 24, 37)),
					new Pen(Brush(Color.FromRgb(46, 76, 99)), 1), card, 8, 8);
				DrawText(drawing, (index + 1).ToString("00", CultureInfo.InvariantCulture) + "  " + expression,
					new Point(card.Left + 14, card.Top + 12), 19, Color.FromRgb(192, 228, 243));
				Rect imageViewport = new Rect(card.Left + 12, card.Top + 48, card.Width - 24, card.Height - 64);
				DrawFaceViewport(drawing, assets.Load(expression), imageViewport);
			}
		}
		RenderTargetBitmap preview = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		preview.Render(scene);
		preview.Freeze();
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(preview));
		using (FileStream output = new FileStream(Path.Combine(outputDirectory, "character-expression-contact-sheet.png"),
			FileMode.Create, FileAccess.Write, FileShare.None))
		{
			encoder.Save(output);
		}
		RenderPoseSheet(assets, Path.Combine(outputDirectory, "character-pose-contact-sheet.png"));
		File.WriteAllText(Path.Combine(outputDirectory, "character-expression-result.json"),
			JsonSerializer.Serialize(new
			{
				Passed = true,
				AlphaVisibilityThreshold = VisibleAlphaThreshold,
				MaximumAllowedBoundsDriftPixels = MaximumBoundsDrift,
				Preview = new
				{
					Columns = columns, Rows = rows, Width = width, Height = height,
					SourceViewport = new
					{
						X = FacePreviewSourceRect.X, Y = FacePreviewSourceRect.Y,
						Width = FacePreviewSourceRect.Width, Height = FacePreviewSourceRect.Height
					},
					PresentationOnly = true
				},
				Assets = results
			}, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static void RenderPoseSheet(CharacterExpressionAssets assets, string path)
	{
		const int columns = 4, rows = 2, cellWidth = 300, cellHeight = 470, padding = 20, headingHeight = 56;
		const int width = padding * 2 + columns * cellWidth;
		const int height = padding * 2 + headingHeight + rows * cellHeight;
		DrawingVisual scene = new DrawingVisual();
		using (DrawingContext drawing = scene.RenderOpen())
		{
			drawing.DrawRectangle(Brush(Color.FromRgb(7, 12, 20)), null, new Rect(0, 0, width, height));
			DrawText(drawing, "GRAY WIND // MASTER + SEVEN ACTION POSES", new Point(padding + 8, padding), 24, Color.FromRgb(204, 234, 249));
			int index = 0;
			foreach (CharacterLook look in new[] { CharacterLook.Of(CharacterExpression.Neutral) }
				.Concat(PoseVariants.Select(pose => CharacterLook.Of(pose))))
			{
				double left = padding + index % columns * cellWidth;
				double top = padding + headingHeight + index / columns * cellHeight;
				Rect card = new Rect(left + 6, top + 6, cellWidth - 12, cellHeight - 12);
				drawing.DrawRoundedRectangle(Brush(Color.FromRgb(14, 24, 37)), new Pen(Brush(Color.FromRgb(46, 76, 99)), 1), card, 8, 8);
				DrawText(drawing, look.ToString(), new Point(card.Left + 12, card.Top + 8), 16, Color.FromRgb(192, 228, 243));
				BitmapSource source = assets.Load(look);
				double scale = Math.Min((card.Width - 20) / source.PixelWidth, (card.Height - 44) / source.PixelHeight);
				double w = source.PixelWidth * scale, h = source.PixelHeight * scale;
				Rect image = new Rect(card.Left + (card.Width - w) / 2, card.Top + 36, w, h);
				drawing.DrawImage(source, image);
				CharacterHitGeometry geometry = Geometry(look);
				Rect a = geometry.Ahoge;
				drawing.DrawRectangle(null, new Pen(Brush(Color.FromRgb(255, 219, 111)), 1.5),
					new Rect(image.Left + a.X * scale, image.Top + a.Y * scale, a.Width * scale, a.Height * scale));
				StreamGeometry chest = new StreamGeometry();
				using (StreamGeometryContext context = chest.Open())
				{
					context.BeginFigure(new Point(image.Left + geometry.Chest[0].X * scale, image.Top + geometry.Chest[0].Y * scale), false, true);
					foreach (Point point in geometry.Chest.Skip(1))
						context.LineTo(new Point(image.Left + point.X * scale, image.Top + point.Y * scale), true, false);
				}
				chest.Freeze();
				drawing.DrawGeometry(null, new Pen(Brush(Color.FromRgb(255, 130, 192)), 1.5), chest);
				index++;
			}
		}
		RenderTargetBitmap preview = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		preview.Render(scene);
		preview.Freeze();
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(preview));
		using FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
		encoder.Save(output);
	}

	private IReadOnlyList<AssetResult> Inspect()
	{
		List<AssetResult> results = new List<AssetResult>();
		CharacterLook neutralLook = CharacterLook.Of(CharacterExpression.Neutral);
		CachedAsset neutral = _assets[neutralLook];
		AssetResult reference = InspectAsset(neutralLook, neutral, null);
		results.Add(reference);
		foreach (CharacterLook look in AllLooks().Skip(1))
		{
			CachedAsset asset = _assets[look];
			if (Math.Abs(asset.Source.DpiX - neutral.Source.DpiX) > 0.01
				|| Math.Abs(asset.Source.DpiY - neutral.Source.DpiY) > 0.01)
			{
				throw InvalidAsset(asset.FileName, "DPI differs from the approved neutral sprite");
			}
			// Expressions must keep the master silhouette; whole-body poses have their own outline.
			results.Add(InspectAsset(look, asset, look.IsPose ? null : reference.VisibleBounds));
		}
		return results;
	}

	private static AssetResult InspectAsset(CharacterLook look, CachedAsset asset, PixelBounds? referenceBounds)
	{
		BitmapSource source = asset.Source;
		if (source.PixelWidth != SourceWidth || source.PixelHeight != SourceHeight)
			throw InvalidAsset(asset.FileName, "must retain the approved 1024 x 1536 full-body canvas");
		if (!source.IsFrozen || !double.IsFinite(source.DpiX) || !double.IsFinite(source.DpiY)
			|| source.DpiX <= 0 || source.DpiY <= 0 || Math.Abs(source.DpiX - source.DpiY) > 0.01)
			throw InvalidAsset(asset.FileName, "must have a frozen bitmap and valid square-pixel DPI");
		BitmapSource bitmap = source.Format == PixelFormats.Bgra32 ? source
			: new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
		int stride = checked(SourceWidth * 4);
		byte[] pixels = new byte[checked(stride * SourceHeight)];
		bitmap.CopyPixels(pixels, stride, 0);
		if (pixels[3] > 8 || pixels[(SourceWidth - 1) * 4 + 3] > 8
			|| pixels[(SourceHeight - 1) * stride + 3] > 8 || pixels[pixels.Length - 1] > 8)
			throw InvalidAsset(asset.FileName, "PNG corners must remain genuinely transparent");
		long transparent = 0;
		long opaque = 0;
		byte minimumAlpha = byte.MaxValue;
		byte maximumAlpha = byte.MinValue;
		int left = SourceWidth, top = SourceHeight, right = -1, bottom = -1;
		for (int y = 0; y < SourceHeight; y++)
		{
			for (int x = 0; x < SourceWidth; x++)
			{
				byte alpha = pixels[y * stride + x * 4 + 3];
				minimumAlpha = Math.Min(minimumAlpha, alpha);
				maximumAlpha = Math.Max(maximumAlpha, alpha);
				if (alpha <= 8) transparent++;
				// Approved imagegen sprites have soft opacity (including max-alpha 254).
				if (alpha >= 230) opaque++;
				if (alpha < VisibleAlphaThreshold) continue;
				left = Math.Min(left, x);
				top = Math.Min(top, y);
				right = Math.Max(right, x);
				bottom = Math.Max(bottom, y);
			}
		}
		long count = (long)SourceWidth * SourceHeight;
		if (transparent * 10 < count || opaque * 10 < count || right < left || bottom < top)
			throw InvalidAsset(asset.FileName, "must contain a substantial opaque figure and real transparent background");
		PixelBounds bounds = new PixelBounds(left, top, right, bottom);
		int drift = referenceBounds == null ? 0 : Math.Max(
			Math.Max(Math.Abs(left - referenceBounds.Left), Math.Abs(top - referenceBounds.Top)),
			Math.Max(Math.Abs(right - referenceBounds.Right), Math.Abs(bottom - referenceBounds.Bottom)));
		if (drift > MaximumBoundsDrift)
			throw InvalidAsset(asset.FileName, "expression changes the master silhouette by more than 3 source pixels");
		// Reuses all existing ahoge/chest/body/background samples, Uniform mapping, and alpha checks.
		try
		{
			CharacterInteractionValidator.Validate(source, Geometry(look));
		}
		catch (InvalidOperationException ex)
		{
			throw InvalidAsset(asset.FileName, ex.Message);
		}
		return new AssetResult(look.ToString(), asset.FileName, asset.EncodedSha256,
			source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY, source.Format.ToString(),
			transparent, opaque, minimumAlpha, maximumAlpha, bounds, drift, true);
	}

	private static CachedAsset Decode(ResourceManager resources, string filename)
	{
		using Stream resource = resources.GetStream("assets/" + filename, CultureInfo.InvariantCulture)
			?? throw InvalidAsset(filename, "embedded expression PNG is missing");
		using MemoryStream encoded = new MemoryStream();
		resource.CopyTo(encoded);
		byte[] bytes = encoded.ToArray();
		if (bytes.Length < 33 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71
			|| bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10
			|| bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82
			|| bytes[25] != 6)
			throw InvalidAsset(filename, "must be a true-alpha RGBA PNG, not a flattened background");
		encoded.Position = 0;
		BitmapDecoder decoder;
		try
		{
			decoder = BitmapDecoder.Create(encoded, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
		}
		catch (Exception ex) when (ex is FileFormatException || ex is NotSupportedException || ex is ArgumentException)
		{
			throw InvalidAsset(filename, "embedded expression PNG cannot be decoded");
		}
		if (decoder.Frames.Count != 1)
			throw InvalidAsset(filename, "expression PNG must have exactly one frame");
		BitmapFrame bitmap = decoder.Frames[0];
		bitmap.Freeze();
		return new CachedAsset(filename, bitmap, Convert.ToHexString(SHA256.HashData(bytes)));
	}

	private static void DrawFaceViewport(DrawingContext drawing, BitmapSource source, Rect viewport)
	{
		double scale = Math.Min(viewport.Width / FacePreviewSourceRect.Width, viewport.Height / FacePreviewSourceRect.Height);
		double left = viewport.Left + (viewport.Width - FacePreviewSourceRect.Width * scale) / 2;
		double top = viewport.Top + (viewport.Height - FacePreviewSourceRect.Height * scale) / 2;
		Rect clip = new Rect(left, top, FacePreviewSourceRect.Width * scale, FacePreviewSourceRect.Height * scale);
		drawing.PushClip(new RectangleGeometry(clip));
		drawing.DrawImage(source, new Rect(left - FacePreviewSourceRect.Left * scale,
			top - FacePreviewSourceRect.Top * scale, source.PixelWidth * scale, source.PixelHeight * scale));
		drawing.Pop();
	}

	private static void DrawText(DrawingContext drawing, string text, Point origin, double fontSize, Color color)
	{
		drawing.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
			new Typeface("Segoe UI"), fontSize, Brush(color), 1.0), origin);
	}

	private static SolidColorBrush Brush(Color color)
	{
		SolidColorBrush brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private static InvalidOperationException InvalidAsset(string filename, string reason)
		=> new InvalidOperationException(filename + ": " + reason);
}
