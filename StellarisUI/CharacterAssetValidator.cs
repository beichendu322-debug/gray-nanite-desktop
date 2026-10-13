using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace StellarisUI;

internal static class CharacterAssetValidator
{
	private static readonly string[] CharacterFiles =
	{
		"character-neutral.png",
		"character-happy.png",
		"character-analyzing.png",
		"character-alert.png",
		"character.png"
	};

	public static void Validate()
	{
		ResourceManager resources = new ResourceManager("StellarisUI.g", typeof(CharacterAssetValidator).Assembly);
		byte[]? expectedPixelHash = null;
		int expectedWidth = 0;
		int expectedHeight = 0;
		try
		{
			foreach (string filename in CharacterFiles)
			{
				using Stream resource = resources.GetStream("assets/" + filename, CultureInfo.InvariantCulture)
					?? throw InvalidAsset(filename, "embedded character resource is missing");
				using MemoryStream encoded = new MemoryStream();
				resource.CopyTo(encoded);
				if (encoded.Length < 100000)
				{
					throw InvalidAsset(filename, "embedded character resource is incomplete");
				}
				byte[] encodedBytes = encoded.ToArray();
				if (!HasPngSignature(encodedBytes))
				{
					throw InvalidAsset(filename, "character resource is not a PNG");
				}
				encoded.Position = 0;
				BitmapDecoder decoder;
				try
				{
					decoder = BitmapDecoder.Create(encoded, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
				}
				catch
				{
					throw InvalidAsset(filename, "character PNG cannot be decoded");
				}
				if (decoder.Frames.Count != 1)
				{
					throw InvalidAsset(filename, "character PNG must have one frame");
				}
				FormatConvertedBitmap bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0.0);
				bitmap.Freeze();
				int width = bitmap.PixelWidth;
				int height = bitmap.PixelHeight;
				if (width < 512 || height < 1280)
				{
					throw InvalidAsset(filename, "character PNG is too small for the approved full-body frame");
				}
				int stride = checked(width * 4);
				byte[] pixels = new byte[checked(stride * height)];
				bitmap.CopyPixels(pixels, stride, 0);
				ValidateTransparency(filename, pixels, width, height, stride);
				byte[] pixelHash = SHA256.HashData(pixels);
				if (expectedPixelHash == null)
				{
					CharacterInteractionValidator.Validate(bitmap);
					expectedWidth = width;
					expectedHeight = height;
					expectedPixelHash = pixelHash;
				}
				else if (width != expectedWidth || height != expectedHeight
					|| !CryptographicOperations.FixedTimeEquals(pixelHash, expectedPixelHash))
				{
					// All runtime states deliberately share the approved commander artwork.
					// Existing mood labels, pulses, breathing and projection effects remain active.
					throw InvalidAsset(filename, "character state does not match the approved commander design");
				}
			}
		}
		finally
		{
			resources.ReleaseAllResources();
		}
	}

	public static void RenderPreview(string outputDirectory)
	{
		Validate();
		Directory.CreateDirectory(outputDirectory);
		ResourceManager resources = new ResourceManager("StellarisUI.g", typeof(CharacterAssetValidator).Assembly);
		try
		{
			BitmapSource character = DecodeEmbeddedImage(resources, "character-neutral.png");
			BitmapSource background = DecodeEmbeddedImage(resources, "background-command.png");
			ImageBrush commandBackground = new ImageBrush(background)
			{
				Stretch = Stretch.UniformToFill,
				Opacity = 0.7
			};
			RenderCharacterPreview(character, commandBackground,
				Path.Combine(outputDirectory, "character-on-background.png"));
			RenderCharacterPreview(character, CreateAlphaCheckBackground(),
				Path.Combine(outputDirectory, "character-alpha-check.png"));
		}
		finally
		{
			resources.ReleaseAllResources();
		}
	}

