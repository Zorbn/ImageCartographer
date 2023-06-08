using System.Diagnostics;
using System.Runtime.CompilerServices;

const string imageExtension = ".png";
const string outputNameWithoutExtension = "atlas";
const string outputName = $"{outputNameWithoutExtension}{imageExtension}";
const string outputInfoName = $"{outputNameWithoutExtension}Info.txt";
const char infoSeparator = ';';
const int padding = 1;

[MethodImpl(MethodImplOptions.AggressiveInlining)]
int PadSize(int size)
{
    return size + padding * 2;
}

(Image<Rgba32>[], string[]) LoadImages(string path)
{
    var filePaths = Directory.GetFiles(path);
    var images = new Image<Rgba32>[filePaths.Length];
    var imageNames = new string[filePaths.Length];
    var imageI = 0;

    foreach (var filePath in filePaths)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (fileName == outputNameWithoutExtension || Path.GetExtension(filePath) != imageExtension) continue;

        images[imageI] = Image.Load<Rgba32>(filePath);
        imageNames[imageI] = fileName;
        ++imageI;
    }

    Array.Resize(ref images, imageI);
    Array.Resize(ref imageNames, imageI);

    return (images, imageNames);
}

void SortImages(Image<Rgba32>[] images, string[] imageNames)
{
    var comparer = Comparer<Image<Rgba32>>.Create((a, b) => b.Height.CompareTo(a.Height));
    Array.Sort(images, imageNames, comparer);
}

void FindDestinationsFromStart(Point start, int maxX, Image<Rgba32>[] images, Dictionary<int, Point> destinations,
    ref Point dimensions)
{
    if (destinations.Count == images.Length) return;

    var point = start;

    for (var i = 0; i < images.Length; i++)
    {
        if (destinations.ContainsKey(i)) continue;

        var image = images[i];
        var imagePaddedWidth = PadSize(image.Width);
        var imagePaddedHeight = PadSize(image.Height);

        if (point.Y + imagePaddedHeight > dimensions.Y) continue;
        if (point.X + imagePaddedWidth > maxX) continue;

        // Add the destination for this image, expanding the dimensions if necessary.
        destinations.Add(i, point);
        var belowStart = new Point(point.X, point.Y + imagePaddedHeight);
        point.X += imagePaddedWidth;
        dimensions.X = Math.Max(dimensions.X, point.X);

        // Recurse to add any destinations that may fit in the space below this image.
        if (belowStart.Y >= dimensions.Y) continue;

        FindDestinationsFromStart(belowStart, point.X, images, destinations, ref dimensions);
    }
}

int CeilToPowerOfTwo(int x)
{
    var ceil = 1;

    while (ceil < x)
    {
        ceil *= 2;
    }

    return ceil;
}

(Dictionary<int, Point>, Point) FindDestinations(Image<Rgba32>[] images)
{
    var destinations = new Dictionary<int, Point>();
    var dimensions = Point.Empty;

    if (images.Length == 0) return (destinations, dimensions);

    dimensions.Y = CeilToPowerOfTwo(images[0].Height);

    FindDestinationsFromStart(Point.Empty, int.MaxValue, images, destinations, ref dimensions);

    dimensions.X = CeilToPowerOfTwo(dimensions.X);

    return (destinations, dimensions);
}

void CopyImageHorizontalPaddingToAtlas(int paddedDestinationX, Image<Rgba32> image, Span<Rgba32> imageRow, Image<Rgba32> atlas, int atlasY)
{
    // Add left/right padding:
    for (var paddingX = -padding; paddingX < 0; paddingX++)
    {
        var atlasX = paddingX + paddedDestinationX;
        atlas[atlasX, atlasY] = imageRow[0];
    }

    for (var paddingX = 0; paddingX < padding; paddingX++)
    {
        var atlasX = paddingX + paddedDestinationX + image.Width;
        atlas[atlasX, atlasY] = imageRow[image.Width - 1];
    }
}

void CopyImagePaddedRowToAtlas(Image<Rgba32> image, PixelAccessor<Rgba32> atlasAccessor, Span<Rgba32> sourceRow, int y, int paddedDestinationX)
{
    var destinationRow = atlasAccessor.GetRowSpan(y);

    for (var x = paddedDestinationX - padding; x < paddedDestinationX + image.Width + padding; x++)
    {
        destinationRow[x] = sourceRow[x];
    }
}

