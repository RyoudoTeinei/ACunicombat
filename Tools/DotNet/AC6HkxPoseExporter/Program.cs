using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulsAssetPipeline.Animation;

try
{
var dsasDir = GetDsasDir();
AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
{
    var name = new AssemblyName(args.Name).Name + ".dll";
    var candidate = Path.Combine(dsasDir, name);
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

var options = CliOptions.Parse(args);
if (options == null)
{
    CliOptions.PrintUsage();
    return 2;
}

var skeletonCompendiumBytes = options.SkeletonCompendiumPath != null
    ? File.ReadAllBytes(options.SkeletonCompendiumPath)
    : null;
var animationCompendiumBytes = options.AnimationCompendiumPath != null
    ? File.ReadAllBytes(options.AnimationCompendiumPath)
    : null;
var skeletonHkx = HKX.GenFakeFromTagFile(File.ReadAllBytes(options.SkeletonPath), skeletonCompendiumBytes);

if (options.DumpObjects)
{
    DumpObjects(skeletonHkx);
    return 0;
}

var skeleton = FindObject<HKX.HKASkeleton>(skeletonHkx)
    ?? throw new InvalidOperationException($"No hkaSkeleton found in '{options.SkeletonPath}'.");

if (options.SkeletonOnly)
{
    var skeletonExport = new PoseExport
    {
        SourceSkeleton = options.SkeletonPath,
        SourceSkeletonCompendium = options.SkeletonCompendiumPath,
        AnimationName = "SkeletonOnly",
        Duration = 0,
        FrameDuration = 0,
        SourceFrameCount = 0,
        ExportedFrameCount = 0,
        Bones = ReadBones(skeleton),
        Frames = new List<PoseFrame>(),
    };

    WriteExport(options.OutputPath, skeletonExport, options.Pretty);
    Console.WriteLine($"Exported skeleton-only, {skeletonExport.Bones.Count} bones");
    Console.WriteLine(options.OutputPath);
    return 0;
}

var animationHkx = HKX.GenFakeFromTagFile(File.ReadAllBytes(options.AnimationPath!), animationCompendiumBytes);
var binding = FindObject<HKX.HKAAnimationBinding>(animationHkx);
var refFrame = FindObject<HKX.HKADefaultAnimatedReferenceFrame>(animationHkx);
var spline = FindObject<HKX.HKASplineCompressedAnimation>(animationHkx);
var interleaved = FindObject<HKX.HKAInterleavedUncompressedAnimation>(animationHkx);

HavokAnimationData animationData = spline != null
    ? new HavokAnimationData_SplineCompressed(options.AnimationId, Path.GetFileName(options.AnimationPath), skeleton, refFrame, binding, spline)
    : interleaved != null
        ? new HavokAnimationData_InterleavedUncompressed(options.AnimationId, Path.GetFileName(options.AnimationPath), skeleton, refFrame, binding, interleaved)
        : throw new InvalidOperationException($"No supported animation data found in '{options.AnimationPath}'.");

var frameCount = Math.Max(1, animationData.FrameCount);
if (options.MaxFrames is > 0)
    frameCount = Math.Min(frameCount, options.MaxFrames.Value);

var export = new PoseExport
{
    SourceSkeleton = options.SkeletonPath,
    SourceAnimation = options.AnimationPath,
    SourceSkeletonCompendium = options.SkeletonCompendiumPath,
    SourceAnimationCompendium = options.AnimationCompendiumPath,
    AnimationName = animationData.Name,
    Duration = animationData.Duration,
    FrameDuration = animationData.FrameDuration,
    SourceFrameCount = animationData.FrameCount,
    ExportedFrameCount = frameCount,
    Bones = ReadBones(skeleton),
    Frames = new List<PoseFrame>(frameCount),
};

for (var frame = 0; frame < frameCount; frame++)
{
    var poseFrame = new PoseFrame
    {
        Frame = frame,
        Time = frame * animationData.FrameDuration,
        LocalTransforms = new List<PoseTransform>(export.Bones.Count),
    };

    for (var boneIndex = 0; boneIndex < export.Bones.Count; boneIndex++)
    {
        var tr = animationData.GetTransformOnFrameByBone(boneIndex, frame, enableLooping: false);
        poseFrame.LocalTransforms.Add(PoseTransform.From(tr));
    }

    export.Frames.Add(poseFrame);
}

WriteExport(options.OutputPath, export, options.Pretty);

Console.WriteLine($"Exported {export.ExportedFrameCount}/{export.SourceFrameCount} frames, {export.Bones.Count} bones");
Console.WriteLine(options.OutputPath);
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("AC6HkxPoseExporter failed:");
    Console.Error.WriteLine(ex);
    return 1;
}

static string GetDsasDir()
{
    var configured = Environment.GetEnvironmentVariable("AC6_DSAS_DIR");
    if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        return Path.GetFullPath(configured);

    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        var candidate = Path.Combine(current.FullName, "DSAnimStudio", "DSAS-V5-RC6.1");
        if (Directory.Exists(candidate))
            return Path.GetFullPath(candidate);

        var siblingTools = Path.Combine(current.FullName, "Tools", "DSAnimStudio", "DSAS-V5-RC6.1");
        if (Directory.Exists(siblingTools))
            return Path.GetFullPath(siblingTools);

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("DSAnimStudio DLL folder not found. Set AC6_DSAS_DIR to the DSAS-V5-RC6.1 directory.");
}

static T? FindObject<T>(HKX hkx) where T : class
{
    foreach (var obj in hkx.DataSection.Objects)
    {
        if (obj is T typed)
            return typed;
    }

    return null;
}

static void DumpObjects(HKX hkx)
{
    for (var i = 0; i < hkx.DataSection.Objects.Count; i++)
    {
        var obj = hkx.DataSection.Objects[i];
        Console.WriteLine($"{i}: {obj?.GetType().FullName ?? "<null>"}");
    }
}

static void WriteExport(string outputPath, PoseExport export, bool pretty)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = pretty,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    File.WriteAllText(outputPath, JsonSerializer.Serialize(export, jsonOptions));
}

