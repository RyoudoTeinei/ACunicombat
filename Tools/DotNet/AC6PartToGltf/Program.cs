// AC6PartToGltf
// Merges AC6 part skeleton JSONs (from AC6HkxPoseExporter --skeleton-only) over the c0000 base
// skeleton, verifies each part FLVER's bind pose against the merged skeleton, and exports a
// skinned glTF 2.0 binary (.glb) with optional animation from a pose JSON.
//
// Coordinate convention: everything stays in FromSoft/Havok space (right-handed, Y-up), which is
// also glTF's native convention. No axis conversion is performed anywhere in this tool.

using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulsFormats;

try
{
    var options = CliOptions.Parse(args);
    if (options == null)
    {
        CliOptions.PrintUsage();
        return 2;
    }

    // ---- 1. Load and merge skeletons -------------------------------------------------------
    var baseSkeleton = PoseJson.Load(options.BaseSkeletonPath);
    var merged = new MergedSkeleton(baseSkeleton);
    foreach (var partSkeletonPath in options.PartSkeletonPaths)
        merged.Overlay(PoseJson.Load(partSkeletonPath), Path.GetFileNameWithoutExtension(partSkeletonPath));

    Console.WriteLine($"Merged skeleton: {merged.Bones.Count} bones ({baseSkeleton.Bones.Count} base + {merged.Bones.Count - baseSkeleton.Bones.Count} part-local), {merged.OverrideCount} locals overridden, {merged.ConflictCount} conflicts (>1mm disagreement)");

    // ---- 2. Load FLVERs, graft part-local bones, verify alignment --------------------------
    var parts = new List<PartModel>();
    foreach (var flverPath in options.FlverPaths)
    {
        var part = PartModel.Load(flverPath, merged);
        parts.Add(part);
        Console.WriteLine($"[{part.Name}] meshes={part.Flver.Meshes.Count} usedBones={part.UsedNodeIndices.Count} grafted={part.GraftedBoneCount} " +
                          $"alignErr: pos max={part.MaxPosError:0.####} avg={part.AvgPosError:0.####}, rot max={part.MaxRotErrorDeg:0.##}deg (over {part.VerifiedBoneCount} shared bones)");
        foreach (var worst in part.WorstBones.Take(options.VerboseVerify ? 20 : 3))
            Console.WriteLine($"    {worst.Name}: dpos={worst.PosError:0.####} drot={worst.RotErrorDeg:0.##}deg");
    }

    if (options.DumpDummies)
    {
        foreach (var part in parts)
        {
            Console.WriteLine($"# {part.Name} dummies={part.Flver.Dummies.Count}");
            foreach (var d in part.Flver.Dummies)
            {
                string BoneName(int idx) => idx >= 0 && idx < part.Flver.Nodes.Count ? part.Flver.Nodes[idx].Name : "<none>";
                Console.WriteLine(
                    $"  ref={d.ReferenceID,5} pos=({d.Position.X:0.###},{d.Position.Y:0.###},{d.Position.Z:0.###}) " +
                    $"fwd=({d.Forward.X:0.###},{d.Forward.Y:0.###},{d.Forward.Z:0.###}) " +
                    $"up=({d.Upward.X:0.###},{d.Upward.Y:0.###},{d.Upward.Z:0.###}) useUp={d.UseUpwardVector} " +
                    $"attach={BoneName(d.AttachBoneIndex)} parent={BoneName(d.ParentBoneIndex)} flag1={d.Flag1}");
            }
        }
        return 0;
    }

    if (options.ReportPath != null)
        WriteReport(options.ReportPath, merged, parts);

    if (options.VerifyOnly)
        return 0;

    // ---- 3. Load optional animation --------------------------------------------------------
    var animations = options.AnimPaths.Select(PoseJson.Load).ToList();
    foreach (var animation in animations)
        Console.WriteLine($"Animation: {animation.AnimationName} frames={animation.ExportedFrameCount} duration={animation.Duration:0.###}s bones={animation.Bones.Count}");

    // ---- 4. Build glTF ----------------------------------------------------------------------
    var glb = GltfBuilder.Build(merged, parts, animations, options);
    File.WriteAllBytes(options.OutputPath!, glb);
    Console.WriteLine($"Wrote {options.OutputPath} ({glb.Length / 1024} KiB)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("AC6PartToGltf failed:");
    Console.Error.WriteLine(ex);
    return 1;
}

static void WriteReport(string path, MergedSkeleton merged, List<PartModel> parts)
{
    var report = new
    {
        Skeleton = new
        {
            BoneCount = merged.Bones.Count,
            Overrides = merged.OverrideCount,
            Conflicts = merged.ConflictCount,
            ConflictBones = merged.ConflictBones,
        },
        Parts = parts.Select(p => new
        {
            p.Name,
            MeshCount = p.Flver.Meshes.Count,
            DynamicMeshCount = p.Flver.Meshes.Count(m => m.Dynamic != 0),
            ReferenceSpaceMeshCount = p.Flver.Meshes.Count(m => m.Dynamic == 0),
            UsedBoneCount = p.UsedNodeIndices.Count,
            GraftedBones = p.GraftedBoneCount,
            VerifiedSharedBones = p.VerifiedBoneCount,
            MaxPositionError = p.MaxPosError,
            AveragePositionError = p.AvgPosError,
            MaxRotationErrorDeg = p.MaxRotErrorDeg,
            Worst = p.WorstBones.Take(20).Select(w => new { w.Name, w.PosError, w.RotErrorDeg }),
            Materials = p.Flver.Materials.Select(m => new
            {
                m.Name,
                m.MTD,
                Textures = m.Textures.Select(t => new { t.ParamName, t.Path }),
            }),
        }),
    };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote report {path}");
}

// ================================ CLI ========================================================

sealed class CliOptions
{
    public required string BaseSkeletonPath { get; init; }
    public List<string> PartSkeletonPaths { get; } = new();
    public List<string> FlverPaths { get; } = new();
    public string? OutputPath { get; init; }
    public List<string> AnimPaths { get; } = new();
    public string? ReportPath { get; init; }
    public string? TexDir { get; init; }
    public bool VerifyOnly { get; init; }
    public bool VerboseVerify { get; init; }
    public bool DumpDummies { get; init; }

    public static CliOptions? Parse(string[] args)
    {
        string? baseSkeleton = null, output = null, report = null, texDir = null;
        var animations = new List<string>();
        var partSkeletons = new List<string>();
        var flvers = new List<string>();
        bool verifyOnly = false, verbose = false, dumpDummies = false;

        for (var i = 0; i < args.Length; i++)
        {
            string Val()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}");
                return args[++i];
            }
            switch (args[i].ToLowerInvariant())
            {
                case "--skeleton-json": baseSkeleton = Val(); break;
                case "--part-skeleton-json": partSkeletons.Add(Val()); break;
                case "--flver": flvers.Add(Val()); break;
                case "--out": output = Val(); break;
                case "--anim": animations.Add(Val()); break;
                case "--report": report = Val(); break;
                case "--tex-dir": texDir = Val(); break;
                case "--verify-only": verifyOnly = true; break;
                case "--verbose": verbose = true; break;
                case "--dump-dummies": dumpDummies = true; break;
                default: throw new ArgumentException($"Unknown argument {args[i]}");
            }
        }

        if (baseSkeleton == null || flvers.Count == 0 || (!verifyOnly && !dumpDummies && output == null))
            return null;

        var opts = new CliOptions
        {
            BaseSkeletonPath = baseSkeleton,
            OutputPath = output,
            ReportPath = report,
            TexDir = texDir,
            VerifyOnly = verifyOnly,
            VerboseVerify = verbose,
            DumpDummies = dumpDummies,
        };
        opts.PartSkeletonPaths.AddRange(partSkeletons);
        opts.FlverPaths.AddRange(flvers);
        opts.AnimPaths.AddRange(animations);
        return opts;
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: AC6PartToGltf --skeleton-json c0000_skeleton.json [--part-skeleton-json skel_lg.json ...]");
        Console.Error.WriteLine("           --flver LG_M_1010.flver [--flver ...] --out out.glb");
        Console.Error.WriteLine("           [--anim pose.json ...] [--tex-dir pngDir] [--report report.json] [--verify-only] [--verbose]");
    }
}

