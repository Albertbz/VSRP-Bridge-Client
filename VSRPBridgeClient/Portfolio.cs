using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace VSRPBridgeClient;

/// <summary>
/// A design as chiseltools' pantograph saves it in the portfolio: an XML PantographData. Only the fields used
/// here, so the other ones are skipped when reading.
/// </summary>
[XmlRoot("PantographData")]
public class PortfolioFile
{
    public string? name;
    // Protobuf List&lt;uint&gt; of the chiseled block's cuboids, see BlockEntityMicroBlock.ToUint
    public byte[]? voxeldata;
    // The block code of each material index
    public string[]? blockcodes;
}

/// <summary>
/// The designs of the player's pantograph portfolio (chiseltools), as baby shapes that keep their own materials.
/// The portfolio is a folder of files on the player's own computer, so only the client can read it, and it sends
/// the chosen design along with the baby choice.
/// </summary>
public static class Portfolio
{
    public const string ShapeCodePrefix = "portfolio:";
    // The folder chiseltools saves the portfolio in, inside the game's data folder
    private const string FolderName = "chiselportfolio";
    private const int MaxFiles = 200;
    private const long MaxFileBytes = 1024 * 1024;
    // A 16x16x16 block can't have more cuboids than voxels
    public const int MaxCuboids = 16 * 16 * 16;
    public const int MaxMaterials = 32;

    public static bool IsPortfolioShape(BabyShape shape) => shape.Code.StartsWith(ShapeCodePrefix, StringComparison.Ordinal);

    /// <summary>
    /// The portfolio's designs that can be built, sorted by name. Designs saved as .vox have no block codes, and
    /// designs whose blocks don't all exist on this server are left out. Never throws.
    /// </summary>
    public static List<BabyShape> Load(ICoreClientAPI capi)
    {
        var shapes = new List<BabyShape>();
        string folder;
        try
        {
            folder = Path.Combine(GamePaths.DataPath, FolderName);
            if (!Directory.Exists(folder)) return shapes;
        }
        catch { return shapes; }

        var serializer = new XmlSerializer(typeof(PortfolioFile));
        IEnumerable<string> files;
        try { files = Directory.GetFiles(folder).Where(f => !f.EndsWith(".vox", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList(); }
        catch { return shapes; }

        foreach (string file in files)
        {
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes) continue;
                PortfolioFile? design;
                using (var reader = new StreamReader(file)) design = serializer.Deserialize(reader) as PortfolioFile;
                var shape = ToShape(capi.World, Path.GetFileName(file), design);
                if (shape != null) shapes.Add(shape);
            }
            catch (Exception ex)
            {
                capi.Logger.Debug("[VSRPBridgeClient] Skipped the portfolio design {0}: {1}", file, ex.Message);
            }
        }
        return shapes;
    }

    private static BabyShape? ToShape(IWorldAccessor world, string fileName, PortfolioFile? design)
    {
        if (design?.voxeldata == null || design.blockcodes == null) return null;
        var cuboids = SerializerUtil.Deserialize<List<uint>>(design.voxeldata);
        var shape = new BabyShape
        {
            Code = ShapeCodePrefix + fileName,
            Label = Lang.Get("vsrpbridgeclient:baby-portfolio-shape", fileName),
            Cuboids = cuboids?.Select(c => (int)c).ToArray() ?? Array.Empty<int>(),
            SlotByMaterialIndex = Array.Empty<string>(),
            FallbackMaterials = design.blockcodes
        };
        return Validate(world, shape) == null ? shape : null;
    }

    /// <summary>
    /// Why the design can't be a baby, or null when it can. Used by the server too, as the design comes from the
    /// player.
    /// </summary>
    public static string? Validate(IWorldAccessor world, BabyShape shape)
    {
        if (!IsPortfolioShape(shape)) return "not a portfolio design";
        if (shape.Cuboids.Length == 0) return "the design is empty";
        if (shape.Cuboids.Length > MaxCuboids) return "the design has too many parts";
        if (shape.FallbackMaterials.Length == 0 || shape.FallbackMaterials.Length > MaxMaterials) return "the design has too many materials";
        foreach (string code in shape.FallbackMaterials)
        {
            if (string.IsNullOrEmpty(code)) return "the design has an empty material";
            Block? block = world.GetBlock(new AssetLocation(code));
            if (block == null || block.Id == 0) return "the block " + code + " doesn't exist";
        }
        foreach (int cuboid in shape.Cuboids)
        {
            // The material index is the top byte, see BlockEntityMicroBlock.ToUint
            int material = (int)((uint)cuboid >> 24);
            if (material >= shape.FallbackMaterials.Length) return "a part uses a material the design doesn't have";
        }
        return null;
    }
}