void CopyImageVerticalPaddingToAtlas(Image<Rgba32> image, Image<Rgba32> atlas, Point destination)
{
    var paddedDestinationX = destination.X + padding;
    var paddedDestinationY = destination.Y + padding;

    atlas.ProcessPixelRows(atlasAccessor =>
    {
        var sourceRow = atlasAccessor.GetRowSpan(paddedDestinationY);

        for (var y = destination.Y; y < paddedDestinationY; y++)
        {
            CopyImagePaddedRowToAtlas(image, atlasAccessor, sourceRow, y, paddedDestinationX);
        }

        var destinationBottom = paddedDestinationY + image.Height;
        sourceRow = atlasAccessor.GetRowSpan(destinationBottom - 1);

        for (var y = destinationBottom; y < destinationBottom + padding; y++)
        {
            CopyImagePaddedRowToAtlas(image, atlasAccessor, sourceRow, y, paddedDestinationX);
        }
    });
}

void CopyImageToAtlas(Image<Rgba32> image, Image<Rgba32> atlas, Point destination)
{
    var paddedDestinationX = destination.X + padding;
    var paddedDestinationY = destination.Y + padding;

    // Copy image:
    image.ProcessPixelRows(imageAccessor =>
    {
        for (var imageY = 0; imageY < imageAccessor.Height; imageY++)
        {
            var imageRow = imageAccessor.GetRowSpan(imageY);
            var atlasY = imageY + paddedDestinationY;

            for (var imageX = 0; imageX < imageRow.Length; imageX++)
            {
                var atlasX = imageX + paddedDestinationX;
                atlas[atlasX, atlasY] = imageRow[imageX];
            }

            if (imageRow.Length < 1) continue;

            CopyImageHorizontalPaddingToAtlas(paddedDestinationX, image, imageRow, atlas, atlasY);
        }
    });

    // Add top/bottom padding:
    CopyImageVerticalPaddingToAtlas(image, atlas, destination);
}

Image GenerateAtlas(Image<Rgba32>[] images, Dictionary<int, Point> destinations, Point dimensions)
{
    var atlas = new Image<Rgba32>(dimensions.X, dimensions.Y);
    foreach (var (imageI, destination) in destinations)
    {
        var image = images[imageI];

        CopyImageToAtlas(image, atlas, destination);
    }

    return atlas;
}

void GenerateAtlasInfo(string path, Image<Rgba32>[] images, string[] imageNames, Dictionary<int, Point> destinations)
{
    using var fileStream = File.Open(path, FileMode.Create);
    using var streamWriter = new StreamWriter(fileStream);

    foreach (var (imageI, destination) in destinations)
    {
        var image = images[imageI];
        var imageName = imageNames[imageI];
        var imageX = destination.X + padding;
        var imageY = destination.Y + padding;
        streamWriter.WriteLine($"{imageName}{infoSeparator}{imageX}{infoSeparator}" +
                               $"{imageY}{infoSeparator}{image.Width}{infoSeparator}{image.Height}");
    }
}

void Main()
{
    Console.WriteLine("Starting to draw...");

    var directory = args.Length < 1 ? Directory.GetCurrentDirectory() : args[0];
    var (images, imageNames) = LoadImages(directory);

    if (images.Length < 1)
    {
        Console.WriteLine("This directory contains no images!");
        return;
    }

    var stopwatch = new Stopwatch();
    stopwatch.Start();

    SortImages(images, imageNames);
    var (destinations, dimensions) = FindDestinations(images);
    var atlas = GenerateAtlas(images, destinations, dimensions);

    stopwatch.Stop();

    atlas.Save(Path.Join(directory, outputName));

    GenerateAtlasInfo(Path.Join(directory, outputInfoName), images, imageNames, destinations);

    Console.WriteLine($"Created an atlas with the dimensions {dimensions.X}x{dimensions.Y} out of {images.Length} images in {stopwatch.ElapsedMilliseconds}ms!\n" +
                      $"Generated {outputName}, and {outputInfoName} with the format \"name{infoSeparator}x{infoSeparator}y{infoSeparator}width{infoSeparator}height\".");
}

Main();