// ============================ Skeleton JSON DTOs =============================================

sealed class PoseJson
{
    public string? AnimationName { get; set; }
    public float Duration { get; set; }
    public float FrameDuration { get; set; }
    public int SourceFrameCount { get; set; }
    public int ExportedFrameCount { get; set; }
    public List<PoseBone> Bones { get; set; } = new();
    public List<PoseFrame> Frames { get; set; } = new();

    public static PoseJson Load(string path)
        => JsonSerializer.Deserialize<PoseJson>(File.ReadAllText(path))
           ?? throw new InvalidDataException($"Cannot parse {path}");
}

sealed class PoseBone
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public short ParentIndex { get; set; }
    public PoseTransform ReferenceLocalTransform { get; set; } = new();
}

sealed class PoseFrame
{
    public int Frame { get; set; }
    public float Time { get; set; }
    public List<PoseTransform> LocalTransforms { get; set; } = new();
}

sealed class PoseTransform
{
    public Vec3 Translation { get; set; } = new();
    public QuatJson Rotation { get; set; } = new();
    public Vec3 Scale { get; set; } = new() { X = 1, Y = 1, Z = 1 };

    public Vector3 T => new(Translation.X, Translation.Y, Translation.Z);
    public Quaternion R => Quaternion.Normalize(new Quaternion(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W));
    public Vector3 S => new(Scale.X, Scale.Y, Scale.Z);
    public Matrix4x4 ToMatrix() => Matrix4x4.CreateScale(S) * Matrix4x4.CreateFromQuaternion(R) * Matrix4x4.CreateTranslation(T);
}

sealed class Vec3 { public float X { get; set; } public float Y { get; set; } public float Z { get; set; } }
sealed class QuatJson { public float X { get; set; } public float Y { get; set; } public float Z { get; set; } public float W { get; set; } = 1; }

// ============================ Merged skeleton ================================================

sealed class SkelBone
{
    public required string Name;
    public int Parent = -1;               // index into MergedSkeleton.Bones
    public Vector3 T;
    public Quaternion R = Quaternion.Identity;
    public Vector3 S = Vector3.One;
    public string Source = "base";
    public Matrix4x4 Local => Matrix4x4.CreateScale(S) * Matrix4x4.CreateFromQuaternion(R) * Matrix4x4.CreateTranslation(T);
}

sealed class MergedSkeleton
{
    public List<SkelBone> Bones { get; } = new();
    public Dictionary<string, int> ByName { get; } = new(StringComparer.Ordinal);
    public int OverrideCount { get; private set; }
    public int ConflictCount { get; private set; }
    public List<string> ConflictBones { get; } = new();

    public MergedSkeleton(PoseJson baseSkeleton)
    {
        foreach (var b in baseSkeleton.Bones)
        {
            var bone = new SkelBone
            {
                Name = b.Name,
                Parent = b.ParentIndex,
                T = b.ReferenceLocalTransform.T,
                R = b.ReferenceLocalTransform.R,
                S = b.ReferenceLocalTransform.S,
            };
            ByName[b.Name] = Bones.Count;
            Bones.Add(bone);
        }
    }

    public void Overlay(PoseJson partSkeleton, string sourceName)
    {
        // Two passes so parents of new bones resolve regardless of ordering.
        foreach (var b in partSkeleton.Bones)
        {
            if (ByName.ContainsKey(b.Name))
                continue;
            ByName[b.Name] = Bones.Count;
            Bones.Add(new SkelBone { Name = b.Name, Source = sourceName });
        }

        foreach (var b in partSkeleton.Bones)
        {
            var idx = ByName[b.Name];
            var bone = Bones[idx];
            var parentName = b.ParentIndex >= 0 && b.ParentIndex < partSkeleton.Bones.Count
                ? partSkeleton.Bones[b.ParentIndex].Name : null;

            var t = b.ReferenceLocalTransform.T;
            var r = b.ReferenceLocalTransform.R;
            var s = b.ReferenceLocalTransform.S;

            if (bone.Source == "base")
            {
                var moved = (t - bone.T).Length();
                if (moved > 0.001f)
                {
                    OverrideCount++;
                    bone.Source = sourceName;
                }
                bone.T = t; bone.R = r; bone.S = s;
            }
            else if (bone.Source != sourceName)
            {
                // Already overridden by an earlier part — check agreement.
                if ((t - bone.T).Length() > 0.001f)
                {
                    ConflictCount++;
                    ConflictBones.Add($"{bone.Name} ({bone.Source} vs {sourceName}, d={(t - bone.T).Length():0.####})");
                }
            }
            else
            {
                bone.T = t; bone.R = r; bone.S = s;
            }

            if (bone.Parent < 0 && parentName != null && ByName.TryGetValue(parentName, out var pIdx) && pIdx != idx)
                bone.Parent = pIdx;
        }
    }