	public static void RenderLayoutRegression(string outputDirectory, string xamlSourcePath, BitmapSource? expressionSource = null,
		CharacterHitGeometry? geometry = null)
	{
		Validate();
		Directory.CreateDirectory(outputDirectory);
		XDocument source = XDocument.Load(xamlSourcePath);
		ResourceManager resources = new ResourceManager("StellarisUI.g", typeof(CharacterAssetValidator).Assembly);
		List<object> results = new List<object>();
		bool failed = false;
		try
		{
			BitmapSource character = expressionSource ?? DecodeEmbeddedImage(resources, "character-neutral.png");
			BitmapSource background = DecodeEmbeddedImage(resources, "background-command.png");
			FormatConvertedBitmap alphaBitmap = new FormatConvertedBitmap(character, PixelFormats.Bgra32, null, 0.0);
			int alphaStride = checked(alphaBitmap.PixelWidth * 4);
			byte[] pixels = new byte[checked(alphaStride * alphaBitmap.PixelHeight)];
			alphaBitmap.CopyPixels(pixels, alphaStride, 0);
			foreach (Size resolution in new[] { new Size(2560, 1440), new Size(2560, 1392), new Size(2048, 1152), new Size(1920, 1080) })
			{
				foreach (string frame in new[] { "idle", "left-top-extreme", "right-bottom-extreme" })
				{
					bool idle = frame == "idle";
					bool rightExtreme = frame == "right-bottom-extreme";
					Grid scene = CreateLayoutOnlyScene(source);
					scene.Width = resolution.Width;
					scene.Height = resolution.Height;
					Image figure = Named<Image>(scene, "CharacterImage");
					figure.Source = character;
					Named<Image>(scene, "CharacterGhostImage").Source = character;
					Named<Image>(scene, "CommandBackdrop").Source = background;
					Named<TranslateTransform>(scene, "CharacterTranslate").X = idle ? 20 : rightExtreme ? 26 : 14;
					Named<TranslateTransform>(scene, "CharacterTranslate").Y = idle ? -72 : rightExtreme ? -66 : -78;
					Named<TranslateTransform>(scene, "CharacterGlitchTranslate").X = idle ? 0 : rightExtreme ? 11 : -8;
					Named<ScaleTransform>(scene, "CharacterScale").ScaleX = idle ? 1 : 1.03;
					Named<ScaleTransform>(scene, "CharacterScale").ScaleY = idle ? 1 : 1.03;
					Named<ScaleTransform>(scene, "CharacterBreathScale").ScaleX = idle ? 1 : 1.006;
					Named<ScaleTransform>(scene, "CharacterBreathScale").ScaleY = idle ? 1 : 1.012;
					Named<RotateTransform>(scene, "CharacterRotate").Angle = idle ? 0 : rightExtreme ? 0.32 : -0.32;
					scene.Measure(resolution);
					scene.Arrange(new Rect(new Point(), resolution));
					scene.UpdateLayout();
					Grid stage = Named<Grid>(scene, "CharacterStage");
					Thickness headroom = MainWindow.GetCharacterImageMargin(new Size(stage.ActualWidth, stage.ActualHeight),
						(double)character.PixelHeight / character.PixelWidth);
					figure.Margin = headroom;
					Named<Image>(scene, "CharacterGhostImage").Margin = headroom;
					scene.Measure(resolution);
					scene.Arrange(new Rect(new Point(), resolution));
					scene.UpdateLayout();
					Rect localClip = MainWindow.GetCharacterStageClip(new Size(stage.ActualWidth, stage.ActualHeight));
					stage.Clip = new RectangleGeometry(localClip);
					Grid characterSection = (Grid)stage.Parent;
					Border dialogue = characterSection.Children.OfType<Border>()
						.Single(item => Grid.GetColumn(item) == 0 && Grid.GetRow(item) == 1);
					Grid contentSection = (Grid)characterSection.Parent;
					Grid filePane = contentSection.Children.OfType<Grid>()
						.Single(item => Grid.GetColumn(item) == 2);
					Rect dialogueBounds = BoundsInScene(dialogue, scene);
					Rect filePaneBounds = BoundsInScene(filePane, scene);
					Rect clipBounds = stage.TransformToAncestor(scene).TransformBounds(localClip);
					GeneralTransform imageToStage = figure.TransformToAncestor(stage);
					GeneralTransform imageToScene = figure.TransformToAncestor(scene);
					double imageScale = Math.Min(figure.ActualWidth / character.PixelWidth, figure.ActualHeight / character.PixelHeight);
					double imageLeft = (figure.ActualWidth - character.PixelWidth * imageScale) / 2;
					double imageTop = (figure.ActualHeight - character.PixelHeight * imageScale) / 2;
					long visiblePixels = 0;
					long clippedPixels = 0;
					long dialogueOverlap = 0;
					long filePaneOverlap = 0;
					long clipTop = 0;
					long clipBottom = 0;
					long clipLeft = 0;
					long clipRight = 0;
					Rect visibleBounds = Rect.Empty;
					for (int y = 0; y < character.PixelHeight; y++)
					{
						for (int x = 0; x < character.PixelWidth; x++)
						{
							if (pixels[y * alphaStride + x * 4 + 3] < 12)
							{
								continue;
							}
							visiblePixels++;
							Point imagePoint = new Point(imageLeft + (x + 0.5) * imageScale, imageTop + (y + 0.5) * imageScale);
							Point scenePoint = imageToScene.Transform(imagePoint);
							visibleBounds.Union(scenePoint);
							Point stagePoint = imageToStage.Transform(imagePoint);
							bool insideClip = localClip.Contains(stagePoint);
							if (stagePoint.Y < localClip.Top) clipTop++;
							if (stagePoint.Y > localClip.Bottom) clipBottom++;
							if (stagePoint.X < localClip.Left) clipLeft++;
							if (stagePoint.X > localClip.Right) clipRight++;
							if (!insideClip || !new Rect(new Point(), resolution).Contains(scenePoint))
							{
								clippedPixels++;
							}
							if (insideClip && dialogueBounds.Contains(scenePoint))
							{
								dialogueOverlap++;
							}
							if (insideClip && filePaneBounds.Contains(scenePoint))
							{
								filePaneOverlap++;
							}
						}
					}
					string tag = $"{(int)resolution.Width}x{(int)resolution.Height}-{frame}";
					CharacterInteractionValidator.LayoutResult interaction = CharacterInteractionValidator.CheckLayout(
						scene, figure, stage, localClip, resolution, character, geometry);
					AddDiagnosticOverlay(scene, resolution, clipBounds, dialogueBounds, filePaneBounds, tag,
						clippedPixels, dialogueOverlap, filePaneOverlap);
					CharacterInteractionValidator.AddLayoutOverlay(scene, figure, resolution, character, interaction, geometry);
					SaveScene(scene, (int)resolution.Width, (int)resolution.Height,
						Path.Combine(outputDirectory, "character-layout-" + tag + ".png"));
					results.Add(new
					{
						Tag = tag,
						Headroom = Math.Round(headroom.Top, 3),
						Stage = RectSummary(BoundsInScene(stage, scene)),
						StageClip = RectSummary(clipBounds),
						CharacterVisibleBounds = RectSummary(visibleBounds),
						Dialogue = RectSummary(dialogueBounds),
						FilePane = RectSummary(filePaneBounds),
						VisibleSourcePixels = visiblePixels,
						ClippedSourcePixels = clippedPixels,
						ClipEdgePixels = new { Top = clipTop, Bottom = clipBottom, Left = clipLeft, Right = clipRight },
						DialogueOverlapPixels = dialogueOverlap,
						FilePaneOverlapPixels = filePaneOverlap,
						Interaction = interaction,
						RightTipGapToFilePane = Math.Round(filePaneBounds.Left - visibleBounds.Right, 3),
						LeftTipGapToDialogue = Math.Round(visibleBounds.Left - dialogueBounds.Right, 3)
					});
					// Tiny extreme-frame clipping is reported per edge, not treated as an idle-layout failure.
					failed |= idle && clippedPixels > 0 || dialogueOverlap > 0 || filePaneOverlap > 0 || !interaction.Passed;
				}
			}
			File.WriteAllText(Path.Combine(outputDirectory, "character-layout-result.json"),
				JsonSerializer.Serialize(new { Passed = !failed, AlphaVisibilityThreshold = 12, Frames = results },
					new JsonSerializerOptions { WriteIndented = true }));
			if (failed)
			{
				throw new InvalidOperationException("character layout regression detected clipping, protected-panel overlap, or click-region mismatch; inspect character-layout-result.json");
			}
		}
		finally
		{
			resources.ReleaseAllResources();
		}
	}