static List<PoseBone> ReadBones(HKX.HKASkeleton skeleton)
{
    var boneCount = checked((int)skeleton.Bones.Size);
    var result = new List<PoseBone>(boneCount);
    for (var i = 0; i < boneCount; i++)
    {
        var transform = skeleton.Transforms[i];
        result.Add(new PoseBone
        {
            Index = i,
            Name = skeleton.Bones[i].Name.GetString(),
            ParentIndex = skeleton.ParentIndices[i].data,
            ReferenceLocalTransform = new PoseTransform
            {
                Translation = new Vec3(transform.Position.Vector.X, transform.Position.Vector.Y, transform.Position.Vector.Z),
                Rotation = new Quat(transform.Rotation.Vector.X, transform.Rotation.Vector.Y, transform.Rotation.Vector.Z, transform.Rotation.Vector.W),
                Scale = new Vec3(transform.Scale.Vector.X, transform.Scale.Vector.Y, transform.Scale.Vector.Z),
            },
        });
    }

    return result;
}

sealed class CliOptions
{
    public required string SkeletonPath { get; init; }
    public string? AnimationPath { get; init; }
    public required string OutputPath { get; init; }
    public string? SkeletonCompendiumPath { get; init; }
    public string? AnimationCompendiumPath { get; init; }
    public long AnimationId { get; init; }
    public int? MaxFrames { get; init; }
    public bool Pretty { get; init; }
    public bool SkeletonOnly { get; init; }
    public bool DumpObjects { get; init; }