    /// Adds a FLVER-only bone. Returns its index.
    public int AddGrafted(string name, int parentIndex, Matrix4x4 local, string source)
    {
        Matrix4x4.Decompose(local, out var s, out var r, out var t);
        var bone = new SkelBone { Name = name, Parent = parentIndex, T = t, R = r, S = s, Source = source };
        ByName[name] = Bones.Count;
        Bones.Add(bone);
        return Bones.Count - 1;
    }

    public Matrix4x4[] ComputeGlobals()
    {
        var globals = new Matrix4x4[Bones.Count];
        for (var i = 0; i < Bones.Count; i++)
        {
            var local = Bones[i].Local;
            globals[i] = Bones[i].Parent >= 0 ? local * globals[Bones[i].Parent] : local;
        }
        return globals;
    }
}

// ============================ Part model (FLVER) =============================================

sealed class PartModel
{
    public required string Name;
    public required FLVER2 Flver;
    public required string SourceDir;
    public SortedSet<int> UsedNodeIndices = new();       // FLVER node indices used by meshes
    public Dictionary<int, int> FlverToMerged = new();   // FLVER node index -> merged bone index
    public Matrix4x4[] FlverGlobals = Array.Empty<Matrix4x4>();
    public int GraftedBoneCount;

    // verification
    public int VerifiedBoneCount;
    public float MaxPosError, AvgPosError, MaxRotErrorDeg;
    public List<(string Name, float PosError, float RotErrorDeg)> WorstBones = new();

    public static PartModel Load(string flverPath, MergedSkeleton merged)
    {
        var flver = FLVER2.Read(flverPath);
        var part = new PartModel
        {
            Name = Path.GetFileNameWithoutExtension(flverPath),
            Flver = flver,
            SourceDir = Path.GetDirectoryName(Path.GetFullPath(flverPath))!,
        };

        // FLVER global matrices via its own parent chain.
        part.FlverGlobals = new Matrix4x4[flver.Nodes.Count];
        for (var i = 0; i < flver.Nodes.Count; i++)
        {
            var node = flver.Nodes[i];
            var local = node.ComputeLocalTransform();
            part.FlverGlobals[i] = node.ParentIndex >= 0 ? local * part.FlverGlobals[node.ParentIndex] : local;
        }

        // Which nodes do meshes actually reference?
        foreach (var mesh in flver.Meshes)
        {
            if (mesh.UseBoneWeights)
            {
                foreach (var v in mesh.Vertices)
                    for (var i = 0; i < 4; i++)
                        if (v.BoneWeights[i] > 0 && v.BoneIndices[i] >= 0 && v.BoneIndices[i] < flver.Nodes.Count)
                            part.UsedNodeIndices.Add(v.BoneIndices[i]);
            }
            else
            {
                var foundNormalWBone = false;
                foreach (var vertex in mesh.Vertices)
                {
                    var boneIndex = vertex.NormalW;
                    if (boneIndex < 0 || boneIndex >= flver.Nodes.Count)
                        continue;

                    part.UsedNodeIndices.Add(boneIndex);
                    foundNormalWBone = true;
                }

                if (!foundNormalWBone && mesh.NodeIndex >= 0)
                    part.UsedNodeIndices.Add(mesh.NodeIndex);
            }
        }

        // Dummy transforms follow ParentBoneIndex in AC6. Include those bones even when no mesh
        // vertex references them so socket nodes can remain under the animated hierarchy.
        foreach (var dummy in flver.Dummies)
        {
            if (dummy.ParentBoneIndex >= 0 && dummy.ParentBoneIndex < flver.Nodes.Count)
                part.UsedNodeIndices.Add(dummy.ParentBoneIndex);
        }

        // Map used FLVER nodes onto the merged skeleton; graft chains for part-local bones.
        foreach (var nodeIndex in part.UsedNodeIndices)
            part.MapNode(nodeIndex, merged);

        // Verify: for every used node that maps to a shared (non-grafted) bone, compare globals.
        var mergedGlobals = merged.ComputeGlobals();
        float sum = 0;
        var errors = new List<(string, float, float)>();
        foreach (var nodeIndex in part.UsedNodeIndices)
        {
            var name = flver.Nodes[nodeIndex].Name;
            if (!part.FlverToMerged.TryGetValue(nodeIndex, out var mIdx)) continue;
            if (merged.Bones[mIdx].Source.StartsWith("graft:")) continue;

            var fg = part.FlverGlobals[nodeIndex];
            var mg = mergedGlobals[mIdx];
            var dpos = (fg.Translation - mg.Translation).Length();
            Matrix4x4.Decompose(fg, out _, out var fq, out _);
            Matrix4x4.Decompose(mg, out _, out var mq, out _);
            var dot = Math.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(fq), Quaternion.Normalize(mq))));
            var drot = 2f * MathF.Acos(dot) * 180f / MathF.PI;

            part.VerifiedBoneCount++;
            sum += dpos;
            part.MaxPosError = MathF.Max(part.MaxPosError, dpos);
            part.MaxRotErrorDeg = MathF.Max(part.MaxRotErrorDeg, drot);
            errors.Add((name, dpos, drot));
        }
        part.AvgPosError = part.VerifiedBoneCount > 0 ? sum / part.VerifiedBoneCount : 0;
        part.WorstBones = errors.OrderByDescending(e => e.Item2).ToList();
        return part;
    }

    void MapNode(int nodeIndex, MergedSkeleton merged)
    {
        if (FlverToMerged.ContainsKey(nodeIndex))
            return;

        var node = Flver.Nodes[nodeIndex];
        if (merged.ByName.TryGetValue(node.Name, out var existing))
        {
            FlverToMerged[nodeIndex] = existing;
            return;
        }

        // Part-local bone: graft it (and its ancestors first).
        int mergedParent;
        if (node.ParentIndex >= 0)
        {
            MapNode(node.ParentIndex, merged);
            mergedParent = FlverToMerged[node.ParentIndex];
        }
        else if (merged.ByName.TryGetValue("Master", out var master))
        {
            mergedParent = master; // parentless flat bone: local == model space, Master is identity
        }
        else
        {
            mergedParent = -1;
        }

        var idx = merged.AddGrafted(node.Name, mergedParent, node.ComputeLocalTransform(), $"graft:{Name}");
        FlverToMerged[nodeIndex] = idx;
        GraftedBoneCount++;
    }
}

