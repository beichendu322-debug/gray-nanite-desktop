using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace StellarisUI;

/// <summary>Offscreen-only checks: never constructs the desktop window, plays audio, or edits configuration.</summary>
internal static class CharacterInteractionValidator
{
	private sealed record ReferenceSample(string Name, Point Point, CharacterTouchRegion Region);
	private static ReferenceSample[] SamplesFor(CharacterHitGeometry geometry) => new ReferenceSample[]
	{
		new("ahoge", geometry.AhogeSample, CharacterTouchRegion.Ahoge),
		new("chest", geometry.ChestSample, CharacterTouchRegion.Chest),
		new("body", geometry.BodySample, CharacterTouchRegion.Body),
		new("transparent-background", geometry.BackgroundSample, CharacterTouchRegion.None)
	};

	internal sealed record LayoutSample(string Name, string Expected, string Actual,
		Point SourcePoint, Point ImagePoint, Point ScenePoint,
		bool InsideClip, bool InsideViewport, double? RoundTripError, bool Passed);
	internal sealed record LayoutResult(bool Passed, IReadOnlyList<LayoutSample> Samples);

	public static void Validate(BitmapSource character, CharacterHitGeometry? geometry = null)
	{
		geometry ??= CharacterHitGeometry.Master;
		ReferenceSample[] Samples = SamplesFor(geometry);
		CharacterHitMap hitMap = new CharacterHitMap(character, geometry);
		foreach (ReferenceSample sample in Samples)
		{
			Point sourcePoint = ToSource(sample.Point, character);
			Require(hitMap.RegionAtSourcePixel(sourcePoint) == sample.Region,
				"source-pixel region mismatch for " + sample.Name + " on " + geometry.Name);
		}
		foreach (Point invalid in new[]
		{
			new Point(-1, 100), new Point(100, -1),
			new Point(character.PixelWidth, 100), new Point(100, character.PixelHeight),
			new Point(double.NaN, 100), new Point(100, double.PositiveInfinity)
		})
		{
			Require(hitMap.RegionAtSourcePixel(invalid) == CharacterTouchRegion.None,
				"out-of-image or non-finite source coordinates must not respond");
		}

		// Both letterbox axes matter: the Image control's bounds are not the painted PNG bounds.
		foreach (Size imageSize in new[] { new Size(600, 900), new Size(900, 600), new Size(400, 900) })
		{
			foreach (ReferenceSample sample in Samples)
			{
				Point sourcePoint = ToSource(sample.Point, character);
				Point imagePoint = ToImage(sourcePoint, imageSize, character);
				Require(hitMap.TryMapImagePoint(imagePoint, imageSize, out Point roundTrip),
					"Uniform mapping rejected a source pixel");
				Require(Distance(roundTrip, sourcePoint) < 0.00001,
					"Uniform mapping did not invert its source coordinates");
				Require(hitMap.RegionAtImagePoint(imagePoint, imageSize) == sample.Region,
					"Uniform mapping selected the wrong region for " + sample.Name);
			}
			foreach (Point outside in new[]
			{
				new Point(-1, imageSize.Height / 2), new Point(imageSize.Width + 1, imageSize.Height / 2),
				new Point(imageSize.Width / 2, -1), new Point(imageSize.Width / 2, imageSize.Height + 1)
			})
			{
				Require(!hitMap.TryMapImagePoint(outside, imageSize, out _)
					&& hitMap.RegionAtImagePoint(outside, imageSize) == CharacterTouchRegion.None,
					"out-of-control coordinates must not respond");
			}
		}
		Require(!hitMap.TryMapImagePoint(new Point(10, 300), new Size(900, 600), out _),
			"horizontal Uniform letterboxing must not respond");
		Require(!hitMap.TryMapImagePoint(new Point(200, 10), new Size(400, 900), out _),
			"vertical Uniform letterboxing must not respond");
		Require(!hitMap.TryMapImagePoint(new Point(0, 0), new Size(0, 0), out _),
			"unmeasured Image controls must not respond");
		ValidateAlphaThresholds();
	}

