using System;
using System.Windows;
using System.Windows.Media;

namespace Spark
{
	public static class ThemesController
	{
		public enum ThemeTypes
		{
			YellowDark,
			OrangeDark,
			RedDark
		}

		public static ThemeTypes CurrentTheme { get; set; }

		// -----------------------------------------------------------------------
		// Runtime colour injection
		// -----------------------------------------------------------------------

		/// <summary>
		/// Applies a custom 3-colour theme by mutating all SolidColorBrush
		/// resources in Application.Current.Resources directly.
		/// dark  → backgrounds, window fill, selected states
		/// mid   → buttons, default borders, interactive controls
		/// light → hover highlights, carets, bright borders
		/// </summary>
		public static void ApplyCustomTheme(Color dark, Color mid, Color light)
		{
			// Derive a slightly darker variant for the window title bar
			Color dark2 = Darken(dark, 0.12f);

			// Semi-transparent container (preserves slight transparency like the original #f5bb2961)
			Color containerColor = Color.FromArgb(0xF5, dark.R, dark.G, dark.B);

			// A "disabled" mid — slightly desaturated/darker
			Color midDark = Darken(mid, 0.15f);

			SetBrush("BackgroundColour",                          dark);
			SetBrush("WindowBorderColour",                        dark2);
			SetBrush("WindowTitleColour",                         dark2);
			SetBrush("ContainerBackground",                       containerColor);
			SetBrush("ContainerBorder",                           containerColor);

			SetBrush("ControlDarkerBackground",                   dark);
			SetBrush("ControlDarkerBorderBrush",                  dark);
			SetBrush("ControlDefaultBackground",                  mid);
			SetBrush("ControlDefaultBorderBrush",                 mid);
			SetBrush("ControlBrightDefaultBackground",            light);
			SetBrush("ControlBrightDefaultBorderBrush",           light);
			SetBrush("ControlDisabledBackground",                 midDark);
			SetBrush("ControlDisabledBorderBrush",                midDark);
			SetBrush("ControlMouseOverBackground",                light);
			SetBrush("ControlMouseOverBorderBrush",               mid);
			SetBrush("ControlSelectedBackground",                 dark);
			SetBrush("ControlSelectedBorderBrush",                dark);
			SetBrush("ControlSelectedMouseOverBackground",        mid);
			SetBrush("ControlSelectedMouseOverBorderBrush",       light);

			// Primary palette (from the colour variant .xaml files)
			SetBrush("ControlPrimaryDarkerBackground",            dark);
			SetBrush("ControlPrimaryDarkerBorderBrush",           dark);
			SetBrush("ControlPrimaryDefaultBackground",           mid);
			SetBrush("ControlPrimaryDefaultBorderBrush",          mid);
			SetBrush("ControlPrimaryBrightDefaultBackground",     light);
			SetBrush("ControlPrimaryBrightDefaultBorderBrush",    light);
			SetBrush("ControlPrimaryDisabledBackground",          midDark);
			SetBrush("ControlPrimaryDisabledBorderBrush",         midDark);
			SetBrush("ControlPrimaryMouseOverBackground",         light);
			SetBrush("ControlPrimaryMouseOverBorderBrush",        mid);
			SetBrush("ControlPrimarySelectedBackground",          dark);
			SetBrush("ControlPrimarySelectedBorderBrush",         dark);
			SetBrush("ControlPrimarySelectedMouseOverBackground", mid);
			SetBrush("ControlPrimarySelectedMouseOverBorderBrush",light);
			SetBrush("ControlPrimaryCaretSelectionBackground",    mid);
			SetBrush("ControlPrimaryCaretBackground",             light);
			SetBrush("ControlPrimaryGlythColour",                 light);
			SetBrush("ControlPrimaryMouseOverGlythColour",        mid);
			SetBrush("ControlPrimarySelectedGlythColour",         light);
			SetBrush("ControlPrimarySelectedMouseOverGlythColour",light);
			SetBrush("ControlPrimaryDisabledGlythColour",         midDark);

			// Alternating Row Backgrounds (Dynamic Greys)
			SetBrush("ControlRowBackground1",                     dark);
			SetBrush("ControlRowBackground2",                     Darken(dark, 0.05f));
			SetBrush("ControlRowBorder1",                         midDark);
			SetBrush("ControlRowBorder2",                         midDark);
		}