// ============================ glTF builder ===================================================

static class GltfBuilder
{
    public static byte[] Build(MergedSkeleton merged, List<PartModel> parts, IReadOnlyList<PoseJson> sourceAnimations, CliOptions options)
    {
        var bin = new BinWriter();
        var accessors = new List<object>();
        var bufferViews = new List<object>();
        var images = new List<object>();
        var textures = new List<object>();
        var materials = new List<object>();
        var meshes = new List<object>();
        var samplers = new List<object> { new { magFilter = 9729, minFilter = 9987, wrapS = 10497, wrapT = 10497 } };

        // ---- Nodes: skeleton bones first, then one mesh node per part -----------------------
        var nodes = new List<Dictionary<string, object>>();
        foreach (var bone in merged.Bones)
        {
            var n = new Dictionary<string, object> { ["name"] = bone.Name };
            if (bone.T != Vector3.Zero) n["translation"] = new[] { bone.T.X, bone.T.Y, bone.T.Z };
            if (bone.R != Quaternion.Identity) n["rotation"] = new[] { bone.R.X, bone.R.Y, bone.R.Z, bone.R.W };
            if (bone.S != Vector3.One) n["scale"] = new[] { bone.S.X, bone.S.Y, bone.S.Z };
            nodes.Add(n);
        }
        for (var i = 0; i < merged.Bones.Count; i++)
        {
            var p = merged.Bones[i].Parent;
            if (p < 0) continue;
            var parentNode = nodes[p];
            if (!parentNode.TryGetValue("children", out var kids))
                parentNode["children"] = kids = new List<int>();
            ((List<int>)kids!).Add(i);
        }
        var rootBoneIndices = Enumerable.Range(0, merged.Bones.Count).Where(i => merged.Bones[i].Parent < 0).ToList();

        // ---- One shared skin over all skeleton nodes ---------------------------------------
        // IBMs: per part we bind against the part's own FLVER globals for bones it uses; for
        // everything else the merged rest globals. Verification already told us how far apart
        // those are. A single skin keeps the file simple; per-part IBM differences beyond the
        // reported alignment error are not representable this way (by design: they should be 0).
        var mergedGlobals = merged.ComputeGlobals();
        var jointCount = merged.Bones.Count;
        var ibms = new Matrix4x4[jointCount];
        for (var i = 0; i < jointCount; i++)
        {
            if (!Matrix4x4.Invert(mergedGlobals[i], out ibms[i]))
                ibms[i] = Matrix4x4.Identity;
        }
        // Prefer FLVER-authored bind matrices where a part uses the bone.
        foreach (var part in parts)
        {
            foreach (var nodeIndex in part.UsedNodeIndices)
            {
                var mIdx = part.FlverToMerged[nodeIndex];
                if (Matrix4x4.Invert(part.FlverGlobals[nodeIndex], out var inv))
                    ibms[mIdx] = inv;
            }
        }

        var ibmBytes = new byte[jointCount * 64];
        for (var i = 0; i < jointCount; i++)
            WriteMatrix(ibmBytes.AsSpan(i * 64), ibms[i]);
        var ibmView = bin.AddBufferView(ibmBytes, bufferViews, null);
        var deformIbmAccessor = accessors.Count;
        accessors.Add(new Dictionary<string, object>
        {
            ["bufferView"] = ibmView, ["componentType"] = 5126, ["count"] = jointCount, ["type"] = "MAT4",
        });

        // FLVER mesh.Dynamic has a material-space consequence, not just an optimization flag.
        // Dynamic meshes store model-space vertices and use currentGlobal * inverseBind. Static
        // (Dynamic == 0) meshes store vertices in bone-local space and use currentGlobal directly.
        // glTF places the inverse bind matrix on the skin, so both representations need separate
        // skins over the same joints. This matches DSAnimStudio's ShaderMatricesNew versus
        // ShaderMatricesNew_RefPose paths and preserves weapon-part animation without scale blowups.
        var identityIbmBytes = new byte[jointCount * 64];
        for (var i = 0; i < jointCount; i++)
            WriteMatrix(identityIbmBytes.AsSpan(i * 64), Matrix4x4.Identity);
        var identityIbmView = bin.AddBufferView(identityIbmBytes, bufferViews, null);
        var referenceIbmAccessor = accessors.Count;
        accessors.Add(new Dictionary<string, object>
        {
            ["bufferView"] = identityIbmView, ["componentType"] = 5126, ["count"] = jointCount, ["type"] = "MAT4",
        });

        const int deformSkinIndex = 0;
        const int referenceSpaceSkinIndex = 1;
        var skinJoints = Enumerable.Range(0, jointCount).ToList();
        var skins = new List<object>
        {
            new { name = "AC6_DeformSkin", inverseBindMatrices = deformIbmAccessor, joints = skinJoints, skeleton = rootBoneIndices[0] },
            new { name = "AC6_ReferenceSpaceSkin", inverseBindMatrices = referenceIbmAccessor, joints = skinJoints, skeleton = rootBoneIndices[0] },
        };

        // ---- Textures ------------------------------------------------------------------------
        var textureCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int? FindTexture(PartModel part, string ddsPath, bool srgb)
        {
            if (string.IsNullOrWhiteSpace(ddsPath)) return null;
            var stem = Path.GetFileNameWithoutExtension(ddsPath);
            if (textureCache.TryGetValue(stem, out var cached)) return cached;

            string? png = null;
            foreach (var dir in new[] { options.TexDir, part.SourceDir })
            {
                if (dir == null) continue;
                var candidate = Path.Combine(dir, stem + ".png");
                if (File.Exists(candidate)) { png = candidate; break; }
            }
            if (png == null) return null;

            var bytes = File.ReadAllBytes(png);
            var view = bin.AddBufferView(bytes, bufferViews, null);
            images.Add(new { name = stem, mimeType = "image/png", bufferView = view });
            textures.Add(new { sampler = 0, source = images.Count - 1 });
            textureCache[stem] = textures.Count - 1;
            return textures.Count - 1;
        }

        // ---- Meshes ----------------------------------------------------------------------------
        var meshNodes = new List<int>();
        foreach (var part in parts)
        {
            var materialBase = materials.Count;
            foreach (var mat in part.Flver.Materials)
            {
                string? Find(params string[] hints)
                    => mat.Textures.FirstOrDefault(t => hints.Any(h => t.ParamName.Contains(h, StringComparison.OrdinalIgnoreCase))
                                                        && !string.IsNullOrWhiteSpace(t.Path))?.Path;

                var albedo = Find("AlbedoMap", "Diffuse");
                var normal = Find("NormalMap", "Bumpmap");
                var emissive = Find("EmissiveMap", "Emissive");

                // AC6 FLVERs carry no texture paths (they live in matbin, which the rip lacks);
                // fall back to the part naming convention: {PART}_01_{a,n,em}.dds. Weapon FLVERs
                // are named WP_[LRE]_xxxx but their textures drop the hand marker (WP_xxxx_a.dds),
                // and multi-material weapons number their sheets _01/_02 by material order.
                if (albedo == null && normal == null && emissive == null)
                {
                    var stripped = System.Text.RegularExpressions.Regex.Replace(part.Name, "^WP_[LRE]_", "WP_");
                    var matOrdinal = $"_{part.Flver.Materials.IndexOf(mat) + 1:00}";
                    var stem = new[] { part.Name + "_01", part.Name, stripped + matOrdinal, stripped + "_01", stripped }
                        .FirstOrDefault(s => TexExists(part, options, s + "_a")) ?? part.Name + "_01";
                    var mtd = mat.MTD ?? "";
                    if (mtd.Contains("Marking", StringComparison.OrdinalIgnoreCase))
                    {
                        albedo = "MarkingEmblem_a.dds";
                    }
                    else if (mtd.Contains("Limiter", StringComparison.OrdinalIgnoreCase))
                    {
                        // translucent tech overlay — leave untextured
                    }
                    else if (mtd.Contains("Emissive", StringComparison.OrdinalIgnoreCase))
                    {
                        albedo = stem + "_a.dds";
                        emissive = stem + "_em.dds";
                    }
                    else
                    {
                        albedo = stem + "_a.dds";
                        normal = stem + "_n.dds";
                        if (mtd.StartsWith("P[", StringComparison.Ordinal))
                            emissive = stem + "_em.dds"; // booster nozzles glow
                    }
                }

                var m = new Dictionary<string, object>
                {
                    ["name"] = mat.Name,
                    ["doubleSided"] = true,
                    ["extras"] = new Dictionary<string, object>
                    {
                        ["mtd"] = mat.MTD ?? string.Empty,
                        ["textures"] = mat.Textures.Where(t => !string.IsNullOrWhiteSpace(t.Path))
                            .ToDictionary(t => t.ParamName, t => (object)Path.GetFileName(t.Path)),
                    },
                };
                var pbr = new Dictionary<string, object> { ["metallicFactor"] = 0.4, ["roughnessFactor"] = 0.6 };
                var albedoTex = albedo != null ? FindTexture(part, albedo, true) : null;
                if (albedoTex != null) pbr["baseColorTexture"] = new { index = albedoTex.Value };
                m["pbrMetallicRoughness"] = pbr;
                var normalTex = normal != null ? FindTexture(part, normal, false) : null;
                if (normalTex != null) m["normalTexture"] = new { index = normalTex.Value };
                var emissiveTex = emissive != null ? FindTexture(part, emissive, true) : null;
                if (emissiveTex != null)
                {
                    m["emissiveTexture"] = new { index = emissiveTex.Value };
                    m["emissiveFactor"] = new[] { 1f, 1f, 1f };
                }
                materials.Add(m);
            }

            var deformPrimitives = new List<object>();
            var referenceSpacePrimitives = new List<object>();
            foreach (var mesh in part.Flver.Meshes)
            {
                var prim = BuildPrimitive(part, mesh, bin, accessors, bufferViews, materialBase);
                if (prim != null)
                {
                    if (mesh.Dynamic == 0)
                        referenceSpacePrimitives.Add(prim);
                    else
                        deformPrimitives.Add(prim);
                }
            }

            void AddMeshNode(List<object> primitives, string kind, int skinIndex)
            {
                if (primitives.Count == 0)
                    return;

                var hasBothRepresentations = deformPrimitives.Count > 0 && referenceSpacePrimitives.Count > 0;
                var suffix = hasBothRepresentations ? "_" + kind : string.Empty;
                meshes.Add(new { name = part.Name + suffix, primitives });
                var meshNode = new Dictionary<string, object>
                {
                    ["name"] = part.Name + suffix + "_mesh",
                    ["mesh"] = meshes.Count - 1,
                    ["skin"] = skinIndex,
                    ["extras"] = new Dictionary<string, object>
                    {
                        ["ac6FlverMeshSpace"] = kind,
                    },
                };
                nodes.Add(meshNode);
                meshNodes.Add(nodes.Count - 1);
            }

            AddMeshNode(deformPrimitives, "Dynamic", deformSkinIndex);
            AddMeshNode(referenceSpacePrimitives, "ReferenceSpace", referenceSpaceSkinIndex);

            // FLVER dummy position/orientation are authored in ParentBoneIndex's reference space
            // (DSAS NewDummyPolyInfo: dummyLocal * ParentBone.ReferenceFKMatrix). In AC6 part
            // FLVERs the parent is a parentless identity "container" node, which makes the values
            // numerically model-space, but we compose the parent reference global anyway.
            // Flag1 selects whether the dummy follows AttachBoneIndex during animation
            // (DSAS: CurrentMatrix = ReferenceMatrix * AttachBone.RelativeMatrix when Flag1).
            // The single-parent glTF encoding of that rule is:
            //   Flag1: parent under AttachBone, local = dummyRefWorld * inverse(attachBindGlobal)
            //   else:  keep the static reference pose (parent bone container / scene root).
            foreach (var dummy in part.Flver.Dummies)
            {
                var forward = SafeNormalize(dummy.Forward, -Vector3.UnitZ);
                var upward = dummy.UseUpwardVector
                    ? SafeNormalize(dummy.Upward, Vector3.UnitY)
                    : Vector3.UnitY;
                // System.Numerics.CreateWorld treats its forward argument as local -Z.
                // FLVER dummies author Forward as local +Z, so negate the argument once
                // here instead of exporting every equipment socket with a 180-degree yaw.
                var local = Matrix4x4.CreateWorld(Vector3.Zero, -forward, upward)
                    * Matrix4x4.CreateTranslation(dummy.Position);
                if (dummy.ParentBoneIndex >= 0 && dummy.ParentBoneIndex < part.Flver.Nodes.Count)
                    local *= part.FlverGlobals[dummy.ParentBoneIndex];

                var parentBone = -1;
                var attachIndex = dummy.AttachBoneIndex >= 0
                    ? dummy.AttachBoneIndex
                    : dummy.ParentBoneIndex;
                if (dummy.Flag1 && attachIndex >= 0 && attachIndex < part.Flver.Nodes.Count
                    && merged.ByName.TryGetValue(part.Flver.Nodes[attachIndex].Name, out var mIdx))
                {
                    parentBone = mIdx;
                    if (Matrix4x4.Invert(part.FlverGlobals[attachIndex], out var inverseAttachBind))
                        local *= inverseAttachBind;
                }
                else if (!dummy.Flag1 && dummy.ParentBoneIndex >= 0 && dummy.ParentBoneIndex < part.Flver.Nodes.Count
                    && merged.ByName.TryGetValue(part.Flver.Nodes[dummy.ParentBoneIndex].Name, out var pIdx))
                {
                    // Non-following dummy whose parent container exists in the tree: express the
                    // reference world back in that parent's space.
                    parentBone = pIdx;
                    if (Matrix4x4.Invert(part.FlverGlobals[dummy.ParentBoneIndex], out var inverseParentBind))
                        local *= inverseParentBind;
                }

                Matrix4x4.Decompose(local, out _, out var rq, out var tv);
                var dn = new Dictionary<string, object>
                {
                    ["name"] = $"{part.Name}_dmy_{dummy.ReferenceID}",
                    ["translation"] = new[] { tv.X, tv.Y, tv.Z },
                    ["rotation"] = new[] { rq.X, rq.Y, rq.Z, rq.W },
                    ["extras"] = new Dictionary<string, object>
                    {
                        ["ac6DummyReferenceId"] = dummy.ReferenceID,
                        ["ac6AttachBoneIndex"] = dummy.AttachBoneIndex,
                        ["ac6ParentBoneIndex"] = dummy.ParentBoneIndex,
                        ["ac6FollowFlag"] = dummy.Flag1,
                    },
                };
                nodes.Add(dn);
                var dummyNodeIndex = nodes.Count - 1;
                if (parentBone >= 0)
                {
                    var parentNode = nodes[parentBone];
                    if (!parentNode.TryGetValue("children", out var kids))
                        parentNode["children"] = kids = new List<int>();
                    ((List<int>)kids!).Add(dummyNodeIndex);
                }
                else
                {
                    meshNodes.Add(dummyNodeIndex); // scene root fallback
                }
            }
        }

        // ---- Animation ---------------------------------------------------------------------
        var animations = new List<object>();
        foreach (var animation in sourceAnimations)
        {
            if (animation.Frames.Count > 0)
                animations.Add(BuildAnimation(animation, merged, bin, accessors, bufferViews));
        }

        // ---- Assemble JSON -------------------------------------------------------------------
        var sceneNodes = rootBoneIndices.Concat(meshNodes).ToList();
        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0", generator = "AC6PartToGltf" },
            ["scene"] = 0,
            ["scenes"] = new object[] { new { nodes = sceneNodes } },
            ["nodes"] = nodes,
            ["skins"] = skins,
            ["meshes"] = meshes,
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new object[] { new { byteLength = bin.Length } },
            ["samplers"] = samplers,
        };
        if (images.Count > 0) { gltf["images"] = images; gltf["textures"] = textures; }
        if (animations.Count > 0) gltf["animations"] = animations;