    public static CliOptions? Parse(string[] args)
    {
        string? skeleton = null;
        string? animation = null;
        string? output = null;
        string? compendium = null;
        string? skeletonCompendium = null;
        string? animationCompendium = null;
        long animationId = 0;
        int? maxFrames = null;
        var pretty = false;
        var skeletonOnly = false;
        var dumpObjects = false;

        for (var i = 0; i < args.Length; i++)
        {
            string RequireValue()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {args[i]}.");
                return args[++i];
            }

            switch (args[i].ToLowerInvariant())
            {
                case "--skeleton":
                    skeleton = RequireValue();
                    break;
                case "--anim":
                case "--animation":
                    animation = RequireValue();
                    break;
                case "--out":
                case "--output":
                    output = RequireValue();
                    break;
                case "--compendium":
                    compendium = RequireValue();
                    break;
                case "--skeleton-compendium":
                    skeletonCompendium = RequireValue();
                    break;
                case "--animation-compendium":
                case "--anim-compendium":
                    animationCompendium = RequireValue();
                    break;
                case "--id":
                    animationId = long.Parse(RequireValue());
                    break;
                case "--max-frames":
                    maxFrames = int.Parse(RequireValue());
                    break;
                case "--pretty":
                    pretty = true;
                    break;
                case "--skeleton-only":
                    skeletonOnly = true;
                    break;
                case "--dump-objects":
                    dumpObjects = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}.");
            }
        }

        if (skeleton == null || (!dumpObjects && output == null) || (!skeletonOnly && !dumpObjects && animation == null))
            return null;

        return new CliOptions
        {
            SkeletonPath = Path.GetFullPath(skeleton),
            AnimationPath = animation != null ? Path.GetFullPath(animation) : null,
            OutputPath = output != null ? Path.GetFullPath(output) : "",
            SkeletonCompendiumPath = skeletonCompendium != null
                ? Path.GetFullPath(skeletonCompendium)
                : compendium != null ? Path.GetFullPath(compendium) : null,
            AnimationCompendiumPath = animationCompendium != null
                ? Path.GetFullPath(animationCompendium)
                : compendium != null ? Path.GetFullPath(compendium) : null,
            AnimationId = animationId,
            MaxFrames = maxFrames,
            Pretty = pretty,
            SkeletonOnly = skeletonOnly,
            DumpObjects = dumpObjects,
        };
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  AC6HkxPoseExporter --skeleton Skeleton.hkx --anim a000_000000.hkx --out pose.json [--skeleton-compendium skeleton.compendium] [--animation-compendium animation.compendium] [--max-frames 30] [--pretty]");
        Console.Error.WriteLine("  AC6HkxPoseExporter --skeleton Skeleton.hkx --skeleton-only --out skeleton.json [--skeleton-compendium skeleton.compendium] [--pretty]");
        Console.Error.WriteLine("  AC6HkxPoseExporter --skeleton Skeleton.hkx --dump-objects [--skeleton-compendium skeleton.compendium]");
        Console.Error.WriteLine("  --compendium remains as a legacy shorthand for both compendium arguments.");
    }
}

sealed class PoseExport
{
    public string? SourceSkeleton { get; init; }
    public string? SourceAnimation { get; init; }
    public string? SourceSkeletonCompendium { get; init; }
    public string? SourceAnimationCompendium { get; init; }
    public string? AnimationName { get; init; }
    public float Duration { get; init; }
    public float FrameDuration { get; init; }
    public int SourceFrameCount { get; init; }
    public int ExportedFrameCount { get; init; }
    public required List<PoseBone> Bones { get; init; }
    public required List<PoseFrame> Frames { get; init; }
}

sealed class PoseBone
{
    public int Index { get; init; }
    public string? Name { get; init; }
    public short ParentIndex { get; init; }
    public required PoseTransform ReferenceLocalTransform { get; init; }
}

sealed class PoseFrame
{
    public int Frame { get; init; }
    public float Time { get; init; }
    public required List<PoseTransform> LocalTransforms { get; init; }
}

readonly record struct PoseTransform(Vec3 Translation, Quat Rotation, Vec3 Scale)
{
    public static PoseTransform From(NewBlendableTransform transform)
        => new(
            new Vec3(transform.Translation.X, transform.Translation.Y, transform.Translation.Z),
            new Quat(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W),
            new Vec3(transform.Scale.X, transform.Scale.Y, transform.Scale.Z));
}

readonly record struct Vec3(float X, float Y, float Z);
readonly record struct Quat(float X, float Y, float Z, float W);
