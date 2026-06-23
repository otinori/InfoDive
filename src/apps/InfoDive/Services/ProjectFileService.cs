using System.IO;
using System.IO.Compression;
using System.Text.Json;
using InfoDive.Models;

namespace InfoDive.Services;

public static class ProjectFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private const string CurrentVersion = "2.0";

    /// <summary>Represents a single image and its raw bytes, used during save/load.</summary>
    public record ImageRef(string Id, string FileName, int Width, int Height, byte[] Bytes);

    public static void Save(string prZipPath, ScenarioData scenario, string imageFilePath)
    {
        var bytes = File.ReadAllBytes(imageFilePath);
        var fileName = Path.GetFileName(imageFilePath);
        var image = new ImageRef("img1", fileName, scenario.Canvas.ImageWidth, scenario.Canvas.ImageHeight, bytes);
        SaveMulti(prZipPath, scenario, [image]);
    }

    public static void SaveWithImageBytes(string prZipPath, ScenarioData scenario, string imageFileName, byte[] imageBytes)
    {
        var image = new ImageRef("img1", imageFileName, scenario.Canvas.ImageWidth, scenario.Canvas.ImageHeight, imageBytes);
        SaveMulti(prZipPath, scenario, [image]);
    }

    /// <summary>
    /// Write a project with one or more images. Produces a v2.0 manifest with the Images list
    /// populated, and also fills the legacy ImageFileName field (first image) for back-compat readers.
    /// </summary>
    public static void SaveMulti(string prZipPath, ScenarioData scenario, IReadOnlyList<ImageRef> images)
    {
        if (images.Count == 0)
            throw new ArgumentException("At least one image is required", nameof(images));

        var manifest = new ManifestData
        {
            Version = CurrentVersion,
            ImageFileName = images[0].FileName, // legacy field
            Images = images.Select(i => new ImageEntry
            {
                Id = i.Id,
                FileName = i.FileName,
                Width = i.Width,
                Height = i.Height,
            }).ToList(),
        };

        scenario.UpdatedAt = DateTime.UtcNow;

        using var fs = new FileStream(prZipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteJsonEntry(zip, "manifest.json", manifest);
        WriteJsonEntry(zip, "scenario.json", scenario);

        foreach (var img in images)
        {
            var entry = zip.CreateEntry(img.FileName);
            using var s = entry.Open();
            s.Write(img.Bytes, 0, img.Bytes.Length);
        }
    }

    /// <summary>
    /// Load a project. Returns the first image for back-compat with the existing single-image UI;
    /// use <see cref="LoadMulti"/> to access all images.
    /// </summary>
    public static (ScenarioData Scenario, string ImageFileName, byte[] ImageBytes) Load(string prZipPath)
    {
        var (scenario, images) = LoadMulti(prZipPath);
        var first = images[0];
        return (scenario, first.FileName, first.Bytes);
    }

    /// <summary>
    /// Load a project with all images. Handles both v2.0 (Images list) and legacy v1.0 (single ImageFileName) formats.
    /// </summary>
    public static (ScenarioData Scenario, List<ImageRef> Images) LoadMulti(string prZipPath)
    {
        using var fs = new FileStream(prZipPath, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var manifest = ReadJsonEntry<ManifestData>(zip, "manifest.json")
            ?? throw new InvalidDataException("Invalid manifest.json");
        var scenario = ReadJsonEntry<ScenarioData>(zip, "scenario.json")
            ?? throw new InvalidDataException("Invalid scenario.json");

        // v2.0+: use Images list. Legacy v1.0: synthesize a single entry from ImageFileName.
        var imageList = manifest.Images.Count > 0
            ? manifest.Images
            : [new ImageEntry { Id = "img1", FileName = manifest.ImageFileName }];

        var images = new List<ImageRef>(imageList.Count);
        foreach (var entry in imageList)
        {
            var zipEntry = zip.GetEntry(entry.FileName)
                ?? throw new InvalidDataException($"Image '{entry.FileName}' not found in .przip");
            using var s = zipEntry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            images.Add(new ImageRef(
                Id: string.IsNullOrEmpty(entry.Id) ? "img1" : entry.Id,
                FileName: entry.FileName,
                Width: entry.Width,
                Height: entry.Height,
                Bytes: ms.ToArray()));
        }

        if (images.Count == 0)
            throw new InvalidDataException("No images found in .przip file");

        return (scenario, images);
    }

    private static void WriteJsonEntry<T>(ZipArchive zip, string name, T data)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(data, JsonOptions));
    }

    private static T? ReadJsonEntry<T>(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name)
            ?? throw new InvalidDataException($"{name} not found in .przip file");
        using var reader = new StreamReader(entry.Open());
        return JsonSerializer.Deserialize<T>(reader.ReadToEnd());
    }
}