	private static Grid CreateLayoutOnlyScene(XDocument source)
	{
		XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
		XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
		XElement window = source.Root ?? throw new InvalidOperationException("layout source root is missing");
		XElement root = new XElement(window.Element(presentation + "Grid")
			?? throw new InvalidOperationException("layout source Grid is missing"));
		root.SetAttributeValue(XNamespace.Xmlns + "x", xaml.NamespaceName);
		XElement dictionary = new XElement(window.Descendants(presentation + "ResourceDictionary").First());
		root.AddFirst(new XElement(presentation + "Grid.Resources", dictionary));
		HashSet<string> eventAttributes = new HashSet<string>(StringComparer.Ordinal)
		{
			"Click", "GotKeyboardFocus", "LostKeyboardFocus", "KeyDown", "TextChanged", "MouseDoubleClick",
			"ValueChanged", "PreviewMouseLeftButtonDown", "PreviewMouseLeftButtonUp", "SizeChanged",
			"MouseMove", "PreviewMouseMove", "MouseLeave", "MouseEnter"
		};
		foreach (XElement element in root.DescendantsAndSelf())
		{
			foreach (XAttribute attribute in element.Attributes().ToArray())
			{
				if (eventAttributes.Contains(attribute.Name.LocalName) || attribute.Name == xaml + "Class"
					|| element.Name.LocalName == "Image" && attribute.Name.LocalName == "Source")
				{
					attribute.Remove();
				}
			}
		}
		// Parse only native layout controls, never the Window class or its constructor.
		return (Grid)XamlReader.Parse(root.ToString(SaveOptions.DisableFormatting));
	}