        return WriteGlb(gltf, bin);
    }

    static bool TexExists(PartModel part, CliOptions options, string stem)
        => new[] { options.TexDir, part.SourceDir }
            .Any(dir => dir != null && File.Exists(Path.Combine(dir, stem + ".png")));

    static object? BuildPrimitive(PartModel part, FLVER2.Mesh mesh, BinWriter bin, List<object> accessors, List<object> bufferViews, int materialBase)
    {
        var verts = mesh.Vertices;
        if (verts.Count == 0) return null;

        // Pick the main (non-LOD) face set.
        var faceSet = mesh.FaceSets.FirstOrDefault(fs => fs.Flags == FLVER2.FaceSet.FSFlags.None) ?? mesh.FaceSets.FirstOrDefault();
        if (faceSet == null) return null;
        var indices = Triangulate(faceSet, verts.Count);
        if (indices.Count == 0) return null;

        var vcount = verts.Count;
        var pos = new byte[vcount * 12];
        var nrm = new byte[vcount * 12];
        var uv = new byte[vcount * 8];
        var joints = new byte[vcount * 8];
        var weights = new byte[vcount * 16];

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        // Dynamic rigid meshes store vertices in the space of their node; bake them to model space
        // for the deform skin. Dynamic == 0 meshes intentionally remain in bone-local space and
        // use the separate identity-IBM skin created above.
        var usesNormalWBones = !mesh.UseBoneWeights && verts.Any(vertex =>
            vertex.NormalW >= 0 && vertex.NormalW < part.Flver.Nodes.Count &&
            part.FlverToMerged.ContainsKey(vertex.NormalW));
        var rigidTransform = Matrix4x4.Identity;
        var isRigid = mesh.Dynamic != 0 && !mesh.UseBoneWeights && !usesNormalWBones &&
            mesh.NodeIndex >= 0 && mesh.NodeIndex < part.FlverGlobals.Length;
        if (isRigid)
            rigidTransform = part.FlverGlobals[mesh.NodeIndex];

        var jointScratch = new ushort[4];
        var weightScratch = new float[4];
        for (var i = 0; i < vcount; i++)
        {
            var v = verts[i];
            var p = v.Position;
            if (isRigid) p = Vector3.Transform(p, rigidTransform);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            BitConverter.TryWriteBytes(pos.AsSpan(i * 12), p.X);
            BitConverter.TryWriteBytes(pos.AsSpan(i * 12 + 4), p.Y);
            BitConverter.TryWriteBytes(pos.AsSpan(i * 12 + 8), p.Z);

            var n = v.Normal;
            if (isRigid) n = Vector3.TransformNormal(n, rigidTransform);
            if (n.LengthSquared() > 1e-6f) n = Vector3.Normalize(n);
            BitConverter.TryWriteBytes(nrm.AsSpan(i * 12), n.X);
            BitConverter.TryWriteBytes(nrm.AsSpan(i * 12 + 4), n.Y);
            BitConverter.TryWriteBytes(nrm.AsSpan(i * 12 + 8), n.Z);

            var texcoord = v.UVs.Count > 0 ? v.UVs[0] : default;
            BitConverter.TryWriteBytes(uv.AsSpan(i * 8), texcoord.X);
            BitConverter.TryWriteBytes(uv.AsSpan(i * 8 + 4), texcoord.Y);

            // Joints/weights
            Array.Clear(jointScratch);
            Array.Clear(weightScratch);
            var j = jointScratch.AsSpan();
            var w = weightScratch.AsSpan();
            if (mesh.UseBoneWeights)
            {
                float total = 0;
                for (var k = 0; k < 4; k++)
                {
                    var weight = v.BoneWeights[k];
                    var boneIndex = v.BoneIndices[k];
                    if (weight > 0 && part.FlverToMerged.TryGetValue(boneIndex, out var mIdx))
                    {
                        j[k] = (ushort)mIdx;
                        w[k] = weight;
                        total += weight;
                    }
                }
                if (total > 0)
                    for (var k = 0; k < 4; k++) w[k] /= total;
                else if (part.FlverToMerged.TryGetValue(mesh.NodeIndex, out var fallback))
                { j[0] = (ushort)fallback; w[0] = 1; }
            }
            else if (usesNormalWBones && part.FlverToMerged.TryGetValue(v.NormalW, out var normalWBone))
            {
                j[0] = (ushort)normalWBone; w[0] = 1;
            }
            else if (part.FlverToMerged.TryGetValue(mesh.NodeIndex, out var rigid))
            {
                j[0] = (ushort)rigid; w[0] = 1;
            }
            for (var k = 0; k < 4; k++)
            {
                BitConverter.TryWriteBytes(joints.AsSpan(i * 8 + k * 2), j[k]);
                BitConverter.TryWriteBytes(weights.AsSpan(i * 16 + k * 4), w[k]);
            }
        }

        var idxBytes = new byte[indices.Count * 4];
        for (var i = 0; i < indices.Count; i++)
            BitConverter.TryWriteBytes(idxBytes.AsSpan(i * 4), (uint)indices[i]);

        int Acc(byte[] data, int componentType, string type, int count, int? target, float[]? mn = null, float[]? mx = null)
        {
            var view = bin.AddBufferView(data, bufferViews, target);
            var acc = new Dictionary<string, object> { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
            if (mn != null) acc["min"] = mn;
            if (mx != null) acc["max"] = mx;
            accessors.Add(acc);
            return accessors.Count - 1;
        }

        var posAcc = Acc(pos, 5126, "VEC3", vcount, 34962, new[] { min.X, min.Y, min.Z }, new[] { max.X, max.Y, max.Z });
        var nrmAcc = Acc(nrm, 5126, "VEC3", vcount, 34962);
        var uvAcc = Acc(uv, 5126, "VEC2", vcount, 34962);
        var jAcc = Acc(joints, 5123, "VEC4", vcount, 34962);
        var wAcc = Acc(weights, 5126, "VEC4", vcount, 34962);
        var iAcc = Acc(idxBytes, 5125, "SCALAR", indices.Count, 34963);

        return new
        {
            attributes = new Dictionary<string, int>
            {
                ["POSITION"] = posAcc, ["NORMAL"] = nrmAcc, ["TEXCOORD_0"] = uvAcc,
                ["JOINTS_0"] = jAcc, ["WEIGHTS_0"] = wAcc,
            },
            indices = iAcc,
            material = materialBase + mesh.MaterialIndex,
        };
    }

    static List<int> Triangulate(FLVER2.FaceSet faceSet, int vertexCount)
    {
        // DSAS SoulsFormats exposes Triangulate(bool allowPrimitiveRestarts, bool includeDegenerateFaces).
        try
        {
            var method = faceSet.GetType().GetMethod("Triangulate", new[] { typeof(bool), typeof(bool) });
            if (method != null)
                return (List<int>)method.Invoke(faceSet, new object[] { vertexCount < ushort.MaxValue, false })!;
        }
        catch (TargetInvocationException)
        {
            // fall through to manual handling
        }

        if (!faceSet.TriangleStrip)
            return faceSet.Indices.ToList();

        var result = new List<int>();
        var flip = false;
        for (var i = 0; i < faceSet.Indices.Count - 2; i++)
        {
            int a = faceSet.Indices[i], b = faceSet.Indices[i + 1], c = faceSet.Indices[i + 2];
            if (a == 0xFFFF || b == 0xFFFF || c == 0xFFFF) { flip = false; continue; }
            if (a != b && b != c && c != a)
                result.AddRange(flip ? new[] { a, c, b } : new[] { a, b, c });
            flip = !flip;
        }
        return result;
    }

    static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

    static object BuildAnimation(PoseJson anim, MergedSkeleton merged, BinWriter bin, List<object> accessors, List<object> bufferViews)
    {
        var frameCount = anim.Frames.Count;
        var times = new byte[frameCount * 4];
        float maxTime = 0;
        for (var f = 0; f < frameCount; f++)
        {
            var t = anim.Frames[f].Time;
            maxTime = MathF.Max(maxTime, t);
            BitConverter.TryWriteBytes(times.AsSpan(f * 4), t);
        }
        var timeView = bin.AddBufferView(times, bufferViews, null);
        accessors.Add(new Dictionary<string, object>
        {
            ["bufferView"] = timeView, ["componentType"] = 5126, ["count"] = frameCount, ["type"] = "SCALAR",
            ["min"] = new[] { 0f }, ["max"] = new[] { maxTime },
        });
        var timeAcc = accessors.Count - 1;

        var channels = new List<object>();
        var samplers = new List<object>();

        for (var b = 0; b < anim.Bones.Count; b++)
        {
            if (!merged.ByName.TryGetValue(anim.Bones[b].Name, out var nodeIndex))
                continue;

            // HKX values are absolute local transforms for the animation source skeleton. AC6
            // frame parts carry their own bind-pose bone lengths, so writing those values directly
            // makes mixed loadouts separate at animated translation tracks. Retarget every sample
            // as a delta from the animation reference pose, applied to this GLB's merged rest pose.
            var sourceRest = anim.Bones[b].ReferenceLocalTransform;
            var targetRest = merged.Bones[nodeIndex];
            var translationRequired = false;
            var rotationRequired = false;
            var scaleRequired = false;
            for (var f = 0; f < frameCount; f++)
            {
                var tr = anim.Frames[f].LocalTransforms[b];
                if ((tr.T - sourceRest.T).Length() > 1e-4f) translationRequired = true;
                if (MathF.Abs(Quaternion.Dot(tr.R, sourceRest.R)) < 1f - 1e-6f) rotationRequired = true;
                if ((tr.S - sourceRest.S).Length() > 1e-4f) scaleRequired = true;
            }

            var tBytes = new byte[frameCount * 12];
            var rBytes = new byte[frameCount * 16];
            var sBytes = new byte[frameCount * 12];
            var prev = Quaternion.Identity;
            for (var f = 0; f < frameCount; f++)
            {
                var tr = anim.Frames[f].LocalTransforms[b];
                var t = targetRest.T + (tr.T - sourceRest.T);
                BitConverter.TryWriteBytes(tBytes.AsSpan(f * 12), t.X);
                BitConverter.TryWriteBytes(tBytes.AsSpan(f * 12 + 4), t.Y);
                BitConverter.TryWriteBytes(tBytes.AsSpan(f * 12 + 8), t.Z);

                var rotationDelta = Quaternion.Normalize(tr.R * Quaternion.Inverse(sourceRest.R));
                var q = Quaternion.Normalize(rotationDelta * targetRest.R);
                if (f > 0 && Quaternion.Dot(q, prev) < 0)
                    q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                prev = q;
                BitConverter.TryWriteBytes(rBytes.AsSpan(f * 16), q.X);
                BitConverter.TryWriteBytes(rBytes.AsSpan(f * 16 + 4), q.Y);
                BitConverter.TryWriteBytes(rBytes.AsSpan(f * 16 + 8), q.Z);
                BitConverter.TryWriteBytes(rBytes.AsSpan(f * 16 + 12), q.W);

                var sourceScale = sourceRest.S;
                var scaleDelta = new Vector3(
                    MathF.Abs(sourceScale.X) > 1e-6f ? tr.S.X / sourceScale.X : 1f,
                    MathF.Abs(sourceScale.Y) > 1e-6f ? tr.S.Y / sourceScale.Y : 1f,
                    MathF.Abs(sourceScale.Z) > 1e-6f ? tr.S.Z / sourceScale.Z : 1f);
                var scale = targetRest.S * scaleDelta;
                BitConverter.TryWriteBytes(sBytes.AsSpan(f * 12), scale.X);
                BitConverter.TryWriteBytes(sBytes.AsSpan(f * 12 + 4), scale.Y);
                BitConverter.TryWriteBytes(sBytes.AsSpan(f * 12 + 8), scale.Z);
            }

            if (translationRequired)
            {
                var tView = bin.AddBufferView(tBytes, bufferViews, null);
                accessors.Add(new Dictionary<string, object> { ["bufferView"] = tView, ["componentType"] = 5126, ["count"] = frameCount, ["type"] = "VEC3" });
                samplers.Add(new { input = timeAcc, output = accessors.Count - 1, interpolation = "LINEAR" });
                channels.Add(new { sampler = samplers.Count - 1, target = new { node = nodeIndex, path = "translation" } });
            }

            if (rotationRequired)
            {
                var rView = bin.AddBufferView(rBytes, bufferViews, null);
                accessors.Add(new Dictionary<string, object> { ["bufferView"] = rView, ["componentType"] = 5126, ["count"] = frameCount, ["type"] = "VEC4" });
                samplers.Add(new { input = timeAcc, output = accessors.Count - 1, interpolation = "LINEAR" });
                channels.Add(new { sampler = samplers.Count - 1, target = new { node = nodeIndex, path = "rotation" } });
            }

            if (scaleRequired)
            {
                var sView = bin.AddBufferView(sBytes, bufferViews, null);
                accessors.Add(new Dictionary<string, object> { ["bufferView"] = sView, ["componentType"] = 5126, ["count"] = frameCount, ["type"] = "VEC3" });
                samplers.Add(new { input = timeAcc, output = accessors.Count - 1, interpolation = "LINEAR" });
                channels.Add(new { sampler = samplers.Count - 1, target = new { node = nodeIndex, path = "scale" } });
            }
        }

        return new { name = anim.AnimationName ?? "anim", channels, samplers };
    }

    static void WriteMatrix(Span<byte> dest, Matrix4x4 m)
    {
        // System.Numerics Matrix4x4 is row-major with translation in M41..M43, which matches
        // glTF's column-major float16 layout for a row-vector convention (both store basis
        // vectors contiguously followed by translation).
        Span<float> f = stackalloc float[16]
        {
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44,
        };
        for (var i = 0; i < 16; i++)
            BitConverter.TryWriteBytes(dest.Slice(i * 4), f[i]);
    }

    static byte[] WriteGlb(Dictionary<string, object> gltf, BinWriter bin)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(gltf, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        var jsonPadded = (json.Length + 3) / 4 * 4;
        var binBytes = bin.ToArray();
        var binPadded = (binBytes.Length + 3) / 4 * 4;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u);                       // magic 'glTF'
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsonPadded + 8 + binPadded));
        w.Write((uint)jsonPadded);
        w.Write(0x4E4F534Au);                       // 'JSON'
        w.Write(json);
        for (var i = json.Length; i < jsonPadded; i++) w.Write((byte)' ');
        w.Write((uint)binPadded);
        w.Write(0x004E4942u);                       // 'BIN\0'
        w.Write(binBytes);
        for (var i = binBytes.Length; i < binPadded; i++) w.Write((byte)0);
        w.Flush();
        return ms.ToArray();
    }
}

sealed class BinWriter
{
    readonly MemoryStream _stream = new();
    public int Length => (int)_stream.Length;

    public int AddBufferView(byte[] data, List<object> bufferViews, int? target)
    {
        // 4-byte alignment
        while (_stream.Length % 4 != 0)
            _stream.WriteByte(0);
        var offset = (int)_stream.Length;
        _stream.Write(data);
        var view = new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length };
        if (target != null) view["target"] = target.Value;
        bufferViews.Add(view);
        return bufferViews.Count - 1;
    }

    public byte[] ToArray() => _stream.ToArray();
}