	private static void ValidateAlphaThresholds()
	{
		// Synthetic sprite on the master geometry: thresholds and the 16 px ahoge allowance.
		CharacterHitGeometry g = CharacterHitGeometry.Master;
		const int width = 1024;
		const int height = 1536;
		byte[] pixels = new byte[width * height * 4];
		void Pixel(Point p, byte alpha) => pixels[((int)p.Y * width + (int)p.X) * 4 + 3] = alpha;
		Point ahoge = new Point(g.Ahoge.Left + 10, g.Ahoge.Top + 10);
		Point ahogeFaint = new Point(g.Ahoge.Left + 70, g.Ahoge.Top + 45);
		Point chest = g.ChestSample, chestFaint = new Point(g.ChestSample.X + 40, g.ChestSample.Y + 4);
		Point body = g.BodySample, bodyFaint = new Point(g.BodySample.X + 8, g.BodySample.Y + 15);
		Pixel(ahoge, 32);
		Pixel(ahogeFaint, 31);
		Pixel(chest, 64);
		Pixel(chestFaint, 63);
		Pixel(body, 64);
		Pixel(bodyFaint, 63);
		BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
			null, pixels, width * 4);
		bitmap.Freeze();
		CharacterHitMap map = new CharacterHitMap(bitmap, g);
		Require(map.RegionAtSourcePixel(ahoge) == CharacterTouchRegion.Ahoge,
			"ahoge alpha threshold must include 32");
		Require(map.RegionAtSourcePixel(new Point(ahoge.X + 16, ahoge.Y)) == CharacterTouchRegion.Ahoge,
			"small ahoge target needs its 16-pixel touch allowance");
		Require(map.RegionAtSourcePixel(new Point(ahoge.X + 17, ahoge.Y)) == CharacterTouchRegion.None,
			"ahoge touch allowance must not extend beyond 16 source pixels");
		Require(map.RegionAtSourcePixel(ahogeFaint) == CharacterTouchRegion.None,
			"nearly transparent ahoge background must not respond");
		Require(map.RegionAtSourcePixel(chest) == CharacterTouchRegion.Chest,
			"chest alpha threshold must include 64");
		Require(map.RegionAtSourcePixel(chestFaint) == CharacterTouchRegion.None,
			"chest polygon must not override transparent pixels");
		Require(map.RegionAtSourcePixel(body) == CharacterTouchRegion.Body,
			"body alpha threshold must include 64");
		Require(map.RegionAtSourcePixel(bodyFaint) == CharacterTouchRegion.None,
			"body background must not respond below alpha 64");
	}

	public static LayoutResult CheckLayout(Grid scene, Image figure, Grid stage,
		Rect localClip, Size resolution, BitmapSource character, CharacterHitGeometry? geometry = null)
	{
		geometry ??= CharacterHitGeometry.Master;
		ReferenceSample[] Samples = SamplesFor(geometry);
		CharacterHitMap map = new CharacterHitMap(character, geometry);
		Size imageSize = new Size(figure.ActualWidth, figure.ActualHeight);
		GeneralTransform imageToScene = figure.TransformToAncestor(scene);
		GeneralTransform sceneToImage = imageToScene.Inverse
			?? throw new InvalidOperationException("character interaction transform is not invertible");
		GeneralTransform imageToStage = figure.TransformToAncestor(stage);
		List<LayoutSample> samples = new List<LayoutSample>();
		foreach (ReferenceSample reference in Samples)
		{
			Point sourcePoint = ToSource(reference.Point, character);
			Point imagePoint = ToImage(sourcePoint, imageSize, character);
			Point scenePoint = imageToScene.Transform(imagePoint);
			Point clickPoint = sceneToImage.Transform(scenePoint);
			CharacterTouchRegion actual = map.RegionAtImagePoint(clickPoint, imageSize);
			bool mapped = map.TryMapImagePoint(clickPoint, imageSize, out Point roundTrip);
			double error = mapped ? Distance(sourcePoint, roundTrip) : double.PositiveInfinity;
			bool insideClip = localClip.Contains(imageToStage.Transform(clickPoint));
			bool insideViewport = new Rect(new Point(), resolution).Contains(scenePoint);
			bool visibleRegion = reference.Region != CharacterTouchRegion.None;
			bool passed = actual == reference.Region && mapped && error < 0.00001
				&& (!visibleRegion || insideClip && insideViewport);
			samples.Add(new LayoutSample(reference.Name, reference.Region.ToString(), actual.ToString(),
				sourcePoint, imagePoint, scenePoint, insideClip, insideViewport, mapped ? Math.Round(error, 8) : null, passed));
		}
		return new LayoutResult(samples.All(sample => sample.Passed), samples);
	}

	public static void AddLayoutOverlay(Grid scene, Image figure, Size resolution,
		BitmapSource character, LayoutResult result, CharacterHitGeometry? geometry = null)
	{
		geometry ??= CharacterHitGeometry.Master;
		Size imageSize = new Size(figure.ActualWidth, figure.ActualHeight);
		GeneralTransform imageToScene = figure.TransformToAncestor(scene);
		Point ScenePoint(Point reference) => imageToScene.Transform(ToImage(ToSource(reference, character), imageSize, character));
		Canvas overlay = new Canvas { IsHitTestVisible = false };
		Rect ahoge = geometry.Ahoge;
		AddPolygon(new[] { ahoge.TopLeft, ahoge.TopRight, ahoge.BottomRight, ahoge.BottomLeft },
			Color.FromRgb(255, 219, 111));
		AddPolygon(geometry.Chest, Color.FromRgb(255, 130, 192));
		void AddPolygon(IEnumerable<Point> referencePoints, Color color)
		{
			overlay.Children.Add(new Polygon
			{
				Points = new PointCollection(referencePoints.Select(ScenePoint)),
				Stroke = new SolidColorBrush(color), StrokeThickness = 2,
				Fill = new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B))
			});
		}
		foreach (LayoutSample sample in result.Samples)
		{
			Color color = sample.Expected switch
			{
				"Ahoge" => Color.FromRgb(255, 219, 111),
				"Chest" => Color.FromRgb(255, 130, 192),
				"Body" => Color.FromRgb(133, 255, 186),
				_ => Color.FromRgb(165, 178, 196)
			};
			Ellipse point = new Ellipse
			{
				Width = 12, Height = 12, Stroke = new SolidColorBrush(color),
				StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0))
			};
			Canvas.SetLeft(point, sample.ScenePoint.X - 6);
			Canvas.SetTop(point, sample.ScenePoint.Y - 6);
			overlay.Children.Add(point);
			TextBlock label = new TextBlock
			{
				Text = sample.Name + (sample.Passed ? " / PASS" : " / FAIL"),
				Foreground = new SolidColorBrush(color), Background = new SolidColorBrush(Color.FromArgb(225, 7, 10, 15)),
				FontFamily = new FontFamily("Consolas"), FontSize = 12
			};
			Canvas.SetLeft(label, sample.ScenePoint.X + 10);
			Canvas.SetTop(label, Math.Max(0, sample.ScenePoint.Y - 6));
			overlay.Children.Add(label);
		}
		scene.Children.Add(overlay);
		scene.Measure(resolution);
		scene.Arrange(new Rect(new Point(), resolution));
		scene.UpdateLayout();
	}

	private static Point ToSource(Point reference, BitmapSource character)
		=> new Point(reference.X * character.PixelWidth / 1024.0, reference.Y * character.PixelHeight / 1536.0);

	private static Point ToImage(Point sourcePoint, Size imageSize, BitmapSource character)
	{
		double scale = Math.Min(imageSize.Width / character.PixelWidth, imageSize.Height / character.PixelHeight);
		return new Point((imageSize.Width - character.PixelWidth * scale) / 2 + sourcePoint.X * scale,
			(imageSize.Height - character.PixelHeight * scale) / 2 + sourcePoint.Y * scale);
	}

	private static double Distance(Point first, Point second)
		=> Math.Max(Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));

	private static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException("character interaction regression: " + message);
	}
}