	private static T Named<T>(Grid scene, string name) where T : class
	{
		return scene.FindName(name) as T
			?? throw new InvalidOperationException("layout diagnostic element is missing: " + name);
	}

	private static Rect BoundsInScene(FrameworkElement element, Grid scene)
	{
		return element.TransformToAncestor(scene).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
	}

	private static object RectSummary(Rect rectangle)
	{
		return new { X = Math.Round(rectangle.X, 3), Y = Math.Round(rectangle.Y, 3), Width = Math.Round(rectangle.Width, 3), Height = Math.Round(rectangle.Height, 3) };
	}

	private static void AddDiagnosticOverlay(Grid scene, Size resolution, Rect clip, Rect dialogue, Rect filePane,
		string tag, long clippedPixels, long dialogueOverlap, long filePaneOverlap)
	{
		Canvas overlay = new Canvas { IsHitTestVisible = false };
		foreach ((Rect bounds, Color color) in new[]
		{
			(clip, Color.FromRgb(90, 240, 255)),
			(dialogue, Color.FromRgb(240, 200, 100)),
			(filePane, Color.FromRgb(150, 190, 230))
		})
		{
			System.Windows.Shapes.Rectangle rectangle = new System.Windows.Shapes.Rectangle
			{
				Width = bounds.Width,
				Height = bounds.Height,
				Stroke = new SolidColorBrush(color),
				StrokeThickness = 1,
				StrokeDashArray = new DoubleCollection { 4, 4 }
			};
			Canvas.SetLeft(rectangle, bounds.Left);
			Canvas.SetTop(rectangle, bounds.Top);
			overlay.Children.Add(rectangle);
		}
		Border caption = new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(230, 7, 10, 15)),
			Padding = new Thickness(12, 8, 12, 8),
			Child = new TextBlock
			{
				Text = $"OFFSCREEN LAYOUT QA // {tag}\nCLIPPED ALPHA PIXELS: {clippedPixels} | DIALOGUE OVERLAP: {dialogueOverlap} | FILE PANE OVERLAP: {filePaneOverlap}\nCyan: runtime clip | Gold: dialogue | Steel: file pane",
				Foreground = Brushes.White,
				FontFamily = new FontFamily("Consolas"),
				FontSize = 13
			}
		};
		Canvas.SetLeft(caption, 350);
		Canvas.SetTop(caption, resolution.Height - 100);
		overlay.Children.Add(caption);
		scene.Children.Add(overlay);
		scene.Measure(resolution);
		scene.Arrange(new Rect(new Point(), resolution));
		scene.UpdateLayout();
	}

	private static BitmapSource DecodeEmbeddedImage(ResourceManager resources, string filename)
	{
		using Stream resource = resources.GetStream("assets/" + filename, CultureInfo.InvariantCulture)
			?? throw InvalidAsset(filename, "embedded image resource is missing");
		BitmapFrame bitmap = BitmapFrame.Create(resource, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
		bitmap.Freeze();
		return bitmap;
	}

	private static Brush CreateAlphaCheckBackground()
	{
		DrawingGroup drawing = new DrawingGroup();
		drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(12, 18, 26)), null,
			new RectangleGeometry(new Rect(0, 0, 32, 32))));
		drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(20, 29, 40)), null,
			new RectangleGeometry(new Rect(0, 0, 16, 16))));
		drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(20, 29, 40)), null,
			new RectangleGeometry(new Rect(16, 16, 16, 16))));
		DrawingBrush brush = new DrawingBrush(drawing)
		{
			Viewport = new Rect(0, 0, 32, 32),
			ViewportUnits = BrushMappingMode.Absolute,
			TileMode = TileMode.Tile,
			Stretch = Stretch.None
		};
		brush.Freeze();
		return brush;
	}

	private static void RenderCharacterPreview(BitmapSource character, Brush background, string outputPath)
	{
		const int width = 1600;
		const int height = 1000;
		Grid scene = new Grid
		{
			Width = width,
			Height = height,
			Background = new SolidColorBrush(Color.FromRgb(7, 10, 15)),
			ClipToBounds = true
		};
		scene.Children.Add(new Border { Background = background });
		scene.Children.Add(new Image
		{
			Source = character,
			Height = 900,
			Width = 600,
			Stretch = Stretch.Uniform,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0, 0, 0, 36),
			SnapsToDevicePixels = true
		});
		scene.Measure(new Size(width, height));
		scene.Arrange(new Rect(0, 0, width, height));
		scene.UpdateLayout();
		SaveScene(scene, width, height, outputPath);
	}

	private static void SaveScene(Grid scene, int width, int height, string outputPath)
	{
		RenderTargetBitmap preview = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		preview.Render(scene);
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(preview));
		using FileStream output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
		encoder.Save(output);
	}

	private static bool HasPngSignature(byte[] bytes)
	{
		return bytes.Length >= 8
			&& bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71
			&& bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;
	}

	private static void ValidateTransparency(string filename, byte[] pixels, int width, int height, int stride)
	{
		int topRightAlpha = (width - 1) * 4 + 3;
		int bottomLeftAlpha = (height - 1) * stride + 3;
		int bottomRightAlpha = pixels.Length - 1;
		if (pixels[3] > 8 || pixels[topRightAlpha] > 8
			|| pixels[bottomLeftAlpha] > 8 || pixels[bottomRightAlpha] > 8)
		{
			throw InvalidAsset(filename, "character PNG corners are not transparent");
		}
		long transparentPixels = 0;
		long opaquePixels = 0;
		for (int offset = 3; offset < pixels.Length; offset += 4)
		{
			byte alpha = pixels[offset];
			if (alpha <= 8)
			{
				transparentPixels++;
			}
			// Imagegen cutouts can deliberately use soft opacity even inside the figure.
			if (alpha >= 230)
			{
				opaquePixels++;
			}
		}
		long pixelCount = (long)width * height;
		if (transparentPixels * 10 < pixelCount || opaquePixels * 10 < pixelCount)
		{
			throw InvalidAsset(filename, "character PNG must contain both a visible figure and transparent background");
		}
	}

	private static InvalidOperationException InvalidAsset(string filename, string reason)
	{
		return new InvalidOperationException(filename + ": " + reason);
	}
}
