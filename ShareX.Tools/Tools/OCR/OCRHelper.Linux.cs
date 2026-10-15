#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

#if LINUX

using ShareX.HelpersLib;
using System.Diagnostics;
using System.Globalization;
using Bitmap = SkiaSharp.SKBitmap;
using ImageFormat = SkiaSharp.SKEncodedImageFormat;

namespace ShareX.Tools;

// linux-port: OCR backed by the Tesseract command line tool instead of Windows.Media.Ocr.
public static class OCRHelper
{
    public static bool IsSupported => LinuxHelpers.HasExecutable("tesseract");

    public static OCRLanguageOption[] AvailableLanguages
    {
        get
        {
            ThrowIfNotSupported();
            var (exitCode, output, _) = LinuxHelpers.Run("tesseract", ["--list-langs"]);
            if (exitCode != 0) return [new OCRLanguageOption("English", "eng")];

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !x.StartsWith("List of", StringComparison.OrdinalIgnoreCase) && x != "osd")
                .Select(x => new OCRLanguageOption(GetDisplayName(x), x))
                .ToArray();
        }
    }

    private static string GetDisplayName(string tesseractCode)
    {
        try
        {
            CultureInfo culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(c => c.ThreeLetterISOLanguageName == tesseractCode.Split('_')[0]);
            if (culture != null) return culture.EnglishName + (tesseractCode.Contains('_') ? $" ({tesseractCode})" : "");
        }
        catch
        {
        }
        return tesseractCode;
    }

    public static void ThrowIfNotSupported()
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException("OCR requires Tesseract. Install it with: sudo pacman -S tesseract tesseract-data-eng");
        }
    }

    /// <summary>Maps BCP-47 tags used by the Windows implementation (e.g. "en-US") to Tesseract codes ("eng").</summary>
    private static string ToTesseractLanguage(string languageTag)
    {
        if (string.IsNullOrEmpty(languageTag)) return "eng";
        string[] installed = AvailableLanguages.Select(x => x.LanguageTag).ToArray();
        if (installed.Contains(languageTag)) return languageTag;

        try
        {
            string threeLetter = new CultureInfo(languageTag).ThreeLetterISOLanguageName;
            if (installed.Contains(threeLetter)) return threeLetter;
            string match = installed.FirstOrDefault(x => x.StartsWith(threeLetter + "_", StringComparison.Ordinal));
            if (match != null) return match;
        }
        catch
        {
        }
        return installed.Contains("eng") ? "eng" : installed.FirstOrDefault() ?? "eng";
    }

    public static async Task<string> OCR(Bitmap bitmap, string languageTag = "en", float scaleFactor = 1f,
        bool singleLine = false)
    {
        ThrowIfNotSupported();
        scaleFactor = Math.Max(scaleFactor, 1f);

        return await Task.Run(() =>
        {
            using Bitmap scaledBitmap = SkiaImageHelpers.ScaleImageFast(bitmap, scaleFactor);
            string tempFile = Path.Combine(Path.GetTempPath(), $"sharex-ocr-{Guid.NewGuid():N}.png");
            try
            {
                using (FileStream fs = File.Create(tempFile))
                {
                    scaledBitmap.Encode(fs, ImageFormat.Png, 100);
                }

                string language = ToTesseractLanguage(languageTag);
                var (exitCode, output, error) = LinuxHelpers.Run("tesseract", [tempFile, "stdout", "-l", language], timeoutMs: 120000);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException("Tesseract failed: " + error.Trim());
                }

                IEnumerable<string> lines = output.Replace("\f", "").Split('\n')
                    .Select(x => x.TrimEnd())
                    .Where(x => x.Length > 0);
                return string.Join(singleLine ? " " : Environment.NewLine, lines);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        });
    }
}

#endif