		/// <summary>
		/// Applies the custom theme stored in SparkSettings.
		/// ALWAYS loads the base XAML first (needed for all WPF control templates),
		/// then overrides colour resources on top.
		/// Safe to call before SparkSettings is loaded (will use defaults).
		/// </summary>
		public static void ApplyFromSettings()
		{
			try
			{
				// Step 1: Load the base theme XAML so all WPF control templates are present.
				// This is what the original ThemesController.SetTheme() did.
				ChangeTheme(new Uri("/Themes/ColourfulDarkTheme_Orange.xaml", UriKind.Relative));
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"ThemesController: {e}");
			}

			try
			{
				// Step 2: Override colour resources with user's saved colours.
				Color dark  = ParseHex(SparkSettings.instance?.customThemeDark  ?? "#c32b61");
				Color mid   = ParseHex(SparkSettings.instance?.customThemeMid   ?? "#ea6192");
				Color light = ParseHex(SparkSettings.instance?.customThemeLight ?? "#ffaac9");
				ApplyCustomTheme(dark, mid, light);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"ThemesController colour apply: {e}");
			}
		}

		/// <summary>
		/// Saves the 3 colours to settings and immediately applies them.
		/// </summary>
		public static void SaveAndApply(Color dark, Color mid, Color light)
		{
			if (SparkSettings.instance != null)
			{
				SparkSettings.instance.customThemeDark  = ColorToHex(dark);
				SparkSettings.instance.customThemeMid   = ColorToHex(mid);
				SparkSettings.instance.customThemeLight = ColorToHex(light);
				SparkSettings.instance.Save();
			}
			ApplyCustomTheme(dark, mid, light);
		}

		// -----------------------------------------------------------------------
		// Legacy file-swap API (no longer needed but kept for compat)
		// -----------------------------------------------------------------------

		public static void SetTheme(ThemeTypes theme)
		{
			CurrentTheme = theme;
			// Legacy themes all mapped to the same pink palette — apply custom theme from settings instead
			ApplyFromSettings();
		}

		private static ResourceDictionary ThemeDictionary
		{
			get => Application.Current.Resources.MergedDictionaries[0];
			set => Application.Current.Resources.MergedDictionaries[0] = value;
		}

		private static void ChangeTheme(Uri uri)
		{
			ThemeDictionary = new ResourceDictionary { Source = uri };
		}

		// -----------------------------------------------------------------------
		// Helpers
		// -----------------------------------------------------------------------

		private static void SetBrush(string key, Color color)
		{
			try
			{
				// Now that ColourfulDarkTheme_base.xaml uses {DynamicResource} for all colour brushes,
				// setting the key in Application.Current.Resources (top-level direct dict) is enough:
				// WPF's DynamicResource lookup checks the app-level direct dict BEFORE merged dicts,
				// so this immediately overrides the base.xaml values across the entire visual tree.
				Application.Current.Resources[key] = new SolidColorBrush(color);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"ThemesController SetBrush '{key}': {e}");
			}
		}

		public static Color ParseHex(string hex)
		{
			hex = hex.TrimStart('#');
			if (hex.Length == 6)
				hex = "FF" + hex;
			byte a = Convert.ToByte(hex.Substring(0, 2), 16);
			byte r = Convert.ToByte(hex.Substring(2, 2), 16);
			byte g = Convert.ToByte(hex.Substring(4, 2), 16);
			byte b = Convert.ToByte(hex.Substring(6, 2), 16);
			return Color.FromArgb(a, r, g, b);
		}

		public static string ColorToHex(Color c)
		{
			return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
		}

		private static Color Darken(Color c, float amount)
		{
			return Color.FromArgb(c.A,
				(byte)Math.Max(0, c.R - (int)(255 * amount)),
				(byte)Math.Max(0, c.G - (int)(255 * amount)),
				(byte)Math.Max(0, c.B - (int)(255 * amount)));
		}
	}
}