using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace InfoDive.Services;

/// <summary>
/// Imports draw.io / diagrams.net files into the app's PNG pipeline.
///
/// Two paths, picked automatically by inspecting the file contents:
///   B) "Editable PNG/SVG" exports (a real PNG/SVG with the diagram XML embedded in
///      metadata) are handled with no external dependency — PNG is passed through and
///      SVG goes through <see cref="SvgRasterizer"/>. Note that the common
///      foo.drawio.svg / foo.drawio.png names already enter via the .svg/.png paths;
///      this only covers the case where such content is saved under a .drawio/.dio name.
///   C) Native mxGraph XML (the default "Save") can only be rendered by the draw.io
///      engine, so we shell out to draw.io Desktop's CLI, export to a multi-page PDF,
///      then reuse <see cref="PdfRasterizer"/> to turn each page into an image.
/// </summary>
public static class DrawioRasterizer
{
    /// <summary>Override the auto-detected draw.io Desktop executable path.</summary>
    private const string ExePathEnvVar = "DRAWIO_PATH";

    public static bool IsDrawio(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return string.Equals(ext, ".drawio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".dio", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns one (PNG bytes, filename) entry per diagram page.
    /// Filenames mirror the PDF importer: foo.drawio → foo_p01.png, foo_p02.png, ...
    /// Throws <see cref="InvalidOperationException"/> with a user-facing message when a
    /// native file is given but draw.io Desktop is not installed.
    /// </summary>
    public static List<(byte[] PngBytes, string FileName)> Rasterize(byte[] bytes, string fileName)
    {
        // B: an editable export saved under a .drawio/.dio name needs no engine.
        if (LooksLikePng(bytes))
            return [(bytes, Path.GetFileNameWithoutExtension(fileName) + ".png")];
        if (LooksLikeSvg(bytes))
            return [(SvgRasterizer.Rasterize(bytes), Path.GetFileNameWithoutExtension(fileName) + ".png")];

        // C: native mxGraph XML → render with the draw.io Desktop engine.
        var exe = FindDrawioExe();
        if (exe == null)
            throw new InvalidOperationException(
                "draw.io Desktop が見つかりませんでした。\n\n" +
                "・https://github.com/jgraph/drawio-desktop/releases からインストールする\n" +
                "  （または環境変数 DRAWIO_PATH に draw.io.exe のパスを設定する）\n" +
                "・もしくは draw.io 側で PNG / SVG / PDF にエクスポートしてから取り込む\n\n" +
                "のいずれかをお試しください。");

        return ExportViaCli(exe, bytes, fileName);
    }

    // ── content sniffing (path B) ──

    private static bool LooksLikePng(byte[] b)
        => b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
           && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    private static bool LooksLikeSvg(byte[] b)
    {
        // Scan the first chunk for an <svg root element. Native .drawio files start with
        // <mxfile>/<mxGraphModel>, so this only matches true SVG content.
        var head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 4096));
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    // ── CLI export (path C) ──

    private static List<(byte[] PngBytes, string FileName)> ExportViaCli(string exe, byte[] bytes, string fileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "InfoDive_drawio_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var inPath = Path.Combine(tempDir, "input.drawio");
        var outPath = Path.Combine(tempDir, "output.pdf");

        try
        {
            File.WriteAllBytes(inPath, bytes);

            // Export every page to a single PDF; PdfRasterizer then splits it into images.
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("--export");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("pdf");
            psi.ArgumentList.Add("--all-pages");
            psi.ArgumentList.Add("--crop");
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(outPath);
            psi.ArgumentList.Add(inPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("draw.io の起動に失敗しました。");

            // Drain stdout/stderr asynchronously so a full pipe buffer can't deadlock the
            // process, and so WaitForExit's timeout actually governs a hung export.
            var stderr = new StringBuilder();
            proc.ErrorDataReceived += (_, ev) => { if (ev.Data != null) stderr.AppendLine(ev.Data); };
            proc.OutputDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new InvalidOperationException("draw.io のエクスポートがタイムアウトしました（60秒）。");
            }

            if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
            {
                var err = stderr.ToString().Trim();
                throw new InvalidOperationException(
                    "draw.io のエクスポート結果が空でした。draw.io がすでに起動している場合は終了してから再試行してください。" +
                    (string.IsNullOrWhiteSpace(err) ? "" : $"\n{err}"));
            }

            var pdfBytes = File.ReadAllBytes(outPath);
            return PdfRasterizer.Rasterize(pdfBytes, fileName);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string? FindDrawioExe()
    {
        var env = Environment.GetEnvironmentVariable(ExePathEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // draw.io Desktop ships as "draw.io.exe"; some builds use "drawio.exe".
        var candidates = new[]
        {
            Path.Combine(local, "Programs", "draw.io", "draw.io.exe"),
            Path.Combine(local, "Programs", "drawio", "drawio.exe"),
            Path.Combine(pf, "draw.io", "draw.io.exe"),
            Path.Combine(pf, "drawio", "drawio.exe"),
            Path.Combine(pf86, "draw.io", "draw.io.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
